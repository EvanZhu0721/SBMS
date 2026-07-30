//! GPU-only virtual-display renderer.
//!
//! The hot path stays on the render adapter:
//! `Desktop Duplication -> D3D11 texture -> pixel shader -> flip-model swap chain`.
//! In particular, this module never creates a staging texture, maps a GPU
//! resource into CPU memory, or sends pixels through GDI.

use std::fmt;
use std::mem::size_of;
use std::slice;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use crate::diagnostics::{self, Level};
use windows::Win32::Foundation::{HMODULE, HWND, RECT};
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
}

#[derive(Debug)]
pub enum GpuRendererError {
    /// Desktop Duplication must be rebuilt after a topology or mode change.
    AccessLost,
    /// The render adapter reset or was removed.
    DeviceLost,
    Failure(String),
}

impl fmt::Display for GpuRendererError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::AccessLost => formatter.write_str("desktop duplication access was lost"),
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
    let copy_mode = CopyMode::from_environment();
    let (adapter, output) = find_source_output(config.source_rect)?;
    let (device, context) = create_device(&adapter)?;
    let duplication = unsafe { output.DuplicateOutput(&device) }.map_err(classify_windows_error)?;
    let pipeline = Pipeline::new(config, &adapter, device, context)?;
    pipeline.run(duplication, stop, copy_mode, &mut on_present)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum CopyMode {
    Full,
    Dirty,
}

impl CopyMode {
    fn from_environment() -> Self {
        let value = std::env::var_os(COPY_MODE_ENV);
        match Self::parse(value.as_deref()) {
            Ok(Self::Dirty) => {
                diagnostics::log(
                    Level::Info,
                    "renderer",
                    "copy-mode",
                    None,
                    "experimental dirty source copy enabled",
                );
                Self::Dirty
            }
            Ok(Self::Full) => Self::Full,
            Err(value) => {
                diagnostics::log(
                    Level::Warn,
                    "renderer",
                    "copy-mode",
                    None,
                    format!("invalid {COPY_MODE_ENV}={value}; using full source copy"),
                );
                Self::Full
            }
        }
    }

