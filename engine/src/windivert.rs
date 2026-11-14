//! WinDivert ile dinamik bağlantı (FFI).
//!
//! `WinDivert.lib` gerektirmez: tüm simgeler çalışma anında `WinDivert.dll`
//! üzerinden çözülür. Böylece dağıtım paketinde yalnızca DLL + .sys bulunur.

use std::ffi::{c_char, c_int, c_void, CString, OsStr};
use std::io;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;

pub const LAYER_NETWORK: c_int = 0;

/// WinDivert adres yapısı (WINDIVERT_ADDRESS, tam 80 bayt)
#[derive(Clone, Copy)]
#[repr(C, align(8))]
pub struct Address(pub [u8; 80]);

impl Address {
    pub fn zeroed() -> Self {
        Address([0u8; 80])
    }

    pub fn mark_tcp_checksums(&mut self, ipv4: bool) {
        let mut flags = u32::from_le_bytes(self.0[8..12].try_into().unwrap());
        flags &= !((1 << 21) | (1 << 23));
        flags |= (u32::from(ipv4) << 21) | (1 << 22);
        self.0[8..12].copy_from_slice(&flags.to_le_bytes());
    }

    #[cfg(test)]
    fn flags(&self) -> u32 {
        u32::from_le_bytes([self.0[8], self.0[9], self.0[10], self.0[11]])
    }

    /// Paket yönü dışarı giden mi? (bit 17 = Outbound)
    #[cfg(test)]
    pub fn outbound(&self) -> bool {
        (self.flags() >> 17) & 1 == 1
    }

    /// Paket IPv6 mı? (bit 20 = IPv6)
    #[cfg(test)]
    pub fn is_ipv6(&self) -> bool {
        (self.flags() >> 20) & 1 == 1
    }
}

pub struct WinDivert {
    handle: *mut c_void,
    close: unsafe extern "system" fn(*mut c_void) -> c_int,
    recv: unsafe extern "system" fn(*mut c_void, *mut c_void, u32, *mut u32, *mut Address) -> c_int,
    send: unsafe extern "system" fn(*mut c_void, *const c_void, u32, *mut u32, *const Address) -> c_int,
    shutdown: unsafe extern "system" fn(*mut c_void, c_int) -> c_int,
    lib: *mut c_void,
}

// Tek iş parçacığında kullanılır; hamle (handle) taşınabilir işaretlenir.
unsafe impl Send for WinDivert {}

impl WinDivert {
    /// Motoru açar. `dll_dir` verilirse önce oradaki WinDivert.dll denenir.
    pub fn open(filter: &str, dll_dir: Option<&Path>, priority: i16) -> io::Result<WinDivert> {
        unsafe {
            let lib = load_dll(dll_dir).ok_or_else(|| {
                io::Error::new(
                    io::ErrorKind::NotFound,
                    "WinDivert.dll yüklenemedi (DLL uygulama klasöründe mi?)",
                )
            })?;

            let open_fn: unsafe extern "system" fn(*const c_char, c_int, i16, u64) -> *mut c_void =
                resolve(lib, b"WinDivertOpen\0").ok_or_else(|| missing("WinDivertOpen"))?;
            let close_fn: unsafe extern "system" fn(*mut c_void) -> c_int =
                resolve(lib, b"WinDivertClose\0").ok_or_else(|| missing("WinDivertClose"))?;
            let recv_fn: unsafe extern "system" fn(
                *mut c_void,
                *mut c_void,
                u32,
                *mut u32,
                *mut Address,
            ) -> c_int =
                resolve(lib, b"WinDivertRecv\0").ok_or_else(|| missing("WinDivertRecv"))?;
            let send_fn: unsafe extern "system" fn(
                *mut c_void,
                *const c_void,
                u32,
                *mut u32,
                *const Address,
            ) -> c_int =
                resolve(lib, b"WinDivertSend\0").ok_or_else(|| missing("WinDivertSend"))?;
            let shutdown = resolve(lib, b"WinDivertShutdown\0")
                .ok_or_else(|| missing("WinDivertShutdown"))?;

            let cfilter = CString::new(filter).map_err(|_| {
                io::Error::new(io::ErrorKind::InvalidInput, "filtre içinde NUL karakteri var")
            })?;
            let handle = open_fn(cfilter.as_ptr(), LAYER_NETWORK, priority, 0);
            // WinDivert hata durumunda NULL değil INVALID_HANDLE_VALUE (-1) döndürür
            if handle.is_null() || handle as isize == -1 {
                return Err(last_error("WinDivertOpen"));
            }

            Ok(WinDivert { handle, close: close_fn, recv: recv_fn, send: send_fn, shutdown, lib })
        }
    }

