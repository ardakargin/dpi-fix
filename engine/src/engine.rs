//! Domain-scoped TCP engine. Unrecognized traffic is forwarded unchanged.
use crate::{args::Config, hostlist::HostList, packet::Packet, sniff, tls_record::RecordSplitter, windivert::{Address, WinDivert}};
use std::{io, path::PathBuf, time::Instant};

const MAX_PACKET: usize = 65_575;

pub struct Engine {
    config: Config,
    hosts: HostList,
    dll_dir: Option<PathBuf>,
}

struct Segmentation {
    header: usize,
    split: usize,
    sequence: u32,
    flags: u8,
    tail_first: bool,
}

impl Engine {
    pub fn new(config: Config, dll_dir: Option<PathBuf>) -> io::Result<Self> {
        let hosts = HostList::load(&config.domains)?;
        if hosts.is_empty() {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "domain list is empty"));
        }
        Ok(Self { config, hosts, dll_dir })
    }

    pub fn open(&self) -> io::Result<WinDivert> {
        WinDivert::open(self.filter(), self.dll_dir.as_deref(), 0)
    }

    fn filter(&self) -> &'static str {
        if self.config.tls_record_split && self.config.http {
            "!loopback and !impostor and tcp and ((outbound and (tcp.DstPort == 443 or (tcp.DstPort == 80 and tcp.PayloadLength > 0))) or (inbound and tcp.SrcPort == 443))"
        } else if self.config.tls_record_split {
            "!loopback and !impostor and tcp and ((outbound and tcp.DstPort == 443) or (inbound and tcp.SrcPort == 443))"
        } else if self.config.http {
            "outbound and !loopback and !impostor and tcp and tcp.PayloadLength > 0 and ((tcp.DstPort == 443 and tcp.Payload[0] == 22) or tcp.DstPort == 80)"
        } else {
            "outbound and !loopback and !impostor and tcp and tcp.DstPort == 443 and tcp.PayloadLength > 0 and tcp.Payload[0] == 22"
        }
    }

    pub fn run(&mut self) -> io::Result<()> {
        let capture = self.open()?;
        self.run_with(&capture)
    }

    pub fn run_with(&mut self, capture: &WinDivert) -> io::Result<()> {
        if self.config.tls_record_split {
            return self.run_record_split(capture);
        }
        let mut original = vec![0; MAX_PACKET];
        // Reused for both segments. No per-flow state or per-packet segment allocation.
        let mut segment = vec![0; MAX_PACKET];
        let mut address = Address::zeroed();
        loop {
            let length = match capture.recv(&mut original, &mut address) {
                Ok(n) => n,
                Err(error) if error.raw_os_error() == Some(232) => return Ok(()),
                Err(error) => return Err(error),
            };
            let original = &mut original[..length];
            let plan = Packet::parse(original).and_then(|packet| self.plan(&packet));
            if let Some(plan) = plan {
                let mut modified_address = address;
                modified_address.mark_tcp_checksums(original[0] >> 4 == 4);
                if send_segments(original, &mut segment, plan, |packet| {
                    capture.send(packet, &modified_address)
                        .is_ok_and(|sent| sent == packet.len())
                }) {
                    continue;
                }
            }
            capture.send(original, &address)?;
        }
    }

    fn run_record_split(&self, capture: &WinDivert) -> io::Result<()> {
        let mut receiver = capture.timed_receiver(MAX_PACKET)?;
        let mut splitter = RecordSplitter::new(&self.hosts, Instant::now());
        let mut segment = self.config.http.then(|| vec![0; MAX_PACKET]);
        let mut send = |packet: &[u8], address: Address| {
            // A failed write is TCP packet loss. Restarting would discard all
            // established translations, and sending original bytes could corrupt one.
            capture.send(packet, &address).is_ok_and(|written| written == packet.len())
        };
        loop {
            splitter.expire(Instant::now(), &mut send);
            let (bytes, address) = match receiver.recv(splitter.deadline()) {
                Ok(Some(packet)) => packet,
                Ok(None) => continue,
                Err(error) => {
                    splitter.finish(&mut send);
                    return if error.raw_os_error() == Some(232) { Ok(()) } else { Err(error) };
                }
            };
            let now = Instant::now();
            splitter.expire(now, &mut send);
            if let Some(mut packet) = Packet::parse(bytes) {
                if splitter.handle(&mut packet, address, now, &mut send) {
                    continue;
                }
                if let Some(segment) = segment.as_mut() {
                    if address.outbound() {
                        if let Some(plan) = self.plan(&packet) {
                            let mut modified = address;
                            modified.mark_tcp_checksums(!packet.v6);
                            if send_segments(packet.data, segment, plan, |bytes| send(bytes, modified)) {
                                continue;
                            }
                        }
                    }
                }
            }
            send(bytes, address);
        }
    }

    fn plan(&self, packet: &Packet<'_>) -> Option<Segmentation> {
        let flags = packet.tcp_flags()?;
        // SYN, RST and URG require additional sequence semantics.
        if flags & 0x26 != 0 || packet.payload_offset() > packet.data.len() {
            return None;
        }
        if !packet.v6 && (u16::from_be_bytes([packet.data[6], packet.data[7]]) & 0x3fff != 0) {
            return None;
        }
        if !packet.has_wire_length() {
            return None;
        }
        let payload = packet.payload();
        let split = match packet.dst_port {
            443 if !self.config.tls_record_split => {
                let sni = sniff::tls_sni(payload, self.config.tls_reorder)?;
                if !self.hosts.matches(sni.host) { return None; }
                sni.offset + sni.len / 2
            }
            80 if self.config.http => {
                if !self.hosts.matches(&sniff::http_host(payload)?) { return None; }
                payload.iter().position(|&b| b == b' ')?
            }
            _ => return None,
        };
        if split == 0 || split >= payload.len() {
            return None;
        }
        Some(Segmentation {
            header: packet.payload_offset(),
            split,
            sequence: packet.tcp_seq()?,
            flags,
            tail_first: packet.dst_port == 443 && self.config.tls_reorder,
        })
    }
}

