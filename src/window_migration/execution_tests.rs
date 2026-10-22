use super::*;
use std::sync::atomic::AtomicUsize;
use windows::Win32::Foundation::{LRESULT, WPARAM};
use windows::Win32::UI::WindowsAndMessaging::{
    DefWindowProcW, DispatchMessageW, GWLP_USERDATA, GWLP_WNDPROC, GetMessageW, MSG, PostMessageW,
    SetWindowLongPtrW, ShowWindow, WM_APP, WM_CLOSE, WM_WINDOWPOSCHANGING, WS_OVERLAPPEDWINDOW,
};

#[derive(Default)]
struct ProbeState {
    block: AtomicBool,
    entered: AtomicBool,
    drained: AtomicBool,
    position_requests: AtomicUsize,
}

unsafe extern "system" fn probe_proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    let pointer = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *const ProbeState;
    if !pointer.is_null() {
        let state = unsafe { &*pointer };
        if message == WM_APP {
            state.entered.store(true, Ordering::SeqCst);
            while state.block.load(Ordering::SeqCst) {
                thread::sleep(Duration::from_millis(5));
            }
            return LRESULT(0);
        }
        if message == WM_APP + 1 {
            state.drained.store(true, Ordering::SeqCst);
            return LRESULT(0);
        }
        if message == WM_WINDOWPOSCHANGING {
            state.position_requests.fetch_add(1, Ordering::SeqCst);
        }
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}

struct Fixture {
    entry: WindowSnapshot,
    monitor: MONITORINFO,
    state: Arc<ProbeState>,
    owner: Option<JoinHandle<()>>,
}

impl Fixture {
    fn create() -> Self {
        let display = crate::display::active_displays()
            .unwrap()
            .into_iter()
            .find(|display| display.primary && !display.virtual_display)
            .unwrap();
        let monitor = default_monitor(display.rect).unwrap();
        let start = monitor.rcWork;
        let state = Arc::new(ProbeState::default());
        let owner_state = Arc::clone(&state);
        let (send, receive) = std::sync::mpsc::channel();
        let owner = thread::spawn(move || unsafe {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            let hwnd = CreateWindowExW(
                WS_EX_NOACTIVATE,
                w!("STATIC"),
                w!("SBMS execution test fixture"),
                WS_OVERLAPPEDWINDOW,
                start.left + 50,
                start.top + 50,
                640,
                480,
                None,
                None,
                None,
                None,
            )
            .unwrap();
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, Arc::as_ptr(&owner_state) as isize);
            SetWindowLongPtrW(hwnd, GWLP_WNDPROC, probe_proc as *const () as isize);
            let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            send.send(hwnd.0 as usize).unwrap();
            let mut message = MSG::default();
            while IsWindow(Some(hwnd)).as_bool() && GetMessageW(&mut message, None, 0, 0).0 > 0 {
                DispatchMessageW(&message);
            }
        });
        let hwnd = HWND(receive.recv().unwrap() as *mut _);
        let mut pid = 0;
        let thread_id = unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
        let entry = WindowSnapshot {
            hwnd,
            pid,
            thread_id,
            class_name: class_name(hwnd),
            tool_window: false,
            moved: true,
            layout: None,
            startup_layout: None,
        };
        wait_for_goal(
            &entry,
            WindowGoal::on_monitor(&entry, monitor.rcMonitor, WindowState::Normal).unwrap(),
            "prepare owned execution fixture",
        )
        .unwrap();
        state.position_requests.store(0, Ordering::SeqCst);
        Self {
            entry,
            monitor,
            state,
            owner: Some(owner),
        }
    }

    fn close(&mut self) {
        self.state.block.store(false, Ordering::SeqCst);
        if let Some(owner) = self.owner.take() {
            unsafe { PostMessageW(Some(self.entry.hwnd), WM_CLOSE, WPARAM(0), LPARAM(0)) }.unwrap();
            owner.join().unwrap();
        }
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        self.close();
    }
}

fn wait_flag(flag: &AtomicBool) {
    let deadline = Instant::now() + Duration::from_secs(3);
    while !flag.load(Ordering::SeqCst) {
        assert!(
            Instant::now() < deadline,
            "fixture message was not processed"
        );
        thread::sleep(Duration::from_millis(5));
    }
}

#[test]
#[ignore = "creates an owned native window and deliberately blocks only its GUI thread"]
fn native_hung_batch_rejects_before_enqueue_and_has_no_late_move() {
    let fixture = Fixture::create();
    let before = observe_window(&fixture.entry).unwrap().unwrap();
    fixture.state.block.store(true, Ordering::SeqCst);
    unsafe { PostMessageW(Some(fixture.entry.hwnd), WM_APP, WPARAM(0), LPARAM(0)) }.unwrap();
    wait_flag(&fixture.state.entered);
    let started = Instant::now();
    let result = restore_layout_batch(&[&fixture.entry], fixture.monitor.rcMonitor);
    let elapsed = started.elapsed();
    fixture.state.block.store(false, Ordering::SeqCst);
    unsafe { PostMessageW(Some(fixture.entry.hwnd), WM_APP + 1, WPARAM(0), LPARAM(0)) }.unwrap();
    wait_flag(&fixture.state.drained);
    assert!(result.unwrap_err().contains("no placement submitted"));
    assert!(
        elapsed < Duration::from_secs(2),
        "bounded preflight took {elapsed:?}"
    );
    let goal = WindowGoal {
        state: WindowState::Normal,
        outer: Some(before.outer),
        normal: Some(before.placement.rcNormalPosition),
        ..WindowGoal::default()
    };
    wait_for_goal(&fixture.entry, goal, "reject without a delayed move").unwrap();
    assert_eq!(fixture.state.position_requests.load(Ordering::SeqCst), 0);
    println!("hung preflight rejected in {elapsed:?}; no queued or late positioning request");
}

