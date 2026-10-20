//! Native D3D11 implementation of the finalized sampling experiment.
//!
//! Source cleanup is stored as UNORM8, matching the experiment. Separable
//! Lanczos uses float32 between axes: clipping or UNORM there would destroy
//! the signed kernel. All textures are reused until the source mode changes.

use super::*;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_RENDER_TARGET, D3D11_CULL_BACK, D3D11_FILL_SOLID, D3D11_RASTERIZER_DESC,
    ID3D11RasterizerState,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT, DXGI_FORMAT_R32G32B32A32_FLOAT};

#[path = "gpu_sampling/regions.rs"]
mod regions;

const QUALITY: &str = include_str!("sampling_quality.hlsl");
const DEFRINGE: &str = include_str!("sampling_defringe.hlsl");
const EWA_LUT: &str = include_str!("sampling_ewa_lut.hlsl");

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum SamplingPath {
    Legacy,
    Identity,
    Reduction,
    Enlargement,
}

fn path(source: [u32; 2], target: [u32; 2], legacy: bool) -> SamplingPath {
    if legacy {
        SamplingPath::Legacy
    } else if source == target {
        SamplingPath::Identity
    } else if source[0] > target[0] || source[1] > target[1] {
        SamplingPath::Reduction
    } else {
        SamplingPath::Enlargement
    }
}

pub(super) struct TextureTarget {
    pub texture: ID3D11Texture2D,
    pub view: ID3D11ShaderResourceView,
    pub target: ID3D11RenderTargetView,
}

impl TextureTarget {
    pub(super) fn new(
        device: &ID3D11Device,
        width: u32,
        height: u32,
        format: DXGI_FORMAT,
    ) -> Result<Self, GpuRendererError> {
        let description = D3D11_TEXTURE2D_DESC {
            Width: width,
            Height: height,
            MipLevels: 1,
            ArraySize: 1,
            Format: format,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: (D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET).0 as u32,
            ..Default::default()
        };
        let mut texture = None;
        unsafe { device.CreateTexture2D(&description, None, Some(&mut texture)) }
            .map_err(|e| classify_windows_error("CreateTexture2D(sampling)", e))?;
        let texture =
            texture.ok_or_else(|| GpuRendererError::Failure("No sampling texture".into()))?;
        let mut view = None;
        let mut target = None;
        unsafe {
            device
                .CreateShaderResourceView(&texture, None, Some(&mut view))
                .map_err(|e| classify_windows_error("CreateShaderResourceView(sampling)", e))?;
            device
                .CreateRenderTargetView(&texture, None, Some(&mut target))
                .map_err(|e| classify_windows_error("CreateRenderTargetView(sampling)", e))?;
        }
        Ok(Self {
            texture,
            view: view.ok_or_else(|| GpuRendererError::Failure("No sampling SRV".into()))?,
            target: target.ok_or_else(|| GpuRendererError::Failure("No sampling RTV".into()))?,
        })
    }
}

struct QualityShaders {
    defringe: ID3D11PixelShader,
    horizontal: ID3D11PixelShader,
    vertical: ID3D11PixelShader,
    upscale: ID3D11PixelShader,
}

pub(super) struct SamplingPipeline {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    vertex: ID3D11VertexShader,
    legacy: ID3D11PixelShader,
    sampler: ID3D11SamplerState,
    parameters: ID3D11Buffer,
    scissor: ID3D11RasterizerState,
    quality: Option<QualityShaders>,
    size: Option<([u32; 2], [u32; 2])>,
    pub(super) clean: Option<TextureTarget>,
    horizontal: Option<TextureTarget>,
}

fn pixel_shader(device: &ID3D11Device, code: &[u8]) -> Result<ID3D11PixelShader, GpuRendererError> {
    let mut shader = None;
    unsafe { device.CreatePixelShader(code, None, Some(&mut shader)) }
        .map_err(|e| classify_windows_error("CreatePixelShader(sampling)", e))?;
    shader.ok_or_else(|| GpuRendererError::Failure("No sampling pixel shader".into()))
}

