use std::error::Error;
use std::fmt::{Display as FmtDisplay, Formatter};
use std::mem::size_of;
use std::thread;
use std::time::Duration;

use windows::Win32::Devices::DeviceAndDriverInstallation::{
    DICS_FLAG_GLOBAL, DIGCF_DEVICEINTERFACE, DIGCF_PRESENT, DIREG_DEV, HDEVINFO,
    SP_DEVICE_INTERFACE_DATA, SP_DEVICE_INTERFACE_DETAIL_DATA_W, SP_DEVINFO_DATA,
    SetupDiDestroyDeviceInfoList, SetupDiGetClassDevsW, SetupDiGetDeviceInterfaceDetailW,
    SetupDiOpenDevRegKey, SetupDiOpenDeviceInterfaceW,
};
use windows::Win32::Devices::Display::{
    DISPLAYCONFIG_ADAPTER_NAME, DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME,
    DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
    DISPLAYCONFIG_DEVICE_INFO_HEADER, DISPLAYCONFIG_MODE_INFO, DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
    DISPLAYCONFIG_MODE_INFO_TYPE_TARGET, DISPLAYCONFIG_PATH_INFO, DISPLAYCONFIG_ROTATION_IDENTITY,
    DISPLAYCONFIG_ROTATION_ROTATE90, DISPLAYCONFIG_ROTATION_ROTATE180,
    DISPLAYCONFIG_ROTATION_ROTATE270, DISPLAYCONFIG_SOURCE_DEVICE_NAME,
    DISPLAYCONFIG_TARGET_DEVICE_NAME, DisplayConfigGetDeviceInfo, GUID_DEVINTERFACE_MONITOR,
    GetDisplayConfigBufferSizes, QDC_ONLY_ACTIVE_PATHS, QueryDisplayConfig,
};
use windows::Win32::Foundation::{ERROR_INSUFFICIENT_BUFFER, ERROR_SUCCESS, RECT};
use windows::Win32::Graphics::Gdi::{
    CDS_FULLSCREEN, CDS_NORESET, CDS_TYPE, CDS_UPDATEREGISTRY, ChangeDisplaySettingsExW, DEVMODEW,
    DISP_CHANGE_SUCCESSFUL, DM_BITSPERPEL, DM_DISPLAYFREQUENCY, DM_PELSHEIGHT, DM_PELSWIDTH,
    DM_POSITION, ENUM_DISPLAY_SETTINGS_FLAGS, ENUM_DISPLAY_SETTINGS_MODE, EnumDisplaySettingsExW,
};
use windows::Win32::System::Registry::{
    HKEY, KEY_READ, REG_BINARY, REG_VALUE_TYPE, RegCloseKey, RegQueryValueExW,
};
use windows::core::{PCWSTR, w};

use crate::frame_transport::VirtualMode;
use crate::geometry::Rotation;

#[derive(Clone, Debug)]
pub struct Display {
    pub id: String,
    pub name: String,
    pub device_name: String,
    pub rect: RECT,
    pub native_width: u32,
    pub native_height: u32,
    pub physical_width_mm: Option<f64>,
    pub physical_height_mm: Option<f64>,
    pub rotation: Rotation,
    pub refresh_numerator: u32,
    pub refresh_denominator: u32,
    pub primary: bool,
    pub virtual_display: bool,
}

#[derive(Debug)]
pub struct DisplayError(String);

impl FmtDisplay for DisplayError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for DisplayError {}

pub fn active_displays() -> Result<Vec<Display>, DisplayError> {
    let mut last_error = None;
    for _ in 0..5 {
        match read_active_displays() {
            Ok(displays) => return Ok(displays),
            Err(error) => last_error = Some(error),
        }
        thread::sleep(Duration::from_millis(20));
    }
    Err(last_error.expect("display read retry ran at least once"))
}

