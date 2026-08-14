use std::error::Error;
use std::process::Command;
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use crate::config::{
    AppConfig, ConfigProfileStore, DisplayOverrideStore, DisplayOverrides, ProfileRevision,
    ReferenceSource,
};
use crate::control::{TrayInstance, listen_for_config_reload, listen_for_shutdown};
use crate::controller::{Controller, ControllerEvent, DisplayOption};
use crate::diagnostics::{self, Level};
use crate::geometry::{
    AspectRatio, DisplayGeometry, PhysicalMeasurement, PixelSize, Rotation, SizingRequest,
    SizingResult, SizingStrategy,
};
use crate::limits::{
    MAX_OUTPUTS, MAX_VIRTUAL_DIMENSION, MILLIMETERS_PER_INCH, valid_physical_millimeters,
};
use crate::mapping::MappingPlan;
use crate::network::preferred_lan_ipv4;
use crate::session_gate::VirtualMode;
use crate::win32_flyout;
use crate::win32_tray::{NativeTray, TrayAction, TrayHandle};
use slint::winit_030::winit::event::WindowEvent;
use slint::winit_030::winit::platform::windows::{CornerPreference, WindowAttributesExtWindows};
use slint::winit_030::{EventResult as WinitEventResult, WinitWindowAccessor};
use slint::{ComponentHandle, Model, ModelRc, SharedString, VecModel};
slint::include_modules!();

const STREAM_ONLY_ID: &str = "__stream_only__";
const DISPLAY_SETTINGS_URI: &str = "ms-settings:display";
const PROJECT_HOME_URL: &str = "https://github.com/EvanZhu0721/SBMS";
static INTRO_GENERATION: AtomicU64 = AtomicU64::new(0);

mod diagnostic_view;
mod geometry_view;
mod handlers;
mod projection;
mod state;

#[cfg(test)]
use diagnostic_view::format_recent_diagnostics;
use diagnostic_view::{open_log_folder, populate_diagnostics};
use geometry_view::*;
use handlers::{
    HandlerState, register_diagnostics_handlers, register_geometry_handlers,
    register_group_handlers, register_lifecycle_handlers, register_screen_size_handlers,
    register_stream_handlers,
};
use projection::{hydrate_group, project_group_telemetry, snapshot_group, update_tab_projection};
use state::{
    GroupDraft, LoadedGroups, SunshineState, ValidatedGroupDraft, ValidatedStreamFields,
    aspect_ratio_from_index, aspect_ratio_option_index, parse_screen_diagonal,
    parse_stream_refresh_millihz, parse_u32, rotation_from_index, rotation_index, rotation_label,
};

type ConfigStateRefs<'a> = (
    &'a Arc<Mutex<Vec<GroupDraft>>>,
    &'a Arc<Mutex<usize>>,
    &'a Arc<Mutex<ProfileRevision>>,
    &'a Arc<AtomicBool>,
);

pub fn run_host(_instance: TrayInstance, open_on_start: bool) -> Result<(), Box<dyn Error>> {
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .with_winit_window_attributes_hook(|attributes| {
            attributes
                .with_transparent(false)
                .with_skip_taskbar(true)
                .with_corner_preference(CornerPreference::Round)
        })
        .select()?;

    listen_for_shutdown(|| {
        diagnostics::log(
            Level::Info,
            "tray",
            "external-shutdown",
            None,
            "shutdown signal received",
        );
        let _ = slint::invoke_from_event_loop(|| {
            let _ = slint::quit_event_loop();
        });
    })?;
    let flyout = QuickAccess::new()?;
    let intro_disabled = Arc::new(AtomicBool::new(
        ConfigProfileStore::default_store()
            .and_then(|store| store.list())
            .map(|profiles| profiles.launch_intro_disabled)
            .unwrap_or(false),
    ));
    let dismiss_flyout = flyout.as_weak();
    flyout.window().on_winit_window_event(move |_, event| {
        if matches!(event, WindowEvent::Focused(false)) {
            let flyout = dismiss_flyout.clone();
            slint::Timer::single_shot(Duration::from_millis(50), move || {
                if let Some(flyout) = flyout.upgrade()
                    && flyout.window().is_visible()
                    && flyout
                        .window()
                        .with_winit_window(|window| !window.has_focus())
                        .unwrap_or(false)
                {
                    diagnostics::log(
                        Level::Debug,
                        "ui",
                        "flyout-dismiss",
                        None,
                        "window lost focus",
                    );
                    let _ = flyout.hide();
                }
            });
        }
        WinitEventResult::Propagate
    });
    flyout.set_max_group_count(MAX_OUTPUTS as i32);
    let loaded = load_group_config(&flyout);
    let override_store = DisplayOverrideStore::default_store()?;
    let override_outcome = override_store.load()?;
    let override_save_blocked = override_outcome.warning.is_some();
    if let Some(warning) = &override_outcome.warning {
        flyout.set_screen_size_error(warning.as_str().into());
    }
    flyout.set_screen_size_save_blocked(override_save_blocked);
    let display_overrides = Arc::new(Mutex::new(override_outcome.overrides));
    let displays = Arc::new(Mutex::new(Vec::<DisplayOption>::new()));
    let groups = Arc::new(Mutex::new(loaded.groups));
    let active_group = Arc::new(Mutex::new(loaded.active));
    let profile_revision = Arc::new(Mutex::new(loaded.profile));
    update_tab_projection(
        &flyout,
        &groups.lock().expect("group drafts poisoned"),
        loaded.active,
    );

    let flyout_weak = flyout.as_weak();
    let tray_intro_disabled = intro_disabled.clone();
    let tray = NativeTray::new(move |action| {
        let flyout_weak = flyout_weak.clone();
        let intro_disabled = tray_intro_disabled.clone();
        if let Err(error) = slint::invoke_from_event_loop(move || match action {
            TrayAction::Toggle => {
                if let Some(flyout) = flyout_weak.upgrade() {
                    if flyout.window().is_visible() {
                        let _ = flyout.hide();
                    } else {
                        let play_intro = !intro_disabled.load(Ordering::Acquire);
                        show_flyout(&flyout, play_intro);
                    }
                }
            }
            TrayAction::Open => {
                if let Some(flyout) = flyout_weak.upgrade() {
                    let play_intro = !intro_disabled.load(Ordering::Acquire);
                    show_flyout(&flyout, play_intro);
                }
            }
            TrayAction::Quit => {
                diagnostics::log(Level::Info, "tray", "action", None, "quit");
                let _ = slint::quit_event_loop();
            }
        }) {
            diagnostics::log(
                Level::Warn,
                "tray",
                "action-dispatch",
                None,
                error.to_string(),
            );
        }
    })?;
    let tray_handle = tray.handle();

    let ui = flyout.as_weak();
    let error_revision = Arc::new(AtomicU64::new(0));
    let pending_config_reload = Arc::new(AtomicBool::new(false));
    {
        let ui = flyout.as_weak();
        let displays = displays.clone();
        let groups = groups.clone();
        let active_group = active_group.clone();
        let profile_revision = profile_revision.clone();
        let pending = pending_config_reload.clone();
        let error_revision = error_revision.clone();
        listen_for_config_reload(move || {
            let ui = ui.clone();
            let displays = displays.clone();
            let groups = groups.clone();
            let active_group = active_group.clone();
            let profile_revision = profile_revision.clone();
            let pending = pending.clone();
            let error_revision = error_revision.clone();
            let _ = slint::invoke_from_event_loop(move || {
                let Some(ui) = ui.upgrade() else {
                    return;
                };
                if ui.get_running() || ui.get_busy() {
                    pending.store(true, Ordering::Release);
                    diagnostics::log(
                        Level::Info,
                        "config",
                        "reload-deferred",
                        None,
                        "config reload deferred until the active mapping stops",
                    );
                    return;
                }
                apply_config_reload(
                    &ui,
                    &displays,
                    &groups,
                    &active_group,
                    &profile_revision,
                    &error_revision,
                );
            });
        })?;
    }
    apply_config_reload(
        &flyout,
        &displays,
        &groups,
        &active_group,
        &profile_revision,
        &error_revision,
    );
    let event_error_revision = error_revision.clone();
    let event_displays = displays.clone();
    let event_groups = groups.clone();
    let event_active_group = active_group.clone();
    let event_display_overrides = display_overrides.clone();
    let event_profile_revision = profile_revision.clone();
    let event_pending_config_reload = pending_config_reload.clone();
    let controller = Controller::spawn(move |event| {
        let ui = ui.clone();
        let tray = tray_handle.clone();
        let error_revision = event_error_revision.clone();
        let displays = event_displays.clone();
        let groups = event_groups.clone();
        let active_group = event_active_group.clone();
        let display_overrides = event_display_overrides.clone();
        let profile_revision = event_profile_revision.clone();
        let pending_config_reload = event_pending_config_reload.clone();
        let _ = slint::invoke_from_event_loop(move || {
            if let Some(ui) = ui.upgrade() {
                apply_event(
                    &ui,
                    tray,
                    &error_revision,
                    (&displays, &display_overrides),
                    (
                        &groups,
                        &active_group,
                        &profile_revision,
                        &pending_config_reload,
                    ),
                    event,
                );
            }
        });
    });
    let sender = controller.sender();

    let handler_state = HandlerState::new(
        displays.clone(),
        groups.clone(),
        active_group.clone(),
        display_overrides.clone(),
        profile_revision.clone(),
    );
    register_lifecycle_handlers(&flyout, &sender, &handler_state, &error_revision);
    register_geometry_handlers(&flyout, &handler_state);
    register_stream_handlers(&flyout, &handler_state);
    register_screen_size_handlers(&flyout, &sender, &handler_state, &override_store);
    register_group_handlers(&flyout, &handler_state);
    register_diagnostics_handlers(&flyout);
    {
        let flyout_weak = flyout.as_weak();
        let intro_disabled = intro_disabled.clone();
        flyout.on_disable_launch_intro(move || {
            INTRO_GENERATION.fetch_add(1, Ordering::AcqRel);
            intro_disabled.store(true, Ordering::Release);
            let Some(ui) = flyout_weak.upgrade() else {
                return;
            };
            ui.set_intro_active(false);
            if let Err(error) = ConfigProfileStore::default_store()
                .and_then(|store| store.set_launch_intro_disabled(true))
            {
                diagnostics::log(
                    Level::Warn,
                    "ui",
                    "disable-launch-intro",
                    None,
                    error.to_string(),
                );
                ui.set_error_text(
                    format!("Couldn’t save the launch logo preference: {error}").into(),
                );
            }
        });
    }
    {
        let flyout_weak = flyout.as_weak();
        flyout.on_open_project_home(move || {
            if let Err(error) = open_project_home() {
                diagnostics::log(Level::Warn, "ui", "open-project-home", None, error.as_str());
                if let Some(ui) = flyout_weak.upgrade() {
                    ui.set_error_text(error.into());
                }
            }
        });
    }

    sender.refresh();
    if open_on_start {
        let play_intro = !intro_disabled.load(Ordering::Acquire);
        show_flyout(&flyout, play_intro);
    }
    diagnostics::log(
        Level::Info,
        "tray",
        "event-loop-start",
        None,
        "Slint event loop started",
    );
    // The tray host is a native Win32 window, so Slint cannot count it when deciding
    // whether the app is still alive. Only the explicit tray Exit action should quit.
    slint::run_event_loop_until_quit()?;
    diagnostics::log(
        Level::Info,
        "tray",
        "event-loop-stop",
        None,
        "Slint event loop stopped",
    );
    {
        let displays = displays.lock().expect("display metadata poisoned");
        let mut groups = groups.lock().expect("group drafts poisoned");
        let active = *active_group.lock().expect("active group poisoned");
        snapshot_group(&flyout, &displays, &mut groups, active);
        persist_groups(&flyout, &groups, active, &profile_revision);
    }
    controller.shutdown();
    Ok(())
}

