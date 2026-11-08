//! Komut satırı ayrıştırma.
//!
//! GoodbyeDPI'ın kullandığı bayraklarla uyumludur; DpiFix arayüzü motoru
//! aynı argümanlarla çalıştırdığı için mevcut yapılandırmalar bozulmaz.

#[derive(Clone, Debug)]
pub struct Config {
    // --- parçalama ---
    /// HTTP parçalama boyutu (0 = kapalı)
    pub frag_http: u32,
    /// HTTPS/TLS parçalama boyutu (0 = kapalı)
    pub frag_https: u32,
    /// TLS paketini SNI'dan hemen önce böl
    pub frag_by_sni: bool,
    /// Parçaları ters sırada gönder (önce kuyruk)
    pub reverse_frag: bool,
    /// Bu boyuttan büyük TCP yüklerine dokunma (0 = sınırsız)
    pub max_payload: u32,

    // --- sahte paketler ---
    pub wrong_seq: bool,
    pub wrong_chksum: bool,
    /// Sabit TTL ile sahte paket gönder
    pub set_ttl: Option<u8>,
    /// Otomatik TTL (hedefin TTL'ini tahmin et)
    pub auto_ttl: bool,
    pub min_ttl: u8,
    /// Aynı sahte paketi kaç kez gönder
    pub fake_resend: u32,

    // --- diğer ---
    /// QUIC/HTTP3 engelle (UDP/443)
    pub block_quic: bool,
    /// Pasif DPI bloğu (gelen RST paketlerini düşür)
    pub block_passive: bool,
    /// Host başlığında boşluk kaldır (GoodbyeDPI -s)
    pub strip_host_space: bool,
    /// Host -> hoSt (GoodbyeDPI -r)
    pub mix_host_case: bool,

    // --- DNS ---
    pub dns_addr: Option<String>,
    pub dns_port: Option<u16>,
    pub dnsv6_addr: Option<String>,
    pub dnsv6_port: Option<u16>,

    // --- liste / çıktı ---
    pub blacklist: Option<String>,
    pub verbose: bool,
    /// Yalnızca bu portlara ek olarak müdahale et
    pub extra_ports: Vec<u16>,
}

impl Default for Config {
    fn default() -> Self {
        Config {
            frag_http: 2,
            frag_https: 2,
            frag_by_sni: false,
            reverse_frag: false,
            max_payload: 0,
            wrong_seq: false,
            wrong_chksum: false,
            set_ttl: None,
            auto_ttl: false,
            min_ttl: 3,
            fake_resend: 1,
            block_quic: false,
            block_passive: false,
            strip_host_space: false,
            mix_host_case: false,
            dns_addr: None,
            dns_port: None,
            dnsv6_addr: None,
            dnsv6_port: None,
            blacklist: None,
            verbose: false,
            extra_ports: Vec::new(),
        }
    }
}

impl Config {
    /// Modern mod setleri (GoodbyeDPI 0.2.x karşılıkları)
    pub fn preset(n: u8) -> Option<Config> {
        let mut c = Config::default();
        match n {
            1 => {
                c.frag_http = 2;
                c.frag_https = 2;
                c.strip_host_space = true;
                c.mix_host_case = true;
                c.block_passive = true;
            }
            5 => {
                // auto-ttl + reverse-frag
                c.auto_ttl = true;
                c.reverse_frag = true;
                c.max_payload = 1200;
            }
            6 => {
                c.wrong_seq = true;
                c.reverse_frag = true;
                c.max_payload = 1200;
            }
            7 => {
                c.wrong_chksum = true;
                c.reverse_frag = true;
                c.max_payload = 1200;
            }
            8 => {
                c.wrong_seq = true;
                c.wrong_chksum = true;
                c.reverse_frag = true;
                c.max_payload = 1200;
            }
            9 => {
                c.wrong_seq = true;
                c.wrong_chksum = true;
                c.reverse_frag = true;
                c.max_payload = 1200;
                c.block_quic = true;
            }
            _ => return None,
        }
        Some(c)
    }

