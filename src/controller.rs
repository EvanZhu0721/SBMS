use std::sync::mpsc::{self, Sender};
use std::thread::{self, JoinHandle};

use crate::display::active_displays;
use crate::geometry::Rotation;
use crate::mapping::{MappingRequest, MappingSession};

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

#[derive(Clone)]
pub struct ControllerSender(Sender<Command>);

pub struct Controller {
    sender: ControllerSender,
    worker: Option<JoinHandle<()>>,
}

impl ControllerSender {
    pub fn refresh(&self) {
        let _ = self.0.send(Command::Refresh);
    }

    pub fn start(&self, target: String) {
        let _ = self.0.send(Command::Start(target));
    }

    pub fn stop(&self) {
        let _ = self.0.send(Command::Stop);
    }
}

impl Controller {
    pub fn spawn(emit: impl Fn(ControllerEvent) + Send + 'static) -> Self {
        let (tx, rx) = mpsc::channel();
        let sender = ControllerSender(tx);
        let worker = thread::spawn(move || {
            let mut session = None;
            while let Ok(command) = rx.recv() {
                match command {
                    Command::Refresh => refresh(&emit),
                    Command::Start(target) => {
                        if session.is_some() {
                            continue;
                        }
                        emit(state(
                            "Starting",
                            "Creating virtual display…".into(),
                            false,
                            true,
                            "",
                        ));
                        match MappingRequest::configured(target).and_then(MappingSession::start) {
                            Ok(started) => {
                                session = Some(started);
                                emit(state(
                                    "Running",
                                    "Mapping is active".into(),
                                    true,
                                    false,
                                    "",
                                ));
                            }
                            Err(error) => emit(state(
                                "Stopped",
                                "Couldn’t start mapping".into(),
                                false,
                                false,
                                error.to_string(),
                            )),
                        }
                    }
                    Command::Stop => stop(&mut session, &emit),
                    Command::Shutdown => {
                        stop(&mut session, &emit);
                        break;
                    }
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
        let _ = self.sender.0.send(Command::Shutdown);
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
