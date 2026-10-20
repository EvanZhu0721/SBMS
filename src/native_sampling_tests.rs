//! Opt-in hardware acceptance: generated public fixtures, no desktop capture.
//! Prepare: python work/native-sampling/prepare-inputs.py
//!          node work/native-sampling/browser-reference.cjs
//! Run: cargo test native_sampling_matches_browser -- --ignored --nocapture
use super::{benchmark_sample_rgba, verify_sample_rgba};
use serde_json::{Value, json};
use std::{fs, path::Path};

fn difference(actual: &[u8], expected: &[u8]) -> Value {
    assert_eq!(actual.len(), expected.len());
    let mut histogram = [0_u64; 256];
    let mut maximum = 0;
    let mut total = 0_u64;
    let mut worst_byte = 0;
    for (i, (&a, &b)) in actual.iter().zip(expected).enumerate() {
        let d = a.abs_diff(b) as usize;
        histogram[d] += 1;
        total += d as u64;
        if d > maximum {
            maximum = d;
            worst_byte = i;
        }
    }
    json!({
        "max": maximum, "mean": total as f64 / actual.len() as f64,
        "bytes_over_2": histogram[3..].iter().sum::<u64>(),
        "worst_byte": worst_byte,
        "histogram": histogram.iter().enumerate().filter(|(_, n)| **n != 0)
            .map(|(d, n)| (d.to_string(), json!(n))).collect::<serde_json::Map<_, _>>()
    })
}