    /// Ayarları çözümler. `Err` = kullanım hatası.
    pub fn parse<I: IntoIterator<Item = String>>(args: I) -> Result<Config, String> {
        let argv: Vec<String> = args.into_iter().collect();
        let mut c = Config::default();
        let mut preset_applied = false;
        let mut i = 0usize;

        while i < argv.len() {
            let a = argv[i].as_str();
            let need = |name: &str| -> Result<String, String> {
                if i + 1 < argv.len() {
                    Ok(argv[i + 1].clone())
                } else {
                    Err(format!("{name} için değer eksik"))
                }
            };

            match a {
                "-h" | "--help" => return Err(String::new()),
                "-v" | "--verbose" => c.verbose = true,

                // mod setleri
                s if s.len() == 2 && s.starts_with('-') && s.as_bytes()[1].is_ascii_digit() => {
                    let n = s.as_bytes()[1] - b'0';
                    match Config::preset(n) {
                        Some(p) => {
                            c = p;
                            preset_applied = true;
                        }
                        None => return Err(format!("bilinmeyen mod seti: {s}")),
                    }
                }

                "-f" => {
                    c.frag_http = need("-f")?.parse().map_err(|_| "-f sayı olmalı".to_string())?;
                    i += 1;
                }
                "-e" => {
                    c.frag_https = need("-e")?.parse().map_err(|_| "-e sayı olmalı".to_string())?;
                    i += 1;
                }
                "-k" => {
                    let _: u32 = need("-k")?.parse().map_err(|_| "-k sayı olmalı".to_string())?;
                    i += 1;
                }
                "--frag-by-sni" => c.frag_by_sni = true,
                "--reverse-frag" => c.reverse_frag = true,
                "--native-frag" => { /* parçalama zaten yerel; bayrak kabul edilir */ }
                "--max-payload" => {
                    c.max_payload = need("--max-payload")?
                        .parse()
                        .map_err(|_| "--max-payload sayı olmalı".to_string())?;
                    i += 1;
                }

                "--wrong-seq" => c.wrong_seq = true,
                "--wrong-chksum" => c.wrong_chksum = true,
                "--set-ttl" => {
                    c.set_ttl = Some(
                        need("--set-ttl")?
                            .parse()
                            .map_err(|_| "--set-ttl sayı olmalı".to_string())?,
                    );
                    i += 1;
                }
                "--auto-ttl" => c.auto_ttl = true,
                "--min-ttl" => {
                    c.min_ttl = need("--min-ttl")?
                        .parse()
                        .map_err(|_| "--min-ttl sayı olmalı".to_string())?;
                    i += 1;
                }
                "--fake-resend" => {
                    c.fake_resend = need("--fake-resend")?
                        .parse()
                        .map_err(|_| "--fake-resend sayı olmalı".to_string())?;
                    i += 1;
                }

                "-q" => c.block_quic = true,
                "-p" => c.block_passive = true,
                "-s" => c.strip_host_space = true,
                "-r" => c.mix_host_case = true,
                "-n" | "-m" | "-a" | "-w" => { /* kabul edilir, etkisi yok */ }

                "--dns-addr" => {
                    c.dns_addr = Some(need("--dns-addr")?);
                    i += 1;
                }
                "--dns-port" => {
                    c.dns_port = Some(
                        need("--dns-port")?
                            .parse()
                            .map_err(|_| "--dns-port sayı olmalı".to_string())?,
                    );
                    i += 1;
                }
                "--dnsv6-addr" => {
                    c.dnsv6_addr = Some(need("--dnsv6-addr")?);
                    i += 1;
                }
                "--dnsv6-port" => {
                    c.dnsv6_port = Some(
                        need("--dnsv6-port")?
                            .parse()
                            .map_err(|_| "--dnsv6-port sayı olmalı".to_string())?,
                    );
                    i += 1;
                }
                "--blacklist" => {
                    c.blacklist = Some(need("--blacklist")?);
                    i += 1;
                }
                "--port" => {
                    let p: u16 = need("--port")?
                        .parse()
                        .map_err(|_| "--port sayı olmalı".to_string())?;
                    c.extra_ports.push(p);
                    i += 1;
                }

                other => return Err(format!("bilinmeyen argüman: {other}")),
            }
            i += 1;
        }

        // --dns-addr olmadan --dns-port anlamsız
        if c.dns_port.is_some() && c.dns_addr.is_none() {
            return Err("--dns-port, --dns-addr ile birlikte kullanılmalı".into());
        }
        if c.dnsv6_port.is_some() && c.dnsv6_addr.is_none() {
            return Err("--dnsv6-port, --dnsv6-addr ile birlikte kullanılmalı".into());
        }
        if !preset_applied && c.frag_by_sni && c.frag_https == 0 {
            c.frag_https = 2;
        }
        Ok(c)
    }

    /// WinDivert filtresi
    pub fn filter(&self) -> String {
        let mut ports: Vec<u16> = vec![80, 443];
        ports.extend(self.extra_ports.iter().copied());
        let tcp = ports
            .iter()
            .map(|p| format!("tcp.DstPort == {p}"))
            .collect::<Vec<_>>()
            .join(" or ");
        if self.dns_addr.is_some() || self.dnsv6_addr.is_some() {
            format!("outbound and (({tcp}) or udp.DstPort == 53)")
        } else {
            format!("outbound and ({tcp})")
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn p(s: &str) -> Result<Config, String> {
        Config::parse(s.split_whitespace().map(|x| x.to_string()))
    }

    #[test]
    fn parses_preset_9() {
        let c = p("-9").unwrap();
        assert!(c.wrong_seq && c.wrong_chksum && c.reverse_frag && c.block_quic);
        assert_eq!(c.max_payload, 1200);
    }

    #[test]
    fn parses_dpifix_app_line() {
        // DpiFix arayüzünün servise yazdığı satır
        let c = p(r#"-9 --blacklist C:\ProgramData\DpiFix\blacklist.txt"#).unwrap();
        assert!(c.wrong_seq);
        assert_eq!(c.blacklist.as_deref(), Some(r"C:\ProgramData\DpiFix\blacklist.txt"));
    }

    #[test]
    fn parses_dns_redirect_line() {
        let c = p("-6 --dns-addr 77.88.8.8 --dns-port 1253").unwrap();
        assert_eq!(c.dns_addr.as_deref(), Some("77.88.8.8"));
        assert_eq!(c.dns_port, Some(1253));
        assert!(c.filter().contains("udp.DstPort == 53"));
    }

    #[test]
    fn overrides_apply_after_preset() {
        let c = p("-5 --set-ttl 3").unwrap();
        assert_eq!(c.set_ttl, Some(3));
        assert!(c.auto_ttl, "preset korunmalı");
    }

    #[test]
    fn rejects_bad_input() {
        assert!(p("--set-ttl").is_err());
        assert!(p("--set-ttl abc").is_err());
        assert!(p("--dns-port 1253").is_err(), "--dns-addr olmadan reddedilmeli");
        assert!(p("--bilinmeyen").is_err());
    }

    #[test]
    fn filter_lists_ports() {
        let c = p("--port 8080").unwrap();
        let f = c.filter();
        assert!(f.contains("tcp.DstPort == 80"));
        assert!(f.contains("tcp.DstPort == 443"));
        assert!(f.contains("tcp.DstPort == 8080"));
        assert!(f.starts_with("outbound"));
    }
}
