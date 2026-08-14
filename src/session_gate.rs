use std::error::Error;
use std::ffi::c_void;
use std::fmt::{Display, Formatter};
use std::mem::size_of;

use serde::{Deserialize, Serialize};
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

use crate::limits::{MAX_OUTPUTS, MAX_REFRESH_HZ, MAX_VIRTUAL_DIMENSION, MIN_REFRESH_HZ};

const GATE_MAGIC: u32 = 0x5342_4737;
const PROTOCOL_VERSION: u32 = 7;
const GATE_MAPPING: PCWSTR = w!("Global\\SBMSSession-v7");

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
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
            || self.width > MAX_VIRTUAL_DIMENSION
            || self.height > MAX_VIRTUAL_DIMENSION
        {
            return Err(SessionGateError(format!(
                "virtual dimensions must be between 1 and {MAX_VIRTUAL_DIMENSION}"
            )));
        }
        if self.refresh_numerator == 0 || self.refresh_denominator == 0 {
            return Err(SessionGateError(
                "virtual refresh numerator and denominator must be non-zero".into(),
            ));
        }
        if u64::from(self.refresh_numerator) < MIN_REFRESH_HZ * u64::from(self.refresh_denominator)
        {
            return Err(SessionGateError(format!(
                "virtual refresh must be at least {MIN_REFRESH_HZ} Hz"
            )));
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

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct VirtualDisplayConfig {
    pub connector_index: u32,
    pub mode: VirtualMode,
}

impl VirtualDisplayConfig {
    pub const fn new(connector_index: u32, mode: VirtualMode) -> Self {
        Self {
            connector_index,
            mode,
        }
    }

    pub fn validate(self) -> Result<(), SessionGateError> {
        if self.connector_index >= MAX_OUTPUTS as u32 {
            return Err(SessionGateError(format!(
                "connector index must be between 0 and {}",
                MAX_OUTPUTS - 1
            )));
        }
        self.mode.validate()
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
struct GateHeader {
    magic: u32,
    version: u32,
    count: u32,
    reserved: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
struct GateEntry {
    connector_index: u32,
    width: u32,
    height: u32,
    refresh_numerator: u32,
    refresh_denominator: u32,
}

impl From<VirtualDisplayConfig> for GateEntry {
    fn from(config: VirtualDisplayConfig) -> Self {
        Self {
            connector_index: config.connector_index,
            width: config.mode.width,
            height: config.mode.height,
            refresh_numerator: config.mode.refresh_numerator,
            refresh_denominator: config.mode.refresh_denominator,
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct GateConfig {
    header: GateHeader,
    entries: [GateEntry; MAX_OUTPUTS],
}

impl GateConfig {
    fn from_displays(displays: &[VirtualDisplayConfig]) -> Result<Self, SessionGateError> {
        validate_displays(displays)?;

        let mut entries = [GateEntry::default(); MAX_OUTPUTS];
        for (entry, display) in entries.iter_mut().zip(displays.iter().copied()) {
            *entry = display.into();
        }

        Ok(Self {
            header: GateHeader {
                magic: GATE_MAGIC,
                version: PROTOCOL_VERSION,
                count: displays.len() as u32,
                reserved: 0,
            },
            entries,
        })
    }
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
        Self::create_many(&[VirtualDisplayConfig::new(0, mode)])
    }

    pub fn create_many(displays: &[VirtualDisplayConfig]) -> Result<Self, SessionGateError> {
        let config = GateConfig::from_displays(displays)?;
        let descriptor = SecurityDescriptor::for_current_user()?;
        let attributes = descriptor.attributes();
        let gate = unsafe {
            CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                Some(&attributes),
                PAGE_READWRITE,
                0,
                size_of::<GateConfig>() as u32,
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
            unsafe { MapViewOfFile(gate, FILE_MAP_WRITE, 0, 0, size_of::<GateConfig>()) };
        if gate_view.Value.is_null() {
            unsafe {
                let _ = CloseHandle(gate);
            }
            return Err(SessionGateError("map session gate failed".into()));
        }
        unsafe {
            gate_view.Value.cast::<GateConfig>().write(config);
            let _ = UnmapViewOfFile(gate_view);
        }

        Ok(Self { gate })
    }
}

fn validate_displays(displays: &[VirtualDisplayConfig]) -> Result<(), SessionGateError> {
    if displays.is_empty() {
        return Err(SessionGateError(
            "at least one virtual display is required".into(),
        ));
    }
    if displays.len() > MAX_OUTPUTS {
        return Err(SessionGateError(format!(
            "at most {MAX_OUTPUTS} virtual displays are supported"
        )));
    }

    let mut seen_connectors = 0u32;
    for display in displays {
        display.validate()?;
        let connector_bit = 1u32 << display.connector_index;
        if seen_connectors & connector_bit != 0 {
            return Err(SessionGateError(format!(
                "connector index {} is duplicated",
                display.connector_index
            )));
        }
        seen_connectors |= connector_bit;
    }
    Ok(())
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
        let config = GateConfig::from_displays(&[VirtualDisplayConfig::new(0, mode)]).unwrap();

        assert_eq!(mode.refresh_numerator, 240);
        assert_eq!(mode.refresh_denominator, 1);
        assert_eq!(size_of::<GateHeader>(), 16);
        assert_eq!(size_of::<GateEntry>(), 20);
        assert_eq!(size_of::<GateConfig>(), 336);
        assert_eq!(config.header.count, 1);
        assert_eq!(config.entries[0].width, 4640);
        assert_eq!(config.entries[0].height, 2610);
    }

    #[test]
    fn invalid_or_excessive_modes_are_rejected() {
        assert!(VirtualMode::from_millihz(0, 2160, 240_000).is_err());
        assert!(VirtualMode::from_millihz(3840, 2160, 0).is_err());
        assert!(VirtualMode::from_millihz(3840, 2160, 500).is_err());
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

    #[test]
    fn multiple_connectors_are_encoded_in_caller_order() {
        let displays = [
            VirtualDisplayConfig::new(5, VirtualMode::default()),
            VirtualDisplayConfig::new(
                2,
                VirtualMode {
                    width: 2560,
                    height: 1440,
                    refresh_numerator: 165,
                    refresh_denominator: 1,
                },
            ),
        ];

        let config = GateConfig::from_displays(&displays).unwrap();

        assert_eq!(config.header.magic, GATE_MAGIC);
        assert_eq!(config.header.version, PROTOCOL_VERSION);
        assert_eq!(config.header.count, 2);
        assert_eq!(config.header.reserved, 0);
        assert_eq!(config.entries[0].connector_index, 5);
        assert_eq!(config.entries[0].width, 3840);
        assert_eq!(config.entries[1].connector_index, 2);
        assert_eq!(config.entries[1].refresh_numerator, 165);
        assert_eq!(config.entries[2], GateEntry::default());
    }

    #[test]
    fn public_display_config_has_stable_json_fields() {
        let display = VirtualDisplayConfig::new(4, VirtualMode::default());

        let json = serde_json::to_value(display).unwrap();

        assert_eq!(json["connector_index"], 4);
        assert_eq!(json["mode"]["width"], 3840);
        assert_eq!(json["mode"]["height"], 2160);
        assert_eq!(json["mode"]["refresh_numerator"], 240);
        assert_eq!(json["mode"]["refresh_denominator"], 1);
        assert_eq!(
            serde_json::from_value::<VirtualDisplayConfig>(json).unwrap(),
            display
        );
    }

    #[test]
    fn display_set_validation_rejects_invalid_shapes() {
        assert!(validate_displays(&[]).is_err());

        let too_many = [VirtualDisplayConfig::new(0, VirtualMode::default()); MAX_OUTPUTS + 1];
        assert!(validate_displays(&too_many).is_err());

        let duplicate = [
            VirtualDisplayConfig::new(3, VirtualMode::default()),
            VirtualDisplayConfig::new(3, VirtualMode::default()),
        ];
        assert!(validate_displays(&duplicate).is_err());

        assert!(
            validate_displays(&[VirtualDisplayConfig::new(
                MAX_OUTPUTS as u32,
                VirtualMode::default()
            )])
            .is_err()
        );

        assert!(
            validate_displays(&[VirtualDisplayConfig::new(
                0,
                VirtualMode {
                    width: 0,
                    ..VirtualMode::default()
                }
            )])
            .is_err()
        );
    }

    #[test]
    fn sixteen_distinct_connectors_are_supported() {
        let displays: [VirtualDisplayConfig; MAX_OUTPUTS] =
            std::array::from_fn(|connector_index| {
                VirtualDisplayConfig::new(connector_index as u32, VirtualMode::default())
            });

        let config = GateConfig::from_displays(&displays).unwrap();

        assert_eq!(config.header.count, MAX_OUTPUTS as u32);
        assert_eq!(config.entries[15].connector_index, 15);
    }
}
