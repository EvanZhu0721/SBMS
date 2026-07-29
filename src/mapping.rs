use std::collections::HashSet;
use std::error::Error;
use std::fmt::{Display as FmtDisplay, Formatter};
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

use crate::config::ConfigStore;
use crate::display::{Display, active_displays, apply_display_mode, restore_display_topology};
use crate::renderer::{Renderer, RendererEvent, RendererReporter};
use crate::session_gate::{MAX_VIRTUAL_DISPLAYS, SessionGate, VirtualDisplayConfig, VirtualMode};
use crate::virtual_display::VirtualDisplay;
use crate::window_migration::WindowMigration;

const TOPOLOGY_TIMEOUT: Duration = Duration::from_secs(15);
const TOPOLOGY_SETTLE: Duration = Duration::from_millis(750);
const POLL_INTERVAL: Duration = Duration::from_millis(100);

pub const MAX_MAPPING_GROUPS: usize = MAX_VIRTUAL_DISPLAYS;

pub struct MappingRequest {
    pub target: String,
    pub mode: VirtualMode,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct MappingPlan {
    pub groups: Vec<MappingGroupRequest>,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct MappingGroupRequest {
    /// Stable zero-based output slot. The value is also the IDD connector index.
    pub id: u32,
    pub mode: VirtualMode,
    pub route: MappingRoute,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum MappingRoute {
    Mirror { target: String },
    StreamOnly,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MappingGroupInfo {
    pub id: u32,
    pub mode: VirtualMode,
    pub route: MappingRoute,
    pub source_id: String,
    pub source_device_name: String,
    pub sunshine_id: Option<String>,
}

#[derive(Clone, Debug)]
pub enum MappingEvent {
    GroupReady(MappingGroupInfo),
    Renderer { id: u32, event: RendererEvent },
}

pub type MappingReporter = Arc<dyn Fn(MappingEvent) + Send + Sync + 'static>;

struct ActiveGroup {
    info: MappingGroupInfo,
    target_id: Option<String>,
    renderer: Option<Renderer>,
    migration: Option<WindowMigration>,
}

pub struct MappingSession {
    groups: Vec<ActiveGroup>,
    display: Option<VirtualDisplay>,
    session_gate: Option<SessionGate>,
    connector_indices: Vec<u32>,
    physical_topology: Vec<Display>,
}

#[derive(Debug)]
pub struct MappingError {
    stage: &'static str,
    group_id: Option<u32>,
    message: String,
}

impl FmtDisplay for MappingError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        match self.group_id {
            Some(id) => write!(formatter, "group {id} {}: {}", self.stage, self.message),
            None => write!(formatter, "{}: {}", self.stage, self.message),
        }
    }
}

impl Error for MappingError {}

impl MappingError {
    pub fn stage(&self) -> &'static str {
        self.stage
    }

    pub fn group_id(&self) -> Option<u32> {
        self.group_id
    }

    pub fn message(&self) -> &str {
        &self.message
    }
}

impl MappingPlan {
    pub fn new(groups: Vec<MappingGroupRequest>) -> Result<Self, MappingError> {
        let plan = Self { groups };
        plan.validate()?;
        Ok(plan)
    }

