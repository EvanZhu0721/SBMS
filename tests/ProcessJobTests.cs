using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SBMSGui;

internal static class ProcessJobTests
{
    private static int assertions;

    private static void Assert(bool condition, string message)
    {
        ++assertions;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "child")
        {
            Thread.Sleep(30000);
            return 0;
        }
        if (args.Length > 0 && args[0] == "owner")
        {
            return RunAbruptOwner(args[1]);
        }

        string executable = Process.GetCurrentProcess().MainModule.FileName;
        using (var job = new ChildProcessJob())
        using (var child = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "child",
            UseShellExecute = false,
            CreateNoWindow = true
        }))
        {
            string error;
            Assert(job.TryAssign(child, out error), "child should enter the job: " + error);
            Assert(!child.HasExited, "assigned child should still be running");
            job.Dispose();
            Assert(child.WaitForExit(5000), "closing the only job handle should terminate the child");
        }

        Console.WriteLine("ProcessJobTests passed: " + assertions.ToString(CultureInfo.InvariantCulture) + " assertions");
        return 0;
    }

    private static int RunAbruptOwner(string statePath)
    {
        string executable = Process.GetCurrentProcess().MainModule.FileName;
        var job = new ChildProcessJob();
        Process child = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "child",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        string error;
        if (!job.TryAssign(child, out error))
        {
            child.Kill();
            File.WriteAllText(statePath, "ERROR " + error);
            return 2;
        }

        File.WriteAllText(statePath, child.Id.ToString(CultureInfo.InvariantCulture));
        TerminateProcess(GetCurrentProcess(), 91);
        return 91;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
