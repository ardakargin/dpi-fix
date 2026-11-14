//! Protokol koklama: TLS ClientHello içinden SNI, HTTP isteğinden Host çıkarımı.

/// TLS ClientHello içindeki SNI alanının (hostname) konumu ve değeri
pub struct Sni<'a> {
    pub host: &'a str,
    /// SNI baytlarının taşıma katmanı yükü içindeki başlangıç ofseti
    pub offset: usize,
    pub len: usize,
}

const TLS_HANDSHAKE: u8 = 0x16;
const TLS_CLIENT_HELLO: u8 = 0x01;
const EXT_SERVER_NAME: u16 = 0x0000;

/// TLS ClientHello ise SNI bilgisini döndürür.
pub fn tls_sni(payload: &[u8]) -> Option<Sni<'_>> {
    if payload.len() < 6 || payload[0] != TLS_HANDSHAKE {
        return None;
    }
    let record_len = be16(payload, 3)? as usize;
    let rec_end = 5 + record_len;
    if record_len < 4 || rec_end > payload.len() {
        return None;
    }
    // Handshake başlığı
    if payload[5] != TLS_CLIENT_HELLO {
        return None;
    }
    let hs_len = be24(payload, 6)? as usize;
    let hs_end = 9 + hs_len;
    if hs_end > rec_end {
        return None;
    }

    let mut p = 9usize;
    // client_version(2) + random(32)
    p = p.checked_add(34)?;
    if p + 1 > hs_end {
        return None;
    }
    // session_id
    let sid_len = payload[p] as usize;
    p = p.checked_add(1 + sid_len)?;
    if p + 2 > hs_end {
        return None;
    }
    // cipher_suites
    let cs_len = be16(payload, p)? as usize;
    p = p.checked_add(2 + cs_len)?;
    if p + 1 > hs_end {
        return None;
    }
    // compression_methods
    let comp_len = payload[p] as usize;
    p = p.checked_add(1 + comp_len)?;
    if p + 2 > hs_end {
        return None;
    }
    // extensions
    let ext_total = be16(payload, p)? as usize;
    p += 2;
    let ext_end = p + ext_total;
    if ext_end != hs_end {
        return None;
    }

    while p + 4 <= ext_end {
        let etype = be16(payload, p)?;
        let elen = be16(payload, p + 2)? as usize;
        let edata = p + 4;
        if edata + elen > ext_end {
            break;
        }
        if etype == EXT_SERVER_NAME {
            // server_name_list: list_len(2), type(1), name_len(2), name
            if elen < 2 {
                return None;
            }
            let list_len = be16(payload, edata)? as usize;
            let mut q = edata + 2;
            let list_end = q + list_len;
            if list_end != edata + elen {
                return None;
            }
            while q + 3 <= list_end {
                let ntype = payload[q];
                let nlen = be16(payload, q + 1)? as usize;
                let nstart = q + 3;
                if nstart + nlen > list_end {
                    break;
                }
                if ntype == 0 && nlen > 0 {
                    let host = std::str::from_utf8(&payload[nstart..nstart + nlen]).ok()?;
                    return Some(Sni { host, offset: nstart, len: nlen });
                }
                q = nstart + nlen;
            }
        }
        p = edata + elen;
    }
    None
}

/// HTTP isteği ise Host başlığını döndürür (küçük harfe çevrilmiş).
pub fn http_host(payload: &[u8]) -> Option<String> {
    const METHODS: [&[u8]; 7] = [
        b"GET ", b"POST ", b"HEAD ", b"PUT ", b"DELETE ", b"OPTIONS ", b"PATCH ",
    ];
    if payload.len() < 16 || !METHODS.iter().any(|m| payload.starts_with(m)) {
        return None;
    }
    // Başlık bloğu (ilk 8 KiB yeterli)
    let head = &payload[..payload.len().min(8192)];
    let text = std::str::from_utf8(head).ok()?;
    for line in text.split("\r\n") {
        let lower = line.to_ascii_lowercase();
        if let Some(rest) = lower.strip_prefix("host:") {
            let host = rest.trim();
            let host = host.split(':').next().unwrap_or(host);
            if !host.is_empty() {
                return Some(host.to_string());
            }
        }
        if line.is_empty() {
            break;
        }
    }
    None
}

