use std::mem::size_of;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{CloseHandle, HANDLE, HWND, LPARAM};
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

#[derive(Default)]
struct Enumeration {
    visible: bool,
    uncertain: bool,
}

pub(super) fn overlay_visible() -> Option<bool> {
    // While a session is armed, any input-desktop switch must defer restoration.
    // The renderer cannot safely move or recapture the cursor on another desktop.
    if !current_desktop_is_input()? {
        return Some(true);
    }
    let mut enumeration = Enumeration::default();
    let result = unsafe {
        EnumWindows(
            Some(enumerate_window),
            LPARAM((&mut enumeration as *mut Enumeration) as isize),
        )
    };
    if enumeration.visible {
        Some(true)
    } else if result.is_err() || enumeration.uncertain {
        None
    } else {
        Some(false)
    }
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
    let _ = unsafe { enumerate_overlay(window, parameter) };
    if unsafe { IsWindowVisible(window) }.as_bool() {
        // The precise overlay root can be hosted beneath a framework window.
        // EnumChildWindows already traverses descendants; do not recurse in its callback.
        unsafe {
            let _ = EnumChildWindows(Some(window), Some(enumerate_overlay), parameter);
        }
    }
    BOOL(1)
}

unsafe extern "system" fn enumerate_overlay(window: HWND, parameter: LPARAM) -> BOOL {
    let enumeration = unsafe { &mut *(parameter.0 as *mut Enumeration) };
    match window_is_overlay(window) {
        Some(true) => enumeration.visible = true,
        Some(false) => {}
        None => enumeration.uncertain = true,
    }
    BOOL(1)
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
    use super::{OverlayKind, Progress, Session, overlay_kind};
    use std::time::{Duration, Instant};

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
}
