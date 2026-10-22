use super::*;

fn rect(left: i32, top: i32, width: i32, height: i32) -> RECT {
    RECT {
        left,
        top,
        right: left + width,
        bottom: top + height,
    }
}

fn monitor_info(bounds: RECT) -> MONITORINFO {
    MONITORINFO {
        rcMonitor: bounds,
        rcWork: bounds,
        ..Default::default()
    }
}

#[test]
fn default_batch_cascades_without_changing_single_window_layout() {
    let work = rect(-1880, 100, 1880, 1000);
    let centered = default_window_rect(work);
    assert_eq!(default_batch_rect(work, 0, 1), centered);
    let positions: Vec<_> = (0..3)
        .map(|index| default_batch_rect(work, index, 3))
        .collect();
    assert_eq!(positions[1], centered);
    for pair in positions.windows(2) {
        assert_eq!(pair[1].left - pair[0].left, 32);
        assert_eq!(pair[1].top - pair[0].top, 32);
        assert_eq!(pair[1].right - pair[1].left, centered.right - centered.left);
        assert_eq!(pair[1].bottom - pair[1].top, centered.bottom - centered.top);
    }
}

#[test]
fn default_batch_compresses_spacing_inside_small_work_area() {
    let work = rect(200, -400, 400, 240);
    let positions: Vec<_> = (0..20)
        .map(|index| default_batch_rect(work, index, 20))
        .collect();
    for position in &positions {
        assert!(position.left >= work.left && position.top >= work.top);
        assert!(position.right <= work.right && position.bottom <= work.bottom);
    }
    for pair in positions.windows(2) {
        assert!(pair[1].left > pair[0].left);
        assert!(pair[1].top > pair[0].top);
    }
    for index in [0, usize::MAX / 2, usize::MAX - 1] {
        let position = default_batch_rect(work, index, usize::MAX);
        assert!(position.left >= work.left && position.top >= work.top);
        assert!(position.right <= work.right && position.bottom <= work.bottom);
    }
}

fn observation() -> WindowObservation {
    WindowObservation {
        outer: rect(0, 0, 800, 600),
        placement: WINDOWPLACEMENT {
            rcNormalPosition: rect(0, 0, 800, 600),
            showCmd: SW_SHOWNOACTIVATE.0 as u32,
            ..Default::default()
        },
        monitor: HMONITOR(std::ptr::dangling_mut()),
        dpi: 192,
        iconic: false,
        zoomed: false,
    }
}

fn layout_test_window() -> WindowSnapshot {
    WindowSnapshot {
        hwnd: HWND::default(),
        pid: 0,
        thread_id: 0,
        class_name: String::new(),
        tool_window: false,
        moved: false,
        layout: None,
        startup_layout: None,
    }
}

#[test]
fn valid_layout_maps_proportionally_and_ignores_default_slots() {
    let mut entry = layout_test_window();
    entry.layout = Some(WindowLayout {
        normal: rect(-900, 250, 800, 500),
        bounds: rect(-1300, 0, 1600, 1000),
        state: WindowState::Normal,
        placement: WINDOWPLACEMENT::default(),
    });
    let target = rect(100, 200, 800, 500);
    assert_eq!(
        planned_window_rect(&entry, &monitor_info(target), (0, 1)),
        rect(300, 325, 400, 250)
    );
    assert_eq!(
        planned_window_rect(&entry, &monitor_info(target), (2, 5)),
        rect(300, 325, 400, 250)
    );
}

#[test]
fn valid_layout_ignores_transient_taskbar_size_but_default_uses_work_area() {
    let mut entry = layout_test_window();
    entry.layout = Some(WindowLayout {
        normal: rect(200, 200, 800, 500),
        bounds: rect(0, 0, 1600, 1000),
        state: WindowState::Normal,
        placement: WINDOWPLACEMENT::default(),
    });
    let mut monitor = monitor_info(rect(100, 200, 800, 500));
    let expected = planned_window_rect(&entry, &monitor, (0, 1));
    monitor.rcWork.bottom -= 40;
    assert_eq!(planned_window_rect(&entry, &monitor, (0, 1)), expected);
    entry.layout.as_mut().unwrap().normal.left = -32000;
    assert_eq!(
        planned_window_rect(&entry, &monitor, (0, 1)),
        default_window_rect(monitor.rcWork)
    );
}

#[test]
fn invalid_layout_alone_uses_default_cascade() {
    let mut entry = layout_test_window();
    let target = rect(100, 200, 1600, 1000);
    for normal in [
        rect(-32000, -32000, 1000, 700),
        rect(0, 0, 0, 30),
        rect(0, 0, 1800, 1200),
    ] {
        entry.layout = Some(WindowLayout {
            normal,
            bounds: rect(0, 0, 1600, 1000),
            state: WindowState::Minimized,
            placement: WINDOWPLACEMENT::default(),
        });
        assert_eq!(
            planned_window_rect(&entry, &monitor_info(target), (1, 3)),
            default_batch_rect(target, 1, 3)
        );
    }
    // Extreme invalid coordinates must be rejected before coordinate arithmetic.
    entry.layout.as_mut().unwrap().normal = RECT {
        left: i32::MIN,
        top: 0,
        right: i32::MAX,
        bottom: 100,
    };
    assert_eq!(
        planned_window_rect(&entry, &monitor_info(target), (0, 2)),
        default_batch_rect(target, 0, 2)
    );
}

