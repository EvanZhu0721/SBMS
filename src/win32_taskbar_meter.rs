use std::cell::Cell;
use std::mem::size_of;
use std::sync::atomic::{AtomicIsize, Ordering};

use windows::Win32::Foundation::{COLORREF, HWND, LPARAM, LRESULT, POINT, RECT, WPARAM};
use windows::Win32::Graphics::Gdi::{
    BeginPaint, CreateSolidBrush, DeleteObject, EndPaint, FillRect, InvalidateRect, PAINTSTRUCT,
    ScreenToClient,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Registry::{HKEY_CURRENT_USER, RRF_RT_REG_DWORD, RegGetValueW};
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT, GetDpiForWindow, GetWindowDpiAwarenessContext,
    SetThreadDpiAwarenessContext,
};
use windows::Win32::UI::WindowsAndMessaging::*;
use windows::core::{PCWSTR, w};

use super::{TrayMonitor, WM_METER_MENU, WM_METER_TOGGLE};

const CLASS: PCWSTR = w!("SbmsTaskbarMeter");
const TRANSPARENT: COLORREF = COLORREF(0x00010001);
static CURRENT_METER: AtomicIsize = AtomicIsize::new(0);

struct DpiContext(DPI_AWARENESS_CONTEXT);
impl DpiContext {
    fn for_window(hwnd: HWND) -> Self {
        Self(unsafe { SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd)) })
    }
}
impl Drop for DpiContext {
    fn drop(&mut self) {
        unsafe { SetThreadDpiAwarenessContext(self.0) };
    }
}

struct State {
    host: HWND,
    monitor: Cell<TrayMonitor>,
    light: Cell<bool>,
}

pub(super) struct TaskbarMeter {
    hwnd: Cell<HWND>,
    state: Box<State>,
}

impl TaskbarMeter {
    pub(super) fn new(host: HWND) -> Self {
        Self {
            hwnd: Cell::new(HWND::default()),
            state: Box::new(State {
                host,
                monitor: Cell::new(TrayMonitor::default()),
                light: Cell::new(false),
            }),
        }
    }

    pub(super) fn refresh(&self, monitor: TrayMonitor) {
        let Ok(taskbar) = (unsafe { FindWindowW(w!("Shell_TrayWnd"), PCWSTR::null()) }) else {
            return;
        };
        // Match Explorer while creating and positioning the child, then restore Slint's context.
        let _dpi = DpiContext::for_window(taskbar);
        let mut parent_rect = RECT::default();
        if unsafe { GetClientRect(taskbar, &mut parent_rect) }.is_err() {
            return;
        }
        let Ok(notify) =
            (unsafe { FindWindowExW(Some(taskbar), None, w!("TrayNotifyWnd"), PCWSTR::null()) })
        else {
            return;
        };
        let mut notify_rect = RECT::default();
        if unsafe { GetWindowRect(notify, &mut notify_rect) }.is_err() {
            return;
        }
        let mut point = POINT {
            x: notify_rect.left,
            y: notify_rect.top,
        };
        if !unsafe { ScreenToClient(taskbar, &mut point) }.as_bool() {
            return;
        }
        let Some(rect) = layout(parent_rect, point.x, unsafe { GetDpiForWindow(taskbar) }) else {
            return;
        };
        let light = light_taskbar();
        let changed =
            self.state.monitor.replace(monitor) != monitor || self.state.light.get() != light;
        self.state.light.set(light);
        let mut hwnd = self.hwnd.get();
        if !unsafe { IsWindow(Some(hwnd)) }.as_bool()
            || unsafe { GetParent(hwnd) }.ok() != Some(taskbar)
        {
            if unsafe { IsWindow(Some(hwnd)) }.as_bool() {
                let _ = unsafe { DestroyWindow(hwnd) };
            }
            let Ok(instance) = (unsafe { GetModuleHandleW(None) }) else {
                return;
            };
            let class = WNDCLASSW {
                lpfnWndProc: Some(window_proc),
                hInstance: instance.into(),
                lpszClassName: CLASS,
                ..Default::default()
            };
            unsafe { RegisterClassW(&class) };
            // Creating directly as a child avoids temporarily reparenting a top-level Slint window.
            let created = unsafe {
                CreateWindowExW(
                    WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                    CLASS,
                    w!("SBMS frame rate"),
                    WS_CHILD,
                    rect.left,
                    rect.top,
                    rect.right - rect.left,
                    rect.bottom - rect.top,
                    Some(taskbar),
                    None,
                    Some(instance.into()),
                    None,
                )
            };
            let created = match created {
                Ok(created) => created,
                Err(error) => {
                    let message = format!(
                        "CreateWindowExW taskbar child failed: {error}; parent={taskbar:?} rect={rect:?}"
                    );
                    super::diagnostics::log(
                        super::Level::Warn,
                        "tray",
                        "taskbar-meter-create",
                        None,
                        &message,
                    );
                    return;
                }
            };
            hwnd = created;
            self.hwnd.set(hwnd);
            unsafe {
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, &*self.state as *const State as isize);
                let _ = SetLayeredWindowAttributes(hwnd, TRANSPARENT, 255, LWA_COLORKEY);
            }
            CURRENT_METER.store(hwnd.0 as isize, Ordering::Release);
        }
        unsafe {
            let _ = SetWindowPos(
                hwnd,
                Some(HWND_TOP),
                rect.left,
                rect.top,
                rect.right - rect.left,
                rect.bottom - rect.top,
                SWP_NOACTIVATE | SWP_SHOWWINDOW,
            );
            if changed {
                let _ = InvalidateRect(Some(hwnd), None, false);
            }
        }
    }
}

