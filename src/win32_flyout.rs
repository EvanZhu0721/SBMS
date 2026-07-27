use std::mem::size_of;

use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use slint::{PhysicalPosition, Window};
use windows::Win32::Foundation::{HWND, POINT, RECT};
use windows::Win32::Graphics::Dwm::{
    DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND, DwmSetWindowAttribute,
};
use windows::Win32::Graphics::Gdi::{
    GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromPoint,
};
use windows::Win32::System::Threading::GetCurrentProcessId;
use windows::Win32::UI::Shell::{NOTIFYICONIDENTIFIER, Shell_NotifyIconGetRect};
use windows::Win32::UI::WindowsAndMessaging::{
    FindWindowExW, GWL_EXSTYLE, GetCursorPos, GetForegroundWindow, GetWindowLongPtrW,
    GetWindowThreadProcessId, HWND_MESSAGE, SWP_FRAMECHANGED, SWP_NOMOVE, SWP_NOSIZE, SWP_NOZORDER,
    SetForegroundWindow, SetWindowLongPtrW, SetWindowPos, WS_EX_APPWINDOW, WS_EX_TOOLWINDOW,
};
use windows::core::{GUID, PCWSTR, w};

const TRAY_WINDOW_CLASS: PCWSTR = w!("SlintSystemTrayWindow");
const SLINT_TRAY_ICON_ID: u32 = 1;
const FLYOUT_GAP: i32 = 8;

pub fn configure(window: &Window) {
    let Some(hwnd) = hwnd(window) else {
        return;
    };
    let current = unsafe { GetWindowLongPtrW(hwnd, GWL_EXSTYLE) };
    let updated = (current | WS_EX_TOOLWINDOW.0 as isize) & !(WS_EX_APPWINDOW.0 as isize);
    if updated != current {
        unsafe {
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, updated);
            let _ = SetWindowPos(
                hwnd,
                None,
                0,
                0,
                0,
                0,
                SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER,
            );
        }
    }

    let preference = DWMWCP_ROUND;
    unsafe {
        let _ = DwmSetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            std::ptr::from_ref(&preference).cast(),
            size_of_val(&preference) as u32,
        );
    }
}

pub fn position(window: &Window) {
    let anchor = tray_icon_rect().or_else(cursor_rect);
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
    let x = x.clamp(info.rcWork.left, info.rcWork.right - width);
    let y = y.clamp(info.rcWork.top, info.rcWork.bottom - height);
    window.set_position(PhysicalPosition::new(x, y));
}

pub fn activate(window: &Window) {
    if let Some(hwnd) = hwnd(window) {
        unsafe {
            let _ = SetForegroundWindow(hwnd);
        }
    }
}

pub fn lost_focus(window: &Window) -> bool {
    if !window.is_visible() {
        return false;
    }
    let foreground = unsafe { GetForegroundWindow() };
    if foreground.0.is_null() {
        return false;
    }
    let mut process_id = 0;
    unsafe { GetWindowThreadProcessId(foreground, Some(&mut process_id)) };
    process_id != unsafe { GetCurrentProcessId() }
}

fn hwnd(window: &Window) -> Option<HWND> {
    let provider = window.window_handle();
    let handle = provider.window_handle().ok()?;
    match handle.as_raw() {
        RawWindowHandle::Win32(handle) => Some(HWND(handle.hwnd.get() as *mut _)),
        _ => None,
    }
}

fn tray_icon_rect() -> Option<RECT> {
    let mut previous = None;
    loop {
        let tray_window = unsafe {
            FindWindowExW(
                Some(HWND_MESSAGE),
                previous,
                TRAY_WINDOW_CLASS,
                PCWSTR::null(),
            )
        }
        .ok()?;
        let mut process_id = 0;
        unsafe { GetWindowThreadProcessId(tray_window, Some(&mut process_id)) };
        if process_id == unsafe { GetCurrentProcessId() } {
            let identifier = NOTIFYICONIDENTIFIER {
                cbSize: size_of::<NOTIFYICONIDENTIFIER>() as u32,
                hWnd: tray_window,
                uID: SLINT_TRAY_ICON_ID,
                guidItem: GUID::zeroed(),
            };
            return unsafe { Shell_NotifyIconGetRect(&identifier) }.ok();
        }
        previous = Some(tray_window);
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
