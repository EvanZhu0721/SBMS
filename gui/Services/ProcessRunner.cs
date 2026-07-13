using System;
using System.Diagnostics;
using System.Text;

namespace SBMSGui
{
    internal static class ProcessRunner
    {
        public static bool Capture(string fileName, string arguments, string workingDirectory, Encoding encoding, int timeoutMs, out string output)
        {
            using (var process = new Process())
            {
                process.StartInfo.FileName = fileName;
                process.StartInfo.Arguments = arguments ?? "";
                process.StartInfo.WorkingDirectory = workingDirectory ?? "";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.StandardOutputEncoding = encoding;
                process.StartInfo.StandardErrorEncoding = encoding;

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
                        KillQuietly(process);
                        lock (buffer)
                        {
                            output = "timeout: " + fileName + " " + arguments + Environment.NewLine + buffer;
                        }
                        return false;
                    }
                    process.WaitForExit();
                    lock (buffer)
                    {
                        output = buffer.ToString();
                    }
                    return process.ExitCode == 0;
                }
                catch (Exception ex)
                {
                    output = ex.Message;
                    return false;
                }
            }
        }

        private static void KillQuietly(Process process)
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
    }
}
