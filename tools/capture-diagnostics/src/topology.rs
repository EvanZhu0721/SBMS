use crate::Result;
use serde_json::{Value, json};
use std::path::Path;
use windows::Win32::Devices::Display::*;
use windows::Win32::Foundation::{ERROR_INSUFFICIENT_BUFFER, LUID, POINTL};
use windows::Win32::Graphics::Gdi::{
    DEVMODEW, ENUM_CURRENT_SETTINGS, ENUM_DISPLAY_SETTINGS_FLAGS, EnumDisplaySettingsExW,
};
use windows::core::PCWSTR;

pub struct Topology {
    paths: Vec<DISPLAYCONFIG_PATH_INFO>,
    modes: Vec<DISPLAYCONFIG_MODE_INFO>,
}

impl Topology {
    pub fn query(all: bool) -> Result<Self> {
        let flags = if all {
            QDC_ALL_PATHS
        } else {
            QDC_ONLY_ACTIVE_PATHS
        };
        // A topology change between the size and data calls invalidates the buffer.
        for _ in 0..3 {
            let (mut np, mut nm) = (0, 0);
            unsafe { GetDisplayConfigBufferSizes(flags, &mut np, &mut nm) }.ok()?;
            let mut paths = vec![DISPLAYCONFIG_PATH_INFO::default(); np as usize];
            let mut modes = vec![DISPLAYCONFIG_MODE_INFO::default(); nm as usize];
            let status = unsafe {
                QueryDisplayConfig(
                    flags,
                    &mut np,
                    paths.as_mut_ptr(),
                    &mut nm,
                    modes.as_mut_ptr(),
                    None,
                )
            };
            if status == ERROR_INSUFFICIENT_BUFFER {
                continue;
            }
            status.ok()?;
            paths.truncate(np as usize);
            modes.truncate(nm as usize);
            return Ok(Self { paths, modes });
        }
        Err("display topology kept changing during enumeration".into())
    }

    pub fn snapshot(&self) -> Result<Value> {
        let mut snapshot = self.raw_snapshot();
        snapshot["displays"] = json!(self.displays()?);
        Ok(snapshot)
    }

    pub fn raw_snapshot(&self) -> Value {
        json!({"format":"sbms-displayconfig-v1","pointer_width":usize::BITS,
            "path_size":size_of::<DISPLAYCONFIG_PATH_INFO>(),"mode_size":size_of::<DISPLAYCONFIG_MODE_INFO>(),
            "query_virtual_mode_aware":false,
            "path_bytes":as_bytes(&self.paths),"mode_bytes":as_bytes(&self.modes)})
    }