pub fn apply_display_mode(display: &Display, requested: VirtualMode) -> Result<(), DisplayError> {
    let requested_hz = u64::from(requested.refresh_numerator)
        .checked_add(u64::from(requested.refresh_denominator) / 2)
        .and_then(|value| value.checked_div(u64::from(requested.refresh_denominator)))
        .and_then(|value| u32::try_from(value).ok())
        .ok_or_else(|| DisplayError("requested refresh rate is invalid".into()))?;
    let device_name = wide(&display.device_name);
    let mut mode = find_display_mode(
        PCWSTR(device_name.as_ptr()),
        requested.width,
        requested.height,
        requested_hz,
    )
    .ok_or_else(|| {
        DisplayError(format!(
            "{} does not expose requested mode {}x{}@{}",
            display.device_name, requested.width, requested.height, requested_hz
        ))
    })?;
    mode.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
    let result = unsafe {
        ChangeDisplaySettingsExW(
            PCWSTR(device_name.as_ptr()),
            Some(&raw const mode),
            None,
            CDS_FULLSCREEN,
            None,
        )
    };
    if result != DISP_CHANGE_SUCCESSFUL {
        return Err(DisplayError(format!(
            "ChangeDisplaySettingsExW({}) rejected {}x{}@{} with status {}",
            display.device_name, requested.width, requested.height, requested_hz, result.0
        )));
    }
    Ok(())
}

pub fn restore_display_topology(expected: &[Display]) -> Result<(), DisplayError> {
    let current = active_displays()?;
    let mut pending = false;
    for display in expected.iter().filter(|display| !display.virtual_display) {
        let Some(actual) = current
            .iter()
            .find(|actual| actual.id.eq_ignore_ascii_case(&display.id))
        else {
            continue;
        };
        if actual.rect == display.rect
            && actual.refresh_numerator == display.refresh_numerator
            && actual.refresh_denominator == display.refresh_denominator
        {
            continue;
        }

        let width = u32::try_from(display.rect.right - display.rect.left)
            .map_err(|_| DisplayError("saved display width is invalid".into()))?;
        let height = u32::try_from(display.rect.bottom - display.rect.top)
            .map_err(|_| DisplayError("saved display height is invalid".into()))?;
        let refresh_hz = u64::from(display.refresh_numerator)
            .checked_add(u64::from(display.refresh_denominator) / 2)
            .and_then(|value| value.checked_div(u64::from(display.refresh_denominator)))
            .and_then(|value| u32::try_from(value).ok())
            .ok_or_else(|| DisplayError("saved display refresh rate is invalid".into()))?;
        let device_name = wide(&actual.device_name);
        let mut mode = find_display_mode(PCWSTR(device_name.as_ptr()), width, height, refresh_hz)
            .ok_or_else(|| {
            DisplayError(format!(
                "{} no longer exposes saved mode {}x{}@{}",
                actual.device_name, width, height, refresh_hz
            ))
        })?;
        let position = unsafe { &mut mode.Anonymous1.Anonymous2.dmPosition };
        position.x = display.rect.left;
        position.y = display.rect.top;
        mode.dmFields =
            DM_POSITION | DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        let result = unsafe {
            ChangeDisplaySettingsExW(
                PCWSTR(device_name.as_ptr()),
                Some(&raw const mode),
                None,
                CDS_UPDATEREGISTRY | CDS_NORESET,
                None,
            )
        };
        if result != DISP_CHANGE_SUCCESSFUL {
            return Err(DisplayError(format!(
                "could not stage saved topology for {}: status {}",
                actual.device_name, result.0
            )));
        }
        pending = true;
    }
    if pending {
        let result = unsafe {
            ChangeDisplaySettingsExW(PCWSTR::null(), None, None, CDS_TYPE::default(), None)
        };
        if result != DISP_CHANGE_SUCCESSFUL {
            return Err(DisplayError(format!(
                "could not apply saved physical topology: status {}",
                result.0
            )));
        }
    }
    Ok(())
}

fn find_display_mode(
    device_name: PCWSTR,
    width: u32,
    height: u32,
    refresh_hz: u32,
) -> Option<DEVMODEW> {
    for index in 0..u32::MAX {
        let mut mode = DEVMODEW {
            dmSize: size_of::<DEVMODEW>() as u16,
            ..Default::default()
        };
        if !unsafe {
            EnumDisplaySettingsExW(
                device_name,
                ENUM_DISPLAY_SETTINGS_MODE(index),
                &mut mode,
                ENUM_DISPLAY_SETTINGS_FLAGS::default(),
            )
        }
        .as_bool()
        {
            break;
        }
        if mode.dmPelsWidth == width
            && mode.dmPelsHeight == height
            && mode.dmDisplayFrequency == refresh_hz
        {
            return Some(mode);
        }
    }
    None
}

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

