//! Opt-in real Firefox coverage. The launcher owns the browser profile and topology.
use super::*;
use serde_json::{Value, json};
use std::path::{Path, PathBuf};
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetThreadDpiAwarenessContext,
};
use windows::Win32::UI::WindowsAndMessaging::{SW_MAXIMIZE, SW_MINIMIZE};

// Test setup only; production restoration executes the final layout directly.
fn fixture_minimize(entry: &WindowSnapshot) -> Result<(), String> {
    unsafe { ShowWindowAsync(entry.hwnd, SW_MINIMIZE) }
        .ok()
        .map_err(|error| error.to_string())?;
    wait_for_goal(
        entry,
        WindowGoal {
            state: WindowState::Minimized,
            ..Default::default()
        },
        "prepare minimized Firefox fixture",
    )?;
    Ok(())
}

fn rect_json(rect: RECT) -> Value {
    json!({"left":rect.left,"top":rect.top,"right":rect.right,"bottom":rect.bottom})
}

fn observe(hwnd: HWND, pid: u32) -> Result<Value, String> {
    let mut actual_pid = 0;
    unsafe { GetWindowThreadProcessId(hwnd, Some(&mut actual_pid)) };
    if actual_pid != pid || class_name(hwnd) != "MozillaWindowClass" {
        return Err("Dedicated Firefox window identity changed".into());
    }
    let mut outer = RECT::default();
    unsafe { GetWindowRect(hwnd, &mut outer) }.map_err(|e| e.to_string())?;
    let mut placement = WINDOWPLACEMENT {
        length: size_of::<WINDOWPLACEMENT>() as u32,
        ..Default::default()
    };
    unsafe { GetWindowPlacement(hwnd, &mut placement) }.map_err(|e| e.to_string())?;
    Ok(json!({
        "outer": rect_json(outer), "normal": rect_json(placement.rcNormalPosition),
        "show_cmd":placement.showCmd,"flags":placement.flags.0,
        "dpi":unsafe {GetDpiForWindow(hwnd)},
        "monitor":unsafe {MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL)}.0 as usize,
        "iconic":unsafe {IsIconic(hwnd)}.as_bool(),
        "zoomed":unsafe {IsZoomed(hwnd)}.as_bool(),
        "visible":unsafe {IsWindowVisible(hwnd)}.as_bool(),
    }))
}

// This only prepares/checks fixtures; actual migration and restoration call production code.
fn fixture_wait(
    hwnd: HWND,
    pid: u32,
    label: &str,
    matches: impl Fn(&Value) -> bool,
) -> Result<Value, String> {
    let deadline = Instant::now() + Duration::from_secs(5);
    let mut last = Value::Null;
    let mut stable_since = Instant::now();
    loop {
        let state = observe(hwnd, pid)?;
        if state != last || !matches(&state) {
            stable_since = Instant::now();
        }
        if matches(&state) && stable_since.elapsed() >= Duration::from_millis(400) {
            return Ok(state);
        }
        if Instant::now() >= deadline {
            return Err(format!("{label} fixture did not settle: {state}"));
        }
        last = state;
        thread::sleep(Duration::from_millis(20));
    }
}

fn ordinary(state: &Value) -> bool {
    state["iconic"] == false && state["zoomed"] == false
}

