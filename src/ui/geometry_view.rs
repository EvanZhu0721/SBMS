use super::*;

pub(super) fn update_geometry_preview(ui: &QuickAccess, displays: &[DisplayOption]) {
    update_resolution_suggestions(ui);
    ui.set_geometry_error("".into());
    ui.set_geometry_valid(false);
    let request = match geometry_request(ui, displays) {
        Ok(Some(request)) => request,
        Ok(None) => {
            ui.set_geometry_result("Enter display measurements".into());
            ui.set_geometry_result_detail("Enter pixels, diagonal inches, and aspect ratio".into());
            return;
        }
        Err(error) => {
            ui.set_geometry_result("Check the values".into());
            ui.set_geometry_result_detail("".into());
            ui.set_geometry_error(error.into());
            return;
        }
    };
    match request.calculate() {
        Ok(result) => set_geometry_result(ui, result, false),
        Err(error) => {
            ui.set_geometry_result("Check the values".into());
            ui.set_geometry_result_detail("".into());
            ui.set_geometry_error(error.to_string().into());
        }
    }
}

pub(super) const COMMON_WIDTHS: &[&str] = &[
    "1280", "1920", "2560", "3440", "3840", "4096", "5120", "6016", "7680",
];
pub(super) const COMMON_HEIGHTS: &[&str] = &[
    "720", "1080", "1440", "1600", "2160", "2880", "3384", "4320",
];
pub(super) const DEFAULT_STREAM_ASPECT_WIDTH: u64 = 16;
pub(super) const DEFAULT_STREAM_ASPECT_HEIGHT: u64 = 10;

pub(super) fn resolution_suggestion(
    input: &str,
    candidates: &[&'static str],
) -> Option<&'static str> {
    let input = input.trim();
    if input.is_empty() || !input.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }

    let mut matches = candidates
        .iter()
        .copied()
        .filter(|candidate| candidate.len() > input.len() && candidate.starts_with(input));
    let suggestion = matches.next()?;
    matches.next().is_none().then_some(suggestion)
}

pub(super) fn set_resolution_suggestion(
    input: &str,
    candidates: &[&'static str],
    set_suggestion: impl FnOnce(SharedString),
    set_suffix: impl FnOnce(SharedString),
) {
    let suggestion = resolution_suggestion(input, candidates);
    set_suggestion(suggestion.unwrap_or_default().into());
    set_suffix(
        suggestion
            .map(|candidate| &candidate[input.trim().len()..])
            .unwrap_or_default()
            .into(),
    );
}

pub(super) fn set_stream_dimension_suggestion(
    input: &str,
    other_dimension: &str,
    candidates: &[&'static str],
    aspect_numerator: u64,
    aspect_denominator: u64,
    set_suggestion: impl FnOnce(SharedString),
    set_suffix: impl FnOnce(SharedString),
) {
    let input = input.trim();
    let suggestion = if input.is_empty() {
        scaled_dimension_suggestion(other_dimension, aspect_numerator, aspect_denominator)
    } else {
        resolution_suggestion(input, candidates).map(str::to_owned)
    };
    let suffix = suggestion
        .as_deref()
        .and_then(|candidate| candidate.strip_prefix(input))
        .unwrap_or_default();
    set_suggestion(suggestion.clone().unwrap_or_default().into());
    set_suffix(suffix.into());
}

pub(super) fn scaled_dimension_suggestion(
    source: &str,
    numerator: u64,
    denominator: u64,
) -> Option<String> {
    let source = source.trim().parse::<u64>().ok()?;
    if source == 0 || denominator == 0 {
        return None;
    }
    let scaled = source
        .checked_mul(numerator)?
        .checked_add(denominator / 2)?
        / denominator;
    (scaled > 0 && scaled <= u64::from(MAX_VIRTUAL_DIMENSION)).then(|| scaled.to_string())
}

pub(super) fn stream_aspect_ratio(width: &str, height: &str) -> Option<String> {
    let width = width.trim().parse::<u32>().ok()?;
    let height = height.trim().parse::<u32>().ok()?;
    if width == 0 || height == 0 {
        return None;
    }
    let divisor = greatest_common_divisor(width, height);
    let reduced_width = width / divisor;
    let reduced_height = height / divisor;
    if (reduced_width, reduced_height) == (8, 5) {
        Some("16:10".into())
    } else {
        Some(format!("{reduced_width}:{reduced_height}"))
    }
}

pub(super) fn greatest_common_divisor(mut left: u32, mut right: u32) -> u32 {
    while right != 0 {
        (left, right) = (right, left % right);
    }
    left
}

pub(super) fn update_resolution_suggestions(ui: &QuickAccess) {
    set_resolution_suggestion(
        ui.get_reference_width().as_str(),
        COMMON_WIDTHS,
        |value| ui.set_reference_width_suggestion(value),
        |value| ui.set_reference_width_suffix(value),
    );
    set_resolution_suggestion(
        ui.get_reference_height().as_str(),
        COMMON_HEIGHTS,
        |value| ui.set_reference_height_suggestion(value),
        |value| ui.set_reference_height_suffix(value),
    );
}

pub(super) fn calculate_geometry(ui: &QuickAccess, displays: &[DisplayOption]) {
    update_geometry_preview(ui, displays);
    if !ui.get_geometry_valid() {
        if ui.get_page() != QuickAccessPage::Geometry && !ui.get_geometry_error().is_empty() {
            surface_geometry_error(ui, ui.get_geometry_error().as_str());
        }
        return;
    }
    if ui.get_stream_only() {
        validate_stream_fields(ui, displays);
        return;
    }
    let request = match geometry_request(ui, displays) {
        Ok(Some(request)) => request,
        Ok(_) => return,
        Err(error) => {
            surface_geometry_error(ui, &error);
            return;
        }
    };
    let result = request
        .calculate()
        .expect("validated geometry request became invalid");
    set_geometry_result(ui, result, true);
}

pub(super) fn selected_option<'a>(
    ui: &QuickAccess,
    displays: &'a [DisplayOption],
    index: i32,
) -> Option<&'a DisplayOption> {
    let id = selected_display_id(ui, index)?;
    (id != STREAM_ONLY_ID)
        .then(|| displays.iter().find(|display| display.id == id))
        .flatten()
}

