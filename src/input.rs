use std::cell::RefCell;
use std::mem::{MaybeUninit, size_of};
use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::time::Instant;

use windows::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, WPARAM};
use windows::Win32::Security::Cryptography::{BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptGenRandom};
use windows::Win32::System::SystemInformation::GetTickCount64;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetAsyncKeyState, INPUT, INPUT_0, INPUT_MOUSE, MOUSE_EVENT_FLAGS, MOUSEEVENTF_ABSOLUTE,
    MOUSEEVENTF_HWHEEL, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, MOUSEEVENTF_MIDDLEDOWN,
    MOUSEEVENTF_MIDDLEUP, MOUSEEVENTF_MOVE, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP,
    MOUSEEVENTF_VIRTUALDESK, MOUSEEVENTF_WHEEL, MOUSEEVENTF_XDOWN, MOUSEEVENTF_XUP, MOUSEINPUT,
    SendInput, VK_LBUTTON, VK_LSHIFT, VK_LWIN, VK_MBUTTON, VK_RBUTTON, VK_RSHIFT, VK_RWIN,
    VK_SNAPSHOT, VK_XBUTTON1, VK_XBUTTON2,
};
use windows::Win32::UI::Input::{
    GetRawInputData, HRAWINPUT, MOUSE_MOVE_ABSOLUTE, RAWINPUT, RAWINPUTDEVICE, RID_INPUT,
    RIDEV_INPUTSINK, RIDEV_REMOVE, RIM_TYPEMOUSE, RegisterRawInputDevices,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, ClipCursor, GA_ROOT, GetAncestor, GetClipCursor, GetCursorPos, GetMessageTime,
    GetSystemMetrics, HC_ACTION, HHOOK, KBDLLHOOKSTRUCT, KillTimer, LLMHF_INJECTED, MSLLHOOKSTRUCT,
    PostMessageW, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN,
    SetCursorPos, SetTimer, SetWindowsHookExW, UnhookWindowsHookEx, WH_KEYBOARD_LL, WH_MOUSE_LL,
    WM_APP, WM_DISPLAYCHANGE, WM_INPUT, WM_KEYDOWN, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MBUTTONDOWN,
    WM_MBUTTONUP, WM_MOUSEHWHEEL, WM_MOUSEMOVE, WM_MOUSEWHEEL, WM_RBUTTONDOWN, WM_RBUTTONUP,
    WM_SYSKEYDOWN, WM_TIMER, WM_XBUTTONDOWN, WM_XBUTTONUP, WindowFromPoint,
};

use crate::display::active_display_topology;
use crate::geometry::{CoordinateTransform, PixelPoint, PixelRect, Rotation};

mod boundary;
mod navigation;
mod screenshot;

const SCREENSHOT_TIMER_ID: usize = 0x5342_4d53;
const SCREENSHOT_POLL_MS: u32 = 50;

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
static INPUT_CAPTURE_GENERATION: AtomicUsize = AtomicUsize::new(0);
static BOUNDARY_WARP_DEPTH: AtomicUsize = AtomicUsize::new(0);
// Encoded as tick + 1 so a transfer at system tick zero is representable.
static SCREENSHOT_RETURN_TICK: AtomicU64 = AtomicU64::new(0);

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
    applied_clip: Option<RECT>,
    restoration_owner: Option<usize>,
    capture_source: Option<RECT>,
    boundary: boundary::BoundaryRegistry,
    screenshot_navigation: Option<navigation::Navigation>,
    screenshot_raw_owner: Option<usize>,
}

impl InputCoordination {
    const fn new() -> Self {
        Self {
            clip_baseline: None,
            applied_clip: None,
            restoration_owner: None,
            capture_source: None,
            boundary: boundary::BoundaryRegistry::new(),
            screenshot_navigation: None,
            screenshot_raw_owner: None,
        }
    }

    fn activation_allowed(&self) -> bool {
        self.restoration_owner.is_none()
    }

    fn observe_clip(&mut self, current: ClipBaseline) {
        if self
            .applied_clip
            .is_some_and(|ours| !rect_eq(ours, current.rect))
        {
            // Another application replaced our clip. Its current setting is
            // now authoritative; the old baseline no longer belongs to us.
            self.applied_clip = None;
            self.clip_baseline = None;
        }
    }

    fn native_clip_allowed(&self, current: ClipBaseline) -> bool {
        self.clip_baseline.unwrap_or(current).was_full_desktop
    }

    fn reserve_restoration(
        &mut self,
        window: usize,
        endpoint: usize,
        current_generation: usize,
        expected_generation: usize,
    ) -> bool {
        if !self.activation_allowed()
            || !screenshot_endpoint_available(endpoint, current_generation, expected_generation)
        {
            return false;
        }
        self.restoration_owner = Some(window);
        true
    }

    fn release_restoration(&mut self, window: usize) {
        if self.restoration_owner == Some(window) {
            self.restoration_owner = None;
        }
    }
}

struct ScreenshotRestorationLease {
    window: usize,
}

impl ScreenshotRestorationLease {
    fn acquire(window: HWND, capture_generation: usize) -> Option<Self> {
        let window = window_key(window);
        let mut coordination = lock_coordination();
        if coordination.reserve_restoration(
            window,
            ACTIVE_INPUT_ENDPOINT.window.load(Ordering::Acquire),
            INPUT_CAPTURE_GENERATION.load(Ordering::Acquire),
            capture_generation,
        ) {
            Some(Self { window })
        } else {
            None
        }
    }

    fn activate(&self, source: RECT) -> Result<(), String> {
        register_raw_mouse(window_from_key(self.window), RIDEV_INPUTSINK)?;
        let clip_result = {
            let mut coordination = lock_coordination();
            debug_assert_eq!(coordination.restoration_owner, Some(self.window));
            debug_assert_eq!(
                ACTIVE_INPUT_ENDPOINT.window.load(Ordering::Acquire),
                NO_INPUT_ENDPOINT
            );
            coordination.capture_source = Some(source);
            ACTIVE_INPUT_ENDPOINT.replace(self.window);
            INPUT_CAPTURE_GENERATION.fetch_add(1, Ordering::AcqRel);
            apply_clip_state_locked(&mut coordination)
        };
        if let Err(error) = clip_result {
            self.rollback_activation();
            return Err(error);
        }
        Ok(())
    }

    fn rollback_activation(&self) {
        let clip_result = {
            let mut coordination = lock_coordination();
            if !ACTIVE_INPUT_ENDPOINT.release(self.window) {
                return;
            }
            coordination.capture_source = None;
            apply_clip_state_locked(&mut coordination)
        };
        if let Err(error) = unregister_raw_mouse() {
            eprintln!("warning: screenshot raw input cleanup failed: {error}");
        }
        if let Err(error) = clip_result {
            eprintln!("warning: screenshot clip rollback failed: {error}");
        }
    }
}