#[test]
fn minimized_layout_uses_normal_placement_and_retains_restore_to_maximized() {
    let entry = layout_test_window();
    let mut actual = observation();
    actual.iconic = true;
    actual.outer = rect(-32000, -32000, 314, 50);
    actual.placement.rcNormalPosition = rect(200, 250, 600, 400);
    actual.placement.flags.0 = windows::Win32::UI::WindowsAndMessaging::WPF_RESTORETOMAXIMIZED.0;
    let monitor = MONITORINFO {
        rcMonitor: rect(0, 0, 1600, 1000),
        rcWork: rect(40, 50, 1560, 950),
        ..Default::default()
    };
    let saved = WindowLayout::capture(&entry, actual, &monitor).unwrap();
    assert_eq!(saved.normal, rect(240, 300, 600, 400));
    assert_eq!(saved.state, WindowState::Minimized);
    let goal = WindowGoal {
        state: WindowState::Minimized,
        restore_to_maximized: Some(true),
        ..Default::default()
    };
    assert!(goal.matches(&actual, None));
    actual.placement.flags.0 = 0;
    assert!(!goal.matches(&actual, None));
    assert_ne!(saved.placement.flags.0, 0);
    actual.iconic = false;
    actual.zoomed = true;
    let maximized = WindowLayout::capture(&entry, actual, &monitor).unwrap();
    assert_eq!(maximized.normal, saved.normal);
    assert_eq!(maximized.state, WindowState::Maximized);
}

#[test]
#[ignore = "moves a dedicated maximized native window between two physical displays"]
fn native_mapped_maximized_layout_round_trip() {
    native_mapped_state_round_trip(false);
}

#[test]
#[ignore = "moves a dedicated minimized native window between two physical displays"]
fn native_mapped_minimized_layout_round_trip() {
    native_mapped_state_round_trip(true);
}

fn prepare_minimized_fixture(entry: &WindowSnapshot) -> Result<(), String> {
    unsafe {
        ShowWindowAsync(
            entry.hwnd,
            windows::Win32::UI::WindowsAndMessaging::SW_SHOWMINNOACTIVE,
        )
    }
    .ok()
    .map_err(|error| error.to_string())?;
    wait_for_goal(
        entry,
        WindowGoal {
            state: WindowState::Minimized,
            ..Default::default()
        },
        "prepare minimized fixture",
    )?;
    Ok(())
}

fn native_mapped_state_round_trip(minimized: bool) {
    use std::sync::atomic::AtomicUsize;
    use windows::Win32::Foundation::{LRESULT, WPARAM};
    use windows::Win32::UI::WindowsAndMessaging::{
        DefWindowProcW, DispatchMessageW, GWLP_USERDATA, GWLP_WNDPROC, GetMessageW, MSG,
        PostMessageW, SW_MAXIMIZE, SW_SHOWMINNOACTIVE, SetWindowLongPtrW, WM_CLOSE, WM_SIZE,
        WS_OVERLAPPEDWINDOW,
    };
    unsafe extern "system" fn observe_size(
        hwnd: HWND,
        message: u32,
        w: WPARAM,
        l: LPARAM,
    ) -> LRESULT {
        if message == WM_SIZE && matches!(w.0, 0 | 2) {
            let counter = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *const AtomicUsize;
            if !counter.is_null() {
                unsafe { &*counter }.fetch_add(1, Ordering::SeqCst);
            }
        }
        unsafe { DefWindowProcW(hwnd, message, w, l) }
    }
    let previous =
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let displays = crate::display::active_displays().unwrap();
    let source = displays
        .iter()
        .find(|display| display.primary && !display.virtual_display)
        .unwrap();
    let target = displays
        .iter()
        .find(|display| !display.virtual_display && display.id != source.id)
        .unwrap();
    let source_info = default_monitor(source.rect).unwrap();
    let start = default_window_rect(source_info.rcWork);
    let unminimized_events = Box::new(AtomicUsize::new(0));
    let counter_address = (&*unminimized_events as *const AtomicUsize) as usize;
    let (sender, receiver) = std::sync::mpsc::channel();
    let owner = thread::spawn(move || unsafe {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        let hwnd = CreateWindowExW(
            WS_EX_NOACTIVATE,
            w!("STATIC"),
            w!("SBMS preserved layout test"),
            WS_OVERLAPPEDWINDOW,
            start.left,
            start.top,
            start.right - start.left,
            start.bottom - start.top,
            None,
            None,
            None,
            None,
        )
        .unwrap();
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, counter_address as isize);
        SetWindowLongPtrW(hwnd, GWLP_WNDPROC, observe_size as *const () as isize);
        sender.send(hwnd.0 as usize).unwrap();
        let mut message = MSG::default();
        while IsWindow(Some(hwnd)).as_bool() && GetMessageW(&mut message, None, 0, 0).0 > 0 {
            DispatchMessageW(&message);
        }
    });
    let hwnd = HWND(receiver.recv().unwrap() as *mut _);
    let result = (|| -> Result<(), String> {
        let mut pid = 0;
        let thread_id = unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
        let mut entry = WindowSnapshot {
            hwnd,
            pid,
            thread_id,
            class_name: class_name(hwnd),
            tool_window: false,
            moved: true,
            layout: None,
            startup_layout: None,
        };
        unsafe { ShowWindowAsync(hwnd, SW_MAXIMIZE) }
            .ok()
            .map_err(|e| e.to_string())?;
        wait_for_goal(
            &entry,
            WindowGoal::on_monitor(&entry, source.rect, WindowState::Maximized)?,
            "prepare maximized fixture",
        )?;
        if minimized {
            unsafe { ShowWindowAsync(hwnd, SW_SHOWMINNOACTIVE) }
                .ok()
                .map_err(|e| e.to_string())?;
            wait_for_goal(
                &entry,
                WindowGoal {
                    state: WindowState::Minimized,
                    ..Default::default()
                },
                "prepare minimized fixture",
            )?;
        }
        let before = observe_window(&entry)?.ok_or("fixture closed")?;
        unminimized_events.store(0, Ordering::SeqCst);
        entry.layout = Some(WindowLayout::capture(&entry, before, &source_info)?);
        for display in [target, source] {
            restore_entry(&entry, display.rect)?;
            let info = default_monitor(display.rect)?;
            let planned = planned_window_rect(&entry, &info, (0, 1));
            let goal = layout_goal(&entry, planned, &info)?;
            let after = observe_window(&entry)?.ok_or("fixture closed")?;
            assert!(goal.matches(&after, goal.current_monitor()), "{after:?}");
            assert_eq!(after.iconic, before.iconic);
            if !minimized {
                assert_eq!(after.zoomed, before.zoomed);
            } else {
                assert_eq!(
                    unminimized_events.load(Ordering::SeqCst),
                    0,
                    "minimized transfer briefly restored the window"
                );
            }
        }
        Ok(())
    })();
    unsafe { PostMessageW(Some(hwnd), WM_CLOSE, WPARAM(0), LPARAM(0)) }.unwrap();
    owner.join().unwrap();
    unsafe { SetThreadDpiAwarenessContext(previous) };
    result.unwrap();
}

