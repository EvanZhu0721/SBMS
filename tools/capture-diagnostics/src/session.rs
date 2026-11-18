use crate::{
    Result,
    protocol::Mapping,
    topology::{self, Topology},
    write_json,
};
use serde_json::{Value, json};
use std::ffi::c_void;
use std::fs::File;
use std::io::Write;
use std::path::Path;
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};
use windows::Win32::Devices::DeviceAndDriverInstallation::{
    CM_LOCATE_DEVNODE_NORMAL, CM_Locate_DevNodeW, CR_NO_SUCH_DEVNODE, CR_SUCCESS,
};
use windows::Win32::Devices::Enumeration::Pnp::*;
use windows::Win32::System::SystemInformation::GetTickCount64;
use windows::core::{GUID, HRESULT, PCWSTR, PWSTR, w};

pub fn run(config: &Value, directory: &Path) -> Result<()> {
    std::fs::create_dir_all(directory)?;
    let kind = config["kind"].as_str().unwrap_or("diagnostic");
    if !matches!(kind, "baseline" | "diagnostic") {
        return Err("kind must be baseline or diagnostic".into());
    }
    let diagnostic = kind == "diagnostic";
    let width = number(config, "width", 1314)?;
    let height = number(config, "height", 2336)?;
    let numerator = number(config, "refresh_numerator", number(config, "refresh", 200)?)?;
    let denominator = number(config, "refresh_denominator", 1)?;
    if width == 0
        || height == 0
        || width > 16384
        || height > 16384
        || numerator == 0
        || denominator == 0
        || u64::from(numerator) > u64::from(denominator) * 1000
    {
        return Err("invalid requested display mode".into());
    }
    let timeout = Duration::from_secs(
        config["timeout_seconds"]
            .as_u64()
            .unwrap_or(120)
            .clamp(10, 900),
    );
    let original_topology = Topology::query(false)?;
    let before = original_topology.snapshot()?;
    write_json(&directory.join("topology-before.json"), &before)?;
    write_json(&directory.join("session-config.json"), config)?;
    let started = Instant::now();
    let mut log = File::create(directory.join("driverstate.jsonl"))?;
    let mut events = File::create(directory.join("session-events.jsonl"))?;
    let mut last_serial = 0;
    let mut diag = None;
    let mut gate = None;
    let mut device = None;
    let mut creation_without_instance = false;
    let mut identity = None;
    let outcome = (|| -> Result<Value> {
        event(
            &mut events,
            started,
            "session_start",
            json!({"kind":kind,"pid":std::process::id(),"mode":[width,height,numerator,denominator]}),
        )?;
        if diagnostic {
            crate::protocol::require_no_product_session()?;
            diag = Some(Mapping::diagnostic(config)?);
        }
        gate = Some(Mapping::gate(
            diagnostic,
            width,
            height,
            numerator,
            denominator,
        )?);
        event(&mut events, started, "gate_created", Value::Null)?;
        device = Some(Device::create(diagnostic, &mut creation_without_instance)?);
        event(
            &mut events,
            started,
            "software_device_created",
            json!({"instance_id":device.as_ref().unwrap().instance_id}),
        )?;
        let ready_deadline = Instant::now() + Duration::from_secs(20);
        let mut activation_requested = false;
        let (source, driver_state) = loop {
            persist_driver(&mut log, started, diag.as_ref(), &mut last_serial)?;
            if directory.join("stop").exists() {
                return Err("session stopped before source readiness".into());
            }
            if Instant::now() >= ready_deadline {
                return Err(
                    "driver identity / active virtual source was not ready within 20 seconds"
                        .into(),
                );
            }
            if let Some(published) = gate.as_ref().unwrap().identity() {
                identity = Some(published);
                let current = Topology::query(false)?;
                let displays = current.displays()?;
                let source = displays
                    .iter()
                    .find(|display| topology::matches_identity(display, published));
                let expected = before["displays"].as_array().unwrap();
                let layout_preserved = topology::contains_displays(expected, &displays);
                let mode_matches = source.is_some_and(|source| {
                    source_matches_mode(source, [width, height, numerator, denominator])
                });
                if !activation_requested && (!layout_preserved || !mode_matches) {
                    event(
                        &mut events,
                        started,
                        "virtual_source_setup_observed",
                        json!({"identity":published,"original":expected,"current":displays}),
                    )?;
                    if original_topology.activate(
                        expected,
                        published,
                        [width, height, numerator, denominator],
                        directory,
                    )? {
                        activation_requested = true;
                        event(
                            &mut events,
                            started,
                            "virtual_source_extension_requested",
                            json!({"identity":published,"restores_original_layout":!layout_preserved}),
                        )?;
                        // SetDisplayConfig completed; inspect its actual result on
                        // the next iteration, without a fixed settling delay.
                        continue;
                    }
                } else if let Some(source) = source {
                    if !layout_preserved {
                        return Err(
                            "virtual source activation changed an existing display layout or mode"
                                .into(),
                        );
                    }
                    if source["width"] != width || source["height"] != height {
                        return Err(
                            format!("virtual source mode differs from request: {source}").into(),
                        );
                    }
                    let actual_numerator = source["refresh_numerator"].as_u64().unwrap_or(0);
                    let actual_denominator = source["refresh_denominator"].as_u64().unwrap_or(0);
                    if actual_denominator == 0
                        || actual_numerator * u64::from(denominator)
                            != u64::from(numerator) * actual_denominator
                    {
                        return Err(format!("virtual source refresh differs from request {numerator}/{denominator}: {actual_numerator}/{actual_denominator}").into());
                    }
                    let driver_state = if let Some(mapping) = &diag {
                        let state = mapping.observations(&mut last_serial.clone());
                        if state.get("snapshot").is_some() {
                            std::thread::yield_now();
                            continue;
                        }
                        if state["driver_build"] != 0x20261105u32 {
                            return Err(format!(
                                "diagnostic driver build receipt mismatch: {state}"
                            )
                            .into());
                        }
                        state
                    } else {
                        Value::Null
                    };
                    break (source.clone(), driver_state);
                }
            }
            std::thread::sleep(Duration::from_millis(100));
        };
        let originals = before["displays"].as_array().unwrap();
        let target_rect = config
            .get("target_rect")
            .cloned()
            .unwrap_or_else(|| stimulus_rect(originals));
        let has_first_driver_frame = driver_state["monitors"].as_array().is_some_and(|monitors| {
            monitors
                .iter()
                .any(|monitor| monitor["first_frame_count"].as_u64().unwrap_or(0) > 0)
        });
        let ready = json!({"status":"ready","kind":kind,"driver_ready":true,"driver_ready_definition":"published target identity and requested display mode are active","has_first_driver_frame":if diagnostic{json!(has_first_driver_frame)}else{Value::Null},"driver_state":driver_state,"pid":std::process::id(),
            "source_device":source["source_device"],"source_rect":source["rect"],"target_rect":target_rect,
            "rect_format":"left,top,right,bottom","adapter_luid":source["adapter_luid"],"identity":identity,
            "monitor_device_path":source["monitor_device_path"],"source":source,
            "instance_id":device.as_ref().unwrap().instance_id,
            "elapsed_ms":started.elapsed().as_millis(),"timeout_seconds":timeout.as_secs()});
        write_json(&directory.join("ready.json"), &ready)?;
        event(&mut events, started, "source_ready", ready.clone())?;
        let mut previous_topology = json!(Topology::query(false)?.displays()?);
        let mut next_topology_read = Instant::now();
        while started.elapsed() < timeout && !directory.join("stop").exists() {
            persist_driver(&mut log, started, diag.as_ref(), &mut last_serial)?;
            if Instant::now() >= next_topology_read {
                let current = match Topology::query(false).and_then(|now| now.displays()) {
                    Ok(now) => json!(now),
                    Err(error) => json!({"error":error.to_string()}),
                };
                if current != previous_topology {
                    event(
                        &mut events,
                        started,
                        "topology_changed",
                        json!({"before":previous_topology,"after":current}),
                    )?;
                    previous_topology = current;
                }
                next_topology_read = Instant::now() + Duration::from_millis(500);
            }
            std::thread::sleep(Duration::from_millis(100));
        }
        Ok(
            json!({"ready":ready,"stop_reason":if directory.join("stop").exists(){"stop_file"}else{"maximum_duration"}}),
        )
    })();
    if let Err(error) = &outcome {
        // Keep the failed active state before closing the software device. The
        // cleanup snapshot otherwise loses the very layout/mode that failed.
        let failed_topology = match Topology::query(false) {
            Ok(now) => {
                let mut snapshot = now.raw_snapshot();
                match now.displays() {
                    Ok(displays) => snapshot["displays"] = json!(displays),
                    Err(error) => snapshot["displays_error"] = json!(error.to_string()),
                }
                snapshot
            }
            Err(error) => json!({"error":error.to_string()}),
        };
        let _ = write_json(&directory.join("topology-failure.json"), &failed_topology);
        let failed = json!({"status":"error","kind":kind,"error":error.to_string(),"elapsed_ms":started.elapsed().as_millis()});
        if !directory.join("ready.json").exists() {
            let _ = write_json(&directory.join("ready.json"), &failed);
        }
        let _ = event(&mut events, started, "session_error", failed);
    }

    // Keep the protocol mappings alive until the device and drain workers detach.
    let instance_id = device.as_ref().map(|device| device.instance_id.clone());
    drop(device.take());
    let _ = event(&mut events, started, "software_device_closed", Value::Null);
    let cleanup_deadline = Instant::now() + Duration::from_secs(15);
    let mut device_removed = false;
    let mut driver_detached = diag.is_none();
    while Instant::now() < cleanup_deadline {
        let _ = persist_driver(&mut log, started, diag.as_ref(), &mut last_serial);
        let source_removed = match (identity, Topology::query(false)) {
            (Some(id), Ok(now)) => matches!(now.find(id), Ok(None)),
            (None, Ok(now)) => now.displays().is_ok_and(|displays| {
                topology::same_displays(before["displays"].as_array().unwrap(), &displays)
            }),
            _ => false,
        };
        let pnp_removed = !creation_without_instance
            && instance_id
                .as_deref()
                .is_none_or(|id| matches!(device_present(id), Ok(false)));
        device_removed = source_removed && pnp_removed;
        if let Some(mapping) = &diag {
            let state = mapping.observations(&mut last_serial.clone());
            driver_detached = state["driver_pid"] == 0 && state["device_attached"] == 0;
        }
        if device_removed && driver_detached {
            break;
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    drop(gate.take());
    let restore = match topology::restore(&before) {
        Ok(value) => value,
        Err(error) => json!({"verified":false,"error":error.to_string()}),
    };
    write_json(&directory.join("topology-restore.json"), &restore)?;
    let restored = restore["verified"] == true;
    let cleanup_ok = device_removed && driver_detached && restored;
    let status = if outcome.is_ok() && cleanup_ok {
        "complete"
    } else {
        "error"
    };
    let summary = json!({"status":status,"kind":kind,"config":config,"elapsed_ms":started.elapsed().as_millis(),
        "cleanup_ok":cleanup_ok,"device_removed":device_removed,"creation_without_instance":creation_without_instance,"driver_detached":if diagnostic{json!(driver_detached)}else{Value::Null},
        "topology_restored":restored,"restore":restore,"outcome":match &outcome{Ok(value)=>value.clone(),Err(error)=>json!({"error":error.to_string()})}});
    write_json(&directory.join("session.json"), &summary)?;
    event(&mut events, started, "session_complete", summary)?;
    if !cleanup_ok {
        return Err("session cleanup could not verify device removal, driver detach and original topology; see session.json".into());
    }
    outcome.map(|_| ())
}

fn persist_driver(
    log: &mut File,
    started: Instant,
    diag: Option<&Mapping>,
    serial: &mut u64,
) -> Result<()> {
    if let Some(mapping) = diag {
        let state = mapping.observations(serial);
        serde_json::to_writer(
            &mut *log,
            &json!({"elapsed_ms":started.elapsed().as_millis(),"tick":unsafe{GetTickCount64()},"utc_unix_ms":std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH)?.as_millis(),"state":state}),
        )?;
        log.write_all(b"\n")?;
        log.flush()?;
    }
    Ok(())
}
fn event(log: &mut File, started: Instant, name: &str, data: Value) -> Result<()> {
    serde_json::to_writer(
        &mut *log,
        &json!({"elapsed_ms":started.elapsed().as_millis(),"tick":unsafe{GetTickCount64()},"utc_unix_ms":std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH)?.as_millis(),"event":name,"data":data}),
    )?;
    log.write_all(b"\n")?;
    log.flush()?;
    Ok(())
}
fn number(config: &Value, name: &str, default: u32) -> Result<u32> {
    match config.get(name) {
        None => Ok(default),
        Some(value) => Ok(value
            .as_u64()
            .ok_or_else(|| format!("{name} must be an unsigned integer"))?
            .try_into()?),
    }
}
fn source_matches_mode(source: &Value, [width, height, numerator, denominator]: [u32; 4]) -> bool {
    let actual_numerator = source["refresh_numerator"].as_u64().unwrap_or(0);
    let actual_denominator = source["refresh_denominator"].as_u64().unwrap_or(0);
    source["width"] == width
        && source["height"] == height
        && actual_denominator != 0
        && actual_numerator * u64::from(denominator) == u64::from(numerator) * actual_denominator
}
fn stimulus_rect(displays: &[Value]) -> Value {
    if let Some(secondary) = displays
        .iter()
        .find(|display| display["rect"][0] != 0 || display["rect"][1] != 0)
    {
        return secondary["rect"].clone();
    }
    let rect = displays.first().map(|d| &d["rect"]);
    let right = rect.and_then(|r| r[2].as_i64()).unwrap_or(640);
    let bottom = rect.and_then(|r| r[3].as_i64()).unwrap_or(480);
    json!([right - 340, bottom - 280, right - 20, bottom - 40])
}

