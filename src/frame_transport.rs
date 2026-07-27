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

pub const HEADER_BYTES: usize = 24;
pub const MAGIC: u32 = 0x5342_4d53;
const GATE_MAGIC: u32 = 0x5342_4734;
const PROTOCOL_VERSION: u32 = 4;
const GATE_MAPPING: PCWSTR = w!("Global\\SBMSSession-v4");
const MAX_DIMENSION: u32 = 16_384;
const MAX_REFRESH_HZ: u64 = 1_000;
const MAX_MAPPING_BYTES: usize = 512 * 1024 * 1024;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct VirtualMode {
    pub width: u32,
    pub height: u32,
    pub refresh_numerator: u32,
    pub refresh_denominator: u32,
}

impl Default for VirtualMode {
    fn default() -> Self {
        Self {
            width: 3840,
            height: 2160,
            refresh_numerator: 240,
            refresh_denominator: 1,
        }
    }
}

impl VirtualMode {
    pub fn from_millihz(
        width: u32,
        height: u32,
        refresh_millihz: u32,
    ) -> Result<Self, TransportError> {
        if refresh_millihz == 0 {
            return Err(TransportError("virtual refresh must be non-zero".into()));
        }
        let divisor = greatest_common_divisor(refresh_millihz, 1_000);
        let mode = Self {
            width,
            height,
            refresh_numerator: refresh_millihz / divisor,
            refresh_denominator: 1_000 / divisor,
        };
        FrameLayout::new(mode)?;
        Ok(mode)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FrameLayout {
    pub mode: VirtualMode,
    pub stride: u32,
    pub plane_bytes: usize,
    pub mapping_bytes: usize,
}

impl FrameLayout {
    pub fn new(mode: VirtualMode) -> Result<Self, TransportError> {
        if mode.width == 0
            || mode.height == 0
            || mode.width > MAX_DIMENSION
            || mode.height > MAX_DIMENSION
        {
            return Err(TransportError(format!(
                "virtual dimensions must be between 1 and {MAX_DIMENSION}"
            )));
        }
        if mode.refresh_numerator == 0 || mode.refresh_denominator == 0 {
            return Err(TransportError(
                "virtual refresh numerator and denominator must be non-zero".into(),
            ));
        }
        if u64::from(mode.refresh_numerator) > MAX_REFRESH_HZ * u64::from(mode.refresh_denominator)
        {
            return Err(TransportError(format!(
                "virtual refresh must not exceed {MAX_REFRESH_HZ} Hz"
            )));
        }
        let stride = mode
            .width
            .checked_mul(4)
            .ok_or_else(|| TransportError("virtual stride overflow".into()))?;
        let plane_bytes = usize::try_from(stride)
            .ok()
            .and_then(|stride| stride.checked_mul(mode.height as usize))
            .ok_or_else(|| TransportError("virtual frame size overflow".into()))?;
        let mapping_bytes = plane_bytes
            .checked_mul(2)
            .and_then(|bytes| bytes.checked_add(HEADER_BYTES))
            .ok_or_else(|| TransportError("virtual frame mapping size overflow".into()))?;
        if mapping_bytes > MAX_MAPPING_BYTES {
            return Err(TransportError(format!(
                "virtual frame mapping exceeds the {} MiB product limit",
                MAX_MAPPING_BYTES / 1024 / 1024
            )));
        }
        u32::try_from(mapping_bytes)
            .map_err(|_| TransportError("virtual frame mapping exceeds 4 GiB".into()))?;
        Ok(Self {
            mode,
            stride,
            plane_bytes,
            mapping_bytes,
        })
    }
}

#[repr(C)]
struct GateHeader {
    magic: u32,
    version: u32,
    width: u32,
    height: u32,
    stride: u32,
    refresh_numerator: u32,
    refresh_denominator: u32,
    flags: u32,
    nonce: [u8; 16],
}

#[derive(Clone)]
pub struct FrameChannel {
    mapping: Vec<u16>,
    event: Vec<u16>,
    layout: FrameLayout,
}

impl FrameChannel {
    pub fn mapping(&self) -> PCWSTR {
        PCWSTR(self.mapping.as_ptr())
    }

    pub fn event(&self) -> PCWSTR {
        PCWSTR(self.event.as_ptr())
    }

    pub fn layout(&self) -> FrameLayout {
        self.layout
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
    pub fn create(mode: VirtualMode) -> Result<Self, TransportError> {
        let layout = FrameLayout::new(mode)?;
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
                width: mode.width,
                height: mode.height,
                stride: layout.stride,
                refresh_numerator: mode.refresh_numerator,
                refresh_denominator: mode.refresh_denominator,
                flags: 0,
                nonce,
            });
            let _ = UnmapViewOfFile(gate_view);
        }

        let suffix = nonce
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect::<String>();
        let channel = FrameChannel {
            mapping: wide(&format!("Global\\SBMSFrame-v4-{suffix}")),
            event: wide(&format!("Global\\SBMSFrameReady-v4-{suffix}")),
            layout,
        };
        let mapping = match unsafe {
            CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                Some(&attributes),
                PAGE_READWRITE,
                0,
                layout.mapping_bytes as u32,
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

const fn greatest_common_divisor(mut left: u32, mut right: u32) -> u32 {
    while right != 0 {
        let remainder = left % right;
        left = right;
        right = remainder;
    }
    left
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dynamic_layout_matches_4640_by_2610() {
        let mode = VirtualMode::from_millihz(4640, 2610, 240_000).unwrap();
        let layout = FrameLayout::new(mode).unwrap();

        assert_eq!(mode.refresh_numerator, 240);
        assert_eq!(mode.refresh_denominator, 1);
        assert_eq!(layout.stride, 18_560);
        assert_eq!(layout.plane_bytes, 48_441_600);
        assert_eq!(layout.mapping_bytes, 96_883_224);
        assert_eq!(size_of::<GateHeader>(), 48);
    }

    #[test]
    fn invalid_or_excessive_modes_are_rejected() {
        assert!(VirtualMode::from_millihz(0, 2160, 240_000).is_err());
        assert!(VirtualMode::from_millihz(3840, 2160, 0).is_err());
        assert!(VirtualMode::from_millihz(16_384, 16_384, 240_000).is_err());
        assert!(
            FrameLayout::new(VirtualMode {
                width: 3840,
                height: 2160,
                refresh_numerator: 1001,
                refresh_denominator: 1,
            })
            .is_err()
        );
    }
}
