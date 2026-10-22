use super::*;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum RestoreMode {
    NormalStop,
    StartupRollback,
}

struct RestoreGroup {
    id: u32,
    target_id: String,
    windows: Vec<WindowSnapshot>,
}

/// Owns one frozen stop-time selection. Runtime history and claims end at capture.
pub(crate) struct WindowRestorePlan {
    groups: Vec<RestoreGroup>,
    physical: Option<PhysicalWindowPreservation>,
    errors: Vec<String>,
}

impl WindowRestorePlan {
    pub(crate) fn capture(
        mut migrations: Vec<(u32, String, WindowMigration)>,
        mode: RestoreMode,
    ) -> Self {
        let mut plan = Self {
            groups: Vec::new(),
            physical: None,
            errors: Vec::new(),
        };
        if migrations.is_empty() {
            return plan;
        }
        // Join every scanner before releasing any runtime ownership.
        for (id, _, migration) in &mut migrations {
            for error in migration.stop_and_join_scanner() {
                plan.errors.push(format!("group {id} scanner: {error}"));
            }
        }
        for (_, _, migration) in &migrations {
            release_owner_claims(migration.migration_id);
        }
        let displays = match crate::display::active_display_topology() {
            Ok(displays) => displays,
            Err(error) => {
                plan.errors.push(format!("stop topology: {error}"));
                Vec::new()
            }
        };
        if !displays.is_empty() {
            match PhysicalWindowPreservation::capture(&displays) {
                Ok(physical) => plan.physical = Some(physical),
                Err(error) => plan.errors.push(format!("physical snapshot: {error}")),
            }
        }
        let mut selected = Vec::new();
        for (id, target_id, migration) in migrations {
            let source = restore_source(&displays, migration.source_id.as_deref());
            let source_monitor = source.and_then(|source| {
                match monitor_for_rect(source.rect, "stop virtual source") {
                    Ok(monitor) => Some(monitor),
                    Err(error) => {
                        plan.errors.push(format!("group {id} selection: {error}"));
                        None
                    }
                }
            });
            let mut history = migration.entries.lock().unwrap_or_else(|poisoned| {
                plan.errors
                    .push(format!("group {id} history lock was poisoned"));
                poisoned.into_inner()
            });
            let history = std::mem::take(&mut *history);
            let candidates = match (mode, source_monitor) {
                (RestoreMode::NormalStop, Some(monitor)) => {
                    match enumerate_windows(monitor, true) {
                        Ok(windows) => windows,
                        Err(error) => {
                            plan.errors.push(format!("group {id} enumeration: {error}"));
                            select_history(history, mode, Some(monitor))
                        }
                    }
                }
                _ => select_history(history, mode, source_monitor),
            };
            let windows = candidates
                .into_iter()
                .filter(|entry| select_once(&mut selected, WindowIdentity::from_snapshot(entry)))
                .collect::<Vec<_>>();
            for window in &windows {
                log_window_state("stop-selected", window, source.map(|source| source.rect));
            }
            plan.groups.push(RestoreGroup {
                id,
                target_id,
                windows,
            });
        }
        plan
    }

    pub(crate) fn transfer(&mut self, displays: &[Display]) {
        for group in &mut self.groups {
            let windows = std::mem::take(&mut group.windows);
            if windows.is_empty() {
                continue;
            }
            let target = crate::display::unique_physical_display(displays, &group.target_id)
                .ok()
                .or_else(|| {
                    displays
                        .iter()
                        .find(|d| d.primary && !d.virtual_display)
                        .cloned()
                });
            let Some(target) = target else {
                self.errors.push(format!(
                    "group {} target and primary display are unavailable",
                    group.id
                ));
                continue;
            };
            let count = windows.len();
            for (index, window) in windows.into_iter().enumerate().rev() {
                log_window_state("transfer-before", &window, Some(target.rect));
                if let Err(error) = restore_entry_at(&window, target.rect, (index, count)) {
                    self.errors
                        .push(format!("group {} transfer: {error}", group.id));
                    continue;
                }
                // Preserve only a completed transfer. A failed transfer is never
                // replayed after its source disappears, even if Windows moves it.
                match PhysicalWindowSnapshot::capture(window, &target, displays) {
                    Ok(Some(snapshot)) => {
                        self.physical
                            .get_or_insert_with(|| PhysicalWindowPreservation {
                                entries: Vec::new(),
                            })
                            .entries
                            .push(snapshot);
                    }
                    Ok(None) => (),
                    Err(error) => self
                        .errors
                        .push(format!("group {} transfer snapshot: {error}", group.id)),
                }
            }
        }
    }

