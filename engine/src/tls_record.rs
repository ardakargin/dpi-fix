//! Splits the first plaintext TLS record for a listed host. The handshake bytes
//! stay identical. Only that connection's TCP sequence space grows by five bytes.

use crate::{hostlist::HostList, packet::{Packet, IPPROTO_TCP}, sniff, windivert::Address};
use std::{collections::HashMap, net::IpAddr, time::{Duration, Instant}};

const FIN: u8 = 0x01;
const SYN: u8 = 0x02;
const RST: u8 = 0x04;
const PSH: u8 = 0x08;
const ACK: u8 = 0x10;
const URG: u8 = 0x20;
const CWR: u8 = 0x80;
const RECORD_HEADER: usize = 5;
const MAX_RECORD: usize = 16_384;
const PENDING_LIMIT: usize = 8192;
const ACTIVE_LIMIT: usize = 8192;
const BUFFER_LIMIT: usize = 64;
const BUFFER_PACKETS: usize = 8;
const HOLD_TIME: Duration = Duration::from_millis(40);
const SYN_TIMEOUT: Duration = Duration::from_secs(60);
const CLOSE_TIMEOUT: Duration = Duration::from_secs(120);

type SendPacket<'a> = dyn FnMut(&[u8], Address) -> bool + 'a;

#[derive(Clone, Copy, Eq, Hash, PartialEq)]
struct Key {
    local: IpAddr,
    remote: IpAddr,
    port: u16,
}

impl Key {
    fn of(packet: &Packet<'_>, outbound: bool) -> Option<Self> {
        let (source, destination) = if packet.v6 {
            (packet.src_ip_v6()?.into(), packet.dst_ip_v6()?.into())
        } else {
            (packet.src_ip_v4()?.into(), packet.dst_ip_v4()?.into())
        };
        let (local, remote, port, peer_port) = if outbound {
            (source, destination, packet.src_port, packet.dst_port)
        } else {
            (destination, source, packet.dst_port, packet.src_port)
        };
        (peer_port == 443).then_some(Self { local, remote, port })
    }
}

struct Candidate {
    started: Instant,
    first_seq: u32,
    offered_scale: bool,
    peer: Option<Peer>,
}

#[derive(Clone, Copy)]
struct Peer {
    next_seq: u32,
    window: u32,
    scale: u8,
}

struct Buffered {
    peer: Peer,
    first_seq: u32,
    deadline: Instant,
    prefix: Vec<u8>,
    packets: Vec<(Vec<u8>, Address)>,
}

impl Buffered {
    fn flush(self, send: &mut SendPacket<'_>) {
        for (bytes, address) in self.packets {
            send(&bytes, address);
        }
    }
}

pub struct RecordSplitter<'a> {
    hosts: &'a HostList,
    pending: HashMap<Key, Candidate>,
    buffered: HashMap<Key, Buffered>,
    active: HashMap<Key, Translation>,
    next_sweep: Instant,
    scratch: Vec<u8>,
}

impl<'a> RecordSplitter<'a> {
    pub fn new(hosts: &'a HostList, now: Instant) -> Self {
        Self {
            hosts,
            pending: HashMap::new(),
            buffered: HashMap::new(),
            active: HashMap::new(),
            next_sweep: now,
            scratch: Vec::new(),
        }
    }

    pub fn deadline(&self) -> Option<Instant> {
        self.buffered.values().map(|state| state.deadline).min()
    }

    pub fn expire(&mut self, now: Instant, send: &mut SendPacket<'_>) {
        self.buffered.retain(|_, state| {
            if state.deadline > now {
                return true;
            }
            for (bytes, address) in &state.packets {
                send(bytes, *address);
            }
            false
        });
        if now >= self.next_sweep {
            self.pending.retain(|_, state| now.duration_since(state.started) < SYN_TIMEOUT);
            self.active.retain(|_, state| {
                state.closed_at.map_or(true, |closed| now.duration_since(closed) < CLOSE_TIMEOUT)
            });
            self.next_sweep = now + Duration::from_secs(5);
        }
    }

    pub fn finish(&mut self, send: &mut SendPacket<'_>) {
        for (_, state) in self.buffered.drain() {
            state.flush(send);
        }
    }

