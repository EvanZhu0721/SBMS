//! WGC counterpart to the DXGI probe, using the exact same monitor and D3D device.
use crate::capture::{Calls, logical_failure, tick};
use serde_json::{Value, json};
use std::{thread, time::Duration};
use windows::Graphics::Capture::{
    Direct3D11CaptureFrame, Direct3D11CaptureFramePool, GraphicsCaptureItem, GraphicsCaptureSession,
};
use windows::Graphics::DirectX::{Direct3D11::IDirect3DDevice, DirectXPixelFormat};
use windows::Win32::Graphics::{Direct3D11::ID3D11Device, Dxgi::IDXGIDevice, Gdi::HMONITOR};
use windows::Win32::System::WinRT::Direct3D11::CreateDirect3D11DeviceFromDXGIDevice;
use windows::Win32::System::WinRT::Graphics::Capture::IGraphicsCaptureItemInterop;
use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize, RoUninitialize};
use windows::core::{Interface, factory};

struct Apartment;
impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe { RoUninitialize() }
    }
}

pub(crate) fn run(
    monitor: HMONITOR,
    device: &ID3D11Device,
    duration: u64,
    calls: &mut Calls,
) -> Result<Value, Value> {
    calls.api("RoInitialize(MTA)", || unsafe {
        RoInitialize(RO_INIT_MULTITHREADED)
    })?;
    let _apartment = Apartment;
    if !calls.api(
        "GraphicsCaptureSession::IsSupported",
        GraphicsCaptureSession::IsSupported,
    )? {
        return Ok(
            json!({"status":"skip","frames":0,"subsequent_frames":0,"error":logical_failure("GraphicsCaptureSession::IsSupported","WGC is unsupported on this system")}),
        );
    }
    let interop: IGraphicsCaptureItemInterop = calls.api(
        "GraphicsCaptureItem::factory(Interop)",
        factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>,
    )?;
    let item: GraphicsCaptureItem = match calls
        .api("IGraphicsCaptureItemInterop::CreateForMonitor", || unsafe {
            interop.CreateForMonitor(monitor)
        }) {
        Ok(item) => item,
        Err(mut error) => {
            if error["hr"] == "0x80070005" {
                error["classification"] = json!("access_denied_or_user_denied");
            }
            return Err(error);
        }
    };
    let size = calls.api("GraphicsCaptureItem::Size", || item.Size())?;
    if size.Width <= 0 || size.Height <= 0 {
        return Err(logical_failure(
            "GraphicsCaptureItem::Size",
            "Capture item has no positive dimensions",
        ));
    }
    let dxgi: IDXGIDevice =
        calls.api("wgc-device::QueryInterface(IDXGIDevice)", || device.cast())?;
    let inspectable = calls.api("CreateDirect3D11DeviceFromDXGIDevice", || unsafe {
        CreateDirect3D11DeviceFromDXGIDevice(&dxgi)
    })?;
    let runtime_device: IDirect3DDevice = calls
        .api("winrt-device::QueryInterface(IDirect3DDevice)", || {
            inspectable.cast()
        })?;
    let pool = calls.api("Direct3D11CaptureFramePool::CreateFreeThreaded", || {
        Direct3D11CaptureFramePool::CreateFreeThreaded(
            &runtime_device,
            DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2,
            size,
        )
    })?;
    let session = match calls.api("Direct3D11CaptureFramePool::CreateCaptureSession", || {
        pool.CreateCaptureSession(&item)
    }) {
        Ok(session) => session,
        Err(error) => {
            let _ = calls.api("Direct3D11CaptureFramePool::Close", || pool.Close());
            return Err(error);
        }
    };
    let capture = (|| -> Result<Value, Value> {
        calls.api("GraphicsCaptureSession::StartCapture", || {
            session.StartCapture()
        })?;
        let begin = tick();
        let mut frames = 0u64;
        let mut empty = 0u64;
        let mut result = json!({"status":"no_frames","frames":0,"subsequent_frames":0,"frame_samples":[],
            "item":{"width":size.Width,"height":size.Height,"monitor":format!("0x{:X}",monitor.0 as usize)},
            "frame_pool":"CreateFreeThreaded","buffer_count":2,"first_acquire":Value::Null});
        while tick() - begin < duration {
            // The projected TryGetNextFrame treats S_OK + null as E_POINTER. Preserve
            // the ABI result so an empty pool is not confused with a real HRESULT failure.
            let frame = calls.api("Direct3D11CaptureFramePool::TryGetNextFrame", || unsafe {
                let mut raw = std::ptr::null_mut();
                (pool.vtable().TryGetNextFrame)(pool.as_raw(), &mut raw).ok()?;
                Ok(if raw.is_null() {
                    None
                } else {
                    Some(Direct3D11CaptureFrame::from_raw(raw))
                })
            });
            if result["first_acquire"].is_null() {
                result["first_acquire"] = match &frame {
                    Ok(Some(_)) => {
                        json!({"status":"success","hr":"0x00000000","elapsed_ms":tick()-begin})
                    }
                    Ok(None) => {
                        json!({"status":"empty","hr":"0x00000000","elapsed_ms":tick()-begin})
                    }
                    Err(error) => json!({"status":"error","error":error,"elapsed_ms":tick()-begin}),
                };
            }
            match frame {
                Ok(Some(frame)) => {
                    frames += 1;
                    if frames == 1 {
                        result["first_frame_tick_ms"] = json!(tick());
                        result["first_frame_elapsed_ms"] = json!(tick() - begin);
                    }
                    let metadata = (|| -> Result<Value, Value> {
                        let size = calls.api("Direct3D11CaptureFrame::ContentSize", || {
                            frame.ContentSize()
                        })?;
                        let time = calls
                            .api("Direct3D11CaptureFrame::SystemRelativeTime", || {
                                frame.SystemRelativeTime()
                            })?;
                        Ok(
                            json!({"sequence":frames,"tick_ms":tick(),"width":size.Width,"height":size.Height,"system_relative_time_100ns":time.Duration}),
                        )
                    })();
                    let closed = calls.api("Direct3D11CaptureFrame::Close", || frame.Close());
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
                    if let Err(error) = closed {
                        result["status"] = json!("error");
                        result["error"] = error;
                        break;
                    }
                    if result.get("error").is_some() {
                        break;
                    }
                    result["status"] = json!("ok");
                }
                Ok(None) => empty += 1,
                Err(error) => {
                    result["status"] = json!("error");
                    result["error"] = error;
                    break;
                }
            }
            thread::sleep(Duration::from_millis(10));
        }
        result["frames"] = json!(frames);
        result["subsequent_frames"] = json!(frames.saturating_sub(1));
        result["empty_polls"] = json!(empty);
        result["capture_elapsed_ms"] = json!(tick() - begin);
        Ok(result)
    })();
    let session_close = calls.api("GraphicsCaptureSession::Close", || session.Close());
    let pool_close = calls.api("Direct3D11CaptureFramePool::Close", || pool.Close());
    let mut result = capture?;
    for error in [session_close.err(), pool_close.err()]
        .into_iter()
        .flatten()
    {
        result["status"] = json!("error");
        if result.get("error").is_none() {
            result["error"] = error;
        }
    }
    Ok(result)
}