    pub fn validate(&self) -> Result<(), MappingError> {
        if self.groups.is_empty() {
            return Err(MappingError {
                stage: "plan",
                group_id: None,
                message: "at least one mapping group is required".into(),
            });
        }
        if self.groups.len() > MAX_MAPPING_GROUPS {
            return Err(MappingError {
                stage: "plan",
                group_id: None,
                message: format!("at most {MAX_MAPPING_GROUPS} mapping groups are supported"),
            });
        }

        let mut ids = HashSet::with_capacity(self.groups.len());
        let mut targets = HashSet::with_capacity(self.groups.len());
        for group in &self.groups {
            if group.id >= MAX_MAPPING_GROUPS as u32 {
                return Err(group_stage(
                    group.id,
                    "plan",
                    format!(
                        "group id must be a connector index between 0 and {}",
                        MAX_MAPPING_GROUPS - 1
                    ),
                ));
            }
            if !ids.insert(group.id) {
                return Err(group_stage(
                    group.id,
                    "plan",
                    "mapping group id is duplicated",
                ));
            }
            group
                .mode
                .validate()
                .map_err(|error| group_stage(group.id, "mode", error))?;
            if let MappingRoute::Mirror { target } = &group.route {
                if target.trim().is_empty() {
                    return Err(group_stage(
                        group.id,
                        "plan",
                        "mirror target id must not be empty",
                    ));
                }
                if !targets.insert(target.to_ascii_lowercase()) {
                    return Err(group_stage(
                        group.id,
                        "plan",
                        "a physical target may only belong to one mapping group",
                    ));
                }
            }
        }
        Ok(())
    }
}

impl MappingRequest {
    pub fn configured(target: String) -> Result<Self, MappingError> {
        let outcome = ConfigStore::default_store()
            .and_then(|store| store.load())
            .map_err(|error| stage("config", error))?;
        if let Some(warning) = outcome.warning {
            return Err(MappingError {
                stage: "config",
                group_id: None,
                message: format!("{warning}; reset or repair the configuration before mapping"),
            });
        }
        let mode = match outcome.config.sizing {
            Some(sizing) => {
                let result = sizing
                    .calculate()
                    .map_err(|error| stage("geometry", error))?;
                let refresh_millihz = match result.preferred_refresh_millihz {
                    Some(refresh) => refresh,
                    None => target_refresh_millihz(&target)?,
                };
                VirtualMode::from_millihz(
                    result.virtual_mode.width,
                    result.virtual_mode.height,
                    refresh_millihz,
                )
                .map_err(|error| stage("mode", error))?
            }
            None => VirtualMode::default(),
        };
        Ok(Self { target, mode })
    }
}

impl MappingSession {
    pub fn start_with_reporter(
        request: MappingRequest,
        reporter: RendererReporter,
    ) -> Result<Self, MappingError> {
        let mapping_reporter: MappingReporter = Arc::new(move |event| {
            if let MappingEvent::Renderer { id: 0, event } = event {
                reporter(event);
            }
        });
        let plan = MappingPlan::new(vec![MappingGroupRequest {
            id: 0,
            mode: request.mode,
            route: MappingRoute::Mirror {
                target: request.target,
            },
        }])?;
        Self::start_plan_with_reporter(plan, mapping_reporter)
    }

    pub fn start_plan(plan: MappingPlan) -> Result<Self, MappingError> {
        Self::start_plan_with_reporter(plan, Arc::new(|_| {}))
    }

    pub fn start_plan_with_reporter(
        plan: MappingPlan,
        reporter: MappingReporter,
    ) -> Result<Self, MappingError> {
        plan.validate()?;
        let displays = active_displays().map_err(|error| stage("target", error))?;
        for group in &plan.groups {
            if let MappingRoute::Mirror { target } = &group.route {
                unique_target(&displays, target).map_err(|error| error.with_group(group.id))?;
            }
        }

        let physical_topology = displays
            .iter()
            .filter(|display| !display.virtual_display)
            .cloned()
            .collect();
        let connector_indices = plan.groups.iter().map(|group| group.id).collect();
        let gate_configs: Vec<_> = plan
            .groups
            .iter()
            .map(|group| VirtualDisplayConfig {
                connector_index: group.id,
                mode: group.mode,
            })
            .collect();
        let session_gate = SessionGate::create_many(&gate_configs)
            .map_err(|error| stage("session-gate", error))?;
        let display = VirtualDisplay::create().map_err(|error| stage("device", error))?;

        let mut session = Self {
            groups: Vec::with_capacity(plan.groups.len()),
            display: Some(display),
            session_gate: Some(session_gate),
            connector_indices,
            physical_topology,
        };

        let sources = match wait_for_sources(&plan) {
            Ok(sources) => sources,
            Err(error) => return Err(rollback_start(session, error)),
        };
        for (request, source) in plan.groups.iter().zip(&sources) {
            let target_id = match &request.route {
                MappingRoute::Mirror { target } => Some(target.clone()),
                MappingRoute::StreamOnly => None,
            };
            session.groups.push(ActiveGroup {
                info: MappingGroupInfo {
                    id: request.id,
                    mode: request.mode,
                    route: request.route.clone(),
                    source_id: source.id.clone(),
                    source_device_name: source.device_name.clone(),
                    sunshine_id: source.sunshine_id.clone(),
                },
                target_id,
                renderer: None,
                migration: None,
            });
        }

        let stable_displays = match active_displays() {
            Ok(displays) => displays,
            Err(error) => return Err(rollback_start(session, stage("topology", error))),
        };
        for (index, request) in plan.groups.iter().enumerate() {
            let MappingRoute::Mirror { target } = &request.route else {
                continue;
            };
            let physical = match unique_target(&stable_displays, target) {
                Ok(target) => target,
                Err(error) => {
                    return Err(rollback_start(session, error.with_group(request.id)));
                }
            };
            let migration = match WindowMigration::start(&physical, &sources[index]) {
                Ok(migration) => migration,
                Err(error) => {
                    return Err(rollback_start(
                        session,
                        group_stage(request.id, "windows", error),
                    ));
                }
            };
            session.groups[index].migration = Some(migration);
        }

        for (index, request) in plan.groups.iter().enumerate() {
            let MappingRoute::Mirror { target } = &request.route else {
                continue;
            };
            let physical = match unique_target(&stable_displays, target) {
                Ok(target) => target,
                Err(error) => {
                    return Err(rollback_start(session, error.with_group(request.id)));
                }
            };
            let group_reporter = Arc::clone(&reporter);
            let id = request.id;
            let renderer_reporter: RendererReporter = Arc::new(move |event| {
                group_reporter(MappingEvent::Renderer { id, event });
            });
            let renderer = match Renderer::start_with_reporter(
                physical,
                sources[index].clone(),
                renderer_reporter,
            ) {
                Ok(renderer) => renderer,
                Err(error) => {
                    return Err(rollback_start(
                        session,
                        group_stage(request.id, "first-frame", error),
                    ));
                }
            };
            session.groups[index].renderer = Some(renderer);
        }

        for group in &session.groups {
            reporter(MappingEvent::GroupReady(group.info.clone()));
        }
        Ok(session)
    }