fn prepare(
    hwnd: HWND,
    pid: u32,
    target: &Display,
    width: i32,
    height: i32,
    maximized: bool,
) -> Result<Value, String> {
    unsafe { ShowWindowAsync(hwnd, SW_SHOWNOACTIVATE) }
        .ok()
        .map_err(|e| e.to_string())?;
    fixture_wait(hwnd, pid, "ordinary state", ordinary)?;
    let monitor = monitor_for_rect(target.rect, "fixture target")?.0 as usize;
    // First cross the DPI boundary, then set fixture pixels in the destination DPI.
    unsafe {
        SetWindowPos(
            hwnd,
            None,
            target.rect.left,
            target.rect.top,
            width,
            height,
            SWP_ASYNCWINDOWPOS | SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER,
        )
    }
    .map_err(|e| e.to_string())?;
    fixture_wait(hwnd, pid, "target monitor", |s| {
        ordinary(s) && s["monitor"] == monitor
    })?;
    let wanted = rect_json(RECT {
        left: target.rect.left,
        top: target.rect.top,
        right: target.rect.left + width,
        bottom: target.rect.top + height,
    });
    unsafe {
        SetWindowPos(
            hwnd,
            None,
            target.rect.left,
            target.rect.top,
            width,
            height,
            SWP_ASYNCWINDOWPOS | SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER,
        )
    }
    .map_err(|e| e.to_string())?;
    let state = fixture_wait(hwnd, pid, "exact ordinary rectangle", |s| {
        ordinary(s) && s["monitor"] == monitor && s["outer"] == wanted
    })?;
    if !maximized {
        return Ok(state);
    }
    unsafe { ShowWindowAsync(hwnd, SW_MAXIMIZE) }
        .ok()
        .map_err(|e| e.to_string())?;
    fixture_wait(hwnd, pid, "maximized state", |s| {
        s["iconic"] == false && s["zoomed"] == true && s["monitor"] == monitor
    })
}

fn verify_default(hwnd: HWND, pid: u32, target: &Display) -> Result<Value, String> {
    verify_layout(hwnd, pid, target, None)
}

fn verify_layout(
    hwnd: HWND,
    pid: u32,
    target: &Display,
    layout: Option<WindowLayout>,
) -> Result<Value, String> {
    let info = default_monitor(target.rect)?;
    let expected = layout
        .and_then(|layout| layout.mapped_rect(info.rcMonitor))
        .unwrap_or_else(|| default_window_rect(info.rcWork));
    let monitor = monitor_for_rect(info.rcMonitor, "default test target")?.0 as usize;
    let state = observe(hwnd, pid)?;
    let window_state = layout.map_or(WindowState::Normal, |layout| layout.state);
    if window_state != WindowState::Normal {
        let normal = offset_rect(
            expected,
            info.rcMonitor.left - info.rcWork.left,
            info.rcMonitor.top - info.rcWork.top,
        )?;
        let minimized = window_state == WindowState::Minimized;
        let maximized = window_state == WindowState::Maximized;
        let restore_max = windows::Win32::UI::WindowsAndMessaging::WPF_RESTORETOMAXIMIZED.0;
        if state["iconic"] != minimized
            || (!minimized && state["zoomed"] != maximized)
            || state["monitor"] != monitor
            || state["normal"] != rect_json(normal)
            || state["flags"].as_u64().unwrap_or_default() as u32 & restore_max
                != layout.unwrap().placement.flags.0 & restore_max
        {
            return Err(format!(
                "Preserved window layout mismatch: expected={expected:?}; actual={state}"
            ));
        }
        return Ok(state);
    }
    let outer: RECT = RECT {
        left: state["outer"]["left"].as_i64().ok_or("missing left")? as i32,
        top: state["outer"]["top"].as_i64().ok_or("missing top")? as i32,
        right: state["outer"]["right"].as_i64().ok_or("missing right")? as i32,
        bottom: state["outer"]["bottom"].as_i64().ok_or("missing bottom")? as i32,
    };
    let normal = offset_rect(
        outer,
        info.rcMonitor.left - info.rcWork.left,
        info.rcMonitor.top - info.rcWork.top,
    )?;
    if !ordinary(&state)
        || state["monitor"] != monitor
        || outer.left != expected.left
        || outer.top != expected.top
        || outer.right < expected.right
        || outer.bottom < expected.bottom
        || state["normal"] != rect_json(normal)
    {
        return Err(format!(
            "Default ordinary layout mismatch: expected={expected:?}; actual={state}"
        ));
    }
    Ok(state)
}

fn wait_topology_restore(dir: &Path) -> Result<(), String> {
    std::fs::write(dir.join("stop.signal"), "production probe complete")
        .map_err(|e| e.to_string())?;
    let deadline = Instant::now() + Duration::from_secs(30);
    while !dir.join("restored.json").exists() {
        if Instant::now() >= deadline {
            return Err("Topology restore was not verified".into());
        }
        thread::sleep(Duration::from_millis(100));
    }
    Ok(())
}

