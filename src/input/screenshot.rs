use std::cell::Cell;
use std::mem::size_of;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{CloseHandle, HANDLE, HWND, LPARAM, POINT, RECT};
use windows::Win32::Graphics::Dwm::{DWMWA_CLOAKED, DwmGetWindowAttribute};
use windows::Win32::System::StationsAndDesktops::{
    CloseDesktop, DESKTOP_CONTROL_FLAGS, DESKTOP_READOBJECTS, GetThreadDesktop,
    GetUserObjectInformationW, HDESK, OpenInputDesktop, UOI_NAME,
};
use windows::Win32::System::Threading::{
    GetCurrentThreadId, OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
    QueryFullProcessImageNameW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumChildWindows, EnumWindows, GetClassNameW, GetWindowThreadProcessId, IsWindow,
    IsWindowVisible,
};
use windows::core::{BOOL, PWSTR};

const START_TIMEOUT: Duration = Duration::from_secs(5);
const CLOSED_SETTLE: Duration = Duration::from_millis(100);

/// Enlarge an owned clip just enough for a cursor transfer. Both the current
/// cursor (inside `clip`) and destination stay legal until placement completes.
pub(super) fn return_clip(clip: RECT, destination: POINT) -> Option<RECT> {
    Some(RECT {
        left: clip.left.min(destination.x),
        top: clip.top.min(destination.y),
        right: clip.right.max(destination.x.checked_add(1)?),
        bottom: clip.bottom.max(destination.y.checked_add(1)?),
    })
}