// Bytecode is immutable and shareable across adapter devices and render groups.
// D3D shaders/resources remain device-local. Compilation happens once per process.
fn quality_bytecode() -> Result<&'static [Vec<u8>; 4], GpuRendererError> {
    static CODE: std::sync::OnceLock<Result<[Vec<u8>; 4], GpuRendererError>> =
        std::sync::OnceLock::new();
    CODE.get_or_init(|| {
        let source = format!("{EWA_LUT}\n{QUALITY}\n{DEFRINGE}");
        Ok([
            compile_shader_source(&source, "defringe_main", "ps_5_0")?,
            compile_shader_source(&source, "horizontal_main", "ps_5_0")?,
            compile_shader_source(&source, "vertical_main", "ps_5_0")?,
            compile_shader_source(&source, "upscale_main", "ps_5_0")?,
        ])
    })
    .as_ref()
    .map_err(Clone::clone)
}

impl SamplingPipeline {
    pub(super) fn new(
        device: ID3D11Device,
        context: ID3D11DeviceContext,
        high_performance: bool,
    ) -> Result<Self, GpuRendererError> {
        let vertex_code = compile_shader("vertex_main", "vs_5_0")?;
        let mut vertex = None;
        unsafe { device.CreateVertexShader(&vertex_code, None, Some(&mut vertex)) }
            .map_err(|e| classify_windows_error("CreateVertexShader(sampling)", e))?;
        let legacy = pixel_shader(&device, &compile_shader("pixel_main", "ps_5_0")?)?;
        let mut sampler = None;
        unsafe {
            device.CreateSamplerState(
                &D3D11_SAMPLER_DESC {
                    Filter: D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                    AddressU: D3D11_TEXTURE_ADDRESS_CLAMP,
                    AddressV: D3D11_TEXTURE_ADDRESS_CLAMP,
                    AddressW: D3D11_TEXTURE_ADDRESS_CLAMP,
                    MaxLOD: f32::MAX,
                    ..Default::default()
                },
                Some(&mut sampler),
            )
        }
        .map_err(|e| classify_windows_error("CreateSamplerState(sampling)", e))?;
        let mut parameters = None;
        unsafe {
            device.CreateBuffer(
                &D3D11_BUFFER_DESC {
                    ByteWidth: size_of::<ScaleParameters>() as u32,
                    Usage: D3D11_USAGE_DEFAULT,
                    BindFlags: D3D11_BIND_CONSTANT_BUFFER.0 as u32,
                    ..Default::default()
                },
                None,
                Some(&mut parameters),
            )
        }
        .map_err(|e| classify_windows_error("CreateBuffer(sampling)", e))?;
        let quality = if high_performance {
            None
        } else {
            let code = quality_bytecode()?;
            Some(QualityShaders {
                defringe: pixel_shader(&device, &code[0])?,
                horizontal: pixel_shader(&device, &code[1])?,
                vertical: pixel_shader(&device, &code[2])?,
                upscale: pixel_shader(&device, &code[3])?,
            })
        };
        let mut scissor = None;
        unsafe {
            device.CreateRasterizerState(
                &D3D11_RASTERIZER_DESC {
                    FillMode: D3D11_FILL_SOLID,
                    CullMode: D3D11_CULL_BACK,
                    DepthClipEnable: true.into(),
                    ScissorEnable: true.into(),
                    ..Default::default()
                },
                Some(&mut scissor),
            )
        }
        .map_err(|e| classify_windows_error("CreateRasterizerState(sampling)", e))?;
        Ok(Self {
            device,
            context,
            legacy,
            quality,
            vertex: vertex.ok_or_else(|| GpuRendererError::Failure("No vertex shader".into()))?,
            sampler: sampler.ok_or_else(|| GpuRendererError::Failure("No sampler".into()))?,
            parameters: parameters
                .ok_or_else(|| GpuRendererError::Failure("No parameters".into()))?,
            scissor: scissor
                .ok_or_else(|| GpuRendererError::Failure("No sampling rasterizer".into()))?,
            size: None,
            clean: None,
            horizontal: None,
        })
    }