#[test]
fn normal_goal_rejects_minimized_and_invalid_geometry() {
    let goal = WindowGoal {
        state: WindowState::Normal,
        ..Default::default()
    };
    let mut actual = observation();
    assert!(goal.matches(&actual, None));
    actual.iconic = true;
    actual.outer = rect(-32000, -32000, 314, 50);
    assert!(!goal.matches(&actual, None));
    actual.iconic = false;
    actual.outer = rect(0, 0, 0, 600);
    assert!(!goal.matches(&actual, None));
    actual.outer = rect(0, 0, 800, 600);
    actual.zoomed = true;
    assert!(!goal.matches(&actual, None));
}

#[test]
fn restored_goal_requires_one_observation_to_match_every_condition() {
    let mut actual = observation();
    let goal = WindowGoal {
        state: WindowState::Normal,
        dpi: Some(192),
        outer: Some(actual.outer),
        normal: Some(actual.placement.rcNormalPosition),
        ..Default::default()
    };
    let target = Some(actual.monitor);
    assert!(goal.matches(&actual, target));
    actual.dpi = 120;
    assert!(!goal.matches(&actual, target));
    actual.dpi = 192;
    actual.placement.rcNormalPosition = rect(0, 0, 801, 600);
    assert!(!goal.matches(&actual, target));
    actual.placement.rcNormalPosition = goal.normal.unwrap();
    actual.monitor = HMONITOR(2usize as *mut _);
    assert!(!goal.matches(&actual, target));
}

#[test]
fn delayed_dpi_cannot_settle_merely_by_reaching_the_monitor() {
    let now = Instant::now();
    let mut tracker = WindowSettling::default();
    let mut actual = observation();
    let goal = WindowGoal {
        dpi: Some(192),
        ..Default::default()
    };
    actual.dpi = 120;
    assert!(!tracker.observe(actual, goal.matches(&actual, None), now));
    assert!(!tracker.observe(
        actual,
        goal.matches(&actual, None),
        now + Duration::from_millis(200)
    ));
    actual.dpi = 192;
    assert!(!tracker.observe(
        actual,
        goal.matches(&actual, None),
        now + Duration::from_millis(309)
    ));
    assert!(!tracker.observe(actual, true, now + Duration::from_millis(408)));
    assert!(tracker.observe(actual, true, now + Duration::from_millis(409)));
}

#[test]
fn default_goal_requires_position_and_allows_application_size_constraints() {
    let requested = rect(100, 80, 800, 600);
    let goal = WindowGoal {
        state: WindowState::Normal,
        position: Some((requested.left, requested.top)),
        ..Default::default()
    };
    let mut actual = observation();
    assert!(!goal.matches(&actual, None));
    actual.outer = requested;
    assert!(goal.matches(&actual, None));
    actual.outer.right += 40;
    assert!(goal.matches(&actual, None));
    actual.outer.right = actual.outer.left + 320;
    actual.outer.bottom = actual.outer.top + 240;
    assert!(goal.matches(&actual, None));
    actual.outer.left += 1;
    assert!(!goal.matches(&actual, None));
    actual.outer = requested;
    actual.outer.bottom = actual.outer.top;
    assert!(!goal.matches(&actual, None));
}

#[test]
fn geometry_change_and_failed_goal_restart_the_settling_interval() {
    let now = Instant::now();
    let mut tracker = WindowSettling::default();
    let mut actual = observation();
    assert!(!tracker.observe(actual, true, now));
    actual.outer.right += 1;
    assert!(!tracker.observe(actual, true, now + Duration::from_millis(90)));
    assert!(!tracker.observe(actual, true, now + Duration::from_millis(100)));
    assert!(!tracker.observe(actual, false, now + Duration::from_millis(180)));
    assert!(!tracker.observe(actual, true, now + Duration::from_millis(190)));
    assert!(!tracker.observe(actual, true, now + Duration::from_millis(289)));
    assert!(tracker.observe(actual, true, now + Duration::from_millis(290)));
}