/// Discard only events from at/before the transfer, not new motion during a
/// fixed quiet period. Expiration prevents the 32-bit message clock from being
/// mistaken for an old event after a long idle or a clock wrap.
pub(super) fn stale_return_motion(time: u32, encoded_tick: u64, now: u64) -> bool {
    let Some(tick) = encoded_tick.checked_sub(1) else {
        return false;
    };
    now.saturating_sub(tick) <= 1_000 && (time.wrapping_sub(tick as u32) as i32) <= 0
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(super) enum Progress {
    Waiting,
    Finished,
    TimedOut,
}

pub(super) struct Session {
    started: Instant,
    observed: bool,
    absent_since: Option<Instant>,
    completed: Option<Progress>,
}

impl Session {
    pub(super) fn new(now: Instant) -> Self {
        Self {
            started: now,
            observed: false,
            absent_since: None,
            completed: None,
        }
    }

    pub(super) fn poll(&mut self, now: Instant, overlay_visible: Option<bool>) -> Progress {
        if let Some(completed) = self.completed {
            return completed;
        }
        match overlay_visible {
            Some(true) => {
                self.observed = true;
                self.absent_since = None;
            }
            Some(false) if self.observed => {
                let absent_since = self.absent_since.get_or_insert(now);
                if now.saturating_duration_since(*absent_since) >= CLOSED_SETTLE {
                    self.completed = Some(Progress::Finished);
                    return Progress::Finished;
                }
            }
            // A failed query cannot contribute to a continuous absence interval.
            _ => self.absent_since = None,
        }
        if !self.observed
            && overlay_visible == Some(false)
            && now.saturating_duration_since(self.started) >= START_TIMEOUT
        {
            self.completed = Some(Progress::TimedOut);
            return Progress::TimedOut;
        }
        Progress::Waiting
    }
}

/// Pure per-scan state for one `EnumWindows` traversal. `observe` returns
/// `false` to stop the enumeration as soon as a real overlay is confirmed;
/// `outcome` folds the traversal result into the established `Option<bool>`
/// semantics where only a fully clean scan may confirm absence.
#[derive(Default)]
struct OverlayScan {
    visible: bool,
    uncertain: bool,
    found: Option<isize>,
}

impl OverlayScan {
    /// Feed one classified window. `false` means "stop the enumeration"; an
    /// uncertain classification keeps scanning but never confirms absence.
    fn observe(&mut self, handle: isize, overlay: Option<bool>) -> bool {
        match overlay {
            Some(true) => {
                self.visible = true;
                self.found = Some(handle);
                false
            }
            Some(false) => true,
            None => {
                self.uncertain = true;
                true
            }
        }
    }

    /// `enumeration_failed` is `EnumWindows`'s own error return. It is only
    /// consulted when no overlay was found, so the FALSE produced by the early
    /// stop (the callback declining further windows after a find) is never
    /// mistaken for a failed enumeration.
    fn outcome(&self, enumeration_failed: bool) -> Option<bool> {
        if self.visible {
            Some(true)
        } else if enumeration_failed || self.uncertain {
            None
        } else {
            Some(false)
        }
    }
}

/// Thread-local cache for the last confirmed overlay handle. The handle is a
/// plain `isize` key so the policy is pure and unit-testable; the caller always
/// re-validates it through the full `window_is_overlay` identity check
/// (class/process/visibility), so a closed, destroyed or reused handle falls
/// back to the full search instead of being trusted.
#[derive(Clone, Copy)]
struct OverlayCache {
    candidate: Option<isize>,
}

impl OverlayCache {
    const fn new() -> Self {
        Self { candidate: None }
    }

    fn visibility(
        &mut self,
        revalidate: impl FnOnce(isize) -> Option<bool>,
        search: impl FnOnce() -> (Option<bool>, Option<isize>),
    ) -> Option<bool> {
        if let Some(candidate) = self.candidate {
            match revalidate(candidate) {
                // Still the live overlay: skip the enumeration entirely.
                Some(true) => return Some(true),
                // Closed, hidden or reused by another window: search again.
                Some(false) => self.candidate = None,
                // A failed re-validation proves nothing. Keep the candidate and
                // the uncertainty: never report a confirmed close.
                None => return None,
            }
        }
        let (visible, found) = search();
        self.candidate = if visible == Some(true) { found } else { None };
        visible
    }
}

thread_local! {
    static OVERLAY_CACHE: Cell<OverlayCache> = const { Cell::new(OverlayCache::new()) };
}

pub(super) fn overlay_visible() -> Option<bool> {
    // While a session is armed, any input-desktop switch must defer restoration.
    // The renderer cannot safely move or recapture the cursor on another desktop.
    // The desktop check runs first on every poll, before the cache.
    if !current_desktop_is_input()? {
        return Some(true);
    }
    OVERLAY_CACHE.with(|cache| {
        // Keep no RefCell borrow alive across Win32 calls: input callbacks can
        // re-enter this thread while a window is being inspected.
        let mut candidate = cache.get();
        let visible = candidate.visibility(
            |handle| window_is_overlay(hwnd_from_key(handle)),
            search_overlay,
        );
        cache.set(candidate);
        visible
    })
}

/// Full enumeration for cache misses: stops at the first confirmed overlay.
fn search_overlay() -> (Option<bool>, Option<isize>) {
    let mut scan = OverlayScan::default();
    let result = unsafe {
        EnumWindows(
            Some(enumerate_window),
            LPARAM((&mut scan as *mut OverlayScan) as isize),
        )
    };
    (scan.outcome(result.is_err()), scan.found)
}

fn hwnd_from_key(handle: isize) -> HWND {
    HWND(handle as *mut _)
}

fn current_desktop_is_input() -> Option<bool> {
    // GetThreadDesktop returns a borrowed handle; only close OpenInputDesktop's handle.
    let current = unsafe { GetThreadDesktop(GetCurrentThreadId()) }.ok()?;
    let current_name = desktop_name(current)?;
    let input =
        unsafe { OpenInputDesktop(DESKTOP_CONTROL_FLAGS(0), false, DESKTOP_READOBJECTS) }.ok()?;
    let input_name = desktop_name(input);
    unsafe {
        let _ = CloseDesktop(input);
    }
    Some(current_name == input_name?)
}

fn desktop_name(desktop: HDESK) -> Option<Vec<u16>> {
    let mut name = [0u16; 256];
    unsafe {
        GetUserObjectInformationW(
            HANDLE(desktop.0),
            UOI_NAME,
            Some(name.as_mut_ptr().cast()),
            size_of::<[u16; 256]>() as u32,
            None,
        )
    }
    .ok()?;
    let length = name.iter().position(|value| *value == 0)?;
    (length != 0).then(|| name[..length].to_vec())
}

unsafe extern "system" fn enumerate_window(window: HWND, parameter: LPARAM) -> BOOL {
    // A confirmed find below returns BOOL(0) and stops this top-level scan too.
    if unsafe { enumerate_overlay(window, parameter) }.0 == 0 {
        return BOOL(0);
    }
    if unsafe { IsWindowVisible(window) }.as_bool() {
        // The precise overlay root can be hosted beneath a framework window.
        // EnumChildWindows already traverses descendants; do not recurse in its callback.
        // Its FALSE return after a find is the stop signal, not an error.
        unsafe {
            let _ = EnumChildWindows(Some(window), Some(enumerate_overlay), parameter);
        }
    }
    // Propagate a find from the child scan to the outer EnumWindows loop.
    let found = unsafe { &*(parameter.0 as *const OverlayScan) }.visible;
    BOOL(if found { 0 } else { 1 })
}

unsafe extern "system" fn enumerate_overlay(window: HWND, parameter: LPARAM) -> BOOL {
    let overlay = window_is_overlay(window);
    let scan = unsafe { &mut *(parameter.0 as *mut OverlayScan) };
    // BOOL(0) stops EnumChildWindows/EnumWindows as soon as a real overlay is
    // confirmed. The found handle is recorded before returning, so the early
    // stop's FALSE can never be mistaken for an enumeration failure.
    BOOL(if scan.observe(window.0 as isize, overlay) {
        1
    } else {
        0
    })
}

fn window_is_overlay(window: HWND) -> Option<bool> {
    if !unsafe { IsWindowVisible(window) }.as_bool() {
        return Some(false);
    }
    let mut class = [0u16; 256];
    let length = unsafe { GetClassNameW(window, &mut class) };
    if length == 0 {
        return if unsafe { IsWindow(Some(window)) }.as_bool() {
            None
        } else {
            Some(false)
        };
    }
    let class = String::from_utf16_lossy(&class[..length as usize]);
    if !is_overlay_class(&class) {
        return Some(false);
    }
    let mut pid = 0;
    if unsafe { GetWindowThreadProcessId(window, Some(&mut pid)) } == 0 {
        return None;
    }
    let image = process_image(pid)?;
    let Some(kind) = overlay_kind(&class, &image) else {
        return Some(false);
    };
    // Snipping Tool's root owns the visible Xaml overlays while remaining cloaked.
    // Its precise class and process identity distinguish it from the image editor.
    if kind == OverlayKind::SnippingToolRoot {
        return kind.visibility(true, None);
    }
    let mut cloaked = 0u32;
    let query = unsafe {
        DwmGetWindowAttribute(
            window,
            DWMWA_CLOAKED,
            (&mut cloaked as *mut u32).cast(),
            size_of::<u32>() as u32,
        )
    };
    kind.visibility(true, query.ok().map(|()| cloaked))
}

fn process_image(pid: u32) -> Option<String> {
    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) }.ok()?;
    let mut image = vec![0u16; 32_768];
    let mut length = image.len() as u32;
    let result = unsafe {
        QueryFullProcessImageNameW(
            process,
            PROCESS_NAME_WIN32,
            PWSTR(image.as_mut_ptr()),
            &mut length,
        )
    };
    unsafe {
        let _ = CloseHandle(process);
    }
    result.ok()?;
    Some(String::from_utf16_lossy(&image[..length as usize]))
}

