use std::error::Error;
use std::fmt::{Display as FmtDisplay, Formatter};
use std::thread;
use std::time::{Duration, Instant};

use crate::display::{Display, active_displays};
use crate::frame_transport::FrameTransport;
use crate::renderer::Renderer;
use crate::virtual_display::VirtualDisplay;

const TOPOLOGY_TIMEOUT: Duration = Duration::from_secs(15);
const POLL_INTERVAL: Duration = Duration::from_millis(100);

pub struct MappingRequest {
    pub target: String,
}

pub struct MappingSession {
    renderer: Option<Renderer>,
    display: Option<VirtualDisplay>,
    transport: Option<FrameTransport>,
    source_id: String,
}

#[derive(Debug)]
pub struct MappingError {
    stage: &'static str,
    message: String,
}

impl FmtDisplay for MappingError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}: {}", self.stage, self.message)
    }
}

impl Error for MappingError {}

impl MappingSession {
    pub fn start(request: MappingRequest) -> Result<Self, MappingError> {
        let displays = active_displays().map_err(|error| stage("target", error))?;
        unique_target(&displays, &request.target)?;
        let transport = FrameTransport::create().map_err(|error| stage("transport", error))?;
        let display = VirtualDisplay::create().map_err(|error| stage("device", error))?;
        let (source, target) = wait_for_source(&request.target)?;
        let source_id = source.id.clone();
        let renderer = Renderer::start(target, transport.channel())
            .map_err(|error| stage("first-frame", error))?;

        Ok(Self {
            renderer: Some(renderer),
            display: Some(display),
            transport: Some(transport),
            source_id,
        })
    }

    pub fn stop(&mut self) -> Result<(), MappingError> {
        if self.renderer.is_none() && self.display.is_none() && self.transport.is_none() {
            return Ok(());
        }
        let renderer_error = self
            .renderer
            .take()
            .and_then(|mut renderer| renderer.stop().err());
        self.display.take();

        let deadline = Instant::now() + TOPOLOGY_TIMEOUT;
        loop {
            let displays = active_displays().map_err(|error| stage("remove", error))?;
            if !displays
                .iter()
                .any(|display| display.id.eq_ignore_ascii_case(&self.source_id))
            {
                break;
            }
            if Instant::now() >= deadline {
                return Err(MappingError {
                    stage: "remove",
                    message: "virtual source stayed active after its device handle closed".into(),
                });
            }
            thread::sleep(POLL_INTERVAL);
        }

        if let Some(error) = renderer_error {
            self.transport.take();
            return Err(stage("renderer-stop", error));
        }
        self.transport.take();
        Ok(())
    }

    pub fn source_id(&self) -> &str {
        &self.source_id
    }
}

impl Drop for MappingSession {
    fn drop(&mut self) {
        let _ = self.stop();
    }
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
                message: format!("active display id not found: {target_id}"),
            });
        }
        _ => {
            return Err(MappingError {
                stage: "target",
                message: format!("display id is ambiguous: {target_id}"),
            });
        }
    };
    if target.virtual_display {
        return Err(MappingError {
            stage: "target",
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
            message: "cloned outputs sharing one GDI display name are not supported".into(),
        });
    }
    Ok(target)
}

fn wait_for_source(target_id: &str) -> Result<(Display, Display), MappingError> {
    let deadline = Instant::now() + TOPOLOGY_TIMEOUT;
    loop {
        let displays = active_displays().map_err(|error| stage("topology", error))?;
        let sources: Vec<_> = displays
            .iter()
            .filter(|display| display.virtual_display)
            .cloned()
            .collect();
        if sources.len() == 1 {
            let target = unique_target(&displays, target_id)?;
            return Ok((sources[0].clone(), target));
        }
        if sources.len() > 1 {
            return Err(MappingError {
                stage: "topology",
                message: "more than one active SBMS virtual display exists".into(),
            });
        }
        if Instant::now() >= deadline {
            return Err(MappingError {
                stage: "topology",
                message: "SBMS virtual display did not become active within 15 seconds".into(),
            });
        }
        thread::sleep(POLL_INTERVAL);
    }
}

fn stage(stage: &'static str, error: impl FmtDisplay) -> MappingError {
    MappingError {
        stage,
        message: error.to_string(),
    }
}
