use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Sender};
use std::thread::{self, JoinHandle};

use crate::diagnostics::{self, Level, MappingSessionId};
use crate::display::active_displays;
use crate::geometry::{AspectRatio, Rotation};
use crate::mapping::{
    MappingEvent, MappingGroupInfo, MappingGroupRequest, MappingPlan, MappingReporter,
    MappingRoute, MappingSession, native_topology_rotation,
};
use crate::renderer::RendererEvent;
use crate::sunshine::{self, CaptureBackend, DeploymentEvent};

const DESKTOP_DUPLICATION_PROCESS_LIMIT: usize = 4;

#[derive(Clone, Debug)]
pub struct DisplayOption {
    pub id: String,
    pub name: String,
    pub label: String,
    pub width: i32,
    pub height: i32,
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
    },
}

enum Command {
    Refresh,
    Start(MappingPlan),
    Stop,
    // Kept as a controller capability even though the tray action now opens
    // Sunshine's Web panel instead of restarting the managed instance.
    #[allow(dead_code)]
    RestartSunshine(u32),
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
pub struct ControllerSender(Sender<Message>);

pub struct Controller {
    sender: ControllerSender,
    shutdown_requested: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}

#[derive(Clone)]
struct SunshineInstance {
    display_id: String,
    port: u16,
    capture: CaptureBackend,
}

impl ControllerSender {
    pub fn refresh(&self) {
        let _ = self.0.send(Message::Command(Command::Refresh));
    }

    pub fn start(&self, plan: MappingPlan) {
        let _ = self.0.send(Message::Command(Command::Start(plan)));
    }

    pub fn stop(&self) {
        let _ = self.0.send(Message::Command(Command::Stop));
    }

