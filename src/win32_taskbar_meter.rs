use std::cell::Cell;
use std::mem::size_of;
use std::sync::atomic::{AtomicIsize, Ordering};

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
// Nonzero alpha gives the entire rectangle native hit testing, including digit gaps.
const BACKGROUND_PIXEL: u32 = 0x01000000;
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
}

pub(super) struct TaskbarMeter {
    hwnd: Cell<HWND>,
    state: Box<State>,
    monitor: Cell<TrayMonitor>,
    light: Cell<bool>,
    placed: Cell<Option<RECT>>,
    rendered: Cell<Option<Frame>>,
}

impl TaskbarMeter {
    pub(super) fn new(host: HWND) -> Self {
        Self {
            hwnd: Cell::new(HWND::default()),
            state: Box::new(State { host }),
            monitor: Cell::new(TrayMonitor::default()),
            light: Cell::new(false),
            placed: Cell::new(None),
            rendered: Cell::new(None),
        }
    }

    pub(super) fn set_monitor(&self, monitor: TrayMonitor) {
        self.monitor.set(monitor);
        self.redraw();
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

    pub(super) fn refresh_layout(&self) {
        let light = light_taskbar();
        if self.light.replace(light) != light {
            self.redraw();
        }
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
        if let Err(error) = self.apply_layout(taskbar, rect) {
            log_failure("taskbar-meter-layout", &error);
        }
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
            CURRENT_METER.store(0, Ordering::Release);
            let instance = unsafe { GetModuleHandleW(None) }?;
            let class = WNDCLASSW {
                lpfnWndProc: Some(window_proc),
                hInstance: instance.into(),
                lpszClassName: CLASS,
                ..Default::default()
            };
            unsafe { RegisterClassW(&class) };
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
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, &*self.state as *const State as isize);
            }
            CURRENT_METER.store(hwnd.0 as isize, Ordering::Release);
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
        Ok(())
    }

    fn render(&self, rect: RECT) -> Result<bool> {
        let frame = Frame::new(rect, self.monitor.get(), self.light.get());
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
                if let Err(error) =
                    unsafe { PostMessageW(Some(state.host), message, WPARAM(0), LPARAM(0)) }
                {
                    log_failure("taskbar-meter-post", &error);
                }
            }
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
    use windows::Win32::Graphics::Gdi::{
        BITMAP, GdiFlush, GetCurrentObject, GetObjectType, GetObjectW, OBJ_BITMAP,
    };

    static ACTIONS: AtomicU32 = AtomicU32::new(0);
    static POSITIONS: AtomicU32 = AtomicU32::new(0);

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

    unsafe extern "system" fn observed_meter_proc(
        hwnd: HWND,
        message: u32,
        wp: WPARAM,
        lp: LPARAM,
    ) -> LRESULT {
        if message == WM_WINDOWPOSCHANGING {
            POSITIONS.fetch_add(1, Ordering::Relaxed);
        }
        unsafe { window_proc(hwnd, message, wp, lp) }
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
        let meter = TaskbarMeter::new(host.0);
        let sample = TrayMonitor {
            fps: 111,
            running: true,
            valid: true,
            ..Default::default()
        };
        meter.set_monitor(sample);
        assert_eq!(
            meter.monitor.get(),
            sample,
            "accept data before a taskbar exists"
        );
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
        meter.apply_layout(parent.0, rect).unwrap();
        let hwnd = meter.hwnd.get();
        assert_eq!(unsafe { GetParent(hwnd) }.unwrap(), parent.0);
        assert!(!unsafe { IsWindowVisible(hwnd) }.as_bool());
        assert_eq!(unsafe { GetDpiForWindow(hwnd) }, unsafe {
            GetDpiForWindow(parent.0)
        });
        assert_eq!(meter.rendered.get().unwrap().glyphs, sample.glyphs());
        assert!(
            unsafe { GetLayeredWindowAttributes(hwnd, None, None, None) }.is_err(),
            "no color-key/global-alpha path"
        );
        unsafe {
            SetWindowLongPtrW(
                hwnd,
                GWLP_WNDPROC,
                observed_meter_proc as *const () as isize,
            )
        };
        POSITIONS.store(0, Ordering::Relaxed);
        meter.apply_layout(parent.0, rect).unwrap();
        meter.set_monitor(sample);
        assert!(
            !meter.render(rect).unwrap(),
            "unchanged content is not resubmitted"
        );
        assert_eq!(
            POSITIONS.load(Ordering::Relaxed),
            0,
            "unchanged layout is not repositioned"
        );
        meter.set_monitor(TrayMonitor { fps: 222, ..sample });
        assert_eq!(meter.rendered.get().unwrap().glyphs, [0x5b; 3]);
        meter.light.set(true);
        assert!(meter.render(rect).unwrap(), "theme change resubmits pixels");
        assert_eq!(meter.rendered.get().unwrap().color, 0xff343236);
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
        meter.apply_layout(parent.0, larger).unwrap();
        let mut actual = RECT::default();
        unsafe { GetClientRect(hwnd, &mut actual) }.unwrap();
        assert_eq!((actual.right, actual.bottom), (116, 96));
        assert!(POSITIONS.load(Ordering::Relaxed) > 0);
        unsafe {
            SendMessageW(hwnd, WM_LBUTTONUP, None, None);
            SendMessageW(hwnd, WM_RBUTTONUP, None, None);
        }
        let mut message = MSG::default();
        while unsafe { PeekMessageW(&mut message, Some(host.0), 0, 0, PM_REMOVE) }.as_bool() {
            unsafe { DispatchMessageW(&message) };
        }
        assert_eq!(
            ACTIONS.load(Ordering::Relaxed),
            17,
            "one left and one right host action"
        );
        drop(parent);
        assert!(!unsafe { IsWindow(Some(hwnd)) }.as_bool());
        meter.set_monitor(sample);
        let replacement = HiddenHost::new();
        meter.apply_layout(replacement.0, rect).unwrap();
        let recreated = meter.hwnd.get();
        assert_eq!(unsafe { GetParent(recreated) }.unwrap(), replacement.0);
        assert_eq!(meter.rendered.get().unwrap().glyphs, sample.glyphs());
        drop(meter);
        assert!(!unsafe { IsWindow(Some(recreated)) }.as_bool());
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
            "Hidden layered child: first frame, data, theme, resize, parent recreation, dedup, routes, GDI/window cleanup passed; BMP rows=96/144/192 DPI, columns=111/---/Err/nAn"
        );
    }
}
