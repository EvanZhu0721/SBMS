use std::error::Error;
use std::ffi::c_void;
use std::fmt::{Display, Formatter};
use std::mem::size_of;
use std::sync::Mutex;
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
use windows::Win32::System::SystemInformation::GetTickCount64;
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::core::{Error as WindowsError, PCWSTR, PWSTR, w};

use crate::diagnostics::{self, Level};
use crate::limits::{MAX_OUTPUTS, MAX_REFRESH_HZ, MAX_VIRTUAL_DIMENSION, MIN_REFRESH_HZ};

const GATE_MAGIC: u32 = 0x5342_4738;
const PROTOCOL_VERSION: u32 = 8;
const GATE_MAPPING: PCWSTR = w!("Global\\SBMSSession-v8");

// Optional diagnostics have their own ABI; the v8 request/identity layout stays unchanged.
const DRIVER_DIAGNOSTICS_MAPPING: PCWSTR = w!("Global\\SBMSDriverDiagnostics-v1");
const DRIVER_DIAGNOSTICS_MAGIC: u32 = 0x5342_4431;
const DRIVER_DIAGNOSTICS_CAPACITY: usize = 64;
static DRIVER_DIAGNOSTICS_CURSOR: Mutex<DriverDiagnosticsCursor> =
    Mutex::new(DriverDiagnosticsCursor {
        session_tick: 0,
        sequence: 0,
        dropped: 0,
    });

#[repr(C)]
struct DriverDiagnosticsBuffer {
    magic: u32,
    version: u32,
    capacity: u32,
    owner_pid: u32,
    session_tick_low: u32,
    session_tick_high: u32,
    writer_lock: AtomicU32,
    next_sequence: AtomicU32,
    dropped: AtomicU32,
    records: [DriverDiagnosticSlot; DRIVER_DIAGNOSTICS_CAPACITY],
}

#[repr(C)]
#[derive(Default)]
struct DriverDiagnosticSlot {
    publication: AtomicU32,
    sequence: AtomicU32,
    tick_low: AtomicU32,
    tick_high: AtomicU32,
    driver_pid: AtomicU32,
    connector_index: AtomicU32,
    assignment: AtomicU32,
    stage: AtomicU32,
    adapter_low: AtomicU32,
    adapter_high: AtomicI32,
    result: AtomicI32,
    detail: AtomicU32,
}

#[derive(Debug)]
struct DriverDiagnosticRecord {
    sequence: u32,
    tick_ms: u64,
    driver_pid: u32,
    connector_index: u32,
    assignment: u32,
    stage: u32,
    adapter_low: u32,
    adapter_high: i32,
    result: i32,
    detail: u32,
}

struct DriverDiagnosticsCursor {
    session_tick: u64,
    sequence: u32,
    dropped: u32,
}

impl DriverDiagnosticsBuffer {
    fn new(session_tick: u64) -> Self {
        Self {
            magic: DRIVER_DIAGNOSTICS_MAGIC,
            version: 1,
            capacity: DRIVER_DIAGNOSTICS_CAPACITY as u32,
            owner_pid: std::process::id(),
            session_tick_low: session_tick as u32,
            session_tick_high: (session_tick >> 32) as u32,
            writer_lock: AtomicU32::new(0),
            next_sequence: AtomicU32::new(0),
            dropped: AtomicU32::new(0),
            records: std::array::from_fn(|_| DriverDiagnosticSlot::default()),
        }
    }

    fn read_records(&self, after: u32) -> Vec<DriverDiagnosticRecord> {
        let mut records = Vec::new();
        for slot in &self.records {
            let publication = slot.publication.load(Ordering::Acquire);
            if publication == 0 || publication % 2 != 0 {
                continue;
            }
            let record = DriverDiagnosticRecord {
                sequence: slot.sequence.load(Ordering::Relaxed),
                tick_ms: u64::from(slot.tick_low.load(Ordering::Relaxed))
                    | (u64::from(slot.tick_high.load(Ordering::Relaxed)) << 32),
                driver_pid: slot.driver_pid.load(Ordering::Relaxed),
                connector_index: slot.connector_index.load(Ordering::Relaxed),
                assignment: slot.assignment.load(Ordering::Relaxed),
                stage: slot.stage.load(Ordering::Relaxed),
                adapter_low: slot.adapter_low.load(Ordering::Relaxed),
                adapter_high: slot.adapter_high.load(Ordering::Relaxed),
                result: slot.result.load(Ordering::Relaxed),
                detail: slot.detail.load(Ordering::Relaxed),
            };
            std::sync::atomic::fence(Ordering::AcqRel);
            if slot.publication.load(Ordering::Acquire) == publication && record.sequence > after {
                records.push(record);
            }
        }
        records.sort_unstable_by_key(|record| record.sequence);
        records
    }
}

