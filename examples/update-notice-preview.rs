//! Real Slint preview; no downloads, installation, configuration writes or mappings.
#![windows_subsystem = "windows"]

use sbms::ui::{QuickAccess, QuickAccessPage};
use slint::winit_030::winit::platform::windows::{CornerPreference, WindowAttributesExtWindows};
use slint::{ComponentHandle, ModelRc, SharedString, VecModel};
use windows::Win32::Foundation::RECT;
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetProcessDpiAwarenessContext,
};
use windows::Win32::UI::WindowsAndMessaging::{
    SPI_GETWORKAREA, SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS, SystemParametersInfoW,
};

fn model(values: &[&str]) -> ModelRc<SharedString> {
    ModelRc::new(VecModel::from(
        values
            .iter()
            .map(|value| (*value).into())
            .collect::<Vec<_>>(),
    ))
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .with_winit_window_attributes_hook(|attributes| {
            attributes
                .with_active(false)
                .with_skip_taskbar(true)
                .with_corner_preference(CornerPreference::Round)
        })
        .select()?;
    let ui = QuickAccess::new()?;
    let args: Vec<_> = std::env::args().collect();
    let running = args.iter().any(|arg| arg == "--running");
    ui.set_display_names(model(&["P245MS"]));
    ui.set_display_labels(model(&["P245MS"]));
    ui.set_display_ids(model(&["preview-target"]));
    ui.set_selected_display(0);
    ui.set_reference_names(model(&["G27K-180"]));
    ui.set_reference_labels(model(&["G27K-180"]));
    ui.set_reference_ids(model(&["preview-reference"]));
    ui.set_selected_reference(0);
    ui.set_reference_action_enabled(true);
    ui.set_tab_labels(model(&["P245MS"]));
    ui.set_tab_details(model(&["1920 × 1080"]));
    ui.set_tab_indices(ModelRc::new(VecModel::from(vec![0])));
    ui.set_max_group_count(3);
    ui.set_profile_names(model(&["default"]));
    ui.set_geometry_configured(true);
    ui.set_geometry_summary("1920 × 1080".into());
    ui.set_geometry_summary_detail("24.5″ · 240 Hz".into());
    ui.set_running(running);
    ui.set_state(if running { "Running" } else { "Stopped" }.into());
    ui.set_state_detail("P245MS".into());
    ui.set_mapping_fps(if running { 240 } else { 0 });
    ui.set_mapping_fps_valid(running);
    ui.set_update_ready(true);
    ui.set_update_visible(true);
    ui.set_update_version("2.2.1".into());
    ui.set_update_download_size("14.1 MB".into());
    ui.set_update_installing(args.iter().any(|arg| arg == "--installing"));
    if args.iter().any(|arg| arg == "--rendering") {
        ui.set_page(QuickAccessPage::Rendering);
    }
    // Preview only: exercise both real platform palettes without changing Windows settings.
    let context = slint::private_unstable_api::re_exports::WindowInner::from_pub(ui.window())
        .context()
        .clone();
    let scheme = if args.iter().any(|arg| arg == "--light") {
        slint::language::ColorScheme::Light
    } else {
        slint::language::ColorScheme::Dark
    };
    slint::Timer::single_shot(std::time::Duration::from_millis(150), move || {
        context.set_color_scheme(scheme);
    });
    ui.on_dismiss(|| {
        let _ = slint::quit_event_loop();
    });
    ui.on_install_update(|| {
        let _ = slint::quit_event_loop();
    });
    ui.on_skip_update(|| {
        let _ = slint::quit_event_loop();
    });
    ui.show()?;
    let mut work = RECT::default();
    unsafe {
        SystemParametersInfoW(
            SPI_GETWORKAREA,
            0,
            Some((&mut work as *mut RECT).cast()),
            SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
        )?;
    }
    let weak = ui.as_weak();
    // Position after the first DPI event has applied the window's physical size.
    slint::Timer::single_shot(std::time::Duration::from_millis(200), move || {
        if let Some(ui) = weak.upgrade() {
            let size = ui.window().size();
            let gap = (12.0 * ui.window().scale_factor()) as i32;
            ui.window().set_position(slint::PhysicalPosition::new(
                work.right - size.width as i32 - gap,
                work.bottom - size.height as i32 - gap,
            ));
            let _ = ui.window().take_snapshot();
            ui.window().request_redraw();
        }
    });
    slint::Timer::single_shot(std::time::Duration::from_secs(90), || {
        let _ = slint::quit_event_loop();
    });
    ui.run()?;
    Ok(())
}
