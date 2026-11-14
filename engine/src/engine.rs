//! Domain-scoped TCP engine. Unrecognized traffic is forwarded unchanged.
use crate::{args::Config, hostlist::HostList, packet::Packet, sniff, windivert::{Address, WinDivert}};
use std::{io, path::PathBuf};

const MAX_PACKET: usize = 65_575;

pub struct Engine {
    config: Config,
    hosts: HostList,
    dll_dir: Option<PathBuf>,
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
        if self.config.http {
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
            if let Some((header, split, seq, flags)) = plan {
                let mut modified_address = address;
                modified_address.mark_tcp_checksums(original[0] >> 4 == 4);
                if send_segments(original, &mut segment, header, split, seq, flags, |packet| {
                    capture.send(packet, &modified_address)
                        .is_ok_and(|sent| sent == packet.len())
                }) {
                    continue;
                }
            }
            capture.send(original, &address)?;
        }
    }

    fn plan(&self, packet: &Packet<'_>) -> Option<(usize, usize, u32, u8)> {
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
            443 => {
                let sni = sniff::tls_sni(payload)?;
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
        Some((packet.payload_offset(), split, packet.tcp_seq()?, flags))
    }
}

fn send_segments(
    original: &[u8], scratch: &mut [u8], header: usize, split: usize, sequence: u32,
    flags: u8, mut send: impl FnMut(&[u8]) -> bool,
) -> bool {
    let boundary = header + split;
    scratch[..boundary].copy_from_slice(&original[..boundary]);
    let Some(mut first) = Packet::parse(&mut scratch[..boundary]) else { return false; };
    prepare(&mut first, sequence, flags & !0x09);
    if !send(first.data) {
        return false;
    }

    // The first segment already left. Returning false lets the caller retry
    // the original bytes at their original sequence number.
    let second_len = original.len() - split;
    scratch[..header].copy_from_slice(&original[..header]);
    scratch[header..second_len].copy_from_slice(&original[boundary..]);
    let Some(mut second) = Packet::parse(&mut scratch[..second_len]) else { return false; };
    prepare(&mut second, sequence.wrapping_add(split as u32), flags & !0x80);
    send(second.data)
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
        let mut bytes = vec![0; 40];
        bytes[0] = 0x45;
        bytes[9] = 6;
        bytes[23] = 80;
        bytes[32] = 0x50;
        bytes[33] = 0x18;
        bytes.extend_from_slice(payload.as_bytes());
        let length = bytes.len() as u16;
        bytes[2..4].copy_from_slice(&length.to_be_bytes());
        bytes
    }

    #[test]
    fn only_plans_for_listed_domains_and_opted_in_http() {
        let mut engine = Engine {
            config: Config { domains: PathBuf::new(), http: true },
            hosts: HostList::parse("example.com"),
            dll_dir: None,
        };
        let mut target = request("www.example.com");
        let target = Packet::parse(&mut target).unwrap();
        assert_eq!(engine.plan(&target), Some((40, 3, 0, 0x18)));
        let mut other = request("notexample.com");
        assert!(engine.plan(&Packet::parse(&mut other).unwrap()).is_none());
        engine.config.http = false;
        assert!(engine.plan(&target).is_none());
    }

    #[test]
    fn failed_second_send_leaves_original_available_for_retry() {
        let original = request("example.com");
        let expected = original.clone();
        let mut scratch = vec![0; original.len()];
        let mut segments = Vec::new();
        let complete = send_segments(&original, &mut scratch, 40, 3, u32::MAX, 0x19, |packet| {
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