fn read_active_displays() -> Result<Vec<Display>, DisplayError> {
    for _ in 0..3 {
        let mut path_count = 0;
        let mut mode_count = 0;
        let size_result = unsafe {
            GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &mut path_count, &mut mode_count)
        };
        if size_result != ERROR_SUCCESS {
            return Err(win32_error("GetDisplayConfigBufferSizes", size_result.0));
        }

        let mut paths = vec![DISPLAYCONFIG_PATH_INFO::default(); path_count as usize];
        let mut modes = vec![DISPLAYCONFIG_MODE_INFO::default(); mode_count as usize];
        let query_result = unsafe {
            QueryDisplayConfig(
                QDC_ONLY_ACTIVE_PATHS,
                &mut path_count,
                paths.as_mut_ptr(),
                &mut mode_count,
                modes.as_mut_ptr(),
                None,
            )
        };
        if query_result == ERROR_INSUFFICIENT_BUFFER {
            continue;
        }
        if query_result != ERROR_SUCCESS {
            return Err(win32_error("QueryDisplayConfig", query_result.0));
        }
        paths.truncate(path_count as usize);
        modes.truncate(mode_count as usize);

        let mut displays = Vec::with_capacity(paths.len());
        for path in paths {
            let source = device_name(
                DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                path.sourceInfo.adapterId,
                path.sourceInfo.id,
            )?;
            let target = target_name(path.targetInfo.adapterId, path.targetInfo.id)?;
            let adapter = adapter_name(path.sourceInfo.adapterId)?;
            let mode_index = unsafe { path.sourceInfo.Anonymous.modeInfoIdx } as usize;
            let Some(mode) = modes.get(mode_index) else {
                continue;
            };
            if mode.infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE {
                continue;
            }
            let mode = unsafe { mode.Anonymous.sourceMode };
            let left = mode.position.x;
            let top = mode.position.y;
            let rotation = rotation(path.targetInfo.rotation);
            let (native_width, native_height) =
                target_active_size(&path, &modes).unwrap_or(match rotation {
                    Rotation::Deg90 | Rotation::Deg270 => (mode.height, mode.width),
                    Rotation::Deg0 | Rotation::Deg180 => (mode.width, mode.height),
                });
            let name = wide_string(&target.monitorFriendlyDeviceName);
            let id = wide_string(&target.monitorDevicePath);
            let physical_size = physical_dimensions_mm(&id);
            displays.push(Display {
                id,
                name: name.clone(),
                device_name: source,
                rect: RECT {
                    left,
                    top,
                    right: left + mode.width as i32,
                    bottom: top + mode.height as i32,
                },
                native_width,
                native_height,
                physical_width_mm: physical_size.map(|size| size.0),
                physical_height_mm: physical_size.map(|size| size.1),
                rotation,
                refresh_numerator: path.targetInfo.refreshRate.Numerator,
                refresh_denominator: path.targetInfo.refreshRate.Denominator,
                primary: left == 0 && top == 0,
                virtual_display: is_sbms_adapter(&adapter),
            });
        }
        return Ok(displays);
    }

    Err(DisplayError(
        "display topology kept changing while it was read".into(),
    ))
}

fn target_active_size(
    path: &DISPLAYCONFIG_PATH_INFO,
    modes: &[DISPLAYCONFIG_MODE_INFO],
) -> Option<(u32, u32)> {
    let index = unsafe { path.targetInfo.Anonymous.modeInfoIdx } as usize;
    let mode = modes.get(index)?;
    if mode.infoType != DISPLAYCONFIG_MODE_INFO_TYPE_TARGET {
        return None;
    }
    let target = unsafe { mode.Anonymous.targetMode };
    let active = target.targetVideoSignalInfo.activeSize;
    (active.cx > 0 && active.cy > 0).then_some((active.cx, active.cy))
}

fn rotation(value: windows::Win32::Devices::Display::DISPLAYCONFIG_ROTATION) -> Rotation {
    match value {
        DISPLAYCONFIG_ROTATION_ROTATE90 => Rotation::Deg90,
        DISPLAYCONFIG_ROTATION_ROTATE180 => Rotation::Deg180,
        DISPLAYCONFIG_ROTATION_ROTATE270 => Rotation::Deg270,
        DISPLAYCONFIG_ROTATION_IDENTITY => Rotation::Deg0,
        _ => Rotation::Deg0,
    }
}

