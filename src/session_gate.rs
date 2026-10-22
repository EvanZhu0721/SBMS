use std::error::Error;
use std::ffi::c_void;
use std::fmt::{Display, Formatter};
use std::mem::size_of;
use std::sync::atomic::{AtomicI32, AtomicU32, Ordering};

use serde::{Deserialize, Serialize};
use windows::Win32::Foundation::{
    CloseHandle, ERROR_ALREADY_EXISTS, ERROR_FILE_NOT_FOUND, GetLastError, HANDLE, HLOCAL,
    INVALID_HANDLE_VALUE, LocalFree,
};
use windows::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
};
use windows::Win32::Security::{
    GetTokenInformation, PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES, TOKEN_QUERY, TOKEN_USER,
    TokenUser,
};
use windows::Win32::System::Memory::{
    CreateFileMappingW, FILE_MAP_READ, FILE_MAP_WRITE, MapViewOfFile, OpenFileMappingW,
    PAGE_READWRITE, UnmapViewOfFile,
};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::core::{PCWSTR, PWSTR, w};

use crate::limits::{MAX_OUTPUTS, MAX_REFRESH_HZ, MAX_VIRTUAL_DIMENSION, MIN_REFRESH_HZ};

const GATE_MAGIC: u32 = 0x5342_4738;
const PROTOCOL_VERSION: u32 = 8;
const GATE_MAPPING: PCWSTR = w!("Global\\SBMSSession-v8");

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
#[derive(Debug)]
struct GateConfig {
    header: GateHeader,
    entries: [GateEntry; MAX_OUTPUTS],
    identities: [GateIdentity; MAX_OUTPUTS],
}

// The driver brackets each publication with InterlockedExchange. Odd sequence
// numbers are being written; equal, nonzero even numbers give a complete receipt.
#[repr(C)]
#[derive(Debug, Default)]
struct GateIdentity {
    sequence: AtomicU32,
    adapter_low: AtomicU32,
    adapter_high: AtomicI32,
    target_id: AtomicU32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct ConnectorIdentity {
    pub connector_index: u32,
    pub adapter_low: u32,
    pub adapter_high: i32,
    pub target_id: u32,
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
            identities: std::array::from_fn(|_| GateIdentity::default()),
        })
    }

    fn published_identities(&self) -> Result<Vec<ConnectorIdentity>, SessionGateError> {
        if self.header.magic != GATE_MAGIC
            || self.header.version != PROTOCOL_VERSION
            || self.header.count == 0
            || self.header.count as usize > MAX_OUTPUTS
        {
            return Err(SessionGateError(
                "invalid driver identity protocol header".into(),
            ));
        }
        let mut identities = Vec::new();
        for entry in &self.entries[..self.header.count as usize] {
            let slot = self
                .identities
                .get(entry.connector_index as usize)
                .ok_or_else(|| {
                    SessionGateError("driver identity connector is out of range".into())
                })?;
            let sequence = slot.sequence.load(Ordering::Acquire);
            if sequence == 0 || sequence % 2 != 0 {
                continue;
            }
            let identity = ConnectorIdentity {
                connector_index: entry.connector_index,
                adapter_low: slot.adapter_low.load(Ordering::Relaxed),
                adapter_high: slot.adapter_high.load(Ordering::Relaxed),
                target_id: slot.target_id.load(Ordering::Relaxed),
            };
            // AcqRel prevents payload reads moving after the second sequence load.
            std::sync::atomic::fence(Ordering::AcqRel);
            if slot.sequence.load(Ordering::Acquire) != sequence {
                continue;
            }
            if identities.iter().any(|other: &ConnectorIdentity| {
                (other.adapter_low, other.adapter_high, other.target_id)
                    == (
                        identity.adapter_low,
                        identity.adapter_high,
                        identity.target_id,
                    )
            }) {
                return Err(SessionGateError(
                    "driver published duplicate Windows target identities".into(),
                ));
            }
            identities.push(identity);
        }
        Ok(identities)
    }
}

pub(crate) fn published_connector_identities() -> Result<Vec<ConnectorIdentity>, SessionGateError> {
    read_connector_identities(GATE_MAPPING)
}

