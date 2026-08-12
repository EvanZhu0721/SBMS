use std::mem::size_of;

use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use slint::{PhysicalPosition, Window};
use windows::Win32::Foundation::{HWND, POINT, RECT};
use windows::Win32::Graphics::Gdi::{
    GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromPoint,
};
use windows::Win32::System::Threading::GetCurrentProcessId;
use windows::Win32::UI::WindowsAndMessaging::{
    GetCursorPos, GetForegroundWindow, GetWindowThreadProcessId, SetForegroundWindow,
};
const FLYOUT_GAP: i32 = 8;

pub fn position(window: &Window) {
    let anchor = crate::win32_tray::icon_rect().or_else(cursor_rect);
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

pub fn lost_focus(window: &Window) -> bool {
    if !window.is_visible() {
        return false;
    }
    matches!(foreground_belongs_to_current_process(), Some(false))
}

pub fn has_focus(window: &Window) -> bool {
    window.is_visible() && matches!(foreground_belongs_to_current_process(), Some(true))
}

fn foreground_belongs_to_current_process() -> Option<bool> {
    let foreground = unsafe { GetForegroundWindow() };
    if foreground.0.is_null() {
        return None;
    }
    let mut process_id = 0;
    unsafe { GetWindowThreadProcessId(foreground, Some(&mut process_id)) };
    Some(process_id == unsafe { GetCurrentProcessId() })
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
