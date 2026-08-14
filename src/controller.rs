use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex, Weak};
use std::thread::{self, JoinHandle};

use crate::diagnostics::{self, Level, MappingSessionId};
use crate::display::active_displays;
use crate::geometry::{AspectRatio, Rotation};
use crate::mapping::{
    MappingError, MappingEvent, MappingGroupInfo, MappingGroupRequest, MappingPlan,
    MappingReporter, MappingRoute, MappingSession, native_topology_rotation,
};
use crate::renderer::RendererEvent;
use crate::sunshine::{self, CaptureBackend, DeploymentEvent};

const DESKTOP_DUPLICATION_PROCESS_LIMIT: usize = 4;
const MAX_RECONNECT_ATTEMPTS: u8 = 2;

#[derive(Clone, Debug)]
pub struct DisplayOption {
    pub id: String,
    pub name: String,
    pub label: String,
    pub native_width: u32,
    pub native_height: u32,
    pub detected_physical_width_mm: Option<f64>,
    pub detected_physical_height_mm: Option<f64>,
    pub physical_width_mm: Option<f64>,
    pub physical_height_mm: Option<f64>,
    pub physical_override_inches: Option<f64>,
    pub physical_override_aspect_ratio: Option<AspectRatio>,
    pub rotation: Rotation,
}