fn active_after_removal(current: usize, removed: usize, remaining: usize) -> usize {
    debug_assert!(remaining > 0);
    if removed < current {
        current - 1
    } else if removed == current {
        current.min(remaining - 1)
    } else {
        current
    }
}

fn apply_event(
    ui: &QuickAccess,
    tray: TrayHandle,
    error_revision: &Arc<AtomicU64>,
    display_state: (
        &Arc<Mutex<Vec<DisplayOption>>>,
        &Arc<Mutex<DisplayOverrides>>,
    ),
    config_state: ConfigStateRefs<'_>,
    event: ControllerEvent,
) {
    let (display_metadata, display_overrides) = display_state;
    let (group_state, active_group, profile_revision, pending_config_reload) = config_state;
    match event {
        ControllerEvent::Displays(mut displays) => {
            apply_display_overrides(
                &mut displays,
                &display_overrides
                    .lock()
                    .expect("display overrides poisoned"),
            );
            let mut groups = group_state.lock().expect("group drafts poisoned");
            let active = *active_group.lock().expect("active group poisoned");
            if displays.is_empty()
                && let Some(group) = groups.get_mut(active)
                && group.target_id.is_none()
            {
                group.stream_only = true;
            }
            refresh_group_sizing(&mut groups, &displays);
            refresh_group_tab_details(&mut groups, &displays);
            hydrate_group(ui, &displays, &groups, active);
            if !ui.get_restart_available() {
                ui.set_error_text("".into());
                if !ui.get_running() && !ui.get_busy() {
                    ui.set_state("Stopped".into());
                    ui.set_state_detail(
                        if displays.is_empty() {
                            "Configure a stream-only screen to start"
                        } else {
                            "Choose a target screen to start"
                        }
                        .into(),
                    );
                }
            }
            if ui.get_page() == QuickAccessPage::ScreenSize {
                populate_screen_size_page(ui, &displays);
            }
            *display_metadata.lock().expect("display metadata poisoned") = displays;
        }
        ControllerEvent::GroupReady(info) => {
            let mut groups = group_state.lock().expect("group drafts poisoned");
            if let Some(group) = groups.iter_mut().find(|group| group.id == info.id) {
                group.telemetry.ready = true;
                group.telemetry.sunshine_id = info.sunshine_id;
                group.telemetry.sunshine_state =
                    if group.telemetry.sunshine_id.as_deref().is_some_and(is_guid) {
                        SunshineState::Starting
                    } else {
                        SunshineState::Failed
                    };
            }
            let active = *active_group.lock().expect("active group poisoned");
            project_group_telemetry(ui, &groups, active);
        }
        ControllerEvent::Fps { id, fps } => {
            let mut groups = group_state.lock().expect("group drafts poisoned");
            if let Some(group) = groups.iter_mut().find(|group| group.id == id) {
                group.telemetry.fps = Some(fps.min(999));
            }
            let active = *active_group.lock().expect("active group poisoned");
            project_group_telemetry(ui, &groups, active);
        }
        ControllerEvent::Sunshine {
            id,
            display_id,
            requested_port,
            port,
            error,
        } => {
            let mut groups = group_state.lock().expect("group drafts poisoned");
            let mut group_number = id + 1;
            if let Some(group) = groups.iter_mut().find(|group| {
                group.id == id
                    && group.telemetry.sunshine_id.as_deref() == Some(display_id.as_str())
            }) {
                group_number = group.id + 1;
                group.telemetry.sunshine_port = Some(port.unwrap_or(requested_port));
                if error.is_some() {
                    group.telemetry.sunshine_state = SunshineState::Failed;
                    group.telemetry.error = true;
                } else {
                    group.telemetry.sunshine_state = SunshineState::Ready;
                    group.telemetry.error = false;
                }
            }
            let active = *active_group.lock().expect("active group poisoned");
            project_group_telemetry(ui, &groups, active);
            drop(groups);

            if let Some(error) = error {
                let message = format!("Output {group_number} Sunshine: {error}");
                let revision = error_revision.fetch_add(1, Ordering::Relaxed) + 1;
                ui.set_error_text(message.as_str().into());
                ui.set_diagnostic_summary(message.as_str().into());
                let ui = ui.as_weak();
                let error_revision = error_revision.clone();
                slint::Timer::single_shot(Duration::from_secs(12), move || {
                    if error_revision.load(Ordering::Relaxed) != revision {
                        return;
                    }
                    if let Some(ui) = ui.upgrade() {
                        ui.set_error_text("".into());
                    }
                });
            }
        }
        ControllerEvent::State {
            state,
            detail,
            running,
            busy,
            error,
            restart_available,
        } => {
            let revision = error_revision.fetch_add(1, Ordering::Relaxed) + 1;
            tray.set_status(state);
            ui.set_state(state.into());
            ui.set_state_detail(detail.into());
            ui.set_running(running);
            ui.set_busy(busy);
            ui.set_restart_available(restart_available);
            ui.set_error_text(error.as_str().into());
            if state == "Reconnecting" {
                show_flyout(ui, false);
            }
            if !running && !busy {
                let mut groups = group_state.lock().expect("group drafts poisoned");
                for group in groups.iter_mut() {
                    group.reset_telemetry();
                }
            }
            if !error.is_empty() {
                let mut groups = group_state.lock().expect("group drafts poisoned");
                for group in groups.iter_mut() {
                    group.telemetry.error = true;
                }
                ui.set_diagnostic_summary(error.as_str().into());
                ui.set_mapping_fps_error(true);
                ui.set_mapping_fps_valid(false);
                let ui = ui.as_weak();
                let error_revision = error_revision.clone();
                slint::Timer::single_shot(Duration::from_secs(12), move || {
                    if error_revision.load(Ordering::Relaxed) != revision {
                        return;
                    }
                    if let Some(ui) = ui.upgrade() {
                        ui.set_error_text("".into());
                        if !ui.get_running() && !ui.get_busy() && !ui.get_restart_available() {
                            if ui.get_selected_display() >= 0 {
                                ui.set_state("Stopped".into());
                                ui.set_state_detail("Choose a display to start".into());
                            } else {
                                ui.set_state("No displays".into());
                                ui.set_state_detail("Connect or enable a physical display".into());
                            }
                        }
                    }
                });
            } else {
                let mut groups = group_state.lock().expect("group drafts poisoned");
                if !running {
                    for group in groups.iter_mut() {
                        group.telemetry.error = false;
                    }
                }
                let active = *active_group.lock().expect("active group poisoned");
                project_group_telemetry(ui, &groups, active);
            }
            if !running && !busy && pending_config_reload.swap(false, Ordering::AcqRel) {
                apply_config_reload(
                    ui,
                    display_metadata,
                    group_state,
                    active_group,
                    profile_revision,
                    error_revision,
                );
            }
        }
    }
}

