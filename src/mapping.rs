use std::error::Error;
use std::fmt::{Display as FmtDisplay, Formatter};
use std::thread;
use std::time::{Duration, Instant};

use crate::config::ConfigStore;
use crate::display::{Display, active_displays, apply_display_mode, restore_display_topology};
use crate::frame_transport::{FrameLayout, FrameTransport, VirtualMode};
use crate::renderer::Renderer;
use crate::virtual_display::VirtualDisplay;
use crate::window_migration::WindowMigration;

const TOPOLOGY_TIMEOUT: Duration = Duration::from_secs(15);
const TOPOLOGY_SETTLE: Duration = Duration::from_millis(750);
const POLL_INTERVAL: Duration = Duration::from_millis(100);

pub struct MappingRequest {
    pub target: String,
    pub mode: VirtualMode,
}

pub struct MappingSession {
    renderer: Option<Renderer>,
    migration: Option<WindowMigration>,
    display: Option<VirtualDisplay>,
    transport: Option<FrameTransport>,
    source_id: String,
    target_id: String,
    physical_topology: Vec<Display>,
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

impl MappingRequest {
    pub fn configured(target: String) -> Result<Self, MappingError> {
        let outcome = ConfigStore::default_store()
            .and_then(|store| store.load())
            .map_err(|error| stage("config", error))?;
        if let Some(warning) = outcome.warning {
            return Err(MappingError {
                stage: "config",
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
        FrameLayout::new(mode).map_err(|error| stage("mode", error))?;
        Ok(Self { target, mode })
    }
}

impl MappingSession {
    pub fn start(request: MappingRequest) -> Result<Self, MappingError> {
        let displays = active_displays().map_err(|error| stage("target", error))?;
        unique_target(&displays, &request.target)?;
        let mut topology_guard = PhysicalTopologyGuard {
            expected: displays
                .iter()
                .filter(|display| !display.virtual_display)
                .cloned()
                .collect(),
            armed: true,
        };
        let transport =
            FrameTransport::create(request.mode).map_err(|error| stage("transport", error))?;
        let display = VirtualDisplay::create().map_err(|error| stage("device", error))?;
        let (source, target) = wait_for_source(&request.target, request.mode)?;
        let source_id = source.id.clone();
        let target_id = target.id.clone();
        let migration =
            WindowMigration::start(&target, &source).map_err(|error| stage("windows", error))?;
        let renderer = Renderer::start(target, source, transport.channel())
            .map_err(|error| stage("first-frame", error))?;
        topology_guard.armed = false;
        let physical_topology = std::mem::take(&mut topology_guard.expected);

        Ok(Self {
            renderer: Some(renderer),
            migration: Some(migration),
            display: Some(display),
            transport: Some(transport),
            source_id,
            target_id,
            physical_topology,
        })
    }

    pub fn stop(&mut self) -> Result<(), MappingError> {
        if self.renderer.is_none()
            && self.migration.is_none()
            && self.display.is_none()
            && self.transport.is_none()
        {
            return Ok(());
        }
        let renderer_error = self
            .renderer
            .take()
            .and_then(|mut renderer| renderer.stop().err());
        let mut migration = self.migration.take();
        let migration_error = migration
            .as_mut()
            .and_then(|migration| migration.restore().err());
        self.display.take();

        let deadline = Instant::now() + TOPOLOGY_TIMEOUT;
        let mut remove_error = None;
        loop {
            let displays = match active_displays() {
                Ok(displays) => displays,
                Err(error) => {
                    remove_error = Some(stage("remove", error));
                    break;
                }
            };
            if !displays
                .iter()
                .any(|display| display.id.eq_ignore_ascii_case(&self.source_id))
            {
                break;
            }
            if Instant::now() >= deadline {
                remove_error = Some(MappingError {
                    stage: "remove",
                    message: "virtual source stayed active after its device handle closed".into(),
                });
                break;
            }
            thread::sleep(POLL_INTERVAL);
        }

        thread::sleep(TOPOLOGY_SETTLE);
        let topology_error = restore_display_topology(&self.physical_topology)
            .map_err(|error| stage("topology-restore", error))
            .err();
        if topology_error.is_none() {
            thread::sleep(TOPOLOGY_SETTLE);
        }
        let final_target = active_displays()
            .ok()
            .and_then(|displays| unique_target(&displays, &self.target_id).ok());
        let reconciliation_error = migration.as_mut().and_then(|migration| {
            migration
                .reconcile_after_topology_change(final_target.as_ref())
                .err()
        });
        if let Some(error) = reconciliation_error.or(migration_error) {
            self.transport.take();
            return Err(stage("windows-restore", error));
        }
        if let Some(error) = renderer_error {
            self.transport.take();
            return Err(stage("renderer-stop", error));
        }
        if let Some(error) = topology_error {
            self.transport.take();
            return Err(error);
        }
        if let Some(error) = remove_error {
            self.transport.take();
            return Err(error);
        }
        self.transport.take();
        Ok(())
    }

    pub fn source_id(&self) -> &str {
        &self.source_id
    }
}

struct PhysicalTopologyGuard {
    expected: Vec<Display>,
    armed: bool,
}

impl Drop for PhysicalTopologyGuard {
    fn drop(&mut self) {
        if self.armed {
            thread::sleep(TOPOLOGY_SETTLE);
            let _ = restore_display_topology(&self.expected);
        }
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

fn wait_for_source(
    target_id: &str,
    requested_mode: VirtualMode,
) -> Result<(Display, Display), MappingError> {
    let deadline = Instant::now() + TOPOLOGY_TIMEOUT;
    let mut mode_applied = false;
    loop {
        let displays = active_displays().map_err(|error| stage("topology", error))?;
        let sources: Vec<_> = displays
            .iter()
            .filter(|display| display.virtual_display)
            .cloned()
            .collect();
        if sources.len() == 1 {
            let target = unique_target(&displays, target_id)?;
            let source = &sources[0];
            let width = source.rect.right - source.rect.left;
            let height = source.rect.bottom - source.rect.top;
            if width == requested_mode.width as i32 && height == requested_mode.height as i32 {
                return Ok((source.clone(), target));
            }
            if !mode_applied {
                apply_display_mode(source, requested_mode).map_err(|error| stage("mode", error))?;
                mode_applied = true;
            }
            if Instant::now() >= deadline {
                return Err(MappingError {
                    stage: "topology",
                    message: format!(
                        "virtual source became {}x{} but {}x{} was requested",
                        width, height, requested_mode.width, requested_mode.height
                    ),
                });
            }
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

fn target_refresh_millihz(target_id: &str) -> Result<u32, MappingError> {
    let displays = active_displays().map_err(|error| stage("target", error))?;
    let target = unique_target(&displays, target_id)?;
    if target.refresh_numerator == 0 || target.refresh_denominator == 0 {
        return Err(MappingError {
            stage: "mode",
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
            message: "target refresh rate is outside the supported range".into(),
        })?;
    Ok(millihz)
}

fn stage(stage: &'static str, error: impl FmtDisplay) -> MappingError {
    MappingError {
        stage,
        message: error.to_string(),
    }
}