#[test]
#[ignore = "requires an explicitly launched dedicated Firefox and authorized test topology"]
fn native_firefox_production_round_trip() {
    let run = || -> Result<(), String> {
        let required = |name| std::env::var(name).map_err(|_| format!("Missing {name}"));
        let pid: u32 = required("SBMS_TEST_FIREFOX_PID")?
            .parse()
            .map_err(|_| "Invalid PID")?;
        let hwnd_text = required("SBMS_TEST_FIREFOX_HWND")?;
        let hwnd_num = if let Some(hex) = hwnd_text.strip_prefix("0x") {
            usize::from_str_radix(hex, 16)
        } else {
            hwnd_text.parse()
        }
        .map_err(|_| "Invalid HWND")?;
        let hwnd = HWND(hwnd_num as *mut std::ffi::c_void);
        let result_dir = PathBuf::from(required("SBMS_TEST_RESULT_DIR")?);
        let topology_dir = PathBuf::from(required("SBMS_TEST_TOPOLOGY_DIR")?);
        std::fs::create_dir_all(&result_dir).map_err(|e| e.to_string())?;
        let target_id = required("SBMS_TEST_TARGET_ID")?;
        let source_id = required("SBMS_TEST_SOURCE_ID")?;
        let displays = crate::display::active_displays().map_err(|e| e.to_string())?;
        let target = displays
            .iter()
            .find(|d| d.id.eq_ignore_ascii_case(&target_id))
            .ok_or("Target display missing")?;
        let source = displays
            .iter()
            .find(|d| d.id.eq_ignore_ascii_case(&source_id))
            .ok_or("Virtual source missing")?;
        if target.virtual_display || !source.virtual_display {
            return Err("Invalid test displays".into());
        }
        observe(hwnd, pid)?;
        let mut records = Vec::new();
        let cases = [
            (
                "ordinary-fullsize",
                target.rect.right - target.rect.left,
                target.rect.bottom - target.rect.top,
                false,
                false,
            ),
            ("ordinary-1348x849", 1348, 849, false, false),
            ("maximized", 1348, 849, true, false),
            ("minimized-during-mapping", 1348, 849, false, true),
        ];
        let result = (|| -> Result<(), String> {
            for (name, width, height, maximized, minimize) in cases {
                let mut record = json!({"case":name});
                let case_result = (|| -> Result<(), String> {
                    record["prepared"] = if name == "ordinary-fullsize" {
                        // The launcher placed this window before creating the virtual display.
                        // Keep that first-transition fixture instead of moving it again.
                        let state = observe(hwnd, pid)?;
                        let monitor = monitor_for_rect(target.rect, "initial fixture")?.0 as usize;
                        if !ordinary(&state)
                            || state["outer"] != rect_json(target.rect)
                            || state["monitor"] != monitor
                        {
                            return Err(format!("Initial full-size fixture mismatch: {state}"));
                        }
                        state
                    } else {
                        prepare(hwnd, pid, target, width, height, maximized)?
                    };
                    let target_monitor = monitor_for_rect(target.rect, "test target")?;
                    let entry = snapshot_window(hwnd, target_monitor, 0)?
                        .ok_or("Prepared Firefox was not eligible for migration")?;
                    let initial_layout = entry.layout;
                    let migration = WindowMigration {
                        migration_id: next_migration_id(),
                        entries: Arc::new(Mutex::new(vec![entry])),
                        stop_scanner: Arc::new(AtomicBool::new(true)),
                        scanner: None,
                        target_rect: target.rect,
                        source_id: Some(source.id.clone()),
                    };
                    let started = Instant::now();
                    {
                        let mut entries = migration.entries.lock().map_err(|e| e.to_string())?;
                        move_entry(&mut entries[0], target.rect, source.rect)?;
                    }
                    record["move_ms"] = json!(started.elapsed().as_millis());
                    record["moved"] = verify_layout(hwnd, pid, source, initial_layout)?;
                    if minimize {
                        unsafe { ShowWindowAsync(hwnd, SW_MINIMIZE) }
                            .ok()
                            .map_err(|e| e.to_string())?;
                        record["minimized"] =
                            fixture_wait(hwnd, pid, "runtime minimize", |s| s["iconic"] == true)?;
                    }
                    let restored_at = Instant::now();
                    let stop_layout = snapshot_window_for_phase(
                        hwnd,
                        monitor_for_rect(source.rect, "stop layout")?,
                        0,
                        true,
                    )?
                    .ok_or("Firefox missing before stop")?
                    .layout;
                    let mut plan = WindowRestorePlan::capture(
                        vec![(0, target.id.clone(), migration)],
                        RestoreMode::NormalStop,
                    );
                    plan.transfer(
                        &crate::display::active_display_topology().map_err(|e| e.to_string())?,
                    );
                    record["before_topology_restore"] = observe(hwnd, pid)?;
                    if minimize {
                        wait_topology_restore(&topology_dir)?;
                        let current =
                            crate::display::active_displays().map_err(|e| e.to_string())?;
                        let restored_target = current
                            .iter()
                            .find(|d| d.id.eq_ignore_ascii_case(&target_id))
                            .ok_or("Physical target missing after topology restore")?;
                        plan.finish(&current)?;
                        record["reconciled"] =
                            verify_layout(hwnd, pid, restored_target, stop_layout)?;
                    } else {
                        plan.finish(
                            &crate::display::active_display_topology()
                                .map_err(|e| e.to_string())?,
                        )?;
                        record["reconciled"] = verify_layout(hwnd, pid, target, stop_layout)?;
                    }
                    record["restore_ms"] = json!(restored_at.elapsed().as_millis());
                    Ok(())
                })();
                record["ok"] = json!(case_result.is_ok());
                record["error"] = json!(case_result.as_ref().err());
                if case_result.is_err() {
                    record["failure_state"] =
                        observe(hwnd, pid).unwrap_or_else(|e| json!({"error":e}));
                }
                println!("{record}");
                records.push(record);
                case_result?;
            }
            Ok(())
        })();
        std::fs::write(
            result_dir.join("firefox-production-results.json"),
            serde_json::to_vec_pretty(
                &json!({"ok":result.is_ok(),"error":result.as_ref().err(),"cases":records}),
            )
            .map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())?;
        result
    };
    let previous =
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let result = run();
    unsafe { SetThreadDpiAwarenessContext(previous) };
    result.unwrap();
}

