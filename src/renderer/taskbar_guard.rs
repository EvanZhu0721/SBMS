use std::cell::Cell;
use std::ffi::c_void;

use windows::Win32::Foundation::{HWND, LPARAM, RECT};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetClassNameW, GetWindowRect, HWND_TOPMOST, IsWindowVisible, SWP_NOACTIVATE,
    SWP_NOMOVE, SWP_NOSIZE, SetWindowPos,
};
use windows::core::BOOL;

use crate::diagnostics::{self, Level};

const EVENT_OBJECT_REORDER: u32 = 0x8004;
type WinEventProc = unsafe extern "system" fn(*mut c_void, u32, HWND, i32, i32, u32, u32);

#[link(name = "user32")]
unsafe extern "system" {
    fn SetWinEventHook(
        first: u32,
        last: u32,
        module: *mut c_void,
        callback: Option<WinEventProc>,
        process: u32,
        thread: u32,
        flags: u32,
    ) -> *mut c_void;
    fn UnhookWinEvent(hook: *mut c_void) -> BOOL;
}

#[derive(Clone, Copy)]
struct State {
    window: HWND,
    events: u64,
    corrections: u64,
}

thread_local! {
    static STATE: Cell<Option<State>> = const { Cell::new(None) };
}

/// Lives on the renderer's message thread; out-of-context events use that same thread.
pub(super) struct TaskbarGuard(*mut c_void);

impl TaskbarGuard {
    pub(super) fn new(window: HWND) -> Result<Self, String> {
        let hook = unsafe {
            SetWinEventHook(
                EVENT_OBJECT_REORDER,
                EVENT_OBJECT_REORDER,
                std::ptr::null_mut(),
                Some(reordered),
                0,
                0,
                0, // WINEVENT_OUTOFCONTEXT: no injected DLL or extra thread.
            )
        };
        if hook.is_null() {
            return Err(format!(
                "SetWinEventHook(EVENT_OBJECT_REORDER) failed: {}",
                windows::core::Error::from_thread()
            ));
        }
        STATE.set(Some(State {
            window,
            events: 0,
            corrections: 0,
        }));
        Ok(Self(hook))
    }
}

impl Drop for TaskbarGuard {
    fn drop(&mut self) {
        let _ = unsafe { UnhookWinEvent(self.0) };
        if let Some(state) = STATE.take() {
            diagnostics::log(
                Level::Debug,
                "renderer",
                "taskbar-guard-stop",
                None,
                format!(
                    "window={:?} reorder_events={} corrections={}",
                    state.window, state.events, state.corrections
                ),
            );
        }
    }
}

unsafe extern "system" fn reordered(
    _hook: *mut c_void,
    _event: u32,
    event_window: HWND,
    object: i32,
    child: i32,
    _thread: u32,
    _time: u32,
) {
    // Temporarily remove state so a synchronous nested callback cannot correct twice.
    let Some(mut state) = STATE.take() else {
        return;
    };
    state.events += 1;
    if let Some(taskbar) = covering_taskbar(state.window) {
        let result = unsafe {
            SetWindowPos(
                state.window,
                Some(HWND_TOPMOST),
                0,
                0,
                0,
                0,
                SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE,
            )
        };
        if result.is_ok() {
            state.corrections += 1;
            diagnostics::log(
                Level::Debug,
                "renderer",
                "taskbar-guard-correct",
                None,
                format!(
                    "window={:?} taskbar={taskbar:?} event_window={event_window:?} object={object} child={child} reorder_events={} corrections={}",
                    state.window, state.events, state.corrections
                ),
            );
        }
    }
    // Queued events from our own correction see the repaired order and do nothing.
    STATE.set(Some(state));
}

struct Search {
    mirror: HWND,
    bounds: RECT,
    taskbar: Option<HWND>,
}

fn covering_taskbar(mirror: HWND) -> Option<HWND> {
    let mut search = Search {
        mirror,
        bounds: RECT::default(),
        taskbar: None,
    };
    if !unsafe { IsWindowVisible(mirror) }.as_bool()
        || unsafe { GetWindowRect(mirror, &mut search.bounds) }.is_err()
    {
        return None;
    }
    // REORDER may name the desktop, so inspect the actual top-level order instead
    // of assuming the event HWND is the taskbar. Stop as soon as we reach our mirror.
    let _ = unsafe {
        EnumWindows(
            Some(visit_window),
            LPARAM((&mut search as *mut Search) as isize),
        )
    };
    search.taskbar
}

