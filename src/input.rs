use std::cell::RefCell;
use std::mem::{MaybeUninit, size_of};
use std::sync::Mutex;
use std::sync::atomic::{AtomicUsize, Ordering};

use windows::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, WPARAM};
use windows::Win32::Security::Cryptography::{BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptGenRandom};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetAsyncKeyState, INPUT, INPUT_0, INPUT_MOUSE, MOUSE_EVENT_FLAGS, MOUSEEVENTF_ABSOLUTE,
    MOUSEEVENTF_HWHEEL, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, MOUSEEVENTF_MIDDLEDOWN,
    MOUSEEVENTF_MIDDLEUP, MOUSEEVENTF_MOVE, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP,
    MOUSEEVENTF_VIRTUALDESK, MOUSEEVENTF_WHEEL, MOUSEEVENTF_XDOWN, MOUSEEVENTF_XUP, MOUSEINPUT,
    SendInput, VK_LSHIFT, VK_LWIN, VK_RSHIFT, VK_RWIN, VK_SNAPSHOT,
};
use windows::Win32::UI::Input::{
    GetRawInputData, HRAWINPUT, MOUSE_MOVE_ABSOLUTE, RAWINPUT, RAWINPUTDEVICE, RID_INPUT,
    RIDEV_INPUTSINK, RIDEV_REMOVE, RIM_TYPEMOUSE, RegisterRawInputDevices,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, ClipCursor, GetClipCursor, GetSystemMetrics, HC_ACTION, HHOOK, KBDLLHOOKSTRUCT,
    LLMHF_INJECTED, MSLLHOOKSTRUCT, PostMessageW, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN,
    SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SetCursorPos, SetWindowsHookExW, UnhookWindowsHookEx,
    WH_KEYBOARD_LL, WH_MOUSE_LL, WM_APP, WM_INPUT, WM_KEYDOWN, WM_LBUTTONDOWN, WM_LBUTTONUP,
    WM_MBUTTONDOWN, WM_MBUTTONUP, WM_MOUSEHWHEEL, WM_MOUSEMOVE, WM_MOUSEWHEEL, WM_RBUTTONDOWN,
    WM_RBUTTONUP, WM_SYSKEYDOWN, WM_XBUTTONDOWN, WM_XBUTTONUP,
};

use crate::geometry::{CoordinateTransform, PixelPoint, PixelRect, Rotation};

const WM_RELEASE_CAPTURE: u32 = WM_APP + 1;
const RELEASE_NORMAL: isize = 0;
const RELEASE_INJECTION_FAILURE: isize = 1;
const RELEASE_EXTERNAL_INJECTION: isize = 2;
const RELEASE_ABSOLUTE_INPUT: isize = 3;

const BUTTON_LEFT: u8 = 1 << 0;
const BUTTON_RIGHT: u8 = 1 << 1;
const BUTTON_MIDDLE: u8 = 1 << 2;
const BUTTON_X1: u8 = 1 << 3;
const BUTTON_X2: u8 = 1 << 4;

const NO_INPUT_ENDPOINT: usize = 0;
const INPUT_TAG_MASK: usize = if usize::BITS >= 64 {
    0xffff_ffff_0000_0000_u64 as usize
} else {
    0xffff_ffff_u64 as usize
};
const INPUT_TAG_SIGNATURE: usize = if usize::BITS >= 64 {
    0x5342_4d53_0000_0000_u64 as usize
} else {
    0x5342_4d53_u64 as usize
};

static ACTIVE_INPUT_ENDPOINT: ActiveEndpoint = ActiveEndpoint::new();
static INPUT_COORDINATION: Mutex<InputCoordination> = Mutex::new(InputCoordination::new());

thread_local! {
    static INPUT_STATE: RefCell<Option<InputMapper>> = const { RefCell::new(None) };
}

struct ActiveEndpoint {
    window: AtomicUsize,
}

impl ActiveEndpoint {
    const fn new() -> Self {
        Self {
            window: AtomicUsize::new(NO_INPUT_ENDPOINT),
        }
    }

