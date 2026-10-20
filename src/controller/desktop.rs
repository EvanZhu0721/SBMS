use std::cell::RefCell;
use std::collections::VecDeque;
use std::ffi::c_void;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc;
use std::thread::{self, JoinHandle};

use windows::Win32::Foundation::{
    CloseHandle, HANDLE, HINSTANCE, HWND, LPARAM, LRESULT, WAIT_FAILED, WAIT_OBJECT_0, WPARAM,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::StationsAndDesktops::{
    GetThreadDesktop, GetUserObjectInformationW, UOI_IO,
};
use windows::Win32::System::Threading::{CreateEventW, GetCurrentThreadId, INFINITE, SetEvent};
use windows::Win32::UI::WindowsAndMessaging::*;
use windows::core::{BOOL, PCWSTR, w};

use crate::diagnostics::{self, Level};

const EVENT_SYSTEM_DESKTOPSWITCH: u32 = 0x0020;
const RECHECK_TIMER: usize = 1;
const RECHECK_COUNT: u8 = 10;

// These bindings avoid enabling whole additional windows-rs API families.
#[link(name = "wtsapi32")]
unsafe extern "system" {
    fn WTSRegisterSessionNotification(window: HWND, flags: u32) -> BOOL;
    fn WTSUnRegisterSessionNotification(window: HWND) -> BOOL;
}

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
    fn RegisterSuspendResumeNotification(recipient: HANDLE, flags: u32) -> *mut c_void;
    fn UnregisterSuspendResumeNotification(registration: *mut c_void) -> BOOL;
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum DesktopEvent {
    Locked,
    Unlocked,
    Suspended,
    Resumed,
    Switched,
    Rechecked,
    MonitorFailed,
}

#[derive(Default)]
pub(super) struct DesktopState(AtomicU64);

impl DesktopState {
    pub(super) fn can_capture(&self) -> bool {
        self.0.load(Ordering::Acquire) & 1 != 0
    }

    pub(super) fn revision(&self) -> u64 {
        self.0.load(Ordering::Acquire) >> 1
    }

    fn publish(&self, gate: &Gate) {
        self.0.store(
            (gate.revision << 1) | u64::from(gate.can_capture()),
            Ordering::Release,
        );
    }
}

#[derive(Clone, Copy, Debug)]
enum Signal {
    Locked,
    Unlocked,
    Suspended,
    Resumed,
    Switched(u32),
    Recheck,
    Failed,
}

impl Signal {
    fn event(self) -> DesktopEvent {
        match self {
            Self::Locked => DesktopEvent::Locked,
            Self::Unlocked => DesktopEvent::Unlocked,
            Self::Suspended => DesktopEvent::Suspended,
            Self::Resumed => DesktopEvent::Resumed,
            Self::Switched(_) => DesktopEvent::Switched,
            Self::Recheck => DesktopEvent::Rechecked,
            Self::Failed => DesktopEvent::MonitorFailed,
        }
    }
}

#[derive(Default)]
struct Gate {
    locked: bool,
    suspended: bool,
    input_available: bool,
    revision: u64,
    last_switch: Option<u32>,
}

impl Gate {
    fn can_capture(&self) -> bool {
        self.input_available && !self.locked && !self.suspended
    }

    // Returns whether this is a new opportunity for a bounded desktop recheck.
    fn apply(&mut self, signal: Signal, input_available: bool) -> bool {
        let was_available = self.can_capture();
        let mut invalidate = false;
        let mut recheck = false;
        match signal {
            Signal::Locked => {
                invalidate = !self.locked;
                self.locked = true;
            }
            Signal::Suspended => {
                invalidate = !self.suspended;
                self.suspended = true;
            }
            Signal::Unlocked => {
                recheck = self.locked;
                self.locked = false;
            }
            Signal::Resumed => {
                recheck = self.suspended;
                self.suspended = false;
            }
            Signal::Switched(time) => {
                invalidate = self.last_switch != Some(time);
                self.last_switch = Some(time);
                // A confirmed return to this desktop also handles delayed WTS unlock.
                if input_available {
                    self.locked = false;
                }
            }
            Signal::Failed => invalidate = was_available,
            Signal::Recheck => {}
        }
        self.input_available = input_available;
        invalidate |= was_available && !self.can_capture();
        if invalidate {
            self.revision += 1;
        }
        recheck || invalidate
    }
}

// Both WndProc and out-of-context WinEvent callbacks run on the monitor thread.
// Queue their signals so neither callback borrows the state machine reentrantly.
thread_local! {
    static SIGNALS: RefCell<VecDeque<Signal>> = const { RefCell::new(VecDeque::new()) };
}

fn enqueue(signal: Signal) {
    SIGNALS.with(|signals| signals.borrow_mut().push_back(signal));
}

pub(super) struct DesktopMonitor {
    state: Arc<DesktopState>,
    stop: HANDLE,
    worker: Option<JoinHandle<()>>,
}

impl DesktopMonitor {
    pub(super) fn start(
        notify: impl Fn(DesktopEvent) + Send + Sync + 'static,
    ) -> Result<Self, String> {
        let stop = unsafe { CreateEventW(None, true, false, None) }
            .map_err(|error| format!("CreateEventW(desktop monitor) failed: {error}"))?;
        let stop_address = stop.0 as usize;
        let state = Arc::new(DesktopState::default());
        let worker_state = Arc::clone(&state);
        let (ready_tx, ready_rx) = mpsc::channel();
        let worker = match thread::Builder::new()
            .name("sbms-desktop".into())
            .spawn(move || {
                let mut gate = Gate::default();
                if let Err(error) = run(
                    HANDLE(stop_address as *mut c_void),
                    &worker_state,
                    &mut gate,
                    &notify,
                    &ready_tx,
                ) {
                    gate.apply(Signal::Failed, false);
                    worker_state.publish(&gate);
                    log_error(&error);
                    notify(DesktopEvent::MonitorFailed);
                    let _ = ready_tx.send(Err(error));
                }
            }) {
            Ok(worker) => worker,
            Err(error) => {
                unsafe {
                    let _ = CloseHandle(stop);
                }
                return Err(format!("desktop monitor thread creation failed: {error}"));
            }
        };
        let monitor = Self {
            state,
            stop,
            worker: Some(worker),
        };
        ready_rx
            .recv()
            .map_err(|error| format!("desktop monitor startup failed: {error}"))??;
        Ok(monitor)
    }

    pub(super) fn state(&self) -> Arc<DesktopState> {
        Arc::clone(&self.state)
    }
}

impl Drop for DesktopMonitor {
    fn drop(&mut self) {
        // An event wakes the wait even if the window message queue is full.
        if let Err(error) = unsafe { SetEvent(self.stop) } {
            log_error(format!("SetEvent(desktop monitor stop) failed: {error}"));
        }
        if let Some(worker) = self.worker.take()
            && worker.join().is_err()
        {
            log_error("desktop monitor thread panicked");
        }
        if let Err(error) = unsafe { CloseHandle(self.stop) } {
            log_error(format!("CloseHandle(desktop monitor stop) failed: {error}"));
        }
    }
}

struct MonitorWindow {
    class_name: Vec<u16>,
    instance: HINSTANCE,
    hwnd: HWND,
    session_registered: bool,
    power: *mut c_void,
    hook: *mut c_void,
}

impl MonitorWindow {
    fn create() -> Result<Self, String> {
        let instance = HINSTANCE(
            unsafe { GetModuleHandleW(None) }
                .map_err(|error| format!("GetModuleHandleW(desktop monitor) failed: {error}"))?
                .0,
        );
        let class_name: Vec<u16> =
            format!("SBMS.DesktopMonitor.{}\0", unsafe { GetCurrentThreadId() })
                .encode_utf16()
                .collect();
        let class = WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            lpszClassName: PCWSTR(class_name.as_ptr()),
            ..Default::default()
        };
        if unsafe { RegisterClassW(&class) } == 0 {
            return Err(last_error("RegisterClassW(desktop monitor)"));
        }
        let mut window = Self {
            class_name,
            instance,
            hwnd: HWND::default(),
            session_registered: false,
            power: std::ptr::null_mut(),
            hook: std::ptr::null_mut(),
        };
        // A hidden top-level window receives broadcasts independently of renderers.
        window.hwnd = unsafe {
            CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                PCWSTR(window.class_name.as_ptr()),
                w!("SBMS Desktop Monitor"),
                WS_POPUP,
                0,
                0,
                0,
                0,
                None,
                None,
                Some(instance),
                None,
            )
        }
        .map_err(|error| format!("CreateWindowExW(desktop monitor) failed: {error}"))?;
        // NOTIFY_FOR_THIS_SESSION = 0; notifications exclude other user sessions.
        if !unsafe { WTSRegisterSessionNotification(window.hwnd, 0) }.as_bool() {
            return Err(last_error("WTSRegisterSessionNotification"));
        }
        window.session_registered = true;
        // DEVICE_NOTIFY_WINDOW_HANDLE = 0; includes modern standby notifications.
        window.power = unsafe { RegisterSuspendResumeNotification(HANDLE(window.hwnd.0), 0) };
        if window.power.is_null() {
            return Err(last_error("RegisterSuspendResumeNotification"));
        }
        // WINEVENT_OUTOFCONTEXT = 0: callback is delivered on this message thread.
        window.hook = unsafe {
            SetWinEventHook(
                EVENT_SYSTEM_DESKTOPSWITCH,
                EVENT_SYSTEM_DESKTOPSWITCH,
                std::ptr::null_mut(),
                Some(desktop_switched),
                0,
                0,
                0,
            )
        };
        if window.hook.is_null() {
            return Err(last_error("SetWinEventHook(EVENT_SYSTEM_DESKTOPSWITCH)"));
        }
        Ok(window)
    }
}

