using System;
using System.Collections.Generic;
using System.IO;
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
        Assert(lifecycle.MarkRunning(firstGeneration) && lifecycle.State == BridgeState.Running, "start should become running");
        Assert(lifecycle.BeginRecovery(firstGeneration) && lifecycle.State == BridgeState.Recovering, "running should enter recovery");
        Assert(lifecycle.MarkRunning(firstGeneration), "recovery should return to running");
        long stopGeneration = lifecycle.BeginStop();
        Assert(stopGeneration == 2 && lifecycle.State == BridgeState.Stopping, "stop should invalidate the active generation");
        Assert(!lifecycle.MarkError(firstGeneration, "stale"), "stale callback should not change state");
        Assert(lifecycle.MarkIdle(stopGeneration) && lifecycle.State == BridgeState.Idle, "stop should finish idle");
        long secondGeneration = lifecycle.BeginStart();
        Assert(secondGeneration == 3, "a new start should receive a new generation");
        Assert(lifecycle.MarkError(secondGeneration, "failure") && lifecycle.LastError == "failure", "current failure should be recorded");
        Assert(lifecycle.BeginStart() == 4 && lifecycle.State == BridgeState.Starting, "error state should allow retry");
        Assert(BridgeLifecycle.FormatTransition(BridgeState.Starting, BridgeState.Running, 4, "ready") ==
               "状态: Starting -> Running generation=4 // ready", "transition log should be deterministic");

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
                FollowWindowsTopologyBeta = false
            };
            config.BetaPairs.Add(new GuiConfigBridgePair { Enabled = true, Mode = "映射", Source = "1920x1080" });
            store.Save(configPath, config);
            Assert(store.Exists(configPath), "configuration should exist after save");
            Assert(!File.Exists(configPath + ".tmp"), "temporary configuration should be removed");
            GuiConfigFile loaded = store.Load(configPath);
            Assert(loaded.Version == 1, "configuration version should round-trip");
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
}