    fn replace(&self, window: usize) -> usize {
        self.window.swap(window, Ordering::AcqRel)
    }

    fn restore(&self, expected: usize, previous: usize) -> bool {
        self.window
            .compare_exchange(expected, previous, Ordering::AcqRel, Ordering::Acquire)
            .is_ok()
    }

    fn release(&self, window: usize) -> bool {
        self.restore(window, NO_INPUT_ENDPOINT)
    }

    fn owns(&self, window: usize) -> bool {
        window != NO_INPUT_ENDPOINT && self.window.load(Ordering::Acquire) == window
    }
}

struct InputCoordination {
    clip_baseline: Option<ClipBaseline>,
}

impl InputCoordination {
    const fn new() -> Self {
        Self {
            clip_baseline: None,
        }
    }
}

#[derive(Clone, Copy)]
struct ClipBaseline {
    rect: RECT,
    was_full_desktop: bool,
}

struct InputMapper {
    target: RECT,
    source: RECT,
    transform: CoordinateTransform,
    cursor: POINT,
    move_pending: bool,
    captured: bool,
    pressed: u8,
    tag: usize,
    window: HWND,
    mouse_hook: HHOOK,
    keyboard_hook: HHOOK,
}

pub struct InputGuard;

impl InputGuard {
    pub fn start(
        window: HWND,
        target: RECT,
        source: RECT,
        instance: HINSTANCE,
    ) -> Result<Self, String> {
        let transform = CoordinateTransform::stretch(
            PixelRect {
                left: 0,
                top: 0,
                width: rect_extent(target.left, target.right)?,
                height: rect_extent(target.top, target.bottom)?,
            },
            PixelRect {
                left: source.left,
                top: source.top,
                width: rect_extent(source.left, source.right)?,
                height: rect_extent(source.top, source.bottom)?,
            },
            Rotation::Deg0,
        )
        .map_err(|error| format!("input geometry is invalid: {error}"))?;
        let mut nonce = [0u8; size_of::<usize>()];
        let status = unsafe { BCryptGenRandom(None, &mut nonce, BCRYPT_USE_SYSTEM_PREFERRED_RNG) };
        if status.0 < 0 {
            return Err(format!("BCryptGenRandom failed: 0x{:08x}", status.0 as u32));
        }
        let tag = input_tag(usize::from_le_bytes(nonce));

        let mouse_hook =
            unsafe { SetWindowsHookExW(WH_MOUSE_LL, Some(mouse_hook), Some(instance), 0) }
                .map_err(|error| format!("SetWindowsHookExW(mouse) failed: {error}"))?;
        let keyboard_hook = match unsafe {
            SetWindowsHookExW(WH_KEYBOARD_LL, Some(keyboard_hook), Some(instance), 0)
        } {
            Ok(hook) => hook,
            Err(error) => {
                unsafe {
                    let _ = UnhookWindowsHookEx(mouse_hook);
                }
                return Err(format!("SetWindowsHookExW(keyboard) failed: {error}"));
            }
        };

        INPUT_STATE.with(|cell| {
            *cell.borrow_mut() = Some(InputMapper {
                target,
                source,
                transform,
                cursor: POINT {
                    x: (target.right - target.left).max(1) / 2,
                    y: (target.bottom - target.top).max(1) / 2,
                },
                move_pending: false,
                captured: false,
                pressed: 0,
                tag,
                window,
                mouse_hook,
                keyboard_hook,
            });
        });
        Ok(Self)
    }
}

impl Drop for InputGuard {
    fn drop(&mut self) {
        cleanup();
    }
}

