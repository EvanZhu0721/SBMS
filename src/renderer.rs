use std::mem::size_of;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::{Arc, mpsc};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use windows::Win32::Foundation::{
    CloseHandle, HINSTANCE, HWND, LPARAM, LRESULT, RECT, WAIT_OBJECT_0, WAIT_TIMEOUT, WPARAM,
};
use windows::Win32::Graphics::Gdi::{
    BI_RGB, BITMAPINFO, BITMAPINFOHEADER, DIB_RGB_COLORS, GetDC, ReleaseDC, SRCCOPY,
    STRETCH_HALFTONE, SetStretchBltMode, StretchDIBits,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Memory::{
    FILE_MAP_READ, MapViewOfFile, OpenFileMappingW, UnmapViewOfFile,
};
use windows::Win32::System::Threading::{
    OpenEventW, SYNCHRONIZATION_SYNCHRONIZE, WaitForSingleObject,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CS_HREDRAW, CS_VREDRAW, CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, MSG,
    PM_REMOVE, PeekMessageW, PostQuitMessage, RegisterClassW, SW_SHOW, ShowWindow,
    TranslateMessage, UnregisterClassW, WINDOW_EX_STYLE, WM_CLOSE, WM_DESTROY, WNDCLASSW,
    WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_POPUP,
};
use windows::core::w;

use crate::display::Display;
use crate::frame_transport::{
    FRAME_BYTES, FRAME_EVENT, FRAME_MAPPING, HEADER_BYTES, HEIGHT, MAGIC, STRIDE, WIDTH,
};

const START_TIMEOUT: Duration = Duration::from_secs(10);
const STOP_TIMEOUT: Duration = Duration::from_secs(2);

#[repr(C, align(8))]
struct FrameHeader {
    magic: u32,
    width: u32,
    height: u32,
    stride: u32,
    sequence: AtomicI64,
}

pub struct Renderer {
    stop: Arc<AtomicBool>,
    done: mpsc::Receiver<Result<(), String>>,
    thread: Option<JoinHandle<()>>,
}

impl Renderer {
    pub fn start(target: Display) -> Result<Self, String> {
        let stop = Arc::new(AtomicBool::new(false));
        let worker_stop = Arc::clone(&stop);
        let (ready_tx, ready_rx) = mpsc::channel();
        let (done_tx, done_rx) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || {
            let result = run(target.rect, &worker_stop, &ready_tx);
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

    let result = draw_frames(window, width, height, stop, ready);
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
    stop: &AtomicBool,
    ready: &mpsc::Sender<Result<(), String>>,
) -> Result<(), String> {
    let mut reader = FrameReader::open()?;
    let dc = unsafe { GetDC(Some(window)) };
    if dc.is_invalid() {
        return Err("GetDC(target) failed".into());
    }
    unsafe {
        SetStretchBltMode(dc, STRETCH_HALFTONE);
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
    let result = loop {
        if stop.load(Ordering::Acquire) {
            break Ok(());
        }
        pump_messages();
        if !reader.read()? {
            continue;
        }
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
                Some(reader.pixels.as_ptr().cast()),
                &bitmap,
                DIB_RGB_COLORS,
                SRCCOPY,
            )
        };
        if copied == 0 {
            break Err("StretchDIBits failed".into());
        }
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

struct FrameReader {
    mapping: windows::Win32::Foundation::HANDLE,
    event: windows::Win32::Foundation::HANDLE,
    view: windows::Win32::System::Memory::MEMORY_MAPPED_VIEW_ADDRESS,
    pixels: Vec<u8>,
}

impl FrameReader {
    fn open() -> Result<Self, String> {
        let mapping = unsafe { OpenFileMappingW(FILE_MAP_READ.0, false, FRAME_MAPPING) }
            .map_err(|error| format!("OpenFileMappingW failed: {error}"))?;
        let view = unsafe { MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, FRAME_BYTES) };
        if view.Value.is_null() {
            unsafe {
                let _ = CloseHandle(mapping);
            }
            return Err("MapViewOfFile failed".into());
        }
        let event = match unsafe { OpenEventW(SYNCHRONIZATION_SYNCHRONIZE, false, FRAME_EVENT) } {
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
            pixels: vec![0; STRIDE * HEIGHT],
        })
    }

    fn read(&mut self) -> Result<bool, String> {
        let wait = unsafe { WaitForSingleObject(self.event, 16) };
        if wait == WAIT_TIMEOUT {
            return Ok(false);
        }
        if wait != WAIT_OBJECT_0 {
            return Err(format!("frame event wait failed: {}", wait.0));
        }

        let header = self.view.Value.cast::<FrameHeader>();
        let sequence = unsafe { &(*header).sequence }.load(Ordering::Acquire);
        if sequence & 1 != 0 {
            return Ok(false);
        }
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

        let source = unsafe {
            std::slice::from_raw_parts(
                self.view.Value.cast::<u8>().add(HEADER_BYTES),
                STRIDE * HEIGHT,
            )
        };
        self.pixels.copy_from_slice(source);
        let after = unsafe { &(*header).sequence }.load(Ordering::Acquire);
        Ok(sequence == after && after & 1 == 0)
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

fn pump_messages() {
    let mut message = MSG::default();
    while unsafe { PeekMessageW(&mut message, None, 0, 0, PM_REMOVE) }.as_bool() {
        unsafe {
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
}

unsafe extern "system" fn window_proc(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
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
