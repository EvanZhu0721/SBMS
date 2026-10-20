//! GPU-only virtual-display renderer.
//!
//! The hot path stays on the render adapter:
//! `Desktop Duplication -> D3D11 texture -> pixel shader -> flip-model swap chain`.
//! The production path never maps pixels into CPU memory or uses GDI.
//! Test-only offscreen readback validates the same native shader pipeline.

use std::fmt;
use std::mem::size_of;
use std::slice;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use crate::config::RenderingOptions;
use crate::diagnostics::{self, Level};

#[path = "gpu_sampling.rs"]
mod sampling;
use sampling::{SamplingPipeline, TextureTarget};

#[cfg(test)]
#[path = "gpu_sampling_benchmark.rs"]
mod native_benchmark;
#[cfg(test)]
#[path = "native_sampling_tests.rs"]
mod native_sampling_tests;
#[cfg(test)]
pub(crate) use native_benchmark::benchmark_sample_rgba;
use windows::Win32::Foundation::{E_ACCESSDENIED, HMODULE, HWND, RECT};
use windows::Win32::Graphics::Direct3D::Fxc::D3DCompile;
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE_UNKNOWN, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_11_1,
    D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST, ID3DBlob, ID3DInclude,
};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_CONSTANT_BUFFER, D3D11_BIND_SHADER_RESOURCE, D3D11_BOX, D3D11_BUFFER_DESC,
    D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_FILTER_MIN_MAG_MIP_LINEAR, D3D11_SAMPLER_DESC,
    D3D11_SDK_VERSION, D3D11_TEXTURE_ADDRESS_CLAMP, D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT,
    D3D11_VIEWPORT, D3D11CreateDevice, ID3D11Buffer, ID3D11Device, ID3D11DeviceContext,
    ID3D11PixelShader, ID3D11RenderTargetView, ID3D11SamplerState, ID3D11ShaderResourceView,
    ID3D11Texture2D, ID3D11VertexShader,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_ALPHA_MODE_IGNORE, DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, DXGI_ERROR_ACCESS_LOST, DXGI_ERROR_DEVICE_REMOVED, DXGI_ERROR_DEVICE_RESET,
    DXGI_ERROR_NOT_FOUND, DXGI_ERROR_WAIT_TIMEOUT, DXGI_MWA_NO_ALT_ENTER, DXGI_OUTDUPL_FRAME_INFO,
    DXGI_OUTDUPL_MOVE_RECT, DXGI_PRESENT, DXGI_SCALING_STRETCH, DXGI_SWAP_CHAIN_DESC1,
    DXGI_SWAP_EFFECT_FLIP_DISCARD, DXGI_USAGE_RENDER_TARGET_OUTPUT, IDXGIAdapter1, IDXGIFactory1,
    IDXGIFactory2, IDXGIOutput, IDXGIOutput1, IDXGIOutputDuplication, IDXGIResource,
    IDXGISwapChain1,
};
use windows::core::{Error as WindowsError, Interface, PCSTR};

const FRAME_WAIT_TIMEOUT_MS: u32 = 50;
const COPY_MODE_ENV: &str = "SBMS_RENDER_COPY_MODE";
const MAX_METADATA_BYTES: u32 = 1024 * 1024;
const MAX_COPY_REGIONS: usize = 128;
const MAX_PARTIAL_COPY_PERCENT: u64 = 50;
const COPY_STATS_INTERVAL: Duration = Duration::from_secs(10);

const SHADER: &str = r#"
Texture2D source_texture : register(t0);
SamplerState source_sampler : register(s0);

cbuffer ScaleParameters : register(b0) {
    float2 source_size;
    float2 target_size;
};