    /// Returns false for traffic handled by the engine's existing forwarding path.
    pub fn handle(
        &mut self, packet: &mut Packet<'_>, address: Address, now: Instant, send: &mut SendPacket<'_>,
    ) -> bool {
        if packet.proto != IPPROTO_TCP || !packet.has_wire_length() || packet.is_fragment() {
            return false;
        }
        let outbound = address.outbound();
        let Some(key) = Key::of(packet, outbound) else { return false; };
        let flags = packet.tcp_flags().unwrap();
        let seq = packet.tcp_seq().unwrap();

        if outbound && flags & (SYN | ACK) == SYN {
            // Reused tuples have a new initial sequence number. An old duplicate SYN
            // must leave the established translation in place.
            if self.active.get(&key).is_some_and(|state| state.first_seq == seq.wrapping_add(1)) {
                send(packet.data, address);
                return true;
            }
            self.active.remove(&key);
            self.pending.remove(&key);
            if let Some(held) = self.buffered.remove(&key) {
                held.flush(send);
            }
            if self.pending.len() < PENDING_LIMIT && packet.payload().is_empty()
                && flags & (FIN | RST | URG) == 0
            {
                if let Some(scale) = window_scale(packet.tcp_options()) {
                    self.pending.insert(key, Candidate {
                        started: now, first_seq: seq.wrapping_add(1),
                        offered_scale: scale.is_some(), peer: None,
                    });
                }
            }
            send(packet.data, address);
            return true;
        }

        if let Some(state) = self.active.get_mut(&key) {
            state.forward(packet, address, now, &mut self.scratch, send);
            return true;
        }

        if let Some(mut held) = self.buffered.remove(&key) {
            if outbound {
                if flags & (SYN | FIN | RST | URG) != 0
                    || seq != held.first_seq.wrapping_add(held.prefix.len() as u32)
                    || held.packets.len() >= BUFFER_PACKETS
                    || held.prefix.len() + packet.payload().len() > MAX_RECORD + RECORD_HEADER
                {
                    held.flush(send);
                } else {
                    held.prefix.extend_from_slice(packet.payload());
                    held.packets.push((packet.data.to_vec(), address));
                    self.classify(key, held, now, send);
                    return true;
                }
            } else if flags & (RST | FIN | SYN) != 0 {
                held.flush(send);
            } else {
                self.buffered.insert(key, held);
            }
        }

        if flags & (RST | FIN) != 0 {
            self.pending.remove(&key);
        }
        if let Some(state) = self.pending.get_mut(&key) {
            if !outbound && flags & ACK != 0 && packet.tcp_ack() == state.first_seq {
                if flags & SYN != 0 {
                    if let Some(scale) = window_scale(packet.tcp_options()) {
                        state.peer = Some(Peer {
                            next_seq: seq.wrapping_add(1),
                            window: u32::from(packet.tcp_window()),
                            scale: if state.offered_scale { scale.unwrap_or(0) } else { 0 },
                        });
                    }
                } else if let Some(peer) = &mut state.peer {
                    peer.window = u32::from(packet.tcp_window()) << peer.scale;
                }
            }
            if outbound && !packet.payload().is_empty() {
                let state = self.pending.remove(&key).unwrap();
                if let Some(peer) = state.peer {
                    let payload = packet.payload();
                    if flags & (SYN | FIN | RST | URG | ACK) == ACK && seq == state.first_seq
                        && packet.tcp_ack() == peer.next_seq && payload[0] == 22
                        && payload.len() <= MAX_RECORD + RECORD_HEADER
                    {
                        let decision = self.decision(payload);
                        if !matches!(decision, Decision::Pass) {
                            let held = Buffered {
                                peer, first_seq: state.first_seq, deadline: now + HOLD_TIME,
                                prefix: payload.to_vec(), packets: vec![(packet.data.to_vec(), address)],
                            };
                            self.apply(key, held, decision, now, send);
                            return true;
                        }
                    }
                }
            }
        }
        send(packet.data, address);
        true
    }

    fn classify(&mut self, key: Key, held: Buffered, now: Instant, send: &mut SendPacket<'_>) {
        let decision = self.decision(&held.prefix);
        self.apply(key, held, decision, now, send);
    }

