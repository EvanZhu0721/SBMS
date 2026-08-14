use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{
    HANDLE, HINSTANCE, HWND, LPARAM, LRESULT, RECT, WAIT_FAILED, WPARAM,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::{CreateEventW, INFINITE, SetEvent};
use windows::Win32::UI::WindowsAndMessaging::{
    CS_HREDRAW, CS_VREDRAW, CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, MSG,
    MWMO_INPUTAVAILABLE, MsgWaitForMultipleObjectsEx, PM_REMOVE, PeekMessageW, PostQuitMessage,
    QS_ALLINPUT, RegisterClassW, SW_SHOW, ShowWindow, TranslateMessage, UnregisterClassW,
    WINDOW_EX_STYLE, WM_CLOSE, WM_DESTROY, WM_QUIT, WNDCLASSW, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
    WS_EX_TOPMOST, WS_POPUP,
};
use windows::core::{PCWSTR, w};

use crate::display::Display;
use crate::gpu_renderer::{GpuRendererConfig, GpuRendererError, run_gpu_renderer};
use crate::input::{InputGuard, flush_movement, handle_message};

const START_TIMEOUT: Duration = Duration::from_secs(10);
const START_CANCEL_POLL_INTERVAL: Duration = Duration::from_millis(50);
#[cfg(not(test))]
const STOP_TIMEOUT: Duration = Duration::from_secs(2);
#[cfg(test)]
const STOP_TIMEOUT: Duration = Duration::from_millis(20);
static WINDOW_CLASS_SEQUENCE: AtomicU64 = AtomicU64::new(1);

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
    done: mpsc::Receiver<Result<(), String>>,
    thread: Option<JoinHandle<()>>,
}

#[derive(Debug, Eq, PartialEq)]
enum StartWait {
    Ready,
    Failed(String),
    Cancelled,
    TimedOut,
    Disconnected,
}

#[derive(Debug, Eq, PartialEq)]
pub(crate) enum RendererStartError {
    Cancelled { cleanup_error: Option<String> },
    Failed(String),
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
        let stop = Arc::new(AtomicBool::new(false));
        let worker_stop = Arc::clone(&stop);
        let wake_event = Arc::new(create_wake_event().map_err(RendererStartError::Failed)?);
        let worker_wake_event = Arc::clone(&wake_event);
        let (ready_tx, ready_rx) = mpsc::channel();
        let (done_tx, done_rx) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || {
            let result = run(
                target.rect,
                source.rect,
                &worker_stop,
                &worker_wake_event,
                &ready_tx,
                &reporter,
            );
            if let Err(error) = &result {
                let _ = ready_tx.send(Err(error.clone()));
            }
            let failure = result.as_ref().err().cloned();
            if let Some(error) = failure {
                reporter(RendererEvent::Failed(error));
            }
            let _ = done_tx.send(result);
        });

        match wait_for_start(&ready_rx, cancel, START_TIMEOUT) {
            StartWait::Ready => Ok(Self {
                stop,
                wake_event,
                done: done_rx,
                thread: Some(thread),
            }),
            StartWait::Failed(error) => {
                stop.store(true, Ordering::Release);
                signal_event(&wake_event);
                let _ = thread.join();
                Err(RendererStartError::Failed(error))
            }
            StartWait::Cancelled => Err(RendererStartError::Cancelled {
                cleanup_error: abort_start(&stop, &wake_event, &done_rx, thread).err(),
            }),
            StartWait::TimedOut => {
                let cleanup = abort_start(&stop, &wake_event, &done_rx, thread)
                    .err()
                    .map(|error| format!("; cleanup was incomplete: {error}"))
                    .unwrap_or_default();
                Err(RendererStartError::Failed(format!(
                    "renderer did not receive and draw a first frame within 10 seconds{cleanup}"
                )))
            }
            StartWait::Disconnected => {
                let cleanup = abort_start(&stop, &wake_event, &done_rx, thread)
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
        self.stop.store(true, Ordering::Release);
        signal_event(&self.wake_event);
    }

    pub fn stop(&mut self) -> Result<(), String> {
        if self.thread.is_none() {
            return Ok(());
        }
        self.request_stop();
        let (result, exceeded_timeout) = match self.done.recv_timeout(STOP_TIMEOUT) {
            Ok(result) => (result, false),
            Err(mpsc::RecvTimeoutError::Timeout) => {
                // A live renderer owns HWND, input and D3D resources. Keep waiting
                // rather than detaching it and allowing the display topology to be
                // torn down underneath the worker.
                let result = self
                    .done
                    .recv()
                    .map_err(|_| "renderer stopped without reporting a result".to_string())?;
                (result, true)
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                let thread = self.thread.take().expect("renderer thread disappeared");
                thread
                    .join()
                    .map_err(|_| "renderer thread panicked".to_string())?;
                return Err("renderer stopped without reporting a result".into());
            }
        };
        let thread = self.thread.take().expect("renderer thread disappeared");
        thread
            .join()
            .map_err(|_| "renderer thread panicked".to_string())?;
        match (result, exceeded_timeout) {
            (Ok(()), false) => Ok(()),
            (Ok(()), true) => Err("renderer needed more than 2 seconds to stop".into()),
            (Err(error), false) => Err(error),
            (Err(error), true) => Err(format!(
                "renderer needed more than 2 seconds to stop; renderer failed: {error}"
            )),
        }
    }
}