struct VertexOutput {
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

VertexOutput vertex_main(uint id : SV_VertexID) {
    float2 positions[3] = {
        float2(-1.0, -1.0),
        float2(-1.0,  3.0),
        float2( 3.0, -1.0)
    };
    VertexOutput output;
    output.position = float4(positions[id], 0.0, 1.0);
    output.uv = float2(
        (positions[id].x + 1.0) * 0.5,
        (1.0 - positions[id].y) * 0.5);
    return output;
}

void area_axis(
    float first,
    float last,
    float source_extent,
    out float2 position,
    out float2 weight) {
    float base = floor(first);
    float3 coverage = float3(
        max(0.0, min(last, base + 1.0) - max(first, base)),
        max(0.0, min(last, base + 2.0) - max(first, base + 1.0)),
        max(0.0, min(last, base + 3.0) - max(first, base + 2.0)));
    coverage /= last - first;

    // Two adjacent weighted texels are exactly representable by one bilinear
    // lookup. The third texel becomes the second lookup for this axis.
    weight = float2(coverage.x + coverage.y, coverage.z);
    float blend = weight.x > 0.0 ? coverage.y / weight.x : 0.0;
    position = float2(base + 0.5 + blend, base + 2.5) / source_extent;
}

float4 exact_area(float2 target_pixel) {
    float2 scale = source_size / target_size;
    float2 first = target_pixel * scale;
    float2 last = (target_pixel + 1.0) * scale;
    float2 position_x;
    float2 position_y;
    float2 weight_x;
    float2 weight_y;
    area_axis(first.x, last.x, source_size.x, position_x, weight_x);
    area_axis(first.y, last.y, source_size.y, position_y, weight_y);

    return
        source_texture.SampleLevel(source_sampler, float2(position_x.x, position_y.x), 0.0)
            * weight_x.x * weight_y.x +
        source_texture.SampleLevel(source_sampler, float2(position_x.y, position_y.x), 0.0)
            * weight_x.y * weight_y.x +
        source_texture.SampleLevel(source_sampler, float2(position_x.x, position_y.y), 0.0)
            * weight_x.x * weight_y.y +
        source_texture.SampleLevel(source_sampler, float2(position_x.y, position_y.y), 0.0)
            * weight_x.y * weight_y.y;
}

float3 suppress_subpixel_fringe(float2 target_pixel, float3 area_color) {
    float2 center_uv = (target_pixel + 0.5) / target_size;
    float2 half_source_pixel = float2(0.5 / source_size.x, 0.0);
    float3 left = source_texture.SampleLevel(
        source_sampler, center_uv - half_source_pixel, 0.0).rgb;
    float3 right = source_texture.SampleLevel(
        source_sampler, center_uv + half_source_pixel, 0.0).rgb;
    float3 chroma_low_pass = (left + 2.0 * area_color + right) * 0.25;

    // ClearType encodes coverage in RGB subpixels. After a non-integer resize
    // that high-frequency chroma is no longer aligned with the physical panel.
    // Preserve the area-filtered luminance and only borrow low-passed chroma
    // where the wider neighborhood is close to neutral. Solid UI colors keep
    // their original chroma.
    const float3 luma_weights = float3(0.2126, 0.7152, 0.0722);
    float area_luma = dot(area_color, luma_weights);
    float low_pass_luma = dot(chroma_low_pass, luma_weights);
    float3 area_chroma = area_color - area_luma;
    float3 low_pass_chroma = chroma_low_pass - low_pass_luma;
    float neighborhood_saturation =
        max(chroma_low_pass.r, max(chroma_low_pass.g, chroma_low_pass.b)) -
        min(chroma_low_pass.r, min(chroma_low_pass.g, chroma_low_pass.b));
    float chroma_delta = length(area_chroma - low_pass_chroma);
    float neutral_gate = 1.0 - smoothstep(0.04, 0.18, neighborhood_saturation);
    float artifact_gate = saturate(chroma_delta * 8.0) * neutral_gate * 0.85;
    float3 filtered_chroma = lerp(area_chroma, low_pass_chroma, artifact_gate);
    return saturate(area_luma + filtered_chroma);
}

float4 pixel_main(VertexOutput input) : SV_TARGET {
    float2 scale = source_size / target_size;
    bool area_supported =
        scale.x > 1.0001 && scale.x <= 2.0 &&
        scale.y > 1.0001 && scale.y <= 2.0;
    if (area_supported) {
        float2 target_pixel = floor(input.position.xy);
        float4 area = exact_area(target_pixel);
        area.rgb = suppress_subpixel_fringe(target_pixel, area.rgb);
        return area;
    }
    return source_texture.Sample(source_sampler, input.uv);
}
"#;

/// Everything the GPU loop needs from the existing window-owning renderer.
#[derive(Clone, Copy, Debug)]
pub struct GpuRendererConfig {
    pub target_window: HWND,
    pub target_width: u32,
    pub target_height: u32,
    pub source_rect: RECT,
    pub rendering: RenderingOptions,
    pub target_refresh_millihz: u32,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum GpuRendererError {
    /// Desktop Duplication must be rebuilt after a topology or mode change.
    AccessLost(String),
    /// Desktop Duplication output duplication access was denied temporarily (e.g. sleep/wake).
    DuplicateOutputAccessDenied(String),
    /// The render adapter reset or was removed.
    DeviceLost,
    Failure(String),
}

impl GpuRendererError {
    pub fn is_retryable(&self) -> bool {
        matches!(
            self,
            Self::AccessLost(_) | Self::DuplicateOutputAccessDenied(_)
        )
    }
}

impl fmt::Display for GpuRendererError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::AccessLost(message) => formatter.write_str(message),
            Self::DuplicateOutputAccessDenied(message) => formatter.write_str(message),
            Self::DeviceLost => formatter.write_str("D3D11 render device was lost"),
            Self::Failure(message) => formatter.write_str(message),
        }
    }
}

impl std::error::Error for GpuRendererError {}