impl Drop for ScreenshotRestorationLease {
    fn drop(&mut self) {
        lock_coordination().release_restoration(self.window);
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
    source_to_target: CoordinateTransform,
    screenshot_restore: Option<ScreenshotRestore>,
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
        let source_to_target = CoordinateTransform::stretch(
            transform.source,
            PixelRect {
                left: target.left,
                top: target.top,
                ..transform.target
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
                source_to_target,
                screenshot_restore: None,
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
        register_input_guard(window, target, source);
        Ok(Self)
    }
}

impl Drop for InputGuard {
    fn drop(&mut self) {
        cleanup();
    }
}

pub fn handle_message(message: u32, wparam: WPARAM, lparam: LPARAM) -> Option<LRESULT> {
    match message {
        WM_LBUTTONDOWN => {
            // The overlay may not yet cover the mirror during its launch.
            // Do not reacquire ordinary mapping capture in that interval.
            if lock_coordination().boundary.has_pause() {
                return Some(LRESULT(0));
            }
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
            cancel_screenshot_restore();
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
        WM_TIMER if wparam.0 == SCREENSHOT_TIMER_ID => {
            poll_screenshot_restore();
            Some(LRESULT(0))
        }
        WM_DISPLAYCHANGE => {
            handle_display_change();
            cancel_screenshot_restore();
            Some(LRESULT(0))
        }
        _ => None,
    }
}

fn capture_at(x: i32, y: i32) -> Result<(), String> {
    cancel_screenshot_restore();
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
    // Registration changes do not remove WM_INPUT already in this window's
    // queue. Those deltas are already represented by the return-position sample.
    if stale_screenshot_motion(unsafe { GetMessageTime() } as u32) {
        return Ok(());
    }
    if mouse.usFlags.0 & MOUSE_MOVE_ABSOLUTE.0 == 0
        && unsafe { mouse.Anonymous.Anonymous.usButtonFlags } == 0
    {
        handle_screenshot_raw_motion(
            POINT {
                x: mouse.lLastX,
                y: mouse.lLastY,
            },
            unsafe { GetMessageTime() } as u32,
        );
    }
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
    let event = unsafe { &*(lparam.0 as *const MSLLHOOKSTRUCT) };
    let stale_return_move = wparam.0 as u32 == WM_MOUSEMOVE
        && BOUNDARY_WARP_DEPTH.load(Ordering::Acquire) == 0
        && stale_screenshot_motion(event.time);
    if event.flags & LLMHF_INJECTED != 0 && is_managed_input_tag(event.dwExtraInfo) {
        // SendInput inserts events into the input stream; a queued move may
        // reach this hook after a screenshot has released capture and moved
        // the cursor to the physical display. Do not let that old move undo
        // the handoff. Button-up events must still clear any held buttons.
        let stale = INPUT_STATE.with(|cell| {
            cell.borrow().as_ref().is_some_and(|state| {
                (event.dwExtraInfo == state.tag && stale_return_move)
                    || stale_managed_move(
                        wparam.0 as u32,
                        event.dwExtraInfo,
                        state.tag,
                        ACTIVE_INPUT_ENDPOINT.window.load(Ordering::Acquire),
                    )
            })
        });
        if stale {
            return LRESULT(1);
        }
        // Our own injected events must not flush another mapped move: doing
        // so adds work (and potentially another injection) to the hook chain.
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }
    // Preserve buttons and foreign injected input. Only old movement from our
    // physical workspace is invalid after returning to logical coordinates.
    if stale_return_move && event.flags & LLMHF_INJECTED == 0 {
        return LRESULT(1);
    }
    flush_movement();
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
        if captured {
            post_release(window, RELEASE_EXTERNAL_INJECTION);
        }
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }
    if !captured {
        return native_boundary_hook(code, wparam, lparam, event);
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
    if matches!(message, WM_KEYDOWN | WM_SYSKEYDOWN)
        && (event.vkCode == VK_SNAPSHOT.0 as u32 || is_snipping_shortcut(event.vkCode))
    {
        // Complete the handoff before the shortcut can open the capture overlay.
        handoff_screenshot_cursor(event.time);
    }
    unsafe { CallNextHookEx(None, code, wparam, lparam) }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ScreenshotHandoff {
    ReleaseCapture,
    MoveCursor(PixelPoint),
    Ignore,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ScreenshotMode {
    Native,
    Captured,
    /// Boundary-only session: the overlay may drag across mapped targets while
    /// the cursor is not on a source; no cursor handoff and no cursor restore.
    PauseOnly,
}

impl ScreenshotMode {
    fn with_handoff(self, incoming: Self) -> Self {
        match (self, incoming) {
            (Self::Captured, _) | (_, Self::Captured) => Self::Captured,
            (Self::Native, _) | (_, Self::Native) => Self::Native,
            _ => Self::PauseOnly,
        }
    }
}

struct ScreenshotRestore {
    session: screenshot::Session,
    mode: ScreenshotMode,
    capture_generation: usize,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ScreenshotRestoreDecision {
    Wait,
    Restore,
    Cancel,
}

fn screenshot_handoff(
    captured: bool,
    owns_endpoint: bool,
    source_to_target: CoordinateTransform,
    cursor: Option<PixelPoint>,
) -> ScreenshotHandoff {
    if captured {
        return if owns_endpoint {
            ScreenshotHandoff::ReleaseCapture
        } else {
            ScreenshotHandoff::Ignore
        };
    }
    cursor
        .and_then(|point| source_to_target.map_target_point(point))
        .map(ScreenshotHandoff::MoveCursor)
        .unwrap_or(ScreenshotHandoff::Ignore)
}

fn handoff_screenshot_cursor(time: u32) {
    let snapshot = INPUT_STATE.with(|cell| {
        cell.borrow().as_ref().map(|state| {
            (
                state.captured,
                ACTIVE_INPUT_ENDPOINT.owns(window_key(state.window)),
                state.source_to_target,
            )
        })
    });
    let Some((captured, owns_endpoint, source_to_target)) = snapshot else {
        return;
    };
    let cursor = if captured {
        None
    } else {
        let mut point = POINT::default();
        if let Err(error) = unsafe { GetCursorPos(&mut point) } {
            eprintln!("warning: screenshot cursor position failed: {error}");
            return;
        }
        Some(PixelPoint {
            x: point.x,
            y: point.y,
        })
    };
    let handoff = screenshot_handoff(captured, owns_endpoint, source_to_target, cursor);
    if handoff == ScreenshotHandoff::Ignore {
        // All guards share one navigation session, including screenshots
        // started on an ordinary display and later moved onto a mapped output.
        if arm_screenshot_restore(ScreenshotMode::PauseOnly) {
            start_screenshot_navigation(time);
        }
        return;
    }
    let mode = if captured {
        ScreenshotMode::Captured
    } else {
        ScreenshotMode::Native
    };
    if !arm_screenshot_restore(mode) {
        return;
    }
    // Win32 input calls can re-enter our hooks; keep the RefCell unborrowed.
    let moved = match handoff {
        ScreenshotHandoff::ReleaseCapture => release_capture(),
        ScreenshotHandoff::MoveCursor(point) => set_cursor_position(POINT {
            x: point.x,
            y: point.y,
        }),
        ScreenshotHandoff::Ignore => false,
    };
    if !moved {
        cancel_screenshot_restore();
    } else {
        start_screenshot_navigation(time);
    }
}

fn arm_screenshot_restore(mode: ScreenshotMode) -> bool {
    let snapshot = INPUT_STATE.with(|cell| {
        let mut state = cell.borrow_mut();
        let state = state.as_mut()?;
        if let Some(restore) = state.screenshot_restore.as_mut() {
            // A repeated shortcut belongs to the same temporary handoff.
            restore.session = screenshot::Session::new(Instant::now());
            restore.mode = restore.mode.with_handoff(mode);
            return Some((state.window, false));
        }
        Some((state.window, true))
    });
    let Some((window, needs_timer)) = snapshot else {
        return false;
    };
    if !needs_timer {
        return true;
    }
    let capture_generation = INPUT_CAPTURE_GENERATION.load(Ordering::Acquire);
    if unsafe { SetTimer(Some(window), SCREENSHOT_TIMER_ID, SCREENSHOT_POLL_MS, None) } == 0 {
        eprintln!(
            "warning: screenshot recovery timer failed: {}",
            windows::core::Error::from_thread()
        );
        return false;
    }
    // Pause the normal source-space boundary before handoff. Screenshot
    // navigation installs a physical-screen fence as soon as capture releases.
    // A live ScreenshotRestore owns exactly one pause.
    acquire_screenshot_pause();
    INPUT_STATE.with(|cell| {
        if let Some(state) = cell.borrow_mut().as_mut() {
            state.screenshot_restore = Some(ScreenshotRestore {
                session: screenshot::Session::new(Instant::now()),
                mode,
                capture_generation,
            });
        }
    });
    true
}

/// Take the running screenshot session (killing its timer) without ending the
/// global pause; callers must pair this with `end_screenshot_pause` after their
/// cursor work so the boundary resumes only once the cursor is back in place.
fn take_screenshot_restore() -> Option<ScreenshotRestore> {
    let taken = INPUT_STATE.with(|cell| {
        let mut state = cell.borrow_mut();
        let state = state.as_mut()?;
        let window = state.window;
        let restore = state.screenshot_restore.take();
        restore.map(|restore| (window, restore))
    });
    let (window, restore) = taken?;
    let _ = unsafe { KillTimer(Some(window), SCREENSHOT_TIMER_ID) };
    Some(restore)
}

fn cancel_screenshot_restore() {
    let restore = take_screenshot_restore();
    end_screenshot_pause(restore.is_some());
}

fn screenshot_restore_decision(
    progress: screenshot::Progress,
    overlay_visible: Option<bool>,
    endpoint_available: bool,
    cursor_in_target: bool,
    buttons_down: bool,
    mirror_receives_input: bool,
) -> ScreenshotRestoreDecision {
    if !endpoint_available {
        return ScreenshotRestoreDecision::Cancel;
    }
    if progress == screenshot::Progress::Waiting {
        return ScreenshotRestoreDecision::Wait;
    }
    if overlay_visible != Some(false) {
        return if progress == screenshot::Progress::TimedOut {
            ScreenshotRestoreDecision::Cancel
        } else {
            ScreenshotRestoreDecision::Wait
        };
    }
    if !cursor_in_target {
        return ScreenshotRestoreDecision::Cancel;
    }
    if buttons_down {
        return ScreenshotRestoreDecision::Wait;
    }
    if progress == screenshot::Progress::TimedOut && !mirror_receives_input {
        return ScreenshotRestoreDecision::Cancel;
    }
    ScreenshotRestoreDecision::Restore
}

fn screenshot_return_point(
    target: RECT,
    transform: CoordinateTransform,
    cursor: POINT,
) -> Option<(PixelPoint, PixelPoint)> {
    let local = PixelPoint {
        x: cursor.x.checked_sub(target.left)?,
        y: cursor.y.checked_sub(target.top)?,
    };
    transform
        .map_target_point(local)
        .map(|source| (local, source))
}

fn screenshot_endpoint_available(
    endpoint: usize,
    current_generation: usize,
    expected_generation: usize,
) -> bool {
    endpoint == NO_INPUT_ENDPOINT && current_generation == expected_generation
}

fn poll_screenshot_restore() {
    let snapshot = INPUT_STATE.with(|cell| {
        let state = cell.borrow();
        let state = state.as_ref()?;
        let restore = state.screenshot_restore.as_ref()?;
        Some((
            state.window,
            state.target,
            state.transform,
            restore.capture_generation,
        ))
    });
    let Some((window, target, transform, capture_generation)) = snapshot else {
        return;
    };
    let endpoint_available = || {
        screenshot_endpoint_available(
            ACTIVE_INPUT_ENDPOINT.window.load(Ordering::Acquire),
            INPUT_CAPTURE_GENERATION.load(Ordering::Acquire),
            capture_generation,
        )
    };
    if !endpoint_available() {
        cancel_screenshot_restore();
        return;
    }
    let overlay_visible = screenshot::overlay_visible();
    if overlay_visible.is_some() {
        // Reconcile missed/synthetic button releases off the hook path. Never
        // treat an inaccessible input desktop as an all-buttons-up signal.
        let held = physical_buttons_down();
        if let Some(navigation) = lock_coordination().screenshot_navigation.as_mut() {
            navigation.sync_buttons(held);
        }
    }
    let progress = INPUT_STATE.with(|cell| {
        cell.borrow_mut()
            .as_mut()?
            .screenshot_restore
            .as_mut()
            .map(|restore| restore.session.poll(Instant::now(), overlay_visible))
    });
    let Some(progress) = progress else {
        return;
    };
    if progress == screenshot::Progress::Waiting {
        return;
    }
    // A desktop switch can also make GetCursorPos temporarily unavailable.
    // Wait for a confirmed end before touching the input desktop's cursor.
    if overlay_visible != Some(false) {
        if progress == screenshot::Progress::TimedOut {
            cancel_screenshot_restore();
        }
        return;
    }
    let mut cursor = POINT::default();
    if let Err(error) = unsafe { GetCursorPos(&mut cursor) } {
        eprintln!("warning: screenshot return position failed: {error}");
        cancel_screenshot_restore();
        return;
    }
    let return_point = screenshot_return_point(target, transform, cursor);
    let buttons_down = [VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2]
        .iter()
        .any(|key| key_down(key.0 as i32) < 0);
    let mirror_receives_input = progress == screenshot::Progress::TimedOut
        && return_point.is_some()
        && unsafe { GetAncestor(WindowFromPoint(cursor), GA_ROOT) } == window;
    match screenshot_restore_decision(
        progress,
        overlay_visible,
        endpoint_available(),
        return_point.is_some(),
        buttons_down,
        mirror_receives_input,
    ) {
        ScreenshotRestoreDecision::Wait => {}
        ScreenshotRestoreDecision::Cancel => cancel_screenshot_restore(),
        ScreenshotRestoreDecision::Restore => {
            if let Some((local, source)) = return_point {
                restore_screenshot_cursor(local, source, cursor);
            }
        }
    }
}

fn restore_screenshot_cursor(local: PixelPoint, source: PixelPoint, physical: POINT) {
    let snapshot: Option<(HWND, RECT, ScreenshotMode, usize)> = INPUT_STATE.with(|cell| {
        let state = cell.borrow();
        let state = state.as_ref()?;
        let restore = state.screenshot_restore.as_ref()?;
        Some((
            state.window,
            state.source,
            restore.mode,
            restore.capture_generation,
        ))
    });
    let Some(snapshot) = snapshot else {
        return;
    };
    let restore = take_screenshot_restore();
    // A pause-only session never owns cursor placement: other guards must not
    // push the screenshot cursor anywhere.
    if snapshot.2 != ScreenshotMode::PauseOnly {
        run_screenshot_cursor_restore(local, source, physical, snapshot);
    }
    // Restore the source cursor first; only then does the boundary resume.
    end_screenshot_pause(restore.is_some());
}

fn run_screenshot_cursor_restore(
    local: PixelPoint,
    source: PixelPoint,
    physical: POINT,
    snapshot: (HWND, RECT, ScreenshotMode, usize),
) {
    let (window, source_rect, mode, capture_generation) = snapshot;
    let point = POINT {
        x: source.x,
        y: source.y,
    };
    let Some(lease) = ScreenshotRestorationLease::acquire(window, capture_generation) else {
        return;
    };
    let _warp = CursorWarpGuard::new();
    if mode == ScreenshotMode::Native {
        // The shared navigator restores the current logical destination after
        // the final guard finishes. Its physical clip is still active here.
        if lock_coordination().screenshot_navigation.is_none() {
            let prepared = prepare_screenshot_return(&mut lock_coordination(), point);
            if prepared.is_ok() {
                place_screenshot_return(point);
            }
            let _ = apply_clip_state_locked(&mut lock_coordination());
        }
        return;
    }
    // Keep the old position legal until the single return warp. Activating
    // capture first would constrain the physical cursor to a source edge.
    if let Err(error) = prepare_screenshot_return(&mut lock_coordination(), point) {
        eprintln!("warning: screenshot cursor recovery preparation failed: {error}");
        return;
    }
    if !place_screenshot_return(point) {
        let _ = apply_clip_state_locked(&mut lock_coordination());
        return;
    }
    if let Err(error) = lease.activate(source_rect) {
        eprintln!("warning: screenshot input recovery failed: {error}");
        let _ = apply_clip_state_locked(&mut lock_coordination());
        set_cursor_position(physical);
        return;
    }
    INPUT_STATE.with(|cell| {
        if let Some(state) = cell.borrow_mut().as_mut() {
            state.cursor = POINT {
                x: local.x,
                y: local.y,
            };
            state.move_pending = false;
            state.pressed = 0;
        }
    });
    // Placement is synchronous above. Do not enqueue a SendInput return move
    // that could overtake raw input received after capture has resumed.
    INPUT_STATE.with(|cell| {
        if let Some(state) = cell.borrow_mut().as_mut()
            && ACTIVE_INPUT_ENDPOINT.owns(window_key(window))
        {
            state.captured = true;
        }
    });
    mark_screenshot_return();
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

fn release_capture() -> bool {
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
            // Never land the released cursor inside a mapped target display;
            // with the boundary paused (screenshot handoff) the raw mirror
            // position is kept.
            return set_cursor_position(boundary_adjusted_warp(target));
        }
    }
    false
}

fn set_cursor_position(point: POINT) -> bool {
    if let Err(error) = unsafe { SetCursorPos(point.x, point.y) } {
        eprintln!("warning: cursor handoff failed: {error}");
        return false;
    }
    true
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
    cancel_screenshot_restore();
    release_capture();
    let hooks = INPUT_STATE.with(|cell| {
        cell.borrow_mut()
            .take()
            .map(|state| (state.window, state.mouse_hook, state.keyboard_hook))
    });
    if let Some((window, mouse, keyboard)) = hooks {
        unregister_input_guard(window);
        unsafe {
            let _ = UnhookWindowsHookEx(mouse);
            let _ = UnhookWindowsHookEx(keyboard);
        }
    }
}

fn activate_endpoint(window: HWND, source: RECT) -> Result<usize, String> {
    let mut coordination = lock_coordination();
    if !coordination.activation_allowed() {
        return Err("mouse capture is busy restoring the screenshot cursor".into());
    }
    activate_endpoint_locked(&mut coordination, window_key(window), source)
}

fn activate_endpoint_locked(
    coordination: &mut InputCoordination,
    window: usize,
    source: RECT,
) -> Result<usize, String> {
    let current = ACTIVE_INPUT_ENDPOINT.window.load(Ordering::Acquire);
    if current == window {
        coordination.capture_source = Some(source);
        apply_clip_state_locked(coordination)?;
        INPUT_CAPTURE_GENERATION.fetch_add(1, Ordering::AcqRel);
        return Ok(current);
    }

    let previous_source = coordination.capture_source;
    let previous = ACTIVE_INPUT_ENDPOINT.replace(window);
    debug_assert_eq!(previous, current);
    coordination.capture_source = Some(source);
    if let Err(error) = register_raw_mouse(window_from_key(window), RIDEV_INPUTSINK) {
        let _ = ACTIVE_INPUT_ENDPOINT.restore(window, previous);
        coordination.capture_source = previous_source;
        return Err(error);
    }
    if let Err(error) = apply_clip_state_locked(coordination) {
        let registration = if previous == NO_INPUT_ENDPOINT {
            unregister_raw_mouse()
        } else {
            register_raw_mouse(window_from_key(previous), RIDEV_INPUTSINK)
        };
        let _ = ACTIVE_INPUT_ENDPOINT.restore(window, previous);
        coordination.capture_source = previous_source;
        let rollback = registration
            .err()
            .map(|rollback| format!("raw input endpoint rollback failed: {rollback}"))
            .or_else(|| apply_clip_state_locked(coordination).err());
        return Err(match rollback {
            Some(rollback) => format!("{error}; {rollback}"),
            None => error,
        });
    }
    INPUT_CAPTURE_GENERATION.fetch_add(1, Ordering::AcqRel);
    Ok(previous)
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
    // Release only the capture clip: an active native boundary (or the external
    // baseline when none is active) must survive capture teardown.
    coordination.capture_source = None;
    if let Err(error) = apply_clip_state_locked(&mut coordination) {
        eprintln!("warning: cursor clip restore failed: {error}");
    }
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

fn restore_clip_baseline(baseline: ClipBaseline) -> Result<(), String> {
    let result = unsafe {
        if baseline.was_full_desktop {
            ClipCursor(None)
        } else {
            ClipCursor(Some(&baseline.rect))
        }
    };
    result.map_err(|error| format!("ClipCursor restore failed: {error}"))
}

/// Temporarily widen only a clip we still own. Narrowing to the destination
/// workspace happens after placement, so Windows never clamps to an unrelated
/// edge in the middle of the return. An external clip remains authoritative.
fn prepare_screenshot_return(
    coordination: &mut InputCoordination,
    destination: POINT,
) -> Result<(), String> {
    let current = read_clip_baseline()?;
    coordination.observe_clip(current);
    if coordination.applied_clip.is_none() {
        return if current.was_full_desktop {
            Ok(())
        } else {
            Err("cursor clip is controlled by another application".into())
        };
    }
    let rect = screenshot::return_clip(current.rect, destination)
        .ok_or_else(|| "screenshot return point is out of range".to_string())?;
    if !rect_eq(current.rect, rect) {
        unsafe { ClipCursor(Some(&rect)) }
            .map_err(|error| format!("ClipCursor return preparation failed: {error}"))?;
        coordination.applied_clip = Some(rect);
    }
    Ok(())
}

fn mark_screenshot_return() {
    SCREENSHOT_RETURN_TICK.fetch_max(unsafe { GetTickCount64() } + 1, Ordering::AcqRel);
}

fn place_screenshot_return(point: POINT) -> bool {
    mark_screenshot_return();
    let placed = set_cursor_position(point);
    mark_screenshot_return();
    placed
}

fn stale_screenshot_motion(time: u32) -> bool {
    let tick = SCREENSHOT_RETURN_TICK.load(Ordering::Acquire);
    tick != 0 && screenshot::stale_return_motion(time, tick, unsafe { GetTickCount64() })
}

/// The single `ClipCursor` authority. Priority: the captured source clip, then
/// the native boundary while no screenshot pause is active. The external
/// baseline is preserved exactly once across capture/boundary swaps and is
/// restored only while the current clip still matches our last write. Native
/// mapping yields to another application's clip; explicit capture may claim
/// it temporarily. Hooks use try_lock and explicit cursor warps stay outside
/// the coordination lock.
fn apply_clip_state_locked(coordination: &mut InputCoordination) -> Result<(), String> {
    let current = read_clip_baseline()?;
    coordination.observe_clip(current);
    let native_allowed = coordination.native_clip_allowed(current);
    let screenshot_rect = native_allowed
        .then(|| {
            coordination
                .screenshot_navigation
                .as_ref()
                .map(navigation::Navigation::clip)
        })
        .flatten();
    let boundary_rect = native_allowed
        .then(|| {
            coordination
                .boundary
                .snapshot()
                .map(|boundary| boundary.clip_rect())
        })
        .flatten();
    match boundary::clip_action(
        coordination.capture_source.or(screenshot_rect),
        coordination.boundary.has_pause(),
        boundary_rect,
        coordination.clip_baseline.is_some(),
    ) {
        boundary::ClipAction::SaveBaselineThenSet(rect) => {
            unsafe { ClipCursor(Some(&rect)) }
                .map_err(|error| format!("ClipCursor failed: {error}"))?;
            coordination.clip_baseline = Some(current);
            coordination.applied_clip = Some(rect);
            Ok(())
        }
        boundary::ClipAction::Set(rect) => {
            if !rect_eq(current.rect, rect) {
                unsafe { ClipCursor(Some(&rect)) }
                    .map_err(|error| format!("ClipCursor failed: {error}"))?;
            }
            coordination.applied_clip = Some(rect);
            Ok(())
        }
        boundary::ClipAction::RestoreBaseline => {
            if let Some(baseline) = coordination.clip_baseline {
                restore_clip_baseline(baseline)?;
            }
            // Keep both fields on failure, so a later attempt never mistakes
            // our still-active restriction for an external baseline.
            coordination.clip_baseline = None;
            coordination.applied_clip = None;
            Ok(())
        }
        boundary::ClipAction::None => Ok(()),
    }
}

/// Clamp a discrete warp destination out of every mapped target display while
/// the boundary is live; paused or failed-open states keep the raw point.
fn boundary_adjusted_warp(point: POINT) -> POINT {
    let boundary = {
        let coordination = lock_coordination();
        if coordination.boundary.has_pause() {
            None
        } else {
            coordination.boundary.snapshot()
        }
    };
    match boundary {
        Some(boundary) => boundary.warp_to_allowed(point),
        None => point,
    }
}

/// `SetCursorPos` re-enters the low-level hooks; the bypass flag keeps our own
/// corrective warp from triggering another correction.
struct CursorWarpGuard;

impl CursorWarpGuard {
    fn new() -> Self {
        BOUNDARY_WARP_DEPTH.fetch_add(1, Ordering::AcqRel);
        Self
    }
}

impl Drop for CursorWarpGuard {
    fn drop(&mut self) {
        BOUNDARY_WARP_DEPTH.fetch_sub(1, Ordering::AcqRel);
    }
}

fn warp_cursor_with_bypass(point: POINT) {
    let _warp = CursorWarpGuard::new();
    set_cursor_position(point);
}

fn boundary_consumable_message(message: u32) -> bool {
    matches!(
        message,
        WM_MOUSEMOVE
            | WM_LBUTTONDOWN
            | WM_LBUTTONUP
            | WM_RBUTTONDOWN
            | WM_RBUTTONUP
            | WM_MBUTTONDOWN
            | WM_MBUTTONUP
            | WM_XBUTTONDOWN
            | WM_XBUTTONUP
            | WM_MOUSEWHEEL
            | WM_MOUSEHWHEEL
    )
}

/// Native-mode boundary fallback: consume movement/button deliveries aimed at
/// a mapped target display and correct the cursor back to the previous allowed
/// monitor edge immediately. Runs with no coordination lock held while the
/// cursor moves (try-lock snapshot only) and issues no topology queries.
fn native_boundary_hook(
    code: i32,
    wparam: WPARAM,
    lparam: LPARAM,
    event: &MSLLHOOKSTRUCT,
) -> LRESULT {
    if BOUNDARY_WARP_DEPTH.load(Ordering::Acquire) != 0 {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }
    if !boundary_consumable_message(wparam.0 as u32) {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }
    // An active capture endpoint or a global screenshot pause disables
    // corrections; only plain native mapping applies the boundary hook.
    if ACTIVE_INPUT_ENDPOINT.window.load(Ordering::Acquire) != NO_INPUT_ENDPOINT {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }
    if screenshot_navigation_hook(wparam.0 as u32, event) {
        return LRESULT(1);
    }
    let boundary = INPUT_COORDINATION.try_lock().ok().and_then(|coordination| {
        if coordination.boundary.has_pause() {
            None
        } else {
            coordination.boundary.snapshot()
        }
    });
    let Some(boundary) = boundary else {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    };
    if !boundary.is_forbidden(event.pt) {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }
    // Forbidden delivery: consume it and correct the position from this
    // callback. In a ClipOnly layout this path should normally never run (the
    // clip already blocks the move); it is the cheap soft fallback for when
    // another application has cleared or replaced the global clip. Win32 gives
    // no frame-level guarantee about callback positioning.
    let mut previous = POINT::default();
    if unsafe { GetCursorPos(&mut previous) }.is_err() {
        previous = event.pt;
    }
    let corrected = boundary.correct_movement(event.pt, Some(previous));
    warp_cursor_with_bypass(corrected);
    LRESULT(1)
}

/// Register one active input guard's rectangles globally and refresh the
/// logical monitor layout off the hook path.
fn register_input_guard(window: HWND, target: RECT, source: RECT) {
    refresh_boundary_layout(Some((
        window_key(window),
        boundary::GuardRects { target, source },
    )));
}

fn unregister_input_guard(window: HWND) {
    let mut coordination = lock_coordination();
    clear_screenshot_navigation(&mut coordination);
    coordination.boundary.unregister(window_key(window));
    if let Err(error) = apply_clip_state_locked(&mut coordination) {
        eprintln!("warning: cursor boundary release failed: {error}");
    }
}

fn monitor_layout() -> Vec<RECT> {
    active_display_topology()
        .map(|displays| {
            displays
                .into_iter()
                .map(|display| display.rect)
                .collect::<Vec<_>>()
        })
        .unwrap_or_default()
}

fn refresh_boundary_layout(register: Option<(usize, boundary::GuardRects)>) {
    let layout = monitor_layout();
    let mut coordination = lock_coordination();
    clear_screenshot_navigation(&mut coordination);
    if layout.is_empty() {
        coordination.boundary.invalidate();
    } else {
        coordination.boundary.set_layout(layout);
    }
    if let Some((window, rects)) = register {
        coordination.boundary.register(window, rects);
    }
    if let Err(error) = apply_clip_state_locked(&mut coordination) {
        eprintln!("warning: cursor boundary refresh failed: {error}");
    }
}

/// A display-layout mutation releases the boundary across all guards until a
/// fresh layout passes validation again; no stale corrective warp may survive.
fn handle_display_change() {
    {
        let mut coordination = lock_coordination();
        clear_screenshot_navigation(&mut coordination);
        coordination.boundary.invalidate();
        if let Err(error) = apply_clip_state_locked(&mut coordination) {
            eprintln!("warning: cursor boundary release failed: {error}");
        }
    }
    refresh_boundary_layout(None);
}

/// Pause the native boundary for one screenshot session owner (global across
/// mappers). Every acquisition is balanced by `end_screenshot_pause`.
fn acquire_screenshot_pause() {
    let mut coordination = lock_coordination();
    coordination.boundary.pause();
    if let Err(error) = apply_clip_state_locked(&mut coordination) {
        eprintln!("warning: screenshot boundary pause failed: {error}");
    }
}

fn end_screenshot_pause(held: bool) {
    if !held {
        return;
    }
    let _warp = CursorWarpGuard::new();
    let (boundary, return_point) = {
        let mut coordination = lock_coordination();
        coordination.boundary.resume();
        // Read the final physical position before restoring the normal clip:
        // ClipCursor itself can move a cursor outside its new rectangle.
        let mut return_point = if !coordination.boundary.has_pause() {
            stop_screenshot_raw_input(&mut coordination);
            coordination
                .screenshot_navigation
                .take()
                .and_then(|navigation| {
                    let mut cursor = POINT::default();
                    (ACTIVE_INPUT_ENDPOINT.window.load(Ordering::Acquire) == NO_INPUT_ENDPOINT
                        && unsafe { GetCursorPos(&mut cursor) }.is_ok())
                    .then(|| navigation.return_point(cursor))
                })
        } else {
            None
        };
        let boundary = if coordination.boundary.has_pause() {
            None
        } else {
            coordination.boundary.snapshot()
        };
        if let Some(point) = return_point
            && let Err(error) = prepare_screenshot_return(&mut coordination, point)
        {
            eprintln!("warning: screenshot return preparation failed: {error}");
            return_point = None;
        }
        // With a return pending, keep the widened clip until after placement.
        if return_point.is_none()
            && let Err(error) = apply_clip_state_locked(&mut coordination)
        {
            eprintln!("warning: screenshot boundary resume failed: {error}");
        }
        (boundary, return_point)
    };
    if let Some(point) = return_point {
        place_screenshot_return(point);
        if let Err(error) = apply_clip_state_locked(&mut lock_coordination()) {
            eprintln!("warning: screenshot boundary resume failed: {error}");
        }
    }
    // The boundary resumes after the cursor is back in place; a cursor left on
    // a forbidden target (cancelled session) is corrected outside every lock.
    if let Some(boundary) = boundary {
        let mut point = POINT::default();
        if unsafe { GetCursorPos(&mut point) }.is_ok() && boundary.is_forbidden(point) {
            warp_cursor_with_bypass(boundary.warp_to_allowed(point));
        }
    }
}

fn physical_buttons_down() -> u8 {
    [
        (VK_LBUTTON, BUTTON_LEFT),
        (VK_RBUTTON, BUTTON_RIGHT),
        (VK_MBUTTON, BUTTON_MIDDLE),
        (VK_XBUTTON1, BUTTON_X1),
        (VK_XBUTTON2, BUTTON_X2),
    ]
    .into_iter()
    .fold(0, |bits, (key, bit)| {
        bits | if key_down(key.0 as i32) < 0 { bit } else { 0 }
    })
}

fn start_screenshot_navigation(time: u32) {
    if ACTIVE_INPUT_ENDPOINT.window.load(Ordering::Acquire) != NO_INPUT_ENDPOINT {
        return;
    }
    let mut cursor = POINT::default();
    if unsafe { GetCursorPos(&mut cursor) }.is_err() {
        return;
    }
    let held = physical_buttons_down();
    let Some(window) = INPUT_STATE.with(|cell| cell.borrow().as_ref().map(|state| state.window))
    else {
        return;
    };
    let _warp = CursorWarpGuard::new();
    let destination = {
        let mut coordination = lock_coordination();
        if !coordination.boundary.has_pause() || coordination.screenshot_navigation.is_some() {
            return;
        }
        let Some(boundary) = coordination.boundary.snapshot() else {
            return;
        };
        let Some(mut navigation) =
            navigation::Navigation::new(boundary.screenshot_screens(), cursor, held)
        else {
            return;
        };
        let destination = navigation.handoff(cursor);
        navigation.warped_at(time);
        coordination.screenshot_navigation = Some(navigation);
        if let Err(error) = apply_clip_state_locked(&mut coordination) {
            eprintln!("warning: screenshot navigation clip failed: {error}");
            clear_screenshot_navigation(&mut coordination);
            let _ = apply_clip_state_locked(&mut coordination);
            return;
        }
        // Honour a foreign clip instead of fighting it with corrective warps.
        if coordination.applied_clip
            != coordination
                .screenshot_navigation
                .as_ref()
                .map(navigation::Navigation::clip)
        {
            clear_screenshot_navigation(&mut coordination);
            return;
        }
        match register_raw_mouse(window, RIDEV_INPUTSINK) {
            Ok(()) => coordination.screenshot_raw_owner = Some(window_key(window)),
            Err(error) => {
                eprintln!("warning: screenshot edge direction input unavailable: {error}")
            }
        }
        destination
    };
    set_cursor_position(destination);
}

/// Return true only when a native move has been replaced. Buttons always
/// remain native so the snipping tool owns the complete down/up sequence.
fn screenshot_navigation_hook(message: u32, event: &MSLLHOOKSTRUCT) -> bool {
    let destination = {
        let Ok(mut coordination) = INPUT_COORDINATION.try_lock() else {
            return false;
        };
        let applied_clip = coordination.applied_clip;
        let Some(navigation) = coordination.screenshot_navigation.as_mut() else {
            return false;
        };
        if applied_clip != Some(navigation.clip()) {
            return false;
        }
        match message {
            WM_LBUTTONDOWN => navigation.button(BUTTON_LEFT, true),
            WM_LBUTTONUP => navigation.button(BUTTON_LEFT, false),
            WM_RBUTTONDOWN => navigation.button(BUTTON_RIGHT, true),
            WM_RBUTTONUP => navigation.button(BUTTON_RIGHT, false),
            WM_MBUTTONDOWN => navigation.button(BUTTON_MIDDLE, true),
            WM_MBUTTONUP => navigation.button(BUTTON_MIDDLE, false),
            WM_XBUTTONDOWN | WM_XBUTTONUP => navigation.button(
                x_button_bit((event.mouseData >> 16) & 0xffff),
                message == WM_XBUTTONDOWN,
            ),
            WM_MOUSEMOVE => {}
            _ => return false,
        }
        if message != WM_MOUSEMOVE {
            return false;
        }
        if !navigation.accepts_motion(event.time) {
            return true;
        }
        let mut previous = POINT::default();
        if unsafe { GetCursorPos(&mut previous) }.is_err() {
            return false;
        }
        let old_clip = navigation.clip();
        let destination = navigation.move_to(event.pt, previous);
        if destination != event.pt {
            navigation.warped_at(event.time);
        }
        if navigation.clip() != old_clip
            && let Err(error) = apply_clip_state_locked(&mut coordination)
        {
            eprintln!("warning: screenshot screen transition failed: {error}");
            clear_screenshot_navigation(&mut coordination);
            let _ = apply_clip_state_locked(&mut coordination);
            return false;
        }
        destination
    };
    if destination == event.pt {
        return false;
    }
    let _warp = CursorWarpGuard::new();
    set_cursor_position(destination)
}

fn stop_screenshot_raw_input(coordination: &mut InputCoordination) {
    if coordination.screenshot_raw_owner.take().is_some()
        && ACTIVE_INPUT_ENDPOINT.window.load(Ordering::Acquire) == NO_INPUT_ENDPOINT
        && let Err(error) = unregister_raw_mouse()
    {
        eprintln!("warning: screenshot edge input cleanup failed: {error}");
    }
}

fn clear_screenshot_navigation(coordination: &mut InputCoordination) {
    coordination.screenshot_navigation = None;
    stop_screenshot_raw_input(coordination);
}

fn handle_screenshot_raw_motion(delta: POINT, time: u32) {
    if ACTIVE_INPUT_ENDPOINT.window.load(Ordering::Acquire) != NO_INPUT_ENDPOINT {
        return;
    }
    let owner =
        INPUT_STATE.with(|cell| cell.borrow().as_ref().map(|state| window_key(state.window)));
    let _warp = CursorWarpGuard::new();
    let destination = {
        let mut coordination = lock_coordination();
        if coordination.screenshot_raw_owner != owner || owner.is_none() {
            return;
        }
        let mut cursor = POINT::default();
        if unsafe { GetCursorPos(&mut cursor) }.is_err() {
            return;
        }
        let applied_clip = coordination.applied_clip;
        let Some(navigation) = coordination.screenshot_navigation.as_mut() else {
            return;
        };
        if applied_clip != Some(navigation.clip()) || !navigation.accepts_motion(time) {
            return;
        }
        let destination = navigation.raw_edge_move(delta, cursor);
        if destination == cursor {
            return;
        }
        navigation.warped_at(time);
        if let Err(error) = apply_clip_state_locked(&mut coordination) {
            eprintln!("warning: screenshot edge transition failed: {error}");
            clear_screenshot_navigation(&mut coordination);
            let _ = apply_clip_state_locked(&mut coordination);
            return;
        }
        destination
    };
    set_cursor_position(destination);
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

fn stale_managed_move(message: u32, tag: usize, owner_tag: usize, endpoint: usize) -> bool {
    // Only the injecting guard rejects its move. Other SBMS instances have
    // independent endpoint state and their tagged input must remain untouched.
    message == WM_MOUSEMOVE
        && tag == owner_tag
        && is_managed_input_tag(tag)
        && endpoint == NO_INPUT_ENDPOINT
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
    use super::boundary::{Boundary, GuardRects};
    use super::navigation::Navigation;
    use super::screenshot::Progress;
    use super::{
        ActiveEndpoint, ClipBaseline, CoordinateTransform, INPUT_TAG_SIGNATURE, InputCoordination,
        NO_INPUT_ENDPOINT, PixelPoint, PixelRect, Rotation, ScreenshotHandoff, ScreenshotMode,
        ScreenshotRestoreDecision, input_tag, is_managed_input_tag, rect_eq,
        screenshot_endpoint_available, screenshot_handoff, screenshot_restore_decision,
        screenshot_return_point, stale_managed_move,
    };
    use windows::Win32::Foundation::{POINT, RECT};
    use windows::Win32::UI::WindowsAndMessaging::{
        WM_LBUTTONUP, WM_MBUTTONUP, WM_MOUSEMOVE, WM_MOUSEWHEEL, WM_RBUTTONUP, WM_XBUTTONUP,
    };

    #[test]
    fn late_physical_move_after_return_cannot_push_cursor_to_virtual_edge() {
        let target = RECT {
            left: -200,
            top: -100,
            right: 0,
            bottom: 0,
        };
        let source = RECT {
            left: 20,
            top: 200,
            right: 380,
            bottom: 400,
        };
        let ordinary = RECT {
            left: 0,
            top: 0,
            right: 400,
            bottom: 200,
        };
        let boundary = Boundary::build(
            &[target, source, ordinary],
            &[GuardRects { target, source }],
        )
        .unwrap();
        let physical = POINT { x: -100, y: -50 };
        let nav = Navigation::new(boundary.screenshot_screens(), physical, 0).unwrap();
        let returned = nav.return_point(physical);
        // Before the fix, a delayed physical-space move goes through the
        // ordinary boundary hook after navigation is removed, landing here.
        let old_move = POINT { x: -99, y: -49 };
        assert!(boundary.is_forbidden(old_move));
        assert_eq!(
            boundary.correct_movement(old_move, Some(returned)),
            POINT { x: 20, y: 200 }
        );
        let mut cursor = returned;
        for (time, point) in [
            (999, old_move),
            (
                1_001,
                POINT {
                    x: returned.x + 1,
                    y: returned.y + 1,
                },
            ),
            (1_000, old_move),
        ] {
            if !super::screenshot::stale_return_motion(time, 1_001, 1_010) {
                cursor = if boundary.is_forbidden(point) {
                    boundary.correct_movement(point, Some(cursor))
                } else {
                    point
                };
            }
        }
        assert_eq!(
            cursor,
            POINT {
                x: returned.x + 1,
                y: returned.y + 1
            }
        );
    }

    #[test]
    fn queued_mapped_move_cannot_undo_screenshot_handoff() {
        let endpoint = ActiveEndpoint::new();
        let tag = input_tag(17);
        endpoint.replace(11);
        let rejects = |message, event_tag| {
            stale_managed_move(
                message,
                event_tag,
                tag,
                endpoint.window.load(std::sync::atomic::Ordering::Acquire),
            )
        };
        assert!(!rejects(WM_MOUSEMOVE, tag));
        assert!(endpoint.release(11));
        // The pending virtual move arrives after the physical cursor handoff.
        assert!(rejects(WM_MOUSEMOVE, tag));
        // Releasing synthetic buttons during handoff must remain possible.
        for message in [
            WM_LBUTTONUP,
            WM_RBUTTONUP,
            WM_MBUTTONUP,
            WM_XBUTTONUP,
            WM_MOUSEWHEEL,
        ] {
            assert!(!rejects(message, tag));
        }
        assert!(!rejects(WM_MOUSEMOVE, 0));
        assert!(!rejects(WM_MOUSEMOVE, input_tag(23)));
        // Screenshot restoration publishes the endpoint before injecting its
        // return move; every mapping hook must let that valid move through.
        endpoint.replace(22);
        assert!(!rejects(WM_MOUSEMOVE, tag));
        assert!(!rejects(WM_MOUSEMOVE, input_tag(23)));
        assert!(!endpoint.release(11));
        assert!(!rejects(WM_MOUSEMOVE, input_tag(23)));
    }

    #[test]
    fn repeated_screenshot_shortcut_keeps_the_strongest_return_mode() {
        let mode = ScreenshotMode::PauseOnly.with_handoff(ScreenshotMode::Native);
        assert_eq!(mode, ScreenshotMode::Native);
        assert_eq!(
            mode.with_handoff(ScreenshotMode::PauseOnly),
            ScreenshotMode::Native
        );
        let mode = mode.with_handoff(ScreenshotMode::Captured);
        assert_eq!(mode, ScreenshotMode::Captured);
        assert_eq!(
            mode.with_handoff(ScreenshotMode::Native),
            ScreenshotMode::Captured
        );
        assert_eq!(
            mode.with_handoff(ScreenshotMode::PauseOnly),
            ScreenshotMode::Captured
        );
    }

    #[test]
    fn foreign_clip_replacement_discards_our_stale_restore_authority() {
        let desktop = ClipBaseline {
            rect: RECT {
                left: -1920,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
            was_full_desktop: true,
        };
        let ours = RECT {
            left: 0,
            top: 0,
            right: 1920,
            bottom: 1080,
        };
        let foreign = ClipBaseline {
            rect: RECT {
                left: 200,
                top: 200,
                right: 900,
                bottom: 700,
            },
            was_full_desktop: false,
        };
        let mut coordination = InputCoordination::new();
        coordination.clip_baseline = Some(desktop);
        coordination.applied_clip = Some(ours);
        coordination.observe_clip(foreign);
        assert!(coordination.clip_baseline.is_none());
        assert!(coordination.applied_clip.is_none());
        assert!(!coordination.native_clip_allowed(foreign));
        // Native clipping can resume once that application releases its clip.
        assert!(coordination.native_clip_allowed(desktop));
    }

    #[test]
    fn unchanged_own_clip_keeps_original_baseline_for_restore_retry() {
        let original = ClipBaseline {
            rect: RECT {
                left: -1920,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
            was_full_desktop: true,
        };
        let current = ClipBaseline {
            rect: RECT {
                left: 0,
                top: 0,
                right: 1920,
                bottom: 1080,
            },
            was_full_desktop: false,
        };
        let mut coordination = InputCoordination::new();
        coordination.clip_baseline = Some(original);
        coordination.applied_clip = Some(current.rect);
        coordination.observe_clip(current);
        assert!(rect_eq(
            coordination.clip_baseline.unwrap().rect,
            original.rect
        ));
        assert!(coordination.native_clip_allowed(current));
        // A capture that borrowed another application's clip must return it,
        // rather than replacing it with the native boundary at capture end.
        coordination.clip_baseline = Some(current);
        assert!(!coordination.native_clip_allowed(current));
    }

    #[test]
    fn restoration_lease_blocks_activation_during_native_cursor_movement() {
        let mut coordination = InputCoordination::new();
        assert!(coordination.reserve_restoration(11, NO_INPUT_ENDPOINT, 7, 7));
        assert!(!coordination.activation_allowed());
        assert!(!coordination.reserve_restoration(22, NO_INPUT_ENDPOINT, 7, 7));
        coordination.release_restoration(22);
        assert!(!coordination.activation_allowed());
        coordination.release_restoration(11);
        assert!(coordination.activation_allowed());
    }

    #[test]
    fn restoration_lease_blocks_transfer_after_captured_endpoint_commit() {
        let mut coordination = InputCoordination::new();
        let endpoints = ActiveEndpoint::new();
        assert!(coordination.reserve_restoration(11, NO_INPUT_ENDPOINT, 7, 7));
        endpoints.replace(11);
        // The endpoint is published before MOVE; another capture must still
        // wait until that movement and the captured state have been committed.
        assert!(!coordination.activation_allowed());
        assert!(endpoints.owns(11));
        coordination.release_restoration(11);
        assert!(coordination.activation_allowed());
        assert_eq!(endpoints.replace(22), 11);
    }

    #[test]
    fn rejected_restoration_does_not_block_later_activation() {
        let mut coordination = InputCoordination::new();
        assert!(!coordination.reserve_restoration(11, 22, 7, 7));
        assert!(!coordination.reserve_restoration(11, NO_INPUT_ENDPOINT, 8, 7));
        assert!(coordination.activation_allowed());
    }

    #[test]
    fn screenshot_return_does_not_reclaim_after_another_capture_already_ended() {
        assert!(screenshot_endpoint_available(NO_INPUT_ENDPOINT, 7, 7));
        assert!(!screenshot_endpoint_available(42, 7, 7));
        assert!(!screenshot_endpoint_available(NO_INPUT_ENDPOINT, 8, 7));
    }

    #[test]
    fn screenshot_return_uses_current_physical_position_and_checks_its_monitor() {
        let target = RECT {
            left: 1920,
            top: 0,
            right: 3840,
            bottom: 1080,
        };
        let source_to_target = screenshot_transform();
        let transform = CoordinateTransform::stretch(
            PixelRect {
                left: 0,
                top: 0,
                ..source_to_target.source
            },
            source_to_target.target,
            Rotation::Deg0,
        )
        .unwrap();
        assert_eq!(
            screenshot_return_point(target, transform, POINT { x: 2880, y: 540 }),
            Some((
                PixelPoint { x: 960, y: 540 },
                PixelPoint { x: -1280, y: -720 }
            ))
        );
        assert_eq!(
            screenshot_return_point(target, transform, POINT { x: 3839, y: 1079 }),
            Some((PixelPoint { x: 1919, y: 1079 }, PixelPoint { x: -1, y: -1 }))
        );
        for cursor in [
            POINT { x: 1919, y: 540 },
            POINT { x: 3840, y: 540 },
            POINT { x: 2880, y: -1 },
            POINT { x: 2880, y: 1080 },
            POINT { x: -1280, y: -720 },
        ] {
            assert!(screenshot_return_point(target, transform, cursor).is_none());
        }
    }

    #[test]
    fn screenshot_return_waits_for_overlay_and_drag_to_end() {
        assert_eq!(
            screenshot_restore_decision(Progress::Waiting, Some(true), true, true, false, true),
            ScreenshotRestoreDecision::Wait
        );
        for overlay in [None, Some(true)] {
            assert_eq!(
                screenshot_restore_decision(Progress::Finished, overlay, true, true, false, true),
                ScreenshotRestoreDecision::Wait
            );
        }
        for progress in [Progress::Finished, Progress::TimedOut] {
            assert_eq!(
                screenshot_restore_decision(progress, Some(false), true, true, true, true),
                ScreenshotRestoreDecision::Wait
            );
            assert_eq!(
                screenshot_restore_decision(progress, Some(false), true, true, false, true),
                ScreenshotRestoreDecision::Restore
            );
        }
    }

    #[test]
    fn screenshot_return_cancels_after_other_input_or_leaving_the_target() {
        for progress in [Progress::Finished, Progress::TimedOut] {
            assert_eq!(
                screenshot_restore_decision(progress, Some(false), false, true, false, true),
                ScreenshotRestoreDecision::Cancel
            );
            assert_eq!(
                screenshot_restore_decision(progress, Some(false), true, false, false, true),
                ScreenshotRestoreDecision::Cancel
            );
        }
        for (overlay, receives_input) in [(None, true), (Some(true), true), (Some(false), false)] {
            assert_eq!(
                screenshot_restore_decision(
                    Progress::TimedOut,
                    overlay,
                    true,
                    true,
                    false,
                    receives_input
                ),
                ScreenshotRestoreDecision::Cancel
            );
        }
    }

    fn screenshot_transform() -> CoordinateTransform {
        CoordinateTransform::stretch(
            PixelRect {
                left: -2560,
                top: -1440,
                width: 2560,
                height: 1440,
            },
            PixelRect {
                left: 1920,
                top: 0,
                width: 1920,
                height: 1080,
            },
            Rotation::Deg0,
        )
        .unwrap()
    }

    #[test]
    fn screenshot_maps_live_uncaptured_cursor_to_its_physical_display() {
        let transform = screenshot_transform();
        for (cursor, expected) in [
            (
                PixelPoint { x: -1280, y: -720 },
                PixelPoint { x: 2879, y: 539 },
            ),
            (
                PixelPoint { x: -1920, y: -1080 },
                PixelPoint { x: 2399, y: 269 },
            ),
            (
                PixelPoint { x: -2560, y: -1440 },
                PixelPoint { x: 1920, y: 0 },
            ),
            (PixelPoint { x: -1, y: -1 }, PixelPoint { x: 3839, y: 1079 }),
        ] {
            assert_eq!(
                screenshot_handoff(false, false, transform, Some(cursor)),
                ScreenshotHandoff::MoveCursor(expected)
            );
        }
    }

    #[test]
    fn screenshot_leaves_physical_displays_and_other_sources_alone() {
        let transform = screenshot_transform();
        for cursor in [
            PixelPoint { x: 2500, y: 500 },
            PixelPoint { x: 0, y: -720 },
            PixelPoint { x: -1280, y: 0 },
            PixelPoint { x: -2561, y: -720 },
            PixelPoint { x: -1280, y: -1441 },
        ] {
            assert_eq!(
                screenshot_handoff(false, false, transform, Some(cursor)),
                ScreenshotHandoff::Ignore
            );
        }
        assert_eq!(
            screenshot_handoff(false, false, transform, None),
            ScreenshotHandoff::Ignore
        );
    }

    #[test]
    fn screenshot_releases_only_the_captured_endpoint_owner() {
        let transform = screenshot_transform();
        for cursor in [None, Some(PixelPoint { x: -1280, y: -720 })] {
            assert_eq!(
                screenshot_handoff(true, true, transform, cursor),
                ScreenshotHandoff::ReleaseCapture
            );
            assert_eq!(
                screenshot_handoff(true, false, transform, cursor),
                ScreenshotHandoff::Ignore
            );
        }
    }

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
