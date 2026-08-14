use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::sync::mpsc::{self, Sender};
use std::thread::{self, JoinHandle};

use serde::Deserialize;

use crate::diagnostics::{self, Level};

pub const FIRST_BASE_PORT: u16 = 54_321;
pub const PORT_STRIDE: u16 = 27;
const LAST_FAMILY_OFFSET: u16 = 21;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum CaptureBackend {
    Auto,
    Wgc,
}

impl CaptureBackend {
    pub(crate) fn as_str(self) -> &'static str {
        match self {
            Self::Auto => "auto",
            Self::Wgc => "wgc",
        }
    }
}

#[derive(Clone, Debug)]
pub struct DeploymentEvent {
    pub generation: u64,
    pub group_id: u32,
    pub display_id: String,
    pub requested_port: u16,
    pub port: Option<u16>,
    pub error: Option<String>,
}

enum SupervisorCommand {
    Deploy {
        generation: u64,
        group_id: u32,
        display_id: String,
        requested_port: u16,
        capture: CaptureBackend,
    },
    StopAll,
    Shutdown,
}

#[derive(Clone)]
pub struct SupervisorSender(Sender<SupervisorCommand>);

pub struct Supervisor {
    sender: SupervisorSender,
    worker: Option<JoinHandle<()>>,
}

impl SupervisorSender {
    pub fn start(
        &self,
        generation: u64,
        group_id: u32,
        display_id: String,
        requested_port: u16,
        capture: CaptureBackend,
    ) {
        let _ = self.0.send(SupervisorCommand::Deploy {
            generation,
            group_id,
            display_id,
            requested_port,
            capture,
        });
    }

    pub fn stop_all(&self) {
        let _ = self.0.send(SupervisorCommand::StopAll);
    }
}

impl Supervisor {
    pub fn spawn(emit: impl Fn(DeploymentEvent) + Send + 'static) -> Self {
        let (tx, rx) = mpsc::channel();
        let sender = SupervisorSender(tx);
        let worker = thread::spawn(move || {
            while let Ok(command) = rx.recv() {
                match command {
                    SupervisorCommand::Deploy {
                        generation,
                        group_id,
                        display_id,
                        requested_port,
                        capture,
                    } => {
                        let result = invoke_deploy(group_id, &display_id, requested_port, capture);
                        let event = match result {
                            Ok(result) => {
                                diagnostics::log(
                                    Level::Info,
                                    "sunshine",
                                    "instance",
                                    None,
                                    format!(
                                        "started group {} on port {} (pid {}, capture={})",
                                        group_id,
                                        result.port,
                                        result.pid,
                                        capture.as_str()
                                    ),
                                );
                                DeploymentEvent {
                                    generation,
                                    group_id,
                                    display_id,
                                    requested_port,
                                    port: Some(result.port),
                                    error: None,
                                }
                            }
                            Err(error) => {
                                diagnostics::log(
                                    Level::Error,
                                    "sunshine",
                                    "instance",
                                    None,
                                    format!(
                                        "starting group {} with capture={} failed: {error}",
                                        group_id,
                                        capture.as_str()
                                    ),
                                );
                                DeploymentEvent {
                                    generation,
                                    group_id,
                                    display_id,
                                    requested_port,
                                    port: None,
                                    error: Some(error),
                                }
                            }
                        };
                        emit(event);
                    }
                    SupervisorCommand::StopAll => {
                        if let Err(error) = invoke_stop_all() {
                            diagnostics::log(Level::Warn, "sunshine", "stop-all", None, error);
                        }
                    }
                    SupervisorCommand::Shutdown => {
                        if let Err(error) = invoke_stop_all() {
                            diagnostics::log(Level::Warn, "sunshine", "shutdown", None, error);
                        }
                        break;
                    }
                }
            }
        });
        Self {
            sender,
            worker: Some(worker),
        }
    }

    pub fn sender(&self) -> SupervisorSender {
        self.sender.clone()
    }