pub(super) fn update_reference_action(ui: &QuickAccess, displays: &[DisplayOption]) {
    let reference_source = selected_reference_source(ui);
    let reference_manual = matches!(reference_source, Some(ReferenceSource::Manual));
    ui.set_reference_manual(reference_manual);
    let target_ready = if ui.get_stream_only() {
        aspect_ratio_from_index(ui.get_stream_aspect_ratio_index())
            .and_then(|aspect_ratio| {
                let rotation = rotation_from_index(ui.get_stream_rotation())?;
                stream_target_geometry(
                    ui.get_stream_width().as_str(),
                    ui.get_stream_height().as_str(),
                    ui.get_stream_diagonal().as_str(),
                    aspect_ratio,
                    rotation,
                )
            })
            .is_ok()
    } else {
        automatic_target_geometry(ui, displays, "Target").is_ok()
    };
    let enabled = if reference_manual {
        target_ready
    } else {
        target_ready
            && selected_reference_display_index(ui, displays)
                .and_then(|index| automatic_display_geometry(displays, index, "Reference").ok())
                .is_some()
    };
    ui.set_reference_action_enabled(enabled);
}

pub(super) fn surface_geometry_error(ui: &QuickAccess, error: &str) {
    ui.set_geometry_error(error.into());
    if ui.get_page() != QuickAccessPage::Geometry {
        ui.set_error_text(error.into());
        ui.set_state_detail("Geometry calculation needs attention".into());
    }
}