    fn apply(
        &mut self, key: Key, held: Buffered, decision: Decision, now: Instant, send: &mut SendPacket<'_>,
    ) {
        match decision {
            Decision::More if self.buffered.len() < BUFFER_LIMIT && now < held.deadline => {
                self.buffered.insert(key, held);
            }
            Decision::Split(edit) if self.active.len() < ACTIVE_LIMIT
                && held.prefix.len() + RECORD_HEADER <= held.peer.window as usize =>
            {
                let mut translation = Translation::new(held.first_seq, held.peer, edit);
                for (mut bytes, address) in held.packets {
                    let mut packet = Packet::parse(&mut bytes).unwrap();
                    translation.forward(&mut packet, address, now, &mut self.scratch, send);
                }
                self.active.insert(key, translation);
            }
            _ => held.flush(send),
        }
    }

    fn decision(&self, prefix: &[u8]) -> Decision {
        if prefix.get(1).is_some_and(|&v| v != 3) || prefix.get(2).is_some_and(|&v| v > 3)
            || prefix.get(5).is_some_and(|&kind| kind != 1)
        {
            return Decision::Pass;
        }
        if prefix.len() < 9 {
            return Decision::More;
        }
        let record_length = u16::from_be_bytes([prefix[3], prefix[4]]) as usize;
        let record_end = RECORD_HEADER + record_length;
        let handshake_length = u32::from_be_bytes([0, prefix[6], prefix[7], prefix[8]]) as usize;
        if record_length > MAX_RECORD || record_end != 9 + handshake_length {
            return Decision::Pass;
        }
        if let Some(sni) = sniff::tls_sni(prefix, true) {
            if sni.len < 2 || !self.hosts.matches(sni.host) {
                return Decision::Pass;
            }
            let cut = sni.offset + sni.len / 2;
            let first_len = ((cut - RECORD_HEADER) as u16).to_be_bytes();
            let second_len = ((record_end - cut) as u16).to_be_bytes();
            return Decision::Split(Edit {
                cut: cut as i64,
                first_len,
                second_header: [22, prefix[1], prefix[2], second_len[0], second_len[1]],
            });
        }
        if prefix.len() < record_end { Decision::More } else { Decision::Pass }
    }
}

enum Decision {
    More,
    Pass,
    Split(Edit),
}

struct Edit {
    cut: i64,
    first_len: [u8; 2],
    second_header: [u8; RECORD_HEADER],
}

impl Edit {
    fn to_wire(&self, original: i64) -> i64 {
        original + if original >= self.cut { RECORD_HEADER as i64 } else { 0 }
    }

    fn to_local(&self, wire: i64) -> i64 {
        wire - (wire - self.cut).clamp(0, RECORD_HEADER as i64)
    }
}

struct Translation {
    first_seq: u32,
    client_end: i64,
    edit: Edit,
    peer_scale: u8,
    client_acked: u32,
    server_acked: u32,
    client_fin: Option<u32>,
    server_fin: Option<u32>,
    closed_at: Option<Instant>,
}

impl Translation {
    fn new(first_seq: u32, peer: Peer, edit: Edit) -> Self {
        Self {
            first_seq, client_end: 0, edit, peer_scale: peer.scale,
            client_acked: first_seq, server_acked: peer.next_seq,
            client_fin: None, server_fin: None, closed_at: None,
        }
    }

    fn unwrap(&self, seq: u32, reference: i64) -> i64 {
        let reference_seq = self.raw_seq(reference);
        reference + i64::from(seq.wrapping_sub(reference_seq) as i32)
    }

    fn raw_seq(&self, offset: i64) -> u32 {
        self.first_seq.wrapping_add(offset as u32)
    }

    fn forward(
        &mut self, packet: &mut Packet<'_>, address: Address, now: Instant,
        scratch: &mut Vec<u8>, send: &mut SendPacket<'_>,
    ) {
        let flags = packet.tcp_flags().unwrap();
        let seq = packet.tcp_seq().unwrap();
        if flags & SYN != 0 {
            send(packet.data, address);
            return;
        }
        let length = packet.payload().len();
        let outbound = address.outbound();
        let (sent, reset) = if outbound {
            let start = self.unwrap(seq, self.client_end);
            let sent = self.outbound(packet, address, start, scratch, send);
            if sent {
                let consumes = length as i64 + i64::from(flags & FIN != 0);
                if consumes > 0 {
                    self.client_end = self.client_end.max(start + consumes);
                }
                if flags & FIN != 0 {
                    self.client_fin = Some(seq.wrapping_add(consumes as u32));
                }
                if flags & ACK != 0 && at_or_after(packet.tcp_ack(), self.server_acked) {
                    self.server_acked = packet.tcp_ack();
                }
            }
            (sent, seq == self.client_acked)
        } else {
            let sent = self.inbound(packet, address, send);
            if sent {
                if flags & FIN != 0 {
                    self.server_fin = Some(seq.wrapping_add(length as u32 + 1));
                }
                if flags & ACK != 0 && at_or_after(packet.tcp_ack(), self.client_acked) {
                    self.client_acked = packet.tcp_ack();
                }
            }
            (sent, seq == self.server_acked)
        };
        let finished = self.client_fin.is_some_and(|fin| at_or_after(self.client_acked, fin))
            && self.server_fin.is_some_and(|fin| at_or_after(self.server_acked, fin));
        if sent && (finished || flags & RST != 0 && reset) {
            self.closed_at.get_or_insert(now);
        }
    }

