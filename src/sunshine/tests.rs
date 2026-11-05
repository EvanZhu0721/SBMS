use super::*;
use std::time::Instant;

// Only this temporary manager's hidden, short-lived test processes are started/stopped.
const MANAGER: &str = r#"
param($Action, $GroupId, $DisplayId, [int]$Port, $Capture, $CancelEventName)
$ErrorActionPreference = 'Stop'
function Stop-TestProcess($record) {
    $saved = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    $process = Get-Process -Id $saved.pid -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        $null = $process.Handle
        if ($process.StartTime.ToUniversalTime().ToFileTimeUtc() -eq $saved.start) {
            $process.Kill()
            $process.WaitForExit()
        }
        $process.Dispose()
    }
    Remove-Item -LiteralPath $record
}
if ($Action -ne 'Start') {
    Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.process' | ForEach-Object {
        if ($Action -eq 'StopAll' -or $_.BaseName -eq $GroupId) { Stop-TestProcess $_.FullName }
    }
    '{"ok":true}'
    exit 0
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot "$GroupId.started"), '')
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = 'powershell.exe'
$seconds = if ($DisplayId -eq 'exit') { 2 } else { 20 }
$info.Arguments = '-NoLogo -NoProfile -NonInteractive -Command "Start-Sleep -Seconds ' + $seconds + '; exit 23"'
# Match the real manager: a detached child must not inherit the manager's JSON pipes.
$info.UseShellExecute = $true
$info.WindowStyle = 'Hidden'
$process = [Diagnostics.Process]::Start($info)
$start = $process.StartTime.ToUniversalTime().ToFileTimeUtc()
@{pid=$process.Id; start=$start} | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $PSScriptRoot "$GroupId.process")
if ($DisplayId -eq 'wait' -or $DisplayId -eq 'wait-fail') {
    $cancel = [Threading.EventWaitHandle]::OpenExisting($CancelEventName)
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot "$GroupId.waiting"), '')
    if (-not $cancel.WaitOne(15000)) { throw 'Cancellation was not signalled' }
    Stop-TestProcess (Join-Path $PSScriptRoot "$GroupId.process")
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot "$GroupId.cancelled"), '')
    $cancel.Dispose()
    if ($DisplayId -eq 'wait-fail') {
        '{"ok":false,"cancelled":false,"message":"cancellation cleanup failed"}'
    } else {
        '{"ok":false,"cancelled":true,"message":"cancelled"}'
    }
    exit 1
}
@{ok=$true; port=$Port; pid=$process.Id; processStartTimeFileTime=$start} | ConvertTo-Json -Compress
$process.Dispose()
"#;

struct TestManager(PathBuf);

