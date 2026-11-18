//! WinDivert ile dinamik bağlantı (FFI).
//!
//! `WinDivert.lib` gerektirmez: tüm simgeler çalışma anında `WinDivert.dll`
//! üzerinden çözülür. Böylece dağıtım paketinde yalnızca DLL + .sys bulunur.

use std::cell::UnsafeCell;
use std::ffi::{c_char, c_int, c_void, CString, OsStr};
use std::io;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use std::ptr::{addr_of_mut, null_mut};
use std::time::Instant;

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

    fn flags(&self) -> u32 {
        u32::from_le_bytes([self.0[8], self.0[9], self.0[10], self.0[11]])
    }

    /// Paket yönü dışarı giden mi? (bit 17 = Outbound)
    pub fn outbound(&self) -> bool {
        (self.flags() >> 17) & 1 == 1
    }

    /// Paket IPv6 mı? (bit 20 = IPv6)
    #[cfg(test)]
    pub fn is_ipv6(&self) -> bool {
        (self.flags() >> 20) & 1 == 1
    }
}

#[repr(C)]
struct Overlapped {
    internal: usize,
    internal_high: usize,
    offset: [u32; 2],
    event: *mut c_void,
}

type RecvEx = unsafe extern "system" fn(
    *mut c_void, *mut c_void, u32, *mut u32, u64, *mut Address, *mut u32, *mut Overlapped,
) -> c_int;

struct ReceiveState {
    bytes: Vec<u8>,
    address: Address,
    overlapped: Overlapped,
}

pub struct TimedReceiver<'a> {
    capture: &'a WinDivert,
    recv_ex: RecvEx,
    state: Box<UnsafeCell<ReceiveState>>,
    event: *mut c_void,
    pending: bool,
}

impl TimedReceiver<'_> {
    // An outstanding receive survives a timeout. The kernel keeps using the
    // same heap-owned buffers until this request completes or is cancelled.
    pub fn recv(&mut self, deadline: Option<Instant>) -> io::Result<Option<(&mut [u8], Address)>> {
        let state = self.state.get();
        // Windows can update these fields asynchronously. Avoid Rust references
        // to them while the receive is outstanding.
        let overlapped = unsafe { addr_of_mut!((*state).overlapped) };
        let mut length = 0;
        if !self.pending {
            if unsafe { ResetEvent(self.event) } == 0 {
                return Err(io::Error::last_os_error());
            }
            let ok = unsafe {
                (*overlapped).internal = 0;
                (*overlapped).internal_high = 0;
                let bytes = &mut (*state).bytes;
                (self.recv_ex)(self.capture.handle, bytes.as_mut_ptr().cast(), bytes.len() as u32,
                    &mut length, 0, addr_of_mut!((*state).address), null_mut(), overlapped)
            };
            if ok != 0 {
                return unsafe { Self::packet(state, length) };
            }
            let error = io::Error::last_os_error();
            if error.raw_os_error() != Some(997) { // ERROR_IO_PENDING
                return Err(error);
            }
            self.pending = true;
        }

        let wait = deadline.map_or(u32::MAX, |when| {
            let nanoseconds = when.saturating_duration_since(Instant::now()).as_nanos();
            nanoseconds.div_ceil(1_000_000).min(u128::from(u32::MAX - 1)) as u32
        });
        match unsafe { WaitForSingleObject(self.event, wait) } {
            0 => {}
            258 => return Ok(None), // WAIT_TIMEOUT
            _ => return Err(io::Error::last_os_error()),
        }
        let ok = unsafe { GetOverlappedResult(self.capture.handle, overlapped, &mut length, 0) };
        self.pending = false;
        if ok == 0 {
            return Err(io::Error::last_os_error());
        }
        unsafe { Self::packet(state, length) }
    }

    // The caller ties the returned borrow to the receiver and has observed I/O completion.
    unsafe fn packet<'a>(state: *mut ReceiveState, length: u32) -> io::Result<Option<(&'a mut [u8], Address)>> {
        let state = unsafe { &mut *state };
        let bytes = state.bytes.get_mut(..length as usize)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "invalid WinDivert packet length"))?;
        Ok(Some((bytes, state.address)))
    }
}

impl Drop for TimedReceiver<'_> {
    fn drop(&mut self) {
        unsafe {
            if self.pending {
                let overlapped = addr_of_mut!((*self.state.get()).overlapped);
                let mut length = 0;
                CancelIoEx(self.capture.handle, overlapped);
                GetOverlappedResult(self.capture.handle, overlapped, &mut length, 1);
            }
            CloseHandle(self.event);
        }
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

    pub fn timed_receiver(&self, capacity: usize) -> io::Result<TimedReceiver<'_>> {
        let recv_ex = unsafe { resolve(self.lib, b"WinDivertRecvEx\0") }
            .ok_or_else(|| missing("WinDivertRecvEx"))?;
        let event = unsafe { CreateEventW(std::ptr::null_mut(), 1, 0, std::ptr::null()) };
        if event.is_null() {
            return Err(io::Error::last_os_error());
        }
        Ok(TimedReceiver {
            capture: self, recv_ex, pending: false, event,
            state: Box::new(UnsafeCell::new(ReceiveState {
                bytes: vec![0; capacity],
                address: Address::zeroed(),
                overlapped: Overlapped { internal: 0, internal_high: 0, offset: [0; 2], event },
            })),
        })
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
    fn CreateEventW(attributes: *mut c_void, manual_reset: c_int, initial_state: c_int, name: *const u16) -> *mut c_void;
    fn ResetEvent(handle: *mut c_void) -> c_int;
    fn WaitForSingleObject(handle: *mut c_void, timeout_ms: u32) -> u32;
    fn GetOverlappedResult(handle: *mut c_void, overlapped: *mut Overlapped, transferred: *mut u32, wait: c_int) -> c_int;
    fn CancelIoEx(handle: *mut c_void, overlapped: *mut Overlapped) -> c_int;
    fn CloseHandle(handle: *mut c_void) -> c_int;
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
