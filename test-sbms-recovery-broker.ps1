$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$BrokerPath = Join-Path $Root "SBMSRecoveryBroker.exe"
if (-not (Test-Path -LiteralPath $BrokerPath -PathType Leaf)) {
    throw "Missing recovery broker binary: $BrokerPath"
}

$TestRoot = Join-Path ([IO.Path]::GetTempPath()) ("SBMS-RecoveryBrokerTests-" + [guid]::NewGuid().ToString("N"))
$SessionDirectory = Join-Path $TestRoot "session"
$ownerProcess = $null
$brokerProcess = $null
$windowProcess = $null
$leaseHolderProcess = $null
try {
    New-Item -ItemType Directory -Path $SessionDirectory -Force | Out-Null
    $ownerProcess = Start-Process `
        -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList @("-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30") `
        -WindowStyle Hidden `
        -PassThru
    $ownerStartTicks = $ownerProcess.StartTime.ToUniversalTime().Ticks

    $brokerProcess = New-Object Diagnostics.Process
    $brokerProcess.StartInfo.FileName = $BrokerPath
    $brokerProcess.StartInfo.Arguments =
        "watch --gui-pid $($ownerProcess.Id) --gui-start-time $ownerStartTicks --session-dir `"$SessionDirectory`""
    $brokerProcess.StartInfo.WorkingDirectory = $Root
    $brokerProcess.StartInfo.UseShellExecute = $false
    $brokerProcess.StartInfo.CreateNoWindow = $true
    $brokerProcess.StartInfo.RedirectStandardOutput = $true
    $brokerProcess.StartInfo.RedirectStandardError = $true
    $brokerProcess.StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $brokerProcess.StartInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    if (-not $brokerProcess.Start()) {
        throw "Failed to start recovery broker."
    }

    $readyTask = $brokerProcess.StandardOutput.ReadLineAsync()
    if (-not $readyTask.Wait(5000) -or $readyTask.Result -notlike "broker=ready*") {
        throw "Recovery broker did not publish readiness."
    }

    $helperSourcePath = Join-Path $TestRoot "RecoveryWindowHelper.cs"
    $helperPath = Join-Path $TestRoot "RecoveryWindowHelper.exe"
    $windowStatePath = Join-Path $TestRoot "window-state.txt"
    $moveSignalPath = Join-Path $TestRoot "move-window.signal"
    $helperSource = @'
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class RecoveryWindowHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr process,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        Application.EnableVisualStyles();
        using (Form form = new Form())
        {
            form.Text = "SBMS recovery test";
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.SetBounds(100, 120, 320, 200);
            Timer timer = new Timer();
            timer.Interval = 20;
            timer.Tick += delegate
            {
                if (!File.Exists(args[1])) return;
                form.SetBounds(2100, 120, 320, 200);
                form.WindowState = FormWindowState.Minimized;
                File.Delete(args[1]);
                File.WriteAllText(args[1] + ".done", "moved");
            };
            form.Shown += delegate
            {
                FileTime creation, exit, kernel, user;
                using (Process process = Process.GetCurrentProcess())
                {
                    if (!GetProcessTimes(process.Handle, out creation, out exit, out kernel, out user))
                        throw new InvalidOperationException("GetProcessTimes failed.");
                    long creationTime = ((long)creation.High << 32) | creation.Low;
                    File.WriteAllText(
                        args[0],
                        process.Id.ToString(CultureInfo.InvariantCulture) + "|" +
                        form.Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture) + "|" +
                        creationTime.ToString("X", CultureInfo.InvariantCulture));
                }
                timer.Start();
            };
            Application.Run(form);
        }
        return 0;
    }
}
'@
    [IO.File]::WriteAllText($helperSourcePath, $helperSource, ([Text.UTF8Encoding]::new($false)))
    $compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        $compiler = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    }
    & $compiler /nologo /target:winexe "/out:$helperPath" /reference:System.Windows.Forms.dll $helperSourcePath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to compile the real-window recovery helper."
    }
    $windowProcess = Start-Process `
        -FilePath $helperPath `
        -ArgumentList @("`"$windowStatePath`"", "`"$moveSignalPath`"") `
        -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path -LiteralPath $windowStatePath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 20
    }
    if (-not (Test-Path -LiteralPath $windowStatePath -PathType Leaf)) {
        throw "Real-window helper did not publish its identity."
    }
    $windowIdentity = ([IO.File]::ReadAllText($windowStatePath, [Text.Encoding]::UTF8)).Split("|")
    $journalPath = Join-Path $SessionDirectory "migration-real-window.journal"
    [IO.File]::WriteAllText(
        $journalPath,
        "SBMSWM2|P|$($windowIdentity[1])|$($windowIdentity[0])|$($windowIdentity[2])|100,120,420,320|2100,120,2420,320|0,0,1000,1000|2000,0,3000,1000|0,1,0,0,0,0,100,120,420,320`r`n",
        ([Text.UTF8Encoding]::new($false)))
    [IO.File]::WriteAllText($moveSignalPath, "move", ([Text.UTF8Encoding]::new($false)))
    $moveDonePath = $moveSignalPath + ".done"
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path -LiteralPath $moveDonePath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 20
    }
    if (-not (Test-Path -LiteralPath $moveDonePath -PathType Leaf)) {
        throw "Real-window helper did not enter the injected migrated state."
    }

    $ownerProcess.Kill()
    if (-not $ownerProcess.WaitForExit(5000)) {
        throw "Test owner did not terminate within 5000ms."
    }
    if (-not $brokerProcess.WaitForExit(5000)) {
        throw "Recovery broker did not finish after its owner was terminated."
    }
    $brokerOutput = $brokerProcess.StandardOutput.ReadToEnd()
    $brokerError = $brokerProcess.StandardError.ReadToEnd()
    if ($brokerProcess.ExitCode -ne 0) {
        throw "Recovery broker failed exit=$($brokerProcess.ExitCode): $brokerError"
    }
    if ($brokerOutput -notlike "*broker=clean*") {
        throw "Recovery broker did not report a bounded clean result."
    }
    if ($brokerOutput -notlike "*restored=1*") {
        throw "Recovery broker did not restore the real Win32 window: $brokerOutput"
    }
    if (Test-Path -LiteralPath $journalPath) {
        throw "Resolved real-window migration journal was not removed."
    }
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SBMSRecoveryTestNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct Placement
    {
        public int Length;
        public uint Flags, ShowCommand;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")]
    public static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
}
'@
    $windowHandle = [IntPtr]::new([Convert]::ToInt64($windowIdentity[1], 16))
    $restoredPlacement = New-Object SBMSRecoveryTestNative+Placement
    $restoredPlacement.Length = [Runtime.InteropServices.Marshal]::SizeOf(
        [type][SBMSRecoveryTestNative+Placement])
    if (-not [SBMSRecoveryTestNative]::GetWindowPlacement($windowHandle, [ref]$restoredPlacement) -or
        $restoredPlacement.ShowCommand -ne 1 -or
        $restoredPlacement.NormalPosition.Left -ne 100 -or
        $restoredPlacement.NormalPosition.Top -ne 120 -or
        $restoredPlacement.NormalPosition.Right -ne 420 -or
        $restoredPlacement.NormalPosition.Bottom -ne 320) {
        throw "Real Win32 window placement was not restored from its minimized migrated state."
    }

    $staleRoot = Join-Path $TestRoot "stale"
    $staleSession = Join-Path $staleRoot "old-session"
    New-Item -ItemType Directory -Path $staleSession -Force | Out-Null
    $staleJournal = Join-Path $staleSession "migration-stale.journal"
    [IO.File]::WriteAllText(
        $staleJournal,
        "SBMSWM1|P|0|999999|1|0,0,100,100|100,100,200,200|0,0,100,100|100,100,200,200`r`n",
        ([Text.UTF8Encoding]::new($false)))
    & $BrokerPath recover --root $staleRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Stale recovery sweep failed with exit code $LASTEXITCODE."
    }
    if (Test-Path -LiteralPath $staleJournal) {
        throw "Resolved stale migration journal was not removed."
    }

    $leaseRoot = Join-Path $TestRoot "lease-root"
    $leaseSession = Join-Path $leaseRoot "contended-session"
    New-Item -ItemType Directory -Path $leaseSession -Force | Out-Null
    $leaseReleasePath = Join-Path $TestRoot "release-lease.signal"
    $leaseHolderSourcePath = Join-Path $TestRoot "RecoveryLeaseHolder.cs"
    $leaseHolderPath = Join-Path $TestRoot "RecoveryLeaseHolder.exe"
    $leaseHolderSource = @'
