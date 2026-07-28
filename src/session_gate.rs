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
use windows::Win32::Security::{
    GetTokenInformation, PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES, TOKEN_QUERY, TOKEN_USER,
    TokenUser,
};
use windows::Win32::System::Memory::{
    CreateFileMappingW, FILE_MAP_WRITE, MapViewOfFile, PAGE_READWRITE, UnmapViewOfFile,
};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::core::{PCWSTR, PWSTR, w};

const GATE_MAGIC: u32 = 0x5342_4735;
const PROTOCOL_VERSION: u32 = 5;
const GATE_MAPPING: PCWSTR = w!("Global\\SBMSSession-v5");
const MAX_DIMENSION: u32 = 16_384;
const MAX_REFRESH_HZ: u64 = 1_000;

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
    ) -> Result<Self, SessionGateError> {
        if refresh_millihz == 0 {
            return Err(SessionGateError("virtual refresh must be non-zero".into()));
        }
        let divisor = greatest_common_divisor(refresh_millihz, 1_000);
        let mode = Self {
            width,
            height,
            refresh_numerator: refresh_millihz / divisor,
            refresh_denominator: 1_000 / divisor,
        };
        mode.validate()?;
        Ok(mode)
    }

    pub fn validate(self) -> Result<(), SessionGateError> {
        if self.width == 0
            || self.height == 0
            || self.width > MAX_DIMENSION
            || self.height > MAX_DIMENSION
        {
            return Err(SessionGateError(format!(
                "virtual dimensions must be between 1 and {MAX_DIMENSION}"
            )));
        }
        if self.refresh_numerator == 0 || self.refresh_denominator == 0 {
            return Err(SessionGateError(
                "virtual refresh numerator and denominator must be non-zero".into(),
            ));
        }
        if u64::from(self.refresh_numerator) > MAX_REFRESH_HZ * u64::from(self.refresh_denominator)
        {
            return Err(SessionGateError(format!(
                "virtual refresh must not exceed {MAX_REFRESH_HZ} Hz"
            )));
        }
        Ok(())
    }
}

#[repr(C)]
struct GateHeader {
    magic: u32,
    version: u32,
    width: u32,
    height: u32,
    refresh_numerator: u32,
    refresh_denominator: u32,
}

pub struct SessionGate {
    gate: HANDLE,
}

#[derive(Debug)]
pub struct SessionGateError(String);

impl Display for SessionGateError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for SessionGateError {}

impl SessionGate {
    pub fn create(mode: VirtualMode) -> Result<Self, SessionGateError> {
        mode.validate()?;
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
        .map_err(|error| SessionGateError(format!("create session gate: {error}")))?;
        if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
            unsafe {
                let _ = CloseHandle(gate);
            }
            return Err(SessionGateError(
                "another SBMS mapping session is already running".into(),
            ));
        }

        let gate_view =
            unsafe { MapViewOfFile(gate, FILE_MAP_WRITE, 0, 0, size_of::<GateHeader>()) };
        if gate_view.Value.is_null() {
            unsafe {
                let _ = CloseHandle(gate);
            }
            return Err(SessionGateError("map session gate failed".into()));
        }
        unsafe {
            gate_view.Value.cast::<GateHeader>().write(GateHeader {
                magic: GATE_MAGIC,
                version: PROTOCOL_VERSION,
                width: mode.width,
                height: mode.height,
                refresh_numerator: mode.refresh_numerator,
                refresh_denominator: mode.refresh_denominator,
            });
            let _ = UnmapViewOfFile(gate_view);
        }

        Ok(Self { gate })
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

impl Drop for SessionGate {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.gate);
        }
    }
}

struct SecurityDescriptor(PSECURITY_DESCRIPTOR);

impl SecurityDescriptor {
    fn for_current_user() -> Result<Self, SessionGateError> {
        let mut token = HANDLE::default();
        unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) }
            .map_err(|error| SessionGateError(format!("OpenProcessToken: {error}")))?;
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
        result.map_err(|error| SessionGateError(format!("GetTokenInformation: {error}")))?;

        let user = unsafe { &*token_user.as_ptr().cast::<TOKEN_USER>() };
        let mut sid = PWSTR::null();
        unsafe { ConvertSidToStringSidW(user.User.Sid, &mut sid) }
            .map_err(|error| SessionGateError(format!("ConvertSidToStringSidW: {error}")))?;
        let sid_text = unsafe { sid.to_string() };
        unsafe {
            LocalFree(Some(HLOCAL(sid.0.cast::<c_void>())));
        }
        let sid_text = sid_text.map_err(|error| SessionGateError(format!("user SID: {error}")))?;

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
        .map_err(|error| SessionGateError(format!("security descriptor: {error}")))?;
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
    fn dynamic_mode_header_matches_4640_by_2610() {
        let mode = VirtualMode::from_millihz(4640, 2610, 240_000).unwrap();

        assert_eq!(mode.refresh_numerator, 240);
        assert_eq!(mode.refresh_denominator, 1);
        assert_eq!(size_of::<GateHeader>(), 24);
    }

    #[test]
    fn invalid_or_excessive_modes_are_rejected() {
        assert!(VirtualMode::from_millihz(0, 2160, 240_000).is_err());
        assert!(VirtualMode::from_millihz(3840, 2160, 0).is_err());
        assert!(VirtualMode::from_millihz(16_385, 16_384, 240_000).is_err());
        assert!(
            VirtualMode {
                width: 3840,
                height: 2160,
                refresh_numerator: 1001,
                refresh_denominator: 1,
            }
            .validate()
            .is_err()
        );
    }
}