#[test]
#[ignore = "creates hidden windows to verify DPI awareness and normal-window preparation"]
fn native_destination_dpi_matches_window_awareness() {
    use windows::Win32::UI::HiDpi::{
        DPI_AWARENESS_CONTEXT_SYSTEM_AWARE, DPI_AWARENESS_CONTEXT_UNAWARE,
    };
    let displays = crate::display::active_displays().unwrap();
    assert!(displays.len() >= 2);
    let original_context =
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    for context in [
        DPI_AWARENESS_CONTEXT_UNAWARE,
        DPI_AWARENESS_CONTEXT_SYSTEM_AWARE,
        DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
    ] {
        unsafe { SetThreadDpiAwarenessContext(context) };
        let hwnd = unsafe {
            CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                w!("STATIC"),
                w!(""),
                WS_POPUP,
                100,
                100,
                20,
                20,
                None,
                None,
                None,
                None,
            )
        }
        .unwrap();
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
        let mut pid = 0;
        let thread_id = unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
        let entry = WindowSnapshot {
            hwnd,
            pid,
            thread_id,
            class_name: class_name(hwnd),
            tool_window: true,
            moved: false,
            layout: None,
            startup_layout: None,
        };
        let checks = (|| -> Result<(), String> {
            for display in &displays {
                let predicted = destination_dpi(&entry, display.rect)?;
                unsafe {
                    SetWindowPos(
                        hwnd,
                        None,
                        display.rect.left + 100,
                        display.rect.top + 100,
                        20,
                        20,
                        SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER,
                    )
                }
                .map_err(|error| error.to_string())?;
                let actual = unsafe { GetDpiForWindow(hwnd) };
                println!(
                    "awareness={context:?} device={} predicted={predicted} actual={actual}",
                    display.device_name
                );
                if predicted != actual {
                    return Err(format!(
                        "DPI mismatch: predicted={predicted} actual={actual}"
                    ));
                }
                let started = Instant::now();
                let prepared = ensure_normal(&entry, window_placement(&entry)?.rcNormalPosition)?
                    .expect("hidden window still exists");
                let preparation_elapsed = started.elapsed();
                assert_eq!(prepared.dpi, predicted);
                assert!(!prepared.iconic && !prepared.zoomed);
                let requested = offset_rect(prepared.outer, 10, 10)?;
                let started = Instant::now();
                let moved = move_normal(&entry, requested, display.rect)?
                    .expect("hidden window still exists after moving");
                let movement_elapsed = started.elapsed();
                assert!(movement_elapsed >= SETTLE_INTERVAL);
                assert_eq!(moved.outer, requested);
                assert_eq!(moved.dpi, predicted);
                println!(
                    "normal preparation={preparation_elapsed:?}; movement with settling={movement_elapsed:?}"
                );
            }
            Ok(())
        })();
        unsafe { DestroyWindow(hwnd) }.unwrap();
        if let Err(error) = checks {
            unsafe { SetThreadDpiAwarenessContext(original_context) };
            panic!("{error}");
        }
    }
    unsafe { SetThreadDpiAwarenessContext(original_context) };
}