using System;
using System.IO;
using System.Threading;
using SBMSGui;

internal static class RecoveryLeaseHolder
{
    private static int Main(string[] args)
    {
        WindowMigrationRecoveryLease lease;
        if (args.Length != 2 ||
            !WindowMigrationRecoveryLease.TryAcquire(args[0], 0, out lease))
        {
            return 2;
        }
        using (lease)
        {
            Console.WriteLine("lease=ready");
            Console.Out.Flush();
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(args[1]) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
            return File.Exists(args[1]) ? 0 : 3;
        }
    }
}
'@
    [IO.File]::WriteAllText(
        $leaseHolderSourcePath,
        $leaseHolderSource,
        ([Text.UTF8Encoding]::new($false)))
    & $compiler /nologo /target:exe "/out:$leaseHolderPath" `
        (Join-Path $Root "gui\Services\WindowMigrationJournal.cs") `
        $leaseHolderSourcePath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to compile the cross-process recovery lease holder."
    }
    $leaseHolderProcess = New-Object Diagnostics.Process
    $leaseHolderProcess.StartInfo.FileName = $leaseHolderPath
    $leaseHolderProcess.StartInfo.UseShellExecute = $false
    $leaseHolderProcess.StartInfo.CreateNoWindow = $true
    $leaseHolderProcess.StartInfo.RedirectStandardOutput = $true
    $leaseHolderProcess.StartInfo.Arguments =
        "`"$leaseSession`" `"$leaseReleasePath`""
    if (-not $leaseHolderProcess.Start()) {
        throw "Failed to start the cross-process recovery lease holder."
    }
    $leaseReadyTask = $leaseHolderProcess.StandardOutput.ReadLineAsync()
    if (-not $leaseReadyTask.Wait(5000) -or $leaseReadyTask.Result -ne "lease=ready") {
        throw "Cross-process recovery lease holder did not publish readiness."
    }

    $contendedBroker = New-Object Diagnostics.Process
    try {
        $contendedBroker.StartInfo.FileName = $BrokerPath
        $contendedBroker.StartInfo.UseShellExecute = $false
        $contendedBroker.StartInfo.CreateNoWindow = $true
        $contendedBroker.StartInfo.Arguments =
            "recover --root `"$leaseRoot`""
        if (-not $contendedBroker.Start()) {
            throw "Failed to start the contended recovery broker."
        }
        if ($contendedBroker.WaitForExit(300)) {
            throw "Recovery broker bypassed a lease held by another process."
        }
        [IO.File]::WriteAllText(
            $leaseReleasePath,
            "release",
            ([Text.UTF8Encoding]::new($false)))
        if (-not $leaseHolderProcess.WaitForExit(5000) -or $leaseHolderProcess.ExitCode -ne 0) {
            throw "Cross-process recovery lease holder did not release cleanly."
        }
        if (-not $contendedBroker.WaitForExit(5000) -or $contendedBroker.ExitCode -ne 0) {
            throw "Recovery broker did not continue after the recovery lease was released."
        }
    } finally {
        if (-not $contendedBroker.HasExited) {
            $contendedBroker.Kill()
            $contendedBroker.WaitForExit(1000) | Out-Null
        }
        $contendedBroker.Dispose()
    }

    Write-Host "Recovery broker tests passed: placement restore, stale sweep, and cross-process lease contention."
} finally {
    foreach ($process in @($brokerProcess, $ownerProcess, $windowProcess, $leaseHolderProcess)) {
        if ($null -eq $process) {
            continue
        }
        try {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit(1000) | Out-Null
            }
        } catch {
        }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $TestRoot) {
        Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