fn be16(b: &[u8], o: usize) -> Option<u16> {
    if o + 2 > b.len() {
        return None;
    }
    Some(u16::from_be_bytes([b[o], b[o + 1]]))
}

fn be24(b: &[u8], o: usize) -> Option<u32> {
    if o + 3 > b.len() {
        return None;
    }
    Some(((b[o] as u32) << 16) | ((b[o + 1] as u32) << 8) | b[o + 2] as u32)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Gerçek bir ClientHello'yu taklit eden minimal kayıt üretir
    fn client_hello(host: &str) -> Vec<u8> {
        let mut ext = Vec::new();
        // server_name uzantısı
        let mut sni = Vec::new();
        sni.push(0u8); // host_name
        sni.extend((host.len() as u16).to_be_bytes());
        sni.extend(host.as_bytes());
        let mut sni_ext = Vec::new();
        sni_ext.extend((sni.len() as u16).to_be_bytes()); // list length
        sni_ext.extend(&sni);
        ext.extend(EXT_SERVER_NAME.to_be_bytes());
        ext.extend((sni_ext.len() as u16).to_be_bytes());
        ext.extend(&sni_ext);

        let mut body = Vec::new();
        body.extend([0x03, 0x03]); // client version
        body.extend([0x11u8; 32]); // random
        body.push(0); // session id len
        body.extend(2u16.to_be_bytes()); // cipher suites len
        body.extend([0x13, 0x01]); // TLS_AES_128_GCM_SHA256
        body.push(1); // compression len
        body.push(0); // null
        body.extend((ext.len() as u16).to_be_bytes());
        body.extend(&ext);

        let mut hs = Vec::new();
        hs.push(TLS_CLIENT_HELLO);
        let l = body.len() as u32;
        hs.extend([(l >> 16) as u8, (l >> 8) as u8, l as u8]);
        hs.extend(&body);

        let mut rec = Vec::new();
        rec.push(TLS_HANDSHAKE);
        rec.extend([0x03, 0x01]);
        rec.extend((hs.len() as u16).to_be_bytes());
        rec.extend(&hs);
        rec
    }

    #[test]
    fn extracts_sni() {
        let pkt = client_hello("discord.com");
        let sni = tls_sni(&pkt).expect("SNI bulunamadı");
        assert_eq!(sni.host, "discord.com");
        // ofset gerçekten host adını gösteriyor mu?
        assert_eq!(&pkt[sni.offset..sni.offset + sni.len], b"discord.com");
    }

    #[test]
    fn sni_offset_points_before_payload_end() {
        let pkt = client_hello("www.instagram.com");
        let sni = tls_sni(&pkt).unwrap();
        assert!(sni.offset > 43, "ofset handshake başlığından sonra olmalı");
        assert!(sni.offset + sni.len <= pkt.len());
    }

    #[test]
    fn rejects_non_tls() {
        assert!(tls_sni(b"GET / HTTP/1.1\r\n").is_none());
        assert!(tls_sni(&[]).is_none());
        assert!(tls_sni(&[0x16, 0x03, 0x01, 0x00, 0x05, 0x01]).is_none());
    }

    #[test]
    fn incomplete_tls_record_is_left_unmodified() {
        let mut hello = client_hello("example.com");
        let length = u16::from_be_bytes([hello[3], hello[4]]) + 1;
        hello[3..5].copy_from_slice(&length.to_be_bytes());
        assert!(tls_sni(&hello).is_none());
    }

    #[test]
    fn extracts_http_host() {
        let req = b"GET /index.html HTTP/1.1\r\nHost: x.com\r\nUser-Agent: t\r\n\r\n";
        assert_eq!(http_host(req).as_deref(), Some("x.com"));
        let req2 = b"GET / HTTP/1.1\r\nhost: API.Example.COM:8443\r\n\r\n";
        assert_eq!(http_host(req2).as_deref(), Some("api.example.com"));
        assert!(http_host(b"NOTAMETHOD x\r\n").is_none());
    }

}