    pub(crate) fn finish(mut self, displays: &[Display]) -> Result<(), String> {
        if let Some(physical) = self.physical
            && let Err(error) = physical.restore(displays)
        {
            self.errors.push(format!("physical restore: {error}"));
        }
        if self.errors.is_empty() {
            Ok(())
        } else {
            Err(self.errors.join("; "))
        }
    }
}

fn select_once(selected: &mut Vec<WindowIdentity>, identity: WindowIdentity) -> bool {
    if selected.contains(&identity) {
        return false;
    }
    selected.push(identity);
    true
}

fn select_history(
    history: Vec<WindowSnapshot>,
    mode: RestoreMode,
    source: Option<HMONITOR>,
) -> Vec<WindowSnapshot> {
    history
        .into_iter()
        .filter_map(|mut entry| {
            if !same_window(&entry) {
                return None;
            }
            let current = unsafe { MonitorFromWindow(entry.hwnd, MONITOR_DEFAULTTONULL) };
            prepare_history_entry(&mut entry, mode, source, current).then_some(entry)
        })
        .collect()
}

fn prepare_history_entry(
    entry: &mut WindowSnapshot,
    mode: RestoreMode,
    source: Option<HMONITOR>,
    current: HMONITOR,
) -> bool {
    let captured_startup = mode == RestoreMode::StartupRollback && entry.startup_layout.is_some();
    if !entry.moved && !captured_startup {
        return false;
    }
    let belongs = match (mode, source) {
        (RestoreMode::NormalStop, Some(source)) => current == source,
        _ => recovery_window_belongs_to_source(current, source),
    };
    if belongs && mode == RestoreMode::StartupRollback {
        entry.layout = entry.startup_layout.or(entry.layout);
    }
    belongs
}

#[cfg(test)]
mod tests {
    use super::*;

    fn history_entry(captured: bool, moved: bool) -> WindowSnapshot {
        let original = WindowLayout {
            normal: RECT {
                left: 10,
                top: 20,
                right: 210,
                bottom: 120,
            },
            bounds: RECT {
                left: 0,
                top: 0,
                right: 1000,
                bottom: 800,
            },
            state: WindowState::Maximized,
            placement: WINDOWPLACEMENT::default(),
        };
        let current = WindowLayout {
            normal: RECT {
                left: 30,
                top: 40,
                right: 430,
                bottom: 240,
            },
            state: WindowState::Normal,
            ..original
        };
        WindowSnapshot {
            hwnd: HWND::default(),
            pid: 0,
            thread_id: 0,
            class_name: "selection-fixture".into(),
            tool_window: false,
            moved,
            layout: Some(current),
            startup_layout: captured.then_some(original),
        }
    }

    #[test]
    fn rollback_recovers_unprocessed_captured_windows_and_original_layout() {
        let source = HMONITOR(std::ptr::dangling_mut());
        for current in [source, HMONITOR::default()] {
            let mut entry = history_entry(true, false);
            let original = entry.startup_layout.unwrap();
            assert!(prepare_history_entry(
                &mut entry,
                RestoreMode::StartupRollback,
                Some(source),
                current,
            ));
            assert_eq!(entry.layout.unwrap().normal, original.normal);
            assert_eq!(entry.layout.unwrap().state, WindowState::Maximized);
        }
    }

    #[test]
    fn rollback_does_not_reclaim_captured_windows_on_other_screens() {
        let source = HMONITOR(std::ptr::dangling_mut());
        let physical = HMONITOR(2usize as *mut _);
        let mut entry = history_entry(true, true);
        let latest = entry.layout.unwrap().normal;
        assert!(!prepare_history_entry(
            &mut entry,
            RestoreMode::StartupRollback,
            Some(source),
            physical,
        ));
        assert_eq!(entry.layout.unwrap().normal, latest);
        assert!(!prepare_history_entry(
            &mut entry,
            RestoreMode::StartupRollback,
            None,
            physical,
        ));
        assert!(prepare_history_entry(
            &mut entry,
            RestoreMode::StartupRollback,
            None,
            HMONITOR::default(),
        ));
    }

