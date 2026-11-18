//! Isolated probe of the production DDA -> shader -> flip swapchain path.
//! Usage: capture-routing-render-probe --config FILE --report FILE [--native-output-routing]
//! The supervising process must enforce a hard timeout if a driver call blocks.

// The included renderer's tests run in the library, not in this manual probe.
#![cfg(not(test))]

use sbms::{config, diagnostics};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::{
    error::Error,
    fs,
    path::{Path, PathBuf},
    sync::atomic::{AtomicBool, AtomicU64, Ordering},
    thread,
    time::{Duration, Instant},
};
use windows::{
    Win32::{
        Foundation::{COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, RECT, WPARAM},
        Graphics::Gdi::{
            BeginPaint, CreateSolidBrush, DeleteObject, EndPaint, FillRect, InvalidateRect,
            PAINTSTRUCT, UpdateWindow,
        },
        System::LibraryLoader::GetModuleHandleW,
        UI::{
            HiDpi::{
                DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, GetDpiForWindow,
                SetProcessDpiAwarenessContext, SetThreadDpiAwarenessContext,
            },
            WindowsAndMessaging::*,
        },
    },
    core::w,
};

#[path = "../src/gpu_renderer.rs"]
mod gpu_renderer;
#[path = "../tools/capture-diagnostics/src/hybrid.rs"]
mod hybrid;

type Result<T> = std::result::Result<T, Box<dyn Error>>;
static STOP: AtomicBool = AtomicBool::new(false);
static SOURCE_TICKS: AtomicU64 = AtomicU64::new(0);
static SOURCE_PAINTS: AtomicU64 = AtomicU64::new(0);
static SOURCE_PAINT_ERRORS: AtomicU64 = AtomicU64::new(0);

#[derive(Clone, Copy, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct ProbeConfig {
    source_rect: [i32; 4],
    target_rect: [i32; 4],
    #[serde(default)]
    high_performance_sampling: Option<bool>,
    #[serde(default)]
    full_frame_copy: bool,
    #[serde(default)]
    static_refresh: bool,
    #[serde(default = "default_duration")]
    duration_ms: u64,
    #[serde(default = "default_refresh")]
    target_refresh_millihz: u32,
    #[serde(default = "default_stimulus")]
    source_stimulus: bool,
}

fn default_duration() -> u64 {
    3000
}
fn default_refresh() -> u32 {
    60_000
}
fn default_stimulus() -> bool {
    true
}

fn rectangle([left, top, right, bottom]: [i32; 4]) -> RECT {
    RECT {
        left,
        top,
        right,
        bottom,
    }
}

fn lifecycle(message: impl AsRef<str>) {
    eprintln!(
        "{}",
        json!({"event":"lifecycle", "message":message.as_ref()})
    );
    diagnostics::log(
        diagnostics::Level::Info,
        "render-probe",
        "lifecycle",
        None,
        message.as_ref(),
    );
}

unsafe extern "system" fn target_proc(hwnd: HWND, message: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match message {
        WM_CLOSE => {
            STOP.store(true, Ordering::Release);
            LRESULT(0)
        }
        WM_MOUSEACTIVATE => LRESULT(MA_NOACTIVATE as isize),
        WM_ERASEBKGND => LRESULT(1),
        WM_PAINT => {
            let mut paint = PAINTSTRUCT::default();
            unsafe {
                BeginPaint(hwnd, &mut paint);
                let _ = EndPaint(hwnd, &paint);
            }
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(hwnd, message, wp, lp) },
    }
}

unsafe extern "system" fn source_proc(hwnd: HWND, message: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match message {
        WM_TIMER => {
            SOURCE_TICKS.fetch_add(1, Ordering::Relaxed);
            unsafe {
                let _ = InvalidateRect(Some(hwnd), None, false);
            }
            LRESULT(0)
        }
        WM_PAINT => {
            let mut paint = PAINTSTRUCT::default();
            let mut client = RECT::default();
            unsafe {
                let dc = BeginPaint(hwnd, &mut paint);
                let color = if SOURCE_TICKS.load(Ordering::Relaxed).is_multiple_of(2) {
                    COLORREF(0x0030_40e0)
                } else {
                    COLORREF(0x00e0_c030)
                };
                let brush = CreateSolidBrush(color);
                let filled =
                    GetClientRect(hwnd, &mut client).is_ok() && FillRect(dc, &client, brush) != 0;
                let deleted = DeleteObject(brush.into()).as_bool();
                let ended = EndPaint(hwnd, &paint).as_bool();
                if filled && deleted && ended {
                    SOURCE_PAINTS.fetch_add(1, Ordering::Relaxed);
                } else {
                    SOURCE_PAINT_ERRORS.fetch_add(1, Ordering::Relaxed);
                }
            }
            LRESULT(0)
        }
        _ => unsafe { target_proc(hwnd, message, wp, lp) },
    }
}

struct ProbeWindows {
    target: HWND,
    source: Option<HWND>,
    instance: HINSTANCE,
}

