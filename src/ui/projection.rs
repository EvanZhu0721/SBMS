use std::rc::Rc;

use slint::{Model, ModelRc, SharedString, VecModel};

use crate::controller::DisplayOption;

use super::state::{GroupDraft, SunshineState, set_shared_reference};
use super::{
    QuickAccess, QuickAccessPage, ReferenceSource, STREAM_ONLY_ID, group_sizing_request,
    populate_reference_fields, rebuild_reference_options, reference_geometry_from_ui,
    refresh_group_sizing, refresh_group_tab_details, selected_display_id,
    selected_reference_source, set_geometry_result, set_geometry_unconfigured,
    sizing_strategy_from_index, sizing_strategy_index, update_reference_action,
    update_stream_suggestions, validate_stream_fields,
};

pub(super) fn update_tab_projection(ui: &QuickAccess, groups: &[GroupDraft], active: usize) {
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
        labels.push(SharedString::from((index + 1).to_string()));
        details.push(SharedString::from(group.tab_detail.as_str()));
        indices.push(index as i32);
    }
    ui.set_tab_labels(ModelRc::new(Rc::new(VecModel::from(labels))));
    ui.set_tab_details(ModelRc::new(Rc::new(VecModel::from(details))));
    ui.set_tab_indices(ModelRc::new(Rc::new(VecModel::from(indices))));
    ui.set_active_group_index(active as i32);
    ui.set_group_count(groups.len() as i32);
}

pub(super) fn project_group_telemetry(ui: &QuickAccess, groups: &[GroupDraft], active: usize) {
    let Some(group) = groups.get(active) else {
        return;
    };
    let telemetry = &group.telemetry;
    ui.set_mapping_fps(telemetry.fps.unwrap_or_default() as i32);
    ui.set_mapping_fps_valid(telemetry.fps.is_some());
    ui.set_mapping_fps_error(telemetry.error);
    ui.set_mapping_fps_nan(ui.get_stream_only() && !telemetry.error);
    ui.set_sunshine_state(telemetry.sunshine_state.as_i32());
    ui.set_sunshine_port(telemetry.sunshine_port.unwrap_or_default().into());
    ui.set_sunshine_panel_enabled(
        telemetry.ready
            && telemetry.sunshine_state == SunshineState::Ready
            && telemetry.sunshine_port.is_some()
            && group.stream_only,
    );
}

pub(super) fn set_target_options(
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
        .filter(|display| {
            !used.contains(&display.id.as_str())
                && groups
                    .first()
                    .and_then(|group| group.reference_source.as_ref())
                    != Some(&ReferenceSource::Display(display.id.clone()))
        })
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
    let group = &groups[active];
    if !group.stream_only
        && let Some(id) = &group.target_id
        && !ids.iter().any(|candidate| candidate.as_str() == id)
    {
        names.push("Unavailable".into());
        labels.push("Unavailable · Saved target screen".into());
        ids.push(id.as_str().into());
    }
    let stream_index = ids.len();
    names.push("Stream only".into());
    labels.push("Stream only · Create a virtual display".into());
    ids.push(STREAM_ONLY_ID.into());
    ui.set_display_names(ModelRc::new(Rc::new(VecModel::from(names))));
    ui.set_display_labels(ModelRc::new(Rc::new(VecModel::from(labels))));
    ui.set_display_ids(ModelRc::new(Rc::new(VecModel::from(ids))));

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
        .unwrap_or(stream_index);
    ui.set_selected_display(selected as i32);
    ui.set_stream_only(
        ui.get_display_ids()
            .row_data(selected)
            .is_some_and(|id| id.as_str() == STREAM_ONLY_ID),
    );
}

pub(super) fn hydrate_group(
    ui: &QuickAccess,
    displays: &[DisplayOption],
    groups: &[GroupDraft],
    active: usize,
) {
    let Some(group) = groups.get(active) else {
        return;
    };
    set_target_options(ui, displays, groups, active);
    ui.set_high_performance_sampling(group.rendering.high_performance_sampling());
    ui.set_full_frame_copy(group.rendering.full_frame_copy);
    ui.set_static_refresh(group.rendering.static_refresh);
    ui.set_stream_width(group.stream_width.as_str().into());
    ui.set_stream_height(group.stream_height.as_str().into());
    ui.set_stream_diagonal(group.stream_diagonal.as_str().into());
    ui.set_stream_refresh(group.stream_refresh.as_str().into());
    ui.set_stream_aspect_ratio_index(group.stream_aspect_ratio_index);
    ui.set_stream_rotation(group.stream_rotation_index);
    if let Some(reference) = group
        .reference_geometry
        .or(group.sizing.map(|request| request.reference))
    {
        populate_reference_fields(ui, reference);
    } else {
        ui.set_reference_width("".into());
        ui.set_reference_height("".into());
        ui.set_reference_diagonal("".into());
        ui.set_reference_aspect_ratio(0);
        ui.set_reference_rotation(0);
    }
    ui.set_sizing_strategy(sizing_strategy_index(group.sizing_strategy));
    rebuild_reference_options(ui, displays, groups, group.reference_source.clone());
    if let Ok(request) = group_sizing_request(group, displays) {
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
    update_reference_action(ui, displays);
    update_stream_suggestions(ui);
    if group.stream_only {
        validate_stream_fields(ui, displays);
    }
    update_tab_projection(ui, groups, active);
    project_group_telemetry(ui, groups, active);
}

pub(super) fn snapshot_group(
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
    group
        .rendering
        .set_effective_sampling(ui.get_high_performance_sampling());
    group.rendering.full_frame_copy = ui.get_full_frame_copy();
    group.rendering.static_refresh = ui.get_static_refresh();
    let source = selected_reference_source(ui);
    let previous_reference = group
        .reference_geometry
        .or(group.sizing.map(|request| request.reference));
    let reference = if source == Some(ReferenceSource::Manual)
        && source == group.reference_source
        && ui.get_page() != QuickAccessPage::Geometry
    {
        previous_reference
    } else {
        reference_geometry_from_ui(ui, displays)
            .ok()
            .flatten()
            .or_else(|| {
                (source == group.reference_source)
                    .then_some(previous_reference)
                    .flatten()
            })
    };
    if let Ok(strategy) = sizing_strategy_from_index(ui.get_sizing_strategy()) {
        group.sizing_strategy = strategy;
        if let Some(request) = &mut group.sizing {
            request.strategy = strategy;
        }
    }
    group.stream_width = ui.get_stream_width().to_string();
    group.stream_height = ui.get_stream_height().to_string();
    group.stream_diagonal = ui.get_stream_diagonal().to_string();
    group.stream_refresh = ui.get_stream_refresh().to_string();
    group.stream_aspect_ratio_index = ui.get_stream_aspect_ratio_index();
    group.stream_rotation_index = ui.get_stream_rotation();
    set_shared_reference(groups, source, reference);
    refresh_group_sizing(groups, displays);
    refresh_group_tab_details(groups, displays);
}
