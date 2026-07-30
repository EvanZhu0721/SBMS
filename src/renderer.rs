use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, RECT, WAIT_FAILED, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::{GetCurrentThreadId, INFINITE};
use windows::Win32::UI::WindowsAndMessaging::{
    CS_HREDRAW, CS_VREDRAW, CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, MSG,
    MWMO_INPUTAVAILABLE, MsgWaitForMultipleObjectsEx, PM_REMOVE, PeekMessageW, PostQuitMessage,
    PostThreadMessageW, QS_ALLINPUT, RegisterClassW, SW_SHOW, ShowWindow, TranslateMessage,
    UnregisterClassW, WINDOW_EX_STYLE, WM_APP, WM_CLOSE, WM_DESTROY, WM_QUIT, WNDCLASSW,
    WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_POPUP,
};
use windows::core::{PCWSTR, w};

use crate::display::Display;
use crate::gpu_renderer::{GpuRendererConfig, run_gpu_renderer};
use crate::input::{InputGuard, flush_movement, handle_message};

const START_TIMEOUT: Duration = Duration::from_secs(10);
const STOP_TIMEOUT: Duration = Duration::from_secs(2);
const WAKE_MESSAGE: u32 = WM_APP + 0x53;
static WINDOW_CLASS_SEQUENCE: AtomicU64 = AtomicU64::new(1);

#[derive(Clone, Debug)]
pub enum RendererEvent {
    Fps(u32),
    Failed(String),
}

pub type RendererReporter = Arc<dyn Fn(RendererEvent) + Send + Sync + 'static>;

pub struct Renderer {
    stop: Arc<AtomicBool>,
    thread_id: Arc<AtomicU32>,
    done: mpsc::Receiver<Result<(), String>>,
    thread: Option<JoinHandle<()>>,
}

impl Renderer {
    pub fn start_with_reporter(
        target: Display,
        source: Display,
        reporter: RendererReporter,
    ) -> Result<Self, String> {
        let stop = Arc::new(AtomicBool::new(false));
        let worker_stop = Arc::clone(&stop);
        let thread_id = Arc::new(AtomicU32::new(0));
        let worker_thread_id = Arc::clone(&thread_id);
        let (ready_tx, ready_rx) = mpsc::channel();
        let (done_tx, done_rx) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || {
            worker_thread_id.store(unsafe { GetCurrentThreadId() }, Ordering::Release);
            let result = run(target.rect, source.rect, &worker_stop, &ready_tx, &reporter);
            if let Err(error) = &result {
                let _ = ready_tx.send(Err(error.clone()));
            }
            let failure = result.as_ref().err().cloned();
            let _ = done_tx.send(result);
            if let Some(error) = failure {
                reporter(RendererEvent::Failed(error));
            }
        });

        match ready_rx.recv_timeout(START_TIMEOUT) {
            Ok(Ok(())) => Ok(Self {
                stop,
                thread_id,
                done: done_rx,
                thread: Some(thread),
            }),
            Ok(Err(error)) => {
                stop.store(true, Ordering::Release);
                wake_thread(thread_id.load(Ordering::Acquire));
                let _ = thread.join();
                Err(error)
            }
            Err(_) => {
                stop.store(true, Ordering::Release);
                wake_thread(thread_id.load(Ordering::Acquire));
                let _ = done_rx.recv_timeout(STOP_TIMEOUT);
                drop(thread);
                Err("renderer did not receive and draw a first frame within 10 seconds".into())
            }
        }
    }

    pub fn request_stop(&self) {
        self.stop.store(true, Ordering::Release);
        wake_thread(self.thread_id.load(Ordering::Acquire));
    }

    pub fn stop(&mut self) -> Result<(), String> {
        let Some(thread) = self.thread.take() else {
            return Ok(());
        };
        self.request_stop();
        let result = match self.done.recv_timeout(STOP_TIMEOUT) {
            Ok(result) => result,
            Err(_) => {
                drop(thread);
                return Err("renderer did not stop within 2 seconds".into());
            }
        };
        thread
            .join()
            .map_err(|_| "renderer thread panicked".to_string())?;
        result
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
        let window_thread_id = unsafe { GetCurrentThreadId() };
        let draw_thread = scope.spawn(move || {
            let window = HWND(window_value as *mut _);
            let result = draw_frames(window, width, height, source, stop, ready, reporter);
            let _ = draw_done_tx.send(result);
            wake_thread(window_thread_id);
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
            wait_for_message()?;
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

fn wait_for_message() -> Result<(), String> {
    let result =
        unsafe { MsgWaitForMultipleObjectsEx(None, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE) };
    if result == WAIT_FAILED {
        return Err(format!(
            "MsgWaitForMultipleObjectsEx failed: {}",
            windows::core::Error::from_thread()
        ));
    }
    Ok(())
}

fn wake_thread(thread_id: u32) {
    if thread_id != 0 {
        let _ = unsafe { PostThreadMessageW(thread_id, WAKE_MESSAGE, WPARAM(0), LPARAM(0)) };
    }
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
    run_gpu_renderer(
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
    )
    .map_err(|error| error.to_string())
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
    use super::{measured_fps, wait_for_message, wake_thread, window_class_name};
    use std::sync::mpsc;
    use std::thread;
    use std::time::Duration;
    use windows::Win32::System::Threading::GetCurrentThreadId;
    use windows::Win32::UI::WindowsAndMessaging::{MSG, PM_REMOVE, PeekMessageW};

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
    fn renderer_window_classes_are_unique_and_null_terminated() {
        let first = window_class_name(7);
        let second = window_class_name(8);
        assert_ne!(first, second);
        assert_eq!(first.last(), Some(&0));
        assert_eq!(second.last(), Some(&0));
    }

    #[test]
    fn queued_wake_releases_message_wait() {
        let (thread_id_tx, thread_id_rx) = mpsc::sync_channel(1);
        let (done_tx, done_rx) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || {
            let mut message = MSG::default();
            let _ = unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) };
            thread_id_tx.send(unsafe { GetCurrentThreadId() }).unwrap();
            done_tx.send(wait_for_message()).unwrap();
        });

        wake_thread(thread_id_rx.recv().unwrap());
        assert_eq!(
            done_rx.recv_timeout(Duration::from_millis(200)).unwrap(),
            Ok(())
        );
        thread.join().unwrap();
    }
}
