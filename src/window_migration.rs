use std::mem::size_of;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{HWND, LPARAM, POINT, RECT};
use windows::Win32::Graphics::Dwm::{DWMWA_CLOAKED, DwmGetWindowAttribute};
use windows::Win32::Graphics::Gdi::{
    HMONITOR, MONITOR_DEFAULTTONULL, MonitorFromRect, MonitorFromWindow,
};
use windows::Win32::System::Threading::GetCurrentProcessId;
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GWL_EXSTYLE, GetClassNameW, GetDesktopWindow, GetShellWindow, GetWindowLongPtrW,
    GetWindowPlacement, GetWindowRect, GetWindowThreadProcessId, IsIconic, IsWindow,
    IsWindowVisible, IsZoomed, SHOW_WINDOW_CMD, SW_RESTORE, SWP_ASYNCWINDOWPOS, SWP_NOACTIVATE,
    SWP_NOOWNERZORDER, SWP_NOZORDER, SetWindowPlacement, SetWindowPos, ShowWindowAsync,
    WINDOWPLACEMENT, WS_EX_APPWINDOW, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
};
use windows::core::BOOL;

use crate::display::Display;

const MOVE_TIMEOUT: Duration = Duration::from_millis(1500);
const POLL_INTERVAL: Duration = Duration::from_millis(20);
const SCAN_INTERVAL: Duration = Duration::from_millis(250);

static NEXT_MIGRATION_ID: AtomicU64 = AtomicU64::new(1);
static WINDOW_CLAIMS: OnceLock<Mutex<WindowClaimRegistry>> = OnceLock::new();

pub struct WindowMigration {
    migration_id: u64,
    entries: Arc<Mutex<Vec<WindowSnapshot>>>,
    stop_scanner: Arc<AtomicBool>,
    scanner: Option<JoinHandle<Vec<String>>>,
    scanner_errors: Vec<String>,
    restore_on_drop: bool,
    target_monitor: HMONITOR,
    target_rect: RECT,
}

struct WindowSnapshot {
    hwnd: HWND,
    pid: u32,
    thread_id: u32,
    class_name: String,
    placement: WINDOWPLACEMENT,
    outer_rect: RECT,
    maximized: bool,
    moved: bool,
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
    entries: Vec<WindowSnapshot>,
    error: Option<String>,
}

