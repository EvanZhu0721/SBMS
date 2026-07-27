# 0002 GPU mirror path

Accepted: 2026-07-28

## Decision

The C++/WDK IDD owns mode reporting and timely completion of every IddCx
swapchain buffer. It does not publish pixels, create staging textures, map GPU
resources into CPU memory, or maintain shared frame objects.

Rust matches the active virtual source rectangle to one exact DXGI output. The
pixel hot path is:

```text
Desktop Duplication
  -> D3D11 texture
  -> pixel shader
  -> flip-model swapchain
  -> selected physical target
```

The renderer copies the acquired desktop resource only to an SRV-capable GPU
texture. No full frame crosses through CPU staging memory, a shared pixel
mapping, or GDI.

The protected `Global\SBMSSession-v4` object remains a fixed, identity-gated
single-session mode channel. It carries validated geometry and refresh data.
There are no nonce-derived frame mappings or frame events.

## Scaling policy

A 1:1 mapping bypasses filtering. When each source-to-target axis is a 1x..=2x
reduction, the shader integrates exact source-pixel coverage using four
bilinear texture fetches. It then takes two horizontal samples and applies a
neutral-content-gated chroma low-pass while retaining the area-filtered
luminance. This reduces ClearType subpixel color fringes without applying the
same desaturation to strongly colored UI elements.

Other ratios use bilinear sampling. The chroma filter is a lightweight image
heuristic: it does not identify text or font outlines, reconstruct grayscale
antialiasing, perform gamma-linear resampling, or provide HDR/color management.

## Boundary

GPU-only means the pixel hot path does not round-trip through CPU memory. It
does not mean zero GPU work, native Windows clone semantics, a general
game-streaming pipeline, or guaranteed sustained 240 fps. Desktop Duplication
must be rebuilt after access loss, device loss, or a relevant topology change.
Pointer metadata must be honored when Windows does not include the pointer in
the duplicated desktop image.
