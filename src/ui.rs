use std::error::Error;
use std::mem::size_of;
use std::rc::Rc;

use slint::{ComponentHandle, Model, ModelRc, SharedString, VecModel};
use windows::Win32::Foundation::{POINT, RECT};
use windows::Win32::Graphics::Gdi::{
    GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromPoint,
};
use windows::Win32::UI::WindowsAndMessaging::GetCursorPos;

use crate::controller::{Controller, ControllerEvent, DisplayOption};

slint::include_modules!();

pub fn run() -> Result<(), Box<dyn Error>> {
    run_inner(false)
}

pub fn run_open() -> Result<(), Box<dyn Error>> {
    run_inner(true)
}

fn run_inner(open_on_start: bool) -> Result<(), Box<dyn Error>> {
    let flyout = QuickAccess::new()?;
    let settings = SettingsWindow::new()?;
    let tray = SbmsTray::new()?;

    let flyout_weak = flyout.as_weak();
    tray.on_tray_clicked(move || {
        if let Some(flyout) = flyout_weak.upgrade() {
            position_near_tray(&flyout);
            let _ = flyout.show();
        }
    });

    let flyout_weak = flyout.as_weak();
    tray.on_open_panel(move || {
        if let Some(flyout) = flyout_weak.upgrade() {
            position_near_tray(&flyout);
            let _ = flyout.show();
        }
    });
    tray.on_quit(|| {
        let _ = slint::quit_event_loop();
    });

    let settings_weak = settings.as_weak();
    flyout.on_open_settings(move || {
        if let Some(settings) = settings_weak.upgrade() {
            let _ = settings.show();
        }
    });
    let flyout_weak = flyout.as_weak();
    flyout.on_dismiss(move || {
        if let Some(flyout) = flyout_weak.upgrade() {
            let _ = flyout.hide();
        }
    });
    flyout.on_quit(|| {
        let _ = slint::quit_event_loop();
    });

    let settings_weak = settings.as_weak();
    settings.on_dismiss(move || {
        if let Some(settings) = settings_weak.upgrade() {
            let _ = settings.hide();
        }
    });

    let ui = flyout.as_weak();
    let tray_weak = tray.as_weak();
    let controller = Controller::spawn(move |event| {
        let ui = ui.clone();
        let tray = tray_weak.clone();
        let _ = slint::invoke_from_event_loop(move || {
            if let Some(ui) = ui.upgrade() {
                apply_event(&ui, tray.upgrade().as_ref(), event);
            }
        });
    });
    let sender = controller.sender();

    let start_sender = sender.clone();
    let flyout_weak = flyout.as_weak();
    flyout.on_start(move || {
        if let Some(ui) = flyout_weak.upgrade() {
            let index = ui.get_selected_display();
            let ids = ui.get_display_ids();
            if index >= 0
                && let Some(target) = ids.row_data(index as usize)
            {
                start_sender.start(target.to_string());
            }
        }
    });
    let stop_sender = sender.clone();
    flyout.on_stop(move || stop_sender.stop());
    let refresh_sender = sender.clone();
    flyout.on_refresh(move || refresh_sender.refresh());

    sender.refresh();
    tray.show()?;
    if open_on_start {
        position_near_tray(&flyout);
        flyout.show()?;
    }
    slint::run_event_loop()?;
    controller.shutdown();
    Ok(())
}

fn apply_event(ui: &QuickAccess, tray: Option<&SbmsTray>, event: ControllerEvent) {
    match event {
        ControllerEvent::Displays(displays) => set_displays(ui, displays),
        ControllerEvent::State {
            state,
            detail,
            running,
            busy,
            error,
        } => {
            if let Some(tray) = tray {
                tray.set_status(state.into());
            }
            ui.set_state(state.into());
            ui.set_state_detail(detail.into());
            ui.set_running(running);
            ui.set_busy(busy);
            ui.set_error_text(error.into());
        }
    }
}

fn set_displays(ui: &QuickAccess, displays: Vec<DisplayOption>) {
    let labels: Vec<_> = displays
        .iter()
        .map(|display| SharedString::from(display.label.as_str()))
        .collect();
    let ids: Vec<_> = displays
        .iter()
        .map(|display| SharedString::from(display.id.as_str()))
        .collect();
    ui.set_display_labels(ModelRc::new(Rc::new(VecModel::from(labels))));
    ui.set_display_ids(ModelRc::new(Rc::new(VecModel::from(ids))));
    ui.set_selected_display(if displays.is_empty() { -1 } else { 0 });
    if displays.is_empty() {
        ui.set_error_text("No supported physical displays found".into());
    }
}

fn position_near_tray(ui: &QuickAccess) {
    let mut cursor = POINT::default();
    if unsafe { GetCursorPos(&mut cursor) }.is_err() {
        return;
    }
    let monitor = unsafe { MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST) };
    let mut info = MONITORINFO {
        cbSize: size_of::<MONITORINFO>() as u32,
        rcMonitor: RECT::default(),
        rcWork: RECT::default(),
        dwFlags: 0,
    };
    if unsafe { GetMonitorInfoW(monitor, &mut info) }.as_bool() {
        let size = ui.window().size();
        let width = size.width as i32;
        let height = size.height as i32;
        let x = (cursor.x - width / 2).clamp(info.rcWork.left, info.rcWork.right - width);
        let y = (cursor.y - height - 12).clamp(info.rcWork.top, info.rcWork.bottom - height);
        ui.window().set_position(slint::PhysicalPosition::new(x, y));
    }
}
