use super::*;

// Windows can restore a window's pre-migration monitor when a virtual display
// disappears, even after the user has moved that window onto a physical screen.
// Preserve the physical desktop separately from the virtual-source return list.
pub(crate) struct PhysicalWindowPreservation {
    pub(super) entries: Vec<PhysicalWindowSnapshot>,
}

pub(super) struct PhysicalWindowSnapshot {
    pub(super) window: WindowSnapshot,
    pub(super) display_id: String,
    pub(super) monitor: MONITORINFO,
    pub(super) normal_display_id: String,
    pub(super) normal_monitor: MONITORINFO,
    pub(super) observation: WindowObservation,
}

impl PhysicalWindowPreservation {
    pub(crate) fn capture(displays: &[Display]) -> Result<Self, String> {
        let mut entries = Vec::new();
        for display in displays.iter().filter(|display| !display.virtual_display) {
            let monitor = monitor_for_rect(display.rect, "preserved physical display")?;
            for window in enumerate_windows(monitor, true)? {
                if let Some(snapshot) = PhysicalWindowSnapshot::capture(window, display, displays)?
                {
                    entries.push(snapshot);
                }
            }
        }
        Ok(Self { entries })
    }

    pub(crate) fn restore(&self, displays: &[Display]) -> Result<(), String> {
        let mut errors = Vec::new();
        for saved in &self.entries {
            if !same_window(&saved.window) {
                continue;
            }
            let Some(display) = displays.iter().find(|display| {
                !display.virtual_display && display.id.eq_ignore_ascii_case(&saved.display_id)
            }) else {
                errors.push(describe(
                    &saved.window,
                    "preserved physical display is unavailable",
                ));
                continue;
            };
            let Some(normal_display) = displays.iter().find(|display| {
                !display.virtual_display
                    && display.id.eq_ignore_ascii_case(&saved.normal_display_id)
            }) else {
                errors.push(describe(
                    &saved.window,
                    "preserved normal-position display is unavailable",
                ));
                continue;
            };
            if let Err(error) = restore_physical_window(saved, display.rect, normal_display.rect) {
                errors.push(error);
            }
        }
        if errors.is_empty() {
            Ok(())
        } else {
            Err(errors.join("; "))
        }
    }
}

impl PhysicalWindowSnapshot {
    pub(super) fn capture(
        window: WindowSnapshot,
        display: &Display,
        displays: &[Display],
    ) -> Result<Option<Self>, String> {
        let Some(observation) = observe_window(&window)? else {
            return Ok(None);
        };
        let info = default_monitor(display.rect)?;
        let normal_display = displays
            .iter()
            .filter(|candidate| !candidate.virtual_display)
            .filter_map(|candidate| {
                let info = default_monitor(candidate.rect).ok()?;
                let rect =
                    screen_position(&window, observation.placement.rcNormalPosition, &info).ok()?;
                let overlap = intersection_area(rect, info.rcMonitor);
                (overlap > 0).then_some((overlap, candidate))
            })
            .max_by_key(|(overlap, _)| *overlap)
            .map(|(_, display)| display)
            .unwrap_or(display);
        log_window_state("physical-preserved", &window, Some(display.rect));
        Ok(Some(Self {
            window,
            display_id: display.id.clone(),
            monitor: info,
            normal_display_id: normal_display.id.clone(),
            normal_monitor: default_monitor(normal_display.rect)?,
            observation,
        }))
    }
}

pub(super) fn preserved_normal_position(
    saved: &PhysicalWindowSnapshot,
    current: &MONITORINFO,
) -> Result<RECT, String> {
    // WINDOWPLACEMENT uses workspace coordinates for ordinary windows. Convert
    // through screen coordinates before translating the physical monitor origin.
    let mut normal = saved.observation.placement.rcNormalPosition;
    if !saved.window.tool_window {
        normal = offset_rect(
            normal,
            saved.normal_monitor.rcWork.left - saved.normal_monitor.rcMonitor.left,
            saved.normal_monitor.rcWork.top - saved.normal_monitor.rcMonitor.top,
        )?;
    }
    normal = offset_rect(
        normal,
        current.rcMonitor.left - saved.normal_monitor.rcMonitor.left,
        current.rcMonitor.top - saved.normal_monitor.rcMonitor.top,
    )?;
    normal_position(&saved.window, normal, current)
}

pub(super) fn restore_physical_window(
    saved: &PhysicalWindowSnapshot,
    target_rect: RECT,
    normal_target_rect: RECT,
) -> Result<(), String> {
    let entry = &saved.window;
    let Some(current) = observe_window(entry)? else {
        return Ok(());
    };
    let monitor = default_monitor(target_rect)?;
    let normal = preserved_normal_position(saved, &default_monitor(normal_target_rect)?)?;
    let expected = saved.observation;
    let state = if expected.iconic {
        WindowState::Minimized
    } else if expected.zoomed {
        WindowState::Maximized
    } else {
        WindowState::Normal
    };
    let outer = if expected.iconic || expected.zoomed {
        None
    } else {
        Some(offset_rect(
            expected.outer,
            monitor.rcMonitor.left - saved.monitor.rcMonitor.left,
            monitor.rcMonitor.top - saved.monitor.rcMonitor.top,
        )?)
    };
    let goal = WindowGoal {
        state,
        monitor: Some(monitor.rcMonitor),
        // Minimized windows may defer DPI messages until shown; their restored
        // position and state still have to belong to the preserved screen.
        dpi: if expected.iconic {
            None
        } else {
            Some(destination_dpi(entry, target_rect)?)
        },
        outer,
        normal: Some(normal),
        ..WindowGoal::default()
    };
    let restore_maximized = windows::Win32::UI::WindowsAndMessaging::WPF_RESTORETOMAXIMIZED.0;
    if goal.matches(&current, goal.current_monitor())
        && (current.placement.flags.0 & restore_maximized)
            == (expected.placement.flags.0 & restore_maximized)
    {
        return Ok(());
    }
    log_window_state("physical-repair", entry, Some(monitor.rcMonitor));
    let requested = outer.unwrap_or(screen_position(
        entry,
        normal,
        &default_monitor(normal_target_rect)?,
    )?);
    let goal = WindowGoal {
        restore_to_maximized: Some(expected.placement.flags.0 & restore_maximized != 0),
        ..goal
    };
    execute_layout(entry, requested, &monitor, goal)?;
    log_window_state("physical-repaired", entry, Some(monitor.rcMonitor));
    Ok(())
}
