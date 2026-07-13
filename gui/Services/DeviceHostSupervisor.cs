using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SBMSGui
{
    internal sealed class DeviceHostSupervisor
    {
        private const uint EventModifyState = 0x0002;
        private const string StopEventName = "Local\\SBMSDeviceHostStop";
        private const int StopTimeoutMilliseconds = 4000;

        private readonly object sync = new object();
        private readonly string executablePath;
        private readonly string workingDirectory;
        private readonly Action<string> outputCallback;
        private readonly Action<Action> dispatch;
        private readonly StringBuilder output = new StringBuilder();

        private Process process;
        private Process stoppingProcess;

        public DeviceHostSupervisor(
            string executablePath,
            string workingDirectory,
            Action<string> outputCallback,
            Action<Action> dispatch)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException("Device host executable path is required.", "executablePath");
            }
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                throw new ArgumentException("Device host working directory is required.", "workingDirectory");
            }

            this.executablePath = executablePath;
            this.workingDirectory = workingDirectory;
            this.outputCallback = outputCallback;
            this.dispatch = dispatch;
        }

        public bool IsRunning
        {
            get
            {
                lock (sync)
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
            }
        }

        public string OutputSnapshot
        {
            get
            {
                lock (sync)
                {
                    return output.ToString();
                }
            }
        }

        public bool Start(int count, Action<int> exitCallback, out string error)
        {
            error = "";

            Process previous = null;
            lock (sync)
            {
                if (IsProcessRunning(process))
                {
                    return true;
                }

                previous = process;
                process = null;
                stoppingProcess = null;
                output.Length = 0;
            }
            DisposeQuietly(previous);

            // Preserve the legacy startup behavior: release a stale host from an
            // earlier GUI instance before creating the new process.
            SignalStopEvent();

            int requestedCount = Math.Max(1, Math.Min(count, 3));
            var startedProcess = new Process();
            startedProcess.StartInfo.FileName = executablePath;
            startedProcess.StartInfo.Arguments = "--count " + requestedCount;
            startedProcess.StartInfo.WorkingDirectory = workingDirectory;
            startedProcess.StartInfo.UseShellExecute = false;
            startedProcess.StartInfo.RedirectStandardOutput = true;
            startedProcess.StartInfo.RedirectStandardError = true;
            startedProcess.StartInfo.CreateNoWindow = true;
            startedProcess.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            startedProcess.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            startedProcess.EnableRaisingEvents = true;
            startedProcess.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                HandleOutput(startedProcess, e.Data);
            };
            startedProcess.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                HandleOutput(startedProcess, e.Data);
            };
            startedProcess.Exited += delegate
            {
                HandleExit(startedProcess, exitCallback);
            };

            lock (sync)
            {
                process = startedProcess;
            }

            try
            {
                startedProcess.Start();
                startedProcess.BeginOutputReadLine();
                startedProcess.BeginErrorReadLine();
                return true;
            }
            catch (Exception ex)
            {
                lock (sync)
                {
                    if (ReferenceEquals(process, startedProcess))
                    {
                        process = null;
                    }
                    if (ReferenceEquals(stoppingProcess, startedProcess))
                    {
                        stoppingProcess = null;
                    }
                }
                DisposeQuietly(startedProcess);
                error = ex.Message;
                return false;
            }
        }

        public void Stop(Action<string> logCallback)
        {
            Process target;
            lock (sync)
            {
                target = process;
                if (target == null)
                {
                    return;
                }
                stoppingProcess = target;
            }

            SignalStopEvent();
            try
            {
                if (!target.HasExited && !target.WaitForExit(StopTimeoutMilliseconds))
                {
                    if (logCallback != null)
                    {
                        logCallback("虚拟显示器 host 正常关闭超时，强制结束");
                    }
                    target.Kill();
                }
            }
            catch
            {
            }
            finally
            {
                lock (sync)
                {
                    if (ReferenceEquals(process, target))
                    {
                        process = null;
                    }
                    if (ReferenceEquals(stoppingProcess, target))
                    {
                        stoppingProcess = null;
                    }
                }
                DisposeQuietly(target);
            }
        }

        private void HandleOutput(Process owner, string line)
        {
            if (line == null)
            {
                return;
            }

            lock (sync)
            {
                if (!ReferenceEquals(process, owner))
                {
                    return;
                }
                output.AppendLine(line);
            }

            if (outputCallback == null)
            {
                return;
            }

            Dispatch(delegate
            {
                lock (sync)
                {
                    if (!ReferenceEquals(process, owner))
                    {
                        return;
                    }
                }
                outputCallback(line);
            });
        }

        private void HandleExit(Process owner, Action<int> exitCallback)
        {
            int exitCode = GetExitCode(owner);
            Dispatch(delegate
            {
                bool intentionalStop;
                lock (sync)
                {
                    // The process reference is the generation token. Delayed callbacks from
                    // an older host must never clear or report against its replacement.
                    if (!ReferenceEquals(process, owner))
                    {
                        return;
                    }

                    intentionalStop = ReferenceEquals(stoppingProcess, owner);
                    process = null;
                    if (intentionalStop)
                    {
                        stoppingProcess = null;
                    }
                }

                if (!intentionalStop && exitCallback != null)
                {
                    exitCallback(exitCode);
                }
                if (!intentionalStop)
                {
                    DisposeQuietly(owner);
                }
            });
        }

        private void Dispatch(Action action)
        {
            try
            {
                if (dispatch == null)
                {
                    action();
                }
                else
                {
                    dispatch(action);
                }
            }
            catch
            {
                // The UI dispatcher can disappear during shutdown. The process remains
                // owned by Stop(), which is responsible for the final synchronous cleanup.
            }
        }

        private static bool IsProcessRunning(Process candidate)
        {
            try
            {
                return candidate != null && !candidate.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static int GetExitCode(Process candidate)
        {
            try
            {
                return candidate != null && candidate.HasExited ? candidate.ExitCode : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static void DisposeQuietly(Process candidate)
        {
            if (candidate == null)
            {
                return;
            }
            try
            {
                candidate.Dispose();
            }
            catch
            {
            }
        }

        private static void SignalStopEvent()
        {
            IntPtr handle = OpenEvent(EventModifyState, false, StopEventName);
            if (handle == IntPtr.Zero)
            {
                return;
            }
            try
            {
                SetEvent(handle);
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenEvent(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
