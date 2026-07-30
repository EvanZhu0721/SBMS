use std::sync::Arc;
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
    Shutdown,
}

enum Message {
    Command(Command),
    Mapping {
        generation: u64,
        event: MappingEvent,
    },
}

#[derive(Clone)]
pub struct ControllerSender(Sender<Message>);

pub struct Controller {
    sender: ControllerSender,
    worker: Option<JoinHandle<()>>,
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
}

impl Controller {
    pub fn spawn(emit: impl Fn(ControllerEvent) + Send + 'static) -> Self {
        let (tx, rx) = mpsc::channel();
        let sender = ControllerSender(tx);
        let worker_tx = sender.0.clone();
        let worker = thread::spawn(move || {
            let mut session = None;
            let mut active_generation = None;
            let mut active_session_id = None;
            let mut next_generation = 0_u64;
            while let Ok(message) = rx.recv() {
                match message {
                    Message::Command(Command::Refresh) => refresh(&emit),
                    Message::Command(Command::Start(plan)) => {
                        if session.is_some() {
                            continue;
                        }
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
                        match MappingSession::start_plan_with_reporter(plan, reporter) {
                            Ok(started) => {
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
                        stop(&mut session, &mut active_session_id, &emit);
                    }
                    Message::Command(Command::Shutdown) => {
                        stop(&mut session, &mut active_session_id, &emit);
                        break;
                    }
                    Message::Mapping { generation, event }
                        if active_generation == Some(generation) && session.is_some() =>
                    {
                        match event {
                            MappingEvent::GroupReady(group) => {
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
                    Message::Mapping { .. } => {}
                }
            }
        });
        Self {
            sender,
            worker: Some(worker),
        }
    }

    pub fn sender(&self) -> ControllerSender {
        self.sender.clone()
    }

    pub fn shutdown(mut self) {
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