    pub fn displays(&self) -> Result<Vec<Value>> {
        if self.paths.is_empty() {
            return Err("active display topology is empty".into());
        }
        self.paths.iter().map(|path| -> Result<Value> {
            let index = unsafe {path.sourceInfo.Anonymous.modeInfoIdx} as usize;
            let mode = self.modes.get(index).ok_or_else(||format!("active source {} has invalid mode index {index}",path.sourceInfo.id))?;
            if mode.infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE || mode.id != path.sourceInfo.id || mode.adapterId != path.sourceInfo.adapterId {return Err(format!("active source {} references a mismatched source mode",path.sourceInfo.id).into());}
            let ccd = unsafe {mode.Anonymous.sourceMode};
            let target = target_name(path.targetInfo.adapterId,path.targetInfo.id)?;
            let name = source_name(path.sourceInfo.adapterId,path.sourceInfo.id)?;
            if name.is_empty() || wide_string(&target.monitorDevicePath).is_empty() {return Err("active display has an empty source name or monitor identity".into());}
            // GDI gives actual desktop bounds even when CCD source dimensions are
            // unrotated. Stimulus windows must use physical desktop coordinates.
            let mut gdi = DEVMODEW {dmSize:size_of::<DEVMODEW>() as u16,..Default::default()};
            let wide_name:Vec<_> = name.encode_utf16().chain(Some(0)).collect();
            let gdi_ok = unsafe {EnumDisplaySettingsExW(PCWSTR(wide_name.as_ptr()),ENUM_CURRENT_SETTINGS,&mut gdi,ENUM_DISPLAY_SETTINGS_FLAGS(0))}.as_bool();
            if !gdi_ok {return Err(format!("EnumDisplaySettingsExW({name}) failed while reading active desktop geometry").into());}
            let position = unsafe {gdi.Anonymous1.Anonymous2.dmPosition};
            let (x,y,width,height) = (position.x,position.y,gdi.dmPelsWidth,gdi.dmPelsHeight);
            Ok(json!({"source_device":name,"adapter_luid":{"low":path.targetInfo.adapterId.LowPart,"high":path.targetInfo.adapterId.HighPart},
                "source_id":path.sourceInfo.id,"target_id":path.targetInfo.id,"monitor_device_path":wide_string(&target.monitorDevicePath),
                "friendly_name":wide_string(&target.monitorFriendlyDeviceName),"rect":[x,y,i64::from(x)+i64::from(width),i64::from(y)+i64::from(height)],
                "ccd_source":{"position":[ccd.position.x,ccd.position.y],"width":ccd.width,"height":ccd.height,"mode_index":index},
                "gdi_orientation":unsafe {gdi.Anonymous1.Anonymous2.dmDisplayOrientation}.0,
                "width":width,"height":height,"rotation":path.targetInfo.rotation.0,
                "refresh_numerator":path.targetInfo.refreshRate.Numerator,"refresh_denominator":path.targetInfo.refreshRate.Denominator,
                "output_technology":path.targetInfo.outputTechnology.0}))
        }).collect()
    }

    pub fn find(&self, identity: (u32, i32, u32)) -> Result<Option<Value>> {
        Ok(self
            .displays()?
            .into_iter()
            .find(|display| matches_identity(display, identity)))
    }

    pub fn activate(
        &self,
        original_displays: &[Value],
        identity: (u32, i32, u32),
        requested_mode: [u32; 4],
        directory: &Path,
    ) -> Result<bool> {
        let all = Self::query(true)?;
        crate::write_json(
            &directory.join("topology-activation-candidates.json"),
            &all.raw_snapshot(),
        )?;
        let Some(extension) = self.extension(&all, original_displays, identity, requested_mode)?
        else {
            return Ok(false);
        };
        // Preserve the actual candidate and exact API inputs before attempting any
        // change: cleanup alone cannot explain a rejected setup configuration.
        let mut request = extension.raw_snapshot();
        request["original_displays"] = json!(original_displays);
        request["requested_mode"] = json!(requested_mode);
        request["identity"] = json!(identity);
        request["validate_flags"] = json!((SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG).0);
        request["apply_flags"] = json!((SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG).0);
        crate::write_json(
            &directory.join("topology-activation-request.json"),
            &request,
        )?;
        let validation = unsafe {
            SetDisplayConfig(
                Some(&extension.paths),
                Some(&extension.modes),
                SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG,
            )
        };
        let mut result = json!({"validation_status":validation,"apply_status":null});
        crate::write_json(&directory.join("topology-activation-result.json"), &result)?;
        if validation != 0 {
            return Err(format!("SetDisplayConfig(validate activation): {validation}").into());
        }
        let status = unsafe {
            SetDisplayConfig(
                Some(&extension.paths),
                Some(&extension.modes),
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG,
            )
        };
        result["apply_status"] = json!(status);
        crate::write_json(&directory.join("topology-activation-result.json"), &result)?;
        if status != 0 {
            return Err(format!("SetDisplayConfig(activate): {status}").into());
        }
        Ok(true)
    }