pub fn handle_message(message: u32, lparam: LPARAM) -> Option<LRESULT> {
    match message {
        WM_LBUTTONDOWN => {
            let x = low_word_signed(lparam.0);
            let y = high_word_signed(lparam.0);
            if let Err(error) = capture_at(x, y) {
                eprintln!("warning: input capture failed: {error}");
            }
            Some(LRESULT(0))
        }
        WM_INPUT => {
            if let Err(error) = handle_raw_input(HRAWINPUT(lparam.0 as *mut _)) {
                eprintln!("warning: raw mouse input failed: {error}");
                release_capture();
            }
            Some(LRESULT(0))
        }
        WM_RELEASE_CAPTURE => {
            release_capture();
            match lparam.0 {
                RELEASE_INJECTION_FAILURE => {
                    eprintln!(
                        "warning: input capture released because SendInput was rejected (possibly UIPI)"
                    )
                }
                RELEASE_EXTERNAL_INJECTION => {
                    eprintln!("warning: input capture released after foreign injected mouse input")
                }
                RELEASE_ABSOLUTE_INPUT => {
                    eprintln!(
                        "warning: absolute mouse/touch/pen input is not supported during capture"
                    )
                }
                _ => {}
            }
            Some(LRESULT(0))
        }
        _ => None,
    }
}

fn capture_at(x: i32, y: i32) -> Result<(), String> {
    let (source, point, tag, window) = INPUT_STATE.with(|cell| {
        let mut state = cell.borrow_mut();
        let state = state
            .as_mut()
            .ok_or_else(|| "input mapper is not initialized".to_string())?;
        let width = (state.target.right - state.target.left).max(1);
        let height = (state.target.bottom - state.target.top).max(1);
        state.cursor.x = x.clamp(0, width - 1);
        state.cursor.y = y.clamp(0, height - 1);
        Ok::<_, String>((state.source, source_point(state), state.tag, state.window))
    })?;

    let previous = activate_endpoint(window, source)?;
    if previous != NO_INPUT_ENDPOINT && previous != window_key(window) {
        post_release(window_from_key(previous), RELEASE_NORMAL);
    }
    if !send_mouse(point, MOUSEEVENTF_MOVE, 0, tag)
        || !send_mouse(point, MOUSEEVENTF_LEFTDOWN, 0, tag)
    {
        deactivate_endpoint(window);
        return Err(format!(
            "SendInput failed: {} (the source may run at a higher integrity level)",
            windows::core::Error::from_thread()
        ));
    }
    INPUT_STATE.with(|cell| {
        if let Some(state) = cell.borrow_mut().as_mut() {
            state.captured = true;
            state.pressed |= BUTTON_LEFT;
        }
    });
    Ok(())
}

fn handle_raw_input(handle: HRAWINPUT) -> Result<(), String> {
    let mut raw = MaybeUninit::<RAWINPUT>::zeroed();
    let mut bytes = size_of::<RAWINPUT>() as u32;
    let read = unsafe {
        GetRawInputData(
            handle,
            RID_INPUT,
            Some(raw.as_mut_ptr().cast()),
            &mut bytes,
            size_of::<windows::Win32::UI::Input::RAWINPUTHEADER>() as u32,
        )
    };
    if read == u32::MAX || read < size_of::<RAWINPUT>() as u32 {
        return Err(format!("GetRawInputData returned {read} bytes"));
    }
    let raw = unsafe { raw.assume_init() };
    if raw.header.dwType != RIM_TYPEMOUSE.0 {
        return Ok(());
    }
    let mouse = unsafe { raw.data.mouse };
    INPUT_STATE.with(|cell| {
        let mut state = cell.borrow_mut();
        let Some(state) = state.as_mut() else {
            return;
        };
        if !state.captured || !ACTIVE_INPUT_ENDPOINT.owns(window_key(state.window)) {
            return;
        }
        if mouse.usFlags.0 & MOUSE_MOVE_ABSOLUTE.0 != 0 {
            post_release(state.window, RELEASE_ABSOLUTE_INPUT);
            return;
        }
        let width = (state.target.right - state.target.left).max(1);
        let height = (state.target.bottom - state.target.top).max(1);
        state.cursor.x = state
            .cursor
            .x
            .saturating_add(mouse.lLastX)
            .clamp(0, width - 1);
        state.cursor.y = state
            .cursor
            .y
            .saturating_add(mouse.lLastY)
            .clamp(0, height - 1);
        state.move_pending = true;
    });
    Ok(())
}

