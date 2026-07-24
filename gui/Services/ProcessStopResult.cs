using System;
using System.Globalization;

namespace SBMSGui
{
    internal sealed class ProcessStopResult
    {
        public bool HadProcess { get; set; }
        public bool Graceful { get; set; }
        public bool Forced { get; set; }
        public bool Exited { get; set; }
        public int ExitCode { get; set; }
        public int TimeoutMilliseconds { get; set; }
        public string Error { get; set; }

        public ProcessStopResult()
        {
            ExitCode = -1;
            Error = "";
        }

        public bool Succeeded
        {
            get { return !HadProcess || (Exited && string.IsNullOrWhiteSpace(Error)); }
        }

        public string Format(string component)
        {
            return (component ?? "process") +
                   " stop timeoutMs=" + TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture) +
                   " graceful=" + Graceful.ToString(CultureInfo.InvariantCulture) +
                   " forced=" + Forced.ToString(CultureInfo.InvariantCulture) +
                   " exited=" + Exited.ToString(CultureInfo.InvariantCulture) +
                   " exitCode=" + ExitCode.ToString(CultureInfo.InvariantCulture) +
                   (string.IsNullOrWhiteSpace(Error) ? "" : " error=" + Error);
        }
    }
}
