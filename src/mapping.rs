use std::collections::HashSet;
use std::error::Error;
use std::fmt::{Display as FmtDisplay, Formatter};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use windows::core::HRESULT;

use crate::config::RenderingOptions;
use crate::display::{
    Display, DisplayError, active_display_topology, active_displays, apply_display_mode,
    restore_display_topology, same_physical_display, unique_physical_display,
};
use crate::geometry::Rotation;
use crate::limits::MAX_OUTPUTS;
use crate::renderer::{Renderer, RendererEvent, RendererReporter, RendererStartError};
use crate::session_gate::{SessionGate, VirtualDisplayConfig, VirtualMode};
use crate::virtual_display::VirtualDisplay;
use crate::window_migration::{RestoreMode, WindowMigration, WindowRestorePlan};

const BASE_TOPOLOGY_TIMEOUT_SECS: u64 = 15;
const TOPOLOGY_TIMEOUT_SECS_PER_GROUP_OVER_EIGHT: u64 = 2;
const POLL_INTERVAL: Duration = Duration::from_millis(100);
const ERROR_CANCELLED_HRESULT: HRESULT = HRESULT(0x800704C7_u32 as i32);
static NEXT_RENDERER_ID: AtomicU64 = AtomicU64::new(1);

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct MappingPlan {
    pub groups: Vec<MappingGroupRequest>,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct MappingGroupRequest {
    /// Stable zero-based output slot. The value is also the IDD connector index.
    pub id: u32,
    #[serde(default, skip_serializing_if = "RenderingOptions::is_default")]
    pub rendering: RenderingOptions,
    pub mode: VirtualMode,
    #[serde(default)]
    pub rotation: Rotation,
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
    pub rotation: Rotation,
    pub route: MappingRoute,
    pub source_id: String,
    pub source_device_name: String,
    pub sunshine_id: Option<String>,
}

#[derive(Clone, Debug)]
pub enum MappingEvent {
    GroupReady(MappingGroupInfo),
    Renderer {
        id: u32,
        renderer_id: u64,
        event: RendererEvent,
    },
    Topology {
        message: String,
    },
}

pub type MappingReporter = Arc<dyn Fn(MappingEvent) + Send + Sync + 'static>;

pub(crate) struct ActiveGroup {
    info: MappingGroupInfo,
    rendering: RenderingOptions,
    target_id: Option<String>,
    renderer: Option<Renderer>,
    renderer_event_gate: Arc<RendererEventGate>,
    migration: Option<WindowMigration>,
}

enum RendererEventGateState {
    Starting(Vec<RendererEvent>),
    Active,
    Discarded,
}

struct RendererEventGate {
    id: u32,
    renderer_id: u64,
    reporter: MappingReporter,
    state: Mutex<RendererEventGateState>,
}

impl RendererEventGate {
    fn new(id: u32, reporter: MappingReporter) -> Arc<Self> {
        Arc::new(Self {
            id,
            renderer_id: NEXT_RENDERER_ID.fetch_add(1, Ordering::Relaxed),
            reporter,
            state: Mutex::new(RendererEventGateState::Starting(Vec::new())),
        })
    }

    fn report(&self, event: RendererEvent) {
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        match &mut *state {
            RendererEventGateState::Starting(events) => events.push(event),
            RendererEventGateState::Active => (self.reporter)(MappingEvent::Renderer {
                id: self.id,
                renderer_id: self.renderer_id,
                event,
            }),
            RendererEventGateState::Discarded => {}
        }
    }

    fn activate(&self) {
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let buffered = match std::mem::replace(&mut *state, RendererEventGateState::Active) {
            RendererEventGateState::Starting(events) => events,
            RendererEventGateState::Active | RendererEventGateState::Discarded => Vec::new(),
        };
        for event in buffered {
            (self.reporter)(MappingEvent::Renderer {
                id: self.id,
                renderer_id: self.renderer_id,
                event,
            });
        }
    }

    fn discard(&self) {
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        *state = RendererEventGateState::Discarded;
    }

    fn accepts(&self, renderer_id: u64) -> bool {
        renderer_id == self.renderer_id
            && matches!(
                *self
                    .state
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner()),
                RendererEventGateState::Active
            )
    }
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
    cleanup_incomplete: bool,
    retryable: bool,
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
    pub(crate) fn renderer_failed(group_id: u32, error: impl FmtDisplay) -> Self {
        group_stage(group_id, "renderer", error)
    }

    pub fn stage(&self) -> &'static str {
        self.stage
    }

    pub fn group_id(&self) -> Option<u32> {
        self.group_id
    }

    pub fn message(&self) -> &str {
        &self.message
    }

    pub(crate) fn is_clean_cancellation(&self) -> bool {
        self.stage == "cancelled" && !self.cleanup_incomplete
    }

    pub(crate) fn is_retryable(&self) -> bool {
        self.retryable && !self.cleanup_incomplete
    }

    fn retryable(mut self) -> Self {
        self.retryable = true;
        self
    }

    pub(crate) fn record_cleanup_failure(&mut self, error: impl FmtDisplay) {
        self.cleanup_incomplete = true;
        self.message
            .push_str(&format!("; rollback was incomplete: {error}"));
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
                cleanup_incomplete: false,
                retryable: false,
            });
        }
        if self.groups.len() > MAX_OUTPUTS {
            return Err(MappingError {
                stage: "plan",
                group_id: None,
                message: format!("at most {MAX_OUTPUTS} mapping groups are supported"),
                cleanup_incomplete: false,
                retryable: false,
            });
        }

        let mut ids = HashSet::with_capacity(self.groups.len());
        let mut targets = HashSet::with_capacity(self.groups.len());
        for group in &self.groups {
            if group.id >= MAX_OUTPUTS as u32 {
                return Err(group_stage(
                    group.id,
                    "plan",
                    format!(
                        "group id must be a connector index between 0 and {}",
                        MAX_OUTPUTS - 1
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

impl MappingSession {
    pub fn start_plan(plan: MappingPlan) -> Result<Self, MappingError> {
        Self::start_plan_with_reporter(plan, Arc::new(|_| {}))
    }

    pub fn start_plan_with_reporter(
        plan: MappingPlan,
        reporter: MappingReporter,
    ) -> Result<Self, MappingError> {
        let cancel = AtomicBool::new(false);
        Self::start_plan_with_reporter_cancellable(plan, reporter, &cancel)
    }

    pub(crate) fn start_plan_with_reporter_cancellable(
        plan: MappingPlan,
        reporter: MappingReporter,
        cancel: &AtomicBool,
    ) -> Result<Self, MappingError> {
        plan.validate()?;
        check_start_cancelled(cancel)?;
        let displays = active_displays().map_err(|error| stage("target", error))?;
        check_start_cancelled(cancel)?;
        for group in &plan.groups {
            if let MappingRoute::Mirror { target } = &group.route {
                mapping_target(&displays, target).map_err(|error| error.with_group(group.id))?;
            }
        }

        let physical_topology: Vec<_> = displays
            .iter()
            .filter(|display| !display.virtual_display)
            .cloned()
            .collect();
        crate::display::trace_topology("saved-at-start", &physical_topology);
        let connector_indices = plan.groups.iter().map(|group| group.id).collect();
        let gate_configs: Vec<_> = plan
            .groups
            .iter()
            .map(|group| VirtualDisplayConfig {
                connector_index: group.id,
                mode: group.mode,
            })
            .collect();
        check_start_cancelled(cancel)?;
        let session_gate = SessionGate::create_many(&gate_configs)
            .map_err(|error| stage("session-gate", error))?;
        check_start_cancelled(cancel)?;
        let mut session = Self {
            groups: Vec::with_capacity(plan.groups.len()),
            display: None,
            session_gate: Some(session_gate),
            connector_indices,
            physical_topology,
        };

        // Capture before adding a display: Windows may relocate windows as soon
        // as the device appears. The session owns snapshots throughout rollback.
        for request in &plan.groups {
            let target_id = match &request.route {
                MappingRoute::Mirror { target } => Some(target.clone()),
                MappingRoute::StreamOnly => None,
            };
            let migration = if let Some(target) = &target_id {
                let physical = mapping_target(&displays, target)?;
                match WindowMigration::capture(&physical) {
                    Ok(mut migration) => {
                        // Bind every captured transaction before creating the device:
                        // Windows can move its windows before begin is reached.
                        migration.bind_source_id(format!("sbms:connector:{}", request.id));
                        Some(migration)
                    }
                    Err(error) => {
                        return Err(rollback_start(
                            session,
                            group_stage(request.id, "windows", error),
                        ));
                    }
                }
            } else {
                None
            };
            session.groups.push(ActiveGroup {
                rendering: request.rendering,
                info: MappingGroupInfo {
                    id: request.id,
                    mode: request.mode,
                    rotation: request.rotation,
                    route: request.route.clone(),
                    source_id: String::new(),
                    source_device_name: String::new(),
                    sunshine_id: None,
                },
                target_id,
                renderer: None,
                renderer_event_gate: RendererEventGate::new(request.id, Arc::clone(&reporter)),
                migration,
            });
        }

        session.display = Some(match VirtualDisplay::create_cancellable(cancel) {
            Ok(display) => display,
            Err(error)
                if cancel.load(Ordering::Acquire) && error.code() == ERROR_CANCELLED_HRESULT =>
            {
                return Err(rollback_start(session, start_cancelled()));
            }
            Err(error) => return Err(rollback_start(session, stage("device", error))),
        });
        if let Err(error) = check_start_cancelled(cancel) {
            return Err(rollback_start(session, error));
        }
        let ReadyTopology {
            sources,
            displays: stable_displays,
        } = match wait_for_sources(&plan, &reporter, cancel) {
            Ok(topology) => topology,
            Err(error) => return Err(rollback_start(session, error)),
        };
        for (group, source) in session.groups.iter_mut().zip(&sources) {
            group.info.source_id = source.id.clone();
            group.info.source_device_name = source.device_name.clone();
            group.info.sunshine_id = source.sunshine_id.clone();
            if let Some(migration) = group.migration.as_mut() {
                migration.bind_source_id(source.id.clone());
            }
        }

        for (index, request) in plan.groups.iter().enumerate() {
            if let Err(error) = check_start_cancelled(cancel) {
                return Err(rollback_start(session, error));
            }
            let MappingRoute::Mirror { target } = &request.route else {
                continue;
            };
            let physical = match mapping_target(&stable_displays, target) {
                Ok(target) => target,
                Err(error) => {
                    return Err(rollback_start(session, error.with_group(request.id)));
                }
            };
            let event_gate = Arc::clone(&session.groups[index].renderer_event_gate);
            let renderer_reporter: RendererReporter = Arc::new(move |event| {
                event_gate.report(event);
            });
            let renderer = match Renderer::start_with_options_interruptible(
                physical,
                sources[index].clone(),
                renderer_reporter,
                request.rendering,
                || cancel.load(Ordering::Acquire),
            ) {
                Ok(renderer) => renderer,
                Err(RendererStartError::Cancelled { cleanup_error }) => {
                    let mut cause = start_cancelled();
                    if let Some(error) = cleanup_error {
                        cause.record_cleanup_failure(format!("renderer startup cleanup: {error}"));
                    }
                    return Err(rollback_start(session, cause));
                }
                Err(RendererStartError::Failed(error) | RendererStartError::Retryable(error)) => {
                    return Err(rollback_start(
                        session,
                        group_stage(request.id, "first-frame", error),
                    ));
                }
            };
            session.groups[index].renderer = Some(renderer);
            session.groups[index].renderer_event_gate.activate();
            if let Err(error) = check_start_cancelled(cancel) {
                return Err(rollback_start(session, error));
            }
        }

        // Establish the renderer's work area before moving any window. Each
        // captured layout is then applied once against the final presentation.
        for (index, request) in plan.groups.iter().enumerate() {
            if let Err(error) = check_start_cancelled(cancel) {
                return Err(rollback_start(session, error));
            }
            let MappingRoute::Mirror { target } = &request.route else {
                continue;
            };
            let physical = match mapping_target(&stable_displays, target) {
                Ok(target) => target,
                Err(error) => {
                    return Err(rollback_start(session, error.with_group(request.id)));
                }
            };
            match session.groups[index]
                .migration
                .as_mut()
                .expect("mirror snapshot")
                .begin(&physical, &sources[index])
            {
                Ok(()) => (),
                Err(_) if cancel.load(Ordering::Acquire) => {
                    return Err(rollback_start(session, start_cancelled()));
                }
                Err(error) => {
                    return Err(rollback_start(
                        session,
                        group_stage(request.id, "windows", error),
                    ));
                }
            }
        }
        if let Err(error) = check_start_cancelled(cancel) {
            return Err(rollback_start(session, error));
        }
        for group in &session.groups {
            reporter(MappingEvent::GroupReady(group.info.clone()));
        }
        Ok(session)
    }

    pub fn stop(&mut self) -> Result<(), MappingError> {
        self.stop_with_window_plan(RestoreMode::NormalStop)
    }

    fn stop_with_window_plan(&mut self, mode: RestoreMode) -> Result<(), MappingError> {
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
        // Transfer tracking into a single-use plan while all sources still exist.
        // Window selection and restoration errors belong to that plan, not to
        // the display lifecycle below.
        let migrations = self
            .groups
            .iter_mut()
            .filter_map(|group| {
                group.migration.take().map(|migration| {
                    (
                        group.info.id,
                        group.target_id.clone().unwrap_or_default(),
                        migration,
                    )
                })
            })
            .collect();
        let mut windows = WindowRestorePlan::capture(migrations, mode);
        for group in self.groups.iter().rev() {
            if let Some(renderer) = group.renderer.as_ref() {
                renderer.request_stop();
            }
        }
        for group in self.groups.iter_mut().rev() {
            if let Some(mut renderer) = group.renderer.take()
                && let Err(error) = renderer.stop()
            {
                errors.push(format!("group {} renderer-stop: {error}", group.info.id));
            }
        }
        // Transfer while the virtual monitors and their DPI anchors still exist.
        // Only successful destinations are protected across the topology change.
        match active_display_topology() {
            Ok(displays) => windows.transfer(&displays),
            Err(error) => {
                errors.push(format!("windows-transfer topology read: {error}"));
                windows.transfer(&[]);
            }
        }
        self.display.take();
        let removed = wait_for_stable_topology(
            &self.connector_indices,
            None,
            active_display_topology,
            || thread::sleep(POLL_INTERVAL),
        );
        if let Err(error) = &removed {
            errors.push(format!("remove: {error}"));
        }
        // The removal readback already proved stability. If Windows restored
        // the saved layout itself, neither a restore nor another wait is needed.
        if !removed
            .as_ref()
            .is_ok_and(|displays| physical_topology_restored(&self.physical_topology, displays))
        {
            match restore_display_topology(&self.physical_topology) {
                Ok(()) => {
                    if let Err(error) = wait_for_stable_topology(
                        &self.connector_indices,
                        Some(&self.physical_topology),
                        active_display_topology,
                        || thread::sleep(POLL_INTERVAL),
                    ) {
                        errors.push(format!("topology-restore: {error}"));
                    }
                }
                Err(error) => errors.push(format!("topology-restore: {error}")),
            }
        }

        let final_displays = active_displays();
        if let Err(error) = &final_displays {
            errors.push(format!("windows-restore topology read: {error}"));
        }
        if let Err(error) = windows.finish(final_displays.as_deref().unwrap_or(&[])) {
            errors.push(error);
        }
        self.session_gate.take();

        if errors.is_empty() {
            Ok(())
        } else {
            Err(MappingError {
                stage: "stop",
                group_id: None,
                message: errors.join("; "),
                cleanup_incomplete: false,
                retryable: false,
            })
        }
    }

    pub fn groups(&self) -> impl ExactSizeIterator<Item = &MappingGroupInfo> {
        self.groups.iter().map(|group| &group.info)
    }

    pub(crate) fn accepts_renderer_event(&self, id: u32, renderer_id: u64) -> bool {
        self.groups
            .iter()
            .find(|group| group.info.id == id)
            .is_some_and(|group| group.renderer_event_gate.accepts(renderer_id))
    }

    pub(crate) fn take_recovery_group(&mut self, group_id: u32) -> ActiveGroup {
        let group = self
            .groups
            .iter_mut()
            .find(|group| group.info.id == group_id)
            .expect("recovery group belongs to session");
        group.renderer_event_gate.discard();
        ActiveGroup {
            info: group.info.clone(),
            rendering: group.rendering,
            target_id: group.target_id.clone(),
            renderer: group.renderer.take(),
            renderer_event_gate: Arc::clone(&group.renderer_event_gate),
            migration: group.migration.take(),
        }
    }

    pub(crate) fn return_recovery_group(
        &mut self,
        group: ActiveGroup,
        topology: Option<Vec<Display>>,
        activate: bool,
    ) {
        let slot = self
            .groups
            .iter_mut()
            .find(|slot| slot.info.id == group.info.id)
            .expect("returned group belongs to session");
        *slot = group;
        if let Some(topology) = topology {
            // Windows may rearrange physical outputs while virtual outputs are
            // present. This is the renderer's current layout, not the user's
            // pre-session layout that Stop must restore.
            crate::display::trace_topology("observed-at-recovery", &topology);
        }
        if activate {
            slot.renderer_event_gate.activate();
        }
    }

    #[cfg(test)]
    pub(crate) fn recovery_test_session(reporter: MappingReporter) -> Self {
        Self {
            groups: vec![ActiveGroup {
                info: MappingGroupInfo {
                    id: 0,
                    mode: VirtualMode::default(),
                    rotation: Rotation::Deg0,
                    route: MappingRoute::Mirror {
                        target: "test-target".into(),
                    },
                    source_id: "test-source".into(),
                    source_device_name: "test-source".into(),
                    sunshine_id: None,
                },
                rendering: RenderingOptions::default(),
                target_id: Some("test-target".into()),
                renderer: None,
                renderer_event_gate: RendererEventGate::new(0, reporter),
                migration: None,
            }],
            display: None,
            session_gate: None,
            connector_indices: vec![],
            physical_topology: vec![],
        }
    }

    pub(crate) fn has_renderer(&self, id: u32) -> bool {
        self.groups
            .iter()
            .any(|group| group.info.id == id && group.renderer.is_some())
    }

    #[cfg(test)]
    pub(crate) fn activate_test_renderer_events(&self) -> u64 {
        let gate = &self.groups[0].renderer_event_gate;
        gate.activate();
        gate.renderer_id
    }

    pub(crate) fn plan(&self) -> MappingPlan {
        MappingPlan {
            groups: self
                .groups
                .iter()
                .map(|group| MappingGroupRequest {
                    id: group.info.id,
                    rendering: group.rendering,
                    mode: group.info.mode,
                    rotation: group.info.rotation,
                    route: group.info.route.clone(),
                })
                .collect(),
        }
    }
}

// Ownership is transferred, never shared: SwDeviceClose and CloseHandle accept
// process-wide handles on any thread. Renderer HWND/D3D resources stay on their
// own worker; WindowMigration already serializes and validates its HWND access.
unsafe impl Send for MappingSession {}

impl ActiveGroup {
    pub(crate) fn pause_renderer(&mut self) -> Result<(), MappingError> {
        self.renderer_event_gate.discard();
        if let Some(mut renderer) = self.renderer.take() {
            renderer
                .stop()
                .map_err(|error| group_stage(self.info.id, "renderer-stop", error))?;
        }
        Ok(())
    }

    pub(crate) fn recover(
        &mut self,
        reporter: MappingReporter,
        interrupted: impl Fn() -> bool,
    ) -> (Option<Vec<Display>>, Result<(), MappingError>) {
        let mut topology = None;
        let result = (|| {
            self.pause_renderer()?;
            let group_id = self.info.id;
            let target_id = self.target_id.as_ref().ok_or_else(|| {
                group_stage(group_id, "recovery", "stream-only group has no renderer")
            })?;
            let started = Instant::now();
            let displays = wait_for_ready_recovery_topology(&self.info, target_id, &interrupted)?;
            diagnostics_recovery(group_id, "topology-ready", started);
            let target =
                mapping_target(&displays, target_id).map_err(|error| error.with_group(group_id))?;
            let source = displays
                .iter()
                .find(|display| display.virtual_display && display.connector_index == group_id)
                .cloned()
                .expect("ready source checked");
            topology = Some(
                displays
                    .into_iter()
                    .filter(|display| !display.virtual_display)
                    .collect(),
            );
            if let Some(migration) = self.migration.as_mut() {
                migration
                    .refresh_topology(&target, &source)
                    .map_err(|error| group_stage(group_id, "window-scanner", error))?;
            }
            if interrupted() {
                return Err(start_cancelled());
            }
            let event_gate = RendererEventGate::new(group_id, reporter);
            let reporter_gate = Arc::clone(&event_gate);
            let renderer_reporter: RendererReporter =
                Arc::new(move |event| reporter_gate.report(event));
            let renderer = Renderer::start_with_options_interruptible(
                target.clone(),
                source.clone(),
                renderer_reporter,
                self.rendering,
                &interrupted,
            )
            .map_err(|error| match error {
                RendererStartError::Cancelled { cleanup_error } => {
                    let mut error =
                        group_stage(group_id, "cancelled", "renderer recovery was cancelled");
                    if let Some(cleanup_error) = cleanup_error {
                        error.record_cleanup_failure(cleanup_error);
                    }
                    error
                }
                RendererStartError::Failed(error) => group_stage(group_id, "first-frame", error),
                RendererStartError::Retryable(error) => {
                    group_stage(group_id, "first-frame", error).retryable()
                }
            })?;
            // CCD readiness alone is not sufficient. Once a real frame has been
            // presented, reject a target/source that changed during startup.
            self.renderer = Some(renderer);
            self.renderer_event_gate = event_gate;
            let validation =
                validate_recovered_topology(active_displays(), &target, &source, group_id);
            let validation = if interrupted()
                && validation
                    .as_ref()
                    .err()
                    .is_none_or(|error| error.is_retryable())
            {
                Err(start_cancelled())
            } else {
                validation
            };
            match validation {
                Ok(current) => {
                    topology = Some(
                        current
                            .into_iter()
                            .filter(|display| !display.virtual_display)
                            .collect(),
                    )
                }
                Err(mut error) => {
                    if let Err(cleanup) = self.pause_renderer() {
                        error.record_cleanup_failure(cleanup);
                    }
                    return Err(error);
                }
            }
            self.info.source_id = source.id;
            self.info.source_device_name = source.device_name;
            self.info.sunshine_id = source.sunshine_id;
            diagnostics_recovery(group_id, "first-frame-ready", started);
            Ok(())
        })();
        (topology, result)
    }
}

fn validate_recovered_topology(
    current: Result<Vec<Display>, DisplayError>,
    target: &Display,
    source: &Display,
    group_id: u32,
) -> Result<Vec<Display>, MappingError> {
    match current {
        Err(error) if error.is_identity_pending() => {
            Err(group_stage(group_id, "recovery", error).retryable())
        }
        Err(error) => Err(group_stage(group_id, "recovery", error)),
        Ok(displays) => {
            let target_matches = unique_physical_display(&displays, &target.id)
                .is_ok_and(|actual| same_recovery_display(&actual, target));
            let sources: Vec<_> = displays
                .iter()
                .filter(|display| display.virtual_display && display.connector_index == group_id)
                .collect();
            if target_matches && sources.len() == 1 && same_recovery_display(sources[0], source) {
                Ok(displays)
            } else {
                Err(group_stage(
                    group_id,
                    "recovery",
                    "display topology changed before first frame could be committed",
                )
                .retryable())
            }
        }
    }
}

fn same_recovery_display(actual: &Display, expected: &Display) -> bool {
    actual.id.eq_ignore_ascii_case(&expected.id)
        && actual
            .device_name
            .eq_ignore_ascii_case(&expected.device_name)
        && actual.connector_index == expected.connector_index
        && actual.rect == expected.rect
        && actual.rotation == expected.rotation
        && actual.refresh_numerator == expected.refresh_numerator
        && actual.refresh_denominator == expected.refresh_denominator
        && actual.virtual_display == expected.virtual_display
}

fn diagnostics_recovery(group_id: u32, phase: &str, started: Instant) {
    crate::diagnostics::log(
        crate::diagnostics::Level::Info,
        "mapping",
        "recovery-phase",
        None,
        format!(
            "group {group_id}: {phase} elapsed_ms={}",
            started.elapsed().as_millis()
        ),
    );
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
    if let Err(error) = session.stop_with_window_plan(RestoreMode::StartupRollback) {
        cause.record_cleanup_failure(error);
    }
    cause
}

fn mapping_target(displays: &[Display], target_id: &str) -> Result<Display, MappingError> {
    unique_physical_display(displays, target_id).map_err(|error| stage("target", error))
}

fn wait_for_ready_recovery_topology(
    group: &MappingGroupInfo,
    target_id: &str,
    interrupted: &impl Fn() -> bool,
) -> Result<Vec<Display>, MappingError> {
    wait_for_recovery_topology(group, target_id, interrupted, active_displays, || {
        thread::sleep(POLL_INTERVAL)
    })
}

fn wait_for_recovery_topology(
    group: &MappingGroupInfo,
    target_id: &str,
    interrupted: &impl Fn() -> bool,
    mut read: impl FnMut() -> Result<Vec<Display>, DisplayError>,
    mut wait: impl FnMut(),
) -> Result<Vec<Display>, MappingError> {
    // One coherent, usable snapshot permits a real capture attempt. Consecutive
    // identical CCD samples cannot guarantee DXGI readiness; the first presented
    // frame is the success condition. Only unready snapshots wait in this worker.
    loop {
        if interrupted() {
            return Err(start_cancelled());
        }
        let result = read();
        if interrupted() {
            return Err(start_cancelled());
        }
        match result {
            Ok(displays) => {
                if unique_physical_display(&displays, target_id).is_ok()
                    && displays.iter().any(|display| {
                        display.virtual_display
                            && display.connector_index == group.id
                            && source_matches_mode(display, group.mode, group.rotation)
                    })
                {
                    return Ok(displays);
                }
            }
            Err(error) if error.is_identity_pending() => {}
            Err(error) => return Err(group_stage(group.id, "recovery", error)),
        }
        wait();
    }
}

// Driver arrival can be visible to CCD before its connector receipt is published.
// Wait only for that explicit state, within the caller's existing deadline.
fn read_ready_topology(
    mut read: impl FnMut() -> Result<Vec<Display>, DisplayError>,
    deadline: Instant,
    interval: Duration,
    interrupted: &impl Fn() -> bool,
    on_pending: &mut impl FnMut(&DisplayError),
    stage_name: &'static str,
) -> Result<(Vec<Display>, bool), MappingError> {
    let mut was_pending = false;
    loop {
        if interrupted() {
            return Err(start_cancelled());
        }
        let result = read();
        if interrupted() {
            return Err(start_cancelled());
        }
        match result {
            Ok(displays) => return Ok((displays, was_pending)),
            Err(error) if error.is_identity_pending() => {
                was_pending = true;
                on_pending(&error);
                let remaining = deadline.saturating_duration_since(Instant::now());
                if remaining.is_zero() {
                    return Err(stage(
                        stage_name,
                        format!("display identity did not become ready before timeout: {error}"),
                    ));
                }
                thread::sleep(interval.min(remaining));
            }
            Err(error) => return Err(stage(stage_name, error)),
        }
    }
}

struct ReadyTopology {
    sources: Vec<Display>,
    displays: Vec<Display>,
}

fn wait_for_sources(
    plan: &MappingPlan,
    reporter: &MappingReporter,
    cancel: &AtomicBool,
) -> Result<ReadyTopology, MappingError> {
    let requested_ids: HashSet<_> = plan.groups.iter().map(|group| group.id).collect();
    let topology_timeout = topology_timeout(plan.groups.len());
    let deadline = Instant::now() + topology_timeout;
    let mut mode_applied = HashSet::new();
    let mut last_snapshot = None;
    let mut last_streaming_identity_message = None;
    let mut last_identity_pending = None;
    let mut report_identity_pending = |error: &DisplayError| {
        let message = format!("waiting for driver connector identity: {error}");
        if last_identity_pending.as_ref() != Some(&message) {
            reporter(MappingEvent::Topology {
                message: message.clone(),
            });
            last_identity_pending = Some(message);
        }
    };
    loop {
        let mut pending_streaming_identity = None;
        check_start_cancelled(cancel)?;
        let (displays, _) = read_ready_topology(
            active_display_topology,
            deadline,
            POLL_INTERVAL,
            &|| cancel.load(Ordering::Acquire),
            &mut report_identity_pending,
            "topology",
        )?;
        let sources: Vec<_> = displays
            .iter()
            .filter(|display| display.virtual_display)
            .cloned()
            .collect();
        let snapshot = format_topology_snapshot(plan, &sources);
        if last_snapshot.as_deref() != Some(snapshot.as_str()) {
            reporter(MappingEvent::Topology {
                message: snapshot.clone(),
            });
            last_snapshot = Some(snapshot.clone());
        }
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
                cleanup_incomplete: false,
                retryable: false,
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
            if source_matches_mode(source, group.mode, group.rotation) {
                ordered.push(source.clone());
                continue;
            }
            all_ready = false;
            if mode_applied.insert(group.id) {
                reporter(MappingEvent::Topology {
                    message: format!(
                        "group {} applying mode on device={} requested={}",
                        group.id,
                        source.device_name,
                        format_requested_mode(group.mode, group.rotation)
                    ),
                });
                match apply_display_mode(source, group.mode) {
                    Ok(()) => reporter(MappingEvent::Topology {
                        message: format!(
                            "group {} mode apply accepted on device={}; waiting for topology",
                            group.id, source.device_name
                        ),
                    }),
                    Err(error) => {
                        reporter(MappingEvent::Topology {
                            message: format!(
                                "group {} mode apply rejected on device={}: {}",
                                group.id, source.device_name, error
                            ),
                        });
                        return Err(group_stage(group.id, "mode", error));
                    }
                }
            }
        }
        if all_ready && ordered.len() == plan.groups.len() {
            let (hydrated, _) = read_ready_topology(
                active_displays,
                deadline,
                POLL_INTERVAL,
                &|| cancel.load(Ordering::Acquire),
                &mut report_identity_pending,
                "topology",
            )?;
            if let Some(ordered) = ordered_ready_sources(plan, &hydrated) {
                return Ok(ReadyTopology {
                    sources: ordered,
                    displays: hydrated,
                });
            }
            let missing: Vec<_> = plan
                .groups
                .iter()
                .filter(|group| matches!(group.route, MappingRoute::StreamOnly))
                .filter(|group| {
                    hydrated.iter().any(|source| {
                        source.virtual_display
                            && source.connector_index == group.id
                            && source.sunshine_id.is_none()
                    })
                })
                .map(|group| group.id.to_string())
                .collect();
            if !missing.is_empty() {
                let groups = missing.join(", ");
                let reason = format!("streaming monitor identity missing: groups {groups}");
                let message = format!("waiting for streaming monitor identity: groups {groups}");
                if last_streaming_identity_message.as_ref() != Some(&message) {
                    reporter(MappingEvent::Topology {
                        message: message.clone(),
                    });
                    last_streaming_identity_message = Some(message);
                }
                pending_streaming_identity = Some(reason);
            }
        }
        if Instant::now() >= deadline {
            let pending = pending_streaming_identity
                .unwrap_or_else(|| format_pending_topology(plan, &sources));
            reporter(MappingEvent::Topology {
                message: format!(
                    "timeout after {} seconds; {snapshot}",
                    topology_timeout.as_secs()
                ),
            });
            return Err(MappingError {
                stage: "topology",
                group_id: None,
                message: format!(
                    "virtual displays were not ready after {} seconds; {pending}",
                    topology_timeout.as_secs()
                ),
                cleanup_incomplete: false,
                retryable: false,
            });
        }
        thread::sleep(POLL_INTERVAL);
    }
}

fn topology_timeout(group_count: usize) -> Duration {
    let extra_groups = group_count.saturating_sub(8) as u64;
    Duration::from_secs(
        BASE_TOPOLOGY_TIMEOUT_SECS + extra_groups * TOPOLOGY_TIMEOUT_SECS_PER_GROUP_OVER_EIGHT,
    )
}

fn check_start_cancelled(cancel: &AtomicBool) -> Result<(), MappingError> {
    if cancel.load(Ordering::Acquire) {
        Err(start_cancelled())
    } else {
        Ok(())
    }
}

fn start_cancelled() -> MappingError {
    stage("cancelled", "mapping startup was cancelled")
}

fn ordered_ready_sources(plan: &MappingPlan, displays: &[Display]) -> Option<Vec<Display>> {
    let sources: Vec<_> = displays
        .iter()
        .filter(|display| display.virtual_display)
        .collect();
    if sources.len() != plan.groups.len() {
        return None;
    }
    let mut connector_indices = HashSet::with_capacity(sources.len());
    let mut device_names = HashSet::with_capacity(sources.len());
    if sources.iter().any(|source| {
        !connector_indices.insert(source.connector_index)
            || !device_names.insert(source.device_name.to_ascii_lowercase())
    }) {
        return None;
    }
    plan.groups
        .iter()
        .map(|group| {
            sources
                .iter()
                .find(|source| {
                    source.connector_index == group.id
                        && source_matches_mode(source, group.mode, group.rotation)
                        && (!matches!(group.route, MappingRoute::StreamOnly)
                            || source.sunshine_id.is_some())
                })
                .map(|source| (*source).clone())
        })
        .collect()
}

fn format_topology_snapshot(plan: &MappingPlan, sources: &[Display]) -> String {
    plan.groups
        .iter()
        .map(|group| format_group_topology(group, sources))
        .collect::<Vec<_>>()
        .join("; ")
}

fn format_pending_topology(plan: &MappingPlan, sources: &[Display]) -> String {
    plan.groups
        .iter()
        .filter(|group| {
            !sources.iter().any(|source| {
                source.connector_index == group.id
                    && source_matches_mode(source, group.mode, group.rotation)
            })
        })
        .map(|group| format_group_topology(group, sources))
        .collect::<Vec<_>>()
        .join("; ")
}

fn format_group_topology(group: &MappingGroupRequest, sources: &[Display]) -> String {
    let requested = format_requested_mode(group.mode, group.rotation);
    let Some(source) = sources
        .iter()
        .find(|source| source.connector_index == group.id)
    else {
        return format!("group {} absent requested={requested}", group.id);
    };
    let state = if source_matches_mode(source, group.mode, group.rotation) {
        "ready"
    } else {
        "observed"
    };
    format!(
        "group {} {} device={} actual={} requested={}",
        group.id,
        state,
        source.device_name,
        format_observed_mode(source),
        requested
    )
}

fn format_requested_mode(mode: VirtualMode, rotation: Rotation) -> String {
    format!(
        "{}x{}@{}/{} orientation={} topology_rotation={}",
        mode.width,
        mode.height,
        mode.refresh_numerator,
        mode.refresh_denominator,
        rotation_degrees(rotation),
        rotation_degrees(native_topology_rotation(rotation))
    )
}

fn format_observed_mode(source: &Display) -> String {
    format!(
        "{}x{}@{}/{} rotation={}",
        source.rect.right - source.rect.left,
        source.rect.bottom - source.rect.top,
        source.refresh_numerator,
        source.refresh_denominator,
        rotation_degrees(source.rotation)
    )
}

fn rotation_degrees(rotation: Rotation) -> u16 {
    match rotation {
        Rotation::Deg0 => 0,
        Rotation::Deg90 => 90,
        Rotation::Deg180 => 180,
        Rotation::Deg270 => 270,
    }
}

// Match the restore policy: disconnected saved displays are skipped, while
// newly attached displays keep their current settings.
fn physical_topology_restored(expected: &[Display], actual: &[Display]) -> bool {
    expected.iter().filter(|d| !d.virtual_display).all(|saved| {
        actual
            .iter()
            .find(|d| d.id.eq_ignore_ascii_case(&saved.id))
            .is_none_or(|current| same_physical_display(saved, current))
    })
}

fn wait_for_stable_topology(
    connector_indices: &[u32],
    expected: Option<&[Display]>,
    mut read: impl FnMut() -> Result<Vec<Display>, DisplayError>,
    mut wait: impl FnMut(),
) -> Result<Vec<Display>, MappingError> {
    let deadline = Instant::now() + topology_timeout(connector_indices.len());
    let stage_name = if expected.is_some() {
        "topology-restore"
    } else {
        "remove"
    };
    let mut previous: Option<Vec<Display>> = None;
    loop {
        match read() {
            Ok(mut displays) => {
                let removed = !displays.iter().any(|display| {
                    display.virtual_display && connector_indices.contains(&display.connector_index)
                });
                displays.retain(|d| !d.virtual_display);
                let ready = removed
                    && expected.is_none_or(|saved| physical_topology_restored(saved, &displays));
                if ready
                    && previous.as_ref().is_some_and(|before| {
                        before.len() == displays.len()
                            && before.iter().all(|old| {
                                displays.iter().any(|new| same_physical_display(old, new))
                            })
                    })
                {
                    return Ok(displays);
                }
                previous = ready.then_some(displays);
            }
            Err(error) if error.is_identity_pending() => previous = None,
            Err(error) => return Err(stage(stage_name, error)),
        }
        if Instant::now() >= deadline {
            return Err(stage(
                stage_name,
                "display topology did not settle before timeout",
            ));
        }
        wait();
    }
}

pub(crate) fn native_topology_rotation(_rotation: Rotation) -> Rotation {
    Rotation::Deg0
}

fn source_matches_mode(
    source: &Display,
    requested: VirtualMode,
    requested_rotation: Rotation,
) -> bool {
    let width = source.rect.right - source.rect.left;
    let height = source.rect.bottom - source.rect.top;
    width == requested.width as i32
        && height == requested.height as i32
        && source.rotation == native_topology_rotation(requested_rotation)
        && u64::from(source.refresh_numerator) * u64::from(requested.refresh_denominator)
            == u64::from(requested.refresh_numerator) * u64::from(source.refresh_denominator)
}

fn stage(stage: &'static str, error: impl FmtDisplay) -> MappingError {
    MappingError {
        stage,
        group_id: None,
        message: error.to_string(),
        cleanup_incomplete: false,
        retryable: false,
    }
}

fn group_stage(group_id: u32, stage: &'static str, error: impl FmtDisplay) -> MappingError {
    MappingError {
        stage,
        group_id: Some(group_id),
        message: error.to_string(),
        cleanup_incomplete: false,
        retryable: false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;
    use std::sync::atomic::AtomicBool;

    #[test]
    fn removal_waits_for_physical_stability_and_resets_after_pending_identity() {
        let (_, initial) = recovery_fixture();
        let physical = initial[0].clone();
        let mut shifted = physical.clone();
        shifted.rect.left += 8;
        shifted.rect.right += 8;
        let mut samples = vec![
            Ok(initial),
            Ok(vec![physical]),
            Ok(vec![shifted.clone()]),
            Err(DisplayError::identity_pending("receipt pending")),
            Ok(vec![shifted.clone()]),
            Ok(vec![shifted.clone()]),
        ]
        .into_iter();
        let mut waits = 0;
        let actual = wait_for_stable_topology(
            &[5],
            None,
            || samples.next().expect("unexpected extra topology read"),
            || waits += 1,
        )
        .unwrap();
        assert_eq!(waits, 5);
        assert!(same_physical_display(&actual[0], &shifted));
    }

    #[test]
    fn restored_topology_must_match_again_after_a_late_layout_change() {
        let (_, initial) = recovery_fixture();
        let expected = vec![initial[0].clone()];
        let mut shifted = expected.clone();
        shifted[0].rect.top += 8;
        shifted[0].rect.bottom += 8;
        let mut samples = vec![
            expected.clone(),
            shifted,
            expected.clone(),
            expected.clone(),
        ]
        .into_iter();
        let mut waits = 0;
        wait_for_stable_topology(
            &[5],
            Some(&expected),
            || Ok(samples.next().expect("unexpected extra topology read")),
            || waits += 1,
        )
        .unwrap();
        assert_eq!(waits, 3);
    }

    #[test]
    fn settled_topology_ignores_enumeration_order_and_equivalent_identity_and_rate() {
        let (_, initial) = recovery_fixture();
        let mut other = initial[0].clone();
        other.id = "second".into();
        let expected = vec![initial[0].clone(), other];
        let mut reordered = expected.clone();
        reordered.reverse();
        reordered[1].id = "PHYSICAL".into();
        reordered[1].device_name = "renumbered".into();
        reordered[1].refresh_numerator *= 1000;
        reordered[1].refresh_denominator *= 1000;
        let mut samples = vec![expected.clone(), reordered].into_iter();
        let mut waits = 0;
        let settled = wait_for_stable_topology(
            &[],
            None,
            || Ok(samples.next().expect("already stable topology must return")),
            || waits += 1,
        )
        .unwrap();
        assert_eq!(waits, 1);
        assert!(physical_topology_restored(&expected, &settled));
    }

    #[test]
    fn restoration_preserves_missing_display_policy_but_checks_present_display_settings() {
        let (_, initial) = recovery_fixture();
        let expected = vec![initial[0].clone()];
        let mut replacement = expected[0].clone();
        replacement.id = "new-panel".into();
        assert!(physical_topology_restored(&expected, &[replacement]));
        for change in 0..4 {
            let mut actual = expected.clone();
            match change {
                0 => actual[0].primary = !actual[0].primary,
                1 => actual[0].rotation = Rotation::Deg180,
                2 => {
                    actual[0].rect.left += 1;
                    actual[0].rect.right += 1;
                }
                _ => {
                    actual[0].refresh_numerator *= 1000;
                    actual[0].refresh_denominator *= 1001;
                }
            }
            assert!(!physical_topology_restored(&expected, &actual));
        }
    }

    #[test]
    fn topology_wait_propagates_real_errors_instead_of_retrying_them() {
        let error = wait_for_stable_topology(
            &[5],
            None,
            || Err(DisplayError::Failure("invalid connector receipt".into())),
            || panic!("real failure must not be retried"),
        )
        .unwrap_err();
        assert_eq!(error.stage(), "remove");
        assert_eq!(error.message(), "invalid connector receipt");
    }

    #[test]
    fn headless_removal_can_settle_but_one_empty_snapshot_is_not_enough() {
        let (_, initial) = recovery_fixture();
        for samples in [
            vec![Vec::new(), Vec::new()],
            vec![
                Vec::new(),
                vec![initial[0].clone()],
                vec![initial[0].clone()],
            ],
        ] {
            let expected = samples.last().unwrap().clone();
            let mut samples = samples.into_iter();
            let actual = wait_for_stable_topology(
                &[5],
                None,
                || Ok(samples.next().expect("unexpected extra read")),
                || {},
            )
            .unwrap();
            assert_eq!(actual.len(), expected.len());
            assert!(samples.next().is_none());
        }
    }

    #[test]
    fn recovery_does_not_replace_pre_session_physical_layout() {
        let mut session = MappingSession::recovery_test_session(Arc::new(|_| {}));
        let mut original = virtual_source(0);
        original.virtual_display = false;
        original.rect.left = 1308;
        original.rect.top = 2880;
        session.physical_topology = vec![original.clone()];

        let mut rearranged = original.clone();
        rearranged.rect.left = -2560;
        rearranged.rect.top = -1001;
        let group = session.take_recovery_group(0);
        session.return_recovery_group(group, Some(vec![rearranged]), true);

        assert_eq!(session.physical_topology[0].rect, original.rect);
        // Test owns no display resources; avoid invoking real topology restore.
        session.physical_topology.clear();
    }

    #[test]
    fn topology_identity_wait_accepts_published_receipt_after_pending() {
        let mut reads = 0;
        let pending_reports = Cell::new(0);
        let (displays, was_pending) = read_ready_topology(
            || {
                reads += 1;
                if reads == 1 {
                    Err(DisplayError::identity_pending("receipt not published"))
                } else {
                    Ok(vec![virtual_source(5)])
                }
            },
            Instant::now() + Duration::from_secs(1),
            Duration::ZERO,
            &|| false,
            &mut |_| pending_reports.set(pending_reports.get() + 1),
            "topology",
        )
        .unwrap();

        assert!(was_pending);
        assert_eq!(displays[0].connector_index, 5);
        assert_eq!(reads, 2);
        assert_eq!(pending_reports.get(), 1);
    }

    #[test]
    fn topology_identity_wait_does_not_retry_invalid_identity() {
        let mut reads = 0;
        let error = read_ready_topology(
            || {
                reads += 1;
                Err(DisplayError::Failure("invalid connector identity".into()))
            },
            Instant::now() + Duration::from_secs(1),
            Duration::ZERO,
            &|| false,
            &mut |_| panic!("invalid identity must not be reported as pending"),
            "topology",
        )
        .unwrap_err();

        assert_eq!(reads, 1);
        assert_eq!(error.message, "invalid connector identity");
    }

    #[test]
    fn topology_identity_wait_keeps_callers_deadline_and_pending_reason() {
        let mut reads = 0;
        let error = read_ready_topology(
            || {
                reads += 1;
                Err(DisplayError::identity_pending(
                    "connector 5 receipt pending",
                ))
            },
            Instant::now(),
            Duration::from_secs(1),
            &|| false,
            &mut |_| {},
            "remove",
        )
        .unwrap_err();

        assert_eq!(reads, 1);
        assert_eq!(error.stage, "remove");
        assert!(error.message.contains("timeout"));
        assert!(error.message.contains("connector 5 receipt pending"));
    }

    #[test]
    fn topology_identity_wait_honors_cancel_before_another_read() {
        let cancelled = Cell::new(false);
        let mut reads = 0;
        let error = read_ready_topology(
            || {
                reads += 1;
                Err(DisplayError::identity_pending("receipt pending"))
            },
            Instant::now() + Duration::from_secs(1),
            Duration::ZERO,
            &|| cancelled.get(),
            &mut |_| cancelled.set(true),
            "topology",
        )
        .unwrap_err();

        assert_eq!(reads, 1);
        assert!(error.is_clean_cancellation());
    }

    #[test]
    fn cleanup_failure_prevents_retrying_a_transient_error() {
        let mut error =
            group_stage(0, "first-frame", "DuplicateOutput: E_ACCESSDENIED").retryable();
        assert!(error.is_retryable());
        error.record_cleanup_failure("renderer thread failed to stop");
        assert!(!error.is_retryable());
        assert!(!error.is_clean_cancellation());
    }

    #[test]
    fn interrupted_recovery_does_not_query_display_topology() {
        let group = MappingGroupInfo {
            id: 0,
            mode: VirtualMode::default(),
            rotation: Rotation::default(),
            route: MappingRoute::Mirror {
                target: "unused".into(),
            },
            source_id: String::new(),
            source_device_name: String::new(),
            sunshine_id: None,
        };
        let error = wait_for_ready_recovery_topology(&group, "unused", &|| true).unwrap_err();
        assert!(error.is_clean_cancellation());
    }

    fn recovery_fixture() -> (MappingGroupInfo, Vec<Display>) {
        let source = virtual_source(5);
        let mut target = virtual_source(0);
        target.id = "physical".into();
        target.virtual_display = false;
        let group = MappingGroupInfo {
            id: 5,
            mode: VirtualMode::default(),
            rotation: Rotation::Deg0,
            route: MappingRoute::Mirror {
                target: target.id.clone(),
            },
            source_id: source.id.clone(),
            source_device_name: source.device_name.clone(),
            sunshine_id: source.sunshine_id.clone(),
        };
        (group, vec![target, source])
    }

    #[test]
    fn first_frame_validation_rejects_moved_source_and_preserves_real_errors() {
        let (group, displays) = recovery_fixture();
        let mut moved = displays.clone();
        moved[1].rect.left += 100;
        let error = validate_recovered_topology(Ok(moved), &displays[0], &displays[1], group.id)
            .unwrap_err();
        assert!(error.is_retryable());
        let error = validate_recovered_topology(
            Err(DisplayError::Failure("protocol duplicate".into())),
            &displays[0],
            &displays[1],
            group.id,
        )
        .unwrap_err();
        assert!(!error.is_retryable());
        assert_eq!(error.message, "protocol duplicate");
        let error = validate_recovered_topology(
            Err(DisplayError::identity_pending("receipt pending")),
            &displays[0],
            &displays[1],
            group.id,
        )
        .unwrap_err();
        assert!(error.is_retryable());
    }

    #[test]
    fn recovery_ready_snapshot_has_no_artificial_wait() {
        let (group, displays) = recovery_fixture();
        let recovered = wait_for_recovery_topology(
            &group,
            "physical",
            &|| false,
            || Ok(displays.clone()),
            || panic!("ready topology must not sleep"),
        )
        .unwrap();
        assert_eq!(recovered[1].connector_index, group.id);
    }

    #[test]
    fn recovery_identity_pending_recovers_on_first_usable_snapshot() {
        let (group, displays) = recovery_fixture();
        let mut reads = 0;
        let waits = Cell::new(0);
        wait_for_recovery_topology(
            &group,
            "physical",
            &|| false,
            || {
                reads += 1;
                if reads == 1 {
                    Err(DisplayError::identity_pending("receipt pending"))
                } else {
                    Ok(displays.clone())
                }
            },
            || waits.set(waits.get() + 1),
        )
        .unwrap();
        assert_eq!(reads, 2);
        assert_eq!(waits.get(), 1);
    }

    #[test]
    fn recovery_identity_pending_ends_only_when_caller_interrupts() {
        let (group, _) = recovery_fixture();
        let waits = Cell::new(0);
        let mut reads = 0;
        let error = wait_for_recovery_topology(
            &group,
            "physical",
            &|| waits.get() == 20,
            || {
                reads += 1;
                Err(DisplayError::identity_pending("receipt pending"))
            },
            || waits.set(waits.get() + 1),
        )
        .unwrap_err();
        assert_eq!(reads, 20);
        assert!(error.is_clean_cancellation());
    }

    #[test]
    fn recovery_does_not_treat_real_read_failure_as_pending() {
        let (group, _) = recovery_fixture();
        let error = wait_for_recovery_topology(
            &group,
            "physical",
            &|| false,
            || Err(DisplayError::Failure("invalid connector identity".into())),
            || panic!("a genuine read failure must not wait"),
        )
        .unwrap_err();
        assert_eq!(error.group_id, Some(group.id));
        assert_eq!(error.message, "invalid connector identity");
        assert!(!error.is_clean_cancellation());
        assert!(!error.is_retryable());
    }

    fn group(id: u32, route: MappingRoute) -> MappingGroupRequest {
        MappingGroupRequest {
            id,
            rendering: RenderingOptions::default(),
            mode: VirtualMode::default(),
            rotation: Rotation::Deg0,
            route,
        }
    }

    fn virtual_source(connector_index: u32) -> Display {
        Display {
            id: format!("virtual-{connector_index}"),
            connector_index,
            sunshine_id: Some(format!("sunshine-{connector_index}")),
            name: "SBMS".into(),
            device_name: format!(r"\\.\DISPLAY{}", connector_index + 20),
            rect: windows::Win32::Foundation::RECT {
                left: 0,
                top: 0,
                right: 3840,
                bottom: 2160,
            },
            native_width: 3840,
            native_height: 2160,
            physical_width_mm: None,
            physical_height_mm: None,
            preferred_refresh_millihz: None,
            rotation: Rotation::Deg0,
            refresh_numerator: 240,
            refresh_denominator: 1,
            primary: false,
            virtual_display: true,
        }
    }

    #[test]
    fn streaming_readiness_requires_capture_identity_but_mirroring_does_not() {
        let mut source = virtual_source(5);
        source.sunshine_id = None;
        let stream = MappingPlan::new(vec![group(5, MappingRoute::StreamOnly)]).unwrap();
        let mirror = MappingPlan::new(vec![group(
            5,
            MappingRoute::Mirror {
                target: "physical".into(),
            },
        )])
        .unwrap();

        assert!(ordered_ready_sources(&stream, std::slice::from_ref(&source)).is_none());
        assert!(ordered_ready_sources(&mirror, std::slice::from_ref(&source)).is_some());
        source.sunshine_id = Some("capture-id".into());
        assert!(ordered_ready_sources(&stream, &[source]).is_some());
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
        assert!(
            MappingPlan::new(vec![group(MAX_OUTPUTS as u32, MappingRoute::StreamOnly,)]).is_err()
        );
    }

    #[test]
    fn cancelled_start_stops_before_hardware_and_invalid_plan_still_wins() {
        let cancel = AtomicBool::new(true);
        let valid = MappingPlan {
            groups: vec![group(0, MappingRoute::StreamOnly)],
        };
        let Err(cancelled) =
            MappingSession::start_plan_with_reporter_cancellable(valid, Arc::new(|_| {}), &cancel)
        else {
            panic!("pre-cancelled mapping unexpectedly started");
        };
        assert!(cancelled.is_clean_cancellation());
        let mut message_only = start_cancelled();
        message_only
            .message
            .push_str("; rollback was incomplete: cleanup failed");
        assert!(message_only.is_clean_cancellation());

        let mut incomplete = start_cancelled();
        incomplete.record_cleanup_failure("cleanup failed");
        assert!(!incomplete.is_clean_cancellation());
        assert!(incomplete.message().contains("rollback was incomplete"));

        let invalid = MappingPlan { groups: Vec::new() };
        let Err(plan_error) = MappingSession::start_plan_with_reporter_cancellable(
            invalid,
            Arc::new(|_| {}),
            &cancel,
        ) else {
            panic!("invalid mapping unexpectedly started");
        };
        assert_eq!(plan_error.stage(), "plan");
        assert!(!plan_error.is_clean_cancellation());
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
        assert_eq!(plan.groups[0].rotation, Rotation::Deg0);
    }

    #[test]
    fn native_modes_keep_final_dimensions_and_rebase_orientation() {
        let native = VirtualMode {
            width: 1800,
            height: 2880,
            refresh_numerator: 120,
            refresh_denominator: 1,
        };
        assert_eq!(native.width, 1800);
        assert_eq!(native.height, 2880);
        assert_eq!(native_topology_rotation(Rotation::Deg0), Rotation::Deg0);
        assert_eq!(native_topology_rotation(Rotation::Deg90), Rotation::Deg0);
        assert_eq!(native_topology_rotation(Rotation::Deg180), Rotation::Deg0);
        assert_eq!(native_topology_rotation(Rotation::Deg270), Rotation::Deg0);
    }

    #[test]
    fn native_portrait_source_satisfies_a_270_degree_request() {
        let requested = VirtualMode {
            width: 1800,
            height: 2880,
            refresh_numerator: 120,
            refresh_denominator: 1,
        };
        let source = Display {
            id: "virtual".into(),
            connector_index: 1,
            sunshine_id: None,
            name: "SBMS".into(),
            device_name: r"\\.\DISPLAY73".into(),
            rect: windows::Win32::Foundation::RECT {
                left: 0,
                top: 0,
                right: 1800,
                bottom: 2880,
            },
            native_width: 1800,
            native_height: 2880,
            physical_width_mm: None,
            physical_height_mm: None,
            preferred_refresh_millihz: None,
            rotation: Rotation::Deg0,
            refresh_numerator: 120,
            refresh_denominator: 1,
            primary: false,
            virtual_display: true,
        };

        assert!(source_matches_mode(&source, requested, Rotation::Deg270));
    }

    #[test]
    fn topology_timeout_scales_only_above_the_previous_eight_group_limit() {
        assert_eq!(topology_timeout(1), Duration::from_secs(15));
        assert_eq!(topology_timeout(8), Duration::from_secs(15));
        assert_eq!(topology_timeout(9), Duration::from_secs(17));
        assert_eq!(topology_timeout(16), Duration::from_secs(31));
    }

    #[test]
    fn topology_snapshot_explains_an_absent_connector() {
        let plan = MappingPlan::new(vec![group(1, MappingRoute::StreamOnly)]).unwrap();

        assert_eq!(
            format_topology_snapshot(&plan, &[]),
            "group 1 absent requested=3840x2160@240/1 orientation=0 topology_rotation=0"
        );
    }

    #[test]
    fn hydrated_sources_follow_plan_order() {
        let plan = MappingPlan::new(vec![
            group(3, MappingRoute::StreamOnly),
            group(1, MappingRoute::StreamOnly),
        ])
        .unwrap();
        let displays = vec![virtual_source(1), virtual_source(3)];

        let ordered = ordered_ready_sources(&plan, &displays).unwrap();

        assert_eq!(
            ordered
                .iter()
                .map(|display| display.connector_index)
                .collect::<Vec<_>>(),
            vec![3, 1]
        );
        assert_eq!(ordered[0].sunshine_id.as_deref(), Some("sunshine-3"));
    }

    #[test]
    fn hydrated_sources_reject_changed_or_cloned_topology() {
        let plan = MappingPlan::new(vec![
            group(0, MappingRoute::StreamOnly),
            group(1, MappingRoute::StreamOnly),
        ])
        .unwrap();
        let first = virtual_source(0);
        let mut second = virtual_source(1);
        second.device_name = first.device_name.clone();
        assert!(ordered_ready_sources(&plan, &[first.clone(), second]).is_none());

        let mut wrong_mode = virtual_source(1);
        wrong_mode.rect.right = 1920;
        assert!(ordered_ready_sources(&plan, &[first, wrong_mode]).is_none());
    }

    #[test]
    fn renderer_events_are_buffered_until_the_candidate_is_installed() {
        let (tx, rx) = std::sync::mpsc::channel();
        let reporter: MappingReporter = Arc::new(move |event| tx.send(event).unwrap());
        let gate = RendererEventGate::new(3, reporter);

        gate.report(RendererEvent::TopologyLost);
        assert!(rx.try_recv().is_err());
        gate.activate();
        assert!(matches!(
            rx.recv().unwrap(),
            MappingEvent::Renderer {
                id: 3,
                event: RendererEvent::TopologyLost,
                ..
            }
        ));
    }

    #[test]
    fn renderer_events_from_discarded_candidates_stay_suppressed() {
        let (tx, rx) = std::sync::mpsc::channel();
        let reporter: MappingReporter = Arc::new(move |event| tx.send(event).unwrap());
        let gate = RendererEventGate::new(4, reporter);

        gate.report(RendererEvent::TopologyLost);
        gate.discard();
        gate.report(RendererEvent::Failed("stale".into()));

        assert!(rx.try_recv().is_err());
    }

    #[test]
    fn already_queued_events_are_rejected_after_renderer_replacement() {
        let (tx, rx) = std::sync::mpsc::channel();
        let reporter: MappingReporter = Arc::new(move |event| tx.send(event).unwrap());
        let old = RendererEventGate::new(0, Arc::clone(&reporter));
        old.activate();
        old.report(RendererEvent::TopologyLost);
        old.discard();
        let current = RendererEventGate::new(0, reporter);
        current.activate();
        let MappingEvent::Renderer { renderer_id, .. } = rx.recv().unwrap() else {
            panic!("renderer event expected")
        };
        assert!(!old.accepts(renderer_id));
        assert!(!current.accepts(renderer_id));
        assert!(current.accepts(current.renderer_id));
    }
}
