use std::sync::atomic::AtomicU64;
use std::sync::{Arc, Mutex};

use crate::config::{DisplayOverrideStore, DisplayOverrides};
use crate::controller::{ControllerSender, DisplayOption};
use crate::diagnostics::{self, Level};
use crate::limits::MAX_OUTPUTS;

use super::*;

#[derive(Clone)]
pub(super) struct HandlerState {
    displays: Arc<Mutex<Vec<DisplayOption>>>,
    groups: Arc<Mutex<Vec<GroupDraft>>>,
    active_group: Arc<Mutex<usize>>,
    display_overrides: Arc<Mutex<DisplayOverrides>>,
    profile_revision: Arc<Mutex<ProfileRevision>>,
}

impl HandlerState {
    pub(super) fn new(
        displays: Arc<Mutex<Vec<DisplayOption>>>,
        groups: Arc<Mutex<Vec<GroupDraft>>>,
        active_group: Arc<Mutex<usize>>,
        display_overrides: Arc<Mutex<DisplayOverrides>>,
        profile_revision: Arc<Mutex<ProfileRevision>>,
    ) -> Self {
        Self {
            displays,
            groups,
            active_group,
            display_overrides,
            profile_revision,
        }
    }
}

pub(super) fn register_lifecycle_handlers(
    flyout: &QuickAccess,
    sender: &ControllerSender,
    state: &HandlerState,
    error_revision: &Arc<AtomicU64>,
) {
    let flyout_weak = flyout.as_weak();
    flyout.on_dismiss(move || {
        if let Some(flyout) = flyout_weak.upgrade() {
            let _ = flyout.hide();
        }
    });

    let start_sender = sender.clone();
    let flyout_weak = flyout.as_weak();
    let start_state = state.clone();
    flyout.on_start(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = start_state
                .displays
                .lock()
                .expect("display metadata poisoned");
            let mut groups = start_state.groups.lock().expect("group drafts poisoned");
            let active = *start_state
                .active_group
                .lock()
                .expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            let validated = match validate_group_drafts(&groups) {
                Ok(validated) => validated,
                Err(error) => {
                    surface_persistence_error(&ui, &error);
                    return;
                }
            };
            if !persist_validated_groups(&ui, &validated, active, &start_state.profile_revision) {
                return;
            }
            match build_mapping_plan(&validated) {
                Ok(plan) => {
                    for group in groups.iter_mut() {
                        group.reset_telemetry();
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
    let restart_sender = sender.clone();
    flyout.on_restart_mapping(move || restart_sender.restart_mapping());

    let flyout_weak = flyout.as_weak();
    let display_settings_error_revision = error_revision.clone();
    flyout.on_open_display_settings(move || {
        let Some(ui) = flyout_weak.upgrade() else {
            return;
        };
        if let Err(error) = open_display_settings() {
            diagnostics::log(
                Level::Warn,
                "ui",
                "open-display-settings",
                None,
                error.as_str(),
            );
            surface_transient_error(&ui, &display_settings_error_revision, &error);
        }
    });

    let flyout_weak = flyout.as_weak();
    let sunshine_state = state.clone();
    let sunshine_error_revision = error_revision.clone();
    flyout.on_open_sunshine_panel(move || {
        let Some(ui) = flyout_weak.upgrade() else {
            return;
        };
        let groups = sunshine_state.groups.lock().expect("group drafts poisoned");
        let active = *sunshine_state
            .active_group
            .lock()
            .expect("active group poisoned");
        let Some(port) = groups
            .get(active)
            .filter(|group| {
                group.stream_only
                    && group.telemetry.ready
                    && group.telemetry.sunshine_state == SunshineState::Ready
            })
            .and_then(|group| group.telemetry.sunshine_port)
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
}

pub(super) fn register_geometry_handlers(flyout: &QuickAccess, state: &HandlerState) {
    let flyout_weak = flyout.as_weak();
    let callback_state = state.clone();
    flyout.on_display_selected(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_state
                .displays
                .lock()
                .expect("display metadata poisoned");
            let selected_id = selected_display_id(&ui, ui.get_selected_display());
            ui.set_stream_only(selected_id.as_deref() == Some(STREAM_ONLY_ID));
            let previous_reference = selected_reference_source(&ui);
            rebuild_reference_options(&ui, &displays, previous_reference);
            update_reference_action(&ui, &displays);
            let mut groups = callback_state.groups.lock().expect("group drafts poisoned");
            let active = *callback_state
                .active_group
                .lock()
                .expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            update_tab_projection(&ui, &groups, active);
            project_group_telemetry(&ui, &groups, active);
            persist_groups(&ui, &groups, active, &callback_state.profile_revision);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_state = state.clone();
    flyout.on_reference_selected(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_state
                .displays
                .lock()
                .expect("display metadata poisoned");
            update_reference_action(&ui, &displays);
            if ui.get_stream_only() {
                validate_stream_fields(&ui, &displays);
            }
            let mut groups = callback_state.groups.lock().expect("group drafts poisoned");
            let active = *callback_state
                .active_group
                .lock()
                .expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            update_tab_projection(&ui, &groups, active);
            persist_groups(&ui, &groups, active, &callback_state.profile_revision);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_state = state.clone();
    flyout.on_strategy_selected(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_state
                .displays
                .lock()
                .expect("display metadata poisoned");
            if ui.get_page() == QuickAccessPage::Geometry {
                update_geometry_preview(&ui, &displays);
            } else {
                if ui.get_stream_only() {
                    validate_stream_fields(&ui, &displays);
                }
                let mut groups = callback_state.groups.lock().expect("group drafts poisoned");
                let active = *callback_state
                    .active_group
                    .lock()
                    .expect("active group poisoned");
                snapshot_group(&ui, &displays, &mut groups, active);
                update_tab_projection(&ui, &groups, active);
                persist_groups(&ui, &groups, active, &callback_state.profile_revision);
            }
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_state = state.clone();
    flyout.on_calculate_geometry(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_state
                .displays
                .lock()
                .expect("display metadata poisoned");
            calculate_geometry(&ui, &displays);
            if !ui.get_geometry_valid() {
                return;
            }
            let mut groups = callback_state.groups.lock().expect("group drafts poisoned");
            let active = *callback_state
                .active_group
                .lock()
                .expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            update_tab_projection(&ui, &groups, active);
            persist_groups(&ui, &groups, active, &callback_state.profile_revision);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = state.displays.clone();
    flyout.on_open_manual_geometry(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_page(QuickAccessPage::Geometry);
            let displays = callback_displays.lock().expect("display metadata poisoned");
            update_geometry_preview(&ui, &displays);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_close_geometry(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_page(QuickAccessPage::Main);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_displays = state.displays.clone();
    flyout.on_geometry_edited(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_displays.lock().expect("display metadata poisoned");
            update_geometry_preview(&ui, &displays);
        }
    });

    let flyout_weak = flyout.as_weak();
    let callback_state = state.clone();
    flyout.on_save_geometry(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = callback_state
                .displays
                .lock()
                .expect("display metadata poisoned");
            calculate_geometry(&ui, &displays);
            if !ui.get_geometry_valid() {
                return;
            }
            let mut groups = callback_state.groups.lock().expect("group drafts poisoned");
            let active = *callback_state
                .active_group
                .lock()
                .expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            update_tab_projection(&ui, &groups, active);
            if persist_groups(&ui, &groups, active, &callback_state.profile_revision) {
                ui.set_page(QuickAccessPage::Main);
                reposition_after_layout(ui.as_weak());
            }
        }
    });
}

pub(super) fn register_stream_handlers(flyout: &QuickAccess, state: &HandlerState) {
    let flyout_weak = flyout.as_weak();
    let stream_displays = state.displays.clone();
    flyout.on_open_stream_config(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            update_stream_suggestions(&ui);
            let displays = stream_displays.lock().expect("display metadata poisoned");
            update_reference_action(&ui, &displays);
            validate_stream_fields(&ui, &displays);
            ui.set_page(QuickAccessPage::Stream);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_close_stream_config(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_page(QuickAccessPage::Main);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    let stream_displays = state.displays.clone();
    flyout.on_stream_edited(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            update_stream_suggestions(&ui);
            let displays = stream_displays.lock().expect("display metadata poisoned");
            update_reference_action(&ui, &displays);
            validate_stream_fields(&ui, &displays);
        }
    });

    let flyout_weak = flyout.as_weak();
    let stream_state = state.clone();
    flyout.on_save_stream_config(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = stream_state
                .displays
                .lock()
                .expect("display metadata poisoned");
            if validate_stream_fields(&ui, &displays).is_none() {
                return;
            }
            let mut groups = stream_state.groups.lock().expect("group drafts poisoned");
            let active = *stream_state
                .active_group
                .lock()
                .expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, active);
            update_tab_projection(&ui, &groups, active);
            if persist_groups(&ui, &groups, active, &stream_state.profile_revision) {
                ui.set_page(QuickAccessPage::Main);
                reposition_after_layout(ui.as_weak());
            }
        }
    });
}

pub(super) fn register_screen_size_handlers(
    flyout: &QuickAccess,
    sender: &ControllerSender,
    state: &HandlerState,
    override_store: &DisplayOverrideStore,
) {
    let flyout_weak = flyout.as_weak();
    let screen_displays = state.displays.clone();
    let screen_sender = sender.clone();
    flyout.on_open_screen_size(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = screen_displays.lock().expect("display metadata poisoned");
            populate_screen_size_page(&ui, &displays);
            ui.set_page(QuickAccessPage::ScreenSize);
            screen_sender.refresh();
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_close_screen_size(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_page(QuickAccessPage::Main);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    let screen_displays = state.displays.clone();
    flyout.on_screen_size_edited(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = screen_displays.lock().expect("display metadata poisoned");
            update_screen_size_preview(&ui, &displays);
        }
    });

    let flyout_weak = flyout.as_weak();
    let screen_displays = state.displays.clone();
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
    let screen_state = state.clone();
    let screen_store = override_store.clone();
    flyout.on_save_screen_size(move || {
        let Some(ui) = flyout_weak.upgrade() else {
            return;
        };
        if ui.get_screen_size_save_blocked() {
            return;
        }
        let mut displays = screen_state
            .displays
            .lock()
            .expect("display metadata poisoned");
        let Some(display_id) = selected_display_id(&ui, ui.get_selected_display()) else {
            ui.set_screen_size_error("Target screen is no longer available".into());
            return;
        };
        if display_id == STREAM_ONLY_ID {
            return;
        }
        let manual = ui.get_screen_size_manual_inches();
        let mut overrides = screen_state
            .display_overrides
            .lock()
            .expect("display overrides poisoned");
        let mut candidate = overrides.clone();
        let candidate_result = if manual.trim().is_empty() {
            candidate.remove(&display_id);
            Ok(())
        } else {
            parse_screen_diagonal(manual.as_str()).and_then(|value| {
                let aspect_ratio = screen_override_aspect_ratio(ui.get_screen_size_aspect_ratio())?;
                candidate
                    .upsert(display_id.clone(), value, aspect_ratio)
                    .map_err(|error| error.to_string())
            })
        };
        if let Err(error) = candidate_result.and_then(|_| {
            persist_then_commit_display_overrides(&mut overrides, candidate, |candidate| {
                screen_store
                    .save(candidate)
                    .map_err(|error| error.to_string())
            })
        }) {
            ui.set_screen_size_error(error.into());
            return;
        }
        apply_display_overrides(&mut displays, &overrides);
        let mut groups = screen_state.groups.lock().expect("group drafts poisoned");
        refresh_group_sizing(&mut groups, &displays);
        refresh_group_tab_details(&mut groups, &displays);
        let active = *screen_state
            .active_group
            .lock()
            .expect("active group poisoned");
        hydrate_group(&ui, &displays, &groups, active);
        ui.set_page(QuickAccessPage::Main);
        reposition_after_layout(ui.as_weak());
    });
}

fn persist_then_commit_display_overrides(
    current: &mut DisplayOverrides,
    candidate: DisplayOverrides,
    persist: impl FnOnce(&DisplayOverrides) -> Result<(), String>,
) -> Result<(), String> {
    persist(&candidate)?;
    *current = candidate;
    Ok(())
}

pub(super) fn register_group_handlers(flyout: &QuickAccess, state: &HandlerState) {
    let flyout_weak = flyout.as_weak();
    let tab_state = state.clone();
    flyout.on_tab_selected(move |index| {
        if let Some(ui) = flyout_weak.upgrade() {
            switch_group(
                &ui,
                index,
                &tab_state.groups,
                &tab_state.active_group,
                &tab_state.displays,
                &tab_state.profile_revision,
            );
        }
    });

    let flyout_weak = flyout.as_weak();
    let wheel_state = state.clone();
    flyout.on_tab_wheel(move |delta| {
        if let Some(ui) = flyout_weak.upgrade() {
            let current = *wheel_state
                .active_group
                .lock()
                .expect("active group poisoned");
            let count = wheel_state
                .groups
                .lock()
                .expect("group drafts poisoned")
                .len();
            let next = if delta > 0 {
                (current + 1).min(count.saturating_sub(1))
            } else {
                current.saturating_sub(1)
            };
            switch_group(
                &ui,
                next as i32,
                &wheel_state.groups,
                &wheel_state.active_group,
                &wheel_state.displays,
                &wheel_state.profile_revision,
            );
        }
    });

    let flyout_weak = flyout.as_weak();
    let add_state = state.clone();
    flyout.on_add_group(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = add_state
                .displays
                .lock()
                .expect("display metadata poisoned");
            let mut groups = add_state.groups.lock().expect("group drafts poisoned");
            let current = *add_state
                .active_group
                .lock()
                .expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, current);
            if groups.len() >= MAX_OUTPUTS {
                return;
            }
            let id = (0..MAX_OUTPUTS as u32)
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
            *add_state
                .active_group
                .lock()
                .expect("active group poisoned") = next;
            hydrate_group(&ui, &displays, &groups, next);
            persist_groups(&ui, &groups, next, &add_state.profile_revision);
        }
    });

    let flyout_weak = flyout.as_weak();
    let remove_state = state.clone();
    flyout.on_remove_group(move |index| {
        if let Some(ui) = flyout_weak.upgrade() {
            let displays = remove_state
                .displays
                .lock()
                .expect("display metadata poisoned");
            let mut groups = remove_state.groups.lock().expect("group drafts poisoned");
            if groups.len() <= 1 || index < 0 || index as usize >= groups.len() {
                return;
            }
            let current = *remove_state
                .active_group
                .lock()
                .expect("active group poisoned");
            snapshot_group(&ui, &displays, &mut groups, current);
            let removed = index as usize;
            groups.remove(removed);
            let next = active_after_removal(current, removed, groups.len());
            *remove_state
                .active_group
                .lock()
                .expect("active group poisoned") = next;
            hydrate_group(&ui, &displays, &groups, next);
            persist_groups(&ui, &groups, next, &remove_state.profile_revision);
        }
    });
}

pub(super) fn register_diagnostics_handlers(flyout: &QuickAccess) {
    let flyout_weak = flyout.as_weak();
    flyout.on_open_diagnostics(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            populate_diagnostics(&ui);
            ui.set_page(QuickAccessPage::Diagnostics);
            reposition_after_layout(ui.as_weak());
        }
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_close_diagnostics(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            ui.set_page(QuickAccessPage::Main);
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
}

#[cfg(test)]
mod tests {
    use super::persist_then_commit_display_overrides;
    use crate::config::DisplayOverrides;
    use crate::geometry::AspectRatio;

    fn overrides_with(display_id: &str, diagonal_inches: f64) -> DisplayOverrides {
        let mut overrides = DisplayOverrides::default();
        overrides
            .upsert(
                display_id.into(),
                diagonal_inches,
                Some(AspectRatio {
                    width: 16,
                    height: 9,
                }),
            )
            .unwrap();
        overrides
    }

    #[test]
    fn failed_override_save_does_not_commit_candidate() {
        let mut current = overrides_with("display-a", 24.0);
        let original = current.clone();
        let candidate = overrides_with("display-a", 27.0);

        let result = persist_then_commit_display_overrides(&mut current, candidate, |_| {
            Err("disk full".into())
        });

        assert_eq!(result, Err("disk full".into()));
        assert_eq!(current, original);
    }

    #[test]
    fn successful_override_save_commits_candidate() {
        let mut current = overrides_with("display-a", 24.0);
        let candidate = overrides_with("display-a", 27.0);
        let expected = candidate.clone();

        persist_then_commit_display_overrides(&mut current, candidate, |persisted| {
            assert_eq!(persisted, &expected);
            Ok(())
        })
        .unwrap();

        assert_eq!(current, expected);
    }
}