    pub fn shutdown(mut self) {
        let _ = self.sender.0.send(SupervisorCommand::Shutdown);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

pub fn default_port_for_slot(slot: usize) -> Result<u16, String> {
    let slot = u16::try_from(slot)
        .map_err(|_| "Too many Sunshine instances for the available port range".to_string())?;
    let offset = slot
        .checked_mul(PORT_STRIDE)
        .ok_or_else(|| "Sunshine port allocation overflowed".to_string())?;
    let port = FIRST_BASE_PORT
        .checked_add(offset)
        .ok_or_else(|| "Sunshine port allocation overflowed".to_string())?;
    if port.checked_add(LAST_FAMILY_OFFSET).is_none() {
        return Err("No complete Sunshine port family remains".into());
    }
    Ok(port)
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScriptResponse {
    ok: bool,
    #[serde(default)]
    port: Option<u16>,
    #[serde(default)]
    pid: Option<u32>,
    #[serde(default)]
    message: Option<String>,
}

struct DeployResult {
    port: u16,
    pid: u32,
}

fn invoke_deploy(
    group_id: u32,
    display_id: &str,
    requested_port: u16,
    capture: CaptureBackend,
) -> Result<DeployResult, String> {
    let script = manager_script_path()?;
    let output = deploy_command(&script, group_id, display_id, requested_port, capture)
        .output()
        .map_err(|error| format!("Couldn’t launch the Sunshine manager: {error}"))?;
    let response = parse_response(&output.stdout, &output.stderr)?;
    if !output.status.success() || !response.ok {
        return Err(response.message.unwrap_or_else(|| {
            format!(
                "Sunshine manager exited with status {}",
                output.status.code().unwrap_or(-1)
            )
        }));
    }
    let port = response
        .port
        .ok_or_else(|| "Sunshine manager did not report its deployed port".to_string())?;
    let pid = response
        .pid
        .ok_or_else(|| "Sunshine manager did not report its process ID".to_string())?;
    Ok(DeployResult { port, pid })
}

fn deploy_command(
    script: &PathBuf,
    group_id: u32,
    display_id: &str,
    requested_port: u16,
    capture: CaptureBackend,
) -> Command {
    let mut command = powershell_command(script);
    command
        .arg("-Action")
        .arg("Start")
        .arg("-GroupId")
        .arg(group_id.to_string())
        .arg("-DisplayId")
        .arg(display_id)
        .arg("-Port")
        .arg(requested_port.to_string())
        .arg("-Capture")
        .arg(capture.as_str());
    command
}

fn invoke_stop_all() -> Result<(), String> {
    let script = manager_script_path()?;
    let output = powershell_command(&script)
        .arg("-Action")
        .arg("StopAll")
        .output()
        .map_err(|error| format!("Couldn’t launch Sunshine cleanup: {error}"))?;
    let response = parse_response(&output.stdout, &output.stderr)?;
    if output.status.success() && response.ok {
        Ok(())
    } else {
        Err(response.message.unwrap_or_else(|| {
            format!(
                "Sunshine cleanup exited with status {}",
                output.status.code().unwrap_or(-1)
            )
        }))
    }
}

fn powershell_command(script: &PathBuf) -> Command {
    let mut command = Command::new("powershell.exe");
    command
        .arg("-NoLogo")
        .arg("-NoProfile")
        .arg("-NonInteractive")
        .arg("-ExecutionPolicy")
        .arg("Bypass")
        .arg("-File")
        .arg(script)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x0800_0000);
    }
    command
}

fn parse_response(stdout: &[u8], stderr: &[u8]) -> Result<ScriptResponse, String> {
    let stdout = String::from_utf8_lossy(stdout);
    if let Some(line) = stdout.lines().rev().find(|line| !line.trim().is_empty()) {
        return serde_json::from_str(line).map_err(|error| {
            format!(
                "Sunshine manager returned invalid JSON ({error}): {}",
                line.trim()
            )
        });
    }
    let stderr = String::from_utf8_lossy(stderr);
    let detail = stderr.trim();
    if detail.is_empty() {
        Err("Sunshine manager returned no result".into())
    } else {
        Err(format!("Sunshine manager returned no result: {detail}"))
    }
}

fn manager_script_path() -> Result<PathBuf, String> {
    let installed = std::env::current_exe()
        .ok()
        .and_then(|path| path.parent().map(|parent| parent.join("installer")))
        .map(|path| path.join("manage-sunshine-instance.ps1"));
    let development =
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("installer/manage-sunshine-instance.ps1");
    installed
        .filter(|path| path.is_file())
        .or_else(|| development.is_file().then_some(development))
        .ok_or_else(|| "The Sunshine instance manager is missing from this installation".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_ports_leave_room_for_the_complete_sunshine_port_family() {
        assert_eq!(default_port_for_slot(0).unwrap(), 54_321);
        assert_eq!(default_port_for_slot(1).unwrap(), 54_348);
        assert_eq!(default_port_for_slot(2).unwrap(), 54_375);
    }

    #[test]
    fn default_port_allocation_rejects_ranges_past_u16() {
        assert!(default_port_for_slot(1_000).is_err());
        assert!(default_port_for_slot(usize::MAX).is_err());
    }

    #[test]
    fn parses_the_last_nonempty_json_line() {
        let response = parse_response(
            b"diagnostic line\n{\"ok\":true,\"port\":54321,\"pid\":42}\n",
            b"",
        )
        .unwrap();
        assert!(response.ok);
        assert_eq!(response.port, Some(54_321));
        assert_eq!(response.pid, Some(42));
    }

    #[test]
    fn deploy_command_passes_capture_as_a_separate_manager_argument() {
        for (capture, expected_capture) in
            [(CaptureBackend::Auto, "auto"), (CaptureBackend::Wgc, "wgc")]
        {
            let command = deploy_command(
                &PathBuf::from(r"C:\Program Files\SBMS\installer\manage-sunshine-instance.ps1"),
                2,
                "{01234567-89ab-cdef-0123-456789abcdef}",
                54_375,
                capture,
            );
            let arguments: Vec<_> = command
                .get_args()
                .map(|argument| argument.to_string_lossy().into_owned())
                .collect();

            assert_eq!(
                &arguments[arguments.len() - 10..],
                [
                    "-Action",
                    "Start",
                    "-GroupId",
                    "2",
                    "-DisplayId",
                    "{01234567-89ab-cdef-0123-456789abcdef}",
                    "-Port",
                    "54375",
                    "-Capture",
                    expected_capture,
                ]
            );
        }
    }
}
