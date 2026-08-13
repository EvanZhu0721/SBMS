use std::error::Error;
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

use crate::diagnostics::{self, Level};
use crate::win32_tray::{find_host_window, request_open};

const TASK_NAME: &str = r"\SBMS\Tray-7EB4D7A8-16A9-4B6F-82E3-31A77BC81B6A";
const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const START_TIMEOUT: Duration = Duration::from_secs(10);
const POLL_INTERVAL: Duration = Duration::from_millis(100);

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LaunchDisposition {
    RunHere,
    Exit,
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
        diagnostics::log(
            Level::Debug,
            "launch",
            "route",
            None,
            "elevated tray process will host the UI",
        );
        return Ok(LaunchDisposition::RunHere);
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

fn start_registered_task() -> Result<(), LaunchError> {
    let system_root = std::env::var_os("SystemRoot")
        .filter(|value| !value.is_empty())
        .ok_or_else(|| LaunchError("Windows did not provide the SystemRoot path.".into()))?;
    let executable = PathBuf::from(system_root)
        .join("System32")
        .join("schtasks.exe");
    let status = Command::new(&executable)
        .args(["/Run", "/TN", TASK_NAME])
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
            TASK_NAME,
            r"\SBMS\Tray-7EB4D7A8-16A9-4B6F-82E3-31A77BC81B6A"
        );
    }

    #[test]
    fn wide_adds_exactly_one_terminator() {
        assert_eq!(wide("SBMS"), [83, 66, 77, 83, 0]);
    }
}