#[derive(Clone, Debug)]
pub enum ControllerEvent {
    Displays(Vec<DisplayOption>),
    GroupReady(MappingGroupInfo),
    Fps {
        id: u32,
        fps: u32,
    },
    Sunshine {
        id: u32,
        display_id: String,
        requested_port: u16,
        port: Option<u16>,
        error: Option<String>,
    },
    State {
        state: &'static str,
        detail: String,
        running: bool,
        busy: bool,
        error: String,
        restart_available: bool,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum StartReason {
    User,
    ManualRestart,
    Reconnect { attempt: u8 },
}

impl StartReason {
    fn reconnect_attempt(self) -> Option<u8> {
        match self {
            Self::Reconnect { attempt } => Some(attempt),
            Self::User | Self::ManualRestart => None,
        }
    }
}

enum Command {
    Refresh,
    Start {
        plan: MappingPlan,
        cancel: Arc<AtomicBool>,
        reason: StartReason,
    },
    Stop,
    RestartMapping {
        cancel: Arc<AtomicBool>,
    },
    Shutdown,
}

enum Message {
    Command(Command),
    Mapping {
        generation: u64,
        event: MappingEvent,
    },
    Sunshine(DeploymentEvent),
}

#[derive(Clone)]
pub struct ControllerSender {
    tx: Sender<Message>,
    startup_cancellations: Arc<Mutex<Vec<Weak<AtomicBool>>>>,
    shutdown_requested: Arc<AtomicBool>,
}

pub struct Controller {
    sender: ControllerSender,
    shutdown_requested: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}

struct ActiveRun {
    session: MappingSession,
    reporter: MappingReporter,
    generation: u64,
    session_id: MappingSessionId,
    sunshine_ports: HashMap<u32, u16>,
    sunshine_capture: CaptureBackend,
    cancel: Arc<AtomicBool>,
}

impl ControllerSender {
    fn new(tx: Sender<Message>, shutdown_requested: Arc<AtomicBool>) -> Self {
        Self {
            tx,
            startup_cancellations: Arc::new(Mutex::new(Vec::new())),
            shutdown_requested,
        }
    }

    pub fn refresh(&self) {
        let _ = self.tx.send(Message::Command(Command::Refresh));
    }

    pub fn start(&self, plan: MappingPlan) {
        self.enqueue_start(plan, StartReason::User);
    }

    pub fn restart_mapping(&self) {
        let cancel = self.startup_cancellation();
        let _ = self
            .tx
            .send(Message::Command(Command::RestartMapping { cancel }));
    }

    fn enqueue_start(&self, plan: MappingPlan, reason: StartReason) {
        let cancel = self.startup_cancellation();
        let _ = self.tx.send(Message::Command(Command::Start {
            plan,
            cancel,
            reason,
        }));
    }

    fn startup_cancellation(&self) -> Arc<AtomicBool> {
        let cancel = Arc::new(AtomicBool::new(false));
        let mut cancellations = self
            .startup_cancellations
            .lock()
            .expect("startup cancellation registry poisoned");
        cancellations.retain(|cancel| cancel.strong_count() > 0);
        cancellations.push(Arc::downgrade(&cancel));
        if self.shutdown_requested.load(Ordering::Acquire) {
            cancel.store(true, Ordering::Release);
        }
        cancel
    }

    pub fn stop(&self) {
        self.cancel_startups();
        let _ = self.tx.send(Message::Command(Command::Stop));
    }

    fn cancel_startups(&self) {
        let mut cancellations = self
            .startup_cancellations
            .lock()
            .expect("startup cancellation registry poisoned");
        cancellations.retain(|cancel| {
            if let Some(cancel) = cancel.upgrade() {
                cancel.store(true, Ordering::Release);
                true
            } else {
                false
            }
        });
    }
}

impl Controller {
    pub fn spawn(emit: impl Fn(ControllerEvent) + Send + 'static) -> Self {
        let (tx, rx) = mpsc::channel();
        let shutdown_requested = Arc::new(AtomicBool::new(false));
        let sender = ControllerSender::new(tx, Arc::clone(&shutdown_requested));
        let worker_tx = sender.tx.clone();
        let worker_shutdown = Arc::clone(&shutdown_requested);
        let worker = thread::spawn(move || {
            let sunshine_tx = worker_tx.clone();
            let sunshine = sunshine::Supervisor::spawn(move |event| {
                let _ = sunshine_tx.send(Message::Sunshine(event));
            });
            let sunshine_sender = sunshine.sender();
            sunshine_sender.stop_all();
            let mut active = None;
            let mut restart_plan = None::<MappingPlan>;
            let mut next_generation = 0_u64;
            while let Ok(message) = rx.recv() {
                match message {
                    Message::Command(Command::Refresh) => refresh(&emit),
                    Message::Command(Command::Start {
                        plan,
                        cancel,
                        reason,
                    }) => {
                        if active.is_some() {
                            continue;
                        }
                        if reason == StartReason::User {
                            restart_plan = None;
                        }
                        let sunshine_ports = match plan
                            .groups
                            .iter()
                            .enumerate()
                            .map(|(slot, group)| {
                                sunshine::default_port_for_slot(slot).map(|port| (group.id, port))
                            })
                            .collect::<Result<HashMap<_, _>, _>>()
                        {
                            Ok(ports) => ports,
                            Err(error) => {
                                diagnostics::log(
                                    Level::Error,
                                    "controller",
                                    "sunshine-ports",
                                    None,
                                    error.as_str(),
                                );
                                if let Some(attempt) = reason.reconnect_attempt() {
                                    if !queue_reconnect_after_failure(
                                        attempt,
                                        &plan,
                                        error.as_str(),
                                        &worker_tx,
                                        &emit,
                                        None,
                                        &cancel,
                                    ) {
                                        restart_plan = Some(plan);
                                    }
                                    continue;
                                }
                                if reason == StartReason::ManualRestart {
                                    restart_plan = Some(plan);
                                    emit(restartable_state("Manual restart failed", error));
                                    continue;
                                }
                                emit(state(
                                    "Stopped",
                                    "Couldn’t reserve streaming ports".into(),
                                    false,
                                    false,
                                    error,
                                ));
                                continue;
                            }
                        };
                        let sunshine_capture = capture_backend_for_plan(&plan);
                        next_generation = next_generation.wrapping_add(1);
                        let generation = next_generation;
                        let session_id = diagnostics::new_mapping_session_id();
                        diagnostics::log(
                            Level::Info,
                            "controller",
                            if reason.reconnect_attempt().is_some() {
                                "reconnect-start"
                            } else {
                                "start"
                            },
                            Some(session_id),
                            format!("mapping plan requested with {} group(s)", plan.groups.len()),
                        );
                        diagnostics::log(
                            Level::Info,
                            "controller",
                            "sunshine-capture",
                            Some(session_id),
                            format!(
                                "selected capture={} for {} managed Sunshine instance(s); projected DDA processes={} (limit={})",
                                sunshine_capture.as_str(),
                                plan.groups.len(),
                                projected_desktop_duplication_processes(&plan),
                                DESKTOP_DUPLICATION_PROCESS_LIMIT,
                            ),
                        );
                        for group in &plan.groups {
                            diagnostics::log(
                                Level::Info,
                                "controller",
                                "plan",
                                Some(session_id),
                                format_mapping_group_plan(group),
                            );
                        }
                        if let Some(attempt) = reason.reconnect_attempt() {
                            emit(state(
                                "Reconnecting",
                                format!("Reconnect attempt {attempt} of {MAX_RECONNECT_ATTEMPTS}…"),
                                false,
                                true,
                                "",
                            ));
                        } else {
                            emit(state(
                                "Starting",
                                if reason == StartReason::ManualRestart {
                                    "Restarting mapping…".into()
                                } else {
                                    "Creating virtual display…".into()
                                },
                                false,
                                true,
                                "",
                            ));
                        }
                        let report_tx = worker_tx.clone();
                        let reporter: MappingReporter = Arc::new(move |event| {
                            if let MappingEvent::Topology { message } = &event {
                                diagnostics::log(
                                    Level::Debug,
                                    "mapping",
                                    "topology",
                                    Some(session_id),
                                    message,
                                );
                                return;
                            }
                            if let MappingEvent::Renderer { id, event } = &event {
                                match event {
                                    RendererEvent::Failed(error) => diagnostics::log(
                                        Level::Error,
                                        "renderer",
                                        "renderer",
                                        Some(session_id),
                                        format!("group {id}: {error}"),
                                    ),
                                    RendererEvent::TopologyLost => diagnostics::log(
                                        Level::Warn,
                                        "renderer",
                                        "topology-lost",
                                        Some(session_id),
                                        format!(
                                            "group {id}: desktop duplication topology was lost"
                                        ),
                                    ),
                                    RendererEvent::Fps(_) => {}
                                }
                            }
                            let _ = report_tx.send(Message::Mapping { generation, event });
                        });
                        match MappingSession::start_plan_with_reporter_cancellable(
                            plan.clone(),
                            Arc::clone(&reporter),
                            &cancel,
                        ) {
                            Ok(mut started) => {
                                if worker_shutdown.load(Ordering::Acquire)
                                    || cancel.load(Ordering::Acquire)
                                {
                                    match started.stop() {
                                        Ok(()) => {
                                            diagnostics::log(
                                                Level::Info,
                                                "controller",
                                                "cancelled",
                                                Some(session_id),
                                                "mapping completed while cancellation was pending and was stopped",
                                            );
                                            if !worker_shutdown.load(Ordering::Acquire) {
                                                emit(state(
                                                    "Stopped",
                                                    "Choose a display to start".into(),
                                                    false,
                                                    false,
                                                    "",
                                                ));
                                            }
                                        }
                                        Err(error) => {
                                            diagnostics::log(
                                                Level::Error,
                                                "controller",
                                                error.stage(),
                                                Some(session_id),
                                                error.to_string(),
                                            );
                                            if !worker_shutdown.load(Ordering::Acquire) {
                                                emit(state(
                                                    "Stopped",
                                                    "Cleanup completed with errors".into(),
                                                    false,
                                                    false,
                                                    error.to_string(),
                                                ));
                                            }
                                        }
                                    }
                                    continue;
                                }
                                active = Some(ActiveRun {
                                    session: started,
                                    reporter,
                                    generation,
                                    session_id,
                                    sunshine_ports,
                                    sunshine_capture,
                                    cancel,
                                });
                                restart_plan = None;
                                diagnostics::log(
                                    Level::Info,
                                    "controller",
                                    "running",
                                    Some(session_id),
                                    "mapping is active",
                                );
                                emit(state(
                                    "Running",
                                    "Mapping is active".into(),
                                    true,
                                    false,
                                    "",
                                ));
                            }
                            Err(error) => {
                                if worker_shutdown.load(Ordering::Acquire) {
                                    let clean_cancel = error.is_clean_cancellation();
                                    diagnostics::log(
                                        if clean_cancel {
                                            Level::Info
                                        } else {
                                            Level::Error
                                        },
                                        "controller",
                                        if clean_cancel {
                                            "shutdown"
                                        } else {
                                            error.stage()
                                        },
                                        Some(session_id),
                                        if clean_cancel {
                                            "mapping startup cancelled during shutdown".into()
                                        } else {
                                            error.to_string()
                                        },
                                    );
                                    continue;
                                }
                                if cancel.load(Ordering::Acquire) {
                                    if error.is_clean_cancellation() {
                                        diagnostics::log(
                                            Level::Info,
                                            "controller",
                                            "cancelled",
                                            Some(session_id),
                                            "mapping startup cancelled",
                                        );
                                        emit(state(
                                            "Stopped",
                                            "Choose a display to start".into(),
                                            false,
                                            false,
                                            "",
                                        ));
                                    } else {
                                        diagnostics::log(
                                            Level::Error,
                                            "controller",
                                            error.stage(),
                                            Some(session_id),
                                            error.to_string(),
                                        );
                                        emit(state(
                                            "Stopped",
                                            "Cleanup completed with errors".into(),
                                            false,
                                            false,
                                            error.to_string(),
                                        ));
                                    }
                                    continue;
                                }
                                diagnostics::log(
                                    Level::Error,
                                    "controller",
                                    error.stage(),
                                    Some(session_id),
                                    error.to_string(),
                                );
                                if let Some(attempt) = reason.reconnect_attempt() {
                                    if !queue_reconnect_after_failure(
                                        attempt,
                                        &plan,
                                        error.to_string(),
                                        &worker_tx,
                                        &emit,
                                        Some(session_id),
                                        &cancel,
                                    ) {
                                        restart_plan = Some(plan);
                                    }
                                    continue;
                                }
                                if reason == StartReason::ManualRestart {
                                    restart_plan = Some(plan);
                                    emit(restartable_state(
                                        "Manual restart failed",
                                        error.to_string(),
                                    ));
                                    continue;
                                }
                                emit(state(
                                    "Stopped",
                                    "Couldn’t start mapping".into(),
                                    false,
                                    false,
                                    error.to_string(),
                                ));
                            }
                        }
                    }
                    Message::Command(Command::Stop) => {
                        restart_plan = None;
                        stop_with_state(&mut active, &sunshine_sender, &emit);
                    }
                    Message::Command(Command::RestartMapping { cancel }) => {
                        if active.is_none()
                            && !cancel.load(Ordering::Acquire)
                            && let Some(plan) = restart_plan.take()
                        {
                            let _ = worker_tx.send(Message::Command(Command::Start {
                                plan,
                                cancel,
                                reason: StartReason::ManualRestart,
                            }));
                        }
                    }
                    Message::Command(Command::Shutdown) => {
                        stop_with_state(&mut active, &sunshine_sender, &emit);
                        break;
                    }
                    Message::Mapping { generation, event }
                        if active
                            .as_ref()
                            .is_some_and(|active| active.generation == generation) =>
                    {
                        match event {
                            MappingEvent::GroupReady(group) => {
                                let active = active.as_ref().expect("guard checked active run");
                                if let (Some(display_id), Some(port)) = (
                                    group.sunshine_id.clone(),
                                    active.sunshine_ports.get(&group.id).copied(),
                                ) {
                                    sunshine_sender.start(
                                        generation,
                                        group.id,
                                        display_id,
                                        port,
                                        active.sunshine_capture,
                                    );
                                }
                                emit(ControllerEvent::GroupReady(group));
                            }
                            MappingEvent::Renderer {
                                id,
                                event: RendererEvent::Fps(fps),
                            } => {
                                emit(ControllerEvent::Fps {
                                    id,
                                    fps: fps.min(999),
                                });
                            }
                            MappingEvent::Renderer {
                                id,
                                event: RendererEvent::TopologyLost,
                            } => {
                                let active_run = active.as_mut().expect("guard checked active run");
                                if active_run.cancel.load(Ordering::Acquire) {
                                    continue;
                                }
                                emit(state(
                                    "Reconnecting",
                                    format!("Output {} changed · rebinding renderer…", id + 1),
                                    false,
                                    true,
                                    "",
                                ));
                                let mut recovery = active_run.session.recover_group_topology(
                                    id,
                                    Arc::clone(&active_run.reporter),
                                    &active_run.cancel,
                                );
                                if recovery.is_err() && !active_run.cancel.load(Ordering::Acquire) {
                                    diagnostics::log(
                                        Level::Warn,
                                        "controller",
                                        "renderer-retry",
                                        Some(active_run.session_id),
                                        format!(
                                            "group {id}: first in-place renderer recovery failed; retrying once"
                                        ),
                                    );
                                    recovery = active_run.session.recover_group_topology(
                                        id,
                                        Arc::clone(&active_run.reporter),
                                        &active_run.cancel,
                                    );
                                }
                                match recovery {
                                    Ok(_) => {
                                        diagnostics::log(
                                            Level::Info,
                                            "controller",
                                            "renderer-recovered",
                                            Some(active_run.session_id),
                                            format!(
                                                "group {id}: renderer rebound without recreating the virtual display"
                                            ),
                                        );
                                        emit(state(
                                            "Running",
                                            format!("Output {} recovered", id + 1),
                                            true,
                                            false,
                                            "",
                                        ));
                                    }
                                    Err(error) => {
                                        diagnostics::log(
                                            Level::Error,
                                            "controller",
                                            error.stage(),
                                            Some(active_run.session_id),
                                            error.to_string(),
                                        );
                                        emit(state(
                                            "Degraded",
                                            format!("Output {} needs attention", id + 1),
                                            true,
                                            false,
                                            error.to_string(),
                                        ));
                                    }
                                }
                            }
                            MappingEvent::Renderer {
                                id,
                                event: RendererEvent::Failed(error),
                            } => {
                                let (stopped_session_id, cleanup) =
                                    stop_active(&mut active, &sunshine_sender)
                                        .expect("guard checked active run");
                                let session_id = Some(stopped_session_id);
                                let cleanup_error = cleanup.err().map(|cleanup| {
                                    diagnostics::log(
                                        Level::Error,
                                        "controller",
                                        cleanup.stage(),
                                        session_id,
                                        cleanup.to_string(),
                                    );
                                    cleanup.to_string()
                                });
                                let error = match cleanup_error {
                                    Some(cleanup) => {
                                        format!("{error}; cleanup also failed: {cleanup}")
                                    }
                                    None => error,
                                };
                                emit(state(
                                    "Stopped",
                                    format!("Output {} stopped unexpectedly", id + 1),
                                    false,
                                    false,
                                    error,
                                ));
                            }
                            MappingEvent::Topology { .. } => {}
                        }
                    }
                    Message::Sunshine(event)
                        if active
                            .as_ref()
                            .is_some_and(|active| active.generation == event.generation) =>
                    {
                        emit(ControllerEvent::Sunshine {
                            id: event.group_id,
                            display_id: event.display_id,
                            requested_port: event.requested_port,
                            port: event.port,
                            error: event.error,
                        });
                    }
                    Message::Sunshine(_) => {}
                    Message::Mapping { .. } => {}
                }
            }
            sunshine.shutdown();
        });
        Self {
            sender,
            shutdown_requested,
            worker: Some(worker),
        }
    }

    pub fn sender(&self) -> ControllerSender {
        self.sender.clone()
    }

    pub fn shutdown(mut self) {
        self.shutdown_requested.store(true, Ordering::Release);
        self.sender.cancel_startups();
        let _ = self.sender.tx.send(Message::Command(Command::Shutdown));
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

fn format_mapping_group_plan(group: &MappingGroupRequest) -> String {
    let route = match &group.route {
        MappingRoute::Mirror { target } => format!("mirror target={target}"),
        MappingRoute::StreamOnly => "stream_only".into(),
    };
    format!(
        "group {} route={} native={}x{}@{}/{} orientation={:?} topology_rotation={:?}",
        group.id,
        route,
        group.mode.width,
        group.mode.height,
        group.mode.refresh_numerator,
        group.mode.refresh_denominator,
        group.rotation,
        native_topology_rotation(group.rotation)
    )
}

fn capture_backend_for_plan(plan: &MappingPlan) -> CaptureBackend {
    if projected_desktop_duplication_processes(plan) > DESKTOP_DUPLICATION_PROCESS_LIMIT {
        CaptureBackend::Wgc
    } else {
        CaptureBackend::Auto
    }
}

fn projected_desktop_duplication_processes(plan: &MappingPlan) -> usize {
    let renderer_processes = usize::from(
        plan.groups
            .iter()
            .any(|group| matches!(group.route, MappingRoute::Mirror { .. })),
    );
    renderer_processes + plan.groups.len()
}

fn refresh(emit: &impl Fn(ControllerEvent)) {
    match active_displays() {
        Ok(displays) => {
            let displays = displays
                .into_iter()
                .filter(|display| !display.virtual_display)
                .map(|display| DisplayOption {
                    id: display.id,
                    name: display.name.clone(),
                    native_width: display.native_width,
                    native_height: display.native_height,
                    detected_physical_width_mm: display.physical_width_mm,
                    detected_physical_height_mm: display.physical_height_mm,
                    physical_width_mm: display.physical_width_mm,
                    physical_height_mm: display.physical_height_mm,
                    physical_override_inches: None,
                    physical_override_aspect_ratio: None,
                    rotation: display.rotation,
                    label: format!(
                        "{} · {}×{}{}",
                        display.name,
                        display.rect.right - display.rect.left,
                        display.rect.bottom - display.rect.top,
                        if display.primary { " · Primary" } else { "" }
                    ),
                })
                .collect();
            emit(ControllerEvent::Displays(displays));
        }
        Err(error) => {
            diagnostics::log(
                Level::Error,
                "controller",
                "display-discovery",
                None,
                error.to_string(),
            );
            emit(state(
                "Stopped",
                "Display discovery failed".into(),
                false,
                false,
                error.to_string(),
            ));
        }
    }
}

fn stop_active(
    active: &mut Option<ActiveRun>,
    sunshine_sender: &sunshine::SupervisorSender,
) -> Option<(MappingSessionId, Result<(), MappingError>)> {
    sunshine_sender.stop_all();
    let mut active = active.take()?;
    let session_id = active.session_id;
    Some((session_id, active.session.stop()))
}

fn stop_with_state(
    active: &mut Option<ActiveRun>,
    sunshine_sender: &sunshine::SupervisorSender,
    emit: &impl Fn(ControllerEvent),
) {
    if active.is_none() {
        let _ = stop_active(active, sunshine_sender);
        return;
    }
    emit(state(
        "Stopping",
        "Restoring windows and display topology…".into(),
        true,
        true,
        "",
    ));
    let (session_id, result) =
        stop_active(active, sunshine_sender).expect("active run was checked before cleanup");
    let stopped_session_id = Some(session_id);
    match result {
        Ok(()) => {
            diagnostics::log(
                Level::Info,
                "controller",
                "stop",
                stopped_session_id,
                "mapping stopped",
            );
            emit(state(
                "Stopped",
                "Choose a display to start".into(),
                false,
                false,
                "",
            ));
        }
        Err(error) => {
            diagnostics::log(
                Level::Error,
                "controller",
                error.stage(),
                stopped_session_id,
                error.to_string(),
            );
            emit(state(
                "Stopped",
                "Cleanup completed with errors".into(),
                false,
                false,
                error.to_string(),
            ));
        }
    }
}

fn queue_reconnect_after_failure(
    attempt: u8,
    plan: &MappingPlan,
    error: impl Into<String>,
    sender: &Sender<Message>,
    emit: &impl Fn(ControllerEvent),
    session_id: Option<MappingSessionId>,
    cancel: &Arc<AtomicBool>,
) -> bool {
    let error = error.into();
    if cancel.load(Ordering::Acquire) {
        return false;
    }
    if let Some(next_attempt) = next_reconnect_attempt(attempt) {
        diagnostics::log(
            Level::Warn,
            "controller",
            "reconnect-retry",
            session_id,
            format!(
                "reconnect attempt {attempt} failed; retrying with attempt {next_attempt}: {error}"
            ),
        );
        let _ = sender.send(Message::Command(Command::Start {
            plan: plan.clone(),
            cancel: Arc::clone(cancel),
            reason: StartReason::Reconnect {
                attempt: next_attempt,
            },
        }));
        true
    } else {
        diagnostics::log(
            Level::Error,
            "controller",
            "reconnect-exhausted",
            session_id,
            format!("automatic reconnect failed after {MAX_RECONNECT_ATTEMPTS} attempts: {error}"),
        );
        emit(restartable_state(
            "Automatic reconnect timed out",
            format!("Automatic reconnect failed after {MAX_RECONNECT_ATTEMPTS} attempts: {error}"),
        ));
        false
    }
}

fn next_reconnect_attempt(attempt: u8) -> Option<u8> {
    (attempt < MAX_RECONNECT_ATTEMPTS).then_some(attempt + 1)
}

fn state(
    state: &'static str,
    detail: String,
    running: bool,
    busy: bool,
    error: impl Into<String>,
) -> ControllerEvent {
    ControllerEvent::State {
        state,
        detail,
        running,
        busy,
        error: error.into(),
        restart_available: false,
    }
}

fn restartable_state(detail: impl Into<String>, error: impl Into<String>) -> ControllerEvent {
    ControllerEvent::State {
        state: "Stopped",
        detail: detail.into(),
        running: false,
        busy: false,
        error: error.into(),
        restart_available: true,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::session_gate::VirtualMode;

    fn plan(routes: Vec<MappingRoute>) -> MappingPlan {
        MappingPlan {
            groups: routes
                .into_iter()
                .enumerate()
                .map(|(id, route)| MappingGroupRequest {
                    id: id as u32,
                    mode: VirtualMode::default(),
                    rotation: crate::geometry::Rotation::Deg0,
                    route,
                })
                .collect(),
        }
    }

    #[test]
    fn stays_on_auto_at_the_desktop_duplication_limit() {
        let plan = plan(vec![
            MappingRoute::Mirror {
                target: "display-a".into(),
            },
            MappingRoute::StreamOnly,
            MappingRoute::StreamOnly,
        ]);

        assert_eq!(capture_backend_for_plan(&plan), CaptureBackend::Auto);
    }

    #[test]
    fn uses_wgc_when_a_mirror_renderer_pushes_four_sunshine_instances_over_the_limit() {
        let plan = plan(vec![
            MappingRoute::Mirror {
                target: "display-a".into(),
            },
            MappingRoute::StreamOnly,
            MappingRoute::StreamOnly,
            MappingRoute::StreamOnly,
        ]);

        assert_eq!(capture_backend_for_plan(&plan), CaptureBackend::Wgc);
    }

    #[test]
    fn uses_wgc_for_five_stream_only_instances() {
        let plan = plan(vec![
            MappingRoute::StreamOnly,
            MappingRoute::StreamOnly,
            MappingRoute::StreamOnly,
            MappingRoute::StreamOnly,
            MappingRoute::StreamOnly,
        ]);

        assert_eq!(capture_backend_for_plan(&plan), CaptureBackend::Wgc);
    }

    #[test]
    fn keeps_four_stream_only_instances_on_auto() {
        let plan = plan(vec![
            MappingRoute::StreamOnly,
            MappingRoute::StreamOnly,
            MappingRoute::StreamOnly,
            MappingRoute::StreamOnly,
        ]);

        assert_eq!(capture_backend_for_plan(&plan), CaptureBackend::Auto);
    }

    #[test]
    fn stop_cancels_a_queued_start_before_the_worker_receives_stop() {
        let (tx, rx) = mpsc::channel();
        let sender = ControllerSender::new(tx, Arc::new(AtomicBool::new(false)));
        sender.start(plan(vec![MappingRoute::StreamOnly]));
        let Message::Command(Command::Start { cancel, .. }) = rx.recv().unwrap() else {
            panic!("expected start command");
        };

        assert!(!cancel.load(Ordering::Acquire));
        sender.stop();
        assert!(cancel.load(Ordering::Acquire));
        assert!(matches!(
            rx.recv().unwrap(),
            Message::Command(Command::Stop)
        ));
    }

    #[test]
    fn a_start_sent_after_stop_gets_a_fresh_cancellation_token() {
        let (tx, rx) = mpsc::channel();
        let sender = ControllerSender::new(tx, Arc::new(AtomicBool::new(false)));
        sender.stop();
        sender.start(plan(vec![MappingRoute::StreamOnly]));
        assert!(matches!(
            rx.recv().unwrap(),
            Message::Command(Command::Stop)
        ));
        let Message::Command(Command::Start { cancel, .. }) = rx.recv().unwrap() else {
            panic!("expected start command");
        };

        assert!(!cancel.load(Ordering::Acquire));
    }

    #[test]
    fn a_start_created_after_shutdown_is_already_cancelled() {
        let (tx, rx) = mpsc::channel();
        let shutdown = Arc::new(AtomicBool::new(false));
        let sender = ControllerSender::new(tx, Arc::clone(&shutdown));
        shutdown.store(true, Ordering::Release);
        sender.start(plan(vec![MappingRoute::StreamOnly]));
        let Message::Command(Command::Start { cancel, .. }) = rx.recv().unwrap() else {
            panic!("expected start command");
        };

        assert!(cancel.load(Ordering::Acquire));
    }

    #[test]
    fn reconnect_retries_at_most_twice() {
        assert_eq!(next_reconnect_attempt(1), Some(2));
        assert_eq!(next_reconnect_attempt(2), None);
    }

    #[test]
    fn stop_cancels_a_queued_manual_restart() {
        let (tx, rx) = mpsc::channel();
        let sender = ControllerSender::new(tx, Arc::new(AtomicBool::new(false)));
        sender.restart_mapping();
        let Message::Command(Command::RestartMapping { cancel }) = rx.recv().unwrap() else {
            panic!("expected restart command");
        };

        assert!(!cancel.load(Ordering::Acquire));
        sender.stop();
        assert!(cancel.load(Ordering::Acquire));
    }

    #[test]
    fn cancelled_reconnect_is_not_queued() {
        let (tx, rx) = mpsc::channel();
        let cancel = Arc::new(AtomicBool::new(true));
        let retry_plan = plan(vec![MappingRoute::StreamOnly]);

        assert!(!queue_reconnect_after_failure(
            1,
            &retry_plan,
            "topology still unavailable",
            &tx,
            &|_| {},
            None,
            &cancel,
        ));
        assert!(rx.try_recv().is_err());
    }
}
