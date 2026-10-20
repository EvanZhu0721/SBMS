use std::cell::RefCell;
use std::mem::size_of;
use std::rc::Rc;

use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use slint::{PhysicalPosition, Window};
use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, POINT, RECT, WPARAM};
use windows::Win32::Graphics::Dwm::{DWMWA_EXTENDED_FRAME_BOUNDS, DwmGetWindowAttribute};
use windows::Win32::Graphics::Gdi::{
    GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromPoint,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, GA_ROOT, GetAncestor, GetClassNameW, GetCursorPos, GetDesktopWindow,
    GetShellWindow, GetWindowRect, HHOOK, IsZoomed, MSLLHOOKSTRUCT, SetForegroundWindow,
    SetWindowsHookExW, UnhookWindowsHookEx, WH_MOUSE_LL, WM_LBUTTONDOWN, WM_MBUTTONDOWN,
    WM_RBUTTONDOWN, WM_XBUTTONDOWN, WindowFromPoint,
};
const FLYOUT_GAP: i32 = 8;

type MouseDownHandler = Rc<dyn Fn(POINT)>;

thread_local! {
    static MOUSE_DOWN_HANDLER: RefCell<Option<MouseDownHandler>> = RefCell::new(None);
}

/// Observes presses without consuming input, even when Windows denied activation.
pub struct OutsideClickObserver(HHOOK);

impl OutsideClickObserver {
    pub fn new(handler: impl Fn(POINT) + 'static) -> windows::core::Result<Self> {
        let hook = unsafe { SetWindowsHookExW(WH_MOUSE_LL, Some(mouse_hook), None, 0) }?;
        MOUSE_DOWN_HANDLER.with(|slot| *slot.borrow_mut() = Some(Rc::new(handler)));
        Ok(Self(hook))
    }
}

impl Drop for OutsideClickObserver {
    fn drop(&mut self) {
        let _ = unsafe { UnhookWindowsHookEx(self.0) };
        MOUSE_DOWN_HANDLER.with(|slot| *slot.borrow_mut() = None);
    }
}

unsafe extern "system" fn mouse_hook(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code >= 0
        && matches!(
            wparam.0 as u32,
            WM_LBUTTONDOWN | WM_RBUTTONDOWN | WM_MBUTTONDOWN | WM_XBUTTONDOWN
        )
    {
        let point = unsafe { (*(lparam.0 as *const MSLLHOOKSTRUCT)).pt };
        let handler = MOUSE_DOWN_HANDLER.with(|slot| slot.borrow().clone());
        if let Some(handler) = handler {
            handler(point);
        }
    }
    unsafe { CallNextHookEx(None, code, wparam, lparam) }
}

pub fn should_dismiss_on_press(window: &Window, point: POINT) -> bool {
    let Some(hwnd) = hwnd(window) else {
        return false;
    };
    let mut rect = RECT::default();
    if unsafe { GetWindowRect(hwnd, &mut rect) }.is_err() {
        return false;
    }
    if !outside_flyout_and_controls(
        rect,
        crate::win32_tray::icon_rect(),
        crate::win32_tray::meter_rect(),
        point,
    ) {
        return false;
    }
    let target = unsafe { GetAncestor(WindowFromPoint(point), GA_ROOT) };
    if target.is_invalid() || target == hwnd {
        return false;
    }
    if target == unsafe { GetDesktopWindow() } || target == unsafe { GetShellWindow() } {
        return true;
    }
    let mut class = [0u16; 256];
    let length = unsafe { GetClassNameW(target, &mut class) } as usize;
    if is_desktop_or_taskbar(&String::from_utf16_lossy(&class[..length])) {
        return true;
    }
    if unsafe { IsZoomed(target) }.as_bool() {
        return true;
    }
    let mut bounds = RECT::default();
    if unsafe {
        DwmGetWindowAttribute(
            target,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            (&mut bounds as *mut RECT).cast(),
            size_of::<RECT>() as u32,
        )
    }
    .is_err()
    {
        return false;
    }
    let monitor = unsafe { MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST) };
    let mut info = MONITORINFO {
        cbSize: size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    unsafe { GetMonitorInfoW(monitor, &mut info) }.as_bool()
        && covers_monitor(bounds, info.rcMonitor)
}

