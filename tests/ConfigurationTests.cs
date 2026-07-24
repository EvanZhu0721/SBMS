using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml.Serialization;
using SBMSGui;

internal static class ConfigurationTests
{
    private static int assertions;

    private sealed class BlockingObserver : IConfigurationCommitObserver
    {
        private readonly string signalPath;
        private readonly string expectedCheckpoint;

        public BlockingObserver(string signalPath, string expectedCheckpoint)
        {
            this.signalPath = signalPath;
            this.expectedCheckpoint = expectedCheckpoint;
        }

        public void OnCheckpoint(string checkpoint, string path)
        {
            if (!string.Equals(checkpoint, expectedCheckpoint, StringComparison.Ordinal))
            {
                return;
            }
            File.WriteAllText(signalPath, checkpoint + Environment.NewLine + path, new UTF8Encoding(false));
            while (true)
            {
                Thread.Sleep(1000);
            }
        }
    }

    public static int Main(string[] args)
    {
        if (args.Length == 5 && args[0] == "--checkpoint-child")
        {
            return RunCheckpointChild(args[1], args[2], args[3], args[4]);
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "SBMS-ConfigurationTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            try
            {
                TestCurrentRoundTrip(root);
                TestV1Migration(root);
                TestMissingVersionMigration(root);
                TestValidation(root);
                TestCorruptPrimaryRecovery(root);
                TestSemanticInvalidRecovery(root);
                TestInterruptedWriteRecovery(root);
                TestBothCopiesInvalid(root);
                TestFutureVersionProtection(root);
                TestUnsafeXml(root);
                TestLockedPrimaryFailsClosed(root);
                TestNonFilePrimaryFailsClosed(root);
                TestPreCommitFailurePreservesPrimary(root);
                TestRealCrashCheckpoint(root, "temp-flushed", false);
                TestRealCrashCheckpoint(root, "primary-committed", true);
                Console.WriteLine(
                    "ConfigurationTests passed: " +
                    assertions.ToString(CultureInfo.InvariantCulture) +
                    " assertions");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static int RunCheckpointChild(
        string configPath,
        string signalPath,
        string checkpoint,
        string buildLabel)
    {
        var store = new XmlConfigurationStore(new BlockingObserver(signalPath, checkpoint));
        store.Save(configPath, CreateValidConfig(buildLabel, @"\\.\DISPLAY2"));
        return 3;
    }

    private static void TestCurrentRoundTrip(string root)
    {
        string path = NewConfigPath(root, "roundtrip");
        var store = new XmlConfigurationStore();
        GuiConfigFile expected = CreateValidConfig("roundtrip<&>", @"\\.\DISPLAY1");
        expected.English = true;
        expected.FollowWindowsTopologyBeta = false;
        ConfigurationSaveResult save = store.Save(path, expected);
        Assert(save.BackupAvailable, "first save creates a validated LKG");
        ConfigurationLoadResult load = store.LoadWithRecovery(path);
        Assert(load.Source == ConfigurationLoadSource.Primary, "current schema loads primary");
        Assert(load.Config.Version == GuiConfigFile.CurrentVersion, "current schema version round-trips");
        Assert(load.Config.SavedByBuild == "roundtrip<&>", "XML special characters round-trip");
        Assert(load.Config.English && !load.Config.FollowWindowsTopologyBeta, "false and true scalars round-trip");
        Assert(load.Config.BetaPairs.Count == 2, "two mapping groups round-trip");
        Assert(load.Config.BetaPairs[0].TargetDeviceName == @"\\.\DISPLAY1", "stable target identity round-trips");
        Assert(load.Config.BetaPairs[0].TargetPersistentId == PersistentIdFor(@"\\.\DISPLAY1"),
            "persistent target identity round-trips");
    }

    private static void TestV1Migration(string root)
    {
        string path = NewConfigPath(root, "migrate-v1");
        GuiConfigFile legacy = CreateValidConfig("legacy", @"\\.\DISPLAY3");
        legacy.Version = 1;
        legacy.English = false;
        legacy.FollowWindowsTopologyBeta = false;
        legacy.SelectedBetaGroupIndex = 0;
        legacy.BetaPairs[0].Mode = "\u8F93\u51FA";
        legacy.BetaPairs[0].Orientation = "\u7AD6\u5C4F";
        legacy.BetaPairs[0].Strategy = "\u6587\u5B57\u6E05\u6670\u4F18\u5148";
        legacy.BetaPairs[0].TargetDeviceName = "";
        legacy.BetaPairs[0].TargetPersistentId = "";
        legacy.SelectedTargetPersistentId = "";
        RawSerialize(path, legacy);

        var store = new XmlConfigurationStore();
        ConfigurationLoadResult load = store.LoadWithRecovery(path);
        Assert(load.Source == ConfigurationLoadSource.Primary && load.Migrated, "v1 primary migrates");
        Assert(load.Config.Version == 2, "v1 migrates to v2");
        Assert(load.Config.BetaPairs.Count == 2, "migration preserves both mappings");
        Assert(load.Config.BetaPairs[0].Mode == "output", "mode migrates to canonical token");
        Assert(load.Config.BetaPairs[0].Orientation == "portrait", "orientation migrates to canonical token");
        Assert(load.Config.BetaPairs[0].Strategy == "text-clarity", "strategy migrates to canonical token");
        Assert(load.Config.BetaPairs[0].TargetDeviceName == @"\\.\DISPLAY3", "target identity is extracted");
        Assert(string.IsNullOrWhiteSpace(load.Config.BetaPairs[0].TargetPersistentId),
            "migration does not invent a persistent identity from transient DISPLAY numbering");
        Assert(!load.Config.English && !load.Config.FollowWindowsTopologyBeta, "migration preserves false values");
        ConfigurationLoadResult reload = store.LoadWithRecovery(path);
        Assert(!reload.Migrated && reload.Config.Version == 2, "migration is persisted and idempotent");
        Assert(File.Exists(path + ".bak"), "legacy bytes remain available in LKG after migration");
    }

    private static void TestMissingVersionMigration(string root)
    {
        string path = NewConfigPath(root, "missing-version");
        GuiConfigFile legacy = CreateValidConfig("missing-version", @"\\.\DISPLAY1");
        legacy.Version = 1;
        legacy.BetaPairs[0].Mode = "Output";
        legacy.BetaPairs[0].Orientation = "Landscape";
        legacy.BetaPairs[0].Strategy = "Physical size";
        RawSerialize(path, legacy);
        string xml = File.ReadAllText(path, Encoding.UTF8);
        xml = xml.Replace("  <Version>1</Version>" + Environment.NewLine, "");
        File.WriteAllText(path, xml, new UTF8Encoding(false));

        ConfigurationLoadResult load = new XmlConfigurationStore().LoadWithRecovery(path);
        Assert(load.Migrated && load.Config.Version == 2, "missing Version is explicit legacy v1");
        Assert(load.Config.SavedByBuild == "missing-version", "missing-version migration preserves scalars");
    }

    private static void TestValidation(string root)
    {
        GuiConfigFile config = CreateValidConfig("validation", @"\\.\DISPLAY1");
        ConfigurationValidationResult valid = GuiConfigValidator.ValidateStatic(config);
        Assert(!valid.HasErrors, "valid configuration passes static validation");

        config.ConfigTabIndex = 2;
        Assert(GuiConfigValidator.ValidateStatic(config).HasErrors, "out-of-range index is rejected");
        config.ConfigTabIndex = 0;
        config.PrimaryResolution = "999999x1";
        Assert(GuiConfigValidator.ValidateStatic(config).HasErrors, "invalid resolution is rejected");
        config.PrimaryResolution = "5120x2880";
        config.BetaPairs.Add(CreatePair(@"\\.\DISPLAY9", "output"));
        Assert(GuiConfigValidator.ValidateStatic(config).HasErrors, "more than two mappings is rejected");
        config.BetaPairs.RemoveAt(2);
        config.BetaPairs[1].Mode = "output";
        config.BetaPairs[1].TargetPersistentId = config.BetaPairs[0].TargetPersistentId.ToUpperInvariant();
        config.BetaPairs[1].TargetDeviceName = config.BetaPairs[0].TargetDeviceName.ToLowerInvariant();
        config.BetaPairs[1].Target = config.BetaPairs[0].Target;
        Assert(GuiConfigValidator.ValidateStatic(config).HasErrors, "duplicate enabled target is rejected case-insensitively");
        config.BetaPairs[1].Enabled = false;
        Assert(!GuiConfigValidator.ValidateStatic(config).HasErrors, "disabled duplicate target is allowed");

        var context = new ConfigurationDisplayContext();
        context.PhysicalDeviceNames.Add(@"\\.\DISPLAY1");
        context.PhysicalPersistentIds.Add(PersistentIdFor(@"\\.\DISPLAY1"));
        context.VirtualDeviceNames.Add(@"\\.\DISPLAY10");
        config.SelectedSourceDevice = @"\\.\DISPLAY77";
        config.SelectedTargetDevice = @"\\.\DISPLAY88";
        config.SelectedTargetPersistentId = "{88888888-8888-8888-8888-888888888888}";
        config.BetaPairs[0].TargetDeviceName = @"\\.\DISPLAY99";
        config.BetaPairs[0].TargetPersistentId = "{99999999-9999-9999-9999-999999999999}";
        ConfigurationValidationResult bindings =
            GuiConfigValidator.ValidateDisplayBindings(config, context);
        Assert(bindings.HasUnresolvedBindings, "stale display bindings are unresolved");
        Assert(bindings.Issues.Count >= 3, "source, target, and mapping diagnostics are retained");
        config.BetaPairs[0].Mode = "stream";
        bindings = GuiConfigValidator.ValidateDisplayBindings(config, context);
        Assert(CountCode(bindings, "display.beta.target.stale") == 0, "stream mapping does not require physical target");

        config.BetaPairs[0].Mode = "output";
        config.BetaPairs[0].TargetPersistentId = "";
        config.BetaPairs[0].TargetDeviceName = @"\\.\DISPLAY1";
        config.BetaPairs[0].Target = @"old label \\.\DISPLAY1";
        config.BetaPairs[1].Enabled = true;
        config.BetaPairs[1].Mode = "output";
        config.BetaPairs[1].TargetPersistentId = "";
        config.BetaPairs[1].TargetDeviceName = "";
        config.BetaPairs[1].Target = @"different label \\.\DISPLAY1";
        Assert(GuiConfigValidator.ValidateStatic(config).HasErrors,
            "duplicate transient device names cannot bypass conflict validation with different labels");
        bindings = GuiConfigValidator.ValidateDisplayBindings(config, context);
        Assert(CountCode(bindings, "display.beta.target.unresolved") >= 1,
            "transient DISPLAY numbers are never accepted as persistent bindings");
    }

    private static void TestCorruptPrimaryRecovery(string root)
    {
        string path = NewConfigPath(root, "corrupt-recovery");
        var store = new XmlConfigurationStore();
        store.Save(path, CreateValidConfig("old-good", @"\\.\DISPLAY1"));
        store.Save(path, CreateValidConfig("new-good", @"\\.\DISPLAY2"));
        byte[] corrupt = Encoding.UTF8.GetBytes("<GuiConfigFile><broken>");
        File.WriteAllBytes(path, corrupt);

        ConfigurationLoadResult load = store.LoadWithRecovery(path);
        Assert(load.Source == ConfigurationLoadSource.LastKnownGood, "corrupt primary falls back to LKG");
        Assert(load.Config.SavedByBuild == "old-good", "validated previous generation is restored");
        Assert(load.QuarantinePaths.Count == 1, "corrupt primary is quarantined");
        Assert(BytesEqual(File.ReadAllBytes(load.QuarantinePaths[0]), corrupt), "quarantine preserves exact corrupt bytes");
        Assert(load.Diagnostics[0].Contains(load.QuarantinePaths[0]) &&
               load.Diagnostics[0].IndexOf("Malformed", StringComparison.OrdinalIgnoreCase) >= 0,
            "corruption feedback includes the quarantine path and parse category");
        Assert(store.Load(path).SavedByBuild == "old-good", "recovered primary is durable");
    }

    private static void TestSemanticInvalidRecovery(string root)
    {
        string path = NewConfigPath(root, "semantic-recovery");
        var store = new XmlConfigurationStore();
        store.Save(path, CreateValidConfig("lkg", @"\\.\DISPLAY1"));
        store.Save(path, CreateValidConfig("current", @"\\.\DISPLAY2"));
        GuiConfigFile invalid = CreateValidConfig("invalid", @"\\.\DISPLAY2");
        invalid.FilterIndex = 99;
        RawSerialize(path, invalid);

        ConfigurationLoadResult load = store.LoadWithRecovery(path);
        Assert(load.Source == ConfigurationLoadSource.LastKnownGood, "semantic invalidity uses LKG");
        Assert(load.Config.SavedByBuild == "lkg", "semantic recovery restores previous valid config");
        Assert(load.Diagnostics[0].IndexOf("filterIndex", StringComparison.OrdinalIgnoreCase) >= 0,
            "semantic recovery diagnostic identifies invalid field");
    }

    private static void TestInterruptedWriteRecovery(string root)
    {
        string path = NewConfigPath(root, "interrupted");
        var store = new XmlConfigurationStore();
        store.Save(path, CreateValidConfig("lkg-only", @"\\.\DISPLAY1"));
        File.Delete(path);
        string orphan = path + ".tmp.deadbeef";
        File.WriteAllText(orphan, "<partial", new UTF8Encoding(false));

        ConfigurationLoadResult load = store.LoadWithRecovery(path);
        Assert(load.Source == ConfigurationLoadSource.LastKnownGood, "missing primary recovers from LKG");
        Assert(load.Config.SavedByBuild == "lkg-only", "interrupted recovery preserves LKG data");
        Assert(File.Exists(path), "interrupted recovery restores primary");
        Assert(!File.Exists(orphan), "orphan partial temp is never promoted and is cleaned");
    }

    private static void TestBothCopiesInvalid(string root)
    {
        string path = NewConfigPath(root, "both-invalid");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "<broken-primary", new UTF8Encoding(false));
        File.WriteAllText(path + ".bak", "<broken-backup", new UTF8Encoding(false));

        ConfigurationLoadResult load = new XmlConfigurationStore().LoadWithRecovery(path);
        Assert(load.Source == ConfigurationLoadSource.Defaults, "both invalid copies use defaults");
        Assert(load.Config != null && load.Config.Version == 2, "defaults use current schema");
        Assert(load.QuarantinePaths.Count == 2, "both invalid copies are preserved");
        Assert(load.AllowAutomaticSave, "defaults may persist only after invalid bytes are preserved");
        Assert(load.Diagnostics[0].Contains(load.QuarantinePaths[0]) &&
               load.Diagnostics[1].Contains(load.QuarantinePaths[1]),
            "both-copy feedback names every preserved quarantine file");
    }

    private static void TestFutureVersionProtection(string root)
    {
        string path = NewConfigPath(root, "future");
        var store = new XmlConfigurationStore();
        store.Save(path, CreateValidConfig("known", @"\\.\DISPLAY1"));
        GuiConfigFile future = CreateValidConfig("future", @"\\.\DISPLAY2");
        future.Version = 99;
        RawSerialize(path, future);
        byte[] before = File.ReadAllBytes(path);

        ConfigurationLoadResult load = store.LoadWithRecovery(path);
        Assert(load.Source == ConfigurationLoadSource.Unusable, "future schema is protected");
        Assert(load.Config == null && !load.AllowAutomaticSave, "future schema blocks fallback and autosave");
        Assert(BytesEqual(before, File.ReadAllBytes(path)), "future schema bytes are untouched");
        Assert(load.QuarantinePaths.Count == 0, "future schema is not mislabeled as corruption");

        string incompatiblePath = NewConfigPath(root, "future-incompatible");
        Directory.CreateDirectory(Path.GetDirectoryName(incompatiblePath));
        File.WriteAllText(
            incompatiblePath,
            "<GuiConfigFile><Version>99</Version><ConfigTabIndex>not-an-int</ConfigTabIndex>" +
            "<FutureOnly><Anything /></FutureOnly></GuiConfigFile>",
            new UTF8Encoding(false));
        byte[] incompatibleBefore = File.ReadAllBytes(incompatiblePath);
        ConfigurationLoadResult incompatible = store.LoadWithRecovery(incompatiblePath);
        Assert(incompatible.Source == ConfigurationLoadSource.Unusable &&
               incompatible.QuarantinePaths.Count == 0,
            "future version is detected before incompatible fields deserialize");
        Assert(BytesEqual(incompatibleBefore, File.ReadAllBytes(incompatiblePath)),
            "incompatible future schema remains byte-for-byte untouched");
    }

    private static void TestUnsafeXml(string root)
    {
        string path = NewConfigPath(root, "unsafe-xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(
            path,
            "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///c:/windows/win.ini'>]><GuiConfigFile>&e;</GuiConfigFile>",
            new UTF8Encoding(false));
        ConfigurationLoadResult load = new XmlConfigurationStore().LoadWithRecovery(path);
        Assert(load.Source == ConfigurationLoadSource.Defaults, "DTD input is rejected");
        Assert(load.QuarantinePaths.Count == 1, "unsafe XML is preserved for diagnosis");
    }

    private static void TestPreCommitFailurePreservesPrimary(string root)
    {
        string path = NewConfigPath(root, "precommit");
        var store = new XmlConfigurationStore();
        store.Save(path, CreateValidConfig("stable", @"\\.\DISPLAY1"));
        byte[] before = File.ReadAllBytes(path);
        GuiConfigFile invalidXml = CreateValidConfig("bad" + ((char)1), @"\\.\DISPLAY2");
        bool failed = false;
        try
        {
            store.Save(path, invalidXml);
        }
        catch
        {
            failed = true;
        }
        Assert(failed, "serialization failure is reported");
        Assert(BytesEqual(before, File.ReadAllBytes(path)), "pre-commit failure leaves primary byte-for-byte unchanged");
        Assert(Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".tmp.*").Length == 0,
            "failed save cleans its unique temp");
    }

    private static void TestLockedPrimaryFailsClosed(string root)
    {
        string path = NewConfigPath(root, "locked");
        var store = new XmlConfigurationStore();
        store.Save(path, CreateValidConfig("locked", @"\\.\DISPLAY1"));
        bool failedClosed = false;
        using (FileStream held = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            try
            {
                store.LoadWithRecovery(path);
            }
            catch (ConfigurationStoreException ex)
            {
                failedClosed = ex.Message.IndexOf(
                    "not overwritten",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        Assert(failedClosed, "locked primary is an I/O failure with actionable fail-closed feedback");
        Assert(File.Exists(path), "locked primary is not quarantined");
        Assert(store.Load(path).SavedByBuild == "locked", "locked primary remains readable after lock release");
    }

    private static void TestNonFilePrimaryFailsClosed(string root)
    {
        string path = NewConfigPath(root, "non-file-primary");
        Directory.CreateDirectory(path);
        bool failedClosed = false;
        try
        {
            new XmlConfigurationStore().LoadWithRecovery(path);
        }
        catch (ConfigurationStoreException ex)
        {
            failedClosed = ex.Message.IndexOf(
                "not overwritten",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }
        Assert(failedClosed,
            "an existing path that cannot be opened as a file is not mistaken for a missing config");
        Assert(Directory.Exists(path), "unreadable primary path is never quarantined or replaced");
    }

    private static void TestRealCrashCheckpoint(string root, string checkpoint, bool expectNew)
    {
        string path = NewConfigPath(root, "crash-" + checkpoint);
        var store = new XmlConfigurationStore();
        store.Save(path, CreateValidConfig("old", @"\\.\DISPLAY1"));
        byte[] oldBytes = File.ReadAllBytes(path);
        string signal = path + ".signal";
        string exe = Process.GetCurrentProcess().MainModule.FileName;
        var start = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Arguments =
            "--checkpoint-child " + Quote(path) + " " + Quote(signal) + " " +
            Quote(checkpoint) + " " + Quote("new");
        using (Process child = Process.Start(start))
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(signal) && !child.HasExited && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            Assert(File.Exists(signal), "child reached real " + checkpoint + " checkpoint");
            if (!child.HasExited)
            {
                child.Kill();
            }
            child.WaitForExit();
        }

        ConfigurationLoadResult load = store.LoadWithRecovery(path);
        Assert(load.Config != null, "config remains readable after kill at " + checkpoint);
        Assert(load.Config.SavedByBuild == (expectNew ? "new" : "old"),
            "kill at " + checkpoint + " leaves exactly the expected committed generation");
        if (!expectNew)
        {
            Assert(BytesEqual(oldBytes, File.ReadAllBytes(path)),
                "kill before swap leaves old primary byte-for-byte unchanged");
        }
        else
        {
            Assert(File.Exists(path + ".bak"), "kill after swap retains old valid LKG");
            Assert(new XmlConfigurationStore().Load(path + ".bak").SavedByBuild == "old",
                "post-swap LKG is the previous valid generation");
        }
    }

    private static GuiConfigFile CreateValidConfig(string buildLabel, string targetDevice)
    {
        var config = new GuiConfigFile
        {
            SavedByBuild = buildLabel,
            English = false,
            LightweightMode = false,
            ConfigTabIndex = 0,
            StrategyIndex = 0,
            FilterIndex = 0,
            SourceText = "3840x2160",
            TargetText = "2560x1440",
            SingleRefresh = "60",
            SelectedSourceDevice = @"\\.\DISPLAY10",
            SelectedTargetDevice = targetDevice,
            SelectedTargetPersistentId = PersistentIdFor(targetDevice),
            PrimaryResolution = "5120x2880",
            PrimarySize = "27",
            TargetResolution = "2560x1440",
            TargetSize = "24",
            PrimaryResolutionPresetIndex = 3,
            PrimaryAspectPresetIndex = 0,
            PrimaryOrientationPresetIndex = 0,
            PrimarySizePresetIndex = 7,
            TargetResolutionPresetIndex = 1,
            TargetAspectPresetIndex = 0,
            TargetOrientationPresetIndex = 0,
            TargetSizePresetIndex = 6,
            ManualBaseHorizontal = "5120",
            ManualBaseAspect = "16:9",
            ManualBaseOrientationIndex = 0,
            ManualBaseSize = "27",
            ManualTargetHorizontal = "2560",
            ManualTargetAspect = "16:9",
            ManualTargetOrientationIndex = 0,
            ManualTargetSize = "24",
            StreamMode = false,
            InputMapping = true,
            WindowMove = true,
            DeviceHost = true,
            VSync = true,
            FollowWindowsTopologyBeta = true,
            SelectedBetaGroupIndex = 0
        };
        config.BetaPairs.Add(CreatePair(targetDevice, "output"));
        config.BetaPairs.Add(CreatePair(
            string.Equals(targetDevice, @"\\.\DISPLAY2", StringComparison.OrdinalIgnoreCase)
                ? @"\\.\DISPLAY3"
                : @"\\.\DISPLAY2",
            "stream"));
        return config;
    }

    private static GuiConfigBridgePair CreatePair(string targetDevice, string mode)
    {
        return new GuiConfigBridgePair
        {
            Enabled = true,
            Mode = mode,
            Target = "1  " + targetDevice + "  2560x1440@60",
            TargetDeviceName = targetDevice,
            TargetPersistentId = PersistentIdFor(targetDevice),
            Horizontal = "2560",
            Aspect = "16:9",
            Orientation = "landscape",
            Size = "24",
            Strategy = "physical",
            Refresh = "60",
            Source = "3840x2160"
        };
    }

    private static string PersistentIdFor(string targetDevice)
    {
        int number = 1;
        string digits = "";
        foreach (char ch in targetDevice ?? "")
        {
            if (char.IsDigit(ch))
            {
                digits += ch;
            }
        }
        if (digits.Length > 0)
        {
            int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }
        return "{" + number.ToString("D8", CultureInfo.InvariantCulture) +
               "-0000-0000-0000-" + number.ToString("D12", CultureInfo.InvariantCulture) + "}";
    }

    private static void RawSerialize(string path, GuiConfigFile config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var serializer = new XmlSerializer(typeof(GuiConfigFile));
        using (FileStream stream = File.Create(path))
        {
            serializer.Serialize(stream, config);
        }
    }

    private static int CountCode(ConfigurationValidationResult result, string code)
    {
        int count = 0;
        foreach (ConfigurationIssue issue in result.Issues)
        {
            if (issue.Code == code)
            {
                ++count;
            }
        }
        return count;
    }

    private static string NewConfigPath(string root, string name)
    {
        return Path.Combine(root, name, "config.xml");
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }
        for (int i = 0; i < left.Length; ++i)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }
        return true;
    }

    private static void Assert(bool condition, string message)
    {
        ++assertions;
        if (!condition)
        {
            throw new InvalidOperationException("Assertion failed: " + message);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }
}