#[test]
#[ignore = "creates a visible native window to verify prepared states and application size constraints"]
fn native_default_layout_restores_prepared_states_and_respects_size_constraints() {
    use windows::Win32::Foundation::{LRESULT, WPARAM};
    use windows::Win32::UI::WindowsAndMessaging::{
        DefWindowProcW, DispatchMessageW, GWLP_USERDATA, GWLP_WNDPROC, GetMessageW, MSG,
        PostMessageW, SW_MAXIMIZE, SW_MINIMIZE, SWP_NOMOVE, SWP_NOSIZE, SetWindowLongPtrW,
        WINDOWPOS, WM_CLOSE, WM_WINDOWPOSCHANGING, WS_OVERLAPPEDWINDOW,
    };
    unsafe extern "system" fn minimum_size(
        hwnd: HWND,
        message: u32,
        w: WPARAM,
        l: LPARAM,
    ) -> LRESULT {
        if message == WM_WINDOWPOSCHANGING && !unsafe { IsIconic(hwnd) }.as_bool() {
            let packed = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as u64;
            let pos = unsafe { &mut *(l.0 as *mut WINDOWPOS) };
            if pos.flags.0 & SWP_NOSIZE.0 == 0 && packed != 0 {
                pos.cx = pos.cx.max((packed >> 32) as i32);
                pos.cy = pos.cy.max(packed as u32 as i32);
            }
        }
        unsafe { DefWindowProcW(hwnd, message, w, l) }
    }
    unsafe extern "system" fn refuse_geometry(
        hwnd: HWND,
        message: u32,
        w: WPARAM,
        l: LPARAM,
    ) -> LRESULT {
        if message == WM_WINDOWPOSCHANGING {
            let pos = unsafe { &mut *(l.0 as *mut WINDOWPOS) };
            pos.flags |= SWP_NOMOVE | SWP_NOSIZE;
        }
        unsafe { DefWindowProcW(hwnd, message, w, l) }
    }
    unsafe extern "system" fn maximum_size(
        hwnd: HWND,
        message: u32,
        w: WPARAM,
        l: LPARAM,
    ) -> LRESULT {
        if message == WM_WINDOWPOSCHANGING && !unsafe { IsIconic(hwnd) }.as_bool() {
            let pos = unsafe { &mut *(l.0 as *mut WINDOWPOS) };
            if pos.flags.0 & SWP_NOSIZE.0 == 0 {
                pos.cx = pos.cx.min(480);
                pos.cy = pos.cy.min(320);
            }
        }
        unsafe { DefWindowProcW(hwnd, message, w, l) }
    }
    unsafe extern "system" fn fixed_size(
        hwnd: HWND,
        message: u32,
        w: WPARAM,
        l: LPARAM,
    ) -> LRESULT {
        if message == WM_WINDOWPOSCHANGING && !unsafe { IsIconic(hwnd) }.as_bool() {
            let pos = unsafe { &mut *(l.0 as *mut WINDOWPOS) };
            if pos.flags.0 & SWP_NOSIZE.0 == 0 {
                pos.cx = 560;
                pos.cy = 360;
            }
        }
        unsafe { DefWindowProcW(hwnd, message, w, l) }
    }
    let previous =
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let display = crate::display::active_displays()
        .unwrap()
        .into_iter()
        .find(|display| display.primary)
        .unwrap();
    let info = default_monitor(display.rect).unwrap();
    let expected = default_window_rect(info.rcWork);
    // Match real applications: the owner processes asynchronous visibility and
    // position requests while the migration caller waits on another thread.
    let (sender, receiver) = std::sync::mpsc::channel();
    let owner = thread::spawn(move || unsafe {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        let hwnd = CreateWindowExW(
            WS_EX_NOACTIVATE,
            w!("STATIC"),
            w!("SBMS default layout test"),
            WS_OVERLAPPEDWINDOW,
            expected.left,
            expected.top,
            640,
            480,
            None,
            None,
            None,
            None,
        )
        .unwrap();
        sender.send(hwnd.0 as usize).unwrap();
        let mut message = MSG::default();
        while IsWindow(Some(hwnd)).as_bool() && GetMessageW(&mut message, None, 0, 0).0 > 0 {
            DispatchMessageW(&message);
        }
    });
    let mut owner = Some(owner);
    let hwnd = HWND(receiver.recv().unwrap() as *mut _);
    let checks = (|| -> Result<(), String> {
        let mut pid = 0;
        let thread_id = unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)) };
        let entry = WindowSnapshot {
            hwnd,
            pid,
            thread_id,
            class_name: class_name(hwnd),
            tool_window: false,
            moved: false,
            layout: None,
            startup_layout: None,
        };
        let migration = WindowMigration {
            migration_id: next_migration_id(),
            entries: Arc::new(Mutex::new(vec![entry])),
            stop_scanner: Arc::new(AtomicBool::new(true)),
            scanner: None,
            target_rect: display.rect,
            source_id: None,
        };
        // Maximize first, then begin the next cycle already minimized with a restore-to-max flag.
        for initial_state in [SW_MAXIMIZE, SW_MINIMIZE] {
            {
                let entries = migration.entries.lock().unwrap();
                let mut placement = window_placement(&entries[0])?;
                placement.showCmd = initial_state.0 as u32;
                placement.flags.0 =
                    windows::Win32::UI::WindowsAndMessaging::WPF_RESTORETOMAXIMIZED.0;
                unsafe { SetWindowPlacement(hwnd, &placement) }
                    .map_err(|error| error.to_string())?;
            }
            migration.entries.lock().unwrap()[0].moved = true;
            for entry in migration
                .entries
                .lock()
                .unwrap()
                .iter()
                .filter(|entry| entry.moved)
            {
                prepare_minimized_fixture(entry)?;
            }
            {
                let entries = migration.entries.lock().unwrap();
                let prepared = observe_window(&entries[0])?.ok_or("test window disappeared")?;
                if !prepared.iconic {
                    return Err(format!("fixture did not minimize: {prepared:?}"));
                }
            }
            restore_layout_batch(
                &migration.entries.lock().unwrap().iter().collect::<Vec<_>>(),
                display.rect,
            )?;
            let entries = migration.entries.lock().unwrap();
            let actual = observe_window(&entries[0])?.ok_or("test window disappeared")?;
            if actual.iconic || actual.zoomed || actual.outer != expected {
                return Err(format!("default restoration mismatch: {actual:?}"));
            }
        }
        let min_width = (expected.right - expected.left + 40) as u64;
        let min_height = (expected.bottom - expected.top + 30) as u64;
        unsafe {
            SetWindowLongPtrW(
                hwnd,
                GWLP_USERDATA,
                ((min_width << 32) | min_height) as isize,
            );
            SetWindowLongPtrW(hwnd, GWLP_WNDPROC, minimum_size as *const () as isize);
        }
        migration.entries.lock().unwrap()[0].moved = true;
        for entry in migration
            .entries
            .lock()
            .unwrap()
            .iter()
            .filter(|entry| entry.moved)
        {
            prepare_minimized_fixture(entry)?;
        }
        restore_layout_batch(
            &migration.entries.lock().unwrap().iter().collect::<Vec<_>>(),
            display.rect,
        )?;
        {
            let entries = migration.entries.lock().unwrap();
            let actual = observe_window(&entries[0])?.ok_or("test window disappeared")?;
            if actual.iconic
                || actual.zoomed
                || actual.outer.left != expected.left
                || actual.outer.top != expected.top
                || actual.outer.right - actual.outer.left != min_width as i32
                || actual.outer.bottom - actual.outer.top != min_height as i32
            {
                return Err(format!(
                    "minimum-size default restoration mismatch: {actual:?}"
                ));
            }
        }
        // Real application callbacks may accept movement but constrain the size
        // below SBMS's default. Exercise the production placement path for both.
        for (procedure, width, height) in [
            (maximum_size as *const () as isize, 480, 320),
            (fixed_size as *const () as isize, 560, 360),
        ] {
            unsafe {
                SetWindowLongPtrW(hwnd, GWLP_WNDPROC, procedure);
            }
            let entries = migration.entries.lock().unwrap();
            set_window_rect(
                &entries[0],
                info.rcWork.left + 10,
                info.rcWork.top + 10,
                800,
                600,
            )
            .map_err(|error| error.to_string())?;
            restore_entry(&entries[0], display.rect)?;
            let actual = observe_window(&entries[0])?.ok_or("test window disappeared")?;
            if actual.iconic
                || actual.zoomed
                || actual.outer != rect(expected.left, expected.top, width, height)
            {
                return Err(format!("constrained-size restoration mismatch: {actual:?}"));
            }
            println!("PASS constrained size {width}x{height} at default position");
        }
        // A window that refuses geometry must not count as restored merely because it is visible.
        unsafe {
            SetWindowLongPtrW(hwnd, GWLP_WNDPROC, DefWindowProcW as *const () as isize);
        }
        {
            let entries = migration.entries.lock().unwrap();
            set_window_rect(
                &entries[0],
                info.rcWork.left + 10,
                info.rcWork.top + 10,
                320,
                240,
            )
            .map_err(|error| error.to_string())?;
            wait_for_goal(
                &entries[0],
                WindowGoal {
                    state: WindowState::Normal,
                    outer: Some(rect(info.rcWork.left + 10, info.rcWork.top + 10, 320, 240)),
                    ..Default::default()
                },
                "prepare rejected-move fixture",
            )?;
            unsafe {
                SetWindowLongPtrW(hwnd, GWLP_WNDPROC, refuse_geometry as *const () as isize);
            }
            let error = match restore_entry(&entries[0], display.rect) {
                Ok(()) => return Err("refused geometry must fail".into()),
                Err(error) => error,
            };
            if !error.contains("place window did not settle") {
                return Err(error);
            }
        }
        unsafe { PostMessageW(Some(hwnd), WM_CLOSE, WPARAM(0), LPARAM(0)) }
            .map_err(|error| error.to_string())?;
        owner.take().unwrap().join().unwrap();
        // A closed HWND must be skipped even if it was marked for cleanup.
        migration.entries.lock().unwrap()[0].moved = true;
        restore_layout_batch(
            &migration.entries.lock().unwrap().iter().collect::<Vec<_>>(),
            display.rect,
        )?;
        Ok(())
    })();
    if unsafe { IsWindow(Some(hwnd)) }.as_bool() {
        let _ = unsafe { PostMessageW(Some(hwnd), WM_CLOSE, WPARAM(0), LPARAM(0)) };
    }
    if let Some(owner) = owner {
        owner.join().unwrap();
    }
    unsafe { SetThreadDpiAwarenessContext(previous) };
    checks.unwrap();
}

