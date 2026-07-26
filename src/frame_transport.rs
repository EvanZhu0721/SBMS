use std::error::Error;
use std::fmt::{Display, Formatter};
use std::mem::size_of;
use std::thread;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{
    CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE, INVALID_HANDLE_VALUE,
};
use windows::Win32::Security::{
    InitializeSecurityDescriptor, PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES, SECURITY_DESCRIPTOR,
    SetSecurityDescriptorDacl,
};
use windows::Win32::System::Memory::{CreateFileMappingW, PAGE_READWRITE};
use windows::Win32::System::Threading::{CreateEventW, CreateMutexW};
use windows::core::{PCWSTR, w};

pub const WIDTH: usize = 1920;
pub const HEIGHT: usize = 1080;
pub const STRIDE: usize = WIDTH * 4;
pub const HEADER_BYTES: usize = 24;
pub const FRAME_BYTES: usize = HEADER_BYTES + STRIDE * HEIGHT;
pub const MAGIC: u32 = 0x5342_4d53;
const SECURITY_DESCRIPTOR_REVISION: u32 = 1;
pub const FRAME_MAPPING: PCWSTR = w!("Global\\SBMSFrame-v1");
pub const FRAME_EVENT: PCWSTR = w!("Global\\SBMSFrameReady-v1");
const SESSION_MUTEX: PCWSTR = w!("Global\\SBMSSession-v1");
const CHANNEL_TIMEOUT: Duration = Duration::from_secs(5);

pub struct FrameTransport {
    session: HANDLE,
    mapping: HANDLE,
    event: HANDLE,
}

#[derive(Debug)]
pub struct TransportError(String);

impl Display for TransportError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for TransportError {}

impl FrameTransport {
    pub fn create() -> Result<Self, TransportError> {
        let mut descriptor = SECURITY_DESCRIPTOR::default();
        let descriptor_ptr =
            PSECURITY_DESCRIPTOR((&mut descriptor as *mut SECURITY_DESCRIPTOR).cast());
        unsafe {
            InitializeSecurityDescriptor(descriptor_ptr, SECURITY_DESCRIPTOR_REVISION)
                .map_err(|error| TransportError(format!("security descriptor: {error}")))?;
            SetSecurityDescriptorDacl(descriptor_ptr, true, None, false)
                .map_err(|error| TransportError(format!("security descriptor DACL: {error}")))?;
        }
        let attributes = SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: descriptor_ptr.0,
            bInheritHandle: false.into(),
        };

        let session = unsafe { CreateMutexW(Some(&attributes), false, SESSION_MUTEX) }
            .map_err(|error| TransportError(format!("CreateMutexW: {error}")))?;
        if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
            unsafe {
                let _ = CloseHandle(session);
            }
            return Err(TransportError(
                "another SBMS mapping session already owns the frame channel".into(),
            ));
        }

        let deadline = Instant::now() + CHANNEL_TIMEOUT;
        loop {
            let mapping = match unsafe {
                CreateFileMappingW(
                    INVALID_HANDLE_VALUE,
                    Some(&attributes),
                    PAGE_READWRITE,
                    0,
                    FRAME_BYTES as u32,
                    FRAME_MAPPING,
                )
            } {
                Ok(mapping) => mapping,
                Err(error) => {
                    unsafe {
                        let _ = CloseHandle(session);
                    }
                    return Err(TransportError(format!("CreateFileMappingW: {error}")));
                }
            };
            let mapping_exists = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
            if !mapping_exists {
                let event =
                    match unsafe { CreateEventW(Some(&attributes), false, false, FRAME_EVENT) } {
                        Ok(event) => event,
                        Err(error) => {
                            unsafe {
                                let _ = CloseHandle(mapping);
                                let _ = CloseHandle(session);
                            }
                            return Err(TransportError(format!("CreateEventW: {error}")));
                        }
                    };
                if unsafe { GetLastError() } != ERROR_ALREADY_EXISTS {
                    return Ok(Self {
                        session,
                        mapping,
                        event,
                    });
                }
                unsafe {
                    let _ = CloseHandle(event);
                }
            }
            unsafe {
                let _ = CloseHandle(mapping);
            }
            if Instant::now() >= deadline {
                unsafe {
                    let _ = CloseHandle(session);
                }
                return Err(TransportError(
                    "the previous driver frame channel did not close within 5 seconds".into(),
                ));
            }
            thread::sleep(Duration::from_millis(25));
        }
    }
}

impl Drop for FrameTransport {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.event);
            let _ = CloseHandle(self.mapping);
            let _ = CloseHandle(self.session);
        }
    }
}
