use std::error::Error;
use std::process::Command;
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use crate::config::{
    AppConfig, ConfigStore, DisplayOverrideStore, DisplayOverrides, GroupConfig, GroupRouteConfig,
    ReferenceSource, StreamScreenConfig,
};
use crate::control::{TrayInstance, listen_for_shutdown};
use crate::controller::{Controller, ControllerEvent, DisplayOption};
use crate::diagnostics::{self, Level, Record};
use crate::geometry::{
    AspectRatio, DisplayGeometry, PhysicalMeasurement, PixelSize, Rotation, SizingRequest,
    SizingResult, SizingStrategy,
};
use crate::mapping::{MAX_MAPPING_GROUPS, MappingGroupRequest, MappingPlan, MappingRoute};
use crate::network::preferred_lan_ipv4;
use crate::session_gate::{MAX_VIRTUAL_DIMENSION, VirtualMode};
use crate::win32_flyout;
use crate::win32_tray::{NativeTray, TrayAction, TrayHandle};
use slint::winit_030::winit::platform::windows::{CornerPreference, WindowAttributesExtWindows};
use slint::{ComponentHandle, Model, ModelRc, SharedString, VecModel};
slint::include_modules!();

const STREAM_ONLY_ID: &str = "__stream_only__";

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
enum SunshineState {
    #[default]
    Unavailable,
    Starting,
    Ready,
    Failed,
}

impl SunshineState {
    fn as_i32(self) -> i32 {
        match self {
            Self::Unavailable => 0,
            Self::Starting => 1,
            Self::Ready => 2,
            Self::Failed => 3,
        }
    }
}

#[derive(Clone)]
struct GroupDraft {
    id: u32,
    target_id: Option<String>,
    stream_only: bool,
    reference_source: Option<ReferenceSource>,
    sizing: Option<SizingRequest>,
    stream_width: String,
    stream_height: String,
    stream_diagonal: String,
    stream_refresh: String,
    stream_aspect_ratio_index: i32,
    stream_rotation_index: i32,
    sunshine_id: Option<String>,
    sunshine_port: Option<u16>,
    sunshine_state: SunshineState,
    fps: Option<u32>,
    ready: bool,
    error: bool,
    tab_detail: String,
}

struct LoadedGroups {
    groups: Vec<GroupDraft>,
    active: usize,
}

impl GroupDraft {
    fn new(id: u32) -> Self {
        Self {
            id,
            target_id: None,
            stream_only: false,
            reference_source: None,
            sizing: None,
            stream_width: "3840".into(),
            stream_height: "2160".into(),
            stream_diagonal: String::new(),
            stream_refresh: "60".into(),
            stream_aspect_ratio_index: 0,
            stream_rotation_index: 0,
            sunshine_id: None,
            sunshine_port: None,
            sunshine_state: SunshineState::Unavailable,
            fps: None,
            ready: false,
            error: false,
            tab_detail: "—".into(),
        }
    }

    fn from_config(config: &GroupConfig) -> Self {
        let mut draft = Self::new(config.id);
        match &config.route {
            GroupRouteConfig::Mirror { target_id } => {
                draft.target_id = target_id.clone();
            }
            GroupRouteConfig::StreamOnly { screen } => {
                draft.stream_only = true;
                draft.stream_width = screen.width.to_string();
                draft.stream_height = screen.height.to_string();
                draft.stream_diagonal = screen
                    .diagonal_inches
                    .map(|value| value.to_string())
                    .unwrap_or_default();
                draft.stream_refresh = format_refresh_input(screen.refresh_millihz);
                draft.stream_aspect_ratio_index = aspect_ratio_option_index(screen.aspect_ratio);
                draft.stream_rotation_index = rotation_index(screen.rotation);
            }
        }
        draft.reference_source = config.reference_source.clone();
        draft.sizing = config.sizing;
        draft
    }

    fn to_config(&self) -> Result<GroupConfig, String> {
        let route = if self.stream_only {
            let diagonal = self.stream_diagonal.trim();
            GroupRouteConfig::StreamOnly {
                screen: StreamScreenConfig {
                    width: parse_u32(&self.stream_width, "Streaming screen width")?,
                    height: parse_u32(&self.stream_height, "Streaming screen height")?,
                    diagonal_inches: if diagonal.is_empty() {
                        None
                    } else {
                        Some(parse_screen_diagonal(diagonal)?)
                    },
                    refresh_millihz: parse_stream_refresh_millihz(&self.stream_refresh)?,
                    aspect_ratio: aspect_ratio_from_index(self.stream_aspect_ratio_index)?,
                    rotation: rotation_from_index(self.stream_rotation_index)?,
                },
            }
        } else {
            GroupRouteConfig::Mirror {
                target_id: self.target_id.clone(),
            }
        };
        Ok(GroupConfig {
            id: self.id,
            route,
            reference_source: self.reference_source.clone(),
            sizing: self.sizing,
        })
    }
}

pub fn run() -> Result<(), Box<dyn Error>> {
    run_inner(false)
}

pub fn run_open() -> Result<(), Box<dyn Error>> {
    run_inner(true)
}