fn sunshine_web_url(base_port: u16) -> Result<String, String> {
    let web_port = base_port
        .checked_add(1)
        .ok_or_else(|| "The Sunshine Web port is outside the valid range".to_string())?;
    Ok(format!("https://localhost:{web_port}/"))
}

fn open_sunshine_panel(base_port: u16) -> Result<(), String> {
    let url = sunshine_web_url(base_port)?;
    Command::new("explorer.exe")
        .arg(url)
        .spawn()
        .map(|_| ())
        .map_err(|error| format!("Couldn’t open the Sunshine Web panel: {error}"))
}

fn open_display_settings() -> Result<(), String> {
    Command::new("explorer.exe")
        .arg(DISPLAY_SETTINGS_URI)
        .spawn()
        .map(|_| ())
        .map_err(|error| format!("Couldn’t open Windows display settings: {error}"))
}

fn open_project_home() -> Result<(), String> {
    Command::new("explorer.exe")
        .arg(PROJECT_HOME_URL)
        .spawn()
        .map(|_| ())
        .map_err(|error| format!("Couldn’t open the SBMS repository: {error}"))
}

fn refresh_lan_ip(ui: &QuickAccess) {
    match preferred_lan_ipv4() {
        Ok(Some(address)) => ui.set_sunshine_lan_ip(address.to_string().into()),
        Ok(None) => ui.set_sunshine_lan_ip("--".into()),
        Err(error) => {
            diagnostics::log(Level::Warn, "ui", "lan-ip", None, error);
            ui.set_sunshine_lan_ip("--".into());
        }
    }
}

fn surface_transient_error(ui: &QuickAccess, error_revision: &Arc<AtomicU64>, message: &str) {
    let revision = error_revision.fetch_add(1, Ordering::Relaxed) + 1;
    ui.set_error_text(message.into());
    let ui = ui.as_weak();
    let error_revision = error_revision.clone();
    slint::Timer::single_shot(Duration::from_secs(12), move || {
        if error_revision.load(Ordering::Relaxed) != revision {
            return;
        }
        if let Some(ui) = ui.upgrade() {
            ui.set_error_text("".into());
        }
    });
}

fn is_guid(value: &str) -> bool {
    let bytes = value.as_bytes();
    bytes.len() == 38
        && bytes[0] == b'{'
        && bytes[37] == b'}'
        && [9, 14, 19, 24].iter().all(|index| bytes[*index] == b'-')
        && bytes[1..37]
            .iter()
            .enumerate()
            .all(|(index, byte)| [8, 13, 18, 23].contains(&index) || byte.is_ascii_hexdigit())
}

fn apply_display_overrides(displays: &mut [DisplayOption], overrides: &DisplayOverrides) {
    for display in displays {
        display.physical_width_mm = display.detected_physical_width_mm;
        display.physical_height_mm = display.detected_physical_height_mm;
        let display_override = overrides.override_for(&display.id);
        display.physical_override_inches = display_override.map(|entry| entry.diagonal_inches);
        display.physical_override_aspect_ratio =
            display_override.and_then(|entry| entry.aspect_ratio);
        if let Some(diagonal_inches) = display.physical_override_inches
            && let Some((width_mm, height_mm)) = dimensions_from_diagonal(
                display,
                diagonal_inches,
                display.physical_override_aspect_ratio,
            )
        {
            display.physical_width_mm = Some(width_mm);
            display.physical_height_mm = Some(height_mm);
        }
    }
}

fn refresh_group_tab_details(groups: &mut [GroupDraft], displays: &[DisplayOption]) {
    for group in groups {
        group.tab_detail = group_tab_detail(group, displays);
    }
}

fn group_tab_detail(group: &GroupDraft, displays: &[DisplayOption]) -> String {
    if group.stream_only {
        return parse_screen_diagonal(&group.stream_diagonal)
            .map(|diagonal| format!("{diagonal:.0}″"))
            .unwrap_or_else(|_| "--".into());
    }
    group
        .target_id
        .as_deref()
        .and_then(|id| displays.iter().find(|display| display.id == id))
        .and_then(display_diagonal_inches)
        .map(|diagonal| format!("{diagonal:.0}″"))
        .unwrap_or_else(|| "—".into())
}

fn display_diagonal_inches(display: &DisplayOption) -> Option<f64> {
    Some(
        display
            .physical_width_mm?
            .hypot(display.physical_height_mm?)
            / MILLIMETERS_PER_INCH,
    )
}

