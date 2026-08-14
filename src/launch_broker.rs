use std::error::Error;
use std::ffi::OsString;
use std::fmt::{Display, Formatter};
use std::mem::size_of;
use std::os::windows::process::CommandExt;
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::{CloseHandle, HANDLE, HWND};
use windows::Win32::Security::{GetTokenInformation, TOKEN_ELEVATION, TOKEN_QUERY, TokenElevation};
use windows::Win32::System::Threading::{
    GetCurrentProcess, OpenProcess, OpenProcessToken, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::WindowsAndMessaging::{
    GetWindowThreadProcessId, MB_ICONERROR, MB_OK, MessageBoxW,
};
use windows::core::{PCWSTR, w};

use crate::control::TrayInstance;
use crate::diagnostics::{self, Level};
use crate::win32_tray::{find_host_window, request_open};

pub const REGISTERED_TASK_NAME: &str = r"\SBMS\Tray-7EB4D7A8-16A9-4B6F-82E3-31A77BC81B6A";
const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const START_TIMEOUT: Duration = Duration::from_secs(10);
const POLL_INTERVAL: Duration = Duration::from_millis(100);

pub enum LaunchDisposition {
    RunHere(TrayInstance),
    Exit,
}

enum HostClaim<H, C> {
    Existing(H),
    Claimed(C),
}

#[derive(Debug)]
pub struct LaunchError(String);

impl Display for LaunchError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for LaunchError {}

pub fn route_tray(open_requested: bool) -> Result<LaunchDisposition, LaunchError> {
    let elevated = current_process_is_elevated()?;
    if let Some(hwnd) = find_host_window() {
        return route_to_existing(hwnd, open_requested);
    }

    if elevated {
        return claim_or_route_elevated(open_requested);
    }

    diagnostics::log(
        Level::Info,
        "launch",
        "task-handoff",
        None,
        "starting the registered elevated tray task",
    );
    start_registered_task()?;

    let deadline = Instant::now() + START_TIMEOUT;
    loop {
        if let Some(hwnd) = find_host_window() {
            return route_to_existing(hwnd, open_requested);
        }
        if Instant::now() >= deadline {
            return Err(LaunchError(
                "The elevated SBMS tray did not become ready. Repair or reinstall SBMS, then try again."
                    .into(),
            ));
        }
        thread::sleep(POLL_INTERVAL);
    }
}

pub fn tray_open_requested(
    arguments: impl IntoIterator<Item = OsString>,
) -> Result<bool, LaunchError> {
    let mut arguments = arguments.into_iter();
    let open_requested = match arguments.next() {
        None => true,
        Some(argument) if argument == "--open" => true,
        Some(argument) if argument == "--background" => false,
        Some(argument) => {
            return Err(LaunchError(format!(
                "unsupported SBMS tray argument: {}",
                argument.to_string_lossy()
            )));
        }
    };
    if let Some(argument) = arguments.next() {
        return Err(LaunchError(format!(
            "unsupported SBMS tray argument: {}",
            argument.to_string_lossy()
        )));
    }
    Ok(open_requested)
}

pub fn show_launch_error(error: &dyn Error) {
    let message = wide(&format!("SBMS could not start.\n\n{error}"));
    unsafe {
        let _ = MessageBoxW(
            None,
            PCWSTR(message.as_ptr()),
            w!("SBMS"),
            MB_OK | MB_ICONERROR,
        );
    }
}

fn route_to_existing(hwnd: HWND, open_requested: bool) -> Result<LaunchDisposition, LaunchError> {
    if !window_process_is_elevated(hwnd)? {
        return Err(LaunchError(
            "A non-elevated SBMS tray is already running. Exit it from the tray menu, then open SBMS again."
                .into(),
        ));
    }
    if open_requested {
        request_open(hwnd).map_err(|error| {
            LaunchError(format!("Could not open the running SBMS tray: {error}"))
        })?;
    }
    Ok(LaunchDisposition::Exit)
}

fn claim_or_route_elevated(open_requested: bool) -> Result<LaunchDisposition, LaunchError> {
    let deadline = Instant::now() + START_TIMEOUT;
    let claim = poll_host_or_claim(
        find_host_window,
        || TrayInstance::acquire().map_err(LaunchError),
        || {
            if Instant::now() >= deadline {
                return false;
            }
            thread::sleep(POLL_INTERVAL);
            true
        },
    )?;
    match claim {
        Some(HostClaim::Existing(hwnd)) => route_to_existing(hwnd, open_requested),
        Some(HostClaim::Claimed(instance)) => {
            diagnostics::log(
                Level::Debug,
                "launch",
                "route",
                None,
                "elevated tray process claimed the UI host",
            );
            Ok(LaunchDisposition::RunHere(instance))
        }
        None => Err(LaunchError(
            "Another elevated SBMS tray owns the host lock but did not become ready. Repair or reinstall SBMS, then try again."
                .into(),
        )),
    }
}

fn poll_host_or_claim<H, C, E>(
    mut find_host: impl FnMut() -> Option<H>,
    mut try_claim: impl FnMut() -> Result<Option<C>, E>,
    mut wait_for_retry: impl FnMut() -> bool,
) -> Result<Option<HostClaim<H, C>>, E> {
    loop {
        if let Some(host) = find_host() {
            return Ok(Some(HostClaim::Existing(host)));
        }
        if let Some(claim) = try_claim()? {
            return Ok(Some(HostClaim::Claimed(claim)));
        }
        if !wait_for_retry() {
            return Ok(None);
        }
    }
}

fn start_registered_task() -> Result<(), LaunchError> {
    let system_root = std::env::var_os("SystemRoot")
        .filter(|value| !value.is_empty())
        .ok_or_else(|| LaunchError("Windows did not provide the SystemRoot path.".into()))?;
    let executable = PathBuf::from(system_root)
        .join("System32")
        .join("schtasks.exe");
    let status = Command::new(&executable)
        .args(["/Run", "/TN", REGISTERED_TASK_NAME])
        .creation_flags(CREATE_NO_WINDOW)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .map_err(|error| {
            LaunchError(format!(
                "Could not start the registered SBMS task at {}: {error}",
                executable.display()
            ))
        })?;
    if !status.success() {
        return Err(LaunchError(format!(
            "The registered SBMS startup task failed with exit code {}. Repair or reinstall SBMS.",
            status.code().unwrap_or(-1)
        )));
    }
    Ok(())
}

fn current_process_is_elevated() -> Result<bool, LaunchError> {
    token_is_elevated(unsafe { GetCurrentProcess() })
}

fn window_process_is_elevated(hwnd: HWND) -> Result<bool, LaunchError> {
    let mut process_id = 0;
    unsafe {
        GetWindowThreadProcessId(hwnd, Some(&mut process_id));
    }
    if process_id == 0 {
        return Err(LaunchError(
            "Could not identify the running SBMS tray process.".into(),
        ));
    }
    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process_id) }
        .map_err(|error| {
            LaunchError(format!("Could not inspect the running SBMS tray: {error}"))
        })?;
    let result = token_is_elevated(process);
    unsafe {
        let _ = CloseHandle(process);
    }
    result
}