/// Runs until `stop` is set or DXGI reports a topology/device change.
///
/// `on_present` is called only after a successful GPU present. It lets the
/// existing renderer keep its first-frame and FPS reporting without exposing
/// D3D objects outside this module.
pub fn run_gpu_renderer(
    config: GpuRendererConfig,
    stop: &AtomicBool,
    mut on_present: impl FnMut() -> Result<(), String>,
) -> Result<(), GpuRendererError> {
    validate_config(config)?;
    let copy_mode = CopyMode::from_options(config.rendering);
    let copy_stats_enabled = CopyMode::diagnostics_requested();
    let (adapter, output) = find_source_output(config.source_rect)?;
    let (device, context) = create_device(&adapter)?;
    let duplication = unsafe { output.DuplicateOutput(&device) }
        .map_err(|error| classify_windows_error("IDXGIOutput1::DuplicateOutput", error))?;
    let pipeline = Pipeline::new(config, &adapter, device, context)?;
    pipeline.run(
        duplication,
        stop,
        copy_mode,
        copy_stats_enabled,
        &mut on_present,
    )
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum CopyMode {
    Full,
    Dirty,
}

impl CopyMode {
    fn from_options(options: RenderingOptions) -> Self {
        // The persisted UI setting is authoritative. The environment variable
        // remains a diagnostics opt-in, never a hidden behavior override.
        if options.full_frame_copy {
            Self::Full
        } else {
            Self::Dirty
        }
    }

    fn parse(value: Option<&std::ffi::OsStr>) -> Result<Self, String> {
        let Some(value) = value else {
            return Ok(Self::Dirty);
        };
        let Some(value) = value.to_str() else {
            return Err(format!("{value:?}"));
        };
        match value.trim().to_ascii_lowercase().as_str() {
            "" | "full" => Ok(Self::Full),
            "dirty" => Ok(Self::Dirty),
            _ => Err(format!("{value:?}")),
        }
    }

    fn diagnostics_requested() -> bool {
        let value = std::env::var_os(COPY_MODE_ENV);
        value.is_some() && Self::parse(value.as_deref()) == Ok(Self::Dirty)
    }
}

fn validate_config(config: GpuRendererConfig) -> Result<(), GpuRendererError> {
    if config.target_window.0.is_null() {
        return Err(GpuRendererError::Failure(
            "GPU renderer target HWND is null".into(),
        ));
    }
    if config.target_width == 0 || config.target_height == 0 {
        return Err(GpuRendererError::Failure(
            "GPU renderer target dimensions must be non-zero".into(),
        ));
    }
    if config.source_rect.right <= config.source_rect.left
        || config.source_rect.bottom <= config.source_rect.top
    {
        return Err(GpuRendererError::Failure(
            "GPU renderer source rectangle is empty".into(),
        ));
    }
    Ok(())
}

fn find_source_output(
    source_rect: RECT,
) -> Result<(IDXGIAdapter1, IDXGIOutput1), GpuRendererError> {
    let factory: IDXGIFactory1 = unsafe { CreateDXGIFactory1() }
        .map_err(|error| classify_windows_error("CreateDXGIFactory1", error))?;
    let mut adapter_index = 0;
    loop {
        let adapter = match unsafe { factory.EnumAdapters1(adapter_index) } {
            Ok(adapter) => adapter,
            Err(error) if error.code() == DXGI_ERROR_NOT_FOUND => break,
            Err(error) => {
                return Err(classify_windows_error(
                    "IDXGIFactory1::EnumAdapters1",
                    error,
                ));
            }
        };
        let mut output_index = 0;
        loop {
            let output = match unsafe { adapter.EnumOutputs(output_index) } {
                Ok(output) => output,
                Err(error) if error.code() == DXGI_ERROR_NOT_FOUND => break,
                Err(error) => {
                    return Err(classify_windows_error("IDXGIAdapter::EnumOutputs", error));
                }
            };
            let description = unsafe { output.GetDesc() }
                .map_err(|error| classify_windows_error("IDXGIOutput::GetDesc", error))?;
            if same_rect(description.DesktopCoordinates, source_rect) {
                return output
                    .cast::<IDXGIOutput1>()
                    .map(|output| (adapter, output))
                    .map_err(|error| {
                        classify_windows_error("IDXGIOutput::QueryInterface(IDXGIOutput1)", error)
                    });
            }
            output_index += 1;
        }
        adapter_index += 1;
    }
    Err(GpuRendererError::Failure(format!(
        "no DXGI output matches source rectangle ({}, {})-({}, {})",
        source_rect.left, source_rect.top, source_rect.right, source_rect.bottom
    )))
}

fn same_rect(left: RECT, right: RECT) -> bool {
    left.left == right.left
        && left.top == right.top
        && left.right == right.right
        && left.bottom == right.bottom
}

fn create_device(
    adapter: &IDXGIAdapter1,
) -> Result<(ID3D11Device, ID3D11DeviceContext), GpuRendererError> {
    let mut device = None;
    let mut context = None;
    let levels = [D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0];
    unsafe {
        D3D11CreateDevice(
            adapter,
            D3D_DRIVER_TYPE_UNKNOWN,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            Some(&levels),
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )
    }
    .map_err(|error| classify_windows_error("D3D11CreateDevice", error))?;
    Ok((
        device.ok_or_else(|| GpuRendererError::Failure("D3D11 returned no device".into()))?,
        context.ok_or_else(|| {
            GpuRendererError::Failure("D3D11 returned no immediate context".into())
        })?,
    ))
}

struct Pipeline {
    config: GpuRendererConfig,
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    swap_chain: IDXGISwapChain1,
    render_target: ID3D11RenderTargetView,
    back_buffer: ID3D11Texture2D,
    sampling: SamplingPipeline,
    cached_frame: Option<TextureTarget>,
}

impl Pipeline {
    fn new(
        config: GpuRendererConfig,
        adapter: &IDXGIAdapter1,
        device: ID3D11Device,
        context: ID3D11DeviceContext,
    ) -> Result<Self, GpuRendererError> {
        let factory: IDXGIFactory2 = unsafe { adapter.GetParent() }.map_err(|error| {
            classify_windows_error("IDXGIAdapter::GetParent(IDXGIFactory2)", error)
        })?;
        let swap_description = DXGI_SWAP_CHAIN_DESC1 {
            Width: config.target_width,
            Height: config.target_height,
            Format: DXGI_FORMAT_B8G8R8A8_UNORM,
            Stereo: false.into(),
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: 2,
            Scaling: DXGI_SCALING_STRETCH,
            SwapEffect: DXGI_SWAP_EFFECT_FLIP_DISCARD,
            AlphaMode: DXGI_ALPHA_MODE_IGNORE,
            Flags: 0,
        };
        let swap_chain = unsafe {
            factory.CreateSwapChainForHwnd(
                &device,
                config.target_window,
                &swap_description,
                None,
                None::<&IDXGIOutput>,
            )
        }
        .map_err(|error| classify_windows_error("IDXGIFactory2::CreateSwapChainForHwnd", error))?;
        unsafe { factory.MakeWindowAssociation(config.target_window, DXGI_MWA_NO_ALT_ENTER) }
            .map_err(|error| {
                classify_windows_error("IDXGIFactory::MakeWindowAssociation", error)
            })?;

        let back_buffer: ID3D11Texture2D = unsafe { swap_chain.GetBuffer(0) }
            .map_err(|error| classify_windows_error("IDXGISwapChain::GetBuffer", error))?;
        let mut render_target = None;
        unsafe { device.CreateRenderTargetView(&back_buffer, None, Some(&mut render_target)) }
            .map_err(|error| {
                classify_windows_error("ID3D11Device::CreateRenderTargetView", error)
            })?;

        let sampling = SamplingPipeline::new(
            device.clone(),
            context.clone(),
            config.rendering.high_performance_sampling,
        )?;
        // Flip-discard back buffers do not preserve untouched pixels. Keep the
        // quality result in our own texture so partial sampling can reuse it.
        let cached_frame =
            if config.rendering.static_refresh || !config.rendering.high_performance_sampling {
                Some(TextureTarget::new(
                    &device,
                    config.target_width,
                    config.target_height,
                    DXGI_FORMAT_B8G8R8A8_UNORM,
                )?)
            } else {
                None
            };
        Ok(Self {
            config,
            device,
            context,
            swap_chain,
            back_buffer,
            sampling,
            cached_frame,
            render_target: render_target
                .ok_or_else(|| GpuRendererError::Failure("D3D11 returned no RTV".into()))?,
        })
    }

    fn run(
        mut self,
        duplication: IDXGIOutputDuplication,
        stop: &AtomicBool,
        copy_mode: CopyMode,
        copy_stats_enabled: bool,
        on_present: &mut impl FnMut() -> Result<(), String>,
    ) -> Result<(), GpuRendererError> {
        let mut source_texture = None;
        let mut source_view = None;
        let mut source_description = None;
        let mut copy_stats = CopyStats::new();
        let mut metadata_buffers = FrameMetadataBuffers::default();
        let mut cache_valid = false;
        let mut last_present: Option<Instant> = None;
        let refresh_interval =
            Duration::from_secs_f64(1000.0 / f64::from(self.config.target_refresh_millihz.max(1)));

        while !stop.load(Ordering::Acquire) {
            let mut frame_info = DXGI_OUTDUPL_FRAME_INFO::default();
            let mut desktop_resource: Option<IDXGIResource> = None;
            match unsafe {
                duplication.AcquireNextFrame(
                    frame_wait_timeout(
                        last_present,
                        refresh_interval,
                        self.config.rendering.static_refresh,
                    ),
                    &mut frame_info,
                    &mut desktop_resource,
                )
            } {
                Ok(()) => {}
                Err(error) if error.code() == DXGI_ERROR_WAIT_TIMEOUT => {
                    if !stop.load(Ordering::Acquire)
                        && self.config.rendering.static_refresh
                        && last_present.is_some_and(|time| time.elapsed() >= refresh_interval)
                    {
                        self.present_cached(0)?;
                        on_present().map_err(GpuRendererError::Failure)?;
                        last_present = Some(advance_refresh_deadline(
                            last_present.unwrap(),
                            refresh_interval,
                        ));
                    }
                    continue;
                }
                Err(error) => {
                    return Err(classify_windows_error(
                        "IDXGIOutputDuplication::AcquireNextFrame",
                        error,
                    ));
                }
            }
            let frame = AcquiredFrame(&duplication);
            if stop.load(Ordering::Acquire) {
                break;
            }
            // Pointer-only events do not alter the captured desktop texture.
            if frame_info.LastPresentTime == 0 && source_texture.is_some() {
                drop(frame);
                if self.config.rendering.static_refresh
                    && last_present.is_some_and(|time| time.elapsed() >= refresh_interval)
                {
                    self.present_cached(0)?;
                    on_present().map_err(GpuRendererError::Failure)?;
                    last_present = Some(advance_refresh_deadline(
                        last_present.unwrap(),
                        refresh_interval,
                    ));
                }
                continue;
            }
            let desktop_resource = desktop_resource.ok_or_else(|| {
                GpuRendererError::Failure("Desktop Duplication returned no texture".into())
            })?;
            let acquired: ID3D11Texture2D = desktop_resource.cast().map_err(|error| {
                classify_windows_error("IDXGIResource::QueryInterface(ID3D11Texture2D)", error)
            })?;
            let mut description = D3D11_TEXTURE2D_DESC::default();
            unsafe { acquired.GetDesc(&mut description) };
            if description.Format != DXGI_FORMAT_B8G8R8A8_UNORM {
                return Err(GpuRendererError::Failure(format!(
                    "unsupported Desktop Duplication texture format {}",
                    description.Format.0
                )));
            }
            let texture_rebuilt =
                source_description != Some((description.Width, description.Height));
            if texture_rebuilt {
                let (texture, view) = self.create_source_texture(description)?;
                source_texture = Some(texture);
                source_view = Some(view);
                source_description = Some((description.Width, description.Height));
            }
            let texture = source_texture.as_ref().ok_or_else(|| {
                GpuRendererError::Failure("source texture was not initialized".into())
            })?;
            let has_regions = self.copy_source_frame(
                &duplication,
                &frame_info,
                texture,
                &acquired,
                description.Width,
                description.Height,
                copy_mode,
                texture_rebuilt,
                &mut metadata_buffers,
                &mut copy_stats,
            );
            drop(frame);

            let view = source_view.as_ref().ok_or_else(|| {
                GpuRendererError::Failure("source texture view was not initialized".into())
            })?;
            let quality = !self.config.rendering.high_performance_sampling;
            let full_region = metadata_buffers.regions.iter().any(|r| {
                r.left == 0
                    && r.top == 0
                    && r.right == description.Width as i32
                    && r.bottom == description.Height as i32
            });
            // Full updates can render straight to the swap chain. On returning
            // to partial updates, initialize the persistent output once.
            let use_cache =
                self.config.rendering.static_refresh || (quality && has_regions && !full_region);
            let output = if use_cache {
                &self.cached_frame.as_ref().unwrap().target
            } else {
                &self.render_target
            };
            self.sampling.render_regions(
                view,
                [description.Width, description.Height],
                output,
                [self.config.target_width, self.config.target_height],
                (use_cache && cache_valid && has_regions && quality)
                    .then_some(metadata_buffers.regions.as_slice()),
            )?;
            cache_valid = use_cache;
            if use_cache {
                self.present_cached(1)?;
            } else {
                self.present(1)?;
            }
            on_present().map_err(GpuRendererError::Failure)?;
            last_present = Some(Instant::now());
            copy_stats.maybe_log(
                copy_mode,
                copy_stats_enabled,
                description.Width,
                description.Height,
            );
        }
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    fn copy_source_frame(
        &self,
        duplication: &IDXGIOutputDuplication,
        frame_info: &DXGI_OUTDUPL_FRAME_INFO,
        target: &ID3D11Texture2D,
        acquired: &ID3D11Texture2D,
        width: u32,
        height: u32,
        copy_mode: CopyMode,
        force_full_copy: bool,
        metadata_buffers: &mut FrameMetadataBuffers,
        stats: &mut CopyStats,
    ) -> bool {
        let regions = if copy_mode == CopyMode::Dirty && !force_full_copy {
            dirty_copy_regions(duplication, frame_info, width, height, metadata_buffers)
        } else {
            None
        };

        let Some(regions) = regions else {
            unsafe { self.context.CopyResource(target, acquired) };
            stats.record_full(width, height);
            return false;
        };
        // Large updates are cheaper to copy in one operation, but their valid
        // metadata still lets the filtering stages preserve unchanged pixels.
        let changed_pixels: u64 = regions
            .iter()
            .map(|r| (r.right - r.left) as u64 * (r.bottom - r.top) as u64)
            .sum();
        if changed_pixels * 100 >= u64::from(width) * u64::from(height) * MAX_PARTIAL_COPY_PERCENT {
            unsafe { self.context.CopyResource(target, acquired) };
            stats.record_full(width, height);
            return true;
        }

        for region in regions {
            let source_box = D3D11_BOX {
                left: region.left as u32,
                top: region.top as u32,
                front: 0,
                right: region.right as u32,
                bottom: region.bottom as u32,
                back: 1,
            };
            unsafe {
                self.context.CopySubresourceRegion(
                    target,
                    0,
                    region.left as u32,
                    region.top as u32,
                    0,
                    acquired,
                    0,
                    Some(&source_box),
                );
            }
        }
        stats.record_partial(regions);
        true
    }

    fn create_source_texture(
        &self,
        mut description: D3D11_TEXTURE2D_DESC,
    ) -> Result<(ID3D11Texture2D, ID3D11ShaderResourceView), GpuRendererError> {
        description.BindFlags = D3D11_BIND_SHADER_RESOURCE.0 as u32;
        description.CPUAccessFlags = 0;
        description.MiscFlags = 0;
        description.Usage = D3D11_USAGE_DEFAULT;
        let mut texture = None;
        unsafe {
            self.device
                .CreateTexture2D(&description, None, Some(&mut texture))
        }
        .map_err(|error| classify_windows_error("ID3D11Device::CreateTexture2D", error))?;
        let texture =
            texture.ok_or_else(|| GpuRendererError::Failure("D3D11 returned no texture".into()))?;
        let mut view = None;
        unsafe {
            self.device
                .CreateShaderResourceView(&texture, None, Some(&mut view))
        }
        .map_err(|error| classify_windows_error("ID3D11Device::CreateShaderResourceView", error))?;
        Ok((
            texture,
            view.ok_or_else(|| GpuRendererError::Failure("D3D11 returned no SRV".into()))?,
        ))
    }

    fn present_cached(&self, sync_interval: u32) -> Result<(), GpuRendererError> {
        if let Some(frame) = &self.cached_frame {
            unsafe {
                self.context.CopyResource(&self.back_buffer, &frame.texture);
            }
        }
        self.present(sync_interval)
    }

    fn present(&self, sync_interval: u32) -> Result<(), GpuRendererError> {
        let present = unsafe { self.swap_chain.Present(sync_interval, DXGI_PRESENT(0)) };
        present
            .ok()
            .map_err(|_| classify_hresult("IDXGISwapChain::Present", present))
    }
}

// Wait for new pixels or for the next static presentation deadline. Cap the
// blocking wait so cancellation/device recovery stays responsive.
fn frame_wait_timeout(last: Option<Instant>, interval: Duration, static_refresh: bool) -> u32 {
    if !static_refresh {
        return FRAME_WAIT_TIMEOUT_MS;
    }
    let Some(last) = last else {
        return FRAME_WAIT_TIMEOUT_MS;
    };
    let remaining = interval.saturating_sub(last.elapsed());
    remaining
        .as_millis()
        .saturating_add(u128::from(
            !remaining.subsec_nanos().is_multiple_of(1_000_000),
        ))
        .min(u128::from(FRAME_WAIT_TIMEOUT_MS)) as u32
}

fn advance_refresh_deadline(previous: Instant, interval: Duration) -> Instant {
    let now = Instant::now();
    let mut scheduled = previous + interval;
    while scheduled + interval <= now {
        scheduled += interval;
    }
    scheduled
}

fn dirty_copy_regions<'a>(
    duplication: &IDXGIOutputDuplication,
    frame_info: &DXGI_OUTDUPL_FRAME_INFO,
    width: u32,
    height: u32,
    buffers: &'a mut FrameMetadataBuffers,
) -> Option<&'a [RECT]> {
    let metadata_bytes = frame_info.TotalMetadataBufferSize;
    if metadata_bytes == 0 {
        buffers.regions.clear();
        return Some(&buffers.regions);
    }
    if metadata_bytes > MAX_METADATA_BYTES {
        return None;
    }

    let FrameMetadataBuffers {
        moves,
        dirty,
        regions,
    } = buffers;
    let moves = read_move_rects(duplication, metadata_bytes, moves)?;
    let dirty = read_dirty_rects(duplication, metadata_bytes, dirty)?;
    if select_copy_regions(
        moves.iter().map(|movement| movement.DestinationRect),
        dirty.iter().copied(),
        width,
        height,
        regions,
    ) {
        Some(regions.as_slice())
    } else {
        None
    }
}