fn dimensions_from_diagonal(
    display: &DisplayOption,
    diagonal_inches: f64,
    aspect_ratio: Option<AspectRatio>,
) -> Option<(f64, f64)> {
    let (width, height) = aspect_ratio
        .map(|ratio| (f64::from(ratio.width), f64::from(ratio.height)))
        .unwrap_or((
            f64::from(display.native_width),
            f64::from(display.native_height),
        ));
    let ratio_diagonal = width.hypot(height);
    (ratio_diagonal > 0.0).then(|| {
        let diagonal_mm = diagonal_inches * MILLIMETERS_PER_INCH;
        (
            diagonal_mm * width / ratio_diagonal,
            diagonal_mm * height / ratio_diagonal,
        )
    })
}

fn refresh_group_sizing(groups: &mut [GroupDraft], displays: &[DisplayOption]) {
    for group in groups {
        let Some(mut sizing) = group.sizing else {
            continue;
        };
        let target_geometry = if group.stream_only {
            match stream_target_geometry(
                &group.stream_width,
                &group.stream_height,
                &group.stream_diagonal,
                match aspect_ratio_from_index(group.stream_aspect_ratio_index) {
                    Ok(aspect_ratio) => aspect_ratio,
                    Err(_) => {
                        group.sizing = None;
                        continue;
                    }
                },
                match rotation_from_index(group.stream_rotation_index) {
                    Ok(rotation) => rotation,
                    Err(_) => {
                        group.sizing = None;
                        continue;
                    }
                },
            ) {
                Ok(target) => target,
                Err(_) => {
                    group.sizing = None;
                    continue;
                }
            }
        } else {
            let Some(target_id) = group.target_id.as_deref() else {
                group.sizing = None;
                continue;
            };
            let Some(target) = displays.iter().find(|display| display.id == target_id) else {
                continue;
            };
            let Ok(target) = display_geometry(target, "Target") else {
                group.sizing = None;
                continue;
            };
            target
        };
        sizing.target = target_geometry;
        if let Some(ReferenceSource::Display(reference_id)) = &group.reference_source {
            let Some(reference) = displays.iter().find(|display| display.id == *reference_id)
            else {
                continue;
            };
            let Ok(reference_geometry) = display_geometry(reference, "Reference") else {
                group.sizing = None;
                continue;
            };
            sizing.reference = reference_geometry;
        }
        group.sizing = Some(sizing);
    }
}

fn populate_screen_size_page(ui: &QuickAccess, displays: &[DisplayOption]) {
    let Some(display) = selected_option(ui, displays, ui.get_selected_display()) else {
        ui.set_screen_size_target("Display unavailable".into());
        ui.set_screen_size_detected_mm("Not reported".into());
        ui.set_screen_size_detected_inches("Physical size unavailable".into());
        ui.set_screen_size_error("Target screen is no longer available".into());
        return;
    };
    ui.set_screen_size_target(display.name.as_str().into());
    ui.set_screen_size_native_mode(
        format!("{} × {}", display.native_width, display.native_height).into(),
    );
    if let (Some(width), Some(height)) = (
        display.detected_physical_width_mm,
        display.detected_physical_height_mm,
    ) {
        ui.set_screen_size_detected_mm(format!("{width:.0} × {height:.0} mm").into());
        ui.set_screen_size_detected_inches(
            format!(
                "{:.1} in diagonal",
                width.hypot(height) / MILLIMETERS_PER_INCH
            )
            .into(),
        );
    } else {
        ui.set_screen_size_detected_mm("Not reported".into());
        ui.set_screen_size_detected_inches("No physical size in EDID".into());
    }
    if ui.get_page() != QuickAccessPage::ScreenSize {
        ui.set_screen_size_manual_inches(
            display
                .physical_override_inches
                .map(|value| format!("{value:.1}"))
                .unwrap_or_default()
                .into(),
        );
        ui.set_screen_size_aspect_ratio(
            display
                .physical_override_aspect_ratio
                .map(|ratio| aspect_ratio_option_index(ratio) + 1)
                .unwrap_or(0),
        );
    }
    update_screen_size_preview(ui, displays);
}

fn update_screen_size_preview(ui: &QuickAccess, displays: &[DisplayOption]) {
    if ui.get_screen_size_save_blocked() {
        return;
    }
    let Some(display) = selected_option(ui, displays, ui.get_selected_display()) else {
        ui.set_screen_size_error("Target screen is no longer available".into());
        ui.set_screen_size_valid(false);
        return;
    };
    let manual = ui.get_screen_size_manual_inches();
    let effective = if manual.trim().is_empty() {
        ui.set_screen_size_source("EDID".into());
        match (
            display.detected_physical_width_mm,
            display.detected_physical_height_mm,
        ) {
            (Some(width), Some(height)) => {
                Some((width, height, width.hypot(height) / MILLIMETERS_PER_INCH))
            }
            _ => None,
        }
    } else {
        match parse_screen_diagonal(manual.as_str())
            .ok()
            .and_then(|diagonal| {
                let aspect_ratio =
                    screen_override_aspect_ratio(ui.get_screen_size_aspect_ratio()).ok()?;
                dimensions_from_diagonal(display, diagonal, aspect_ratio)
                    .map(|(width, height)| (width, height, diagonal))
            }) {
            Some(value) => {
                ui.set_screen_size_source("Manual override".into());
                Some(value)
            }
            None => {
                ui.set_screen_size_error("Enter a valid diagonal in inches".into());
                ui.set_screen_size_valid(false);
                return;
            }
        }
    };
    match effective {
        Some((width, height, diagonal)) => {
            ui.set_screen_size_effective_mm(format!("{width:.0} × {height:.0} mm").into());
            ui.set_screen_size_effective_inches(format!("{diagonal:.1} in diagonal").into());
            ui.set_screen_size_error("".into());
            ui.set_screen_size_valid(true);
        }
        None => {
            ui.set_screen_size_effective_mm("Physical size unavailable".into());
            ui.set_screen_size_effective_inches("Enter a manual diagonal".into());
            ui.set_screen_size_error("".into());
            ui.set_screen_size_valid(true);
        }
    }
}

fn switch_group(
    ui: &QuickAccess,
    next: i32,
    groups: &Arc<Mutex<Vec<GroupDraft>>>,
    active_group: &Arc<Mutex<usize>>,
    displays: &Arc<Mutex<Vec<DisplayOption>>>,
    profile_revision: &Arc<Mutex<ProfileRevision>>,
) {
    if next < 0 {
        return;
    }
    let displays = displays.lock().expect("display metadata poisoned");
    let mut groups = groups.lock().expect("group drafts poisoned");
    if next as usize >= groups.len() {
        return;
    }
    let current = *active_group.lock().expect("active group poisoned");
    snapshot_group(ui, &displays, &mut groups, current);
    let next = next as usize;
    *active_group.lock().expect("active group poisoned") = next;
    hydrate_group(ui, &displays, &groups, next);
    persist_groups(ui, &groups, next, profile_revision);
}

fn stream_mode(ui: &QuickAccess, displays: &[DisplayOption]) -> Result<VirtualMode, String> {
    let request = geometry_request(ui, displays)?
        .ok_or_else(|| "Enter the reference display measurements".to_string())?;
    let result = request.calculate().map_err(|error| error.to_string())?;
    let refresh_millihz = parse_stream_refresh_millihz(ui.get_stream_refresh().as_str())?;
    VirtualMode::from_millihz(
        result.virtual_mode.width,
        result.virtual_mode.height,
        refresh_millihz,
    )
    .map_err(|error| error.to_string())
}

