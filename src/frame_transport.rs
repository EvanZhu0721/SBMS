use std::error::Error;
use std::ffi::c_void;
use std::fmt::{Display, Formatter};
use std::mem::size_of;

use windows::Win32::Foundation::{
    CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE, HLOCAL, INVALID_HANDLE_VALUE,
    LocalFree,
};
use windows::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
};
use windows::Win32::Security::Cryptography::{BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptGenRandom};
use windows::Win32::Security::{
    GetTokenInformation, PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES, TOKEN_QUERY, TOKEN_USER,
    TokenUser,
};
use windows::Win32::System::Memory::{
    CreateFileMappingW, FILE_MAP_WRITE, MapViewOfFile, PAGE_READWRITE, UnmapViewOfFile,
};
use windows::Win32::System::Threading::{CreateEventW, GetCurrentProcess, OpenProcessToken};
use windows::core::{PCWSTR, PWSTR, w};

pub const WIDTH: usize = 1920;
pub const HEIGHT: usize = 1080;
pub const STRIDE: usize = WIDTH * 4;
pub const HEADER_BYTES: usize = 24;
pub const FRAME_PIXELS: usize = STRIDE * HEIGHT;
pub const FRAME_BYTES: usize = HEADER_BYTES + 2 * FRAME_PIXELS;
pub const MAGIC: u32 = 0x5342_4d53;
const GATE_MAGIC: u32 = 0x5342_4732;
const PROTOCOL_VERSION: u32 = 2;
const GATE_MAPPING: PCWSTR = w!("Global\\SBMSSession-v2");

#[repr(C)]
struct GateHeader {
    magic: u32,
    version: u32,
    nonce: [u8; 16],
}

#[derive(Clone)]
pub struct FrameChannel {
    mapping: Vec<u16>,
    event: Vec<u16>,
}

impl FrameChannel {
    pub fn mapping(&self) -> PCWSTR {
        PCWSTR(self.mapping.as_ptr())
    }

    pub fn event(&self) -> PCWSTR {
        PCWSTR(self.event.as_ptr())
    }
}

pub struct FrameTransport {
    gate: HANDLE,
    mapping: HANDLE,
    event: HANDLE,
    channel: FrameChannel,
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
        let descriptor = SecurityDescriptor::for_current_user()?;
        let attributes = descriptor.attributes();
        let gate = unsafe {
            CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                Some(&attributes),
                PAGE_READWRITE,
                0,
                size_of::<GateHeader>() as u32,
                GATE_MAPPING,
            )
        }
        .map_err(|error| TransportError(format!("create session gate: {error}")))?;
        if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
            unsafe {
                let _ = CloseHandle(gate);
            }
            return Err(TransportError(
                "another SBMS mapping session already owns the frame channel".into(),
            ));
        }

        let mut nonce = [0u8; 16];
        let status = unsafe { BCryptGenRandom(None, &mut nonce, BCRYPT_USE_SYSTEM_PREFERRED_RNG) };
        if status.0 < 0 {
            unsafe {
                let _ = CloseHandle(gate);
            }
            return Err(TransportError(format!(
                "BCryptGenRandom failed: 0x{:08x}",
                status.0 as u32
            )));
        }
        let gate_view =
            unsafe { MapViewOfFile(gate, FILE_MAP_WRITE, 0, 0, size_of::<GateHeader>()) };
        if gate_view.Value.is_null() {
            unsafe {
                let _ = CloseHandle(gate);
            }
            return Err(TransportError("map session gate failed".into()));
        }
        unsafe {
            gate_view.Value.cast::<GateHeader>().write(GateHeader {
                magic: GATE_MAGIC,
                version: PROTOCOL_VERSION,
                nonce,
            });
            let _ = UnmapViewOfFile(gate_view);
        }

        let suffix = nonce
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect::<String>();
        let channel = FrameChannel {
            mapping: wide(&format!("Global\\SBMSFrame-v2-{suffix}")),
            event: wide(&format!("Global\\SBMSFrameReady-v2-{suffix}")),
        };
        let mapping = match unsafe {
            CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                Some(&attributes),
                PAGE_READWRITE,
                0,
                FRAME_BYTES as u32,
                channel.mapping(),
            )
        } {
            Ok(mapping) => mapping,
            Err(error) => {
                unsafe {
                    let _ = CloseHandle(gate);
                }
                return Err(TransportError(format!("create frame mapping: {error}")));
            }
        };
        if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
            unsafe {
                let _ = CloseHandle(mapping);
                let _ = CloseHandle(gate);
            }
            return Err(TransportError("random frame mapping name collision".into()));
        }
        let event = match unsafe { CreateEventW(Some(&attributes), false, false, channel.event()) }
        {
            Ok(event) => event,
            Err(error) => {
                unsafe {
                    let _ = CloseHandle(mapping);
                    let _ = CloseHandle(gate);
                }
                return Err(TransportError(format!("create frame event: {error}")));
            }
        };
        if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
            unsafe {
                let _ = CloseHandle(event);
                let _ = CloseHandle(mapping);
                let _ = CloseHandle(gate);
            }
            return Err(TransportError("random frame event name collision".into()));
        }

        Ok(Self {
            gate,
            mapping,
            event,
            channel,
        })
    }

    pub fn channel(&self) -> FrameChannel {
        self.channel.clone()
    }
}

impl Drop for FrameTransport {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.event);
            let _ = CloseHandle(self.mapping);
            let _ = CloseHandle(self.gate);
        }
    }
}

struct SecurityDescriptor(PSECURITY_DESCRIPTOR);

impl SecurityDescriptor {
    fn for_current_user() -> Result<Self, TransportError> {
        let mut token = HANDLE::default();
        unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) }
            .map_err(|error| TransportError(format!("OpenProcessToken: {error}")))?;
        let mut length = 0;
        let _ = unsafe { GetTokenInformation(token, TokenUser, None, 0, &mut length) };
        let mut token_user = vec![0u8; length as usize];
        let result = unsafe {
            GetTokenInformation(
                token,
                TokenUser,
                Some(token_user.as_mut_ptr().cast()),
                length,
                &mut length,
            )
        };
        unsafe {
            let _ = CloseHandle(token);
        }
        result.map_err(|error| TransportError(format!("GetTokenInformation: {error}")))?;

        let user = unsafe { &*token_user.as_ptr().cast::<TOKEN_USER>() };
        let mut sid = PWSTR::null();
        unsafe { ConvertSidToStringSidW(user.User.Sid, &mut sid) }
            .map_err(|error| TransportError(format!("ConvertSidToStringSidW: {error}")))?;
        let sid_text = unsafe { sid.to_string() };
        unsafe {
            LocalFree(Some(HLOCAL(sid.0.cast::<c_void>())));
        }
        let sid_text = sid_text.map_err(|error| TransportError(format!("user SID: {error}")))?;

        let sddl = wide(&format!(
            "D:P(A;;GA;;;{sid_text})(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)"
        ));
        let mut descriptor = PSECURITY_DESCRIPTOR::default();
        unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                PCWSTR(sddl.as_ptr()),
                SDDL_REVISION_1,
                &mut descriptor,
                None,
            )
        }
        .map_err(|error| TransportError(format!("security descriptor: {error}")))?;
        Ok(Self(descriptor))
    }

    fn attributes(&self) -> SECURITY_ATTRIBUTES {
        SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: self.0.0,
            bInheritHandle: false.into(),
        }
    }
}

impl Drop for SecurityDescriptor {
    fn drop(&mut self) {
        unsafe {
            LocalFree(Some(HLOCAL(self.0.0)));
        }
    }
}

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(Some(0)).collect()
}