unsafe extern "system" fn mouse_hook(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code < HC_ACTION as i32 {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }
    flush_movement();
    let event = unsafe { &*(lparam.0 as *const MSLLHOOKSTRUCT) };
    let snapshot = INPUT_STATE.with(|cell| {
        cell.borrow().as_ref().map(|state| {
            (
                state.captured && ACTIVE_INPUT_ENDPOINT.owns(window_key(state.window)),
                state.tag,
                state.window,
                source_point(state),
            )
        })
    });
    let Some((captured, tag, window, point)) = snapshot else {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    };
    if event.flags & LLMHF_INJECTED != 0 {
        if is_managed_input_tag(event.dwExtraInfo) {
            return unsafe { CallNextHookEx(None, code, wparam, lparam) };
        }
        if captured {
            post_release(window, RELEASE_EXTERNAL_INJECTION);
        }
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }
    if !captured {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }

    let (flags, data, pressed_bit, is_down) = match wparam.0 as u32 {
        WM_MOUSEMOVE => return LRESULT(1),
        WM_LBUTTONDOWN => (MOUSEEVENTF_LEFTDOWN, 0, BUTTON_LEFT, true),
        WM_LBUTTONUP => (MOUSEEVENTF_LEFTUP, 0, BUTTON_LEFT, false),
        WM_RBUTTONDOWN => (MOUSEEVENTF_RIGHTDOWN, 0, BUTTON_RIGHT, true),
        WM_RBUTTONUP => (MOUSEEVENTF_RIGHTUP, 0, BUTTON_RIGHT, false),
        WM_MBUTTONDOWN => (MOUSEEVENTF_MIDDLEDOWN, 0, BUTTON_MIDDLE, true),
        WM_MBUTTONUP => (MOUSEEVENTF_MIDDLEUP, 0, BUTTON_MIDDLE, false),
        WM_MOUSEWHEEL => (
            MOUSEEVENTF_WHEEL,
            ((event.mouseData >> 16) as u16 as i16 as i32) as u32,
            0,
            false,
        ),
        WM_MOUSEHWHEEL => (
            MOUSEEVENTF_HWHEEL,
            ((event.mouseData >> 16) as u16 as i16 as i32) as u32,
            0,
            false,
        ),
        WM_XBUTTONDOWN => {
            let data = (event.mouseData >> 16) & 0xffff;
            (MOUSEEVENTF_XDOWN, data, x_button_bit(data), true)
        }
        WM_XBUTTONUP => {
            let data = (event.mouseData >> 16) & 0xffff;
            (MOUSEEVENTF_XUP, data, x_button_bit(data), false)
        }
        _ => return unsafe { CallNextHookEx(None, code, wparam, lparam) },
    };
    if send_mouse(point, flags, data, tag) {
        if pressed_bit != 0 {
            INPUT_STATE.with(|cell| {
                if let Some(state) = cell.borrow_mut().as_mut() {
                    if is_down {
                        state.pressed |= pressed_bit;
                    } else {
                        state.pressed &= !pressed_bit;
                    }
                }
            });
        }
    } else {
        post_release(window, RELEASE_INJECTION_FAILURE);
    }
    LRESULT(1)
}

unsafe extern "system" fn keyboard_hook(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code < HC_ACTION as i32 {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }
    let message = wparam.0 as u32;
    let event = unsafe { &*(lparam.0 as *const KBDLLHOOKSTRUCT) };
    let release_window = INPUT_STATE.with(|cell| {
        let state = cell.borrow();
        let state = state.as_ref()?;
        let active = ACTIVE_INPUT_ENDPOINT.owns(window_key(state.window));
        if !active || !state.captured || !matches!(message, WM_KEYDOWN | WM_SYSKEYDOWN) {
            return None;
        }
        if event.vkCode == VK_SNAPSHOT.0 as u32 || is_snipping_shortcut(event.vkCode) {
            return Some(state.window);
        }
        None
    });
    if let Some(window) = release_window {
        post_release(window, RELEASE_NORMAL);
    }
    unsafe { CallNextHookEx(None, code, wparam, lparam) }
}

