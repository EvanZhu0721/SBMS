use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex, Weak};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

#[path = "controller/desktop.rs"]
mod desktop_state;
#[cfg(test)]
#[path = "controller/recovery_live_test.rs"]
mod recovery_live_test;
use crate::diagnostics::{self, Level, MappingSessionId};
use crate::display::active_displays;
use crate::geometry::{AspectRatio, Rotation};
use crate::mapping::{
    ActiveGroup, MappingError, MappingEvent, MappingGroupInfo, MappingGroupRequest, MappingPlan,
    MappingReporter, MappingRoute, MappingSession, native_topology_rotation,
};
use crate::renderer::RendererEvent;
use crate::sunshine::{self, CaptureBackend, DeploymentEvent};
use desktop_state::{DesktopEvent, DesktopMonitor, DesktopState};

const DESKTOP_DUPLICATION_PROCESS_LIMIT: usize = 4;
const MAX_RECONNECT_ATTEMPTS: u8 = 2;
const RENDERER_RECOVERY_WINDOW: Duration = Duration::from_secs(30);
const RENDERER_RECOVERY_ATTEMPTS: u8 = 4;

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
    pub preferred_refresh_millihz: Option<u32>,
    pub current_refresh_millihz: u32,
    pub refresh_override_millihz: Option<u32>,
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
    #[cfg(test)]
    TestActivate(Box<ActiveRun>),
    Command(Command),
    Mapping {
        generation: u64,
        event: MappingEvent,
    },
    Sunshine(DeploymentEvent),
    Desktop(DesktopEvent),
    Recovered(Box<RecoveryCompletion>),
    Stopped {
        session_id: MappingSessionId,
        result: Result<(), MappingError>,
    },
}

struct RecoveryCompletion {
    generation: u64,
    id: u32,
    desktop: (bool, u64),
    group: ActiveGroup,
    topology: Option<Vec<crate::display::Display>>,
    result: Result<(), MappingError>,
    cleaned: bool,
    paused: bool,
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
    recovery: HashMap<u32, PendingRecovery>,
    interrupted_failure: Option<MappingError>,
    recovery_jobs: HashMap<u32, Arc<AtomicBool>>,
}

/// A pending group belongs to its ActiveRun, so Stop also discards all timers.
/// Only desktop transitions may rearm an exhausted transient failure.
struct PendingRecovery {
    next: Option<Instant>,
    deadline: Option<Instant>,
    attempts: u8,
    error: Option<String>,
    resumable: bool,
    pause: bool,
}

impl PendingRecovery {
    fn new(now: Instant, available: bool) -> Self {
        Self {
            next: available.then_some(now),
            deadline: None,
            attempts: 0,
            error: None,
            resumable: true,
            pause: false,
        }
    }

    fn desktop_changed(&mut self, now: Instant, available: bool) {
        if self.resumable {
            *self = Self::new(now, available);
        }
    }

    fn begin_attempt(&mut self, now: Instant) -> Instant {
        self.next = None;
        self.attempts += 1;
        *self.deadline.get_or_insert(now + RENDERER_RECOVERY_WINDOW)
    }

    fn failed(&mut self, now: Instant, error: String, retryable: bool) {
        self.error = Some(error);
        self.resumable = retryable;
        let delay = Duration::from_millis(500 * u64::from(self.attempts.max(1)));
        self.next = (retryable
            && self.attempts < RENDERER_RECOVERY_ATTEMPTS
            && self.deadline.is_some_and(|deadline| now + delay < deadline))
        .then_some(now + delay);
    }

