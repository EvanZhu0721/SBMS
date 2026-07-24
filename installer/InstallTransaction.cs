using System;

namespace SBMSSetup
{
    internal static class InstallTransaction
    {
        internal static void Execute(
            Action verifyRelease,
            Action stageAndReverify,
            Action ensureNotRunning,
            Action copyPayload,
            Action installDriver,
            Action createShortcut,
            Action createStartupTask)
        {
            if (verifyRelease == null) throw new ArgumentNullException("verifyRelease");
            if (stageAndReverify == null) throw new ArgumentNullException("stageAndReverify");
            if (ensureNotRunning == null) throw new ArgumentNullException("ensureNotRunning");
            if (copyPayload == null) throw new ArgumentNullException("copyPayload");
            if (installDriver == null) throw new ArgumentNullException("installDriver");
            if (createShortcut == null) throw new ArgumentNullException("createShortcut");
            if (createStartupTask == null) throw new ArgumentNullException("createStartupTask");

            // Trust verification is deliberately the first executable operation.
            // No filesystem, PnP, shortcut, task or process mutation may precede it.
            verifyRelease();
            stageAndReverify();
            ensureNotRunning();
            copyPayload();
            installDriver();

            // These delegates must be best-effort wrappers. Core installation
            // is already complete, so shell-integration cleanup cannot roll
            // files or Driver Store staging back into an inconsistent state.
            createShortcut();
            createStartupTask();
        }
    }
}
