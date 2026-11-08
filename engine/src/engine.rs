//! Ana motor döngüsü ve DPI atlatma stratejileri.

use crate::args::Config;
use crate::hostlist::HostList;
use crate::packet::{Packet, IPPROTO_TCP, IPPROTO_UDP};
use crate::sniff;
use crate::windivert::{Address, WinDivert};
use std::collections::HashMap;
use std::io;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::path::PathBuf;
use std::time::{Duration, Instant};

const MAX_PACKET: usize = 65535;

pub struct Stats {
    pub seen: u64,
    pub fragmented: u64,
    pub faked: u64,
    pub dns_redirected: u64,
    pub quic_blocked: u64,
    pub skipped_host: u64,
}

impl Stats {
    fn new() -> Self {
        Stats { seen: 0, fragmented: 0, faked: 0, dns_redirected: 0, quic_blocked: 0, skipped_host: 0 }
    }
}

pub struct Engine {
    cfg: Config,
    list: HostList,
    dll_dir: Option<PathBuf>,
    /// Son görülen DPI hedefi IP'ler (QUIC engellemesi için)
    targets: HashMap<IpAddr, Instant>,
    /// Hedefe göre tahmini TTL (auto-ttl)
    ttl_cache: HashMap<IpAddr, u8>,
}

impl Engine {
    pub fn new(cfg: Config, dll_dir: Option<PathBuf>) -> io::Result<Engine> {
        let list = match &cfg.blacklist {
            Some(p) => HostList::load(p).map_err(|e| {
                io::Error::new(io::ErrorKind::NotFound, format!("site listesi okunamadı ({p}): {e}"))
            })?,
            None => HostList::default(),
        };
        if let Some(p) = &cfg.blacklist {
            if list.is_empty() {
                eprintln!("UYARI: site listesi boş ({p}) - hiçbir siteye dokunulmayacak");
            }
        }
        Ok(Engine { cfg, list, dll_dir, targets: HashMap::new(), ttl_cache: HashMap::new() })
    }

    pub fn run(&mut self) -> io::Result<()> {
        let filter = self.cfg.filter();
        println!("filtre     : {filter}");
        println!(
            "strateji   : frag_http={} frag_https={} sni={} reverse={} fake(seq={},chksum={},ttl={:?},auto={}) quic={}",
            self.cfg.frag_http,
            self.cfg.frag_https,
            self.cfg.frag_by_sni,
            self.cfg.reverse_frag,
            self.cfg.wrong_seq,
            self.cfg.wrong_chksum,
            self.cfg.set_ttl,
            self.cfg.auto_ttl,
            self.cfg.block_quic
        );
        if !self.list.is_empty() {
            println!("site listesi: {} alan adı", self.list.len());
        }

        let wd = WinDivert::open(&filter, self.dll_dir.as_deref(), 0)?;
        println!("WinDivert hazır, trafik izleniyor. Çıkmak için Ctrl+C.");

        let mut buf = vec![0u8; MAX_PACKET];
        let mut addr = Address::zeroed();
        let mut stats = Stats::new();
        let mut last_report = Instant::now();
        let mut dropped_rst = 0u64;

        loop {
            let len = match wd.recv(&mut buf, &mut addr) {
                Ok(0) => continue,
                Ok(n) => n,
                Err(e) => {
                    // Zaman aşımı beklenen durum; diğerlerini bildir
                    if e.raw_os_error() == Some(0) || format!("{e}").contains("timeout") {
                        continue;
                    }
                    return Err(e);
                }
            };
            stats.seen += 1;

            let outbound = addr.outbound();
            let ipv6 = addr.is_ipv6();

            let verdict = {
                let pkt = match Packet::parse(&mut buf[..len]) {
                    Some(p) => p,
                    None => continue,
                };
                if !outbound {
                    // Gelen RST: pasif DPI bloğu açıksa yut
                    if self.cfg.block_passive && pkt.proto == IPPROTO_TCP {
                        if let Some(f) = pkt.tcp_flags() {
                            if f & 0x04 != 0 {
                                dropped_rst += 1;
                                continue;
                            }
                        }
                    }
                    continue;
                }
                match pkt.proto {
                    IPPROTO_UDP => self.handle_udp(pkt, ipv6, &mut stats),
                    IPPROTO_TCP => self.handle_tcp(pkt, &mut stats),
                    _ => Verdict::Forward,
                }
            };

            match verdict {
                Verdict::Forward => {
                    let _ = wd.send(&buf[..len], &addr);
                }
                Verdict::Drop => {}
                Verdict::SendAll(v) => {
                    for part in v {
                        let _ = wd.send(&part, &addr);
                    }
                }
            }

            // Hedef önbelleğini temizle
            let cutoff = Instant::now() - Duration::from_secs(120);
            self.targets.retain(|_, t| *t > cutoff);

            if self.cfg.verbose && last_report.elapsed() >= Duration::from_secs(10) {
                println!(
                    "istatistik: görülen={} parçalanan={} sahte={} dns={} quic_engel={} liste_dışı={} rst_düşen={}",
                    stats.seen,
                    stats.fragmented,
                    stats.faked,
                    stats.dns_redirected,
                    stats.quic_blocked,
                    stats.skipped_host,
                    dropped_rst
                );
                last_report = Instant::now();
            }
        }
    }