#[derive(Default)]
struct FrameMetadataBuffers {
    moves: Vec<DXGI_OUTDUPL_MOVE_RECT>,
    dirty: Vec<RECT>,
    regions: Vec<RECT>,
}

fn read_move_rects<'a>(
    duplication: &IDXGIOutputDuplication,
    metadata_bytes: u32,
    buffer: &'a mut Vec<DXGI_OUTDUPL_MOVE_RECT>,
) -> Option<&'a [DXGI_OUTDUPL_MOVE_RECT]> {
    let element_bytes = size_of::<DXGI_OUTDUPL_MOVE_RECT>();
    let element_count = (metadata_bytes as usize).div_ceil(element_bytes).max(1);
    buffer.resize(element_count, DXGI_OUTDUPL_MOVE_RECT::default());
    let buffer_bytes = u32::try_from(buffer.len().checked_mul(element_bytes)?).ok()?;
    let mut required_bytes = 0;
    unsafe {
        duplication
            .GetFrameMoveRects(buffer_bytes, buffer.as_mut_ptr(), &mut required_bytes)
            .ok()?;
    }
    let required_bytes = required_bytes as usize;
    if required_bytes > buffer_bytes as usize || !required_bytes.is_multiple_of(element_bytes) {
        return None;
    }
    Some(&buffer[..required_bytes / element_bytes])
}