    fn parse(value: Option<&std::ffi::OsStr>) -> Result<Self, String> {
        let Some(value) = value else {
            return Ok(Self::Full);
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
    let factory: IDXGIFactory1 = unsafe { CreateDXGIFactory1() }.map_err(classify_windows_error)?;
    let mut adapter_index = 0;
    loop {
        let adapter = match unsafe { factory.EnumAdapters1(adapter_index) } {
            Ok(adapter) => adapter,
            Err(error) if error.code() == DXGI_ERROR_NOT_FOUND => break,
            Err(error) => return Err(classify_windows_error(error)),
        };
        let mut output_index = 0;
        loop {
            let output = match unsafe { adapter.EnumOutputs(output_index) } {
                Ok(output) => output,
                Err(error) if error.code() == DXGI_ERROR_NOT_FOUND => break,
                Err(error) => return Err(classify_windows_error(error)),
            };
            let description = unsafe { output.GetDesc() }.map_err(classify_windows_error)?;
            if same_rect(description.DesktopCoordinates, source_rect) {
                return output
                    .cast::<IDXGIOutput1>()
                    .map(|output| (adapter, output))
                    .map_err(classify_windows_error);
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
    .map_err(classify_windows_error)?;
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
    vertex_shader: ID3D11VertexShader,
    pixel_shader: ID3D11PixelShader,
    sampler: ID3D11SamplerState,
    scale_parameters: ID3D11Buffer,
}

impl Pipeline {
    fn new(
        config: GpuRendererConfig,
        adapter: &IDXGIAdapter1,
        device: ID3D11Device,
        context: ID3D11DeviceContext,
    ) -> Result<Self, GpuRendererError> {
        let factory: IDXGIFactory2 =
            unsafe { adapter.GetParent() }.map_err(classify_windows_error)?;
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
        .map_err(classify_windows_error)?;
        unsafe { factory.MakeWindowAssociation(config.target_window, DXGI_MWA_NO_ALT_ENTER) }
            .map_err(classify_windows_error)?;

        let back_buffer: ID3D11Texture2D =
            unsafe { swap_chain.GetBuffer(0) }.map_err(classify_windows_error)?;
        let mut render_target = None;
        unsafe { device.CreateRenderTargetView(&back_buffer, None, Some(&mut render_target)) }
            .map_err(classify_windows_error)?;

        let vertex_code = compile_shader("vertex_main", "vs_5_0")?;
        let pixel_code = compile_shader("pixel_main", "ps_5_0")?;
        let mut vertex_shader = None;
        let mut pixel_shader = None;
        unsafe { device.CreateVertexShader(&vertex_code, None, Some(&mut vertex_shader)) }
            .map_err(classify_windows_error)?;
        unsafe { device.CreatePixelShader(&pixel_code, None, Some(&mut pixel_shader)) }
            .map_err(classify_windows_error)?;

        let sampler_description = D3D11_SAMPLER_DESC {
            Filter: D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU: D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV: D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW: D3D11_TEXTURE_ADDRESS_CLAMP,
            MaxLOD: f32::MAX,
            ..Default::default()
        };
        let mut sampler = None;
        unsafe { device.CreateSamplerState(&sampler_description, Some(&mut sampler)) }
            .map_err(classify_windows_error)?;

        let buffer_description = D3D11_BUFFER_DESC {
            ByteWidth: size_of::<ScaleParameters>() as u32,
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: D3D11_BIND_CONSTANT_BUFFER.0 as u32,
            ..Default::default()
        };
        let mut scale_parameters = None;
        unsafe { device.CreateBuffer(&buffer_description, None, Some(&mut scale_parameters)) }
            .map_err(classify_windows_error)?;

        Ok(Self {
            config,
            device,
            context,
            swap_chain,
            render_target: render_target
                .ok_or_else(|| GpuRendererError::Failure("D3D11 returned no RTV".into()))?,
            vertex_shader: vertex_shader.ok_or_else(|| {
                GpuRendererError::Failure("D3D11 returned no vertex shader".into())
            })?,
            pixel_shader: pixel_shader.ok_or_else(|| {
                GpuRendererError::Failure("D3D11 returned no pixel shader".into())
            })?,
            sampler: sampler
                .ok_or_else(|| GpuRendererError::Failure("D3D11 returned no sampler".into()))?,
            scale_parameters: scale_parameters.ok_or_else(|| {
                GpuRendererError::Failure("D3D11 returned no constant buffer".into())
            })?,
        })
    }

    fn run(
        self,
        duplication: IDXGIOutputDuplication,
        stop: &AtomicBool,
        copy_mode: CopyMode,
        on_present: &mut impl FnMut() -> Result<(), String>,
    ) -> Result<(), GpuRendererError> {
        let mut source_texture = None;
        let mut source_view = None;
        let mut source_description = None;
        let mut copy_stats = CopyStats::new();
        let mut metadata_buffers = FrameMetadataBuffers::default();

        while !stop.load(Ordering::Acquire) {
            let mut frame_info = DXGI_OUTDUPL_FRAME_INFO::default();
            let mut desktop_resource: Option<IDXGIResource> = None;
            match unsafe {
                duplication.AcquireNextFrame(
                    FRAME_WAIT_TIMEOUT_MS,
                    &mut frame_info,
                    &mut desktop_resource,
                )
            } {
                Ok(()) => {}
                Err(error) if error.code() == DXGI_ERROR_WAIT_TIMEOUT => continue,
                Err(error) => return Err(classify_windows_error(error)),
            }
            let frame = AcquiredFrame(&duplication);
            let desktop_resource = desktop_resource.ok_or_else(|| {
                GpuRendererError::Failure("Desktop Duplication returned no texture".into())
            })?;
            let acquired: ID3D11Texture2D =
                desktop_resource.cast().map_err(classify_windows_error)?;
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
                self.update_scale_parameters(description.Width, description.Height);
            }
            let texture = source_texture.as_ref().ok_or_else(|| {
                GpuRendererError::Failure("source texture was not initialized".into())
            })?;
            self.copy_source_frame(
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
            self.draw(view);
            let present = unsafe { self.swap_chain.Present(1, DXGI_PRESENT(0)) };
            present.ok().map_err(|_| classify_hresult(present))?;
            on_present().map_err(GpuRendererError::Failure)?;
            unsafe {
                self.context.PSSetShaderResources(0, Some(&[None]));
            }
            copy_stats.maybe_log(copy_mode, description.Width, description.Height);
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
    ) {
        let regions = if copy_mode == CopyMode::Dirty && !force_full_copy {
            dirty_copy_regions(duplication, frame_info, width, height, metadata_buffers)
        } else {
            None
        };

        let Some(regions) = regions else {
            unsafe { self.context.CopyResource(target, acquired) };
            stats.record_full(width, height);
            return;
        };

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
        .map_err(classify_windows_error)?;
        let texture =
            texture.ok_or_else(|| GpuRendererError::Failure("D3D11 returned no texture".into()))?;
        let mut view = None;
        unsafe {
            self.device
                .CreateShaderResourceView(&texture, None, Some(&mut view))
        }
        .map_err(classify_windows_error)?;
        Ok((
            texture,
            view.ok_or_else(|| GpuRendererError::Failure("D3D11 returned no SRV".into()))?,
        ))
    }

    fn update_scale_parameters(&self, source_width: u32, source_height: u32) {
        let parameters = ScaleParameters {
            source_size: [source_width as f32, source_height as f32],
            target_size: [
                self.config.target_width as f32,
                self.config.target_height as f32,
            ],
        };
        unsafe {
            self.context.UpdateSubresource(
                &self.scale_parameters,
                0,
                None,
                (&parameters as *const ScaleParameters).cast(),
                0,
                0,
            );
        }
    }

    fn draw(&self, source_view: &ID3D11ShaderResourceView) {
        let viewport = D3D11_VIEWPORT {
            Width: self.config.target_width as f32,
            Height: self.config.target_height as f32,
            MinDepth: 0.0,
            MaxDepth: 1.0,
            ..Default::default()
        };
        unsafe {
            self.context
                .OMSetRenderTargets(Some(&[Some(self.render_target.clone())]), None);
            self.context.RSSetViewports(Some(&[viewport]));
            self.context
                .IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            self.context.VSSetShader(&self.vertex_shader, None);
            self.context.PSSetShader(&self.pixel_shader, None);
            self.context
                .PSSetSamplers(0, Some(&[Some(self.sampler.clone())]));
            self.context
                .PSSetConstantBuffers(0, Some(&[Some(self.scale_parameters.clone())]));
            self.context
                .PSSetShaderResources(0, Some(&[Some(source_view.clone())]));
            self.context.Draw(3, 0);
        }
    }
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

    let mut copied_pixels = 0_u64;
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
        let region_pixels =
            (region.right - region.left) as u64 * (region.bottom - region.top) as u64;
        let Some(updated_pixels) = copied_pixels.checked_add(region_pixels) else {
            return false;
        };
        copied_pixels = updated_pixels;
        if copied_pixels.saturating_mul(100)
            >= source_pixels.saturating_mul(MAX_PARTIAL_COPY_PERCENT)
        {
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

    fn maybe_log(&mut self, copy_mode: CopyMode, width: u32, height: u32) {
        if copy_mode != CopyMode::Dirty || self.interval_started.elapsed() < COPY_STATS_INTERVAL {
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
    let entry = null_terminated(entry);
    let target = null_terminated(target);
    let mut code: Option<ID3DBlob> = None;
    let mut diagnostics: Option<ID3DBlob> = None;
    let result = unsafe {
        D3DCompile(
            SHADER.as_ptr().cast(),
            SHADER.len(),
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
            "D3D shader compilation failed: {detail}"
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

fn classify_windows_error(error: WindowsError) -> GpuRendererError {
    classify_hresult(error.code())
}

fn classify_hresult(code: windows::core::HRESULT) -> GpuRendererError {
    if code == DXGI_ERROR_ACCESS_LOST {
        GpuRendererError::AccessLost
    } else if code == DXGI_ERROR_DEVICE_REMOVED || code == DXGI_ERROR_DEVICE_RESET {
        GpuRendererError::DeviceLost
    } else {
        GpuRendererError::Failure(format!("DXGI/D3D11 call failed: {code:?}"))
    }
}

#[cfg(test)]
mod tests {
    use super::{CopyMode, MAX_COPY_REGIONS, ScaleParameters, compile_shader, select_copy_regions};
    use std::ffi::OsStr;
    use windows::Win32::Foundation::RECT;

    #[test]
    fn shaders_compile_with_the_system_d3d_compiler() {
        assert!(!compile_shader("vertex_main", "vs_5_0").unwrap().is_empty());
        assert!(!compile_shader("pixel_main", "ps_5_0").unwrap().is_empty());
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
        assert!(!select_copy_regions([], half_frame, 100, 100, &mut regions));

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
        assert_eq!(CopyMode::parse(None), Ok(CopyMode::Full));
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