impl Drop for MonitorWindow {
    fn drop(&mut self) {
        unsafe {
            if !self.hook.is_null() && !UnhookWinEvent(self.hook).as_bool() {
                log_error(last_error("UnhookWinEvent"));
            }
            if !self.power.is_null() && !UnregisterSuspendResumeNotification(self.power).as_bool() {
                log_error(last_error("UnregisterSuspendResumeNotification"));
            }
            if self.session_registered && !WTSUnRegisterSessionNotification(self.hwnd).as_bool() {
                log_error(last_error("WTSUnRegisterSessionNotification"));
            }
            if !self.hwnd.is_invalid()
                && let Err(error) = DestroyWindow(self.hwnd)
            {
                log_error(format!("DestroyWindow(desktop monitor) failed: {error}"));
            }
            if let Err(error) =
                UnregisterClassW(PCWSTR(self.class_name.as_ptr()), Some(self.instance))
            {
                log_error(format!("UnregisterClassW(desktop monitor) failed: {error}"));
            }
        }
    }
}

fn run(
    stop: HANDLE,
    state: &DesktopState,
    gate: &mut Gate,
    notify: &impl Fn(DesktopEvent),
    ready: &mpsc::Sender<Result<(), String>>,
) -> Result<(), String> {
    let window = MonitorWindow::create()?;
    gate.apply(Signal::Recheck, input_desktop_available());
    state.publish(gate);
    let mut rechecks = 0;
    update_rechecks(window.hwnd, gate, &mut rechecks, true)?;
    let _ = ready.send(Ok(()));
    loop {
        let wait = unsafe {
            MsgWaitForMultipleObjectsEx(Some(&[stop]), INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE)
        };
        if wait == WAIT_OBJECT_0 {
            return Ok(());
        }
        if wait == WAIT_FAILED {
            return Err(last_error("MsgWaitForMultipleObjectsEx(desktop monitor)"));
        }
        let mut message = MSG::default();
        loop {
            let has_message =
                unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool();
            if has_message {
                unsafe {
                    let _ = DispatchMessageW(&message);
                }
            }
            // PeekMessage can also deliver sent messages without returning a MSG.
            // Publish each batch immediately instead of draining the whole queue first.
            while let Some(signal) = SIGNALS.with(|signals| signals.borrow_mut().pop_front()) {
                if matches!(signal, Signal::Recheck) {
                    if rechecks == 0 {
                        continue;
                    }
                    rechecks -= 1;
                }
                let before = (gate.can_capture(), gate.revision);
                let input = !matches!(signal, Signal::Locked | Signal::Suspended)
                    && input_desktop_available();
                let new_opportunity = gate.apply(signal, input);
                state.publish(gate);
                if before != (gate.can_capture(), gate.revision) {
                    diagnostics::log(
                        Level::Info,
                        "desktop-monitor",
                        "desktop-state",
                        None,
                        format!(
                            "{:?}: capture_allowed={}, revision={}",
                            signal.event(),
                            gate.can_capture(),
                            gate.revision
                        ),
                    );
                    notify(signal.event());
                }
                update_rechecks(window.hwnd, gate, &mut rechecks, new_opportunity)?;
            }
            if !has_message {
                break;
            }
        }
    }
}

