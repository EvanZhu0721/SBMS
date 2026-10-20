use slint::{ComponentHandle, ModelRc, VecModel};
use std::{error::Error, path::Path};

slint::slint! {
    #[style = "material"]
    export { QuickAccess, QuickAccessPage } from "../ui/quick-access.slint";
    export { Palette } from "std-widgets.slint";
}

fn capture(ui: &QuickAccess, directory: &Path, name: &str) -> Result<(), Box<dyn Error>> {
    ui.window().take_snapshot()?;
    std::thread::sleep(std::time::Duration::from_millis(180));
    slint::platform::update_timers_and_animations();
    let pixels = ui.window().take_snapshot()?;
    std::fs::write(directory.join(format!("{name}.rgba")), pixels.as_bytes())?;
    std::fs::write(
        directory.join(format!("{name}.json")),
        format!(
            "{{\"width\":{},\"height\":{}}}",
            pixels.width(),
            pixels.height()
        ),
    )?;
    Ok(())
}

fn main() -> Result<(), Box<dyn Error>> {
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .select()?;
    let dir = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "work/diagnostics-scroll".into());
    let scale: f32 = std::env::args()
        .nth(2)
        .unwrap_or_else(|| "1".into())
        .parse()?;
    let dir = Path::new(&dir);
    std::fs::create_dir_all(dir)?;
    let ui = QuickAccess::new()?;
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::ScaleFactorChanged {
            scale_factor: scale,
        });
    ui.window().set_size(slint::PhysicalSize::new(
        (384.0 * scale) as u32,
        (544.0 * scale) as u32,
    ));
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::Resized {
            size: slint::LogicalSize::new(384.0, 544.0),
        });
    ui.set_page(QuickAccessPage::Diagnostics);
    ui.set_tab_indices(ModelRc::new(VecModel::from(vec![0])));
    let log = std::env::args()
        .nth(3)
        .ok_or("provide a diagnostic log as the third argument")?;
    let summary = std::fs::read_to_string(log)?;
    ui.set_diagnostic_summary(summary.split("\n\n").next().unwrap_or(&summary).into());
    ui.set_diagnostic_text(summary.into());
    for (scheme, prefix) in [
        (slint::language::ColorScheme::Light, "light"),
        (slint::language::ColorScheme::Dark, "dark"),
    ] {
        ui.global::<Palette>().set_color_scheme(scheme);
        ui.window()
            .dispatch_event(slint::platform::WindowEvent::PointerScrolled {
                position: slint::LogicalPosition::new(170.0, 128.0),
                delta_x: 0.0,
                delta_y: 4000.0,
            });
        capture(&ui, dir, &format!("{prefix}-top"))?;
        ui.window()
            .dispatch_event(slint::platform::WindowEvent::PointerScrolled {
                position: slint::LogicalPosition::new(170.0, 128.0),
                delta_x: 0.0,
                delta_y: -4000.0,
            });
        capture(&ui, dir, &format!("{prefix}-bottom"))?;
    }
    Ok(())
}