#[test]
fn movable_state_does_not_require_valid_current_coordinates() {
    let mut actual = observation();
    actual.outer = rect(-32000, -32000, 1348, 253);
    actual.monitor = HMONITOR::default();
    let movable = WindowGoal {
        state: WindowState::Restored,
        ..Default::default()
    };
    assert!(movable.matches(&actual, None));
    let final_goal = WindowGoal {
        state: WindowState::Normal,
        ..Default::default()
    };
    assert!(!final_goal.matches(&actual, None));
    actual.iconic = true;
    assert!(!movable.matches(&actual, None));
}

#[test]
#[ignore = "creates one non-activating test window and moves it across existing physical displays"]
fn native_invalid_normal_coordinates_move_to_valid_destination_without_minimize() {
    use std::sync::atomic::AtomicUsize;
    use windows::Win32::Foundation::{LRESULT, WPARAM};
    use windows::Win32::UI::WindowsAndMessaging::{
        DefWindowProcW, GWLP_WNDPROC, SIZE_MINIMIZED, SetWindowLongPtrW, WM_SIZE,
        WS_OVERLAPPEDWINDOW,
    };
    static MINIMIZATIONS: AtomicUsize = AtomicUsize::new(0);
    unsafe extern "system" fn count_minimize(
        hwnd: HWND,
        message: u32,
        w: WPARAM,
        l: LPARAM,
    ) -> LRESULT {
        if message == WM_SIZE && w.0 == SIZE_MINIMIZED as usize {
            MINIMIZATIONS.fetch_add(1, Ordering::Relaxed);
        }
        unsafe { DefWindowProcW(hwnd, message, w, l) }
    }
    let previous =
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let displays: Vec<_> = crate::display::active_displays()
        .unwrap()
        .into_iter()
        .filter(|display| !display.virtual_display)
        .collect();
    assert!(
        displays.len() >= 2,
        "requires two existing physical displays"
    );
    let hwnd = unsafe {
        CreateWindowExW(
            WS_EX_NOACTIVATE,
            w!("STATIC"),
            w!("SBMS invalid coordinate test"),
            WS_OVERLAPPEDWINDOW,
            displays[0].rect.left + 20,
            displays[0].rect.top + 20,
            640,
            480,
            None,
            None,
            None,
            None,
        )
    }
    .unwrap();
    let checks = (|| -> Result<(), String> {
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
        unsafe {
            SetWindowLongPtrW(hwnd, GWLP_WNDPROC, count_minimize as *const () as isize);
            ShowWindowAsync(hwnd, SW_SHOWNOACTIVATE)
                .ok()
                .map_err(|error| error.to_string())?;
        }
        MINIMIZATIONS.store(0, Ordering::Relaxed);
        for display in &displays {
            set_window_rect(&entry, -32000, -32000, 1348, 253)
                .map_err(|error| error.to_string())?;
            let invalid = observe_window(&entry)?.ok_or("test window disappeared")?;
            assert!(!invalid.iconic && !invalid.zoomed);
            assert!(invalid.monitor.0.is_null());
            assert_eq!(invalid.outer.left, -32000);
            let requested = default_window_rect(default_monitor(display.rect)?.rcWork);
            let prepared = ensure_normal(&entry, requested)?.ok_or("test window disappeared")?;
            // Preparation must not require or rewrite the invalid coordinates;
            // the following destination move repairs them.
            assert_eq!(prepared.outer, invalid.outer);
            restore_entry(&entry, display.rect)?;
            let actual = observe_window(&entry)?.ok_or("test window disappeared")?;
            assert_eq!(actual.outer, requested);
            assert_eq!(actual.dpi, destination_dpi(&entry, display.rect)?);
            assert_eq!(
                actual.monitor,
                monitor_for_rect(display.rect, "test target")?
            );
            assert!(!actual.iconic && !actual.zoomed);
            println!(
                "PASS invalid normal -> {} dpi={} outer={:?}",
                display.device_name, actual.dpi, actual.outer
            );
        }
        // Now cross directly between physical displays, without an invalid
        // intermediate position or any minimization used as a DPI prerequisite.
        for display in &displays {
            restore_entry(&entry, display.rect)?;
            let actual = observe_window(&entry)?.ok_or("test window disappeared")?;
            assert_eq!(actual.dpi, destination_dpi(&entry, display.rect)?);
            assert_eq!(
                actual.monitor,
                monitor_for_rect(display.rect, "test target")?
            );
            println!(
                "PASS direct cross-DPI -> {} dpi={}",
                display.device_name, actual.dpi
            );
        }
        assert_eq!(MINIMIZATIONS.load(Ordering::Relaxed), 0);
        Ok(())
    })();
    unsafe {
        let _ = DestroyWindow(hwnd);
        SetThreadDpiAwarenessContext(previous);
    }
    checks.unwrap();
}

fn selection_display(id: &str, virtual_display: bool) -> Display {
    Display {
        id: id.into(),
        connector_index: 0,
        sunshine_id: None,
        name: String::new(),
        device_name: String::new(),
        rect: rect(0, 0, 1920, 1080),
        native_width: 1920,
        native_height: 1080,
        physical_width_mm: None,
        physical_height_mm: None,
        preferred_refresh_millihz: None,
        rotation: crate::geometry::Rotation::Deg0,
        refresh_numerator: 60,
        refresh_denominator: 1,
        primary: !virtual_display,
        virtual_display,
    }
}