fn read_dirty_rects<'a>(
    duplication: &IDXGIOutputDuplication,
    metadata_bytes: u32,
    buffer: &'a mut Vec<RECT>,
) -> Option<&'a [RECT]> {
    let element_bytes = size_of::<RECT>();
    let element_count = (metadata_bytes as usize).div_ceil(element_bytes).max(1);
    buffer.resize(element_count, RECT::default());
    let buffer_bytes = u32::try_from(buffer.len().checked_mul(element_bytes)?).ok()?;
    let mut required_bytes = 0;
    unsafe {
        duplication
            .GetFrameDirtyRects(buffer_bytes, buffer.as_mut_ptr(), &mut required_bytes)
            .ok()?;
    }
    let required_bytes = required_bytes as usize;
    if required_bytes > buffer_bytes as usize || !required_bytes.is_multiple_of(element_bytes) {
        return None;
    }
    Some(&buffer[..required_bytes / element_bytes])
}

fn select_copy_regions(
    moved: impl IntoIterator<Item = RECT>,
    dirty: impl IntoIterator<Item = RECT>,
    width: u32,
    height: u32,
    regions: &mut Vec<RECT>,
) -> bool {
    regions.clear();
    let Some(source_pixels) = u64::from(width).checked_mul(u64::from(height)) else {
        return false;
    };
    if source_pixels == 0 {
        return false;
    }

    for region in moved.into_iter().chain(dirty) {
        if region.left < 0
            || region.top < 0
            || region.right <= region.left
            || region.bottom <= region.top
            || region.right as u32 > width
            || region.bottom as u32 > height
        {
            return false;
        }
        if regions.contains(&region) {
            continue;
        }
        if regions.len() == MAX_COPY_REGIONS {
            return false;
        }
        regions.push(region);
    }
    true
}