    fn inbound(&self, packet: &mut Packet<'_>, mut address: Address, send: &mut SendPacket<'_>) -> bool {
        if packet.tcp_flags().unwrap() & ACK == 0 {
            return send(packet.data, address);
        }
        let reference = self.edit.to_wire(self.client_end);
        let ack = self.unwrap(packet.tcp_ack(), reference);
        let local_ack = self.edit.to_local(ack);
        let window = i64::from(packet.tcp_window()) << self.peer_scale;
        let local_window = ((self.edit.to_local(ack + window) - local_ack) >> self.peer_scale) as u16;
        let raw_ack = self.raw_seq(local_ack);
        let mut changed = raw_ack != packet.tcp_ack() || local_window != packet.tcp_window();
        if changed {
            packet.set_tcp_ack(raw_ack);
            packet.set_tcp_window(local_window);
        }
        changed |= translate_sack(packet.tcp_options_mut(), |edge| {
            self.raw_seq(self.edit.to_local(self.unwrap(edge, ack)))
        });
        if changed {
            packet.recompute_checksums();
            address.mark_tcp_checksums(!packet.v6);
        }
        send(packet.data, address)
    }

    fn outbound(
        &self, packet: &mut Packet<'_>, mut address: Address, start: i64,
        scratch: &mut Vec<u8>, send: &mut SendPacket<'_>,
    ) -> bool {
        let size = packet.payload().len();
        let end = start + size as i64;
        let inserts_header = start <= self.edit.cut && self.edit.cut < end;
        let edits_length = start < 5 && end > 3 && size != 0;
        if !inserts_header && !edits_length {
            let translated = self.raw_seq(self.edit.to_wire(start));
            if translated != packet.tcp_seq().unwrap() {
                packet.set_tcp_seq(translated);
                packet.recompute_checksums();
                address.mark_tcp_checksums(!packet.v6);
            }
            return send(packet.data, address);
        }

        address.mark_tcp_checksums(!packet.v6);
        let head_size = if inserts_header { (self.edit.cut - start) as usize } else { size };
        if head_size > 0 && !self.emit(
            packet, address, self.edit.to_wire(start), 0..head_size, &[],
            true, !inserts_header, start, scratch, send,
        ) {
            return false;
        }
        if inserts_header {
            // Avoid enlarging any TCP packet past the input size, even when an
            // overlapping retransmission starts immediately before the cut.
            let tail_payload = &packet.payload()[head_size..];
            let tail = [&self.edit.second_header[..], tail_payload].concat();
            for (index, chunk) in tail.chunks(size).enumerate() {
                let offset = index * size;
                if !self.emit(
                    packet, address, self.edit.cut + offset as i64, 0..0, chunk,
                    index == 0 && head_size == 0, offset + chunk.len() == tail.len(),
                    start, scratch, send,
                ) {
                    return false;
                }
            }
        }
        true
    }

