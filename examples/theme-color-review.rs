//! Render real SBMS pages while simulating platform accent refreshes.
use slint::{ComponentHandle, ModelRc, SharedString, VecModel};
use std::{error::Error, path::Path};

slint::slint! {
    #[style = "material"]
    #[include_path = "ui/vendor/material"]
    export { QuickAccess, QuickAccessPage } from "../ui/quick-access.slint";
    export { Palette } from "../ui/vendor/material/std-widgets.slint";
}

fn model(values: &[&str]) -> ModelRc<SharedString> {
    ModelRc::new(VecModel::from(
        values
            .iter()
            .map(|value| (*value).into())
            .collect::<Vec<_>>(),
    ))
}

fn snapshot(
    ui: &QuickAccess,
) -> Result<slint::SharedPixelBuffer<slint::Rgba8Pixel>, Box<dyn Error>> {
    ui.window().take_snapshot()?;
    std::thread::sleep(std::time::Duration::from_millis(350));
    slint::platform::update_timers_and_animations();
    Ok(ui.window().take_snapshot()?)
}

fn main() -> Result<(), Box<dyn Error>> {
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .select()?;
    let output = std::env::args().nth(1).expect("output directory");
    let output = Path::new(&output);
    std::fs::create_dir_all(output)?;
    let ui = QuickAccess::new()?;
    ui.window().set_size(slint::LogicalSize::new(384.0, 544.0));
    ui.set_display_names(model(&["Laptop display"]));
    ui.set_display_labels(model(&["Laptop display"]));
    ui.set_selected_display(0);
    ui.set_reference_names(model(&["27-inch 4K"]));
    ui.set_reference_labels(model(&["27-inch 4K"]));
    ui.set_selected_reference(0);
    ui.set_tab_labels(model(&["1"]));
    ui.set_tab_details(model(&["14″"]));
    ui.set_tab_indices(ModelRc::new(VecModel::from(vec![0])));
    ui.set_state_detail("Ready to start".into());
    ui.set_high_performance_sampling(true);
    ui.set_full_frame_copy(true);

    // Test-only access to the same context setter used by the Windows backend.
    // Production code must not depend on this private API to suppress updates.
    let context = slint::private_unstable_api::re_exports::WindowInner::from_pub(ui.window())
        .context()
        .clone();
    for (scheme, name) in [
        (slint::language::ColorScheme::Light, "light"),
        (slint::language::ColorScheme::Dark, "dark"),
    ] {
        context.set_color_scheme(scheme);
        assert_eq!(ui.global::<Palette>().get_color_scheme(), scheme);
        for (page, page_name) in [
            (QuickAccessPage::Main, "main"),
            (QuickAccessPage::Rendering, "rendering"),
        ] {
            ui.set_page(page);
            context.set_accent_color(slint::Color::from_argb_u8(0, 0, 0, 0));
            let baseline = snapshot(&ui)?;
            if page_name == "main" {
                // Inspect the bottom action area, excluding the purple tab above.
                let action_pixels = baseline
                    .as_bytes()
                    .chunks_exact(4)
                    .skip((baseline.width() * baseline.height() * 3 / 4) as usize);
                let count = action_pixels
                    .filter(|pixel| pixel[..3] == [0x4f, 0x37, 0x8b])
                    .count();
                assert!(
                    count > 1000,
                    "Start button must match reference purple #4F378B: {count}"
                );
                println!("{name}-main: #4F378B fill {count} pixels");
            }
            for (accent, color) in [
                ("blue", slint::Color::from_rgb_u8(0, 120, 215)),
                ("orange", slint::Color::from_rgb_u8(255, 90, 0)),
            ] {
                context.set_accent_color(color);
                let pixels = snapshot(&ui)?;
                let changed = baseline
                    .as_bytes()
                    .chunks_exact(4)
                    .zip(pixels.as_bytes().chunks_exact(4))
                    .filter(|(before, after)| before != after)
                    .count();
                println!("{name}-{page_name}-{accent}: {changed} changed pixels");
                assert_eq!(changed, 0, "system accent must not recolor SBMS");
                let stem = format!("{name}-{page_name}-{accent}");
                std::fs::write(output.join(format!("{stem}.rgba")), pixels.as_bytes())?;
                std::fs::write(
                    output.join(format!("{stem}.json")),
                    serde_json::to_vec(&serde_json::json!({
                        "width": pixels.width(), "height": pixels.height(),
                        "changed_pixels": changed
                    }))?,
                )?;
            }
        }
    }
    Ok(())
}
