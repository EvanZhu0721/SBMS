use std::mem::size_of;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{HWND, LPARAM, RECT};
use windows::Win32::Graphics::Dwm::{DWMWA_CLOAKED, DwmGetWindowAttribute};
use windows::Win32::Graphics::Gdi::{
    GetMonitorInfoW, HMONITOR, MONITOR_DEFAULTTONULL, MONITORINFO, MonitorFromRect,
    MonitorFromWindow,
};
use windows::Win32::System::Threading::GetCurrentProcessId;
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, DPI_AWARENESS_PER_MONITOR_AWARE,
    GetAwarenessFromDpiAwarenessContext, GetDpiForWindow, GetWindowDpiAwarenessContext,
    SetThreadDpiAwarenessContext,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DestroyWindow, EnumWindows, GWL_EXSTYLE, GetClassNameW, GetDesktopWindow,
    GetShellWindow, GetWindowLongPtrW, GetWindowPlacement, GetWindowRect, GetWindowThreadProcessId,
    IsIconic, IsWindow, IsWindowVisible, IsZoomed, SW_SHOWNOACTIVATE, SWP_ASYNCWINDOWPOS,
    SWP_NOACTIVATE, SWP_NOOWNERZORDER, SWP_NOZORDER, SetWindowPlacement, SetWindowPos,
    WINDOWPLACEMENT, WS_EX_APPWINDOW, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_POPUP,
};
use windows::core::{BOOL, w};

use crate::display::Display;

mod physical_preservation;
mod restore_plan;
use physical_preservation::*;
pub(crate) use restore_plan::{RestoreMode, WindowRestorePlan};

const MOVE_TIMEOUT: Duration = Duration::from_millis(1500);
const POLL_INTERVAL: Duration = Duration::from_millis(20);
const SETTLE_INTERVAL: Duration = Duration::from_millis(100);
const SCAN_INTERVAL: Duration = Duration::from_millis(250);

static NEXT_MIGRATION_ID: AtomicU64 = AtomicU64::new(1);
static WINDOW_CLAIMS: OnceLock<Mutex<WindowClaimRegistry>> = OnceLock::new();

pub struct WindowMigration {
    migration_id: u64,
    entries: Arc<Mutex<Vec<WindowSnapshot>>>,
    stop_scanner: Arc<AtomicBool>,
    scanner: Option<JoinHandle<Vec<String>>>,
    target_rect: RECT,
    source_id: Option<String>,
}

struct WindowSnapshot {
    hwnd: HWND,
    pid: u32,
    thread_id: u32,
    class_name: String,
    tool_window: bool,
    moved: bool,
    layout: Option<WindowLayout>,
    // The startup transaction keeps its original layout even if the scanner
    // later observes this window returning to the physical target.
    startup_layout: Option<WindowLayout>,
}

#[derive(Clone, Copy)]
struct WindowLayout {
    normal: RECT,
    bounds: RECT,
    state: WindowState,
    placement: WINDOWPLACEMENT,
}

// HWND values are system-wide window identities. The scanner and stop path
// serialize access through the entries mutex and revalidate HWND/PID/thread/class
// before every operation.
unsafe impl Send for WindowSnapshot {}

#[derive(Clone, Debug, Eq, PartialEq)]
struct WindowIdentity {
    hwnd: isize,
    pid: u32,
    thread_id: u32,
    class_name: String,
}

impl WindowIdentity {
    fn from_snapshot(snapshot: &WindowSnapshot) -> Self {
        Self {
            hwnd: snapshot.hwnd.0 as isize,
            pid: snapshot.pid,
            thread_id: snapshot.thread_id,
            class_name: snapshot.class_name.clone(),
        }
    }
}

#[derive(Debug, Eq, PartialEq)]
struct WindowClaim {
    owner: u64,
    identity: WindowIdentity,
}

#[derive(Default)]
struct WindowClaimRegistry {
    claims: Vec<WindowClaim>,
}

impl WindowClaimRegistry {
    fn claim(&mut self, owner: u64, identity: WindowIdentity) -> bool {
        if let Some(claim) = self.claims.iter().find(|claim| claim.identity == identity) {
            return claim.owner == owner;
        }
        self.claims.push(WindowClaim { owner, identity });
        true
    }

    fn release(&mut self, owner: u64, identity: &WindowIdentity) {
        self.claims
            .retain(|claim| claim.owner != owner || claim.identity != *identity);
    }

    fn release_owner(&mut self, owner: u64) {
        self.claims.retain(|claim| claim.owner != owner);
    }
}

fn next_migration_id() -> u64 {
    NEXT_MIGRATION_ID.fetch_add(1, Ordering::Relaxed)
}

fn with_window_claims<T>(operation: impl FnOnce(&mut WindowClaimRegistry) -> T) -> T {
    let registry = WINDOW_CLAIMS.get_or_init(|| Mutex::new(WindowClaimRegistry::default()));
    let mut claims = registry
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    operation(&mut claims)
}

fn claim_window(owner: u64, identity: WindowIdentity) -> bool {
    with_window_claims(|claims| claims.claim(owner, identity))
}

fn release_window_claim(owner: u64, identity: &WindowIdentity) {
    with_window_claims(|claims| claims.release(owner, identity));
}

fn release_owner_claims(owner: u64) {
    with_window_claims(|claims| claims.release_owner(owner));
}

struct Enumeration {
    target_monitor: HMONITOR,
    current_pid: u32,
    include_minimized: bool,
    entries: Vec<WindowSnapshot>,
    error: Option<String>,
}

