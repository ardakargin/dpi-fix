//! IP / TCP / UDP paket ayrıştırma, kurma ve sağlama toplamı (checksum) hesapları.
//!
//! Tüm işlemler yerinde (in-place) yapılır; ek kopya çıkarılmaz.

#![allow(dead_code)]

use std::net::{Ipv4Addr, Ipv6Addr};

pub const IPPROTO_TCP: u8 = 6;
pub const IPPROTO_UDP: u8 = 17;

pub const IPV4_HDR_MIN: usize = 20;
pub const IPV6_HDR: usize = 40;
pub const TCP_HDR_MIN: usize = 20;
pub const UDP_HDR: usize = 8;

/// Ayrıştırılmış paket görünümü (veriye referans tutar)
pub struct Packet<'a> {
    pub data: &'a mut [u8],
    pub v6: bool,
    pub ip_hdr_len: usize,
    pub proto: u8,
    pub src_port: u16,
    pub dst_port: u16,
}

impl<'a> Packet<'a> {
    /// IPv4/IPv6 paketi ayrıştırır. Tanınmayan/eksik pakette None döner.
    pub fn parse(data: &'a mut [u8]) -> Option<Packet<'a>> {
        if data.is_empty() {
            return None;
        }
        match data[0] >> 4 {
            4 => {
                if data.len() < IPV4_HDR_MIN {
                    return None;
                }
                let ihl = ((data[0] & 0x0f) as usize) * 4;
                if ihl < IPV4_HDR_MIN || data.len() < ihl {
                    return None;
                }
                let proto = data[9];
                let (sp, dp) = ports(data, ihl, proto)?;
                Some(Packet { data, v6: false, ip_hdr_len: ihl, proto, src_port: sp, dst_port: dp })
            }
            6 => {
                if data.len() < IPV6_HDR {
                    return None;
                }
                let proto = data[6];
                let (sp, dp) = ports(data, IPV6_HDR, proto)?;
                Some(Packet { data, v6: true, ip_hdr_len: IPV6_HDR, proto, src_port: sp, dst_port: dp })
            }
            _ => None,
        }
    }

    /// Taşıma katmanı yükü (TCP/UDP gövdesi)
    pub fn payload_offset(&self) -> usize {
        self.ip_hdr_len + transport_hdr_len(self.data, self.ip_hdr_len, self.proto)
    }

    pub fn has_wire_length(&self) -> bool {
        if self.v6 {
            usize::from(u16::from_be_bytes([self.data[4], self.data[5]])) + IPV6_HDR == self.data.len()
        } else {
            usize::from(u16::from_be_bytes([self.data[2], self.data[3]])) == self.data.len()
        }
    }

    pub fn payload(&self) -> &[u8] {
        &self.data[self.payload_offset()..]
    }

    pub fn payload_mut(&mut self) -> &mut [u8] {
        let off = self.payload_offset();
        &mut self.data[off..]
    }

    /// Toplam IP paket uzunluğunu günceller (bölme sonrası gerekir)
    pub fn set_total_len(&mut self, len: usize) {
        if self.v6 {
            let v = ((len - IPV6_HDR) as u16).to_be_bytes();
            self.data[4] = v[0];
            self.data[5] = v[1];
        } else {
            let v = (len as u16).to_be_bytes();
            self.data[2] = v[0];
            self.data[3] = v[1];
        }
    }

    /// TCP bayrakları (SYN/ACK/RST/FIN/PSH)
    pub fn tcp_flags(&self) -> Option<u8> {
        if self.proto != IPPROTO_TCP {
            return None;
        }
        Some(self.data[self.ip_hdr_len + 13])
    }

    /// TCP sıra numarası
    pub fn tcp_seq(&self) -> Option<u32> {
        if self.proto != IPPROTO_TCP {
            return None;
        }
        let o = self.ip_hdr_len + 4;
        Some(u32::from_be_bytes([self.data[o], self.data[o + 1], self.data[o + 2], self.data[o + 3]]))
    }

    pub fn set_tcp_seq(&mut self, seq: u32) {
        let o = self.ip_hdr_len + 4;
        self.data[o..o + 4].copy_from_slice(&seq.to_be_bytes());
    }

    /// Yükün ilk baytından sonuna kadar TCP SEQ'i ilerletir
    pub fn advance_tcp_seq(&mut self, by: u32) {
        if let Some(s) = self.tcp_seq() {
            self.set_tcp_seq(s.wrapping_add(by));
        }
    }

    pub fn ttl(&self) -> Option<u8> {
        if self.v6 { Some(self.data[7]) } else { Some(self.data[8]) }
    }

    pub fn set_ttl(&mut self, v: u8) {
        if self.v6 {
            self.data[7] = v;
        } else {
            self.data[8] = v;
        }
    }

    pub fn src_ip_v4(&self) -> Option<Ipv4Addr> {
        if self.v6 {
            return None;
        }
        Some(Ipv4Addr::new(self.data[12], self.data[13], self.data[14], self.data[15]))
    }

    pub fn dst_ip_v4(&self) -> Option<Ipv4Addr> {
        if self.v6 {
            return None;
        }
        Some(Ipv4Addr::new(self.data[16], self.data[17], self.data[18], self.data[19]))
    }

    pub fn src_ip_v6(&self) -> Option<Ipv6Addr> {
        if !self.v6 {
            return None;
        }
        let mut b = [0u8; 16];
        b.copy_from_slice(&self.data[8..24]);
        Some(Ipv6Addr::from(b))
    }

    pub fn dst_ip_v6(&self) -> Option<Ipv6Addr> {
        if !self.v6 {
            return None;
        }
        let mut b = [0u8; 16];
        b.copy_from_slice(&self.data[24..40]);
        Some(Ipv6Addr::from(b))
    }

    pub fn set_dst_ip_v4(&mut self, ip: Ipv4Addr) {
        self.data[16..20].copy_from_slice(&ip.octets());
    }

    pub fn set_dst_ip_v6(&mut self, ip: Ipv6Addr) {
        self.data[24..40].copy_from_slice(&ip.octets());
    }

    pub fn set_dst_port(&mut self, port: u16) {
        let o = self.ip_hdr_len + 2;
        self.data[o..o + 2].copy_from_slice(&port.to_be_bytes());
    }

    /// Paketin tüm sağlama toplamlarını yeniden hesaplar.
    pub fn recompute_checksums(&mut self) {
        recompute_checksums(self.data, self.ip_hdr_len, self.proto, self.v6);
    }
}

fn ports(data: &[u8], ip_hdr_len: usize, proto: u8) -> Option<(u16, u16)> {
    if proto != IPPROTO_TCP && proto != IPPROTO_UDP {
        return None;
    }
    if data.len() < ip_hdr_len + if proto == IPPROTO_TCP { TCP_HDR_MIN } else { UDP_HDR } {
        return None;
    }
    if ip_hdr_len + transport_hdr_len(data, ip_hdr_len, proto) > data.len() {
        return None;
    }
    let sp = u16::from_be_bytes([data[ip_hdr_len], data[ip_hdr_len + 1]]);
    let dp = u16::from_be_bytes([data[ip_hdr_len + 2], data[ip_hdr_len + 3]]);
    Some((sp, dp))
}

pub fn transport_hdr_len(data: &[u8], ip_hdr_len: usize, proto: u8) -> usize {
    match proto {
        IPPROTO_TCP => {
            if data.len() < ip_hdr_len + TCP_HDR_MIN {
                return TCP_HDR_MIN;
            }
            (((data[ip_hdr_len + 12] >> 4) as usize) * 4).max(TCP_HDR_MIN)
        }
        IPPROTO_UDP => UDP_HDR,
        _ => 0,
    }
}

// ---------------------------------------------------------------- checksums

/// Ones-complement toplamı (RFC 1071)
pub fn ones_complement_sum(data: &[u8], init: u32) -> u32 {
    let mut sum = init;
    let mut chunks = data.chunks_exact(2);
    for c in &mut chunks {
        sum += u16::from_be_bytes([c[0], c[1]]) as u32;
    }
    if let [last] = chunks.remainder() {
        sum += (*last as u32) << 8;
    }
    sum
}

pub fn fold(mut sum: u32) -> u16 {
    while sum >> 16 != 0 {
        sum = (sum & 0xffff) + (sum >> 16);
    }
    !(sum as u16)
}

/// IPv4 başlık sağlama toplamı
pub fn ipv4_header_checksum(header: &[u8]) -> u16 {
    fold(ones_complement_sum(header, 0))
}

fn pseudo_header_v4(data: &[u8], proto: u8, len: u16) -> u32 {
    let mut sum = 0u32;
    sum += u16::from_be_bytes([data[12], data[13]]) as u32;
    sum += u16::from_be_bytes([data[14], data[15]]) as u32;
    sum += u16::from_be_bytes([data[16], data[17]]) as u32;
    sum += u16::from_be_bytes([data[18], data[19]]) as u32;
    sum += proto as u32;
    sum += len as u32;
    sum
}

fn pseudo_header_v6(data: &[u8], proto: u8, len: u32) -> u32 {
    let mut sum = 0u32;
    for i in 0..16 {
        if i % 2 == 0 {
            sum += u16::from_be_bytes([data[8 + i], data[8 + i + 1]]) as u32;
            sum += u16::from_be_bytes([data[24 + i], data[24 + i + 1]]) as u32;
        }
    }
    sum += ((len >> 16) & 0xffff) as u32;
    sum += (len & 0xffff) as u32;
    sum += proto as u32;
    sum
}

/// Hesaplanan sağlama toplamını ilgili alana yazar.
pub fn recompute_checksums(data: &mut [u8], ip_hdr_len: usize, proto: u8, v6: bool) {
    if !v6 {
        // IPv4 başlık toplamı
        data[10] = 0;
        data[11] = 0;
        let c = ipv4_header_checksum(&data[..ip_hdr_len]);
        data[10..12].copy_from_slice(&c.to_be_bytes());
    }

    if proto == IPPROTO_TCP || proto == IPPROTO_UDP {
        let hdr_len = transport_hdr_len(data, ip_hdr_len, proto);
        let payload_len = data.len().saturating_sub(ip_hdr_len + hdr_len);
        let transport_len = hdr_len + payload_len;

        let csum_off = ip_hdr_len + if proto == IPPROTO_TCP { 16 } else { 6 };
        if data.len() < csum_off + 2 {
            return;
        }
        data[csum_off] = 0;
        data[csum_off + 1] = 0;

        let init = if v6 {
            pseudo_header_v6(data, proto, transport_len as u32)
        } else {
            pseudo_header_v4(data, proto, transport_len as u16)
        };
        let sum = ones_complement_sum(&data[ip_hdr_len..], init);
        let mut c = fold(sum);
        // UDP'de 0 sonucu "checksum yok" demektir; 0xffff yazmak zorunlu
        if proto == IPPROTO_UDP && c == 0 {
            c = 0xffff;
        }
        data[csum_off..csum_off + 2].copy_from_slice(&c.to_be_bytes());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Basit IPv4/TCP paketi kurar (payload verilen)
    fn build_v4(payload: &[u8]) -> Vec<u8> {
        let mut p = vec![0u8; IPV4_HDR_MIN + TCP_HDR_MIN + payload.len()];
        p[0] = 0x45;
        let total = p.len() as u16;
        p[2..4].copy_from_slice(&total.to_be_bytes());
        p[8] = 64; // TTL
        p[9] = IPPROTO_TCP;
        p[12..16].copy_from_slice(&[192, 168, 1, 10]);
        p[16..20].copy_from_slice(&[93, 184, 216, 34]);
        let t = IPV4_HDR_MIN;
        p[t..t + 2].copy_from_slice(&51000u16.to_be_bytes());
        p[t + 2..t + 4].copy_from_slice(&443u16.to_be_bytes());
        p[t + 4..t + 8].copy_from_slice(&1000u32.to_be_bytes());
        p[t + 12] = 5 << 4; // data offset
        p[t + 13] = 0x18; // PSH|ACK
        p[IPV4_HDR_MIN + TCP_HDR_MIN..].copy_from_slice(payload);
        p
    }

    #[test]
    fn parses_ipv4_tcp() {
        let mut buf = build_v4(b"hello");
        let p = Packet::parse(&mut buf).expect("paket ayrıştırılamadı");
        assert!(!p.v6);
        assert_eq!(p.proto, IPPROTO_TCP);
        assert_eq!(p.dst_port, 443);
        assert_eq!(p.src_port, 51000);
        assert_eq!(p.payload(), b"hello");
        assert_eq!(p.tcp_flags().unwrap(), 0x18);
    }

    #[test]
    fn checksum_roundtrip_is_valid() {
        let mut buf = build_v4(b"hello world");
        let hdr = IPV4_HDR_MIN;
        recompute_checksums(&mut buf, hdr, IPPROTO_TCP, false);
        // Hesaplanan toplamları yeniden doğrula: toplam 0xffff olmalı
        let mut check = buf.clone();
        check[10] = 0;
        check[11] = 0;
        let sum = fold(ones_complement_sum(&check[..hdr], 0));
        assert_eq!(sum, u16::from_be_bytes([buf[10], buf[11]]));
        // TCP toplamı: geçerli bir checksum, taşıma katmanı toplamı + pseudo header
        // birin tümleyeni alındığında 0 verir. Bozuk paket 0 vermez.
        let tsum = fold(ones_complement_sum(
            &buf[hdr..],
            pseudo_header_v4(&buf, IPPROTO_TCP, (buf.len() - hdr) as u16),
        ));
        assert_eq!(tsum, 0, "geçerli TCP checksum 0 vermeli");

        // Tek bayt bozulduğunda yakalanıyor mu?
        let mut bad = buf.clone();
        bad[hdr + 20] ^= 0xff; // yükün ilk baytı
        let bsum = fold(ones_complement_sum(
            &bad[hdr..],
            pseudo_header_v4(&bad, IPPROTO_TCP, (bad.len() - hdr) as u16),
        ));
        assert_ne!(bsum, 0, "bozulmuş paket doğrulamadan geçmemeli");
    }

    #[test]
    fn total_len_can_be_updated() {
        let mut buf = build_v4(b"abcdefgh");
        let mut p = Packet::parse(&mut buf).unwrap();
        p.set_total_len(40);
        assert_eq!(u16::from_be_bytes([buf[2], buf[3]]), 40);
    }

    #[test]
    fn corrupt_packets_return_none() {
        let mut empty: Vec<u8> = vec![];
        assert!(Packet::parse(&mut empty).is_none());
        let mut short = vec![0x45u8; 10];
        assert!(Packet::parse(&mut short).is_none());
        let mut udp_like = build_v4(b"x");
        udp_like[9] = 47; // GRE: taşıma katmanı yok
        assert!(Packet::parse(&mut udp_like).is_none());
    }

    #[test]
    fn ipv6_packet_parses() {
        let mut buf = vec![0u8; IPV6_HDR + TCP_HDR_MIN + 3];
        buf[0] = 0x60;
        buf[6] = IPPROTO_TCP;
        buf[7] = 64;
        let t = IPV6_HDR;
        buf[t..t + 2].copy_from_slice(&12345u16.to_be_bytes());
        buf[t + 2..t + 4].copy_from_slice(&80u16.to_be_bytes());
        buf[t + 12] = 5 << 4;
        let p = Packet::parse(&mut buf).unwrap();
        assert!(p.v6);
        assert_eq!(p.dst_port, 80);
        assert_eq!(p.payload().len(), 3);
    }

    #[test]
    fn ipv6_length_field_excludes_the_base_header() {
        let mut buf = [0; IPV6_HDR + TCP_HDR_MIN + 3];
        buf[0] = 0x60;
        buf[6] = IPPROTO_TCP;
        buf[IPV6_HDR + 12] = 0x50;
        let length = buf.len();
        let mut packet = Packet::parse(&mut buf).unwrap();
        packet.set_total_len(length);
        assert!(packet.has_wire_length());
        assert_eq!(&packet.data[4..6], &[0, 23]);
    }
}