impl WindowMigration {
    pub fn start(target: &Display, source: &Display) -> Result<Self, String> {
        let target_monitor = monitor_for_rect(target.rect, "target")?;
        let source_monitor = monitor_for_rect(source.rect, "virtual source")?;
        let migration_id = next_migration_id();
        let mut entries = Vec::new();
        for mut entry in enumerate_target_windows(target_monitor)? {
            let identity = WindowIdentity::from_snapshot(&entry);
            if !claim_window(migration_id, identity.clone()) {
                continue;
            }
            let result = move_entry(
                &mut entry,
                target_monitor,
                source_monitor,
                target.rect,
                source.rect,
            );
            if entry.moved {
                entries.push(entry);
            } else {
                release_window_claim(migration_id, &identity);
            }
            if let Err(error) = result {
                let mut migration = Self {
                    migration_id,
                    entries: Arc::new(Mutex::new(entries)),
                    stop_scanner: Arc::new(AtomicBool::new(true)),
                    scanner: None,
                    scanner_errors: Vec::new(),
                    restore_on_drop: true,
                    target_monitor,
                    target_rect: target.rect,
                };
                let rollback = migration.restore();
                return Err(match rollback {
                    Ok(()) => error,
                    Err(rollback_error) => {
                        format!("{error}; rollback was incomplete: {rollback_error}")
                    }
                });
            }
        }

        let entries = Arc::new(Mutex::new(entries));
        let stop_scanner = Arc::new(AtomicBool::new(false));
        let scanner = match spawn_scanner(
            migration_id,
            Arc::clone(&entries),
            Arc::clone(&stop_scanner),
            target.rect,
            source.rect,
        ) {
            Ok(scanner) => scanner,
            Err(error) => {
                let mut migration = Self {
                    migration_id,
                    entries,
                    stop_scanner,
                    scanner: None,
                    scanner_errors: Vec::new(),
                    restore_on_drop: true,
                    target_monitor,
                    target_rect: target.rect,
                };
                let rollback = migration.restore();
                return Err(match rollback {
                    Ok(()) => error,
                    Err(rollback_error) => {
                        format!("{error}; rollback was incomplete: {rollback_error}")
                    }
                });
            }
        };
        Ok(Self {
            migration_id,
            entries,
            stop_scanner,
            scanner: Some(scanner),
            scanner_errors: Vec::new(),
            restore_on_drop: true,
            target_monitor,
            target_rect: target.rect,
        })
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

    pub(crate) fn prepare_restore(&mut self) {
        let scanner_errors = self.stop_and_join_scanner();
        for error in scanner_errors {
            remember_error(&mut self.scanner_errors, error);
        }
    }

    pub fn restore(&mut self) -> Result<(), String> {
        self.prepare_restore();
        let mut errors = std::mem::take(&mut self.scanner_errors);
        let mut entries = match self.entries.lock() {
            Ok(entries) => entries,
            Err(poisoned) => {
                errors.push("window migration state lock was poisoned".into());
                poisoned.into_inner()
            }
        };
        for entry in entries.iter_mut().rev() {
            if !entry.moved {
                continue;
            }
            if !same_window(entry) {
                entry.moved = false;
                continue;
            }
            if entry.maximized {
                unsafe {
                    let _ = ShowWindowAsync(entry.hwnd, SW_RESTORE);
                }
                if !wait_for_zoomed_state(entry, false) {
                    errors.push(describe(
                        entry,
                        "maximized window could not be restored for migration",
                    ));
                    continue;
                }
                if let Err(error) = set_window_rect(
                    entry,
                    entry.outer_rect.left,
                    entry.outer_rect.top,
                    entry.outer_rect.right - entry.outer_rect.left,
                    entry.outer_rect.bottom - entry.outer_rect.top,
                ) {
                    errors.push(describe(
                        entry,
                        &format!("maximized window return move failed: {error}"),
                    ));
                    continue;
                }
                if let Err(error) = align_normal_rect(entry, entry.placement.rcNormalPosition) {
                    errors.push(error);
                    continue;
                }
                if !wait_for_monitor(entry, self.target_monitor) {
                    errors.push(describe(
                        entry,
                        "maximized window did not return to its original monitor",
                    ));
                    continue;
                }
                if let Err(error) = unsafe { SetWindowPlacement(entry.hwnd, &entry.placement) } {
                    errors.push(describe(
                        entry,
                        &format!("maximized placement restore failed: {error}"),
                    ));
                    continue;
                }
                unsafe {
                    let _ = ShowWindowAsync(
                        entry.hwnd,
                        SHOW_WINDOW_CMD(entry.placement.showCmd as i32),
                    );
                }
                if !wait_for_zoomed_state(entry, true)
                    || !wait_for_normal_rect(entry, entry.placement.rcNormalPosition)
                    || !wait_for_monitor(entry, self.target_monitor)
                {
                    errors.push(describe(entry, "maximized window state was not restored"));
                    continue;
                }
                entry.moved = false;
                continue;
            }

            let width = entry.outer_rect.right - entry.outer_rect.left;
            let height = entry.outer_rect.bottom - entry.outer_rect.top;
            let mut exact = false;
            for _ in 0..3 {
                if let Err(error) = set_window_rect(
                    entry,
                    entry.outer_rect.left,
                    entry.outer_rect.top,
                    width,
                    height,
                ) {
                    errors.push(describe(
                        entry,
                        &format!("SetWindowPos restore failed: {error}"),
                    ));
                    break;
                }
                if wait_for_outer_rect(entry, entry.outer_rect) {
                    exact = true;
                    break;
                }
            }
            if exact && wait_for_monitor(entry, self.target_monitor) {
                entry.moved = false;
            } else {
                errors.push(describe(entry, "window placement was not restored"));
            }
        }

        release_owner_claims(self.migration_id);
        self.restore_on_drop = false;
        if errors.is_empty() {
            Ok(())
        } else {
            Err(errors.join("; "))
        }
    }

    pub fn reconcile_after_topology_change(
        &mut self,
        target: Option<&Display>,
    ) -> Result<(), String> {
        let target = target.ok_or_else(|| {
            "restored target display could not be resolved by stable ID".to_string()
        })?;
        self.target_rect = target.rect;
        self.target_monitor = monitor_for_rect(self.target_rect, "restored target")?;
        self.prepare_restore();
        let mut errors = std::mem::take(&mut self.scanner_errors);
        let mut entries = match self.entries.lock() {
            Ok(entries) => entries,
            Err(poisoned) => {
                errors.push("window migration state lock was poisoned".into());
                poisoned.into_inner()
            }
        };
        for entry in entries.iter_mut().rev() {
            if !same_window(entry) {
                entry.moved = false;
                continue;
            }
            if let Err(error) = unsafe { SetWindowPlacement(entry.hwnd, &entry.placement) } {
                errors.push(describe(
                    entry,
                    &format!("final SetWindowPlacement failed: {error}"),
                ));
                continue;
            }
            unsafe {
                let _ =
                    ShowWindowAsync(entry.hwnd, SHOW_WINDOW_CMD(entry.placement.showCmd as i32));
            }

            let state_restored = if entry.maximized {
                wait_for_zoomed_state(entry, true)
            } else {
                wait_for_window_state(entry, false) && wait_for_zoomed_state(entry, false)
            };
            if !state_restored {
                errors.push(describe(entry, "final window state was not restored"));
                continue;
            }

            if !entry.maximized {
                let width = entry.outer_rect.right - entry.outer_rect.left;
                let height = entry.outer_rect.bottom - entry.outer_rect.top;
                let mut exact = false;
                for _ in 0..3 {
                    if let Err(error) = set_window_rect(
                        entry,
                        entry.outer_rect.left,
                        entry.outer_rect.top,
                        width,
                        height,
                    ) {
                        errors.push(describe(
                            entry,
                            &format!("final SetWindowPos failed: {error}"),
                        ));
                        break;
                    }
                    if wait_for_outer_rect(entry, entry.outer_rect) {
                        exact = true;
                        break;
                    }
                }
                if !exact {
                    errors.push(describe(entry, "final outer rectangle was not restored"));
                    continue;
                }
            }

            if !wait_for_normal_rect(entry, entry.placement.rcNormalPosition)
                || !wait_for_monitor(entry, self.target_monitor)
            {
                errors.push(describe(entry, "final window placement was not restored"));
                continue;
            }
            entry.moved = false;
        }

        if errors.is_empty() {
            Ok(())
        } else {
            Err(errors.join("; "))
        }
    }
}

impl Drop for WindowMigration {
    fn drop(&mut self) {
        if self.restore_on_drop
            && let Err(error) = self.restore()
        {
            eprintln!("warning: incomplete window restore: {error}");
        }
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
            while !stop.load(Ordering::Acquire) {
                thread::park_timeout(SCAN_INTERVAL);
                if stop.load(Ordering::Acquire) {
                    break;
                }
                let target_monitor = match monitor_for_rect(target_rect, "scanner target") {
                    Ok(monitor) => monitor,
                    Err(error) => {
                        remember_error(&mut errors, error);
                        continue;
                    }
                };
                let source_monitor = match monitor_for_rect(source_rect, "scanner virtual source") {
                    Ok(monitor) => monitor,
                    Err(error) => {
                        remember_error(&mut errors, error);
                        continue;
                    }
                };
                let candidates = match enumerate_target_windows(target_monitor) {
                    Ok(candidates) => candidates,
                    Err(error) => {
                        remember_error(&mut errors, error);
                        continue;
                    }
                };
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
                    if !claim_window(migration_id, identity.clone()) {
                        continue;
                    }
                    if let Some(existing) = tracked
                        .iter_mut()
                        .find(|entry| same_identity(entry, &candidate))
                    {
                        let result = move_entry(
                            &mut candidate,
                            target_monitor,
                            source_monitor,
                            target_rect,
                            source_rect,
                        );
                        if candidate.moved {
                            existing.moved = true;
                        }
                        if let Err(error) = result {
                            remember_error(&mut errors, error);
                        }
                        if !candidate.moved {
                            release_window_claim(migration_id, &identity);
                        }
                        continue;
                    }
                    let result = move_entry(
                        &mut candidate,
                        target_monitor,
                        source_monitor,
                        target_rect,
                        source_rect,
                    );
                    if candidate.moved {
                        tracked.push(candidate);
                    } else {
                        release_window_claim(migration_id, &identity);
                    }
                    if let Err(error) = result {
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
    let mut enumeration = Enumeration {
        target_monitor,
        current_pid: unsafe { GetCurrentProcessId() },
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
    match snapshot_window(hwnd, state.target_monitor, state.current_pid) {
        Ok(Some(snapshot)) => state.entries.push(snapshot),
        Ok(None) => {}
        Err(error) => {
            state.error = Some(error);
            return BOOL(0);
        }
    }
    BOOL(1)
}

fn snapshot_window(
    hwnd: HWND,
    target_monitor: HMONITOR,
    current_pid: u32,
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
    if unsafe { IsIconic(hwnd) }.as_bool() {
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

    let mut placement = WINDOWPLACEMENT {
        length: size_of::<WINDOWPLACEMENT>() as u32,
        ..Default::default()
    };
    unsafe { GetWindowPlacement(hwnd, &mut placement) }.map_err(|error| {
        format!("GetWindowPlacement failed for pid {pid} class {class_name}: {error}")
    })?;

    Ok(Some(WindowSnapshot {
        hwnd,
        pid,
        thread_id,
        class_name,
        placement,
        outer_rect,
        maximized: unsafe { IsZoomed(hwnd) }.as_bool(),
        moved: false,
    }))
}

fn move_entry(
    entry: &mut WindowSnapshot,
    target_monitor: HMONITOR,
    source_monitor: HMONITOR,
    target_rect: RECT,
    source_rect: RECT,
) -> Result<(), String> {
    if !same_window(entry) {
        return Ok(());
    }
    entry.moved = true;
    if entry.maximized {
        unsafe {
            let _ = ShowWindowAsync(entry.hwnd, SW_RESTORE);
        }
        let restored = wait_for_zoomed_state(entry, false);
        if !restored || !wait_for_monitor(entry, target_monitor) {
            return Err(describe(
                entry,
                "window could not enter a stable restored state for migration",
            ));
        }
        let current = current_rect(entry)?;
        entry.outer_rect = current;
        let mut virtual_placement = window_placement(entry)?;
        virtual_placement.rcNormalPosition = translated_rect(
            entry,
            virtual_placement.rcNormalPosition,
            target_rect,
            source_rect,
        )?;
        virtual_placement.showCmd = SW_RESTORE.0 as u32;
        unsafe { SetWindowPlacement(entry.hwnd, &virtual_placement) }
            .map_err(|error| describe(entry, &format!("restored window move failed: {error}")))?;
        unsafe {
            let _ = ShowWindowAsync(entry.hwnd, SW_RESTORE);
        }
        if !wait_for_monitor(entry, source_monitor) {
            let actual = current_rect(entry).unwrap_or_default();
            let normal = window_placement(entry)
                .map(|placement| placement.rcNormalPosition)
                .unwrap_or_default();
            return Err(describe(
                entry,
                &format!(
                    "restored window did not move to the virtual display \
                     (outer={},{},{},{}, normal={},{},{},{})",
                    actual.left,
                    actual.top,
                    actual.right,
                    actual.bottom,
                    normal.left,
                    normal.top,
                    normal.right,
                    normal.bottom,
                ),
            ));
        }
        virtual_placement.flags = entry.placement.flags;
        virtual_placement.showCmd = entry.placement.showCmd;
        unsafe { SetWindowPlacement(entry.hwnd, &virtual_placement) }
            .map_err(|error| describe(entry, &format!("window state move failed: {error}")))?;
        unsafe {
            let _ = ShowWindowAsync(entry.hwnd, SHOW_WINDOW_CMD(entry.placement.showCmd as i32));
        }
        let state_restored = wait_for_zoomed_state(entry, true);
        if !state_restored || !wait_for_monitor(entry, source_monitor) {
            return Err(describe(
                entry,
                "window did not restore its state on the virtual display",
            ));
        }
        return Ok(());
    }

    let width = entry.outer_rect.right - entry.outer_rect.left;
    let height = entry.outer_rect.bottom - entry.outer_rect.top;
    let moved_width = width.min(source_rect.right - source_rect.left).max(1);
    let moved_height = height.min(source_rect.bottom - source_rect.top).max(1);
    let destination = translated_position(
        entry,
        entry.outer_rect.left,
        entry.outer_rect.top,
        moved_width,
        moved_height,
        target_rect,
        source_rect,
    )?;
    unsafe {
        SetWindowPos(
            entry.hwnd,
            None,
            destination.x,
            destination.y,
            moved_width,
            moved_height,
            SWP_ASYNCWINDOWPOS | SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER,
        )
    }
    .map_err(|error| describe(entry, &format!("window move failed: {error}")))?;

    if !wait_for_monitor(entry, source_monitor) {
        return Err(describe(
            entry,
            "window did not move to the virtual display",
        ));
    }
    Ok(())
}

fn translated_position(
    entry: &WindowSnapshot,
    left: i32,
    top: i32,
    width: i32,
    height: i32,
    target_rect: RECT,
    source_rect: RECT,
) -> Result<POINT, String> {
    // main() makes this process per-monitor-v2 aware before any display or
    // window API call, so GetWindowRect and SetWindowPos share physical
    // virtual-screen coordinates across monitors.
    let relative_x = left
        .checked_sub(target_rect.left)
        .ok_or_else(|| describe(entry, "horizontal window coordinate overflow"))?;
    let relative_y = top
        .checked_sub(target_rect.top)
        .ok_or_else(|| describe(entry, "vertical window coordinate overflow"))?;
    let max_x = (source_rect.right - source_rect.left - width).max(0);
    let max_y = (source_rect.bottom - source_rect.top - height).max(0);
    let x = source_rect
        .left
        .checked_add(relative_x.clamp(0, max_x))
        .ok_or_else(|| describe(entry, "horizontal window coordinate overflow"))?;
    let y = source_rect
        .top
        .checked_add(relative_y.clamp(0, max_y))
        .ok_or_else(|| describe(entry, "vertical window coordinate overflow"))?;
    Ok(POINT { x, y })
}

fn translated_rect(
    entry: &WindowSnapshot,
    rect: RECT,
    target_rect: RECT,
    source_rect: RECT,
) -> Result<RECT, String> {
    let width = (rect.right - rect.left)
        .min(source_rect.right - source_rect.left)
        .max(1);
    let height = (rect.bottom - rect.top)
        .min(source_rect.bottom - source_rect.top)
        .max(1);
    let top_left = translated_position(
        entry,
        rect.left,
        rect.top,
        width,
        height,
        target_rect,
        source_rect,
    )?;
    Ok(RECT {
        left: top_left.x,
        top: top_left.y,
        right: top_left.x + width,
        bottom: top_left.y + height,
    })
}

fn wait_for_normal_rect(entry: &WindowSnapshot, expected: RECT) -> bool {
    let deadline = Instant::now() + MOVE_TIMEOUT;
    loop {
        if !same_window(entry) {
            return true;
        }
        let mut placement = WINDOWPLACEMENT {
            length: size_of::<WINDOWPLACEMENT>() as u32,
            ..Default::default()
        };
        if unsafe { GetWindowPlacement(entry.hwnd, &mut placement) }.is_ok()
            && placement.rcNormalPosition == expected
        {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        thread::sleep(POLL_INTERVAL);
    }
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

fn align_normal_rect(entry: &WindowSnapshot, expected: RECT) -> Result<(), String> {
    for _ in 0..3 {
        let placement = window_placement(entry)?;
        if placement.rcNormalPosition == expected {
            return Ok(());
        }
        let outer = current_rect(entry)?;
        let current = placement.rcNormalPosition;
        set_window_rect(
            entry,
            outer.left + expected.left - current.left,
            outer.top + expected.top - current.top,
            outer.right - outer.left + (expected.right - expected.left)
                - (current.right - current.left),
            outer.bottom - outer.top + (expected.bottom - expected.top)
                - (current.bottom - current.top),
        )
        .map_err(|error| {
            describe(
                entry,
                &format!("normal rectangle alignment failed: {error}"),
            )
        })?;
        if wait_for_normal_rect(entry, expected) {
            return Ok(());
        }
    }
    Err(describe(entry, "normal rectangle alignment did not settle"))
}

fn wait_for_outer_rect(entry: &WindowSnapshot, expected: RECT) -> bool {
    let deadline = Instant::now() + MOVE_TIMEOUT;
    loop {
        if !same_window(entry) {
            return true;
        }
        if current_rect(entry).is_ok_and(|rect| rect == expected) {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        thread::sleep(POLL_INTERVAL);
    }
}

fn wait_for_window_state(entry: &WindowSnapshot, minimized: bool) -> bool {
    let deadline = Instant::now() + MOVE_TIMEOUT;
    loop {
        if !same_window(entry) {
            return true;
        }
        if unsafe { IsIconic(entry.hwnd) }.as_bool() == minimized {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        thread::sleep(POLL_INTERVAL);
    }
}

fn wait_for_zoomed_state(entry: &WindowSnapshot, maximized: bool) -> bool {
    let deadline = Instant::now() + MOVE_TIMEOUT;
    loop {
        if !same_window(entry) {
            return true;
        }
        if unsafe { IsZoomed(entry.hwnd) }.as_bool() == maximized {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        thread::sleep(POLL_INTERVAL);
    }
}

fn wait_for_monitor(entry: &WindowSnapshot, expected: HMONITOR) -> bool {
    let deadline = Instant::now() + MOVE_TIMEOUT;
    loop {
        if !same_window(entry) {
            return true;
        }
        if unsafe { MonitorFromWindow(entry.hwnd, MONITOR_DEFAULTTONULL) } == expected {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        thread::sleep(POLL_INTERVAL);
    }
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
    format!(
        "{message} (pid {}, class {}, maximized={})",
        entry.pid, entry.class_name, entry.maximized
    )
}

#[cfg(test)]
mod tests {
    use super::{WindowClaimRegistry, WindowIdentity};

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
}
