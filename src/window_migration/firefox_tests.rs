//! Opt-in real Firefox coverage. The launcher owns the browser profile and topology.
use super::*;
use serde_json::{Value, json};
use std::path::{Path, PathBuf};
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetThreadDpiAwarenessContext,
};
use windows::Win32::UI::WindowsAndMessaging::{SW_MAXIMIZE, SW_MINIMIZE};

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
        let source_monitor = monitor_for_rect(source.rect, "virtual test source")?.0 as usize;
        let expected_source_dpi = std::env::var("SBMS_TEST_SOURCE_DPI")
            .ok()
            .map(|value| value.parse::<u32>().map_err(|_| "Invalid source DPI"))
            .transpose()?;
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
                    let mut entry = snapshot_window(hwnd, target_monitor, 0)?
                        .ok_or("Prepared Firefox was not eligible for migration")?;
                    entry.normal_anchor = Some(normal_anchor(
                        entry.placement.rcNormalPosition,
                        entry.tool_window,
                        &placement_displays()?,
                    )?);
                    let saved_outer = entry.outer_rect;
                    let saved_normal = entry.placement.rcNormalPosition;
                    record["expected_saved_outer"] = rect_json(saved_outer);
                    record["expected_saved_normal"] = rect_json(saved_normal);
                    let mut expected_final_outer = saved_outer;
                    let mut expected_final_normal = saved_normal;
                    let mut expected_final_monitor = target_monitor.0 as usize;
                    let mut migration = WindowMigration {
                        migration_id: next_migration_id(),
                        entries: Arc::new(Mutex::new(vec![entry])),
                        stop_scanner: Arc::new(AtomicBool::new(true)),
                        scanner: None,
                        scanner_errors: Vec::new(),
                        restore_on_drop: true,
                        target_rect: target.rect,
                    };
                    let started = Instant::now();
                    {
                        let mut entries = migration.entries.lock().map_err(|e| e.to_string())?;
                        move_entry(&mut entries[0], target.rect, source.rect)?;
                    }
                    record["move_ms"] = json!(started.elapsed().as_millis());
                    record["moved"] =
                        fixture_wait(hwnd, pid, "production migration result", |s| {
                            s["monitor"] == source_monitor
                                && s["iconic"] == false
                                && s["zoomed"] == maximized
                                && expected_source_dpi.is_none_or(|dpi| s["dpi"] == dpi)
                        })?;
                    if minimize {
                        unsafe { ShowWindowAsync(hwnd, SW_MINIMIZE) }
                            .ok()
                            .map_err(|e| e.to_string())?;
                        record["minimized"] =
                            fixture_wait(hwnd, pid, "runtime minimize", |s| s["iconic"] == true)?;
                    }
                    let restored_at = Instant::now();
                    migration.restore()?;
                    record["restore_ms"] = json!(restored_at.elapsed().as_millis());
                    record["restored"] = observe(hwnd, pid)?;
                    let restored = &record["restored"];
                    if restored["monitor"] != expected_final_monitor
                        || restored["iconic"] != false
                        || restored["zoomed"] != maximized
                        || restored["normal"] != rect_json(saved_normal)
                        || (!maximized && restored["outer"] != rect_json(saved_outer))
                    {
                        return Err(format!("Pre-topology restore mismatch: {restored}"));
                    }
                    if minimize {
                        wait_topology_restore(&topology_dir)?;
                        let current =
                            crate::display::active_displays().map_err(|e| e.to_string())?;
                        let restored_target = current
                            .iter()
                            .find(|d| d.id.eq_ignore_ascii_case(&target_id))
                            .ok_or("Physical target missing after topology restore")?;
                        migration.reconcile_after_topology_change(Some(restored_target))?;
                        record["reconciled"] = observe(hwnd, pid)?;
                        let dx = restored_target.rect.left - target.rect.left;
                        let dy = restored_target.rect.top - target.rect.top;
                        record["topology_translation"] = json!({"dx":dx,"dy":dy});
                        let shifted_outer = RECT {
                            left: saved_outer.left + dx,
                            top: saved_outer.top + dy,
                            right: saved_outer.right + dx,
                            bottom: saved_outer.bottom + dy,
                        };
                        record["expected_shifted_outer"] = rect_json(shifted_outer);
                        expected_final_outer = shifted_outer;
                        expected_final_normal = RECT {
                            left: saved_normal.left + dx,
                            top: saved_normal.top + dy,
                            right: saved_normal.right + dx,
                            bottom: saved_normal.bottom + dy,
                        };
                        expected_final_monitor =
                            monitor_for_rect(restored_target.rect, "restored test target")?.0
                                as usize;
                        if record["reconciled"]["outer"] != rect_json(shifted_outer) {
                            return Err(format!(
                                "Topology reconciliation missed expected translation: {}",
                                record["reconciled"]
                            ));
                        }
                    } else {
                        migration.reconcile_after_topology_change(Some(target))?;
                        record["reconciled"] = observe(hwnd, pid)?;
                    }
                    let state = observe(hwnd, pid)?;
                    if state["monitor"] != expected_final_monitor
                        || state["iconic"] != false
                        || state["zoomed"] != maximized
                        || state["normal"] != rect_json(expected_final_normal)
                        || (!maximized && state["outer"] != rect_json(expected_final_outer))
                    {
                        return Err(format!("Final restored state mismatch: {state}"));
                    }
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