    pub fn stop(&mut self) -> Result<(), MappingError> {
        if self
            .groups
            .iter()
            .all(|group| group.renderer.is_none() && group.migration.is_none())
            && self.display.is_none()
            && self.session_gate.is_none()
        {
            return Ok(());
        }

        let mut errors = Vec::new();
        for group in self.groups.iter_mut().rev() {
            if let Some(mut renderer) = group.renderer.take()
                && let Err(error) = renderer.stop()
            {
                errors.push(format!("group {} renderer-stop: {error}", group.info.id));
            }
        }
        for group in self.groups.iter_mut().rev() {
            if let Some(migration) = group.migration.as_mut() {
                migration.prepare_restore();
            }
        }
        for group in self.groups.iter_mut().rev() {
            if let Some(migration) = group.migration.as_mut()
                && let Err(error) = migration.restore()
            {
                errors.push(format!("group {} windows-restore: {error}", group.info.id));
            }
        }

        self.display.take();
        if let Err(error) = wait_for_sources_removed(&self.connector_indices) {
            errors.push(format!("remove: {error}"));
        }

        thread::sleep(TOPOLOGY_SETTLE);
        let topology_restored = match restore_display_topology(&self.physical_topology) {
            Ok(()) => true,
            Err(error) => {
                errors.push(format!("topology-restore: {error}"));
                false
            }
        };
        if topology_restored {
            thread::sleep(TOPOLOGY_SETTLE);
        }

        let final_displays = active_displays();
        if let Err(error) = &final_displays {
            errors.push(format!("windows-reconcile topology read: {error}"));
        }
        for group in self.groups.iter_mut().rev() {
            let Some(migration) = group.migration.as_mut() else {
                continue;
            };
            let target = group.target_id.as_ref().and_then(|target_id| {
                final_displays
                    .as_ref()
                    .ok()
                    .and_then(|displays| unique_target(displays, target_id).ok())
            });
            if let Err(error) = migration.reconcile_after_topology_change(target.as_ref()) {
                errors.push(format!(
                    "group {} windows-reconcile: {error}",
                    group.info.id
                ));
            }
        }
        for group in &mut self.groups {
            group.migration.take();
        }
        self.session_gate.take();

        if errors.is_empty() {
            Ok(())
        } else {
            Err(MappingError {
                stage: "stop",
                group_id: None,
                message: errors.join("; "),
            })
        }
    }

    pub fn source_id(&self) -> &str {
        &self
            .groups
            .first()
            .expect("a running mapping session has at least one group")
            .info
            .source_id
    }

