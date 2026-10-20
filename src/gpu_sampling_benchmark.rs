//! Test-only GPU timestamps around the production sampling pipeline.
use super::*;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CPU_ACCESS_READ, D3D11_MAP_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_QUERY,
    D3D11_QUERY_DATA_TIMESTAMP_DISJOINT, D3D11_QUERY_DESC, D3D11_QUERY_TIMESTAMP,
    D3D11_QUERY_TIMESTAMP_DISJOINT, D3D11_USAGE_STAGING, ID3D11Query,
};

pub(crate) struct NativeBenchmark {
    pub setup_ms: f64,
    pub warmup_ms: f64,
    pub frames_ms: Vec<f64>,
    pub adapter_name: String,
}

fn query(device: &ID3D11Device, kind: D3D11_QUERY) -> Result<ID3D11Query, String> {
    let mut result = None;
    unsafe {
        device.CreateQuery(
            &D3D11_QUERY_DESC {
                Query: kind,
                MiscFlags: 0,
            },
            Some(&mut result),
        )
    }
    .map_err(|e| e.to_string())?;
    result.ok_or("No timestamp query".into())
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn benchmark_sample_rgba(
    source: &[u8],
    sw: u32,
    sh: u32,
    tw: u32,
    th: u32,
    high_performance: bool,
    iterations: usize,
) -> Result<NativeBenchmark, String> {
    let setup = Instant::now();
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
    let input = TextureTarget::new(&device, sw, sh, DXGI_FORMAT_B8G8R8A8_UNORM)
        .map_err(|e| e.to_string())?;
    let output = TextureTarget::new(&device, tw, th, DXGI_FORMAT_B8G8R8A8_UNORM)
        .map_err(|e| e.to_string())?;
    if source.len() != sw as usize * sh as usize * 4 {
        return Err("Invalid benchmark RGBA buffer".into());
    }
    let mut bgra = source.to_vec();
    for pixel in bgra.chunks_exact_mut(4) {
        pixel.swap(0, 2);
    }
    unsafe {
        context.UpdateSubresource(&input.texture, 0, None, bgra.as_ptr().cast(), sw * 4, 0);
    }
    let mut staging_description = D3D11_TEXTURE2D_DESC::default();
    unsafe {
        output.texture.GetDesc(&mut staging_description);
    }
    staging_description.Usage = D3D11_USAGE_STAGING;
    staging_description.BindFlags = 0;
    staging_description.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    let mut staging = None;
    unsafe { device.CreateTexture2D(&staging_description, None, Some(&mut staging)) }
        .map_err(|e| e.to_string())?;
    let staging = staging.ok_or("No benchmark staging texture")?;
    let disjoint = query(&device, D3D11_QUERY_TIMESTAMP_DISJOINT)?;
    let start = query(&device, D3D11_QUERY_TIMESTAMP)?;
    let finish = query(&device, D3D11_QUERY_TIMESTAMP)?;
    let wait_gpu = || -> Result<(), String> {
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        unsafe {
            context.CopyResource(&staging, &output.texture);
            context
                .Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))
                .map_err(|e| e.to_string())?;
            context.Unmap(&staging, 0);
        }
        Ok(())
    };
    let setup_ms = setup.elapsed().as_secs_f64() * 1000.0;
    // Allocate intermediate textures, then run long enough for GPU clocks to
    // leave their idle state. Warmup is reported separately from setup/frames.
    let warmup = Instant::now();
    while warmup.elapsed() < Duration::from_secs(1) {
        sampling
            .render(&input.view, [sw, sh], &output.target, [tw, th])
            .map_err(|e| e.to_string())?;
        wait_gpu()?;
    }
    let warmup_ms = warmup.elapsed().as_secs_f64() * 1000.0;
    let mut frames_ms = Vec::with_capacity(iterations);
    for _ in 0..iterations {
        unsafe {
            context.Begin(&disjoint);
            context.End(&start);
        }
        sampling
            .render(&input.view, [sw, sh], &output.target, [tw, th])
            .map_err(|e| e.to_string())?;
        unsafe {
            context.End(&finish);
            context.End(&disjoint);
        }
        // Readback synchronization follows the measured interval. It does not
        // count toward GPU time and no pixels are copied to a CPU Vec.
        wait_gpu()?;
        let mut timing = D3D11_QUERY_DATA_TIMESTAMP_DISJOINT::default();
        let mut begin = 0_u64;
        let mut end = 0_u64;
        unsafe {
            context
                .GetData(
                    &disjoint,
                    Some((&mut timing as *mut D3D11_QUERY_DATA_TIMESTAMP_DISJOINT).cast()),
                    size_of::<D3D11_QUERY_DATA_TIMESTAMP_DISJOINT>() as u32,
                    0,
                )
                .map_err(|e| e.to_string())?;
            context
                .GetData(
                    &start,
                    Some((&mut begin as *mut u64).cast()),
                    size_of::<u64>() as u32,
                    0,
                )
                .map_err(|e| e.to_string())?;
            context
                .GetData(
                    &finish,
                    Some((&mut end as *mut u64).cast()),
                    size_of::<u64>() as u32,
                    0,
                )
                .map_err(|e| e.to_string())?;
        }
        if timing.Disjoint.as_bool() || timing.Frequency == 0 || end < begin {
            return Err("GPU timestamps were disjoint".into());
        }
        frames_ms.push((end - begin) as f64 / timing.Frequency as f64 * 1000.0);
    }
    Ok(NativeBenchmark {
        setup_ms,
        warmup_ms,
        frames_ms,
        adapter_name,
    })
}

