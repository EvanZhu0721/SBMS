//! Real controller recovery probe; opt in with SBMS_RECOVERY_LIVE_OUTPUT.
//! Creates a temporary mirror and moves only its virtual output. Does not inject input.
use crate::{
    config::RenderingOptions,
    controller::{Controller, ControllerEvent, ControllerSender},
    display::{Display, active_displays, restore_display_topology},
    geometry::Rotation,
    mapping::{MappingGroupRequest, MappingPlan, MappingRoute},
    session_gate::VirtualMode,
};
use serde_json::{Value, json};
use std::{
    error::Error,
    fs,
    path::{Path, PathBuf},
    sync::mpsc,
    time::{Duration, Instant},
};
use windows::{
    Win32::{
        Graphics::Gdi::{
            CDS_FULLSCREEN, ChangeDisplaySettingsExW, DEVMODEW, DISP_CHANGE_SUCCESSFUL,
            DM_POSITION, ENUM_CURRENT_SETTINGS, ENUM_DISPLAY_SETTINGS_FLAGS,
            EnumDisplaySettingsExW,
        },
        UI::HiDpi::{DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetProcessDpiAwarenessContext},
    },
    core::PCWSTR,
};

type Result<T> = std::result::Result<T, Box<dyn Error>>;
const EVENT_TIMEOUT: Duration = Duration::from_secs(45);

struct Probe {
    controller: Option<Controller>,
    sender: ControllerSender,
    events: mpsc::Receiver<(u128, ControllerEvent)>,
    start: Instant,
    records: Vec<Value>,
    cancel_path: PathBuf,
}

impl Probe {
    fn new(output: &Path) -> Self {
        let start = Instant::now();
        let (tx, events) = mpsc::channel();
        let controller = Controller::spawn(move |event| {
            let _ = tx.send((start.elapsed().as_millis(), event));
        });
        let sender = controller.sender();
        Self {
            controller: Some(controller),
            sender,
            events,
            start,
            records: Vec::new(),
            cancel_path: output.join("cancel.signal"),
        }
    }

    fn command(&mut self, name: &str) -> u128 {
        let ms = self.start.elapsed().as_millis();
        self.records.push(json!({"ms": ms, "command": name}));
        ms
    }

    fn next(&mut self, deadline: Instant) -> Result<(u128, ControllerEvent)> {
        loop {
            if self.cancel_path.exists() {
                return Err("live probe cancelled by launcher".into());
            }
            let remaining = deadline.saturating_duration_since(Instant::now());
            match self
                .events
                .recv_timeout(remaining.min(Duration::from_millis(200)))
            {
                Ok((ms, event)) => return self.record(ms, event),
                Err(mpsc::RecvTimeoutError::Timeout) if Instant::now() < deadline => {}
                Err(error) => return Err(error.into()),
            }
        }
    }

    fn record(&mut self, ms: u128, event: ControllerEvent) -> Result<(u128, ControllerEvent)> {
        let value = match &event {
            ControllerEvent::State {
                state,
                detail,
                error,
                running,
                busy,
                ..
            } => {
                json!({"ms":ms,"state":state,"detail":detail,"error":error,"running":running,"busy":busy})
            }
            ControllerEvent::Displays(displays) => {
                json!({"ms":ms,"event":"Displays","count":displays.len()})
            }
            ControllerEvent::GroupReady(group) => {
                json!({"ms":ms,"event":"GroupReady","id":group.id})
            }
            ControllerEvent::Fps { id, fps } => json!({"ms":ms,"event":"Fps","id":id,"fps":fps}),
            ControllerEvent::Sunshine { id, error, .. } => {
                json!({"ms":ms,"event":"Sunshine","id":id,"error":error})
            }
        };
        self.records.push(value);
        if let ControllerEvent::State {
            state: "Degraded",
            error,
            ..
        } = &event
        {
            return Err(format!("controller recovery failed: {error}").into());
        }
        Ok((ms, event))
    }

    fn state(&mut self, wanted: &str) -> Result<u128> {
        let deadline = Instant::now() + EVENT_TIMEOUT;
        loop {
            let (ms, event) = self.next(deadline)?;
            if let ControllerEvent::State { state, error, .. } = event {
                if state == wanted {
                    if !error.is_empty() {
                        return Err(error.into());
                    }
                    return Ok(ms);
                }
                if state == "Stopped" && wanted != "Stopped" {
                    return Err(format!("stopped while waiting for {wanted}: {error}").into());
                }
            }
        }
    }

    fn shutdown(&mut self) {
        self.sender.stop();
        if let Some(controller) = self.controller.take() {
            controller.shutdown();
        }
    }
}

