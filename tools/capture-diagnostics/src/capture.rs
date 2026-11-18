//! Metadata-only capture diagnostics. No pixel readback or presentation.
use std::cell::Cell;
use std::io::Write;
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
    mpsc,
};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use serde_json::{Value, json};
use windows::Win32::Foundation::{HMODULE, HWND, LPARAM, LRESULT, LUID, RECT, WPARAM};
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE_UNKNOWN, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_11_1,
};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC, D3D11CreateDevice,
    ID3D11Device, ID3D11Texture2D,
};
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, DXGI_ERROR_ACCESS_LOST, DXGI_ERROR_NOT_FOUND, DXGI_ERROR_WAIT_TIMEOUT,
    DXGI_GPU_PREFERENCE, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, DXGI_GPU_PREFERENCE_MINIMUM_POWER,
    DXGI_OUTDUPL_FRAME_INFO, DXGI_OUTPUT_DESC, IDXGIAdapter1, IDXGIDevice, IDXGIFactory1,
    IDXGIFactory4, IDXGIFactory6, IDXGIOutput1, IDXGIResource,
};
use windows::Win32::Graphics::Gdi::{
    BLACK_BRUSH, BeginPaint, EndPaint, FillRect, GetMonitorInfoW, GetStockObject, HBRUSH,
    InvalidateRect, LTGRAY_BRUSH, MONITORINFO, PAINTSTRUCT, UpdateWindow,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::SystemInformation::GetTickCount64;
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetThreadDpiAwarenessContext,
};
use windows::Win32::UI::WindowsAndMessaging::*;
use windows::core::{Error, Interface, Result as WinResult, w};

pub(crate) fn tick() -> u64 {
    unsafe { GetTickCount64() }
}
pub(crate) fn failure(stage: &str, error: &Error) -> Value {
    json!({"stage":stage,"hr":format!("0x{:08X}", error.code().0 as u32),"message":error.message()})
}
pub(crate) fn logical_failure(stage: &str, message: &str) -> Value {
    json!({"stage":stage,"hr":Value::Null,"message":message})
}

fn journal(row: &Value) {
    let mut output = std::io::stderr().lock();
    let _ = writeln!(output, "{row}");
    let _ = output.flush();
}

#[derive(Default)]
pub(crate) struct Calls {
    pub rows: Vec<Value>,
}
impl Calls {
    pub fn api<T>(&mut self, stage: &str, call: impl FnOnce() -> WinResult<T>) -> Result<T, Value> {
        journal(&json!({"event":"BEGIN","stage":stage,"tick_ms":tick()}));
        let start = tick();
        let clock = Instant::now();
        let result = call();
        let elapsed_us = clock.elapsed().as_micros();
        let mut row = json!({"event":"END","stage":stage,"start_tick_ms":start,"elapsed_ms":tick()-start,"elapsed_us":elapsed_us,"hr":"0x00000000"});
        if let Err(error) = &result {
            row["hr"] = json!(format!("0x{:08X}", error.code().0 as u32));
            row["message"] = json!(error.message());
        }
        journal(&row);
        self.rows.push(row);
        result.map_err(|error| failure(stage, &error))
    }
    pub fn value<T>(&mut self, stage: &str, call: impl FnOnce() -> T) -> T {
        journal(&json!({"event":"BEGIN","stage":stage,"tick_ms":tick()}));
        let start = tick();
        let clock = Instant::now();
        let result = call();
        let elapsed_us = clock.elapsed().as_micros();
        let row = json!({"event":"END","stage":stage,"start_tick_ms":start,"elapsed_ms":tick()-start,"elapsed_us":elapsed_us,"hr":Value::Null});
        journal(&row);
        self.rows.push(row);
        result
    }
}

