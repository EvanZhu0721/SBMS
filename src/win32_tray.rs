use std::cell::Cell;
use std::error::Error;
use std::io;
use std::mem::size_of;
use std::sync::atomic::{AtomicBool, AtomicIsize, AtomicU32, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};

use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, POINT, RECT, WPARAM};
use windows::Win32::Graphics::Gdi::{
    BI_RGB, BITMAPINFO, BITMAPINFOHEADER, CreateBitmap, CreateDIBSection, DIB_RGB_COLORS,
    DeleteObject, GetDC, ReleaseDC,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Shell::{
    NIF_ICON, NIF_MESSAGE, NIF_SHOWTIP, NIF_TIP, NIM_ADD, NIM_DELETE, NIM_MODIFY, NIM_SETFOCUS,
    NIM_SETVERSION, NOTIFYICON_VERSION_4, NOTIFYICONDATAW, NOTIFYICONDATAW_0, NOTIFYICONIDENTIFIER,
    Shell_NotifyIconGetRect, Shell_NotifyIconW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, ChangeWindowMessageFilterEx, CreateIconIndirect, CreatePopupMenu, CreateWindowExW,
    DefWindowProcW, DestroyIcon, DestroyMenu, DestroyWindow, FindWindowW, GWLP_USERDATA,
    GetWindowLongPtrW, HICON, HMENU, ICONINFO, KillTimer, MF_SEPARATOR, MF_STRING, MSGFLT_ALLOW,
    PostMessageW, RegisterClassW, RegisterWindowMessageW, SetForegroundWindow, SetTimer,
    SetWindowLongPtrW, TPM_BOTTOMALIGN, TPM_LEFTALIGN, TPM_RETURNCMD, TPM_RIGHTBUTTON,
    TrackPopupMenu, WM_APP, WM_CONTEXTMENU, WM_NULL, WM_TIMER, WNDCLASSW, WS_EX_NOACTIVATE,
    WS_EX_TOOLWINDOW, WS_POPUP,
};
use windows::core::{BOOL, PCWSTR, w};

use crate::diagnostics::{self, Level};

const CLASS_NAME: PCWSTR = w!("SbmsTrayHostWindow");
const TRAY_ICON_ID: u32 = 1;
const WM_TRAY_ICON: u32 = WM_APP + 1;
const WM_TRAY_STATUS: u32 = WM_APP + 2;
const NIN_SELECT: u32 = 0x0400;
const NIN_KEYSELECT: u32 = NIN_SELECT + 1;
const OPEN_COMMAND: u32 = 1;
const EXIT_COMMAND: u32 = 2;
const MAINTENANCE_TIMER_ID: usize = 1;
const RETRY_DELAYS_MS: [u32; 6] = [250, 500, 1_000, 2_000, 4_000, 8_000];
const RECOVERY_RETRY_MS: u32 = 30_000;
const HEALTH_CHECK_MS: u32 = 5_000;

static CLASS_REGISTERED: AtomicBool = AtomicBool::new(false);
static TASKBAR_CREATED_MESSAGE: AtomicU32 = AtomicU32::new(0);
static OPEN_MESSAGE: AtomicU32 = AtomicU32::new(0);
static CURRENT_HWND: AtomicIsize = AtomicIsize::new(0);

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TrayAction {
    Toggle,
    Open,
    Quit,
}

#[derive(Clone)]
pub struct TrayHandle(Arc<TrayState>);

impl TrayHandle {
    pub fn set_status(&self, status: &str) {
        let mut state = lock_state(&self.0);
        state.tooltip = format!("SBMS · {status}");
        let raw = state.hwnd;
        if raw != 0 {
            let _ = unsafe {
                PostMessageW(
                    Some(HWND(raw as *mut _)),
                    WM_TRAY_STATUS,
                    WPARAM(0),
                    LPARAM(0),
                )
            };
        }
    }
}

pub struct NativeTray {
    inner: Box<Inner>,
}

struct Inner {
    hwnd: HWND,
    hicon: HICON,
    hmenu: HMENU,
    state: Arc<TrayState>,
    registered: Cell<bool>,
    next_retry: Cell<usize>,
    recovery_announced: Cell<bool>,
    registration_reason: Cell<&'static str>,
    action: Box<dyn Fn(TrayAction)>,
}

struct TrayState {
    shared: Mutex<SharedTrayState>,
}

struct SharedTrayState {
    hwnd: isize,
    tooltip: String,
}

impl NativeTray {
    pub fn new(action: impl Fn(TrayAction) + 'static) -> Result<Self, Box<dyn Error>> {
        ensure_class_registered()?;
        let taskbar_created = taskbar_created_message();
        if taskbar_created == 0 {
            return Err(io::Error::last_os_error().into());
        }
        let open_message = open_message();
        if open_message == 0 {
            return Err(io::Error::last_os_error().into());
        }

        let hinstance = unsafe { GetModuleHandleW(None) }?;
        // TaskbarCreated is a broadcast and does not reach HWND_MESSAGE windows. A hidden
        // tool window remains absent from the taskbar while receiving Explorer broadcasts.
        let hwnd = unsafe {
            CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                CLASS_NAME,
                PCWSTR::null(),
                WS_POPUP,
                0,
                0,
                0,
                0,
                None,
                None,
                Some(hinstance.into()),
                None,
            )
        }?;
        // The tray runs elevated, while Explorer normally does not. Explicitly allow the
        // shell callback and recreation messages through UIPI for this process-owned window.
        for message in [taskbar_created, WM_TRAY_ICON, open_message] {
            if let Err(error) =
                unsafe { ChangeWindowMessageFilterEx(hwnd, message, MSGFLT_ALLOW, None) }
            {
                let _ = unsafe { DestroyWindow(hwnd) };
                return Err(error.into());
            }
        }

        let hicon = match create_hicon() {
            Ok(icon) => icon,
            Err(error) => {
                let _ = unsafe { DestroyWindow(hwnd) };
                return Err(error);
            }
        };
        let hmenu = match create_menu() {
            Ok(menu) => menu,
            Err(error) => {
                let _ = unsafe { DestroyIcon(hicon) };
                let _ = unsafe { DestroyWindow(hwnd) };
                return Err(error.into());
            }
        };

        let state = Arc::new(TrayState {
            shared: Mutex::new(SharedTrayState {
                hwnd: hwnd.0 as isize,
                tooltip: "SBMS · Stopped".into(),
            }),
        });
        let inner = Box::new(Inner {
            hwnd,
            hicon,
            hmenu,
            state,
            registered: Cell::new(false),
            next_retry: Cell::new(0),
            recovery_announced: Cell::new(false),
            registration_reason: Cell::new("startup"),
            action: Box::new(action),
        });
        unsafe { SetWindowLongPtrW(hwnd, GWLP_USERDATA, &*inner as *const Inner as _) };
        CURRENT_HWND.store(hwnd.0 as isize, Ordering::Release);
        inner.begin_registration("startup");

        Ok(Self { inner })
    }

    pub fn handle(&self) -> TrayHandle {
        TrayHandle(Arc::clone(&self.inner.state))
    }
}

