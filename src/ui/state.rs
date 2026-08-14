use crate::config::ProfileRevision;
use crate::config::{GroupConfig, GroupRouteConfig, ReferenceSource, StreamScreenConfig};
use crate::geometry::{
    AspectRatio, DisplayGeometry, PhysicalMeasurement, PixelSize, Rotation, SizingRequest,
};
use crate::limits::{
    MAX_REFRESH_MILLIHZ, MILLIMETERS_PER_INCH, MIN_REFRESH_MILLIHZ, valid_refresh_millihz,
};
use crate::mapping::{MappingGroupRequest, MappingRoute};
use crate::session_gate::VirtualMode;

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(super) enum SunshineState {
    #[default]
    Unavailable,
    Starting,
    Ready,
    Failed,
}

impl SunshineState {
    pub(super) fn as_i32(self) -> i32 {
        match self {
            Self::Unavailable => 0,
            Self::Starting => 1,
            Self::Ready => 2,
            Self::Failed => 3,
        }
    }
}

#[derive(Clone, Debug, Default)]
pub(super) struct GroupTelemetry {
    pub sunshine_id: Option<String>,
    pub sunshine_port: Option<u16>,
    pub sunshine_state: SunshineState,
    pub fps: Option<u32>,
    pub ready: bool,
    pub error: bool,
}

#[derive(Clone, Debug)]
pub(super) struct GroupDraft {
    pub id: u32,
    pub target_id: Option<String>,
    pub stream_only: bool,
    pub reference_source: Option<ReferenceSource>,
    pub sizing: Option<SizingRequest>,
    pub stream_width: String,
    pub stream_height: String,
    pub stream_diagonal: String,
    pub stream_refresh: String,
    pub stream_aspect_ratio_index: i32,
    pub stream_rotation_index: i32,
    pub telemetry: GroupTelemetry,
    pub tab_detail: String,
}

pub(super) struct LoadedGroups {
    pub groups: Vec<GroupDraft>,
    pub active: usize,
    pub profile: ProfileRevision,
}

#[derive(Clone, Copy, Debug)]
pub(super) struct ValidatedStreamFields {
    pub screen: StreamScreenConfig,
}

impl ValidatedStreamFields {
    pub(super) fn parse(
        width: &str,
        height: &str,
        diagonal: &str,
        refresh: &str,
        aspect_ratio_index: i32,
        rotation_index: i32,
    ) -> Result<Self, String> {
        let width = parse_u32(width, "Streaming screen width")?;
        let height = parse_u32(height, "Streaming screen height")?;
        let refresh_millihz = parse_stream_refresh_millihz(refresh)?;
        VirtualMode::from_millihz(width, height, refresh_millihz)
            .map_err(|error| error.to_string())?;

        let diagonal = diagonal.trim();
        let diagonal_inches = if diagonal.is_empty() {
            None
        } else {
            Some(parse_screen_diagonal(diagonal)?)
        };
        Ok(Self {
            screen: StreamScreenConfig {
                width,
                height,
                diagonal_inches,
                refresh_millihz,
                aspect_ratio: aspect_ratio_from_index(aspect_ratio_index)?,
                rotation: rotation_from_index(rotation_index)?,
            },
        })
    }

    pub(super) fn target_geometry(self) -> Result<DisplayGeometry, String> {
        let diagonal_inches = self
            .screen
            .diagonal_inches
            .ok_or_else(|| "Enter a valid diagonal in inches".to_string())?;
        Ok(DisplayGeometry {
            native_pixels: PixelSize {
                width: self.screen.width,
                height: self.screen.height,
            },
            physical: PhysicalMeasurement::DiagonalMm(diagonal_inches * MILLIMETERS_PER_INCH),
            aspect_ratio: Some(self.screen.aspect_ratio),
            rotation: self.screen.rotation,
        })
    }
}

#[derive(Clone)]
pub(super) struct ValidatedGroupDraft {
    id: u32,
    reference_source: Option<ReferenceSource>,
    sizing: Option<SizingRequest>,
    route: ValidatedRoute,
}

#[derive(Clone)]
enum ValidatedRoute {
    Mirror {
        target_id: Option<String>,
        mode: VirtualMode,
        rotation: Rotation,
    },
    StreamOnly {
        screen: StreamScreenConfig,
        mode: Option<VirtualMode>,
    },
}

impl ValidatedGroupDraft {
    pub(super) fn id(&self) -> u32 {
        self.id
    }