    pub fn groups(&self) -> impl ExactSizeIterator<Item = &MappingGroupInfo> {
        self.groups.iter().map(|group| &group.info)
    }
}

impl MappingError {
    fn with_group(mut self, group_id: u32) -> Self {
        self.group_id = Some(group_id);
        self
    }
}

impl Drop for MappingSession {
    fn drop(&mut self) {
        let _ = self.stop();
    }
}

fn rollback_start(mut session: MappingSession, mut cause: MappingError) -> MappingError {
    if let Err(error) = session.stop() {
        cause
            .message
            .push_str(&format!("; rollback was incomplete: {error}"));
    }
    cause
}

fn unique_target(displays: &[Display], target_id: &str) -> Result<Display, MappingError> {
    let matches: Vec<_> = displays
        .iter()
        .filter(|display| display.id.eq_ignore_ascii_case(target_id))
        .collect();
    let target = match matches.as_slice() {
        [target] => (*target).clone(),
        [] => {
            return Err(MappingError {
                stage: "target",
                group_id: None,
                message: format!("active display id not found: {target_id}"),
            });
        }
        _ => {
            return Err(MappingError {
                stage: "target",
                group_id: None,
                message: format!("display id is ambiguous: {target_id}"),
            });
        }
    };
    if target.virtual_display {
        return Err(MappingError {
            stage: "target",
            group_id: None,
            message: "the physical output cannot be the SBMS virtual display".into(),
        });
    }
    if displays
        .iter()
        .filter(|display| {
            display
                .device_name
                .eq_ignore_ascii_case(&target.device_name)
        })
        .count()
        != 1
    {
        return Err(MappingError {
            stage: "target",
            group_id: None,
            message: "cloned outputs sharing one GDI display name are not supported".into(),
        });
    }
    Ok(target)
}

fn wait_for_sources(plan: &MappingPlan) -> Result<Vec<Display>, MappingError> {
    let requested_ids: HashSet<_> = plan.groups.iter().map(|group| group.id).collect();
    let deadline = Instant::now() + TOPOLOGY_TIMEOUT;
    let mut mode_applied = HashSet::new();
    loop {
        let displays = active_displays().map_err(|error| stage("topology", error))?;
        let sources: Vec<_> = displays
            .iter()
            .filter(|display| display.virtual_display)
            .cloned()
            .collect();
        if let Some(source) = sources
            .iter()
            .find(|source| !requested_ids.contains(&source.connector_index))
        {
            return Err(MappingError {
                stage: "topology",
                group_id: None,
                message: format!(
                    "unexpected SBMS virtual connector {} is active",
                    source.connector_index
                ),
            });
        }
        let mut seen = HashSet::new();
        for source in &sources {
            if !seen.insert(source.connector_index) {
                return Err(group_stage(
                    source.connector_index,
                    "topology",
                    "more than one virtual source reported the same connector",
                ));
            }
        }
        if sources.len() == plan.groups.len() {
            let mut device_names = HashSet::with_capacity(sources.len());
            for source in &sources {
                if !device_names.insert(source.device_name.to_ascii_lowercase()) {
                    return Err(group_stage(
                        source.connector_index,
                        "topology",
                        "virtual connectors were cloned onto one GDI source",
                    ));
                }
            }
        }

        let mut ordered = Vec::with_capacity(plan.groups.len());
        let mut all_ready = true;
        for group in &plan.groups {
            let Some(source) = sources
                .iter()
                .find(|source| source.connector_index == group.id)
            else {
                all_ready = false;
                continue;
            };
            if source_matches_mode(source, group.mode) {
                ordered.push(source.clone());
                continue;
            }
            all_ready = false;
            if mode_applied.insert(group.id) {
                apply_display_mode(source, group.mode)
                    .map_err(|error| group_stage(group.id, "mode", error))?;
            }
        }
        if all_ready && ordered.len() == plan.groups.len() {
            return Ok(ordered);
        }
        if Instant::now() >= deadline {
            let pending = plan
                .groups
                .iter()
                .filter(|group| {
                    !sources.iter().any(|source| {
                        source.connector_index == group.id
                            && source_matches_mode(source, group.mode)
                    })
                })
                .map(|group| group.id.to_string())
                .collect::<Vec<_>>()
                .join(", ");
            return Err(MappingError {
                stage: "topology",
                group_id: None,
                message: format!(
                    "virtual connectors [{pending}] did not become active at their requested modes within 15 seconds"
                ),
            });
        }
        thread::sleep(POLL_INTERVAL);
    }
}

fn wait_for_sources_removed(connector_indices: &[u32]) -> Result<(), MappingError> {
    let deadline = Instant::now() + TOPOLOGY_TIMEOUT;
    loop {
        let displays = active_displays().map_err(|error| stage("remove", error))?;
        if !displays.iter().any(|display| {
            display.virtual_display && connector_indices.contains(&display.connector_index)
        }) {
            return Ok(());
        }
        if Instant::now() >= deadline {
            return Err(MappingError {
                stage: "remove",
                group_id: None,
                message: "virtual sources stayed active after the device handle closed".into(),
            });
        }
        thread::sleep(POLL_INTERVAL);
    }
}

fn source_matches_mode(source: &Display, requested: VirtualMode) -> bool {
    let width = source.rect.right - source.rect.left;
    let height = source.rect.bottom - source.rect.top;
    width == requested.width as i32
        && height == requested.height as i32
        && u64::from(source.refresh_numerator) * u64::from(requested.refresh_denominator)
            == u64::from(requested.refresh_numerator) * u64::from(source.refresh_denominator)
}

fn target_refresh_millihz(target_id: &str) -> Result<u32, MappingError> {
    let displays = active_displays().map_err(|error| stage("target", error))?;
    let target = unique_target(&displays, target_id)?;
    if target.refresh_numerator == 0 || target.refresh_denominator == 0 {
        return Err(MappingError {
            stage: "mode",
            group_id: None,
            message: "target display reported an invalid refresh rate".into(),
        });
    }
    let millihz = u64::from(target.refresh_numerator)
        .checked_mul(1_000)
        .and_then(|value| value.checked_div(u64::from(target.refresh_denominator)))
        .and_then(|value| u32::try_from(value).ok())
        .filter(|value| *value > 0)
        .ok_or_else(|| MappingError {
            stage: "mode",
            group_id: None,
            message: "target refresh rate is outside the supported range".into(),
        })?;
    Ok(millihz)
}

fn stage(stage: &'static str, error: impl FmtDisplay) -> MappingError {
    MappingError {
        stage,
        group_id: None,
        message: error.to_string(),
    }
}

fn group_stage(group_id: u32, stage: &'static str, error: impl FmtDisplay) -> MappingError {
    MappingError {
        stage,
        group_id: Some(group_id),
        message: error.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn group(id: u32, route: MappingRoute) -> MappingGroupRequest {
        MappingGroupRequest {
            id,
            mode: VirtualMode::default(),
            route,
        }
    }

    #[test]
    fn mixed_plan_preserves_stable_connector_ids() {
        let plan = MappingPlan::new(vec![
            group(
                3,
                MappingRoute::Mirror {
                    target: "physical-a".into(),
                },
            ),
            group(1, MappingRoute::StreamOnly),
        ])
        .unwrap();

        assert_eq!(plan.groups[0].id, 3);
        assert_eq!(plan.groups[1].id, 1);
    }

    #[test]
    fn invalid_plans_fail_before_starting_hardware() {
        assert!(MappingPlan::new(Vec::new()).is_err());
        assert!(
            MappingPlan::new(vec![
                group(0, MappingRoute::StreamOnly),
                group(0, MappingRoute::StreamOnly),
            ])
            .is_err()
        );
        assert!(
            MappingPlan::new(vec![
                group(
                    0,
                    MappingRoute::Mirror {
                        target: "same".into(),
                    },
                ),
                group(
                    1,
                    MappingRoute::Mirror {
                        target: "SAME".into(),
                    },
                ),
            ])
            .is_err()
        );
        assert!(MappingPlan::new(vec![group(8, MappingRoute::StreamOnly)]).is_err());
    }

    #[test]
    fn plan_json_is_an_explicit_core_interface() {
        let json = r#"{
            "groups": [
                {
                    "id": 0,
                    "mode": {
                        "width": 3840,
                        "height": 2160,
                        "refresh_numerator": 240,
                        "refresh_denominator": 1
                    },
                    "route": { "kind": "stream_only" }
                }
            ]
        }"#;

        let plan: MappingPlan = serde_json::from_str(json).unwrap();
        plan.validate().unwrap();
        assert_eq!(plan.groups[0].route, MappingRoute::StreamOnly);
    }
}
