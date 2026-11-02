use std::cell::Cell;
use std::mem::size_of;
use std::sync::{Arc, Mutex, MutexGuard, Weak};

use windows::Win32::Foundation::{COLORREF, HWND, LPARAM, LRESULT, POINT, RECT, SIZE, WPARAM};
use windows::Win32::Graphics::Gdi::{
    AC_SRC_ALPHA, AC_SRC_OVER, BI_RGB, BITMAPINFO, BITMAPINFOHEADER, BLENDFUNCTION,
    CreateCompatibleDC, CreateDIBSection, DIB_RGB_COLORS, DeleteDC, DeleteObject, HBITMAP, HDC,
    HGDIOBJ, ScreenToClient, SelectObject,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Registry::{HKEY_CURRENT_USER, RRF_RT_REG_DWORD, RegGetValueW};
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT, GetDpiForWindow, GetWindowDpiAwarenessContext,
    SetThreadDpiAwarenessContext,
};
use windows::Win32::UI::WindowsAndMessaging::*;
use windows::core::{Error, PCWSTR, Result, w};

use super::{TrayMonitor, WM_METER_MENU, WM_METER_TOGGLE};

const CLASS: PCWSTR = w!("SbmsTaskbarMeter");
const CONTROL_CLASS: PCWSTR = w!("SbmsTaskbarMeterControl");
const WM_UPDATE: u32 = WM_APP + 1;
const WM_LAYOUT: u32 = WM_APP + 2;
const WM_STOP: u32 = WM_APP + 3;
const LAYOUT_TIMER: usize = 1;
// Nonzero alpha gives the entire rectangle native hit testing, including digit gaps.
const BACKGROUND_PIXEL: u32 = 0x01000000;
static CURRENT_METER: Mutex<Weak<Shared>> = Mutex::new(Weak::new());

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

struct Snapshot {
    host: isize,
    control: isize,
    stopped: bool,
    monitor: TrayMonitor,
    rect: Option<RECT>,
    #[cfg(test)]
    layout: Option<tests::TestLayout>,
    #[cfg(test)]
    observed: tests::Observed,
}

struct Shared {
    snapshot: Mutex<Snapshot>,
}

impl Shared {
    fn lock(&self) -> MutexGuard<'_, Snapshot> {
        self.snapshot.lock().unwrap_or_else(|p| p.into_inner())
    }
}

// Only plain data crosses this boundary. All meter HWND/GDI operations stay on Worker.
pub(super) struct TaskbarMeter {
    shared: Arc<Shared>,
}

impl TaskbarMeter {
    pub(super) fn new(host: HWND) -> Self {
        Self::start(
            host,
            #[cfg(test)]
            None,
        )
    }

    fn start(host: HWND, #[cfg(test)] layout: Option<tests::TestLayout>) -> Self {
        let shared = Arc::new(Shared {
            snapshot: Mutex::new(Snapshot {
                host: host.0 as isize,
                control: 0,
                stopped: false,
                monitor: TrayMonitor::default(),
                rect: None,
                #[cfg(test)]
                layout,
                #[cfg(test)]
                observed: tests::Observed::default(),
            }),
        });
        *CURRENT_METER.lock().unwrap_or_else(|p| p.into_inner()) = Arc::downgrade(&shared);
        let worker_shared = Arc::clone(&shared);
        if let Err(error) = std::thread::Builder::new()
            .name("sbms-taskbar-meter".into())
            .spawn(move || Worker::run(worker_shared))
        {
            shared.lock().stopped = true;
            super::diagnostics::log(
                super::Level::Error,
                "tray",
                "taskbar-meter-thread-start-failed",
                None,
                error.to_string(),
            );
        }
        Self { shared }
    }

    pub(super) fn set_monitor(&self, monitor: TrayMonitor) {
        let mut state = self.shared.lock();
        if state.monitor != monitor {
            state.monitor = monitor;
            post_control(&state, WM_UPDATE);
        }
    }