    pub(super) fn to_config(&self) -> GroupConfig {
        let route = match &self.route {
            ValidatedRoute::Mirror { target_id, .. } => GroupRouteConfig::Mirror {
                target_id: target_id.clone(),
            },
            ValidatedRoute::StreamOnly { screen, .. } => {
                GroupRouteConfig::StreamOnly { screen: *screen }
            }
        };
        GroupConfig {
            id: self.id,
            route,
            reference_source: self.reference_source.clone(),
            sizing: self.sizing,
        }
    }

    pub(super) fn to_mapping_request(&self) -> Result<MappingGroupRequest, String> {
        let (mode, rotation, route) = match &self.route {
            ValidatedRoute::Mirror {
                target_id,
                mode,
                rotation,
            } => {
                let target = target_id
                    .clone()
                    .ok_or_else(|| format!("Choose a target screen for Output {}", self.id + 1))?;
                (*mode, *rotation, MappingRoute::Mirror { target })
            }
            ValidatedRoute::StreamOnly { mode, screen } => {
                let mode = (*mode).ok_or_else(|| {
                    format!(
                        "Configure streaming screen geometry and a reference display for Output {}",
                        self.id + 1
                    )
                })?;
                (mode, screen.rotation, MappingRoute::StreamOnly)
            }
        };
        Ok(MappingGroupRequest {
            id: self.id,
            mode,
            rotation,
            route,
        })
    }
}

impl GroupDraft {
    pub(super) fn new(id: u32) -> Self {
        Self {
            id,
            target_id: None,
            stream_only: false,
            reference_source: None,
            sizing: None,
            stream_width: "3840".into(),
            stream_height: "2160".into(),
            stream_diagonal: String::new(),
            stream_refresh: "60".into(),
            stream_aspect_ratio_index: 0,
            stream_rotation_index: 0,
            telemetry: GroupTelemetry::default(),
            tab_detail: "—".into(),
        }
    }

    pub(super) fn from_config(config: &GroupConfig) -> Self {
        let mut draft = Self::new(config.id);
        match &config.route {
            GroupRouteConfig::Mirror { target_id } => draft.target_id = target_id.clone(),
            GroupRouteConfig::StreamOnly { screen } => {
                draft.stream_only = true;
                draft.stream_width = screen.width.to_string();
                draft.stream_height = screen.height.to_string();
                draft.stream_diagonal = screen
                    .diagonal_inches
                    .map(|value| value.to_string())
                    .unwrap_or_default();
                draft.stream_refresh = format_refresh_input(screen.refresh_millihz);
                draft.stream_aspect_ratio_index = aspect_ratio_option_index(screen.aspect_ratio);
                draft.stream_rotation_index = rotation_index(screen.rotation);
            }
        }
        draft.reference_source = config.reference_source.clone();
        draft.sizing = config.sizing;
        draft
    }

    pub(super) fn validate(&self) -> Result<ValidatedGroupDraft, String> {
        let calculated = self
            .sizing
            .map(|request| {
                request
                    .calculate()
                    .map_err(|error| format!("Output {} geometry: {error}", self.id + 1))
            })
            .transpose()?;
        let route = if self.stream_only {
            let stream = ValidatedStreamFields::parse(
                &self.stream_width,
                &self.stream_height,
                &self.stream_diagonal,
                &self.stream_refresh,
                self.stream_aspect_ratio_index,
                self.stream_rotation_index,
            )?;
            let mode = calculated
                .map(|result| {
                    VirtualMode::from_millihz(
                        result.virtual_mode.width,
                        result.virtual_mode.height,
                        stream.screen.refresh_millihz,
                    )
                    .map_err(|error| format!("Output {}: {error}", self.id + 1))
                })
                .transpose()?;
            ValidatedRoute::StreamOnly {
                screen: stream.screen,
                mode,
            }
        } else {
            let (mode, rotation) = match calculated {
                Some(result) => (
                    VirtualMode::from_millihz(
                        result.virtual_mode.width,
                        result.virtual_mode.height,
                        result.preferred_refresh_millihz.unwrap_or(240_000),
                    )
                    .map_err(|error| format!("Output {}: {error}", self.id + 1))?,
                    self.sizing
                        .map(|request| request.target.rotation)
                        .unwrap_or(Rotation::Deg0),
                ),
                None => (VirtualMode::default(), Rotation::Deg0),
            };
            ValidatedRoute::Mirror {
                target_id: self.target_id.clone(),
                mode,
                rotation,
            }
        };
        Ok(ValidatedGroupDraft {
            id: self.id,
            reference_source: self.reference_source.clone(),
            sizing: self.sizing,
            route,
        })
    }

