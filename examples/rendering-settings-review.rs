use slint::{ComponentHandle, ModelRc, SharedString, VecModel};
use std::{cell::Cell, error::Error, path::Path, rc::Rc};

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

fn key(ui: &QuickAccess, text: SharedString) {
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::KeyPressed { text: text.clone() });
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::KeyReleased { text });
    slint::platform::update_timers_and_animations();
}

fn capture(ui: &QuickAccess, directory: &Path, name: &str) -> Result<(), Box<dyn Error>> {
    capture_at(ui, directory, name, slint::LogicalPosition::new(1.0, 543.0))
}

fn capture_at(
    ui: &QuickAccess,
    directory: &Path,
    name: &str,
    position: slint::LogicalPosition,
) -> Result<(), Box<dyn Error>> {
    // Only dispatch to this unshown fixture; never send OS input or create displays.
    ui.window()
        .dispatch_event(slint::platform::WindowEvent::PointerMoved { position });
    ui.window().take_snapshot()?;
    std::thread::sleep(std::time::Duration::from_millis(180));
    slint::platform::update_timers_and_animations();
    let pixels = ui.window().take_snapshot()?;
    let scale = ui.window().scale_factor();
    assert_eq!(pixels.width(), (384.0 * scale).round() as u32);
    assert_eq!(pixels.height(), (544.0 * scale).round() as u32);
    std::fs::write(directory.join(format!("{name}.rgba")), pixels.as_bytes())?;
    std::fs::write(
        directory.join(format!("{name}.json")),
        serde_json::to_vec_pretty(&serde_json::json!({
            "width": pixels.width(),
            "height": pixels.height(),
            "scale": scale,
            "format": "RGBA",
        }))?,
    )?;
    println!("{name}: {} x {}", pixels.width(), pixels.height());
    Ok(())
}