struct CopyStats {
    interval_started: Instant,
    frames: u64,
    full_frames: u64,
    partial_frames: u64,
    copied_regions: u64,
    copied_pixels: u64,
}

impl CopyStats {
    fn new() -> Self {
        Self {
            interval_started: Instant::now(),
            frames: 0,
            full_frames: 0,
            partial_frames: 0,
            copied_regions: 0,
            copied_pixels: 0,
        }
    }

    fn record_full(&mut self, width: u32, height: u32) {
        self.frames += 1;
        self.full_frames += 1;
        self.copied_pixels += u64::from(width) * u64::from(height);
    }

    fn record_partial(&mut self, regions: &[RECT]) {
        self.frames += 1;
        self.partial_frames += 1;
        self.copied_regions += regions.len() as u64;
        self.copied_pixels += regions
            .iter()
            .map(|region| (region.right - region.left) as u64 * (region.bottom - region.top) as u64)
            .sum::<u64>();
    }

    fn maybe_log(&mut self, copy_mode: CopyMode, enabled: bool, width: u32, height: u32) {
        if !enabled
            || copy_mode != CopyMode::Dirty
            || self.interval_started.elapsed() < COPY_STATS_INTERVAL
        {
            return;
        }
        let available_pixels = self
            .frames
            .saturating_mul(u64::from(width))
            .saturating_mul(u64::from(height));
        let copied_percent = if available_pixels == 0 {
            0.0
        } else {
            self.copied_pixels as f64 * 100.0 / available_pixels as f64
        };
        diagnostics::log(
            Level::Info,
            "renderer",
            "dirty-copy-stats",
            None,
            format!(
                "frames={} partial={} full_fallbacks={} rects={} copied={copied_percent:.2}%",
                self.frames, self.partial_frames, self.full_frames, self.copied_regions,
            ),
        );
        *self = Self::new();
    }
}

struct AcquiredFrame<'a>(&'a IDXGIOutputDuplication);

impl Drop for AcquiredFrame<'_> {
    fn drop(&mut self) {
        unsafe {
            let _ = self.0.ReleaseFrame();
        }
    }
}

#[repr(C)]
struct ScaleParameters {
    source_size: [f32; 2],
    target_size: [f32; 2],
}

fn compile_shader(entry: &str, target: &str) -> Result<Vec<u8>, GpuRendererError> {
    compile_shader_source(SHADER, entry, target)
}

fn compile_shader_source(
    source: &str,
    entry: &str,
    target: &str,
) -> Result<Vec<u8>, GpuRendererError> {
    let entry = null_terminated(entry);
    let target = null_terminated(target);
    let mut code: Option<ID3DBlob> = None;
    let mut diagnostics: Option<ID3DBlob> = None;
    let result = unsafe {
        D3DCompile(
            source.as_ptr().cast(),
            source.len(),
            PCSTR::null(),
            None,
            None::<&ID3DInclude>,
            PCSTR(entry.as_ptr()),
            PCSTR(target.as_ptr()),
            0,
            0,
            &mut code,
            Some(&mut diagnostics),
        )
    };
    if let Err(error) = result {
        let detail = diagnostics
            .as_ref()
            .map(blob_text)
            .filter(|message| !message.is_empty())
            .unwrap_or_else(|| error.to_string());
        return Err(GpuRendererError::Failure(format!(
            "D3DCompile failed: {:?}; {detail}",
            error.code()
        )));
    }
    let code =
        code.ok_or_else(|| GpuRendererError::Failure("D3DCompile returned no bytecode".into()))?;
    let bytes =
        unsafe { slice::from_raw_parts(code.GetBufferPointer().cast(), code.GetBufferSize()) };
    Ok(bytes.to_vec())
}