    #[allow(clippy::too_many_arguments)]
    fn emit(
        &self, original: &Packet<'_>, address: Address, wire_offset: i64,
        payload: std::ops::Range<usize>, extra: &[u8], first: bool, last: bool,
        original_offset: i64, scratch: &mut Vec<u8>, send: &mut SendPacket<'_>,
    ) -> bool {
        let header = original.payload_offset();
        scratch.clear();
        scratch.extend_from_slice(&original.data[..header]);
        let payload_offset = original_offset + payload.start as i64;
        let payload_end = original_offset + payload.end as i64;
        scratch.extend_from_slice(&original.payload()[payload]);
        scratch.extend_from_slice(extra);
        for (index, &byte) in self.edit.first_len.iter().enumerate() {
            let offset = (3 + index) as i64;
            if (payload_offset..payload_end).contains(&offset) {
                scratch[header + (offset - payload_offset) as usize] = byte;
            }
        }
        let mut packet = Packet::parse(scratch).unwrap();
        let mut flags = original.tcp_flags().unwrap();
        if !first { flags &= !CWR; }
        if !last { flags &= !(FIN | PSH); }
        if flags & URG != 0 {
            let urgent = self.edit.to_wire(original_offset + i64::from(original.tcp_urgent()));
            if let Ok(distance) = u16::try_from(urgent - wire_offset) {
                packet.set_tcp_urgent(distance);
            } else {
                flags &= !URG;
            }
        }
        packet.set_tcp_flags(flags);
        packet.set_total_len(packet.data.len());
        packet.set_tcp_seq(self.raw_seq(wire_offset));
        packet.recompute_checksums();
        send(packet.data, address)
    }
}

fn at_or_after(seq: u32, previous: u32) -> bool {
    (seq.wrapping_sub(previous) as i32) >= 0
}

fn option_size(bytes: &[u8]) -> Option<usize> {
    let size = match bytes[0] {
        0 => return None,
        1 => 1,
        _ => usize::from(*bytes.get(1)?),
    };
    (size <= bytes.len() && (size >= 2 || bytes[0] == 1)).then_some(size)
}

fn window_scale(mut bytes: &[u8]) -> Option<Option<u8>> {
    let mut scale = None;
    while !bytes.is_empty() && bytes[0] != 0 {
        let size = option_size(bytes)?;
        match bytes[0] {
            3 if size == 3 && scale.is_none() => scale = Some(bytes[2].min(14)),
            3 | 19 | 29 | 30 => return None, // Invalid WS, TCP MD5, TCP-AO, or Multipath TCP.
            _ => {}
        }
        bytes = &bytes[size..];
    }
    Some(scale)
}