fn main() -> Result<(), Box<dyn Error>> {
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .select()?;
    let output = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "work/rendering-settings-review".into());
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
    ui.set_reference_names(model(&["27-inch 4K", "Manual"]));
    ui.set_reference_labels(model(&["27-inch 4K · 3840 × 2160", "Manual"]));
    ui.set_selected_reference(0);
    ui.set_reference_action_enabled(true);
    ui.set_tab_labels(model(&["1", "2"]));
    ui.set_tab_details(model(&["24″", "27″"]));
    ui.set_tab_indices(ModelRc::new(VecModel::from(vec![0, 1])));
    ui.set_group_count(2);
    ui.set_max_group_count(16);
    ui.set_state_detail("Ready to start".into());
    ui.set_geometry_configured(true);
    ui.set_geometry_summary("3414 × 1920".into());
    ui.set_geometry_summary_detail("Physical · 60 Hz".into());
    ui.set_display_names(model(&["Display 01"]));
    ui.set_display_labels(model(&["Display 01 · 2560 × 1440"]));
    ui.set_selected_display(0);
    let changes = Rc::new(Cell::new(0));
    let changes_callback = changes.clone();
    ui.on_rendering_edited(move || changes_callback.set(changes_callback.get() + 1));
    let weak = ui.as_weak();
    ui.on_set_auto_map_on_login(move |enabled| {
        weak.upgrade().unwrap().set_auto_map_on_login(enabled)
    });
    let weak = ui.as_weak();
    ui.on_open_manual_geometry(move || {
        weak.upgrade().unwrap().set_page(QuickAccessPage::Geometry);
    });
    let weak = ui.as_weak();
    ui.on_close_geometry(move || weak.upgrade().unwrap().set_page(QuickAccessPage::Main));

    if std::env::args().nth(3).as_deref() == Some("status-cards") {
        ui.set_profile_names(model(&["default", "Desk"]));
        for (scheme, prefix) in [
            (slint::language::ColorScheme::Light, "light"),
            (slint::language::ColorScheme::Dark, "dark"),
        ] {
            ui.global::<Palette>().set_color_scheme(scheme);
            ui.set_profile_saving(false);
            capture(&ui, directory, &format!("{prefix}-saved-enabled"))?;
            capture_at(
                &ui,
                directory,
                &format!("{prefix}-saved-hover"),
                slint::LogicalPosition::new(270.0, 384.0),
            )?;
            ui.set_profile_saving(true);
            capture(&ui, directory, &format!("{prefix}-saving-disabled"))?;
            capture_at(
                &ui,
                directory,
                &format!("{prefix}-saving-hover"),
                slint::LogicalPosition::new(270.0, 384.0),
            )?;
            click(&ui, 270.0, 384.0);
            capture(&ui, directory, &format!("{prefix}-saving-click"))?;
            key(&ui, slint::platform::Key::Escape.into());
            ui.set_profile_saving(false);
            click(&ui, 270.0, 384.0);
            capture(&ui, directory, &format!("{prefix}-profile-expanded"))?;
            key(&ui, slint::platform::Key::Escape.into());
        }
        std::fs::write(
            directory.join("verification.json"),
            serde_json::to_vec_pretty(&serde_json::json!({
                "scale": scale,
                "physical_size": [(384.0 * scale).round() as u32, (544.0 * scale).round() as u32],
                "states": ["saved-enabled", "saved-hover", "saving-disabled", "saving-hover", "saving-click", "profile-expanded"],
                "os_input_sent": false,
            }))?,
        )?;
        return Ok(());
    }

    for (scheme, prefix) in [
        (slint::language::ColorScheme::Light, "light"),
        (slint::language::ColorScheme::Dark, "dark"),
    ] {
        ui.global::<Palette>().set_color_scheme(scheme);
        ui.set_page(QuickAccessPage::Main);
        ui.set_stream_only(false);
        ui.set_reference_manual(false);
        ui.set_selected_reference(0);
        ui.set_display_names(model(&["Display 01"]));
        ui.set_display_labels(model(&["Display 01 · 2560 × 1440"]));
        ui.set_running(false);
        ui.set_busy(false);
        ui.set_high_performance_sampling(false);
        ui.set_full_frame_copy(false);
        ui.set_static_refresh(false);
        capture(&ui, directory, &format!("{prefix}-main"))?;
        ui.set_profile_names(model(&["default", "Desk"]));
        click(&ui, 280.0, 380.0);
        capture(&ui, directory, &format!("{prefix}-profile-menu"))?;
        key(&ui, slint::platform::Key::Escape.into());
        click(&ui, 344.0, 324.0);
        assert_eq!(ui.get_page(), QuickAccessPage::Rendering);
        assert!(!ui.get_high_performance_sampling());
        assert!(!ui.get_full_frame_copy());
        assert!(!ui.get_static_refresh());
        capture(&ui, directory, &format!("{prefix}-default"))?;
        click(&ui, 320.0, 380.0);
        assert!(ui.get_auto_map_on_login());
        capture(&ui, directory, &format!("{prefix}-login-enabled"))?;
        click(&ui, 320.0, 380.0);
        assert!(!ui.get_auto_map_on_login());

        let before = changes.get();
        click(&ui, 160.0, 116.0);
        assert!(ui.get_high_performance_sampling());
        key(&ui, " ".into());
        assert!(!ui.get_high_performance_sampling());
        key(&ui, slint::platform::Key::Return.into());
        assert!(ui.get_high_performance_sampling());
        key(&ui, slint::platform::Key::Tab.into());
        key(&ui, " ".into());
        assert!(ui.get_full_frame_copy());
        key(&ui, slint::platform::Key::Tab.into());
        key(&ui, " ".into());
        assert!(ui.get_static_refresh());
        assert_eq!(
            changes.get(),
            before + 5,
            "pointer and keyboard notify once"
        );
        capture(&ui, directory, &format!("{prefix}-enabled-keyboard"))?;

        ui.set_running(true);
        let before = changes.get();
        for y in [116.0, 204.0, 292.0] {
            click(&ui, 320.0, y);
            key(&ui, " ".into());
        }
        assert_eq!(changes.get(), before, "running disables all editing");
        assert!(ui.get_high_performance_sampling());
        assert!(ui.get_full_frame_copy());
        assert!(ui.get_static_refresh());
        capture(&ui, directory, &format!("{prefix}-running-disabled"))?;
        key(&ui, slint::platform::Key::Escape.into());
        assert_eq!(ui.get_page(), QuickAccessPage::Main);
        click(&ui, 344.0, 324.0);
        assert_eq!(
            ui.get_page(),
            QuickAccessPage::Rendering,
            "running keeps global settings accessible"
        );
        ui.set_page(QuickAccessPage::Main);
        ui.set_state("Running".into());
        ui.set_state_detail("Mapping active".into());
        capture(&ui, directory, &format!("{prefix}-main-running"))?;

        ui.set_running(false);
        ui.set_state("Stopped".into());
        ui.set_state_detail("Ready to start".into());
        ui.set_busy(true);
        click(&ui, 344.0, 324.0);
        assert_eq!(
            ui.get_page(),
            QuickAccessPage::Rendering,
            "busy keeps global settings accessible"
        );
        capture(&ui, directory, &format!("{prefix}-main-busy"))?;
        ui.set_page(QuickAccessPage::Rendering);
        ui.set_high_performance_sampling(false);
        ui.set_full_frame_copy(false);
        ui.set_static_refresh(false);
        let before = changes.get();
        for y in [116.0, 204.0, 292.0] {
            click(&ui, 320.0, y);
        }
        assert_eq!(changes.get(), before, "busy disables all editing");
        capture(&ui, directory, &format!("{prefix}-busy-disabled"))?;

        ui.set_busy(false);
        ui.set_page(QuickAccessPage::Main);
        ui.set_reference_manual(true);
        ui.set_selected_reference(1);
        capture(&ui, directory, &format!("{prefix}-manual-entry"))?;
        ui.set_running(true);
        click(&ui, 148.0, 289.0);
        assert_eq!(
            ui.get_page(),
            QuickAccessPage::Main,
            "running locks manual reference"
        );
        capture(&ui, directory, &format!("{prefix}-manual-running-disabled"))?;
        ui.set_running(false);
        click(&ui, 148.0, 289.0);
        assert_eq!(ui.get_page(), QuickAccessPage::Geometry);
        ui.set_reference_width("3840".into());
        ui.set_reference_height("2160".into());
        ui.set_reference_diagonal("27".into());
        ui.set_geometry_valid(true);
        ui.set_geometry_result("3414 × 1920".into());
        ui.set_geometry_result_detail("Reference 163.2 PPI · target 122.4 PPI".into());
        capture(&ui, directory, &format!("{prefix}-manual-reference"))?;

        ui.set_page(QuickAccessPage::Main);
        ui.set_stream_only(true);
        ui.set_display_names(model(&["Stream only"]));
        ui.set_display_labels(model(&["Stream only · Create a virtual display"]));
        capture(&ui, directory, &format!("{prefix}-stream-only"))?;
        click(&ui, 344.0, 324.0);
        assert_eq!(
            ui.get_page(),
            QuickAccessPage::Rendering,
            "stream-only keeps global settings accessible"
        );
    }
    std::fs::write(
        directory.join("verification.json"),
        serde_json::to_vec_pretty(&serde_json::json!({
            "defaults": "all false",
            "pointer_and_keyboard": "passed",
            "space_enter_tab_escape": "passed",
            "running_and_busy_disabled": "passed",
            "global_settings_accessible_while_running_and_busy": "passed",
            "manual_reference_entry": "passed",
            "manual_reference_locked_while_running": "passed",
            "stream_only_global_settings_accessible": "passed",
            "callback_count": changes.get(),
            "scale": scale,
            "physical_size": [(384.0 * scale).round() as u32, (544.0 * scale).round() as u32],
            "os_input_sent": false,
        }))?,
    )?;
    Ok(())
}