    pub fn receive_shutdown(&self) -> impl Fn() + Send + 'static {
        let handle = self.handle as usize;
        let shutdown = self.shutdown;
        move || unsafe { shutdown(handle as *mut c_void, 1); }
    }

    pub fn recv(&self, buf: &mut [u8], addr: &mut Address) -> io::Result<usize> {
        let mut len: u32 = 0;
        let ok = unsafe {
            (self.recv)(
                self.handle,
                buf.as_mut_ptr() as *mut c_void,
                buf.len() as u32,
                &mut len,
                addr,
            )
        };
        if ok == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(len as usize)
    }

    pub fn send(&self, buf: &[u8], addr: &Address) -> io::Result<usize> {
        let mut len: u32 = 0;
        let ok = unsafe {
            (self.send)(
                self.handle,
                buf.as_ptr() as *const c_void,
                buf.len() as u32,
                &mut len,
                addr,
            )
        };
        if ok == 0 {
            return Err(last_error("WinDivertSend"));
        }
        Ok(len as usize)
    }
}

impl Drop for WinDivert {
    fn drop(&mut self) {
        unsafe {
            if !self.handle.is_null() {
                (self.close)(self.handle);
            }
            if !self.lib.is_null() {
                FreeLibrary(self.lib);
            }
        }
    }
}

fn missing(name: &str) -> io::Error {
    io::Error::new(
        io::ErrorKind::Unsupported,
        format!("WinDivert.dll içinde {name} bulunamadı (sürüm uyumsuz olabilir)"),
    )
}

/// WinDivert işlevi başarısız olduğunda son Windows hatasını anlamlı metne çevirir.
fn last_error(context: &str) -> io::Error {
    let code = unsafe { GetLastError() };
    let hint = match code {
        5 => "yönetici yetkisi gerekiyor",
        6 => "geçersiz hamle (sürücü yüklenemedi)",
        87 => "geçersiz filtre",
        577 => "sürücü imzası doğrulanamadı (WinDivert*.sys eksik veya engelli)",
        1058 => "gerekli hizmet devre dışı (WinDivert sürücüsü yüklenemedi)",
        _ => "bilinmeyen hata",
    };
    io::Error::new(io::ErrorKind::Other, format!("{context} başarısız (Windows hata {code}: {hint})"))
}

fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

/// Use the DLL beside the executable when an explicit directory is supplied.
unsafe fn load_dll(dir: Option<&Path>) -> Option<*mut c_void> {
    if let Some(d) = dir {
        let p = d.join("WinDivert.dll");
        if p.exists() {
            let w = wide(&p.to_string_lossy());
            let h = LoadLibraryW(w.as_ptr());
            if !h.is_null() {
                return Some(h);
            }
        }
        return None;
    }
    let w = wide("WinDivert.dll");
    let h = LoadLibraryW(w.as_ptr());
    if h.is_null() {
        None
    } else {
        Some(h)
    }
}

unsafe fn resolve<T: Copy>(lib: *mut c_void, name: &[u8]) -> Option<T> {
    let p = GetProcAddress(lib, name.as_ptr() as *const c_char);
    if p.is_null() {
        None
    } else {
        Some(std::mem::transmute_copy(&p))
    }
}

unsafe extern "system" {
    fn LoadLibraryW(name: *const u16) -> *mut c_void;
    fn FreeLibrary(h: *mut c_void) -> c_int;
    fn GetProcAddress(h: *mut c_void, name: *const c_char) -> *mut c_void;
    fn GetLastError() -> u32;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_direction_and_family_bits() {
        let mut a = Address::zeroed();
        assert!(!a.outbound() && !a.is_ipv6());

        // bit 17 (Outbound) ve bit 20 (IPv6) -> flags u32
        let flags: u32 = (1 << 17) | (1 << 20);
        a.0[8..12].copy_from_slice(&flags.to_le_bytes());
        assert!(a.outbound());
        assert!(a.is_ipv6());

        let inbound_only: u32 = 1 << 20;
        a.0[8..12].copy_from_slice(&inbound_only.to_le_bytes());
        assert!(!a.outbound());
    }

    #[test]
    fn missing_dll_returns_error_instead_of_panicking() {
        let err = WinDivert::open("outbound", Some(Path::new("C:\\olmayan-klasor-xyz")), 0);
        assert!(err.is_err());
    }
}