fn read_connector_identities(name: PCWSTR) -> Result<Vec<ConnectorIdentity>, SessionGateError> {
    let gate = match unsafe { OpenFileMappingW(FILE_MAP_READ.0, false, name) } {
        Ok(gate) => gate,
        Err(error)
            if error.code() == windows::core::HRESULT::from_win32(ERROR_FILE_NOT_FOUND.0) =>
        {
            return Ok(Vec::new());
        }
        Err(error) => return Err(SessionGateError(format!("open driver identities: {error}"))),
    };
    let view = unsafe { MapViewOfFile(gate, FILE_MAP_READ, 0, 0, size_of::<GateConfig>()) };
    let result = if view.Value.is_null() {
        Err(SessionGateError(format!(
            "map driver identities: {}",
            windows::core::Error::from_thread()
        )))
    } else {
        // The immutable request and atomic response remain mapped for this read.
        let result = unsafe { &*view.Value.cast::<GateConfig>() }.published_identities();
        unsafe {
            let _ = UnmapViewOfFile(view);
        }
        result
    };
    unsafe {
        let _ = CloseHandle(gate);
    }
    result
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
        assert_eq!(size_of::<GateIdentity>(), 16);
        assert_eq!(std::mem::offset_of!(GateConfig, identities), 336);
        assert_eq!(size_of::<GateConfig>(), 592);
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

    #[test]
    fn driver_receipts_are_published_atomically_for_sparse_slots() {
        let config = GateConfig::from_displays(&[
            VirtualDisplayConfig::new(5, VirtualMode::default()),
            VirtualDisplayConfig::new(1, VirtualMode::default()),
        ])
        .unwrap();
        let slot = &config.identities[5];
        slot.adapter_low.store(42, Ordering::Relaxed);
        slot.adapter_high.store(-9, Ordering::Relaxed);
        slot.target_id.store(256, Ordering::Relaxed);
        // Partly written responses must not be used before the driver's publication.
        assert!(config.published_identities().unwrap().is_empty());
        slot.sequence.store(2, Ordering::Release);
        assert_eq!(
            config.published_identities().unwrap(),
            vec![ConnectorIdentity {
                connector_index: 5,
                adapter_low: 42,
                adapter_high: -9,
                target_id: 256,
            }]
        );

        // A restarted driver must not expose partially replaced identity fields.
        slot.sequence.store(3, Ordering::Release);
        slot.adapter_low.store(84, Ordering::Relaxed);
        assert!(config.published_identities().unwrap().is_empty());
        slot.target_id.store(512, Ordering::Relaxed);
        slot.sequence.store(4, Ordering::Release);
        assert_eq!(config.published_identities().unwrap()[0].target_id, 512);
        assert_eq!(config.published_identities().unwrap()[0].adapter_low, 84);

        let other = &config.identities[1];
        other.adapter_low.store(84, Ordering::Relaxed);
        other.adapter_high.store(-9, Ordering::Relaxed);
        other.target_id.store(512, Ordering::Relaxed);
        other.sequence.store(2, Ordering::Release);
        assert!(
            config
                .published_identities()
                .unwrap_err()
                .to_string()
                .contains("duplicate")
        );
    }

    #[test]
    fn connector_receipt_crosses_independent_windows_mapping_views() {
        let name = wide(&format!("Local\\SBMS-identity-test-{}", std::process::id()));
        let config =
            GateConfig::from_displays(&[VirtualDisplayConfig::new(5, VirtualMode::default())])
                .unwrap();
        let handle = unsafe {
            CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                None,
                PAGE_READWRITE,
                0,
                size_of::<GateConfig>() as u32,
                PCWSTR(name.as_ptr()),
            )
        }
        .unwrap();
        let view = unsafe { MapViewOfFile(handle, FILE_MAP_WRITE, 0, 0, size_of::<GateConfig>()) };
        assert!(!view.Value.is_null());
        unsafe {
            view.Value.cast::<GateConfig>().write(config);
        }
        assert!(
            read_connector_identities(PCWSTR(name.as_ptr()))
                .unwrap()
                .is_empty()
        );
        let slot = unsafe { &(*view.Value.cast::<GateConfig>()).identities[5] };
        slot.sequence.store(1, Ordering::SeqCst);
        slot.adapter_low.store(0xaabbccdd, Ordering::Relaxed);
        slot.adapter_high.store(-2, Ordering::Relaxed);
        slot.target_id.store(0, Ordering::Relaxed);
        slot.sequence.store(2, Ordering::Release);
        let received = read_connector_identities(PCWSTR(name.as_ptr())).unwrap();
        unsafe {
            let _ = UnmapViewOfFile(view);
            let _ = CloseHandle(handle);
        }
        assert_eq!(
            received,
            vec![ConnectorIdentity {
                connector_index: 5,
                adapter_low: 0xaabbccdd,
                adapter_high: -2,
                target_id: 0,
            }]
        );
    }
}
