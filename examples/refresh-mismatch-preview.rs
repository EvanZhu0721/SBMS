//! Isolated Slint design preview; never starts or changes a mapping.
use sbms::diagnostics;
use slint::ComponentHandle;
#[path = "../src/ui/refresh_notice.rs"]
mod refresh_notice;
use slint::winit_030::winit::platform::windows::{CornerPreference, WindowAttributesExtWindows};
use windows::Win32::Foundation::RECT;
use windows::Win32::UI::WindowsAndMessaging::{
    SPI_GETWORKAREA, SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS, SystemParametersInfoW,
};

slint::slint! {
    #[style = "material"]
    #[include_path = "ui/vendor/material"]
    export { RefreshNoticeWindow } from "../ui/refresh-notice.slint";
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .with_winit_window_attributes_hook(|attributes| {
            refresh_notice::window_attributes(
                attributes
                    .with_active(false)
                    .with_skip_taskbar(true)
                    .with_transparent(true)
                    .with_corner_preference(CornerPreference::Round),
            )
        })
        .select()?;
    let mut work = RECT::default();
    unsafe {
        SystemParametersInfoW(
            SPI_GETWORKAREA,
            0,
            Some((&mut work as *mut RECT).cast()),
            SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
        )?;
    }
    if std::env::args().any(|arg| arg == "--timed") {
        refresh_notice::show(120_000, 240_000, work)?;
        slint::Timer::single_shot(std::time::Duration::from_secs(7), || {
            let _ = slint::quit_event_loop();
        });
        slint::run_event_loop()?;
        return Ok(());
    }
    let ui = RefreshNoticeWindow::new()?;
    ui.show()?;
    let weak = ui.as_weak();
    slint::invoke_from_event_loop(move || {
        if let Some(ui) = weak.upgrade() {
            let size = ui.window().size();
            ui.window().set_position(slint::PhysicalPosition::new(
                work.left + (work.right - work.left - size.width as i32) / 2,
                work.top + (work.bottom - work.top - size.height as i32) / 2,
            ));
        }
    })?;
    ui.run()?;
    Ok(())
}