#[test]
#[ignore = "moves one owned native window to verify a single final geometry submission"]
fn native_normal_batch_submits_once_and_closed_window_is_skipped() {
    let mut fixture = Fixture::create();
    restore_layout_batch(&[&fixture.entry], fixture.monitor.rcMonitor).unwrap();
    assert_eq!(fixture.state.position_requests.load(Ordering::SeqCst), 1);
    restore_layout_batch(&[&fixture.entry], fixture.monitor.rcMonitor).unwrap();
    assert_eq!(fixture.state.position_requests.load(Ordering::SeqCst), 1);
    fixture.close();
    let requested = default_window_rect(fixture.monitor.rcWork);
    let goal = WindowGoal {
        state: WindowState::Normal,
        ..WindowGoal::default()
    };
    assert!(
        execute_layout(&fixture.entry, requested, &fixture.monitor, goal)
            .unwrap()
            .is_none()
    );
    println!("one placement, unchanged repeat zero writes, closed HWND skipped");
}

#[test]
#[ignore = "moves two owned native windows offscreen to simulate startup orphaning before begin"]
fn native_startup_rollback_restores_two_unmoved_orphans_once() {
    let fixtures = [Fixture::create(), Fixture::create()];
    let displays = crate::display::active_display_topology().unwrap();
    let target = displays
        .iter()
        .find(|display| display.primary && !display.virtual_display)
        .unwrap();
    let mut originals = Vec::new();
    let mut migrations = Vec::new();
    for (index, fixture) in fixtures.iter().enumerate() {
        let original = observe_window(&fixture.entry).unwrap().unwrap();
        let layout = WindowLayout::capture(&fixture.entry, original, &fixture.monitor).unwrap();
        originals.push(original);
        let snapshot = WindowSnapshot {
            hwnd: fixture.entry.hwnd,
            pid: fixture.entry.pid,
            thread_id: fixture.entry.thread_id,
            class_name: fixture.entry.class_name.clone(),
            tool_window: fixture.entry.tool_window,
            moved: false,
            layout: Some(layout),
            startup_layout: Some(layout),
        };
        // Model Windows relocating a captured window before begin gets to it.
        // No virtual device is created and no production move is marked complete.
        let width = original.outer.right - original.outer.left;
        let height = original.outer.bottom - original.outer.top;
        set_window_rect(&fixture.entry, -32000, -32000, width, height).unwrap();
        let offscreen = RECT {
            left: -32000,
            top: -32000,
            right: -32000 + width,
            bottom: -32000 + height,
        };
        let orphan = wait_for_goal(
            &fixture.entry,
            WindowGoal {
                state: WindowState::Restored,
                outer: Some(offscreen),
                ..WindowGoal::default()
            },
            "prepare orphan before begin",
        )
        .unwrap()
        .unwrap();
        assert!(orphan.monitor.0.is_null());
        assert!(
            unsafe { MonitorFromRect(&orphan.placement.rcNormalPosition, MONITOR_DEFAULTTONULL) }
                .0
                .is_null(),
            "fixture normal position must also be offscreen"
        );
        fixture.state.position_requests.store(0, Ordering::SeqCst);
        migrations.push((
            index as u32 + 1,
            target.id.clone(),
            WindowMigration {
                migration_id: next_migration_id(),
                entries: Arc::new(Mutex::new(vec![snapshot])),
                stop_scanner: Arc::new(AtomicBool::new(true)),
                scanner: None,
                target_rect: target.rect,
                source_id: Some("missing-test-source".into()),
            },
        ));
    }
    let mut plan = WindowRestorePlan::capture(migrations, RestoreMode::StartupRollback);
    plan.transfer(&displays);
    for fixture in &fixtures {
        assert_eq!(fixture.state.position_requests.load(Ordering::SeqCst), 1);
    }
    plan.finish(&displays).unwrap();
    for (fixture, original) in fixtures.iter().zip(originals) {
        let actual = observe_window(&fixture.entry).unwrap().unwrap();
        assert_eq!(actual.outer, original.outer);
        assert_eq!(
            actual.placement.rcNormalPosition,
            original.placement.rcNormalPosition
        );
        assert!(!actual.iconic && !actual.zoomed);
        assert_eq!(fixture.state.position_requests.load(Ordering::SeqCst), 1);
    }
    println!("two pre-begin unmoved orphan windows recovered once; finish added no writes");
}