fn is_desktop_or_taskbar(class: &str) -> bool {
    matches!(
        class,
        "Progman" | "WorkerW" | "Shell_TrayWnd" | "Shell_SecondaryTrayWnd"
    )
}

fn covers_monitor(window: RECT, monitor: RECT) -> bool {
    window.left <= monitor.left
        && window.top <= monitor.top
        && window.right >= monitor.right
        && window.bottom >= monitor.bottom
}

fn outside_flyout_and_controls(
    window: RECT,
    icon: Option<RECT>,
    meter: Option<RECT>,
    point: POINT,
) -> bool {
    let contains = |rect: RECT| {
        point.x >= rect.left && point.x < rect.right && point.y >= rect.top && point.y < rect.bottom
    };
    // The tray callback owns toggling. Dismissing on its mouse-down would let
    // the subsequent tray notification reopen the same flyout.
    !contains(window) && !icon.is_some_and(contains) && !meter.is_some_and(contains)
}

pub fn position(window: &Window) {
    let anchor = crate::win32_tray::meter_rect()
        .or_else(crate::win32_tray::icon_rect)
        .or_else(cursor_rect);
    let Some(anchor) = anchor else {
        return;
    };
    let center = POINT {
        x: anchor.left + (anchor.right - anchor.left) / 2,
        y: anchor.top + (anchor.bottom - anchor.top) / 2,
    };
    let monitor = unsafe { MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST) };
    let mut info = MONITORINFO {
        cbSize: size_of::<MONITORINFO>() as u32,
        rcMonitor: RECT::default(),
        rcWork: RECT::default(),
        dwFlags: 0,
    };
    if !unsafe { GetMonitorInfoW(monitor, &mut info) }.as_bool() {
        return;
    }

    let size = window.size();
    let width = size.width as i32;
    let height = size.height as i32;
    let edge = taskbar_edge(info.rcMonitor, info.rcWork, center);
    let (x, y) = match edge {
        Edge::Bottom => (
            center.x - width / 2,
            info.rcWork.bottom - height - FLYOUT_GAP,
        ),
        Edge::Top => (center.x - width / 2, info.rcWork.top + FLYOUT_GAP),
        Edge::Left => (info.rcWork.left + FLYOUT_GAP, center.y - height / 2),
        Edge::Right => (
            info.rcWork.right - width - FLYOUT_GAP,
            center.y - height / 2,
        ),
    };
    let max_x = (info.rcWork.right - width).max(info.rcWork.left);
    let max_y = (info.rcWork.bottom - height).max(info.rcWork.top);
    let x = x.clamp(info.rcWork.left, max_x);
    let y = y.clamp(info.rcWork.top, max_y);
    window.set_position(PhysicalPosition::new(x, y));
}

pub fn activate(window: &Window) -> bool {
    if let Some(hwnd) = hwnd(window) {
        return unsafe { SetForegroundWindow(hwnd) }.as_bool();
    }
    false
}

fn hwnd(window: &Window) -> Option<HWND> {
    let provider = window.window_handle();
    let handle = provider.window_handle().ok()?;
    match handle.as_raw() {
        RawWindowHandle::Win32(handle) => Some(HWND(handle.hwnd.get() as *mut _)),
        _ => None,
    }
}

fn cursor_rect() -> Option<RECT> {
    let mut cursor = POINT::default();
    unsafe { GetCursorPos(&mut cursor) }.ok()?;
    Some(RECT {
        left: cursor.x,
        top: cursor.y,
        right: cursor.x + 1,
        bottom: cursor.y + 1,
    })
}

#[derive(Clone, Copy)]
enum Edge {
    Bottom,
    Top,
    Left,
    Right,
}

