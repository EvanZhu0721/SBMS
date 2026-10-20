use slint::{ComponentHandle, Model, ModelRc, VecModel};
use std::{error::Error, path::Path};

slint::slint! {
    #[style = "material"]
    export { QuickAccess, QuickAccessPage, ScreenSizeRow } from "../ui/quick-access.slint";
    export { Palette } from "std-widgets.slint";
}

fn row(index: usize) -> ScreenSizeRow {
    ScreenSizeRow {
        display_id: format!("display-{index}").into(),
        name: match index {
            0 => "27-inch 4K".into(),
            1 => "P245MS".into(),
            _ => format!("Display {:02}", index + 1).into(),
        },
        native_mode: if index == 0 {
            "3840 × 2160".into()
        } else {
            "2560 × 1440".into()
        },
        detected_size: if index == 0 { "27.0" } else { "24.4" }.into(),
        manual_inches: if index == 1 { "24.0" } else { "" }.into(),
        aspect_ratio: 0,
        effective_size: if index == 0 {
            "598 × 336 mm"
        } else {
            "531 × 299 mm"
        }
        .into(),
        detected_refresh: "EDID · 144".into(),
        manual_refresh: if index == 1 { "120" } else { "" }.into(),
        error: "".into(),
    }
}

fn capture(ui: &QuickAccess, directory: &Path, name: &str) -> Result<(), Box<dyn Error>> {
    // Render the real Slint component without showing a window or sending OS input.
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::PointerMoved {
            position: slint::LogicalPosition::new(1.0, 543.0),
        });
    ui.window().take_snapshot()?;
    std::thread::sleep(std::time::Duration::from_millis(180));
    slint::platform::update_timers_and_animations();
    let pixels = ui.window().take_snapshot()?;
    std::fs::write(directory.join(format!("{name}.rgba")), pixels.as_bytes())?;
    std::fs::write(
        directory.join(format!("{name}.json")),
        serde_json::to_vec_pretty(&serde_json::json!({
            "width": pixels.width(), "height": pixels.height(),
            "scale": ui.window().scale_factor(), "format": "RGBA",
        }))?,
    )?;
    println!("{name}: {} × {}", pixels.width(), pixels.height());
    Ok(())
}

fn main() -> Result<(), Box<dyn Error>> {
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .select()?;
    let output = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "work/screen-size-list-review/100".into());
    let scale = std::env::args()
        .nth(2)
        .unwrap_or_else(|| "1".into())
        .parse::<f32>()?;
    let directory = Path::new(&output);
    std::fs::create_dir_all(directory)?;
    let ui = QuickAccess::new()?;
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::ScaleFactorChanged {
            scale_factor: scale,
        });
    ui.window().set_size(slint::PhysicalSize::new(
        (384.0 * scale).round() as u32,
        (544.0 * scale).round() as u32,
    ));
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::Resized {
            size: slint::LogicalSize::new(384.0, 544.0),
        });
    ui.set_page(QuickAccessPage::ScreenSize);
    ui.set_screen_size_valid(true);
    let weak = ui.as_weak();
    ui.on_screen_refresh_edited(move |index, refresh| {
        let ui = weak.upgrade().unwrap();
        let rows = ui.get_screen_size_rows();
        let mut row = rows.row_data(index as usize).unwrap();
        row.manual_refresh = refresh;
        rows.set_row_data(index as usize, row);
    });

    for (scheme, prefix) in [
        (slint::language::ColorScheme::Light, "light"),
        (slint::language::ColorScheme::Dark, "dark"),
    ] {
        ui.global::<Palette>().set_color_scheme(scheme);
        let mut unnamed = row(0);
        unnamed.name = "15.3-inch display".into();
        unnamed.native_mode = "1920 × 1080".into();
        unnamed.detected_size = "15.3".into();
        unnamed.effective_size = "340 × 190 mm".into();
        ui.set_screen_size_rows(ModelRc::new(VecModel::from(vec![unnamed, row(1)])));
        capture(&ui, directory, &format!("{prefix}-unnamed-edid"))?;
        ui.set_screen_size_rows(ModelRc::new(VecModel::from(vec![row(0), row(1)])));
        capture(&ui, directory, &format!("{prefix}-list"))?;

        ui.invoke_screen_refresh_edited(0, "119.88".into());
        assert_eq!(
            ui.get_screen_size_rows()
                .row_data(0)
                .unwrap()
                .manual_refresh,
            "119.88"
        );
        capture(&ui, directory, &format!("{prefix}-refresh-manual"))?;
        ui.invoke_screen_refresh_edited(0, "".into());
        assert!(
            ui.get_screen_size_rows()
                .row_data(0)
                .unwrap()
                .manual_refresh
                .is_empty()
        );
        capture(&ui, directory, &format!("{prefix}-refresh-auto"))?;
        let mut fallback = row(0);
        fallback.detected_refresh = "Current · 60".into();
        ui.set_screen_size_rows(ModelRc::new(VecModel::from(vec![fallback])));
        capture(&ui, directory, &format!("{prefix}-refresh-no-edid"))?;
    }
    std::fs::write(
        directory.join("verification.json"),
        serde_json::to_vec_pretty(&serde_json::json!({
            "scale": scale, "refresh_manual_and_reset": true,
            "os_input_sent": false, "driver_or_config_changes": false,
        }))?,
    )?;
    Ok(())
}