    #[cfg(test)]
    pub(super) fn render(
        &mut self,
        source: &ID3D11ShaderResourceView,
        source_size: [u32; 2],
        target: &ID3D11RenderTargetView,
        target_size: [u32; 2],
    ) -> Result<(), GpuRendererError> {
        self.render_regions(source, source_size, target, target_size, None)
    }

    /// Update a persistent target from source-space dirty rectangles.
    /// None redraws everything; an empty slice leaves the initialized target intact.
    /// When replacing the target resource without resizing, pass None to initialize it.
    pub(super) fn render_regions(
        &mut self,
        source: &ID3D11ShaderResourceView,
        source_size: [u32; 2],
        target: &ID3D11RenderTargetView,
        target_size: [u32; 2],
        dirty: Option<&[RECT]>,
    ) -> Result<(), GpuRendererError> {
        let selected = path(source_size, target_size, self.quality.is_none());
        let resized = self.size != Some((source_size, target_size));
        if resized {
            self.clean = None;
            self.horizontal = None;
            if matches!(
                selected,
                SamplingPath::Reduction | SamplingPath::Enlargement
            ) {
                self.clean = Some(TextureTarget::new(
                    &self.device,
                    source_size[0],
                    source_size[1],
                    DXGI_FORMAT_B8G8R8A8_UNORM,
                )?);
                if selected == SamplingPath::Reduction {
                    self.horizontal = Some(TextureTarget::new(
                        &self.device,
                        target_size[0],
                        source_size[1],
                        DXGI_FORMAT_R32G32B32A32_FLOAT,
                    )?);
                }
            }
            self.size = Some((source_size, target_size));
        }
        let source_regions = match dirty.filter(|_| !resized) {
            Some(rects) => regions::merge(rects.iter().copied(), source_size),
            None => vec![regions::full(source_size)],
        };
        if source_regions.is_empty() {
            return Ok(());
        }
        // Save all rasterizer state touched here. The viewport always covers
        // the whole pass, so dirty updates have exactly the same sampling phase.
        let previous_state = unsafe { self.context.RSGetState() }.ok();
        let mut previous_viewports = [D3D11_VIEWPORT::default(); 16];
        let mut viewport_count = previous_viewports.len() as u32;
        let mut previous_scissors = [RECT::default(); 16];
        let mut scissor_count = previous_scissors.len() as u32;
        unsafe {
            self.context
                .RSGetViewports(&mut viewport_count, Some(previous_viewports.as_mut_ptr()));
            self.context
                .RSGetScissorRects(&mut scissor_count, Some(previous_scissors.as_mut_ptr()));
            self.context.RSSetState(&self.scissor);
        }
        let values = ScaleParameters {
            source_size: [source_size[0] as f32, source_size[1] as f32],
            target_size: [target_size[0] as f32, target_size[1] as f32],
        };
        unsafe {
            self.context.UpdateSubresource(
                &self.parameters,
                0,
                None,
                (&values as *const ScaleParameters).cast(),
                0,
                0,
            );
        }
        if selected == SamplingPath::Legacy {
            // Legacy area+fringe (<=2x) and bilinear both fit this source halo.
            let output = regions::resample(
                &source_regions,
                source_size,
                target_size,
                [Some(2.0), Some(2.0)],
            );
            self.draw(source, target, target_size, &self.legacy, &output);
        } else {
            let shaders = self.quality.as_ref().unwrap();
            let cleaned = regions::cleanup(&source_regions, source_size);
            if selected == SamplingPath::Identity {
                self.draw(source, target, target_size, &shaders.defringe, &cleaned);
            } else {
                let clean = self.clean.as_ref().unwrap();
                self.draw(
                    source,
                    &clean.target,
                    source_size,
                    &shaders.defringe,
                    &cleaned,
                );
                if selected == SamplingPath::Reduction {
                    let horizontal = self.horizontal.as_ref().unwrap();
                    let intermediate_size = [target_size[0], source_size[1]];
                    let horizontal_regions = regions::resample(
                        &cleaned,
                        source_size,
                        intermediate_size,
                        [
                            Some(3.0 * (source_size[0] as f64 / target_size[0] as f64).max(1.0)),
                            None,
                        ],
                    );
                    let output = regions::resample(
                        &horizontal_regions,
                        intermediate_size,
                        target_size,
                        [
                            None,
                            Some(3.0 * (source_size[1] as f64 / target_size[1] as f64).max(1.0)),
                        ],
                    );
                    self.draw(
                        &clean.view,
                        &horizontal.target,
                        [target_size[0], source_size[1]],
                        &shaders.horizontal,
                        &horizontal_regions,
                    );
                    self.draw(
                        &horizontal.view,
                        target,
                        target_size,
                        &shaders.vertical,
                        &output,
                    );
                } else {
                    let output = regions::resample(
                        &cleaned,
                        source_size,
                        target_size,
                        [Some(4.0), Some(4.0)],
                    );
                    self.draw(&clean.view, target, target_size, &shaders.upscale, &output);
                }
            }
        }
        unsafe {
            self.context.RSSetState(previous_state.as_ref());
            self.context
                .RSSetViewports(Some(&previous_viewports[..viewport_count as usize]));
            self.context
                .RSSetScissorRects(Some(&previous_scissors[..scissor_count as usize]));
        }
        Ok(())
    }

