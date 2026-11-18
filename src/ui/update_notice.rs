use std::error::Error;
use std::fs::OpenOptions;
use std::os::windows::{ffi::OsStrExt, fs::OpenOptionsExt};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use slint::{ComponentHandle, Timer, TimerMode};
use windows::Win32::Foundation::CloseHandle;
use windows::Win32::Storage::FileSystem::FILE_SHARE_READ;
use windows::Win32::UI::Shell::{
    SEE_MASK_NOASYNC, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW, ShellExecuteExW,
};
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
use windows::core::{PCWSTR, w};

use super::QuickAccess;
use crate::diagnostics::{self, Level};
use crate::updater::{ReadyUpdate, check_and_download, is_update_skipped, skip_update};
use crate::win32_tray::TrayHandle;

pub(super) struct UpdateNotice {
    _check_timer: Timer,
}

pub(super) fn start(
    flyout: &QuickAccess,
    tray: TrayHandle,
) -> Result<UpdateNotice, Box<dyn Error>> {
    let cache = crate::config::ConfigStore::default_path()?.with_file_name("updates");
    let ready = Arc::new(Mutex::new(None::<ReadyUpdate>));
    let checking = Arc::new(AtomicBool::new(false));

    let weak = flyout.as_weak();
    let skip_ready = ready.clone();
    let skip_cache = cache.clone();
    let skip_tray = tray.clone();
    flyout.on_skip_update(move || {
        let Some(flyout) = weak.upgrade() else { return };
        if flyout.get_update_installing() {
            return;
        }
        let mut ready = skip_ready.lock().unwrap();
        let Some(update) = ready.as_ref() else { return };
        if let Err(error) = skip_update(&skip_cache, update) {
            log(error.stage, &error);
            flyout.set_update_action_error("Couldn't skip update. Try again.".into());
            return;
        }
        *ready = None;
        skip_tray.set_update_ready(false);
        flyout.set_update_ready(false);
        flyout.set_update_visible(false);
        flyout.set_update_action_error("".into());
    });

    let weak = flyout.as_weak();
    let install_ready = ready.clone();
    flyout.on_install_update(move || {
        let Some(flyout) = weak.upgrade() else { return };
        if flyout.get_update_installing() {
            return;
        }
        let Some(update) = install_ready.lock().unwrap().clone() else {
            return;
        };
        flyout.set_update_installing(true);
        flyout.set_update_action_error("".into());
        let weak = weak.clone();
        std::thread::spawn(move || {
            let result = launch_installer(&update);
            if let Err((stage, error)) = &result {
                log(stage, error);
            }
            if let Err(error) = slint::invoke_from_event_loop(move || {
                let Some(flyout) = weak.upgrade() else { return };
                flyout.set_update_installing(false);
                if matches!(result, Ok(true)) {
                    flyout.set_update_visible(false);
                } else if result.is_err() {
                    flyout.set_update_action_error("Couldn't open installer. Try again.".into());
                }
            }) {
                log("install-result", error);
            }
        });
    });

    let check_timer = Timer::default();
    let weak = flyout.as_weak();
    let check = move || {
        if weak
            .upgrade()
            .is_some_and(|flyout| !flyout.get_update_installing())
        {
            check_update(
                cache.clone(),
                weak.clone(),
                ready.clone(),
                checking.clone(),
                tray.clone(),
            );
        }
    };
    check();
    check_timer.start(
        TimerMode::Repeated,
        Duration::from_secs(24 * 60 * 60),
        check,
    );
    Ok(UpdateNotice {
        _check_timer: check_timer,
    })
}

fn check_update(
    cache: PathBuf,
    flyout: slint::Weak<QuickAccess>,
    ready: Arc<Mutex<Option<ReadyUpdate>>>,
    checking: Arc<AtomicBool>,
    tray: TrayHandle,
) {
    if checking.swap(true, Ordering::AcqRel) {
        return;
    }
    std::thread::spawn(move || {
        let result = check_and_download(&cache);
        checking.store(false, Ordering::Release);
        let update = match result {
            Ok(update) => update,
            Err(error) => {
                log(error.stage, error.to_string());
                if error.stage != "cache-verify" {
                    return;
                }
                None
            }
        };
        if let Err(error) = slint::invoke_from_event_loop(move || {
            let Some(flyout) = flyout.upgrade() else {
                return;
            };
            if flyout.get_update_installing() {
                return;
            }
            // A check already in flight must respect a skip saved after it started.
            let update = match update {
                Some(update) => match is_update_skipped(&cache, &update.version, update.revision) {
                    Ok(false) => Some(update),
                    Ok(true) => None,
                    Err(error) => {
                        log(error.stage, &error);
                        return;
                    }
                },
                None => None,
            };
            // Opening the flyout chooses when to show the card; background
            // completion only marks readiness and never interrupts the current page.
            tray.set_update_ready(update.is_some());
            flyout.set_update_ready(update.is_some());
            *ready.lock().unwrap() = update.clone();
            let Some(update) = update else {
                flyout.set_update_visible(false);
                return;
            };
            flyout.set_update_version(update.version.clone().into());
            flyout.set_update_download_size(
                format!("{:.1} MB", update.size as f64 / 1_000_000.0).into(),
            );
            flyout.set_update_action_error("".into());
        }) {
            log("check-result", error);
        }
    });
}

fn launch_installer(update: &ReadyUpdate) -> Result<bool, (&'static str, String)> {
    // Deny writes and deletion until the Shell has created the verified image's process.
    let _locked = OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ.0)
        .open(&update.path)
        .map_err(|error| ("install-lock", error.to_string()))?;
    let mut version = [0, 0, 0, update.revision];
    for (text, part) in update.version.split('.').zip(&mut version[..3]) {
        *part = text
            .parse::<u16>()
            .map_err(|error| ("install-version", error.to_string()))?;
    }
    crate::update_trust::verify_installer(&update.path, version)
        .map_err(|error| ("install-verify", error.to_string()))?;
    let path: Vec<u16> = update
        .path
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect();
    let mut execute = SHELLEXECUTEINFOW {
        cbSize: size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_NOASYNC | SEE_MASK_NOCLOSEPROCESS,
        lpVerb: w!("open"),
        lpFile: PCWSTR(path.as_ptr()),
        nShow: SW_SHOWNORMAL.0,
        ..Default::default()
    };
    if let Err(error) = unsafe { ShellExecuteExW(&mut execute) } {
        if error.code().0 == 0x800704C7_u32 as i32 {
            // Dismissing UAC is a user choice, not an update failure.
            return Ok(false);
        }
        return Err(("install-open", error.to_string()));
    }
    if !execute.hProcess.is_invalid() {
        let _ = unsafe { CloseHandle(execute.hProcess) };
    }
    Ok(true)
}

fn log(stage: &str, error: impl std::fmt::Display) {
    diagnostics::log(Level::Warn, "update", stage, None, error.to_string());
}