fn driver_stage(stage: u32) -> &'static str {
    match stage {
        1 => "monitor-ready",
        2 => "assign-swapchain",
        3 => "unassign-swapchain",
        4 => "CreateDXGIFactory2",
        5 => "EnumAdapterByLuid",
        6 => "D3D11CreateDevice",
        7 => "QueryInterface(IDXGIDevice)",
        8 => "IddCxSwapChainSetDevice",
        9 => "allocate-drain",
        10 => "CreateEventW",
        11 => "CreateThread",
        12 => "drain-started",
        13 => "drain-first-frame",
        14 => "drain-exit",
        15 => "drain-stop-wait",
        16 => "GetDeviceRemovedReason",
        17 => "SetEvent(stop)",
        _ => "unknown",
    }
}

fn driver_detail(stage: u32, detail: u32) -> &'static str {
    match (stage, detail) {
        (1, _) => "OsTargetId",
        (14, 1) => "stop-before-acquire",
        (14, 2) => "stop-while-waiting",
        (14, 3) => "frame-wait-failed",
        (14, 4) => "ReleaseAndAcquireBuffer-failed",
        (14, 5) => "FinishedProcessingFrame-failed",
        (15, _) => "wait-result",
        _ => "none",
    }
}

/// Export the bounded driver lifecycle records to the ordinary rolling user log.
/// A missing/older driver or diagnostic failure never changes mapping behavior.
pub(crate) fn log_driver_diagnostics(context: &str) {
    let Ok(mut cursor) = DRIVER_DIAGNOSTICS_CURSOR.lock() else {
        return;
    };
    if cursor.session_tick == 0 {
        return;
    }
    let result = read_driver_diagnostics(DRIVER_DIAGNOSTICS_MAPPING, &mut cursor, context);
    if let Err(error) = result {
        diagnostics::log(
            Level::Warn,
            "driver",
            "diagnostics",
            None,
            format!("context={context} unavailable={error}"),
        );
    }
}

fn read_driver_diagnostics(
    name: PCWSTR,
    cursor: &mut DriverDiagnosticsCursor,
    context: &str,
) -> Result<(), SessionGateError> {
    let gate = unsafe { OpenFileMappingW(FILE_MAP_READ.0, false, name) }
        .map_err(|error| SessionGateError(format!("open diagnostics: {error}")))?;
    let view = unsafe {
        MapViewOfFile(
            gate,
            FILE_MAP_READ,
            0,
            0,
            size_of::<DriverDiagnosticsBuffer>(),
        )
    };
    let result = if view.Value.is_null() {
        Err(SessionGateError(format!(
            "map diagnostics: {}",
            WindowsError::from_thread()
        )))
    } else {
        let buffer = unsafe { &*view.Value.cast::<DriverDiagnosticsBuffer>() };
        let session_tick =
            u64::from(buffer.session_tick_low) | (u64::from(buffer.session_tick_high) << 32);
        if buffer.magic != DRIVER_DIAGNOSTICS_MAGIC
            || buffer.version != 1
            || buffer.capacity != DRIVER_DIAGNOSTICS_CAPACITY as u32
            || buffer.owner_pid != std::process::id()
            || session_tick != cursor.session_tick
        {
            Err(SessionGateError(
                "diagnostic protocol or session identity mismatch".into(),
            ))
        } else {
            let records = buffer.read_records(cursor.sequence);
            let dropped = buffer.dropped.load(Ordering::Acquire);
            if records.is_empty() && cursor.sequence == 0 {
                diagnostics::log(
                    Level::Info,
                    "driver",
                    "diagnostics",
                    None,
                    format!(
                        "context={context} session_tick={session_tick} no_driver_events=true support=unconfirmed (older driver or callbacks not reached)"
                    ),
                );
            }
            if dropped != cursor.dropped {
                diagnostics::log(
                    Level::Warn,
                    "driver",
                    "diagnostics",
                    None,
                    format!(
                        "context={context} session_tick={session_tick} contended_events_dropped={} total={dropped}",
                        dropped.wrapping_sub(cursor.dropped)
                    ),
                );
                cursor.dropped = dropped;
            }
            for record in records {
                if record.sequence > cursor.sequence + 1 {
                    diagnostics::log(
                        Level::Warn,
                        "driver",
                        "diagnostics",
                        None,
                        format!(
                            "context={context} session_tick={session_tick} overwritten_or_unread={} next_sequence={}",
                            record.sequence - cursor.sequence - 1,
                            record.sequence
                        ),
                    );
                }
                diagnostics::log(
                    if record.result < 0 {
                        Level::Warn
                    } else {
                        Level::Info
                    },
                    "driver",
                    "lifecycle",
                    None,
                    format!(
                        "context={context} host_pid={} session_tick={session_tick} sequence={} tick_ms={} driver_pid={} connector={} assignment={} stage={} adapter_luid={:08X}:{:08X} hresult=0x{:08X} detail={}({})",
                        std::process::id(),
                        record.sequence,
                        record.tick_ms,
                        record.driver_pid,
                        record.connector_index,
                        record.assignment,
                        driver_stage(record.stage),
                        record.adapter_high as u32,
                        record.adapter_low,
                        record.result as u32,
                        record.detail,
                        driver_detail(record.stage, record.detail)
                    ),
                );
                cursor.sequence = record.sequence;
            }
            Ok(())
        }
    };
    unsafe {
        if !view.Value.is_null() {
            let _ = UnmapViewOfFile(view);
        }
        let _ = CloseHandle(gate);
    }
    result
}

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
    driver_diagnostics: Option<(HANDLE, u64)>,
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

        let driver_diagnostics = match create_driver_diagnostics(
            DRIVER_DIAGNOSTICS_MAPPING,
            &attributes,
        ) {
            Ok((handle, tick)) => {
                if let Ok(mut cursor) = DRIVER_DIAGNOSTICS_CURSOR.lock() {
                    *cursor = DriverDiagnosticsCursor {
                        session_tick: tick,
                        sequence: 0,
                        dropped: 0,
                    };
                }
                diagnostics::log(
                    Level::Info,
                    "driver",
                    "diagnostics",
                    None,
                    format!(
                        "enabled host_pid={} session_tick={tick} capacity={DRIVER_DIAGNOSTICS_CAPACITY}",
                        std::process::id()
                    ),
                );
                Some((handle, tick))
            }
            Err(error) => {
                diagnostics::log(
                    Level::Warn,
                    "driver",
                    "diagnostics",
                    None,
                    format!("unavailable={error}; mapping continues without IDD diagnostics"),
                );
                None
            }
        };
        Ok(Self {
            gate,
            driver_diagnostics,
        })
    }
}