struct DeviceInfoSet(HDEVINFO);

impl Drop for DeviceInfoSet {
    fn drop(&mut self) {
        let _ = unsafe { SetupDiDestroyDeviceInfoList(self.0) };
    }
}

struct RegistryKey(HKEY);

impl Drop for RegistryKey {
    fn drop(&mut self) {
        let _ = unsafe { RegCloseKey(self.0) };
    }
}

fn physical_dimensions_mm(monitor_device_path: &str) -> Option<(f64, f64)> {
    let device_path: Vec<u16> = monitor_device_path
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    let device_info_set = DeviceInfoSet(
        unsafe {
            SetupDiGetClassDevsW(
                Some(&GUID_DEVINTERFACE_MONITOR),
                PCWSTR::null(),
                None,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE,
            )
        }
        .ok()?,
    );

    let mut interface = SP_DEVICE_INTERFACE_DATA {
        cbSize: size_of::<SP_DEVICE_INTERFACE_DATA>() as u32,
        ..Default::default()
    };
    unsafe {
        SetupDiOpenDeviceInterfaceW(
            device_info_set.0,
            PCWSTR(device_path.as_ptr()),
            0,
            Some(&mut interface),
        )
    }
    .ok()?;

    let mut required_size = 0;
    let _ = unsafe {
        SetupDiGetDeviceInterfaceDetailW(
            device_info_set.0,
            &interface,
            None,
            0,
            Some(&mut required_size),
            None,
        )
    };
    if required_size < size_of::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>() as u32 {
        return None;
    }

    let word_count = (required_size as usize).div_ceil(size_of::<usize>());
    let mut detail_buffer = vec![0usize; word_count];
    let detail = detail_buffer
        .as_mut_ptr()
        .cast::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>();
    unsafe {
        (*detail).cbSize = size_of::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>() as u32;
    }
    let mut device_info = SP_DEVINFO_DATA {
        cbSize: size_of::<SP_DEVINFO_DATA>() as u32,
        ..Default::default()
    };
    unsafe {
        SetupDiGetDeviceInterfaceDetailW(
            device_info_set.0,
            &interface,
            Some(detail),
            required_size,
            None,
            Some(&mut device_info),
        )
    }
    .ok()?;

    let registry_key = RegistryKey(
        unsafe {
            SetupDiOpenDevRegKey(
                device_info_set.0,
                &device_info,
                DICS_FLAG_GLOBAL.0,
                0,
                DIREG_DEV,
                KEY_READ.0,
            )
        }
        .ok()?,
    );
    let mut value_type = REG_VALUE_TYPE::default();
    let mut byte_count = 0;
    if unsafe {
        RegQueryValueExW(
            registry_key.0,
            w!("EDID"),
            None,
            Some(&mut value_type),
            None,
            Some(&mut byte_count),
        )
    } != ERROR_SUCCESS
        || value_type != REG_BINARY
        || byte_count < 128
    {
        return None;
    }

    let mut edid = vec![0u8; byte_count as usize];
    if unsafe {
        RegQueryValueExW(
            registry_key.0,
            w!("EDID"),
            None,
            Some(&mut value_type),
            Some(edid.as_mut_ptr()),
            Some(&mut byte_count),
        )
    } != ERROR_SUCCESS
    {
        return None;
    }
    edid_dimensions_mm(&edid[..byte_count as usize])
}

fn edid_dimensions_mm(edid: &[u8]) -> Option<(f64, f64)> {
    const HEADER: [u8; 8] = [0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00];
    if edid.len() < 128 || edid[..8] != HEADER {
        return None;
    }

    for offset in (54..=108).step_by(18) {
        let descriptor = &edid[offset..offset + 18];
        if descriptor[0] == 0 && descriptor[1] == 0 {
            continue;
        }
        let width = u16::from(descriptor[12]) | (u16::from(descriptor[14] & 0xf0) << 4);
        let height = u16::from(descriptor[13]) | (u16::from(descriptor[14] & 0x0f) << 8);
        if width > 0 && height > 0 {
            return Some((f64::from(width), f64::from(height)));
        }
    }

    let width_cm = edid[21];
    let height_cm = edid[22];
    (width_cm > 0 && height_cm > 0)
        .then_some((f64::from(width_cm) * 10.0, f64::from(height_cm) * 10.0))
}

