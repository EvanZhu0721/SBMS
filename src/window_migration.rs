use std::mem::size_of;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{HWND, LPARAM, POINT, RECT};
use windows::Win32::Graphics::Dwm::{DWMWA_CLOAKED, DwmGetWindowAttribute};
use windows::Win32::Graphics::Gdi::{
    GetMonitorInfoW, HMONITOR, MONITOR_DEFAULTTONULL, MONITORINFO, MonitorFromRect,
    MonitorFromWindow,
};
use windows::Win32::System::Threading::GetCurrentProcessId;
use windows::Win32::UI::HiDpi::GetDpiForWindow;
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
    target_rect: RECT,
}

struct WindowSnapshot {
    hwnd: HWND,
    pid: u32,
    thread_id: u32,
    class_name: String,
    placement: WINDOWPLACEMENT,
    normal_anchor: Option<NormalAnchor>,
    tool_window: bool,
    restore_target: Option<RECT>,
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
        monitor_for_rect(source.rect, "virtual source")?;
        let migration_id = next_migration_id();
        let mut entries = Vec::new();
        for mut entry in enumerate_target_windows(target_monitor)? {
            let identity = WindowIdentity::from_snapshot(&entry);
            if !claim_window(migration_id, identity.clone()) {
                continue;
            }
            log_window_state("snapshot", &entry, Some(target.rect));
            let result = move_entry(&mut entry, target.rect, source.rect);
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
        let _ = self.stop_and_join_scanner();
        let dx = target
            .rect
            .left
            .checked_sub(self.target_rect.left)
            .ok_or_else(|| "updated target horizontal offset overflowed".to_string())?;
        let dy = target
            .rect
            .top
            .checked_sub(self.target_rect.top)
            .ok_or_else(|| "updated target vertical offset overflowed".to_string())?;
        let mut entries = self
            .entries
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        shift_saved_rectangles(&mut entries, dx, dy, &placement_displays()?)?;
        drop(entries);
        self.target_rect = target.rect;
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

    pub(crate) fn prepare_restore(&mut self) {
        let scanner_errors = self.stop_and_join_scanner();
        for error in scanner_errors {
            remember_error(&mut self.scanner_errors, error);
        }
    }

    pub fn restore(&mut self) -> Result<(), String> {
        self.prepare_restore();
        // A successful final reconcile may supersede an intermediate move
        // failure, but it must still report errors from the background scanner.
        let mut errors = self.scanner_errors.clone();
        let mut entries = match self.entries.lock() {
            Ok(entries) => entries,
            Err(poisoned) => {
                errors.push("window migration state lock was poisoned".into());
                poisoned.into_inner()
            }
        };
        for entry in entries.iter_mut().rev() {
            let target_rect = entry.restore_target.unwrap_or(self.target_rect);
            log_window_state("restore-before", entry, Some(target_rect));
            if !entry.moved {
                continue;
            }
            if !same_window(entry) {
                entry.moved = false;
                continue;
            }
            if entry.maximized {
                if let Err(error) = restore_maximized(entry, target_rect) {
                    errors.push(error);
                } else {
                    entry.moved = false;
                }
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
            if exact && wait_for_monitor(entry, target_rect) {
                entry.moved = false;
            } else {
                errors.push(describe(entry, "window placement was not restored"));
            }
        }

        for entry in entries.iter() {
            log_window_state(
                "restore-after",
                entry,
                Some(entry.restore_target.unwrap_or(self.target_rect)),
            );
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
        let target_missing = target.is_none();
        let fallback_displays;
        let target = if let Some(target) = target {
            target
        } else {
            fallback_displays =
                crate::display::active_display_topology().map_err(|error| error.to_string())?;
            crate::diagnostics::log(
                crate::diagnostics::Level::Warn,
                "windows",
                "target-default",
                None,
                "Saved target display is unavailable; restoring windows to the primary display",
            );
            fallback_displays
                .iter()
                .find(|display| display.primary)
                .ok_or("restored target and primary display are unavailable")?
        };
        let dx = target
            .rect
            .left
            .checked_sub(self.target_rect.left)
            .ok_or_else(|| "restored target horizontal offset overflowed".to_string())?;
        let dy = target
            .rect
            .top
            .checked_sub(self.target_rect.top)
            .ok_or_else(|| "restored target vertical offset overflowed".to_string())?;
        monitor_for_rect(target.rect, "restored target")?;
        self.prepare_restore();
        let mut errors = std::mem::take(&mut self.scanner_errors);
        let mut entries = match self.entries.lock() {
            Ok(entries) => entries,
            Err(poisoned) => {
                errors.push("window migration state lock was poisoned".into());
                poisoned.into_inner()
            }
        };
        let displays = placement_displays()?;
        shift_saved_rectangles(&mut entries, dx, dy, &displays)?;
        if target_missing {
            let work = displays
                .iter()
                .find(|display| display.id == target.id)
                .ok_or("primary display work area is unavailable")?
                .work;
            for entry in entries.iter_mut().filter(|entry| entry.maximized) {
                // The old maximized size may span adjacent monitors after its
                // display disappears. Seed maximize inside the new work area.
                entry.outer_rect = work;
            }
        }
        self.target_rect = target.rect;

        for entry in entries.iter_mut().rev() {
            let target_rect = entry.restore_target.unwrap_or(self.target_rect);
            log_window_state("reconcile-before", entry, Some(target_rect));
            if !same_window(entry) {
                entry.moved = false;
                continue;
            }
            if entry.maximized {
                if let Err(error) = restore_maximized(entry, target_rect) {
                    errors.push(error);
                } else {
                    entry.moved = false;
                }
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

            let state_restored =
                wait_for_window_state(entry, false) && wait_for_zoomed_state(entry, false);
            if !state_restored {
                errors.push(describe(entry, "final window state was not restored"));
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

            if !wait_for_normal_rect(entry, entry.placement.rcNormalPosition)
                || !wait_for_monitor(entry, target_rect)
            {
                // Some app frameworks retain their virtual-display rectangle
                // after the physical topology returns even though placement APIs
                // report success. Reapply the saved restored rectangle first,
                // then restore the original show state.
                unsafe {
                    let _ = ShowWindowAsync(entry.hwnd, SW_RESTORE);
                }
                if !wait_for_zoomed_state(entry, false) {
                    errors.push(describe(
                        entry,
                        "final window could not be restored for retry",
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
                        &format!("final fallback move failed: {error}"),
                    ));
                    continue;
                }
                if let Err(error) = align_normal_rect(entry, entry.placement.rcNormalPosition) {
                    errors.push(error);
                    continue;
                }
                if !wait_for_monitor(entry, target_rect) {
                    errors.push(describe(
                        entry,
                        "final fallback move missed the target monitor",
                    ));
                    continue;
                }
                if let Err(error) = unsafe { SetWindowPlacement(entry.hwnd, &entry.placement) } {
                    errors.push(describe(
                        entry,
                        &format!("final fallback placement failed: {error}"),
                    ));
                    continue;
                }
                unsafe {
                    let _ = ShowWindowAsync(
                        entry.hwnd,
                        SHOW_WINDOW_CMD(entry.placement.showCmd as i32),
                    );
                }
                let state_restored =
                    wait_for_window_state(entry, false) && wait_for_zoomed_state(entry, false);
                if !state_restored
                    || !wait_for_normal_rect(entry, entry.placement.rcNormalPosition)
                    || !wait_for_monitor(entry, target_rect)
                {
                    errors.push(describe(entry, "final window placement was not restored"));
                    continue;
                }
            }
            entry.moved = false;
        }

        for entry in entries.iter() {
            log_window_state(
                "reconcile-after",
                entry,
                Some(entry.restore_target.unwrap_or(self.target_rect)),
            );
        }
        if errors.is_empty() {
            Ok(())
        } else {
            Err(errors.join("; "))
        }
    }
}

#[derive(Clone, Debug)]
struct PlacementDisplay {
    id: String,
    rect: RECT,
    work: RECT,
    primary: bool,
}

#[derive(Clone, Debug)]
struct NormalAnchor {
    display_id: String,
    origin: POINT,
    defaulted: bool,
}

impl PlacementDisplay {
    // WINDOWPLACEMENT uses workspace coordinates, except for tool windows.
    fn origin(&self, tool_window: bool) -> POINT {
        POINT {
            x: self.rect.left
                - if tool_window {
                    0
                } else {
                    self.work.left - self.rect.left
                },
            y: self.rect.top
                - if tool_window {
                    0
                } else {
                    self.work.top - self.rect.top
                },
        }
    }
}

fn placement_displays() -> Result<Vec<PlacementDisplay>, String> {
    crate::display::active_display_topology()
        .map_err(|error| format!("could not resolve window placement displays: {error}"))?
        .into_iter()
        .map(|display| {
            let monitor = monitor_for_rect(display.rect, "window placement")?;
            let mut info = MONITORINFO {
                cbSize: size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            if !unsafe { GetMonitorInfoW(monitor, &mut info) }.as_bool() {
                return Err(format!("could not read work area for {}", display.id));
            }
            Ok(PlacementDisplay {
                id: display.id,
                rect: info.rcMonitor,
                work: info.rcWork,
                primary: display.primary,
            })
        })
        .collect()
}

fn normal_anchor(
    rect: RECT,
    tool_window: bool,
    displays: &[PlacementDisplay],
) -> Result<NormalAnchor, String> {
    if !normal_is_visible(rect, tool_window, displays) {
        return Ok(NormalAnchor {
            display_id: String::new(),
            origin: POINT::default(),
            defaulted: true,
        });
    }
    let display = displays
        .iter()
        .max_by_key(|display| {
            let origin = display.origin(tool_window);
            let right =
                i64::from(origin.x) + i64::from(display.rect.right) - i64::from(display.rect.left);
            let bottom =
                i64::from(origin.y) + i64::from(display.rect.bottom) - i64::from(display.rect.top);
            let width = (i64::from(rect.right).min(right)
                - i64::from(rect.left).max(i64::from(origin.x)))
            .max(0);
            let height = (i64::from(rect.bottom).min(bottom)
                - i64::from(rect.top).max(i64::from(origin.y)))
            .max(0);
            width * height
        })
        .ok_or_else(|| "no display for saved normal rectangle".to_string())?;
    Ok(NormalAnchor {
        display_id: display.id.clone(),
        origin: display.origin(tool_window),
        defaulted: false,
    })
}

fn rebase_normal(
    rect: RECT,
    anchor: &NormalAnchor,
    tool_window: bool,
    displays: &[PlacementDisplay],
) -> Result<(RECT, NormalAnchor), String> {
    let Some(display) = displays
        .iter()
        .find(|display| display.id == anchor.display_id)
    else {
        return default_normal(
            rect,
            tool_window,
            displays,
            "saved display is no longer active",
        );
    };
    let origin = display.origin(tool_window);
    let dx = origin
        .x
        .checked_sub(anchor.origin.x)
        .ok_or("normal rectangle horizontal offset overflowed")?;
    let dy = origin
        .y
        .checked_sub(anchor.origin.y)
        .ok_or("normal rectangle vertical offset overflowed")?;
    let shifted = offset_rect(rect, dx, dy)?;
    if !normal_is_visible(shifted, tool_window, displays) {
        return default_normal(
            rect,
            tool_window,
            displays,
            "saved rectangle is outside all work areas",
        );
    }
    Ok((
        shifted,
        NormalAnchor {
            display_id: anchor.display_id.clone(),
            origin,
            defaulted: false,
        },
    ))
}

fn normal_is_visible(rect: RECT, tool_window: bool, displays: &[PlacementDisplay]) -> bool {
    displays.iter().any(|display| {
        let inset_x = if tool_window {
            0
        } else {
            display.work.left - display.rect.left
        };
        let inset_y = if tool_window {
            0
        } else {
            display.work.top - display.rect.top
        };
        i64::from(rect.left) + i64::from(inset_x) < i64::from(display.work.right)
            && i64::from(rect.right) + i64::from(inset_x) > i64::from(display.work.left)
            && i64::from(rect.top) + i64::from(inset_y) < i64::from(display.work.bottom)
            && i64::from(rect.bottom) + i64::from(inset_y) > i64::from(display.work.top)
            && rect.right > rect.left
            && rect.bottom > rect.top
    })
}

fn default_normal(
    rect: RECT,
    tool_window: bool,
    displays: &[PlacementDisplay],
    reason: &str,
) -> Result<(RECT, NormalAnchor), String> {
    let display = displays
        .iter()
        .find(|display| display.primary)
        .ok_or("no active primary display for default window placement")?;
    let available_width = i64::from(display.work.right) - i64::from(display.work.left);
    let available_height = i64::from(display.work.bottom) - i64::from(display.work.top);
    if available_width <= 0 || available_height <= 0 {
        return Err("primary display has no usable work area".into());
    }
    let saved_width = i64::from(rect.right) - i64::from(rect.left);
    let width = if saved_width > 0 {
        saved_width.min(available_width)
    } else {
        (available_width * 2 / 3).max(1)
    };
    let saved_height = i64::from(rect.bottom) - i64::from(rect.top);
    let height = if saved_height > 0 {
        saved_height.min(available_height)
    } else {
        (available_height * 2 / 3).max(1)
    };
    let left = i64::from(if tool_window {
        display.work.left
    } else {
        display.rect.left
    }) + (available_width - width) / 2;
    let top = i64::from(if tool_window {
        display.work.top
    } else {
        display.rect.top
    }) + (available_height - height) / 2;
    let result = RECT {
        left: left as i32,
        top: top as i32,
        right: (left + width) as i32,
        bottom: (top + height) as i32,
    };
    crate::diagnostics::log(
        crate::diagnostics::Level::Warn,
        "windows",
        "placement-default",
        None,
        format!(
            "{reason}; saved={rect:?}; primary={}; default={result:?}",
            display.id
        ),
    );
    Ok((
        result,
        NormalAnchor {
            display_id: display.id.clone(),
            origin: display.origin(tool_window),
            defaulted: true,
        },
    ))
}

fn shift_saved_rectangles(
    entries: &mut [WindowSnapshot],
    dx: i32,
    dy: i32,
    displays: &[PlacementDisplay],
) -> Result<(), String> {
    let shifted: Result<Vec<_>, String> = entries
        .iter()
        .map(|entry| {
            let anchor = entry
                .normal_anchor
                .as_ref()
                .ok_or("saved normal rectangle has no display anchor")?;
            let (normal_rect, anchor) = rebase_normal(
                entry.placement.rcNormalPosition,
                anchor,
                entry.tool_window,
                displays,
            )?;
            let mut outer = offset_rect(entry.outer_rect, dx, dy)?;
            let mut restore_target = None;
            if !entry.maximized && (anchor.defaulted || entry.restore_target.is_some()) {
                let display = displays
                    .iter()
                    .find(|display| display.id == anchor.display_id)
                    .ok_or("default window display is unavailable")?;
                outer = offset_rect(
                    normal_rect,
                    if entry.tool_window {
                        0
                    } else {
                        display.work.left - display.rect.left
                    },
                    if entry.tool_window {
                        0
                    } else {
                        display.work.top - display.rect.top
                    },
                )?;
                restore_target = Some(display.rect);
            }
            Ok((outer, normal_rect, anchor, restore_target))
        })
        .collect();
    for (entry, (outer_rect, normal_rect, anchor, restore_target)) in
        entries.iter_mut().zip(shifted?)
    {
        entry.outer_rect = outer_rect;
        entry.placement.rcNormalPosition = normal_rect;
        entry.normal_anchor = Some(anchor);
        entry.restore_target = restore_target;
    }
    Ok(())
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
                if let Err(error) = monitor_for_rect(source_rect, "scanner virtual source") {
                    remember_error(&mut errors, error);
                    continue;
                }
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
                        let result = move_entry(&mut candidate, target_rect, source_rect);
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
                    log_window_state("snapshot", &candidate, Some(target_rect));
                    let result = move_entry(&mut candidate, target_rect, source_rect);
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
    if !enumeration.entries.is_empty() {
        let displays = placement_displays()?;
        for entry in &mut enumeration.entries {
            entry.normal_anchor = Some(normal_anchor(
                entry.placement.rcNormalPosition,
                entry.tool_window,
                &displays,
            )?);
        }
    }
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

    let snapshot = WindowSnapshot {
        hwnd,
        pid,
        thread_id,
        class_name,
        placement,
        normal_anchor: None,
        tool_window: extended_style & WS_EX_TOOLWINDOW.0 != 0,
        restore_target: None,
        outer_rect,
        maximized: unsafe { IsZoomed(hwnd) }.as_bool(),
        moved: false,
    };
    Ok(Some(snapshot))
}

fn move_entry(
    entry: &mut WindowSnapshot,
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
        if !wait_for_zoomed_state(entry, false) {
            return Err(describe(
                entry,
                "window did not leave its maximized state for migration",
            ));
        }
        // A maximized window may keep its restore rectangle on another
        // monitor. SW_RESTORE legitimately moves it there; only the final
        // migrated monitor must match the destination.
        let current = current_rect(entry)?;
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
        if !wait_for_monitor(entry, source_rect) {
            // Chromium windows can report success from SetWindowPlacement while
            // leaving both their outer and normal rectangles unchanged. Force the
            // restored window across with the same SetWindowPos/alignment sequence
            // used by the return path before treating the migration as failed.
            let moved_outer = translated_rect(entry, current, target_rect, source_rect)?;
            set_window_rect(
                entry,
                moved_outer.left,
                moved_outer.top,
                moved_outer.right - moved_outer.left,
                moved_outer.bottom - moved_outer.top,
            )
            .map_err(|error| {
                describe(
                    entry,
                    &format!("restored window fallback move failed: {error}"),
                )
            })?;
            align_normal_rect(entry, virtual_placement.rcNormalPosition)?;
            if !wait_for_monitor(entry, source_rect) {
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
        }
        virtual_placement.flags = entry.placement.flags;
        virtual_placement.showCmd = entry.placement.showCmd;
        unsafe { SetWindowPlacement(entry.hwnd, &virtual_placement) }
            .map_err(|error| describe(entry, &format!("window state move failed: {error}")))?;
        unsafe {
            let _ = ShowWindowAsync(entry.hwnd, SHOW_WINDOW_CMD(entry.placement.showCmd as i32));
        }
        let state_restored = wait_for_zoomed_state(entry, true);
        if !state_restored || !wait_for_monitor(entry, source_rect) {
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

    if !wait_for_monitor(entry, source_rect) {
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

fn restore_maximized(entry: &WindowSnapshot, target_rect: RECT) -> Result<(), String> {
    // The preliminary restore may already have succeeded and survived the
    // topology change. Do not unmaximize and move an already restored window.
    let target_monitor = monitor_for_rect(target_rect, "restore target")?;
    if window_placement(entry).is_ok_and(|placement| {
        maximized_state_matches(
            unsafe { IsZoomed(entry.hwnd) }.as_bool(),
            unsafe { IsIconic(entry.hwnd) }.as_bool(),
            unsafe { MonitorFromWindow(entry.hwnd, MONITOR_DEFAULTTONULL) } == target_monitor,
            placement.rcNormalPosition,
            entry.placement.rcNormalPosition,
        )
    }) {
        log_window_state("restore-already-matched", entry, Some(target_rect));
        return Ok(());
    }
    log_window_state("restore-move-required", entry, Some(target_rect));
    unsafe {
        let _ = ShowWindowAsync(entry.hwnd, SW_RESTORE);
    }
    if !wait_for_zoomed_state(entry, false) {
        return Err(describe_restore_state(
            entry,
            "maximized window could not be restored for migration",
            target_rect,
        ));
    }
    set_window_rect(
        entry,
        entry.outer_rect.left,
        entry.outer_rect.top,
        entry.outer_rect.right - entry.outer_rect.left,
        entry.outer_rect.bottom - entry.outer_rect.top,
    )
    .map_err(|error| {
        describe_restore_state(
            entry,
            &format!("maximized window return move failed: {error}"),
            target_rect,
        )
    })?;
    if !wait_for_monitor(entry, target_rect) {
        return Err(describe_restore_state(
            entry,
            "maximized window did not return to its original monitor",
            target_rect,
        ));
    } // Maximize on the original monitor before restoring rcNormalPosition:
    // the saved normal rectangle is allowed to belong to a different monitor.
    unsafe {
        let _ = ShowWindowAsync(entry.hwnd, SHOW_WINDOW_CMD(entry.placement.showCmd as i32));
    }
    if !wait_for_zoomed_state(entry, true) {
        return Err(describe_restore_state(
            entry,
            "window did not maximize after its return move",
            target_rect,
        ));
    }
    if !wait_for_monitor(entry, target_rect) {
        return Err(describe_restore_state(
            entry,
            "maximized window left its original monitor",
            target_rect,
        ));
    }
    unsafe { SetWindowPlacement(entry.hwnd, &entry.placement) }.map_err(|error| {
        describe(
            entry,
            &format!("maximized placement restore failed: {error}"),
        )
    })?;
    if !wait_for_zoomed_state(entry, true) {
        return Err(describe_restore_state(
            entry,
            "saved placement did not preserve maximization",
            target_rect,
        ));
    }
    if !wait_for_normal_rect(entry, entry.placement.rcNormalPosition) {
        return Err(describe_restore_state(
            entry,
            "saved normal rectangle was not restored",
            target_rect,
        ));
    }
    if !wait_for_monitor(entry, target_rect) {
        return Err(describe_restore_state(
            entry,
            "saved placement moved the maximized window off its original monitor",
            target_rect,
        ));
    }
    Ok(())
}

fn maximized_state_matches(
    zoomed: bool,
    iconic: bool,
    on_target: bool,
    actual_normal: RECT,
    saved_normal: RECT,
) -> bool {
    zoomed && !iconic && on_target && actual_normal == saved_normal
}

fn log_window_state(stage: &str, entry: &WindowSnapshot, target_rect: Option<RECT>) {
    crate::diagnostics::log(
        crate::diagnostics::Level::Debug,
        "windows",
        stage,
        None,
        format!(
            "host-pid={} pid={} hwnd={:?} class={} target={target_rect:?} saved-outer={:?} saved-normal={:?} normal-anchor={:?} saved-maximized={} moved={} same-window={} actual-outer={:?} actual-placement={:?} zoomed={} iconic={} dpi={}",
            std::process::id(),
            entry.pid,
            entry.hwnd,
            entry.class_name,
            entry.outer_rect,
            entry.placement.rcNormalPosition,
            entry.normal_anchor,
            entry.maximized,
            entry.moved,
            same_window(entry),
            current_rect(entry),
            window_placement(entry),
            unsafe { IsZoomed(entry.hwnd) }.as_bool(),
            unsafe { IsIconic(entry.hwnd) }.as_bool(),
            unsafe { GetDpiForWindow(entry.hwnd) },
        ),
    );
}

fn describe_restore_state(entry: &WindowSnapshot, message: &str, target_rect: RECT) -> String {
    describe(
        entry,
        &format!(
            "{message}; target={target_rect:?}; saved-normal={:?}; actual-outer={:?}; actual-placement={:?}; zoomed={}",
            entry.placement.rcNormalPosition,
            current_rect(entry),
            window_placement(entry),
            unsafe { IsZoomed(entry.hwnd) }.as_bool(),
        ),
    )
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

fn wait_for_monitor(entry: &WindowSnapshot, expected_rect: RECT) -> bool {
    let deadline = Instant::now() + MOVE_TIMEOUT;
    loop {
        if !same_window(entry) {
            return true;
        }
        // Display changes can invalidate HMONITOR values. Resolve the current
        // handle from the session's current geometry on every observation.
        let expected = unsafe { MonitorFromRect(&expected_rect, MONITOR_DEFAULTTONULL) };
        if !expected.0.is_null()
            && unsafe { MonitorFromWindow(entry.hwnd, MONITOR_DEFAULTTONULL) } == expected
        {
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
    use super::{RECT, WindowClaimRegistry, WindowIdentity, offset_rect};

    fn identity(hwnd: isize, pid: u32, thread_id: u32, class_name: &str) -> WindowIdentity {
        WindowIdentity {
            hwnd,
            pid,
            thread_id,
            class_name: class_name.into(),
        }
    }

    fn placement_display(
        id: &str,
        left: i32,
        top: i32,
        width: i32,
        height: i32,
        top_inset: i32,
    ) -> super::PlacementDisplay {
        super::PlacementDisplay {
            id: id.into(),
            primary: left == 0 && top == 0,
            rect: RECT {
                left,
                top,
                right: left + width,
                bottom: top + height,
            },
            work: RECT {
                left,
                top: top + top_inset,
                right: left + width,
                bottom: top + height,
            },
        }
    }

    #[test]
    fn firefox_normal_follows_g27k_instead_of_maximized_internal_screen() {
        let before = [
            placement_display("g27k", 0, 0, 3840, 2160, 0),
            placement_display("internal", -2880, -1416, 2880, 1800, 0),
        ];
        let after = [
            placement_display("g27k", -447, -2160, 3840, 2160, 0),
            placement_display("internal", 0, 0, 2880, 1800, 0),
        ];
        let normal = RECT {
            left: 1069,
            top: 0,
            right: 2891,
            bottom: 1706,
        };
        let anchor = super::normal_anchor(normal, false, &before).unwrap();
        assert_eq!(anchor.display_id, "g27k");
        let (shifted, next) = super::rebase_normal(normal, &anchor, false, &after).unwrap();
        assert_eq!(
            shifted,
            RECT {
                left: 622,
                top: -2160,
                right: 2444,
                bottom: -454
            }
        );
        assert_eq!(
            super::rebase_normal(shifted, &next, false, &before)
                .unwrap()
                .0,
            normal
        );
        let on_internal = RECT {
            left: -2879,
            top: -1415,
            right: -1,
            bottom: 287,
        };
        let anchor = super::normal_anchor(on_internal, false, &before).unwrap();
        assert_eq!(anchor.display_id, "internal");
        assert_eq!(
            super::rebase_normal(on_internal, &anchor, false, &after)
                .unwrap()
                .0,
            RECT {
                left: 1,
                top: 1,
                right: 2879,
                bottom: 1703
            }
        );
    }

    #[test]
    fn normal_anchor_accounts_for_taskbar_workspace_but_not_tool_windows() {
        let before = [placement_display("screen", 0, 0, 1920, 1080, 40)];
        let after = [placement_display("screen", -1920, 100, 1920, 1080, 60)];
        let normal = RECT {
            left: 100,
            top: 100,
            right: 900,
            bottom: 700,
        };
        for (tool_window, expected_top) in [(false, 180), (true, 200)] {
            let anchor = super::normal_anchor(normal, tool_window, &before).unwrap();
            let (shifted, _) = super::rebase_normal(normal, &anchor, tool_window, &after).unwrap();
            assert_eq!(shifted.left, -1820);
            assert_eq!(shifted.top, expected_top);
            assert_eq!(shifted.bottom - shifted.top, 600);
        }
        let mut left_before = before;
        let mut left_after = after;
        left_before[0].work.left += 30;
        left_after[0].work.left += 50;
        for (tool_window, expected_left) in [(false, -1840), (true, -1820)] {
            let anchor = super::normal_anchor(normal, tool_window, &left_before).unwrap();
            let (shifted, _) =
                super::rebase_normal(normal, &anchor, tool_window, &left_after).unwrap();
            assert_eq!(shifted.left, expected_left);
        }
    }

    #[test]
    fn vanished_normal_display_returns_to_primary_work_area() {
        let before = [placement_display("normal", 0, 0, 1920, 1080, 0)];
        let after = [placement_display("maximized", 0, 0, 1920, 1080, 0)];
        let normal = RECT {
            left: 100,
            top: 100,
            right: 900,
            bottom: 700,
        };
        let anchor = super::normal_anchor(normal, false, &before).unwrap();
        let (actual, anchor) = super::rebase_normal(normal, &anchor, false, &after).unwrap();
        assert_eq!(
            actual,
            RECT {
                left: 560,
                top: 240,
                right: 1360,
                bottom: 840
            }
        );
        assert_eq!(anchor.display_id, "maximized");
    }

    #[test]
    fn invalid_normal_returns_visible_default_and_updates_nonmaximized_outer() {
        let displays = [placement_display("primary", 0, 0, 1920, 1080, 40)];
        let invalid = RECT {
            left: 9000,
            top: 9000,
            right: 9000,
            bottom: 9000,
        };
        let anchor = super::normal_anchor(invalid, false, &displays).unwrap();
        assert!(anchor.display_id.is_empty());
        let mut entries = [super::WindowSnapshot {
            hwnd: super::HWND::default(),
            pid: 0,
            thread_id: 0,
            class_name: "test".into(),
            placement: super::WINDOWPLACEMENT {
                rcNormalPosition: invalid,
                ..Default::default()
            },
            normal_anchor: Some(anchor),
            tool_window: false,
            restore_target: None,
            outer_rect: invalid,
            maximized: false,
            moved: true,
        }];
        super::shift_saved_rectangles(&mut entries, 0, 0, &displays).unwrap();
        let entry = &entries[0];
        let normal = entry.placement.rcNormalPosition;
        assert_eq!(normal.right - normal.left, 1280);
        assert_eq!(normal.bottom - normal.top, 693);
        assert_eq!(entry.outer_rect, super::offset_rect(normal, 0, 40).unwrap());
        assert_eq!(entry.restore_target, Some(displays[0].rect));
        super::shift_saved_rectangles(&mut entries, 0, 0, &displays).unwrap();
        assert_eq!(entries[0].placement.rcNormalPosition, normal);
    }

    #[test]
    fn resized_display_offscreen_normal_is_clipped_only_by_default_fallback() {
        let before = [placement_display("primary", 0, 0, 3840, 2160, 0)];
        let after = [placement_display("primary", 0, 0, 1280, 720, 40)];
        let normal = RECT {
            left: 2000,
            top: 900,
            right: 3800,
            bottom: 2100,
        };
        let anchor = super::normal_anchor(normal, false, &before).unwrap();
        let (normal, anchor) = super::rebase_normal(normal, &anchor, false, &after).unwrap();
        assert!(anchor.defaulted);
        assert_eq!(
            normal,
            RECT {
                left: 0,
                top: 0,
                right: 1280,
                bottom: 680
            }
        );
        assert!(super::normal_is_visible(normal, false, &after));
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
    fn saved_window_rectangles_follow_target_position_changes() {
        let shifted = offset_rect(
            RECT {
                left: -100,
                top: 50,
                right: 900,
                bottom: 650,
            },
            -1920,
            1080,
        )
        .unwrap();

        assert_eq!(shifted.left, -2020);
        assert_eq!(shifted.top, 1130);
        assert_eq!(shifted.right, -1020);
        assert_eq!(shifted.bottom, 1730);
    }

    #[test]
    fn maximized_restore_only_skips_a_complete_state_match() {
        let saved = RECT {
            left: -1920,
            top: 100,
            right: -1280,
            bottom: 580,
        };
        // The saved restore position may legitimately be on another screen.
        assert!(super::maximized_state_matches(
            true, false, true, saved, saved
        ));
        assert!(!super::maximized_state_matches(
            false, false, true, saved, saved
        ));
        assert!(!super::maximized_state_matches(
            true, true, true, saved, saved
        ));
        assert!(!super::maximized_state_matches(
            true, false, false, saved, saved
        ));
        let changed = RECT {
            left: saved.left + 1,
            ..saved
        };
        assert!(!super::maximized_state_matches(
            true, false, true, changed, saved
        ));
    }

    #[test]
    fn intermediate_restore_preserves_scanner_errors_for_final_reconcile() {
        let mut migration = super::WindowMigration {
            migration_id: super::next_migration_id(),
            entries: super::Arc::new(super::Mutex::new(Vec::new())),
            stop_scanner: super::Arc::new(super::AtomicBool::new(true)),
            scanner: None,
            scanner_errors: vec!["scanner could not snapshot a window".into()],
            restore_on_drop: true,
            target_rect: RECT::default(),
        };
        assert_eq!(
            migration.restore().unwrap_err(),
            "scanner could not snapshot a window"
        );
        assert_eq!(
            migration.scanner_errors,
            ["scanner could not snapshot a window"]
        );
    }
}

#[cfg(test)]
mod native_tests {
    use super::*;
    use windows::Win32::Foundation::WPARAM;
    use windows::Win32::UI::HiDpi::{
        DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetThreadDpiAwarenessContext,
    };
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DispatchMessageW, GetForegroundWindow, GetMessageW, MSG, PostMessageW,
        SW_MAXIMIZE, SW_SHOWNOACTIVATE, ShowWindow, WM_CLOSE, WS_OVERLAPPEDWINDOW,
    };
    use windows::core::w;

    struct TestWindow {
        hwnd: HWND,
        ui_thread: Option<JoinHandle<()>>,
    }
    impl TestWindow {
        fn new(target: RECT) -> Self {
            let (sender, receiver) = std::sync::mpsc::channel();
            let ui_thread = thread::spawn(move || {
                unsafe {
                    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                }
                let hwnd = unsafe {
                    CreateWindowExW(
                        WS_EX_NOACTIVATE | WS_EX_APPWINDOW,
                        w!("STATIC"),
                        w!("SBMS isolated migration regression"),
                        WS_OVERLAPPEDWINDOW,
                        target.left + 100,
                        target.top + 100,
                        480,
                        320,
                        None,
                        None,
                        None,
                        None,
                    )
                }
                .unwrap();
                unsafe {
                    let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                    let _ = ShowWindow(hwnd, SW_MAXIMIZE);
                }
                sender.send(hwnd.0 as isize).unwrap();
                // Match real applications: the window owner keeps dispatching
                // messages while another thread executes the migration.
                let mut message = MSG::default();
                while unsafe { IsWindow(Some(hwnd)) }.as_bool()
                    && unsafe { GetMessageW(&mut message, None, 0, 0) }.0 > 0
                {
                    unsafe {
                        DispatchMessageW(&message);
                    }
                }
            });
            Self {
                hwnd: HWND(receiver.recv().unwrap() as *mut _),
                ui_thread: Some(ui_thread),
            }
        }
    }
    impl Drop for TestWindow {
        fn drop(&mut self) {
            unsafe {
                let _ = PostMessageW(Some(self.hwnd), WM_CLOSE, WPARAM(0), LPARAM(0));
            }
            self.ui_thread.take().unwrap().join().unwrap();
        }
    }

    // Own windows only; no display configuration or third-party window changes.
    // Run explicitly on an interactive machine with two physical displays.
    #[test]
    #[ignore = "creates non-activating test windows on two existing physical displays"]
    fn native_maximized_window_round_trip_preserves_cross_monitor_restore_position() {
        let previous_dpi =
            unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
        let displays: Vec<_> = crate::display::active_displays()
            .unwrap()
            .into_iter()
            .filter(|display| !display.virtual_display)
            .collect();
        assert!(
            displays.len() >= 2,
            "two existing physical displays required"
        );
        for reverse in [false, true] {
            let (target, source) = if reverse {
                (&displays[1], &displays[0])
            } else {
                (&displays[0], &displays[1])
            };
            for cross_monitor_normal in [false, true] {
                let window = TestWindow::new(target.rect);
                let hwnd = window.hwnd;
                let target_monitor = monitor_for_rect(target.rect, "test target").unwrap();
                let mut original = snapshot_window(hwnd, target_monitor, 0).unwrap().unwrap();
                if cross_monitor_normal {
                    original.placement.rcNormalPosition = RECT {
                        left: source.rect.left + 100,
                        top: source.rect.top + 100,
                        right: source.rect.left + 580,
                        bottom: source.rect.top + 420,
                    };
                    unsafe { SetWindowPlacement(hwnd, &original.placement) }.unwrap();
                }
                original = snapshot_window(hwnd, target_monitor, 0).unwrap().unwrap();
                let expected_normal = original.placement.rcNormalPosition;
                assert!(original.maximized);
                assert_ne!(
                    unsafe { GetForegroundWindow() },
                    hwnd,
                    "test must not activate"
                );

                // Demonstrate why requiring the original monitor after restore
                // was wrong, then reconstruct the same original maximized state.
                if cross_monitor_normal {
                    unsafe {
                        let _ = ShowWindow(hwnd, SW_RESTORE);
                    }
                    assert!(wait_for_zoomed_state(&original, false));
                    assert_ne!(
                        unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL) },
                        target_monitor
                    );
                    restore_maximized(&original, target.rect).unwrap();
                }
                original.normal_anchor = Some(
                    normal_anchor(
                        original.placement.rcNormalPosition,
                        original.tool_window,
                        &placement_displays().unwrap(),
                    )
                    .unwrap(),
                );
                move_entry(&mut original, target.rect, source.rect).unwrap();
                assert!(wait_for_zoomed_state(&original, true));
                assert!(wait_for_monitor(&original, source.rect));
                let mut migration = WindowMigration {
                    migration_id: next_migration_id(),
                    entries: Arc::new(Mutex::new(vec![original])),
                    stop_scanner: Arc::new(AtomicBool::new(true)),
                    scanner: None,
                    scanner_errors: Vec::new(),
                    restore_on_drop: true,
                    target_rect: target.rect,
                };
                migration.restore().unwrap();
                migration
                    .reconcile_after_topology_change(Some(target))
                    .unwrap();
                let entries = migration.entries.lock().unwrap();
                let restored = &entries[0];
                assert!(wait_for_zoomed_state(restored, true));
                assert!(wait_for_monitor(restored, target.rect));
                assert!(wait_for_normal_rect(restored, expected_normal));
                assert_ne!(
                    unsafe { GetForegroundWindow() },
                    hwnd,
                    "test must not activate"
                );
                println!(
                    "PASS target={} source={} cross_monitor_normal={} normal={:?}",
                    target.device_name, source.device_name, cross_monitor_normal, expected_normal
                );
                drop(entries);
                drop(migration);
                drop(window);
            }
        }
        unsafe {
            let _ = SetThreadDpiAwarenessContext(previous_dpi);
        }
    }
}

#[cfg(test)]
mod external_uwp_tests {
    use super::*;
    use windows::Win32::UI::HiDpi::{
        DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetThreadDpiAwarenessContext,
    };
    use windows::Win32::UI::WindowsAndMessaging::SW_MAXIMIZE;

    fn legacy_restore_maximized(entry: &WindowSnapshot, target_rect: RECT) -> Result<(), String> {
        unsafe {
            let _ = ShowWindowAsync(entry.hwnd, SW_RESTORE);
        }
        if !wait_for_zoomed_state(entry, false) {
            return Err(describe_restore_state(
                entry,
                "legacy restore failed",
                target_rect,
            ));
        }
        set_window_rect(
            entry,
            entry.outer_rect.left,
            entry.outer_rect.top,
            entry.outer_rect.right - entry.outer_rect.left,
            entry.outer_rect.bottom - entry.outer_rect.top,
        )
        .map_err(|error| error.to_string())?;
        if !wait_for_monitor(entry, target_rect) {
            return Err(describe_restore_state(
                entry,
                "legacy return failed",
                target_rect,
            ));
        }
        unsafe {
            let _ = ShowWindowAsync(entry.hwnd, SHOW_WINDOW_CMD(entry.placement.showCmd as i32));
        }
        if !wait_for_zoomed_state(entry, true) || !wait_for_monitor(entry, target_rect) {
            return Err(describe_restore_state(
                entry,
                "legacy maximize failed",
                target_rect,
            ));
        }
        unsafe { SetWindowPlacement(entry.hwnd, &entry.placement) }
            .map_err(|error| error.to_string())?;
        if !wait_for_zoomed_state(entry, true)
            || !wait_for_normal_rect(entry, entry.placement.rcNormalPosition)
            || !wait_for_monitor(entry, target_rect)
        {
            return Err(describe_restore_state(
                entry,
                "legacy final placement failed",
                target_rect,
            ));
        }
        Ok(())
    }

    // Only use an explicitly launched disposable test app. The caller owns
    // its lifetime; this never selects a user's existing window.
    #[test]
    #[ignore = "requires SBMS_TEST_OWNED_UWP_HWND and SBMS_TEST_OWNED_UWP_PID for a disposable app"]
    fn native_owned_uwp_round_trip() {
        unsafe {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        let hwnd = HWND(
            std::env::var("SBMS_TEST_OWNED_UWP_HWND")
                .unwrap()
                .parse::<usize>()
                .unwrap() as *mut _,
        );
        let expected_pid = std::env::var("SBMS_TEST_OWNED_UWP_PID")
            .unwrap()
            .parse::<u32>()
            .unwrap();
        let mut pid = 0;
        unsafe {
            GetWindowThreadProcessId(hwnd, Some(&mut pid));
        }
        assert_eq!(pid, expected_pid);
        assert_eq!(class_name(hwnd), "ApplicationFrameWindow");
        let displays: Vec<_> = crate::display::active_displays()
            .unwrap()
            .into_iter()
            .filter(|d| !d.virtual_display)
            .collect();
        assert!(displays.len() >= 2);
        for reverse in [false, true] {
            let (target, source) = if reverse {
                (&displays[1], &displays[0])
            } else {
                (&displays[0], &displays[1])
            };
            for cross_monitor_normal in [false, true] {
                unsafe {
                    let _ = ShowWindowAsync(hwnd, SW_RESTORE);
                }
                let current_monitor = unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL) };
                let mut initial = snapshot_window(hwnd, current_monitor, 0).unwrap().unwrap();
                assert!(wait_for_zoomed_state(&initial, false));
                // Fixture setup must finish the app's restore animation before
                // relocating it and capturing the stable pre-migration state.
                thread::sleep(Duration::from_millis(300));
                let mut setup = window_placement(&initial).unwrap();
                setup.showCmd = SW_RESTORE.0 as u32;
                setup.rcNormalPosition = RECT {
                    left: target.rect.left + 100,
                    top: target.rect.top + 100,
                    right: target.rect.left + 700,
                    bottom: target.rect.top + 600,
                };
                unsafe { SetWindowPlacement(hwnd, &setup) }.unwrap();
                assert!(wait_for_monitor(&initial, target.rect));
                thread::sleep(Duration::from_millis(300));
                unsafe {
                    let _ = ShowWindowAsync(hwnd, SW_MAXIMIZE);
                }
                assert!(wait_for_zoomed_state(&initial, true));
                thread::sleep(Duration::from_millis(300));
                let target_monitor = monitor_for_rect(target.rect, "test").unwrap();
                initial = snapshot_window(hwnd, target_monitor, 0).unwrap().unwrap();
                if cross_monitor_normal {
                    let mut placement = initial.placement;
                    placement.rcNormalPosition = RECT {
                        left: source.rect.left + 100,
                        top: source.rect.top + 100,
                        right: source.rect.left + 700,
                        bottom: source.rect.top + 600,
                    };
                    unsafe { SetWindowPlacement(hwnd, &placement) }.unwrap();
                    assert!(wait_for_normal_rect(&initial, placement.rcNormalPosition));
                    initial = snapshot_window(hwnd, target_monitor, 0).unwrap().unwrap();
                }
                println!(
                    "UWP before target={} reverse={} cross-normal={}: outer={:?} placement={:?}",
                    target.device_name,
                    reverse,
                    cross_monitor_normal,
                    initial.outer_rect,
                    initial.placement
                );
                let move_result = move_entry(&mut initial, target.rect, source.rect);
                println!(
                    "UWP migrated: {:?}; outer={:?} placement={:?}",
                    move_result,
                    current_rect(&initial),
                    window_placement(&initial)
                );
                move_result.unwrap();
                let legacy = std::env::var_os("SBMS_TEST_LEGACY_RETURN").is_some();
                let restore_result = if legacy {
                    legacy_restore_maximized(&initial, target.rect)
                } else {
                    restore_maximized(&initial, target.rect)
                };
                println!("UWP legacy-return={legacy}");
                println!(
                    "UWP restored: {:?}; outer={:?} placement={:?}",
                    restore_result,
                    current_rect(&initial),
                    window_placement(&initial)
                );
                restore_result.unwrap();
                assert!(wait_for_zoomed_state(&initial, true));
                assert!(wait_for_monitor(&initial, target.rect));
                assert!(wait_for_normal_rect(
                    &initial,
                    initial.placement.rcNormalPosition
                ));
            }
        }
    }
}