    // ------------------------------------------------------------- UDP

    fn handle_udp(&mut self, mut pkt: Packet<'_>, ipv6: bool, stats: &mut Stats) -> Verdict {
        // QUIC / HTTP3
        if pkt.dst_port == 443 {
            if self.cfg.block_quic {
                let dst = if ipv6 { pkt.dst_ip_v6().map(IpAddr::V6) } else { pkt.dst_ip_v4().map(IpAddr::V4) };
                let block = match dst {
                    Some(ip) if !self.list.is_empty() => self.targets.contains_key(&ip),
                    // Liste yoksa: QUIC Initial paketlerini genel olarak düşür
                    _ => sniff::is_quic(pkt.payload()),
                };
                if block {
                    stats.quic_blocked += 1;
                    return Verdict::Drop;
                }
            }
            return Verdict::Forward;
        }

        // DNS yönlendirme
        if pkt.dst_port == 53 {
            let target = if ipv6 {
                self.cfg.dnsv6_addr.clone().map(|a| (a, self.cfg.dnsv6_port.unwrap_or(53)))
            } else {
                self.cfg.dns_addr.clone().map(|a| (a, self.cfg.dns_port.unwrap_or(53)))
            };
            if let Some((addr, port)) = target {
                if !ipv6 {
                    if let Ok(ip) = addr.parse::<Ipv4Addr>() {
                        pkt.set_dst_ip_v4(ip);
                    } else {
                        return Verdict::Forward;
                    }
                } else if let Ok(ip) = addr.parse::<Ipv6Addr>() {
                    pkt.set_dst_ip_v6(ip);
                } else {
                    return Verdict::Forward;
                }
                pkt.set_dst_port(port);
                pkt.recompute_checksums();
                stats.dns_redirected += 1;
                return Verdict::Forward;
            }
        }
        Verdict::Forward
    }

    // ------------------------------------------------------------- TCP

    fn handle_tcp(&mut self, mut pkt: Packet<'_>, stats: &mut Stats) -> Verdict {
        let payload_off = pkt.payload_offset();
        let payload_len = pkt.data.len().saturating_sub(payload_off);
        if payload_len < 4 {
            return Verdict::Forward;
        }
        if self.cfg.max_payload > 0 && payload_len > self.cfg.max_payload as usize {
            return Verdict::Forward;
        }

        // Sahte paket modu yalnızca veri taşıyan segmentlerde anlamlı
        if let Some(f) = pkt.tcp_flags() {
            if f & 0x02 != 0 && payload_len <= 4 {
                return Verdict::Forward; // saf SYN
            }
        }

        // Koklama: ödünç alma kapsamı kısa tutulur, dışarıya kopya çıkar
        let mut sni: Option<(usize, usize)> = None;
        let mut host_header: Option<String> = None;
        {
            let payload = &pkt.data[payload_off..];
            if pkt.dst_port == 443 {
                if let Some(s) = sniff::tls_sni(payload) {
                    sni = Some((s.offset, s.len));
                }
            } else if pkt.dst_port == 80 {
                host_header = sniff::http_host(payload);
            }
        }

        let host: Option<String> = match (&sni, &host_header) {
            (Some(_), _) => {
                let payload = &pkt.data[payload_off..];
                sniff::tls_sni(payload).map(|s| s.host.to_ascii_lowercase())
            }
            (None, Some(h)) => Some(h.clone()),
            _ => None,
        };

        // Site listesi modu: listede olmayan ana makinelere hiç dokunma
        if !self.list.is_empty() {
            match &host {
                Some(h) if self.list.matches(h) => {}
                Some(_) => {
                    stats.skipped_host += 1;
                    return Verdict::Forward;
                }
                // Ana makine görünmüyorsa (şifreli/parçalı) dokunma
                None => return Verdict::Forward,
            }
        }

        // QUIC engellemesi için hedef IP'yi hatırla
        if host.is_some() {
            let ip = if pkt.v6 { pkt.dst_ip_v6().map(IpAddr::V6) } else { pkt.dst_ip_v4().map(IpAddr::V4) };
            if let Some(ip) = ip {
                self.targets.insert(ip, Instant::now());
            }
        }

        // HTTP başlık oynamaları (-s / -r) yalnızca düz HTTP'de uygulanır
        if host_header.is_some() && self.mangle_http(&mut pkt) {
            let out = pkt.data.to_vec();
            return Verdict::SendAll(vec![out]);
        }

        // Parçalama
        let split = self.split_position(sni, payload_len);
        let parts = match split {
            Some(pos) if pos > 0 && pos < payload_len => self.fragment(&pkt, pos),
            _ => Vec::new(),
        };
        if parts.is_empty() {
            return Verdict::Forward;
        }
        stats.fragmented += 1;

        let mut out = parts;
        // Sahte paketler: parçalardan önce gönderilir
        if self.cfg.wrong_seq || self.cfg.wrong_chksum || self.cfg.set_ttl.is_some() || self.cfg.auto_ttl {
            if let Some(fake) = self.make_fake(&pkt) {
                for _ in 0..self.cfg.fake_resend.max(1) {
                    out.insert(0, fake.clone());
                }
                stats.faked += 1;
            }
        }

        Verdict::SendAll(out)
    }

