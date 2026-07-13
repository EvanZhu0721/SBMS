using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SBMSGui
{
    internal sealed class NativeProcessSupervisor
    {
        private const int WM_CLOSE = 0x0010;
        private const int StopTimeoutMs = 3000;

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
        private readonly List<Process> betaProcesses = new List<Process>();
        private Process primaryProcess;

        public NativeProcessSupervisor(
            string executablePath,
            string workingDirectory,
            Action<string> output,
            Action<Action> dispatch)
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

        public bool StartPrimary(string arguments, Action<int> exited, out string error)
        {
            error = "";
            Process startedProcess = CreateProcess(arguments);
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
            try
            {
                startedProcess.Start();
                startedProcess.BeginOutputReadLine();
                startedProcess.BeginErrorReadLine();
                return true;
            }
            catch (Exception ex)
            {
                if (primaryProcess == startedProcess)
                {
                    primaryProcess = null;
                }
                error = ex.Message;
                DisposeQuietly(startedProcess);
                return false;
            }
        }

        public bool StartBeta(string arguments, int index, Action<int, int> exited, out string error)
        {
            error = "";
            Process startedProcess = CreateProcess(arguments);
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

            try
            {
                startedProcess.Start();
                startedProcess.BeginOutputReadLine();
                startedProcess.BeginErrorReadLine();
                return true;
            }
            catch (Exception ex)
            {
                betaProcesses.Remove(startedProcess);
                error = ex.Message;
                KillProcessQuietly(startedProcess);
                DisposeQuietly(startedProcess);
                return false;
            }
        }

        public bool Capture(string arguments, int timeoutMs, out string capturedOutput)
        {
            using (Process captureProcess = CreateProcess(arguments))
            {
                return CaptureProcessOutput(captureProcess, timeoutMs, out capturedOutput);
            }
        }

        public void StopPrimary(Action<string> log)
        {
            Process process = primaryProcess;
            primaryProcess = null;
            StopProcess(process, "正常关闭超时，强制结束", log);
            DisposeQuietly(process);
        }

        public void StopAllBeta(Action<string> log)
        {
            for (int i = betaProcesses.Count - 1; i >= 0; --i)
            {
                RequestClose(betaProcesses[i]);
            }

            for (int i = betaProcesses.Count - 1; i >= 0; --i)
            {
                Process process = betaProcesses[i];
                WaitAndKillIfNeeded(process, "beta native 正常关闭超时，强制结束", log);
                DisposeQuietly(process);
            }
            betaProcesses.Clear();
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

        private static bool CaptureProcessOutput(Process process, int timeoutMs, out string capturedOutput)
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

        private static void StopProcess(Process process, string timeoutMessage, Action<string> log)
        {
            RequestClose(process);
            WaitAndKillIfNeeded(process, timeoutMessage, log);
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

        private static void WaitAndKillIfNeeded(Process process, string timeoutMessage, Action<string> log)
        {
            try
            {
                if (process != null && !process.HasExited && !process.WaitForExit(StopTimeoutMs))
                {
                    if (log != null)
                    {
                        log(timeoutMessage);
                    }
                    process.Kill();
                }
            }
            catch
            {
            }
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

        private static void KillProcessQuietly(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch
            {
            }
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
    }
}