unsafe extern "system" fn visit_window(window: HWND, data: LPARAM) -> BOOL {
    let search = unsafe { &mut *(data.0 as *mut Search) };
    if window == search.mirror {
        return BOOL(0);
    }
    if !unsafe { IsWindowVisible(window) }.as_bool() {
        return BOOL(1);
    }
    let mut class = [0u16; 64];
    let length = unsafe { GetClassNameW(window, &mut class) } as usize;
    let class = &class[..length];
    if !class.iter().copied().eq("Shell_TrayWnd".encode_utf16())
        && !class
            .iter()
            .copied()
            .eq("Shell_SecondaryTrayWnd".encode_utf16())
    {
        return BOOL(1);
    }
    let mut bounds = RECT::default();
    if unsafe { GetWindowRect(window, &mut bounds) }.is_ok()
        && bounds.left < search.bounds.right
        && bounds.right > search.bounds.left
        && bounds.top < search.bounds.bottom
        && bounds.bottom > search.bounds.top
    {
        search.taskbar = Some(window);
        return BOOL(0);
    }
    BOOL(1)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{Duration, Instant};
    use windows::Win32::UI::HiDpi::{
        DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetThreadDpiAwarenessContext,
    };
    use windows::Win32::UI::WindowsAndMessaging::{
        DispatchMessageW, GWL_EXSTYLE, GetForegroundWindow, GetWindowLongPtrW, HWND_NOTOPMOST, MSG,
        PM_REMOVE, PeekMessageW, WS_EX_TOPMOST,
    };

    fn pump(duration: Duration) {
        let end = Instant::now() + duration;
        while Instant::now() < end {
            let mut message = MSG::default();
            while unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool() {
                unsafe { DispatchMessageW(&message) };
            }
            std::thread::sleep(Duration::from_millis(5));
        }
    }

    struct Restore {
        mirror: HWND,
        taskbar: HWND,
        was_topmost: bool,
    }

    impl Drop for Restore {
        fn drop(&mut self) {
            unsafe {
                let _ = SetWindowPos(
                    self.taskbar,
                    Some(if self.was_topmost {
                        HWND_TOPMOST
                    } else {
                        HWND_NOTOPMOST
                    }),
                    0,
                    0,
                    0,
                    0,
                    SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE,
                );
                let _ = SetWindowPos(
                    self.mirror,
                    Some(HWND_TOPMOST),
                    0,
                    0,
                    0,
                    0,
                    SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE,
                );
            }
        }
    }

    #[test]
    #[ignore = "temporarily raises the specified live physical taskbar"]
    fn native_taskbar_reorder_corrects_once_and_unhooks() {
        let mirror = HWND(
            std::env::var("SBMS_TASKBAR_TEST_MIRROR")
                .unwrap()
                .parse::<usize>()
                .unwrap() as *mut c_void,
        );
        let taskbar = HWND(
            std::env::var("SBMS_TASKBAR_TEST_TASKBAR")
                .unwrap()
                .parse::<usize>()
                .unwrap() as *mut c_void,
        );
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
        let _ = diagnostics::init();
        let _restore = Restore {
            mirror,
            taskbar,
            was_topmost: unsafe { GetWindowLongPtrW(taskbar, GWL_EXSTYLE) }
                & WS_EX_TOPMOST.0 as isize
                != 0,
        };
        let raise_taskbar = || unsafe {
            SetWindowPos(
                taskbar,
                Some(HWND_TOPMOST),
                0,
                0,
                0,
                0,
                SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE,
            )
            .unwrap();
        };
        assert_eq!(
            covering_taskbar(mirror),
            None,
            "baseline must be unobscured"
        );
        let foreground = unsafe { GetForegroundWindow() };
        let guard = TaskbarGuard::new(mirror).unwrap();
        for attempt in 1..=3 {
            let before = STATE.get().unwrap().corrections;
            raise_taskbar();
            assert_eq!(
                covering_taskbar(mirror),
                Some(taskbar),
                "injection must cover mirror"
            );
            let started = Instant::now();
            while STATE.get().unwrap().corrections == before
                && started.elapsed() < Duration::from_secs(2)
            {
                pump(Duration::from_millis(5));
            }
            let corrected = STATE.get().unwrap();
            assert_eq!(corrected.corrections, before + 1);
            assert_eq!(covering_taskbar(mirror), None);
            println!(
                "attempt={attempt} corrected_after_ms={} events={} corrections={}",
                started.elapsed().as_millis(),
                corrected.events,
                corrected.corrections
            );
            pump(Duration::from_secs(10));
            assert_eq!(
                STATE.get().unwrap().corrections,
                before + 1,
                "no repeated corrections"
            );
            assert_eq!(
                covering_taskbar(mirror),
                None,
                "stable order after ten seconds"
            );
            assert_eq!(
                unsafe { GetForegroundWindow() },
                foreground,
                "must not steal focus"
            );
        }
        drop(guard);
        raise_taskbar();
        pump(Duration::from_secs(2));
        assert_eq!(
            covering_taskbar(mirror),
            Some(taskbar),
            "unhook must stop correction"
        );
        assert!(STATE.get().is_none());
        println!(
            "PASS: three raises, one correction each, stable during waits, focus unchanged, drop unhooks"
        );
    }
}
