# Geometry API

The public types in `src/geometry.rs` are the single source of truth for
virtual-display sizing and pointer-coordinate conversion. A frontend should
persist `SizingRequest`, call `SizingRequest::calculate`, and pass the request
through the existing mapping path. It should not calculate or persist a derived
display mode itself.

## Sizing

```rust
pub struct DisplayGeometry {
    pub native_pixels: PixelSize,
    pub physical: PhysicalMeasurement,
    pub aspect_ratio: Option<AspectRatio>,
    pub rotation: Rotation,
}

pub struct SizingRequest {
    pub reference: DisplayGeometry,
    pub target: DisplayGeometry,
    pub strategy: SizingStrategy,
    pub alignment: u32,
    pub preferred_refresh_millihz: Option<u32>,
}

pub enum SizingStrategy {
    MatchPhysicalSize,
    RoundedScale,
    IntegerScale,
}
```

`reference` describes the display whose apparent pixel density should be
matched. `target` describes the physical display that receives the mirror.
Physical size may be supplied as width and height in millimetres or as a
diagonal; an explicit aspect ratio can be supplied when a diagonal is used.

`SizingRequest::calculate()` returns:

```rust
pub struct SizingResult {
    pub virtual_mode: PixelSize,
    pub oriented_target: PixelSize,
    pub scale_x: f64,
    pub scale_y: f64,
    pub preferred_refresh_millihz: Option<u32>,
}
```

The alignment must be a power of two from 1 through 256. Measurements must be
finite and positive. A preferred refresh rate, when present, must be non-zero.
The session gate accepts dimensions from 1 through 16384 and refresh rates up
to 1000 Hz.

## Mapping path

```text
SizingRequest::calculate
  -> SizingResult.virtual_mode
  -> VirtualMode
  -> protected v5 session gate
  -> IDD monitor and target mode
  -> Windows display source
  -> GPU renderer
```

The mode is fixed before the software display device is created. Changing it
requires stopping and starting the mapping session.

## Pointer coordinates

`CoordinateTransform::stretch(target, source, rotation)` builds the transform
shared by the renderer input path. `map_target_point` maps a physical target
point to the corresponding virtual-desktop point and returns `None` outside the
target rectangle.

```rust
let transform = CoordinateTransform::stretch(target_rect, source_rect, rotation)?;
let source_point = transform.map_target_point(target_point);
```

The transform supports 0, 90, 180 and 270 degree rotation.
