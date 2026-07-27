use std::sync::Arc;
use std::sync::mpsc::{self, Sender};
use std::thread::{self, JoinHandle};

use crate::display::active_displays;
use crate::geometry::Rotation;
use crate::mapping::{MappingRequest, MappingSession};
use crate::renderer::{RendererEvent, RendererReporter};

#[derive(Clone, Debug)]
pub struct DisplayOption {
    pub id: String,
    pub name: String,
    pub label: String,
    pub width: i32,
    pub height: i32,
    pub native_width: u32,
    pub native_height: u32,
    pub physical_width_mm: Option<f64>,
    pub physical_height_mm: Option<f64>,
    pub rotation: Rotation,
    pub refresh_numerator: u32,
    pub refresh_denominator: u32,
    pub primary: bool,
}

#[derive(Clone, Debug)]
pub enum ControllerEvent {
    Displays(Vec<DisplayOption>),
    Fps(u32),
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
    Start(String),
    Stop,
    Shutdown,
}

enum Message {
    Command(Command),
    Renderer {
        generation: u64,
        event: RendererEvent,
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

    pub fn start(&self, target: String) {
        let _ = self.0.send(Message::Command(Command::Start(target)));
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
            let mut next_generation = 0_u64;
            while let Ok(message) = rx.recv() {
                match message {
                    Message::Command(Command::Refresh) => refresh(&emit),
                    Message::Command(Command::Start(target)) => {
                        if session.is_some() {
                            continue;
                        }
                        next_generation = next_generation.wrapping_add(1);
                        let generation = next_generation;
                        emit(state(
                            "Starting",
                            "Creating virtual display…".into(),
                            false,
                            true,
                            "",
                        ));
                        let report_tx = worker_tx.clone();
                        let reporter: RendererReporter = Arc::new(move |event| {
                            let _ = report_tx.send(Message::Renderer { generation, event });
                        });
                        match MappingRequest::configured(target).and_then(|request| {
                            MappingSession::start_with_reporter(request, reporter)
                        }) {
                            Ok(started) => {
                                session = Some(started);
                                active_generation = Some(generation);
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
                        stop(&mut session, &emit);
                    }
                    Message::Command(Command::Shutdown) => {
                        stop(&mut session, &emit);
                        break;
                    }
                    Message::Renderer { generation, event }
                        if active_generation == Some(generation) && session.is_some() =>
                    {
                        match event {
                            RendererEvent::Fps(fps) => {
                                emit(ControllerEvent::Fps(fps.min(999)));
                            }
                            RendererEvent::Failed(error) => {
                                active_generation = None;
                                let cleanup_error = session
                                    .take()
                                    .and_then(|mut active| active.stop().err())
                                    .map(|cleanup| cleanup.to_string());
                                let error = match cleanup_error {
                                    Some(cleanup) => {
                                        format!("{error}; cleanup also failed: {cleanup}")
                                    }
                                    None => error,
                                };
                                emit(state(
                                    "Stopped",
                                    "Mapping stopped unexpectedly".into(),
                                    false,
                                    false,
                                    error,
                                ));
                            }
                        }
                    }
                    Message::Renderer { .. } => {}
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
                    physical_width_mm: display.physical_width_mm,
                    physical_height_mm: display.physical_height_mm,
                    rotation: display.rotation,
                    refresh_numerator: display.refresh_numerator,
                    refresh_denominator: display.refresh_denominator,
                    primary: display.primary,
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
        Err(error) => emit(state(
            "Stopped",
            "Display discovery failed".into(),
            false,
            false,
            error.to_string(),
        )),
    }
}

fn stop(session: &mut Option<MappingSession>, emit: &impl Fn(ControllerEvent)) {
    let Some(mut active) = session.take() else {
        return;
    };
    emit(state(
        "Stopping",
        "Restoring windows and display topology…".into(),
        true,
        true,
        "",
    ));
    match active.stop() {
        Ok(()) => emit(state(
            "Stopped",
            "Choose a display to start".into(),
            false,
            false,
            "",
        )),
        Err(error) => emit(state(
            "Stopped",
            "Cleanup completed with errors".into(),
            false,
            false,
            error.to_string(),
        )),
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