    fn extension(
        &self,
        all: &Self,
        original_displays: &[Value],
        identity: (u32, i32, u32),
        [width, height, numerator, denominator]: [u32; 4],
    ) -> Result<Option<Self>> {
        // Use the saved pre-device paths and modes. Windows may auto-enable a
        // newly arrived monitor using a cached layout that moves existing screens.
        // Only proceed while every saved source/target connection still exists.
        for saved in &self.paths {
            if !all.paths.iter().any(|now| {
                now.sourceInfo.adapterId == saved.sourceInfo.adapterId
                    && now.sourceInfo.id == saved.sourceInfo.id
                    && now.targetInfo.adapterId == saved.targetInfo.adapterId
                    && now.targetInfo.id == saved.targetInfo.id
                    && now.targetInfo.targetAvailable.as_bool()
            }) {
                return Err("an original display connection disappeared during setup".into());
            }
        }
        if all.paths.iter().any(|path| {
            path.flags & 1 != 0
                && (
                    path.targetInfo.adapterId.LowPart,
                    path.targetInfo.adapterId.HighPart,
                    path.targetInfo.id,
                ) != identity
                && !self.paths.iter().any(|saved| {
                    saved.targetInfo.adapterId == path.targetInfo.adapterId
                        && saved.targetInfo.id == path.targetInfo.id
                })
        }) {
            return Err("an unrelated display became active during setup".into());
        }
        let candidate = all.paths.iter().find(|path| {
            path.targetInfo.adapterId.LowPart == identity.0
                && path.targetInfo.adapterId.HighPart == identity.1
                && path.targetInfo.id == identity.2
                && path.targetInfo.targetAvailable.as_bool()
                && !self.paths.iter().any(|active| {
                    active.sourceInfo.adapterId.LowPart == path.sourceInfo.adapterId.LowPart
                        && active.sourceInfo.adapterId.HighPart
                            == path.sourceInfo.adapterId.HighPart
                        && active.sourceInfo.id == path.sourceInfo.id
                })
        });
        let Some(candidate) = candidate else {
            return Ok(None);
        };
        let mut path = *candidate;
        // Attach to the rightmost display at its top edge, including layouts
        // whose rightmost display does not intersect y=0.
        let adjacent = original_displays
            .iter()
            .max_by_key(|d| d["rect"][2].as_i64())
            .ok_or("cannot extend an empty original desktop")?;
        let x = adjacent["rect"][2]
            .as_i64()
            .ok_or("invalid original display right edge")?
            .try_into()?;
        let y = adjacent["rect"][1]
            .as_i64()
            .ok_or("invalid original display top edge")?
            .try_into()?;
        let mut paths = self.paths.clone();
        let mut modes = self.modes.clone();
        let source = DISPLAYCONFIG_SOURCE_MODE {
            width,
            height,
            pixelFormat: DISPLAYCONFIG_PIXELFORMAT_32BPP,
            position: POINTL { x, y },
        };
        let mut mode = DISPLAYCONFIG_MODE_INFO {
            infoType: DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
            id: path.sourceInfo.id,
            adapterId: path.sourceInfo.adapterId,
            ..Default::default()
        };
        mode.Anonymous.sourceMode = source;
        path.sourceInfo.Anonymous.modeInfoIdx = modes.len() as u32;
        path.targetInfo.Anonymous.modeInfoIdx = u32::MAX;
        path.targetInfo.refreshRate.Numerator = numerator;
        path.targetInfo.refreshRate.Denominator = denominator;
        // Inactive QDC paths only supply default target details. The temporary
        // virtual monitor has an unrotated, progressive mode, with no scaling.
        path.targetInfo.rotation = DISPLAYCONFIG_ROTATION_IDENTITY;
        path.targetInfo.scaling = DISPLAYCONFIG_SCALING_IDENTITY;
        path.targetInfo.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
        path.flags |= 1; // DISPLAYCONFIG_PATH_ACTIVE (wingdi.h).
        modes.push(mode);
        paths.push(path);
        Ok(Some(Self { paths, modes }))
    }
}

