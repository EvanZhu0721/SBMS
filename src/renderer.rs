use std::mem::size_of;
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::sync::{Arc, mpsc};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use windows::Win32::Foundation::{
    CloseHandle, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, WAIT_OBJECT_0, WAIT_TIMEOUT, WPARAM,
};
use windows::Win32::Graphics::Gdi::{
    BI_RGB, BITMAPINFO, BITMAPINFOHEADER, COLORONCOLOR, DIB_RGB_COLORS, GetDC, ReleaseDC, SRCCOPY,
    SetStretchBltMode, StretchDIBits,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Memory::{
    FILE_MAP_READ, FILE_MAP_WRITE, MapViewOfFile, OpenFileMappingW, UnmapViewOfFile,
};
use windows::Win32::System::Threading::{
    OpenEventW, SYNCHRONIZATION_SYNCHRONIZE, WaitForSingleObject,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CS_HREDRAW, CS_VREDRAW, CURSOR_SHOWING, CURSORINFO, CreateWindowExW, DefWindowProcW,
    DestroyWindow, DispatchMessageW, GetCursorInfo, HICON, MSG, PM_REMOVE, PeekMessageW,
    PostQuitMessage, RegisterClassW, SW_SHOW, ShowWindow, TranslateMessage, UnregisterClassW,
    WINDOW_EX_STYLE, WM_CLOSE, WM_DESTROY, WM_QUIT, WNDCLASSW, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
    WS_EX_TOPMOST, WS_POPUP,
};
use windows::core::w;

use crate::display::Display;
use crate::frame_transport::{
    FRAME_BYTES, FRAME_PIXELS, FrameChannel, HEADER_BYTES, HEIGHT, MAGIC, STRIDE, WIDTH,
};
use crate::input::{InputGuard, flush_movement, handle_message};

const START_TIMEOUT: Duration = Duration::from_secs(10);
const STOP_TIMEOUT: Duration = Duration::from_secs(2);

#[repr(C, align(8))]
struct FrameHeader {
    magic: u32,
    width: u32,
    height: u32,
    stride: u32,
    published_slot: AtomicI32,
    reader_slot: AtomicI32,
}

pub struct Renderer {
    stop: Arc<AtomicBool>,
    done: mpsc::Receiver<Result<(), String>>,
    thread: Option<JoinHandle<()>>,
}

impl Renderer {
    pub fn start(target: Display, source: Display, channel: FrameChannel) -> Result<Self, String> {
        let stop = Arc::new(AtomicBool::new(false));
        let worker_stop = Arc::clone(&stop);
        let (ready_tx, ready_rx) = mpsc::channel();
        let (done_tx, done_rx) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || {
            let result = run(target.rect, source.rect, channel, &worker_stop, &ready_tx);
            if let Err(error) = &result {
                let _ = ready_tx.send(Err(error.clone()));
            }
            let _ = done_tx.send(result);
        });

        match ready_rx.recv_timeout(START_TIMEOUT) {
            Ok(Ok(())) => Ok(Self {
                stop,
                done: done_rx,
                thread: Some(thread),
            }),
            Ok(Err(error)) => {
                stop.store(true, Ordering::Release);
                let _ = thread.join();
                Err(error)
            }
            Err(_) => {
                stop.store(true, Ordering::Release);
                let _ = done_rx.recv_timeout(STOP_TIMEOUT);
                drop(thread);
                Err("renderer did not receive and draw a first frame within 10 seconds".into())
            }
        }
    }

    pub fn stop(&mut self) -> Result<(), String> {
        let Some(thread) = self.thread.take() else {
            return Ok(());
        };
        self.stop.store(true, Ordering::Release);
        let result = match self.done.recv_timeout(STOP_TIMEOUT) {
            Ok(result) => result,
            Err(_) => {
                drop(thread);
                return Err("renderer did not stop within 2 seconds".into());
            }
        };
        thread
            .join()
            .map_err(|_| "renderer thread panicked".to_string())?;
        result
    }
}

impl Drop for Renderer {
    fn drop(&mut self) {
        let _ = self.stop();
    }
}