    pub(super) fn refresh_layout(&self) {
        post_control(&self.shared.lock(), WM_LAYOUT);
    }

    pub(super) fn stop(&self) {
        let mut state = self.shared.lock();
        state.host = 0;
        state.rect = None;
        state.stopped = true;
        post_control(&state, WM_STOP);
    }
}

impl Drop for TaskbarMeter {
    fn drop(&mut self) {
        self.stop();
    }
}

// Holding the short data lock across PostMessage serializes it with endpoint invalidation.
// No synchronous Win32 call or Explorer operation runs while this lock is held.
fn post_control(state: &Snapshot, message: u32) {
    if state.control != 0 {
        let _ = unsafe {
            PostMessageW(
                Some(HWND(state.control as *mut _)),
                message,
                WPARAM(0),
                LPARAM(0),
            )
        };
    }
}

struct Worker {
    shared: Arc<Shared>,
    control: Cell<HWND>,
    hwnd: Cell<HWND>,
    light: Cell<bool>,
    placed: Cell<Option<RECT>>,
    rendered: Cell<Option<Frame>>,
}

impl Worker {
    fn run(shared: Arc<Shared>) {
        let worker = Box::new(Self {
            shared,
            control: Cell::new(HWND::default()),
            hwnd: Cell::new(HWND::default()),
            light: Cell::new(false),
            placed: Cell::new(None),
            rendered: Cell::new(None),
        });
        let result = worker.message_loop();
        if let Err(error) = result {
            log_failure("taskbar-meter-thread-failed", &error);
        }
    }

