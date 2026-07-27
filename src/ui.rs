use std::error::Error;
use std::rc::Rc;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use crate::control::{TrayInstance, listen_for_shutdown};
use crate::controller::{Controller, ControllerEvent, DisplayOption};
use crate::win32_flyout;
use slint::winit_030::winit::platform::windows::{CornerPreference, WindowAttributesExtWindows};
use slint::{ComponentHandle, Model, ModelRc, SharedString, VecModel};

slint::include_modules!();

pub fn run() -> Result<(), Box<dyn Error>> {
    run_inner(false)
}

pub fn run_open() -> Result<(), Box<dyn Error>> {
    run_inner(true)
}

fn run_inner(open_on_start: bool) -> Result<(), Box<dyn Error>> {
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .with_winit_window_attributes_hook(|attributes| {
            attributes
                .with_transparent(false)
                .with_skip_taskbar(true)
                .with_corner_preference(CornerPreference::Round)
        })
        .select()?;

    let Some(_instance) = TrayInstance::acquire()? else {
        return Ok(());
    };
    listen_for_shutdown(|| {
        let _ = slint::invoke_from_event_loop(|| {
            let _ = slint::quit_event_loop();
        });
    })?;
    let flyout = QuickAccess::new()?;
    let tray = SbmsTray::new()?;

    let flyout_weak = flyout.as_weak();
    tray.on_tray_clicked(move || {
        if let Some(flyout) = flyout_weak.upgrade() {
            if flyout.window().is_visible() {
                let _ = flyout.hide();
            } else {
                show_flyout(&flyout);
            }
        }
    });

    let flyout_weak = flyout.as_weak();
    tray.on_open_panel(move || {
        if let Some(flyout) = flyout_weak.upgrade() {
            show_flyout(&flyout);
        }
    });
    tray.on_quit(|| {
        let _ = slint::quit_event_loop();
    });

    let flyout_weak = flyout.as_weak();
    flyout.on_dismiss(move || {
        if let Some(flyout) = flyout_weak.upgrade() {
            let _ = flyout.hide();
        }
    });

    let ui = flyout.as_weak();
    let tray_weak = tray.as_weak();
    let error_revision = Arc::new(AtomicU64::new(0));
    let event_error_revision = error_revision.clone();
    let controller = Controller::spawn(move |event| {
        let ui = ui.clone();
        let tray = tray_weak.clone();
        let error_revision = event_error_revision.clone();
        let _ = slint::invoke_from_event_loop(move || {
            if let Some(ui) = ui.upgrade() {
                apply_event(&ui, tray.upgrade().as_ref(), &error_revision, event);
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

    let dismiss_timer = slint::Timer::default();
    let dismiss_flyout = flyout.as_weak();
    dismiss_timer.start(
        slint::TimerMode::Repeated,
        Duration::from_millis(150),
        move || {
            if let Some(flyout) = dismiss_flyout.upgrade() {
                if win32_flyout::lost_focus(flyout.window()) {
                    let _ = flyout.hide();
                }
            }
        },
    );

    sender.refresh();
    tray.show()?;
    if open_on_start {
        show_flyout(&flyout);
    }
    slint::run_event_loop()?;
    controller.shutdown();
    Ok(())
}

fn apply_event(
    ui: &QuickAccess,
    tray: Option<&SbmsTray>,
    error_revision: &Arc<AtomicU64>,
    event: ControllerEvent,
) {
    let revision = error_revision.fetch_add(1, Ordering::Relaxed) + 1;
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
            ui.set_error_text(error.as_str().into());
            if !error.is_empty() {
                let ui = ui.as_weak();
                let error_revision = error_revision.clone();
                slint::Timer::single_shot(Duration::from_secs(6), move || {
                    if error_revision.load(Ordering::Relaxed) != revision {
                        return;
                    }
                    if let Some(ui) = ui.upgrade() {
                        ui.set_error_text("".into());
                        if !ui.get_running() && !ui.get_busy() {
                            if ui.get_selected_display() >= 0 {
                                ui.set_state("Stopped".into());
                                ui.set_state_detail("Choose a display to start".into());
                            } else {
                                ui.set_state("No displays".into());
                                ui.set_state_detail("Connect or enable a physical display".into());
                            }
                        }
                    }
                });
            }
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
        ui.set_state("No displays".into());
        ui.set_state_detail("Connect or enable a physical display".into());
    } else {
        ui.set_error_text("".into());
        if !ui.get_running() && !ui.get_busy() {
            ui.set_state("Stopped".into());
            ui.set_state_detail("Choose a display to start".into());
        }
    }
}

fn show_flyout(ui: &QuickAccess) {
    win32_flyout::position(ui.window());

    // Slint 1.17's winit software renderer can retain a reused-buffer cache
    // after Windows clears a hidden window during a display-topology change.
    // Taking a snapshot temporarily selects a new repaint buffer and clears
    // that cache, so the visible frame below is rendered in full.
    let _ = ui.window().take_snapshot();
    let _ = ui.show();

    let ui = ui.as_weak();
    slint::Timer::single_shot(Duration::ZERO, move || {
        if let Some(ui) = ui.upgrade() {
            win32_flyout::activate(ui.window());
            ui.window().request_redraw();
        }
    });
}