impl Drop for Probe {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn snapshot(displays: &[Display]) -> Value {
    Value::Array(
        displays
            .iter()
            .map(|d| {
                json!({
                    "id":d.id,"device":d.device_name,"virtual":d.virtual_display,
                    "rect":[d.rect.left,d.rect.top,d.rect.right,d.rect.bottom],
                    "rotation":d.rotation,"primary":d.primary,
                    "refresh":[d.refresh_numerator,d.refresh_denominator]
                })
            })
            .collect(),
    )
}

fn matches_original(original: &[Display], current: &[Display]) -> bool {
    current.len() == original.len()
        && original.iter().all(|before| {
            current.iter().any(|after| {
                before.id.eq_ignore_ascii_case(&after.id)
                    && before.rect == after.rect
                    && before.rotation == after.rotation
                    && before.primary == after.primary
                    && before.refresh_numerator == after.refresh_numerator
                    && before.refresh_denominator == after.refresh_denominator
                    && !after.virtual_display
            })
        })
}

fn shift_virtual() -> Result<Value> {
    let displays = active_displays()?;
    let source = displays
        .iter()
        .find(|d| d.virtual_display)
        .ok_or("virtual source missing")?;
    let name: Vec<_> = source.device_name.encode_utf16().chain(Some(0)).collect();
    let mut mode = DEVMODEW {
        dmSize: size_of::<DEVMODEW>() as u16,
        ..Default::default()
    };
    if !unsafe {
        EnumDisplaySettingsExW(
            PCWSTR(name.as_ptr()),
            ENUM_CURRENT_SETTINGS,
            &mut mode,
            ENUM_DISPLAY_SETTINGS_FLAGS::default(),
        )
    }
    .as_bool()
    {
        return Err("cannot read virtual display mode".into());
    }
    mode.dmFields = DM_POSITION;
    unsafe {
        mode.Anonymous1.Anonymous2.dmPosition.y += 8;
    }
    let status = unsafe {
        ChangeDisplaySettingsExW(
            PCWSTR(name.as_ptr()),
            Some(&mode),
            None,
            CDS_FULLSCREEN,
            None,
        )
    };
    if status != DISP_CHANGE_SUCCESSFUL {
        return Err(format!("virtual position change failed: {}", status.0).into());
    }
    Ok(snapshot(&active_displays()?))
}

fn live_plan(target: &Display) -> Result<MappingPlan> {
    let plan = match std::env::var_os("SBMS_RECOVERY_LIVE_PLAN") {
        Some(path) => {
            let plan: MappingPlan = serde_json::from_slice(&fs::read(path)?)?;
            plan.validate()?;
            plan
        }
        None => MappingPlan::new(vec![MappingGroupRequest {
            id: 0,
            rendering: RenderingOptions::default(),
            mode: VirtualMode::from_millihz(1280, 720, 60_000)?,
            rotation: Rotation::Deg0,
            route: MappingRoute::Mirror {
                target: target.id.clone(),
            },
        }])?,
    };
    Ok(plan)
}

fn exercise(probe: &mut Probe, target: &Display, rounds: usize) -> Result<Value> {
    let plan = live_plan(target)?;
    probe.command("start");
    probe.sender.start(plan);
    probe.state("Running")?;
    let mut recoveries = Vec::new();
    for _ in 0..rounds {
        let changed = probe.command("shift_virtual");
        let topology = shift_virtual()?;
        // No observed Reconnecting means the experiment did not exercise recovery.
        let reconnecting = probe.state("Reconnecting")?;
        let refresh_sent = probe.command("refresh_during_recovery");
        probe.sender.refresh();
        let deadline = Instant::now() + EVENT_TIMEOUT;
        let mut refreshed = None;
        let mut recovered = None;
        while refreshed.is_none() || recovered.is_none() {
            let (ms, event) = probe.next(deadline)?;
            match event {
                ControllerEvent::Displays(_) if ms >= refresh_sent => refreshed = Some(ms),
                ControllerEvent::State {
                    state: "Running", ..
                } => recovered = Some(ms),
                ControllerEvent::State {
                    state: "Stopped",
                    error,
                    ..
                } => return Err(format!("unexpected stop: {error}").into()),
                _ => {}
            }
        }
        recoveries.push(json!({"topology":topology,"shift_ms":changed,
            "reconnecting_ms":reconnecting,"running_ms":recovered,
            "recovery_to_first_frame_ms":recovered.unwrap()-reconnecting,
            "refresh_response_ms":refreshed.unwrap()-refresh_sent,
            "refresh_before_recovered":refreshed.unwrap()<recovered.unwrap()}));
    }
    probe.command("shift_virtual_for_restart");
    shift_virtual()?;
    probe.state("Reconnecting")?;
    let restart_sent = probe.command("restart_during_recovery");
    probe.sender.restart_mapping();
    let restart_accepted = probe.state("Stopping")?;
    let deadline = Instant::now() + EVENT_TIMEOUT;
    loop {
        let (_, event) = probe.next(deadline)?;
        if let ControllerEvent::State { state, error, .. } = event {
            if state == "Running" {
                return Err("old recovery returned Running before the new Start".into());
            }
            if !error.is_empty() {
                return Err(error.into());
            }
            if state == "Starting" {
                break;
            }
        }
    }
    let restart_completed = probe.state("Running")?;

    probe.command("shift_virtual_for_stop");
    shift_virtual()?;
    probe.state("Reconnecting")?;
    let sent = probe.command("stop_during_recovery");
    probe.sender.stop();
    let accepted = probe.state("Stopping")?;
    let completed = probe.state("Stopped")?;
    Ok(json!({"recoveries":recoveries,
        "restart_response_ms":restart_accepted-restart_sent,
        "restart_complete_ms":restart_completed-restart_sent,
        "stop_response_ms":accepted-sent,
        "stop_complete_ms":completed-sent}))
}

// The external operator performs real S3 and unlocks the desktop. This probe never
// synthesizes a power event or calls a power-management API.
fn exercise_sleep(probe: &mut Probe, target: &Display, output: &Path) -> Result<Value> {
    probe.command("start");
    probe.sender.start(live_plan(target)?);
    let initial_running = probe.state("Running")?;
    let log_path = crate::diagnostics::default_log_path()?;
    let initial_log = fs::read_to_string(&log_path)?;
    let baseline = initial_log.lines().count();
    let session_id = initial_log
        .lines()
        .filter_map(|line| serde_json::from_str::<crate::diagnostics::Record>(line).ok())
        .rfind(|record| {
            record.module == "controller"
                && record.stage == "start"
                && record
                    .mapping_session_id
                    .starts_with(&format!("{}-", std::process::id()))
        })
        .ok_or("current controller session log missing")?
        .mapping_session_id;
    fs::write(
        output.join("ready.signal"),
        format!("Running at {initial_running} ms\n"),
    )?;
    let deadline = Instant::now() + Duration::from_secs(900);
    let mut suspended = None;
    let mut resumed = None;
    let mut unlocked = None;
    let mut first_frame = None;
    let mut running_after_resume = None;
    loop {
        if output.join("cancel.signal").exists() {
            return Err("real sleep probe cancelled by operator".into());
        }
        if Instant::now() >= deadline {
            return Err(
                "timed out waiting for real suspend, resume, unlock and first frame".into(),
            );
        }
        // An isolated LOCALAPPDATA keeps this short and excludes other processes.
        for (index, line) in fs::read_to_string(&log_path)?
            .lines()
            .enumerate()
            .skip(baseline)
        {
            let Ok(record) = serde_json::from_str::<crate::diagnostics::Record>(line) else {
                continue; // The writer may currently be finishing the last JSONL line.
            };
            if record.module == "desktop-monitor" && record.message.starts_with("Suspended:") {
                suspended.get_or_insert(index);
            }
            if record.module == "desktop-monitor"
                && record.message.starts_with("Resumed:")
                && suspended.is_some_and(|prior| prior < index)
            {
                resumed.get_or_insert(index);
            }
            if record.module == "desktop-monitor"
                && record.message.starts_with("Unlocked:")
                && suspended.is_some_and(|prior| prior < index)
            {
                unlocked.get_or_insert(index);
            }
            if record.mapping_session_id == session_id
                && record.stage == "renderer-recovered"
                && record.message.contains("first frame presented")
                && unlocked.is_some_and(|prior| prior < index)
            {
                first_frame.get_or_insert(index);
            }
        }
        match probe.events.recv_timeout(Duration::from_millis(200)) {
            Ok((ms, event)) => {
                let (_, event) = probe.record(ms, event)?;
                match event {
                    ControllerEvent::State {
                        state: "Running",
                        error,
                        ..
                    } if ms > initial_running => {
                        if !error.is_empty() {
                            return Err(error.into());
                        }
                        running_after_resume = Some(ms);
                    }
                    ControllerEvent::State {
                        state: "Stopped",
                        error,
                        ..
                    } => {
                        return Err(
                            format!("unexpected stop during real sleep probe: {error}").into()
                        );
                    }
                    ControllerEvent::State { .. } => running_after_resume = None,
                    _ => {}
                }
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
            Err(error) => return Err(error.into()),
        }
        // The operator creates this only after collecting OS sleep/wake evidence.
        // A marker is a handshake, not independent proof of S3.
        if first_frame.is_some()
            && running_after_resume.is_some()
            && output.join("power-resumed.signal").exists()
        {
            break;
        }
    }
    let evidence = json!({"initial_running_ms":initial_running,
        "suspended_log_line":suspended.map(|line| line+1),
        "resumed_log_line":resumed.map(|line| line+1),
        "unlocked_log_line":unlocked.map(|line| line+1),
        "first_frame_log_line":first_frame.map(|line| line+1),
        "recovered_running_ms":running_after_resume,"log_path":log_path,
        "os_evidence_required":"external Kernel-Power 42 / Power-Troubleshooter 1 records",
        "power_resumed_handshake":true,"mapping_session_id":session_id});
    fs::write(
        output.join("resumed.signal"),
        serde_json::to_vec_pretty(&evidence)?,
    )?;
    let sent = probe.command("stop_after_real_resume");
    probe.sender.stop();
    let stopped = probe.state("Stopped")?;
    Ok(json!({"real_power_events":evidence,"stop_complete_ms":stopped-sent}))
}

fn run(real_sleep: bool) -> Result<()> {
    unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) }?;
    let output = PathBuf::from(std::env::var("SBMS_RECOVERY_LIVE_OUTPUT")?);
    let target_id = std::env::var("SBMS_RECOVERY_LIVE_TARGET").ok();
    let rounds = std::env::var("SBMS_RECOVERY_LIVE_ROUNDS")
        .ok()
        .map(|s| s.parse::<usize>())
        .transpose()?
        .unwrap_or(3);
    if rounds == 0 {
        return Err("ROUNDS must be positive".into());
    }
    fs::create_dir_all(&output)?;
    if output.join("original.json").exists() {
        return Err("use a fresh output directory".into());
    }
    let original = active_displays()?;
    if original.iter().any(|d| d.virtual_display) {
        return Err("an SBMS virtual display is already active".into());
    }
    let target = original
        .iter()
        .find(|d| {
            target_id
                .as_ref()
                .map_or(!d.primary, |id| d.id.eq_ignore_ascii_case(id))
        })
        .ok_or("no matching physical display; specify TARGET_ID if there is no secondary")?;
    fs::write(
        output.join("original.json"),
        serde_json::to_vec_pretty(&snapshot(&original))?,
    )?;
    let _ = crate::diagnostics::init();
    let mut probe = Probe::new(&output);
    let result = if real_sleep {
        exercise_sleep(&mut probe, target, &output)
    } else {
        exercise(&mut probe, target, rounds)
    };
    probe.shutdown();
    let controller_final = active_displays();
    let controller_restored = controller_final
        .as_ref()
        .is_ok_and(|current| matches_original(&original, current));
    // Emergency repair must not turn a failed product restoration into a passed test.
    let cleanup = if controller_restored {
        Ok(())
    } else {
        restore_display_topology(&original).map_err(|e| e.to_string())
    };
    let final_displays = active_displays();
    let restored = final_displays
        .as_ref()
        .is_ok_and(|current| matches_original(&original, current));
    let report = json!({"result":result.as_ref().ok(),"error":result.as_ref().err().map(ToString::to_string),
        "events":probe.records,"cleanup_error":cleanup.err(),"restored_verified":restored,
        "controller_restored_verified":controller_restored,
        "controller_final":controller_final.as_ref().ok().map(|d| snapshot(d)),
        "final":final_displays.as_ref().ok().map(|d| snapshot(d)),
        "final_read_error":final_displays.as_ref().err().map(ToString::to_string)});
    fs::write(
        output.join("results.json"),
        serde_json::to_vec_pretty(&report)?,
    )?;
    println!("{}", serde_json::to_string_pretty(&report)?);
    result?;
    if !controller_restored || !restored {
        return Err("controller did not restore the original physical topology; emergency repair recorded separately".into());
    }
    Ok(())
}

#[test]
#[ignore = "creates a real virtual mirror and changes its desktop position; requires an idle matching driver"]
fn live_recovery_and_stop_remain_responsive() {
    run(false).expect("real controller recovery probe failed; inspect results.json");
}

#[test]
#[ignore = "creates a real mirror and waits for an externally initiated S3 sleep and manual wake/unlock"]
fn live_real_sleep_resume_and_stop() {
    run(true).expect("real S3 controller probe failed; inspect results.json");
}