    /// Yükün neresinden bölünecek? (yük içi ofset)
    fn split_position(&self, sni: Option<(usize, usize)>, payload_len: usize) -> Option<usize> {
        if let Some((off, len)) = sni {
            // frag_by_sni: alan adının hemen öncesinden böl
            if self.cfg.frag_by_sni {
                return Some(off.saturating_sub(1).max(1));
            }
            let n = self.cfg.frag_https as usize;
            if n == 0 {
                return None;
            }
            // Alan adının İÇİNDEN böl: "di|scord.com" — DPI alan adını bütün göremez
            let inside = off + n.min(len.saturating_sub(1).max(1));
            if inside > 1 && inside < payload_len {
                return Some(inside);
            }
            return Some(off.saturating_sub(1).max(1));
        }
        let n = self.cfg.frag_http as usize;
        if n == 0 || n >= payload_len {
            return None;
        }
        Some(n)
    }

    /// Paketi iki TCP segmentine böler (checksum'lar yeniden hesaplanır).
    fn fragment(&self, pkt: &Packet<'_>, split_at_payload: usize) -> Vec<Vec<u8>> {
        let payload_off = pkt.payload_offset();
        let split = payload_off + split_at_payload;
        if split >= pkt.data.len() {
            return Vec::new();
        }
        let mut first = pkt.data[..split].to_vec();
        // İkinci parça: IP + TCP başlıkları + kalan yük
        let mut second = pkt.data[..payload_off].to_vec();
        second.extend_from_slice(&pkt.data[split..]);
        let second_len = second.len();
        {
            let mut p2 = match Packet::parse(&mut second) {
                Some(p) => p,
                None => return Vec::new(),
            };
            p2.set_total_len(second_len);
            p2.set_tcp_seq(pkt.tcp_seq().unwrap_or(0).wrapping_add(split_at_payload as u32));
            p2.recompute_checksums();
        }
        // İlk parça
        {
            let mut p1 = match Packet::parse(&mut first) {
                Some(p) => p,
                None => return Vec::new(),
            };
            p1.set_total_len(split);
            p1.recompute_checksums();
        }

        if self.cfg.reverse_frag {
            vec![second, first]
        } else {
            vec![first, second]
        }
    }

    /// Hedefi hiç görmeyecek sahte paket üretir (yanlış TTL / SEQ / checksum).
    fn make_fake(&mut self, pkt: &Packet<'_>) -> Option<Vec<u8>> {
        let mut fake = pkt.data.to_vec();
        {
            let mut p = Packet::parse(&mut fake)?;
            if self.cfg.wrong_seq {
                let seq = p.tcp_seq().unwrap_or(0);
                p.set_tcp_seq(seq.wrapping_sub(0x10000));
            }
            if let Some(ttl) = self.cfg.set_ttl {
                p.set_ttl(ttl);
            } else if self.cfg.auto_ttl {
                let ip = if p.v6 { p.dst_ip_v6().map(IpAddr::V6) } else { p.dst_ip_v4().map(IpAddr::V4) };
                let ttl = ip
                    .and_then(|i| self.ttl_cache.get(&i).copied())
                    .unwrap_or(1u8.max(self.cfg.min_ttl));
                p.set_ttl(ttl);
            }
            p.recompute_checksums();
        }
        if self.cfg.wrong_chksum {
            // TCP checksum alanını bilerek boz (TCP başlığında ofset 16..17)
            let off = pkt.ip_hdr_len + 16;
            if fake.len() >= off + 2 {
                let cur = u16::from_be_bytes([fake[off], fake[off + 1]]);
                let bad = cur ^ 0x00ff;
                fake[off..off + 2].copy_from_slice(&bad.to_be_bytes());
            }
        }
        Some(fake)
    }