fn adapter_name(adapter_id: windows::Win32::Foundation::LUID) -> Result<String, DisplayError> {
    let mut packet = DISPLAYCONFIG_ADAPTER_NAME {
        header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
            r#type: DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME,
            size: size_of::<DISPLAYCONFIG_ADAPTER_NAME>() as u32,
            adapterId: adapter_id,
            id: 0,
        },
        ..Default::default()
    };
    let result = unsafe { DisplayConfigGetDeviceInfo(&mut packet.header) };
    if result != 0 {
        return Err(win32_error(
            "DisplayConfigGetDeviceInfo(adapter)",
            result as u32,
        ));
    }
    Ok(wide_string(&packet.adapterDevicePath))
}

fn is_sbms_adapter(adapter: &str) -> bool {
    let adapter = adapter.to_ascii_lowercase();
    adapter.contains(r"swd#sbms#virtualdisplay-01")
        || adapter.contains(r"swd\sbms\virtualdisplay-01")
}

fn device_name(
    info_type: windows::Win32::Devices::Display::DISPLAYCONFIG_DEVICE_INFO_TYPE,
    adapter_id: windows::Win32::Foundation::LUID,
    id: u32,
) -> Result<String, DisplayError> {
    let mut packet = DISPLAYCONFIG_SOURCE_DEVICE_NAME {
        header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
            r#type: info_type,
            size: size_of::<DISPLAYCONFIG_SOURCE_DEVICE_NAME>() as u32,
            adapterId: adapter_id,
            id,
        },
        ..Default::default()
    };
    let result = unsafe { DisplayConfigGetDeviceInfo(&mut packet.header) };
    if result != 0 {
        return Err(win32_error(
            "DisplayConfigGetDeviceInfo(source)",
            result as u32,
        ));
    }
    Ok(wide_string(&packet.viewGdiDeviceName))
}

fn target_name(
    adapter_id: windows::Win32::Foundation::LUID,
    id: u32,
) -> Result<DISPLAYCONFIG_TARGET_DEVICE_NAME, DisplayError> {
    let mut packet = DISPLAYCONFIG_TARGET_DEVICE_NAME {
        header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
            r#type: DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
            size: size_of::<DISPLAYCONFIG_TARGET_DEVICE_NAME>() as u32,
            adapterId: adapter_id,
            id,
        },
        ..Default::default()
    };
    let result = unsafe { DisplayConfigGetDeviceInfo(&mut packet.header) };
    if result != 0 {
        return Err(win32_error(
            "DisplayConfigGetDeviceInfo(target)",
            result as u32,
        ));
    }
    Ok(packet)
}

fn wide_string(value: &[u16]) -> String {
    let end = value
        .iter()
        .position(|character| *character == 0)
        .unwrap_or(value.len());
    String::from_utf16_lossy(&value[..end])
}

fn win32_error(operation: &str, code: u32) -> DisplayError {
    DisplayError(format!("{operation} failed with Win32 error {code}"))
}

#[cfg(test)]
mod tests {
    use super::edid_dimensions_mm;

    const HEADER: [u8; 8] = [0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00];

    fn edid() -> Vec<u8> {
        let mut value = vec![0u8; 128];
        value[..8].copy_from_slice(&HEADER);
        value
    }

    #[test]
    fn edid_prefers_detailed_timing_dimensions() {
        let mut value = edid();
        value[21] = 52;
        value[22] = 29;
        let descriptor = &mut value[54..72];
        descriptor[0] = 1;
        descriptor[12] = (600u16 & 0xff) as u8;
        descriptor[13] = (340u16 & 0xff) as u8;
        descriptor[14] = ((600u16 >> 8) as u8) << 4 | (340u16 >> 8) as u8;

        assert_eq!(edid_dimensions_mm(&value), Some((600.0, 340.0)));
    }

    #[test]
    fn edid_falls_back_to_basic_centimeter_dimensions() {
        let mut value = edid();
        value[21] = 60;
        value[22] = 34;

        assert_eq!(edid_dimensions_mm(&value), Some((600.0, 340.0)));
    }

    #[test]
    fn edid_rejects_missing_or_unidentified_dimensions() {
        assert_eq!(edid_dimensions_mm(&[0; 128]), None);
        assert_eq!(edid_dimensions_mm(&edid()), None);
    }
}