impl TestManager {
    fn new() -> Self {
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let directory = std::env::temp_dir().join(format!(
            "sbms-sunshine-test-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        std::fs::create_dir(&directory).unwrap();
        let script = directory.join("manager.ps1");
        std::fs::write(&script, MANAGER).unwrap();
        Self(script)
    }

    fn path(&self, name: &str) -> PathBuf {
        self.0.parent().unwrap().join(name)
    }

    fn wait_for(&self, name: &str) {
        let deadline = Instant::now() + Duration::from_secs(10);
        while !self.path(name).exists() {
            assert!(Instant::now() < deadline, "manager did not produce {name}");
            thread::sleep(Duration::from_millis(20));
        }
    }

    fn supervisor(&self) -> (Supervisor, mpsc::Receiver<DeploymentEvent>) {
        let (tx, rx) = mpsc::channel();
        let supervisor = Supervisor::spawn_with_script(Ok(self.0.clone()), move |event| {
            let _ = tx.send(event);
        });
        (supervisor, rx)
    }
}

impl Drop for TestManager {
    fn drop(&mut self) {
        let _ = invoke_stop(&self.0, None);
        let _ = std::fs::remove_dir_all(self.0.parent().unwrap());
    }
}

fn start(
    sender: &SupervisorSender,
    generation: u64,
    group: u32,
    mode: &str,
    cancel: &Arc<AtomicBool>,
) {
    sender.start(
        generation,
        group,
        mode.into(),
        FIRST_BASE_PORT,
        CaptureBackend::Auto,
        Arc::clone(cancel),
    );
}

#[test]
fn exit_is_reported_during_another_deployment() {
    let manager = TestManager::new();
    let (supervisor, events) = manager.supervisor();
    let sender = supervisor.sender();
    let cancel = Arc::new(AtomicBool::new(false));
    start(&sender, 7, 1, "exit", &cancel);
    assert!(
        events
            .recv_timeout(Duration::from_secs(10))
            .unwrap()
            .error
            .is_none()
    );
    start(&sender, 7, 2, "wait", &cancel);
    manager.wait_for("2.waiting");
    let event = events.recv_timeout(Duration::from_secs(6)).unwrap();
    assert_eq!((event.generation, event.group_id), (7, 1));
    assert_eq!(event.port, None);
    assert!(event.error.unwrap().contains("exit code 23"));
    supervisor.shutdown();
    manager.wait_for("2.cancelled");
}

#[test]
fn run_cancellation_cleans_inflight_and_skips_queued_deployments() {
    let manager = TestManager::new();
    let (supervisor, events) = manager.supervisor();
    let sender = supervisor.sender();
    let cancel = Arc::new(AtomicBool::new(false));
    start(&sender, 1, 1, "wait", &cancel);
    manager.wait_for("1.waiting");
    start(&sender, 1, 2, "ready", &cancel);
    cancel.store(true, Ordering::Release);
    manager.wait_for("1.cancelled");
    assert!(!manager.path("1.process").exists());
    assert!(!manager.path("2.started").exists());
    supervisor.shutdown();
    assert!(events.try_iter().next().is_none());
    assert!(!manager.path("2.started").exists());
}

#[test]
fn shutdown_cancels_inflight_and_invalidates_queued_deployments() {
    let manager = TestManager::new();
    let (supervisor, events) = manager.supervisor();
    let sender = supervisor.sender();
    let cancel = Arc::new(AtomicBool::new(false));
    start(&sender, 1, 1, "wait", &cancel);
    manager.wait_for("1.waiting");
    start(&sender, 1, 2, "ready", &cancel);
    supervisor.shutdown();
    assert!(manager.path("1.cancelled").exists());
    assert!(!manager.path("1.process").exists());
    assert!(!manager.path("2.started").exists());
    assert!(events.try_iter().next().is_none());
}

#[test]
fn stop_suppresses_old_exit_and_next_generation_is_monitored() {
    let manager = TestManager::new();
    let (supervisor, events) = manager.supervisor();
    let sender = supervisor.sender();
    let cancel = Arc::new(AtomicBool::new(false));
    start(&sender, 1, 1, "ready", &cancel);
    assert!(
        events
            .recv_timeout(Duration::from_secs(10))
            .unwrap()
            .error
            .is_none()
    );
    sender.stop_all();
    start(&sender, 2, 1, "exit", &cancel);
    let started = events.recv_timeout(Duration::from_secs(10)).unwrap();
    assert_eq!(started.generation, 2);
    assert!(started.error.is_none());
    let exited = events.recv_timeout(Duration::from_secs(6)).unwrap();
    assert_eq!(exited.generation, 2);
    assert!(exited.error.unwrap().contains("exit code 23"));
    supervisor.shutdown();
    assert!(events.try_iter().next().is_none());
}

#[test]
fn process_identity_rejects_a_different_creation_time() {
    let handle =
        unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, std::process::id()) }
            .unwrap();
    let handle = unsafe { OwnedHandle::from_raw_handle(handle.0) };
    let created = process_start_time(&handle).unwrap();
    assert!(ManagedProcess::open(std::process::id(), created).is_ok());
    assert!(ManagedProcess::open(std::process::id(), created + 1).is_err());
}

#[test]
fn cooperative_cancellation_preserves_cleanup_failures() {
    for mode in ["wait", "wait-fail"] {
        let manager = TestManager::new();
        let run = Arc::new(AtomicBool::new(false));
        let cancellation = Cancellation {
            run: Arc::clone(&run),
            stop_epoch: Arc::new(AtomicU64::new(0)),
            epoch: 0,
        };
        let result = invoke_deploy(
            &manager.0,
            1,
            mode,
            FIRST_BASE_PORT,
            CaptureBackend::Auto,
            &cancellation,
            || {
                if manager.path("1.waiting").exists() {
                    run.store(true, Ordering::Release);
                }
            },
        );
        if mode == "wait" {
            assert!(matches!(result, Ok(None)));
        } else {
            assert!(matches!(result, Err(error) if error == "cancellation cleanup failed"));
        }
    }
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
            .map(|arg| arg.to_string_lossy().into_owned())
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
                expected_capture
            ]
        );
    }
}
