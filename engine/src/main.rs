//! DPI Fix — Rust motoru
//!
//! WinDivert üzerinde çalışan, kendi TLS/HTTP ayrıştırıcısına ve parçalama
//! stratejilerine sahip DPI atlatma motoru. Harici crate kullanmaz.

mod args;
mod engine;
mod hostlist;
mod packet;
mod sniff;
mod windivert;

use args::Config;
use std::path::PathBuf;
use std::process::ExitCode;

const VERSION: &str = env!("CARGO_PKG_VERSION");

fn help() {
    println!(
        "DPI Fix motoru v{VERSION} — DPI/DNS zorlamasini kaldirir

KULLANIM
  dpifix-engine [SECENEKLER]

MOD SETLERI (GoodbyeDPI uyumlu)
  -1            En uyumlu: HTTP+HTTPS parcalama, pasif DPI blogu
  -5            auto-ttl + reverse-frag (sahte paket)
  -6            wrong-seq + reverse-frag
  -7            wrong-chksum + reverse-frag
  -8            wrong-seq + wrong-chksum + reverse-frag
  -9            -8 + QUIC engeli (onerilen)

PARCALAMA
  -f <n>              HTTP parcalama boyutu (0 = kapali, varsayilan 2)
  -e <n>              TLS parcalama: SNI icinde kacinci bayttan bolunecek
  --frag-by-sni       TLS paketini SNI'dan hemen once bol
  --reverse-frag      Parcalari ters sirada gonder
  --max-payload <n>   Bu boyuttan buyuk yuklere dokunma (0 = sinirsiz)

SAHTE PAKET
  --wrong-seq         Gecmis TCP SEQ ile sahte paket
  --wrong-chksum      Bozuk TCP checksum ile sahte paket
  --set-ttl <n>       Sabit TTL ile sahte paket
  --auto-ttl          TTL'i otomatik tahmin et
  --min-ttl <n>       Otomatik TTL icin en kucuk mesafe (varsayilan 3)
  --fake-resend <n>   Sahte paketi kac kez gonder (varsayilan 1)

DNS
  --dns-addr <ip>     UDP/53 isteklerini bu adrese yonlendir
  --dns-port <n>      Yonlendirilen port (varsayilan 53)
  --dnsv6-addr <ip>   IPv6 icin ayni islem
  --dnsv6-port <n>

LISTE
  --blacklist <dosya> Yalnizca bu alan adlarina dokun (alt alan adlari dahil)

DIGER
  -q                  QUIC/HTTP3 engelle (UDP/443)
  -p                  Pasif DPI: gelen RST paketlerini dusur
  -s                  Host basligindaki boslugu kaldir
  -r                  Host -> hoSt
  --port <n>          Ek TCP portu (tekrarlanabilir)
  -v, --verbose       Istatistikleri yazdir
  -h, --help          Bu metin
  --version           Surum

NOT
  Yonetici yetkisi gerekir. WinDivert.dll ve WinDivert64.sys bu calistirilabilir
  dosyanin yaninda olmalidir."
    );
}

fn dll_dir() -> Option<PathBuf> {
    std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.to_path_buf()))
}

fn main() -> ExitCode {
    let argv: Vec<String> = std::env::args().skip(1).collect();

    if argv.iter().any(|a| a == "--version" || a == "-V") {
        println!("dpifix-engine {VERSION}");
        return ExitCode::SUCCESS;
    }

    let cfg: Config = match Config::parse(argv) {
        Ok(c) => c,
        Err(msg) => {
            if msg.is_empty() {
                help();
                return ExitCode::SUCCESS;
            }
            eprintln!("HATA: {msg}");
            eprintln!("Yardim icin: dpifix-engine --help");
            return ExitCode::from(2);
        }
    };

    println!("DPI Fix motoru v{VERSION} (Rust)");
    match engine::Engine::new(cfg, dll_dir()) {
        Ok(mut e) => {
            if let Err(err) = e.run() {
                eprintln!("HATA: {err}");
                return ExitCode::from(1);
            }
        }
        Err(e) => {
            eprintln!("HATA: {e}");
            return ExitCode::from(1);
        }
    }
    ExitCode::SUCCESS
}