impl Drop for NativeTray {
    fn drop(&mut self) {
        let mut state = lock_state(&self.inner.state);
        state.hwnd = 0;
        unsafe {
            let _ = KillTimer(Some(self.inner.hwnd), MAINTENANCE_TIMER_ID);
            let _ = Shell_NotifyIconW(NIM_DELETE, &identity_data(self.inner.hwnd));
            SetWindowLongPtrW(self.inner.hwnd, GWLP_USERDATA, 0);
            let _ = DestroyMenu(self.inner.hmenu);
            let _ = DestroyWindow(self.inner.hwnd);
            let _ = DestroyIcon(self.inner.hicon);
        }
        drop(state);
        let _ = CURRENT_HWND.compare_exchange(
            self.inner.hwnd.0 as isize,
            0,
            Ordering::AcqRel,
            Ordering::Acquire,
        );
    }
}

impl Inner {
    fn begin_registration(&self, reason: &'static str) {
        let _ = unsafe { KillTimer(Some(self.hwnd), MAINTENANCE_TIMER_ID) };
        self.registration_reason.set(reason);
        self.next_retry.set(0);
        self.recovery_announced.set(false);
        self.registered.set(false);

        // Clear any stale identity before re-adding after a failed modify or an
        // Explorer restart. NIM_DELETE is harmless when no icon exists.
        let _ = unsafe { Shell_NotifyIconW(NIM_DELETE, &identity_data(self.hwnd)) };
        if !self.try_register(1) {
            self.schedule_retry();
        }
    }

