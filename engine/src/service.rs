//! Native Windows service entry point. The service owns the capture handle.

use crate::{args::Config, engine::Engine};
use std::ffi::c_void;
use std::io;
use std::path::PathBuf;
use std::ptr;
use std::sync::Mutex;

const START_PENDING: u32 = 2;
const STOP_PENDING: u32 = 3;
const RUNNING: u32 = 4;
const STOPPED: u32 = 1;
const STOP: u32 = 1;
const INTERROGATE: u32 = 4;
const SHUTDOWN: u32 = 5;

#[repr(C)]
struct Status {
    service_type: u32,
    state: u32,
    accepted: u32,
    win32_exit_code: u32,
    service_exit_code: u32,
    checkpoint: u32,
    wait_hint: u32,
}

#[repr(C)]
struct Entry {
    name: *const u16,
    main: Option<extern "system" fn(u32, *mut *mut u16)>,
}

struct Service {
    handle: usize,
    state: u32,
    stop_capture: Option<Box<dyn Fn() + Send>>,
}

impl Service {
    fn report(&mut self, state: u32, failed: bool) -> io::Result<()> {
        self.state = state;
        let pending = state == START_PENDING || state == STOP_PENDING;
        let status = Status {
            service_type: 0x10,
            state,
            accepted: if state == RUNNING { 0x1 | 0x4 } else { 0 },
            win32_exit_code: if failed { 1066 } else { 0 },
            service_exit_code: u32::from(failed),
            checkpoint: u32::from(pending),
            wait_hint: if pending { 10_000 } else { 0 },
        };
        if unsafe { SetServiceStatus(self.handle as *mut c_void, &status) } == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }
}

static NAME: [u16; 7] = [68, 112, 105, 70, 105, 120, 0]; // DpiFix
static INPUT: Mutex<Option<(Config, Option<PathBuf>)>> = Mutex::new(None);
static SERVICE: Mutex<Service> = Mutex::new(Service { handle: 0, state: STOPPED, stop_capture: None });

pub fn dispatch(config: Config, dll_dir: Option<PathBuf>) -> io::Result<()> {
    *INPUT.lock().unwrap() = Some((config, dll_dir));
    let entries = [
        Entry { name: NAME.as_ptr(), main: Some(service_main) },
        Entry { name: ptr::null(), main: None },
    ];
    if unsafe { StartServiceCtrlDispatcherW(entries.as_ptr()) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

extern "system" fn service_main(_: u32, _: *mut *mut u16) {
    let handle = unsafe { RegisterServiceCtrlHandlerExW(NAME.as_ptr(), control, ptr::null_mut()) };
    if handle.is_null() {
        return;
    }
    {
        let mut service = SERVICE.lock().unwrap();
        service.handle = handle as usize;
        if service.report(START_PENDING, false).is_err() {
            return;
        }
    }
    let result = run_engine();
    let mut service = SERVICE.lock().unwrap();
    service.stop_capture = None;
    let stopping = service.state == STOP_PENDING;
    // An unexpected exit must be a process failure so SCM recovery can restart it.
    if result.is_err() && !stopping {
        std::process::exit(1);
    }
    let _ = service.report(STOPPED, result.is_err());
}

fn run_engine() -> io::Result<()> {
    let (config, dll_dir) = INPUT.lock().unwrap().take()
        .ok_or_else(|| io::Error::other("service configuration missing"))?;
    let mut engine = Engine::new(config, dll_dir)?;
    let capture = engine.open()?;
    {
        let mut service = SERVICE.lock().unwrap();
        service.stop_capture = Some(Box::new(capture.receive_shutdown()));
        if let Err(error) = service.report(RUNNING, false) {
            service.stop_capture = None;
            return Err(error);
        }
    }
    let result = engine.run_with(&capture);
    SERVICE.lock().unwrap().stop_capture = None;
    result
}

extern "system" fn control(code: u32, _: u32, _: *mut c_void, _: *mut c_void) -> u32 {
    let mut service = SERVICE.lock().unwrap();
    match code {
        STOP | SHUTDOWN if service.state == RUNNING => {
            let _ = service.report(STOP_PENDING, false);
            if let Some(stop) = &service.stop_capture {
                stop();
            }
        }
        STOP | SHUTDOWN | INTERROGATE => {}
        _ => return 120,
    }
    0
}

#[link(name = "advapi32")]
unsafe extern "system" {
    fn StartServiceCtrlDispatcherW(table: *const Entry) -> i32;
    fn RegisterServiceCtrlHandlerExW(
        name: *const u16,
        handler: extern "system" fn(u32, u32, *mut c_void, *mut c_void) -> u32,
        context: *mut c_void,
    ) -> *mut c_void;
    fn SetServiceStatus(handle: *mut c_void, status: *const Status) -> i32;
}