fn send_segments(
    original: &[u8], scratch: &mut [u8], plan: Segmentation, mut send: impl FnMut(&[u8]) -> bool,
) -> bool {
    let Segmentation { header, split, sequence, flags, tail_first } = plan;
    let boundary = header + split;
    // Keep FIN and PSH with the tail, and CWR with the head of the TCP byte stream.
    let mut segments = [
        (header..boundary, sequence, flags & !0x09),
        (boundary..original.len(), sequence.wrapping_add(split as u32), flags & !0x80),
    ];
    if tail_first {
        segments.reverse();
    }

    for (payload, sequence, flags) in segments {
        let length = header + payload.len();
        scratch[..header].copy_from_slice(&original[..header]);
        scratch[header..length].copy_from_slice(&original[payload]);
        let Some(mut packet) = Packet::parse(&mut scratch[..length]) else { return false; };
        prepare(&mut packet, sequence, flags);
        // Retry the original at its original sequence number if either send fails.
        if !send(packet.data) {
            return false;
        }
    }
    true
}

fn prepare(packet: &mut Packet<'_>, sequence: u32, flags: u8) {
    let length = packet.data.len();
    packet.data[packet.ip_hdr_len + 13] = flags;
    packet.set_total_len(length);
    packet.set_tcp_seq(sequence);
    packet.recompute_checksums();
}

#[cfg(test)]
mod tests {
    use super::*;

    fn request(host: &str) -> Vec<u8> {
        let payload = format!("GET / HTTP/1.1\r\nHost: {host}\r\n\r\n");
        tcp_packet(80, payload.as_bytes())
    }

    fn tcp_packet(port: u16, payload: &[u8]) -> Vec<u8> {
        let mut bytes = vec![0; 40];
        bytes[0] = 0x45;
        bytes[9] = 6;
        bytes[22..24].copy_from_slice(&port.to_be_bytes());
        bytes[32] = 0x50;
        bytes[33] = 0x18;
        bytes.extend_from_slice(payload);
        let length = bytes.len() as u16;
        bytes[2..4].copy_from_slice(&length.to_be_bytes());
        bytes
    }

    #[test]
    fn leaves_tiktok_tls_untouched_unless_explicitly_listed() {
        let mut engine = Engine {
            config: Config { domains: PathBuf::new(), http: false, tls_reorder: true, tls_record_split: false },
            hosts: HostList::parse("discord.com\ninstagram.com\nx.com"),
            dll_dir: None,
        };
        let hello = sniff::tests::client_hello("www.tiktok.com");
        let mut bytes = tcp_packet(443, &hello);
        let packet = Packet::parse(&mut bytes).unwrap();
        assert!(engine.plan(&packet).is_none());
        engine.hosts = HostList::parse("tiktok.com");
        assert!(engine.plan(&packet).is_some());
    }