    #[test]
    fn rollback_runtime_windows_require_a_move_and_keep_their_own_layout() {
        let source = HMONITOR(std::ptr::dangling_mut());
        let mut entry = history_entry(false, false);
        let latest = entry.layout.unwrap().normal;
        assert!(!prepare_history_entry(
            &mut entry,
            RestoreMode::StartupRollback,
            Some(source),
            source,
        ));
        entry.moved = true;
        assert!(prepare_history_entry(
            &mut entry,
            RestoreMode::StartupRollback,
            Some(source),
            source,
        ));
        assert_eq!(entry.layout.unwrap().normal, latest);
    }

    #[test]
    fn normal_stop_history_preserves_current_layout_and_requires_current_source() {
        let source = HMONITOR(std::ptr::dangling_mut());
        let mut entry = history_entry(true, true);
        let latest = entry.layout.unwrap().normal;
        assert!(prepare_history_entry(
            &mut entry,
            RestoreMode::NormalStop,
            Some(source),
            source,
        ));
        assert_eq!(entry.layout.unwrap().normal, latest);
        assert_eq!(entry.layout.unwrap().state, WindowState::Normal);
        for current in [HMONITOR::default(), HMONITOR(2usize as *mut _)] {
            assert!(!prepare_history_entry(
                &mut entry,
                RestoreMode::NormalStop,
                Some(source),
                current,
            ));
        }
    }

    #[test]
    fn failed_group_transfers_are_consumed_and_not_retried_by_finish() {
        let mut plan = WindowRestorePlan {
            groups: (0..2)
                .map(|id| RestoreGroup {
                    id,
                    target_id: format!("missing-physical-{id}"),
                    windows: vec![history_entry(true, false)],
                })
                .collect(),
            physical: None,
            errors: Vec::new(),
        };
        plan.transfer(&[]);
        assert!(plan.groups.iter().all(|group| group.windows.is_empty()));
        assert_eq!(plan.errors.len(), 2);
        assert!(plan.physical.is_none());
        plan.transfer(&[]);
        assert_eq!(plan.errors.len(), 2);
        let error = plan.finish(&[]).unwrap_err();
        assert_eq!(error.matches("unavailable").count(), 2);
    }

    #[test]
    fn stop_selection_deduplicates_identity_without_runtime_claims() {
        let mut selected = Vec::new();
        let identity = WindowIdentity {
            hwnd: 42,
            pid: 1,
            thread_id: 2,
            class_name: "fixture".into(),
        };
        assert!(select_once(&mut selected, identity.clone()));
        assert!(!select_once(&mut selected, identity));
        assert_eq!(selected.len(), 1);
    }
    #[test]
    fn final_plan_reports_scanner_error_once_without_target_or_drop_retry() {
        let plan = WindowRestorePlan {
            groups: Vec::new(),
            physical: None,
            errors: vec!["scanner failure".into()],
        };
        assert_eq!(plan.finish(&[]).unwrap_err(), "scanner failure");
    }
    #[test]
    fn dropping_runtime_only_joins_scanner_and_releases_claims() {
        let owner = next_migration_id();
        let identity = WindowIdentity {
            hwnd: owner as isize,
            pid: 0,
            thread_id: 0,
            class_name: "drop-test".into(),
        };
        assert!(claim_window(owner, identity.clone()));
        let stop = Arc::new(AtomicBool::new(false));
        drop(WindowMigration {
            migration_id: owner,
            entries: Arc::new(Mutex::new(Vec::new())),
            stop_scanner: stop.clone(),
            scanner: None,
            target_rect: RECT::default(),
            source_id: None,
        });
        assert!(stop.load(Ordering::Acquire));
        assert!(claim_window(owner + 10000, identity));
        release_owner_claims(owner + 10000);
    }
}