pub(super) fn geometry_request(
    ui: &QuickAccess,
    displays: &[DisplayOption],
) -> Result<Option<SizingRequest>, String> {
    let stream = ui
        .get_stream_only()
        .then(|| {
            ValidatedStreamFields::parse(
                ui.get_stream_width().as_str(),
                ui.get_stream_height().as_str(),
                ui.get_stream_diagonal().as_str(),
                ui.get_stream_refresh().as_str(),
                ui.get_stream_aspect_ratio_index(),
                ui.get_stream_rotation(),
            )
        })
        .transpose()?;
    let target = if let Some(stream) = stream {
        stream.target_geometry()?
    } else {
        automatic_target_geometry(ui, displays, "Target")?
    };
    let reference = if ui.get_reference_manual() {
        let values = [
            ui.get_reference_width(),
            ui.get_reference_height(),
            ui.get_reference_diagonal(),
        ];
        if values.iter().any(|value| value.trim().is_empty()) {
            return Ok(None);
        }
        DisplayGeometry {
            native_pixels: PixelSize {
                width: parse_u32(&values[0], "Reference width")?,
                height: parse_u32(&values[1], "Reference height")?,
            },
            physical: PhysicalMeasurement::DiagonalMm(
                parse_f64(&values[2], "Reference diagonal")? * MILLIMETERS_PER_INCH,
            ),
            aspect_ratio: Some(aspect_ratio_from_index(ui.get_reference_aspect_ratio())?),
            rotation: rotation_from_index(ui.get_reference_rotation())?,
        }
    } else {
        let reference_index = selected_reference_display_index(ui, displays)
            .ok_or_else(|| "Reference display is not selected".to_string())?;
        automatic_display_geometry(displays, reference_index, "Reference")?
    };
    Ok(Some(SizingRequest {
        reference,
        target,
        strategy: sizing_strategy_from_index(ui.get_sizing_strategy())?,
        alignment: 2,
        preferred_refresh_millihz: Some(
            stream
                .map(|fields| fields.screen.refresh_millihz)
                .unwrap_or(240_000),
        ),
    }))
}

pub(super) fn stream_target_geometry(
    width: &str,
    height: &str,
    diagonal: &str,
    aspect_ratio: AspectRatio,
    rotation: Rotation,
) -> Result<DisplayGeometry, String> {
    ValidatedStreamFields::parse(
        width,
        height,
        diagonal,
        "60",
        aspect_ratio_option_index(aspect_ratio),
        rotation_index(rotation),
    )?
    .target_geometry()
}

pub(super) fn automatic_display_geometry(
    displays: &[DisplayOption],
    index: i32,
    role: &str,
) -> Result<DisplayGeometry, String> {
    let display = (index >= 0)
        .then(|| displays.get(index as usize))
        .flatten()
        .ok_or_else(|| format!("{role} display is not selected"))?;
    let (Some(width_mm), Some(height_mm)) = (display.physical_width_mm, display.physical_height_mm)
    else {
        return Err(format!(
            "{role} display physical size is unavailable; choose Manual or reconnect it directly"
        ));
    };
    Ok(DisplayGeometry {
        native_pixels: PixelSize {
            width: display.native_width,
            height: display.native_height,
        },
        physical: PhysicalMeasurement::DimensionsMm {
            width: width_mm,
            height: height_mm,
        },
        aspect_ratio: None,
        rotation: display.rotation,
    })
}

pub(super) fn automatic_target_geometry(
    ui: &QuickAccess,
    displays: &[DisplayOption],
    role: &str,
) -> Result<DisplayGeometry, String> {
    let display = selected_option(ui, displays, ui.get_selected_display())
        .ok_or_else(|| format!("{role} display is not selected"))?;
    display_geometry(display, role)
}

pub(super) fn display_geometry(
    display: &DisplayOption,
    role: &str,
) -> Result<DisplayGeometry, String> {
    let (Some(width_mm), Some(height_mm)) = (display.physical_width_mm, display.physical_height_mm)
    else {
        return Err(format!(
            "{role} display physical size is unavailable; choose Manual or reconnect it directly"
        ));
    };
    Ok(DisplayGeometry {
        native_pixels: PixelSize {
            width: display.native_width,
            height: display.native_height,
        },
        physical: PhysicalMeasurement::DimensionsMm {
            width: width_mm,
            height: height_mm,
        },
        aspect_ratio: None,
        rotation: display.rotation,
    })
}

pub(super) fn selected_reference_display_index(
    ui: &QuickAccess,
    displays: &[DisplayOption],
) -> Option<i32> {
    let Some(ReferenceSource::Display(id)) = selected_reference_source(ui) else {
        return None;
    };
    displays
        .iter()
        .position(|display| display.id == id)
        .map(|index| index as i32)
}

