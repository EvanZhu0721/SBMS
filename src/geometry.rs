use std::error::Error;
use std::fmt::{Display, Formatter};

use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct PixelSize {
    pub width: u32,
    pub height: u32,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum PhysicalMeasurement {
    DimensionsMm { width: f64, height: f64 },
    DiagonalMm(f64),
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Rotation {
    #[default]
    Deg0,
    Deg90,
    Deg180,
    Deg270,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SizingStrategy {
    #[default]
    MatchPhysicalSize,
    IntegerScale {
        max_scale: u8,
    },
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct DisplayGeometry {
    pub native_pixels: PixelSize,
    pub physical: PhysicalMeasurement,
    #[serde(default)]
    pub rotation: Rotation,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct SizingRequest {
    pub reference: DisplayGeometry,
    pub target: DisplayGeometry,
    #[serde(default)]
    pub strategy: SizingStrategy,
    #[serde(default = "default_alignment")]
    pub alignment: u32,
    #[serde(default)]
    pub preferred_refresh_millihz: Option<u32>,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SizingResult {
    pub virtual_mode: PixelSize,
    pub oriented_target: PixelSize,
    pub scale_x: f64,
    pub scale_y: f64,
    pub preferred_refresh_millihz: Option<u32>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PixelPoint {
    pub x: i32,
    pub y: i32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PixelRect {
    pub left: i32,
    pub top: i32,
    pub width: u32,
    pub height: u32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CoordinateTransform {
    pub target: PixelRect,
    pub source: PixelRect,
    pub rotation: Rotation,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct GeometryError(String);

impl Display for GeometryError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for GeometryError {}

impl PixelSize {
    pub fn oriented(self, rotation: Rotation) -> Self {
        match rotation {
            Rotation::Deg0 | Rotation::Deg180 => self,
            Rotation::Deg90 | Rotation::Deg270 => Self {
                width: self.height,
                height: self.width,
            },
        }
    }

    fn validate(self, name: &str) -> Result<(), GeometryError> {
        if self.width == 0 || self.height == 0 {
            return Err(GeometryError(format!(
                "{name} pixel dimensions must be non-zero"
            )));
        }
        Ok(())
    }
}

impl DisplayGeometry {
    fn oriented_pixels(self) -> Result<PixelSize, GeometryError> {
        self.native_pixels.validate("display")?;
        Ok(self.native_pixels.oriented(self.rotation))
    }

    fn oriented_physical_mm(self) -> Result<(f64, f64), GeometryError> {
        let pixels = self.oriented_pixels()?;
        let (width, height) = match self.physical {
            PhysicalMeasurement::DimensionsMm { width, height } => match self.rotation {
                Rotation::Deg0 | Rotation::Deg180 => (width, height),
                Rotation::Deg90 | Rotation::Deg270 => (height, width),
            },
            PhysicalMeasurement::DiagonalMm(diagonal) => {
                validate_measurement(diagonal, "diagonal")?;
                let hypotenuse = (pixels.width as f64).hypot(pixels.height as f64);
                (
                    diagonal * pixels.width as f64 / hypotenuse,
                    diagonal * pixels.height as f64 / hypotenuse,
                )
            }
        };
        validate_measurement(width, "physical width")?;
        validate_measurement(height, "physical height")?;
        Ok((width, height))
    }
}

impl SizingRequest {
    pub fn calculate(self) -> Result<SizingResult, GeometryError> {
        if self.alignment == 0 || !self.alignment.is_power_of_two() || self.alignment > 256 {
            return Err(GeometryError(
                "alignment must be a power of two between 1 and 256".into(),
            ));
        }
        if self.preferred_refresh_millihz == Some(0) {
            return Err(GeometryError(
                "preferred refresh must be non-zero when present".into(),
            ));
        }

        let reference_pixels = self.reference.oriented_pixels()?;
        let target_pixels = self.target.oriented_pixels()?;
        let (reference_width_mm, _) = self.reference.oriented_physical_mm()?;
        let (target_width_mm, _) = self.target.oriented_physical_mm()?;
        let pixels_per_mm = reference_pixels.width as f64 / reference_width_mm;
        let physical_width = target_width_mm * pixels_per_mm;
        let physical_mode = aspect_aligned_mode(physical_width, target_pixels, self.alignment)?;

        let virtual_mode = match self.strategy {
            SizingStrategy::MatchPhysicalSize => physical_mode,
            SizingStrategy::IntegerScale { max_scale } => {
                if !(1..=8).contains(&max_scale) {
                    return Err(GeometryError(
                        "max integer scale must be between 1 and 8".into(),
                    ));
                }
                (1..=max_scale)
                    .filter_map(|scale| {
                        let width = target_pixels.width.checked_mul(scale as u32)?;
                        let height = target_pixels.height.checked_mul(scale as u32)?;
                        (width % self.alignment == 0 && height % self.alignment == 0)
                            .then_some(PixelSize { width, height })
                    })
                    .min_by(|left, right| {
                        sizing_error(*left, physical_mode)
                            .total_cmp(&sizing_error(*right, physical_mode))
                    })
                    .ok_or_else(|| GeometryError("no integer scale candidate".into()))?
            }
        };
        virtual_mode.validate("calculated virtual mode")?;

        Ok(SizingResult {
            virtual_mode,
            oriented_target: target_pixels,
            scale_x: virtual_mode.width as f64 / target_pixels.width as f64,
            scale_y: virtual_mode.height as f64 / target_pixels.height as f64,
            preferred_refresh_millihz: self.preferred_refresh_millihz,
        })
    }
}

impl CoordinateTransform {
    pub fn stretch(
        target: PixelRect,
        source: PixelRect,
        rotation: Rotation,
    ) -> Result<Self, GeometryError> {
        target.validate("target")?;
        source.validate("source")?;
        Ok(Self {
            target,
            source,
            rotation,
        })
    }

    pub fn map_target_point(self, point: PixelPoint) -> Option<PixelPoint> {
        let local_x = point.x.checked_sub(self.target.left)?;
        let local_y = point.y.checked_sub(self.target.top)?;
        if local_x < 0
            || local_y < 0
            || local_x >= self.target.width as i32
            || local_y >= self.target.height as i32
        {
            return None;
        }

        let (source_x, source_y) = match self.rotation {
            Rotation::Deg0 => (
                scale_index(local_x, self.target.width, self.source.width),
                scale_index(local_y, self.target.height, self.source.height),
            ),
            Rotation::Deg90 => (
                scale_index(local_y, self.target.height, self.source.width),
                self.source.height as i32
                    - 1
                    - scale_index(local_x, self.target.width, self.source.height),
            ),
            Rotation::Deg180 => (
                self.source.width as i32
                    - 1
                    - scale_index(local_x, self.target.width, self.source.width),
                self.source.height as i32
                    - 1
                    - scale_index(local_y, self.target.height, self.source.height),
            ),
            Rotation::Deg270 => (
                self.source.width as i32
                    - 1
                    - scale_index(local_y, self.target.height, self.source.width),
                scale_index(local_x, self.target.width, self.source.height),
            ),
        };
        Some(PixelPoint {
            x: self.source.left.saturating_add(source_x),
            y: self.source.top.saturating_add(source_y),
        })
    }
}

impl PixelRect {
    fn validate(self, name: &str) -> Result<(), GeometryError> {
        if self.width == 0 || self.height == 0 {
            return Err(GeometryError(format!(
                "{name} rectangle dimensions must be non-zero"
            )));
        }
        if self.width > i32::MAX as u32 || self.height > i32::MAX as u32 {
            return Err(GeometryError(format!(
                "{name} rectangle dimensions exceed Win32 coordinate range"
            )));
        }
        Ok(())
    }
}

const fn default_alignment() -> u32 {
    2
}

fn validate_measurement(value: f64, name: &str) -> Result<(), GeometryError> {
    if !value.is_finite() || !(10.0..=10_000.0).contains(&value) {
        return Err(GeometryError(format!(
            "{name} must be finite and between 10 and 10000 mm"
        )));
    }
    Ok(())
}

fn aspect_aligned_mode(
    ideal_width: f64,
    aspect: PixelSize,
    alignment: u32,
) -> Result<PixelSize, GeometryError> {
    if !ideal_width.is_finite() || ideal_width < 1.0 || ideal_width > u32::MAX as f64 {
        return Err(GeometryError(
            "calculated dimension is outside the supported range".into(),
        ));
    }
    let divisor = greatest_common_divisor(aspect.width, aspect.height);
    let unit_width = (aspect.width / divisor) as u64;
    let unit_height = (aspect.height / divisor) as u64;
    let mut multiplier = (ideal_width / unit_width as f64).ceil() as u64;
    let alignment = alignment as u64;
    loop {
        let width = unit_width
            .checked_mul(multiplier)
            .ok_or_else(|| GeometryError("calculated dimension overflowed".into()))?;
        let height = unit_height
            .checked_mul(multiplier)
            .ok_or_else(|| GeometryError("calculated dimension overflowed".into()))?;
        if width % alignment == 0 && height % alignment == 0 {
            return Ok(PixelSize {
                width: u32::try_from(width)
                    .map_err(|_| GeometryError("calculated dimension overflowed".into()))?,
                height: u32::try_from(height)
                    .map_err(|_| GeometryError("calculated dimension overflowed".into()))?,
            });
        }
        multiplier = multiplier
            .checked_add(1)
            .ok_or_else(|| GeometryError("calculated dimension overflowed".into()))?;
    }
}

fn greatest_common_divisor(mut left: u32, mut right: u32) -> u32 {
    while right != 0 {
        let remainder = left % right;
        left = right;
        right = remainder;
    }
    left.max(1)
}

fn sizing_error(candidate: PixelSize, ideal: PixelSize) -> f64 {
    let width = candidate.width as f64 / ideal.width as f64 - 1.0;
    let height = candidate.height as f64 / ideal.height as f64 - 1.0;
    width * width + height * height
}

fn scale_index(value: i32, from_extent: u32, to_extent: u32) -> i32 {
    let from_max = from_extent.saturating_sub(1).max(1) as i64;
    let to_max = to_extent.saturating_sub(1) as i64;
    ((value as i64 * to_max) / from_max).clamp(0, to_max) as i32
}

#[cfg(test)]
mod tests {
    use super::*;

    fn display(width: u32, height: u32, diagonal_mm: f64) -> DisplayGeometry {
        DisplayGeometry {
            native_pixels: PixelSize { width, height },
            physical: PhysicalMeasurement::DiagonalMm(diagonal_mm),
            rotation: Rotation::Deg0,
        }
    }

    #[test]
    fn physical_strategy_matches_reference_density_and_target_aspect() {
        let result = SizingRequest {
            reference: display(3840, 2160, 708.0),
            target: display(2560, 1440, 604.0),
            strategy: SizingStrategy::MatchPhysicalSize,
            alignment: 2,
            preferred_refresh_millihz: Some(240_000),
        }
        .calculate()
        .unwrap();

        assert_eq!(result.virtual_mode.width % 2, 0);
        assert_eq!(result.virtual_mode.height % 2, 0);
        assert_eq!(
            result.virtual_mode.width as u64 * 9,
            result.virtual_mode.height as u64 * 16
        );
        assert_eq!(result.preferred_refresh_millihz, Some(240_000));
    }

    #[test]
    fn rotation_swaps_oriented_extent_without_guessing_from_aspect() {
        let size = PixelSize {
            width: 3840,
            height: 2160,
        };
        assert_eq!(
            size.oriented(Rotation::Deg90),
            PixelSize {
                width: 2160,
                height: 3840
            }
        );
        assert_eq!(size.oriented(Rotation::Deg180), size);
    }

    #[test]
    fn integer_strategy_selects_nearest_two_dimensional_scale() {
        let result = SizingRequest {
            reference: display(3840, 2160, 708.0),
            target: display(1920, 1080, 354.0),
            strategy: SizingStrategy::IntegerScale { max_scale: 4 },
            alignment: 2,
            preferred_refresh_millihz: None,
        }
        .calculate()
        .unwrap();
        assert_eq!(
            result.virtual_mode,
            PixelSize {
                width: 1920,
                height: 1080
            }
        );
    }

    #[test]
    fn coordinate_transform_maps_edges_and_rotation() {
        let target = PixelRect {
            left: 0,
            top: 0,
            width: 100,
            height: 50,
        };
        let source = PixelRect {
            left: 1000,
            top: -500,
            width: 200,
            height: 400,
        };
        let normal = CoordinateTransform::stretch(target, source, Rotation::Deg0).unwrap();
        assert_eq!(
            normal.map_target_point(PixelPoint { x: 99, y: 49 }),
            Some(PixelPoint { x: 1199, y: -101 })
        );
        assert_eq!(normal.map_target_point(PixelPoint { x: 100, y: 49 }), None);

        let clockwise = CoordinateTransform::stretch(target, source, Rotation::Deg90).unwrap();
        assert_eq!(
            clockwise.map_target_point(PixelPoint { x: 0, y: 0 }),
            Some(PixelPoint { x: 1000, y: -101 })
        );
        assert_eq!(
            clockwise.map_target_point(PixelPoint { x: 99, y: 49 }),
            Some(PixelPoint { x: 1199, y: -500 })
        );
    }
}