impl WindowMigration {
    pub(crate) fn capture(target: &Display) -> Result<Self, String> {
        let target_monitor = monitor_for_rect(target.rect, "target")?;
        let migration_id = next_migration_id();
        let mut entries = Vec::new();
        for mut entry in enumerate_target_windows(target_monitor)? {
            let identity = WindowIdentity::from_snapshot(&entry);
            if !claim_window(migration_id, identity) {
                continue;
            }
            log_window_state("snapshot", &entry, Some(target.rect));
            entry.startup_layout = entry.layout;
            entries.push(entry);
        }
        Ok(Self {
            migration_id,
            entries: Arc::new(Mutex::new(entries)),
            stop_scanner: Arc::new(AtomicBool::new(true)),
            scanner: None,
            target_rect: target.rect,
            source_id: None,
        })
    }

    pub(crate) fn bind_source_id(&mut self, id: String) {
        self.source_id = Some(id);
    }

    pub(crate) fn begin(&mut self, target: &Display, source: &Display) -> Result<(), String> {
        monitor_for_rect(target.rect, "target")?;
        monitor_for_rect(source.rect, "virtual source")?;
        let mut entries = self
            .entries
            .lock()
            .map_err(|_| "window migration state lock was poisoned")?;
        self.target_rect = target.rect;
        self.source_id = Some(source.id.clone());
        for entry in entries.iter_mut() {
            entry.moved = same_window(entry);
            if !entry.moved {
                release_window_claim(self.migration_id, &WindowIdentity::from_snapshot(entry));
            }
        }
        restore_layout_batch(&entries.iter().collect::<Vec<_>>(), source.rect)?;
        drop(entries);
        self.stop_scanner = Arc::new(AtomicBool::new(false));
        self.scanner = Some(spawn_scanner(
            self.migration_id,
            Arc::clone(&self.entries),
            Arc::clone(&self.stop_scanner),
            target.rect,
            source.rect,
        )?);
        Ok(())
    }

    fn stop_and_join_scanner(&mut self) -> Vec<String> {
        self.stop_scanner.store(true, Ordering::Release);
        match self.scanner.take() {
            Some(scanner) => {
                scanner.thread().unpark();
                scanner
                    .join()
                    .unwrap_or_else(|_| vec!["window scanner thread panicked".into()])
            }
            None => Vec::new(),
        }
    }

    pub(crate) fn refresh_topology(
        &mut self,
        target: &Display,
        source: &Display,
    ) -> Result<(), String> {
        monitor_for_rect(target.rect, "updated target")?;
        monitor_for_rect(source.rect, "updated virtual source")?;

        // A display-layout mutation invalidates the rectangles captured by the
        // scanner. Stop it before publishing the replacement rectangles; errors
        // produced by that expected transition must not poison eventual cleanup.
        for error in self.stop_and_join_scanner() {
            crate::diagnostics::log(
                crate::diagnostics::Level::Warn,
                "windows",
                "scanner-ended",
                None,
                error,
            );
        }
        self.target_rect = target.rect;
        self.source_id = Some(source.id.clone());
        self.stop_scanner = Arc::new(AtomicBool::new(false));
        let scanner = spawn_scanner(
            self.migration_id,
            Arc::clone(&self.entries),
            Arc::clone(&self.stop_scanner),
            target.rect,
            source.rect,
        )?;
        self.scanner = Some(scanner);
        Ok(())
    }
}

// Match the source identity, never its old rectangle: Windows may reuse that
// area for the primary screen after a device disappears.
fn restore_source<'a>(displays: &'a [Display], source_id: Option<&str>) -> Option<&'a Display> {
    let id = source_id?;
    displays
        .iter()
        .find(|display| display.virtual_display && display.id.eq_ignore_ascii_case(id))
}

fn recovery_window_belongs_to_source(current: HMONITOR, source: Option<HMONITOR>) -> bool {
    current.0.is_null() || source == Some(current)
}

fn offset_rect(rect: RECT, dx: i32, dy: i32) -> Result<RECT, String> {
    Ok(RECT {
        left: rect
            .left
            .checked_add(dx)
            .ok_or_else(|| "window rectangle left coordinate overflowed".to_string())?,
        top: rect
            .top
            .checked_add(dy)
            .ok_or_else(|| "window rectangle top coordinate overflowed".to_string())?,
        right: rect
            .right
            .checked_add(dx)
            .ok_or_else(|| "window rectangle right coordinate overflowed".to_string())?,
        bottom: rect
            .bottom
            .checked_add(dy)
            .ok_or_else(|| "window rectangle bottom coordinate overflowed".to_string())?,
    })
}

impl Drop for WindowMigration {
    fn drop(&mut self) {
        let _ = self.stop_and_join_scanner();
        release_owner_claims(self.migration_id);
    }
}

