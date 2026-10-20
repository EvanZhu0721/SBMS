use slint::{ComponentHandle, ModelRc, SharedString, VecModel};
use std::{error::Error, path::Path};
slint::slint! {
    #[style = "material"]
    #[include_path = "ui/vendor/material"]
    export { QuickAccess, QuickAccessPage, ScreenSizeRow } from "../ui/quick-access.slint";
    export { Palette } from "../ui/vendor/material/std-widgets.slint";
}
fn model(values: &[&str]) -> ModelRc<SharedString> {
    ModelRc::new(VecModel::from(
        values.iter().map(|s| (*s).into()).collect::<Vec<_>>(),
    ))
}
fn click(ui: &QuickAccess, x: f32, y: f32) {
    let position = slint::LogicalPosition::new(x, y);
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::PointerMoved { position });
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::PointerPressed {
            position,
            button: slint::platform::PointerEventButton::Left,
        });
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::PointerReleased {
            position,
            button: slint::platform::PointerEventButton::Left,
        });
    slint::platform::update_timers_and_animations();
}
fn key(ui: &QuickAccess, key: slint::platform::Key) {
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::KeyPressed { text: key.into() });
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::KeyReleased { text: key.into() });
    slint::platform::update_timers_and_animations();
}
fn capture(ui: &QuickAccess, directory: &Path, name: &str) -> Result<(), Box<dyn Error>> {
    // Evaluate transitions before capturing their settled pixels.
    ui.window().take_snapshot()?;
    std::thread::sleep(std::time::Duration::from_millis(150));
    slint::platform::update_timers_and_animations();
    let pixels = ui.window().take_snapshot()?;
    std::fs::write(directory.join(format!("{name}.rgba")), pixels.as_bytes())?;
    println!("{name}: {} x {} RGBA", pixels.width(), pixels.height());
    Ok(())
}
fn menu(ui: &QuickAccess, dir: &Path, name: &str, x: f32, y: f32) -> Result<(), Box<dyn Error>> {
    click(ui, x, y);
    capture(ui, dir, name)?;
    key(ui, slint::platform::Key::Escape);
    Ok(())
}
fn main() -> Result<(), Box<dyn Error>> {
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .select()?;
    let output = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "work/rollback-ui-review".into());
    let dir = Path::new(&output);
    std::fs::create_dir_all(dir)?;
    let ui = QuickAccess::new()?;
    ui.window().set_size(slint::LogicalSize::new(384.0, 544.0));
    // UI fixtures only: no driver instances or saved configuration are changed.
    ui.set_reference_names(model(&["27-inch 4K", "Manual"]));
    ui.set_reference_labels(model(&["27-inch 4K · 3840 × 2160", "Manual"]));
    ui.set_selected_reference(0);
    ui.set_reference_action_enabled(true);
    ui.set_tab_labels(model(&["1", "2", "3"]));
    ui.set_tab_details(model(&["24″", "27″", "14″"]));
    ui.set_state_detail("Ready to start".into());
    ui.set_tab_indices(ModelRc::new(VecModel::from(vec![0, 1, 2])));
    ui.set_group_count(3);
    ui.set_max_group_count(16);
    ui.set_geometry_configured(true);
    ui.set_geometry_summary("3414 × 1920".into());
    ui.set_geometry_summary_detail("Physical · 60 Hz".into());
    let names = (1..=17)
        .map(|n| SharedString::from(format!("Display {n:02}")))
        .collect::<Vec<_>>();
    ui.set_display_names(ModelRc::new(VecModel::from(names.clone())));
    ui.set_display_labels(ModelRc::new(VecModel::from(names)));
    for (scheme, prefix) in [
        (slint::language::ColorScheme::Light, "light"),
        (slint::language::ColorScheme::Dark, "dark"),
    ] {
        ui.global::<Palette>().set_color_scheme(scheme);
        ui.set_page(QuickAccessPage::Main);
        ui.set_selected_display(0);
        capture(&ui, dir, &format!("{prefix}-main"))?;
        ui.set_running(true);
        ui.set_state("Running".into());
        ui.set_stream_only(false);
        ui.set_sunshine_state(0);
        ui.set_mapping_fps_valid(true);
        ui.set_mapping_fps(120);
        ui.set_state_detail("Running".into());
        capture(&ui, dir, &format!("{prefix}-mirror-without-sunshine"))?;
        ui.set_running(false);
        ui.set_state("Stopped".into());
        ui.set_mapping_fps_valid(false);
        ui.set_state_detail("Ready to start".into());
        menu(&ui, dir, &format!("{prefix}-sizing-menu"), 240.0, 324.0)?;
        menu(&ui, dir, &format!("{prefix}-reference-menu"), 80.0, 324.0)?;
        click(&ui, 170.0, 244.0);
        capture(&ui, dir, &format!("{prefix}-target-menu"))?;
        for _ in 0..16 {
            key(&ui, slint::platform::Key::DownArrow);
        }
        assert_eq!(
            ui.get_selected_display(),
            16,
            "target keyboard navigation reaches last row"
        );
        capture(&ui, dir, &format!("{prefix}-target-menu-last"))?;
        key(&ui, slint::platform::Key::Escape);

        ui.set_page(QuickAccessPage::ScreenSize);
        ui.set_screen_size_rows(ModelRc::new(VecModel::from(vec![
            ScreenSizeRow {
                detected_refresh: "EDID · 144".into(),
                manual_refresh: "".into(),
                display_id: "display-01".into(),
                name: "Display 01 · P245MS".into(),
                native_mode: "2560 × 1440".into(),
                detected_size: "24.0".into(),
                manual_inches: "24".into(),
                aspect_ratio: 0,
                effective_size: "531 × 299 mm".into(),
                error: "".into(),
            },
            ScreenSizeRow {
                detected_refresh: "EDID · 144".into(),
                manual_refresh: "".into(),
                display_id: "display-02".into(),
                name: "27-inch 4K".into(),
                native_mode: "3840 × 2160".into(),
                detected_size: "27.0".into(),
                manual_inches: "".into(),
                aspect_ratio: 0,
                effective_size: "598 × 336 mm".into(),
                error: "".into(),
            },
        ])));
        capture(&ui, dir, &format!("{prefix}-screen-size"))?;
        menu(&ui, dir, &format!("{prefix}-size-ratio-menu"), 256.0, 152.0)?;
        ui.window()
            .dispatch_event(slint::platform::WindowEvent::PointerMoved {
                position: slint::LogicalPosition::new(55.0, 40.0),
            });
        capture(&ui, dir, &format!("{prefix}-back-hover"))?;

        ui.set_page(QuickAccessPage::Geometry);
        ui.set_reference_width("3840".into());
        ui.set_reference_height("2160".into());
        ui.set_reference_diagonal("27".into());
        ui.set_reference_aspect_ratio(0);
        ui.set_reference_rotation(3);
        ui.set_geometry_valid(true);
        ui.set_geometry_result("3414 × 1920".into());
        ui.set_geometry_result_detail("Reference 163.2 PPI · target 122.4 PPI".into());
        capture(&ui, dir, &format!("{prefix}-manual-reference"))?;
        menu(
            &ui,
            dir,
            &format!("{prefix}-reference-ratio-menu"),
            300.0,
            184.0,
        )?;
        menu(
            &ui,
            dir,
            &format!("{prefix}-reference-rotation-menu"),
            95.0,
            236.0,
        )?;

        ui.set_page(QuickAccessPage::Stream);
        ui.set_stream_width("2880".into());
        ui.set_stream_height("1800".into());
        ui.set_stream_diagonal("14".into());
        ui.set_stream_refresh("60".into());
        ui.set_stream_aspect_ratio_index(1);
        ui.set_stream_rotation(3);
        ui.set_stream_result("1210 × 1938".into());
        ui.set_stream_result_detail("Reference 163.2 PPI · 60 Hz".into());
        capture(&ui, dir, &format!("{prefix}-stream"))?;
        menu(
            &ui,
            dir,
            &format!("{prefix}-stream-ratio-menu"),
            280.0,
            244.0,
        )?;
        menu(
            &ui,
            dir,
            &format!("{prefix}-stream-rotation-menu"),
            280.0,
            302.0,
        )?;
    }
    Ok(())
}