fn is_snipping_shortcut(vk_code: u32) -> bool {
    const VK_S: u32 = b'S' as u32;
    vk_code == VK_S
        && (key_down(VK_LWIN.0 as i32) < 0 || key_down(VK_RWIN.0 as i32) < 0)
        && (key_down(VK_LSHIFT.0 as i32) < 0 || key_down(VK_RSHIFT.0 as i32) < 0)
}

fn key_down(key: i32) -> i16 {
    unsafe { GetAsyncKeyState(key) }
}

fn send_mouse(point: POINT, flags: MOUSE_EVENT_FLAGS, data: u32, tag: usize) -> bool {
    let desktop = virtual_desktop_rect();
    let dx = normalize(point.x - desktop.left, desktop.right - desktop.left);
    let dy = normalize(point.y - desktop.top, desktop.bottom - desktop.top);
    let input = INPUT {
        r#type: INPUT_MOUSE,
        Anonymous: INPUT_0 {
            mi: MOUSEINPUT {
                dx,
                dy,
                mouseData: data,
                dwFlags: MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | flags,
                time: 0,
                dwExtraInfo: tag,
            },
        },
    };
    (unsafe { SendInput(&[input], size_of::<INPUT>() as i32) }) == 1
}

pub(crate) fn flush_movement() {
    let pending = INPUT_STATE.with(|cell| {
        let mut state = cell.borrow_mut();
        let state = state.as_mut()?;
        if !state.captured
            || !state.move_pending
            || !ACTIVE_INPUT_ENDPOINT.owns(window_key(state.window))
        {
            return None;
        }
        state.move_pending = false;
        Some((source_point(state), state.tag, state.window))
    });
    if let Some((point, tag, window)) = pending
        && !send_mouse(point, MOUSEEVENTF_MOVE, 0, tag)
    {
        post_release(window, RELEASE_INJECTION_FAILURE);
    }
}

fn release_capture() {
    let release = INPUT_STATE.with(|cell| {
        let mut state = cell.borrow_mut();
        let state = state.as_mut()?;
        if !state.captured {
            return None;
        }
        state.captured = false;
        state.move_pending = false;
        let pressed = state.pressed;
        state.pressed = 0;
        Some((
            source_point(state),
            POINT {
                x: state.target.left + state.cursor.x,
                y: state.target.top + state.cursor.y,
            },
            state.tag,
            pressed,
            state.window,
        ))
    });
    if let Some((source, target, tag, pressed, window)) = release {
        let owned_endpoint = deactivate_endpoint(window);
        release_pressed_buttons(source, tag, pressed);
        if owned_endpoint {
            unsafe {
                let _ = SetCursorPos(target.x, target.y);
            }
        }
    }
}

fn release_pressed_buttons(point: POINT, tag: usize, pressed: u8) {
    for (bit, flags, data) in [
        (BUTTON_LEFT, MOUSEEVENTF_LEFTUP, 0),
        (BUTTON_RIGHT, MOUSEEVENTF_RIGHTUP, 0),
        (BUTTON_MIDDLE, MOUSEEVENTF_MIDDLEUP, 0),
        (BUTTON_X1, MOUSEEVENTF_XUP, 1),
        (BUTTON_X2, MOUSEEVENTF_XUP, 2),
    ] {
        if pressed & bit != 0 {
            let _ = send_mouse(point, flags, data, tag);
        }
    }
}

fn cleanup() {
    release_capture();
    let hooks = INPUT_STATE.with(|cell| {
        cell.borrow_mut()
            .take()
            .map(|state| (state.mouse_hook, state.keyboard_hook))
    });
    if let Some((mouse, keyboard)) = hooks {
        unsafe {
            let _ = UnhookWindowsHookEx(mouse);
            let _ = UnhookWindowsHookEx(keyboard);
        }
    }
}