    pub(super) fn reset_telemetry(&mut self) {
        self.telemetry = GroupTelemetry::default();
    }
}

pub(super) fn parse_screen_diagonal(value: &str) -> Result<f64, String> {
    let diagonal = value
        .trim()
        .parse::<f64>()
        .map_err(|_| "Enter a valid diagonal in inches".to_string())?;
    if !crate::limits::valid_physical_millimeters(diagonal * MILLIMETERS_PER_INCH) {
        return Err("Diagonal must be between 0.4 and 393.7 inches".into());
    }
    Ok(diagonal)
}

pub(super) fn parse_stream_refresh_millihz(value: &str) -> Result<u32, String> {
    let refresh = value
        .trim()
        .parse::<f64>()
        .map_err(|_| "Refresh rate must be a positive number".to_string())?;
    if !refresh.is_finite() || refresh <= 0.0 {
        return Err("Refresh rate must be a positive number".into());
    }
    let millihz = (refresh * 1_000.0).round();
    if millihz > u32::MAX as f64 {
        return Err("Refresh rate is too large".into());
    }
    let millihz = millihz as u32;
    if !valid_refresh_millihz(millihz) {
        return Err(format!(
            "Refresh rate must be between {} and {} Hz",
            MIN_REFRESH_MILLIHZ / 1_000,
            MAX_REFRESH_MILLIHZ / 1_000
        ));
    }
    Ok(millihz)
}

pub(super) fn format_refresh_input(refresh_millihz: u32) -> String {
    if refresh_millihz.is_multiple_of(1_000) {
        (refresh_millihz / 1_000).to_string()
    } else {
        let value = format!("{:.3}", f64::from(refresh_millihz) / 1_000.0);
        value
            .trim_end_matches('0')
            .trim_end_matches('.')
            .to_string()
    }
}

pub(super) fn parse_u32(value: &str, name: &str) -> Result<u32, String> {
    value
        .trim()
        .parse()
        .map_err(|_| format!("{name} must be a positive whole number"))
}

pub(super) fn aspect_ratio_from_index(index: i32) -> Result<AspectRatio, String> {
    let (width, height) = match index {
        0 => (16, 9),
        1 => (16, 10),
        2 => (3, 2),
        3 => (4, 3),
        4 => (21, 9),
        5 => (32, 9),
        _ => return Err("Choose a supported aspect ratio".into()),
    };
    Ok(AspectRatio { width, height })
}

pub(super) fn rotation_from_index(index: i32) -> Result<Rotation, String> {
    match index {
        0 => Ok(Rotation::Deg0),
        1 => Ok(Rotation::Deg180),
        2 => Ok(Rotation::Deg90),
        3 => Ok(Rotation::Deg270),
        _ => Err("Choose a supported display orientation".into()),
    }
}

pub(super) fn rotation_index(rotation: Rotation) -> i32 {
    match rotation {
        Rotation::Deg0 => 0,
        Rotation::Deg180 => 1,
        Rotation::Deg90 => 2,
        Rotation::Deg270 => 3,
    }
}

pub(super) fn rotation_label(index: i32) -> Option<&'static str> {
    match index {
        0 => Some("Landscape"),
        1 => Some("Landscape flipped"),
        2 => Some("Portrait clockwise"),
        3 => Some("Portrait counter-clockwise"),
        _ => None,
    }
}

const ASPECT_RATIOS: [AspectRatio; 6] = [
    AspectRatio {
        width: 16,
        height: 9,
    },
    AspectRatio {
        width: 16,
        height: 10,
    },
    AspectRatio {
        width: 3,
        height: 2,
    },
    AspectRatio {
        width: 4,
        height: 3,
    },
    AspectRatio {
        width: 21,
        height: 9,
    },
    AspectRatio {
        width: 32,
        height: 9,
    },
];

pub(super) fn aspect_ratio_option_index(ratio: AspectRatio) -> i32 {
    ASPECT_RATIOS
        .iter()
        .enumerate()
        .min_by(|(_, left), (_, right)| {
            aspect_ratio_error(ratio, **left).total_cmp(&aspect_ratio_error(ratio, **right))
        })
        .map(|(index, _)| index as i32)
        .unwrap_or(0)
}

fn aspect_ratio_error(left: AspectRatio, right: AspectRatio) -> f64 {
    (left.width as f64 / left.height as f64 - right.width as f64 / right.height as f64).abs()
}
