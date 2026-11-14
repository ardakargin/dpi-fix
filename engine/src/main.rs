//! DPI Fix — Rust motoru
//!
//! WinDivert üzerinde çalışan, kendi TLS/HTTP ayrıştırıcısına ve parçalama
//! stratejilerine sahip DPI atlatma motoru. Harici crate kullanmaz.

mod args;
mod engine;
mod hostlist;
mod packet;
mod service;
mod sniff;
mod windivert;

use args::Config;
use std::path::PathBuf;
use std::process::ExitCode;

const VERSION: &str = env!("CARGO_PKG_VERSION");

fn dll_dir() -> Option<PathBuf> {
    std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.to_path_buf()))
}

fn main() -> ExitCode {
    let mut argv: Vec<String> = std::env::args().skip(1).collect();
    let service_mode = argv.first().map(String::as_str) == Some("--service");
    if service_mode {
        argv.remove(0);
    }

    if argv.iter().any(|a| a == "--version" || a == "-V") {
        println!("dpifix-engine {VERSION}");
        return ExitCode::SUCCESS;
    }

    if argv.iter().any(|arg| arg == "--help" || arg == "-h") {
        println!("dpifix-engine --domains FILE [--http]");
        return ExitCode::SUCCESS;
    }
    let cfg = match Config::parse(argv) {
        Ok(config) => config,
        Err(message) => {
            eprintln!("{message}");
            return ExitCode::from(2);
        }
    };

    if service_mode {
        return match service::dispatch(cfg, dll_dir()) {
            Ok(()) => ExitCode::SUCCESS,
            Err(err) => {
                eprintln!("Windows service: {err}");
                ExitCode::FAILURE
            }
        };
    }

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