#[test]
fn stop_source_identity_does_not_fall_back_to_reused_rectangle_or_physical_screen() {
    let physical = selection_display("source-id", false);
    let other_virtual = selection_display("other-source", true);
    let displays = vec![physical, other_virtual];
    assert!(restore_source(&displays, Some("source-id")).is_none());
    assert!(restore_source(&displays, None).is_none());
    let mut displays = displays;
    displays.push(selection_display("SOURCE-ID", true));
    let source = restore_source(&displays, Some("source-id")).unwrap();
    assert!(source.virtual_display);
    assert_eq!(source.id, "SOURCE-ID");
}

#[test]
fn failed_start_recovery_keeps_invalid_windows_but_not_windows_on_another_screen() {
    let source = HMONITOR(std::ptr::dangling_mut());
    let primary = HMONITOR(2usize as *mut _);
    assert!(recovery_window_belongs_to_source(source, Some(source)));
    assert!(recovery_window_belongs_to_source(
        HMONITOR::default(),
        Some(source)
    ));
    assert!(!recovery_window_belongs_to_source(primary, Some(source)));
    // A missing source or unreadable topology cannot identify any live screen
    // as ours. This also prevents an old group from stealing another's window.
    assert!(recovery_window_belongs_to_source(HMONITOR::default(), None));
    assert!(!recovery_window_belongs_to_source(primary, None));
    assert!(!recovery_window_belongs_to_source(source, None));
}

#[test]
fn releasing_all_historical_claims_allows_two_groups_to_exchange_windows() {
    let mut claims = WindowClaimRegistry::default();
    let first = WindowIdentity {
        hwnd: 10,
        pid: 20,
        thread_id: 30,
        class_name: "first".into(),
    };
    let second = WindowIdentity {
        hwnd: 11,
        ..first.clone()
    };
    assert!(claims.claim(1, first.clone()));
    assert!(claims.claim(2, second.clone()));
    claims.release_owner(1);
    claims.release_owner(2);
    assert!(claims.claim(2, first.clone()));
    assert!(claims.claim(1, second.clone()));
    assert!(!claims.claim(1, first));
    assert!(!claims.claim(2, second));
}

#[test]
#[ignore = "creates a dedicated visible window to check stop-time monitor ownership, including minimized windows"]
fn native_stop_snapshot_includes_minimized_source_windows_and_excludes_other_screens() {
    use windows::Win32::UI::WindowsAndMessaging::{
        SW_SHOWMINNOACTIVE, WS_OVERLAPPEDWINDOW, WS_VISIBLE,
    };
    let displays = crate::display::active_displays().unwrap();
    assert!(displays.len() >= 2, "requires two displays");
    let source = &displays[0];
    let other = &displays[1];
    let previous =
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let hwnd = unsafe {
        CreateWindowExW(
            WS_EX_APPWINDOW | WS_EX_NOACTIVATE,
            w!("STATIC"),
            w!("SBMS stop ownership test"),
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            source.rect.left + 100,
            source.rect.top + 100,
            480,
            320,
            None,
            None,
            None,
            None,
        )
    }
    .unwrap();
    let checks = (|| -> Result<(), String> {
        let source_monitor = monitor_for_rect(source.rect, "fixture source")?;
        let other_monitor = monitor_for_rect(other.rect, "fixture other")?;
        assert!(snapshot_window_for_phase(hwnd, source_monitor, 0, true)?.is_some());
        assert!(snapshot_window_for_phase(hwnd, other_monitor, 0, true)?.is_none());
        // A minimized window's outer rectangle is -32000. Ownership must still
        // come from MonitorFromWindow's pre-minimize placement.
        let mut placement = WINDOWPLACEMENT {
            length: size_of::<WINDOWPLACEMENT>() as u32,
            ..Default::default()
        };
        unsafe { GetWindowPlacement(hwnd, &mut placement) }.map_err(|e| e.to_string())?;
        placement.showCmd = SW_SHOWMINNOACTIVE.0 as u32;
        unsafe { SetWindowPlacement(hwnd, &placement) }.map_err(|e| e.to_string())?;
        assert!(unsafe { IsIconic(hwnd) }.as_bool());
        assert!(snapshot_window_for_phase(hwnd, source_monitor, 0, true)?.is_some());
        assert!(snapshot_window_for_phase(hwnd, source_monitor, 0, false)?.is_none());
        assert!(snapshot_window_for_phase(hwnd, other_monitor, 0, true)?.is_none());
        // Once the window is deliberately moved elsewhere, stop selection must
        // follow the new screen rather than its historical migration record.
        placement.showCmd = SW_SHOWNOACTIVATE.0 as u32;
        let snapshot = snapshot_window_for_phase(hwnd, source_monitor, 0, true)?
            .ok_or("minimized test window was not selected")?;
        placement.rcNormalPosition = normal_position(
            &snapshot,
            rect(other.rect.left + 100, other.rect.top + 100, 480, 320),
            &default_monitor(other.rect)?,
        )?;
        unsafe { SetWindowPlacement(hwnd, &placement) }.map_err(|e| e.to_string())?;
        assert!(snapshot_window_for_phase(hwnd, source_monitor, 0, true)?.is_none());
        assert!(snapshot_window_for_phase(hwnd, other_monitor, 0, true)?.is_some());
        Ok(())
    })();
    unsafe { DestroyWindow(hwnd) }.unwrap();
    unsafe { SetThreadDpiAwarenessContext(previous) };
    checks.unwrap();
}

#[test]
fn batch_reports_failure_once_and_continues_other_windows() {
    let mut calls = Vec::new();
    let error = run_window_batch(3, |index| {
        calls.push(index);
        if index == 1 {
            Err("window refused valid position".into())
        } else {
            Ok(())
        }
    })
    .unwrap_err();
    calls.sort_unstable();
    assert_eq!(
        calls,
        [0, 1, 2],
        "each window must be attempted exactly once"
    );
    assert!(error.contains("window refused valid position"), "{error}");
}