fn update_rechecks(
    hwnd: HWND,
    gate: &Gate,
    remaining: &mut u8,
    new_opportunity: bool,
) -> Result<(), String> {
    if gate.can_capture() || gate.locked || gate.suspended {
        *remaining = 0;
    } else if new_opportunity {
        *remaining = RECHECK_COUNT;
        if unsafe { SetTimer(Some(hwnd), RECHECK_TIMER, 500, None) } == 0 {
            return Err(last_error("SetTimer(desktop recheck)"));
        }
    }
    if *remaining == 0 {
        // KillTimer returns an error if no timer exists; there is nothing to release.
        unsafe {
            let _ = KillTimer(Some(hwnd), RECHECK_TIMER);
        }
    }
    Ok(())
}

fn input_desktop_available() -> bool {
    let result = (|| {
        // This is a borrowed thread desktop; it must not be closed by this monitor.
        let desktop = unsafe { GetThreadDesktop(GetCurrentThreadId()) }
            .map_err(|error| format!("GetThreadDesktop failed: {error}"))?;
        let mut receives_input = BOOL(0);
        unsafe {
            GetUserObjectInformationW(
                HANDLE(desktop.0),
                UOI_IO,
                Some((&mut receives_input as *mut BOOL).cast()),
                size_of::<BOOL>() as u32,
                None,
            )
        }
        .map_err(|error| format!("GetUserObjectInformationW(UOI_IO) failed: {error}"))?;
        Ok::<bool, String>(receives_input.as_bool())
    })();
    match result {
        Ok(available) => available,
        Err(error) => {
            log_error(error);
            false
        }
    }
}