/// Operates exclusively on the launcher's dedicated Firefox HWND. It neither
/// creates a display nor changes any physical display mode, position, or DPI.
#[test]
#[ignore = "requires a dedicated Firefox profile; run work/firefox-restore-fixture.ps1"]
fn native_firefox_invalid_normal_restore() {
    let run = || -> Result<(), String> {
        let required = |name| std::env::var(name).map_err(|_| format!("Missing {name}"));
        let pid = required("SBMS_TEST_FIREFOX_PID")?
            .parse::<u32>()
            .map_err(|_| "Invalid dedicated Firefox PID")?;
        let hwnd = HWND(
            required("SBMS_TEST_FIREFOX_HWND")?
                .parse::<usize>()
                .map_err(|_| "Invalid dedicated Firefox HWND")?
                as *mut std::ffi::c_void,
        );
        let result_dir = PathBuf::from(required("SBMS_TEST_RESULT_DIR")?);
        std::fs::create_dir_all(&result_dir).map_err(|e| e.to_string())?;
        observe(hwnd, pid)?;
        let displays = crate::display::active_displays().map_err(|e| e.to_string())?;
        let target = displays
            .iter()
            .find(|d| !d.virtual_display && d.primary)
            .ok_or("Physical primary display missing")?;
        let topology = |items: &[Display]| {
            items
                .iter()
                .map(|d| {
                    json!({
                        "id":d.id, "rect":rect_json(d.rect), "primary":d.primary,
                        "refresh_numerator":d.refresh_numerator,
                        "refresh_denominator":d.refresh_denominator,
                        "rotation":format!("{:?}",d.rotation),
                        "virtual":d.virtual_display,
                    })
                })
                .collect::<Vec<_>>()
        };
        let original_topology = topology(&displays);
        let mut records = Vec::new();
        for case in [
            "invalid-ordinary",
            "invalid-ordinary-park",
            "minimize-transition",
            "existing-monitor-cross-dpi",
        ] {
            let mut record = json!({"case":case});
            let result = (|| -> Result<(), String> {
                record["prepared"] = prepare(hwnd, pid, target, 1000, 700, false)?;
                let mut entry =
                    snapshot_window(hwnd, monitor_for_rect(target.rect, "fixture")?, 0)?
                        .ok_or("Dedicated Firefox was not eligible")?;
                // Exercise the explicit invalid-layout fallback independently of
                // a previously captured valid layout in the production lifecycle.
                entry.layout = None;
                let expected_dpi = destination_dpi(&entry, target.rect)?;
                record["expected_dpi"] = json!(expected_dpi);
                let started = Instant::now();
                match case {
                    "invalid-ordinary" | "invalid-ordinary-park" => {
                        // SetWindowPlacement may normalize an offscreen rectangle.
                        // SetWindowPos injects the actual invalid ordinary state.
                        unsafe {
                            SetWindowPos(
                                hwnd,
                                None,
                                -32000,
                                -32000,
                                1000,
                                700,
                                SWP_ASYNCWINDOWPOS
                                    | SWP_NOACTIVATE
                                    | SWP_NOZORDER
                                    | SWP_NOOWNERZORDER,
                            )
                        }
                        .map_err(|e| e.to_string())?;
                        record["injected"] =
                            fixture_wait(hwnd, pid, "invalid ordinary coordinates", |s| {
                                ordinary(s)
                                    && s["outer"]["left"].as_i64().is_some_and(|x| x <= -30000)
                                    && s["normal"]["left"].as_i64().is_some_and(|x| x <= -30000)
                            })?;
                        if case == "invalid-ordinary-park" {
                            fixture_minimize(&entry)?;
                            record["parked"] = observe(hwnd, pid)?;
                        }
                    }
                    "minimize-transition" => {
                        unsafe { ShowWindowAsync(hwnd, SW_MINIMIZE) }
                            .ok()
                            .map_err(|e| e.to_string())?;
                        // Intentionally do not wait for IsIconic before restoring.
                        record["injected"] = observe(hwnd, pid)?;
                    }
                    "existing-monitor-cross-dpi" => {
                        let source = displays.iter().filter(|d| !d.virtual_display).find(|d| {
                            d.id != target.id
                                && destination_dpi(&entry, d.rect)
                                    .is_ok_and(|dpi| dpi != expected_dpi)
                        });
                        let Some(source) = source else {
                            record["skipped"] =
                                json!("No existing physical display with a different DPI");
                            return Ok(());
                        };
                        restore_entry(&entry, source.rect)?;
                        record["source"] = observe(hwnd, pid)?;
                        record["source_id"] = json!(source.id);
                        fixture_minimize(&entry)?;
                        record["parked"] = observe(hwnd, pid)?;
                    }
                    _ => unreachable!(),
                }
                restore_entry(&entry, target.rect)?;
                let state = verify_default(hwnd, pid, target)?;
                if state["dpi"] != expected_dpi || state["visible"] != true {
                    return Err(format!(
                        "Firefox was not visible at the destination DPI: {state}"
                    ));
                }
                record["restored"] = state;
                record["elapsed_ms"] = json!(started.elapsed().as_millis());
                Ok(())
            })();
            record["ok"] = json!(result.is_ok());
            record["error"] = json!(result.as_ref().err());
            if result.is_err() {
                record["failure_state"] = observe(hwnd, pid).unwrap_or_else(|e| json!({"error":e}));
            }
            println!("{record}");
            records.push(record);
        }
        let current = crate::display::active_displays().map_err(|e| e.to_string())?;
        let final_topology = topology(&current);
        let topology_unchanged = original_topology == final_topology;
        let passed = topology_unchanged && records.iter().all(|r| r["ok"] == true);
        std::fs::write(
            result_dir.join("firefox-invalid-normal-results.json"),
            serde_json::to_vec_pretty(&json!({
                "ok":passed, "cases":records, "topology_unchanged":topology_unchanged,
                "topology_before":original_topology, "topology_after":final_topology,
            }))
            .map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())?;
        if passed {
            Ok(())
        } else {
            Err("Dedicated Firefox restoration fixture failed; see JSON evidence".into())
        }
    };
    let previous =
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let result = run();
    unsafe { SetThreadDpiAwarenessContext(previous) };
    result.unwrap();
}