fn run_inner(open_on_start: bool) -> Result<(), Box<dyn Error>> {
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

    let Some(_instance) = TrayInstance::acquire()? else {
        return Ok(());
    };
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
    flyout.set_max_group_count(MAX_MAPPING_GROUPS as i32);
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
    update_tab_projection(
        &flyout,
        &groups.lock().expect("group drafts poisoned"),
        loaded.active,
    );

    let flyout_focus = Arc::new(AtomicBool::new(false));
    let flyout_weak = flyout.as_weak();
    let action_focus = flyout_focus.clone();
    let tray = NativeTray::new(move |action| {
        let flyout_weak = flyout_weak.clone();
        let focus = action_focus.clone();
        if let Err(error) = slint::invoke_from_event_loop(move || match action {
            TrayAction::Toggle => {
                if let Some(flyout) = flyout_weak.upgrade() {
                    if flyout.window().is_visible() {
                        focus.store(false, Ordering::Relaxed);
                        let _ = flyout.hide();
                    } else {
                        show_flyout(&flyout, &focus);
                    }
                }
            }
            TrayAction::Open => {
                if let Some(flyout) = flyout_weak.upgrade() {
                    show_flyout(&flyout, &focus);
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

    let flyout_weak = flyout.as_weak();
    let dismiss_focus = flyout_focus.clone();
    flyout.on_dismiss(move || {
        if let Some(flyout) = flyout_weak.upgrade() {
            dismiss_focus.store(false, Ordering::Relaxed);
            let _ = flyout.hide();
        }
    });

    let ui = flyout.as_weak();
    let error_revision = Arc::new(AtomicU64::new(0));
    let event_error_revision = error_revision.clone();
    let event_displays = displays.clone();
    let event_groups = groups.clone();
    let event_active_group = active_group.clone();
    let event_display_overrides = display_overrides.clone();
    let controller = Controller::spawn(move |event| {
        let ui = ui.clone();
        let tray = tray_handle.clone();
        let error_revision = event_error_revision.clone();
        let displays = event_displays.clone();
        let groups = event_groups.clone();
        let active_group = event_active_group.clone();
        let display_overrides = event_display_overrides.clone();
        let _ = slint::invoke_from_event_loop(move || {
            if let Some(ui) = ui.upgrade() {
                apply_event(
                    &ui,
                    tray,
                    &error_revision,
                    (&displays, &display_overrides),
                    &groups,
                    &active_group,
                    event,
                );
            }
        });
    });
    let sender = controller.sender();

    let start_sender = sender.clone();
    let flyout_weak = flyout.as_weak();
    let start_groups = groups.clone();
    let start_active_group = active_group.clone();
    let start_displays = displays.clone();
    flyout.on_start(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = start_displays.lock().expect("display metadata poisoned");
            let mut groups = start_groups.lock().expect("group drafts poisoned");
            let active = *start_active_group.lock().expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            if !persist_groups(&ui, &groups, active) {
                return;
            }
            match build_mapping_plan(&groups) {
                Ok(plan) => {
                    for group in groups.iter_mut() {
                        group.fps = None;
                        group.ready = false;
                        group.error = false;
                        group.sunshine_id = None;
                        group.sunshine_port = None;
                        group.sunshine_state = SunshineState::Unavailable;
                    }
                    project_group_telemetry(&ui, &groups, active);
                    start_sender.start(plan);
                }
                Err(error) => {
                    ui.set_error_text(error.as_str().into());
                    ui.set_state_detail("Mapping plan needs attention".into());
                }
            }
        }
    });
    let stop_sender = sender.clone();
    flyout.on_stop(move || stop_sender.stop());
    let refresh_sender = sender.clone();
    flyout.on_refresh(move || refresh_sender.refresh());

    let flyout_weak = flyout.as_weak();
    let sunshine_groups = groups.clone();
    let sunshine_active_group = active_group.clone();
    let sunshine_error_revision = error_revision.clone();
    flyout.on_open_sunshine_panel(move || {
        let Some(ui) = flyout_weak.upgrade() else {
            return;
        };
        let groups = sunshine_groups.lock().expect("group drafts poisoned");
        let active = *sunshine_active_group.lock().expect("active group poisoned");
        let Some(port) = groups
            .get(active)
            .filter(|group| {
                group.stream_only && group.ready && group.sunshine_state == SunshineState::Ready
            })
            .and_then(|group| group.sunshine_port)
        else {
            surface_transient_error(
                &ui,
                &sunshine_error_revision,
                "This Sunshine instance is not ready yet",
            );
            return;
        };
        drop(groups);
        if let Err(error) = open_sunshine_panel(port) {
            diagnostics::log(
                Level::Warn,
                "ui",
                "open-sunshine-panel",
                None,
                error.as_str(),
            );
            surface_transient_error(&ui, &sunshine_error_revision, &error);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = displays.clone();
    let callback_groups = groups.clone();
    let callback_active_group = active_group.clone();
    flyout.on_display_selected(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            let selected_id = selected_display_id(&ui, ui.get_selected_display());
            ui.set_stream_only(selected_id.as_deref() == Some(STREAM_ONLY_ID));
            let previous_reference = selected_reference_source(&ui);
            rebuild_reference_options(&ui, &displays, previous_reference);
            update_reference_action(&ui, &displays);
            let mut groups = callback_groups.lock().expect("group drafts poisoned");
            let active = *callback_active_group.lock().expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            update_tab_projection(&ui, &groups, active);
            project_group_telemetry(&ui, &groups, active);
            persist_groups(&ui, &groups, active);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = displays.clone();
    let callback_groups = groups.clone();
    let callback_active_group = active_group.clone();
    flyout.on_reference_selected(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            update_reference_action(&ui, &displays);
            if ui.get_stream_only() {
                validate_stream_fields(&ui, &displays);
            }
            let mut groups = callback_groups.lock().expect("group drafts poisoned");
            let active = *callback_active_group.lock().expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            update_tab_projection(&ui, &groups, active);
            persist_groups(&ui, &groups, active);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = displays.clone();
    let callback_groups = groups.clone();
    let callback_active_group = active_group.clone();
    flyout.on_strategy_selected(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            if ui.get_geometry_page() {
                update_geometry_preview(&ui, &displays);
            } else {
                if ui.get_stream_only() {
                    validate_stream_fields(&ui, &displays);
                }
                let mut groups = callback_groups.lock().expect("group drafts poisoned");
                let active = *callback_active_group.lock().expect("active group poisoned");
                snapshot_group(&ui, &displays, &mut groups, active);
                update_tab_projection(&ui, &groups, active);
                persist_groups(&ui, &groups, active);
            }
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = displays.clone();
    let callback_groups = groups.clone();
    let callback_active_group = active_group.clone();
    flyout.on_calculate_geometry(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            calculate_geometry(&ui, &displays);
            if !ui.get_geometry_valid() {
                return;
            }
            let mut groups = callback_groups.lock().expect("group drafts poisoned");
            let active = *callback_active_group.lock().expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            update_tab_projection(&ui, &groups, active);
            persist_groups(&ui, &groups, active);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = displays.clone();
    flyout.on_open_manual_geometry(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_geometry_page(true);
            let displays = callback_displays.lock().expect("display metadata poisoned");
            update_geometry_preview(&ui, &displays);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_close_geometry(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_geometry_page(false);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = displays.clone();
    flyout.on_geometry_edited(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            update_geometry_preview(&ui, &displays);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = displays.clone();
    let callback_groups = groups.clone();
    let callback_active_group = active_group.clone();
    flyout.on_save_geometry(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            calculate_geometry(&ui, &displays);
            if !ui.get_geometry_valid() {
                return;
            }
            let mut groups = callback_groups.lock().expect("group drafts poisoned");
            let active = *callback_active_group.lock().expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            update_tab_projection(&ui, &groups, active);
            if persist_groups(&ui, &groups, active) {
                ui.set_geometry_page(false);
                reposition_after_layout(ui.as_weak());
            }
        }
    });

    let flyout_weak = flyout.as_weak();
    let stream_displays = displays.clone();
    flyout.on_open_stream_config(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            update_stream_suggestions(&ui);
            let displays = stream_displays.lock().expect("display metadata poisoned");
            update_reference_action(&ui, &displays);
            validate_stream_fields(&ui, &displays);
            ui.set_stream_page(true);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_close_stream_config(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_stream_page(false);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    let stream_displays = displays.clone();
    flyout.on_stream_edited(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            update_stream_suggestions(&ui);
            let displays = stream_displays.lock().expect("display metadata poisoned");
            update_reference_action(&ui, &displays);
            validate_stream_fields(&ui, &displays);
        }
    });

    let flyout_weak = flyout.as_weak();
    let stream_groups = groups.clone();
    let stream_active_group = active_group.clone();
    let stream_displays = displays.clone();
    flyout.on_save_stream_config(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = stream_displays.lock().expect("display metadata poisoned");
            if validate_stream_fields(&ui, &displays).is_none() {
                return;
            }
            let mut groups = stream_groups.lock().expect("group drafts poisoned");
            let active = *stream_active_group.lock().expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            update_tab_projection(&ui, &groups, active);
            if persist_groups(&ui, &groups, active) {
                ui.set_stream_page(false);
                reposition_after_layout(ui.as_weak());
            }
        }
    });

    let flyout_weak = flyout.as_weak();
    let screen_displays = displays.clone();
    let screen_sender = sender.clone();
    flyout.on_open_screen_size(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = screen_displays.lock().expect("display metadata poisoned");
            populate_screen_size_page(&ui, &displays);
            ui.set_screen_size_page(true);
            screen_sender.refresh();
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_close_screen_size(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_screen_size_page(false);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    let screen_displays = displays.clone();
    flyout.on_screen_size_edited(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = screen_displays.lock().expect("display metadata poisoned");
            update_screen_size_preview(&ui, &displays);
        }
    });

    let flyout_weak = flyout.as_weak();
    let screen_displays = displays.clone();
    flyout.on_reset_screen_size(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_screen_size_manual_inches("".into());
            let displays = screen_displays.lock().expect("display metadata poisoned");
            update_screen_size_preview(&ui, &displays);
        }
    });

    let reread_sender = sender.clone();
    flyout.on_reread_screen_size(move || reread_sender.refresh());

    let flyout_weak = flyout.as_weak();
    let screen_displays = displays.clone();
    let screen_groups = groups.clone();
    let screen_active_group = active_group.clone();
    let screen_overrides = display_overrides.clone();
    let screen_store = override_store.clone();
    flyout.on_save_screen_size(move || {
        let Some(ui) = flyout_weak.upgrade() else {
            return;
        };
        if ui.get_screen_size_save_blocked() {
            return;
        }
        let mut displays = screen_displays.lock().expect("display metadata poisoned");
        let Some(display_id) = selected_display_id(&ui, ui.get_selected_display()) else {
            ui.set_screen_size_error("Target screen is no longer available".into());
            return;
        };
        if display_id == STREAM_ONLY_ID {
            return;
        }
        let manual = ui.get_screen_size_manual_inches();
        let mut overrides = screen_overrides.lock().expect("display overrides poisoned");
        let result = if manual.trim().is_empty() {
            overrides.remove(&display_id);
            Ok(())
        } else {
            parse_screen_diagonal(manual.as_str()).and_then(|value| {
                let aspect_ratio = screen_override_aspect_ratio(ui.get_screen_size_aspect_ratio())?;
                overrides
                    .upsert(display_id.clone(), value, aspect_ratio)
                    .map_err(|e| e.to_string())
            })
        };
        if let Err(error) =
            result.and_then(|_| screen_store.save(&overrides).map_err(|e| e.to_string()))
        {
            ui.set_screen_size_error(error.into());
            return;
        }
        apply_display_overrides(&mut displays, &overrides);
        let mut groups = screen_groups.lock().expect("group drafts poisoned");
        refresh_group_sizing(&mut groups, &displays);
        refresh_group_tab_details(&mut groups, &displays);
        let active = *screen_active_group.lock().expect("active group poisoned");
        hydrate_group(&ui, &displays, &groups, active);
        ui.set_screen_size_page(false);
        reposition_after_layout(ui.as_weak());
    });

    let flyout_weak = flyout.as_weak();
    let tab_groups = groups.clone();
    let tab_active_group = active_group.clone();
    let tab_displays = displays.clone();
    flyout.on_tab_selected(move |index| {
        if let Some(ui) = flyout_weak.upgrade() {
            switch_group(&ui, index, &tab_groups, &tab_active_group, &tab_displays);
        }
    });

    let flyout_weak = flyout.as_weak();
    let wheel_groups = groups.clone();
    let wheel_active_group = active_group.clone();
    let wheel_displays = displays.clone();
    flyout.on_tab_wheel(move |delta| {
        if let Some(ui) = flyout_weak.upgrade() {
            let current = *wheel_active_group.lock().expect("active group poisoned");
            let count = wheel_groups.lock().expect("group drafts poisoned").len();
            let next = if delta > 0 {
                (current + 1).min(count.saturating_sub(1))
            } else {
                current.saturating_sub(1)
            };
            switch_group(
                &ui,
                next as i32,
                &wheel_groups,
                &wheel_active_group,
                &wheel_displays,
            );
        }
    });

    let flyout_weak = flyout.as_weak();
    let add_groups = groups.clone();
    let add_active_group = active_group.clone();
    let add_displays = displays.clone();
    flyout.on_add_group(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = add_displays.lock().expect("display metadata poisoned");
            let mut groups = add_groups.lock().expect("group drafts poisoned");
            let current = *add_active_group.lock().expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, current);
            if groups.len() >= MAX_MAPPING_GROUPS {
                return;
            }
            let id = (0..MAX_MAPPING_GROUPS as u32)
                .find(|id| groups.iter().all(|group| group.id != *id))
                .expect("mapping group capacity and ids diverged");
            let mut group = GroupDraft::new(id);
            group.stream_only = displays.iter().all(|display| {
                groups.iter().any(|group| {
                    !group.stream_only && group.target_id.as_deref() == Some(display.id.as_str())
                })
            });
            group.tab_detail = group_tab_detail(&group, &displays);
            groups.push(group);
            let next = groups.len() - 1;
            *add_active_group.lock().expect("active group poisoned") = next;
            hydrate_group(&ui, &displays, &groups, next);
            persist_groups(&ui, &groups, next);
        }
    });

    let flyout_weak = flyout.as_weak();
    let remove_groups = groups.clone();
    let remove_active_group = active_group.clone();
    let remove_displays = displays.clone();
    flyout.on_remove_group(move |index| {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = remove_displays.lock().expect("display metadata poisoned");
            let mut groups = remove_groups.lock().expect("group drafts poisoned");
            if groups.len() <= 1 || index < 0 || index as usize >= groups.len() {
                return;
            }
            let current = *remove_active_group.lock().expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, current);
            let removed = index as usize;
            groups.remove(removed);
            let next = active_after_removal(current, removed, groups.len());
            *remove_active_group.lock().expect("active group poisoned") = next;
            hydrate_group(&ui, &displays, &groups, next);
            persist_groups(&ui, &groups, next);
        }
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_open_diagnostics(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            populate_diagnostics(&ui);
            ui.set_geometry_page(false);
            ui.set_diagnostics_page(true);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_close_diagnostics(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_diagnostics_page(false);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_open_log_folder(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_diagnostic_action_error("".into());
            if let Err(error) = open_log_folder() {
                diagnostics::log(Level::Warn, "ui", "open-log-folder", None, error.as_str());
                ui.set_diagnostic_action_error(error.into());
            }
        }
    });

    let dismiss_timer = slint::Timer::default();
    let dismiss_flyout = flyout.as_weak();
    let timer_focus = flyout_focus.clone();
    dismiss_timer.start(
        slint::TimerMode::Repeated,
        Duration::from_millis(150),
        move || {
            if let Some(flyout) = dismiss_flyout.upgrade() {
                if !flyout.window().is_visible() {
                    timer_focus.store(false, Ordering::Relaxed);
                } else if timer_focus.load(Ordering::Relaxed) {
                    if win32_flyout::lost_focus(flyout.window()) {
                        timer_focus.store(false, Ordering::Relaxed);
                        let _ = flyout.hide();
                    }
                } else if win32_flyout::has_focus(flyout.window()) {
                    timer_focus.store(true, Ordering::Relaxed);
                }
            }
        },
    );

    sender.refresh();
    if open_on_start {
        show_flyout(&flyout, &flyout_focus);
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
        persist_groups(&flyout, &groups, active);
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
    group_state: &Arc<Mutex<Vec<GroupDraft>>>,
    active_group: &Arc<Mutex<usize>>,
    event: ControllerEvent,
) {
    let (display_metadata, display_overrides) = display_state;
    match event {
        ControllerEvent::Displays(mut displays) => {
            apply_display_overrides(
                &mut displays,
                &display_overrides
                    .lock()
                    .expect("display overrides poisoned"),
            );
            set_displays(ui, &displays);
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
            if ui.get_screen_size_page() {
                populate_screen_size_page(ui, &displays);
            }
            *display_metadata.lock().expect("display metadata poisoned") = displays;
        }
        ControllerEvent::GroupReady(info) => {
            let mut groups = group_state.lock().expect("group drafts poisoned");
            if let Some(group) = groups.iter_mut().find(|group| group.id == info.id) {
                group.ready = true;
                group.sunshine_id = info.sunshine_id;
                group.sunshine_state = if group.sunshine_id.as_deref().is_some_and(is_guid) {
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
                group.fps = Some(fps.min(999));
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
                group.id == id && group.sunshine_id.as_deref() == Some(display_id.as_str())
            }) {
                group_number = group.id + 1;
                group.sunshine_port = Some(port.unwrap_or(requested_port));
                if error.is_some() {
                    group.sunshine_state = SunshineState::Failed;
                    group.error = true;
                } else {
                    group.sunshine_state = SunshineState::Ready;
                    group.error = false;
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
        } => {
            let revision = error_revision.fetch_add(1, Ordering::Relaxed) + 1;
            tray.set_status(state);
            ui.set_state(state.into());
            ui.set_state_detail(detail.into());
            ui.set_running(running);
            ui.set_busy(busy);
            ui.set_error_text(error.as_str().into());
            if !running && !busy {
                let mut groups = group_state.lock().expect("group drafts poisoned");
                for group in groups.iter_mut() {
                    group.fps = None;
                    group.ready = false;
                    group.sunshine_id = None;
                    group.sunshine_port = None;
                    group.sunshine_state = SunshineState::Unavailable;
                }
            }
            if !error.is_empty() {
                let mut groups = group_state.lock().expect("group drafts poisoned");
                for group in groups.iter_mut() {
                    group.error = true;
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
                        if !ui.get_running() && !ui.get_busy() {
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
                        group.error = false;
                    }
                }
                let active = *active_group.lock().expect("active group poisoned");
                project_group_telemetry(ui, &groups, active);
            }
        }
    }
}

fn populate_diagnostics(ui: &QuickAccess) {
    let records = diagnostics::recent();
    ui.set_diagnostic_text(format_recent_diagnostics(&records).into());
    let path = diagnostics::default_log_path()
        .map(|path| path.display().to_string())
        .unwrap_or_else(|error| format!("Log path unavailable: {error}"));
    ui.set_diagnostic_path(path.into());
    ui.set_diagnostic_action_error("".into());
}

fn format_recent_diagnostics(records: &[Record]) -> String {
    let error_session = records
        .iter()
        .rev()
        .find(|record| record.level == Level::Error)
        .map(|record| record.mapping_session_id.as_str())
        .filter(|session| *session != "-");
    let selected = records
        .iter()
        .filter(|record| {
            error_session
                .map(|session| record.mapping_session_id == session)
                .unwrap_or(true)
        })
        .rev()
        .take(40)
        .collect::<Vec<_>>()
        .into_iter()
        .rev()
        .collect::<Vec<_>>();
    let Some(latest_time) = selected.last().map(|record| record.time) else {
        return "No diagnostic records are available.".into();
    };

    selected
        .into_iter()
        .map(|record| {
            let age = latest_time.saturating_sub(record.time);
            let age = if age < 1_000 {
                "now".to_string()
            } else if age < 60_000 {
                format!("-{:.1}s", age as f64 / 1_000.0)
            } else {
                format!("-{}m", age / 60_000)
            };
            let session = if record.mapping_session_id == "-" {
                String::new()
            } else {
                format!(" · {}", record.mapping_session_id)
            };
            let message = record.message.replace(['\r', '\n'], " ");
            format!(
                "[{age}] {} · {}/{}{}\n{}",
                diagnostic_level(record),
                record.module,
                record.stage,
                session,
                message
            )
        })
        .collect::<Vec<_>>()
        .join("\n\n")
}

fn diagnostic_level(record: &Record) -> &'static str {
    match record.level {
        Level::Debug => "DEBUG",
        Level::Info => "INFO",
        Level::Warn => "WARN",
        Level::Error => "ERROR",
    }
}

fn open_log_folder() -> Result<(), String> {
    let path = diagnostics::default_log_path()
        .map_err(|error| format!("Couldn’t locate the log folder: {error}"))?;
    let folder = path
        .parent()
        .ok_or_else(|| "The diagnostic log path has no parent folder".to_string())?;
    Command::new("explorer.exe")
        .arg(folder)
        .spawn()
        .map(|_| ())
        .map_err(|error| format!("Couldn’t open the log folder: {error}"))
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

fn update_tab_projection(ui: &QuickAccess, groups: &[GroupDraft], active: usize) {
    let first = if groups.len() <= 3 {
        0
    } else {
        active.saturating_sub(1).min(groups.len() - 3)
    };
    let visible = groups.iter().enumerate().skip(first).take(3);
    let mut labels = Vec::new();
    let mut details = Vec::new();
    let mut indices = Vec::new();
    for (index, group) in visible {
        labels.push(SharedString::from((group.id + 1).to_string()));
        details.push(SharedString::from(group.tab_detail.as_str()));
        indices.push(index as i32);
    }
    ui.set_tab_labels(ModelRc::new(Rc::new(VecModel::from(labels))));
    ui.set_tab_details(ModelRc::new(Rc::new(VecModel::from(details))));
    ui.set_tab_indices(ModelRc::new(Rc::new(VecModel::from(indices))));
    ui.set_active_group_index(active as i32);
    ui.set_group_count(groups.len() as i32);
}

fn project_group_telemetry(ui: &QuickAccess, groups: &[GroupDraft], active: usize) {
    let Some(group) = groups.get(active) else {
        return;
    };
    ui.set_mapping_fps(group.fps.unwrap_or_default() as i32);
    ui.set_mapping_fps_valid(group.fps.is_some());
    ui.set_mapping_fps_error(group.error);
    ui.set_mapping_fps_nan(ui.get_stream_only() && !group.error);
    ui.set_sunshine_state(group.sunshine_state.as_i32());
    ui.set_sunshine_port(group.sunshine_port.unwrap_or_default().into());
    ui.set_sunshine_panel_enabled(
        group.ready
            && group.sunshine_state == SunshineState::Ready
            && group.sunshine_port.is_some()
            && group.stream_only,
    );
}

fn set_target_options(
    ui: &QuickAccess,
    displays: &[DisplayOption],
    groups: &[GroupDraft],
    active: usize,
) {
    let used = groups
        .iter()
        .enumerate()
        .filter(|(index, group)| *index != active && !group.stream_only)
        .filter_map(|(_, group)| group.target_id.as_deref())
        .collect::<Vec<_>>();
    let candidates = displays
        .iter()
        .filter(|display| !used.contains(&display.id.as_str()))
        .collect::<Vec<_>>();
    let mut names = candidates
        .iter()
        .map(|display| SharedString::from(display.name.as_str()))
        .collect::<Vec<_>>();
    let mut labels = candidates
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
        .collect::<Vec<_>>();
    let mut ids = candidates
        .iter()
        .map(|display| SharedString::from(display.id.as_str()))
        .collect::<Vec<_>>();
    let widths: Vec<_> = candidates.iter().map(|display| display.width).collect();
    let heights: Vec<_> = candidates.iter().map(|display| display.height).collect();
    names.push("Stream only".into());
    labels.push("Stream only · Create a virtual display".into());
    ids.push(STREAM_ONLY_ID.into());
    ui.set_display_names(ModelRc::new(Rc::new(VecModel::from(names))));
    ui.set_display_labels(ModelRc::new(Rc::new(VecModel::from(labels))));
    ui.set_display_ids(ModelRc::new(Rc::new(VecModel::from(ids))));
    ui.set_display_widths(ModelRc::new(Rc::new(VecModel::from(widths))));
    ui.set_display_heights(ModelRc::new(Rc::new(VecModel::from(heights))));

    let group = &groups[active];
    let desired = if group.stream_only {
        STREAM_ONLY_ID
    } else {
        group.target_id.as_deref().unwrap_or("")
    };
    let selected = (0..ui.get_display_ids().row_count())
        .find(|index| {
            ui.get_display_ids()
                .row_data(*index)
                .is_some_and(|id| id.as_str() == desired)
        })
        .or_else(|| (!candidates.is_empty()).then_some(0))
        .unwrap_or(candidates.len());
    ui.set_selected_display(selected as i32);
    ui.set_stream_only(
        ui.get_display_ids()
            .row_data(selected)
            .is_some_and(|id| id.as_str() == STREAM_ONLY_ID),
    );
}

fn hydrate_group(
    ui: &QuickAccess,
    displays: &[DisplayOption],
    groups: &[GroupDraft],
    active: usize,
) {
    let Some(group) = groups.get(active) else {
        return;
    };
    set_target_options(ui, displays, groups, active);
    ui.set_stream_width(group.stream_width.as_str().into());
    ui.set_stream_height(group.stream_height.as_str().into());
    ui.set_stream_diagonal(group.stream_diagonal.as_str().into());
    ui.set_stream_refresh(group.stream_refresh.as_str().into());
    ui.set_stream_aspect_ratio_index(group.stream_aspect_ratio_index);
    ui.set_stream_rotation(group.stream_rotation_index);
    if let Some(request) = group.sizing {
        populate_geometry_fields(ui, request);
        match request.calculate() {
            Ok(result) => set_geometry_result(ui, result, true),
            Err(error) => {
                set_geometry_unconfigured(ui);
                ui.set_geometry_error(error.to_string().into());
            }
        }
    } else {
        set_geometry_unconfigured(ui);
    }
    rebuild_reference_options(ui, displays, group.reference_source.clone());
    update_reference_action(ui, displays);
    update_stream_suggestions(ui);
    if group.stream_only {
        validate_stream_fields(ui, displays);
    }
    update_tab_projection(ui, groups, active);
    project_group_telemetry(ui, groups, active);
}

fn snapshot_group(
    ui: &QuickAccess,
    displays: &[DisplayOption],
    groups: &mut [GroupDraft],
    active: usize,
) {
    let Some(group) = groups.get_mut(active) else {
        return;
    };
    let target_id = selected_display_id(ui, ui.get_selected_display());
    group.stream_only = target_id.as_deref() == Some(STREAM_ONLY_ID);
    group.target_id = (!group.stream_only).then_some(target_id).flatten();
    group.reference_source = selected_reference_source(ui);
    group.sizing = geometry_request(ui, displays)
        .ok()
        .flatten()
        .filter(|request| request.calculate().is_ok());
    group.stream_width = ui.get_stream_width().to_string();
    group.stream_height = ui.get_stream_height().to_string();
    group.stream_diagonal = ui.get_stream_diagonal().to_string();
    group.stream_refresh = ui.get_stream_refresh().to_string();
    group.stream_aspect_ratio_index = ui.get_stream_aspect_ratio_index();
    group.stream_rotation_index = ui.get_stream_rotation();
    group.tab_detail = group_tab_detail(group, displays);
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
            / 25.4,
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
        let diagonal_mm = diagonal_inches * 25.4;
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

fn parse_screen_diagonal(value: &str) -> Result<f64, String> {
    let diagonal = value
        .trim()
        .parse::<f64>()
        .map_err(|_| "Enter a valid diagonal in inches".to_string())?;
    let diagonal_mm = diagonal * 25.4;
    if !diagonal.is_finite() || !(10.0..=10_000.0).contains(&diagonal_mm) {
        return Err("Diagonal must be between 0.4 and 393.7 inches".into());
    }
    Ok(diagonal)
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
            format!("{:.1} in diagonal", width.hypot(height) / 25.4).into(),
        );
    } else {
        ui.set_screen_size_detected_mm("Not reported".into());
        ui.set_screen_size_detected_inches("No physical size in EDID".into());
    }
    if !ui.get_screen_size_page() {
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
            (Some(width), Some(height)) => Some((width, height, width.hypot(height) / 25.4)),
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
    persist_groups(ui, &groups, next);
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

fn parse_stream_refresh_millihz(value: &str) -> Result<u32, String> {
    let refresh = value
        .trim()
        .parse::<f64>()
        .map_err(|_| "Refresh rate must be a positive number".to_string())?;
    if !refresh.is_finite() || refresh <= 0.0 {
        return Err("Refresh rate must be a positive number".into());
    }
    let millihz = (refresh * 1_000.0).round();
    if millihz > u32::MAX as f64 {
        return Err("Refresh rate is too large".into());
    }
    Ok(millihz as u32)
}

fn format_refresh_input(refresh_millihz: u32) -> String {
    if refresh_millihz.is_multiple_of(1_000) {
        (refresh_millihz / 1_000).to_string()
    } else {
        let value = format!("{:.3}", f64::from(refresh_millihz) / 1_000.0);
        value
            .trim_end_matches('0')
            .trim_end_matches('.')
            .to_string()
    }
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

fn group_stream_mode(group: &GroupDraft) -> Result<VirtualMode, String> {
    let request = group.sizing.ok_or_else(|| {
        format!(
            "Configure streaming screen geometry and a reference display for Output {}",
            group.id + 1
        )
    })?;
    let result = request
        .calculate()
        .map_err(|error| format!("Output {} geometry: {error}", group.id + 1))?;
    let refresh_millihz = parse_stream_refresh_millihz(&group.stream_refresh)
        .map_err(|error| format!("Output {}: {error}", group.id + 1))?;
    VirtualMode::from_millihz(
        result.virtual_mode.width,
        result.virtual_mode.height,
        refresh_millihz,
    )
    .map_err(|error| format!("Output {}: {error}", group.id + 1))
}

fn build_mapping_plan(groups: &[GroupDraft]) -> Result<MappingPlan, String> {
    let requests = groups
        .iter()
        .map(|group| {
            let (mode, route) = if group.stream_only {
                (group_stream_mode(group)?, MappingRoute::StreamOnly)
            } else {
                let target = group
                    .target_id
                    .clone()
                    .ok_or_else(|| format!("Choose a target screen for Output {}", group.id + 1))?;
                let mode = match group.sizing {
                    Some(request) => {
                        let result = request.calculate().map_err(|error| {
                            format!("Output {} geometry: {error}", group.id + 1)
                        })?;
                        VirtualMode::from_millihz(
                            result.virtual_mode.width,
                            result.virtual_mode.height,
                            result.preferred_refresh_millihz.unwrap_or(240_000),
                        )
                        .map_err(|error| format!("Output {}: {error}", group.id + 1))?
                    }
                    None => VirtualMode::default(),
                };
                (mode, MappingRoute::Mirror { target })
            };
            Ok(MappingGroupRequest {
                id: group.id,
                mode,
                rotation: if group.stream_only {
                    rotation_from_index(group.stream_rotation_index)?
                } else {
                    group
                        .sizing
                        .map(|request| request.target.rotation)
                        .unwrap_or(Rotation::Deg0)
                },
                route,
            })
        })
        .collect::<Result<Vec<_>, String>>()?;
    MappingPlan::new(requests).map_err(|error| error.to_string())
}

fn set_displays(ui: &QuickAccess, displays: &[DisplayOption]) {
    let previous_target_id = selected_display_id(ui, ui.get_selected_display());
    let previous_reference = selected_reference_source(ui);

    let names: Vec<_> = displays
        .iter()
        .map(|display| SharedString::from(display.name.as_str()))
        .collect();
    let labels: Vec<_> = displays
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
    let ids: Vec<_> = displays
        .iter()
        .map(|display| SharedString::from(display.id.as_str()))
        .collect();
    let widths: Vec<_> = displays.iter().map(|display| display.width).collect();
    let heights: Vec<_> = displays.iter().map(|display| display.height).collect();
    let target_id = previous_target_id
        .and_then(|id| displays.iter().position(|display| display.id == id))
        .map(|index| index as i32)
        .unwrap_or(if displays.is_empty() { -1 } else { 0 });
    ui.set_display_names(ModelRc::new(Rc::new(VecModel::from(names))));
    ui.set_display_labels(ModelRc::new(Rc::new(VecModel::from(labels))));
    ui.set_display_ids(ModelRc::new(Rc::new(VecModel::from(ids))));
    ui.set_display_widths(ModelRc::new(Rc::new(VecModel::from(widths))));
    ui.set_display_heights(ModelRc::new(Rc::new(VecModel::from(heights))));
    ui.set_selected_display(target_id);
    rebuild_reference_options(ui, displays, previous_reference);
    update_reference_action(ui, displays);

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
    let outcome = match ConfigStore::default_store().and_then(|store| store.load()) {
        Ok(outcome) => outcome,
        Err(error) => {
            set_geometry_unconfigured(ui);
            ui.set_geometry_save_blocked(true);
            ui.set_geometry_error(error.to_string().into());
            return default_loaded_groups();
        }
    };
    if let Some(warning) = outcome.warning {
        set_geometry_unconfigured(ui);
        ui.set_geometry_save_blocked(true);
        ui.set_geometry_error(warning.into());
        return default_loaded_groups();
    }
    ui.set_geometry_save_blocked(false);
    let groups = outcome
        .config
        .groups
        .iter()
        .map(GroupDraft::from_config)
        .collect::<Vec<_>>();
    let active = outcome
        .config
        .selected_group_id
        .and_then(|id| groups.iter().position(|group| group.id == id))
        .unwrap_or(0);
    LoadedGroups { groups, active }
}

fn default_loaded_groups() -> LoadedGroups {
    LoadedGroups {
        groups: vec![GroupDraft::new(0)],
        active: 0,
    }
}

fn persist_groups(ui: &QuickAccess, groups: &[GroupDraft], active: usize) -> bool {
    if ui.get_geometry_save_blocked() {
        surface_persistence_error(
            ui,
            "The saved configuration is invalid; reset or repair it before saving",
        );
        return false;
    }
    let persisted = match groups
        .iter()
        .map(GroupDraft::to_config)
        .collect::<Result<Vec<_>, _>>()
    {
        Ok(groups) => groups,
        Err(error) => {
            surface_persistence_error(ui, &error);
            return false;
        }
    };
    let Some(selected_group_id) = groups.get(active).map(|group| group.id) else {
        surface_persistence_error(ui, "The selected mapping group no longer exists");
        return false;
    };
    let config = AppConfig {
        groups: persisted,
        selected_group_id: Some(selected_group_id),
        ..AppConfig::default()
    };
    let result = ConfigStore::default_store().and_then(|store| store.save(&config));
    match result {
        Ok(()) => true,
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

fn update_geometry_preview(ui: &QuickAccess, displays: &[DisplayOption]) {
    update_resolution_suggestions(ui);
    ui.set_geometry_error("".into());
    ui.set_geometry_valid(false);
    let request = match geometry_request(ui, displays) {
        Ok(Some(request)) => request,
        Ok(None) => {
            ui.set_geometry_result("Enter display measurements".into());
            ui.set_geometry_result_detail("Enter pixels, diagonal inches, and aspect ratio".into());
            return;
        }
        Err(error) => {
            ui.set_geometry_result("Check the values".into());
            ui.set_geometry_result_detail("".into());
            ui.set_geometry_error(error.into());
            return;
        }
    };
    match request.calculate() {
        Ok(result) => set_geometry_result(ui, result, false),
        Err(error) => {
            ui.set_geometry_result("Check the values".into());
            ui.set_geometry_result_detail("".into());
            ui.set_geometry_error(error.to_string().into());
        }
    }
}

const COMMON_WIDTHS: &[&str] = &[
    "1280", "1920", "2560", "3440", "3840", "4096", "5120", "6016", "7680",
];
const COMMON_HEIGHTS: &[&str] = &[
    "720", "1080", "1440", "1600", "2160", "2880", "3384", "4320",
];
const DEFAULT_STREAM_ASPECT_WIDTH: u64 = 16;
const DEFAULT_STREAM_ASPECT_HEIGHT: u64 = 10;

fn resolution_suggestion(input: &str, candidates: &[&'static str]) -> Option<&'static str> {
    let input = input.trim();
    if input.is_empty() || !input.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }

    let mut matches = candidates
        .iter()
        .copied()
        .filter(|candidate| candidate.len() > input.len() && candidate.starts_with(input));
    let suggestion = matches.next()?;
    matches.next().is_none().then_some(suggestion)
}

fn set_resolution_suggestion(
    input: &str,
    candidates: &[&'static str],
    set_suggestion: impl FnOnce(SharedString),
    set_suffix: impl FnOnce(SharedString),
) {
    let suggestion = resolution_suggestion(input, candidates);
    set_suggestion(suggestion.unwrap_or_default().into());
    set_suffix(
        suggestion
            .map(|candidate| &candidate[input.trim().len()..])
            .unwrap_or_default()
            .into(),
    );
}

fn set_stream_dimension_suggestion(
    input: &str,
    other_dimension: &str,
    candidates: &[&'static str],
    aspect_numerator: u64,
    aspect_denominator: u64,
    set_suggestion: impl FnOnce(SharedString),
    set_suffix: impl FnOnce(SharedString),
) {
    let input = input.trim();
    let suggestion = if input.is_empty() {
        scaled_dimension_suggestion(other_dimension, aspect_numerator, aspect_denominator)
    } else {
        resolution_suggestion(input, candidates).map(str::to_owned)
    };
    let suffix = suggestion
        .as_deref()
        .and_then(|candidate| candidate.strip_prefix(input))
        .unwrap_or_default();
    set_suggestion(suggestion.clone().unwrap_or_default().into());
    set_suffix(suffix.into());
}

fn scaled_dimension_suggestion(source: &str, numerator: u64, denominator: u64) -> Option<String> {
    let source = source.trim().parse::<u64>().ok()?;
    if source == 0 || denominator == 0 {
        return None;
    }
    let scaled = source
        .checked_mul(numerator)?
        .checked_add(denominator / 2)?
        / denominator;
    (scaled > 0 && scaled <= u64::from(MAX_VIRTUAL_DIMENSION)).then(|| scaled.to_string())
}

fn stream_aspect_ratio(width: &str, height: &str) -> Option<String> {
    let width = width.trim().parse::<u32>().ok()?;
    let height = height.trim().parse::<u32>().ok()?;
    if width == 0 || height == 0 {
        return None;
    }
    let divisor = greatest_common_divisor(width, height);
    let reduced_width = width / divisor;
    let reduced_height = height / divisor;
    if (reduced_width, reduced_height) == (8, 5) {
        Some("16:10".into())
    } else {
        Some(format!("{reduced_width}:{reduced_height}"))
    }
}

fn greatest_common_divisor(mut left: u32, mut right: u32) -> u32 {
    while right != 0 {
        (left, right) = (right, left % right);
    }
    left
}

fn update_resolution_suggestions(ui: &QuickAccess) {
    set_resolution_suggestion(
        ui.get_reference_width().as_str(),
        COMMON_WIDTHS,
        |value| ui.set_reference_width_suggestion(value),
        |value| ui.set_reference_width_suffix(value),
    );
    set_resolution_suggestion(
        ui.get_reference_height().as_str(),
        COMMON_HEIGHTS,
        |value| ui.set_reference_height_suggestion(value),
        |value| ui.set_reference_height_suffix(value),
    );
}

fn calculate_geometry(ui: &QuickAccess, displays: &[DisplayOption]) {
    update_geometry_preview(ui, displays);
    if !ui.get_geometry_valid() {
        if !ui.get_geometry_page() && !ui.get_geometry_error().is_empty() {
            surface_geometry_error(ui, ui.get_geometry_error().as_str());
        }
        return;
    }
    if ui.get_stream_only() {
        validate_stream_fields(ui, displays);
        return;
    }
    let request = match geometry_request(ui, displays) {
        Ok(Some(request)) => request,
        Ok(_) => return,
        Err(error) => {
            surface_geometry_error(ui, &error);
            return;
        }
    };
    let result = request
        .calculate()
        .expect("validated geometry request became invalid");
    set_geometry_result(ui, result, true);
}

fn selected_option<'a>(
    ui: &QuickAccess,
    displays: &'a [DisplayOption],
    index: i32,
) -> Option<&'a DisplayOption> {
    let id = selected_display_id(ui, index)?;
    (id != STREAM_ONLY_ID)
        .then(|| displays.iter().find(|display| display.id == id))
        .flatten()
}

fn update_reference_action(ui: &QuickAccess, displays: &[DisplayOption]) {
    let reference_source = selected_reference_source(ui);
    let reference_manual = matches!(reference_source, Some(ReferenceSource::Manual));
    ui.set_reference_manual(reference_manual);
    let target_ready = if ui.get_stream_only() {
        aspect_ratio_from_index(ui.get_stream_aspect_ratio_index())
            .and_then(|aspect_ratio| {
                let rotation = rotation_from_index(ui.get_stream_rotation())?;
                stream_target_geometry(
                    ui.get_stream_width().as_str(),
                    ui.get_stream_height().as_str(),
                    ui.get_stream_diagonal().as_str(),
                    aspect_ratio,
                    rotation,
                )
            })
            .is_ok()
    } else {
        automatic_target_geometry(ui, displays, "Target").is_ok()
    };
    let enabled = if reference_manual {
        target_ready
    } else {
        target_ready
            && selected_reference_display_index(ui, displays)
                .and_then(|index| automatic_display_geometry(displays, index, "Reference").ok())
                .is_some()
    };
    ui.set_reference_action_enabled(enabled);
}

fn surface_geometry_error(ui: &QuickAccess, error: &str) {
    ui.set_geometry_error(error.into());
    if !ui.get_geometry_page() {
        ui.set_error_text(error.into());
        ui.set_state_detail("Geometry calculation needs attention".into());
    }
}

fn geometry_request(
    ui: &QuickAccess,
    displays: &[DisplayOption],
) -> Result<Option<SizingRequest>, String> {
    let target = if ui.get_stream_only() {
        stream_target_geometry(
            ui.get_stream_width().as_str(),
            ui.get_stream_height().as_str(),
            ui.get_stream_diagonal().as_str(),
            aspect_ratio_from_index(ui.get_stream_aspect_ratio_index())?,
            rotation_from_index(ui.get_stream_rotation())?,
        )?
    } else {
        automatic_target_geometry(ui, displays, "Target")?
    };
    let reference = if ui.get_reference_manual() {
        let values = [
            ui.get_reference_width(),
            ui.get_reference_height(),
            ui.get_reference_diagonal(),
        ];
        if values.iter().any(|value| value.trim().is_empty()) {
            return Ok(None);
        }
        DisplayGeometry {
            native_pixels: PixelSize {
                width: parse_u32(&values[0], "Reference width")?,
                height: parse_u32(&values[1], "Reference height")?,
            },
            physical: PhysicalMeasurement::DiagonalMm(
                parse_f64(&values[2], "Reference diagonal")? * 25.4,
            ),
            aspect_ratio: Some(aspect_ratio_from_index(ui.get_reference_aspect_ratio())?),
            rotation: rotation_from_index(ui.get_reference_rotation())?,
        }
    } else {
        let reference_index = selected_reference_display_index(ui, displays)
            .ok_or_else(|| "Reference display is not selected".to_string())?;
        automatic_display_geometry(displays, reference_index, "Reference")?
    };
    Ok(Some(SizingRequest {
        reference,
        target,
        strategy: sizing_strategy_from_index(ui.get_sizing_strategy())?,
        alignment: 2,
        preferred_refresh_millihz: Some(if ui.get_stream_only() {
            parse_stream_refresh_millihz(ui.get_stream_refresh().as_str())?
        } else {
            240_000
        }),
    }))
}

fn stream_target_geometry(
    width: &str,
    height: &str,
    diagonal: &str,
    aspect_ratio: AspectRatio,
    rotation: Rotation,
) -> Result<DisplayGeometry, String> {
    let width = parse_u32(width, "Streaming screen width")?;
    let height = parse_u32(height, "Streaming screen height")?;
    if width == 0 || height == 0 {
        return Err("Streaming screen dimensions must be positive".into());
    }
    let diagonal_inches = parse_screen_diagonal(diagonal)?;
    Ok(DisplayGeometry {
        native_pixels: PixelSize { width, height },
        physical: PhysicalMeasurement::DiagonalMm(diagonal_inches * 25.4),
        aspect_ratio: Some(aspect_ratio),
        rotation,
    })
}

fn automatic_display_geometry(
    displays: &[DisplayOption],
    index: i32,
    role: &str,
) -> Result<DisplayGeometry, String> {
    let display = (index >= 0)
        .then(|| displays.get(index as usize))
        .flatten()
        .ok_or_else(|| format!("{role} display is not selected"))?;
    let (Some(width_mm), Some(height_mm)) = (display.physical_width_mm, display.physical_height_mm)
    else {
        return Err(format!(
            "{role} display physical size is unavailable; choose Manual or reconnect it directly"
        ));
    };
    Ok(DisplayGeometry {
        native_pixels: PixelSize {
            width: display.native_width,
            height: display.native_height,
        },
        physical: PhysicalMeasurement::DimensionsMm {
            width: width_mm,
            height: height_mm,
        },
        aspect_ratio: None,
        rotation: display.rotation,
    })
}

fn automatic_target_geometry(
    ui: &QuickAccess,
    displays: &[DisplayOption],
    role: &str,
) -> Result<DisplayGeometry, String> {
    let display = selected_option(ui, displays, ui.get_selected_display())
        .ok_or_else(|| format!("{role} display is not selected"))?;
    display_geometry(display, role)
}

fn display_geometry(display: &DisplayOption, role: &str) -> Result<DisplayGeometry, String> {
    let (Some(width_mm), Some(height_mm)) = (display.physical_width_mm, display.physical_height_mm)
    else {
        return Err(format!(
            "{role} display physical size is unavailable; choose Manual or reconnect it directly"
        ));
    };
    Ok(DisplayGeometry {
        native_pixels: PixelSize {
            width: display.native_width,
            height: display.native_height,
        },
        physical: PhysicalMeasurement::DimensionsMm {
            width: width_mm,
            height: height_mm,
        },
        aspect_ratio: None,
        rotation: display.rotation,
    })
}

fn selected_reference_display_index(ui: &QuickAccess, displays: &[DisplayOption]) -> Option<i32> {
    let Some(ReferenceSource::Display(id)) = selected_reference_source(ui) else {
        return None;
    };
    displays
        .iter()
        .position(|display| display.id == id)
        .map(|index| index as i32)
}

fn parse_u32(value: &str, name: &str) -> Result<u32, String> {
    value
        .trim()
        .parse()
        .map_err(|_| format!("{name} must be a positive whole number"))
}

fn parse_f64(value: &str, name: &str) -> Result<f64, String> {
    value
        .trim()
        .parse()
        .map_err(|_| format!("{name} must be a number in inches"))
}

fn aspect_ratio_from_index(index: i32) -> Result<AspectRatio, String> {
    let (width, height) = match index {
        0 => (16, 9),
        1 => (16, 10),
        2 => (3, 2),
        3 => (4, 3),
        4 => (21, 9),
        5 => (32, 9),
        _ => return Err("Choose a supported aspect ratio".into()),
    };
    Ok(AspectRatio { width, height })
}

fn sizing_strategy_from_index(index: i32) -> Result<SizingStrategy, String> {
    match index {
        0 => Ok(SizingStrategy::MatchPhysicalSize),
        1 => Ok(SizingStrategy::RoundedScale),
        2 => Ok(SizingStrategy::IntegerScale),
        _ => Err("Choose a supported sizing strategy".into()),
    }
}

fn sizing_strategy_index(strategy: SizingStrategy) -> i32 {
    match strategy {
        SizingStrategy::MatchPhysicalSize => 0,
        SizingStrategy::RoundedScale => 1,
        SizingStrategy::IntegerScale => 2,
    }
}

fn rotation_from_index(index: i32) -> Result<Rotation, String> {
    match index {
        0 => Ok(Rotation::Deg0),
        1 => Ok(Rotation::Deg180),
        2 => Ok(Rotation::Deg90),
        3 => Ok(Rotation::Deg270),
        _ => Err("Choose a supported display orientation".into()),
    }
}

fn rotation_index(rotation: Rotation) -> i32 {
    match rotation {
        Rotation::Deg0 => 0,
        Rotation::Deg180 => 1,
        Rotation::Deg90 => 2,
        Rotation::Deg270 => 3,
    }
}

fn rotation_label(index: i32) -> Option<&'static str> {
    match index {
        0 => Some("Landscape"),
        1 => Some("Landscape flipped"),
        2 => Some("Portrait clockwise"),
        3 => Some("Portrait counter-clockwise"),
        _ => None,
    }
}

fn populate_geometry_fields(ui: &QuickAccess, request: SizingRequest) {
    ui.set_reference_width(request.reference.native_pixels.width.to_string().into());
    ui.set_reference_height(request.reference.native_pixels.height.to_string().into());
    ui.set_reference_diagonal(format!("{:.1}", diagonal_mm(request.reference) / 25.4).into());
    ui.set_reference_aspect_ratio(aspect_ratio_index(request.reference));
    ui.set_reference_rotation(rotation_index(request.reference.rotation));
    ui.set_sizing_strategy(sizing_strategy_index(request.strategy));
}

fn diagonal_mm(display: DisplayGeometry) -> f64 {
    match display.physical {
        PhysicalMeasurement::DiagonalMm(value) => value,
        PhysicalMeasurement::DimensionsMm { width, height } => width.hypot(height),
    }
}

const ASPECT_RATIOS: [AspectRatio; 6] = [
    AspectRatio {
        width: 16,
        height: 9,
    },
    AspectRatio {
        width: 16,
        height: 10,
    },
    AspectRatio {
        width: 3,
        height: 2,
    },
    AspectRatio {
        width: 4,
        height: 3,
    },
    AspectRatio {
        width: 21,
        height: 9,
    },
    AspectRatio {
        width: 32,
        height: 9,
    },
];

fn aspect_ratio_index(display: DisplayGeometry) -> i32 {
    let ratio = display.aspect_ratio.unwrap_or(AspectRatio {
        width: display.native_pixels.width,
        height: display.native_pixels.height,
    });
    aspect_ratio_option_index(ratio)
}

fn aspect_ratio_option_index(ratio: AspectRatio) -> i32 {
    ASPECT_RATIOS
        .iter()
        .enumerate()
        .min_by(|(_, left), (_, right)| {
            aspect_ratio_error(ratio, **left).total_cmp(&aspect_ratio_error(ratio, **right))
        })
        .map(|(index, _)| index as i32)
        .unwrap_or(0)
}

fn screen_override_aspect_ratio(index: i32) -> Result<Option<AspectRatio>, String> {
    if index == 0 {
        Ok(None)
    } else {
        aspect_ratio_from_index(index - 1).map(Some)
    }
}

fn aspect_ratio_error(left: AspectRatio, right: AspectRatio) -> f64 {
    (left.width as f64 / left.height as f64 - right.width as f64 / right.height as f64).abs()
}

fn set_geometry_result(ui: &QuickAccess, result: SizingResult, configured: bool) {
    let mode = format!(
        "{} × {}",
        result.virtual_mode.width, result.virtual_mode.height
    );
    ui.set_geometry_result(mode.as_str().into());
    ui.set_geometry_result_detail(
        format!("Scale {:.2}× × {:.2}×", result.scale_x, result.scale_y).into(),
    );
    ui.set_geometry_valid(!ui.get_geometry_save_blocked());
    ui.set_geometry_error("".into());
    if configured {
        let refresh = result
            .preferred_refresh_millihz
            .map(format_refresh_millihz)
            .unwrap_or_else(|| "Driver refresh".into());
        ui.set_geometry_configured(true);
        ui.set_geometry_summary(mode.as_str().into());
        ui.set_geometry_summary_detail(format!("Planned · {refresh}").into());
        ui.set_state_detail(format!("Planned {mode} · Preview only").into());
    }
}

fn format_refresh_millihz(refresh: u32) -> String {
    if refresh.is_multiple_of(1_000) {
        format!("{} Hz", refresh / 1_000)
    } else {
        format!("{:.2} Hz", f64::from(refresh) / 1_000.0)
    }
}

fn set_geometry_unconfigured(ui: &QuickAccess) {
    ui.set_geometry_valid(false);
    ui.set_geometry_configured(false);
    ui.set_geometry_summary("3840 × 2160".into());
    ui.set_geometry_summary_detail("Driver · 240 Hz".into());
    ui.set_geometry_result("Enter display measurements".into());
    ui.set_geometry_result_detail("Enter pixels, diagonal inches, and aspect ratio".into());
}

fn reposition_after_layout(ui: slint::Weak<QuickAccess>) {
    slint::Timer::single_shot(Duration::ZERO, move || {
        if let Some(ui) = ui.upgrade() {
            win32_flyout::position(ui.window());
            ui.window().request_redraw();
        }
    });
}

fn show_flyout(ui: &QuickAccess, focus: &Arc<AtomicBool>) {
    focus.store(false, Ordering::Relaxed);
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
    focus.store(activated, Ordering::Relaxed);
    diagnostics::log(
        Level::Debug,
        "ui",
        "flyout-activate",
        None,
        format!("attempt=immediate foreground={activated}"),
    );

    let ui = ui.as_weak();
    let focus = focus.clone();
    slint::Timer::single_shot(Duration::ZERO, move || {
        if let Some(ui) = ui.upgrade() {
            let activated = focus.load(Ordering::Relaxed) || win32_flyout::activate(ui.window());
            focus.store(activated, Ordering::Relaxed);
            diagnostics::log(
                Level::Debug,
                "ui",
                "flyout-activate",
                None,
                format!("attempt=deferred foreground={activated}"),
            );
            ui.window().request_redraw();
        }
    });
}

#[cfg(test)]
mod tests {
    use super::{
        COMMON_HEIGHTS, COMMON_WIDTHS, GroupDraft, active_after_removal, aspect_ratio_from_index,
        build_mapping_plan, dimensions_from_diagonal, format_recent_diagnostics, group_tab_detail,
        is_guid, reference_candidate_indices, reference_selection_index, resolution_suggestion,
        rotation_from_index, rotation_index, rotation_label, scaled_dimension_suggestion,
        screen_override_aspect_ratio, sizing_strategy_from_index, sizing_strategy_index,
        stream_aspect_ratio, stream_target_geometry, sunshine_web_url,
    };
    use crate::config::ReferenceSource;
    use crate::controller::DisplayOption;
    use crate::diagnostics::{Level, Record};
    use crate::geometry::{
        AspectRatio, DisplayGeometry, PhysicalMeasurement, PixelSize, Rotation, SizingRequest,
        SizingStrategy,
    };
    use crate::mapping::MappingRoute;

    fn display_option(id: &str, name: &str) -> DisplayOption {
        DisplayOption {
            id: id.into(),
            name: name.into(),
            label: format!("{name} · 2560×1440"),
            width: 2560,
            height: 1440,
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
        assert!((width.hypot(height) / 25.4 - 24.0).abs() < 0.001);
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

        let plan = build_mapping_plan(&[stream]).unwrap();
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
        assert!(preview.ends_with("record-44 cleanup context"));
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

        let plan = build_mapping_plan(&[mirror, stream]).expect("mixed plan should be valid");
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
                physical: PhysicalMeasurement::DiagonalMm(31.5 * 25.4),
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

        let config = original.to_config().unwrap();
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

        let plan = build_mapping_plan(&[mirror]).unwrap();
        assert_eq!(plan.groups[0].rotation, Rotation::Deg90);
    }

    #[test]
    fn invalid_stream_screen_is_rejected_before_controller_start() {
        let mut stream = GroupDraft::new(0);
        stream.stream_only = true;
        stream.stream_width = "4k".into();
        assert!(build_mapping_plan(&[stream]).is_err());
    }
}