pub fn restore(snapshot: &Value) -> Result<Value> {
    if snapshot["format"] != "sbms-displayconfig-v1"
        || snapshot["pointer_width"] != usize::BITS
        || snapshot["path_size"] != size_of::<DISPLAYCONFIG_PATH_INFO>()
        || snapshot["mode_size"] != size_of::<DISPLAYCONFIG_MODE_INFO>()
    {
        return Err("unsupported topology snapshot format or architecture".into());
    }
    let paths = from_bytes::<DISPLAYCONFIG_PATH_INFO>(&serde_json::from_value::<Vec<u8>>(
        snapshot["path_bytes"].clone(),
    )?)?;
    let modes = from_bytes::<DISPLAYCONFIG_MODE_INFO>(&serde_json::from_value::<Vec<u8>>(
        snapshot["mode_bytes"].clone(),
    )?)?;
    if paths.is_empty() {
        return Err("refusing to restore an empty display topology".into());
    }
    let before_read = Topology::query(false).and_then(|topology| topology.displays());
    let expected = snapshot["displays"]
        .as_array()
        .ok_or("snapshot lacks display descriptions")?;
    // A broken current topology must not prevent applying the valid saved one.
    // Read errors only disable the no-op optimization and remain in the report.
    let unchanged = before_read
        .as_ref()
        .is_ok_and(|before| same_displays(expected, before));
    let (before, before_error) = match before_read {
        Ok(value) => (json!(value), Value::Null),
        Err(error) => (Value::Null, json!(error.to_string())),
    };
    let status = if unchanged {
        0
    } else {
        unsafe {
            SetDisplayConfig(
                Some(&paths),
                Some(&modes),
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG,
            )
        }
    };
    let after_read = Topology::query(false).and_then(|topology| topology.displays());
    let verified = status == 0
        && after_read
            .as_ref()
            .is_ok_and(|after| same_displays(expected, after));
    let (after, after_error) = match after_read {
        Ok(value) => (json!(value), Value::Null),
        Err(error) => (Value::Null, json!(error.to_string())),
    };
    Ok(
        json!({"verified":verified,"apply_status":status,"already_restored":unchanged,"before":before,"before_error":before_error,"after":after,"after_error":after_error,"expected":expected}),
    )
}

pub fn same_displays(expected: &[Value], actual: &[Value]) -> bool {
    expected.len() == actual.len() && contains_displays(expected, actual)
}

pub fn contains_displays(expected: &[Value], actual: &[Value]) -> bool {
    expected.iter().all(|saved| {
        actual.iter().any(|now| {
            saved["monitor_device_path"] == now["monitor_device_path"]
                && saved["adapter_luid"] == now["adapter_luid"]
                && saved["target_id"] == now["target_id"]
                && saved["rect"] == now["rect"]
                && saved["rotation"] == now["rotation"]
                && saved["refresh_numerator"] == now["refresh_numerator"]
                && saved["refresh_denominator"] == now["refresh_denominator"]
        })
    })
}

pub fn matches_identity(display: &Value, identity: (u32, i32, u32)) -> bool {
    display["adapter_luid"]["low"] == identity.0
        && display["adapter_luid"]["high"] == identity.1
        && display["target_id"] == identity.2
}

fn source_name(adapter: LUID, id: u32) -> Result<String> {
    let mut packet = DISPLAYCONFIG_SOURCE_DEVICE_NAME {
        header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
            r#type: DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
            size: size_of::<DISPLAYCONFIG_SOURCE_DEVICE_NAME>() as u32,
            adapterId: adapter,
            id,
        },
        ..Default::default()
    };
    let status = unsafe { DisplayConfigGetDeviceInfo(&mut packet.header) };
    if status != 0 {
        return Err(format!("DisplayConfigGetDeviceInfo(source): {status}").into());
    }
    Ok(wide_string(&packet.viewGdiDeviceName))
}

fn target_name(adapter: LUID, id: u32) -> Result<DISPLAYCONFIG_TARGET_DEVICE_NAME> {
    let mut packet = DISPLAYCONFIG_TARGET_DEVICE_NAME {
        header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
            r#type: DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
            size: size_of::<DISPLAYCONFIG_TARGET_DEVICE_NAME>() as u32,
            adapterId: adapter,
            id,
        },
        ..Default::default()
    };
    let status = unsafe { DisplayConfigGetDeviceInfo(&mut packet.header) };
    if status != 0 {
        return Err(format!("DisplayConfigGetDeviceInfo(target): {status}").into());
    }
    Ok(packet)
}

