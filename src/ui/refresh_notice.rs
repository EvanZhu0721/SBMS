use std::cell::Cell;
use std::error::Error;
use std::time::{Duration, Instant};

use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use slint::winit_030::winit::platform::windows::WindowAttributesExtWindows;
use slint::winit_030::{WinitWindowAccessor, winit};
use slint::{ComponentHandle, PhysicalPosition, Timer};
use windows::Win32::Foundation::{COLORREF, HWND, LPARAM, RECT};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GWL_EXSTYLE, GWLP_HWNDPARENT, GetClassNameW, GetWindowLongPtrW, GetWindowRect,
    GetWindowThreadProcessId, LWA_ALPHA, LWA_COLORKEY, SetLayeredWindowAttributes,
    SetWindowLongPtrW, WS_EX_NOACTIVATE,
};
use windows::core::BOOL;

use super::RefreshNoticeWindow;
use crate::diagnostics::{self, Level};

thread_local! {
    // Slint calls the attributes hook before applying the component's title.
    // Some(0) is an ownerless standalone preview; None is an ordinary app window.
    static NOTICE_OWNER: Cell<Option<isize>> = const { Cell::new(None) };
}

pub(super) fn window_attributes(
    attributes: winit::window::WindowAttributes,
) -> winit::window::WindowAttributes {
    if let Some(owner) = NOTICE_OWNER.get() {
        let attributes = attributes.with_active(false).with_transparent(true);
        if owner != 0 {
            attributes.with_owner_window(owner)
        } else {
            attributes
        }
    } else {
        attributes
    }
}

/// The timers keep the window alive; the caller can hide it through the weak handle on stop.
pub(super) fn show(
    virtual_millihz: u32,
    target_millihz: u32,
    target: RECT,
) -> Result<slint::Weak<RefreshNoticeWindow>, Box<dyn Error>> {
    // An owned popup stays above its mirror even when TaskbarGuard raises it.
    let owner = mirror_window(target);
    NOTICE_OWNER.set(Some(owner));
    let notice = RefreshNoticeWindow::new();
    NOTICE_OWNER.set(None);
    let notice = notice?;
    notice.set_virtual_rate(((virtual_millihz + 500) / 1_000).to_string().into());
    notice.set_target_rate(((target_millihz + 500) / 1_000).to_string().into());
    notice.set_rate_order(match virtual_millihz.cmp(&target_millihz) {
        std::cmp::Ordering::Less => -1,
        std::cmp::Ordering::Equal => 0,
        std::cmp::Ordering::Greater => 1,
    });
    notice.set_presented(false);
    // Create on the target monitor so its DPI is applied before reading native size.
    notice
        .window()
        .set_position(PhysicalPosition::new(target.left, target.top));
    notice.show()?;
    let created = Instant::now();
    let weak = notice.as_weak();
    slint::spawn_local(async move {
        let result = async {
            let native = notice.window().winit_window().await?;
            if !notice.window().is_visible() {
                diagnostics::log(
                    Level::Debug,
                    "ui",
                    "refresh-notice",
                    None,
                    "cancelled before native setup",
                );
                return Ok::<_, Box<dyn Error>>(None);
            }
            let handle = native.window_handle()?;
            let RawWindowHandle::Win32(handle) = handle.as_raw() else {
                return Err("Refresh notice requires a Win32 window".into());
            };
            let hwnd = HWND(handle.hwnd.get() as *mut _);
            set_no_activate(hwnd);
            native.set_cursor_hittest(false)?;
            // Winit adds WS_EX_LAYERED for click-through. Initialize its
            // alpha so the software renderer's GDI output remains visible.
            unsafe {
                SetLayeredWindowAttributes(hwnd, COLORREF(0), 255, LWA_ALPHA | LWA_COLORKEY)
            }?;
            // Winit reapplies its extended styles when hit testing changes.
            set_no_activate(hwnd);
            let size = native.outer_size();
            notice.window().set_position(PhysicalPosition::new(
                target.left + (target.right - target.left - size.width as i32) / 2,
                target.top + (target.bottom - target.top - size.height as i32) / 2,
            ));
            notice.set_presented(true);
            diagnostics::log(
                Level::Debug,
                "ui",
                "refresh-notice",
                None,
                format!(
                    "shown hwnd={hwnd:?} owner={owner:#x} position={:?} size={size:?}",
                    native.outer_position()
                ),
            );
            Ok(Some(hwnd))
        }
        .await;
        match result {
            Ok(Some(hwnd)) => Timer::single_shot(Duration::from_secs(5), move || {
                diagnostics::log(
                    Level::Debug,
                    "ui",
                    "refresh-notice",
                    None,
                    format!("dismiss elapsed_ms={}", created.elapsed().as_millis()),
                );
                fade(notice, hwnd, Instant::now())
            }),
            Ok(None) => {}
            Err(error) => {
                diagnostics::log(Level::Warn, "ui", "refresh-notice", None, error.to_string());
                dismiss(&notice);
            }
        }
    })?;
    Ok(weak)
}

fn fade(notice: RefreshNoticeWindow, hwnd: HWND, started: Instant) {
    let remaining = 1.0 - started.elapsed().as_secs_f32() / 0.2;
    if remaining <= 0.0 || !notice.window().is_visible() {
        dismiss(&notice);
        return;
    }
    // Fade the whole native layer; fading GDI pixels would expose a black fill.
    let _ = unsafe {
        SetLayeredWindowAttributes(
            hwnd,
            COLORREF(0),
            (remaining * 255.0) as u8,
            LWA_ALPHA | LWA_COLORKEY,
        )
    };
    Timer::single_shot(Duration::from_millis(16), move || {
        fade(notice, hwnd, started)
    });
}

fn set_no_activate(hwnd: HWND) {
    unsafe {
        let style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE.0 as isize);
    }
}

pub(super) fn dismiss(notice: &RefreshNoticeWindow) {
    notice.window().with_winit_window(|native| {
        if let Ok(handle) = native.window_handle()
            && let RawWindowHandle::Win32(handle) = handle.as_raw()
        {
            // Renderer and UI own different threads. Break that relationship
            // before the UI waits for the renderer to finish.
            unsafe {
                SetWindowLongPtrW(HWND(handle.hwnd.get() as *mut _), GWLP_HWNDPARENT, 0);
            }
        }
    });
    let _ = notice.hide();
}

/// Resolve the current renderer once, on the UI thread; no HWND is kept across starts.
fn mirror_window(target: RECT) -> isize {
    struct Search {
        target: RECT,
        owner: isize,
    }
    unsafe extern "system" fn visit(window: HWND, data: LPARAM) -> BOOL {
        let search = unsafe { &mut *(data.0 as *mut Search) };
        let mut process = 0;
        unsafe { GetWindowThreadProcessId(window, Some(&mut process)) };
        if process != std::process::id() {
            return BOOL(1);
        }
        let mut class = [0u16; 64];
        let length = unsafe { GetClassNameW(window, &mut class) } as usize;
        if !String::from_utf16_lossy(&class[..length]).starts_with("SBMSMirrorWindow-") {
            return BOOL(1);
        }
        let mut bounds = RECT::default();
        if unsafe { GetWindowRect(window, &mut bounds) }.is_ok() && bounds == search.target {
            search.owner = window.0 as isize;
            return BOOL(0);
        }
        BOOL(1)
    }
    let mut search = Search { target, owner: 0 };
    let _ = unsafe { EnumWindows(Some(visit), LPARAM((&mut search as *mut Search) as isize)) };
    search.owner
}
