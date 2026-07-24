using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using SBMSGui;

internal static class GuiCoreTests
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

    private static void AssertResolution(Resolution value, int width, int height, string message)
    {
        Assert(value.Width == width && value.Height == height,
            message + ": expected " + width + "x" + height + ", got " + ResolutionMath.Format(value));
    }

    public static int Main()
    {
        var lifecycle = new BridgeLifecycle();
        Assert(lifecycle.State == BridgeState.Idle && lifecycle.Generation == 0, "lifecycle should start idle");
        Assert(!lifecycle.BeginRecovery(0), "idle lifecycle should reject recovery");
        long firstGeneration = lifecycle.BeginStart();
        Assert(lifecycle.State == BridgeState.Starting && firstGeneration == 1, "start should create first generation");
        Assert(lifecycle.BeginStart() == firstGeneration && lifecycle.State == BridgeState.Starting,
            "duplicate start should be an idempotent no-op");
        Assert(lifecycle.MarkRunning(firstGeneration) && lifecycle.State == BridgeState.Running, "start should become running");
        Assert(lifecycle.BeginStart() == firstGeneration && lifecycle.State == BridgeState.Running,
            "start while running should preserve the active generation");
        Assert(lifecycle.BeginRecovery(firstGeneration) && lifecycle.State == BridgeState.Recovering, "running should enter recovery");
        Assert(lifecycle.MarkRunning(firstGeneration), "recovery should return to running");
        long stopGeneration = lifecycle.BeginStop();
        Assert(stopGeneration == 2 && lifecycle.State == BridgeState.Stopping, "stop should invalidate the active generation");
        Assert(lifecycle.BeginStop() == stopGeneration && lifecycle.State == BridgeState.Stopping,
            "duplicate stop should preserve the cleanup generation");
        Assert(!lifecycle.MarkError(firstGeneration, "stale"), "stale callback should not change state");
        Assert(lifecycle.MarkIdle(stopGeneration) && lifecycle.State == BridgeState.Idle, "stop should finish idle");
        Assert(lifecycle.BeginStop() == stopGeneration && lifecycle.State == BridgeState.Idle,
            "stop while idle should be a no-op");
        long secondGeneration = lifecycle.BeginStart();
        Assert(secondGeneration == 3, "a new start should receive a new generation");
        long failedStopGeneration = lifecycle.BeginStop("failure");
        Assert(lifecycle.BeginStop("later failure") == failedStopGeneration,
            "duplicate terminal stop should coalesce");
        Assert(lifecycle.CompleteStop(failedStopGeneration, "cleanup detail") &&
               lifecycle.State == BridgeState.Error &&
               lifecycle.LastError == "failure" &&
               lifecycle.LastCleanupError == "cleanup detail" &&
               !lifecycle.CleanupPending,
            "terminal cleanup should preserve the first failure and cleanup detail");
        Assert(lifecycle.BeginStart() == 5 && lifecycle.State == BridgeState.Starting,
            "a quiescent error should allow a new start generation");
        Assert(BridgeLifecycle.FormatTransition(BridgeState.Starting, BridgeState.Running, 5, "ready") ==
               "状态: Starting -> Running generation=5 // ready", "transition log should be deterministic");

        var pendingCleanupLifecycle = new BridgeLifecycle();
        long pendingStartGeneration = pendingCleanupLifecycle.BeginStart();
        long pendingStopGeneration = pendingCleanupLifecycle.BeginStop();
        Assert(pendingCleanupLifecycle.CompleteStop(
                   pendingStopGeneration,
                   "native process still alive",
                   true) &&
               pendingCleanupLifecycle.State == BridgeState.Error &&
               pendingCleanupLifecycle.CleanupPending,
            "failed cleanup should remain explicitly pending");
        bool pendingStartRejected = false;
        try
        {
            pendingCleanupLifecycle.BeginStart();
        }
        catch (InvalidOperationException)
        {
            pendingStartRejected = true;
        }
        Assert(pendingStartRejected &&
               pendingCleanupLifecycle.Generation == pendingStopGeneration,
            "start should fail closed without advancing generation while cleanup is pending");
        long retryCleanupGeneration = pendingCleanupLifecycle.BeginStop();
        Assert(retryCleanupGeneration == pendingStopGeneration + 1 &&
               pendingCleanupLifecycle.State == BridgeState.Stopping,
            "error state should enter a new stopping generation for cleanup retry");
        Assert(pendingCleanupLifecycle.CompleteStop(
                   retryCleanupGeneration,
                   "",
                   false) &&
               pendingCleanupLifecycle.State == BridgeState.Idle &&
               !pendingCleanupLifecycle.CleanupPending,
            "successful cleanup retry should return to idle");
        Assert(pendingCleanupLifecycle.BeginStart() == retryCleanupGeneration + 1,
            "start should resume only after pending cleanup is confirmed complete");

        var recoveryPolicy = new LifecycleRecoveryPolicy(
            3,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(1000),
            TimeSpan.FromSeconds(30));
        DateTimeOffset recoveryStart = new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);
        LifecycleRecoveryDecision recoveryDecision = recoveryPolicy.RegisterFailure(4, recoveryStart, "exit=100");
        Assert(recoveryDecision.ShouldRetry && recoveryDecision.Attempt == 1 &&
               recoveryDecision.Delay == TimeSpan.FromMilliseconds(250) &&
               recoveryDecision.FirstFailure == "exit=100",
            "first recovery failure should receive the first bounded delay");
        recoveryDecision = recoveryPolicy.RegisterFailure(4, recoveryStart.AddSeconds(1), "exit=101");
        Assert(recoveryDecision.ShouldRetry && recoveryDecision.Attempt == 2 &&
               recoveryDecision.Delay == TimeSpan.FromMilliseconds(500) &&
               recoveryDecision.FirstFailure == "exit=100" &&
               recoveryDecision.LastFailure == "exit=101",
            "second recovery failure should retain the first and last reason");
        recoveryDecision = recoveryPolicy.RegisterFailure(4, recoveryStart.AddSeconds(2), "rebind timeout");
        Assert(recoveryDecision.ShouldRetry && recoveryDecision.Attempt == 3 &&
               recoveryDecision.Delay == TimeSpan.FromMilliseconds(1000),
            "third recovery failure should use the capped delay");
        recoveryDecision = recoveryPolicy.RegisterFailure(4, recoveryStart.AddSeconds(3), "source unavailable");
        Assert(!recoveryDecision.ShouldRetry && recoveryDecision.Attempt == 4 &&
               recoveryDecision.TerminalFailure.Contains("first=exit=100") &&
               recoveryDecision.TerminalFailure.Contains("last=source unavailable"),
            "recovery budget exhaustion should preserve the causal chain");
        recoveryPolicy.Reset(7);
        recoveryPolicy.RegisterFailure(7, recoveryStart, "transient");
        recoveryPolicy.MarkRecoverySucceeded(7, recoveryStart.AddSeconds(1));
        recoveryDecision = recoveryPolicy.RegisterFailure(7, recoveryStart.AddSeconds(40), "new episode");
        Assert(recoveryDecision.ShouldRetry && recoveryDecision.Attempt == 1,
            "a recovery that remains stable for the full window should receive a new bounded episode");
        recoveryDecision = recoveryPolicy.RegisterFailure(5, recoveryStart.AddSeconds(41), "new generation");
        Assert(recoveryDecision.ShouldRetry && recoveryDecision.Attempt == 1 &&
               recoveryDecision.FirstFailure == "new generation",
            "a new lifecycle generation should receive an independent recovery budget");
        recoveryPolicy.Reset(6);
        recoveryPolicy.RegisterFailure(6, recoveryStart, "slow recovery");
        recoveryDecision = recoveryPolicy.RegisterFailure(6, recoveryStart.AddSeconds(30), "deadline");
        Assert(!recoveryDecision.ShouldRetry &&
               recoveryDecision.TerminalFailure.Contains("deadline exhausted"),
            "an unfinished recovery episode should stop at its absolute deadline");

        string journalRoot = Path.Combine(Path.GetTempPath(), "SBMS-WindowJournalTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(journalRoot);
        try
        {
            var record = new WindowMigrationRecord
            {
                WindowHandle = 0x1234,
                ProcessId = 42,
                ProcessCreationTime = 123456,
                Original = Rect(100, 100, 300, 300),
                Migrated = Rect(1050, 50, 1150, 150),
                PhysicalDisplay = Rect(0, 0, 1000, 1000),
                VirtualDisplay = Rect(1000, 0, 1500, 500),
                HasOriginalPlacement = true,
                OriginalPlacement = Placement(3, Rect(100, 100, 300, 300))
            };
            var fakeWindows = new FakeWindowRecoveryApi
            {
                ProcessId = 42,
                ProcessCreationTime = 123456,
                Current = record.Migrated,
                CurrentPlacement = Placement(3, record.Migrated),
                WorkArea = Rect(0, 0, 1000, 900)
            };
            var journal = new WindowMigrationJournal(fakeWindows);
            string movedPath = Path.Combine(journalRoot, "moved.journal");
            File.WriteAllText(
                movedPath,
                WindowMigrationJournal.FormatPrepared(record) + Environment.NewLine,
                new UTF8Encoding(false));
            WindowMigrationRecoveryResult journalResult = journal.RecoverFile(movedPath);
            Assert(journalResult.Prepared == 1 && journalResult.Restored == 1 && journalResult.Unresolved == 0,
                "a matching migrated window should be restored exactly once");
            Assert(fakeWindows.RestoreCalls == 1 &&
                   fakeWindows.Restored.Left == 100 &&
                   fakeWindows.Restored.Top == 100 &&
                   fakeWindows.Restored.Right == 300 &&
                   fakeWindows.Restored.Bottom == 300 &&
                   fakeWindows.RestoredPlacement.ShowCommand == 3 &&
                   fakeWindows.RestoredPlacement.NormalPosition.Left == 100,
                "journal recovery should round-trip maximized placement and its normal rectangle");
            Assert(!File.Exists(movedPath), "fully resolved migration journal should be removed");

            string stalePath = Path.Combine(journalRoot, "stale.journal");
            File.WriteAllText(
                stalePath,
                WindowMigrationJournal.FormatPrepared(record) + Environment.NewLine,
                new UTF8Encoding(false));
            fakeWindows.ProcessCreationTime = 654321;
            journalResult = journal.RecoverFile(stalePath);
            Assert(journalResult.Unresolved == 1 && fakeWindows.RestoreCalls == 1 && File.Exists(stalePath),
                "HWND reuse or process identity drift must fail closed");

            string originalPath = Path.Combine(journalRoot, "original.journal");
            File.WriteAllText(
                originalPath,
                WindowMigrationJournal.FormatPrepared(record) + Environment.NewLine,
                new UTF8Encoding(false));
            fakeWindows.ProcessCreationTime = 123456;
            fakeWindows.Current = record.Original;
            fakeWindows.CurrentPlacement = record.OriginalPlacement;
            journalResult = journal.RecoverFile(originalPath);
            Assert(journalResult.AlreadyOriginal == 1 && fakeWindows.RestoreCalls == 1 && !File.Exists(originalPath),
                "a crash after journal flush but before window movement should resolve without moving the window");

            string corruptPath = Path.Combine(journalRoot, "corrupt.journal");
            fakeWindows.Current = record.Migrated;
            fakeWindows.CurrentPlacement = Placement(3, record.Migrated);
            File.WriteAllText(
                corruptPath,
                WindowMigrationJournal.FormatPrepared(record) + Environment.NewLine +
                "unknown journal record" + Environment.NewLine,
                new UTF8Encoding(false));
            journalResult = journal.RecoverFile(corruptPath);
            Assert(journalResult.Prepared == 1 &&
                   journalResult.Restored == 1 &&
                   journalResult.Corrupt == 1 &&
                   journalResult.Unresolved == 1 &&
                   File.Exists(corruptPath),
                "a nonempty unknown journal line must remain corrupt and prevent deletion");
            int restoreCallsAfterCorruptRecovery = fakeWindows.RestoreCalls;
            journalResult = journal.RecoverFile(corruptPath);
            Assert(journalResult.Prepared == 0 &&
                   journalResult.Restored == 0 &&
                   journalResult.Corrupt == 1 &&
                   journalResult.Unresolved == 1 &&
                   fakeWindows.RestoreCalls == restoreCallsAfterCorruptRecovery &&
                   File.Exists(corruptPath),
                "re-reading a partially resolved corrupt journal must be idempotent");

            string ioRoot = Path.Combine(journalRoot, "io");
            Directory.CreateDirectory(ioRoot);
            string lockedPath = Path.Combine(ioRoot, "locked.journal");
            File.WriteAllText(
                lockedPath,
                WindowMigrationJournal.FormatPrepared(record) + Environment.NewLine,
                new UTF8Encoding(false));
            fakeWindows.Current = record.Migrated;
            fakeWindows.CurrentPlacement = Placement(3, record.Migrated);
            using (var locked = new FileStream(
                lockedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                journalResult = journal.RecoverDirectory(ioRoot);
                Assert(journalResult.IoFailures == 1 &&
                       journalResult.Unresolved == 1 &&
                       journalResult.FailureDetails.Count == 1 &&
                       journalResult.FailureDetails[0].Contains("journal read failed") &&
                       File.Exists(lockedPath),
                    "a locked journal must return a structured directory failure without throwing");
                WindowMigrationRecoveryResult repeatedLockedResult = journal.RecoverFile(lockedPath);
                Assert(repeatedLockedResult.IoFailures == 1 &&
                       repeatedLockedResult.Unresolved == 1 &&
                       fakeWindows.RestoreCalls == restoreCallsAfterCorruptRecovery,
                    "repeated locked-journal recovery must remain side-effect free");
            }

            journalResult = journal.RecoverFile(lockedPath);
            Assert(journalResult.Prepared == 1 &&
                   journalResult.Restored == 1 &&
                   journalResult.IoFailures == 0 &&
                   journalResult.Unresolved == 0 &&
                   !File.Exists(lockedPath),
                "a previously locked journal should recover normally after the IO condition clears");
            journalResult = journal.RecoverFile(lockedPath);
            Assert(journalResult.Prepared == 0 &&
                   journalResult.Restored == 0 &&
                   journalResult.Corrupt == 0 &&
                   journalResult.IoFailures == 0 &&
                   journalResult.Unresolved == 0,
                "recovering an already removed journal should be an idempotent no-op");

            string minimizedPath = Path.Combine(journalRoot, "minimized.journal");
            record.OriginalPlacement = Placement(2, Rect(200, 150, 600, 450));
            record.Original = record.OriginalPlacement.NormalPosition;
            record.Migrated = Rect(1100, 50, 1300, 200);
            fakeWindows.Current = Rect(-32000, -32000, -31840, -31910);
            fakeWindows.CurrentPlacement = Placement(2, record.Migrated);
            File.WriteAllText(
                minimizedPath,
                WindowMigrationJournal.FormatPrepared(record) + Environment.NewLine,
                new UTF8Encoding(false));
            journalResult = journal.RecoverFile(minimizedPath);
            Assert(journalResult.Restored == 1 &&
                   fakeWindows.RestoredPlacement.ShowCommand == 2 &&
                   fakeWindows.RestoredPlacement.NormalPosition.Left == 200 &&
                   fakeWindows.RestoredPlacement.NormalPosition.Top == 150 &&
                   !File.Exists(minimizedPath),
                "a minimized window should restore its normal rectangle and minimized show state");

            string clampPath = Path.Combine(journalRoot, "clamp.journal");
            record.Original = Rect(3000, 2000, 5500, 3600);
            record.OriginalPlacement = Placement(1, record.Original);
            record.Migrated = Rect(1100, 50, 1400, 250);
            fakeWindows.Current = record.Migrated;
            fakeWindows.CurrentPlacement = Placement(1, record.Migrated);
            fakeWindows.WorkArea = Rect(0, 0, 1920, 1040);
            File.WriteAllText(
                clampPath,
                WindowMigrationJournal.FormatPrepared(record) + Environment.NewLine,
                new UTF8Encoding(false));
            journalResult = journal.RecoverFile(clampPath);
            Assert(journalResult.Restored == 1 &&
                   fakeWindows.Restored.Left == 0 &&
                   fakeWindows.Restored.Top == 0 &&
                   fakeWindows.Restored.Right == 1920 &&
                   fakeWindows.Restored.Bottom == 1040,
                "a disconnected monitor target should clamp and shrink to the nearest current work area");

            string legacyPath = Path.Combine(journalRoot, "legacy.journal");
            File.WriteAllText(
                legacyPath,
                "SBMSWM1|P|1234|42|1E240|100,100,300,300|1050,50,1150,150|0,0,1000,1000|1000,0,1500,500" +
                Environment.NewLine,
                new UTF8Encoding(false));
            fakeWindows.Current = Rect(1050, 50, 1150, 150);
            fakeWindows.CurrentPlacement = Placement(1, fakeWindows.Current);
            fakeWindows.WorkArea = Rect(0, 0, 1000, 900);
            journalResult = journal.RecoverFile(legacyPath);
            Assert(journalResult.Prepared == 1 &&
                   journalResult.Restored == 1 &&
                   !fakeWindows.RestoredWithPlacement &&
                   !File.Exists(legacyPath),
                "existing SBMSWM1 journals should remain readable and use rect-only recovery");

            string leaseDirectory = Path.Combine(journalRoot, "lease");
            Directory.CreateDirectory(leaseDirectory);
            var leaseReady = new ManualResetEvent(false);
            var releaseLease = new ManualResetEvent(false);
            Exception leaseThreadFailure = null;
            var leaseThread = new Thread(delegate()
            {
                try
                {
                    WindowMigrationRecoveryLease held;
                    if (!WindowMigrationRecoveryLease.TryAcquire(
                        leaseDirectory,
                        0,
                        out held))
                    {
                        throw new InvalidOperationException("lease holder could not acquire the recovery lease");
                    }
                    using (held)
                    {
                        leaseReady.Set();
                        releaseLease.WaitOne(5000);
                    }
                }
                catch (Exception ex)
                {
                    leaseThreadFailure = ex;
                    leaseReady.Set();
                }
            });
            leaseThread.IsBackground = true;
            leaseThread.Start();
            Assert(leaseReady.WaitOne(5000) && leaseThreadFailure == null,
                "the recovery lease holder should start");
            WindowMigrationRecoveryLease contendedLease;
            Assert(!WindowMigrationRecoveryLease.TryAcquire(
                    leaseDirectory + Path.DirectorySeparatorChar,
                    0,
                    out contendedLease),
                "normalized equivalent session paths must contend on the same recovery lease");
            releaseLease.Set();
            Assert(leaseThread.Join(5000) && leaseThreadFailure == null,
                "the recovery lease holder should release cleanly");
            WindowMigrationRecoveryLease reacquiredLease;
            Assert(WindowMigrationRecoveryLease.TryAcquire(
                    leaseDirectory,
                    1000,
                    out reacquiredLease),
                "a released recovery lease should be acquirable");
            if (reacquiredLease != null)
            {
                reacquiredLease.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(journalRoot))
            {
                Directory.Delete(journalRoot, true);
            }
        }

        var topology = new TopologyDiscoveryService();
        const string physicalLine = "\\\\.\\DISPLAY1 primary: pos=0,0 mode=2560x1440@144 sunshine={12345678-1234-1234-1234-123456789abc} name=Generic Monitor";
        const string virtualLine = "\\\\.\\DISPLAY9: pos=2560,0 mode=1920x1080@60 name=SBMS Virtual Display";
        DisplayChoice parsedDisplay;
        Assert(topology.TryParseLine(physicalLine, null, out parsedDisplay), "physical display line should parse");
        Assert(parsedDisplay.Primary && !parsedDisplay.Virtual && parsedDisplay.SunshineId.Length == 38, "physical display metadata should parse");
        Assert(topology.TryParseLine(physicalLine, delegate(string deviceName)
        {
            return new DisplayRuntimeMode
            {
                Resolution = new Resolution { Width = 1920, Height = 1080 },
                Refresh = "120",
                Orientation = 1
            };
        }, out parsedDisplay) && parsedDisplay.Resolution == "1920x1080" && parsedDisplay.Refresh == "120" && parsedDisplay.Orientation == 1,
            "runtime mode should override stale list metadata");
        Assert(topology.TryParseLine(virtualLine, null, out parsedDisplay) && parsedDisplay.Virtual, "virtual display should be classified");
        var discovered = topology.Parse(physicalLine + Environment.NewLine + virtualLine, null);
        Assert(discovered.Count == 2 && discovered[0].Number == 1 && discovered[1].Number == 2, "discovery should number valid displays");
        Assert(topology.ParseVirtualSources(physicalLine + Environment.NewLine + virtualLine).Count == 1, "virtual discovery should filter physical displays");
        Assert(topology.BuildSignature(physicalLine + Environment.NewLine + virtualLine).Length > 0, "topology signature should be stable and nonempty");
        var swappedDisplays = new List<DisplayChoice>
        {
            new DisplayChoice
            {
                DeviceName = @"\\.\DISPLAY1",
                SunshineId = "{bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb}"
            },
            new DisplayChoice
            {
                DeviceName = @"\\.\DISPLAY2",
                SunshineId = "{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}"
            }
        };
        DisplayChoice rebound = DisplayBindingResolver.ResolveUniquePhysicalByPersistentId(
            swappedDisplays,
            "{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}");
        Assert(rebound != null && rebound.DeviceName == @"\\.\DISPLAY2",
            "persistent identity follows the same monitor when DISPLAY numbers swap");
        swappedDisplays.Add(new DisplayChoice
        {
            DeviceName = @"\\.\DISPLAY3",
            SunshineId = "{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}"
        });
        Assert(DisplayBindingResolver.ResolveUniquePhysicalByPersistentId(
                   swappedDisplays,
                   "{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}") == null,
            "duplicate persistent identities fail closed instead of choosing a monitor");

        int fakeTick = 0;
        string topologyOutput = physicalLine + Environment.NewLine + virtualLine;
        var recovery = new TopologyRecoveryService(
            delegate { return topologyOutput; },
            delegate { return true; },
            topology.ParseVirtualSources,
            delegate(string deviceName, Resolution resolution, string output)
            {
                foreach (DisplayChoice candidate in topology.ParseVirtualSources(output))
                {
                    if (candidate.DeviceName == deviceName && candidate.Resolution == ResolutionMath.Format(resolution))
                    {
                        return candidate;
                    }
                }
                return null;
            },
            delegate { return fakeTick; });
        string stableSignature;
        Assert(recovery.WaitForStable(2000, 2, topology.BuildSignature, delegate(int delay) { fakeTick += delay; return true; }, out stableSignature), "stable topology should be detected");
        List<DisplayChoice> recoveredSources;
        Assert(recovery.WaitForCount(1, 1000, delegate(int delay) { fakeTick += delay; return true; }, out recoveredSources) && recoveredSources.Count == 1, "recovery should discover requested virtual sources");
        DisplayChoice recoveredMode;
        Assert(recovery.WaitForMode("\\\\.\\DISPLAY9", new Resolution { Width = 1920, Height = 1080 }, 1000, delegate(int delay) { fakeTick += delay; return true; }, out recoveredMode), "recovery should confirm a virtual mode");
        topologyOutput = physicalLine;
        Assert(recovery.WaitForClear(1000, delegate(int delay) { fakeTick += delay; return true; }), "recovery should confirm virtual displays cleared");
        topologyOutput = "";
        int cancellationPumps = 0;
        DisplayChoice cancelledSource;
        Assert(!recovery.WaitForAny(5000, delegate(int delay) { ++cancellationPumps; return false; }, out cancelledSource) && cancellationPumps == 1,
            "recovery wait should stop immediately when the pump cancels");
        List<DisplayChoice> cancelledSources;
        Assert(!recovery.WaitForCount(1, 5000, delegate(int delay) { return false; }, out cancelledSources),
            "count wait should honor cancellation");
        Assert(!recovery.WaitForMode("missing", new Resolution { Width = 1, Height = 1 }, 5000, delegate(int delay) { return false; }, out cancelledSource),
            "mode wait should honor cancellation");
        string cancelledSignature;
        Assert(!recovery.WaitForStable(5000, 2, topology.BuildSignature, delegate(int delay) { return false; }, out cancelledSignature),
            "stable wait should honor cancellation");

        var failingRecovery = new TopologyRecoveryService(
            delegate { return virtualLine; },
            delegate { return true; },
            topology.ParseVirtualSources,
            delegate(string deviceName, Resolution resolution, string output) { return null; },
            (Func<string>)delegate { return "driver problem"; });
        Assert(!failingRecovery.WaitForAny(5000, delegate(int delay) { return true; }, out cancelledSource) && failingRecovery.LastFailure == "driver problem",
            "recovery failure probe should stop polling and preserve diagnostics");

        var displayModes = new DisplayModeService();
        Assert(displayModes.NormalizeOrientation(-1) == 0, "negative orientation should normalize to default");
        Assert(displayModes.NormalizeOrientation(3) == 3, "valid orientation should be preserved");
        Assert(displayModes.NormalizeOrientation(4) == 0, "out-of-range orientation should normalize to default");

        Resolution parsed;
        Assert(ResolutionMath.TryParseResolution(" 2560 X 1440 ", out parsed), "resolution should parse");
        AssertResolution(parsed, 2560, 1440, "parsed resolution");
        Assert(!ResolutionMath.TryParseResolution("2560", out parsed), "invalid resolution should fail");

        double size;
        Assert(ResolutionMath.TryParseSize("27,5", out size), "localized size should parse");
        Assert(Math.Abs(size - 27.5) < 0.0001, "localized size value");

        int aspectWidth;
        int aspectHeight;
        Assert(ResolutionMath.TryParseAspect("16：9", out aspectWidth, out aspectHeight), "full-width aspect separator should parse");
        Assert(aspectWidth == 16 && aspectHeight == 9, "aspect values");
        Assert(!ResolutionMath.TryParseAspect("16:0", out aspectWidth, out aspectHeight), "zero aspect should fail");

        Assert(ResolutionMath.RoundEven(3.2) == 4, "odd rounded value should advance to even");
        Assert(ResolutionMath.RoundEven(4.4) == 4, "even rounded value should stay even");
        Assert(ResolutionMath.GreatestCommonDivisor(3840, 2160) == 240, "GCD should match");

        Resolution primary = new Resolution { Width = 3840, Height = 2160 };
        Resolution target = new Resolution { Width = 1920, Height = 1080 };
        AssertResolution(ResolutionMath.CalculatePhysicalSource(primary, target, 27.0, 27.0), 3840, 2160, "physical source");
        Resolution quality = ResolutionMath.CalculateQualitySource(primary, target, 27.0, 27.0);
        AssertResolution(quality, 3840, 2160, "quality source");
        Assert(ResolutionMath.IsExact2x(quality, target), "quality source should be exact 2x");

        int horizontal;
        string aspect;
        string orientation;
        ResolutionMath.BuildParts(new Resolution { Width = 1080, Height = 1920 }, out horizontal, out aspect, out orientation);
        Assert(horizontal == 1920, "portrait horizontal basis");
        Assert(aspect == "16:9", "portrait aspect");
        Assert(orientation == "竖屏", "portrait orientation");

        string configRoot = Path.Combine(Path.GetTempPath(), "SBMS-GuiConfigTest-" + Guid.NewGuid().ToString("N"));
        string configPath = Path.Combine(configRoot, "config.xml");
        try
        {
            var store = new XmlConfigurationStore();
            var config = new GuiConfigFile
            {
                SavedByBuild = "test-build",
                English = true,
                SourceText = "3840x2160",
                TargetText = "2560x1440",
                SingleRefresh = "60",
                PrimaryResolution = "5120x2880",
                PrimarySize = "27",
                TargetResolution = "2560x1440",
                TargetSize = "24",
                ManualBaseHorizontal = "5120",
                ManualBaseAspect = "16:9",
                ManualBaseSize = "27",
                ManualTargetHorizontal = "2560",
                ManualTargetAspect = "16:9",
                ManualTargetSize = "24",
                FollowWindowsTopologyBeta = false
            };
            config.BetaPairs.Add(new GuiConfigBridgePair
            {
                Enabled = true,
                Mode = "output",
                Target = @"1  \\.\DISPLAY1  2560x1440@60",
                TargetDeviceName = @"\\.\DISPLAY1",
                Horizontal = "2560",
                Aspect = "16:9",
                Orientation = "landscape",
                Size = "24",
                Strategy = "physical",
                Refresh = "60",
                Source = "1920x1080"
            });
            store.Save(configPath, config);
            Assert(store.Exists(configPath), "configuration should exist after save");
            Assert(!File.Exists(configPath + ".tmp"), "temporary configuration should be removed");
            GuiConfigFile loaded = store.Load(configPath);
            Assert(loaded.Version == GuiConfigFile.CurrentVersion, "configuration version should round-trip");
            Assert(loaded.SavedByBuild == "test-build" && loaded.English, "configuration scalar fields should round-trip");
            Assert(!loaded.FollowWindowsTopologyBeta, "configuration false boolean should round-trip");
            Assert(loaded.BetaPairs.Count == 1 && loaded.BetaPairs[0].Source == "1920x1080", "configuration pairs should round-trip");
        }
        finally
        {
            if (Directory.Exists(configRoot))
            {
                Directory.Delete(configRoot, true);
            }
        }

        Console.WriteLine("GuiCoreTests passed: " + assertions + " assertions");
        return 0;
    }

    private static WindowRecoveryRect Rect(int left, int top, int right, int bottom)
    {
        return new WindowRecoveryRect
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };
    }

    private static WindowRecoveryPlacement Placement(
        uint showCommand,
        WindowRecoveryRect normalPosition)
    {
        return new WindowRecoveryPlacement
        {
            Flags = 0,
            ShowCommand = showCommand,
            NormalPosition = normalPosition
        };
    }

    private sealed class FakeWindowRecoveryApi : IWindowRecoveryApi
    {
        public uint ProcessId;
        public long ProcessCreationTime;
        public WindowRecoveryRect Current;
        public WindowRecoveryRect Restored;
        public WindowRecoveryPlacement CurrentPlacement;
        public WindowRecoveryPlacement RestoredPlacement;
        public WindowRecoveryRect WorkArea;
        public bool RestoredWithPlacement;
        public int RestoreCalls;

        public bool TryGetIdentity(IntPtr window, out uint processId, out long processCreationTime)
        {
            processId = ProcessId;
            processCreationTime = ProcessCreationTime;
            return true;
        }

        public bool TryGetWindowRect(IntPtr window, out WindowRecoveryRect rect)
        {
            rect = Current;
            return true;
        }

        public bool TryGetWindowPlacement(
            IntPtr window,
            out WindowRecoveryPlacement placement)
        {
            placement = CurrentPlacement;
            return true;
        }

        public bool TryGetMonitorWorkArea(
            WindowRecoveryRect preferredRect,
            out WindowRecoveryRect workArea)
        {
            workArea = WorkArea;
            return true;
        }

        public bool TryRestoreWindow(
            IntPtr window,
            WindowRecoveryRect rect,
            bool restorePlacement,
            WindowRecoveryPlacement placement)
        {
            ++RestoreCalls;
            Restored = rect;
            Current = rect;
            RestoredWithPlacement = restorePlacement;
            RestoredPlacement = placement;
            CurrentPlacement = restorePlacement
                ? placement
                : Placement(1, rect);
            return true;
        }
    }
}