fn token_is_elevated(process: HANDLE) -> Result<bool, LaunchError> {
    let mut token = HANDLE::default();
    unsafe { OpenProcessToken(process, TOKEN_QUERY, &mut token) }.map_err(|error| {
        LaunchError(format!("Could not inspect the SBMS process token: {error}"))
    })?;
    let mut elevation = TOKEN_ELEVATION::default();
    let mut returned = 0;
    let result = unsafe {
        GetTokenInformation(
            token,
            TokenElevation,
            Some((&mut elevation as *mut TOKEN_ELEVATION).cast()),
            size_of::<TOKEN_ELEVATION>() as u32,
            &mut returned,
        )
    };
    unsafe {
        let _ = CloseHandle(token);
    }
    result
        .map(|()| elevation.TokenIsElevated != 0)
        .map_err(|error| LaunchError(format!("Could not read the SBMS elevation state: {error}")))
}

fn wide(value: &str) -> Vec<u16> {
    let mut encoded: Vec<u16> = value.encode_utf16().collect();
    encoded.push(0);
    encoded
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn registered_task_name_is_stable() {
        assert_eq!(
            REGISTERED_TASK_NAME,
            r"\SBMS\Tray-7EB4D7A8-16A9-4B6F-82E3-31A77BC81B6A"
        );
    }

    #[test]
    fn tray_arguments_default_to_open() {
        assert!(tray_open_requested([]).unwrap());
        assert!(tray_open_requested(["--open".into()]).unwrap());
        assert!(!tray_open_requested(["--background".into()]).unwrap());
    }

    #[test]
    fn tray_arguments_reject_unknown_or_extra_values() {
        assert!(tray_open_requested(["--unknown".into()]).is_err());
        assert!(tray_open_requested(["--open".into(), "extra".into()]).is_err());
    }

    #[test]
    fn polling_routes_to_host_when_busy_owner_becomes_ready() {
        let mut host_checks = 0;
        let mut claim_attempts = 0;
        let mut waits = 0;
        let result = poll_host_or_claim(
            || {
                host_checks += 1;
                (host_checks == 2).then_some("host")
            },
            || {
                claim_attempts += 1;
                Ok::<_, ()>(None::<&str>)
            },
            || {
                waits += 1;
                true
            },
        )
        .unwrap();

        assert!(matches!(result, Some(HostClaim::Existing("host"))));
        assert_eq!(claim_attempts, 1);
        assert_eq!(waits, 1);
    }

    #[test]
    fn polling_claims_host_when_busy_mutex_is_released() {
        let mut claim_attempts = 0;
        let mut waits = 0;
        let result = poll_host_or_claim(
            || None::<&str>,
            || {
                claim_attempts += 1;
                Ok::<_, ()>((claim_attempts == 2).then_some("claim"))
            },
            || {
                waits += 1;
                true
            },
        )
        .unwrap();

        assert!(matches!(result, Some(HostClaim::Claimed("claim"))));
        assert_eq!(waits, 1);
    }

    #[test]
    fn wide_adds_exactly_one_terminator() {
        assert_eq!(wide("SBMS"), [83, 66, 77, 83, 0]);
    }
}