fn activate_endpoint(window: HWND, source: RECT) -> Result<usize, String> {
    let window = window_key(window);
    let mut coordination = lock_coordination();
    let current = ACTIVE_INPUT_ENDPOINT.window.load(Ordering::Acquire);
    if current == window {
        unsafe { ClipCursor(Some(&source)) }
            .map_err(|error| format!("ClipCursor failed: {error}"))?;
        return Ok(current);
    }

    let added_baseline = current == NO_INPUT_ENDPOINT;
    if added_baseline {
        coordination.clip_baseline = Some(read_clip_baseline()?);
    }

    let previous = ACTIVE_INPUT_ENDPOINT.replace(window);
    debug_assert_eq!(previous, current);
    if let Err(error) = register_raw_mouse(window_from_key(window), RIDEV_INPUTSINK) {
        let _ = ACTIVE_INPUT_ENDPOINT.restore(window, previous);
        if added_baseline {
            coordination.clip_baseline = None;
        }
        return Err(error);
    }
    if let Err(error) = unsafe { ClipCursor(Some(&source)) } {
        let rollback_error =
            rollback_activation(&mut coordination, window, previous, added_baseline);
        return Err(match rollback_error {
            Some(rollback) => format!("ClipCursor failed: {error}; {rollback}"),
            None => format!("ClipCursor failed: {error}"),
        });
    }
    Ok(previous)
}

fn rollback_activation(
    coordination: &mut InputCoordination,
    window: usize,
    previous: usize,
    added_baseline: bool,
) -> Option<String> {
    let registration = if previous == NO_INPUT_ENDPOINT {
        unregister_raw_mouse()
    } else {
        register_raw_mouse(window_from_key(previous), RIDEV_INPUTSINK)
    };
    if registration.is_ok() {
        let _ = ACTIVE_INPUT_ENDPOINT.restore(window, previous);
        if added_baseline {
            coordination.clip_baseline = None;
        }
        return None;
    }

    let _ = unregister_raw_mouse();
    let _ = ACTIVE_INPUT_ENDPOINT.restore(window, NO_INPUT_ENDPOINT);
    restore_clip_baseline(coordination.clip_baseline.take());
    Some(format!(
        "raw input endpoint rollback failed: {}",
        registration.unwrap_err()
    ))
}

fn deactivate_endpoint(window: HWND) -> bool {
    let window = window_key(window);
    let mut coordination = lock_coordination();
    if !ACTIVE_INPUT_ENDPOINT.release(window) {
        return false;
    }
    if let Err(error) = unregister_raw_mouse() {
        eprintln!("warning: raw mouse cleanup failed: {error}");
    }
    restore_clip_baseline(coordination.clip_baseline.take());
    true
}

fn read_clip_baseline() -> Result<ClipBaseline, String> {
    let mut rect = RECT::default();
    unsafe { GetClipCursor(&mut rect) }
        .map_err(|error| format!("GetClipCursor failed: {error}"))?;
    Ok(ClipBaseline {
        rect,
        was_full_desktop: rect_eq(rect, virtual_desktop_rect()),
    })
}

fn restore_clip_baseline(baseline: Option<ClipBaseline>) {
    let Some(baseline) = baseline else {
        return;
    };
    let result = unsafe {
        if baseline.was_full_desktop {
            ClipCursor(None)
        } else {
            ClipCursor(Some(&baseline.rect))
        }
    };
    if let Err(error) = result {
        eprintln!("warning: ClipCursor restore failed: {error}");
    }
}

fn lock_coordination() -> std::sync::MutexGuard<'static, InputCoordination> {
    INPUT_COORDINATION
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn register_raw_mouse(
    window: HWND,
    flags: windows::Win32::UI::Input::RAWINPUTDEVICE_FLAGS,
) -> Result<(), String> {
    let mouse = RAWINPUTDEVICE {
        usUsagePage: 0x01,
        usUsage: 0x02,
        dwFlags: flags,
        hwndTarget: window,
    };
    unsafe { RegisterRawInputDevices(&[mouse], size_of::<RAWINPUTDEVICE>() as u32) }
        .map_err(|error| format!("RegisterRawInputDevices(mouse) failed: {error}"))
}