#[test]
#[ignore = "requires generated reference fixtures and actual D3D11 hardware"]
fn native_sampling_matches_browser() {
    let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("work/native-sampling");
    let manifest: Value =
        serde_json::from_slice(&fs::read(dir.join("inputs.json")).unwrap()).unwrap();
    let mut results = Vec::new();
    let mut failures = Vec::new();
    for case in manifest["cases"].as_array().unwrap() {
        let id = case["id"].as_str().unwrap();
        let number = |field: &str| case[field].as_u64().unwrap() as u32;
        let (sw, sh, tw, th) = (number("sw"), number("sh"), number("tw"), number("th"));
        let source = fs::read(dir.join(case["input"].as_str().unwrap())).unwrap();
        for (name, legacy) in [("quality", false), ("legacy", true)] {
            let output = verify_sample_rgba(&source, sw, sh, tw, th, legacy).unwrap();
            fs::write(dir.join(format!("{id}.native-{name}.rgba")), &output.rgba).unwrap();
            let reference = fs::read(dir.join(format!("{id}.reference-{name}.rgba"))).unwrap();
            let diff = difference(&output.rgba, &reference);
            if diff["max"].as_u64().unwrap() > 2 {
                failures.push(format!("{id}/{name}: output {diff}"));
            }
            let mut cleaned_diff = Value::Null;
            if let Some(cleaned) = output.cleaned_rgba {
                fs::write(dir.join(format!("{id}.native-cleaned.rgba")), &cleaned).unwrap();
                let expected = fs::read(dir.join(format!("{id}.reference-cleaned.rgba"))).unwrap();
                cleaned_diff = difference(&cleaned, &expected);
                if cleaned_diff["max"].as_u64().unwrap() > 2 {
                    failures.push(format!("{id}/{name}: cleanup {cleaned_diff}"));
                }
            }
            if legacy
                && case["checks"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .any(|s| s == "identity")
                && output.rgba != source
            {
                failures.push(format!("{id}/{name}: 1:1 changed source pixels"));
            }
            if case["checks"]
                .as_array()
                .unwrap()
                .iter()
                .any(|s| s == "achromatic")
                && output
                    .rgba
                    .chunks_exact(4)
                    .any(|p| p[0] != p[1] || p[1] != p[2])
            {
                failures.push(format!("{id}/{name}: introduced color into grayscale"));
            }
            eprintln!(
                "{id}/{name} max={} mean={} wall={:.3} ms",
                diff["max"],
                diff["mean"],
                output.elapsed.as_secs_f64() * 1000.
            );
            results.push(json!({
                "id": id, "mode": name, "source": [sw, sh], "target": [tw, th],
                "adapter": output.adapter_name, "output": diff, "cleanup": cleaned_diff,
                "cpu_wall_ms": output.elapsed.as_secs_f64() * 1000.,
            }));
        }
    }
    fs::write(dir.join("native-verification.json"), serde_json::to_vec_pretty(&json!({
        "scope": "Actual production D3D11 SamplingPipeline output vs WebGL experiment. Full byte comparison.",
        "tolerance": "At most 2/255 per channel for independent GPU arithmetic and intermediate precision.",
        "timing_scope": "CPU wall includes offscreen resources, submit, GPU completion and readback; excludes shader/device setup. Not GPU-only time or frame-rate benchmark.",
        "results": results, "failures": failures,
    })).unwrap()).unwrap();
    assert!(
        failures.is_empty(),
        "{} native parity failures; see work/native-sampling/native-verification.json",
        failures.len()
    );
}

#[test]
#[ignore = "requires full-frame public fixtures and D3D11 GPU timestamp queries"]
fn native_sampling_full_frame_benchmark() {
    let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("work/native-sampling");
    let manifest: Value =
        serde_json::from_slice(&fs::read(dir.join("benchmark-inputs.json")).unwrap()).unwrap();
    let mut results = Vec::new();
    for case in manifest["cases"].as_array().unwrap() {
        let id = case["id"].as_str().unwrap();
        let number = |field: &str| case[field].as_u64().unwrap() as u32;
        let (sw, sh, tw, th) = (number("sw"), number("sh"), number("tw"), number("th"));
        let source = fs::read(dir.join(case["input"].as_str().unwrap())).unwrap();
        for (name, legacy) in [("quality", false), ("legacy", true)] {
            let output = benchmark_sample_rgba(&source, sw, sh, tw, th, legacy, 60).unwrap();
            assert_eq!(output.frames_ms.len(), 60);
            assert!(output.frames_ms.iter().all(|t| t.is_finite() && *t > 0.));
            let mut sorted = output.frames_ms.clone();
            sorted.sort_by(f64::total_cmp);
            let median = (sorted[29] + sorted[30]) * 0.5;
            eprintln!(
                "{id}/{name}: GPU median {median:.3} ms; setup {:.3} ms",
                output.setup_ms
            );
            results.push(json!({
                "id": id, "mode": name, "source": [sw, sh], "target": [tw, th],
                "adapter": output.adapter_name, "setup_ms": output.setup_ms,
                "warmup_ms": output.warmup_ms,
                "gpu_frames_ms": output.frames_ms, "gpu_median_ms": median,
                "gpu_min_ms": sorted[0], "gpu_max_ms": sorted[59],
            }));
        }
    }
    fs::write(
        dir.join("native-benchmark.json"),
        serde_json::to_vec_pretty(&json!({
            "scope": "Production D3D11 SamplingPipeline, 60 measured frames per mode after at least one second of warmup. Same real-DPI GDI text pixels for both modes.",
            "timing_scope": "D3D11 GPU timestamps surround render; exclude source upload, staging copy/readback, desktop duplication and presentation. Setup includes initial device/shader preparation; first quality setup in this fresh test process includes shader compile, later quality setup reuses cached bytecode.",
            "results": results,
        })).unwrap(),
    ).unwrap();
}

#[test]
#[ignore = "requires strategy comparison fixtures and actual D3D11 hardware"]
fn native_sizing_strategy_comparison() {
    use crate::geometry::{
        DisplayGeometry, PhysicalMeasurement, PixelSize, Rotation, SizingRequest, SizingStrategy,
    };
    let fixture_dir = std::env::var("SBMS_STRATEGY_FIXTURES")
        .unwrap_or_else(|_| "work/strategy-comparison".into());
    let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join(fixture_dir);
    let manifest: Value =
        serde_json::from_slice(&fs::read(dir.join("inputs.json")).unwrap()).unwrap();
    let cases = manifest["cases"].as_array().unwrap();
    let mut images = Vec::new();
    let mut results = Vec::new();
    let save = |images: &[Value], results: &[Value]| {
        fs::write(
            dir.join("results.json"),
            serde_json::to_vec_pretty(&json!({
                "scope": "Production quality SamplingPipeline; native output pixels and three interleaved rounds of 60 measured frames per benchmark case.",
                "timing_scope": "D3D11 GPU timestamps cover sampling and adaptive defringe only. Excludes application drawing, source upload, capture, readback, encoding and Present. Reciprocal GPU time is not end-to-end FPS.",
                "warmup": "Each benchmark call performs at least one second of warmup before measuring.",
                "p95_method": "Nearest rank: sorted frame 57 of 60.",
                "images": images,
                "results": results,
            }))
            .unwrap(),
        )
        .unwrap();
    };
    for case in cases {
        let id = case["id"].as_str().unwrap();
        let number = |field: &str| case[field].as_u64().unwrap() as u32;
        let (sw, sh, tw, th) = (number("sw"), number("sh"), number("tw"), number("th"));
        if case["strategy"] != "reference" {
            let display = |width, height, inches: f64| DisplayGeometry {
                native_pixels: PixelSize { width, height },
                physical: PhysicalMeasurement::DiagonalMm(inches * 25.4),
                aspect_ratio: None,
                rotation: Rotation::Deg0,
            };
            let result = SizingRequest {
                reference: display(
                    number("ref_width"),
                    number("ref_height"),
                    case["ref_inches"].as_f64().unwrap(),
                ),
                target: display(tw, th, case["target_inches"].as_f64().unwrap()),
                strategy: match case["strategy"].as_str().unwrap() {
                    "phy" => SizingStrategy::MatchPhysicalSize,
                    "rounded" => SizingStrategy::RoundedScale,
                    "int" => SizingStrategy::IntegerScale,
                    name => panic!("Unknown strategy {name}"),
                },
                alignment: 2,
                preferred_refresh_millihz: None,
            }
            .calculate()
            .unwrap();
            assert_eq!(
                result.virtual_mode,
                PixelSize {
                    width: sw,
                    height: sh
                },
                "{id}"
            );
        }
        let source = fs::read(dir.join(case["input"].as_str().unwrap())).unwrap();
        let output = verify_sample_rgba(&source, sw, sh, tw, th, false).unwrap();
        let filename = format!("{id}.output.rgba");
        fs::write(dir.join(&filename), &output.rgba).unwrap();
        images.push(json!({
            "id": id, "source": [sw, sh], "target": [tw, th],
            "adapter": output.adapter_name, "output": filename,
        }));
        save(&images, &results);
        eprintln!("{id}: native output saved");
    }
    let benchmarks: Vec<_> = cases
        .iter()
        .filter(|case| case["benchmark"].as_bool().unwrap_or(true))
        .collect();
    for round in 0..3 {
        let mut order: Vec<_> = (0..benchmarks.len()).collect();
        if !order.is_empty() {
            let shift = round * order.len() / 3;
            order.rotate_left(shift);
            if round == 1 {
                order.reverse();
            }
        }
        for (position, index) in order.into_iter().enumerate() {
            let case = benchmarks[index];
            let id = case["id"].as_str().unwrap();
            let number = |field: &str| case[field].as_u64().unwrap() as u32;
            let (sw, sh, tw, th) = (number("sw"), number("sh"), number("tw"), number("th"));
            let source = fs::read(dir.join(case["input"].as_str().unwrap())).unwrap();
            let output = benchmark_sample_rgba(&source, sw, sh, tw, th, false, 60).unwrap();
            assert_eq!(output.frames_ms.len(), 60);
            assert!(output.frames_ms.iter().all(|t| t.is_finite() && *t > 0.));
            let mut sorted = output.frames_ms.clone();
            sorted.sort_by(f64::total_cmp);
            let median = (sorted[29] + sorted[30]) * 0.5;
            let p95 = sorted[56];
            eprintln!(
                "{id}: round {} GPU median {median:.3} ms, p95 {p95:.3} ms",
                round + 1
            );
            results.push(json!({
                "id": id, "round": round + 1, "position": position + 1,
                "source": [sw, sh], "target": [tw, th], "mode": "quality",
                "adapter": output.adapter_name, "setup_ms": output.setup_ms,
                "warmup_ms": output.warmup_ms, "gpu_frames_ms": output.frames_ms,
                "gpu_median_ms": median, "gpu_p95_ms": p95,
                "gpu_min_ms": sorted[0], "gpu_max_ms": sorted[59],
            }));
            save(&images, &results);
        }
    }
}

#[test]
#[ignore = "requires exact-DPI fixtures and actual D3D11 hardware"]
fn native_identity_defringe_comparison() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let dir = root.join("work/identity-defringe");
    fs::create_dir_all(&dir).unwrap();
    let fixtures = root.join("work/strategy-quality-exact");
    let source = fs::read(fixtures.join("input-em-16.rgba")).unwrap();
    let before = fs::read(fixtures.join("s1-rounded-normalized.output.rgba")).unwrap();
    assert_eq!(
        before, source,
        "Archived identity must be exact source pixels"
    );
    fs::write(dir.join("before.rgba"), &before).unwrap();
    let after = verify_sample_rgba(&source, 2560, 1440, 2560, 1440, false).unwrap();
    assert_ne!(
        after.rgba, before,
        "Quality identity must now remove color fringes"
    );
    // Preserve legacy opt-out and true grayscale samples away from ROI boundaries.
    let legacy = verify_sample_rgba(&source, 2560, 1440, 2560, 1440, true).unwrap();
    assert_eq!(legacy.rgba, source, "Legacy identity must remain unchanged");
    for y in 8..232 {
        for x in 568..1112 {
            let offset = (y * 2560 + x) * 4;
            assert_eq!(&after.rgba[offset..offset + 4], &source[offset..offset + 4]);
        }
    }
    fs::write(dir.join("after.rgba"), &after.rgba).unwrap();
    let mut rounds = Vec::new();
    for round in 1..=3 {
        let output = benchmark_sample_rgba(&source, 2560, 1440, 2560, 1440, false, 60).unwrap();
        let mut sorted = output.frames_ms.clone();
        assert_eq!(sorted.len(), 60);
        assert!(sorted.iter().all(|t| t.is_finite() && *t > 0.));
        sorted.sort_by(f64::total_cmp);
        let median = (sorted[29] + sorted[30]) * 0.5;
        eprintln!("Identity defringe round {round}: {median:.3} ms");
        rounds.push(json!({
            "round": round, "gpu_frames_ms": output.frames_ms,
            "gpu_median_ms": median, "gpu_p95_ms": sorted[56],
            "adapter": output.adapter_name,
        }));
    }
    fs::write(dir.join("results.json"), serde_json::to_vec_pretty(&json!({
        "source": [2560,1440], "target": [2560,1440],
        "before": "Archived production identity output; verified byte-identical to source",
        "after": "Current production quality pipeline",
        "checks": {"legacy_identity_unchanged": true, "interior_grayscale_unchanged": true},
        "timing_scope": "GPU timestamps for production render only; excludes upload, capture, encoding and presentation",
        "rounds": rounds,
    })).unwrap()).unwrap();
}