unsafe extern "system" fn desktop_switched(
    _: *mut c_void,
    event: u32,
    _: HWND,
    _: i32,
    _: i32,
    _: u32,
    time: u32,
) {
    if event == EVENT_SYSTEM_DESKTOPSWITCH {
        enqueue(Signal::Switched(time));
    }
}

unsafe extern "system" fn window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match message {
        WM_WTSSESSION_CHANGE => {
            match wparam.0 {
                0x2 | 0x4 | 0x6 | 0x7 => enqueue(Signal::Locked),
                0x1 | 0x3 | 0x5 | 0x8 | 0xf => enqueue(Signal::Unlocked),
                _ => {}
            }
            LRESULT(0)
        }
        WM_POWERBROADCAST => {
            match wparam.0 as u32 {
                PBT_APMSUSPEND => enqueue(Signal::Suspended),
                PBT_APMRESUMEAUTOMATIC | PBT_APMRESUMESUSPEND | PBT_APMRESUMECRITICAL => {
                    enqueue(Signal::Resumed)
                }
                _ => {}
            }
            LRESULT(1)
        }
        WM_TIMER if wparam.0 == RECHECK_TIMER => {
            enqueue(Signal::Recheck);
            LRESULT(0)
        }
        WM_CLOSE => LRESULT(0),
        _ => unsafe { DefWindowProcW(hwnd, message, wparam, lparam) },
    }
}