impl Drop for TaskbarMeter {
    fn drop(&mut self) {
        let hwnd = self.hwnd.get();
        CURRENT_METER.store(0, Ordering::Release);
        if unsafe { IsWindow(Some(hwnd)) }.as_bool() {
            let _ = unsafe { DestroyWindow(hwnd) };
        }
    }
}

pub(super) fn meter_rect() -> Option<RECT> {
    let hwnd = HWND(CURRENT_METER.load(Ordering::Acquire) as *mut _);
    if !unsafe { IsWindowVisible(hwnd) }.as_bool() {
        return None;
    }
    let _dpi = DpiContext::for_window(hwnd);
    let mut rect = RECT::default();
    unsafe { GetWindowRect(hwnd, &mut rect) }.ok()?;
    Some(rect)
}

fn layout(parent: RECT, notify_left: i32, dpi: u32) -> Option<RECT> {
    let height = parent.bottom - parent.top;
    let scale = (height as f64 / 36.0).min(dpi.max(96) as f64 / 96.0);
    let width = (58.0 * scale).round() as i32;
    let gap = (4.0 * scale).round() as i32;
    let left = notify_left - gap - width;
    (height > 0 && width > 0 && left >= parent.left && notify_left <= parent.right).then_some(
        RECT {
            left,
            top: parent.top,
            right: left + width,
            bottom: parent.bottom,
        },
    )
}

fn light_taskbar() -> bool {
    let mut light = 0u32;
    let mut size = size_of::<u32>() as u32;
    unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            w!("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"),
            w!("SystemUsesLightTheme"),
            RRF_RT_REG_DWORD,
            None,
            Some((&mut light as *mut u32).cast()),
            Some(&mut size),
        )
    }
    .is_ok()
        && light != 0
}

