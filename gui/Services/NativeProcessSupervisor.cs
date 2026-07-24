using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SBMSGui
{
    internal sealed class NativeProcessSupervisor
    {
        private const int WM_CLOSE = 0x0010;
        private const int StopTimeoutMs = 3000;
        private const int LaunchGateAckTimeoutMs = 5000;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly string executablePath;
        private readonly string workingDirectory;
        private readonly Action<string> output;
        private readonly Action<Action> dispatch;
        private readonly ChildProcessJob childProcessJob;
        private readonly string migrationJournalDirectory;
        private readonly Func<Process, bool> terminateFailedStart;
        private readonly List<Process> betaProcesses = new List<Process>();
        private Process primaryProcess;

        public NativeProcessSupervisor(
            string executablePath,
            string workingDirectory,
            Action<string> output,
            Action<Action> dispatch,
            ChildProcessJob childProcessJob,
            string migrationJournalDirectory,
            Func<Process, bool> terminateFailedStart = null)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException("Executable path is required.", "executablePath");
            }
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                throw new ArgumentException("Working directory is required.", "workingDirectory");
            }

            this.executablePath = executablePath;
            this.workingDirectory = workingDirectory;
            this.output = output;
            this.dispatch = dispatch;
            if (childProcessJob == null)
            {
                throw new ArgumentNullException("childProcessJob");
            }
            this.childProcessJob = childProcessJob;
            if (string.IsNullOrWhiteSpace(migrationJournalDirectory))
            {
                throw new ArgumentException("Migration journal directory is required.", "migrationJournalDirectory");
            }
            this.migrationJournalDirectory = migrationJournalDirectory;
            this.terminateFailedStart = terminateFailedStart ?? KillProcessQuietly;
            Directory.CreateDirectory(this.migrationJournalDirectory);
        }

        public bool IsPrimaryRunning
        {
            get { return IsProcessRunning(primaryProcess); }
        }

        public bool HasRunningBetaProcess
        {
            get
            {
                for (int i = 0; i < betaProcesses.Count; ++i)
                {
                    if (IsProcessRunning(betaProcesses[i]))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public bool IsAnyRunning
        {
            get { return IsPrimaryRunning || HasRunningBetaProcess; }
        }

        public bool HasCleanupPending
        {
            get { return primaryProcess != null || betaProcesses.Count > 0; }
        }

        public bool StartPrimary(string arguments, Action<int> exited, out string error)
        {
            error = "";
            if (IsProcessRunning(primaryProcess))
            {
                error = "A primary native process is already running.";
                return false;
            }
            DisposeQuietly(primaryProcess);
            primaryProcess = null;

            ChildLaunchGate launchGate = new ChildLaunchGate();
            var gateWaiting = new ManualResetEvent(false);
            Process startedProcess = CreateProcess(AppendStartGate(
                AppendMigrationJournal(arguments),
                launchGate.Name));
            DataReceivedEventHandler gateHandler = delegate(object sender, DataReceivedEventArgs e)
            {
                if (string.Equals(e.Data, "start_gate=waiting", StringComparison.Ordinal))
                {
                    gateWaiting.Set();
                }
            };
            startedProcess.OutputDataReceived += gateHandler;
            startedProcess.EnableRaisingEvents = true;
            AttachOutput(startedProcess);
            startedProcess.Exited += delegate
            {
                Dispatch(delegate
                {
                    if (primaryProcess != startedProcess)
                    {
                        return;
                    }
                    int exitCode = GetExitCode(startedProcess);
                    primaryProcess = null;
                    try
                    {
                        if (exited != null)
                        {
                            exited(exitCode);
                        }
                    }
                    finally
                    {
                        DisposeQuietly(startedProcess);
                    }
                });
            };

            primaryProcess = startedProcess;
            bool committed = false;
            bool processStarted = false;
            try
            {
                startedProcess.Start();
                processStarted = true;
                string assignmentError;
                if (!childProcessJob.TryAssign(startedProcess, out assignmentError))
                {
                    throw new InvalidOperationException(
                        "Failed to contain native process before launch: " + assignmentError);
                }
                startedProcess.BeginOutputReadLine();
                startedProcess.BeginErrorReadLine();
                if (!WaitForLaunchGateAck(startedProcess, gateWaiting, LaunchGateAckTimeoutMs))
                {
                    throw new TimeoutException(
                        "Native process did not acknowledge its launch gate within " +
                        LaunchGateAckTimeoutMs + "ms.");
                }
                launchGate.Release();
                committed = true;
                return true;
            }
            catch (Exception ex)
            {
                bool exitedAfterFailure = !processStarted;
                if (!committed && processStarted)
                {
                    exitedAfterFailure = terminateFailedStart(startedProcess);
                }
                if (exitedAfterFailure && primaryProcess == startedProcess)
                {
                    primaryProcess = null;
                }
                error = ex.Message +
                    (exitedAfterFailure ? "" : " Cleanup pending: native process did not exit.");
                if (exitedAfterFailure)
                {
                    DisposeQuietly(startedProcess);
                }
                return false;
            }
            finally
            {
                startedProcess.OutputDataReceived -= gateHandler;
                gateWaiting.Dispose();
                launchGate.Dispose();
            }
        }

        public bool StartBeta(string arguments, int index, Action<int, int> exited, out string error)
        {
            error = "";
            ChildLaunchGate launchGate = new ChildLaunchGate();
            var gateWaiting = new ManualResetEvent(false);
            Process startedProcess = CreateProcess(AppendStartGate(
                AppendMigrationJournal(arguments),
                launchGate.Name));
            DataReceivedEventHandler gateHandler = delegate(object sender, DataReceivedEventArgs e)
            {
                if (string.Equals(e.Data, "start_gate=waiting", StringComparison.Ordinal))
                {
                    gateWaiting.Set();
                }
            };
            startedProcess.OutputDataReceived += gateHandler;
            betaProcesses.Add(startedProcess);
            startedProcess.EnableRaisingEvents = true;
            AttachOutput(startedProcess);
            startedProcess.Exited += delegate
            {
                Dispatch(delegate
                {
                    if (!betaProcesses.Contains(startedProcess))
                    {
                        return;
                    }
                    if (exited != null)
                    {
                        exited(index, GetExitCode(startedProcess));
                    }
                });
            };

            bool committed = false;
            bool processStarted = false;
            try
            {
                startedProcess.Start();
                processStarted = true;
                string assignmentError;
                if (!childProcessJob.TryAssign(startedProcess, out assignmentError))
                {
                    throw new InvalidOperationException(
                        "Failed to contain beta native process before launch: " + assignmentError);
                }
                startedProcess.BeginOutputReadLine();
                startedProcess.BeginErrorReadLine();
                if (!WaitForLaunchGateAck(startedProcess, gateWaiting, LaunchGateAckTimeoutMs))
                {
                    throw new TimeoutException(
                        "Beta native process did not acknowledge its launch gate within " +
                        LaunchGateAckTimeoutMs + "ms.");
                }
                launchGate.Release();
                committed = true;
                return true;
            }
            catch (Exception ex)
            {
                bool exitedAfterFailure = !processStarted;
                if (!committed && processStarted)
                {
                    exitedAfterFailure = terminateFailedStart(startedProcess);
                }
                if (exitedAfterFailure)
                {
                    betaProcesses.Remove(startedProcess);
                    DisposeQuietly(startedProcess);
                }
                error = ex.Message +
                    (exitedAfterFailure ? "" : " Cleanup pending: beta native process did not exit.");
                return false;
            }
            finally
            {
                startedProcess.OutputDataReceived -= gateHandler;
                gateWaiting.Dispose();
                launchGate.Dispose();
            }
        }

        public bool Capture(string arguments, int timeoutMs, out string capturedOutput)
        {
            using (Process captureProcess = CreateProcess(arguments))
            {
                return CaptureProcessOutput(captureProcess, timeoutMs, out capturedOutput);
            }
        }

        public ProcessStopResult StopPrimary(Action<string> log)
        {
            Process process = primaryProcess;
            ProcessStopResult result = StopProcess(process, "正常关闭超时，强制结束", log);
            if (result.Exited)
            {
                if (ReferenceEquals(primaryProcess, process))
                {
                    primaryProcess = null;
                }
                DisposeQuietly(process);
            }
            return result;
        }

        public ProcessStopResult StopAllBeta(Action<string> log)
        {
            var combined = new ProcessStopResult
            {
                TimeoutMilliseconds = StopTimeoutMs,
                Exited = true
            };
            for (int i = betaProcesses.Count - 1; i >= 0; --i)
            {
                RequestClose(betaProcesses[i]);
            }

            for (int i = betaProcesses.Count - 1; i >= 0; --i)
            {
                Process process = betaProcesses[i];
                ProcessStopResult current = WaitAndKillIfNeeded(
                    process,
                    "beta native 正常关闭超时，强制结束",
                    log);
                combined.HadProcess = combined.HadProcess || current.HadProcess;
                combined.Forced = combined.Forced || current.Forced;
                combined.Exited = combined.Exited && current.Exited;
                if (!string.IsNullOrWhiteSpace(current.Error))
                {
                    combined.Error = string.IsNullOrWhiteSpace(combined.Error)
                        ? current.Error
                        : combined.Error + "; " + current.Error;
                }
                if (current.Exited)
                {
                    if (i < betaProcesses.Count &&
                        ReferenceEquals(betaProcesses[i], process))
                    {
                        betaProcesses.RemoveAt(i);
                    }
                    else
                    {
                        betaProcesses.Remove(process);
                    }
                    DisposeQuietly(process);
                }
            }
            if (!combined.HadProcess)
            {
                combined.Exited = true;
            }
            combined.Graceful = combined.Exited &&
                !combined.Forced &&
                string.IsNullOrWhiteSpace(combined.Error);
            return combined;
        }

        public void RemoveExitedBetaProcesses()
        {
            for (int i = betaProcesses.Count - 1; i >= 0; --i)
            {
                Process process = betaProcesses[i];
                if (!IsProcessRunning(process))
                {
                    betaProcesses.RemoveAt(i);
                    DisposeQuietly(process);
                }
            }
        }

        private Process CreateProcess(string arguments)
        {
            var process = new Process();
            process.StartInfo.FileName = executablePath;
            process.StartInfo.Arguments = arguments ?? "";
            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            return process;
        }

        private void AttachOutput(Process process)
        {
            if (output == null)
            {
                return;
            }

            DataReceivedEventHandler handler = delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    try
                    {
                        output(e.Data);
                    }
                    catch
                    {
                        // The UI dispatcher may disappear while processes are stopping.
                    }
                }
            };
            process.OutputDataReceived += handler;
            process.ErrorDataReceived += handler;
        }

        private void Dispatch(Action action)
        {
            try
            {
                if (dispatch != null)
                {
                    dispatch(action);
                }
                else
                {
                    action();
                }
            }
            catch
            {
                // Shutdown owns synchronous cleanup; a disposed dispatcher must not crash it.
            }
        }

        private bool CaptureProcessOutput(Process process, int timeoutMs, out string capturedOutput)
        {
            var buffer = new StringBuilder();
            DataReceivedEventHandler appendLine = delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null)
                {
                    return;
                }
                lock (buffer)
                {
                    buffer.AppendLine(e.Data);
                }
            };

            try
            {
                process.OutputDataReceived += appendLine;
                process.ErrorDataReceived += appendLine;
                process.Start();
                string assignmentError;
                if (!childProcessJob.TryAssign(process, out assignmentError))
                {
                    KillProcessQuietly(process);
                    capturedOutput = "Failed to contain capture process: " + assignmentError;
                    return false;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMs))
                {
                    KillProcessQuietly(process);
                    lock (buffer)
                    {
                        capturedOutput = "timeout: " + process.StartInfo.FileName + " " + process.StartInfo.Arguments + Environment.NewLine + buffer;
                    }
                    return false;
                }
                process.WaitForExit();
                lock (buffer)
                {
                    capturedOutput = buffer.ToString();
                }
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                capturedOutput = ex.Message;
                return false;
            }
        }

        private static ProcessStopResult StopProcess(Process process, string timeoutMessage, Action<string> log)
        {
            RequestClose(process);
            return WaitAndKillIfNeeded(process, timeoutMessage, log);
        }

        private static void RequestClose(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.CloseMainWindow();
                    PostCloseToProcess(process.Id);
                }
            }
            catch
            {
            }
        }

        private static ProcessStopResult WaitAndKillIfNeeded(Process process, string timeoutMessage, Action<string> log)
        {
            var result = new ProcessStopResult
            {
                HadProcess = process != null,
                TimeoutMilliseconds = StopTimeoutMs
            };
            if (process == null)
            {
                result.Exited = true;
                result.Graceful = true;
                return result;
            }
            try
            {
                if (!process.HasExited && !process.WaitForExit(StopTimeoutMs))
                {
                    if (log != null)
                    {
                        log(timeoutMessage);
                    }
                    process.Kill();
                    result.Forced = true;
                    if (!process.WaitForExit(1000))
                    {
                        result.Error = "process did not exit after force kill";
                    }
                }
                result.Exited = process.HasExited;
                result.Graceful = result.Exited && !result.Forced;
                result.ExitCode = result.Exited ? GetExitCode(process) : -1;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                bool exited;
                result.Exited = TryGetExited(process, out exited) && exited;
                result.ExitCode = result.Exited ? GetExitCode(process) : -1;
            }
            return result;
        }

        private static bool IsProcessRunning(Process process)
        {
            try
            {
                return process != null && !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetExited(Process process, out bool exited)
        {
            exited = false;
            if (process == null)
            {
                exited = true;
                return true;
            }
            try
            {
                exited = process.HasExited;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int GetExitCode(Process process)
        {
            try
            {
                return process == null ? -1 : process.ExitCode;
            }
            catch
            {
                return -1;
            }
        }

        private static bool KillProcessQuietly(Process process)
        {
            try
            {
                if (process == null || process.HasExited)
                {
                    return true;
                }
                process.Kill();
                process.WaitForExit(1000);
                bool exited;
                if (TryGetExited(process, out exited))
                {
                    return exited;
                }
            }
            catch
            {
                bool exited;
                if (TryGetExited(process, out exited))
                {
                    return exited;
                }
            }
            return false;
        }

        private static void DisposeQuietly(Process process)
        {
            try
            {
                if (process != null)
                {
                    process.Dispose();
                }
            }
            catch
            {
            }
        }

        private static void PostCloseToProcess(int processId)
        {
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == (uint)processId)
                {
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
        }

        private static bool WaitForLaunchGateAck(
            Process process,
            WaitHandle gateWaiting,
            int timeoutMilliseconds)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                int remaining = timeoutMilliseconds - (int)timer.ElapsedMilliseconds;
                if (gateWaiting.WaitOne(Math.Max(1, Math.Min(50, remaining))))
                {
                    return true;
                }
                if (!IsProcessRunning(process))
                {
                    return false;
                }
            }
            return gateWaiting.WaitOne(0);
        }

        private static string AppendStartGate(string arguments, string gateName)
        {
            string prefix = string.IsNullOrWhiteSpace(arguments) ? "" : arguments.Trim() + " ";
            return prefix + "--start-gate " + gateName;
        }

        private string AppendMigrationJournal(string arguments)
        {
            string path = Path.Combine(
                migrationJournalDirectory,
                "migration-" + Guid.NewGuid().ToString("N") + ".journal");
            string prefix = string.IsNullOrWhiteSpace(arguments) ? "" : arguments.Trim() + " ";
            return prefix + "--migration-journal \"" + path + "\"";
        }
    }
}