#[test]
fn preserved_normal_position_translates_workspace_coordinates_between_monitor_origins() {
    let saved = PhysicalWindowSnapshot {
        window: WindowSnapshot {
            hwnd: HWND::default(),
            pid: 0,
            thread_id: 0,
            class_name: String::new(),
            tool_window: false,
            moved: false,
            layout: None,
            startup_layout: None,
        },
        display_id: "physical".into(),
        monitor: MONITORINFO::default(),
        normal_display_id: "physical".into(),
        normal_monitor: MONITORINFO {
            rcMonitor: rect(1000, 500, 1920, 1080),
            rcWork: rect(1040, 530, 1880, 1050),
            ..Default::default()
        },
        observation: WindowObservation {
            placement: WINDOWPLACEMENT {
                rcNormalPosition: rect(1100, 600, 800, 600),
                ..Default::default()
            },
            ..observation()
        },
    };
    let current = MONITORINFO {
        rcMonitor: rect(0, 0, 1920, 1080),
        rcWork: rect(0, 50, 1920, 1030),
        ..Default::default()
    };
    assert_eq!(
        preserved_normal_position(&saved, &current).unwrap(),
        rect(140, 80, 800, 600)
    );
}

#[test]
#[ignore = "uses a dedicated visible window to verify physical-desktop preservation without modifying other applications"]
fn native_physical_preservation_restores_os_relocation_and_does_not_write_unchanged_windows() {
    use std::sync::atomic::AtomicUsize;
    use windows::Win32::Foundation::{LRESULT, WPARAM};
    use windows::Win32::UI::WindowsAndMessaging::{
        DefWindowProcW, GWLP_WNDPROC, SW_SHOWMAXIMIZED, SW_SHOWMINNOACTIVE, SetWindowLongPtrW,
        WM_WINDOWPOSCHANGING, WS_OVERLAPPEDWINDOW, WS_VISIBLE,
    };
    static POSITION_REQUESTS: AtomicUsize = AtomicUsize::new(0);
    unsafe extern "system" fn count_position_changes(
        hwnd: HWND,
        message: u32,
        w: WPARAM,
        l: LPARAM,
    ) -> LRESULT {
        if message == WM_WINDOWPOSCHANGING {
            POSITION_REQUESTS.fetch_add(1, Ordering::Relaxed);
        }
        unsafe { DefWindowProcW(hwnd, message, w, l) }
    }
    let displays = crate::display::active_displays().unwrap();
    let target = displays.iter().find(|display| display.primary).unwrap();
    let other = displays
        .iter()
        .find(|display| !display.primary && !display.virtual_display)
        .expect("test requires a second physical screen");
    let previous =
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let hwnd = unsafe {
        CreateWindowExW(
            WS_EX_APPWINDOW | WS_EX_NOACTIVATE,
            w!("STATIC"),
            w!("SBMS physical preservation test"),
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            target.rect.left + 150,
            target.rect.top + 150,
            640,
            480,
            None,
            None,
            None,
            None,
        )
    }
    .unwrap();
    unsafe {
        SetWindowLongPtrW(
            hwnd,
            GWLP_WNDPROC,
            count_position_changes as *const () as isize,
        );
    }
    let checks = (|| -> Result<(), String> {
        for state in [SW_SHOWNOACTIVATE, SW_SHOWMINNOACTIVE, SW_SHOWMAXIMIZED] {
            let window = snapshot_window_for_phase(
                hwnd,
                unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL) },
                0,
                true,
            )?
            .ok_or("test window unavailable")?;
            let target_info = default_monitor(target.rect)?;
            let mut initial = window_placement(&window)?;
            initial.showCmd = state.0 as u32;
            initial.flags.0 = 0;
            initial.rcNormalPosition = normal_position(
                &window,
                rect(target.rect.left + 150, target.rect.top + 150, 640, 480),
                &target_info,
            )?;
            unsafe { SetWindowPlacement(hwnd, &initial) }.map_err(|e| e.to_string())?;
            let saved = PhysicalWindowSnapshot {
                observation: observe_window(&window)?.ok_or("test window disappeared")?,
                window,
                display_id: target.id.clone(),
                monitor: target_info,
                normal_display_id: target.id.clone(),
                normal_monitor: target_info,
            };
            POSITION_REQUESTS.store(0, Ordering::Relaxed);
            restore_physical_window(&saved, target.rect, target.rect)?;
            assert_eq!(
                POSITION_REQUESTS.load(Ordering::Relaxed),
                0,
                "unchanged physical window must receive no placement writes"
            );
            let mut relocated = saved.observation.placement;
            relocated.showCmd = SW_SHOWNOACTIVATE.0 as u32;
            relocated.rcNormalPosition = normal_position(
                &saved.window,
                rect(other.rect.left + 100, other.rect.top + 100, 640, 480),
                &default_monitor(other.rect)?,
            )?;
            unsafe { SetWindowPlacement(hwnd, &relocated) }.map_err(|e| e.to_string())?;
            assert_ne!(
                unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL) },
                monitor_for_rect(target.rect, "preserved physical target")?
            );
            restore_physical_window(&saved, target.rect, target.rect)?;
            let actual =
                observe_window(&saved.window)?.ok_or("test window disappeared after restore")?;
            assert_eq!(
                actual.monitor,
                monitor_for_rect(target.rect, "preserved physical target")?
            );
            assert_eq!(actual.iconic, saved.observation.iconic);
            assert_eq!(actual.zoomed, saved.observation.zoomed);
            assert_eq!(
                actual.placement.rcNormalPosition,
                saved.observation.placement.rcNormalPosition
            );
            if !actual.iconic && !actual.zoomed {
                assert_eq!(actual.outer, saved.observation.outer);
            }
            println!(
                "physical position/state preserved for {state:?}; unchanged window received zero writes"
            );
        }
        Ok(())
    })();
    unsafe { DestroyWindow(hwnd) }.unwrap();
    unsafe { SetThreadDpiAwarenessContext(previous) };
    checks.unwrap();
}