#[test]
#[ignore = "creates a hidden native D3D11 swap chain and measures presentation deadlines"]
fn native_static_refresh_keeps_target_cadence() {
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DestroyWindow, WINDOW_EX_STYLE, WS_OVERLAPPEDWINDOW,
    };
    use windows::core::w;
    let hwnd = unsafe {
        CreateWindowExW(
            WINDOW_EX_STYLE(0),
            w!("STATIC"),
            w!("SBMS timing probe"),
            WS_OVERLAPPEDWINDOW,
            0,
            0,
            64,
            64,
            None,
            None,
            None,
            None,
        )
    }
    .unwrap();
    let result = (|| -> Result<(), GpuRendererError> {
        let factory: IDXGIFactory1 = unsafe { CreateDXGIFactory1() }
            .map_err(|e| classify_windows_error("CreateDXGIFactory1", e))?;
        let adapter = unsafe { factory.EnumAdapters1(0) }
            .map_err(|e| classify_windows_error("EnumAdapters1", e))?;
        let (device, context) = create_device(&adapter)?;
        let config = GpuRendererConfig {
            target_window: hwnd,
            target_width: 64,
            target_height: 64,
            source_rect: RECT {
                left: 0,
                top: 0,
                right: 64,
                bottom: 64,
            },
            rendering: RenderingOptions {
                high_performance_sampling: true,
                full_frame_copy: false,
                static_refresh: true,
            },
            target_refresh_millihz: 120_000,
        };
        let pipeline = Pipeline::new(config, &adapter, device, context)?;
        unsafe {
            pipeline.context.ClearRenderTargetView(
                &pipeline.cached_frame.as_ref().unwrap().target,
                &[0.2, 0.2, 0.2, 1.0],
            );
        }
        for hz in [60.0, 120.0] {
            let interval = Duration::from_secs_f64(1.0 / hz);
            let started = Instant::now();
            let mut last = started;
            let mut count = 0;
            while started.elapsed() < Duration::from_secs(2) {
                let wait = frame_wait_timeout(Some(last), interval, true);
                std::thread::sleep(Duration::from_millis(wait.into()));
                if last.elapsed() >= interval {
                    pipeline.present_cached(0)?;
                    last = advance_refresh_deadline(last, interval);
                    count += 1;
                }
            }
            let actual = count as f64 / started.elapsed().as_secs_f64();
            eprintln!(
                "hidden native swap-chain static target={hz:.1}Hz actual={actual:.2}Hz, frames={count}"
            );
            assert!(
                actual > hz * 0.85 && actual < hz * 1.10,
                "static deadline cadence {actual}"
            );
        }
        Ok(())
    })();
    unsafe {
        DestroyWindow(hwnd).unwrap();
    }
    result.unwrap();
}

