using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

namespace SBMSGui
{
    internal static class SupervisorTests
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

        private static string FindArgument(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; ++i)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    return args[i + 1];
                }
            }
            return "";
        }

        private static int RunFakeChild(string[] args)
        {
            string gateName = FindArgument(args, "--start-gate");
            if (string.IsNullOrWhiteSpace(gateName))
            {
                return 2;
            }
            if (Array.IndexOf(args, "--fake-no-ack") >= 0 ||
                string.Equals(
                    Environment.GetEnvironmentVariable("SBMS_SUPERVISOR_NO_ACK"),
                    "1",
                    StringComparison.Ordinal))
            {
                Thread.Sleep(30000);
                return 3;
            }
            using (EventWaitHandle gate = EventWaitHandle.OpenExisting(gateName))
            {
                Console.WriteLine("start_gate=waiting");
                Console.Out.Flush();
                gate.WaitOne();
                Console.WriteLine("start_gate=released");
                Console.Out.Flush();
                Thread.Sleep(30000);
            }
            return 0;
        }

        private static int RunTests()
        {
            string executable = Assembly.GetExecutingAssembly().Location;
            string root = Path.GetDirectoryName(executable);
            string journals = Path.Combine(
                Path.GetTempPath(),
                "SBMS-SupervisorTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journals);
            try
            {
                using (var job = new ChildProcessJob())
                {
                    var native = new NativeProcessSupervisor(
                        executable,
                        root,
                        delegate { },
                        delegate(Action action) { },
                        job,
                        journals);
                    string error;
                    Assert(
                        native.StartPrimary("--fake-child", delegate { }, out error),
                        "Primary supervisor start failed: " + error);
                    Assert(native.HasCleanupPending, "Primary process was not retained after commit.");
                    Assert(
                        !native.StartPrimary("--fake-child", delegate { }, out error),
                        "Duplicate primary start was accepted.");
                    Assert(
                        error.IndexOf("already running", StringComparison.OrdinalIgnoreCase) >= 0,
                        "Duplicate primary start did not fail explicitly.");
                    ProcessStopResult nativeStop = native.StopPrimary(delegate { });
                    Assert(nativeStop.Exited, "Primary supervisor did not complete forced cleanup.");
                    Assert(!native.HasCleanupPending, "Primary cleanup handle was not released.");

                    Assert(
                        !native.StartPrimary("--fake-no-ack", delegate { }, out error),
                        "Primary start without a gate ACK unexpectedly succeeded.");
                    Assert(
                        error.IndexOf("acknowledge", StringComparison.OrdinalIgnoreCase) >= 0,
                        "Gate ACK timeout did not retain its causal error.");
                    Assert(
                        !native.HasCleanupPending,
                        "Successfully killed failed-start primary remained cleanup-pending.");

                    var retainedNative = new NativeProcessSupervisor(
                        executable,
                        root,
                        delegate { },
                        delegate(Action action) { },
                        job,
                        journals,
                        delegate { return false; });
                    Assert(
                        !retainedNative.StartPrimary("--fake-no-ack", delegate { }, out error),
                        "Unconfirmed primary failed-start unexpectedly succeeded.");
                    Assert(
                        retainedNative.HasCleanupPending &&
                        error.IndexOf("Cleanup pending", StringComparison.Ordinal) >= 0,
                        "Unconfirmed primary cleanup did not retain its handle and error.");
                    Assert(
                        retainedNative.StopPrimary(delegate { }).Exited &&
                        !retainedNative.HasCleanupPending,
                        "Primary cleanup retry did not release the retained handle.");
                    Assert(
                        !retainedNative.StartBeta(
                            "--fake-no-ack",
                            0,
                            delegate { },
                            out error),
                        "Unconfirmed beta failed-start unexpectedly succeeded.");
                    Assert(
                        retainedNative.HasCleanupPending &&
                        error.IndexOf("Cleanup pending", StringComparison.Ordinal) >= 0,
                        "Unconfirmed beta cleanup did not retain its handle and error.");
                    Assert(
                        retainedNative.StopAllBeta(delegate { }).Exited &&
                        !retainedNative.HasCleanupPending,
                        "Beta cleanup retry did not release the retained handle.");

                    var host = new DeviceHostSupervisor(
                        executable,
                        root,
                        delegate { },
                        delegate(Action action) { },
                        job);
                    Assert(host.Start(1, delegate { }, out error), "Host supervisor start failed: " + error);
                    Assert(host.HasCleanupPending, "Host process was not retained after commit.");
                    ProcessStopResult hostStop = host.Stop(delegate { });
                    Assert(hostStop.Exited, "Host supervisor did not complete forced cleanup.");
                    Assert(!host.HasCleanupPending, "Host cleanup handle was not released.");

                    var retainedHost = new DeviceHostSupervisor(
                        executable,
                        root,
                        delegate { },
                        delegate(Action action) { },
                        job,
                        delegate { return false; });
                    Environment.SetEnvironmentVariable("SBMS_SUPERVISOR_NO_ACK", "1");
                    try
                    {
                        Assert(
                            !retainedHost.Start(1, delegate { }, out error),
                            "Unconfirmed host failed-start unexpectedly succeeded.");
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("SBMS_SUPERVISOR_NO_ACK", null);
                    }
                    Assert(
                        retainedHost.HasCleanupPending &&
                        error.IndexOf("Cleanup pending", StringComparison.Ordinal) >= 0,
                        "Unconfirmed host cleanup did not retain its handle and error.");
                    Assert(
                        retainedHost.Stop(delegate { }).Exited &&
                        !retainedHost.HasCleanupPending,
                        "Host cleanup retry did not release the retained handle.");
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(journals, true);
                }
                catch
                {
                }
            }
            Console.WriteLine(
                "SupervisorTests passed: " +
                assertions.ToString(CultureInfo.InvariantCulture) +
                " assertions");
            return 0;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (!string.IsNullOrWhiteSpace(FindArgument(args, "--start-gate")))
            {
                return RunFakeChild(args);
            }
            try
            {
                return RunTests();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }
}
