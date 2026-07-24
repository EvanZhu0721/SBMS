using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using SBMSGui;

internal static class SBMSRecoveryBroker
{
    private const int RecoveryDeadlineMilliseconds = 5000;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 7 &&
                string.Equals(args[0], "watch", StringComparison.Ordinal) &&
                string.Equals(args[1], "--gui-pid", StringComparison.Ordinal) &&
                string.Equals(args[3], "--gui-start-time", StringComparison.Ordinal) &&
                string.Equals(args[5], "--session-dir", StringComparison.Ordinal))
            {
                return Watch(
                    int.Parse(args[2], CultureInfo.InvariantCulture),
                    long.Parse(args[4], CultureInfo.InvariantCulture),
                    Path.GetFullPath(args[6]));
            }
            if (args.Length == 3 &&
                string.Equals(args[0], "recover", StringComparison.Ordinal) &&
                string.Equals(args[1], "--root", StringComparison.Ordinal))
            {
                return RecoverRoot(Path.GetFullPath(args[2]));
            }

            Console.Error.WriteLine(
                "usage: watch --gui-pid PID --gui-start-time UTC_TICKS --session-dir PATH | recover --root PATH");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("broker=terminal_error detail=" + ex.Message);
            return 1;
        }
    }

    private static int Watch(int guiProcessId, long expectedStartTimeUtcTicks, string sessionDirectory)
    {
        using (Process gui = Process.GetProcessById(guiProcessId))
        {
            if (gui.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks)
            {
                throw new InvalidOperationException("GUI process identity did not match the requested broker owner.");
            }

            Directory.CreateDirectory(sessionDirectory);
            Console.WriteLine("broker=ready guiPid=" + guiProcessId.ToString(CultureInfo.InvariantCulture));
            Console.Out.Flush();
            gui.WaitForExit();
        }

        return RecoverWithDeadline(sessionDirectory);
    }

    private static int RecoverRoot(string recoveryRoot)
    {
        if (!Directory.Exists(recoveryRoot))
        {
            return 0;
        }

        int exitCode = 0;
        foreach (string sessionDirectory in Directory.GetDirectories(
            recoveryRoot,
            "*",
            SearchOption.TopDirectoryOnly))
        {
            if (RecoverWithDeadline(sessionDirectory) != 0)
            {
                exitCode = 3;
            }
        }
        return exitCode;
    }

    private static int RecoverWithDeadline(string sessionDirectory)
    {
        WindowMigrationRecoveryLease lease;
        if (!WindowMigrationRecoveryLease.TryAcquire(
            sessionDirectory,
            RecoveryDeadlineMilliseconds,
            out lease))
        {
            Console.Error.WriteLine(
                "broker=recovery_busy timeoutMs=" +
                RecoveryDeadlineMilliseconds.ToString(CultureInfo.InvariantCulture) +
                " session=" + sessionDirectory);
            return 3;
        }

        using (lease)
        {
            var journal = new WindowMigrationJournal(new Win32WindowRecoveryApi());
            Stopwatch timer = Stopwatch.StartNew();
            WindowMigrationRecoveryResult result;
            do
            {
                result = journal.RecoverDirectory(sessionDirectory);
                if (result.Unresolved == 0)
                {
                    Console.WriteLine(
                        "broker=clean prepared=" + result.Prepared.ToString(CultureInfo.InvariantCulture) +
                        " restored=" + result.Restored.ToString(CultureInfo.InvariantCulture) +
                        " alreadyOriginal=" + result.AlreadyOriginal.ToString(CultureInfo.InvariantCulture));
                    return 0;
                }
                Thread.Sleep(100);
            }
            while (timer.ElapsedMilliseconds < RecoveryDeadlineMilliseconds);

            Console.Error.WriteLine(
                "broker=recovery_timeout timeoutMs=" +
                RecoveryDeadlineMilliseconds.ToString(CultureInfo.InvariantCulture) +
                " unresolved=" + result.Unresolved.ToString(CultureInfo.InvariantCulture));
            return 3;
        }
    }
}