    fn message_loop(&self) -> Result<()> {
        if self.shared.lock().stopped {
            return Ok(());
        }
        let instance = unsafe { GetModuleHandleW(None) }?;
        let classes: [(PCWSTR, WNDPROC); 2] = [
            (CONTROL_CLASS, Some(control_proc)),
            (CLASS, Some(window_proc)),
        ];
        for (name, procedure) in classes {
            let class = WNDCLASSW {
                lpfnWndProc: procedure,
                hInstance: instance.into(),
                lpszClassName: name,
                ..Default::default()
            };
            unsafe { RegisterClassW(&class) };
        }
        // A message-only window has no ownership or parent relationship with the UI host.
        let control = unsafe {
            CreateWindowExW(
                WINDOW_EX_STYLE::default(),
                CONTROL_CLASS,
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
        }?;
        self.control.set(control);
        unsafe { SetWindowLongPtrW(control, GWLP_USERDATA, self as *const Self as isize) };
        if unsafe { SetTimer(Some(control), LAYOUT_TIMER, 500, None) } == 0 {
            return Err(Error::from_thread());
        }
        {
            let mut state = self.shared.lock();
            if state.stopped {
                return Ok(());
            }
            state.control = control.0 as isize;
        }
        self.refresh_layout();
        let mut message = MSG::default();
        loop {
            if self.shared.lock().stopped {
                return Ok(());
            }
            let result = unsafe { GetMessageW(&mut message, None, 0, 0) }.0;
            if result == -1 {
                return Err(Error::from_thread());
            }
            if result == 0 {
                return Ok(());
            }
            unsafe {
                let _ = TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
    }

    fn redraw(&self) {
        if let Some(rect) = self.placed.get()
            && unsafe { IsWindow(Some(self.hwnd.get())) }.as_bool()
        {
            let _dpi = DpiContext::for_window(self.hwnd.get());
            if let Err(error) = self.render(rect) {
                log_failure("taskbar-meter-render", &error);
            }
        }
    }

    fn refresh_layout(&self) {
        if self.shared.lock().stopped {
            return;
        }
        if self.update_layout().is_none() {
            self.shared.lock().rect = None;
        }
    }

    fn update_layout(&self) -> Option<()> {
        #[cfg(test)]
        let test_layout = self.shared.lock().layout;
        #[cfg(test)]
        if let Some(layout) = test_layout {
            self.light.set(layout.light);
            let result = if unsafe { IsWindow(Some(HWND(layout.parent as *mut _))) }.as_bool() {
                self.apply_layout(HWND(layout.parent as *mut _), layout.rect)
                    .map_err(|error| log_failure("taskbar-meter-layout", &error))
                    .ok()
            } else {
                None
            };
            self.shared.lock().observed.layouts += 1;
            return result;
        }
        let light = light_taskbar();
        if self.light.replace(light) != light {
            self.redraw();
        }
        let taskbar = unsafe { FindWindowW(w!("Shell_TrayWnd"), PCWSTR::null()) }.ok()?;
        // Match Explorer while creating and positioning the child on this worker.
        let _dpi = DpiContext::for_window(taskbar);
        let mut parent_rect = RECT::default();
        unsafe { GetClientRect(taskbar, &mut parent_rect) }.ok()?;
        let notify =
            unsafe { FindWindowExW(Some(taskbar), None, w!("TrayNotifyWnd"), PCWSTR::null()) }
                .ok()?;
        let mut notify_rect = RECT::default();
        unsafe { GetWindowRect(notify, &mut notify_rect) }.ok()?;
        let mut point = POINT {
            x: notify_rect.left,
            y: notify_rect.top,
        };
        if !unsafe { ScreenToClient(taskbar, &mut point) }.as_bool() {
            return None;
        }
        let rect = layout(parent_rect, point.x, unsafe { GetDpiForWindow(taskbar) })?;
        self.apply_layout(taskbar, rect)
            .map_err(|error| log_failure("taskbar-meter-layout", &error))
            .ok()
    }

    fn apply_layout(&self, taskbar: HWND, rect: RECT) -> Result<()> {
        let mut hwnd = self.hwnd.get();
        if !unsafe { IsWindow(Some(hwnd)) }.as_bool()
            || unsafe { GetParent(hwnd) }.ok() != Some(taskbar)
        {
            if unsafe { IsWindow(Some(hwnd)) }.as_bool() {
                let _ = unsafe { DestroyWindow(hwnd) };
            }
            self.hwnd.set(HWND::default());
            self.placed.set(None);
            self.rendered.set(None);
            self.shared.lock().rect = None;
            let instance = unsafe { GetModuleHandleW(None) }?;
            // Creating directly as a child avoids temporarily reparenting a top-level Slint window.
            hwnd = unsafe {
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
            }?;
            self.hwnd.set(hwnd);
            unsafe {
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, self as *const Self as isize);
            }
            #[cfg(test)]
            {
                self.shared.lock().observed.hwnd = hwnd.0 as isize;
            }
        }
        // Submit before showing a new child. ULW owns its size; SetWindowPos uses parent coordinates.
        self.render(rect)?;
        if self.placed.get() != Some(rect) {
            unsafe {
                SetWindowPos(
                    hwnd,
                    Some(HWND_TOP),
                    rect.left,
                    rect.top,
                    0,
                    0,
                    SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW,
                )?;
            }
            self.placed.set(Some(rect));
        }
        let mut screen = RECT::default();
        let visible = unsafe { IsWindowVisible(hwnd) }.as_bool();
        let screen = if visible && unsafe { GetWindowRect(hwnd, &mut screen) }.is_ok() {
            Some(screen)
        } else {
            None
        };
        let mut state = self.shared.lock();
        if !state.stopped {
            state.rect = screen;
        }
        Ok(())
    }

    fn render(&self, rect: RECT) -> Result<bool> {
        let monitor = self.shared.lock().monitor;
        let frame = Frame::new(rect, monitor, self.light.get());
        if self.rendered.get() == Some(frame) {
            return Ok(false);
        }
        let surface = Surface::new(frame)?;
        let size = SIZE {
            cx: frame.width,
            cy: frame.height,
        };
        let blend = BLENDFUNCTION {
            BlendOp: AC_SRC_OVER as u8,
            BlendFlags: 0,
            SourceConstantAlpha: 255,
            AlphaFormat: AC_SRC_ALPHA as u8,
        };
        unsafe {
            UpdateLayeredWindow(
                self.hwnd.get(),
                None,
                None,
                Some(&size),
                Some(surface.dc),
                Some(&POINT::default()),
                COLORREF(0),
                Some(&blend),
                ULW_ALPHA,
            )?;
        }
        self.rendered.set(Some(frame));
        #[cfg(test)]
        {
            let mut state = self.shared.lock();
            state.observed.frame = Some(frame);
            state.observed.renders += 1;
        }
        Ok(true)
    }
}

fn log_failure(event: &str, error: &Error) {
    super::diagnostics::log(super::Level::Warn, "tray", event, None, error.to_string());
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Frame {
    width: i32,
    height: i32,
    glyphs: [u8; 3],
    color: u32,
}

impl Frame {
    fn new(rect: RECT, monitor: TrayMonitor, light: bool) -> Self {
        Self {
            width: rect.right - rect.left,
            height: rect.bottom - rect.top,
            glyphs: monitor.glyphs(),
            color: match (light, monitor.error) {
                (true, false) => 0xff343236,
                (true, true) => 0xffba1a1a,
                (false, false) => 0xffcacaca,
                (false, true) => 0xffffb4ab,
            },
        }
    }
}

// The bitmap is selected only into this DC. Restore its previous object before freeing either.
struct Surface {
    dc: HDC,
    bitmap: HBITMAP,
    previous: HGDIOBJ,
    bits: *mut u32,
}

impl Surface {
    fn new(frame: Frame) -> Result<Self> {
        let mut surface = Self {
            dc: unsafe { CreateCompatibleDC(None) },
            bitmap: HBITMAP::default(),
            previous: HGDIOBJ::default(),
            bits: std::ptr::null_mut(),
        };
        if surface.dc.is_invalid() {
            return Err(Error::from_thread());
        }
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: frame.width,
                biHeight: -frame.height,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut bits = std::ptr::null_mut();
        surface.bitmap = unsafe {
            CreateDIBSection(Some(surface.dc), &info, DIB_RGB_COLORS, &mut bits, None, 0)
        }?;
        surface.bits = bits.cast();
        surface.previous = unsafe { SelectObject(surface.dc, surface.bitmap.into()) };
        if surface.previous.is_invalid() {
            return Err(Error::from_thread());
        }
        let pixels = unsafe {
            std::slice::from_raw_parts_mut(surface.bits, (frame.width * frame.height) as usize)
        };
        paint_pixels(pixels, frame);
        Ok(surface)
    }
}

impl Drop for Surface {
    fn drop(&mut self) {
        unsafe {
            if !self.previous.is_invalid() {
                let _restored = SelectObject(self.dc, self.previous);
                #[cfg(test)]
                assert!(!_restored.is_invalid(), "restore previous bitmap");
            }
            if !self.bitmap.is_invalid() {
                let _deleted = DeleteObject(self.bitmap.into());
                #[cfg(test)]
                assert!(_deleted.as_bool(), "delete bitmap");
            }
            if !self.dc.is_invalid() {
                let _deleted = DeleteDC(self.dc);
                #[cfg(test)]
                assert!(_deleted.as_bool(), "delete DC");
            }
        }
    }
}

fn paint_pixels(pixels: &mut [u32], frame: Frame) {
    // BGRA bytes are premultiplied: black alpha=1 background; fully opaque colored segments.
    pixels.fill(BACKGROUND_PIXEL);
    let scale = frame.width as f64 / 58.0;
    let y = (frame.height as f64 - 24.0 * scale) / 2.0;
    let segments = [
        (2, 0, 10, 2),
        (12, 2, 2, 9),
        (12, 13, 2, 9),
        (2, 22, 10, 2),
        (0, 13, 2, 9),
        (0, 2, 2, 9),
        (2, 11, 10, 2),
    ];
    for (digit, glyph) in frame.glyphs.into_iter().enumerate() {
        for (bit, (x, sy, w, h)) in segments.into_iter().enumerate() {
            if glyph & (1 << bit) == 0 {
                continue;
            }
            let x = (x + 4 + digit * 18) as f64 * scale;
            let top = y + sy as f64 * scale;
            let left = (x.round() as i32).clamp(0, frame.width) as usize;
            let right = ((x + w as f64 * scale).round() as i32).clamp(0, frame.width) as usize;
            let top_row = (top.round() as i32).clamp(0, frame.height);
            let bottom = ((top + h as f64 * scale).round() as i32).clamp(0, frame.height);
            for row in top_row..bottom {
                let start = (row * frame.width) as usize;
                pixels[start + left..start + right].fill(frame.color);
            }
        }
    }
}

impl Drop for Worker {
    fn drop(&mut self) {
        // Clear endpoints before destroying HWNDs, so no sender can post to a recycled handle.
        {
            let mut state = self.shared.lock();
            state.control = 0;
            state.rect = None;
            state.host = 0;
            state.stopped = true;
        }
        unsafe {
            for hwnd in [self.control.get(), self.hwnd.get()] {
                if !hwnd.is_invalid() {
                    SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
                }
            }
            if !self.control.get().is_invalid() {
                let _ = KillTimer(Some(self.control.get()), LAYOUT_TIMER);
                let _ = DestroyWindow(self.control.get());
            }
            if !self.hwnd.get().is_invalid() {
                let _ = DestroyWindow(self.hwnd.get());
            }
        }
        #[cfg(test)]
        {
            let mut state = self.shared.lock();
            state.observed.hwnd = 0;
            state.observed.ended = true;
        }
    }
}

pub(super) fn meter_rect() -> Option<RECT> {
    let shared = CURRENT_METER
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .upgrade()?;
    shared.lock().rect
}

unsafe extern "system" fn control_proc(
    hwnd: HWND,
    message: u32,
    wp: WPARAM,
    lp: LPARAM,
) -> LRESULT {
    let pointer = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *const Worker;
    if let Some(worker) = unsafe { pointer.as_ref() } {
        match message {
            WM_UPDATE => {
                worker.redraw();
                return LRESULT(0);
            }
            WM_LAYOUT => {
                worker.refresh_layout();
                return LRESULT(0);
            }
            WM_TIMER if wp.0 == LAYOUT_TIMER => {
                worker.refresh_layout();
                return LRESULT(0);
            }
            WM_STOP => {
                unsafe { PostQuitMessage(0) };
                return LRESULT(0);
            }
            WM_NCDESTROY => {
                worker.shared.lock().control = 0;
                worker.control.set(HWND::default());
                unsafe {
                    SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
                    PostQuitMessage(0);
                }
            }
            _ => {}
        }
    }
    unsafe { DefWindowProcW(hwnd, message, wp, lp) }
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
    let pointer = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *const Worker;
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
                let result = {
                    let shared = state.shared.lock();
                    (shared.host != 0).then(|| unsafe {
                        PostMessageW(
                            Some(HWND(shared.host as *mut _)),
                            message,
                            WPARAM(0),
                            LPARAM(0),
                        )
                    })
                };
                if let Some(Err(error)) = result {
                    log_failure("taskbar-meter-post", &error);
                }
            }
            return LRESULT(0);
        }
        WM_NCDESTROY => {
            if let Some(state) = state {
                state.hwnd.set(HWND::default());
                state.placed.set(None);
                state.rendered.set(None);
                state.shared.lock().rect = None;
                #[cfg(test)]
                {
                    state.shared.lock().observed.hwnd = 0;
                }
            }
            unsafe { SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0) };
        }
        #[cfg(test)]
        WM_WINDOWPOSCHANGING => {
            if let Some(state) = state {
                state.shared.lock().observed.positions += 1;
            }
        }
        _ => {}
    }
    unsafe { DefWindowProcW(hwnd, message, wp, lp) }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};
    use std::time::{Duration, Instant};
    use windows::Win32::Graphics::Gdi::{
        BITMAP, GdiFlush, GetCurrentObject, GetObjectType, GetObjectW, OBJ_BITMAP,
    };
    use windows::Win32::System::Threading::GetCurrentThreadId;

    static ACTIONS: AtomicU32 = AtomicU32::new(0);

    #[derive(Clone, Copy)]
    pub(super) struct TestLayout {
        pub parent: isize,
        pub rect: RECT,
        pub light: bool,
    }

    #[derive(Clone, Copy, Default)]
    pub(super) struct Observed {
        pub hwnd: isize,
        pub frame: Option<Frame>,
        pub positions: u32,
        pub renders: u32,
        pub layouts: u32,
        pub ended: bool,
    }

    fn pump_until(mut ready: impl FnMut() -> bool) {
        let started = Instant::now();
        while !ready() {
            let mut message = MSG::default();
            while unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool() {
                unsafe { DispatchMessageW(&message) };
            }
            assert!(
                started.elapsed() < Duration::from_secs(5),
                "worker response timeout"
            );
            std::thread::sleep(Duration::from_millis(1));
        }
    }

    unsafe extern "system" fn host_proc(
        hwnd: HWND,
        message: u32,
        wp: WPARAM,
        lp: LPARAM,
    ) -> LRESULT {
        if message == WM_METER_TOGGLE || message == WM_METER_MENU {
            ACTIONS.fetch_add(
                if message == WM_METER_TOGGLE { 1 } else { 16 },
                Ordering::Relaxed,
            );
            return LRESULT(0);
        }
        unsafe { DefWindowProcW(hwnd, message, wp, lp) }
    }

    struct HiddenHost(HWND);
    impl HiddenHost {
        fn new() -> Self {
            let instance = unsafe { GetModuleHandleW(None) }.unwrap();
            let class = WNDCLASSW {
                lpfnWndProc: Some(host_proc),
                hInstance: instance.into(),
                lpszClassName: w!("SbmsMeterHiddenTestHost"),
                ..Default::default()
            };
            unsafe { RegisterClassW(&class) };
            Self(
                unsafe {
                    CreateWindowExW(
                        WINDOW_EX_STYLE::default(),
                        class.lpszClassName,
                        PCWSTR::null(),
                        WS_POPUP,
                        0,
                        0,
                        1600,
                        200,
                        None,
                        None,
                        Some(instance.into()),
                        None,
                    )
                }
                .unwrap(),
            )
        }
    }
    impl Drop for HiddenHost {
        fn drop(&mut self) {
            unsafe { DestroyWindow(self.0) }.unwrap();
        }
    }

    fn save_composite(path: &std::path::Path, light: bool, samples: &[TrayMonitor]) {
        use std::io::Write;
        let (width, height) = (528usize, 336usize);
        let background = if light { 245u32 } else { 32u32 };
        let mut image = vec![0xff000000 | (background * 0x010101); width * height];
        for (row, dpi) in [96, 144, 192].into_iter().enumerate() {
            let rect = layout(
                RECT {
                    right: 1600,
                    bottom: 48 * dpi as i32 / 96,
                    ..Default::default()
                },
                1200,
                dpi,
            )
            .unwrap();
            for (column, sample) in samples.iter().enumerate() {
                let frame = Frame::new(rect, *sample, light);
                let surface = Surface::new(frame).unwrap();
                let pixels = unsafe {
                    std::slice::from_raw_parts(surface.bits, (frame.width * frame.height) as usize)
                };
                assert!(
                    pixels
                        .iter()
                        .all(|pixel| *pixel == BACKGROUND_PIXEL || *pixel == frame.color)
                );
                assert!(
                    pixels.contains(&frame.color),
                    "visible strokes at DPI {dpi}"
                );
                assert_eq!(pixels[0] >> 24, 1, "blank corner remains a hit target");
                let x = column * 132 + (132 - frame.width as usize) / 2;
                let y = row * 112 + (112 - frame.height as usize) / 2;
                for sy in 0..frame.height as usize {
                    for sx in 0..frame.width as usize {
                        let pixel = pixels[sy * frame.width as usize + sx];
                        let under = background * (255 - (pixel >> 24)) / 255;
                        image[(y + sy) * width + x + sx] =
                            0xff000000 | ((pixel & 0x00ffffff) + under * 0x010101);
                    }
                }
                let (dc, bitmap, previous) = (surface.dc, surface.bitmap, surface.previous);
                drop(surface);
                assert!(unsafe { GdiFlush() }.as_bool());
                let mut bitmap_info = BITMAP::default();
                assert_eq!(
                    unsafe {
                        GetObjectW(
                            bitmap.into(),
                            size_of::<BITMAP>() as i32,
                            Some((&mut bitmap_info as *mut BITMAP).cast()),
                        )
                    },
                    0,
                    "bitmap released"
                );
                assert!(
                    unsafe { GetCurrentObject(dc, OBJ_BITMAP) }.is_invalid(),
                    "memory DC released"
                );
                assert_ne!(
                    unsafe { GetObjectType(previous) },
                    0,
                    "previous stock object preserved"
                );
            }
        }
        let mut file = std::fs::File::create(path).unwrap();
        file.write_all(b"BM").unwrap();
        for value in [
            (54 + width * height * 4) as u32,
            0,
            54,
            40,
            width as u32,
            (-(height as i32)) as u32,
        ] {
            file.write_all(&value.to_le_bytes()).unwrap();
        }
        file.write_all(&[1, 0, 32, 0]).unwrap();
        file.write_all(&[0; 24]).unwrap();
        for pixel in image {
            file.write_all(&pixel.to_le_bytes()).unwrap();
        }
    }

    #[test]
    fn taskbar_meter_preview() {
        // Real layered-child API, with a hidden parent: no Explorer access or desktop input.
        ACTIONS.store(0, Ordering::Relaxed);
        let host = HiddenHost::new();
        let parent = HiddenHost::new();
        let rect = layout(
            RECT {
                right: 1200,
                bottom: 48,
                ..Default::default()
            },
            1000,
            96,
        )
        .unwrap();
        let meter = TaskbarMeter::start(
            host.0,
            Some(TestLayout {
                parent: parent.0.0 as isize,
                rect,
                light: false,
            }),
        );
        let sample = TrayMonitor {
            fps: 111,
            running: true,
            valid: true,
            ..Default::default()
        };
        meter.set_monitor(sample);
        pump_until(|| {
            let observed = meter.shared.lock().observed;
            observed.layouts > 0
                && observed
                    .frame
                    .is_some_and(|frame| frame.glyphs == sample.glyphs())
        });
        let observed = meter.shared.lock().observed;
        let hwnd = HWND(observed.hwnd as *mut _);
        assert_eq!(unsafe { GetParent(hwnd) }.unwrap(), parent.0);
        assert!(!unsafe { IsWindowVisible(hwnd) }.as_bool());
        assert_eq!(meter_rect(), None, "hidden parent has no click rectangle");
        let control = HWND(meter.shared.lock().control as *mut _);
        let worker_thread = unsafe { GetWindowThreadProcessId(hwnd, None) };
        assert_ne!(
            worker_thread,
            unsafe { GetCurrentThreadId() },
            "meter has its own message thread"
        );
        assert_eq!(worker_thread, unsafe {
            GetWindowThreadProcessId(control, None)
        });
        assert_ne!(
            unsafe { GetParent(control) }.ok(),
            Some(host.0),
            "controller is independent of UI host"
        );
        assert_eq!(unsafe { GetDpiForWindow(hwnd) }, unsafe {
            GetDpiForWindow(parent.0)
        });
        assert!(
            unsafe { GetLayeredWindowAttributes(hwnd, None, None, None) }.is_err(),
            "no color-key/global-alpha path"
        );

        let before = meter.shared.lock().observed;
        meter.set_monitor(sample);
        meter.refresh_layout();
        pump_until(|| meter.shared.lock().observed.layouts > before.layouts);
        let after = meter.shared.lock().observed;
        assert_eq!(
            after.renders, before.renders,
            "unchanged content is not resubmitted"
        );
        assert_eq!(
            after.positions, before.positions,
            "unchanged layout is not repositioned"
        );
        meter.set_monitor(TrayMonitor { fps: 222, ..sample });
        pump_until(|| meter.shared.lock().observed.frame.unwrap().glyphs == [0x5b; 3]);
        meter.shared.lock().layout.as_mut().unwrap().light = true;
        meter.refresh_layout();
        pump_until(|| meter.shared.lock().observed.frame.unwrap().color == 0xff343236);
        let larger = layout(
            RECT {
                right: 1600,
                bottom: 96,
                ..Default::default()
            },
            1200,
            192,
        )
        .unwrap();
        meter.shared.lock().layout.as_mut().unwrap().rect = larger;
        meter.refresh_layout();
        pump_until(|| {
            let observed = meter.shared.lock().observed;
            observed.frame.unwrap().width == 116 && observed.positions > before.positions
        });
        let mut actual = RECT::default();
        unsafe { GetClientRect(hwnd, &mut actual) }.unwrap();
        assert_eq!((actual.right, actual.bottom), (116, 96));
        assert!(meter.shared.lock().observed.positions > before.positions);
        unsafe {
            PostMessageW(Some(hwnd), WM_LBUTTONUP, WPARAM(0), LPARAM(0)).unwrap();
            PostMessageW(Some(hwnd), WM_RBUTTONUP, WPARAM(0), LPARAM(0)).unwrap();
        }
        pump_until(|| ACTIONS.load(Ordering::Relaxed) == 17);

        drop(parent);
        pump_until(|| meter.shared.lock().observed.hwnd == 0);
        assert!(!unsafe { IsWindow(Some(hwnd)) }.as_bool());
        assert_eq!(meter_rect(), None);
        meter.set_monitor(sample);
        let replacement = HiddenHost::new();
        *meter.shared.lock().layout.as_mut().unwrap() = TestLayout {
            parent: replacement.0.0 as isize,
            rect,
            light: false,
        };
        // The worker's own 500 ms timer must recreate the child without a UI layout call.
        pump_until(|| meter.shared.lock().observed.hwnd != 0);
        let recreated = HWND(meter.shared.lock().observed.hwnd as *mut _);
        assert_eq!(unsafe { GetParent(recreated) }.unwrap(), replacement.0);
        pump_until(|| meter.shared.lock().observed.frame.unwrap().glyphs == sample.glyphs());
        let shared = Arc::clone(&meter.shared);
        drop(meter);
        assert_eq!(
            shared.lock().host,
            0,
            "drop invalidates click destination immediately"
        );
        assert_eq!(meter_rect(), None);
        pump_until(|| shared.lock().observed.ended);
        assert!(!unsafe { IsWindow(Some(recreated)) }.as_bool());
        assert!(!unsafe { IsWindow(Some(control)) }.as_bool());

        let startup = TaskbarMeter::start(
            host.0,
            Some(TestLayout {
                parent: replacement.0.0 as isize,
                rect,
                light: false,
            }),
        );
        let startup_shared = Arc::clone(&startup.shared);
        drop(startup);
        pump_until(|| startup_shared.lock().observed.ended);
        assert_eq!(startup_shared.lock().control, 0);
        assert_eq!(startup_shared.lock().observed.hwnd, 0);
        let samples = [
            sample,
            TrayMonitor::default(),
            TrayMonitor {
                error: true,
                ..sample
            },
            TrayMonitor {
                not_a_number: true,
                ..sample
            },
        ];
        let output = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("target");
        save_composite(
            &output.join("taskbar-meter-alpha-dark.bmp"),
            false,
            &samples,
        );
        save_composite(
            &output.join("taskbar-meter-alpha-light.bmp"),
            true,
            &samples,
        );
        println!(
            "Threaded hidden layered child: distinct UI/worker threads, first frame, data, theme, resize, timer parent recreation, dedup, async routes, startup-drop and GDI/window cleanup passed; BMP rows=96/144/192 DPI, columns=111/---/Err/nAn"
        );
    }
}