fn spawn_scanner(
    migration_id: u64,
    entries: Arc<Mutex<Vec<WindowSnapshot>>>,
    stop: Arc<AtomicBool>,
    target_rect: RECT,
    source_rect: RECT,
) -> Result<JoinHandle<Vec<String>>, String> {
    thread::Builder::new()
        .name("sbms-window-scanner".into())
        .spawn(move || {
            let mut errors = Vec::new();
            let mut rejected = Vec::<WindowIdentity>::new();
            while !stop.load(Ordering::Acquire) {
                thread::park_timeout(SCAN_INTERVAL);
                if stop.load(Ordering::Acquire) {
                    break;
                }
                let target_monitor = match monitor_for_rect(target_rect, "scanner target") {
                    Ok(monitor) => monitor,
                    Err(error) => {
                        remember_error(&mut errors, error);
                        break;
                    }
                };
                if let Err(error) = monitor_for_rect(source_rect, "scanner virtual source") {
                    remember_error(&mut errors, error);
                    break;
                }
                let candidates = match enumerate_target_windows(target_monitor) {
                    Ok(candidates) => candidates,
                    Err(error) => {
                        remember_error(&mut errors, error);
                        continue;
                    }
                };
                // A failed arrival is attempted once. Leaving the target screen
                // ends that arrival; a later user move back is a new operation.
                rejected.retain(|identity| {
                    candidates
                        .iter()
                        .any(|candidate| WindowIdentity::from_snapshot(candidate) == *identity)
                });
                let mut tracked = match entries.lock() {
                    Ok(entries) => entries,
                    Err(_) => {
                        remember_error(
                            &mut errors,
                            "window migration state lock was poisoned".into(),
                        );
                        break;
                    }
                };
                for mut candidate in candidates {
                    if stop.load(Ordering::Acquire) {
                        break;
                    }
                    let identity = WindowIdentity::from_snapshot(&candidate);
                    if rejected.contains(&identity) {
                        continue;
                    }
                    if !claim_window(migration_id, identity.clone()) {
                        continue;
                    }
                    if let Some(existing) = tracked
                        .iter_mut()
                        .find(|entry| same_identity(entry, &candidate))
                    {
                        let result = move_entry(&mut candidate, target_rect, source_rect);
                        if candidate.moved {
                            existing.moved = true;
                            existing.layout = candidate.layout;
                        }
                        if let Err(error) = result {
                            rejected.push(identity.clone());
                            remember_error(&mut errors, error);
                        }
                        if !candidate.moved {
                            release_window_claim(migration_id, &identity);
                        }
                        continue;
                    }
                    log_window_state("snapshot", &candidate, Some(target_rect));
                    let result = move_entry(&mut candidate, target_rect, source_rect);
                    if candidate.moved {
                        tracked.push(candidate);
                    } else {
                        release_window_claim(migration_id, &identity);
                    }
                    if let Err(error) = result {
                        rejected.push(identity);
                        remember_error(&mut errors, error);
                    }
                }
            }
            errors
        })
        .map_err(|error| format!("failed to start window scanner: {error}"))
}

fn remember_error(errors: &mut Vec<String>, error: String) {
    const MAX_REPORTED_ERRORS: usize = 16;
    if errors.len() < MAX_REPORTED_ERRORS && !errors.contains(&error) {
        errors.push(error);
    }
}

fn enumerate_target_windows(target_monitor: HMONITOR) -> Result<Vec<WindowSnapshot>, String> {
    enumerate_windows(target_monitor, false)
}

fn enumerate_windows(
    target_monitor: HMONITOR,
    include_minimized: bool,
) -> Result<Vec<WindowSnapshot>, String> {
    let mut enumeration = Enumeration {
        target_monitor,
        current_pid: unsafe { GetCurrentProcessId() },
        include_minimized,
        entries: Vec::new(),
        error: None,
    };
    let result = unsafe {
        EnumWindows(
            Some(enumerate_window),
            LPARAM((&mut enumeration as *mut Enumeration) as isize),
        )
    };
    if let Some(error) = enumeration.error {
        return Err(error);
    }
    result.map_err(|error| format!("EnumWindows failed: {error}"))?;
    Ok(enumeration.entries)
}

unsafe extern "system" fn enumerate_window(hwnd: HWND, parameter: LPARAM) -> BOOL {
    let state = unsafe { &mut *(parameter.0 as *mut Enumeration) };
    match snapshot_window_for_phase(
        hwnd,
        state.target_monitor,
        state.current_pid,
        state.include_minimized,
    ) {
        Ok(Some(snapshot)) => state.entries.push(snapshot),
        Ok(None) => {}
        Err(error) => {
            state.error = Some(error);
            return BOOL(0);
        }
    }
    BOOL(1)
}

#[cfg(test)]
fn snapshot_window(
    hwnd: HWND,
    target_monitor: HMONITOR,
    current_pid: u32,
) -> Result<Option<WindowSnapshot>, String> {
    snapshot_window_for_phase(hwnd, target_monitor, current_pid, false)
}

fn snapshot_window_for_phase(
    hwnd: HWND,
    target_monitor: HMONITOR,
    current_pid: u32,
    include_minimized: bool,
) -> Result<Option<WindowSnapshot>, String> {
    if !unsafe { IsWindowVisible(hwnd) }.as_bool()
        || hwnd == unsafe { GetDesktopWindow() }
        || hwnd == unsafe { GetShellWindow() }
    {
        return Ok(None);
    }

    let mut pid = 0;
    let thread_id = unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
    if thread_id == 0 || pid == current_pid {
        return Ok(None);
    }
    if unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL) } != target_monitor {
        return Ok(None);
    }
    if !include_minimized && unsafe { IsIconic(hwnd) }.as_bool() {
        return Ok(None);
    }
    let class_name = class_name(hwnd);
    if matches!(
        class_name.as_str(),
        "Progman" | "WorkerW" | "Shell_TrayWnd" | "Shell_SecondaryTrayWnd"
    ) {
        return Ok(None);
    }

    let mut cloaked = 0u32;
    if unsafe {
        DwmGetWindowAttribute(
            hwnd,
            DWMWA_CLOAKED,
            (&mut cloaked as *mut u32).cast(),
            size_of::<u32>() as u32,
        )
    }
    .is_ok()
        && cloaked != 0
    {
        return Ok(None);
    }

    let extended_style = unsafe { GetWindowLongPtrW(hwnd, GWL_EXSTYLE) } as u32;
    let auxiliary = extended_style & (WS_EX_TOOLWINDOW.0 | WS_EX_NOACTIVATE.0) != 0;
    if auxiliary && extended_style & WS_EX_APPWINDOW.0 == 0 {
        return Ok(None);
    }

    let mut outer_rect = RECT::default();
    unsafe { GetWindowRect(hwnd, &mut outer_rect) }.map_err(|error| {
        format!("GetWindowRect failed for pid {pid} class {class_name}: {error}")
    })?;
    if outer_rect.right <= outer_rect.left || outer_rect.bottom <= outer_rect.top {
        return Ok(None);
    }

    let mut snapshot = WindowSnapshot {
        hwnd,
        pid,
        thread_id,
        class_name,
        tool_window: extended_style & WS_EX_TOOLWINDOW.0 != 0,
        moved: false,
        layout: None,
        startup_layout: None,
    };
    let mut monitor = MONITORINFO {
        cbSize: size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    unsafe { GetMonitorInfoW(target_monitor, &mut monitor) }
        .ok()
        .map_err(|error| describe(&snapshot, &format!("capture window work area: {error}")))?;
    let Some(observation) = observe_window(&snapshot)? else {
        return Ok(None);
    };
    snapshot.layout = Some(WindowLayout::capture(&snapshot, observation, &monitor)?);
    Ok(Some(snapshot))
}