fn validate_stream_fields(ui: &QuickAccess, displays: &[DisplayOption]) -> Option<VirtualMode> {
    match stream_mode(ui, displays) {
        Ok(mode) => {
            ui.set_stream_error("".into());
            let mode_text = format!("{} × {}", mode.width, mode.height);
            let mode_ratio = stream_aspect_ratio(&mode.width.to_string(), &mode.height.to_string())
                .unwrap_or_default();
            let direction = rotation_label(ui.get_stream_rotation()).unwrap_or("Unknown direction");
            let detail = format!(
                "{} Hz · {} · {}",
                ui.get_stream_refresh(),
                mode_ratio,
                direction
            );
            ui.set_stream_result(mode_text.as_str().into());
            ui.set_stream_result_detail(detail.into());
            ui.set_geometry_configured(true);
            ui.set_geometry_summary(mode_text.into());
            ui.set_geometry_summary_detail(
                format!(
                    "Stream only · {} · {} Hz",
                    direction,
                    ui.get_stream_refresh()
                )
                .into(),
            );
            Some(mode)
        }
        Err(error) => {
            ui.set_stream_error(error.into());
            None
        }
    }
}

fn update_stream_suggestions(ui: &QuickAccess) {
    let width = ui.get_stream_width().to_string();
    let height = ui.get_stream_height().to_string();
    let aspect_ratio =
        aspect_ratio_from_index(ui.get_stream_aspect_ratio_index()).unwrap_or(AspectRatio {
            width: DEFAULT_STREAM_ASPECT_WIDTH as u32,
            height: DEFAULT_STREAM_ASPECT_HEIGHT as u32,
        });

    set_stream_dimension_suggestion(
        &width,
        &height,
        COMMON_WIDTHS,
        u64::from(aspect_ratio.width),
        u64::from(aspect_ratio.height),
        |value| ui.set_stream_width_suggestion(value),
        |value| ui.set_stream_width_suffix(value),
    );
    set_stream_dimension_suggestion(
        &height,
        &width,
        COMMON_HEIGHTS,
        u64::from(aspect_ratio.height),
        u64::from(aspect_ratio.width),
        |value| ui.set_stream_height_suggestion(value),
        |value| ui.set_stream_height_suffix(value),
    );
    ui.set_stream_aspect_ratio(
        stream_aspect_ratio(&width, &height)
            .unwrap_or_default()
            .into(),
    );
}

fn validate_group_drafts(groups: &[GroupDraft]) -> Result<Vec<ValidatedGroupDraft>, String> {
    groups.iter().map(GroupDraft::validate).collect()
}

fn build_mapping_plan(groups: &[ValidatedGroupDraft]) -> Result<MappingPlan, String> {
    let requests = groups
        .iter()
        .map(ValidatedGroupDraft::to_mapping_request)
        .collect::<Result<Vec<_>, String>>()?;
    MappingPlan::new(requests).map_err(|error| error.to_string())
}

fn selected_display_id(ui: &QuickAccess, index: i32) -> Option<String> {
    (index >= 0)
        .then(|| ui.get_display_ids().row_data(index as usize))
        .flatten()
        .map(|id| id.to_string())
}

fn selected_reference_source(ui: &QuickAccess) -> Option<ReferenceSource> {
    let index = ui.get_selected_reference();
    let display_count = ui.get_reference_ids().row_count() as i32;
    if index == display_count && ui.get_reference_labels().row_count() == display_count as usize + 1
    {
        return Some(ReferenceSource::Manual);
    }
    (index >= 0)
        .then(|| ui.get_reference_ids().row_data(index as usize))
        .flatten()
        .map(|id| ReferenceSource::Display(id.to_string()))
}

fn rebuild_reference_options(
    ui: &QuickAccess,
    displays: &[DisplayOption],
    desired_source: Option<ReferenceSource>,
) {
    let target_id = selected_display_id(ui, ui.get_selected_display());
    let candidate_indices = reference_candidate_indices(displays, target_id.as_deref());
    let candidates: Vec<_> = candidate_indices
        .iter()
        .map(|index| &displays[*index])
        .collect();
    let mut names: Vec<_> = candidates
        .iter()
        .map(|display| SharedString::from(display.name.as_str()))
        .collect();
    let mut labels: Vec<_> = candidates
        .iter()
        .map(|display| {
            SharedString::from(
                if display.physical_width_mm.is_some() && display.physical_height_mm.is_some() {
                    display.label.clone()
                } else {
                    format!("{} · Size unavailable", display.label)
                },
            )
        })
        .collect();
    let ids: Vec<_> = candidates
        .iter()
        .map(|display| SharedString::from(display.id.as_str()))
        .collect();
    let selected_index =
        reference_selection_index(displays, &candidate_indices, desired_source.as_ref());
    names.push("Manual".into());
    labels.push("Manual · Enter display geometry".into());
    ui.set_reference_names(ModelRc::new(Rc::new(VecModel::from(names))));
    ui.set_reference_labels(ModelRc::new(Rc::new(VecModel::from(labels))));
    ui.set_reference_ids(ModelRc::new(Rc::new(VecModel::from(ids))));
    ui.set_selected_reference(selected_index);
}

fn reference_candidate_indices(displays: &[DisplayOption], target_id: Option<&str>) -> Vec<usize> {
    displays
        .iter()
        .enumerate()
        .filter_map(|(index, display)| (target_id != Some(display.id.as_str())).then_some(index))
        .collect()
}

fn reference_selection_index(
    displays: &[DisplayOption],
    candidates: &[usize],
    desired_source: Option<&ReferenceSource>,
) -> i32 {
    let manual_index = candidates.len() as i32;
    match desired_source {
        Some(ReferenceSource::Manual) => manual_index,
        Some(ReferenceSource::Display(id)) => candidates
            .iter()
            .position(|index| displays[*index].id == *id)
            .map(|index| index as i32)
            .unwrap_or(if candidates.is_empty() {
                manual_index
            } else {
                0
            }),
        None => {
            if candidates.is_empty() {
                manual_index
            } else {
                0
            }
        }
    }
}

fn load_group_config(ui: &QuickAccess) -> LoadedGroups {
    let snapshot = match ConfigProfileStore::default_store().and_then(|store| store.load_active()) {
        Ok(snapshot) => snapshot,
        Err(error) => {
            set_geometry_unconfigured(ui);
            ui.set_geometry_save_blocked(true);
            ui.set_geometry_error(error.to_string().into());
            return default_loaded_groups();
        }
    };
    ui.set_geometry_save_blocked(false);
    loaded_groups(snapshot)
}

fn loaded_groups(snapshot: crate::config::ProfileSnapshot) -> LoadedGroups {
    let groups = snapshot
        .config
        .groups
        .iter()
        .map(GroupDraft::from_config)
        .collect::<Vec<_>>();
    let active = snapshot
        .config
        .selected_group_id
        .and_then(|id| groups.iter().position(|group| group.id == id))
        .unwrap_or(0);
    LoadedGroups {
        groups,
        active,
        profile: snapshot.profile,
    }
}

fn default_loaded_groups() -> LoadedGroups {
    LoadedGroups {
        groups: vec![GroupDraft::new(0)],
        active: 0,
        profile: ProfileRevision {
            id: "default".into(),
            revision: 0,
        },
    }
}

