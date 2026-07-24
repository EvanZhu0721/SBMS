using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace SBMSGui
{
    internal interface IConfigurationStore
    {
        bool Exists(string path);
        GuiConfigFile Load(string path);
        ConfigurationLoadResult LoadWithRecovery(string path);
        ConfigurationSaveResult Save(string path, GuiConfigFile config);
    }

    internal interface IConfigurationCommitObserver
    {
        void OnCheckpoint(string checkpoint, string path);
    }

    internal enum ConfigurationLoadSource
    {
        Primary,
        LastKnownGood,
        Defaults,
        Unusable
    }

    internal enum ConfigurationIssueSeverity
    {
        Warning,
        Error,
        Unresolved
    }

    internal sealed class ConfigurationIssue
    {
        public string Code;
        public ConfigurationIssueSeverity Severity;
        public string Message;

        public override string ToString()
        {
            return Severity.ToString().ToLowerInvariant() + "[" + Code + "] " + Message;
        }
    }

    internal sealed class ConfigurationValidationResult
    {
        public readonly List<ConfigurationIssue> Issues = new List<ConfigurationIssue>();

        public bool HasErrors
        {
            get
            {
                foreach (ConfigurationIssue issue in Issues)
                {
                    if (issue.Severity == ConfigurationIssueSeverity.Error)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public bool HasUnresolvedBindings
        {
            get
            {
                foreach (ConfigurationIssue issue in Issues)
                {
                    if (issue.Severity == ConfigurationIssueSeverity.Unresolved)
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }

    internal sealed class ConfigurationDisplayContext
    {
        public readonly HashSet<string> PhysicalDeviceNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> PhysicalPersistentIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> VirtualDeviceNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class ConfigurationLoadResult
    {
        public GuiConfigFile Config;
        public ConfigurationLoadSource Source;
        public bool Migrated;
        public bool AllowAutomaticSave;
        public bool PersistenceDegraded;
        public readonly List<string> Diagnostics = new List<string>();
        public readonly List<string> QuarantinePaths = new List<string>();
    }

    internal sealed class ConfigurationSaveResult
    {
        public bool BackupAvailable;
        public bool BackupDegraded;
        public string Diagnostic;
    }

    internal sealed class ConfigurationStoreException : Exception
    {
        public ConfigurationStoreException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class UnsupportedConfigurationVersionException : Exception
    {
        public UnsupportedConfigurationVersionException(string message)
            : base(message)
        {
        }
    }

    internal sealed class ConfigurationValidationException : Exception
    {
        public readonly ConfigurationValidationResult Validation;

        public ConfigurationValidationException(ConfigurationValidationResult validation)
            : base(BuildMessage(validation))
        {
            Validation = validation;
        }

        private static string BuildMessage(ConfigurationValidationResult validation)
        {
            var builder = new StringBuilder("Configuration validation failed");
            foreach (ConfigurationIssue issue in validation.Issues)
            {
                if (issue.Severity == ConfigurationIssueSeverity.Error)
                {
                    builder.Append("; ");
                    builder.Append(issue.ToString());
                }
            }
            return builder.ToString();
        }
    }

    internal static class GuiConfigMigrator
    {
        public static GuiConfigFile MigrateToCurrent(GuiConfigFile config, out bool migrated)
        {
            if (config == null)
            {
                throw new InvalidDataException("Configuration document is empty.");
            }
            if (config.Version > GuiConfigFile.CurrentVersion)
            {
                throw new UnsupportedConfigurationVersionException(
                    "Configuration schema v" + config.Version.ToString(CultureInfo.InvariantCulture) +
                    " is newer than this build supports (v" +
                    GuiConfigFile.CurrentVersion.ToString(CultureInfo.InvariantCulture) +
                    "). Update SBMS; the file was not changed.");
            }
            if (config.Version <= 0)
            {
                throw new InvalidDataException(
                    "Configuration schema version must be 1.." +
                    GuiConfigFile.CurrentVersion.ToString(CultureInfo.InvariantCulture) + ".");
            }

            migrated = false;
            while (config.Version < GuiConfigFile.CurrentVersion)
            {
                switch (config.Version)
                {
                    case 1:
                        MigrateV1ToV2(config);
                        migrated = true;
                        break;
                    default:
                        throw new InvalidDataException(
                            "No migration exists for configuration schema v" +
                            config.Version.ToString(CultureInfo.InvariantCulture) + ".");
                }
            }
            return config;
        }

        private static void MigrateV1ToV2(GuiConfigFile config)
        {
            if (config.BetaPairs == null)
            {
                config.BetaPairs = new List<GuiConfigBridgePair>();
            }
            foreach (GuiConfigBridgePair pair in config.BetaPairs)
            {
                if (pair == null)
                {
                    continue;
                }
                pair.Mode = CanonicalMode(pair.Mode);
                pair.Orientation = CanonicalOrientation(pair.Orientation);
                pair.Strategy = CanonicalStrategy(pair.Strategy);
                if (string.IsNullOrWhiteSpace(pair.TargetDeviceName))
                {
                    pair.TargetDeviceName = ExtractDeviceName(pair.Target);
                }
            }
            config.Version = 2;
        }

        internal static string CanonicalMode(string value)
        {
            string normalized = (value ?? "").Trim();
            if (EqualsAny(normalized, "output", "Output", "输出", "映射", "Mapping"))
            {
                return "output";
            }
            if (EqualsAny(normalized, "stream", "Streaming", "串流", "Virtual only", "仅虚拟桌面"))
            {
                return "stream";
            }
            return normalized;
        }

        internal static string CanonicalOrientation(string value)
        {
            string normalized = (value ?? "").Trim();
            if (EqualsAny(normalized, "landscape", "Landscape", "横屏"))
            {
                return "landscape";
            }
            if (EqualsAny(normalized, "portrait", "Portrait", "竖屏"))
            {
                return "portrait";
            }
            if (EqualsAny(normalized, "landscape-flipped", "Landscape flipped", "横屏反向"))
            {
                return "landscape-flipped";
            }
            if (EqualsAny(normalized, "portrait-flipped", "Portrait flipped", "竖屏反向"))
            {
                return "portrait-flipped";
            }
            return normalized;
        }

        internal static string CanonicalStrategy(string value)
        {
            string normalized = (value ?? "").Trim();
            if (EqualsAny(normalized, "physical", "Physical size", "真实尺寸比例"))
            {
                return "physical";
            }
            if (EqualsAny(normalized, "text-clarity", "Text clarity", "文字清晰优先"))
            {
                return "text-clarity";
            }
            if (EqualsAny(normalized, "direct", "Direct source", "直接使用源"))
            {
                return "direct";
            }
            return normalized;
        }

        internal static string ExtractDeviceName(string value)
        {
            string text = value ?? "";
            int start = text.IndexOf(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return "";
            }
            int end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                ++end;
            }
            return text.Substring(start, end - start);
        }

        private static bool EqualsAny(string value, params string[] candidates)
        {
            foreach (string candidate in candidates)
            {
                if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal static class GuiConfigValidator
    {
        private const int MaxTextLength = 4096;
        private const int MaxBetaPairs = 2;

        public static ConfigurationValidationResult ValidateStatic(GuiConfigFile config)
        {
            var result = new ConfigurationValidationResult();
            if (config == null)
            {
                Add(result, "config.null", ConfigurationIssueSeverity.Error, "Configuration is empty.");
                return result;
            }
            if (config.Version != GuiConfigFile.CurrentVersion)
            {
                Add(result, "schema.version", ConfigurationIssueSeverity.Error,
                    "Expected schema v" + GuiConfigFile.CurrentVersion.ToString(CultureInfo.InvariantCulture) +
                    ", got v" + config.Version.ToString(CultureInfo.InvariantCulture) + ".");
            }

            ValidateIndex(result, "configTabIndex", config.ConfigTabIndex, 0, 1);
            ValidateIndex(result, "strategyIndex", config.StrategyIndex, 0, 2);
            ValidateIndex(result, "filterIndex", config.FilterIndex, 0, 3);
            ValidateIndex(result, "primaryResolutionPresetIndex", config.PrimaryResolutionPresetIndex, 0, 5);
            ValidateIndex(result, "primaryAspectPresetIndex", config.PrimaryAspectPresetIndex, 0, 2);
            ValidateIndex(result, "primaryOrientationPresetIndex", config.PrimaryOrientationPresetIndex, 0, 3);
            ValidateIndex(result, "primarySizePresetIndex", config.PrimarySizePresetIndex, 0, 8);
            ValidateIndex(result, "targetResolutionPresetIndex", config.TargetResolutionPresetIndex, 0, 5);
            ValidateIndex(result, "targetAspectPresetIndex", config.TargetAspectPresetIndex, 0, 2);
            ValidateIndex(result, "targetOrientationPresetIndex", config.TargetOrientationPresetIndex, 0, 3);
            ValidateIndex(result, "targetSizePresetIndex", config.TargetSizePresetIndex, 0, 8);
            ValidateIndex(result, "manualBaseOrientationIndex", config.ManualBaseOrientationIndex, 0, 3);
            ValidateIndex(result, "manualTargetOrientationIndex", config.ManualTargetOrientationIndex, 0, 3);

            ValidateResolution(result, "primaryResolution", config.PrimaryResolution, false);
            ValidateResolution(result, "targetResolution", config.TargetResolution, false);
            ValidateSize(result, "primarySize", config.PrimarySize, false);
            ValidateSize(result, "targetSize", config.TargetSize, false);
            ValidatePositiveInteger(result, "manualBaseHorizontal", config.ManualBaseHorizontal, 320, 16384, false);
            ValidateAspect(result, "manualBaseAspect", config.ManualBaseAspect, false);
            ValidateSize(result, "manualBaseSize", config.ManualBaseSize, false);
            ValidatePositiveInteger(result, "manualTargetHorizontal", config.ManualTargetHorizontal, 320, 16384, false);
            ValidateAspect(result, "manualTargetAspect", config.ManualTargetAspect, false);
            ValidateSize(result, "manualTargetSize", config.ManualTargetSize, false);
            ValidatePositiveInteger(result, "singleRefresh", config.SingleRefresh, 1, 1000, false);

            ValidateTextLengths(result, config);
            if (config.BetaPairs == null)
            {
                Add(result, "pairs.null", ConfigurationIssueSeverity.Error, "BetaPairs must be present.");
                return result;
            }
            if (config.BetaPairs.Count > MaxBetaPairs)
            {
                Add(result, "pairs.count", ConfigurationIssueSeverity.Error,
                    "At most " + MaxBetaPairs.ToString(CultureInfo.InvariantCulture) +
                    " mapping groups are supported; found " +
                    config.BetaPairs.Count.ToString(CultureInfo.InvariantCulture) + ".");
            }
            if (config.SelectedBetaGroupIndex < 0 ||
                (config.BetaPairs.Count > 0 && config.SelectedBetaGroupIndex >= config.BetaPairs.Count))
            {
                Add(result, "selectedBetaGroupIndex", ConfigurationIssueSeverity.Error,
                    "SelectedBetaGroupIndex is outside the saved mapping groups.");
            }

            var enabledTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < config.BetaPairs.Count; ++i)
            {
                GuiConfigBridgePair pair = config.BetaPairs[i];
                string prefix = "betaPairs[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (pair == null)
                {
                    Add(result, prefix, ConfigurationIssueSeverity.Error, "Mapping group is null.");
                    continue;
                }
                ValidateEnum(result, prefix + ".mode", pair.Mode, "output", "stream");
                ValidateEnum(result, prefix + ".orientation", pair.Orientation,
                    "landscape", "portrait", "landscape-flipped", "portrait-flipped");
                ValidateEnum(result, prefix + ".strategy", pair.Strategy,
                    "physical", "text-clarity", "direct");
                ValidatePositiveInteger(result, prefix + ".horizontal", pair.Horizontal, 320, 16384, true);
                ValidateAspect(result, prefix + ".aspect", pair.Aspect, true);
                ValidateSize(result, prefix + ".size", pair.Size, true);
                ValidatePositiveInteger(result, prefix + ".refresh", pair.Refresh, 1, 1000, true);
                ValidateResolution(result, prefix + ".source", pair.Source, true);

                string targetIdentity = !string.IsNullOrWhiteSpace(pair.TargetPersistentId)
                    ? pair.TargetPersistentId.Trim()
                    : (!string.IsNullOrWhiteSpace(pair.TargetDeviceName)
                        ? pair.TargetDeviceName.Trim()
                        : GuiConfigMigrator.ExtractDeviceName(pair.Target));
                if (pair.Enabled && string.Equals(pair.Mode, "output", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(targetIdentity) && !enabledTargets.Add(targetIdentity))
                {
                    Add(result, prefix + ".target", ConfigurationIssueSeverity.Error,
                        "Two enabled output mappings target the same physical display: " +
                        targetIdentity + ".");
                }
            }
            return result;
        }

        public static ConfigurationValidationResult ValidateDisplayBindings(
            GuiConfigFile config,
            ConfigurationDisplayContext context)
        {
            var result = new ConfigurationValidationResult();
            if (config == null || context == null)
            {
                return result;
            }
            if (!string.IsNullOrWhiteSpace(config.SelectedSourceDevice) &&
                !context.VirtualDeviceNames.Contains(config.SelectedSourceDevice))
            {
                Add(result, "display.source.stale", ConfigurationIssueSeverity.Unresolved,
                    "Saved virtual source is not currently available: " + config.SelectedSourceDevice + ".");
            }
            if (!string.IsNullOrWhiteSpace(config.SelectedTargetDevice) &&
                string.IsNullOrWhiteSpace(config.SelectedTargetPersistentId))
            {
                Add(result, "display.target.identity-missing", ConfigurationIssueSeverity.Unresolved,
                    "Saved physical target has only a transient Windows display number. " +
                    "Select the target again to confirm its persistent identity.");
            }
            else if (!string.IsNullOrWhiteSpace(config.SelectedTargetPersistentId) &&
                !context.PhysicalPersistentIds.Contains(config.SelectedTargetPersistentId))
            {
                Add(result, "display.target.stale", ConfigurationIssueSeverity.Unresolved,
                    "Saved physical target is not currently available: " + config.SelectedTargetPersistentId +
                    ". SBMS will not select a different display automatically.");
            }
            if (config.BetaPairs == null)
            {
                return result;
            }
            for (int i = 0; i < config.BetaPairs.Count; ++i)
            {
                GuiConfigBridgePair pair = config.BetaPairs[i];
                if (pair == null || !pair.Enabled ||
                    string.Equals(pair.Mode, "stream", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string identity = (pair.TargetPersistentId ?? "").Trim();
                if (string.IsNullOrWhiteSpace(identity))
                {
                    Add(result, "display.beta.target.unresolved", ConfigurationIssueSeverity.Unresolved,
                        "Mapping group " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                        " has no persistent physical target identity. Select the target again to confirm it.");
                }
                else if (!context.PhysicalPersistentIds.Contains(identity))
                {
                    Add(result, "display.beta.target.stale", ConfigurationIssueSeverity.Unresolved,
                        "Mapping group " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                        " target is not currently available: " + identity +
                        ". The saved binding was preserved.");
                }
            }
            return result;
        }

        private static void ValidateTextLengths(ConfigurationValidationResult result, GuiConfigFile config)
        {
            ValidateLength(result, "savedByBuild", config.SavedByBuild);
            ValidateLength(result, "sourceText", config.SourceText);
            ValidateLength(result, "targetText", config.TargetText);
            ValidateLength(result, "selectedSourceDevice", config.SelectedSourceDevice);
            ValidateLength(result, "selectedTargetDevice", config.SelectedTargetDevice);
            ValidateLength(result, "selectedTargetPersistentId", config.SelectedTargetPersistentId);
            if (config.BetaPairs == null)
            {
                return;
            }
            for (int i = 0; i < config.BetaPairs.Count; ++i)
            {
                GuiConfigBridgePair pair = config.BetaPairs[i];
                if (pair == null)
                {
                    continue;
                }
                string prefix = "betaPairs[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                ValidateLength(result, prefix + ".target", pair.Target);
                ValidateLength(result, prefix + ".targetDeviceName", pair.TargetDeviceName);
                ValidateLength(result, prefix + ".targetPersistentId", pair.TargetPersistentId);
            }
        }

        private static void ValidateLength(ConfigurationValidationResult result, string field, string value)
        {
            if (value != null && value.Length > MaxTextLength)
            {
                Add(result, field, ConfigurationIssueSeverity.Error,
                    field + " exceeds " + MaxTextLength.ToString(CultureInfo.InvariantCulture) + " characters.");
            }
        }

        private static void ValidateIndex(
            ConfigurationValidationResult result,
            string field,
            int value,
            int minimum,
            int maximum)
        {
            if (value < minimum || value > maximum)
            {
                Add(result, field, ConfigurationIssueSeverity.Error,
                    field + " must be " + minimum.ToString(CultureInfo.InvariantCulture) + ".." +
                    maximum.ToString(CultureInfo.InvariantCulture) + "; got " +
                    value.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }

        private static void ValidateResolution(
            ConfigurationValidationResult result,
            string field,
            string value,
            bool allowEmpty)
        {
            if (allowEmpty && string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            string[] parts = (value ?? "").Trim().ToLowerInvariant().Split('x');
            int width;
            int height;
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height) ||
                width < 320 || width > 16384 || height < 200 || height > 16384)
            {
                Add(result, field, ConfigurationIssueSeverity.Error,
                    field + " must be WIDTHxHEIGHT within 320x200..16384x16384.");
            }
        }

        private static void ValidateSize(
            ConfigurationValidationResult result,
            string field,
            string value,
            bool allowEmpty)
        {
            if (allowEmpty && string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            double parsed;
            if (!double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
                double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < 1.0 || parsed > 200.0)
            {
                Add(result, field, ConfigurationIssueSeverity.Error,
                    field + " must be an invariant number between 1 and 200 inches.");
            }
        }

        private static void ValidatePositiveInteger(
            ConfigurationValidationResult result,
            string field,
            string value,
            int minimum,
            int maximum,
            bool allowEmpty)
        {
            if (allowEmpty && string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            int parsed;
            if (!int.TryParse((value ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out parsed) ||
                parsed < minimum || parsed > maximum)
            {
                Add(result, field, ConfigurationIssueSeverity.Error,
                    field + " must be an integer between " +
                    minimum.ToString(CultureInfo.InvariantCulture) + " and " +
                    maximum.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }

        private static void ValidateAspect(
            ConfigurationValidationResult result,
            string field,
            string value,
            bool allowEmpty)
        {
            if (allowEmpty && string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            string[] parts = (value ?? "").Trim().Split(':');
            int width;
            int height;
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height) ||
                width <= 0 || height <= 0 || width > 1000 || height > 1000)
            {
                Add(result, field, ConfigurationIssueSeverity.Error,
                    field + " must be a positive W:H ratio.");
            }
        }

        private static void ValidateEnum(
            ConfigurationValidationResult result,
            string field,
            string value,
            params string[] allowed)
        {
            foreach (string candidate in allowed)
            {
                if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            Add(result, field, ConfigurationIssueSeverity.Error,
                field + " has unsupported value '" + (value ?? "<null>") + "'.");
        }

        private static void Add(
            ConfigurationValidationResult result,
            string code,
            ConfigurationIssueSeverity severity,
            string message)
        {
            result.Issues.Add(new ConfigurationIssue
            {
                Code = code,
                Severity = severity,
                Message = message
            });
        }
    }

    internal sealed class XmlConfigurationStore : IConfigurationStore
    {
        private const long MaxConfigurationBytes = 1024 * 1024;
        private readonly IConfigurationCommitObserver commitObserver;

        public XmlConfigurationStore()
            : this(null)
        {
        }

        internal XmlConfigurationStore(IConfigurationCommitObserver commitObserver)
        {
            this.commitObserver = commitObserver;
        }

        public bool Exists(string path)
        {
            return PathExistsStrict(path) || PathExistsStrict(GetBackupPath(path));
        }

        public GuiConfigFile Load(string path)
        {
            ConfigurationLoadResult result = LoadWithRecovery(path);
            if (result.Config == null)
            {
                throw new InvalidDataException(
                    result.Diagnostics.Count > 0
                        ? string.Join(" ", result.Diagnostics.ToArray())
                        : "Configuration is unusable.");
            }
            return result.Config;
        }

        public ConfigurationLoadResult LoadWithRecovery(string path)
        {
            var result = new ConfigurationLoadResult
            {
                Source = ConfigurationLoadSource.Defaults,
                AllowAutomaticSave = true
            };
            string backupPath = GetBackupPath(path);
            try
            {
                if (PathExistsStrict(path))
                {
                    ReadOutcome primary = ReadValidated(path);
                    if (primary.UnsupportedFutureVersion)
                    {
                        result.Source = ConfigurationLoadSource.Unusable;
                        result.AllowAutomaticSave = false;
                        result.Diagnostics.Add(primary.Diagnostic);
                        return result;
                    }
                    if (primary.Config != null)
                    {
                        result.Config = primary.Config;
                        result.Source = ConfigurationLoadSource.Primary;
                        result.Migrated = primary.Migrated;
                        result.Diagnostics.Add(primary.Migrated
                            ? "Configuration migrated to schema v" +
                              GuiConfigFile.CurrentVersion.ToString(CultureInfo.InvariantCulture) + "."
                            : "Configuration schema v" +
                              GuiConfigFile.CurrentVersion.ToString(CultureInfo.InvariantCulture) +
                              " loaded from primary.");
                        CleanupOrphanTemps(path, result);
                        if (primary.Migrated)
                        {
                            try
                            {
                                Save(path, primary.Config);
                                result.Diagnostics.Add("Migrated configuration committed atomically.");
                            }
                            catch (Exception ex)
                            {
                                result.AllowAutomaticSave = false;
                                result.PersistenceDegraded = true;
                                result.Diagnostics.Add(
                                    "Migration is active in memory but could not be committed: " + ex.Message);
                            }
                        }
                        return result;
                    }

                    string quarantinePath = Quarantine(path, "invalid");
                    result.QuarantinePaths.Add(quarantinePath);
                    result.Diagnostics.Add(
                        "Primary configuration was invalid and preserved at " + quarantinePath +
                        ": " + primary.Diagnostic);
                }

                if (PathExistsStrict(backupPath))
                {
                    ReadOutcome backup = ReadValidated(backupPath);
                    if (backup.UnsupportedFutureVersion)
                    {
                        result.Source = ConfigurationLoadSource.Unusable;
                        result.AllowAutomaticSave = false;
                        result.Diagnostics.Add(
                            "Last-known-good configuration uses a newer schema. " + backup.Diagnostic);
                        return result;
                    }
                    if (backup.Config != null)
                    {
                        RestorePrimaryFromBackup(path, backupPath);
                        result.Config = backup.Config;
                        result.Source = ConfigurationLoadSource.LastKnownGood;
                        result.Migrated = backup.Migrated;
                        result.Diagnostics.Add(
                            "Recovered configuration from the validated last-known-good copy and restored primary.");
                        CleanupOrphanTemps(path, result);
                        if (backup.Migrated)
                        {
                            Save(path, backup.Config);
                            result.Diagnostics.Add("Recovered configuration migrated to the current schema.");
                        }
                        return result;
                    }

                    string backupQuarantine = Quarantine(backupPath, "backup-invalid");
                    result.QuarantinePaths.Add(backupQuarantine);
                    result.Diagnostics.Add(
                        "Last-known-good configuration was also invalid and preserved at " +
                        backupQuarantine + ": " + backup.Diagnostic);
                }

                CleanupOrphanTemps(path, result);
                result.Config = new GuiConfigFile();
                result.Source = ConfigurationLoadSource.Defaults;
                result.Diagnostics.Add(
                    "No valid configuration remained; defaults are active. Review the preserved invalid files.");
                return result;
            }
            catch (UnsupportedConfigurationVersionException ex)
            {
                result.Source = ConfigurationLoadSource.Unusable;
                result.AllowAutomaticSave = false;
                result.Diagnostics.Add(ex.Message);
                return result;
            }
            catch (IOException ex)
            {
                throw new ConfigurationStoreException(
                    "Configuration I/O failed. The original files were not overwritten: " + ex.Message, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ConfigurationStoreException(
                    "Configuration access was denied. Check file permissions; the original files were not overwritten: " +
                    ex.Message, ex);
            }
        }

        public ConfigurationSaveResult Save(string path, GuiConfigFile config)
        {
            bool migrated;
            config = GuiConfigMigrator.MigrateToCurrent(config, out migrated);
            ConfigurationValidationResult validation = GuiConfigValidator.ValidateStatic(config);
            if (validation.HasErrors)
            {
                throw new ConfigurationValidationException(validation);
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string tempPath = NewTempPath(path);
            string backupPath = GetBackupPath(path);
            bool committed = false;
            try
            {
                WriteDurable(tempPath, config);
                ReadOutcome verification = ReadValidated(tempPath);
                if (verification.Config == null || verification.Migrated ||
                    verification.UnsupportedFutureVersion)
                {
                    throw new InvalidDataException(
                        "Serialized configuration failed read-back verification: " + verification.Diagnostic);
                }
                NotifyCheckpoint("temp-flushed", tempPath);

                if (PathExistsStrict(path))
                {
                    File.Replace(tempPath, path, backupPath, true);
                }
                else
                {
                    File.Move(tempPath, path);
                }
                committed = true;
                NotifyCheckpoint("primary-committed", path);

                var result = new ConfigurationSaveResult
                {
                    BackupAvailable = File.Exists(backupPath)
                };
                if (!result.BackupAvailable)
                {
                    try
                    {
                        CreateInitialBackup(path, backupPath);
                        result.BackupAvailable = true;
                    }
                    catch (Exception ex)
                    {
                        result.BackupDegraded = true;
                        result.Diagnostic =
                            "Configuration was committed, but the initial last-known-good copy could not be created: " +
                            ex.Message;
                    }
                }
                return result;
            }
            catch
            {
                if (!committed)
                {
                    TryDelete(tempPath);
                }
                throw;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private void NotifyCheckpoint(string checkpoint, string path)
        {
            if (commitObserver != null)
            {
                commitObserver.OnCheckpoint(checkpoint, path);
            }
        }

        private sealed class ReadOutcome
        {
            public GuiConfigFile Config;
            public bool Migrated;
            public bool UnsupportedFutureVersion;
            public string Diagnostic;
        }

        private static ReadOutcome ReadValidated(string path)
        {
            try
            {
                FileInfo file = new FileInfo(path);
                if (file.Length <= 0 || file.Length > MaxConfigurationBytes)
                {
                    return new ReadOutcome
                    {
                        Diagnostic = "File size must be 1.." +
                                     MaxConfigurationBytes.ToString(CultureInfo.InvariantCulture) + " bytes."
                    };
                }

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxConfigurationBytes,
                    IgnoreComments = false,
                    IgnoreWhitespace = false
                };
                var document = new XmlDocument
                {
                    XmlResolver = null
                };
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan))
                using (XmlReader reader = XmlReader.Create(stream, settings))
                {
                    document.Load(reader);
                }

                var serializer = new XmlSerializer(typeof(GuiConfigFile));
                XmlElement root = document.DocumentElement;
                if (root == null || !string.Equals(root.LocalName, "GuiConfigFile", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Configuration root element must be GuiConfigFile.");
                }
                XmlNode versionNode = document.SelectSingleNode(
                    "/*[local-name()='GuiConfigFile']/*[local-name()='Version']");
                int declaredVersion = 1;
                if (versionNode != null &&
                    !int.TryParse(versionNode.InnerText, NumberStyles.None, CultureInfo.InvariantCulture,
                        out declaredVersion))
                {
                    throw new InvalidDataException("Configuration Version must be a non-negative integer.");
                }
                if (declaredVersion > GuiConfigFile.CurrentVersion)
                {
                    throw new UnsupportedConfigurationVersionException(
                        "Configuration schema v" +
                        declaredVersion.ToString(CultureInfo.InvariantCulture) +
                        " is newer than this SBMS build supports. The file was left untouched.");
                }
                if (declaredVersion < 1)
                {
                    throw new InvalidDataException("Configuration Version must be at least 1.");
                }
                GuiConfigFile config;
                using (var nodeReader = new XmlNodeReader(document))
                {
                    config = (GuiConfigFile)serializer.Deserialize(nodeReader);
                }

                if (versionNode == null)
                {
                    config.Version = 1;
                }

                bool migrated;
                config = GuiConfigMigrator.MigrateToCurrent(config, out migrated);
                ConfigurationValidationResult validation = GuiConfigValidator.ValidateStatic(config);
                if (validation.HasErrors)
                {
                    throw new ConfigurationValidationException(validation);
                }
                return new ReadOutcome
                {
                    Config = config,
                    Migrated = migrated,
                    Diagnostic = "ok"
                };
            }
            catch (UnsupportedConfigurationVersionException ex)
            {
                return new ReadOutcome
                {
                    UnsupportedFutureVersion = true,
                    Diagnostic = ex.Message
                };
            }
            catch (ConfigurationValidationException ex)
            {
                return new ReadOutcome { Diagnostic = ex.Message };
            }
            catch (InvalidDataException ex)
            {
                return new ReadOutcome { Diagnostic = ex.Message };
            }
            catch (InvalidOperationException ex)
            {
                return new ReadOutcome
                {
                    Diagnostic = "XML does not match the SBMS configuration schema: " +
                                 (ex.InnerException == null ? ex.Message : ex.InnerException.Message)
                };
            }
            catch (XmlException ex)
            {
                return new ReadOutcome { Diagnostic = "Malformed or unsafe XML: " + ex.Message };
            }
        }

        private static void WriteDurable(string path, GuiConfigFile config)
        {
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                OmitXmlDeclaration = false,
                CloseOutput = false
            };
            var serializer = new XmlSerializer(typeof(GuiConfigFile));
            using (FileStream stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                using (XmlWriter writer = XmlWriter.Create(stream, settings))
                {
                    serializer.Serialize(writer, config);
                }
                stream.Flush(true);
            }
        }

        private static void CreateInitialBackup(string primaryPath, string backupPath)
        {
            string tempBackup = NewTempPath(backupPath);
            try
            {
                CopyDurable(primaryPath, tempBackup);
                if (File.Exists(backupPath))
                {
                    File.Replace(tempBackup, backupPath, null, true);
                }
                else
                {
                    File.Move(tempBackup, backupPath);
                }
            }
            finally
            {
                TryDelete(tempBackup);
            }
        }

        private static bool PathExistsStrict(string path)
        {
            try
            {
                File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static void RestorePrimaryFromBackup(string primaryPath, string backupPath)
        {
            string tempPath = NewTempPath(primaryPath);
            try
            {
                CopyDurable(backupPath, tempPath);
                ReadOutcome verification = ReadValidated(tempPath);
                if (verification.Config == null || verification.UnsupportedFutureVersion)
                {
                    throw new InvalidDataException(
                        "Last-known-good copy failed recovery read-back verification.");
                }
                if (File.Exists(primaryPath))
                {
                    throw new IOException(
                        "Invalid primary must be preserved before last-known-good recovery.");
                }
                File.Move(tempPath, primaryPath);
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private static void CopyDurable(string sourcePath, string destinationPath)
        {
            using (FileStream source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan))
            using (FileStream destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(true);
            }
        }

        private static string Quarantine(string path, string category)
        {
            string directory = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            string archiveName = name + "." + category + "." +
                                 DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) +
                                 "." + Guid.NewGuid().ToString("N") + ".xml";
            string destination = Path.Combine(directory ?? "", archiveName);
            File.Move(path, destination);
            return destination;
        }

        private static void CleanupOrphanTemps(string path, ConfigurationLoadResult result)
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }
            string pattern = Path.GetFileName(path) + ".tmp.*";
            foreach (string tempPath in Directory.GetFiles(directory, pattern))
            {
                try
                {
                    File.Delete(tempPath);
                    result.Diagnostics.Add("Removed orphan configuration temp: " + tempPath);
                }
                catch (Exception ex)
                {
                    result.PersistenceDegraded = true;
                    result.Diagnostics.Add(
                        "Could not remove orphan configuration temp " + tempPath + ": " + ex.Message);
                }
            }
        }

        private static string GetBackupPath(string path)
        {
            return path + ".bak";
        }

        private static string NewTempPath(string path)
        {
            return path + ".tmp." + Guid.NewGuid().ToString("N");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
