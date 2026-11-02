use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{HANDLE, HINSTANCE, HWND, LPARAM, LRESULT, WAIT_FAILED, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::{CreateEventW, GetCurrentThreadId, INFINITE, SetEvent};
use windows::Win32::UI::WindowsAndMessaging::{
    CS_HREDRAW, CS_VREDRAW, CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW,
    IsWindow, MSG, MWMO_INPUTAVAILABLE, MsgWaitForMultipleObjectsEx, PM_REMOVE, PeekMessageW,
    PostQuitMessage, QS_ALLINPUT, RegisterClassW, SW_SHOW, ShowWindow, TranslateMessage,
    UnregisterClassW, WINDOW_EX_STYLE, WM_CLOSE, WM_DESTROY, WM_QUIT, WNDCLASSW, WS_EX_NOACTIVATE,
    WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_POPUP,
};
use windows::core::{PCWSTR, w};

use crate::config::RenderingOptions;
use crate::display::Display;
use crate::gpu_renderer::{
    GpuRendererConfig, GpuRendererError, GpuRendererEvent, GpuRendererIdentity, run_gpu_renderer,
};
use crate::input::{InputGuard, flush_movement, handle_message};

mod taskbar_guard;

pub(crate) const START_TIMEOUT: Duration = Duration::from_secs(10);
const RECOVERY_FIRST_FRAME_TIMEOUT: Duration = Duration::from_secs(30);
const START_CANCEL_POLL_INTERVAL: Duration = Duration::from_millis(50);
static WINDOW_CLASS_SEQUENCE: AtomicU64 = AtomicU64::new(1);

fn log_lifecycle(detail: impl std::fmt::Display) {
    crate::diagnostics::log(
        crate::diagnostics::Level::Info,
        "renderer",
        "lifecycle",
        None,
        format!(
            "pid={} tid={} thread={:?} {detail}",
            std::process::id(),
            unsafe { GetCurrentThreadId() },
            thread::current().id(),
        ),
    );
}

#[derive(Clone, Debug)]
pub enum RendererEvent {
    Fps(u32),
    TopologyLost,
    Failed(String),
}

pub type RendererReporter = Arc<dyn Fn(RendererEvent) + Send + Sync + 'static>;

pub struct Renderer {
    stop: Arc<AtomicBool>,
    wake_event: Arc<OwnedHandle>,
    thread: Option<JoinHandle<Result<(), String>>>,
}

#[derive(Debug, Eq, PartialEq)]
enum StartWait {
    Ready,
    Failed(String),
    Retryable(String),
    Cancelled,
    TimedOut,
    Disconnected,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum RendererStartError {
    Cancelled { cleanup_error: Option<String> },
    Failed(String),
    Retryable(String),
}

impl RendererStartError {
    fn into_message(self) -> String {
        match self {
            Self::Cancelled {
                cleanup_error: None,
            } => "renderer startup was cancelled".into(),
            Self::Cancelled {
                cleanup_error: Some(error),
            } => format!("renderer startup was cancelled; cleanup was incomplete: {error}"),
            Self::Failed(error) => error,
            Self::Retryable(error) => error,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
enum WorkerRunError {
    Failed(String),
    Retryable(String),
}

impl WorkerRunError {
    fn to_start_error(&self) -> RendererStartError {
        match self {
            Self::Failed(error) => RendererStartError::Failed(error.clone()),
            Self::Retryable(error) => RendererStartError::Retryable(error.clone()),
        }
    }
}

impl Renderer {
    pub fn start_with_reporter(
        target: Display,
        source: Display,
        reporter: RendererReporter,
    ) -> Result<Self, String> {
        let cancel = AtomicBool::new(false);
        Self::start_with_reporter_cancellable(target, source, reporter, &cancel)
            .map_err(RendererStartError::into_message)
    }

    pub(crate) fn start_with_reporter_cancellable(
        target: Display,
        source: Display,
        reporter: RendererReporter,
        cancel: &AtomicBool,
    ) -> Result<Self, RendererStartError> {
        Self::start_with_reporter_interruptible(target, source, reporter, || {
            cancel.load(Ordering::Acquire)
        })
    }

    pub(crate) fn start_with_reporter_interruptible(
        target: Display,
        source: Display,
        reporter: RendererReporter,
        interrupted: impl Fn() -> bool,
    ) -> Result<Self, RendererStartError> {
        Self::start_with_options_interruptible(
            target,
            source,
            reporter,
            RenderingOptions::default(),
            interrupted,
        )
    }

    pub(crate) fn start_with_options_interruptible(
        target: Display,
        source: Display,
        reporter: RendererReporter,
        rendering: RenderingOptions,
        interrupted: impl Fn() -> bool,
    ) -> Result<Self, RendererStartError> {
        Self::start_with_options_until_interruptible(
            target,
            source,
            reporter,
            rendering,
            Instant::now() + START_TIMEOUT,
            interrupted,
        )
    }

    pub(crate) fn start_with_options_until_interruptible(
        target: Display,
        source: Display,
        reporter: RendererReporter,
        rendering: RenderingOptions,
        deadline: Instant,
        interrupted: impl Fn() -> bool,
    ) -> Result<Self, RendererStartError> {
        Self::start_worker(target, source, reporter, rendering, interrupted, deadline)
    }

    pub(crate) fn recover_with_options_interruptible(
        target: Display,
        source: Display,
        reporter: RendererReporter,
        rendering: RenderingOptions,
        interrupted: impl Fn() -> bool,
    ) -> Result<Self, RendererStartError> {
        // Allow a longer diagnostic window after resume, but retain a deadline.
        Self::start_worker(
            target,
            source,
            reporter,
            rendering,
            interrupted,
            Instant::now() + RECOVERY_FIRST_FRAME_TIMEOUT,
        )
    }

    fn start_worker(
        target: Display,
        source: Display,
        reporter: RendererReporter,
        rendering: RenderingOptions,
        interrupted: impl Fn() -> bool,
        deadline: Instant,
    ) -> Result<Self, RendererStartError> {
        if interrupted() {
            return Err(RendererStartError::Cancelled {
                cleanup_error: None,
            });
        }
        let first_frame_timeout = deadline.saturating_duration_since(Instant::now());
        if first_frame_timeout.is_zero() {
            return Err(classify_start_timeout(first_frame_timeout, Ok(())));
        }
        let source_rect = source.rect;
        let target_rect = target.rect;
        let stop = Arc::new(AtomicBool::new(false));
        let worker_stop = Arc::clone(&stop);
        let wake_event = Arc::new(create_wake_event().map_err(RendererStartError::Failed)?);
        let worker_wake_event = Arc::clone(&wake_event);
        let (ready_tx, ready_rx) = mpsc::channel();
        let worker_reporter = Arc::clone(&reporter);
        let thread = thread::spawn(move || {
            log_lifecycle(format_args!(
                "owner enter source={source:?} target={target:?}"
            ));
            let result = run(
                target,
                source,
                rendering,
                &worker_stop,
                &worker_wake_event,
                &ready_tx,
                &worker_reporter,
            );
            log_lifecycle(format_args!("owner return result={result:?}"));
            if let Err(error) = &result {
                let _ = ready_tx.send(Err(error.to_start_error()));
            }
            if let Err(WorkerRunError::Failed(error)) = &result {
                worker_reporter(RendererEvent::Failed(error.clone()));
            }
            match result {
                Ok(()) => Ok(()),
                Err(WorkerRunError::Retryable(_)) => Ok(()),
                Err(WorkerRunError::Failed(error)) => Err(error),
            }
        });

        let started = Instant::now();
        log_lifecycle(format_args!(
            "first-frame wait enter owner={:?} timeout_ms={}",
            thread.thread().id(),
            first_frame_timeout.as_millis()
        ));
        let start_result = wait_for_start(&ready_rx, &interrupted, deadline);
        log_lifecycle(format_args!(
            "first-frame wait return owner={:?} elapsed_ms={} result={start_result:?}",
            thread.thread().id(),
            started.elapsed().as_millis()
        ));
        match start_result {
            StartWait::Ready => Ok(Self {
                stop,
                wake_event,
                thread: Some(thread),
            }),
            StartWait::Retryable(error) => Err(finish_start_failure(
                RendererStartError::Retryable(error),
                abort_start(&stop, &wake_event, thread),
            )),
            StartWait::Failed(error) => Err(finish_start_failure(
                RendererStartError::Failed(error),
                abort_start(&stop, &wake_event, thread),
            )),
            StartWait::Cancelled => Err(RendererStartError::Cancelled {
                cleanup_error: abort_start(&stop, &wake_event, thread).err(),
            }),
            StartWait::TimedOut => {
                // Log before joining: a blocked GPU call can delay cleanup.
                crate::diagnostics::log(
                    crate::diagnostics::Level::Warn,
                    "renderer",
                    "first-frame-timeout",
                    None,
                    format!(
                        "pid={} timeout_ms={} source={:?} target={:?}; requesting stop",
                        std::process::id(),
                        first_frame_timeout.as_millis(),
                        source_rect,
                        target_rect,
                    ),
                );
                Err(classify_start_timeout(
                    first_frame_timeout,
                    abort_start(&stop, &wake_event, thread),
                ))
            }
            StartWait::Disconnected => {
                let cleanup = abort_start(&stop, &wake_event, thread)
                    .err()
                    .map(|error| format!("; cleanup was incomplete: {error}"))
                    .unwrap_or_default();
                Err(RendererStartError::Failed(format!(
                    "renderer exited before drawing its first frame{cleanup}"
                )))
            }
        }
    }

    pub fn request_stop(&self) {
        log_lifecycle(format_args!(
            "request-stop owner={:?}",
            self.thread.as_ref().map(|worker| worker.thread().id())
        ));
        self.stop.store(true, Ordering::Release);
        signal_event(&self.wake_event);
    }

    #[cfg(test)]
    pub(crate) fn startup_test_ready() -> Self {
        Self {
            stop: Arc::new(AtomicBool::new(false)),
            wake_event: Arc::new(create_wake_event().unwrap()),
            thread: None,
        }
    }

    pub fn stop(&mut self) -> Result<(), String> {
        let Some(thread) = self.thread.take() else {
            return Ok(());
        };
        self.request_stop();
        join_worker(thread)
    }
}

fn wait_for_start(
    ready: &mpsc::Receiver<Result<(), RendererStartError>>,
    interrupted: impl Fn() -> bool,
    deadline: Instant,
) -> StartWait {
    loop {
        if interrupted() {
            return StartWait::Cancelled;
        }
        let now = Instant::now();
        if now >= deadline {
            return StartWait::TimedOut;
        }
        let wait = (deadline - now).min(START_CANCEL_POLL_INTERVAL);
        let result = ready.recv_timeout(wait);
        if interrupted() {
            return StartWait::Cancelled;
        }
        if Instant::now() >= deadline {
            return StartWait::TimedOut;
        }
        match result {
            Ok(Ok(())) => return StartWait::Ready,
            Ok(Err(RendererStartError::Retryable(error))) => {
                return StartWait::Retryable(error);
            }
            Ok(Err(RendererStartError::Failed(error))) => {
                return StartWait::Failed(error);
            }
            Ok(Err(RendererStartError::Cancelled { .. })) => return StartWait::Cancelled,
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                return StartWait::Disconnected;
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
    }
}

fn finish_start_failure(
    cause: RendererStartError,
    cleanup: Result<(), String>,
) -> RendererStartError {
    match cleanup {
        Ok(()) => cause,
        Err(ref error) if matches!(&cause, RendererStartError::Failed(message) if message == error) =>
        {
            // The worker also returns the original drawing failure. It is not
            // an additional cleanup failure when both channels report it.
            cause
        }
        Err(error) => RendererStartError::Failed(format!(
            "{}; renderer startup cleanup failed: {error}",
            cause.into_message()
        )),
    }
}

fn classify_start_timeout(timeout: Duration, cleanup: Result<(), String>) -> RendererStartError {
    let message = format!(
        "renderer did not complete its first frame within the remaining {} ms of its startup budget; see renderer/first-frame logs for the last stage",
        timeout.as_millis()
    );
    match cleanup {
        Ok(()) => RendererStartError::Failed(message),
        Err(error) => RendererStartError::Failed(format!("{message}; cleanup failed: {error}")),
    }
}

fn abort_start(
    stop: &AtomicBool,
    wake_event: &OwnedHandle,
    thread: JoinHandle<Result<(), String>>,
) -> Result<(), String> {
    log_lifecycle(format_args!("abort-start owner={:?}", thread.thread().id()));
    stop.store(true, Ordering::Release);
    signal_event(wake_event);
    join_worker(thread)
}

fn join_worker(thread: JoinHandle<Result<(), String>>) -> Result<(), String> {
    // The worker owns HWND, input and D3D resources until it exits. Elapsed
    // time cannot tell us whether those resources were released successfully.
    let owner = thread.thread().id();
    let started = Instant::now();
    log_lifecycle(format_args!("join enter owner={owner:?}"));
    let result = thread
        .join()
        .map_err(|_| "renderer thread panicked while stopping".to_string())
        .and_then(|result| result);
    log_lifecycle(format_args!(
        "join return owner={owner:?} elapsed_ms={} result={result:?}",
        started.elapsed().as_millis()
    ));
    result
}

impl Drop for Renderer {
    fn drop(&mut self) {
        let _ = self.stop();
    }
}

fn run(
    target: Display,
    source: Display,
    rendering: RenderingOptions,
    stop: &AtomicBool,
    wake_event: &OwnedHandle,
    ready: &mpsc::Sender<Result<(), RendererStartError>>,
    reporter: &RendererReporter,
) -> Result<(), WorkerRunError> {
    let identity = GpuRendererIdentity {
        source_device_name: source.device_name.clone(),
        source_id: source.id.clone(),
        source_connector_index: source.connector_index,
        target_device_name: target.device_name.clone(),
        target_id: target.id.clone(),
        target_rect: target.rect,
    };
    let target_refresh_millihz = if target.refresh_numerator == 0 || target.refresh_denominator == 0
    {
        60_000
    } else {
        (u64::from(target.refresh_numerator) * 1000 / u64::from(target.refresh_denominator)) as u32
    };
    let target = target.rect;
    let source = source.rect;
    let instance = unsafe { GetModuleHandleW(None) }
        .map_err(|error| WorkerRunError::Failed(format!("GetModuleHandleW failed: {error}")))?;
    let instance = HINSTANCE(instance.0);
    let class_name_storage =
        window_class_name(WINDOW_CLASS_SEQUENCE.fetch_add(1, Ordering::Relaxed));
    let class_name = PCWSTR(class_name_storage.as_ptr());
    let class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(window_proc),
        hInstance: instance,
        lpszClassName: class_name,
        ..Default::default()
    };
    if unsafe { RegisterClassW(&class) } == 0 {
        return Err(WorkerRunError::Failed(format!(
            "RegisterClassW failed: {}",
            windows::core::Error::from_thread()
        )));
    }

    let width = target.right - target.left;
    let height = target.bottom - target.top;
    log_lifecycle("CreateWindowExW enter");
    let window = unsafe {
        CreateWindowExW(
            WINDOW_EX_STYLE(WS_EX_TOPMOST.0 | WS_EX_TOOLWINDOW.0 | WS_EX_NOACTIVATE.0),
            class_name,
            w!("SBMS"),
            WS_POPUP,
            target.left,
            target.top,
            width,
            height,
            None,
            None,
            Some(instance),
            None,
        )
    }
    .map_err(|error| {
        unsafe {
            let _ = UnregisterClassW(class_name, Some(instance));
        }
        WorkerRunError::Failed(format!("CreateWindowExW failed: {error}"))
    })?;
    log_lifecycle(format_args!(
        "CreateWindowExW return window={window:?}; ShowWindow enter"
    ));
    unsafe {
        let _ = ShowWindow(window, SW_SHOW);
    }

    log_lifecycle(format_args!(
        "window={window:?} ShowWindow return; InputGuard::start enter"
    ));

    let input = match InputGuard::start(window, target, source, instance) {
        Ok(input) => input,
        Err(error) => {
            unsafe {
                let _ = DestroyWindow(window);
                let _ = UnregisterClassW(class_name, Some(instance));
            }
            return Err(WorkerRunError::Failed(error));
        }
    };
    log_lifecycle(format_args!(
        "window={window:?} InputGuard::start return; TaskbarGuard::new enter"
    ));
    let taskbar_guard = match taskbar_guard::TaskbarGuard::new(window) {
        Ok(guard) => guard,
        Err(error) => {
            drop(input);
            unsafe {
                let _ = DestroyWindow(window);
                let _ = UnregisterClassW(class_name, Some(instance));
            }
            return Err(WorkerRunError::Failed(error));
        }
    };
    log_lifecycle(format_args!("window={window:?} TaskbarGuard::new return"));
    let result = thread::scope(|scope| {
        let (draw_done_sender, draw_done_rx) = mpsc::sync_channel(1);
        let window_value = window.0 as usize;
        let draw_thread = scope.spawn(move || {
            // Locals drop in reverse order: disconnect the result channel
            // before waking the owner, including when drawing panics.
            let _wake = WakeOnDrop(wake_event);
            let draw_done_tx = draw_done_sender;
            let window = HWND(window_value as *mut _);
            log_lifecycle(format_args!("draw enter window={window:?}"));
            let result = draw_frames(
                GpuRendererConfig {
                    target_window: window,
                    target_width: width as u32,
                    target_height: height as u32,
                    source_rect: source,
                    rendering,
                    target_refresh_millihz,
                },
                &identity,
                stop,
                ready,
                reporter,
            );
            log_lifecycle(format_args!(
                "draw return window={window:?} result={result:?}"
            ));
            let _ = draw_done_tx.send(result);
        });
        log_lifecycle(format_args!(
            "message-pump enter window={window:?} draw={:?}",
            draw_thread.thread().id()
        ));
        let draw_result = wait_for_draw(&draw_done_rx, stop, wake_event);
        log_lifecycle(format_args!(
            "message-pump return window={window:?} result={draw_result:?}; draw join enter"
        ));
        draw_thread
            .join()
            .map_err(|_| WorkerRunError::Failed("renderer draw worker panicked".to_string()))?;
        log_lifecycle(format_args!("draw join return window={window:?}"));
        draw_result
    });
    log_lifecycle(format_args!("window={window:?} TaskbarGuard drop enter"));
    drop(taskbar_guard);
    log_lifecycle(format_args!(
        "window={window:?} TaskbarGuard drop return; InputGuard drop enter"
    ));
    drop(input);
    log_lifecycle(format_args!(
        "window={window:?} InputGuard drop return; DestroyWindow enter"
    ));
    let cleanup = unsafe {
        // WM_CLOSE may already have destroyed it on this same owner thread.
        let destroyed = if IsWindow(Some(window)).as_bool() {
            DestroyWindow(window)
        } else {
            Ok(())
        };
        log_lifecycle(format_args!(
            "window={window:?} DestroyWindow return result={destroyed:?}; UnregisterClassW enter"
        ));
        let unregistered = UnregisterClassW(class_name, Some(instance));
        log_lifecycle(format_args!(
            "window={window:?} UnregisterClassW return result={unregistered:?}"
        ));
        destroyed.and(unregistered)
    };
    match cleanup {
        Ok(()) => result,
        Err(error) => Err(WorkerRunError::Failed(format!(
            "renderer window cleanup failed: {error}; drawing result={result:?}"
        ))),
    }
}

struct WakeOnDrop<'a>(&'a OwnedHandle);

impl Drop for WakeOnDrop<'_> {
    fn drop(&mut self) {
        signal_event(self.0);
    }
}

fn wait_for_draw(
    draw_done: &mpsc::Receiver<Result<(), WorkerRunError>>,
    stop: &AtomicBool,
    wake_event: &OwnedHandle,
) -> Result<(), WorkerRunError> {
    loop {
        match draw_done.try_recv() {
            Ok(result) => return result,
            Err(mpsc::TryRecvError::Disconnected) => {
                return Err(WorkerRunError::Failed(
                    "renderer draw worker exited without a result".to_string(),
                ));
            }
            Err(mpsc::TryRecvError::Empty) => {}
        }
        // DXGI can synchronously call the HWND owner even while drawing stops.
        // Keep servicing that owner until the draw worker releases its resources.
        if !pump_messages() {
            stop.store(true, Ordering::Release);
            continue;
        }
        if !stop.load(Ordering::Acquire) {
            flush_movement();
        }
        if let Err(error) = wait_for_message(wake_event) {
            stop.store(true, Ordering::Release);
            let _ = draw_done.recv();
            return Err(WorkerRunError::Failed(error));
        }
    }
}

fn create_wake_event() -> Result<OwnedHandle, String> {
    let handle = unsafe { CreateEventW(None, false, false, PCWSTR::null()) }
        .map_err(|error| format!("CreateEventW(renderer wake) failed: {error}"))?;
    Ok(unsafe { OwnedHandle::from_raw_handle(handle.0) })
}

fn wait_for_message(wake_event: &OwnedHandle) -> Result<(), String> {
    let handle = HANDLE(wake_event.as_raw_handle());
    let result = unsafe {
        MsgWaitForMultipleObjectsEx(
            Some(std::slice::from_ref(&handle)),
            INFINITE,
            QS_ALLINPUT,
            MWMO_INPUTAVAILABLE,
        )
    };
    if result == WAIT_FAILED {
        return Err(format!(
            "MsgWaitForMultipleObjectsEx failed: {}",
            windows::core::Error::from_thread()
        ));
    }
    Ok(())
}

fn signal_event(wake_event: &OwnedHandle) {
    let _ = unsafe { SetEvent(HANDLE(wake_event.as_raw_handle())) };
}

fn draw_frames(
    config: GpuRendererConfig,
    identity: &GpuRendererIdentity,
    stop: &AtomicBool,
    ready: &mpsc::Sender<Result<(), RendererStartError>>,
    reporter: &RendererReporter,
) -> Result<(), WorkerRunError> {
    let mut first_frame = true;
    let mut fps = FpsSampler::default();
    let result = run_gpu_renderer(config, identity, stop, |event| {
        match event {
            GpuRendererEvent::Presented => {
                fps.record_present(Instant::now());
                if first_frame {
                    let sent = ready.send(Ok(()));
                    log_lifecycle(format_args!(
                        "first-frame notify window={:?} result={sent:?}",
                        config.target_window
                    ));
                    sent.map_err(|_| "mapping start was cancelled".to_string())?;
                    first_frame = false;
                }
            }
            GpuRendererEvent::CaptureChecked => {
                if let Some(fps) = fps.sample(Instant::now()) {
                    reporter(RendererEvent::Fps(fps));
                }
            }
        }
        Ok(())
    });
    finish_gpu_renderer(result, reporter, first_frame, ready)
}

fn finish_gpu_renderer(
    result: Result<(), GpuRendererError>,
    reporter: &RendererReporter,
    first_frame: bool,
    ready: &mpsc::Sender<Result<(), RendererStartError>>,
) -> Result<(), WorkerRunError> {
    match result {
        Ok(()) => Ok(()),
        Err(error) if first_frame => {
            let message = error.to_string();
            if error.is_retryable() {
                let _ = ready.send(Err(RendererStartError::Retryable(message.clone())));
                Err(WorkerRunError::Retryable(message))
            } else {
                let _ = ready.send(Err(RendererStartError::Failed(message.clone())));
                Err(WorkerRunError::Failed(message))
            }
        }
        Err(error) if error.is_retryable() => {
            reporter(RendererEvent::TopologyLost);
            Ok(())
        }
        Err(error) => Err(WorkerRunError::Failed(error.to_string())),
    }
}

#[derive(Default)]
struct FpsSampler {
    started: Option<Instant>,
    frames: u32,
}

impl FpsSampler {
    fn record_present(&mut self, now: Instant) {
        self.started.get_or_insert(now);
        self.frames = self.frames.saturating_add(1);
    }

    fn sample(&mut self, now: Instant) -> Option<u32> {
        let elapsed = now.duration_since(self.started?);
        if elapsed < Duration::from_secs(1) {
            return None;
        }
        let fps = (self.frames as f64 / elapsed.as_secs_f64()).round() as u32;
        self.frames = 0;
        self.started = Some(now);
        Some(fps)
    }
}

fn window_class_name(sequence: u64) -> Vec<u16> {
    format!("SBMSMirrorWindow-{}-{sequence}", std::process::id())
        .encode_utf16()
        .chain(Some(0))
        .collect()
}

fn pump_messages() -> bool {
    let mut message = MSG::default();
    while unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool() {
        if message.message == WM_QUIT {
            return false;
        }
        unsafe {
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
    true
}

unsafe extern "system" fn window_proc(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if let Some(result) = handle_message(message, wparam, lparam) {
        return result;
    }
    match message {
        WM_CLOSE => {
            unsafe {
                let _ = DestroyWindow(window);
            }
            LRESULT(0)
        }
        WM_DESTROY => {
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(window, message, wparam, lparam) },
    }
}

#[cfg(test)]
mod tests {
    use super::{
        FpsSampler, Renderer, RendererEvent, RendererReporter, RendererStartError, StartWait,
        WakeOnDrop, WorkerRunError, abort_start, classify_start_timeout, create_wake_event,
        finish_gpu_renderer, finish_start_failure, signal_event, wait_for_draw, wait_for_start,
    };
    use crate::gpu_renderer::GpuRendererError;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::mpsc;
    use std::thread;
    use std::time::{Duration, Instant};

    #[test]
    fn fps_sampling_counts_presents_across_active_and_idle_windows() {
        let start = Instant::now();
        let mut fps = FpsSampler::default();
        assert_eq!(fps.sample(start), None);
        assert_eq!(fps.sample(start + Duration::from_secs(5)), None);

        // Initialization time is not a valid capture interval. Start at the
        // first successful Present and publish at most once per full window.
        let first = start + Duration::from_secs(6);
        for frame in 0..60 {
            let now = first + Duration::from_millis(frame * 16);
            fps.record_present(now);
            assert_eq!(fps.sample(now), None);
        }
        assert_eq!(fps.sample(first + Duration::from_millis(999)), None);
        assert_eq!(fps.sample(first + Duration::from_secs(1)), Some(60));
        assert_eq!(fps.sample(first + Duration::from_secs(1)), None);

        // Capture timeouts and pointer-only iterations keep sampling even
        // though neither has submitted a new frame.
        assert_eq!(fps.sample(first + Duration::from_millis(1999)), None);
        assert_eq!(fps.sample(first + Duration::from_secs(2)), Some(0));
        assert_eq!(fps.sample(first + Duration::from_secs(3)), Some(0));

        // Repeated successful cached presents count exactly like new pixels.
        for frame in 0..120 {
            let now =
                first + Duration::from_secs(3) + Duration::from_nanos(frame * 1_000_000_000 / 120);
            fps.record_present(now);
            assert_eq!(fps.sample(now), None);
        }
        assert_eq!(fps.sample(first + Duration::from_secs(4)), Some(120));

        // Scheduling can make a window longer than one second; use its actual
        // duration and consume it once, without replaying missed samples.
        for frame in 0..90 {
            fps.record_present(first + Duration::from_secs(4) + Duration::from_millis(frame * 16));
        }
        let delayed = first + Duration::from_millis(5500);
        assert_eq!(fps.sample(delayed), Some(60));
        assert_eq!(fps.sample(delayed), None);
        assert_eq!(fps.sample(delayed + Duration::from_secs(1)), Some(0));
        fps.record_present(delayed + Duration::from_millis(1500));
        assert_eq!(fps.sample(delayed + Duration::from_secs(2)), Some(1));
        assert_eq!(fps.sample(delayed + Duration::from_secs(3)), Some(0));
    }

    #[test]
    fn renderer_failure_classification_preserves_startup_and_running_boundaries() {
        for (error, retryable) in [
            (GpuRendererError::AccessLost("access lost".into()), true),
            (
                GpuRendererError::DuplicateOutputAccessDenied("access denied".into()),
                true,
            ),
            (
                GpuRendererError::OutputUnavailable("source not enumerated".into()),
                true,
            ),
            (
                GpuRendererError::Failure("texture allocation failed".into()),
                false,
            ),
        ] {
            let message = error.to_string();
            let reporter: RendererReporter = Arc::new(|_| panic!("no renderer is active yet"));
            let (ready_tx, ready_rx) = mpsc::channel();
            let result = finish_gpu_renderer(Err(error), &reporter, true, &ready_tx);
            let expected = if retryable {
                assert!(matches!(result, Err(WorkerRunError::Retryable(_))));
                StartWait::Retryable(message)
            } else {
                assert!(matches!(result, Err(WorkerRunError::Failed(_))));
                StartWait::Failed(message)
            };
            assert_eq!(
                wait_for_start(&ready_rx, || false, Instant::now() + Duration::from_secs(1)),
                expected
            );
        }

        let (event_tx, event_rx) = mpsc::channel();
        let reporter: RendererReporter = Arc::new(move |event| {
            event_tx.send(event).unwrap();
        });
        let (ready_tx, _ready_rx) = mpsc::channel();
        let error = GpuRendererError::AccessLost(
            "IDXGIOutputDuplication::AcquireNextFrame failed: HRESULT(0x887A0026)".into(),
        );

        assert!(finish_gpu_renderer(Err(error), &reporter, false, &ready_tx).is_ok());
        assert!(matches!(
            event_rx.recv().unwrap(),
            RendererEvent::TopologyLost
        ));
    }

    #[test]
    fn start_wait_prioritizes_cancellation_and_distinguishes_terminal_states() {
        let (ready_tx, ready_rx) = mpsc::channel();
        ready_tx.send(Ok(())).unwrap();
        assert_eq!(
            wait_for_start(
                &ready_rx,
                || true,
                Instant::now() + super::RECOVERY_FIRST_FRAME_TIMEOUT
            ),
            StartWait::Cancelled
        );

        let (ready_tx, ready_rx) = mpsc::channel();
        ready_tx.send(Ok(())).unwrap();
        let checked = AtomicBool::new(false);
        assert_eq!(
            wait_for_start(
                &ready_rx,
                || checked.swap(true, Ordering::AcqRel),
                Instant::now() + super::RECOVERY_FIRST_FRAME_TIMEOUT,
            ),
            StartWait::Cancelled
        );

        let (_ready_tx, ready_rx) = mpsc::channel();
        assert_eq!(
            wait_for_start(
                &ready_rx,
                || true,
                Instant::now() + super::RECOVERY_FIRST_FRAME_TIMEOUT
            ),
            StartWait::Cancelled
        );

        let (_ready_tx, ready_rx) = mpsc::channel();
        assert_eq!(
            wait_for_start(
                &ready_rx,
                || false,
                Instant::now() + Duration::from_millis(1)
            ),
            StartWait::TimedOut
        );

        let (ready_tx, ready_rx) = mpsc::channel::<Result<(), RendererStartError>>();
        drop(ready_tx);
        assert_eq!(
            wait_for_start(&ready_rx, || false, Instant::now() + Duration::from_secs(1)),
            StartWait::Disconnected
        );

        let (_ready_tx, ready_rx) = mpsc::channel();
        let cancel = Arc::new(AtomicBool::new(false));
        let worker_cancel = Arc::clone(&cancel);
        let (waiting_tx, waiting_rx) = mpsc::channel();
        let worker = thread::spawn(move || {
            let announced = std::cell::Cell::new(false);
            wait_for_start(
                &ready_rx,
                || {
                    if !announced.replace(true) {
                        waiting_tx.send(()).unwrap();
                        return false;
                    }
                    worker_cancel.load(Ordering::Acquire)
                },
                Instant::now() + super::RECOVERY_FIRST_FRAME_TIMEOUT,
            )
        });
        waiting_rx.recv().unwrap();
        cancel.store(true, Ordering::Release);
        assert_eq!(worker.join().unwrap(), StartWait::Cancelled);

        let (ready_tx, ready_rx) = mpsc::channel();
        ready_tx.send(Ok(())).unwrap();
        let expired = Instant::now();
        assert_eq!(
            wait_for_start(&ready_rx, || false, expired),
            StartWait::TimedOut
        );
        assert_eq!(
            wait_for_start(&ready_rx, || true, expired),
            StartWait::Cancelled
        );
    }

    #[test]
    fn first_frame_timeout_reports_its_budget_and_preserves_cleanup_failure() {
        for timeout in [super::START_TIMEOUT, super::RECOVERY_FIRST_FRAME_TIMEOUT] {
            let error = classify_start_timeout(timeout, Ok(()));
            assert!(matches!(error, RendererStartError::Failed(message)
                if message.contains(&format!("{} ms", timeout.as_millis()))));
        }
        let error = classify_start_timeout(
            super::RECOVERY_FIRST_FRAME_TIMEOUT,
            Err("D3D11 device removed".into()),
        );
        assert!(matches!(error, RendererStartError::Failed(message)
            if message.contains("D3D11 device removed")));
    }

    #[test]
    fn recovery_keeps_the_same_capture_until_a_late_first_frame() {
        let (ready_tx, ready_rx) = mpsc::channel();
        let worker = thread::spawn(move || {
            thread::sleep(super::START_TIMEOUT + Duration::from_millis(20));
            ready_tx.send(Ok(())).unwrap();
        });
        assert_eq!(
            wait_for_start(
                &ready_rx,
                || false,
                Instant::now() + super::RECOVERY_FIRST_FRAME_TIMEOUT
            ),
            StartWait::Ready
        );
        worker.join().unwrap();
    }

    #[test]
    fn stopping_draw_worker_can_synchronously_call_its_window_owner() {
        use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
        use windows::Win32::UI::WindowsAndMessaging::{
            CreateWindowExW, DefWindowProcW, DestroyWindow, GWLP_WNDPROC, HWND_MESSAGE,
            SMTO_ABORTIFHUNG, SendMessageTimeoutW, SetWindowLongPtrW, WINDOW_EX_STYLE,
            WINDOW_STYLE, WM_APP,
        };
        use windows::core::w;

        unsafe extern "system" fn owner_proc(
            hwnd: HWND,
            message: u32,
            wparam: WPARAM,
            lparam: LPARAM,
        ) -> LRESULT {
            if message == WM_APP {
                return LRESULT(17);
            }
            unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
        }
        struct OwnerWindow(HWND);
        impl Drop for OwnerWindow {
            fn drop(&mut self) {
                let _ = unsafe { DestroyWindow(self.0) };
            }
        }

        let owner = OwnerWindow(unsafe {
            CreateWindowExW(
                WINDOW_EX_STYLE::default(),
                w!("STATIC"),
                w!("SBMS renderer owner callback test"),
                WINDOW_STYLE::default(),
                0,
                0,
                1,
                1,
                Some(HWND_MESSAGE),
                None,
                None,
                None,
            )
            .unwrap()
        });
        unsafe {
            SetWindowLongPtrW(owner.0, GWLP_WNDPROC, owner_proc as *const () as isize);
        }
        let owner_value = owner.0.0 as usize;
        let stop = AtomicBool::new(true);
        let wake_event = Arc::new(create_wake_event().unwrap());
        let draw_wake_event = Arc::clone(&wake_event);
        let (draw_done_sender, draw_done_rx) = mpsc::sync_channel(1);
        let draw = thread::spawn(move || {
            let _wake = WakeOnDrop(&draw_wake_event);
            let draw_done_tx = draw_done_sender;
            let mut reply = 0;
            // The timeout bounds a regression: the old non-pumping stop path
            // must fail this assertion, rather than deadlock the test runner.
            let sent = unsafe {
                SendMessageTimeoutW(
                    HWND(owner_value as *mut _),
                    WM_APP,
                    WPARAM(0),
                    LPARAM(0),
                    SMTO_ABORTIFHUNG,
                    3000,
                    Some(&mut reply),
                )
            };
            let result = if sent.0 != 0 && reply == 17 {
                Ok(())
            } else {
                Err(WorkerRunError::Failed("owner callback was blocked".into()))
            };
            draw_done_tx.send(result).unwrap();
        });

        let result = wait_for_draw(&draw_done_rx, &stop, &wake_event);
        draw.join().unwrap();
        assert_eq!(result, Ok(()));
    }

    #[test]
    fn panicked_draw_worker_disconnects_before_waking_its_owner() {
        let wake_event = Arc::new(create_wake_event().unwrap());
        let owner_wake_event = Arc::clone(&wake_event);
        let draw_wake_event = Arc::clone(&wake_event);
        let (draw_done_sender, draw_done_rx) = mpsc::sync_channel(1);
        let (waiting_tx, waiting_rx) = mpsc::sync_channel(1);
        let (result_tx, result_rx) = mpsc::sync_channel(1);
        let owner = thread::spawn(move || {
            let stop = AtomicBool::new(true);
            waiting_tx.send(()).unwrap();
            result_tx
                .send(wait_for_draw(&draw_done_rx, &stop, &owner_wake_event))
                .unwrap();
        });
        waiting_rx.recv().unwrap();
        let draw = thread::spawn(move || {
            let _wake = WakeOnDrop(&draw_wake_event);
            let _draw_done_tx = draw_done_sender;
            panic!("fixture draw worker failure");
        });
        assert!(draw.join().is_err());
        let result = result_rx.recv_timeout(Duration::from_secs(3));
        if result.is_err() {
            // Release a waiter after a lost-wake regression before failing.
            signal_event(&wake_event);
        }
        owner.join().unwrap();
        assert_eq!(
            result.expect("panicked draw worker did not wake its owner"),
            Err(WorkerRunError::Failed(
                "renderer draw worker exited without a result".into()
            ))
        );
    }

    #[test]
    fn slow_successful_stop_is_not_a_cleanup_failure() {
        let stop = Arc::new(AtomicBool::new(false));
        let worker_stop = Arc::clone(&stop);
        let wake_event = Arc::new(create_wake_event().unwrap());
        let thread = thread::spawn(move || {
            while !worker_stop.load(Ordering::Acquire) {
                thread::yield_now();
            }
            // Reproduce cleanup that completes after the former two-second
            // threshold. Its outcome, not elapsed time, determines success.
            thread::sleep(Duration::from_millis(2100));
            Ok(())
        });
        let mut renderer = Renderer {
            stop,
            wake_event,
            thread: Some(thread),
        };

        renderer.stop().unwrap();
        assert!(renderer.thread.is_none());
        renderer.stop().unwrap();
    }

    #[test]
    fn abort_start_preserves_worker_failure_and_panic() {
        let stop = AtomicBool::new(false);
        let wake_event = create_wake_event().unwrap();
        let thread = thread::spawn(move || Err("D3D11 device removed".into()));
        assert_eq!(
            abort_start(&stop, &wake_event, thread),
            Err("D3D11 device removed".into())
        );

        let stop = AtomicBool::new(false);
        let wake_event = create_wake_event().unwrap();
        let thread = thread::spawn(|| panic!("fixture worker failure"));
        assert_eq!(
            abort_start(&stop, &wake_event, thread),
            Err("renderer thread panicked while stopping".into())
        );

        let retryable = RendererStartError::Retryable("AcquireNextFrame ACCESS_LOST".into());
        assert_eq!(finish_start_failure(retryable.clone(), Ok(())), retryable);
        assert_eq!(
            finish_start_failure(
                RendererStartError::Failed("texture allocation failed".into()),
                Err("texture allocation failed".into()),
            ),
            RendererStartError::Failed("texture allocation failed".into()),
        );
        let joined = Arc::new(AtomicBool::new(false));
        let worker_joined = Arc::clone(&joined);
        let thread = thread::spawn(move || {
            worker_joined.store(true, Ordering::Release);
            Err("DestroyWindow failed".into())
        });
        let result = finish_start_failure(retryable, abort_start(&stop, &wake_event, thread));
        assert!(joined.load(Ordering::Acquire));
        assert!(matches!(result, RendererStartError::Failed(error)
            if error.contains("ACCESS_LOST") && error.contains("DestroyWindow failed")));
    }
}