    #[test]
    fn only_plans_for_listed_domains_and_opted_in_http() {
        let mut engine = Engine {
            config: Config { domains: PathBuf::new(), http: true, tls_reorder: true, tls_record_split: false },
            hosts: HostList::parse("example.com"),
            dll_dir: None,
        };
        let mut target = request("www.example.com");
        let target = Packet::parse(&mut target).unwrap();
        let plan = engine.plan(&target).unwrap();
        assert_eq!(plan.split, 3);
        assert!(!plan.tail_first);
        let mut other = request("notexample.com");
        assert!(engine.plan(&Packet::parse(&mut other).unwrap()).is_none());
        engine.config.http = false;
        assert!(engine.plan(&target).is_none());
    }

    #[test]
    fn reorders_the_same_tls_bytes_and_preserves_sequence_and_flags() {
        let mut engine = Engine {
            config: Config { domains: PathBuf::new(), http: false, tls_reorder: false, tls_record_split: false },
            hosts: HostList::parse("example.com"),
            dll_dir: None,
        };
        let hello = sniff::tests::client_hello("www.example.com");
        let mut original = tcp_packet(443, &hello);
        let sni = sniff::tls_sni(&hello, false).unwrap();
        let split = sni.offset + sni.len / 2;
        let sequence = u32::MAX;
        let tail_sequence = sequence.wrapping_add(split as u32);
        original[24..28].copy_from_slice(&sequence.to_be_bytes());
        original[33] = 0x99; // CWR | ACK | PSH | FIN
        let packet = Packet::parse(&mut original).unwrap();
        let plan = engine.plan(&packet).unwrap();
        assert_eq!(plan.split, split);
        assert!(!plan.tail_first);

        engine.config.tls_reorder = true;
        let plan = engine.plan(&packet).unwrap();
        assert_eq!(plan.split, split);
        assert!(plan.tail_first);
        let mut scratch = vec![0; original.len()];
        let mut segments = Vec::new();
        assert!(send_segments(&original, &mut scratch, plan, |packet| {
            segments.push(packet.to_vec());
            true
        }));
        let expected_segments = [
            (&hello[split..], tail_sequence, 0x19),
            (&hello[..split], sequence, 0x90),
        ];
        assert_eq!(segments.len(), expected_segments.len());
        for (bytes, (payload, seq, flags)) in segments.iter_mut().zip(expected_segments) {
            let expected = bytes.clone();
            let mut segment = Packet::parse(bytes).unwrap();
            assert!(segment.has_wire_length());
            assert_eq!(segment.tcp_seq(), Some(seq));
            assert_eq!(segment.tcp_flags(), Some(flags));
            assert_eq!(segment.payload(), payload);
            segment.recompute_checksums();
            assert_eq!(segment.data, expected);
        }
    }

    #[test]
    fn reorders_the_first_segment_of_a_larger_hello_with_a_complete_listed_sni() {
        let mut engine = Engine {
            config: Config { domains: PathBuf::new(), http: false, tls_reorder: false, tls_record_split: false },
            hosts: HostList::parse("example.com"),
            dll_dir: None,
        };
        let hello = sniff::tests::client_hello_with_padding("www.example.com", 1600);
        let mut first_bytes = tcp_packet(443, &hello[..1460]);
        let first = Packet::parse(&mut first_bytes).unwrap();
        assert!(engine.plan(&first).is_none());

        engine.config.tls_reorder = true;
        assert!(engine.plan(&first).unwrap().tail_first);
        let mut continuation_bytes = tcp_packet(443, &hello[1460..]);
        assert!(engine.plan(&Packet::parse(&mut continuation_bytes).unwrap()).is_none());

        engine.hosts = HostList::parse("other.example");
        assert!(engine.plan(&first).is_none());
    }

    #[test]
    fn failed_second_send_leaves_original_available_for_retry() {
        let original = request("example.com");
        let expected = original.clone();
        let mut scratch = vec![0; original.len()];
        let mut segments = Vec::new();
        let plan = Segmentation { header: 40, split: 3, sequence: u32::MAX, flags: 0x19, tail_first: false };
        let complete = send_segments(&original, &mut scratch, plan, |packet| {
            segments.push(packet.to_vec());
            segments.len() == 1
        });
        assert!(!complete);
        assert_eq!(original, expected);
        let (left, right) = segments.split_at_mut(1);
        let first = Packet::parse(&mut left[0]).unwrap();
        let second = Packet::parse(&mut right[0]).unwrap();
        assert!(first.has_wire_length() && second.has_wire_length());
        assert_eq!(first.tcp_flags(), Some(0x10));
        assert_eq!(second.tcp_flags(), Some(0x19));
        assert_eq!(second.tcp_seq(), Some(2));
        assert_eq!([first.payload(), second.payload()].concat(), original[40..]);
    }
}