/// Sequential changed-frame acceptance, including the new persistent-output copy.
#[test]
#[ignore = "requires actual D3D11 hardware and the real GDI text fixture"]
fn native_partial_sampling_scroll() {
    use serde_json::json;
    use std::{fs, path::Path};
    let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("work/partial-sampling");
    fs::create_dir_all(&dir).unwrap();
    let fixture = fs::read(
        Path::new(env!("CARGO_MANIFEST_DIR")).join("work/defringe-perf/live-ratio/input.rgba"),
    )
    .unwrap();
    assert_eq!(fixture.len(), 4480 * 2520 * 4);
    let factory: IDXGIFactory1 = unsafe { CreateDXGIFactory1() }.unwrap();
    let adapter = unsafe { factory.EnumAdapters1(0) }.unwrap();
    let description = unsafe { adapter.GetDesc1() }.unwrap();
    let len = description
        .Description
        .iter()
        .position(|&v| v == 0)
        .unwrap();
    let adapter_name = String::from_utf16_lossy(&description.Description[..len]);
    let (device, context) = create_device(&adapter).unwrap();
    let mut full = SamplingPipeline::new(device.clone(), context.clone(), false).unwrap();
    let mut partial = SamplingPipeline::new(device.clone(), context.clone(), false).unwrap();
    let disjoint = query(&device, D3D11_QUERY_TIMESTAMP_DISJOINT).unwrap();
    let stamps: Vec<_> = (0..3)
        .map(|_| query(&device, D3D11_QUERY_TIMESTAMP).unwrap())
        .collect();
    let mut results = Vec::new();
    // Reuse pipelines across mode changes to exercise invalidated intermediates.
    let cases = [
        ("small-window", 4480, 2520, 2560, 1440, 0),
        ("quarter-screen", 4480, 2520, 2560, 1440, 1),
        ("half-screen", 4480, 2520, 2560, 1440, 2),
        ("full-screen", 4480, 2520, 2560, 1440, 3),
        ("identity-edge-multiple", 640, 360, 640, 360, 4),
        ("enlarge-edge-multiple", 640, 360, 1024, 576, 4),
        ("anisotropic-edge-multiple", 640, 360, 480, 540, 4),
        ("target-transitions", 640, 360, 480, 270, 5),
    ];
    let mut previous_size = None;
    for (id, sw, sh, tw, th, layout) in cases {
        let input = TextureTarget::new(&device, sw, sh, DXGI_FORMAT_B8G8R8A8_UNORM).unwrap();
        let expected = TextureTarget::new(&device, tw, th, DXGI_FORMAT_B8G8R8A8_UNORM).unwrap();
        let actual = TextureTarget::new(&device, tw, th, DXGI_FORMAT_B8G8R8A8_UNORM).unwrap();
        let present = TextureTarget::new(&device, tw, th, DXGI_FORMAT_B8G8R8A8_UNORM).unwrap();
        let mut desc = D3D11_TEXTURE2D_DESC::default();
        unsafe { actual.texture.GetDesc(&mut desc) };
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
        let mut staging = None;
        unsafe { device.CreateTexture2D(&desc, None, Some(&mut staging)) }.unwrap();
        let staging = staging.unwrap();
        let read = |texture: &ID3D11Texture2D| {
            let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
            unsafe {
                context.CopyResource(&staging, texture);
                context
                    .Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))
                    .unwrap();
            }
            let mut pixels = Vec::with_capacity((tw * th * 4) as usize);
            for y in 0..th as usize {
                pixels.extend_from_slice(unsafe {
                    slice::from_raw_parts(
                        mapped.pData.cast::<u8>().add(y * mapped.RowPitch as usize),
                        tw as usize * 4,
                    )
                });
            }
            unsafe { context.Unmap(&staging, 0) };
            pixels
        };
        let mut source = vec![0; (sw * sh * 4) as usize];
        for y in 0..sh as usize {
            source[y * sw as usize * 4..(y + 1) * sw as usize * 4]
                .copy_from_slice(&fixture[y * 4480 * 4..y * 4480 * 4 + sw as usize * 4]);
        }
        for p in source.chunks_exact_mut(4) {
            p.swap(0, 2);
        }
        let base = source.clone();
        unsafe {
            context.UpdateSubresource(&input.texture, 0, None, source.as_ptr().cast(), sw * 4, 0)
        };
        // Even an empty invalidation must initialize newly sized resources.
        full.render(&input.view, [sw, sh], &expected.target, [tw, th])
            .unwrap();
        // New target with unchanged size requires the documented explicit initialization.
        let initialization_dirty = if previous_size == Some((sw, sh, tw, th)) {
            None
        } else {
            Some(&[][..])
        };
        partial
            .render_regions(
                &input.view,
                [sw, sh],
                &actual.target,
                [tw, th],
                initialization_dirty,
            )
            .unwrap();
        previous_size = Some((sw, sh, tw, th));
        assert_eq!(
            read(&expected.texture),
            read(&actual.texture),
            "{id} initialization"
        );
        let rect = |l, t, r, b| RECT {
            left: l,
            top: t,
            right: r,
            bottom: b,
        };
        let regions = match layout {
            0 => vec![rect(400, 300, 1520, 930)],
            1 => vec![rect(0, 0, sw as i32 / 2, sh as i32 / 2)],
            2 => vec![rect(0, 0, sw as i32 / 2, sh as i32)],
            3 => vec![rect(0, 0, sw as i32, sh as i32)],
            _ => vec![
                rect(0, 0, 119, 97),
                rect(sw as i32 - 137, sh as i32 - 111, sw as i32, sh as i32),
                rect(103, 83, 231, 157),
            ],
        };
        let warmup = Instant::now();
        while warmup.elapsed() < Duration::from_secs(1) {
            full.render(&input.view, [sw, sh], &expected.target, [tw, th])
                .unwrap();
            partial
                .render_regions(
                    &input.view,
                    [sw, sh],
                    &actual.target,
                    [tw, th],
                    Some(&regions),
                )
                .unwrap();
            read(&actual.texture);
        }
        let mut times = Vec::new();
        let mut cache_valid = true;
        for frame in 0..24 {
            // The final frame is static; earlier frames scroll each region independently.
            let static_frame = frame == 23 || (layout == 5 && frame % 6 == 3);
            let direct_full =
                !static_frame && (layout == 3 || (layout == 5 && matches!(frame % 6, 0 | 2)));
            let dirty: &[RECT] = if static_frame { &[] } else { &regions };
            if !static_frame {
                for r in &regions {
                    let height = (r.bottom - r.top) as usize;
                    for y in r.top as usize..r.bottom as usize {
                        let sy = r.top as usize + (y - r.top as usize + (frame + 1) * 7) % height;
                        let start = (y * sw as usize + r.left as usize) * 4;
                        let src = (sy * sw as usize + r.left as usize) * 4;
                        let width = (r.right - r.left) as usize * 4;
                        source[start..start + width].copy_from_slice(&base[src..src + width]);
                    }
                }
            }
            let full_first = frame % 2 == 0;
            let cache_rebuilt = !direct_full && !cache_valid;
            // Submit and complete each method separately. Upload the identical source
            // before each interval to give both methods the same source preparation.
            let mut measure = |new_path: bool| {
                unsafe {
                    context.UpdateSubresource(
                        &input.texture,
                        0,
                        None,
                        source.as_ptr().cast(),
                        sw * 4,
                        0,
                    );
                    context.Begin(&disjoint);
                    context.End(&stamps[0]);
                }
                if new_path {
                    if direct_full {
                        partial
                            .render_regions(&input.view, [sw, sh], &present.target, [tw, th], None)
                            .unwrap();
                        cache_valid = false;
                    } else {
                        partial
                            .render_regions(
                                &input.view,
                                [sw, sh],
                                &actual.target,
                                [tw, th],
                                if cache_valid { Some(dirty) } else { None },
                            )
                            .unwrap();
                        cache_valid = true;
                    }
                } else {
                    full.render(&input.view, [sw, sh], &expected.target, [tw, th])
                        .unwrap();
                }
                unsafe {
                    context.End(&stamps[1]);
                    if new_path && !direct_full {
                        context.CopyResource(&present.texture, &actual.texture);
                    }
                    context.End(&stamps[2]);
                    context.End(&disjoint);
                }
                // Map waits for this method to finish; it lies outside the interval.
                let pixels = read(if new_path {
                    &present.texture
                } else {
                    &expected.texture
                });
                let mut timing = D3D11_QUERY_DATA_TIMESTAMP_DISJOINT::default();
                unsafe {
                    context.GetData(
                        &disjoint,
                        Some((&mut timing as *mut D3D11_QUERY_DATA_TIMESTAMP_DISJOINT).cast()),
                        size_of::<D3D11_QUERY_DATA_TIMESTAMP_DISJOINT>() as u32,
                        0,
                    )
                }
                .unwrap();
                assert!(!timing.Disjoint.as_bool() && timing.Frequency > 0);
                let mut values = [0_u64; 3];
                for (stamp, value) in stamps.iter().zip(&mut values) {
                    unsafe {
                        context.GetData(
                            stamp,
                            Some((value as *mut u64).cast()),
                            size_of::<u64>() as u32,
                            0,
                        )
                    }
                    .unwrap();
                }
                let ms =
                    |i: usize| (values[i] - values[0]) as f64 / timing.Frequency as f64 * 1000.;
                (ms(1), ms(2), pixels)
            };
            let (old_measure, new_measure) = if full_first {
                let old = measure(false);
                (old, measure(true))
            } else {
                let new = measure(true);
                (measure(false), new)
            };
            let (full_ms, _, expected_pixels) = old_measure;
            let (partial_ms, partial_with_copy_ms, actual_pixels) = new_measure;
            let differences = if expected_pixels == actual_pixels {
                0
            } else {
                expected_pixels
                    .iter()
                    .zip(&actual_pixels)
                    .filter(|(a, b)| a != b)
                    .count()
            };
            if differences != 0 {
                fs::write(
                    dir.join(format!("{id}-{frame}-expected.bgra")),
                    &expected_pixels,
                )
                .unwrap();
                fs::write(
                    dir.join(format!("{id}-{frame}-actual.bgra")),
                    &actual_pixels,
                )
                .unwrap();
            }
            assert_eq!(
                differences, 0,
                "{id} frame {frame}: partial differs from full"
            );
            times.push(json!({"frame":frame,"static":dirty.is_empty(),"direct_full":direct_full,"cache_rebuilt":cache_rebuilt,"full_first":full_first,"full_ms":full_ms,"partial_ms":partial_ms,"partial_with_copy_ms":partial_with_copy_ms,"different_bytes":differences}));
            if frame == 12 {
                fs::write(dir.join(format!("{id}-output.bgra")), &actual_pixels).unwrap();
                fs::write(dir.join(format!("{id}-expected.bgra")), &expected_pixels).unwrap();
            }
        }
        let median = |key: &str| {
            let mut values: Vec<f64> = times[..23]
                .iter()
                .map(|v| v[key].as_f64().unwrap())
                .collect();
            values.sort_by(f64::total_cmp);
            values[values.len() / 2]
        };
        let fm = median("full_ms");
        let pm = median("partial_ms");
        let cm = median("partial_with_copy_ms");
        eprintln!(
            "{id}: full {fm:.4} ms, partial {pm:.4} ms, with copy {cm:.4} ms, saving {:.1}%",
            100. * (1. - cm / fm)
        );
        results.push(json!({"id":id,"source":[sw,sh],"target":[tw,th],"full_median_ms":fm,"partial_median_ms":pm,"partial_with_copy_median_ms":cm,"saving_percent":100.*(1.-cm/fm),"frames":times}));
        fs::write(dir.join("results.json"),serde_json::to_vec_pretty(&json!({"adapter":adapter_name,"scope":"Production D3D11 pipeline, sequential scrolling real GDI text fixture, byte-exact comparison every frame. GPU timestamps exclude input upload and readback. Each method is separately submitted and completed by readback, with identical source re-uploaded before each interval; method order alternates. New path includes extra persistent-output copy only for partial updates; full-frame updates render directly to output. Old path excludes the copy. Target transitions exercise direct-full to cached-partial/empty restoration. Configured 240Hz is not an observed frame rate.","results":results})).unwrap()).unwrap();
    }
}
