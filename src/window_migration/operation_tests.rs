use super::*;

fn rect(left: i32, top: i32, width: i32, height: i32) -> RECT {
    RECT {
        left,
        top,
        right: left + width,
        bottom: top + height,
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
fn maximized_goal_allows_normal_rectangle_on_another_monitor() {
    let mut actual = observation();
    actual.zoomed = true;
    actual.placement.rcNormalPosition = rect(-2000, 0, 800, 600);
    let goal = WindowGoal {
        state: WindowState::Maximized,
        normal: Some(actual.placement.rcNormalPosition),
        ..Default::default()
    };
    assert!(goal.matches(&actual, Some(actual.monitor)));
    actual.iconic = true;
    assert!(!goal.matches(&actual, Some(actual.monitor)));
}

#[test]
#[ignore = "creates hidden windows on existing monitors to verify DPI awareness behavior"]
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
            placement: WINDOWPLACEMENT::default(),
            normal_anchor: None,
            tool_window: true,
            restore_target: None,
            outer_rect: RECT::default(),
            maximized: false,
            moved: false,
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