    fn exhausted(&self) -> bool {
        self.next.is_none() && self.error.is_some()
    }
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
            let mut stop_requested = false;
            let mut stopping = false;
            let mut stopping_plan: Option<MappingPlan> = None;
            let mut shutdown_pending = false;
            let mut restart_after_stop = None;
            let mut next_generation = 0_u64;
            let desktop_tx = worker_tx.clone();
            let mut desktop_monitor_error = None;
            let desktop_monitor = DesktopMonitor::start(move |event| {
                let _ = desktop_tx.send(Message::Desktop(event));
            })
            .map_err(|error| {
                desktop_monitor_error = Some(error.clone());
                diagnostics::log(Level::Error, "controller", "desktop-monitor", None, &error);
                emit(state(
                    "Stopped",
                    "Desktop monitor unavailable".into(),
                    false,
                    false,
                    error,
                ));
            })
            .ok();
            let desktop = desktop_monitor.as_ref().map(DesktopMonitor::state);
            let mut observed_desktop = desktop_snapshot(desktop.as_deref());
            loop {
                let timeout = active.as_ref().and_then(next_recovery_delay);
                let message = match timeout {
                    Some(timeout) => rx.recv_timeout(timeout),
                    None => rx.recv().map_err(|_| mpsc::RecvTimeoutError::Disconnected),
                };
                synchronize_desktop(
                    &mut active,
                    desktop.as_deref(),
                    &mut observed_desktop,
                    &emit,
                );
                match message {
                    Err(mpsc::RecvTimeoutError::Disconnected) => break,
                    Err(mpsc::RecvTimeoutError::Timeout) => {}
                    Ok(message) => match message {
                        #[cfg(test)]
                        Message::TestActivate(run) => active = Some(*run),
                        Message::Command(Command::Refresh) => {
                            refresh(&emit, active.is_some() || stopping)
                        }
                        Message::Command(Command::Start {
                            plan,
                            cancel,
                            reason,
                        }) => {
                            if active.is_some() || stopping {
                                continue;
                            }
                            if let Some(error) = &desktop_monitor_error
                                && plan
                                    .groups
                                    .iter()
                                    .any(|group| matches!(group.route, MappingRoute::Mirror { .. }))
                            {
                                emit(state(
                                    "Stopped",
                                    "Desktop monitor unavailable; restart SBMS".into(),
                                    false,
                                    false,
                                    error,
                                ));
                                continue;
                            }
                            if reason == StartReason::User {
                                restart_plan = None;
                            }
                            let sunshine_ports = match sunshine_ports_for_plan(&plan) {
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
                                format!(
                                    "mapping plan requested with {} group(s)",
                                    plan.groups.len()
                                ),
                            );
                            if !sunshine_ports.is_empty() {
                                diagnostics::log(
                                    Level::Info,
                                    "controller",
                                    "sunshine-capture",
                                    Some(session_id),
                                    format!(
                                        "selected capture={} for {} managed Sunshine instance(s); projected DDA processes={} (limit={})",
                                        sunshine_capture.as_str(),
                                        sunshine_ports.len(),
                                        projected_desktop_duplication_processes(&plan),
                                        DESKTOP_DUPLICATION_PROCESS_LIMIT,
                                    ),
                                );
                            }
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
                                    format!(
                                        "Reconnect attempt {attempt} of {MAX_RECONNECT_ATTEMPTS}…"
                                    ),
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
                                if let MappingEvent::Renderer { id, event, .. } = &event {
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
                                        recovery: HashMap::new(),
                                        interrupted_failure: None,
                                        recovery_jobs: HashMap::new(),
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
                            restart_after_stop = None;
                            request_stop(&active, &emit);
                            stop_requested = true;
                        }
                        Message::Command(Command::RestartMapping { cancel }) => {
                            if !cancel.load(Ordering::Acquire) {
                                if let Some(run) = active.as_ref() {
                                    restart_after_stop = Some((run.session.plan(), cancel));
                                    request_stop(&active, &emit);
                                    stop_requested = true;
                                } else if stopping {
                                    if let Some(plan) = stopping_plan.clone() {
                                        restart_after_stop = Some((plan, cancel));
                                    }
                                } else if let Some(plan) = restart_plan.take() {
                                    let _ = worker_tx.send(Message::Command(Command::Start {
                                        plan,
                                        cancel,
                                        reason: StartReason::ManualRestart,
                                    }));
                                }
                            }
                        }
                        Message::Command(Command::Shutdown) => {
                            restart_after_stop = None;
                            shutdown_pending = true;
                            request_stop(&active, &emit);
                            stop_requested = true;
                        }
                        Message::Recovered(completed) => {
                            complete_recovery(
                                &mut active,
                                *completed,
                                desktop.as_deref(),
                                &worker_tx,
                                &emit,
                            );
                        }
                        Message::Stopped { session_id, result } => {
                            stopping = false;
                            stopping_plan = None;
                            let clean = result.is_ok();
                            report_stopped(session_id, result, &emit);
                            if let Some((plan, cancel)) = restart_after_stop.take()
                                && clean
                                && !shutdown_pending
                                && !cancel.load(Ordering::Acquire)
                            {
                                let _ = worker_tx.send(Message::Command(Command::Start {
                                    plan,
                                    cancel,
                                    reason: StartReason::ManualRestart,
                                }));
                            }
                        }
                        Message::Mapping { generation, event }
                            if active
                                .as_ref()
                                .is_some_and(|active| active.generation == generation) =>
                        {
                            if let MappingEvent::Renderer {
                                id, renderer_id, ..
                            } = &event
                                && !active
                                    .as_ref()
                                    .expect("guard checked active run")
                                    .session
                                    .accepts_renderer_event(*id, *renderer_id)
                            {
                                continue;
                            }
                            match event {
                                MappingEvent::GroupReady(group) => {
                                    let active = active.as_ref().expect("guard checked active run");
                                    if matches!(group.route, MappingRoute::StreamOnly)
                                        && let (Some(display_id), Some(port)) = (
                                            group.sunshine_id.clone(),
                                            active.sunshine_ports.get(&group.id).copied(),
                                        )
                                    {
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
                                    ..
                                } => {
                                    emit(ControllerEvent::Fps {
                                        id,
                                        fps: fps.min(999),
                                    });
                                }
                                MappingEvent::Renderer {
                                    id,
                                    event: RendererEvent::TopologyLost,
                                    ..
                                } => {
                                    let active_run =
                                        active.as_mut().expect("guard checked active run");
                                    if active_run.cancel.load(Ordering::Acquire) {
                                        continue;
                                    }
                                    let (available, _) = desktop_snapshot(desktop.as_deref());
                                    queue_renderer_recovery(active_run, id, available);
                                    emit_recovery_state(active_run, available, &emit);
                                }
                                MappingEvent::Renderer {
                                    id,
                                    event: RendererEvent::Failed(error),
                                    ..
                                } => {
                                    active
                                        .as_mut()
                                        .expect("guard checked active run")
                                        .interrupted_failure
                                        .get_or_insert_with(|| {
                                            MappingError::renderer_failed(id, &error)
                                        });
                                    let output_number = active
                                        .as_ref()
                                        .expect("guard checked active run")
                                        .session
                                        .groups()
                                        .position(|group| group.id == id)
                                        .expect("accepted renderer belongs to an active group")
                                        + 1;
                                    diagnostics::log(
                                        Level::Error,
                                        "controller",
                                        "renderer",
                                        None,
                                        format!(
                                            "Output {output_number} stopped unexpectedly: {error}"
                                        ),
                                    );
                                    emit(state(
                                        "Degraded",
                                        format!("Output {output_number} stopped unexpectedly"),
                                        true,
                                        false,
                                        error,
                                    ));
                                    request_stop(&active, &emit);
                                    stop_requested = true;
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
                        Message::Desktop(DesktopEvent::MonitorFailed) => {
                            let error = desktop_monitor_error.get_or_insert_with(||
                            "Desktop monitor stopped; restart SBMS. See diagnostics for the failed API.".into());
                            if let Some(active) = active.as_mut() {
                                // A Start command may have preceded this failure in
                                // the queue, so include newly started mirror groups.
                                let ids: Vec<_> = active
                                    .session
                                    .groups()
                                    .filter(|group| {
                                        matches!(group.route, MappingRoute::Mirror { .. })
                                    })
                                    .map(|group| group.id)
                                    .collect();
                                for id in ids {
                                    queue_renderer_recovery(active, id, false);
                                }
                                for pending in active.recovery.values_mut() {
                                    pending.failed(Instant::now(), error.clone(), false);
                                }
                                if !active.recovery.is_empty() {
                                    emit_recovery_state(active, false, &emit);
                                }
                            }
                        }
                        Message::Desktop(_) => {}
                        Message::Sunshine(_) => {}
                        Message::Mapping { .. } => {}
                    },
                }
                synchronize_desktop(
                    &mut active,
                    desktop.as_deref(),
                    &mut observed_desktop,
                    &emit,
                );
                if stop_requested
                    && !stopping
                    && active
                        .as_ref()
                        .is_none_or(|run| run.recovery_jobs.is_empty())
                {
                    stop_requested = false;
                    if let Some(run) = active.take() {
                        stopping = true;
                        stopping_plan = Some(run.session.plan());
                        sunshine_sender.stop_all();
                        let tx = worker_tx.clone();
                        thread::spawn(move || {
                            let mut run = run;
                            let cleanup = run.session.stop();
                            let result = stop_result(run.interrupted_failure.take(), cleanup);
                            let _ = tx.send(Message::Stopped {
                                session_id: run.session_id,
                                result,
                            });
                        });
                    }
                }
                if shutdown_pending && active.is_none() && !stopping {
                    break;
                }
                if let Some(active) = active.as_mut() {
                    recover_due_renderer(
                        active,
                        desktop.clone(),
                        observed_desktop,
                        &worker_tx,
                        &emit,
                    );
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

fn desktop_snapshot(desktop: Option<&DesktopState>) -> (bool, u64) {
    desktop
        .map(|desktop| (desktop.can_capture(), desktop.revision()))
        .unwrap_or((true, 0))
}

fn synchronize_desktop(
    active: &mut Option<ActiveRun>,
    desktop: Option<&DesktopState>,
    observed: &mut (bool, u64),
    emit: &impl Fn(ControllerEvent),
) {
    let snapshot = desktop_snapshot(desktop);
    if snapshot == *observed {
        return;
    }
    *observed = snapshot;
    let Some(active) = active
        .as_mut()
        .filter(|active| !active.cancel.load(Ordering::Acquire))
    else {
        return;
    };
    diagnostics::log(
        Level::Info,
        "controller",
        "desktop-state",
        Some(active.session_id),
        format!("capture_allowed={}, revision={}", snapshot.0, snapshot.1),
    );
    let ids: Vec<_> = active
        .session
        .groups()
        .filter(|group| matches!(group.route, MappingRoute::Mirror { .. }))
        .map(|group| group.id)
        .collect();
    for id in ids {
        queue_renderer_recovery(active, id, snapshot.0);
        active
            .recovery
            .get_mut(&id)
            .expect("pending group inserted")
            .desktop_changed(Instant::now(), snapshot.0);
        active.recovery.get_mut(&id).expect("queued recovery").pause =
            active.session.has_renderer(id);
        if !snapshot.0 && active.session.has_renderer(id) && !active.recovery_jobs.contains_key(&id)
        {
            active.recovery.get_mut(&id).expect("queued recovery").next = Some(Instant::now());
        }
    }
    if !active.recovery.is_empty() {
        emit_recovery_state(active, snapshot.0, emit);
    }
}

fn next_recovery_delay(active: &ActiveRun) -> Option<Duration> {
    if active.cancel.load(Ordering::Acquire) {
        return None;
    }
    active
        .recovery
        .iter()
        .filter(|(id, _)| !active.recovery_jobs.contains_key(id))
        .filter_map(|(_, pending)| {
            if pending.pause {
                Some(Instant::now())
            } else {
                pending.next
            }
        })
        .min()
        .map(|next| next.saturating_duration_since(Instant::now()))
}

fn queue_renderer_recovery(active: &mut ActiveRun, id: u32, available: bool) {
    active
        .recovery
        .entry(id)
        .or_insert_with(|| PendingRecovery::new(Instant::now(), available));
    active.recovery.get_mut(&id).expect("queued recovery").pause = active.session.has_renderer(id);
    if let Some(cancel) = active.recovery_jobs.get(&id) {
        cancel.store(true, Ordering::Release);
    } else if !available && active.session.has_renderer(id) {
        active.recovery.get_mut(&id).expect("queued recovery").next = Some(Instant::now());
    }
}

fn emit_recovery_state(active: &ActiveRun, available: bool, emit: &impl Fn(ControllerEvent)) {
    if active.recovery.is_empty() {
        emit(state(
            "Running",
            "Mapping is active".into(),
            true,
            false,
            "",
        ));
    } else if let Some((id, pending)) = active
        .recovery
        .iter()
        .find(|(_, pending)| pending.exhausted())
    {
        let output_number = active
            .session
            .groups()
            .position(|group| group.id == *id)
            .expect("pending recovery belongs to an active group")
            + 1;
        emit(state(
            "Degraded",
            format!("Output {output_number} needs attention"),
            true,
            false,
            pending.error.as_deref().unwrap_or_default(),
        ));
    } else {
        // Keep Stop enabled while waiting; the virtual mapping is still active.
        emit(state(
            "Reconnecting",
            if available {
                "Restoring capture…"
            } else {
                "Waiting for desktop…"
            }
            .into(),
            true,
            false,
            "",
        ));
    }
}

fn recover_due_renderer(
    active: &mut ActiveRun,
    desktop: Option<Arc<DesktopState>>,
    observed_desktop: (bool, u64),
    tx: &Sender<Message>,
    emit: &impl Fn(ControllerEvent),
) {
    if active.cancel.load(Ordering::Acquire) {
        return;
    }
    let now = Instant::now();
    let Some(id) = active
        .recovery
        .iter()
        .filter(|(id, pending)| {
            !active.recovery_jobs.contains_key(id)
                && (pending.pause || pending.next.is_some_and(|next| next <= now))
        })
        .min_by_key(|(id, pending)| (pending.next, **id))
        .map(|(id, _)| *id)
    else {
        return;
    };
    let before = desktop_snapshot(desktop.as_deref());
    if before != observed_desktop {
        return;
    }
    let pending = active.recovery.get_mut(&id).expect("selected recovery");
    let pause_only = pending.pause && (!before.0 || !pending.resumable);
    pending.pause = false;
    let deadline = if !pause_only {
        pending.begin_attempt(now)
    } else {
        pending.next = None;
        now
    };
    let mut group = active.session.take_recovery_group(id);
    let job_cancel = Arc::new(AtomicBool::new(false));
    active.recovery_jobs.insert(id, Arc::clone(&job_cancel));
    let run_cancel = Arc::clone(&active.cancel);
    let reporter = Arc::clone(&active.reporter);
    let generation = active.generation;
    let tx = tx.clone();
    diagnostics::log(
        Level::Info,
        "controller",
        "renderer-recovery",
        Some(active.session_id),
        format!(
            "group {id}: background attempt {}, desktop revision {}",
            pending.attempts, before.1
        ),
    );
    thread::spawn(move || {
        let (topology, result) = if !pause_only {
            group.recover(reporter, || {
                run_cancel.load(Ordering::Acquire)
                    || job_cancel.load(Ordering::Acquire)
                    || desktop_snapshot(desktop.as_deref()) != before
                    || Instant::now() >= deadline
            })
        } else {
            (None, group.pause_renderer())
        };
        let _ = tx.send(Message::Recovered(Box::new(RecoveryCompletion {
            generation,
            id,
            desktop: before,
            group,
            topology,
            result,
            cleaned: false,
            paused: pause_only,
        })));
    });
    emit_recovery_state(active, before.0, emit);
}

fn complete_recovery(
    active: &mut Option<ActiveRun>,
    mut completed: RecoveryCompletion,
    desktop: Option<&DesktopState>,
    tx: &Sender<Message>,
    emit: &impl Fn(ControllerEvent),
) {
    let current = desktop_snapshot(desktop);
    let accepted = active.as_ref().is_some_and(|run| {
        run.generation == completed.generation
            && !run.cancel.load(Ordering::Acquire)
            && current == completed.desktop
            && run
                .recovery_jobs
                .get(&completed.id)
                .is_some_and(|cancel| !cancel.load(Ordering::Acquire))
    });
    // Do not drop/join a stale live candidate on the message thread. Return all
    // group resources after its renderer has been stopped by the cleanup worker.
    if !accepted && !completed.cleaned {
        let tx = tx.clone();
        thread::spawn(move || {
            if let Err(cleanup) = completed.group.pause_renderer() {
                if let Err(error) = completed.result.as_mut() {
                    error.record_cleanup_failure(cleanup);
                } else {
                    completed.result = Err(cleanup);
                }
            }
            completed.cleaned = true;
            let _ = tx.send(Message::Recovered(Box::new(completed)));
        });
        return;
    }
    let run = active
        .as_mut()
        .expect("session retained until all recovery resources return");
    assert_eq!(run.generation, completed.generation);
    run.recovery_jobs.remove(&completed.id);
    let activate = accepted && !completed.paused && completed.result.is_ok();
    run.session.return_recovery_group(
        completed.group,
        accepted.then_some(completed.topology).flatten(),
        activate,
    );
    let fatal = completed
        .result
        .as_ref()
        .err()
        .is_some_and(|error| !error.is_clean_cancellation() && !error.is_retryable());
    if run.cancel.load(Ordering::Acquire) {
        if fatal {
            run.interrupted_failure =
                stop_result(run.interrupted_failure.take(), completed.result).err();
        }
        return;
    }
    if !accepted && !fatal {
        // synchronize_desktop already rearmed the group for the new revision.
        return;
    }
    match completed.result {
        Ok(()) if completed.paused => {}
        Ok(()) => {
            run.recovery.remove(&completed.id);
            diagnostics::log(
                Level::Info,
                "controller",
                "renderer-recovered",
                Some(run.session_id),
                format!(
                    "group {}: first frame presented; virtual display retained",
                    completed.id
                ),
            );
        }
        Err(error) => {
            let retryable = error.is_retryable() || error.is_clean_cancellation();
            let pending = run
                .recovery
                .get_mut(&completed.id)
                .expect("completed recovery");
            let message = if error.is_clean_cancellation() {
                "desktop recovery exceeded its existing 30-second budget".to_string()
            } else {
                error.to_string()
            };
            pending.failed(Instant::now(), message.clone(), retryable);
            diagnostics::log(
                if pending.exhausted() {
                    Level::Error
                } else {
                    Level::Warn
                },
                "controller",
                if pending.exhausted() {
                    "renderer-recovery-failed"
                } else {
                    "renderer-retry"
                },
                Some(run.session_id),
                format!("group {}: {message}", completed.id),
            );
        }
    }
    emit_recovery_state(run, current.0, emit);
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

fn sunshine_ports_for_plan(plan: &MappingPlan) -> Result<HashMap<u32, u16>, String> {
    plan.groups
        .iter()
        .enumerate()
        .filter(|(_, group)| matches!(group.route, MappingRoute::StreamOnly))
        .map(|(slot, group)| sunshine::default_port_for_slot(slot).map(|port| (group.id, port)))
        .collect()
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
    renderer_processes
        + plan
            .groups
            .iter()
            .filter(|group| matches!(group.route, MappingRoute::StreamOnly))
            .count()
}

fn refresh(emit: &impl Fn(ControllerEvent), mapping_present: bool) {
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
                    preferred_refresh_millihz: display.preferred_refresh_millihz,
                    current_refresh_millihz: ((u64::from(display.refresh_numerator) * 1000)
                        / u64::from(display.refresh_denominator.max(1)))
                        as u32,
                    refresh_override_millihz: None,
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
            if mapping_present {
                return;
            }
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

fn request_stop(active: &Option<ActiveRun>, emit: &impl Fn(ControllerEvent)) {
    if let Some(run) = active.as_ref() {
        run.cancel.store(true, Ordering::Release);
        for cancel in run.recovery_jobs.values() {
            cancel.store(true, Ordering::Release);
        }
        emit(state(
            "Stopping",
            "Restoring windows and display topology…".into(),
            true,
            true,
            "",
        ));
    }
}

fn stop_result(
    failure: Option<MappingError>,
    cleanup: Result<(), MappingError>,
) -> Result<(), MappingError> {
    match failure {
        Some(mut error) => {
            if let Err(cleanup) = cleanup {
                error.record_cleanup_failure(cleanup);
            }
            Err(error)
        }
        None => cleanup,
    }
}

fn report_stopped(
    session_id: MappingSessionId,
    result: Result<(), MappingError>,
    emit: &impl Fn(ControllerEvent),
) {
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
                "Mapping stopped with errors".into(),
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
                    rendering: Default::default(),
                    mode: VirtualMode::default(),
                    rotation: crate::geometry::Rotation::Deg0,
                    route,
                })
                .collect(),
        }
    }

    #[test]
    fn mirror_only_plan_does_not_reserve_sunshine_ports() {
        let plan = plan(vec![
            MappingRoute::Mirror {
                target: "display-a".into(),
            },
            MappingRoute::Mirror {
                target: "display-b".into(),
            },
        ]);

        assert!(sunshine_ports_for_plan(&plan).unwrap().is_empty());
        assert_eq!(projected_desktop_duplication_processes(&plan), 1);
        assert_eq!(capture_backend_for_plan(&plan), CaptureBackend::Auto);
    }

    #[test]
    fn mixed_plan_reserves_only_streaming_ports_and_preserves_output_slots() {
        let plan = plan(vec![
            MappingRoute::Mirror {
                target: "display-a".into(),
            },
            MappingRoute::StreamOnly,
            MappingRoute::Mirror {
                target: "display-b".into(),
            },
            MappingRoute::StreamOnly,
        ]);

        assert_eq!(
            sunshine_ports_for_plan(&plan).unwrap(),
            HashMap::from([
                (1, sunshine::default_port_for_slot(1).unwrap()),
                (3, sunshine::default_port_for_slot(3).unwrap()),
            ])
        );
        assert_eq!(projected_desktop_duplication_processes(&plan), 3);
    }

    #[test]
    fn stream_only_plan_preserves_all_streaming_ports() {
        let plan = plan(vec![MappingRoute::StreamOnly, MappingRoute::StreamOnly]);

        assert_eq!(
            sunshine_ports_for_plan(&plan).unwrap(),
            HashMap::from([
                (0, sunshine::default_port_for_slot(0).unwrap()),
                (1, sunshine::default_port_for_slot(1).unwrap()),
            ])
        );
        assert_eq!(projected_desktop_duplication_processes(&plan), 2);
    }

    #[test]
    fn stays_on_auto_at_the_desktop_duplication_limit() {
        let plan = plan(vec![
            MappingRoute::Mirror {
                target: "display-a".into(),
            },
            MappingRoute::StreamOnly,
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

    #[test]
    fn locked_time_does_not_consume_recovery_budget() {
        let now = Instant::now();
        let mut pending = PendingRecovery::new(now, false);
        assert!(pending.next.is_none());
        assert!(pending.deadline.is_none());
        let unlocked = now + Duration::from_secs(3600);
        pending.desktop_changed(unlocked, true);
        assert_eq!(pending.next, Some(unlocked));
        assert_eq!(
            pending.begin_attempt(unlocked),
            unlocked + RENDERER_RECOVERY_WINDOW
        );
    }

    #[test]
    fn transient_recovery_is_bounded_and_next_desktop_transition_rearms_it() {
        let now = Instant::now();
        let mut pending = PendingRecovery::new(now, true);
        for attempt in 0..RENDERER_RECOVERY_ATTEMPTS {
            let time = now + Duration::from_secs(u64::from(attempt) * 2);
            pending.begin_attempt(time);
            pending.failed(time, "DuplicateOutput: E_ACCESSDENIED".into(), true);
        }
        assert!(pending.exhausted());
        assert!(pending.next.is_none());
        pending.desktop_changed(now + Duration::from_secs(60), true);
        assert_eq!(pending.attempts, 0);
        assert!(pending.error.is_none());
        assert!(pending.next.is_some());
    }

    #[test]
    fn repeated_retry_does_not_extend_first_attempt_deadline() {
        let now = Instant::now();
        let mut pending = PendingRecovery::new(now, true);
        let deadline = pending.begin_attempt(now);
        pending.failed(now, "access denied".into(), true);
        assert_eq!(
            pending.begin_attempt(now + Duration::from_secs(25)),
            deadline
        );
        pending.failed(deadline, "access denied".into(), true);
        assert!(pending.exhausted());
    }

    #[test]
    fn relocking_resets_the_awake_budget_without_scheduling_gpu_work() {
        let now = Instant::now();
        let mut pending = PendingRecovery::new(now, true);
        pending.begin_attempt(now);
        pending.failed(now, "access lost".into(), true);
        pending.desktop_changed(now + Duration::from_secs(1), false);
        assert!(pending.next.is_none());
        assert!(pending.deadline.is_none());
        assert_eq!(pending.attempts, 0);
        assert!(!pending.exhausted());
    }

    #[test]
    fn genuine_failure_is_not_retried_after_unlock() {
        let now = Instant::now();
        let mut pending = PendingRecovery::new(now, true);
        pending.begin_attempt(now);
        pending.failed(now, "device removed".into(), false);
        pending.desktop_changed(now, false);
        pending.desktop_changed(now + Duration::from_secs(1), true);
        assert!(pending.exhausted());
        assert_eq!(pending.error.as_deref(), Some("device removed"));
    }
    fn recovery_run() -> ActiveRun {
        let reporter: MappingReporter = Arc::new(|_| {});
        ActiveRun {
            session: MappingSession::recovery_test_session(Arc::clone(&reporter)),
            reporter,
            generation: 1,
            session_id: diagnostics::new_mapping_session_id(),
            sunshine_ports: HashMap::new(),
            sunshine_capture: CaptureBackend::Auto,
            cancel: Arc::new(AtomicBool::new(false)),
            recovery: HashMap::new(),
            recovery_jobs: HashMap::new(),
            interrupted_failure: None,
        }
    }

    #[test]
    fn renderer_failure_survives_controller_stop_and_next_normal_stop_is_clean() {
        let (events, rx) = mpsc::channel();
        let controller = Controller::spawn(move |event| {
            let _ = events.send(event);
        });
        let sender = controller.sender();
        let wait_stopped = || {
            let deadline = Instant::now() + Duration::from_secs(10);
            let mut stopping = false;
            loop {
                let event = rx
                    .recv_timeout(deadline.saturating_duration_since(Instant::now()))
                    .expect("controller did not complete Stop");
                if let ControllerEvent::State { state, error, .. } = event {
                    stopping |= state == "Stopping";
                    if stopping && state == "Stopped" {
                        return error;
                    }
                }
            }
        };
        let run = recovery_run();
        let renderer_id = run.session.activate_test_renderer_events();
        sender
            .tx
            .send(Message::TestActivate(Box::new(run)))
            .unwrap();
        sender
            .tx
            .send(Message::Mapping {
                generation: 1,
                event: MappingEvent::Renderer {
                    id: 0,
                    renderer_id,
                    event: RendererEvent::Failed("injected render device failure".into()),
                },
            })
            .unwrap();
        let failed_stop = wait_stopped();
        sender
            .tx
            .send(Message::TestActivate(Box::new(recovery_run())))
            .unwrap();
        sender.stop();
        let normal_stop = wait_stopped();
        controller.shutdown();
        assert!(
            failed_stop.contains("injected render device failure"),
            "{failed_stop:?}"
        );
        assert!(normal_stop.is_empty(), "{normal_stop}");
    }

    #[test]
    fn renderer_failure_and_cleanup_failure_both_survive_stop() {
        let result = stop_result(
            Some(MappingError::renderer_failed(
                0,
                "original renderer failure",
            )),
            Err(MappingError::renderer_failed(0, "cleanup failure")),
        );
        let (events, rx) = mpsc::channel();
        report_stopped(diagnostics::new_mapping_session_id(), result, &|event| {
            events.send(event).unwrap();
        });
        let ControllerEvent::State { state, error, .. } = rx.recv().unwrap() else {
            panic!("expected stopped state");
        };
        assert_eq!(state, "Stopped");
        assert!(error.contains("original renderer failure"));
        assert!(error.contains("cleanup failure"));
    }

    #[test]
    fn canceled_recovery_does_not_replace_original_renderer_failure() {
        let mut run = recovery_run();
        run.interrupted_failure = Some(MappingError::renderer_failed(0, "original failure"));
        run.cancel.store(true, Ordering::Release);
        run.recovery_jobs.insert(0, Arc::new(AtomicBool::new(true)));
        let group = run.session.take_recovery_group(0);
        let mut active = Some(run);
        let (tx, _) = mpsc::channel();
        complete_recovery(
            &mut active,
            RecoveryCompletion {
                generation: 1,
                id: 0,
                desktop: (true, 0),
                group,
                topology: None,
                result: Err(MappingError::renderer_failed(0, "recovery cleanup failure")),
                cleaned: true,
                paused: false,
            },
            None,
            &tx,
            &|_| panic!("canceled recovery must not emit state"),
        );
        let run = active.unwrap();
        assert!(run.recovery_jobs.is_empty());
        let error = run.interrupted_failure.unwrap().to_string();
        assert!(error.contains("original failure"));
        assert!(error.contains("recovery cleanup failure"));
    }

    #[test]
    fn inflight_recovery_does_not_busy_poll_after_desktop_change() {
        let mut run = recovery_run();
        run.recovery
            .insert(0, PendingRecovery::new(Instant::now(), true));
        run.recovery_jobs
            .insert(0, Arc::new(AtomicBool::new(false)));
        assert!(next_recovery_delay(&run).is_none());
        run.recovery_jobs.clear();
        assert!(next_recovery_delay(&run).is_some());
    }

    #[test]
    fn stop_is_accepted_before_recovery_resources_return() {
        let mut run = recovery_run();
        let cancel = Arc::new(AtomicBool::new(false));
        run.recovery_jobs.insert(0, Arc::clone(&cancel));
        let active = Some(run);
        let (events, rx) = mpsc::channel();
        request_stop(&active, &|event| {
            events.send(event).unwrap();
        });
        assert!(cancel.load(Ordering::Acquire));
        assert!(active.as_ref().unwrap().cancel.load(Ordering::Acquire));
        assert!(!active.as_ref().unwrap().recovery_jobs.is_empty());
        assert!(matches!(
            rx.try_recv().unwrap(),
            ControllerEvent::State {
                state: "Stopping",
                ..
            }
        ));
        // The session and its display remain owned until completion returns them.
        assert!(active.is_some());
    }

    #[test]
    fn stale_success_is_cleaned_in_background_before_returning_resources() {
        let mut run = recovery_run();
        run.recovery
            .insert(0, PendingRecovery::new(Instant::now(), true));
        run.recovery_jobs.insert(0, Arc::new(AtomicBool::new(true)));
        let group = run.session.take_recovery_group(0);
        let mut active = Some(run);
        let (tx, rx) = mpsc::channel();
        complete_recovery(
            &mut active,
            RecoveryCompletion {
                generation: 1,
                id: 0,
                desktop: (true, 0),
                group,
                topology: None,
                result: Ok(()),
                cleaned: false,
                paused: false,
            },
            None,
            &tx,
            &|_| panic!("stale candidate must not report Running"),
        );
        assert!(active.as_ref().unwrap().recovery_jobs.contains_key(&0));
        let Message::Recovered(completed) = rx.recv_timeout(Duration::from_secs(1)).unwrap() else {
            panic!("expected resource return")
        };
        assert!(completed.cleaned);
        complete_recovery(&mut active, *completed, None, &tx, &|_| {
            panic!("stale candidate must not report Running")
        });
        let run = active.as_ref().unwrap();
        assert!(run.recovery_jobs.is_empty());
        assert!(run.recovery.contains_key(&0));
    }
    #[test]
    fn scheduled_recovery_leaves_refresh_and_stop_handlers_available() {
        let mut run = recovery_run();
        run.recovery
            .insert(0, PendingRecovery::new(Instant::now(), true));
        let (tx, rx) = mpsc::channel();
        let (events, event_rx) = mpsc::channel();
        let emit = |event| {
            events.send(event).unwrap();
        };
        // The fixture target cannot exist. The production worker may wait for
        // topology, but scheduling it must return so handlers can run now.
        recover_due_renderer(&mut run, None, (true, 0), &tx, &emit);
        assert!(run.recovery_jobs.contains_key(&0));
        refresh(&emit, true);
        let mut active = Some(run);
        request_stop(&active, &emit);
        assert!(event_rx.try_iter().any(|event| matches!(
            event,
            ControllerEvent::State {
                state: "Stopping",
                ..
            }
        )));
        // Stop cannot remove the session until the canceled worker has returned
        // its resources, including the stale-candidate cleanup pass.
        while !active.as_ref().unwrap().recovery_jobs.is_empty() {
            let Message::Recovered(completed) = rx.recv_timeout(Duration::from_secs(3)).unwrap()
            else {
                panic!("expected completion")
            };
            complete_recovery(&mut active, *completed, None, &tx, &emit);
        }
        assert!(active.as_ref().unwrap().cancel.load(Ordering::Acquire));
    }
}