fn translate_sack(mut bytes: &mut [u8], map: impl Fn(u32) -> u32) -> bool {
    let mut changed = false;
    while !bytes.is_empty() {
        let Some(size) = option_size(bytes) else { break; };
        if bytes[0] == 5 && size >= 10 && (size - 2) % 8 == 0 {
            let mut write = 2;
            for read in (2..size).step_by(8) {
                let left = u32::from_be_bytes(bytes[read..read + 4].try_into().unwrap());
                let right = u32::from_be_bytes(bytes[read + 4..read + 8].try_into().unwrap());
                let mapped_left = map(left);
                let mapped_right = map(right);
                changed |= mapped_left != left || mapped_right != right;
                if mapped_left != mapped_right {
                    bytes[write..write + 4].copy_from_slice(&mapped_left.to_be_bytes());
                    bytes[write + 4..write + 8].copy_from_slice(&mapped_right.to_be_bytes());
                    write += 8;
                }
            }
            if write < size {
                // Discard blocks containing only injected bytes and preserve option alignment.
                let padding = if write == 2 { 0 } else { write };
                if padding != 0 { bytes[1] = write as u8; }
                bytes[padding..size].fill(1);
                changed = true;
            }
        }
        bytes = &mut bytes[size..];
    }
    changed
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sniff::tests::{client_hello, client_hello_with_padding};

    const SERVER: u32 = 25;
    const PORT: u16 = 51_321;

    struct Flow {
        first: u32,
        v6: bool,
    }

    impl Flow {
        fn packet(
            &self, outbound: bool, seq: u32, ack: u32, flags: u8, options: &[u8], payload: &[u8],
        ) -> (Vec<u8>, Address) {
            assert_eq!(options.len() % 4, 0);
            let ip = if self.v6 { 48 } else { 20 };
            let mut bytes = vec![0; ip + 20];
            if self.v6 {
                bytes[0] = 0x60;
                bytes[6] = 60; // Destination options before TCP.
                bytes[7] = 128;
                bytes[8..10].copy_from_slice(&[0x20, 1]);
                bytes[24..26].copy_from_slice(&[0x20, 1]);
                bytes[23] = if outbound { 1 } else { 2 };
                bytes[39] = if outbound { 2 } else { 1 };
                bytes[40] = IPPROTO_TCP;
            } else {
                bytes[0] = 0x45;
                bytes[6] = 0x40;
                bytes[9] = IPPROTO_TCP;
                bytes[12..16].copy_from_slice(if outbound { &[10, 0, 0, 1] } else { &[10, 0, 0, 2] });
                bytes[16..20].copy_from_slice(if outbound { &[10, 0, 0, 2] } else { &[10, 0, 0, 1] });
            }
            let (src, dst) = if outbound { (PORT, 443) } else { (443, PORT) };
            bytes[ip..ip + 2].copy_from_slice(&src.to_be_bytes());
            bytes[ip + 2..ip + 4].copy_from_slice(&dst.to_be_bytes());
            bytes[ip + 12] = ((20 + options.len()) as u8 / 4) << 4;
            bytes.extend_from_slice(options);
            bytes.extend_from_slice(payload);
            let mut packet = Packet::parse(&mut bytes).unwrap();
            packet.set_total_len(packet.data.len());
            packet.set_tcp_seq(seq);
            packet.set_tcp_ack(ack);
            packet.set_tcp_flags(flags);
            packet.set_tcp_window(16_000);
            packet.recompute_checksums();
            let mut addr = Address::zeroed();
            let bits = (u32::from(outbound) << 17) | (u32::from(self.v6) << 20);
            addr.0[8..12].copy_from_slice(&bits.to_le_bytes());
            (bytes, addr)
        }

        fn connect(&self, state: &mut RecordSplitter<'_>, now: Instant) {
            let syn = self.packet(true, self.first.wrapping_sub(1), 0, SYN, &[3, 3, 4, 1], &[]);
            let synack = self.packet(false, SERVER - 1, self.first, SYN | ACK, &[3, 3, 4, 1], &[]);
            let ack = self.packet(true, self.first, SERVER, ACK, &[], &[]);
            for original in [syn, synack, ack] {
                assert_eq!(capture(state, original.clone(), now), vec![original.0]);
            }
        }

        fn outbound(&self, offset: usize, flags: u8, payload: &[u8]) -> (Vec<u8>, Address) {
            self.packet(true, self.first.wrapping_add(offset as u32), SERVER, flags, &[], payload)
        }
    }

    fn capture(state: &mut RecordSplitter<'_>, input: (Vec<u8>, Address), now: Instant) -> Vec<Vec<u8>> {
        let mut output = Vec::new();
        let mut send = |bytes: &[u8], _: Address| {
            output.push(bytes.to_vec());
            true
        };
        let (mut bytes, address) = input;
        state.expire(now, &mut send);
        assert!(state.handle(&mut Packet::parse(&mut bytes).unwrap(), address, now, &mut send));
        output
    }

    fn payloads(packets: &mut [Vec<u8>], first: u32) -> Vec<u8> {
        let mut stream = Vec::new();
        for bytes in packets {
            let original = bytes.clone();
            let mut packet = Packet::parse(bytes).unwrap();
            assert!(packet.has_wire_length());
            assert_eq!(packet.tcp_seq(), Some(first.wrapping_add(stream.len() as u32)));
            packet.recompute_checksums();
            assert_eq!(packet.data, original, "each emitted IP/TCP checksum is present");
            stream.extend_from_slice(packet.payload());
        }
        stream
    }

    fn reframed(hello: &[u8]) -> (usize, Vec<u8>) {
        let sni = sniff::tls_sni(hello, false).unwrap();
        let cut = sni.offset + sni.len / 2;
        let (left, right) = hello.split_at(cut);
        let mut expected = left.to_vec();
        expected[3..5].copy_from_slice(&((cut - 5) as u16).to_be_bytes());
        expected.extend_from_slice(&hello[..3]);
        expected.extend_from_slice(&(right.len() as u16).to_be_bytes());
        expected.extend_from_slice(right);
        (cut, expected)
    }

    #[test]
    fn splits_ipv4_and_ipv6_tls_and_translates_following_traffic() {
        let now = Instant::now();
        let hosts = HostList::parse("example.com");
        let hello = client_hello("www.example.com");
        let (cut, wire) = reframed(&hello);
        for v6 in [false, true] {
            let mut state = RecordSplitter::new(&hosts, now);
            let flow = Flow { first: u32::MAX - 20, v6 };
            flow.connect(&mut state, now);
            let mut output = capture(&mut state, flow.outbound(0, CWR | ACK | PSH, &hello), now);
            assert_eq!(output.len(), 2);
            assert_eq!(Packet::parse(&mut output[0]).unwrap().tcp_flags(), Some(CWR | ACK));
            assert_eq!(Packet::parse(&mut output[1]).unwrap().tcp_flags(), Some(ACK | PSH));
            assert_eq!(payloads(&mut output, flow.first), wire);

            // ACKs within the injected header acknowledge only the original stream cut.
            let wire_ack = flow.first.wrapping_add(cut as u32 + 3);
            let response = flow.packet(false, SERVER, wire_ack, ACK, &[], b"odd");
            let mut output = capture(&mut state, response, now);
            let reply = Packet::parse(&mut output[0]).unwrap();
            assert_eq!(reply.tcp_ack(), flow.first.wrapping_add(cut as u32));
            assert_eq!(reply.tcp_window(), 15_999);
            assert_eq!(reply.payload(), b"odd");

            let response = flow.packet(false, SERVER, flow.first.wrapping_add(wire.len() as u32),
                ACK, &[], &[]);
            let mut output = capture(&mut state, response, now);
            let reply = Packet::parse(&mut output[0]).unwrap();
            assert_eq!(reply.tcp_ack(), flow.first.wrapping_add(hello.len() as u32));
            assert_eq!(reply.tcp_window(), 16_000);

            let mut encrypted = capture(&mut state, flow.outbound(hello.len(), ACK, b"abc"), now);
            assert_eq!(payloads(&mut encrypted, flow.first.wrapping_add(wire.len() as u32)), b"abc");
        }
    }

    #[test]
    fn reframes_a_client_hello_with_sni_in_the_second_tcp_packet() {
        let now = Instant::now();
        let hosts = HostList::parse("discord.com");
        let mut state = RecordSplitter::new(&hosts, now);
        let flow = Flow { first: 300, v6: false };
        flow.connect(&mut state, now);
        let mut hello = client_hello_with_padding("discord.com", 1630);
        let sni = sniff::tls_sni(&hello, false).unwrap();
        let extension = sni.offset - 9;
        let sni_length = 9 + sni.len;
        hello[extension..].rotate_left(sni_length);
        let (cut, wire) = reframed(&hello);
        assert!(cut > 1460);

        assert!(capture(&mut state, flow.outbound(0, ACK, &hello[..1460]), now).is_empty());
        let other = Flow { first: 600, v6: true };
        other.connect(&mut state, now);
        let unlisted = other.outbound(0, ACK, &client_hello("unlisted.com"));
        assert_eq!(capture(&mut state, unlisted.clone(), now), vec![unlisted.0]);
        let mut output = capture(&mut state, flow.outbound(1460, ACK | PSH, &hello[1460..]), now);
        assert_eq!(output.len(), 3);
        assert_eq!(payloads(&mut output, flow.first), wire);
    }

    #[test]
    fn reassembly_timeout_and_out_of_order_input_send_the_original_packets() {
        let now = Instant::now();
        let hosts = HostList::parse("discord.com");
        let flow = Flow { first: 400, v6: false };
        let hello = client_hello("discord.com");
        let first = flow.outbound(0, ACK, &hello[..3]);

        let mut state = RecordSplitter::new(&hosts, now);
        flow.connect(&mut state, now);
        assert!(capture(&mut state, first.clone(), now).is_empty());
        assert_eq!(state.deadline(), Some(now + HOLD_TIME));
        let late = flow.outbound(3, ACK, &hello[3..]);
        assert_eq!(capture(&mut state, late.clone(), now + HOLD_TIME), vec![first.0.clone(), late.0]);
        assert!(state.deadline().is_none());
        assert!(state.active.is_empty());

        let mut state = RecordSplitter::new(&hosts, now);
        flow.connect(&mut state, now);
        assert!(capture(&mut state, first.clone(), now).is_empty());
        let gap = flow.outbound(4, ACK, &hello[4..]);
        assert_eq!(capture(&mut state, gap.clone(), now), vec![first.0, gap.0]);
        assert!(state.active.is_empty());
    }

    #[test]
    fn retransmission_after_partial_send_stays_in_the_same_sequence_space() {
        let now = Instant::now();
        let hosts = HostList::parse("example.com");
        let mut state = RecordSplitter::new(&hosts, now);
        let flow = Flow { first: 500, v6: false };
        flow.connect(&mut state, now);
        let hello = client_hello("example.com");
        let (cut, wire) = reframed(&hello);
        let (mut bytes, address) = flow.outbound(0, ACK, &hello);
        let mut attempted = Vec::new();
        assert!(state.handle(&mut Packet::parse(&mut bytes).unwrap(), address, now,
            &mut |packet, _| {
                attempted.push(packet.to_vec());
                attempted.len() == 1
            }));
        assert_eq!(payloads(&mut attempted, flow.first), wire);
        assert_eq!(capture(&mut state, flow.outbound(0, ACK, &hello), now), attempted);
        let mut overlap = capture(&mut state, flow.outbound(cut - 2, ACK, &hello[cut - 2..cut + 1]), now);
        assert_eq!(payloads(&mut overlap, flow.first + cut as u32 - 2), wire[cut - 2..cut + 6]);
    }

    #[test]
    fn sack_ranges_and_closure_translate_across_the_inserted_bytes() {
        let now = Instant::now();
        let hosts = HostList::parse("example.com");
        let mut state = RecordSplitter::new(&hosts, now);
        let flow = Flow { first: u32::MAX - 50, v6: false };
        flow.connect(&mut state, now);
        let hello = client_hello("example.com");
        let (cut, wire) = reframed(&hello);
        capture(&mut state, flow.outbound(0, ACK, &hello), now);

        let boundary = flow.first.wrapping_add(cut as u32);
        let mut sacks = vec![1, 1, 5, 18];
        for offset in [1, 3, 4, 9] {
            sacks.extend_from_slice(&boundary.wrapping_add(offset).to_be_bytes());
        }
        let a = flow.packet(false, SERVER, boundary, ACK, &sacks, b"");
        let mut response = capture(&mut state, a, now);
        let packet = Packet::parse(&mut response[0]).unwrap();
        let options = packet.tcp_options();
        assert_eq!(&options[..4], &[1, 1, 5, 10]);
        assert_eq!(&options[4..8], &boundary.to_be_bytes());
        assert_eq!(&options[8..12], &boundary.wrapping_add(4).to_be_bytes());
        assert_eq!(&options[12..20], &[1; 8]);

        let mut fin = capture(&mut state, flow.outbound(hello.len(), ACK | FIN, &[]), now);
        let wire_end = flow.first.wrapping_add(wire.len() as u32 + 1);
        assert_eq!(Packet::parse(&mut fin[0]).unwrap().tcp_seq(), Some(wire_end.wrapping_sub(1)));
        let response = flow.packet(false, SERVER, wire_end, ACK | FIN, &[], &[]);
        let mut inbound_fin = capture(&mut state, response, now);
        let expected_ack = flow.first.wrapping_add(hello.len() as u32 + 1);
        assert_eq!(Packet::parse(&mut inbound_fin[0]).unwrap().tcp_ack(), expected_ack);
        assert!(state.active.values().all(|translation| translation.closed_at.is_none()));

        let reply = flow.packet(true, expected_ack, SERVER + 1, ACK, &[], &[]);
        let mut final_ack = capture(&mut state, reply.clone(), now);
        assert_eq!(Packet::parse(&mut final_ack[0]).unwrap().tcp_seq(), Some(wire_end));
        assert_eq!(capture(&mut state, reply.clone(), now + CLOSE_TIMEOUT / 2), final_ack);
        assert_eq!(capture(&mut state, reply.clone(), now + CLOSE_TIMEOUT), vec![reply.0]);
        assert!(state.active.is_empty());
    }

    #[test]
    fn fresh_syn_replaces_a_still_translated_connection_but_old_syn_preserves_it() {
        let now = Instant::now();
        let hosts = HostList::parse("example.com");
        let mut state = RecordSplitter::new(&hosts, now);
        let flow = Flow { first: 400, v6: false };
        flow.connect(&mut state, now);
        let hello = client_hello("example.com");
        capture(&mut state, flow.outbound(0, ACK, &hello), now);
        let old_syn = flow.packet(true, flow.first - 1, 0, SYN, &[], &[]);
        capture(&mut state, old_syn, now + Duration::from_secs(3600));
        assert_eq!(state.active.len(), 1);
        let new_syn = flow.packet(true, flow.first + 300, 0, SYN, &[], &hello);
        assert_eq!(capture(&mut state, new_syn.clone(), now + Duration::from_secs(3601)), vec![new_syn.0]);
        assert!(state.active.is_empty());
        assert!(state.pending.is_empty());
    }
}