impl WindowLayout {
    fn capture(
        entry: &WindowSnapshot,
        observation: WindowObservation,
        monitor: &MONITORINFO,
    ) -> Result<Self, String> {
        let state = if observation.iconic {
            WindowState::Minimized
        } else if observation.zoomed {
            WindowState::Maximized
        } else {
            WindowState::Normal
        };
        let normal = if state == WindowState::Normal {
            observation.outer
        } else if entry.tool_window {
            observation.placement.rcNormalPosition
        } else {
            offset_rect(
                observation.placement.rcNormalPosition,
                monitor.rcWork.left - monitor.rcMonitor.left,
                monitor.rcWork.top - monitor.rcMonitor.top,
            )?
        };
        Ok(Self {
            normal,
            bounds: monitor.rcMonitor,
            state,
            placement: observation.placement,
        })
    }

    fn mapped_rect(&self, work: RECT) -> Option<RECT> {
        let source = self.bounds;
        let rect = self.normal;
        if rect.right <= rect.left
            || rect.bottom <= rect.top
            || source.right <= source.left
            || source.bottom <= source.top
            || rect.left < source.left
            || rect.top < source.top
            || rect.right > source.right
            || rect.bottom > source.bottom
        {
            return None;
        }
        let map = |value: i32, start: i32, end: i32, target: i32, target_end: i32| {
            (i128::from(target)
                + (i128::from(value) - i128::from(start))
                    * (i128::from(target_end) - i128::from(target))
                    / (i128::from(end) - i128::from(start))) as i32
        };
        let mapped = RECT {
            left: map(rect.left, source.left, source.right, work.left, work.right),
            right: map(rect.right, source.left, source.right, work.left, work.right),
            top: map(rect.top, source.top, source.bottom, work.top, work.bottom),
            bottom: map(
                rect.bottom,
                source.top,
                source.bottom,
                work.top,
                work.bottom,
            ),
        };
        (mapped.right > mapped.left && mapped.bottom > mapped.top).then_some(mapped)
    }
}

fn planned_window_rect(
    entry: &WindowSnapshot,
    monitor: &MONITORINFO,
    slot: (usize, usize),
) -> RECT {
    entry
        .layout
        .and_then(|layout| layout.mapped_rect(monitor.rcMonitor))
        .unwrap_or_else(|| default_batch_rect(monitor.rcWork, slot.0, slot.1))
}

fn move_entry(
    entry: &mut WindowSnapshot,
    _target_rect: RECT,
    source_rect: RECT,
) -> Result<(), String> {
    if !same_window(entry) {
        return Ok(());
    }
    entry.moved = true;
    restore_entry(entry, source_rect)
}

fn default_window_rect(work: RECT) -> RECT {
    let width = (work.right - work.left) * 3 / 4;
    let height = (work.bottom - work.top) * 3 / 4;
    let left = work.left + (work.right - work.left - width) / 2;
    let top = work.top + (work.bottom - work.top - height) / 2;
    RECT {
        left,
        top,
        right: left + width,
        bottom: top + height,
    }
}

fn default_batch_rect(work: RECT, index: usize, count: usize) -> RECT {
    let centered = default_window_rect(work);
    if count <= 1 {
        return centered;
    }
    // Center the cascade as a whole. Compress its spacing when the work area
    // cannot fit 32 pixels between windows, keeping every requested rect inside.
    let free_x = work.right - centered.right + centered.left - work.left;
    let free_y = work.bottom - centered.bottom + centered.top - work.top;
    let span = ((count - 1).saturating_mul(32)).min(free_x.min(free_y) as usize);
    let offset = (index as u128 * span as u128 / (count - 1) as u128) as i32;
    let left = work.left + (free_x - span as i32) / 2 + offset;
    let top = work.top + (free_y - span as i32) / 2 + offset;
    RECT {
        left,
        top,
        right: left + centered.right - centered.left,
        bottom: top + centered.bottom - centered.top,
    }
}