    fn try_register(&self, attempt: usize) -> bool {
        let reason = self.registration_reason.get();
        let state = lock_state(&self.state);
        let data = notify_icon_data(self.hwnd, self.hicon, &state.tooltip);
        drop(state);
        if !unsafe { Shell_NotifyIconW(NIM_ADD, &data) }.as_bool() {
            diagnostics::log(
                Level::Warn,
                "tray",
                "registration-failed",
                None,
                format!("reason={reason} attempt={attempt}"),
            );
            return false;
        }

        let mut version = identity_data(self.hwnd);
        version.Anonymous = NOTIFYICONDATAW_0 {
            uVersion: NOTIFYICON_VERSION_4,
        };
        if !unsafe { Shell_NotifyIconW(NIM_SETVERSION, &version) }.as_bool() {
            diagnostics::log(
                Level::Warn,
                "tray",
                "set-version-failed",
                None,
                format!("reason={reason} attempt={attempt}"),
            );
            let _ = unsafe { Shell_NotifyIconW(NIM_DELETE, &identity_data(self.hwnd)) };
            return false;
        }

        self.registered.set(true);
        self.next_retry.set(0);
        diagnostics::log(
            Level::Info,
            "tray",
            "ready",
            None,
            format!("native tray icon registered reason={reason} attempt={attempt}"),
        );
        self.arm_timer(HEALTH_CHECK_MS, "health-check");
        true
    }

    fn schedule_retry(&self) {
        let retry = self.next_retry.get();
        let delay = if let Some(delay) = RETRY_DELAYS_MS.get(retry).copied() {
            self.next_retry.set(retry + 1);
            delay
        } else {
            if !self.recovery_announced.replace(true) {
                diagnostics::log(
                    Level::Warn,
                    "tray",
                    "registration-recovery-loop",
                    None,
                    format!(
                        "reason={} fast_attempts={} retry_ms={RECOVERY_RETRY_MS}",
                        self.registration_reason.get(),
                        RETRY_DELAYS_MS.len() + 1
                    ),
                );
            }
            RECOVERY_RETRY_MS
        };
        self.arm_timer(delay, "registration-retry");
    }

    fn arm_timer(&self, delay: u32, purpose: &'static str) {
        let timer = unsafe { SetTimer(Some(self.hwnd), MAINTENANCE_TIMER_ID, delay, None) };
        if timer == 0 {
            diagnostics::log(
                Level::Error,
                "tray",
                "maintenance-timer-failed",
                None,
                format!("purpose={purpose} delay_ms={delay}"),
            );
        }
    }

    fn retry_registration(&self) {
        let _ = unsafe { KillTimer(Some(self.hwnd), MAINTENANCE_TIMER_ID) };
        let attempt = self.next_retry.get() + 1;
        if !self.try_register(attempt) {
            self.schedule_retry();
        }
    }

    fn maintain_registration(&self) {
        if !self.registered.get() {
            self.retry_registration();
        } else {
            self.update_status("health-check");
        }
    }

    fn update_status(&self, reason: &'static str) {
        if !self.registered.get() {
            return;
        }

        let state = lock_state(&self.state);
        let data = notify_icon_data(self.hwnd, self.hicon, &state.tooltip);
        drop(state);
        if !unsafe { Shell_NotifyIconW(NIM_MODIFY, &data) }.as_bool() {
            diagnostics::log(
                Level::Warn,
                "tray",
                "icon-update-failed",
                None,
                format!("reason={reason}; tray icon is unavailable; registering it again"),
            );
            self.begin_registration(reason);
        }
    }
}

