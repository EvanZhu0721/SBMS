use std::error::Error;
use std::fmt::{Display as FmtDisplay, Formatter};
use std::mem::size_of;
use std::thread;
use std::time::Duration;

use windows::Win32::Devices::Display::{
    DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
    DISPLAYCONFIG_DEVICE_INFO_HEADER, DISPLAYCONFIG_MODE_INFO, DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
    DISPLAYCONFIG_PATH_INFO, DISPLAYCONFIG_SOURCE_DEVICE_NAME, DISPLAYCONFIG_TARGET_DEVICE_NAME,
    DisplayConfigGetDeviceInfo, GetDisplayConfigBufferSizes, QDC_ONLY_ACTIVE_PATHS,
    QueryDisplayConfig,
};
use windows::Win32::Foundation::{ERROR_INSUFFICIENT_BUFFER, ERROR_SUCCESS, RECT};

#[derive(Clone, Debug)]
pub struct Display {
    pub id: String,
    pub name: String,
    pub device_name: String,
    pub rect: RECT,
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
            let name = wide_string(&target.monitorFriendlyDeviceName);
            displays.push(Display {
                id: wide_string(&target.monitorDevicePath),
                name: name.clone(),
                device_name: source,
                rect: RECT {
                    left,
                    top,
                    right: left + mode.width as i32,
                    bottom: top + mode.height as i32,
                },
                primary: left == 0 && top == 0,
                virtual_display: name == "SBMS Display",
            });
        }
        return Ok(displays);
    }

    Err(DisplayError(
        "display topology kept changing while it was read".into(),
    ))
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