fn run(
    target: RECT,
    source: RECT,
    channel: FrameChannel,
    stop: &AtomicBool,
    ready: &mpsc::Sender<Result<(), String>>,
) -> Result<(), String> {
    let instance = unsafe { GetModuleHandleW(None) }
        .map_err(|error| format!("GetModuleHandleW failed: {error}"))?;
    let instance = HINSTANCE(instance.0);
    let class_name = w!("SBMSMirrorWindow");
    let class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(window_proc),
        hInstance: instance,
        lpszClassName: class_name,
        ..Default::default()
    };
    if unsafe { RegisterClassW(&class) } == 0 {
        return Err("RegisterClassW failed".into());
    }

    let width = target.right - target.left;
    let height = target.bottom - target.top;
    let window = unsafe {
        CreateWindowExW(
            WINDOW_EX_STYLE(WS_EX_TOPMOST.0 | WS_EX_TOOLWINDOW.0 | WS_EX_NOACTIVATE.0),
            class_name,
            w!("SBMS"),
            WS_POPUP,
            target.left,
            target.top,
            width,
            height,
            None,
            None,
            Some(instance),
            None,
        )
    }
    .map_err(|error| {
        unsafe {
            let _ = UnregisterClassW(class_name, Some(instance));
        }
        format!("CreateWindowExW failed: {error}")
    })?;
    unsafe {
        let _ = ShowWindow(window, SW_SHOW);
    }

    let input = match InputGuard::start(window, target, source, instance) {
        Ok(input) => input,
        Err(error) => {
            unsafe {
                let _ = DestroyWindow(window);
                let _ = UnregisterClassW(class_name, Some(instance));
            }
            return Err(error);
        }
    };
    let result = thread::scope(|scope| {
        let (draw_done_tx, draw_done_rx) = mpsc::sync_channel(1);
        let window_value = window.0 as usize;
        let draw_thread = scope.spawn(move || {
            let window = HWND(window_value as *mut _);
            let result = draw_frames(window, width, height, source, channel, stop, ready);
            let _ = draw_done_tx.send(result);
        });
        let draw_result = loop {
            if let Ok(result) = draw_done_rx.try_recv() {
                break result;
            }
            if stop.load(Ordering::Acquire) {
                break draw_done_rx
                    .recv()
                    .map_err(|_| "renderer draw worker exited without a result".to_string())?;
            }
            if !pump_messages() {
                stop.store(true, Ordering::Release);
                continue;
            }
            flush_movement();
            thread::sleep(Duration::from_millis(1));
        };
        draw_thread
            .join()
            .map_err(|_| "renderer draw worker panicked".to_string())?;
        draw_result
    });
    drop(input);
    unsafe {
        let _ = DestroyWindow(window);
        let _ = UnregisterClassW(class_name, Some(instance));
    }
    result
}

fn draw_frames(
    window: HWND,
    target_width: i32,
    target_height: i32,
    source: RECT,
    channel: FrameChannel,
    stop: &AtomicBool,
    ready: &mpsc::Sender<Result<(), String>>,
) -> Result<(), String> {
    let mut reader = FrameReader::open(channel)?;
    let dc = unsafe { GetDC(Some(window)) };
    if dc.is_invalid() {
        return Err("GetDC(target) failed".into());
    }
    unsafe {
        SetStretchBltMode(dc, COLORONCOLOR);
    }

    let bitmap = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: WIDTH as i32,
            biHeight: -(HEIGHT as i32),
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            biSizeImage: (STRIDE * HEIGHT) as u32,
            ..Default::default()
        },
        ..Default::default()
    };
    let mut first_frame = true;
    let mut last_cursor = None;
    let result = loop {
        if stop.load(Ordering::Acquire) {
            break Ok(());
        }
        let cursor = cursor_snapshot(source);
        let cursor_changed = cursor != last_cursor;
        let Some(frame) = reader.acquire(cursor_changed)? else {
            continue;
        };
        let _cursor_overlay =
            cursor.map(|cursor| CursorOverlay::apply(frame.pixels, cursor, source));
        let copied = unsafe {
            StretchDIBits(
                dc,
                0,
                0,
                target_width,
                target_height,
                0,
                0,
                WIDTH as i32,
                HEIGHT as i32,
                Some(frame.pixels.cast()),
                &bitmap,
                DIB_RGB_COLORS,
                SRCCOPY,
            )
        };
        if copied == 0 {
            break Err("StretchDIBits failed".into());
        }
        last_cursor = cursor;
        if first_frame {
            ready
                .send(Ok(()))
                .map_err(|_| "mapping start was cancelled".to_string())?;
            first_frame = false;
        }
    };
    unsafe {
        ReleaseDC(Some(window), dc);
    }
    result
}