pub fn icon_rect() -> Option<RECT> {
    let raw = CURRENT_HWND.load(Ordering::Acquire);
    if raw == 0 {
        return None;
    }
    icon_rect_for_hwnd(HWND(raw as *mut _))
}

pub(crate) fn find_host_window() -> Option<HWND> {
    unsafe { FindWindowW(CLASS_NAME, PCWSTR::null()) }.ok()
}

pub(crate) fn request_open(hwnd: HWND) -> windows::core::Result<()> {
    let message = open_message();
    if message == 0 {
        return Err(windows::core::Error::from_thread());
    }
    unsafe { PostMessageW(Some(hwnd), message, WPARAM(0), LPARAM(0)) }
}

fn icon_rect_for_hwnd(hwnd: HWND) -> Option<RECT> {
    let identifier = NOTIFYICONIDENTIFIER {
        cbSize: size_of::<NOTIFYICONIDENTIFIER>() as u32,
        hWnd: hwnd,
        uID: TRAY_ICON_ID,
        ..Default::default()
    };
    unsafe { Shell_NotifyIconGetRect(&identifier) }.ok()
}

unsafe extern "system" fn wnd_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if message == WM_TRAY_ICON {
        if let Some(inner) = unsafe { inner(hwnd) } {
            let Some(event) = notification_event(lparam) else {
                return LRESULT(0);
            };
            if is_toggle_event(event) {
                diagnostics::log(
                    Level::Debug,
                    "tray",
                    "action",
                    None,
                    format!("toggle event=0x{event:04X}"),
                );
                (inner.action)(TrayAction::Toggle);
            } else if is_context_event(event) {
                diagnostics::log(
                    Level::Debug,
                    "tray",
                    "action",
                    None,
                    format!("context-menu event=0x{event:04X}"),
                );
                let point = tray_icon_center(hwnd).unwrap_or_else(|| callback_point(wparam));
                show_menu(inner, point);
            }
        }
        return LRESULT(0);
    }

    if message == WM_TRAY_STATUS {
        if let Some(inner) = unsafe { inner(hwnd) } {
            inner.update_status("status-update");
        }
        return LRESULT(0);
    }

    if message == open_message() && message != 0 {
        if let Some(inner) = unsafe { inner(hwnd) } {
            diagnostics::log(
                Level::Debug,
                "tray",
                "action",
                None,
                "external open request",
            );
            (inner.action)(TrayAction::Open);
        }
        return LRESULT(0);
    }

    if message == WM_TIMER && wparam.0 == MAINTENANCE_TIMER_ID {
        if let Some(inner) = unsafe { inner(hwnd) } {
            inner.maintain_registration();
        }
        return LRESULT(0);
    }

    if message == taskbar_created_message() && message != 0 {
        if let Some(inner) = unsafe { inner(hwnd) } {
            diagnostics::log(
                Level::Info,
                "tray",
                "taskbar-created",
                None,
                "Explorer notification area was recreated; registering tray icon again",
            );
            inner.begin_registration("taskbar-created");
        }
        return LRESULT(0);
    }

    unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
}

unsafe fn inner(hwnd: HWND) -> Option<&'static Inner> {
    let pointer = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *const Inner;
    unsafe { pointer.as_ref() }
}

fn show_menu(inner: &Inner, point: POINT) {
    let _ = unsafe { SetForegroundWindow(inner.hwnd) };
    let command = unsafe {
        TrackPopupMenu(
            inner.hmenu,
            TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LEFTALIGN | TPM_BOTTOMALIGN,
            point.x,
            point.y,
            None,
            inner.hwnd,
            None,
        )
    }
    .0 as u32;
    let _ = unsafe { Shell_NotifyIconW(NIM_SETFOCUS, &identity_data(inner.hwnd)) };
    // The foreground-window + WM_NULL sequence keeps native popup dismissal reliable.
    let _ = unsafe { PostMessageW(Some(inner.hwnd), WM_NULL, WPARAM(0), LPARAM(0)) };
    match command {
        OPEN_COMMAND => {
            diagnostics::log(Level::Debug, "tray", "menu", None, "open");
            (inner.action)(TrayAction::Open);
        }
        EXIT_COMMAND => {
            diagnostics::log(Level::Info, "tray", "menu", None, "exit");
            (inner.action)(TrayAction::Quit);
        }
        _ => {}
    }
}