fn blob_text(blob: &ID3DBlob) -> String {
    let bytes =
        unsafe { slice::from_raw_parts(blob.GetBufferPointer().cast(), blob.GetBufferSize()) };
    String::from_utf8_lossy(bytes)
        .trim_end_matches('\0')
        .trim()
        .to_owned()
}

fn null_terminated(value: &str) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(value.len() + 1);
    bytes.extend_from_slice(value.as_bytes());
    bytes.push(0);
    bytes
}

fn classify_windows_error(api: &str, error: WindowsError) -> GpuRendererError {
    classify_hresult(api, error.code())
}

fn classify_hresult(api: &str, code: windows::core::HRESULT) -> GpuRendererError {
    let detail = format!("{api} failed: {code:?}");
    // Log every failing API, including device loss whose classification does
    // not retain the original error text.
    diagnostics::log(Level::Warn, "renderer", "gpu-api", None, &detail);
    if code == DXGI_ERROR_ACCESS_LOST {
        GpuRendererError::AccessLost(detail)
    } else if code == DXGI_ERROR_DEVICE_REMOVED || code == DXGI_ERROR_DEVICE_RESET {
        GpuRendererError::DeviceLost
    } else if api == "IDXGIOutput1::DuplicateOutput" && code == E_ACCESSDENIED {
        GpuRendererError::DuplicateOutputAccessDenied(detail)
    } else {
        GpuRendererError::Failure(detail)
    }
}

/// Test-only readback. The production renderer never stages or maps pixels.
#[cfg(test)]
pub(crate) struct NativeSample {
    pub rgba: Vec<u8>,
    pub cleaned_rgba: Option<Vec<u8>>,
    pub adapter_name: String,
    /// CPU wall time including resource allocation/upload, submission and
    /// readback/GPU completion; excludes device creation and shader compilation.
    pub elapsed: Duration,
}

#[cfg(test)]
pub(crate) fn verify_sample_rgba(
    source: &[u8],
    source_width: u32,
    source_height: u32,
    target_width: u32,
    target_height: u32,
    high_performance: bool,
) -> Result<NativeSample, String> {
    use windows::Win32::Graphics::Direct3D11::{
        D3D11_CPU_ACCESS_READ, D3D11_MAP_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_USAGE_STAGING,
    };
    fn readback(
        device: &ID3D11Device,
        context: &ID3D11DeviceContext,
        texture: &ID3D11Texture2D,
    ) -> Result<Vec<u8>, String> {
        let mut description = D3D11_TEXTURE2D_DESC::default();
        unsafe { texture.GetDesc(&mut description) };
        description.Usage = D3D11_USAGE_STAGING;
        description.BindFlags = 0;
        description.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
        description.MiscFlags = 0;
        let mut staging = None;
        unsafe { device.CreateTexture2D(&description, None, Some(&mut staging)) }
            .map_err(|e| e.to_string())?;
        let staging = staging.ok_or("No readback texture")?;
        unsafe { context.CopyResource(&staging, texture) };
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        unsafe { context.Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped)) }
            .map_err(|e| e.to_string())?;
        let mut rgba = vec![0; (description.Width * description.Height * 4) as usize];
        for y in 0..description.Height as usize {
            let row = unsafe {
                slice::from_raw_parts(
                    mapped.pData.cast::<u8>().add(y * mapped.RowPitch as usize),
                    description.Width as usize * 4,
                )
            };
            let output = &mut rgba[y * description.Width as usize * 4..][..row.len()];
            for (input, output) in row.chunks_exact(4).zip(output.chunks_exact_mut(4)) {
                output.copy_from_slice(&[input[2], input[1], input[0], input[3]]);
            }
        }
        unsafe { context.Unmap(&staging, 0) };
        Ok(rgba)
    }
    if [source_width, source_height, target_width, target_height]
        .iter()
        .any(|&v| v == 0 || v > 16384)
        || source.len() != source_width as usize * source_height as usize * 4
    {
        return Err("Invalid native sampling test dimensions or RGBA buffer".into());
    }
    let factory: IDXGIFactory1 = unsafe { CreateDXGIFactory1() }.map_err(|e| e.to_string())?;
    let adapter = unsafe { factory.EnumAdapters1(0) }.map_err(|e| e.to_string())?;
    let description = unsafe { adapter.GetDesc1() }.map_err(|e| e.to_string())?;
    let length = description
        .Description
        .iter()
        .position(|&v| v == 0)
        .unwrap_or(description.Description.len());
    let adapter_name = String::from_utf16_lossy(&description.Description[..length]);
    let (device, context) = create_device(&adapter).map_err(|e| e.to_string())?;
    let mut sampling = SamplingPipeline::new(device.clone(), context.clone(), high_performance)
        .map_err(|e| e.to_string())?;
    let start = Instant::now();
    let input = TextureTarget::new(
        &device,
        source_width,
        source_height,
        DXGI_FORMAT_B8G8R8A8_UNORM,
    )
    .map_err(|e| e.to_string())?;
    let output = TextureTarget::new(
        &device,
        target_width,
        target_height,
        DXGI_FORMAT_B8G8R8A8_UNORM,
    )
    .map_err(|e| e.to_string())?;
    let mut bgra = source.to_vec();
    for pixel in bgra.chunks_exact_mut(4) {
        pixel.swap(0, 2);
    }
    unsafe {
        context.UpdateSubresource(
            &input.texture,
            0,
            None,
            bgra.as_ptr().cast(),
            source_width * 4,
            0,
        );
    }
    sampling
        .render(
            &input.view,
            [source_width, source_height],
            &output.target,
            [target_width, target_height],
        )
        .map_err(|e| e.to_string())?;
    let rgba = readback(&device, &context, &output.texture)?;
    let cleaned_rgba = sampling
        .clean
        .as_ref()
        .map(|clean| readback(&device, &context, &clean.texture))
        .transpose()?;
    Ok(NativeSample {
        rgba,
        cleaned_rgba,
        adapter_name,
        elapsed: start.elapsed(),
    })
}