impl ProbeWindows {
    fn create(config: ProbeConfig) -> Result<Self> {
        let instance = HINSTANCE(unsafe { GetModuleHandleW(None)? }.0);
        let classes: [(_, WNDPROC); 2] = [
            (w!("SBMS.RenderProbe.Target"), Some(target_proc)),
            (w!("SBMS.RenderProbe.Source"), Some(source_proc)),
        ];
        for (name, proc) in classes {
            let class = WNDCLASSW {
                lpfnWndProc: proc,
                hInstance: instance,
                lpszClassName: name,
                ..Default::default()
            };
            if unsafe { RegisterClassW(&class) } == 0 {
                return Err(windows::core::Error::from_thread().into());
            }
        }
        let create = |name, r: [i32; 4]| unsafe {
            CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                name,
                w!("SBMS render probe"),
                WS_POPUP,
                r[0],
                r[1],
                r[2] - r[0],
                r[3] - r[1],
                None,
                None,
                Some(instance),
                None,
            )
        };
        let target = create(w!("SBMS.RenderProbe.Target"), config.target_rect)?;
        let mut windows = Self {
            target,
            source: None,
            instance,
        };
        if config.source_stimulus {
            let source = create(w!("SBMS.RenderProbe.Source"), config.source_rect)?;
            windows.source = Some(source);
            unsafe {
                let _ = ShowWindow(source, SW_SHOWNOACTIVATE);
                if SetTimer(Some(source), 1, 100, None) == 0 {
                    return Err(windows::core::Error::from_thread().into());
                }
                let _ = InvalidateRect(Some(source), None, false);
                UpdateWindow(source).ok()?;
            }
        }
        unsafe {
            let _ = ShowWindow(target, SW_SHOWNOACTIVATE);
        }
        Ok(windows)
    }

    fn observation(&self) -> Result<Value> {
        let observe = |window| -> Result<Value> {
            let mut outer = RECT::default();
            let mut client = RECT::default();
            unsafe {
                GetWindowRect(window, &mut outer)?;
                GetClientRect(window, &mut client)?;
            }
            Ok(json!({"hwnd":window.0 as usize,
                "outer":[outer.left,outer.top,outer.right,outer.bottom],
                "client":[client.left,client.top,client.right,client.bottom],
                "dpi":unsafe { GetDpiForWindow(window) }}))
        };
        Ok(json!({"target":observe(self.target)?,
            "source":self.source.map(observe).transpose()?}))
    }

    fn close(&mut self) -> Vec<String> {
        let mut errors = Vec::new();
        unsafe {
            if let Some(source) = self.source.take() {
                let _ = KillTimer(Some(source), 1);
                if let Err(error) = DestroyWindow(source) {
                    errors.push(error.to_string());
                }
            }
            if !self.target.0.is_null() {
                if let Err(error) = DestroyWindow(self.target) {
                    errors.push(error.to_string());
                }
                self.target = HWND::default();
            }
            for name in [w!("SBMS.RenderProbe.Target"), w!("SBMS.RenderProbe.Source")] {
                if let Err(error) = UnregisterClassW(name, Some(self.instance)) {
                    errors.push(error.to_string());
                }
            }
        }
        errors
    }
}

impl Drop for ProbeWindows {
    fn drop(&mut self) {
        if !self.target.0.is_null() {
            let _ = self.close();
        }
    }
}