    #[allow(dead_code)]
    pub fn restart_sunshine(&self, group_id: u32) {
        let _ = self
            .0
            .send(Message::Command(Command::RestartSunshine(group_id)));
    }
}

impl Controller {
    pub fn spawn(emit: impl Fn(ControllerEvent) + Send + 'static) -> Self {
        let (tx, rx) = mpsc::channel();
        let sender = ControllerSender(tx);
        let worker_tx = sender.0.clone();
        let shutdown_requested = Arc::new(AtomicBool::new(false));
        let worker_shutdown = Arc::clone(&shutdown_requested);
        let worker = thread::spawn(move || {
            let sunshine_tx = worker_tx.clone();
            let sunshine = sunshine::Supervisor::spawn(move |event| {
                let _ = sunshine_tx.send(Message::Sunshine(event));
            });
            let sunshine_sender = sunshine.sender();
            sunshine_sender.stop_all();
            let mut session = None;
            let mut active_generation = None;
            let mut active_session_id = None;
            let mut next_generation = 0_u64;
            let mut sunshine_ports = HashMap::<u32, u16>::new();
            let mut sunshine_instances = HashMap::<u32, SunshineInstance>::new();
            let mut sunshine_capture = CaptureBackend::Auto;
            while let Ok(message) = rx.recv() {
                match message {
                    Message::Command(Command::Refresh) => refresh(&emit),
                    Message::Command(Command::Start(plan)) => {
                        if session.is_some() {
                            continue;
                        }
                        sunshine_ports = match plan
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
                        sunshine_capture = capture_backend_for_plan(&plan);
                        sunshine_instances.clear();
                        next_generation = next_generation.wrapping_add(1);
                        let generation = next_generation;
                        let session_id = diagnostics::new_mapping_session_id();
                        diagnostics::log(
                            Level::Info,
                            "controller",
                            "start",
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
                        emit(state(
                            "Starting",
                            "Creating virtual display…".into(),
                            false,
                            true,
                            "",
                        ));
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
                            if let MappingEvent::Renderer {
                                id,
                                event: RendererEvent::Failed(error),
                            } = &event
                            {
                                diagnostics::log(
                                    Level::Error,
                                    "renderer",
                                    "renderer",
                                    Some(session_id),
                                    format!("group {id}: {error}"),
                                );
                            }
                            let _ = report_tx.send(Message::Mapping { generation, event });
                        });
                        match MappingSession::start_plan_with_reporter_cancellable(
                            plan,
                            reporter,
                            &worker_shutdown,
                        ) {
                            Ok(mut started) => {
                                if worker_shutdown.load(Ordering::Acquire) {
                                    match started.stop() {
                                        Ok(()) => diagnostics::log(
                                            Level::Info,
                                            "controller",
                                            "shutdown",
                                            Some(session_id),
                                            "mapping completed during shutdown and was stopped",
                                        ),
                                        Err(error) => diagnostics::log(
                                            Level::Error,
                                            "controller",
                                            error.stage(),
                                            Some(session_id),
                                            error.to_string(),
                                        ),
                                    }
                                    continue;
                                }
                                session = Some(started);
                                active_generation = Some(generation);
                                active_session_id = Some(session_id);
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
                                active_generation = None;
                                active_session_id = None;
                                sunshine_ports.clear();
                                sunshine_instances.clear();
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
                                diagnostics::log(
                                    Level::Error,
                                    "controller",
                                    error.stage(),
                                    Some(session_id),
                                    error.to_string(),
                                );
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
                        active_generation = None;
                        sunshine_sender.stop_all();
                        sunshine_ports.clear();
                        sunshine_instances.clear();
                        stop(&mut session, &mut active_session_id, &emit);
                    }
                    Message::Command(Command::RestartSunshine(group_id)) => {
                        if let Some(generation) = active_generation
                            && let Some(instance) = sunshine_instances.get(&group_id).cloned()
                        {
                            sunshine_sender.restart(
                                generation,
                                group_id,
                                instance.display_id,
                                instance.port,
                                instance.capture,
                            );
                        }
                    }
                    Message::Command(Command::Shutdown) => {
                        sunshine_sender.stop_all();
                        sunshine_ports.clear();
                        sunshine_instances.clear();
                        stop(&mut session, &mut active_session_id, &emit);
                        break;
                    }
                    Message::Mapping { generation, event }
                        if active_generation == Some(generation) && session.is_some() =>
                    {
                        match event {
                            MappingEvent::GroupReady(group) => {
                                if let (Some(display_id), Some(port)) = (
                                    group.sunshine_id.clone(),
                                    sunshine_ports.get(&group.id).copied(),
                                ) {
                                    sunshine_instances.insert(
                                        group.id,
                                        SunshineInstance {
                                            display_id: display_id.clone(),
                                            port,
                                            capture: sunshine_capture,
                                        },
                                    );
                                    sunshine_sender.start(
                                        generation,
                                        group.id,
                                        display_id,
                                        port,
                                        sunshine_capture,
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
                                event: RendererEvent::Failed(error),
                            } => {
                                active_generation = None;
                                sunshine_sender.stop_all();
                                sunshine_ports.clear();
                                sunshine_instances.clear();
                                let session_id = active_session_id.take();
                                let cleanup_error = session
                                    .take()
                                    .and_then(|mut active| active.stop().err())
                                    .map(|cleanup| {
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
                        if active_generation == Some(event.generation) && session.is_some() =>
                    {
                        if let Some(port) = event.port
                            && event.error.is_none()
                            && let Some(instance) = sunshine_instances.get_mut(&event.group_id)
                        {
                            instance.display_id = event.display_id.clone();
                            instance.port = port;
                        }
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
        let _ = self.sender.0.send(Message::Command(Command::Shutdown));
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
                    width: display.rect.right - display.rect.left,
                    height: display.rect.bottom - display.rect.top,
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

fn stop(
    session: &mut Option<MappingSession>,
    session_id: &mut Option<MappingSessionId>,
    emit: &impl Fn(ControllerEvent),
) {
    let Some(mut active) = session.take() else {
        *session_id = None;
        return;
    };
    let stopped_session_id = session_id.take();
    emit(state(
        "Stopping",
        "Restoring windows and display topology…".into(),
        true,
        true,
        "",
    ));
    match active.stop() {
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
}