fn callback_point(wparam: WPARAM) -> POINT {
    let packed = wparam.0 as u32;
    POINT {
        x: i32::from(packed as u16 as i16),
        y: i32::from((packed >> 16) as u16 as i16),
    }
}

fn tray_icon_center(hwnd: HWND) -> Option<POINT> {
    icon_rect_for_hwnd(hwnd).map(|rect| POINT {
        x: rect.left + (rect.right - rect.left) / 2,
        y: rect.top + (rect.bottom - rect.top) / 2,
    })
}

fn is_toggle_event(event: u32) -> bool {
    matches!(event, NIN_SELECT | NIN_KEYSELECT)
}

fn is_context_event(event: u32) -> bool {
    event == WM_CONTEXTMENU
}

fn notification_event(lparam: LPARAM) -> Option<u32> {
    let packed = lparam.0 as u32;
    ((packed >> 16) == TRAY_ICON_ID).then_some(packed & 0xffff)
}

fn ensure_class_registered() -> windows::core::Result<()> {
    if CLASS_REGISTERED.load(Ordering::Acquire) {
        return Ok(());
    }
    let hinstance = unsafe { GetModuleHandleW(None) }?;
    let class = WNDCLASSW {
        lpfnWndProc: Some(wnd_proc),
        hInstance: hinstance.into(),
        lpszClassName: CLASS_NAME,
        ..Default::default()
    };
    if unsafe { RegisterClassW(&class) } == 0 {
        return Err(windows::core::Error::from_thread());
    }
    CLASS_REGISTERED.store(true, Ordering::Release);
    Ok(())
}

fn lock_state(state: &TrayState) -> MutexGuard<'_, SharedTrayState> {
    state
        .shared
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn taskbar_created_message() -> u32 {
    let cached = TASKBAR_CREATED_MESSAGE.load(Ordering::Acquire);
    if cached != 0 {
        return cached;
    }
    let message = unsafe { RegisterWindowMessageW(w!("TaskbarCreated")) };
    TASKBAR_CREATED_MESSAGE.store(message, Ordering::Release);
    message
}

fn open_message() -> u32 {
    let cached = OPEN_MESSAGE.load(Ordering::Acquire);
    if cached != 0 {
        return cached;
    }
    let message = unsafe { RegisterWindowMessageW(w!("SBMS.Tray.Open.v1")) };
    OPEN_MESSAGE.store(message, Ordering::Release);
    message
}

fn create_menu() -> windows::core::Result<HMENU> {
    let menu = unsafe { CreatePopupMenu() }?;
    let result = unsafe { AppendMenuW(menu, MF_STRING, OPEN_COMMAND as usize, w!("Open SBMS")) }
        .and_then(|_| unsafe { AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null()) })
        .and_then(|_| unsafe { AppendMenuW(menu, MF_STRING, EXIT_COMMAND as usize, w!("Exit")) });
    if let Err(error) = result {
        let _ = unsafe { DestroyMenu(menu) };
        return Err(error);
    }
    Ok(menu)
}

