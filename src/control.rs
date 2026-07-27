use std::thread;

use windows::Win32::Foundation::{
    CloseHandle, ERROR_ALREADY_EXISTS, ERROR_FILE_NOT_FOUND, GetLastError, HANDLE,
};
use windows::Win32::System::Threading::{
    CreateEventW, CreateMutexW, EVENT_MODIFY_STATE, INFINITE, OpenEventW, SetEvent,
    WaitForSingleObject,
};
use windows::core::w;

const TRAY_MUTEX: windows::core::PCWSTR = w!("Local\\SBMSTray-v1");
const SHUTDOWN_EVENT: windows::core::PCWSTR = w!("Local\\SBMSShutdown-v1");

pub struct TrayInstance(HANDLE);

impl TrayInstance {
    pub fn acquire() -> Result<Option<Self>, String> {
        let handle = unsafe { CreateMutexW(None, true, TRAY_MUTEX) }
            .map_err(|error| format!("CreateMutexW(tray) failed: {error}"))?;
        if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
            unsafe {
                let _ = CloseHandle(handle);
            }
            return Ok(None);
        }
        Ok(Some(Self(handle)))
    }
}

impl Drop for TrayInstance {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}

pub fn listen_for_shutdown(callback: impl FnOnce() + Send + 'static) -> Result<(), String> {
    let event = unsafe { CreateEventW(None, false, false, SHUTDOWN_EVENT) }
        .map_err(|error| format!("CreateEventW(shutdown) failed: {error}"))?;
    let event_value = event.0 as usize;
    thread::Builder::new()
        .name("sbms-shutdown-listener".into())
        .spawn(move || {
            let event = HANDLE(event_value as *mut _);
            unsafe {
                let _ = WaitForSingleObject(event, INFINITE);
                let _ = CloseHandle(event);
            }
            callback();
        })
        .map(|_| ())
        .map_err(|error| {
            unsafe {
                let _ = CloseHandle(event);
            }
            format!("failed to start shutdown listener: {error}")
        })
}

pub fn signal_shutdown() -> Result<bool, String> {
    let event = match unsafe { OpenEventW(EVENT_MODIFY_STATE, false, SHUTDOWN_EVENT) } {
        Ok(event) => event,
        Err(_error) if unsafe { GetLastError() } == ERROR_FILE_NOT_FOUND => return Ok(false),
        Err(error) => return Err(format!("OpenEventW(shutdown) failed: {error}")),
    };
    let result = unsafe { SetEvent(event) }
        .map(|()| true)
        .map_err(|error| format!("SetEvent(shutdown) failed: {error}"));
    unsafe {
        let _ = CloseHandle(event);
    }
    result
}