fn apply_config_reload(
    ui: &QuickAccess,
    displays: &Arc<Mutex<Vec<DisplayOption>>>,
    groups: &Arc<Mutex<Vec<GroupDraft>>>,
    active_group: &Arc<Mutex<usize>>,
    profile_revision: &Arc<Mutex<ProfileRevision>>,
    error_revision: &Arc<AtomicU64>,
) {
    let snapshot = match ConfigProfileStore::default_store().and_then(|store| store.load_active()) {
        Ok(snapshot) => snapshot,
        Err(error) => {
            diagnostics::log(Level::Error, "config", "reload", None, error.to_string());
            surface_transient_error(
                ui,
                error_revision,
                &format!("Couldn’t reload configuration: {error}"),
            );
            return;
        }
    };
    let mut loaded = loaded_groups(snapshot);
    let displays = displays.lock().expect("display metadata poisoned");
    let mut groups = groups.lock().expect("group drafts poisoned");
    for group in &mut loaded.groups {
        if let Some(previous) = groups.iter().find(|previous| previous.id == group.id) {
            group.telemetry = previous.telemetry.clone();
        }
    }
    refresh_group_sizing(&mut loaded.groups, &displays);
    refresh_group_tab_details(&mut loaded.groups, &displays);
    *groups = loaded.groups;
    let mut active = active_group.lock().expect("active group poisoned");
    *active = loaded.active;
    *profile_revision
        .lock()
        .expect("config profile revision poisoned") = loaded.profile.clone();
    ui.set_geometry_save_blocked(false);
    ui.set_geometry_error("".into());
    hydrate_group(ui, &displays, &groups, *active);
    diagnostics::log(
        Level::Info,
        "config",
        "reload",
        None,
        format!(
            "loaded profile={} revision={}",
            loaded.profile.id, loaded.profile.revision
        ),
    );
}

fn persist_groups(
    ui: &QuickAccess,
    groups: &[GroupDraft],
    active: usize,
    profile_revision: &Arc<Mutex<ProfileRevision>>,
) -> bool {
    if ui.get_geometry_save_blocked() {
        surface_persistence_error(
            ui,
            "The saved configuration is invalid; reset or repair it before saving",
        );
        return false;
    }
    let validated = match validate_group_drafts(groups) {
        Ok(validated) => validated,
        Err(error) => {
            surface_persistence_error(ui, &error);
            return false;
        }
    };
    persist_validated_groups(ui, &validated, active, profile_revision)
}

fn persist_validated_groups(
    ui: &QuickAccess,
    groups: &[ValidatedGroupDraft],
    active: usize,
    profile_revision: &Arc<Mutex<ProfileRevision>>,
) -> bool {
    let persisted = groups.iter().map(ValidatedGroupDraft::to_config).collect();
    let Some(selected_group_id) = groups.get(active).map(ValidatedGroupDraft::id) else {
        surface_persistence_error(ui, "The selected mapping group no longer exists");
        return false;
    };
    let config = AppConfig {
        groups: persisted,
        selected_group_id: Some(selected_group_id),
        ..AppConfig::default()
    };
    let expected = profile_revision
        .lock()
        .expect("config profile revision poisoned")
        .clone();
    let result = ConfigProfileStore::default_store()
        .and_then(|store| store.save_active_if_revision(&expected, &config));
    match result {
        Ok(snapshot) => {
            *profile_revision
                .lock()
                .expect("config profile revision poisoned") = snapshot.profile;
            true
        }
        Err(error) => {
            surface_persistence_error(ui, &error.to_string());
            false
        }
    }
}

fn surface_persistence_error(ui: &QuickAccess, error: &str) {
    ui.set_error_text(format!("Couldn’t save mapping groups: {error}").into());
    ui.set_state_detail("Mapping group changes were not saved".into());
    ui.set_geometry_error(error.into());
}

fn reposition_after_layout(ui: slint::Weak<QuickAccess>) {
    slint::Timer::single_shot(Duration::ZERO, move || {
        if let Some(ui) = ui.upgrade() {
            win32_flyout::position(ui.window());
            ui.window().request_redraw();
        }
    });
}

fn show_flyout(ui: &QuickAccess, play_intro: bool) {
    let intro_generation = play_intro.then(|| INTRO_GENERATION.fetch_add(1, Ordering::AcqRel) + 1);
    if intro_generation.is_some() {
        ui.set_intro_stage(0);
        ui.set_intro_active(true);
    }
    refresh_lan_ip(ui);
    win32_flyout::position(ui.window());

    // Slint 1.17's winit software renderer can retain a reused-buffer cache
    // after Windows clears a hidden window during a display-topology change.
    // Taking a snapshot temporarily selects a new repaint buffer and clears
    // that cache, so the visible frame below is rendered in full.
    let _ = ui.window().take_snapshot();
    if let Err(error) = ui.show() {
        diagnostics::log(Level::Warn, "ui", "flyout-show", None, error.to_string());
        return;
    }

    let activated = win32_flyout::activate(ui.window());
    diagnostics::log(
        Level::Debug,
        "ui",
        "flyout-activate",
        None,
        format!("attempt=immediate foreground={activated}"),
    );
    ui.window().request_redraw();

    if let Some(generation) = intro_generation {
        play_launch_intro(ui.as_weak(), generation);
    }
}

fn play_launch_intro(ui: slint::Weak<QuickAccess>, generation: u64) {
    // Solid tiles -> carve MS -> move the mark -> B/S enter and recoil immediately.
    for (delay_ms, stage) in [
        (420, 1),
        (543, 2),
        (665, 3),
        (788, 4),
        (910, 5),
        (1_033, 6),
        (1_155, 7),
        (1_278, 8),
        (1_400, 9),
        (1_523, 10),
        (1_645, 11),
        (1_768, 12),
        (1_890, 13),
        (2_013, 14),
        (2_135, 15),
        (2_258, 16),
        (2_380, 17),
        (2_700, 18),
        (3_120, 19),
        (3_380, 20),
        (3_470, 21),
        (3_630, 22),
        (3_720, 23),
        (3_810, 24),
        (4_010, 25),
        (4_180, 26),
    ] {
        let ui = ui.clone();
        slint::Timer::single_shot(Duration::from_millis(delay_ms), move || {
            if INTRO_GENERATION.load(Ordering::Acquire) == generation
                && let Some(ui) = ui.upgrade()
            {
                ui.set_intro_stage(stage);
            }
        });
    }

    slint::Timer::single_shot(Duration::from_millis(4_460), move || {
        if INTRO_GENERATION.load(Ordering::Acquire) == generation
            && let Some(ui) = ui.upgrade()
        {
            ui.set_intro_active(false);
        }
    });
}

#[cfg(test)]
mod tests {
    use super::{
        COMMON_HEIGHTS, COMMON_WIDTHS, DISPLAY_SETTINGS_URI, GroupDraft, active_after_removal,
        aspect_ratio_from_index, build_mapping_plan, dimensions_from_diagonal,
        format_recent_diagnostics, group_tab_detail, is_guid, reference_candidate_indices,
        reference_selection_index, resolution_suggestion, rotation_from_index, rotation_index,
        rotation_label, scaled_dimension_suggestion, screen_override_aspect_ratio,
        sizing_strategy_from_index, sizing_strategy_index, stream_aspect_ratio,
        stream_target_geometry, sunshine_web_url, validate_group_drafts,
    };
    use crate::config::ReferenceSource;
    use crate::controller::DisplayOption;
    use crate::diagnostics::{Level, Record};
    use crate::geometry::{
        AspectRatio, DisplayGeometry, PhysicalMeasurement, PixelSize, Rotation, SizingRequest,
        SizingStrategy,
    };
    use crate::limits::MILLIMETERS_PER_INCH;
    use crate::mapping::MappingRoute;

    fn display_option(id: &str, name: &str) -> DisplayOption {
        DisplayOption {
            id: id.into(),
            name: name.into(),
            label: format!("{name} · 2560×1440"),
            native_width: 2560,
            native_height: 1440,
            detected_physical_width_mm: Some(527.0),
            detected_physical_height_mm: Some(296.0),
            physical_width_mm: Some(527.0),
            physical_height_mm: Some(296.0),
            physical_override_inches: None,
            physical_override_aspect_ratio: None,
            rotation: Rotation::Deg0,
        }
    }

    fn test_geometry(width: u32, height: u32) -> DisplayGeometry {
        DisplayGeometry {
            native_pixels: PixelSize { width, height },
            physical: PhysicalMeasurement::DimensionsMm {
                width: f64::from(width),
                height: f64::from(height),
            },
            aspect_ratio: None,
            rotation: Rotation::Deg0,
        }
    }