unsafe extern "system" fn window_proc(hwnd: HWND, message: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let pointer = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *const State;
    let state = unsafe { pointer.as_ref() };
    match message {
        WM_MOUSEACTIVATE => return LRESULT(MA_NOACTIVATE as isize),
        WM_ERASEBKGND => return LRESULT(1),
        WM_LBUTTONUP | WM_RBUTTONUP => {
            if let Some(state) = state {
                let message = if message == WM_LBUTTONUP {
                    WM_METER_TOGGLE
                } else {
                    WM_METER_MENU
                };
                let _ = unsafe { PostMessageW(Some(state.host), message, WPARAM(0), LPARAM(0)) };
            }
            return LRESULT(0);
        }
        WM_PAINT => {
            let mut paint = PAINTSTRUCT::default();
            let dc = unsafe { BeginPaint(hwnd, &mut paint) };
            let mut rect = RECT::default();
            let _ = unsafe { GetClientRect(hwnd, &mut rect) };
            let background = unsafe { CreateSolidBrush(TRANSPARENT) };
            unsafe {
                FillRect(dc, &rect, background);
                let _ = DeleteObject(background.into());
            }
            if let Some(state) = state {
                let monitor = state.monitor.get();
                let color = match (state.light.get(), monitor.error) {
                    (true, false) => COLORREF(0x00363234),
                    (true, true) => COLORREF(0x001a1aba),
                    (false, false) => COLORREF(0x00cacaca),
                    (false, true) => COLORREF(0x00abb4ff),
                };
                let brush = unsafe { CreateSolidBrush(color) };
                let scale = (rect.right - rect.left) as f64 / 58.0;
                let y = ((rect.bottom - rect.top) as f64 - 24.0 * scale) / 2.0;
                let segments = [
                    (2, 0, 10, 2),
                    (12, 2, 2, 9),
                    (12, 13, 2, 9),
                    (2, 22, 10, 2),
                    (0, 13, 2, 9),
                    (0, 2, 2, 9),
                    (2, 11, 10, 2),
                ];
                for (digit, glyph) in monitor.glyphs().into_iter().enumerate() {
                    for (bit, (x, sy, w, h)) in segments.into_iter().enumerate() {
                        if glyph & (1 << bit) == 0 {
                            continue;
                        }
                        let x = (x + 4 + digit * 18) as f64 * scale;
                        let top = y + sy as f64 * scale;
                        let segment = RECT {
                            left: x.round() as i32,
                            top: top.round() as i32,
                            right: (x + w as f64 * scale).round() as i32,
                            bottom: (top + h as f64 * scale).round() as i32,
                        };
                        unsafe { FillRect(dc, &segment, brush) };
                    }
                }
                let _ = unsafe { DeleteObject(brush.into()) };
            }
            let _ = unsafe { EndPaint(hwnd, &paint) };
            return LRESULT(0);
        }
        WM_NCDESTROY => {
            unsafe { SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0) };
        }
        _ => {}
    }
    unsafe { DefWindowProcW(hwnd, message, wp, lp) }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicU32;

    static PREVIEW_ACTIONS: AtomicU32 = AtomicU32::new(0);

    unsafe extern "system" fn preview_host_proc(
        hwnd: HWND,
        message: u32,
        wp: WPARAM,
        lp: LPARAM,
    ) -> LRESULT {
        if message == WM_METER_TOGGLE || message == WM_METER_MENU {
            PREVIEW_ACTIONS.fetch_or(
                if message == WM_METER_TOGGLE { 1 } else { 2 },
                Ordering::Relaxed,
            );
            return LRESULT(0);
        }
        unsafe { DefWindowProcW(hwnd, message, wp, lp) }
    }

    #[test]
    #[ignore = "shows an isolated taskbar preview"]
    fn taskbar_meter_preview() {
        use std::io::Write;
        use std::time::{Duration, Instant};
        PREVIEW_ACTIONS.store(0, Ordering::Relaxed);
        let instance = unsafe { GetModuleHandleW(None) }.unwrap();
        let class = WNDCLASSW {
            lpfnWndProc: Some(preview_host_proc),
            hInstance: instance.into(),
            lpszClassName: w!("SbmsMeterPreviewHost"),
            ..Default::default()
        };
        unsafe { RegisterClassW(&class) };
        let host = unsafe {
            CreateWindowExW(
                WINDOW_EX_STYLE::default(),
                class.lpszClassName,
                PCWSTR::null(),
                WINDOW_STYLE::default(),
                0,
                0,
                0,
                0,
                Some(HWND_MESSAGE),
                None,
                Some(instance.into()),
                None,
            )
        }
        .unwrap();
        let meter = TaskbarMeter::new(host);
        let started = Instant::now();
        let seconds = std::env::var("SBMS_TASKBAR_PREVIEW_SECONDS")
            .ok()
            .and_then(|value| value.parse::<u64>().ok())
            .unwrap_or(15);
        let mut printed = false;
        while started.elapsed() < Duration::from_secs(seconds) {
            let sample = started.elapsed().as_secs() % 6;
            let monitor = TrayMonitor {
                fps: match sample {
                    0 => 60,
                    1 => 120,
                    2 => 999,
                    _ => 0,
                },
                running: sample != 5,
                valid: true,
                error: sample == 3,
                not_a_number: sample == 4,
            };
            meter.refresh(monitor);
            let mut message = MSG::default();
            while unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool() {
                unsafe {
                    let _ = TranslateMessage(&message);
                    DispatchMessageW(&message);
                }
            }
            if !printed {
                let hwnd = meter.hwnd.get();
                assert!(!hwnd.is_invalid(), "taskbar meter could not be created");
                let taskbar = unsafe { FindWindowW(w!("Shell_TrayWnd"), PCWSTR::null()) }.unwrap();
                assert_eq!(unsafe { GetParent(hwnd) }.unwrap(), taskbar);
                assert_eq!(unsafe { GetDpiForWindow(hwnd) }, unsafe {
                    GetDpiForWindow(taskbar)
                });
                // Exercise only our own window's message route; no mouse or keyboard input.
                unsafe {
                    SendMessageW(hwnd, WM_LBUTTONUP, None, None);
                    SendMessageW(hwnd, WM_RBUTTONUP, None, None);
                }
                println!(
                    "meter={hwnd:?} parent={:?} dpi={} rect={:?}",
                    unsafe { GetParent(hwnd) },
                    unsafe { GetDpiForWindow(hwnd) },
                    meter_rect()
                );
                std::io::stdout().flush().unwrap();
                printed = true;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        let hwnd = meter.hwnd.get();
        drop(meter);
        assert!(!unsafe { IsWindow(Some(hwnd)) }.as_bool());
        assert_eq!(PREVIEW_ACTIONS.load(Ordering::Relaxed), 3);
        unsafe { DestroyWindow(host) }.unwrap();
        println!("preview ended; native window destroyed; left/right message routes passed");
    }

    #[test]
    fn meter_scales_to_taskbar_and_stays_before_notification_area() {
        let parent = RECT {
            left: 0,
            top: 0,
            right: 5120,
            bottom: 96,
        };
        let rect = layout(parent, 4600, 192).unwrap();
        assert_eq!(
            (rect.left, rect.top, rect.right, rect.bottom),
            (4476, 0, 4592, 96)
        );
        assert!(layout(parent, 60, 192).is_none());
        let compact = layout(
            RECT {
                bottom: 24,
                ..parent
            },
            4600,
            192,
        )
        .unwrap();
        assert!(compact.right - compact.left < rect.right - rect.left);
    }
}