fn is_overlay_class(class: &str) -> bool {
    matches!(
        class,
        "SnipOverlayRootWindow" | "Windows.UI.Core.CoreWindow"
    )
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum OverlayKind {
    SnippingToolRoot,
    ScreenClippingHost,
}

impl OverlayKind {
    fn visibility(self, visible: bool, cloaked: Option<u32>) -> Option<bool> {
        if !visible {
            Some(false)
        } else if self == Self::SnippingToolRoot {
            Some(true)
        } else {
            cloaked.map(|cloaked| cloaked == 0)
        }
    }
}

fn overlay_kind(class: &str, image: &str) -> Option<OverlayKind> {
    let executable = image.rsplit(['\\', '/']).next().unwrap_or(image);
    match class {
        "SnipOverlayRootWindow" if executable.eq_ignore_ascii_case("SnippingTool.exe") => {
            Some(OverlayKind::SnippingToolRoot)
        }
        "Windows.UI.Core.CoreWindow"
            if executable.eq_ignore_ascii_case("ScreenClippingHost.exe") =>
        {
            Some(OverlayKind::ScreenClippingHost)
        }
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::{
        OverlayCache, OverlayKind, OverlayScan, Progress, Session, overlay_kind, return_clip,
        stale_return_motion,
    };
    use std::cell::Cell;
    use std::time::{Duration, Instant};
    use windows::Win32::Foundation::{POINT, RECT};

    #[test]
    fn return_keeps_both_positions_legal_until_final_placement() {
        let physical = RECT {
            left: -200,
            top: -100,
            right: 0,
            bottom: 0,
        };
        let logical = RECT {
            left: 0,
            top: 0,
            right: 400,
            bottom: 400,
        };
        let start = POINT { x: -100, y: -50 };
        let destination = POINT { x: 200, y: 300 };
        let clamp = |rect: RECT, p: POINT| POINT {
            x: p.x.clamp(rect.left, rect.right - 1),
            y: p.y.clamp(rect.top, rect.bottom - 1),
        };
        // The old order creates a visible, unrelated intermediate edge point.
        assert_eq!(clamp(logical, start), POINT { x: 0, y: 0 });
        let bridge = return_clip(physical, destination).unwrap();
        assert_eq!(clamp(bridge, start), start);
        assert_eq!(clamp(bridge, destination), destination);
        assert_eq!(clamp(logical, destination), destination);
        // An ordinary display needs no repositioning or unnecessary expansion.
        assert_eq!(return_clip(logical, destination), Some(logical));
        // Half-open bounds must include a destination exactly at the old edge.
        let edge = POINT {
            x: physical.right,
            y: physical.bottom,
        };
        assert_eq!(clamp(return_clip(physical, edge).unwrap(), edge), edge);
        assert!(return_clip(physical, POINT { x: i32::MAX, y: 0 }).is_none());
    }

    #[test]
    fn return_fence_rejects_delayed_motion_but_never_waits_for_a_quiet_mouse() {
        let encoded_tick = 1_001;
        for time in [950, 999, 1_000] {
            assert!(stale_return_motion(time, encoded_tick, 1_010));
        }
        // Keep accepting fresh motion even while an older raw packet is late.
        assert!(!stale_return_motion(1_001, encoded_tick, 1_010));
        assert!(stale_return_motion(999, encoded_tick, 1_011));
        assert!(!stale_return_motion(1_010, encoded_tick, 1_011));
        assert!(!stale_return_motion(999, 0, 1_010));
        assert!(!stale_return_motion(999, encoded_tick, 2_001));
    }

    #[test]
    fn return_fence_handles_zero_and_32_bit_message_clock_wrap() {
        assert!(stale_return_motion(0, 1, 0));
        assert!(!stale_return_motion(1, 1, 1));
        let tick = u32::MAX as u64 + 2;
        assert!(stale_return_motion(u32::MAX, tick + 1, tick + 1));
        assert!(stale_return_motion(1, tick + 1, tick + 1));
        assert!(!stale_return_motion(2, tick + 1, tick + 1));
        assert!(!stale_return_motion(0, tick + 1, tick + (1 << 32)));
    }

    #[test]
    fn a_long_selection_has_no_forced_completion() {
        let now = Instant::now();
        let mut session = Session::new(now);
        assert_eq!(session.poll(now, Some(true)), Progress::Waiting);
        assert_eq!(
            session.poll(now + Duration::from_secs(600), Some(true)),
            Progress::Waiting
        );
    }

    #[test]
    fn completion_or_cancel_waits_for_all_overlays_to_stay_absent() {
        let now = Instant::now();
        let mut session = Session::new(now);
        session.poll(now, Some(true));
        assert_eq!(
            session.poll(now + Duration::from_millis(50), Some(false)),
            Progress::Waiting
        );
        assert_eq!(
            session.poll(now + Duration::from_millis(149), Some(false)),
            Progress::Waiting
        );
        assert_eq!(
            session.poll(now + Duration::from_millis(150), Some(false)),
            Progress::Finished
        );
        assert_eq!(
            session.poll(now + Duration::from_millis(200), Some(false)),
            Progress::Finished
        );
    }

    #[test]
    fn a_short_hide_or_failed_query_restarts_the_absence_interval() {
        for interruption in [Some(true), None] {
            let now = Instant::now();
            let mut session = Session::new(now);
            session.poll(now, Some(true));
            session.poll(now + Duration::from_millis(50), Some(false));
            assert_eq!(
                session.poll(now + Duration::from_millis(100), interruption),
                Progress::Waiting
            );
            assert_eq!(
                session.poll(now + Duration::from_millis(150), Some(false)),
                Progress::Waiting
            );
            assert_eq!(
                session.poll(now + Duration::from_millis(250), Some(false)),
                Progress::Finished
            );
        }
    }

    #[test]
    fn startup_timeout_is_distinct_from_observed_completion() {
        let now = Instant::now();
        let mut session = Session::new(now);
        assert_eq!(
            session.poll(now + Duration::from_millis(4_999), Some(false)),
            Progress::Waiting
        );
        assert_eq!(
            session.poll(now + Duration::from_secs(5), None),
            Progress::Waiting
        );
        assert_eq!(
            session.poll(now + Duration::from_secs(5), Some(false)),
            Progress::TimedOut
        );
        assert_eq!(
            session.poll(now + Duration::from_secs(6), Some(false)),
            Progress::TimedOut
        );
        let mut visible_at_deadline = Session::new(now);
        assert_eq!(
            visible_at_deadline.poll(now + Duration::from_secs(5), Some(true)),
            Progress::Waiting
        );
    }

    #[test]
    fn selector_rejects_editors_and_unrelated_core_windows() {
        assert_eq!(
            overlay_kind(
                "Windows.UI.Core.CoreWindow",
                r"C:\Windows\SystemApps\ScreenClippingHost.exe"
            ),
            Some(OverlayKind::ScreenClippingHost)
        );
        assert_eq!(
            overlay_kind(
                "SnipOverlayRootWindow",
                r"C:\Program Files\WindowsApps\Microsoft.ScreenSketch\SnippingTool.exe"
            ),
            Some(OverlayKind::SnippingToolRoot)
        );
        for (class, image) in [
            ("ApplicationFrameWindow", "SnippingTool.exe"),
            ("WinUIDesktopWin32WindowClass", "SnippingTool.exe"),
            ("Microsoft-Windows-SnipperEditor", "SnippingTool.exe"),
            ("Windows.UI.Core.CoreWindow", "SnippingTool.exe"),
            ("Windows.UI.Core.CoreWindow", "SomeOtherApp.exe"),
            ("SnipOverlayRootWindow", "SomeOtherApp.exe"),
        ] {
            assert_eq!(overlay_kind(class, image), None);
        }
    }

    #[test]
    fn modern_overlay_root_can_be_visible_and_cloaked() {
        assert_eq!(
            OverlayKind::SnippingToolRoot.visibility(true, Some(1)),
            Some(true)
        );
        assert_eq!(
            OverlayKind::SnippingToolRoot.visibility(false, Some(1)),
            Some(false)
        );
        assert_eq!(
            OverlayKind::ScreenClippingHost.visibility(true, Some(1)),
            Some(false)
        );
        assert_eq!(OverlayKind::ScreenClippingHost.visibility(true, None), None);
    }

    #[test]
    fn cache_hit_skips_the_full_enumeration() {
        let mut cache = OverlayCache::new();
        assert_eq!(
            cache.visibility(|_| unreachable!(), || (Some(true), Some(7))),
            Some(true)
        );
        let searched = Cell::new(false);
        assert_eq!(
            cache.visibility(
                |handle| {
                    assert_eq!(handle, 7);
                    Some(true)
                },
                || {
                    searched.set(true);
                    (Some(false), None)
                }
            ),
            Some(true)
        );
        assert!(
            !searched.get(),
            "a confirmed cache hit must not scan the desktop"
        );
    }

    #[test]
    fn rejected_cache_falls_back_to_search_and_replaces_the_candidate() {
        let mut cache = OverlayCache::new();
        assert_eq!(
            cache.visibility(|_| unreachable!(), || (Some(true), Some(7))),
            Some(true)
        );
        // Window 7 closed mid-poll: the re-validation rejects it and the full
        // search runs again, caching the overlay it actually found.
        assert_eq!(
            cache.visibility(|_| Some(false), || (Some(true), Some(9))),
            Some(true)
        );
        let searched = Cell::new(false);
        assert_eq!(
            cache.visibility(
                |handle| {
                    assert_eq!(handle, 9, "the replacement candidate must be re-validated");
                    Some(true)
                },
                || {
                    searched.set(true);
                    (Some(false), None)
                }
            ),
            Some(true)
        );
        assert!(!searched.get());
    }

    #[test]
    fn reused_handle_is_not_mistaken_for_the_overlay() {
        let mut cache = OverlayCache::new();
        assert_eq!(
            cache.visibility(|_| unreachable!(), || (Some(true), Some(7))),
            Some(true)
        );
        // Handle 7 was destroyed and reused by a foreign window: the full
        // class/process/visibility re-validation rejects it, so the outcome is
        // the search's own answer, never a false "overlay visible".
        assert_eq!(
            cache.visibility(|_| Some(false), || (Some(false), None)),
            Some(false)
        );
        // The cache is empty again: the next poll searches without a probe.
        assert_eq!(
            cache.visibility(|_| unreachable!(), || (Some(true), Some(8))),
            Some(true)
        );
    }

    #[test]
    fn failed_revalidation_keeps_the_candidate_and_never_confirms_closure() {
        let mut cache = OverlayCache::new();
        assert_eq!(
            cache.visibility(|_| unreachable!(), || (Some(true), Some(7))),
            Some(true)
        );
        let searched = Cell::new(false);
        // A failed identity query is uncertainty: it must not become a
        // confirmed close, and no search may overrule it behind the candidate.
        assert_eq!(
            cache.visibility(
                |_| None,
                || {
                    searched.set(true);
                    (Some(false), None)
                }
            ),
            None
        );
        assert!(!searched.get());
        // The candidate stays cached: the next poll re-validates the same handle.
        assert_eq!(
            cache.visibility(
                |handle| {
                    assert_eq!(handle, 7);
                    Some(true)
                },
                || unreachable!()
            ),
            Some(true)
        );
    }

    #[test]
    fn search_stops_at_the_first_confirmed_overlay() {
        let mut scan = OverlayScan::default();
        assert!(scan.observe(1, Some(false)));
        // An uncertain classification keeps scanning; it must not stop the
        // search before a real overlay is found.
        assert!(scan.observe(2, None));
        // The confirmed overlay stops the enumeration immediately.
        assert!(!scan.observe(3, Some(true)));
        assert_eq!(scan.found, Some(3));
        // Windows after the stop are never classified: the find already won.
        assert_eq!(scan.outcome(false), Some(true));
    }

    #[test]
    fn uncertain_results_are_never_confirmed_closed() {
        // A failed window query keeps the scan uncertain even if the rest is clean.
        let mut scan = OverlayScan::default();
        assert!(scan.observe(1, Some(false)));
        assert!(scan.observe(2, None));
        assert_eq!(scan.outcome(false), None);
        // An unexplained EnumWindows failure without a find is uncertainty too.
        let mut scan = OverlayScan::default();
        assert!(scan.observe(1, Some(false)));
        assert_eq!(scan.outcome(true), None);
        // Only a fully clean scan confirms absence.
        let mut scan = OverlayScan::default();
        assert!(scan.observe(1, Some(false)));
        assert_eq!(scan.outcome(false), Some(false));
    }

    #[test]
    fn an_early_stop_find_is_not_an_enumeration_failure() {
        // EnumWindows reports FALSE when the callback stops it after a find;
        // the recorded find must win over that error return.
        let mut scan = OverlayScan::default();
        assert!(!scan.observe(4, Some(true)));
        assert_eq!(scan.outcome(true), Some(true));
        assert_eq!(scan.found, Some(4));
    }
}