fn wide_string(value: &[u16]) -> String {
    String::from_utf16_lossy(&value[..value.iter().position(|c| *c == 0).unwrap_or(value.len())])
}
fn as_bytes<T>(values: &[T]) -> Vec<u8> {
    unsafe {
        std::slice::from_raw_parts(values.as_ptr().cast::<u8>(), std::mem::size_of_val(values))
    }
    .to_vec()
}
fn from_bytes<T: Default + Copy>(bytes: &[u8]) -> Result<Vec<T>> {
    if bytes.is_empty() || !bytes.len().is_multiple_of(size_of::<T>()) || bytes.len() > 1_000_000 {
        return Err("invalid topology buffer length".into());
    }
    let mut values = vec![T::default(); bytes.len() / size_of::<T>()];
    unsafe {
        std::ptr::copy_nonoverlapping(
            bytes.as_ptr(),
            values.as_mut_ptr().cast::<u8>(),
            bytes.len(),
        );
    }
    Ok(values)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn candidate() -> DISPLAYCONFIG_PATH_INFO {
        let mut path = DISPLAYCONFIG_PATH_INFO::default();
        path.sourceInfo.adapterId.LowPart = 900;
        path.targetInfo.adapterId.LowPart = 900;
        path.targetInfo.id = 256;
        path.targetInfo.targetAvailable = true.into();
        path.sourceInfo.Anonymous.modeInfoIdx = u32::MAX;
        path.targetInfo.Anonymous.modeInfoIdx = u32::MAX;
        path
    }

    fn assert_extension(original: &Topology, displays: &[Value]) {
        let mut all = Topology {
            paths: original.paths.clone(),
            modes: Vec::new(),
        };
        all.paths.push(candidate());
        let extension = original
            .extension(&all, displays, (900, 0, 256), [1314, 2336, 200, 1])
            .unwrap()
            .unwrap();
        // Original modes and indices must survive unchanged, including rotated
        // sources, fractional refresh rates and paths that share a source.
        assert_eq!(
            as_bytes(&extension.paths[..original.paths.len()]),
            as_bytes(&original.paths)
        );
        assert_eq!(
            as_bytes(&extension.modes[..original.modes.len()]),
            as_bytes(&original.modes)
        );
        let path = extension.paths.last().unwrap();
        assert_eq!(
            unsafe { path.sourceInfo.Anonymous.modeInfoIdx },
            original.modes.len() as u32
        );
        assert_eq!(unsafe { path.targetInfo.Anonymous.modeInfoIdx }, u32::MAX);
        let mode = extension.modes.last().unwrap();
        assert_eq!(mode.adapterId, path.sourceInfo.adapterId);
        assert_eq!(mode.id, path.sourceInfo.id);
        let source = unsafe { mode.Anonymous.sourceMode };
        let adjacent = displays
            .iter()
            .max_by_key(|d| d["rect"][2].as_i64())
            .unwrap();
        assert_eq!(
            i64::from(source.position.x),
            adjacent["rect"][2].as_i64().unwrap()
        );
        assert_eq!(
            i64::from(source.position.y),
            adjacent["rect"][1].as_i64().unwrap()
        );
        assert_eq!((source.width, source.height), (1314, 2336));
        assert_eq!(path.targetInfo.rotation, DISPLAYCONFIG_ROTATION_IDENTITY);
        assert_eq!(path.targetInfo.scaling, DISPLAYCONFIG_SCALING_IDENTITY);
        assert_eq!(
            path.targetInfo.scanLineOrdering,
            DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE
        );
        assert_eq!(
            (
                path.targetInfo.refreshRate.Numerator,
                path.targetInfo.refreshRate.Denominator
            ),
            (200, 1)
        );
        // A cached active path must be reconfigured, not accepted as ready just
        // because it already exists. Its stale mode-table indices are discarded.
        let cached = all.paths.last_mut().unwrap();
        cached.flags = 1;
        cached.sourceInfo.Anonymous.modeInfoIdx = 123;
        cached.targetInfo.Anonymous.modeInfoIdx = 456;
        cached.targetInfo.rotation = DISPLAYCONFIG_ROTATION_ROTATE90;
        let repaired = original
            .extension(&all, displays, (900, 0, 256), [1314, 2336, 200, 1])
            .unwrap()
            .unwrap();
        assert_eq!(as_bytes(&extension.paths), as_bytes(&repaired.paths));
        // Never disable an unrelated newly active display to make setup pass.
        let mut unrelated = candidate();
        unrelated.targetInfo.id += 1;
        unrelated.flags = 1;
        all.paths.push(unrelated);
        assert!(
            original
                .extension(&all, displays, (900, 0, 256), [1314, 2336, 200, 1])
                .is_err()
        );
        all.paths.pop();
        all.paths[0].targetInfo.targetAvailable = false.into();
        assert!(
            original
                .extension(&all, displays, (900, 0, 256), [1314, 2336, 200, 1])
                .is_err()
        );
    }

    #[test]
    fn extension_preserves_saved_modes_and_repairs_only_its_virtual_path() {
        let mut path = candidate();
        path.sourceInfo.adapterId.LowPart = 42;
        path.targetInfo.adapterId.LowPart = 42;
        path.sourceInfo.Anonymous.modeInfoIdx = 0;
        path.flags = 1;
        let mut mode = DISPLAYCONFIG_MODE_INFO {
            infoType: DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
            adapterId: path.sourceInfo.adapterId,
            ..Default::default()
        };
        // #50's CCD surface remains 1920x1080 while the GDI desktop is portrait.
        mode.Anonymous.sourceMode = DISPLAYCONFIG_SOURCE_MODE {
            width: 1920,
            height: 1080,
            position: POINTL { x: 2560, y: -312 },
            pixelFormat: DISPLAYCONFIG_PIXELFORMAT_32BPP,
        };
        path.targetInfo.rotation = DISPLAYCONFIG_ROTATION_ROTATE270;
        let original = Topology {
            paths: vec![path],
            modes: vec![mode],
        };
        assert_extension(&original, &[json!({"rect":[2560,-312,3640,1608]})]);
    }

    #[test]
    #[ignore = "offline returned-report replay; set SBMS_TOPOLOGY_FIXTURES to snapshot paths"]
    fn replay_returned_topology_snapshots() {
        let files = std::env::var_os("SBMS_TOPOLOGY_FIXTURES").expect("snapshot paths required");
        for file in std::env::split_paths(&files) {
            let text = std::fs::read_to_string(&file).unwrap();
            let snapshot: Value =
                serde_json::from_str(text.trim_start_matches('\u{feff}')).unwrap();
            let original = Topology {
                paths: from_bytes(
                    &serde_json::from_value::<Vec<u8>>(snapshot["path_bytes"].clone()).unwrap(),
                )
                .unwrap(),
                modes: from_bytes(
                    &serde_json::from_value::<Vec<u8>>(snapshot["mode_bytes"].clone()).unwrap(),
                )
                .unwrap(),
            };
            assert_extension(&original, snapshot["displays"].as_array().unwrap());
            println!(
                "replayed {}: {} paths / {} modes",
                file.display(),
                original.paths.len(),
                original.modes.len()
            );
        }
    }

    #[test]
    fn restoration_requires_identity_geometry_rotation_and_refresh() {
        let original = json!({"monitor_device_path":"physical-A","adapter_luid":{"low":42,"high":0},"target_id":3,"rect":[0,0,1920,1080],"rotation":1,"refresh_numerator":60,"refresh_denominator":1});
        let expected = vec![original.clone()];
        assert!(same_displays(&expected, &expected));
        for (field, value) in [
            ("monitor_device_path", json!("physical-B")),
            ("rect", json!([0, 0, 1080, 1920])),
            ("rotation", json!(2)),
            ("refresh_numerator", json!(120)),
        ] {
            let mut changed = original.clone();
            changed[field] = value;
            assert!(!same_displays(&expected, &[changed]), "{field}");
        }
        let mut virtual_display = original;
        virtual_display["monitor_device_path"] = json!("virtual");
        assert!(!same_displays(
            &expected,
            &[expected[0].clone(), virtual_display.clone()]
        ));
        assert!(contains_displays(
            &expected,
            &[expected[0].clone(), virtual_display]
        ));
    }
}
