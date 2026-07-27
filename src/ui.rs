use std::error::Error;
use std::rc::Rc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use crate::config::{ConfigStore, ReferenceSource};
use crate::control::{TrayInstance, listen_for_shutdown};
use crate::controller::{Controller, ControllerEvent, DisplayOption};
use crate::geometry::{
    AspectRatio, DisplayGeometry, PhysicalMeasurement, PixelSize, Rotation, SizingRequest,
    SizingResult, SizingStrategy,
};
use crate::win32_flyout;
use slint::winit_030::winit::platform::windows::{CornerPreference, WindowAttributesExtWindows};
use slint::{ComponentHandle, Model, ModelRc, SharedString, VecModel};

slint::include_modules!();

#[derive(Clone, Default)]
struct SelectionPreferences {
    target_id: Option<String>,
    reference_source: Option<ReferenceSource>,
    has_sizing: bool,
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
        let _ = slint::invoke_from_event_loop(|| {
            let _ = slint::quit_event_loop();
        });
    })?;
    let flyout = QuickAccess::new()?;
    let tray = SbmsTray::new()?;
    let preferences = load_geometry_config(&flyout);
    let displays = Arc::new(Mutex::new(Vec::<DisplayOption>::new()));

    let flyout_weak = flyout.as_weak();
    tray.on_tray_clicked(move || {
        if let Some(flyout) = flyout_weak.upgrade() {
            if flyout.window().is_visible() {
                let _ = flyout.hide();
            } else {
                show_flyout(&flyout);
            }
        }
    });

    let flyout_weak = flyout.as_weak();
    tray.on_open_panel(move || {
        if let Some(flyout) = flyout_weak.upgrade() {
            show_flyout(&flyout);
        }
    });
    tray.on_quit(|| {
        let _ = slint::quit_event_loop();
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_dismiss(move || {
        if let Some(flyout) = flyout_weak.upgrade() {
            let _ = flyout.hide();
        }
    });

    let ui = flyout.as_weak();
    let tray_weak = tray.as_weak();
    let error_revision = Arc::new(AtomicU64::new(0));
    let event_error_revision = error_revision.clone();
    let event_displays = displays.clone();
    let event_preferences = preferences.clone();
    let controller = Controller::spawn(move |event| {
        let ui = ui.clone();
        let tray = tray_weak.clone();
        let error_revision = event_error_revision.clone();
        let displays = event_displays.clone();
        let preferences = event_preferences.clone();
        let _ = slint::invoke_from_event_loop(move || {
            if let Some(ui) = ui.upgrade() {
                apply_event(
                    &ui,
                    tray.upgrade().as_ref(),
                    &error_revision,
                    &displays,
                    &preferences,
                    event,
                );
            }
        });
    });
    let sender = controller.sender();

    let start_sender = sender.clone();
    let flyout_weak = flyout.as_weak();
    flyout.on_start(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let index = ui.get_selected_display();
            let ids = ui.get_display_ids();
            if index >= 0
                && let Some(target) = ids.row_data(index as usize)
            {
                start_sender.start(target.to_string());
            }
        }
    });
    let stop_sender = sender.clone();
    flyout.on_stop(move || stop_sender.stop());
    let refresh_sender = sender.clone();
    flyout.on_refresh(move || refresh_sender.refresh());

    let flyout_weak = flyout.as_weak();
    let callback_displays = displays.clone();
    flyout.on_display_selected(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            let previous_reference = selected_reference_source(&ui);
            rebuild_reference_options(&ui, &displays, previous_reference);
            update_reference_action(&ui, &displays);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = displays.clone();
    flyout.on_reference_selected(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            update_reference_action(&ui, &displays);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = displays.clone();
    flyout.on_strategy_selected(move || {
        if let Some(ui) = flyout_weak.upgrade()
            && ui.get_geometry_page()
        {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            update_geometry_preview(&ui, &displays);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = displays.clone();
    flyout.on_calculate_geometry(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            calculate_and_save_geometry(&ui, &displays, false);
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
    flyout.on_save_geometry(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            calculate_and_save_geometry(&ui, &displays, true);
        }
    });

    let dismiss_timer = slint::Timer::default();
    let dismiss_flyout = flyout.as_weak();
    dismiss_timer.start(
        slint::TimerMode::Repeated,
        Duration::from_millis(150),
        move || {
            if let Some(flyout) = dismiss_flyout.upgrade()
                && win32_flyout::lost_focus(flyout.window())
            {
                let _ = flyout.hide();
            }
        },
    );

    sender.refresh();
    tray.show()?;
    if open_on_start {
        show_flyout(&flyout);
    }
    slint::run_event_loop()?;
    controller.shutdown();
    Ok(())
}

fn apply_event(
    ui: &QuickAccess,
    tray: Option<&SbmsTray>,
    error_revision: &Arc<AtomicU64>,
    display_state: &Arc<Mutex<Vec<DisplayOption>>>,
    preferences: &SelectionPreferences,
    event: ControllerEvent,
) {
    match event {
        ControllerEvent::Displays(displays) => {
            set_displays(ui, &displays, preferences);
            *display_state.lock().expect("display metadata poisoned") = displays;
        }
        ControllerEvent::Fps(fps) => {
            if ui.get_running() && !ui.get_mapping_fps_error() {
                ui.set_mapping_fps(fps.min(999) as i32);
                ui.set_mapping_fps_valid(true);
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
            if let Some(tray) = tray {
                tray.set_status(state.into());
            }
            ui.set_state(state.into());
            ui.set_state_detail(detail.into());
            ui.set_running(running);
            ui.set_busy(busy);
            ui.set_error_text(error.as_str().into());
            if !error.is_empty() {
                ui.set_mapping_fps_error(true);
                ui.set_mapping_fps_valid(false);
                let ui = ui.as_weak();
                let error_revision = error_revision.clone();
                slint::Timer::single_shot(Duration::from_secs(6), move || {
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
                ui.set_mapping_fps(0);
                ui.set_mapping_fps_valid(false);
                ui.set_mapping_fps_error(false);
            }
        }
    }
}

fn set_displays(ui: &QuickAccess, displays: &[DisplayOption], preferences: &SelectionPreferences) {
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
        .or_else(|| preferences.target_id.clone())
        .and_then(|id| displays.iter().position(|display| display.id == id))
        .map(|index| index as i32)
        .unwrap_or(if displays.is_empty() { -1 } else { 0 });
    ui.set_display_names(ModelRc::new(Rc::new(VecModel::from(names))));
    ui.set_display_labels(ModelRc::new(Rc::new(VecModel::from(labels))));
    ui.set_display_ids(ModelRc::new(Rc::new(VecModel::from(ids))));
    ui.set_display_widths(ModelRc::new(Rc::new(VecModel::from(widths))));
    ui.set_display_heights(ModelRc::new(Rc::new(VecModel::from(heights))));
    ui.set_selected_display(target_id);
    rebuild_reference_options(
        ui,
        displays,
        previous_reference.or_else(|| preferences.reference_source.clone()),
    );
    update_reference_action(ui, displays);

    if displays.is_empty() {
        ui.set_state("No displays".into());
        ui.set_state_detail("Connect or enable a physical display".into());
    } else {
        ui.set_error_text("".into());
        if !ui.get_running() && !ui.get_busy() {
            ui.set_state("Stopped".into());
            ui.set_state_detail("Choose a display to start".into());
        }
    }
    if preferences.has_sizing
        && let Ok(Some(request)) = geometry_request(ui, displays)
        && let Ok(result) = request.calculate()
    {
        set_geometry_result(ui, result, true);
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

fn load_geometry_config(ui: &QuickAccess) -> SelectionPreferences {
    let outcome = match ConfigStore::default_store().and_then(|store| store.load()) {
        Ok(outcome) => outcome,
        Err(error) => {
            set_geometry_unconfigured(ui);
            ui.set_geometry_save_blocked(true);
            ui.set_geometry_error(error.to_string().into());
            return SelectionPreferences::default();
        }
    };
    if let Some(warning) = outcome.warning {
        set_geometry_unconfigured(ui);
        ui.set_geometry_save_blocked(true);
        ui.set_geometry_error(warning.into());
        return SelectionPreferences::default();
    }
    let preferences = SelectionPreferences {
        target_id: outcome.config.target_id.clone(),
        reference_source: outcome.config.reference_source.clone().or_else(|| {
            outcome
                .config
                .sizing
                .is_some()
                .then_some(ReferenceSource::Manual)
        }),
        has_sizing: outcome.config.sizing.is_some(),
    };
    ui.set_geometry_save_blocked(false);
    let Some(request) = outcome.config.sizing else {
        set_geometry_unconfigured(ui);
        return preferences;
    };
    populate_geometry_fields(ui, request);
    match request.calculate() {
        Ok(result) => set_geometry_result(ui, result, true),
        Err(error) => {
            set_geometry_unconfigured(ui);
            ui.set_geometry_error(error.to_string().into());
        }
    }
    preferences
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

fn calculate_and_save_geometry(ui: &QuickAccess, displays: &[DisplayOption], close_page: bool) {
    update_geometry_preview(ui, displays);
    if !ui.get_geometry_valid() {
        if !ui.get_geometry_page() && !ui.get_geometry_error().is_empty() {
            surface_geometry_error(ui, ui.get_geometry_error().as_str());
        }
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
    let store = match ConfigStore::default_store() {
        Ok(store) => store,
        Err(error) => {
            ui.set_geometry_save_blocked(true);
            ui.set_geometry_valid(false);
            surface_geometry_error(ui, &error.to_string());
            return;
        }
    };
    let outcome = match store.load() {
        Ok(outcome) => outcome,
        Err(error) => {
            ui.set_geometry_save_blocked(true);
            ui.set_geometry_valid(false);
            surface_geometry_error(ui, &error.to_string());
            return;
        }
    };
    if let Some(warning) = outcome.warning {
        ui.set_geometry_save_blocked(true);
        ui.set_geometry_valid(false);
        surface_geometry_error(ui, &warning);
        return;
    }
    let mut config = outcome.config;
    config.target_id =
        selected_option(displays, ui.get_selected_display()).map(|display| display.id.clone());
    config.reference_source = selected_reference_source(ui);
    config.sizing = Some(request);
    if let Err(error) = store.save(&config) {
        surface_geometry_error(ui, &error.to_string());
        return;
    }
    let result = request
        .calculate()
        .expect("validated geometry request became invalid");
    set_geometry_result(ui, result, true);
    if close_page {
        ui.set_geometry_page(false);
        reposition_after_layout(ui.as_weak());
    }
}

fn selected_option(displays: &[DisplayOption], index: i32) -> Option<&DisplayOption> {
    (index >= 0).then(|| displays.get(index as usize)).flatten()
}

fn update_reference_action(ui: &QuickAccess, displays: &[DisplayOption]) {
    let reference_source = selected_reference_source(ui);
    let reference_manual = matches!(reference_source, Some(ReferenceSource::Manual));
    ui.set_reference_manual(reference_manual);
    let target_selected = selected_option(displays, ui.get_selected_display()).is_some();
    let enabled = if reference_manual {
        target_selected
    } else {
        automatic_display_geometry(displays, ui.get_selected_display(), "Target").is_ok()
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
    let target = automatic_display_geometry(displays, ui.get_selected_display(), "Target")?;
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
        preferred_refresh_millihz: Some(240_000),
    }))
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
    ASPECT_RATIOS
        .iter()
        .enumerate()
        .min_by(|(_, left), (_, right)| {
            aspect_ratio_error(ratio, **left).total_cmp(&aspect_ratio_error(ratio, **right))
        })
        .map(|(index, _)| index as i32)
        .unwrap_or(0)
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
        ui.set_geometry_configured(true);
        ui.set_geometry_summary(mode.as_str().into());
        ui.set_geometry_summary_detail("Planned · 240 Hz".into());
        ui.set_state_detail(format!("Planned {mode} · Preview only").into());
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

fn show_flyout(ui: &QuickAccess) {
    win32_flyout::position(ui.window());

    // Slint 1.17's winit software renderer can retain a reused-buffer cache
    // after Windows clears a hidden window during a display-topology change.
    // Taking a snapshot temporarily selects a new repaint buffer and clears
    // that cache, so the visible frame below is rendered in full.
    let _ = ui.window().take_snapshot();
    let _ = ui.show();

    let ui = ui.as_weak();
    slint::Timer::single_shot(Duration::ZERO, move || {
        if let Some(ui) = ui.upgrade() {
            win32_flyout::activate(ui.window());
            ui.window().request_redraw();
        }
    });
}

#[cfg(test)]
mod tests {
    use super::{
        COMMON_HEIGHTS, COMMON_WIDTHS, aspect_ratio_from_index, reference_candidate_indices,
        reference_selection_index, resolution_suggestion, rotation_from_index, rotation_index,
        sizing_strategy_from_index, sizing_strategy_index,
    };
    use crate::config::ReferenceSource;
    use crate::controller::DisplayOption;
    use crate::geometry::{AspectRatio, Rotation, SizingStrategy};

    fn display_option(id: &str, name: &str) -> DisplayOption {
        DisplayOption {
            id: id.into(),
            name: name.into(),
            label: format!("{name} · 2560×1440"),
            width: 2560,
            height: 1440,
            native_width: 2560,
            native_height: 1440,
            physical_width_mm: Some(527.0),
            physical_height_mm: Some(296.0),
            rotation: Rotation::Deg0,
            refresh_numerator: 60_000,
            refresh_denominator: 1_000,
            primary: false,
        }
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
}