fn create_hicon() -> Result<HICON, Box<dyn Error>> {
    let image = slint::Image::load_from_svg_data(include_bytes!("../ui/tray-icon.svg"))
        .map_err(|_| io::Error::other("failed to decode the embedded tray icon"))?;
    let pixels = image
        .to_rgba8_premultiplied()
        .ok_or_else(|| io::Error::other("failed to rasterize the embedded tray icon"))?;
    let width = i32::try_from(pixels.width())?;
    let height = i32::try_from(pixels.height())?;
    let bitmap_info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            ..Default::default()
        },
        ..Default::default()
    };

    let dc = unsafe { GetDC(None) };
    let mut bits = std::ptr::null_mut();
    let color =
        unsafe { CreateDIBSection(Some(dc), &bitmap_info, DIB_RGB_COLORS, &mut bits, None, 0) };
    let _ = unsafe { ReleaseDC(None, dc) };
    let color = color?;
    if bits.is_null() {
        let _ = unsafe { DeleteObject(color.into()) };
        return Err(io::Error::other("tray icon bitmap has no pixel storage").into());
    }

    let rgba = pixels.as_bytes();
    let destination = unsafe { std::slice::from_raw_parts_mut(bits as *mut u8, rgba.len()) };
    for (source, destination) in rgba.chunks_exact(4).zip(destination.chunks_exact_mut(4)) {
        destination.copy_from_slice(&[source[2], source[1], source[0], source[3]]);
    }

    let mask_stride = usize::try_from(width)?.div_ceil(16) * 2;
    let mask_bits = vec![0u8; mask_stride * usize::try_from(height)?];
    let mask = unsafe { CreateBitmap(width, height, 1, 1, Some(mask_bits.as_ptr().cast())) };
    if mask.0.is_null() {
        let _ = unsafe { DeleteObject(color.into()) };
        return Err(io::Error::last_os_error().into());
    }
    let icon_info = ICONINFO {
        fIcon: BOOL(1),
        hbmMask: mask,
        hbmColor: color,
        ..Default::default()
    };
    let icon = unsafe { CreateIconIndirect(&icon_info) };
    let _ = unsafe { DeleteObject(mask.into()) };
    let _ = unsafe { DeleteObject(color.into()) };
    Ok(icon?)
}

fn notify_icon_data(hwnd: HWND, hicon: HICON, tooltip: &str) -> NOTIFYICONDATAW {
    let mut tip = [0u16; 128];
    for (destination, source) in tip.iter_mut().take(127).zip(tooltip.encode_utf16()) {
        *destination = source;
    }
    NOTIFYICONDATAW {
        cbSize: size_of::<NOTIFYICONDATAW>() as u32,
        hWnd: hwnd,
        uID: TRAY_ICON_ID,
        uFlags: NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
        uCallbackMessage: WM_TRAY_ICON,
        hIcon: hicon,
        szTip: tip,
        ..Default::default()
    }
}

fn identity_data(hwnd: HWND) -> NOTIFYICONDATAW {
    NOTIFYICONDATAW {
        cbSize: size_of::<NOTIFYICONDATAW>() as u32,
        hWnd: hwnd,
        uID: TRAY_ICON_ID,
        ..Default::default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn retry_backoff_is_bounded_and_increasing() {
        assert_eq!(RETRY_DELAYS_MS, [250, 500, 1_000, 2_000, 4_000, 8_000]);
        assert!(RETRY_DELAYS_MS.windows(2).all(|pair| pair[0] < pair[1]));
        assert!(RECOVERY_RETRY_MS > *RETRY_DELAYS_MS.last().unwrap());
    }

    #[test]
    fn notification_events_preserve_mouse_and_keyboard_access() {
        for event in [NIN_SELECT, NIN_KEYSELECT] {
            assert!(is_toggle_event(event));
            assert!(!is_context_event(event));
        }
        assert!(is_context_event(WM_CONTEXTMENU));
        assert!(!is_toggle_event(WM_CONTEXTMENU));
    }

    #[test]
    fn callback_coordinates_preserve_signed_virtual_screen_positions() {
        let packed = ((-20_i16 as u16 as u32) << 16) | (-10_i16 as u16 as u32);
        let point = callback_point(WPARAM(packed as usize));
        assert_eq!((point.x, point.y), (-10, -20));
    }

    #[test]
    fn version_four_notifications_require_the_expected_icon_id() {
        let selected = LPARAM(((TRAY_ICON_ID << 16) | NIN_SELECT) as isize);
        assert_eq!(notification_event(selected), Some(NIN_SELECT));

        let foreign = LPARAM((((TRAY_ICON_ID + 1) << 16) | NIN_SELECT) as isize);
        assert_eq!(notification_event(foreign), None);
    }
}
