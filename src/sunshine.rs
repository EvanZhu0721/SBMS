use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::path::PathBuf;
use std::process::{Command, Output, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use serde::Deserialize;
use windows::Win32::Foundation::{FILETIME, HANDLE, WAIT_OBJECT_0, WAIT_TIMEOUT};
use windows::Win32::System::Threading::{
    CreateEventW, GetExitCodeProcess, GetProcessTimes, OpenProcess,
    PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE, SetEvent, WaitForSingleObject,
};
use windows::core::PCWSTR;

use crate::diagnostics::{self, Level};

pub const FIRST_BASE_PORT: u16 = 54_321;
pub const PORT_STRIDE: u16 = 27;
const LAST_FAMILY_OFFSET: u16 = 21;
const POLL_INTERVAL: Duration = Duration::from_millis(100);

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
        cancellation: Cancellation,
    },
    StopAll,
    Shutdown,
}

#[derive(Clone)]
pub struct SupervisorSender {
    tx: Sender<SupervisorCommand>,
    stop_epoch: Arc<AtomicU64>,
}

#[derive(Clone)]
struct Cancellation {
    run: Arc<AtomicBool>,
    stop_epoch: Arc<AtomicU64>,
    epoch: u64,
}

impl Cancellation {
    fn is_cancelled(&self) -> bool {
        self.run.load(Ordering::Acquire) || self.stop_epoch.load(Ordering::Acquire) != self.epoch
    }
}

struct Instance {
    event: DeploymentEvent,
    process: ManagedProcess,
    cancellation: Cancellation,
}

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
        run_cancel: Arc<AtomicBool>,
    ) {
        let cancellation = Cancellation {
            run: run_cancel,
            stop_epoch: Arc::clone(&self.stop_epoch),
            epoch: self.stop_epoch.load(Ordering::Acquire),
        };
        let _ = self.tx.send(SupervisorCommand::Deploy {
            generation,
            group_id,
            display_id,
            requested_port,
            capture,
            cancellation,
        });
    }

    pub fn stop_all(&self) {
        self.stop_epoch.fetch_add(1, Ordering::AcqRel);
        let _ = self.tx.send(SupervisorCommand::StopAll);
    }
}

impl Supervisor {
    pub fn spawn(emit: impl Fn(DeploymentEvent) + Send + 'static) -> Self {
        Self::spawn_with_script(manager_script_path(), emit)
    }