#[derive(Clone, Copy, PartialEq)]
struct CursorSnapshot {
    position: POINT,
    icon: HICON,
}

fn cursor_snapshot(source: RECT) -> Option<CursorSnapshot> {
    let mut info = CURSORINFO {
        cbSize: size_of::<CURSORINFO>() as u32,
        ..Default::default()
    };
    unsafe { GetCursorInfo(&mut info) }.ok()?;
    if info.flags.0 & CURSOR_SHOWING.0 == 0
        || info.ptScreenPos.x < source.left
        || info.ptScreenPos.x >= source.right
        || info.ptScreenPos.y < source.top
        || info.ptScreenPos.y >= source.bottom
    {
        return None;
    }
    Some(CursorSnapshot {
        position: info.ptScreenPos,
        icon: HICON(info.hCursor.0),
    })
}

struct CursorOverlay {
    pixels: *mut u8,
    left: usize,
    top: usize,
    width: usize,
    height: usize,
    backup: Vec<u8>,
}

impl CursorOverlay {
    fn apply(pixels: *const u8, cursor: CursorSnapshot, source: RECT) -> Self {
        const MARKER_WIDTH: i32 = 18;
        const MARKER_HEIGHT: i32 = 28;

        let marker_left = cursor.position.x - source.left;
        let marker_top = cursor.position.y - source.top;
        let left = marker_left.clamp(0, WIDTH as i32) as usize;
        let top = marker_top.clamp(0, HEIGHT as i32) as usize;
        let right = (marker_left + MARKER_WIDTH).clamp(0, WIDTH as i32) as usize;
        let bottom = (marker_top + MARKER_HEIGHT).clamp(0, HEIGHT as i32) as usize;
        let width = right.saturating_sub(left);
        let height = bottom.saturating_sub(top);
        let pixels = pixels.cast_mut();
        let mut backup = vec![0u8; width * height * 4];

        for row in 0..height {
            unsafe {
                std::ptr::copy_nonoverlapping(
                    pixels.add((top + row) * STRIDE + left * 4),
                    backup.as_mut_ptr().add(row * width * 4),
                    width * 4,
                );
            }
        }
        for row in 0..height {
            for column in 0..width {
                let marker_x = left as i32 + column as i32 - marker_left;
                let marker_y = top as i32 + row as i32 - marker_top;
                if !cursor_marker_contains(marker_x, marker_y) {
                    continue;
                }
                let boundary = !cursor_marker_contains(marker_x - 1, marker_y)
                    || !cursor_marker_contains(marker_x + 1, marker_y)
                    || !cursor_marker_contains(marker_x, marker_y - 1)
                    || !cursor_marker_contains(marker_x, marker_y + 1);
                let color = if boundary { 0 } else { 255 };
                let pixel = unsafe { pixels.add((top + row) * STRIDE + (left + column) * 4) };
                unsafe {
                    pixel.write(color);
                    pixel.add(1).write(color);
                    pixel.add(2).write(color);
                    pixel.add(3).write(255);
                }
            }
        }

        Self {
            pixels,
            left,
            top,
            width,
            height,
            backup,
        }
    }
}

impl Drop for CursorOverlay {
    fn drop(&mut self) {
        for row in 0..self.height {
            unsafe {
                std::ptr::copy_nonoverlapping(
                    self.backup.as_ptr().add(row * self.width * 4),
                    self.pixels.add((self.top + row) * STRIDE + self.left * 4),
                    self.width * 4,
                );
            }
        }
    }
}