fn default_monitor(target: RECT) -> Result<MONITORINFO, String> {
    let monitor = match monitor_for_rect(target, "default window target") {
        Ok(monitor) => monitor,
        Err(_) => {
            let displays =
                crate::display::active_display_topology().map_err(|error| error.to_string())?;
            let primary = displays
                .iter()
                .find(|display| display.primary)
                .ok_or("default target and primary display are unavailable")?;
            monitor_for_rect(primary.rect, "primary window target")?
        }
    };
    let mut info = MONITORINFO {
        cbSize: size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    unsafe { GetMonitorInfoW(monitor, &mut info) }
        .ok()
        .map_err(|error| format!("query default window work area: {error}"))?;
    Ok(info)
}

fn normal_position(
    entry: &WindowSnapshot,
    outer: RECT,
    monitor: &MONITORINFO,
) -> Result<RECT, String> {
    if entry.tool_window {
        Ok(outer)
    } else {
        offset_rect(
            outer,
            monitor.rcMonitor.left - monitor.rcWork.left,
            monitor.rcMonitor.top - monitor.rcWork.top,
        )
    }
}

fn screen_position(
    entry: &WindowSnapshot,
    normal: RECT,
    monitor: &MONITORINFO,
) -> Result<RECT, String> {
    if entry.tool_window {
        Ok(normal)
    } else {
        offset_rect(
            normal,
            monitor.rcWork.left - monitor.rcMonitor.left,
            monitor.rcWork.top - monitor.rcMonitor.top,
        )
    }
}

fn intersection_area(a: RECT, b: RECT) -> i128 {
    let width = (i128::from(a.right.min(b.right)) - i128::from(a.left.max(b.left))).max(0);
    let height = (i128::from(a.bottom.min(b.bottom)) - i128::from(a.top.max(b.top))).max(0);
    width * height
}

fn layout_goal(
    entry: &WindowSnapshot,
    requested: RECT,
    monitor: &MONITORINFO,
) -> Result<WindowGoal, String> {
    let state = entry
        .layout
        .map_or(WindowState::Normal, |layout| layout.state);
    let mut goal = WindowGoal {
        state,
        monitor: Some(monitor.rcMonitor),
        dpi: if state == WindowState::Minimized {
            None
        } else {
            Some(destination_dpi(entry, monitor.rcMonitor)?)
        },
        ..WindowGoal::default()
    };
    if state == WindowState::Normal {
        goal.position = Some((requested.left, requested.top));
    } else {
        goal.normal = Some(normal_position(entry, requested, monitor)?);
        goal.restore_to_maximized = entry
            .layout
            .filter(|_| state == WindowState::Minimized)
            .map(|layout| {
                layout.placement.flags.0
                    & windows::Win32::UI::WindowsAndMessaging::WPF_RESTORETOMAXIMIZED.0
                    != 0
            });
    }
    Ok(goal)
}

// Every caller supplies one final plan. There is no staging/final replay:
// only a cross-DPI transition may precede the final geometry submission.
fn execute_layout(
    entry: &WindowSnapshot,
    requested: RECT,
    monitor: &MONITORINFO,
    goal: WindowGoal,
) -> Result<Option<WindowObservation>, String> {
    let Some(current) = observe_window(entry)? else {
        return Ok(None);
    };
    if goal.matches(&current, goal.current_monitor()) {
        return Ok(Some(current));
    }
    require_responsive(entry)?;
    if !same_window(entry) {
        return Ok(None);
    }
    if goal.state == WindowState::Minimized {
        // A topology change can leave the restore rectangle outside every
        // monitor while the minimized HWND retains its old DPI. Windows then
        // scales/clamps the first placement as it establishes a new monitor.
        // Establish ownership with the destination work area, independently
        // of the final rectangle; never retry a rejected final placement.
        let current_normal = screen_position(entry, current.placement.rcNormalPosition, monitor)?;
        let ownership = WindowGoal {
            state: WindowState::Minimized,
            monitor: Some(monitor.rcMonitor),
            ..WindowGoal::default()
        };
        if !ownership.matches(&current, ownership.current_monitor())
            || intersection_area(current_normal, monitor.rcMonitor) == 0
        {
            let mut anchor = current.placement;
            anchor.rcNormalPosition = normal_position(entry, monitor.rcWork, monitor)?;
            anchor.showCmd = windows::Win32::UI::WindowsAndMessaging::SW_SHOWMINIMIZED.0 as u32;
            if apply_placement(entry, &anchor, ownership, "establish minimized monitor")?.is_none()
            {
                return Ok(None);
            }
        }
        let mut placement = entry
            .layout
            .map_or(current.placement, |layout| layout.placement);
        placement.rcNormalPosition = goal
            .normal
            .unwrap_or(normal_position(entry, requested, monitor)?);
        placement.showCmd = windows::Win32::UI::WindowsAndMessaging::SW_SHOWMINIMIZED.0 as u32;
        // Minimized windows use rcNormalPosition for monitor ownership. Moving
        // their -32000 icon rectangle does not establish a destination anchor.
        return apply_placement(entry, &placement, goal, "place minimized window");
    }
    if goal.state == WindowState::Maximized
        && WindowGoal::on_monitor(entry, monitor.rcMonitor, WindowState::Maximized)?
            .matches(&current, goal.current_monitor())
    {
        let mut placement = current.placement;
        placement.rcNormalPosition = goal
            .normal
            .unwrap_or(normal_position(entry, requested, monitor)?);
        placement.showCmd = windows::Win32::UI::WindowsAndMessaging::SW_SHOWMAXIMIZED.0 as u32;
        return apply_placement(entry, &placement, goal, "place maximized window");
    }
    let Some(current) = ensure_normal(entry, normal_position(entry, requested, monitor)?)? else {
        return Ok(None);
    };
    let destination = WindowGoal::on_monitor(entry, monitor.rcMonitor, WindowState::Normal)?;
    // A DPI transition lets the app resize itself. Complete that transition
    // before issuing the final rectangle, rather than retrying a rejected size.
    if destination.dpi != Some(current.dpi)
        && move_normal(entry, requested, monitor.rcMonitor)?.is_none()
    {
        return Ok(None);
    }
    if !same_window(entry) {
        return Ok(None);
    }
    require_responsive(entry)?;
    if !same_window(entry) {
        return Ok(None);
    }
    set_window_rect(
        entry,
        requested.left,
        requested.top,
        requested.right - requested.left,
        requested.bottom - requested.top,
    )
    .map_err(|error| describe(entry, &format!("place window: {error}")))?;
    if goal.state == WindowState::Maximized {
        wait_for_goal(entry, destination, "prepare maximized destination")?;
        let Some(current) = observe_window(entry)? else {
            return Ok(None);
        };
        let mut placement = current.placement;
        placement.rcNormalPosition = goal
            .normal
            .unwrap_or(normal_position(entry, requested, monitor)?);
        placement.showCmd = windows::Win32::UI::WindowsAndMessaging::SW_SHOWMAXIMIZED.0 as u32;
        apply_placement(entry, &placement, goal, "place maximized window")
    } else {
        wait_for_goal(entry, goal, "place window")
    }
}

fn restore_layout_batch(entries: &[&WindowSnapshot], target_rect: RECT) -> Result<(), String> {
    let monitor = default_monitor(target_rect)?;
    run_window_batch(entries.len(), |index| {
        let entry = entries[index];
        let requested = planned_window_rect(entry, &monitor, (index, entries.len()));
        let goal = layout_goal(entry, requested, &monitor)?;
        execute_layout(entry, requested, &monitor, goal)?;
        Ok(())
    })
}

fn run_window_batch(
    count: usize,
    mut restore: impl FnMut(usize) -> Result<(), String>,
) -> Result<(), String> {
    let errors: Vec<_> = (0..count)
        .rev()
        .filter_map(|index| restore(index).err())
        .collect();
    if errors.is_empty() {
        Ok(())
    } else {
        Err(errors.join("; "))
    }
}

fn restore_entry(entry: &WindowSnapshot, target_rect: RECT) -> Result<(), String> {
    restore_entry_at(entry, target_rect, (0, 1))
}

fn restore_entry_at(
    entry: &WindowSnapshot,
    target_rect: RECT,
    slot: (usize, usize),
) -> Result<(), String> {
    if !same_window(entry) {
        return Ok(());
    }
    let monitor = default_monitor(target_rect)?;
    let requested = planned_window_rect(entry, &monitor, slot);
    let goal = layout_goal(entry, requested, &monitor)?;
    execute_layout(entry, requested, &monitor, goal)?;
    Ok(())
}

// WM_NULL has no side effects. Do not enqueue moves for an already hung thread.
// The async mutation flags below still protect against a hang after this check.
fn require_responsive(entry: &WindowSnapshot) -> Result<(), String> {
    use windows::Win32::Foundation::WPARAM;
    use windows::Win32::UI::WindowsAndMessaging::{
        SMTO_ABORTIFHUNG, SMTO_BLOCK, SMTO_ERRORONEXIT, SendMessageTimeoutW, WM_NULL,
    };
    let result = unsafe {
        SendMessageTimeoutW(
            entry.hwnd,
            WM_NULL,
            WPARAM(0),
            LPARAM(0),
            SMTO_ABORTIFHUNG | SMTO_BLOCK | SMTO_ERRORONEXIT,
            MOVE_TIMEOUT.as_millis() as u32,
            None,
        )
    };
    if result.0 == 0 && same_window(entry) {
        Err(describe(
            entry,
            "window thread is not responding; no placement submitted",
        ))
    } else {
        Ok(())
    }
}

fn log_window_state(stage: &str, entry: &WindowSnapshot, target_rect: Option<RECT>) {
    crate::diagnostics::log(
        crate::diagnostics::Level::Debug,
        "windows",
        stage,
        None,
        format!(
            "pid={} hwnd={:?} class={} target={target_rect:?} moved={} actual={:?}",
            entry.pid,
            entry.hwnd,
            entry.class_name,
            entry.moved,
            observe_window(entry)
        ),
    );
}

#[derive(Clone, Copy, Debug)]
struct WindowObservation {
    outer: RECT,
    placement: WINDOWPLACEMENT,
    monitor: HMONITOR,
    dpi: u32,
    iconic: bool,
    zoomed: bool,
}

impl WindowObservation {
    fn geometry_matches(&self, other: &Self) -> bool {
        self.outer == other.outer
            && self.placement.rcNormalPosition == other.placement.rcNormalPosition
            && self.placement.showCmd == other.placement.showCmd
            && self.placement.flags == other.placement.flags
            && self.monitor == other.monitor
            && self.dpi == other.dpi
            && self.iconic == other.iconic
            && self.zoomed == other.zoomed
    }

    fn valid_outer(&self) -> bool {
        self.outer.right > self.outer.left
            && self.outer.bottom > self.outer.top
            && !self.monitor.0.is_null()
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
enum WindowState {
    #[default]
    Any,
    Restored,
    Normal,
    Maximized,
    Minimized,
}

#[derive(Clone, Copy, Debug, Default)]
struct WindowGoal {
    state: WindowState,
    monitor: Option<RECT>,
    dpi: Option<u32>,
    outer: Option<RECT>,
    normal: Option<RECT>,
    position: Option<(i32, i32)>,
    restore_to_maximized: Option<bool>,
}

impl WindowGoal {
    fn on_monitor(entry: &WindowSnapshot, rect: RECT, state: WindowState) -> Result<Self, String> {
        Ok(Self {
            state,
            monitor: Some(rect),
            dpi: Some(destination_dpi(entry, rect)?),
            ..Self::default()
        })
    }

    fn matches(&self, actual: &WindowObservation, target: Option<HMONITOR>) -> bool {
        let state_matches = match self.state {
            WindowState::Any => true,
            WindowState::Restored => !actual.iconic && !actual.zoomed,
            WindowState::Normal => !actual.iconic && !actual.zoomed && actual.valid_outer(),
            WindowState::Maximized => !actual.iconic && actual.zoomed && actual.valid_outer(),
            WindowState::Minimized => actual.iconic,
        };
        state_matches
            && target.is_none_or(|monitor| !monitor.0.is_null() && actual.monitor == monitor)
            && self.dpi.is_none_or(|dpi| actual.dpi == dpi)
            && self.outer.is_none_or(|rect| actual.outer == rect)
            && self
                .position
                .is_none_or(|(left, top)| actual.outer.left == left && actual.outer.top == top)
            && self
                .normal
                .is_none_or(|rect| actual.placement.rcNormalPosition == rect)
            && self.restore_to_maximized.is_none_or(|expected| {
                (actual.placement.flags.0
                    & windows::Win32::UI::WindowsAndMessaging::WPF_RESTORETOMAXIMIZED.0
                    != 0)
                    == expected
            })
    }

    fn current_monitor(&self) -> Option<HMONITOR> {
        self.monitor
            .map(|rect| unsafe { MonitorFromRect(&rect, MONITOR_DEFAULTTONULL) })
    }
}

fn observe_window(entry: &WindowSnapshot) -> Result<Option<WindowObservation>, String> {
    if !same_window(entry) {
        return Ok(None);
    }
    let observation = (|| {
        Ok(WindowObservation {
            outer: current_rect(entry)?,
            placement: window_placement(entry)?,
            monitor: unsafe { MonitorFromWindow(entry.hwnd, MONITOR_DEFAULTTONULL) },
            dpi: unsafe { GetDpiForWindow(entry.hwnd) },
            iconic: unsafe { IsIconic(entry.hwnd) }.as_bool(),
            zoomed: unsafe { IsZoomed(entry.hwnd) }.as_bool(),
        })
    })();
    // Closing a window during an operation is not a migration failure, and a
    // recycled HWND must never receive the next step in the operation.
    if !same_window(entry) {
        return Ok(None);
    }
    observation.map(Some)
}

#[derive(Default)]
struct WindowSettling {
    previous: Option<WindowObservation>,
    since: Option<Instant>,
}

impl WindowSettling {
    fn observe(&mut self, actual: WindowObservation, matches: bool, now: Instant) -> bool {
        if !matches {
            self.since = None;
        } else if self
            .previous
            .is_none_or(|old| !actual.geometry_matches(&old))
            || self.since.is_none()
        {
            self.since = Some(now);
        }
        self.previous = Some(actual);
        matches
            && self
                .since
                .is_some_and(|since| now.duration_since(since) >= SETTLE_INTERVAL)
    }
}

fn wait_for_goal(
    entry: &WindowSnapshot,
    goal: WindowGoal,
    stage: &str,
) -> Result<Option<WindowObservation>, String> {
    let deadline = Instant::now() + MOVE_TIMEOUT;
    let mut settling = WindowSettling::default();
    loop {
        let Some(actual) = observe_window(entry)? else {
            return Ok(None);
        };
        let now = Instant::now();
        if settling.observe(actual, goal.matches(&actual, goal.current_monitor()), now) {
            return Ok(Some(actual));
        }
        if now >= deadline {
            return Err(describe(
                entry,
                &format!("{stage} did not settle; expected={goal:?}; actual={actual:?}"),
            ));
        }
        thread::sleep(POLL_INTERVAL);
    }
}

fn destination_dpi(entry: &WindowSnapshot, target: RECT) -> Result<u32, String> {
    let awareness =
        unsafe { GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(entry.hwnd)) };
    if awareness != DPI_AWARENESS_PER_MONITOR_AWARE {
        // System-aware and unaware windows retain their own DPI across monitors.
        return Ok(unsafe { GetDpiForWindow(entry.hwnd) });
    }
    // GetDpiForMonitor is not supported on a per-monitor-aware calling thread.
    // A never-shown PMv2 window gives the destination's actual effective DPI
    // through GetDpiForWindow without relying on registry scale factors.
    let previous =
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let probe = unsafe {
        CreateWindowExW(
            WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            w!("STATIC"),
            w!(""),
            WS_POPUP,
            target.left + (target.right - target.left) / 2,
            target.top + (target.bottom - target.top) / 2,
            1,
            1,
            None,
            None,
            None,
            None,
        )
    };
    unsafe {
        SetThreadDpiAwarenessContext(previous);
    }
    let probe =
        probe.map_err(|error| describe(entry, &format!("query destination DPI: {error}")))?;
    let dpi = unsafe { GetDpiForWindow(probe) };
    unsafe { DestroyWindow(probe) }
        .map_err(|error| describe(entry, &format!("release DPI probe: {error}")))?;
    if dpi == 0 {
        return Err(describe(entry, "destination DPI is unavailable"));
    }
    Ok(dpi)
}

fn ensure_normal(
    entry: &WindowSnapshot,
    normal: RECT,
) -> Result<Option<WindowObservation>, String> {
    // Restore callers already know their destination. Do not pin this step to
    // a transient monitor while Windows is rearranging windows after removal;
    // move_normal verifies the actual destination monitor and DPI next.
    let Some(current) = observe_window(entry)? else {
        return Ok(None);
    };
    let goal = WindowGoal {
        // A normal window may currently be offscreen, including the -32000
        // minimized-position sentinel. Its valid destination is applied next;
        // requiring valid current geometry would prevent that repair.
        state: WindowState::Restored,
        ..WindowGoal::default()
    };
    Ok(if !goal.matches(&current, None) {
        let mut placement = current.placement;
        placement.flags.0 &= !windows::Win32::UI::WindowsAndMessaging::WPF_RESTORETOMAXIMIZED.0;
        placement.showCmd = SW_SHOWNOACTIVATE.0 as u32;
        placement.rcNormalPosition = normal;
        apply_placement(entry, &placement, goal, "restore movable window state")?
    } else {
        Some(current)
    })
}

fn move_normal(
    entry: &WindowSnapshot,
    requested: RECT,
    target: RECT,
) -> Result<Option<WindowObservation>, String> {
    if !same_window(entry) {
        return Ok(None);
    }
    let goal = WindowGoal::on_monitor(entry, target, WindowState::Normal)?;
    set_window_rect(
        entry,
        requested.left,
        requested.top,
        requested.right - requested.left,
        requested.bottom - requested.top,
    )
    .map_err(|error| describe(entry, &format!("move normal window: {error}")))?;
    wait_for_goal(entry, goal, "move normal window")
}

fn apply_placement(
    entry: &WindowSnapshot,
    placement: &WINDOWPLACEMENT,
    goal: WindowGoal,
    stage: &str,
) -> Result<Option<WindowObservation>, String> {
    if !same_window(entry) {
        return Ok(None);
    }
    require_responsive(entry)?;
    if !same_window(entry) {
        return Ok(None);
    }
    let mut placement = *placement;
    placement.flags |= windows::Win32::UI::WindowsAndMessaging::WPF_ASYNCWINDOWPLACEMENT;
    unsafe { SetWindowPlacement(entry.hwnd, &placement) }
        .map_err(|error| describe(entry, &format!("{stage}: {error}")))?;
    wait_for_goal(entry, goal, stage)
}

fn window_placement(entry: &WindowSnapshot) -> Result<WINDOWPLACEMENT, String> {
    let mut placement = WINDOWPLACEMENT {
        length: size_of::<WINDOWPLACEMENT>() as u32,
        ..Default::default()
    };
    unsafe { GetWindowPlacement(entry.hwnd, &mut placement) }
        .map_err(|error| describe(entry, &format!("GetWindowPlacement failed: {error}")))?;
    Ok(placement)
}

fn same_window(entry: &WindowSnapshot) -> bool {
    if !unsafe { IsWindow(Some(entry.hwnd)) }.as_bool() {
        return false;
    }
    let mut pid = 0;
    let thread_id = unsafe { GetWindowThreadProcessId(entry.hwnd, Some(&mut pid)) };
    thread_id == entry.thread_id && pid == entry.pid && class_name(entry.hwnd) == entry.class_name
}

fn same_identity(left: &WindowSnapshot, right: &WindowSnapshot) -> bool {
    left.hwnd == right.hwnd
        && left.pid == right.pid
        && left.thread_id == right.thread_id
        && left.class_name == right.class_name
}

fn monitor_for_rect(rect: RECT, label: &str) -> Result<HMONITOR, String> {
    let monitor = unsafe { MonitorFromRect(&rect, MONITOR_DEFAULTTONULL) };
    if monitor.0.is_null() {
        Err(format!("{label} rectangle is not attached to a monitor"))
    } else {
        Ok(monitor)
    }
}

fn class_name(hwnd: HWND) -> String {
    let mut value = [0u16; 256];
    let length = unsafe { GetClassNameW(hwnd, &mut value) };
    String::from_utf16_lossy(&value[..length.max(0) as usize])
}

fn current_rect(entry: &WindowSnapshot) -> Result<RECT, String> {
    let mut rect = RECT::default();
    unsafe { GetWindowRect(entry.hwnd, &mut rect) }
        .map_err(|error| describe(entry, &format!("GetWindowRect failed: {error}")))?;
    Ok(rect)
}

fn set_window_rect(
    entry: &WindowSnapshot,
    left: i32,
    top: i32,
    width: i32,
    height: i32,
) -> windows::core::Result<()> {
    unsafe {
        SetWindowPos(
            entry.hwnd,
            None,
            left,
            top,
            width,
            height,
            SWP_ASYNCWINDOWPOS | SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER,
        )
    }
}

fn describe(entry: &WindowSnapshot, message: &str) -> String {
    format!("{message} (pid {}, class {})", entry.pid, entry.class_name)
}

#[cfg(test)]
use windows::Win32::UI::WindowsAndMessaging::ShowWindowAsync;

#[cfg(test)]
#[path = "window_migration/operation_tests.rs"]
mod operation_tests;

#[cfg(test)]
#[path = "window_migration/execution_tests.rs"]
mod execution_tests;

#[cfg(test)]
mod tests {
    use super::{RECT, WindowClaimRegistry, WindowIdentity};

    fn identity(hwnd: isize, pid: u32, thread_id: u32, class_name: &str) -> WindowIdentity {
        WindowIdentity {
            hwnd,
            pid,
            thread_id,
            class_name: class_name.into(),
        }
    }

    #[test]
    fn a_window_can_only_be_claimed_by_one_owner() {
        let mut claims = WindowClaimRegistry::default();
        let window = identity(42, 100, 200, "EditorWindow");

        assert!(claims.claim(1, window.clone()));
        assert!(claims.claim(1, window.clone()));
        assert!(!claims.claim(2, window));
        assert_eq!(claims.claims.len(), 1);
    }

    #[test]
    fn reused_hwnd_with_a_different_identity_is_not_blocked() {
        let mut claims = WindowClaimRegistry::default();
        assert!(claims.claim(1, identity(42, 100, 200, "EditorWindow")));

        assert!(claims.claim(2, identity(42, 101, 200, "EditorWindow")));
        assert!(claims.claim(2, identity(42, 100, 201, "EditorWindow")));
        assert!(claims.claim(2, identity(42, 100, 200, "DialogWindow")));
    }

    #[test]
    fn releasing_one_identity_keeps_the_owners_other_claims() {
        let mut claims = WindowClaimRegistry::default();
        let first = identity(42, 100, 200, "EditorWindow");
        let second = identity(43, 100, 200, "EditorWindow");
        assert!(claims.claim(1, first.clone()));
        assert!(claims.claim(1, second.clone()));

        claims.release(1, &first);

        assert!(claims.claim(2, first));
        assert!(!claims.claim(2, second));
    }

    #[test]
    fn releasing_an_owner_releases_all_of_its_windows_only() {
        let mut claims = WindowClaimRegistry::default();
        let first = identity(42, 100, 200, "EditorWindow");
        let second = identity(43, 100, 200, "EditorWindow");
        let other = identity(44, 300, 400, "BrowserWindow");
        assert!(claims.claim(1, first.clone()));
        assert!(claims.claim(1, second.clone()));
        assert!(claims.claim(2, other.clone()));

        claims.release_owner(1);

        assert!(claims.claim(3, first));
        assert!(claims.claim(3, second));
        assert!(!claims.claim(3, other));
    }

    #[test]
    fn default_layout_is_centered_in_work_area() {
        let work = RECT {
            left: -1880,
            top: 100,
            right: 0,
            bottom: 1100,
        };
        assert_eq!(
            super::default_window_rect(work),
            RECT {
                left: -1645,
                top: 225,
                right: -235,
                bottom: 975
            }
        );
    }
}

#[cfg(test)]
#[path = "window_migration/firefox_tests.rs"]
mod firefox_tests;