fn wait_for_start(
    ready: &mpsc::Receiver<Result<(), String>>,
    cancel: &AtomicBool,
    timeout: Duration,
) -> StartWait {
    let deadline = Instant::now() + timeout;
    loop {
        match ready.try_recv() {
            Ok(Ok(())) => return StartWait::Ready,
            Ok(Err(error)) => return StartWait::Failed(error),
            Err(mpsc::TryRecvError::Disconnected) => return StartWait::Disconnected,
            Err(mpsc::TryRecvError::Empty) => {}
        }
        if cancel.load(Ordering::Acquire) {
            return StartWait::Cancelled;
        }
        let now = Instant::now();
        if now >= deadline {
            return StartWait::TimedOut;
        }
        let wait = (deadline - now).min(START_CANCEL_POLL_INTERVAL);
        match ready.recv_timeout(wait) {
            Ok(Ok(())) => return StartWait::Ready,
            Ok(Err(error)) => return StartWait::Failed(error),
            Err(mpsc::RecvTimeoutError::Disconnected) => return StartWait::Disconnected,
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
    }
}

fn abort_start(
    stop: &AtomicBool,
    wake_event: &OwnedHandle,
    done: &mpsc::Receiver<Result<(), String>>,
    thread: JoinHandle<()>,
) -> Result<(), String> {
    stop.store(true, Ordering::Release);
    signal_event(wake_event);
    let exceeded_timeout = match done.recv_timeout(STOP_TIMEOUT) {
        Ok(_) | Err(mpsc::RecvTimeoutError::Disconnected) => false,
        Err(mpsc::RecvTimeoutError::Timeout) => {
            // Startup failure cannot hand an unowned live renderer to the caller.
            // Wait for definitive termination before mapping rollback changes the
            // virtual display or physical topology.
            let _ = done.recv();
            true
        }
    };
    thread
        .join()
        .map_err(|_| "renderer thread panicked while stopping".to_string())?;
    if exceeded_timeout {
        Err("renderer needed more than 2 seconds to stop".into())
    } else {
        Ok(())
    }
}

impl Drop for Renderer {
    fn drop(&mut self) {
        let _ = self.stop();
    }
}

fn run(
    target: RECT,
    source: RECT,
    stop: &AtomicBool,
    wake_event: &OwnedHandle,
    ready: &mpsc::Sender<Result<(), String>>,
    reporter: &RendererReporter,
) -> Result<(), String> {
    let instance = unsafe { GetModuleHandleW(None) }
        .map_err(|error| format!("GetModuleHandleW failed: {error}"))?;
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
        return Err(format!(
            "RegisterClassW failed: {}",
            windows::core::Error::from_thread()
        ));
    }

    let width = target.right - target.left;
    let height = target.bottom - target.top;
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
        format!("CreateWindowExW failed: {error}")
    })?;
    unsafe {
        let _ = ShowWindow(window, SW_SHOW);
    }

    let input = match InputGuard::start(window, target, source, instance) {
        Ok(input) => input,
        Err(error) => {
            unsafe {
                let _ = DestroyWindow(window);
                let _ = UnregisterClassW(class_name, Some(instance));
            }
            return Err(error);
        }
    };
    let result = thread::scope(|scope| {
        let (draw_done_tx, draw_done_rx) = mpsc::sync_channel(1);
        let window_value = window.0 as usize;
        let draw_thread = scope.spawn(move || {
            let window = HWND(window_value as *mut _);
            let result = draw_frames(window, width, height, source, stop, ready, reporter);
            let _ = draw_done_tx.send(result);
            signal_event(wake_event);
        });
        let draw_result = loop {
            if let Ok(result) = draw_done_rx.try_recv() {
                break result;
            }
            if stop.load(Ordering::Acquire) {
                break draw_done_rx
                    .recv()
                    .map_err(|_| "renderer draw worker exited without a result".to_string())?;
            }
            if !pump_messages() {
                stop.store(true, Ordering::Release);
                continue;
            }
            flush_movement();
            if let Err(error) = wait_for_message(wake_event) {
                stop.store(true, Ordering::Release);
                let _ = draw_done_rx.recv();
                break Err(error);
            }
        };
        draw_thread
            .join()
            .map_err(|_| "renderer draw worker panicked".to_string())?;
        draw_result
    });
    drop(input);
    unsafe {
        let _ = DestroyWindow(window);
        let _ = UnregisterClassW(class_name, Some(instance));
    }
    result
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
    window: HWND,
    target_width: i32,
    target_height: i32,
    source: RECT,
    stop: &AtomicBool,
    ready: &mpsc::Sender<Result<(), String>>,
    reporter: &RendererReporter,
) -> Result<(), String> {
    let mut first_frame = true;
    let mut frames = 0_u32;
    let mut sample_started = Instant::now();
    let result = run_gpu_renderer(
        GpuRendererConfig {
            target_window: window,
            target_width: target_width as u32,
            target_height: target_height as u32,
            source_rect: source,
        },
        stop,
        || {
            frames = frames.saturating_add(1);
            if first_frame {
                ready
                    .send(Ok(()))
                    .map_err(|_| "mapping start was cancelled".to_string())?;
                first_frame = false;
            }
            let elapsed = sample_started.elapsed();
            if elapsed >= Duration::from_secs(1) {
                reporter(RendererEvent::Fps(measured_fps(frames, elapsed)));
                frames = 0;
                sample_started = Instant::now();
            }
            Ok(())
        },
    );
    finish_gpu_renderer(result, reporter)
}