fn unregister_raw_mouse() -> Result<(), String> {
    register_raw_mouse(HWND::default(), RIDEV_REMOVE)
}

fn post_release(window: HWND, reason: isize) {
    unsafe {
        let _ = PostMessageW(Some(window), WM_RELEASE_CAPTURE, WPARAM(0), LPARAM(reason));
    }
}

fn source_point(state: &InputMapper) -> POINT {
    let point = state
        .transform
        .map_target_point(PixelPoint {
            x: state.cursor.x,
            y: state.cursor.y,
        })
        .expect("captured cursor is clamped inside the target transform");
    POINT {
        x: point.x,
        y: point.y,
    }
}

fn input_tag(nonce: usize) -> usize {
    (nonce & !INPUT_TAG_MASK) | INPUT_TAG_SIGNATURE
}

fn is_managed_input_tag(tag: usize) -> bool {
    tag & INPUT_TAG_MASK == INPUT_TAG_SIGNATURE
}

fn window_key(window: HWND) -> usize {
    window.0 as usize
}

fn window_from_key(window: usize) -> HWND {
    HWND(window as *mut _)
}

fn virtual_desktop_rect() -> RECT {
    let left = unsafe { GetSystemMetrics(SM_XVIRTUALSCREEN) };
    let top = unsafe { GetSystemMetrics(SM_YVIRTUALSCREEN) };
    RECT {
        left,
        top,
        right: left + unsafe { GetSystemMetrics(SM_CXVIRTUALSCREEN) }.max(1),
        bottom: top + unsafe { GetSystemMetrics(SM_CYVIRTUALSCREEN) }.max(1),
    }
}

fn normalize(value: i32, extent: i32) -> i32 {
    ((value as i64 * 65_535) / extent.saturating_sub(1).max(1) as i64).clamp(0, 65_535) as i32
}

fn rect_extent(start: i32, end: i32) -> Result<u32, String> {
    u32::try_from(end.saturating_sub(start))
        .ok()
        .filter(|extent| *extent > 0)
        .ok_or_else(|| format!("invalid rectangle extent {start}..{end}"))
}

fn x_button_bit(data: u32) -> u8 {
    match data {
        1 => BUTTON_X1,
        2 => BUTTON_X2,
        _ => 0,
    }
}

fn rect_eq(left: RECT, right: RECT) -> bool {
    left.left == right.left
        && left.top == right.top
        && left.right == right.right
        && left.bottom == right.bottom
}

fn low_word_signed(value: isize) -> i32 {
    value as u16 as i16 as i32
}

fn high_word_signed(value: isize) -> i32 {
    (value as usize >> 16) as u16 as i16 as i32
}

#[cfg(test)]
mod tests {
    use super::{
        ActiveEndpoint, INPUT_TAG_SIGNATURE, NO_INPUT_ENDPOINT, input_tag, is_managed_input_tag,
    };

    #[test]
    fn stale_endpoint_cannot_release_new_owner() {
        let endpoints = ActiveEndpoint::new();
        assert_eq!(endpoints.replace(11), NO_INPUT_ENDPOINT);
        assert_eq!(endpoints.replace(22), 11);
        assert!(!endpoints.release(11));
        assert!(endpoints.owns(22));
        assert!(endpoints.release(22));
        assert!(!endpoints.owns(22));
    }

    #[test]
    fn all_sbms_injection_tags_are_recognized() {
        let first = input_tag(1);
        let second = input_tag(usize::MAX);
        assert!(is_managed_input_tag(first));
        assert!(is_managed_input_tag(second));
        assert!(is_managed_input_tag(INPUT_TAG_SIGNATURE));
        assert!(!is_managed_input_tag(0));
    }
}