fn last_error(api: &str) -> String {
    format!("{api} failed: {}", windows::core::Error::from_thread())
}

fn log_error(message: impl Into<String>) {
    diagnostics::log(
        Level::Error,
        "desktop-monitor",
        "desktop-state",
        None,
        message,
    );
}

#[cfg(test)]
mod tests {
    use super::*;

    fn available_gate() -> Gate {
        let mut gate = Gate::default();
        gate.apply(Signal::Recheck, true);
        gate
    }

    #[test]
    fn startup_requires_confirmed_input_desktop() {
        let mut gate = Gate::default();
        assert!(!gate.can_capture());
        gate.apply(Signal::Recheck, false);
        assert!(!gate.can_capture());
        gate.apply(Signal::Recheck, true);
        assert!(gate.can_capture());
        assert_eq!(gate.revision, 0);
    }

    #[test]
    fn unlock_waits_for_the_user_desktop() {
        let mut gate = available_gate();
        gate.apply(Signal::Locked, false);
        assert!(!gate.can_capture());
        assert_eq!(gate.revision, 1);
        assert!(gate.apply(Signal::Unlocked, false));
        assert!(!gate.can_capture());
        gate.apply(Signal::Recheck, true);
        assert!(gate.can_capture());
        assert_eq!(gate.revision, 1);
    }

    #[test]
    fn resume_cannot_bypass_lock_and_unlock_cannot_bypass_suspend() {
        let mut gate = available_gate();
        gate.apply(Signal::Locked, false);
        gate.apply(Signal::Suspended, false);
        gate.apply(Signal::Resumed, true);
        assert!(!gate.can_capture());
        gate.apply(Signal::Suspended, false);
        gate.apply(Signal::Unlocked, true);
        assert!(!gate.can_capture());
        gate.apply(Signal::Resumed, true);
        assert!(gate.can_capture());
    }

    #[test]
    fn duplicate_notifications_do_not_refresh_revision_or_rechecks() {
        let mut gate = available_gate();
        for signal in [Signal::Unlocked, Signal::Resumed] {
            assert!(!gate.apply(signal, true));
        }
        gate.apply(Signal::Locked, false);
        let revision = gate.revision;
        assert!(!gate.apply(Signal::Locked, false));
        assert_eq!(gate.revision, revision);
        assert!(gate.apply(Signal::Unlocked, false));
        for _ in 0..20 {
            assert!(!gate.apply(Signal::Unlocked, false));
            assert!(!gate.apply(Signal::Resumed, false));
            assert!(!gate.apply(Signal::Recheck, false));
        }
        assert_eq!(gate.revision, revision);
    }

    #[test]
    fn real_switch_invalidates_even_after_a_fast_return() {
        let mut gate = available_gate();
        assert!(gate.apply(Signal::Switched(100), true));
        assert_eq!(gate.revision, 1);
        assert!(!gate.apply(Signal::Switched(100), true));
        assert_eq!(gate.revision, 1);
        assert!(gate.apply(Signal::Switched(101), true));
        assert_eq!(gate.revision, 2);
    }

    #[test]
    fn query_failure_blocks_until_a_successful_recheck() {
        let mut gate = available_gate();
        assert!(gate.apply(Signal::Recheck, false));
        assert_eq!(gate.revision, 1);
        assert!(!gate.can_capture());
        assert!(!gate.apply(Signal::Recheck, false));
        gate.apply(Signal::Recheck, true);
        assert!(gate.can_capture());
        assert_eq!(gate.revision, 1);
    }

    #[test]
    fn published_state_is_fail_closed_and_keeps_its_revision() {
        let state = DesktopState::default();
        assert!(!state.can_capture());
        let mut gate = available_gate();
        state.publish(&gate);
        assert!(state.can_capture());
        gate.apply(Signal::Failed, false);
        state.publish(&gate);
        assert!(!state.can_capture());
        assert_eq!(state.revision(), 1);
    }
}