    #[test]
    fn tab_detail_uses_effective_diagonal_and_stream_marker() {
        let display = display_option("display-a", "Panel");
        let mut group = GroupDraft::new(0);
        group.target_id = Some(display.id.clone());
        assert_eq!(group_tab_detail(&group, &[display]), "24″");
        group.stream_only = true;
        assert_eq!(group_tab_detail(&group, &[]), "--");
        group.stream_diagonal = "24.4".into();
        assert_eq!(group_tab_detail(&group, &[]), "24″");
    }

    #[test]
    fn sunshine_web_panel_uses_the_https_port_after_the_base_port() {
        assert_eq!(
            sunshine_web_url(54_321).unwrap(),
            "https://localhost:54322/"
        );
        assert!(sunshine_web_url(u16::MAX).is_err());
    }

    #[test]
    fn display_settings_uses_the_windows_display_uri() {
        assert_eq!(DISPLAY_SETTINGS_URI, "ms-settings:display");
    }

    #[test]
    fn diagonal_override_preserves_native_aspect_ratio() {
        let display = display_option("display-a", "Panel");
        let (width, height) = dimensions_from_diagonal(
            &display,
            24.0,
            Some(AspectRatio {
                width: 16,
                height: 9,
            }),
        )
        .unwrap();
        assert!((width / height - 16.0 / 9.0).abs() < 0.001);
        assert!((width.hypot(height) / MILLIMETERS_PER_INCH - 24.0).abs() < 0.001);
    }

    #[test]
    fn screen_override_supports_native_and_explicit_ratios() {
        assert_eq!(screen_override_aspect_ratio(0).unwrap(), None);
        assert_eq!(
            screen_override_aspect_ratio(1).unwrap(),
            Some(AspectRatio {
                width: 16,
                height: 9
            })
        );
        assert_eq!(
            screen_override_aspect_ratio(5).unwrap(),
            Some(AspectRatio {
                width: 21,
                height: 9
            })
        );
    }

    #[test]
    fn removing_an_inactive_left_tab_keeps_the_same_group_active() {
        assert_eq!(active_after_removal(2, 0, 2), 1);
        assert_eq!(active_after_removal(0, 1, 2), 0);
        assert_eq!(active_after_removal(1, 1, 2), 1);
    }

    #[test]
    fn completes_unique_resolution_prefixes() {
        assert_eq!(resolution_suggestion("2", COMMON_WIDTHS), Some("2560"));
        assert_eq!(resolution_suggestion("38", COMMON_WIDTHS), Some("3840"));
        assert_eq!(resolution_suggestion("21", COMMON_HEIGHTS), Some("2160"));
    }

    #[test]
    fn does_not_guess_ambiguous_or_complete_values() {
        assert_eq!(resolution_suggestion("1", COMMON_WIDTHS), None);
        assert_eq!(resolution_suggestion("2", COMMON_HEIGHTS), None);
        assert_eq!(resolution_suggestion("2560", COMMON_WIDTHS), None);
        assert_eq!(resolution_suggestion("", COMMON_WIDTHS), None);
        assert_eq!(resolution_suggestion("4K", COMMON_WIDTHS), None);
    }

    #[test]
    fn derives_missing_stream_dimension_from_default_sixteen_by_ten_ratio() {
        assert_eq!(
            scaled_dimension_suggestion("1920", 10, 16),
            Some("1200".into())
        );
        assert_eq!(
            scaled_dimension_suggestion("1080", 16, 10),
            Some("1728".into())
        );
        assert_eq!(
            scaled_dimension_suggestion("1921", 10, 16),
            Some("1201".into())
        );
    }

    #[test]
    fn missing_stream_dimension_suggestion_respects_virtual_mode_bounds() {
        assert_eq!(scaled_dimension_suggestion("", 16, 10), None);
        assert_eq!(scaled_dimension_suggestion("0", 16, 10), None);
        assert_eq!(scaled_dimension_suggestion("4k", 16, 10), None);
        assert_eq!(
            scaled_dimension_suggestion("10240", 16, 10),
            Some("16384".into())
        );
        assert_eq!(scaled_dimension_suggestion("16384", 16, 10), None);
        assert_eq!(
            scaled_dimension_suggestion(&u64::MAX.to_string(), 16, 10),
            None
        );
    }

    #[test]
    fn formats_live_stream_aspect_ratio() {
        assert_eq!(stream_aspect_ratio("3840", "2160"), Some("16:9".into()));
        assert_eq!(stream_aspect_ratio("2560", "1600"), Some("16:10".into()));
        assert_eq!(stream_aspect_ratio("3440", "1440"), Some("43:18".into()));
        assert_eq!(stream_aspect_ratio("", "1440"), None);
        assert_eq!(stream_aspect_ratio("3840", "0"), None);
        assert_eq!(stream_aspect_ratio("4k", "2160"), None);
    }

    #[test]
    fn validates_sunshine_ids_and_stream_rotation_labels() {
        assert!(is_guid("{f2b109d3-3184-5c7d-be17-00d066d470a3}"));
        assert!(!is_guid("f2b109d3-3184-5c7d-be17-00d066d470a3"));
        assert!(!is_guid("{not-a-display-id}"));
        assert_eq!(rotation_label(2), Some("Portrait clockwise"));
        assert_eq!(rotation_label(4), None);
    }

    #[test]
    fn streaming_target_geometry_uses_selected_physical_ratio() {
        let target = stream_target_geometry(
            "2560",
            "1600",
            "24",
            AspectRatio {
                width: 16,
                height: 9,
            },
            Rotation::Deg90,
        )
        .unwrap();
        assert_eq!(target.native_pixels.width, 2560);
        assert_eq!(target.native_pixels.height, 1600);
        assert_eq!(
            target.aspect_ratio,
            Some(AspectRatio {
                width: 16,
                height: 9
            })
        );
        assert_eq!(target.rotation, Rotation::Deg90);
        let PhysicalMeasurement::DiagonalMm(diagonal_mm) = target.physical else {
            panic!("stream target should preserve a diagonal measurement");
        };
        assert!((diagonal_mm - 609.6).abs() < 0.001);
        assert!(
            stream_target_geometry(
                "2560",
                "1600",
                "",
                AspectRatio {
                    width: 16,
                    height: 10
                },
                Rotation::Deg0,
            )
            .is_err()
        );
    }

    #[test]
    fn stream_plan_uses_reference_density_instead_of_raw_target_pixels() {
        let mut stream = GroupDraft::new(0);
        stream.stream_only = true;
        stream.stream_width = "2560".into();
        stream.stream_height = "1600".into();
        stream.stream_diagonal = "24".into();
        stream.stream_refresh = "60".into();
        stream.sizing = Some(SizingRequest {
            reference: DisplayGeometry {
                native_pixels: PixelSize {
                    width: 1920,
                    height: 1200,
                },
                physical: PhysicalMeasurement::DimensionsMm {
                    width: 960.0,
                    height: 600.0,
                },
                aspect_ratio: None,
                rotation: Rotation::Deg0,
            },
            target: test_geometry(2560, 1600),
            strategy: SizingStrategy::MatchPhysicalSize,
            alignment: 2,
            preferred_refresh_millihz: Some(60_000),
        });

        let validated = validate_group_drafts(&[stream]).unwrap();
        let plan = build_mapping_plan(&validated).unwrap();
        assert_eq!(plan.groups[0].mode.width, 5120);
        assert_eq!(plan.groups[0].mode.height, 3200);
        assert_eq!(plan.groups[0].route, MappingRoute::StreamOnly);
    }