#[test]
#[ignore = "requires the dedicated Firefox run-preserve topology fixture"]
fn native_firefox_topology_dpi_restore() {
    let run = || -> Result<(), String> {
        let required = |name| std::env::var(name).map_err(|_| format!("Missing {name}"));
        let pid = required("SBMS_TEST_FIREFOX_PID")?
            .parse::<u32>()
            .map_err(|_| "Invalid PID")?;
        let hwnd = HWND(
            required("SBMS_TEST_FIREFOX_HWND")?
                .parse::<usize>()
                .map_err(|_| "Invalid HWND")? as *mut std::ffi::c_void,
        );
        let result_dir = PathBuf::from(required("SBMS_TEST_RESULT_DIR")?);
        std::fs::create_dir_all(&result_dir).map_err(|e| e.to_string())?;
        let mut record = json!({"case":"same-monitor-stale-dpi", "precondition_met":false});
        let result = (|| -> Result<(), String> {
            let source_id = required("SBMS_TEST_SOURCE_ID")?;
            let target_id = required("SBMS_TEST_TARGET_ID")?;
            let displays = crate::display::active_displays().map_err(|e| e.to_string())?;
            let source = displays
                .iter()
                .find(|d| d.id.eq_ignore_ascii_case(&source_id) && d.virtual_display)
                .ok_or("Virtual source missing")?;
            let target = displays
                .iter()
                .find(|d| d.id.eq_ignore_ascii_case(&target_id) && !d.virtual_display)
                .ok_or("Physical target missing")?;
            // Never prepare/move this HWND here: the first observation must retain
            // the state produced by the launcher's virtual-topology transition.
            let before = observe(hwnd, pid)?;
            record["before"] = before.clone();
            let monitor = monitor_for_rect(source.rect, "virtual topology source")?;
            if !ordinary(&before)
                || before["monitor"] != monitor.0 as usize
                || before["visible"] != true
            {
                return Err("Fixture precondition not reproduced: Firefox is not ordinary on the new virtual monitor".into());
            }
            let mut entry =
                snapshot_window(hwnd, monitor, 0)?.ok_or("Firefox snapshot unavailable")?;
            entry.layout = None;
            let expected = destination_dpi(&entry, source.rect)?;
            record["expected_source_dpi"] = json!(expected);
            if before["dpi"] == expected {
                return Err("Fixture precondition not reproduced: Firefox DPI already matches its virtual monitor".into());
            }
            record["precondition_met"] = json!(true);
            let started = Instant::now();
            restore_entry(&entry, source.rect)?;
            let restored = verify_default(hwnd, pid, source)?;
            if restored["dpi"] != expected || restored["visible"] != true {
                return Err(format!(
                    "Virtual restoration DPI/visibility mismatch: {restored}"
                ));
            }
            record["virtual_restored"] = restored;
            fixture_minimize(&entry)?;
            record["parked"] = observe(hwnd, pid)?;
            restore_entry(&entry, target.rect)?;
            let physical = verify_default(hwnd, pid, target)?;
            if physical["dpi"] != destination_dpi(&entry, target.rect)?
                || physical["visible"] != true
            {
                return Err(format!(
                    "Physical restoration DPI/visibility mismatch: {physical}"
                ));
            }
            record["physical_restored"] = physical;
            record["elapsed_ms"] = json!(started.elapsed().as_millis());
            Ok(())
        })();
        record["ok"] = json!(result.is_ok());
        record["error"] = json!(result.as_ref().err());
        if result.is_err() {
            record["failure_state"] = observe(hwnd, pid).unwrap_or_else(|e| json!({"error":e}));
        }
        println!("{record}");
        std::fs::write(
            result_dir.join("firefox-topology-dpi-results.json"),
            serde_json::to_vec_pretty(&record).map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())?;
        result
    };
    let previous =
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let result = run();
    unsafe { SetThreadDpiAwarenessContext(previous) };
    result.unwrap();
}