    fn spawn_with_script(
        script: Result<PathBuf, String>,
        emit: impl Fn(DeploymentEvent) + Send + 'static,
    ) -> Self {
        let (tx, rx) = mpsc::channel();
        let sender = SupervisorSender {
            tx,
            stop_epoch: Arc::new(AtomicU64::new(0)),
        };
        let worker = thread::spawn(move || {
            let mut instances = Vec::new();
            loop {
                poll_instances(&mut instances, &emit);
                let command = match rx.recv_timeout(POLL_INTERVAL) {
                    Ok(command) => command,
                    Err(RecvTimeoutError::Timeout) => continue,
                    Err(RecvTimeoutError::Disconnected) => break,
                };
                match command {
                    SupervisorCommand::Deploy {
                        generation,
                        group_id,
                        display_id,
                        requested_port,
                        capture,
                        cancellation,
                    } => {
                        if cancellation.is_cancelled() {
                            continue;
                        }
                        // A replacement must not report the previous instance's planned exit.
                        instances.retain(|instance| instance.event.group_id != group_id);
                        let result = script.as_ref().map_err(Clone::clone).and_then(|script| {
                            invoke_deploy(
                                script,
                                group_id,
                                &display_id,
                                requested_port,
                                capture,
                                &cancellation,
                                || poll_instances(&mut instances, &emit),
                            )
                        });
                        if cancellation.is_cancelled() {
                            // The cancellation may arrive just after the manager's final check.
                            let cleanup = match result {
                                Ok(Some(_)) => script
                                    .as_ref()
                                    .map_err(Clone::clone)
                                    .and_then(|script| invoke_stop(script, Some(group_id))),
                                Ok(None) => Ok(()),
                                Err(error) => Err(error),
                            };
                            if let Err(error) = cleanup {
                                diagnostics::log(Level::Warn, "sunshine", "cancel", None, error);
                            }
                            continue;
                        }
                        let event = match result {
                            Ok(Some(result)) => {
                                diagnostics::log(
                                    Level::Info,
                                    "sunshine",
                                    "instance",
                                    None,
                                    format!(
                                        "started generation {} group {} on port {} (pid {}, start {}, capture={})",
                                        generation,
                                        group_id,
                                        result.port,
                                        result.process.pid,
                                        result.process.start_time,
                                        capture.as_str()
                                    ),
                                );
                                let event = DeploymentEvent {
                                    generation,
                                    group_id,
                                    display_id,
                                    requested_port,
                                    port: Some(result.port),
                                    error: None,
                                };
                                instances.push(Instance {
                                    event: event.clone(),
                                    process: result.process,
                                    cancellation,
                                });
                                event
                            }
                            Ok(None) => continue,
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
                        instances.clear();
                        if let Err(error) = script
                            .as_ref()
                            .map_err(Clone::clone)
                            .and_then(|script| invoke_stop(script, None))
                        {
                            diagnostics::log(Level::Warn, "sunshine", "stop-all", None, error);
                        }
                    }
                    SupervisorCommand::Shutdown => {
                        instances.clear();
                        if let Err(error) = script
                            .as_ref()
                            .map_err(Clone::clone)
                            .and_then(|script| invoke_stop(script, None))
                        {
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
        self.sender.stop_epoch.fetch_add(1, Ordering::AcqRel);
        let _ = self.sender.tx.send(SupervisorCommand::Shutdown);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

fn poll_instances(instances: &mut Vec<Instance>, emit: &impl Fn(DeploymentEvent)) {
    instances.retain_mut(|instance| {
        if instance.cancellation.is_cancelled() {
            return false;
        }
        let error = match instance.process.exit_code() {
            Ok(None) => return true,
            Ok(Some(code)) => format!("Sunshine exited unexpectedly (exit code {code})"),
            Err(error) => error,
        };
        if !instance.cancellation.is_cancelled() {
            diagnostics::log(
                Level::Error,
                "sunshine",
                "exit",
                None,
                format!(
                    "generation {} group {} pid {} start {}: {error}",
                    instance.event.generation,
                    instance.event.group_id,
                    instance.process.pid,
                    instance.process.start_time,
                ),
            );
            let mut event = instance.event.clone();
            event.port = None;
            event.error = Some(error);
            emit(event);
        }
        false
    });
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
    cancelled: bool,
    #[serde(default)]
    port: Option<u16>,
    #[serde(default)]
    pid: Option<u32>,
    #[serde(default)]
    process_start_time_file_time: Option<u64>,
    #[serde(default)]
    message: Option<String>,
}

struct DeployResult {
    port: u16,
    process: ManagedProcess,
}

struct ManagedProcess {
    handle: OwnedHandle,
    pid: u32,
    start_time: u64,
}

impl ManagedProcess {
    fn open(pid: u32, expected_start: u64) -> Result<Self, String> {
        let handle = unsafe {
            OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                false,
                pid,
            )
        }
        .map_err(|error| format!("Couldn’t monitor Sunshine process {pid}: {error}"))?;
        let handle = unsafe { OwnedHandle::from_raw_handle(handle.0) };
        let start_time = process_start_time(&handle)?;
        if start_time != expected_start {
            return Err(format!(
                "Sunshine process {pid} changed before monitoring began"
            ));
        }
        Ok(Self {
            handle,
            pid,
            start_time,
        })
    }

    fn exit_code(&self) -> Result<Option<u32>, String> {
        let handle = HANDLE(self.handle.as_raw_handle());
        match unsafe { WaitForSingleObject(handle, 0) } {
            WAIT_TIMEOUT => Ok(None),
            WAIT_OBJECT_0 => {
                let mut code = 0;
                unsafe { GetExitCodeProcess(handle, &mut code) }
                    .map_err(|error| format!("Couldn’t read Sunshine exit status: {error}"))?;
                Ok(Some(code))
            }
            _ => Err(format!(
                "Couldn’t monitor Sunshine process: {}",
                windows::core::Error::from_thread()
            )),
        }
    }
}

fn process_start_time(handle: &OwnedHandle) -> Result<u64, String> {
    let mut created = FILETIME::default();
    let mut exited = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    unsafe {
        GetProcessTimes(
            HANDLE(handle.as_raw_handle()),
            &mut created,
            &mut exited,
            &mut kernel,
            &mut user,
        )
    }
    .map_err(|error| format!("Couldn’t verify Sunshine process identity: {error}"))?;
    Ok((u64::from(created.dwHighDateTime) << 32) | u64::from(created.dwLowDateTime))
}

struct CancelEvent {
    handle: OwnedHandle,
    name: String,
}

impl CancelEvent {
    fn create() -> Result<Self, String> {
        static NEXT_ID: AtomicU64 = AtomicU64::new(0);
        let name = format!(
            "Local\\SBMS-Sunshine-Cancel-{}-{}",
            std::process::id(),
            NEXT_ID.fetch_add(1, Ordering::Relaxed)
        );
        let wide: Vec<u16> = name.encode_utf16().chain(Some(0)).collect();
        let handle = unsafe { CreateEventW(None, true, false, PCWSTR(wide.as_ptr())) }
            .map_err(|error| format!("Couldn’t create Sunshine cancellation event: {error}"))?;
        Ok(Self {
            handle: unsafe { OwnedHandle::from_raw_handle(handle.0) },
            name,
        })
    }

    fn signal(&self) -> Result<(), String> {
        unsafe { SetEvent(HANDLE(self.handle.as_raw_handle())) }
            .map_err(|error| format!("Couldn’t cancel Sunshine startup: {error}"))
    }
}

fn invoke_deploy(
    script: &PathBuf,
    group_id: u32,
    display_id: &str,
    requested_port: u16,
    capture: CaptureBackend,
    cancellation: &Cancellation,
    on_wait: impl FnMut(),
) -> Result<Option<DeployResult>, String> {
    let cancel_event = CancelEvent::create()?;
    let mut command = deploy_command(script, group_id, display_id, requested_port, capture);
    command.arg("-CancelEventName").arg(&cancel_event.name);
    let Some(output) = wait_for_deploy(command, cancellation, &cancel_event, on_wait)? else {
        return Ok(None);
    };
    let response = parse_response(&output.stdout, &output.stderr)?;
    if !response.ok && response.cancelled {
        return Ok(None);
    }
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
    let process = response
        .process_start_time_file_time
        .ok_or_else(|| "Sunshine manager did not report its process creation time".to_string())
        .and_then(|start| ManagedProcess::open(pid, start));
    match process {
        Ok(process) => Ok(Some(DeployResult { port, process })),
        Err(error) => {
            if let Err(cleanup_error) = invoke_stop(script, Some(group_id)) {
                diagnostics::log(Level::Warn, "sunshine", "cleanup", None, cleanup_error);
            }
            Err(error)
        }
    }
}

fn wait_for_deploy(
    mut command: Command,
    cancellation: &Cancellation,
    cancel_event: &CancelEvent,
    mut on_wait: impl FnMut(),
) -> Result<Option<Output>, String> {
    if cancellation.is_cancelled() {
        return Ok(None);
    }
    let child = command
        .spawn()
        .map_err(|error| format!("Couldn’t launch the Sunshine manager: {error}"))?;
    // Drain both pipes while waiting so manager output cannot block cancellation.
    thread::scope(|scope| {
        let (tx, rx) = mpsc::channel();
        scope.spawn(move || {
            let _ = tx.send(child.wait_with_output());
        });
        let mut signalled = false;
        loop {
            on_wait();
            if cancellation.is_cancelled() && !signalled {
                cancel_event.signal()?;
                signalled = true;
            }
            match rx.recv_timeout(POLL_INTERVAL) {
                Ok(output) => {
                    return output
                        .map(Some)
                        .map_err(|error| format!("Couldn’t wait for Sunshine manager: {error}"));
                }
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => {
                    return Err("Sunshine manager output reader stopped".into());
                }
            }
        }
    })
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

fn invoke_stop(script: &PathBuf, group_id: Option<u32>) -> Result<(), String> {
    let mut command = powershell_command(script);
    command.arg("-Action");
    if let Some(group_id) = group_id {
        command
            .arg("Stop")
            .arg("-GroupId")
            .arg(group_id.to_string());
    } else {
        command.arg("StopAll");
    }
    let output = command
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
mod tests;