struct Creation {
    result: Mutex<Option<std::result::Result<String, HRESULT>>>,
    changed: Condvar,
}
struct Device {
    handle: HSWDEVICE,
    instance_id: String,
}
impl Device {
    fn create(diagnostic: bool, creation_without_instance: &mut bool) -> Result<Self> {
        let hardware = if diagnostic {
            "SBMS\\CaptureDiagnostics"
        } else {
            "SBMS\\IndirectDisplay"
        };
        let ids: Vec<_> = hardware.encode_utf16().chain([0, 0]).collect();
        let instance: Vec<_> = format!("CaptureDiagnostics-{}", std::process::id())
            .encode_utf16()
            .chain(Some(0))
            .collect();
        let container = GUID::from_u128(0xd68a6e74_9232_4a02_b274_6370e027e4c2);
        let info = SW_DEVICE_CREATE_INFO {
            cbSize: size_of::<SW_DEVICE_CREATE_INFO>() as u32,
            pszInstanceId: PCWSTR(instance.as_ptr()),
            pszzHardwareIds: PCWSTR(ids.as_ptr()),
            pContainerId: &container,
            CapabilityFlags: (SWDeviceCapabilitiesRemovable.0
                | SWDeviceCapabilitiesSilentInstall.0
                | SWDeviceCapabilitiesDriverRequired.0) as u32,
            pszDeviceDescription: w!("SBMS isolated capture diagnostics"),
            pszDeviceLocation: w!("Temporary diagnostic software device"),
            ..Default::default()
        };
        let state = Arc::new(Creation {
            result: Mutex::new(None),
            changed: Condvar::new(),
        });
        let callback_state = Arc::into_raw(Arc::clone(&state));
        let created = unsafe {
            SwDeviceCreate(
                w!("SBMSCaptureDiagnostics"),
                w!("HTREE\\ROOT\\0"),
                &info,
                None,
                Some(device_created),
                Some(callback_state.cast::<c_void>()),
            )
        };
        let handle = match created {
            Ok(handle) => handle,
            Err(error) => {
                unsafe {
                    drop(Arc::from_raw(callback_state));
                }
                return Err(error.into());
            }
        };
        // The asynchronous device exists, but no usable PnP instance identity
        // is known until its successful callback. Closing it alone is not proof
        // of cleanup when that callback times out or reports an error.
        *creation_without_instance = true;
        let result = state.result.lock().unwrap();
        let (mut result, _) = state
            .changed
            .wait_timeout_while(result, Duration::from_secs(10), |result| result.is_none())
            .unwrap();
        match result.take() {
            Some(Ok(instance_id)) => {
                *creation_without_instance = false;
                Ok(Self {
                    handle,
                    instance_id,
                })
            }
            other => {
                unsafe {
                    SwDeviceClose(handle);
                }
                Err(match other {
                    Some(Err(hr)) => windows::core::Error::from_hresult(hr).into(),
                    _ => "SwDeviceCreate callback timed out after 10 seconds".into(),
                })
            }
        }
    }
}
impl Drop for Device {
    fn drop(&mut self) {
        unsafe {
            SwDeviceClose(self.handle);
        }
    }
}
unsafe extern "system" fn device_created(
    _device: HSWDEVICE,
    result: HRESULT,
    context: *const c_void,
    instance: PCWSTR,
) {
    let state = unsafe { Arc::from_raw(context.cast::<Creation>()) };
    let value = if result.is_ok() {
        unsafe { instance.to_string() }.map_err(|_| HRESULT(0x80070057u32 as i32))
    } else {
        Err(result)
    };
    *state.result.lock().unwrap() = Some(value);
    state.changed.notify_one();
}

fn device_present(instance: &str) -> Result<bool> {
    let mut instance: Vec<_> = instance.encode_utf16().chain(Some(0)).collect();
    let mut node = 0;
    let status = unsafe {
        CM_Locate_DevNodeW(
            &mut node,
            PWSTR(instance.as_mut_ptr()),
            CM_LOCATE_DEVNODE_NORMAL,
        )
    };
    if status == CR_SUCCESS {
        Ok(true)
    } else if status == CR_NO_SUCH_DEVNODE {
        Ok(false)
    } else {
        Err(format!("CM_Locate_DevNodeW: {}", status.0).into())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn readiness_requires_requested_size_and_rational_refresh() {
        let mut source = json!({"width":1314,"height":2336,"refresh_numerator":200000,"refresh_denominator":1000});
        let requested = [1314, 2336, 200, 1];
        assert!(source_matches_mode(&source, requested));
        source["refresh_numerator"] = json!(60000);
        assert!(!source_matches_mode(&source, requested));
        source["refresh_numerator"] = json!(200000);
        source["width"] = json!(2336);
        assert!(!source_matches_mode(&source, requested));
        source["width"] = json!(1314);
        source["refresh_denominator"] = json!(0);
        assert!(!source_matches_mode(&source, requested));
    }
}