fn cursor_marker_contains(x: i32, y: i32) -> bool {
    (0..20).contains(&y) && (0..=(y / 2)).contains(&x)
        || (4..9).contains(&x) && (10..27).contains(&y)
}

struct FrameReader {
    mapping: windows::Win32::Foundation::HANDLE,
    event: windows::Win32::Foundation::HANDLE,
    view: windows::Win32::System::Memory::MEMORY_MAPPED_VIEW_ADDRESS,
}

impl FrameReader {
    fn open(channel: FrameChannel) -> Result<Self, String> {
        let mapping = unsafe {
            OpenFileMappingW(FILE_MAP_READ.0 | FILE_MAP_WRITE.0, false, channel.mapping())
        }
        .map_err(|error| format!("OpenFileMappingW failed: {error}"))?;
        let view =
            unsafe { MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, FRAME_BYTES) };
        if view.Value.is_null() {
            unsafe {
                let _ = CloseHandle(mapping);
            }
            return Err("MapViewOfFile failed".into());
        }
        let event = match unsafe { OpenEventW(SYNCHRONIZATION_SYNCHRONIZE, false, channel.event()) }
        {
            Ok(event) => event,
            Err(error) => {
                unsafe {
                    let _ = UnmapViewOfFile(view);
                    let _ = CloseHandle(mapping);
                }
                return Err(format!("OpenEventW failed: {error}"));
            }
        };
        Ok(Self {
            mapping,
            event,
            view,
        })
    }

    fn acquire(&mut self, reuse_latest_on_timeout: bool) -> Result<Option<FrameLease>, String> {
        let wait = unsafe { WaitForSingleObject(self.event, 4) };
        if wait == WAIT_TIMEOUT && !reuse_latest_on_timeout {
            return Ok(None);
        }
        if wait != WAIT_OBJECT_0 && wait != WAIT_TIMEOUT {
            return Err(format!("frame event wait failed: {}", wait.0));
        }

        let header = self.view.Value.cast::<FrameHeader>();
        let valid = unsafe {
            (*header).magic == MAGIC
                && (*header).width == WIDTH as u32
                && (*header).height == HEIGHT as u32
                && (*header).stride == STRIDE as u32
        };
        if !valid {
            return Err(unsafe {
                format!(
                    "driver frame error magic=0x{:08x} stage={} hr=0x{:08x} detail={}",
                    (*header).magic,
                    (*header).width,
                    (*header).height,
                    (*header).stride
                )
            });
        }

        loop {
            let published = unsafe { &(*header).published_slot }.load(Ordering::Acquire);
            if !(0..=1).contains(&published) {
                return Ok(None);
            }
            unsafe { &(*header).reader_slot }.store(published, Ordering::Release);
            if unsafe { &(*header).published_slot }.load(Ordering::Acquire) == published {
                let pixels = unsafe {
                    self.view
                        .Value
                        .cast::<u8>()
                        .add(HEADER_BYTES + published as usize * FRAME_PIXELS)
                };
                return Ok(Some(FrameLease {
                    pixels,
                    reader_slot: unsafe { &(*header).reader_slot },
                }));
            }
            unsafe { &(*header).reader_slot }.store(-1, Ordering::Release);
        }
    }
}

struct FrameLease {
    pixels: *const u8,
    reader_slot: *const AtomicI32,
}

impl Drop for FrameLease {
    fn drop(&mut self) {
        unsafe { &*self.reader_slot }.store(-1, Ordering::Release);
    }
}

impl Drop for FrameReader {
    fn drop(&mut self) {
        unsafe {
            let _ = UnmapViewOfFile(self.view);
            let _ = CloseHandle(self.event);
            let _ = CloseHandle(self.mapping);
        }
    }
}

fn pump_messages() -> bool {
    let mut message = MSG::default();
    while unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool() {
        if message.message == WM_QUIT {
            return false;
        }
        unsafe {
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
    true
}

unsafe extern "system" fn window_proc(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if let Some(result) = handle_message(message, lparam) {
        return result;
    }
    match message {
        WM_CLOSE => {
            unsafe {
                let _ = DestroyWindow(window);
            }
            LRESULT(0)
        }
        WM_DESTROY => {
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(window, message, wparam, lparam) },
    }
}