fn luid(value: LUID) -> Value {
    json!({"low":value.LowPart,"high":value.HighPart})
}
fn rect(value: RECT) -> [i32; 4] {
    [value.left, value.top, value.right, value.bottom]
}
fn utf16(value: &[u16]) -> String {
    String::from_utf16_lossy(&value[..value.iter().position(|v| *v == 0).unwrap_or(value.len())])
}
fn adapter_info(adapter: &IDXGIAdapter1, index: u32, calls: &mut Calls) -> Result<Value, Value> {
    let description = calls.api("IDXGIAdapter1::GetDesc1", || unsafe { adapter.GetDesc1() })?;
    Ok(
        json!({"index":index,"description":utf16(&description.Description),"luid":luid(description.AdapterLuid),
        "adapter_luid":luid(description.AdapterLuid),"vendor_id":description.VendorId,"device_id":description.DeviceId,
        "subsystem_id":description.SubSysId,"revision":description.Revision,"flags":description.Flags,"software":description.Flags & 2 != 0,
        "dedicated_video_memory":description.DedicatedVideoMemory,"dedicated_system_memory":description.DedicatedSystemMemory,
        "shared_system_memory":description.SharedSystemMemory}),
    )
}
fn output_info(
    output: &IDXGIOutput1,
    index: u32,
    desc: DXGI_OUTPUT_DESC,
    calls: &mut Calls,
) -> Result<Value, Value> {
    let parent: IDXGIAdapter1 =
        calls.api("IDXGIOutput::GetParent", || unsafe { output.GetParent() })?;
    let parent_desc = calls.api("output-parent::GetDesc1", || unsafe { parent.GetDesc1() })?;
    let mut monitor_info = MONITORINFO {
        cbSize: std::mem::size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    let monitor_ok = calls
        .api("GetMonitorInfoW", || unsafe {
            GetMonitorInfoW(desc.Monitor, &mut monitor_info).ok()
        })
        .is_ok();
    Ok(
        json!({"index":index,"device_name":utf16(&desc.DeviceName),"desktop_rect":rect(desc.DesktopCoordinates),
        "width":desc.DesktopCoordinates.right-desc.DesktopCoordinates.left,"height":desc.DesktopCoordinates.bottom-desc.DesktopCoordinates.top,
        "attached":desc.AttachedToDesktop.as_bool(),"rotation":desc.Rotation.0,
        "monitor":format!("0x{:X}",desc.Monitor.0 as usize),"primary":if monitor_ok {Some(monitor_info.dwFlags & 1 != 0)} else {None},
        "output_parent_luid":luid(parent_desc.AdapterLuid),"output_parent_description":utf16(&parent_desc.Description)}),
    )
}
fn preference(
    factory: &IDXGIFactory6,
    kind: DXGI_GPU_PREFERENCE,
    calls: &mut Calls,
) -> Result<Vec<Value>, Value> {
    let mut rows = Vec::new();
    for index in 0.. {
        let result = calls.api("IDXGIFactory6::EnumAdapterByGpuPreference", || unsafe {
            factory.EnumAdapterByGpuPreference::<IDXGIAdapter1>(index, kind)
        });
        match result {
            Ok(adapter) => rows.push(adapter_info(&adapter, index, calls)?),
            Err(e) if e["hr"] == format!("0x{:08X}", DXGI_ERROR_NOT_FOUND.0 as u32) => break,
            Err(e) => return Err(e),
        }
    }
    Ok(rows)
}

fn adapter_outputs(adapter: &IDXGIAdapter1, calls: &mut Calls) -> Result<Vec<Value>, Value> {
    let mut outputs = Vec::new();
    for index in 0.. {
        let output = match calls.api("IDXGIAdapter::EnumOutputs", || unsafe {
            adapter.EnumOutputs(index)
        }) {
            Ok(value) => value,
            Err(e) if e["hr"] == format!("0x{:08X}", DXGI_ERROR_NOT_FOUND.0 as u32) => break,
            Err(e) => return Err(e),
        };
        let output: IDXGIOutput1 =
            calls.api("output::QueryInterface(IDXGIOutput1)", || output.cast())?;
        let desc = calls.api("IDXGIOutput::GetDesc", || unsafe { output.GetDesc() })?;
        outputs.push(output_info(&output, index, desc, calls)?);
    }
    Ok(outputs)
}

pub fn inventory() -> Value {
    let start = tick();
    let mut calls = Calls::default();
    let mut result = json!({"status":"ok","adapters":[],"adapters_by_luid":[],"min_power":[],"high_performance":[],
        "gpu_preferences":{"minimum_power":[],"high_performance":[]},"errors":[]});
    let run = (|| -> Result<(), Value> {
        let factory: IDXGIFactory1 =
            calls.api("CreateDXGIFactory1", || unsafe { CreateDXGIFactory1() })?;
        for index in 0.. {
            let adapter = match calls.api("IDXGIFactory1::EnumAdapters1", || unsafe {
                factory.EnumAdapters1(index)
            }) {
                Ok(value) => value,
                Err(e) if e["hr"] == format!("0x{:08X}", DXGI_ERROR_NOT_FOUND.0 as u32) => break,
                Err(e) => return Err(e),
            };
            let mut row = adapter_info(&adapter, index, &mut calls)?;
            row["outputs"] = json!(adapter_outputs(&adapter, &mut calls)?);
            result["adapters"].as_array_mut().unwrap().push(row);
        }
        // Test the supported explicit-LUID selection independently of enum order.
        // On hybrid systems the output view can still be reparented even here.
        let factory4: IDXGIFactory4 =
            calls.api("factory::QueryInterface(IDXGIFactory4)", || factory.cast())?;
        for original in result["adapters"].as_array().unwrap().clone() {
            let id = LUID {
                LowPart: original["luid"]["low"].as_u64().unwrap() as u32,
                HighPart: original["luid"]["high"].as_i64().unwrap() as i32,
            };
            let selected: Result<Value, Value> = (|| {
                let adapter: IDXGIAdapter1 = calls
                    .api("IDXGIFactory4::EnumAdapterByLuid", || unsafe {
                        factory4.EnumAdapterByLuid(id)
                    })?;
                let mut row = adapter_info(
                    &adapter,
                    original["index"].as_u64().unwrap() as u32,
                    &mut calls,
                )?;
                row["outputs"] = json!(adapter_outputs(&adapter, &mut calls)?);
                Ok(row)
            })();
            result["adapters_by_luid"]
                .as_array_mut()
                .unwrap()
                .push(match selected {
                    Ok(row) => row,
                    Err(error) => json!({"requested_luid":luid(id),"error":error}),
                });
        }
        let factory6: IDXGIFactory6 =
            calls.api("factory::QueryInterface(IDXGIFactory6)", || factory.cast())?;
        for (name, kind) in [
            ("min_power", DXGI_GPU_PREFERENCE_MINIMUM_POWER),
            ("high_performance", DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE),
        ] {
            match preference(&factory6, kind, &mut calls) {
                Ok(rows) => {
                    result[name] = json!(
                        rows.iter()
                            .map(|row| row["luid"].clone())
                            .collect::<Vec<_>>()
                    );
                    result["gpu_preferences"][if name == "min_power" {
                        "minimum_power"
                    } else {
                        name
                    }] = json!(rows);
                }
                Err(e) => result["errors"].as_array_mut().unwrap().push(e),
            }
        }
        Ok(())
    })();
    if let Err(error) = run {
        result["status"] = json!("error");
        result["error"] = error;
    } else if !result["errors"].as_array().unwrap().is_empty() {
        result["status"] = json!("partial");
    }
    result["start_tick_ms"] = json!(start);
    result["elapsed_ms"] = json!(tick() - start);
    result["calls"] = json!(calls.rows);
    result
}

fn find_output(
    source: &str,
    calls: &mut Calls,
) -> Result<(IDXGIAdapter1, IDXGIOutput1, DXGI_OUTPUT_DESC, Value), Value> {
    let factory: IDXGIFactory1 =
        calls.api("CreateDXGIFactory1", || unsafe { CreateDXGIFactory1() })?;
    for index in 0.. {
        let adapter = match calls.api("IDXGIFactory1::EnumAdapters1", || unsafe {
            factory.EnumAdapters1(index)
        }) {
            Ok(value) => value,
            Err(e) if e["hr"] == format!("0x{:08X}", DXGI_ERROR_NOT_FOUND.0 as u32) => break,
            Err(e) => return Err(e),
        };
        for output_index in 0.. {
            let output = match calls.api("IDXGIAdapter::EnumOutputs", || unsafe {
                adapter.EnumOutputs(output_index)
            }) {
                Ok(value) => value,
                Err(e) if e["hr"] == format!("0x{:08X}", DXGI_ERROR_NOT_FOUND.0 as u32) => break,
                Err(e) => return Err(e),
            };
            let desc = calls.api("IDXGIOutput::GetDesc", || unsafe { output.GetDesc() })?;
            if utf16(&desc.DeviceName).eq_ignore_ascii_case(source)
                && desc.AttachedToDesktop.as_bool()
            {
                let output: IDXGIOutput1 =
                    calls.api("output::QueryInterface(IDXGIOutput1)", || output.cast())?;
                // Ask the output itself: never pair an output with a preferred but unrelated GPU.
                let parent: IDXGIAdapter1 =
                    calls.api("source::GetParent", || unsafe { output.GetParent() })?;
                let info = output_info(&output, output_index, desc, calls)?;
                return Ok((parent, output, desc, info));
            }
        }
    }
    Err(logical_failure(
        "resolve-source",
        &format!("No attached DXGI output matches {source}"),
    ))
}

pub(crate) fn device_luid(device: &ID3D11Device, calls: &mut Calls) -> Result<Value, Value> {
    let dxgi: IDXGIDevice = calls.api("device::QueryInterface(IDXGIDevice)", || device.cast())?;
    let adapter = calls.api("IDXGIDevice::GetAdapter", || unsafe { dxgi.GetAdapter() })?;
    let description = calls.api("device-adapter::GetDesc", || unsafe { adapter.GetDesc() })?;
    Ok(luid(description.AdapterLuid))
}

pub fn probe(config: &Value) -> Value {
    let start = tick();
    let mut calls = Calls::default();
    let mut result = json!({"status":"error","frames":0,"subsequent_frames":0,"config":config,
        "start_tick_ms":start,"clock":"GetTickCount64","pixels_saved":false,"present_calls":0});
    let run = (|| -> Result<Value, Value> {
        let source = config["source_device"]
            .as_str()
            .ok_or_else(|| logical_failure("config", "source_device is required"))?;
        let backend = config["backend"].as_str().unwrap_or("dda");
        if !matches!(backend, "dda" | "wgc") {
            return Err(logical_failure("config", "backend must be dda or wgc"));
        }
        let window_mode = config["window"].as_str().unwrap_or("none");
        if !matches!(window_mode, "none" | "shown") {
            return Err(logical_failure("config", "window must be none or shown"));
        }
        let duration = config["duration_ms"]
            .as_u64()
            .unwrap_or(1500)
            .clamp(50, 10000);
        result["duration_ms"] = json!(duration);
        let (adapter, output, desc, info) = find_output(source, &mut calls)?;
        result["output"] = info;
        result["output_parent_luid"] = result["output"]["output_parent_luid"].clone();
        if let Some(expected) = config.get("expected_adapter_luid")
            && *expected != result["output_parent_luid"]
        {
            return Err(logical_failure(
                "verify-source-adapter",
                "Output parent LUID differs from expected_adapter_luid; no device or capture created",
            ));
        }
        let mut device = None;
        calls.api("D3D11CreateDevice", || unsafe {
            D3D11CreateDevice(
                &adapter,
                D3D_DRIVER_TYPE_UNKNOWN,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                Some(&[D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0]),
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                None,
            )
        })?;
        let device =
            device.ok_or_else(|| logical_failure("D3D11CreateDevice", "No device returned"))?;
        result["device_luid"] = device_luid(&device, &mut calls)?;
        result["device_matches_output_parent"] =
            json!(result["device_luid"] == result["output_parent_luid"]);
        if result["device_matches_output_parent"] != true {
            return Err(logical_failure(
                "verify-device-adapter",
                "Actual D3D device does not match output parent",
            ));
        }
        let window = if window_mode == "shown" {
            let target = window_rect(config.get("target_rect"), desc.DesktopCoordinates)?;
            let begin = tick();
            let clock = Instant::now();
            let guard = TestWindow::new(target, &mut calls)?;
            calls.rows.push(json!({"stage":"test-window-create-and-show","start_tick_ms":begin,"elapsed_ms":tick()-begin,"elapsed_us":clock.elapsed().as_micros(),"hr":"0x00000000"}));
            result["window"] = json!({"kind":"topmost-noactivate-hwnd-only","requested_rect":rect(target),"rect":rect(guard.actual_rect),"visible":true,"hwnd":format!("0x{:X}",guard.hwnd),
                "source_rect":rect(desc.DesktopCoordinates),"intersects_source":intersects(guard.actual_rect,desc.DesktopCoordinates),
                "stimulus":"alternating-black-gray-gdi-100ms",
                "note":"Window and GDI stimulus test; no SBMS pipeline, swap chain, hooks, or Present"});
            Some(guard)
        } else {
            None
        };
        let capture = match backend {
            "dda" => dda(&output, &device, duration, &mut calls),
            "wgc" => crate::wgc::run(desc.Monitor, &device, duration, &mut calls),
            _ => unreachable!(),
        };
        let close_start = tick();
        let close_clock = Instant::now();
        if let Some(mut window) = window {
            let closed = window.close(&mut calls);
            calls.rows.push(json!({"stage":"test-window-close-and-join","start_tick_ms":close_start,"elapsed_ms":tick()-close_start,
                "elapsed_us":close_clock.elapsed().as_micros(),"hr":closed.as_ref().err().map_or(json!("0x00000000"),|e|e["hr"].clone())}));
            match closed {
                Ok(metrics) => result["window"]["paint_metrics"] = metrics,
                Err(error) => result["window_cleanup_error"] = error,
            }
        }
        result["device_removed_reason"] = match calls
            .api("ID3D11Device::GetDeviceRemovedReason", || unsafe {
                device.GetDeviceRemovedReason()
            }) {
            Ok(()) => json!({"hr":"0x00000000"}),
            Err(error) => error,
        };
        capture
    })();
    match run {
        Ok(data) => {
            for (key, value) in data.as_object().unwrap() {
                result[key] = value.clone();
            }
        }
        Err(error) => {
            result["status"] = json!("error");
            result["error"] = error;
        }
    }
    if result.get("window_cleanup_error").is_some() {
        result["status"] = json!("error");
        if result.get("error").is_none() {
            result["error"] = result["window_cleanup_error"].clone();
        }
    }
    if result["device_removed_reason"].get("stage").is_some() {
        result["status"] = json!("error");
        if result.get("error").is_none() {
            result["error"] = result["device_removed_reason"].clone();
        }
    }
    result["end_tick_ms"] = json!(tick());
    result["elapsed_ms"] = json!(tick() - start);
    calls
        .rows
        .sort_by_key(|row| row["start_tick_ms"].as_u64().unwrap_or(0));
    result["calls"] = json!(calls.rows);
    result
}

fn dda(
    output: &IDXGIOutput1,
    device: &ID3D11Device,
    duration: u64,
    calls: &mut Calls,
) -> Result<Value, Value> {
    let duplication = calls.api("IDXGIOutput1::DuplicateOutput", || unsafe {
        output.DuplicateOutput(device)
    })?;
    let desc = calls.value("IDXGIOutputDuplication::GetDesc", || unsafe {
        duplication.GetDesc()
    });
    let mut result = json!({"status":"no_frames","frames":0,"subsequent_frames":0,"desktop_frames":0,"pointer_only_frames":0,"timeouts":0,"frame_samples":[],
        "duplication":{"width":desc.ModeDesc.Width,"height":desc.ModeDesc.Height,"format":desc.ModeDesc.Format.0,
            "refresh_numerator":desc.ModeDesc.RefreshRate.Numerator,"refresh_denominator":desc.ModeDesc.RefreshRate.Denominator,
            "rotation":desc.Rotation.0,"desktop_image_in_system_memory":desc.DesktopImageInSystemMemory.as_bool()},
        "duplication_instances":1,"first_acquire":Value::Null});
    let begin = tick();
    let mut frames = 0u64;
    let mut desktop_frames = 0u64;
    let mut timeouts = 0u64;
    let mut attempts = 0u64;
    loop {
        let mut info = DXGI_OUTDUPL_FRAME_INFO::default();
        let mut resource: Option<IDXGIResource> = None;
        let acquired = calls.api(
            "IDXGIOutputDuplication::AcquireNextFrame(50ms)",
            || unsafe { duplication.AcquireNextFrame(50, &mut info, &mut resource) },
        );
        attempts += 1;
        if attempts == 1 {
            result["first_acquire"] = match &acquired {
                Ok(()) => json!({"status":"success","hr":"0x00000000","elapsed_ms":tick()-begin}),
                Err(error) => {
                    json!({"status":if error["hr"] == format!("0x{:08X}",DXGI_ERROR_WAIT_TIMEOUT.0 as u32) {"timeout"}
                    else if error["hr"] == format!("0x{:08X}",DXGI_ERROR_ACCESS_LOST.0 as u32) {"access_lost"} else {"error"},
                    "error":error,"elapsed_ms":tick()-begin})
                }
            };
        }
        match acquired {
            Ok(()) => {
                frames += 1;
                if info.LastPresentTime != 0 {
                    desktop_frames += 1;
                    if desktop_frames == 1 {
                        result["first_desktop_frame_elapsed_ms"] = json!(tick() - begin);
                    }
                }
                if frames == 1 {
                    result["first_frame_tick_ms"] = json!(tick());
                    result["first_frame_elapsed_ms"] = json!(tick() - begin);
                }
                let metadata = (|| -> Result<Value, Value> {
                    let resource = resource.as_ref().ok_or_else(|| {
                        logical_failure(
                            "AcquireNextFrame",
                            "Successful acquire returned no resource",
                        )
                    })?;
                    let texture: ID3D11Texture2D =
                        calls.api("frame::QueryInterface(ID3D11Texture2D)", || resource.cast())?;
                    let mut texture_desc = D3D11_TEXTURE2D_DESC::default();
                    calls.value("ID3D11Texture2D::GetDesc", || unsafe {
                        texture.GetDesc(&mut texture_desc)
                    });
                    Ok(
                        json!({"sequence":frames,"tick_ms":tick(),"last_present_qpc":info.LastPresentTime,"last_mouse_update_qpc":info.LastMouseUpdateTime,
                        "accumulated_frames":info.AccumulatedFrames,"metadata_bytes":info.TotalMetadataBufferSize,"protected_content_masked":info.ProtectedContentMaskedOut.as_bool(),
                        "width":texture_desc.Width,"height":texture_desc.Height,"format":texture_desc.Format.0}),
                    )
                })();
                drop(resource);
                let release = calls.api("IDXGIOutputDuplication::ReleaseFrame", || unsafe {
                    duplication.ReleaseFrame()
                });
                match metadata {
                    Ok(data) => {
                        if frames <= 8 {
                            result["frame_samples"].as_array_mut().unwrap().push(data)
                        }
                    }
                    Err(error) => {
                        result["status"] = json!("error");
                        result["error"] = error;
                    }
                }
                if let Err(error) = release {
                    result["status"] = json!("error");
                    result["error"] = error;
                    break;
                }
                if result.get("error").is_some() {
                    break;
                }
                result["status"] = json!("ok");
            }
            Err(error) if error["hr"] == format!("0x{:08X}", DXGI_ERROR_WAIT_TIMEOUT.0 as u32) => {
                timeouts += 1
            }
            Err(error) => {
                result["status"] = json!("error");
                result["error"] = error;
                break;
            }
        }
        if tick() - begin >= duration || attempts >= 1000 {
            break;
        }
    }
    result["frames"] = json!(frames);
    result["desktop_frames"] = json!(desktop_frames);
    result["pointer_only_frames"] = json!(frames - desktop_frames);
    result["subsequent_frames"] = json!(frames.saturating_sub(1));
    result["timeouts"] = json!(timeouts);
    result["acquire_attempts"] = json!(attempts);
    result["capture_elapsed_ms"] = json!(tick() - begin);
    Ok(result)
}

fn window_rect(value: Option<&Value>, source: RECT) -> Result<RECT, Value> {
    if let Some(value) = value {
        let parts = value
            .as_array()
            .filter(|parts| parts.len() == 4)
            .ok_or_else(|| {
                logical_failure("config", "target_rect must be [left,top,right,bottom]")
            })?;
        let values = parts
            .iter()
            .map(|part| part.as_i64().and_then(|n| i32::try_from(n).ok()))
            .collect::<Option<Vec<_>>>()
            .ok_or_else(|| {
                logical_failure("config", "target_rect requires four i32 coordinates")
            })?;
        let rect = RECT {
            left: values[0],
            top: values[1],
            right: values[2],
            bottom: values[3],
        };
        if rect
            .right
            .checked_sub(rect.left)
            .filter(|n| *n > 0)
            .is_none()
            || rect
                .bottom
                .checked_sub(rect.top)
                .filter(|n| *n > 0)
                .is_none()
        {
            return Err(logical_failure(
                "config",
                "target_rect must have positive, non-overflowing dimensions",
            ));
        }
        Ok(rect)
    } else {
        Ok(RECT {
            left: (source.right - 160).max(source.left),
            top: (source.bottom - 120).max(source.top),
            right: source.right,
            bottom: source.bottom,
        })
    }
}

fn intersects(left: RECT, right: RECT) -> bool {
    left.left < right.right
        && left.right > right.left
        && left.top < right.bottom
        && left.bottom > right.top
}

thread_local! {static PAINT_COUNTS:Cell<(u64,u64)>=const {Cell::new((0,0))};}

struct WindowOutcome {
    cleanup: Result<(), Value>,
    calls: Vec<Value>,
    metrics: Value,
}
struct TestWindow {
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<WindowOutcome>>,
    hwnd: usize,
    actual_rect: RECT,
}
impl TestWindow {
    fn new(rect: RECT, calls: &mut Calls) -> Result<Self, Value> {
        let stop = Arc::new(AtomicBool::new(false));
        let flag = stop.clone();
        let (sender, receiver) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || unsafe {
            let mut trace = Calls::default();
            let old_dpi = trace.api("window::SetThreadDpiAwarenessContext", || {
                let previous =
                    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                if previous.0.is_null() {
                    Err(Error::from_thread())
                } else {
                    Ok(previous)
                }
            });
            let setup = (|| -> Result<(HWND, RECT), Value> {
                old_dpi.as_ref().map_err(Clone::clone)?;
                let instance = trace.api("window::GetModuleHandleW", || GetModuleHandleW(None))?;
                let class = WNDCLASSW {
                    lpfnWndProc: Some(window_proc),
                    hInstance: instance.into(),
                    lpszClassName: w!("SBMSCaptureDiagnosticWindow"),
                    hbrBackground: HBRUSH(GetStockObject(BLACK_BRUSH).0),
                    ..Default::default()
                };
                if let Err(error) = trace.api("window::RegisterClassW", || {
                    if RegisterClassW(&class) == 0 {
                        Err(Error::from_thread())
                    } else {
                        Ok(())
                    }
                }) && error["hr"] != "0x80070582"
                {
                    return Err(error);
                }
                let hwnd = trace.api("window::CreateWindowExW", || {
                    CreateWindowExW(
                        WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                        w!("SBMSCaptureDiagnosticWindow"),
                        w!(""),
                        WS_POPUP,
                        rect.left,
                        rect.top,
                        rect.right - rect.left,
                        rect.bottom - rect.top,
                        None,
                        None,
                        Some(instance.into()),
                        None,
                    )
                })?;
                let _ = trace.value("window::ShowWindow(SW_SHOWNOACTIVATE)", || {
                    ShowWindow(hwnd, SW_SHOWNOACTIVATE)
                });
                let mut actual_rect = RECT::default();
                if let Err(error) = trace.api("window::GetWindowRect", || {
                    GetWindowRect(hwnd, &mut actual_rect)
                }) {
                    let _ = trace.api("window::DestroyWindow", || DestroyWindow(hwnd));
                    return Err(error);
                }
                if !trace.value("window::IsWindowVisible", || {
                    IsWindowVisible(hwnd).as_bool()
                }) {
                    let _ = trace.api("window::DestroyWindow", || DestroyWindow(hwnd));
                    return Err(logical_failure(
                        "window::IsWindowVisible",
                        "Test window is not visible",
                    ));
                }
                let _ = trace.value("window::UpdateWindow(initial)", || UpdateWindow(hwnd));
                Ok((hwnd, actual_rect))
            })();
            let cleanup = match setup {
                Ok((hwnd, actual_rect)) => {
                    let _ = sender.send(Ok((hwnd.0 as usize, actual_rect)));
                    let mut last_paint = tick();
                    while !flag.load(Ordering::Acquire) {
                        let mut message = MSG::default();
                        while PeekMessageW(&mut message, None, 0, 0, PM_REMOVE).as_bool() {
                            let _ = TranslateMessage(&message);
                            DispatchMessageW(&message);
                        }
                        if tick() - last_paint >= 100 {
                            let _ = trace.value("window::InvalidateRect(stimulus)", || {
                                InvalidateRect(Some(hwnd), None, false)
                            });
                            let _ = trace
                                .value("window::UpdateWindow(stimulus)", || UpdateWindow(hwnd));
                            last_paint = tick();
                        }
                        thread::sleep(Duration::from_millis(10));
                    }
                    trace.api("window::DestroyWindow", || DestroyWindow(hwnd))
                }
                Err(error) => {
                    let _ = sender.send(Err(error));
                    Ok(())
                }
            };
            if let Ok(old_dpi) = old_dpi {
                trace.value("window::restore-DPI-context", || {
                    SetThreadDpiAwarenessContext(old_dpi)
                });
            }
            let (paints, failures) = PAINT_COUNTS.with(Cell::get);
            WindowOutcome {
                cleanup,
                calls: trace.rows,
                metrics: json!({"successful_paints":paints,"paint_failures":failures,"stimulus_confirmed":paints>=2&&failures==0}),
            }
        });
        match receiver.recv_timeout(Duration::from_secs(5)) {
            Ok(Ok((hwnd, actual_rect))) => Ok(Self {
                stop,
                thread: Some(thread),
                hwnd,
                actual_rect,
            }),
            other => {
                stop.store(true, Ordering::Release);
                if let Ok(outcome) = thread.join() {
                    calls.rows.extend(outcome.calls);
                }
                Err(match other {
                    Ok(Err(error)) => error,
                    _ => logical_failure(
                        "test-window-create",
                        "Window thread did not report readiness",
                    ),
                })
            }
        }
    }
    fn close(&mut self, calls: &mut Calls) -> Result<Value, Value> {
        self.stop.store(true, Ordering::Release);
        match self.thread.take() {
            Some(thread) => {
                let outcome = thread
                    .join()
                    .map_err(|_| logical_failure("test-window-join", "Window thread panicked"))?;
                calls.rows.extend(outcome.calls);
                outcome.cleanup?;
                Ok(outcome.metrics)
            }
            None => Ok(Value::Null),
        }
    }
}
impl Drop for TestWindow {
    fn drop(&mut self) {
        let _ = self.close(&mut Calls::default());
    }
}
unsafe extern "system" fn window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if message == WM_PAINT {
        unsafe {
            let mut paint = PAINTSTRUCT::default();
            let dc = BeginPaint(hwnd, &mut paint);
            let brush = HBRUSH(
                GetStockObject(if PAINT_COUNTS.with(Cell::get).0.is_multiple_of(2) {
                    BLACK_BRUSH
                } else {
                    LTGRAY_BRUSH
                })
                .0,
            );
            let filled = !dc.0.is_null() && FillRect(dc, &paint.rcPaint, brush) != 0;
            let ended = EndPaint(hwnd, &paint).as_bool();
            PAINT_COUNTS.with(|counts| {
                let (ok, err) = counts.get();
                counts.set((
                    ok + u64::from(filled && ended),
                    err + u64::from(!filled || !ended),
                ));
            });
        }
        LRESULT(0)
    } else if message == WM_MOUSEACTIVATE {
        LRESULT(MA_NOACTIVATE as isize)
    } else {
        unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn explicit_window_rect_uses_ltrb_and_rejects_inverted_bounds() {
        let input = json!([-1920, 20, 0, 1080]);
        assert_eq!(
            rect(window_rect(Some(&input), RECT::default()).unwrap()),
            [-1920, 20, 0, 1080]
        );
        assert!(window_rect(Some(&json!([0, 0, -1, 100])), RECT::default()).is_err());
        assert!(window_rect(Some(&json!([i32::MIN, 0, i32::MAX, 100])), RECT::default()).is_err());
    }
}