fn finish_gpu_renderer(
    result: Result<(), GpuRendererError>,
    reporter: &RendererReporter,
) -> Result<(), String> {
    match result {
        Ok(()) => Ok(()),
        Err(GpuRendererError::AccessLost) => {
            reporter(RendererEvent::TopologyLost);
            Ok(())
        }
        Err(error) => Err(error.to_string()),
    }
}

fn measured_fps(frames: u32, elapsed: Duration) -> u32 {
    if elapsed.is_zero() {
        return 0;
    }
    ((frames as f64 / elapsed.as_secs_f64()).round()).clamp(0.0, u32::MAX as f64) as u32
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
    if let Some(result) = handle_message(message, lparam) {
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
        Renderer, RendererEvent, RendererReporter, StartWait, create_wake_event,
        finish_gpu_renderer, measured_fps, signal_event, wait_for_message, wait_for_start,
        window_class_name,
    };
    use crate::gpu_renderer::GpuRendererError;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::mpsc;
    use std::thread;
    use std::time::Duration;

    #[test]
    fn fps_uses_actual_sample_duration() {
        assert_eq!(measured_fps(60, Duration::from_secs(1)), 60);
        assert_eq!(measured_fps(90, Duration::from_millis(1500)), 60);
    }

    #[test]
    fn fps_reports_idle_and_handles_zero_duration() {
        assert_eq!(measured_fps(0, Duration::from_secs(1)), 0);
        assert_eq!(measured_fps(1, Duration::ZERO), 0);
    }

    #[test]
    fn access_lost_reports_recoverable_topology_loss() {
        let (event_tx, event_rx) = mpsc::channel();
        let reporter: RendererReporter = Arc::new(move |event| {
            event_tx.send(event).unwrap();
        });

        assert!(finish_gpu_renderer(Err(GpuRendererError::AccessLost), &reporter).is_ok());
        assert!(matches!(
            event_rx.recv().unwrap(),
            RendererEvent::TopologyLost
        ));
    }

    #[test]
    fn renderer_window_classes_are_unique_and_null_terminated() {
        let first = window_class_name(7);
        let second = window_class_name(8);
        assert_ne!(first, second);
        assert_eq!(first.last(), Some(&0));
        assert_eq!(second.last(), Some(&0));
    }

    #[test]
    fn ready_frame_wins_a_simultaneous_start_cancellation() {
        let (ready_tx, ready_rx) = mpsc::channel();
        ready_tx.send(Ok(())).unwrap();
        let cancel = AtomicBool::new(true);
        assert_eq!(
            wait_for_start(&ready_rx, &cancel, Duration::from_secs(1)),
            StartWait::Ready
        );
    }

    #[test]
    fn renderer_start_wait_distinguishes_cancel_timeout_and_disconnect() {
        let (_ready_tx, ready_rx) = mpsc::channel();
        let cancel = AtomicBool::new(true);
        assert_eq!(
            wait_for_start(&ready_rx, &cancel, Duration::from_secs(1)),
            StartWait::Cancelled
        );

        let (_ready_tx, ready_rx) = mpsc::channel();
        let cancel = AtomicBool::new(false);
        assert_eq!(
            wait_for_start(&ready_rx, &cancel, Duration::from_millis(1)),
            StartWait::TimedOut
        );

        let (ready_tx, ready_rx) = mpsc::channel::<Result<(), String>>();
        drop(ready_tx);
        assert_eq!(
            wait_for_start(&ready_rx, &cancel, Duration::from_secs(1)),
            StartWait::Disconnected
        );
    }

    #[test]
    fn event_releases_message_wait() {
        let wake_event = Arc::new(create_wake_event().unwrap());
        let waiting_event = Arc::clone(&wake_event);
        let (ready_tx, ready_rx) = mpsc::sync_channel(1);
        let (done_tx, done_rx) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || {
            ready_tx.send(()).unwrap();
            done_tx.send(wait_for_message(&waiting_event)).unwrap();
        });

        ready_rx.recv().unwrap();
        signal_event(&wake_event);
        assert_eq!(
            done_rx.recv_timeout(Duration::from_millis(200)).unwrap(),
            Ok(())
        );
        thread.join().unwrap();
    }

    #[test]
    fn stop_waits_for_actual_thread_exit_after_reporting_timeout() {
        let stop = Arc::new(AtomicBool::new(false));
        let worker_stop = Arc::clone(&stop);
        let wake_event = Arc::new(create_wake_event().unwrap());
        let (done_tx, done_rx) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || {
            while !worker_stop.load(Ordering::Acquire) {
                thread::yield_now();
            }
            thread::sleep(Duration::from_millis(40));
            done_tx.send(Ok(())).unwrap();
        });
        let mut renderer = Renderer {
            stop,
            wake_event,
            done: done_rx,
            thread: Some(thread),
        };

        let error = renderer.stop().unwrap_err();
        assert!(error.contains("needed more than"));
        assert!(renderer.thread.is_none());
    }
}