fn taskbar_edge(monitor: RECT, work: RECT, anchor: POINT) -> Edge {
    if work.bottom < monitor.bottom {
        return Edge::Bottom;
    }
    if work.top > monitor.top {
        return Edge::Top;
    }
    if work.left > monitor.left {
        return Edge::Left;
    }
    if work.right < monitor.right {
        return Edge::Right;
    }

    let distances = [
        (anchor.y - monitor.top, Edge::Top),
        (monitor.bottom - anchor.y, Edge::Bottom),
        (anchor.x - monitor.left, Edge::Left),
        (monitor.right - anchor.x, Edge::Right),
    ];
    distances
        .into_iter()
        .min_by_key(|(distance, _)| *distance)
        .map(|(_, edge)| edge)
        .unwrap_or(Edge::Bottom)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;
    use windows::Win32::UI::WindowsAndMessaging::WM_LBUTTONUP;

    #[test]
    fn desktop_and_both_taskbars_dismiss_but_ordinary_windows_do_not() {
        for class in [
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
        ] {
            assert!(is_desktop_or_taskbar(class));
        }
        for class in ["CabinetWClass", "Chrome_WidgetWin_1", "#32770"] {
            assert!(!is_desktop_or_taskbar(class));
        }
    }

    #[test]
    fn fullscreen_requires_the_monitor_not_just_the_work_area() {
        let monitor = RECT {
            left: -1920,
            top: 0,
            right: 0,
            bottom: 1080,
        };
        assert!(covers_monitor(monitor, monitor));
        let maximized = RECT {
            bottom: 1032,
            ..monitor
        };
        assert!(!covers_monitor(maximized, monitor));
        let ordinary = RECT {
            left: -1600,
            right: -300,
            ..maximized
        };
        assert!(!covers_monitor(ordinary, monitor));
    }

    #[test]
    fn outside_press_excludes_flyout_and_tray_toggle() {
        let window = RECT {
            left: -400,
            top: 100,
            right: -16,
            bottom: 644,
        };
        let icon = RECT {
            left: -48,
            top: 660,
            right: -16,
            bottom: 692,
        };
        for point in [POINT { x: -400, y: 100 }, POINT { x: -17, y: 643 }] {
            assert!(!outside_flyout_and_controls(
                window,
                Some(icon),
                None,
                point
            ));
        }
        assert!(!outside_flyout_and_controls(
            window,
            Some(icon),
            None,
            POINT { x: -32, y: 676 }
        ));
        for point in [POINT { x: -16, y: 200 }, POINT { x: -401, y: 200 }] {
            assert!(outside_flyout_and_controls(window, Some(icon), None, point));
        }
        assert!(outside_flyout_and_controls(
            window,
            None,
            None,
            POINT { x: -32, y: 676 }
        ));
        let meter = RECT {
            left: -140,
            right: -60,
            ..icon
        };
        assert!(!outside_flyout_and_controls(
            window,
            Some(icon),
            Some(meter),
            POINT { x: -100, y: 676 }
        ));
    }

    #[test]
    fn native_observer_dispatches_presses_without_a_focus_event() {
        let presses = Rc::new(Cell::new(0));
        let observed = presses.clone();
        let observer = OutsideClickObserver::new(move |point| {
            assert_eq!((point.x, point.y), (-20, 700));
            observed.set(observed.get() + 1);
        })
        .unwrap();
        let mouse = MSLLHOOKSTRUCT {
            pt: POINT { x: -20, y: 700 },
            ..Default::default()
        };
        let data = LPARAM((&mouse as *const MSLLHOOKSTRUCT) as isize);
        for message in [
            WM_LBUTTONDOWN,
            WM_RBUTTONDOWN,
            WM_MBUTTONDOWN,
            WM_XBUTTONDOWN,
        ] {
            unsafe { mouse_hook(0, WPARAM(message as usize), data) };
        }
        unsafe { mouse_hook(0, WPARAM(WM_LBUTTONUP as usize), data) };
        unsafe { mouse_hook(-1, WPARAM(WM_LBUTTONDOWN as usize), data) };
        assert_eq!(presses.get(), 4);
        drop(observer);
        assert!(MOUSE_DOWN_HANDLER.with(|slot| slot.borrow().is_none()));
    }
}