#[cfg(test)]
mod tests {
    use super::{
        CopyMode, DXGI_ERROR_ACCESS_LOST, DXGI_ERROR_DEVICE_REMOVED, DXGI_ERROR_DEVICE_RESET,
        GpuRendererError, MAX_COPY_REGIONS, ScaleParameters, classify_hresult,
        classify_windows_error, compile_shader, select_copy_regions,
    };
    use std::ffi::OsStr;
    use windows::Win32::Foundation::{E_ACCESSDENIED, RECT};
    use windows::core::Error as WindowsError;

    #[test]
    fn shaders_compile_with_the_system_d3d_compiler() {
        assert!(!compile_shader("vertex_main", "vs_5_0").unwrap().is_empty());
        assert!(!compile_shader("pixel_main", "ps_5_0").unwrap().is_empty());
    }

    #[test]
    fn dxgi_access_lost_is_a_recoverable_topology_loss() {
        let error = classify_hresult(
            "IDXGIOutputDuplication::AcquireNextFrame",
            DXGI_ERROR_ACCESS_LOST,
        );
        assert!(matches!(error, GpuRendererError::AccessLost(_)));
        assert!(error.is_retryable());
        assert_eq!(
            error.to_string(),
            "IDXGIOutputDuplication::AcquireNextFrame failed: HRESULT(0x887A0026)"
        );
    }

    #[test]
    fn duplicate_output_access_denied_is_retryable() {
        let error = classify_windows_error(
            "IDXGIOutput1::DuplicateOutput",
            WindowsError::from_hresult(E_ACCESSDENIED),
        );
        assert!(matches!(
            error,
            GpuRendererError::DuplicateOutputAccessDenied(_)
        ));
        assert!(error.is_retryable());
        assert_eq!(
            error.to_string(),
            "IDXGIOutput1::DuplicateOutput failed: HRESULT(0x80070005)"
        );
    }

    #[test]
    fn other_api_access_denied_does_not_become_retryable() {
        let error = classify_windows_error(
            "ID3D11Device::CreateTexture2D",
            WindowsError::from_hresult(E_ACCESSDENIED),
        );
        assert!(matches!(error, GpuRendererError::Failure(_)));
        assert!(!error.is_retryable());
        assert_eq!(
            error.to_string(),
            "ID3D11Device::CreateTexture2D failed: HRESULT(0x80070005)"
        );
    }

    #[test]
    fn dxgi_device_removed_and_reset_keep_the_device_loss_classification() {
        for code in [DXGI_ERROR_DEVICE_REMOVED, DXGI_ERROR_DEVICE_RESET] {
            let error = classify_hresult("IDXGISwapChain::Present", code);
            assert!(matches!(error, GpuRendererError::DeviceLost));
            assert!(!error.is_retryable());
        }
    }

    #[test]
    fn scale_parameters_obey_constant_buffer_alignment() {
        assert_eq!(size_of::<ScaleParameters>(), 16);
    }

    #[test]
    fn dirty_copy_keeps_move_destinations_and_dirty_rects() {
        let moved = [RECT {
            left: 1,
            top: 2,
            right: 11,
            bottom: 12,
        }];
        let dirty = [RECT {
            left: 20,
            top: 30,
            right: 40,
            bottom: 50,
        }];
        let mut regions = Vec::new();
        assert!(select_copy_regions(moved, dirty, 100, 100, &mut regions));
        assert_eq!(regions, [moved[0], dirty[0]]);
    }

    #[test]
    fn dirty_copy_falls_back_for_invalid_or_large_metadata() {
        let invalid = [RECT {
            left: -1,
            top: 0,
            right: 10,
            bottom: 10,
        }];
        let mut regions = Vec::new();
        assert!(!select_copy_regions([], invalid, 100, 100, &mut regions));

        let half_frame = [RECT {
            left: 0,
            top: 0,
            right: 50,
            bottom: 100,
        }];
        // Full-copy threshold must not discard valid sampling metadata.
        assert!(select_copy_regions([], half_frame, 100, 100, &mut regions));
        assert_eq!(regions, half_frame);

        let too_many = (0..=MAX_COPY_REGIONS).map(|column| RECT {
            left: column as i32,
            top: 0,
            right: column as i32 + 1,
            bottom: 1,
        });
        assert!(!select_copy_regions([], too_many, 1000, 1000, &mut regions));
    }

    #[test]
    fn full_copy_remains_the_default_and_invalid_values_are_rejected() {
        assert_eq!(CopyMode::parse(None), Ok(CopyMode::Dirty));
        assert_eq!(
            CopyMode::parse(Some(OsStr::new(" full "))),
            Ok(CopyMode::Full)
        );
        assert_eq!(
            CopyMode::parse(Some(OsStr::new("DIRTY"))),
            Ok(CopyMode::Dirty)
        );
        assert!(CopyMode::parse(Some(OsStr::new("partial"))).is_err());
    }

    #[test]
    fn four_fetch_area_weights_preserve_all_16_phases() {
        let scale = 29.0_f32 / 16.0;
        for target_pixel in 0..16 {
            let first = target_pixel as f32 * scale;
            let last = (target_pixel + 1) as f32 * scale;
            let base = first.floor();
            let coverage = [
                overlap(first, last, base, base + 1.0) / scale,
                overlap(first, last, base + 1.0, base + 2.0) / scale,
                overlap(first, last, base + 2.0, base + 3.0) / scale,
            ];
            let paired_weight = coverage[0] + coverage[1];
            let blend = coverage[1] / paired_weight;
            let reconstructed = [
                paired_weight * (1.0 - blend),
                paired_weight * blend,
                coverage[2],
            ];
            for index in 0..3 {
                assert!((coverage[index] - reconstructed[index]).abs() < 1.0e-6);
            }
            assert!((reconstructed.iter().sum::<f32>() - 1.0).abs() < 1.0e-6);
        }
    }

    fn overlap(first: f32, last: f32, pixel_first: f32, pixel_last: f32) -> f32 {
        (last.min(pixel_last) - first.max(pixel_first)).max(0.0)
    }
}
