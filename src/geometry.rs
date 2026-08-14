use std::error::Error;
use std::fmt::{Display, Formatter};

use serde::de::Error as _;
use serde::{Deserialize, Deserializer, Serialize};

use crate::limits::{
    MAX_PHYSICAL_MILLIMETERS, MIN_PHYSICAL_MILLIMETERS, valid_physical_millimeters,
};

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct PixelSize {
    pub width: u32,
    pub height: u32,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct AspectRatio {
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

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SizingStrategy {
    #[default]
    MatchPhysicalSize,
    RoundedScale,
    IntegerScale,
}

impl<'de> Deserialize<'de> for SizingStrategy {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        #[derive(Deserialize)]
        #[serde(rename_all = "snake_case")]
        enum Current {
            MatchPhysicalSize,
            RoundedScale,
            IntegerScale,
        }

        #[derive(Deserialize)]
        #[serde(rename_all = "snake_case")]
        enum Legacy {
            IntegerScale { max_scale: u8 },
        }

        #[derive(Deserialize)]
        #[serde(untagged)]
        enum Compatible {
            Current(Current),
            Legacy(Legacy),
        }

        match Compatible::deserialize(deserializer)? {
            Compatible::Current(Current::MatchPhysicalSize) => Ok(Self::MatchPhysicalSize),
            Compatible::Current(Current::RoundedScale) => Ok(Self::RoundedScale),
            Compatible::Current(Current::IntegerScale) => Ok(Self::IntegerScale),
            Compatible::Legacy(Legacy::IntegerScale { max_scale })
                if (1..=8).contains(&max_scale) =>
            {
                Ok(Self::IntegerScale)
            }
            Compatible::Legacy(Legacy::IntegerScale { .. }) => {
                Err(D::Error::custom("legacy max_scale must be between 1 and 8"))
            }
        }
    }
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct DisplayGeometry {
    pub native_pixels: PixelSize,
    pub physical: PhysicalMeasurement,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub aspect_ratio: Option<AspectRatio>,
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

impl AspectRatio {
    fn validate(self) -> Result<(), GeometryError> {
        if self.width == 0 || self.height == 0 {
            return Err(GeometryError(
                "aspect ratio dimensions must be non-zero".into(),
            ));
        }
        Ok(())
    }

    fn physical_dimensions(self, diagonal: f64) -> Result<(f64, f64), GeometryError> {
        self.validate()?;
        let hypotenuse = (self.width as f64).hypot(self.height as f64);
        Ok((
            diagonal * self.width as f64 / hypotenuse,
            diagonal * self.height as f64 / hypotenuse,
        ))
    }
}

impl DisplayGeometry {
    fn oriented_pixels(self) -> Result<PixelSize, GeometryError> {
        self.native_pixels.validate("display")?;
        Ok(self.native_pixels.oriented(self.rotation))
    }

    fn oriented_physical_mm(self) -> Result<(f64, f64), GeometryError> {
        self.oriented_pixels()?;
        if let Some(aspect_ratio) = self.aspect_ratio {
            aspect_ratio.validate()?;
        }
        let (width, height) = match self.physical {
            PhysicalMeasurement::DimensionsMm { width, height } => match self.rotation {
                Rotation::Deg0 | Rotation::Deg180 => (width, height),
                Rotation::Deg90 | Rotation::Deg270 => (height, width),
            },
            PhysicalMeasurement::DiagonalMm(diagonal) => {
                validate_measurement(diagonal, "diagonal")?;
                let aspect_ratio = self.aspect_ratio.unwrap_or(AspectRatio {
                    width: self.native_pixels.width,
                    height: self.native_pixels.height,
                });
                let dimensions = aspect_ratio.physical_dimensions(diagonal)?;
                match self.rotation {
                    Rotation::Deg0 | Rotation::Deg180 => dimensions,
                    Rotation::Deg90 | Rotation::Deg270 => (dimensions.1, dimensions.0),
                }
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
            SizingStrategy::RoundedScale => {
                snapped_scale_mode(physical_width, target_pixels, self.alignment, 4)?
            }
            SizingStrategy::IntegerScale => {
                snapped_scale_mode(physical_width, target_pixels, self.alignment, 1)?
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
    if !valid_physical_millimeters(value) {
        return Err(GeometryError(format!(
            "{name} must be finite and between {MIN_PHYSICAL_MILLIMETERS:.0} and {MAX_PHYSICAL_MILLIMETERS:.0} mm"
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

fn snapped_scale_mode(
    ideal_width: f64,
    target: PixelSize,
    alignment: u32,
    steps_per_unit: u32,
) -> Result<PixelSize, GeometryError> {
    let target_width = target.width as f64;
    let steps_per_unit = steps_per_unit as f64;
    let minimum_scale = 1.0 / steps_per_unit;
    let scale = ((ideal_width / target_width) * steps_per_unit).round() / steps_per_unit;
    aspect_aligned_mode(target_width * scale.max(minimum_scale), target, alignment)
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
            aspect_ratio: None,
            rotation: Rotation::Deg0,
        }
    }

    fn assert_approximately_equal(left: f64, right: f64) {
        assert!(
            (left - right).abs() < 1e-9,
            "expected {left} to approximately equal {right}"
        );
    }

    #[test]
    fn explicit_aspect_ratio_changes_physical_width_for_the_same_diagonal() {
        let mut widescreen = display(2560, 1440, 600.0);
        widescreen.aspect_ratio = Some(AspectRatio {
            width: 16,
            height: 9,
        });
        let mut square = widescreen;
        square.aspect_ratio = Some(AspectRatio {
            width: 1,
            height: 1,
        });

        let (widescreen_width, _) = widescreen.oriented_physical_mm().unwrap();
        let (square_width, _) = square.oriented_physical_mm().unwrap();

        assert!(widescreen_width > square_width);
        assert_approximately_equal(widescreen_width, 600.0 * 16.0 / 337.0_f64.sqrt());
        assert_approximately_equal(square_width, 600.0 / 2.0_f64.sqrt());
    }

    #[test]
    fn explicit_aspect_ratio_swaps_physical_axes_when_vertical() {
        let mut horizontal = display(3840, 2160, 600.0);
        horizontal.aspect_ratio = Some(AspectRatio {
            width: 16,
            height: 9,
        });
        let mut vertical = horizontal;
        vertical.rotation = Rotation::Deg90;

        let horizontal_dimensions = horizontal.oriented_physical_mm().unwrap();
        let vertical_dimensions = vertical.oriented_physical_mm().unwrap();

        assert_approximately_equal(vertical_dimensions.0, horizontal_dimensions.1);
        assert_approximately_equal(vertical_dimensions.1, horizontal_dimensions.0);
    }

    #[test]
    fn missing_aspect_ratio_preserves_native_pixel_ratio_behavior() {
        let legacy = display(2560, 1600, 400.0);
        let (width, height) = legacy.oriented_physical_mm().unwrap();
        let hypotenuse = 2560.0_f64.hypot(1600.0);

        assert_approximately_equal(width, 400.0 * 2560.0 / hypotenuse);
        assert_approximately_equal(height, 400.0 * 1600.0 / hypotenuse);
    }

    #[test]
    fn zero_aspect_ratio_dimension_is_rejected() {
        let mut invalid = display(2560, 1440, 600.0);
        invalid.aspect_ratio = Some(AspectRatio {
            width: 0,
            height: 9,
        });

        let error = invalid.oriented_physical_mm().unwrap_err();
        assert!(error.to_string().contains("aspect ratio"));
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
    fn rounded_strategy_snaps_to_quarter_scale() {
        let result = SizingRequest {
            reference: display(2496, 1404, 1_000.0),
            target: display(1920, 1080, 1_000.0),
            strategy: SizingStrategy::RoundedScale,
            alignment: 2,
            preferred_refresh_millihz: None,
        }
        .calculate()
        .unwrap();
        assert_eq!(
            result.virtual_mode,
            PixelSize {
                width: 2400,
                height: 1350
            }
        );
    }

    #[test]
    fn integer_strategy_snaps_to_nearest_whole_scale() {
        let result = SizingRequest {
            reference: display(3008, 1692, 1_000.0),
            target: display(1920, 1080, 1_000.0),
            strategy: SizingStrategy::IntegerScale,
            alignment: 2,
            preferred_refresh_millihz: None,
        }
        .calculate()
        .unwrap();
        assert_eq!(
            result.virtual_mode,
            PixelSize {
                width: 3840,
                height: 2160
            }
        );
    }

    #[test]
    fn integer_strategy_never_drops_below_native_size() {
        let result = SizingRequest {
            reference: display(960, 540, 1_000.0),
            target: display(1920, 1080, 1_000.0),
            strategy: SizingStrategy::IntegerScale,
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
    fn legacy_integer_strategy_deserializes_without_exposing_its_old_limit() {
        for max_scale in [1, 4, 8] {
            let json = format!(r#"{{"integer_scale":{{"max_scale":{max_scale}}}}}"#);
            let strategy: SizingStrategy = serde_json::from_str(&json).unwrap();
            assert_eq!(strategy, SizingStrategy::IntegerScale);
        }
        for max_scale in [0, 9] {
            let json = format!(r#"{{"integer_scale":{{"max_scale":{max_scale}}}}}"#);
            assert!(serde_json::from_str::<SizingStrategy>(&json).is_err());
        }
    }

    #[test]
    fn current_sizing_strategies_round_trip_as_canonical_strings() {
        for strategy in [
            SizingStrategy::MatchPhysicalSize,
            SizingStrategy::RoundedScale,
            SizingStrategy::IntegerScale,
        ] {
            let json = serde_json::to_string(&strategy).unwrap();
            assert_eq!(
                serde_json::from_str::<SizingStrategy>(&json).unwrap(),
                strategy
            );
        }
        assert_eq!(
            serde_json::to_string(&SizingStrategy::IntegerScale).unwrap(),
            r#""integer_scale""#
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