    #[test]
    fn orientation_labels_map_to_explicit_rotations() {
        let expected = [
            Rotation::Deg0,
            Rotation::Deg180,
            Rotation::Deg90,
            Rotation::Deg270,
        ];
        for (index, rotation) in expected.into_iter().enumerate() {
            assert_eq!(rotation_from_index(index as i32), Ok(rotation));
            assert_eq!(rotation_index(rotation), index as i32);
        }
    }

    #[test]
    fn aspect_ratio_options_are_explicit() {
        assert_eq!(
            aspect_ratio_from_index(0),
            Ok(AspectRatio {
                width: 16,
                height: 9,
            })
        );
        assert_eq!(
            aspect_ratio_from_index(5),
            Ok(AspectRatio {
                width: 32,
                height: 9,
            })
        );
        assert!(aspect_ratio_from_index(6).is_err());
    }

    #[test]
    fn sizing_strategy_options_round_trip() {
        for (index, strategy) in [
            SizingStrategy::MatchPhysicalSize,
            SizingStrategy::RoundedScale,
            SizingStrategy::IntegerScale,
        ]
        .into_iter()
        .enumerate()
        {
            assert_eq!(sizing_strategy_from_index(index as i32), Ok(strategy));
            assert_eq!(sizing_strategy_index(strategy), index as i32);
        }
        assert!(sizing_strategy_from_index(3).is_err());
    }

    #[test]
    fn reference_options_exclude_the_selected_physical_display() {
        let displays = vec![
            display_option("a", "Alpha"),
            display_option("b", "Beta"),
            display_option("c", "Gamma"),
        ];
        let candidates = reference_candidate_indices(&displays, Some("a"));
        assert_eq!(candidates, vec![1, 2]);
        assert_eq!(
            reference_selection_index(
                &displays,
                &candidates,
                Some(&ReferenceSource::Display("b".into()))
            ),
            0
        );

        let candidates = reference_candidate_indices(&displays, Some("b"));
        assert_eq!(candidates, vec![0, 2]);
        assert_eq!(
            reference_selection_index(
                &displays,
                &candidates,
                Some(&ReferenceSource::Display("b".into()))
            ),
            0
        );
    }

    #[test]
    fn a_single_physical_display_leaves_only_manual_reference() {
        let displays = vec![display_option("only", "Only")];
        let candidates = reference_candidate_indices(&displays, Some("only"));
        assert!(candidates.is_empty());
        assert_eq!(
            reference_selection_index(
                &displays,
                &candidates,
                Some(&ReferenceSource::Display("only".into()))
            ),
            0
        );
        assert_eq!(
            reference_selection_index(&displays, &candidates, Some(&ReferenceSource::Manual)),
            0
        );
    }

    #[test]
    fn diagnostic_preview_is_bounded_and_keeps_error_context() {
        let records = (0..45)
            .map(|index| Record {
                time: index * 1_000,
                local_time: format!("time-{index:02}"),
                level: if index == 44 {
                    Level::Error
                } else {
                    Level::Info
                },
                module: "controller".into(),
                stage: "start".into(),
                mapping_session_id: "42-1".into(),
                message: if index == 44 {
                    "record-44\ncleanup context".into()
                } else {
                    format!("record-{index}")
                },
            })
            .collect::<Vec<_>>();

        let preview = format_recent_diagnostics(&records);
        assert!(!preview.contains("record-4\n"));
        assert!(preview.contains("record-5"));
        assert!(preview.contains("ERROR · controller/start · 42-1"));
        assert!(preview.starts_with("[time-44] ERROR"));
        assert!(preview.ends_with("record-5"));
        assert!(!preview.contains("record-44\ncleanup"));
    }

    #[test]
    fn mixed_gui_groups_build_a_real_mapping_plan() {
        let mut mirror = GroupDraft::new(0);
        mirror.target_id = Some("physical-a".into());
        let mut stream = GroupDraft::new(3);
        stream.stream_only = true;
        stream.stream_width = "5120".into();
        stream.stream_height = "2880".into();
        stream.stream_diagonal = "48.8".into();
        stream.stream_refresh = "60".into();
        stream.sizing = Some(SizingRequest {
            reference: test_geometry(2560, 1440),
            target: test_geometry(5120, 2880),
            strategy: SizingStrategy::MatchPhysicalSize,
            alignment: 2,
            preferred_refresh_millihz: Some(60_000),
        });

        let validated = validate_group_drafts(&[mirror, stream]).unwrap();
        let plan = build_mapping_plan(&validated).expect("mixed plan should be valid");
        assert_eq!(plan.groups[0].id, 0);
        assert_eq!(
            plan.groups[0].route,
            MappingRoute::Mirror {
                target: "physical-a".into()
            }
        );
        assert_eq!(plan.groups[1].id, 3);
        assert_eq!(plan.groups[1].route, MappingRoute::StreamOnly);
        assert_eq!(plan.groups[1].mode.width, 5120);
        assert_eq!(plan.groups[1].mode.height, 2880);
        assert_eq!(plan.groups[1].mode.refresh_numerator, 60);
        assert_eq!(plan.groups[1].mode.refresh_denominator, 1);
    }

    #[test]
    fn persisted_stream_group_round_trips_every_user_setting() {
        let mut original = GroupDraft::new(5);
        original.stream_only = true;
        original.reference_source = Some(ReferenceSource::Manual);
        original.stream_width = "4640".into();
        original.stream_height = "2610".into();
        original.stream_diagonal = "31.5".into();
        original.stream_refresh = "119.88".into();
        original.stream_aspect_ratio_index = 4;
        original.stream_rotation_index = 2;
        original.sizing = Some(SizingRequest {
            reference: test_geometry(3840, 2160),
            target: DisplayGeometry {
                native_pixels: PixelSize {
                    width: 4640,
                    height: 2610,
                },
                physical: PhysicalMeasurement::DiagonalMm(31.5 * MILLIMETERS_PER_INCH),
                aspect_ratio: Some(AspectRatio {
                    width: 21,
                    height: 9,
                }),
                rotation: Rotation::Deg90,
            },
            strategy: SizingStrategy::RoundedScale,
            alignment: 2,
            preferred_refresh_millihz: Some(119_880),
        });

        let config = original.validate().unwrap().to_config();
        let restored = GroupDraft::from_config(&config);
        assert_eq!(restored.id, 5);
        assert!(restored.stream_only);
        assert_eq!(restored.reference_source, Some(ReferenceSource::Manual));
        assert_eq!(restored.sizing, original.sizing);
        assert_eq!(restored.stream_width, "4640");
        assert_eq!(restored.stream_height, "2610");
        assert_eq!(restored.stream_diagonal, "31.5");
        assert_eq!(restored.stream_refresh, "119.88");
        assert_eq!(restored.stream_aspect_ratio_index, 4);
        assert_eq!(restored.stream_rotation_index, 2);
    }

    #[test]
    fn mirror_rotation_reaches_the_mapping_plan() {
        let mut mirror = GroupDraft::new(0);
        mirror.target_id = Some("physical-a".into());
        mirror.sizing = Some(SizingRequest {
            reference: test_geometry(3840, 2160),
            target: DisplayGeometry {
                rotation: Rotation::Deg90,
                ..test_geometry(2560, 1440)
            },
            strategy: SizingStrategy::MatchPhysicalSize,
            alignment: 2,
            preferred_refresh_millihz: Some(240_000),
        });

        let validated = validate_group_drafts(&[mirror]).unwrap();
        let plan = build_mapping_plan(&validated).unwrap();
        assert_eq!(plan.groups[0].rotation, Rotation::Deg90);
    }

    #[test]
    fn invalid_stream_screen_is_rejected_before_controller_start() {
        let mut stream = GroupDraft::new(0);
        stream.stream_only = true;
        stream.stream_width = "4k".into();
        assert!(validate_group_drafts(&[stream]).is_err());
    }
}