    fn draw(
        &self,
        source: &ID3D11ShaderResourceView,
        target: &ID3D11RenderTargetView,
        size: [u32; 2],
        shader: &ID3D11PixelShader,
        regions: &[RECT],
    ) {
        unsafe {
            self.context
                .OMSetRenderTargets(Some(&[Some(target.clone())]), None);
            self.context.RSSetViewports(Some(&[D3D11_VIEWPORT {
                Width: size[0] as f32,
                Height: size[1] as f32,
                MinDepth: 0.0,
                MaxDepth: 1.0,
                ..Default::default()
            }]));
            self.context
                .IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            self.context.VSSetShader(&self.vertex, None);
            self.context.PSSetShader(shader, None);
            self.context
                .PSSetSamplers(0, Some(&[Some(self.sampler.clone())]));
            self.context
                .PSSetConstantBuffers(0, Some(&[Some(self.parameters.clone())]));
            self.context
                .PSSetShaderResources(0, Some(&[Some(source.clone())]));
            for rect in regions {
                self.context
                    .RSSetScissorRects(Some(std::slice::from_ref(rect)));
                self.context.Draw(3, 0);
            }
            self.context.PSSetShaderResources(0, Some(&[None]));
            self.context.OMSetRenderTargets(None, None);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quality_shaders_compile_with_native_compiler() {
        use windows::Win32::Graphics::Direct3D::Fxc::D3DDisassemble;
        for code in quality_bytecode().unwrap() {
            assert!(!code.is_empty());
            let disassembly =
                unsafe { D3DDisassemble(code.as_ptr().cast(), code.len(), 0, PCSTR::null()) }
                    .unwrap();
            // The pipeline binds only ScaleParameters (b0). In HLSL, a global
            // `const` without `static` silently becomes an unbound uniform.
            assert!(
                !blob_text(&disassembly).contains("$Globals"),
                "sampling shader unexpectedly requires an implicit global constant buffer"
            );
        }
    }

    #[test]
    fn sampling_selects_single_axis_and_large_reductions() {
        assert_eq!(
            path([3840, 2160], [3840, 2160], false),
            SamplingPath::Identity
        );
        assert_eq!(path([3840, 2160], [3840, 2160], true), SamplingPath::Legacy);
        assert_eq!(
            path([3840, 1080], [1920, 2160], false),
            SamplingPath::Reduction
        );
        assert_eq!(
            path([16384, 16384], [1024, 1024], false),
            SamplingPath::Reduction
        );
        assert_eq!(
            path([1920, 1080], [3840, 1080], false),
            SamplingPath::Enlargement
        );
    }
}