pub(super) fn parse_f64(value: &str, name: &str) -> Result<f64, String> {
    let inches = value
        .trim()
        .parse()
        .map_err(|_| format!("{name} must be a number in inches"))?;
    if !valid_physical_millimeters(inches * MILLIMETERS_PER_INCH) {
        return Err(format!("{name} must be between 0.4 and 393.7 inches"));
    }
    Ok(inches)
}

pub(super) fn sizing_strategy_from_index(index: i32) -> Result<SizingStrategy, String> {
    match index {
        0 => Ok(SizingStrategy::MatchPhysicalSize),
        1 => Ok(SizingStrategy::RoundedScale),
        2 => Ok(SizingStrategy::IntegerScale),
        _ => Err("Choose a supported sizing strategy".into()),
    }
}

pub(super) fn sizing_strategy_index(strategy: SizingStrategy) -> i32 {
    match strategy {
        SizingStrategy::MatchPhysicalSize => 0,
        SizingStrategy::RoundedScale => 1,
        SizingStrategy::IntegerScale => 2,
    }
}

pub(super) fn populate_geometry_fields(ui: &QuickAccess, request: SizingRequest) {
    ui.set_reference_width(request.reference.native_pixels.width.to_string().into());
    ui.set_reference_height(request.reference.native_pixels.height.to_string().into());
    ui.set_reference_diagonal(
        format!(
            "{:.1}",
            diagonal_mm(request.reference) / MILLIMETERS_PER_INCH
        )
        .into(),
    );
    ui.set_reference_aspect_ratio(aspect_ratio_index(request.reference));
    ui.set_reference_rotation(rotation_index(request.reference.rotation));
    ui.set_sizing_strategy(sizing_strategy_index(request.strategy));
}

pub(super) fn diagonal_mm(display: DisplayGeometry) -> f64 {
    match display.physical {
        PhysicalMeasurement::DiagonalMm(value) => value,
        PhysicalMeasurement::DimensionsMm { width, height } => width.hypot(height),
    }
}

pub(super) fn aspect_ratio_index(display: DisplayGeometry) -> i32 {
    let ratio = display.aspect_ratio.unwrap_or(AspectRatio {
        width: display.native_pixels.width,
        height: display.native_pixels.height,
    });
    aspect_ratio_option_index(ratio)
}

pub(super) fn screen_override_aspect_ratio(index: i32) -> Result<Option<AspectRatio>, String> {
    if index == 0 {
        Ok(None)
    } else {
        aspect_ratio_from_index(index - 1).map(Some)
    }
}

pub(super) fn set_geometry_result(ui: &QuickAccess, result: SizingResult, configured: bool) {
    let mode = format!(
        "{} × {}",
        result.virtual_mode.width, result.virtual_mode.height
    );
    ui.set_geometry_result(mode.as_str().into());
    ui.set_geometry_result_detail(
        format!("Scale {:.2}× × {:.2}×", result.scale_x, result.scale_y).into(),
    );
    ui.set_geometry_valid(!ui.get_geometry_save_blocked());
    ui.set_geometry_error("".into());
    if configured {
        let refresh = result
            .preferred_refresh_millihz
            .map(format_refresh_millihz)
            .unwrap_or_else(|| "Driver refresh".into());
        ui.set_geometry_configured(true);
        ui.set_geometry_summary(mode.as_str().into());
        ui.set_geometry_summary_detail(format!("Planned · {refresh}").into());
        ui.set_state_detail("Ready to start".into());
    }
}

pub(super) fn format_refresh_millihz(refresh: u32) -> String {
    if refresh.is_multiple_of(1_000) {
        format!("{} Hz", refresh / 1_000)
    } else {
        format!("{:.2} Hz", f64::from(refresh) / 1_000.0)
    }
}

pub(super) fn set_geometry_unconfigured(ui: &QuickAccess) {
    ui.set_geometry_valid(false);
    ui.set_geometry_configured(false);
    ui.set_geometry_summary("3840 × 2160".into());
    ui.set_geometry_summary_detail("Driver · 240 Hz".into());
    ui.set_geometry_result("Enter display measurements".into());
    ui.set_geometry_result_detail("Enter pixels, diagonal inches, and aspect ratio".into());
}