    /// HTTP Host başlığını oynatır; değişiklik olduysa true döner.
    fn mangle_http(&self, pkt: &mut Packet<'_>) -> bool {
        if !self.cfg.strip_host_space && !self.cfg.mix_host_case {
            return false;
        }
        let off = pkt.payload_offset();
        let end = pkt.data.len().min(off + 2048);
        let mut changed = false;
        let mut i = off;
        while i + 5 < end {
            let window = &pkt.data[i..(i + 5).min(end)];
            if window.eq_ignore_ascii_case(b"Host:") {
                let j = i + 5;
                // baştaki boşluğu kaldır (-s)
                if self.cfg.strip_host_space && j < end && pkt.data[j] == b' ' {
                    let mut k = j;
                    while k + 1 < end {
                        pkt.data[k] = pkt.data[k + 1];
                        k += 1;
                    }
                    pkt.data[end - 1] = b' ';
                    changed = true;
                    continue;
                }
                // hoSt biçimine çevir (-r)
                if self.cfg.mix_host_case {
                    let head = pkt.data[i].to_ascii_lowercase();
                    if head == b'h' {
                        pkt.data[i + 1] = b'o';
                        pkt.data[i + 2] = b'S';
                        pkt.data[i + 3] = b't';
                        changed = true;
                    }
                }
                break;
            }
            i += 1;
        }
        if changed {
            pkt.recompute_checksums();
        }
        changed
    }
}

enum Verdict {
    Forward,
    Drop,
    SendAll(Vec<Vec<u8>>),
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::packet::{IPV4_HDR_MIN, TCP_HDR_MIN};

    fn sample_packet(payload: &[u8]) -> Vec<u8> {
        let mut p = vec![0u8; IPV4_HDR_MIN + TCP_HDR_MIN + payload.len()];
        p[0] = 0x45;
        let total = p.len() as u16;
        p[2..4].copy_from_slice(&total.to_be_bytes());
        p[8] = 64;
        p[9] = IPPROTO_TCP;
        p[12..16].copy_from_slice(&[10, 0, 0, 1]);
        p[16..20].copy_from_slice(&[1, 1, 1, 1]);
        let t = IPV4_HDR_MIN;
        p[t..t + 2].copy_from_slice(&40000u16.to_be_bytes());
        p[t + 2..t + 4].copy_from_slice(&443u16.to_be_bytes());
        p[t + 4..t + 8].copy_from_slice(&5000u32.to_be_bytes());
        p[t + 12] = 5 << 4;
        p[t + 13] = 0x18;
        p[IPV4_HDR_MIN + TCP_HDR_MIN..].copy_from_slice(payload);
        crate::packet::recompute_checksums(&mut p, IPV4_HDR_MIN, IPPROTO_TCP, false);
        p
    }

    #[test]
    fn fragment_produces_two_valid_ip_packets() {
        let mut raw = sample_packet(b"ABCDEFGHIJ");
        let orig_seq = 5000u32;
        let engine = Engine::new(Config::default(), None).unwrap();
        let pkt = Packet::parse(&mut raw).unwrap();

        let frags = engine.fragment(&pkt, 3);
        assert_eq!(frags.len(), 2, "2 parça üretilmeli");

        let mut p1_data = frags[0].clone();
        let p1 = Packet::parse(&mut p1_data).expect("1. parça geçerli IP olmalı");
        assert_eq!(p1.payload(), b"ABC");
        assert_eq!(p1.tcp_seq().unwrap(), orig_seq);

        let mut p2_data = frags[1].clone();
        let p2 = Packet::parse(&mut p2_data).expect("2. parça geçerli IP olmalı");
        assert_eq!(p2.payload(), b"DEFGHIJ");
        assert_eq!(p2.tcp_seq().unwrap(), orig_seq + 3);
    }

    #[test]
    fn make_fake_corrupts_checksum_cleanly() {
        let mut raw = sample_packet(b"TESTFAKE");
        let mut cfg = Config::default();
        cfg.wrong_chksum = true;
        cfg.wrong_seq = true;
        let mut engine = Engine::new(cfg, None).unwrap();
        let pkt = Packet::parse(&mut raw).unwrap();

        let fake = engine.make_fake(&pkt).expect("sahte paket üretilmeli");
        let mut fake_clone = fake.clone();
        let p = Packet::parse(&mut fake_clone).expect("sahte paket geçerli IP/TCP yapısında olmalı");
        assert_ne!(p.tcp_seq().unwrap(), 5000u32, "SEQ değiştirilmiş olmalı");
    }
}