fn run(config: ProbeConfig) -> Result<Value> {
    for rect in [config.source_rect, config.target_rect] {
        if rect[2] <= rect[0] || rect[3] <= rect[1] {
            return Err("source_rect and target_rect must have positive extents".into());
        }
    }
    let s = config.source_rect;
    let t = config.target_rect;
    if s[0] < t[2] && t[0] < s[2] && s[1] < t[3] && t[1] < s[3] {
        return Err("source_rect and target_rect overlap; refusing presentation feedback".into());
    }
    if config.duration_ms == 0 {
        return Err("duration_ms must be positive".into());
    }
    let mut windows = ProbeWindows::create(config)?;
    let window_observation = windows.observation()?;
    if window_observation["target"]["outer"] != json!(t)
        || window_observation["target"]["client"] != json!([0, 0, t[2] - t[0], t[3] - t[1]])
        || (config.source_stimulus && window_observation["source"]["outer"] != json!(s))
    {
        return Err(
            format!("window rectangles differ from physical config: {window_observation}").into(),
        );
    }
    lifecycle(format!("windows ready {window_observation}"));
    let window_value = windows.target.0 as usize;
    let started = Instant::now();
    let worker = thread::spawn(move || {
        unsafe {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        let mut presented = 0u64;
        let mut capture_checked = 0u64;
        let mut first_present_ms = None;
        lifecycle("gpu worker enter");
        let result = gpu_renderer::run_gpu_renderer(
            gpu_renderer::GpuRendererConfig {
                target_window: HWND(window_value as *mut _),
                target_width: (t[2] - t[0]) as u32,
                target_height: (t[3] - t[1]) as u32,
                source_rect: rectangle(s),
                rendering: config::RenderingOptions {
                    high_performance_sampling: config.high_performance_sampling,
                    full_frame_copy: config.full_frame_copy,
                    static_refresh: config.static_refresh,
                },
                target_refresh_millihz: config.target_refresh_millihz,
            },
            &STOP,
            |event| {
                match event {
                    gpu_renderer::GpuRendererEvent::Presented => {
                        presented += 1;
                        if first_present_ms.is_none() {
                            let elapsed = started.elapsed().as_secs_f64() * 1000.0;
                            first_present_ms = Some(elapsed);
                            lifecycle(format!("first Present elapsed_ms={elapsed:.3}"));
                        }
                    }
                    gpu_renderer::GpuRendererEvent::CaptureChecked => capture_checked += 1,
                }
                Ok(())
            },
        );
        lifecycle(format!(
            "gpu worker exit stop_requested={} result={result:?}",
            STOP.load(Ordering::Acquire)
        ));
        let retryable = result
            .as_ref()
            .err()
            .is_some_and(gpu_renderer::GpuRendererError::is_retryable);
        json!({"returned_ok":result.is_ok(), "result":format!("{result:?}"), "retryable":retryable,
            "error":result.err().map(|error|error.to_string()),
            "presented":presented,"capture_checked":capture_checked,
            "first_present_ms":first_present_ms,
            "elapsed_ms":started.elapsed().as_secs_f64()*1000.0,
            "stop_requested_on_exit":STOP.load(Ordering::Acquire)})
    });
    let mut deadline_reached = false;
    while !worker.is_finished() {
        let mut message = MSG::default();
        unsafe {
            while PeekMessageW(&mut message, None, 0, 0, PM_REMOVE).as_bool() {
                if message.message == WM_QUIT {
                    STOP.store(true, Ordering::Release);
                }
                let _ = TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
        if !deadline_reached && started.elapsed() >= Duration::from_millis(config.duration_ms) {
            deadline_reached = true;
            STOP.store(true, Ordering::Release);
            lifecycle("deadline reached; stop requested; pumping until gpu exit");
        }
        thread::sleep(Duration::from_millis(2));
    }
    lifecycle("gpu join enter");
    let worker_result = worker.join().map_err(|_| "gpu worker panicked");
    lifecycle("gpu join returned; window cleanup enter");
    let cleanup_errors = windows.close();
    lifecycle(format!("window cleanup returned errors={cleanup_errors:?}"));
    Ok(json!({"windows":window_observation, "gpu":worker_result?,
        "deadline_reached":deadline_reached,"worker_joined":true,
        "cleanup_errors":cleanup_errors,
        "source_stimulus":{"enabled":config.source_stimulus,"timer_period_ms":100,
            "ticks":SOURCE_TICKS.load(Ordering::Relaxed),
            "successful_paints":SOURCE_PAINTS.load(Ordering::Relaxed),
            "paint_errors":SOURCE_PAINT_ERRORS.load(Ordering::Relaxed)}}))
}

fn write_report(path: &Path, report: &Value) -> Result<()> {
    fs::write(path, serde_json::to_vec_pretty(report)?)?;
    Ok(())
}

fn main() -> Result<()> {
    let args: Vec<_> = std::env::args().collect();
    let argument = |flag| -> Result<PathBuf> {
        args.windows(2)
            .find(|pair| pair[0] == flag)
            .map(|pair| PathBuf::from(&pair[1]))
            .ok_or_else(|| {
                format!(
                    "missing {flag}; usage: --config FILE --report FILE [--native-output-routing]"
                )
                .into()
            })
    };
    let report_path = argument("--report")?;
    let config_path = argument("--config")?;
    let native_routing = args.iter().any(|arg| arg == "--native-output-routing");
    let mut report = json!({"pid":std::process::id(),"status":"starting",
        "coordinate_space":"physical-pixels","config_path":config_path,
        "pipeline":"src/gpu_renderer.rs::run_gpu_renderer"});
    write_report(&report_path, &report)?;
    let outcome = (|| -> Result<Value> {
        // This must precede DXGI and RenderingOptions automatic preference detection.
        if native_routing {
            hybrid::install()?;
        }
        unsafe {
            let process_dpi =
                SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            report["process_dpi_result"] = json!(format!("{process_dpi:?}"));
            if SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)
                .0
                .is_null()
            {
                return Err(windows::core::Error::from_thread().into());
            }
        }
        report["diagnostics_path"] = json!(diagnostics::init()?);
        let config_text = fs::read_to_string(config_path)?;
        let config: ProbeConfig = serde_json::from_str(config_text.trim_start_matches('\u{feff}'))?;
        report["config"] = json!(config);
        report["status"] = json!("running");
        write_report(&report_path, &report)?;
        run(config)
    })();
    report["routing"] = hybrid::report(native_routing);
    report["diagnostics"] = json!(diagnostics::recent());
    match &outcome {
        Ok(result) => {
            report["status"] = json!("completed");
            report["run"] = result.clone();
        }
        Err(error) => {
            report["status"] = json!("harness-error");
            report["error"] = json!(error.to_string());
        }
    }
    write_report(&report_path, &report)?;
    eprintln!(
        "{}",
        json!({"event":"report", "path":report_path, "status":report["status"]})
    );
    outcome.map(|_| ())
}