fn create_driver_diagnostics(
    name: PCWSTR,
    attributes: &SECURITY_ATTRIBUTES,
) -> Result<(HANDLE, u64), SessionGateError> {
    let handle = unsafe {
        CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            Some(attributes),
            PAGE_READWRITE,
            0,
            size_of::<DriverDiagnosticsBuffer>() as u32,
            name,
        )
    }
    .map_err(|error| SessionGateError(format!("create driver diagnostics: {error}")))?;
    if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
        unsafe {
            let _ = CloseHandle(handle);
        }
        return Err(SessionGateError(
            "previous diagnostic session is still open; its records will not be reused".into(),
        ));
    }
    let view = unsafe {
        MapViewOfFile(
            handle,
            FILE_MAP_WRITE,
            0,
            0,
            size_of::<DriverDiagnosticsBuffer>(),
        )
    };
    if view.Value.is_null() {
        let error = WindowsError::from_thread();
        unsafe {
            let _ = CloseHandle(handle);
        }
        return Err(SessionGateError(format!(
            "map new driver diagnostics: {error}"
        )));
    }
    let tick = unsafe { GetTickCount64() }.max(1);
    unsafe {
        view.Value
            .cast::<DriverDiagnosticsBuffer>()
            .write(DriverDiagnosticsBuffer::new(tick));
        let _ = UnmapViewOfFile(view);
    }
    Ok((handle, tick))
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
        if let Some((handle, tick)) = self.driver_diagnostics.take() {
            log_driver_diagnostics("session-gate-close");
            if let Ok(mut cursor) = DRIVER_DIAGNOSTICS_CURSOR.lock()
                && cursor.session_tick == tick
            {
                cursor.session_tick = 0;
            }
            unsafe {
                let _ = CloseHandle(handle);
            }
        }
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
    fn driver_protocol_layout_encodes_modes_and_sparse_connectors() {
        let mode = VirtualMode::from_millihz(4640, 2610, 240_000).unwrap();
        let config = GateConfig::from_displays(&[VirtualDisplayConfig::new(0, mode)]).unwrap();

        assert_eq!(mode.refresh_numerator, 240);
        assert_eq!(mode.refresh_denominator, 1);
        assert_eq!(size_of::<GateHeader>(), 16);
        assert_eq!(size_of::<GateEntry>(), 20);
        assert_eq!(size_of::<GateIdentity>(), 16);
        assert_eq!(std::mem::offset_of!(GateConfig, identities), 336);
        assert_eq!(size_of::<GateConfig>(), 592);
        assert_eq!(size_of::<DriverDiagnosticSlot>(), 48);
        assert_eq!(std::mem::offset_of!(DriverDiagnosticsBuffer, records), 36);
        assert_eq!(size_of::<DriverDiagnosticsBuffer>(), 3108);
        assert_eq!(config.header.count, 1);
        assert_eq!(config.entries[0].width, 4640);
        assert_eq!(config.entries[0].height, 2610);

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
    fn display_validation_accepts_capacity_and_rejects_invalid_modes_or_sets() {
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
        use std::hash::BuildHasher;
        let nonce = std::collections::hash_map::RandomState::new().hash_one(());
        let name = wide(&format!(
            "Local\\SBMS-identity-test-{}-{nonce:x}",
            std::process::id()
        ));
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

        let diagnostic_name = wide(&format!(
            "Local\\SBMS-driver-diagnostics-test-{}-{nonce:x}",
            std::process::id()
        ));
        let name = PCWSTR(diagnostic_name.as_ptr());
        let descriptor = SecurityDescriptor::for_current_user().unwrap();
        let (handle, tick) = create_driver_diagnostics(name, &descriptor.attributes()).unwrap();
        assert!(create_driver_diagnostics(name, &descriptor.attributes()).is_err());
        let view = unsafe {
            MapViewOfFile(
                handle,
                FILE_MAP_WRITE,
                0,
                0,
                size_of::<DriverDiagnosticsBuffer>(),
            )
        };
        assert!(!view.Value.is_null());
        let buffer = unsafe { &*view.Value.cast::<DriverDiagnosticsBuffer>() };
        let mut cursor = DriverDiagnosticsCursor {
            session_tick: tick,
            sequence: 0,
            dropped: 0,
        };
        read_driver_diagnostics(name, &mut cursor, "isolated-test-empty").unwrap();
        assert_eq!(cursor.sequence, 0);

        // A partly published record is invisible across the independently opened view.
        let slot = &buffer.records[0];
        slot.publication.store(1, Ordering::Release);
        slot.sequence.store(1, Ordering::Relaxed);
        slot.connector_index.store(5, Ordering::Relaxed);
        slot.assignment.store(3, Ordering::Relaxed);
        slot.stage.store(14, Ordering::Relaxed);
        slot.adapter_low.store(42, Ordering::Relaxed);
        slot.adapter_high.store(-9, Ordering::Relaxed);
        slot.result.store(0x887a0026u32 as i32, Ordering::Relaxed);
        slot.detail.store(4, Ordering::Relaxed);
        read_driver_diagnostics(name, &mut cursor, "isolated-test-writing").unwrap();
        assert_eq!(cursor.sequence, 0);
        slot.publication.store(2, Ordering::Release);
        let records = buffer.read_records(0);
        assert_eq!(records.len(), 1);
        assert_eq!(
            (
                records[0].connector_index,
                records[0].assignment,
                records[0].adapter_high
            ),
            (5, 3, -9)
        );
        assert_eq!(records[0].result as u32, 0x887a0026);
        read_driver_diagnostics(name, &mut cursor, "isolated-test-published").unwrap();
        assert_eq!(cursor.sequence, 1);
        assert!(buffer.read_records(cursor.sequence).is_empty());

        // Wrap the bounded ring and preserve the newest sequence order, not slot order.
        for sequence in 2..=70u32 {
            let slot = &buffer.records[(sequence as usize - 1) % DRIVER_DIAGNOSTICS_CAPACITY];
            slot.publication.store(sequence * 2 - 1, Ordering::Release);
            slot.sequence.store(sequence, Ordering::Relaxed);
            slot.publication.store(sequence * 2, Ordering::Release);
        }
        let records = buffer.read_records(cursor.sequence);
        assert_eq!(records.len(), DRIVER_DIAGNOSTICS_CAPACITY);
        assert_eq!(records.first().unwrap().sequence, 7);
        assert_eq!(records.last().unwrap().sequence, 70);
        buffer.dropped.store(2, Ordering::Release);
        read_driver_diagnostics(name, &mut cursor, "isolated-test-wrapped").unwrap();
        assert_eq!((cursor.sequence, cursor.dropped), (70, 2));
        cursor.session_tick = tick + 1;
        assert!(read_driver_diagnostics(name, &mut cursor, "isolated-test-wrong-session").is_err());
        unsafe {
            let _ = UnmapViewOfFile(view);
            let _ = CloseHandle(handle);
        }
        cursor.session_tick = tick;
        assert!(read_driver_diagnostics(name, &mut cursor, "isolated-test-closed").is_err());
    }
}
