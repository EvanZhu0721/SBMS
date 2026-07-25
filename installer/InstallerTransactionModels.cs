using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;

namespace SBMSSetup
{
    internal static class WindowsPathSafety
    {
        internal static void RequireCanonicalFullyQualified(
            string value,
            string label)
        {
            if (String.IsNullOrWhiteSpace(value) ||
                ContainsControl(value) ||
                value.IndexOf('/') >= 0 ||
                value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                value.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    label + " is not a canonical Windows path.");
            }

            bool driveAbsolute =
                value.Length >= 3 &&
                Char.IsLetter(value[0]) &&
                value[1] == ':' &&
                value[2] == '\\';
            bool unc = value.StartsWith(
                @"\\",
                StringComparison.Ordinal);
            if (!driveAbsolute && !unc)
            {
                throw new InvalidOperationException(
                    label + " is not fully qualified.");
            }

            string[] segments;
            int firstSegment;
            if (driveAbsolute)
            {
                segments = value.Substring(3).Split('\\');
                firstSegment = 0;
            }
            else
            {
                segments = value.Substring(2).Split('\\');
                if (segments.Length < 2 ||
                    String.IsNullOrWhiteSpace(segments[0]) ||
                    String.IsNullOrWhiteSpace(segments[1]))
                {
                    throw new InvalidOperationException(
                        label + " UNC server/share is incomplete.");
                }
                firstSegment = 0;
            }
            for (int index = firstSegment;
                 index < segments.Length;
                 ++index)
            {
                string segment = segments[index];
                bool trailingEmpty =
                    index == segments.Length - 1 &&
                    segment.Length == 0;
                if ((!trailingEmpty && segment.Length == 0) ||
                    segment == "." ||
                    segment == ".." ||
                    segment.IndexOf(':') >= 0)
                {
                    throw new InvalidOperationException(
                        label + " is not canonical.");
                }
            }

            string canonical;
            try
            {
                canonical = Path.GetFullPath(value);
            }
            catch (Exception failure)
            {
                throw new InvalidOperationException(
                    label + " is not a valid Windows path.",
                    failure);
            }
            if (!String.Equals(
                TrimDirectorySeparator(canonical),
                TrimDirectorySeparator(value),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    label + " is not canonical.");
            }
        }

        internal static bool ContainsControl(string value)
        {
            if (value == null)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (Char.IsControl(character))
                {
                    return true;
                }
            }
            return false;
        }

        internal static void RequireCanonicalRelative(
            string value,
            string label)
        {
            if (String.IsNullOrWhiteSpace(value) ||
                ContainsControl(value) ||
                value.IndexOf('/') >= 0 ||
                value[0] == '\\' ||
                (value.Length >= 2 && value[1] == ':'))
            {
                throw new InvalidOperationException(
                    label + " is not a canonical relative Windows path.");
            }
            string[] segments = value.Split('\\');
            foreach (string segment in segments)
            {
                if (segment.Length == 0 ||
                    segment == "." ||
                    segment == ".." ||
                    segment.IndexOf(':') >= 0)
                {
                    throw new InvalidOperationException(
                        label + " is not canonical.");
                }
            }
        }

        private static string TrimDirectorySeparator(string value)
        {
            if (value.Length == 3 &&
                Char.IsLetter(value[0]) &&
                value[1] == ':' &&
                value[2] == '\\')
            {
                return value;
            }
            return value.TrimEnd('\\');
        }
    }

    internal enum InstallOperation
    {
        FreshInstall,
        Upgrade,
        Repair,
        ExplicitDowngrade,
        Uninstall
    }

    internal enum InstallOperationRequest
    {
        Auto,
        ExplicitDowngrade,
        Uninstall
    }

    internal enum InstallerMutation
    {
        CreateEscrow,
        StagePayload,
        StageDriver,
        CommitPayload,
        ActivateDriver,
        ApplyIntegrations,
        RemoveStaleOwnedAssets,
        RemoveIntegrations,
        RemoveOwnedDevices,
        RemoveOwnedPackages,
        RemoveOwnedPayload
    }

    internal enum TransactionStatus
    {
        Created,
        Applying,
        RollingBack,
        RolledBack,
        Committed,
        RecoveryFailed
    }

    internal enum CompensationIntentStatus
    {
        Prepared,
        Applied,
        RestorePrepared,
        Restored,
        RestoreFailed
    }

    internal enum InstallerCompensationAction
    {
        RetainEscrowUntilBaselineVerified,
        RemoveTransactionPayloadStaging,
        RemoveTransactionDriverStaging,
        RestoreBaselinePayload,
        RestoreBaselineDeviceBindings,
        RestoreBaselineIntegrations,
        RestoreBaselineOwnedAssets,
        RestoreBaselineDevices,
        RestoreBaselineDriverPackages
    }

    internal enum TransactionFinalizationStatus
    {
        NotRequired,
        Pending,
        Complete,
        Failed
    }

    internal enum RecoveryEvidenceState
    {
        Complete,
        Partial,
        Unavailable
    }

    [DataContract]
    internal sealed class InstallerRequestFlags
    {
        [DataMember(Order = 1)]
        internal bool InstallDriver;

        [DataMember(Order = 2)]
        internal bool CreateShortcut;

        [DataMember(Order = 3)]
        internal bool CreateStartupTask;

        [DataMember(Order = 4)]
        internal bool PreserveConfiguration;
    }

    [DataContract]
    internal sealed class InstallerTransactionRequest
    {
        [DataMember(Order = 1)]
        internal InstallOperationRequest RequestedOperation;

        [DataMember(Order = 2)]
        internal ReleaseIdentity Target;

        [DataMember(Order = 3)]
        internal InstallerRequestFlags Flags;
    }

    [DataContract]
    internal sealed class ReleaseIdentity
    {
        [DataMember(Order = 1)]
        internal string Version;

        [DataMember(Order = 2)]
        internal string PackageFingerprint;

        internal ReleaseIdentity()
        {
        }

        internal ReleaseIdentity(string version, string packageFingerprint)
        {
            if (String.IsNullOrWhiteSpace(version))
            {
                throw new ArgumentException("Release version is required.", "version");
            }
            if (String.IsNullOrWhiteSpace(packageFingerprint))
            {
                throw new ArgumentException("Package fingerprint is required.", "packageFingerprint");
            }
            Version = version.Trim();
            PackageFingerprint = packageFingerprint.Trim();
        }

        internal void Validate()
        {
            Require(Version, "release version");
            Require(PackageFingerprint, "package fingerprint");
        }

        private static void Require(string value, string label)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(label + " is missing.");
            }
        }
    }

    [DataContract]
    internal sealed class InstalledReleaseState
    {
        [DataMember(Order = 1)]
        internal bool IsInstalled;

        [DataMember(Order = 2)]
        internal ReleaseIdentity Release;

        internal void Validate()
        {
            if (IsInstalled && Release == null)
            {
                throw new InvalidOperationException(
                    "Installed release identity is missing.");
            }
            if (!IsInstalled && Release != null)
            {
                throw new InvalidOperationException(
                    "Absent installed state carries a release identity.");
            }
            if (Release != null)
            {
                Release.Validate();
            }
        }
    }

    [DataContract]
    internal sealed class PayloadEvidence
    {
        [DataMember(Order = 1)]
        internal bool Present;

        [DataMember(Order = 2)]
        internal string ReleaseVersion;

        [DataMember(Order = 3)]
        internal string PackageFingerprint;

        internal void Validate()
        {
            if (Present)
            {
                Require(ReleaseVersion, "payload release version");
                Require(PackageFingerprint, "payload package fingerprint");
            }
            else if (!String.IsNullOrEmpty(ReleaseVersion) ||
                     !String.IsNullOrEmpty(PackageFingerprint))
            {
                throw new InvalidOperationException(
                    "Absent payload carries release identity.");
            }
        }

        private static void Require(string value, string label)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(label + " is missing.");
            }
        }
    }

    [DataContract]
    internal sealed class DriverEvidence
    {
        [DataMember(Order = 1)]
        internal bool Present;

        [DataMember(Order = 2)]
        internal string PackageSetFingerprint;

        [DataMember(Order = 3)]
        internal string ActivePublishedInf;

        [DataMember(Order = 4)]
        internal string BindingFingerprint;

        [DataMember(Order = 5)]
        internal string DeviceInstanceFingerprint;

        [DataMember(Order = 6)]
        internal bool HasProblem;

        [DataMember(Order = 7)]
        internal int ProblemCode;

        [DataMember(Order = 8)]
        internal bool PackagePresent;

        internal void Validate()
        {
            if (Present)
            {
                Require(PackageSetFingerprint, "driver package set fingerprint");
                Require(ActivePublishedInf, "active published INF");
                Require(BindingFingerprint, "driver binding fingerprint");
                Require(DeviceInstanceFingerprint, "device instance fingerprint");
            }
            else if ((!PackagePresent &&
                      !String.IsNullOrEmpty(PackageSetFingerprint)) ||
                     !String.IsNullOrEmpty(ActivePublishedInf) ||
                     !String.IsNullOrEmpty(BindingFingerprint) ||
                     !String.IsNullOrEmpty(DeviceInstanceFingerprint))
            {
                throw new InvalidOperationException(
                    "Driver evidence carries an inconsistent package or binding identity.");
            }
            if (PackagePresent)
            {
                Require(PackageSetFingerprint, "driver package set fingerprint");
            }
            if (!Present && (HasProblem || ProblemCode != 0))
            {
                throw new InvalidOperationException(
                    "Absent driver carries a device problem state.");
            }
            if (HasProblem != (ProblemCode != 0))
            {
                throw new InvalidOperationException(
                    "Driver problem flag and problem code disagree.");
            }
        }

        internal void ValidateForRecovery()
        {
            if (PackagePresent && String.IsNullOrWhiteSpace(PackageSetFingerprint))
            {
                throw new InvalidOperationException(
                    "Recovery driver package fingerprint is missing.");
            }
            if (HasProblem != (ProblemCode != 0))
            {
                throw new InvalidOperationException(
                    "Driver problem flag and problem code disagree.");
            }
        }

        private static void Require(string value, string label)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(label + " is missing.");
            }
        }
    }

    [DataContract]
    internal sealed class EscrowEvidence
    {
        [DataMember(Order = 1)]
        internal string ManifestPath;

        [DataMember(Order = 2)]
        internal string ManifestSha256;

        [DataMember(Order = 3)]
        internal bool Complete;

        [DataMember(Order = 4)]
        internal int DriverPackageCount;

        [DataMember(Order = 5)]
        internal int PayloadFileCount;

        [DataMember(Order = 6)]
        internal int ConfigurationFileCount;

        [DataMember(Order = 7)]
        internal int IntegrationCount;

        internal void Validate()
        {
            if (DriverPackageCount < 0 || PayloadFileCount < 0 ||
                ConfigurationFileCount < 0 || IntegrationCount < 0)
            {
                throw new InvalidOperationException(
                    "Escrow evidence count is invalid.");
            }
            if (Complete)
            {
                if (String.IsNullOrWhiteSpace(ManifestPath) ||
                    String.IsNullOrWhiteSpace(ManifestSha256) ||
                    ManifestSha256.Length != 64)
                {
                    throw new InvalidOperationException(
                        "Complete escrow manifest evidence is invalid.");
                }
                WindowsPathSafety.RequireCanonicalFullyQualified(
                    ManifestPath,
                    "Complete escrow manifest path");
            }
            else if (!String.IsNullOrEmpty(ManifestPath) ||
                     !String.IsNullOrEmpty(ManifestSha256) ||
                     DriverPackageCount != 0 ||
                     PayloadFileCount != 0 ||
                     ConfigurationFileCount != 0 ||
                     IntegrationCount != 0)
            {
                throw new InvalidOperationException(
                    "Incomplete escrow carries completed evidence.");
            }
        }
    }

    internal enum EscrowRetentionState
    {
        Building,
        SealedForRollback,
        FinalizationPending,
        Finalized,
        RetainedAfterCleanupFailure
    }

    internal enum BaselinePayloadState
    {
        Absent,
        Present
    }

    internal enum EscrowContentKind
    {
        BaselinePayload,
        TargetPayload,
        Configuration
    }

    [DataContract]
    internal sealed class EscrowContentEntry
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal string RelativePath;

        [DataMember(Order = 2, IsRequired = true)]
        internal EscrowContentKind Kind;

        [DataMember(Order = 3, IsRequired = true)]
        internal long Length;

        [DataMember(Order = 4, IsRequired = true)]
        internal string Sha256;

        internal string StorageRelativePath
        {
            get
            {
                return EscrowManifestValidation.ContentRoot(Kind) +
                    "\\" + RelativePath;
            }
        }

        internal void Validate()
        {
            if (Length < 0 ||
                !EscrowManifestValidation.IsSha256(Sha256) ||
                !Enum.IsDefined(typeof(EscrowContentKind), Kind))
            {
                throw new InvalidOperationException(
                    "Escrow content entry is unsafe or incomplete.");
            }
            WindowsPathSafety.RequireCanonicalRelative(
                RelativePath,
                "Escrow content entry path");
        }
    }

    [DataContract]
    internal sealed class EscrowManifest
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal int Revision;

        [DataMember(Order = 3, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 4, IsRequired = true)]
        internal InstallOperation Operation;

        [DataMember(Order = 5, IsRequired = true)]
        internal string BaselineEvidenceDigest;

        [DataMember(Order = 6, IsRequired = true)]
        internal BaselinePayloadState BaselinePayloadState;

        [DataMember(Order = 7, IsRequired = true)]
        internal ReleaseIdentity Target;

        [DataMember(Order = 8, IsRequired = true)]
        internal List<EscrowContentEntry> Content;

        [DataMember(Order = 9, IsRequired = true)]
        internal bool Sealed;

        [DataMember(Order = 10, IsRequired = true)]
        internal string SealedUtc;

        [DataMember(Order = 11, IsRequired = true)]
        internal EscrowRetentionState RetentionState;

        [DataMember(Order = 12, IsRequired = true)]
        internal string FinalizationEvidence;

        internal EscrowManifest()
        {
            Content = new List<EscrowContentEntry>();
        }

        internal void Validate()
        {
            Guid parsed;
            if (SchemaVersion != 2 ||
                Revision <= 0 ||
                !Guid.TryParseExact(TransactionId, "N", out parsed) ||
                !String.Equals(
                    TransactionId,
                    parsed.ToString("N"),
                    StringComparison.Ordinal) ||
                !EscrowManifestValidation.IsSha256(BaselineEvidenceDigest) ||
                !Enum.IsDefined(typeof(InstallOperation), Operation) ||
                !Enum.IsDefined(
                    typeof(BaselinePayloadState),
                    BaselinePayloadState) ||
                !Enum.IsDefined(
                    typeof(EscrowRetentionState),
                    RetentionState) ||
                Content == null)
            {
                throw new InvalidOperationException(
                    "Escrow manifest identity is incomplete.");
            }
            if (Operation == InstallOperation.Uninstall)
            {
                if (Target != null)
                {
                    throw new InvalidOperationException(
                        "Uninstall escrow cannot carry a target release.");
                }
            }
            else
            {
                if (Target == null)
                {
                    throw new InvalidOperationException(
                        "Install escrow target release is missing.");
                }
                Target.Validate();
            }
            bool operationRequiresBaseline =
                Operation != InstallOperation.FreshInstall;
            if ((BaselinePayloadState == BaselinePayloadState.Present) !=
                operationRequiresBaseline)
            {
                throw new InvalidOperationException(
                    "Escrow operation disagrees with baseline payload state.");
            }
            var uniqueContent = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            bool hasBaselinePayload = false;
            foreach (EscrowContentEntry entry in Content)
            {
                if (entry == null)
                {
                    throw new InvalidOperationException(
                        "Escrow manifest contains a null content entry.");
                }
                entry.Validate();
                string contentIdentity =
                    entry.Kind.ToString() + "\0" + entry.RelativePath;
                if (!uniqueContent.Add(contentIdentity))
                {
                    throw new InvalidOperationException(
                        "Escrow manifest contains duplicate content.");
                }
                if (entry.Kind == EscrowContentKind.BaselinePayload)
                {
                    hasBaselinePayload = true;
                }
                if (entry.Kind == EscrowContentKind.TargetPayload)
                {
                    throw new InvalidOperationException(
                        "Sealed rollback escrow cannot contain target payload.");
                }
            }
            if ((BaselinePayloadState == BaselinePayloadState.Present) !=
                hasBaselinePayload)
            {
                throw new InvalidOperationException(
                    "Baseline payload state disagrees with escrow content.");
            }
            if (Sealed)
            {
                if (!EscrowManifestValidation.IsUtcTimestamp(SealedUtc) ||
                    RetentionState == EscrowRetentionState.Building)
                {
                    throw new InvalidOperationException(
                        "Sealed escrow retention state or timestamp is invalid.");
                }
                bool requiresFinalizationEvidence =
                    RetentionState == EscrowRetentionState.Finalized ||
                    RetentionState ==
                        EscrowRetentionState.RetainedAfterCleanupFailure;
                if (requiresFinalizationEvidence !=
                    !String.IsNullOrWhiteSpace(FinalizationEvidence) ||
                    (!requiresFinalizationEvidence &&
                     !String.IsNullOrEmpty(FinalizationEvidence)) ||
                    !EscrowManifestValidation.IsSafeOptionalEvidence(
                        FinalizationEvidence))
                {
                    throw new InvalidOperationException(
                        "Escrow finalization evidence is inconsistent.");
                }
            }
            else if (RetentionState != EscrowRetentionState.Building ||
                     !String.IsNullOrEmpty(SealedUtc) ||
                     !String.IsNullOrEmpty(FinalizationEvidence))
            {
                throw new InvalidOperationException(
                    "Unsealed escrow cannot enter retention lifecycle.");
            }
        }
    }

    internal static class EscrowManifestValidation
    {
        internal static string ContentRoot(EscrowContentKind kind)
        {
            switch (kind)
            {
                case EscrowContentKind.BaselinePayload:
                    return @"baseline\payload";
                case EscrowContentKind.TargetPayload:
                    return @"target\payload";
                case EscrowContentKind.Configuration:
                    return @"baseline\configuration";
                default:
                    throw new InvalidOperationException(
                        "Escrow content kind has no storage namespace.");
            }
        }

        internal static bool IsSha256(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 64)
            {
                return false;
            }
            for (int index = 0; index < value.Length; ++index)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool IsUtcTimestamp(string value)
        {
            if (String.IsNullOrWhiteSpace(value) ||
                !value.EndsWith("Z", StringComparison.Ordinal))
            {
                return false;
            }
            DateTimeOffset parsed;
            return DateTimeOffset.TryParseExact(
                       value,
                       "o",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind,
                       out parsed) &&
                   parsed.Offset == TimeSpan.Zero;
        }

        internal static bool IsSafeOptionalEvidence(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return true;
            }
            if (value.Length > 512)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (Char.IsControl(character))
                {
                    return false;
                }
            }
            return true;
        }
    }

    [DataContract]
    internal sealed class IntegrationEvidence
    {
        [DataMember(Order = 1)]
        internal string ShortcutFingerprint;

        [DataMember(Order = 2)]
        internal string StartupTaskFingerprint;

        internal void Validate()
        {
            Require(ShortcutFingerprint, "shortcut fingerprint");
            Require(StartupTaskFingerprint, "startup task fingerprint");
        }

        private static void Require(string value, string label)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(label + " is missing.");
            }
        }
    }

    [DataContract]
    internal sealed class ConfigurationEvidence
    {
        [DataMember(Order = 1)]
        internal string SchemaVersion;

        [DataMember(Order = 2)]
        internal string ContentFingerprint;

        internal void Validate()
        {
            Require(SchemaVersion, "configuration schema");
            Require(ContentFingerprint, "configuration fingerprint");
        }

        private static void Require(string value, string label)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(label + " is missing.");
            }
        }
    }

    [DataContract]
    internal sealed class DisplayEvidence
    {
        [DataMember(Order = 1)]
        internal int ActivePhysicalPathCount;

        [DataMember(Order = 2)]
        internal string ActivePhysicalPathFingerprint;

        internal void Validate()
        {
            if (ActivePhysicalPathCount < 1)
            {
                throw new InvalidOperationException(
                    "At least one active physical display path is required.");
            }
            if (String.IsNullOrWhiteSpace(ActivePhysicalPathFingerprint))
            {
                throw new InvalidOperationException(
                    "Active physical path fingerprint is missing.");
            }
        }

        internal void ValidateForRecovery()
        {
            if (ActivePhysicalPathCount < 0)
            {
                throw new InvalidOperationException(
                    "Active physical display path count is invalid.");
            }
            if (ActivePhysicalPathCount > 0 &&
                String.IsNullOrWhiteSpace(ActivePhysicalPathFingerprint))
            {
                throw new InvalidOperationException(
                    "Active physical path fingerprint is missing.");
            }
        }
    }

    [DataContract]
    internal sealed class RecoveryEvidenceEnvelope
    {
        [DataMember(Order = 1)]
        internal RecoveryEvidenceState PayloadState;

        [DataMember(Order = 2)]
        internal string PayloadLocator;

        [DataMember(Order = 3)]
        internal RecoveryEvidenceState IntegrationsState;

        [DataMember(Order = 4)]
        internal string IntegrationsLocator;

        [DataMember(Order = 5)]
        internal RecoveryEvidenceState ConfigurationState;

        [DataMember(Order = 6)]
        internal string ConfigurationLocator;

        [DataMember(Order = 7)]
        internal RecoveryEvidenceState EscrowState;

        [DataMember(Order = 8)]
        internal string EscrowLocator;

        [DataMember(Order = 9)]
        internal string CaptureErrorFingerprint;

        internal void Validate()
        {
            ValidateComponent(
                PayloadState,
                PayloadLocator,
                "payload");
            ValidateComponent(
                IntegrationsState,
                IntegrationsLocator,
                "integrations");
            ValidateComponent(
                ConfigurationState,
                ConfigurationLocator,
                "configuration");
            ValidateComponent(
                EscrowState,
                EscrowLocator,
                "escrow");
            if (CaptureErrorFingerprint == null ||
                ContainsControl(CaptureErrorFingerprint))
            {
                throw new InvalidOperationException(
                    "Recovery capture error fingerprint is invalid.");
            }
        }

        private static void ValidateComponent(
            RecoveryEvidenceState state,
            string locator,
            string label)
        {
            if (!Enum.IsDefined(typeof(RecoveryEvidenceState), state))
            {
                throw new InvalidOperationException(
                    "Recovery " + label + " state is invalid.");
            }
            if (state == RecoveryEvidenceState.Complete)
            {
                if (locator != null && ContainsControl(locator))
                {
                    throw new InvalidOperationException(
                        "Recovery " + label + " locator is invalid.");
                }
                return;
            }
            if (String.IsNullOrWhiteSpace(locator) ||
                WindowsPathSafety.ContainsControl(locator))
            {
                throw new InvalidOperationException(
                    "Degraded recovery " + label +
                    " evidence has no safe absolute locator.");
            }
            WindowsPathSafety.RequireCanonicalFullyQualified(
                locator,
                "Degraded recovery " + label + " locator");
        }

        internal static bool ContainsControl(string value)
        {
            return WindowsPathSafety.ContainsControl(value);
        }
    }

    [DataContract]
    internal sealed class MachineSnapshot
    {
        [DataMember(Order = 1)]
        internal PayloadEvidence Payload;

        [DataMember(Order = 2)]
        internal DriverEvidence Driver;

        [DataMember(Order = 3)]
        internal IntegrationEvidence Integrations;

        [DataMember(Order = 4)]
        internal ConfigurationEvidence Configuration;

        [DataMember(Order = 5)]
        internal DisplayEvidence Display;

        [DataMember(Order = 6)]
        internal EscrowEvidence Escrow;

        [DataMember(Order = 7)]
        internal RecoveryEvidenceEnvelope Recovery;

        internal void Validate()
        {
            if (Payload == null || Driver == null || Integrations == null ||
                Configuration == null || Display == null || Escrow == null)
            {
                throw new InvalidOperationException(
                    "Snapshot structured evidence is incomplete.");
            }
            Payload.Validate();
            Driver.Validate();
            Integrations.Validate();
            Configuration.Validate();
            Display.Validate();
            Escrow.Validate();
            if (Recovery != null)
            {
                Recovery.Validate();
                if (Recovery.PayloadState != RecoveryEvidenceState.Complete ||
                    Recovery.IntegrationsState !=
                        RecoveryEvidenceState.Complete ||
                    Recovery.ConfigurationState !=
                        RecoveryEvidenceState.Complete ||
                    Recovery.EscrowState != RecoveryEvidenceState.Complete)
                {
                    throw new InvalidOperationException(
                        "Strict snapshot validation rejects degraded recovery evidence.");
                }
            }
            if (Payload.Present)
            {
                if (String.IsNullOrWhiteSpace(Payload.ReleaseVersion) ||
                    String.IsNullOrWhiteSpace(Payload.PackageFingerprint))
                {
                    throw new InvalidOperationException(
                        "Present payload identity is incomplete.");
                }
            }
            else if (!String.IsNullOrEmpty(Payload.ReleaseVersion) ||
                     !String.IsNullOrEmpty(Payload.PackageFingerprint))
            {
                throw new InvalidOperationException(
                    "Absent payload carries release identity.");
            }
        }

        // Recovery must be able to inspect damage caused by a partial native
        // operation. It validates the evidence envelope without requiring the
        // healthy display/device invariants enforced for normal commit.
        internal void ValidateForRecovery()
        {
            if (Payload == null || Driver == null || Integrations == null ||
                Configuration == null || Display == null || Escrow == null)
            {
                throw new InvalidOperationException(
                    "Recovery snapshot structured evidence is incomplete.");
            }
            RecoveryEvidenceEnvelope envelope = Recovery ??
                new RecoveryEvidenceEnvelope
                {
                    PayloadState = RecoveryEvidenceState.Complete,
                    PayloadLocator = String.Empty,
                    IntegrationsState = RecoveryEvidenceState.Complete,
                    IntegrationsLocator = String.Empty,
                    ConfigurationState = RecoveryEvidenceState.Complete,
                    ConfigurationLocator = String.Empty,
                    EscrowState = RecoveryEvidenceState.Complete,
                    EscrowLocator = String.Empty,
                    CaptureErrorFingerprint = String.Empty
                };
            envelope.Validate();
            if (envelope.PayloadState == RecoveryEvidenceState.Complete)
            {
                Payload.Validate();
            }
            else
            {
                ValidatePartialPayload(Payload);
            }
            Driver.ValidateForRecovery();
            if (envelope.IntegrationsState ==
                RecoveryEvidenceState.Complete)
            {
                Integrations.Validate();
            }
            else
            {
                ValidatePartialText(
                    Integrations.ShortcutFingerprint,
                    "shortcut");
                ValidatePartialText(
                    Integrations.StartupTaskFingerprint,
                    "startup task");
            }
            if (envelope.ConfigurationState ==
                RecoveryEvidenceState.Complete)
            {
                Configuration.Validate();
            }
            else
            {
                ValidatePartialText(
                    Configuration.SchemaVersion,
                    "configuration schema");
                ValidatePartialText(
                    Configuration.ContentFingerprint,
                    "configuration fingerprint");
            }
            Display.ValidateForRecovery();
            if (envelope.EscrowState == RecoveryEvidenceState.Complete)
            {
                Escrow.Validate();
            }
            else
            {
                ValidatePartialEscrow(Escrow);
            }
        }

        private static void ValidatePartialPayload(PayloadEvidence payload)
        {
            ValidatePartialText(
                payload.ReleaseVersion,
                "payload release version");
            ValidatePartialText(
                payload.PackageFingerprint,
                "payload package fingerprint");
        }

        private static void ValidatePartialEscrow(EscrowEvidence escrow)
        {
            if (escrow.DriverPackageCount < 0 ||
                escrow.PayloadFileCount < 0 ||
                escrow.ConfigurationFileCount < 0 ||
                escrow.IntegrationCount < 0)
            {
                throw new InvalidOperationException(
                    "Recovery escrow evidence count is invalid.");
            }
            if (escrow.ManifestPath == null ||
                WindowsPathSafety.ContainsControl(
                    escrow.ManifestPath) ||
                (!String.IsNullOrEmpty(escrow.ManifestPath) &&
                 !IsCanonicalFullyQualified(escrow.ManifestPath)))
            {
                throw new InvalidOperationException(
                    "Recovery escrow manifest path is invalid.");
            }
            if (escrow.ManifestSha256 == null ||
                (escrow.ManifestSha256.Length != 0 &&
                 !IsSha256(escrow.ManifestSha256)))
            {
                throw new InvalidOperationException(
                    "Recovery escrow manifest digest is invalid.");
            }
        }

        private static bool IsCanonicalFullyQualified(string value)
        {
            try
            {
                WindowsPathSafety.RequireCanonicalFullyQualified(
                    value,
                    "Recovery escrow manifest path");
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void ValidatePartialText(
            string value,
            string label)
        {
            if (value == null ||
                RecoveryEvidenceEnvelope.ContainsControl(value) ||
                (value.Length > 0 &&
                 String.IsNullOrWhiteSpace(value)))
            {
                throw new InvalidOperationException(
                    "Recovery " + label + " evidence is invalid.");
            }
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }
            foreach (char character in value)
            {
                bool digit = character >= '0' && character <= '9';
                bool lower = character >= 'a' && character <= 'f';
                bool upper = character >= 'A' && character <= 'F';
                if (!digit && !lower && !upper)
                {
                    return false;
                }
            }
            return true;
        }

        internal string EvidenceDigest
        {
            get
            {
                Validate();
                return String.Join("|", new[]
                {
                    Payload.Present.ToString(CultureInfo.InvariantCulture),
                    Payload.ReleaseVersion,
                    Payload.PackageFingerprint,
                    Driver.PackageSetFingerprint,
                    Driver.PackagePresent.ToString(CultureInfo.InvariantCulture),
                    Driver.Present.ToString(CultureInfo.InvariantCulture),
                    Driver.ActivePublishedInf,
                    Driver.BindingFingerprint,
                    Driver.DeviceInstanceFingerprint,
                    Driver.HasProblem.ToString(CultureInfo.InvariantCulture),
                    Driver.ProblemCode.ToString(CultureInfo.InvariantCulture),
                    Integrations.ShortcutFingerprint,
                    Integrations.StartupTaskFingerprint,
                    Configuration.SchemaVersion,
                    Configuration.ContentFingerprint,
                    Display.ActivePhysicalPathCount.ToString(CultureInfo.InvariantCulture),
                    Display.ActivePhysicalPathFingerprint,
                    Escrow.ManifestPath,
                    Escrow.ManifestSha256,
                    Escrow.Complete.ToString(CultureInfo.InvariantCulture),
                    Escrow.DriverPackageCount.ToString(CultureInfo.InvariantCulture),
                    Escrow.PayloadFileCount.ToString(CultureInfo.InvariantCulture),
                    Escrow.ConfigurationFileCount.ToString(CultureInfo.InvariantCulture),
                    Escrow.IntegrationCount.ToString(CultureInfo.InvariantCulture)
                });
            }
        }

        internal string RecoveryEvidenceDigest
        {
            get
            {
                ValidateForRecovery();
                return String.Join("|", new[]
                {
                    Payload.Present.ToString(CultureInfo.InvariantCulture),
                    Payload.ReleaseVersion ?? String.Empty,
                    Payload.PackageFingerprint ?? String.Empty,
                    Driver.PackageSetFingerprint ?? String.Empty,
                    Driver.PackagePresent.ToString(CultureInfo.InvariantCulture),
                    Driver.Present.ToString(CultureInfo.InvariantCulture),
                    Driver.ActivePublishedInf ?? String.Empty,
                    Driver.BindingFingerprint ?? String.Empty,
                    Driver.DeviceInstanceFingerprint ?? String.Empty,
                    Driver.HasProblem.ToString(CultureInfo.InvariantCulture),
                    Driver.ProblemCode.ToString(CultureInfo.InvariantCulture),
                    Integrations.ShortcutFingerprint ?? String.Empty,
                    Integrations.StartupTaskFingerprint ?? String.Empty,
                    Configuration.SchemaVersion ?? String.Empty,
                    Configuration.ContentFingerprint ?? String.Empty,
                    Display.ActivePhysicalPathCount.ToString(CultureInfo.InvariantCulture),
                    Display.ActivePhysicalPathFingerprint ?? String.Empty,
                    Escrow.ManifestPath ?? String.Empty,
                    Escrow.ManifestSha256 ?? String.Empty,
                    Escrow.Complete.ToString(CultureInfo.InvariantCulture),
                    Escrow.DriverPackageCount.ToString(
                        CultureInfo.InvariantCulture),
                    Escrow.PayloadFileCount.ToString(
                        CultureInfo.InvariantCulture),
                    Escrow.ConfigurationFileCount.ToString(
                        CultureInfo.InvariantCulture),
                    Escrow.IntegrationCount.ToString(
                        CultureInfo.InvariantCulture),
                    Recovery == null
                        ? RecoveryEvidenceState.Complete.ToString()
                        : Recovery.PayloadState.ToString(),
                    Recovery == null ? String.Empty : Recovery.PayloadLocator,
                    Recovery == null
                        ? RecoveryEvidenceState.Complete.ToString()
                        : Recovery.IntegrationsState.ToString(),
                    Recovery == null ? String.Empty : Recovery.IntegrationsLocator,
                    Recovery == null
                        ? RecoveryEvidenceState.Complete.ToString()
                        : Recovery.ConfigurationState.ToString(),
                    Recovery == null ? String.Empty : Recovery.ConfigurationLocator,
                    Recovery == null
                        ? RecoveryEvidenceState.Complete.ToString()
                        : Recovery.EscrowState.ToString(),
                    Recovery == null ? String.Empty : Recovery.EscrowLocator,
                    Recovery == null
                        ? String.Empty
                        : Recovery.CaptureErrorFingerprint
                });
            }
        }
    }

    [DataContract]
    internal sealed class TransactionContext
    {
        [DataMember(Order = 1)]
        internal string TransactionId;

        [DataMember(Order = 2)]
        internal InstallOperation Operation;

        [DataMember(Order = 3)]
        internal InstallerRequestFlags RequestFlags;

        [DataMember(Order = 4)]
        internal MachineSnapshot Baseline;

        [DataMember(Order = 5)]
        internal string EscrowLocator;

        internal void Validate()
        {
            if (String.IsNullOrWhiteSpace(TransactionId) ||
                RequestFlags == null ||
                Baseline == null ||
                String.IsNullOrWhiteSpace(EscrowLocator))
            {
                throw new InvalidOperationException(
                    "Transaction context is incomplete.");
            }
            Guid parsedId;
            if (!Guid.TryParseExact(TransactionId, "N", out parsedId))
            {
                throw new InvalidOperationException(
                    "Transaction context identity is not an N-format GUID.");
            }
            WindowsPathSafety.RequireCanonicalFullyQualified(
                EscrowLocator,
                "Escrow locator");
            string normalizedEscrow = EscrowLocator.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!String.Equals(
                Path.GetFileName(normalizedEscrow),
                TransactionId,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Escrow locator is not bound to the transaction identity.");
            }
            if (!Enum.IsDefined(typeof(InstallOperation), Operation))
            {
                throw new InvalidOperationException(
                    "Transaction context operation is invalid.");
            }
            Baseline.Validate();
        }

        internal TransactionContext DeepClone()
        {
            return new TransactionContext
            {
                TransactionId = TransactionId,
                Operation = Operation,
                RequestFlags = new InstallerRequestFlags
                {
                    InstallDriver = RequestFlags.InstallDriver,
                    CreateShortcut = RequestFlags.CreateShortcut,
                    CreateStartupTask = RequestFlags.CreateStartupTask,
                    PreserveConfiguration = RequestFlags.PreserveConfiguration
                },
                Baseline = SnapshotClone.Clone(Baseline),
                EscrowLocator = EscrowLocator
            };
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return String.Join("|", new[]
                {
                    TransactionId,
                    Operation.ToString(),
                    RequestFlags.InstallDriver.ToString(CultureInfo.InvariantCulture),
                    RequestFlags.CreateShortcut.ToString(CultureInfo.InvariantCulture),
                    RequestFlags.CreateStartupTask.ToString(CultureInfo.InvariantCulture),
                    RequestFlags.PreserveConfiguration.ToString(CultureInfo.InvariantCulture),
                    Baseline.EvidenceDigest,
                    EscrowLocator
                });
            }
        }
    }

    internal static class SnapshotClone
    {
        internal static MachineSnapshot Clone(MachineSnapshot source)
        {
            source.Validate();
            return new MachineSnapshot
            {
                Payload = new PayloadEvidence
                {
                    Present = source.Payload.Present,
                    ReleaseVersion = source.Payload.ReleaseVersion,
                    PackageFingerprint = source.Payload.PackageFingerprint
                },
                Driver = new DriverEvidence
                {
                    Present = source.Driver.Present,
                    PackageSetFingerprint = source.Driver.PackageSetFingerprint,
                    ActivePublishedInf = source.Driver.ActivePublishedInf,
                    BindingFingerprint = source.Driver.BindingFingerprint,
                    DeviceInstanceFingerprint = source.Driver.DeviceInstanceFingerprint,
                    HasProblem = source.Driver.HasProblem,
                    ProblemCode = source.Driver.ProblemCode,
                    PackagePresent = source.Driver.PackagePresent
                },
                Integrations = new IntegrationEvidence
                {
                    ShortcutFingerprint = source.Integrations.ShortcutFingerprint,
                    StartupTaskFingerprint = source.Integrations.StartupTaskFingerprint
                },
                Configuration = new ConfigurationEvidence
                {
                    SchemaVersion = source.Configuration.SchemaVersion,
                    ContentFingerprint = source.Configuration.ContentFingerprint
                },
                Display = new DisplayEvidence
                {
                    ActivePhysicalPathCount = source.Display.ActivePhysicalPathCount,
                    ActivePhysicalPathFingerprint =
                        source.Display.ActivePhysicalPathFingerprint
                },
                Escrow = new EscrowEvidence
                {
                    ManifestPath = source.Escrow.ManifestPath,
                    ManifestSha256 = source.Escrow.ManifestSha256,
                    Complete = source.Escrow.Complete,
                    DriverPackageCount = source.Escrow.DriverPackageCount,
                    PayloadFileCount = source.Escrow.PayloadFileCount,
                    ConfigurationFileCount = source.Escrow.ConfigurationFileCount,
                    IntegrationCount = source.Escrow.IntegrationCount
                },
                Recovery = source.Recovery == null
                    ? null
                    : new RecoveryEvidenceEnvelope
                    {
                        PayloadState = source.Recovery.PayloadState,
                        PayloadLocator = source.Recovery.PayloadLocator,
                        IntegrationsState =
                            source.Recovery.IntegrationsState,
                        IntegrationsLocator =
                            source.Recovery.IntegrationsLocator,
                        ConfigurationState =
                            source.Recovery.ConfigurationState,
                        ConfigurationLocator =
                            source.Recovery.ConfigurationLocator,
                        EscrowState = source.Recovery.EscrowState,
                        EscrowLocator = source.Recovery.EscrowLocator,
                        CaptureErrorFingerprint =
                            source.Recovery.CaptureErrorFingerprint
                    }
            };
        }
    }

    [DataContract]
    internal sealed class CompensationIntent
    {
        [DataMember(Order = 1)]
        internal int Sequence;

        [DataMember(Order = 2)]
        internal InstallerMutation Mutation;

        [DataMember(Order = 3)]
        internal CompensationIntentStatus Status;

        [DataMember(Order = 4)]
        internal InstallerCompensationAction InverseAction;

        [DataMember(Order = 5)]
        internal string BeforeEvidence;

        [DataMember(Order = 6)]
        internal string AfterEvidence;

        [DataMember(Order = 7)]
        internal string RecoveryError;

        [DataMember(Order = 8)]
        internal string CompensationBeforeEvidence;
    }

    [DataContract]
    internal sealed class TransactionStageEvent
    {
        [DataMember(Order = 1)]
        internal int Sequence;

        [DataMember(Order = 2)]
        internal string TimestampUtc;

        [DataMember(Order = 3)]
        internal string Stage;

        [DataMember(Order = 4)]
        internal string Mutation;

        [DataMember(Order = 5)]
        internal string Outcome;

        [DataMember(Order = 6)]
        internal string ObservedEvidence;

        [DataMember(Order = 7)]
        internal string Detail;
    }

    [DataContract]
    internal sealed class TransactionJournal
    {
        [DataMember(Order = 1)]
        internal int SchemaVersion;

        [DataMember(Order = 2)]
        internal string TransactionId;

        [DataMember(Order = 3)]
        internal InstallOperation Operation;

        [DataMember(Order = 4)]
        internal TransactionStatus Status;

        [DataMember(Order = 5)]
        internal MachineSnapshot Baseline;

        [DataMember(Order = 6)]
        internal ReleaseIdentity Target;

        [DataMember(Order = 7)]
        internal List<CompensationIntent> Intents;

        [DataMember(Order = 8)]
        internal string LastError;

        [DataMember(Order = 9)]
        internal long Revision;

        [DataMember(Order = 10)]
        internal string CreatedUtc;

        [DataMember(Order = 11)]
        internal string UpdatedUtc;

        [DataMember(Order = 12)]
        internal List<TransactionStageEvent> StageEvents;

        [DataMember(Order = 13)]
        internal string OriginalError;

        [DataMember(Order = 14)]
        internal string RecoveryError;

        [DataMember(Order = 15)]
        internal string RollbackResult;

        [DataMember(Order = 16)]
        internal TransactionContext Context;

        [DataMember(Order = 17)]
        internal string ContentDigest;

        [DataMember(Order = 18)]
        internal TransactionFinalizationStatus FinalizationStatus;

        [DataMember(Order = 19)]
        internal string FinalizationEvidence;

        [DataMember(Order = 20)]
        internal string FinalizationError;

        internal TransactionJournal()
        {
            Intents = new List<CompensationIntent>();
            StageEvents = new List<TransactionStageEvent>();
        }

        internal static TransactionJournal Create(
            string transactionId,
            InstallOperation operation,
            MachineSnapshot baseline,
            ReleaseIdentity target,
            InstallerRequestFlags flags,
            string escrowLocator)
        {
            baseline.Validate();
            string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (String.IsNullOrWhiteSpace(transactionId))
            {
                throw new ArgumentException(
                    "Transaction identity is required.",
                    "transactionId");
            }
            var context = new TransactionContext
            {
                TransactionId = transactionId,
                Operation = operation,
                RequestFlags = new InstallerRequestFlags
                {
                    InstallDriver = flags.InstallDriver,
                    CreateShortcut = flags.CreateShortcut,
                    CreateStartupTask = flags.CreateStartupTask,
                    PreserveConfiguration = flags.PreserveConfiguration
                },
                Baseline = SnapshotClone.Clone(baseline),
                EscrowLocator = escrowLocator
            };
            context.Validate();
            return new TransactionJournal
            {
                SchemaVersion = 3,
                TransactionId = transactionId,
                Operation = operation,
                Status = TransactionStatus.Created,
                Baseline = SnapshotClone.Clone(baseline),
                Target = target == null
                    ? null
                    : new ReleaseIdentity(
                        target.Version,
                        target.PackageFingerprint),
                Intents = new List<CompensationIntent>(),
                StageEvents = new List<TransactionStageEvent>(),
                Revision = 0,
                CreatedUtc = now,
                UpdatedUtc = now,
                Context = context
                ,
                FinalizationStatus = TransactionFinalizationStatus.NotRequired,
                FinalizationEvidence = String.Empty,
                FinalizationError = String.Empty
            };
        }

        internal void AddStage(
            string stage,
            InstallerMutation? mutation,
            string outcome,
            MachineSnapshot observed,
            string detail)
        {
            StageEvents.Add(new TransactionStageEvent
            {
                Sequence = StageEvents.Count,
                TimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Stage = stage ?? String.Empty,
                Mutation = mutation.HasValue ? mutation.Value.ToString() : String.Empty,
                Outcome = outcome ?? String.Empty,
                ObservedEvidence = observed == null ? String.Empty : observed.EvidenceDigest,
                Detail = detail ?? String.Empty
            });
        }

        internal void AddRecoveryStage(
            string stage,
            InstallerMutation? mutation,
            string outcome,
            MachineSnapshot observed,
            string detail)
        {
            StageEvents.Add(new TransactionStageEvent
            {
                Sequence = StageEvents.Count,
                TimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Stage = stage ?? String.Empty,
                Mutation = mutation.HasValue ? mutation.Value.ToString() : String.Empty,
                Outcome = outcome ?? String.Empty,
                ObservedEvidence = observed == null
                    ? String.Empty
                    : observed.RecoveryEvidenceDigest,
                Detail = detail ?? String.Empty
            });
        }
    }

    internal static class InstallOperationClassifier
    {
        internal static InstallOperation Classify(
            InstallOperationRequest request,
            InstalledReleaseState installed,
            ReleaseIdentity target)
        {
            if (installed == null)
            {
                throw new ArgumentNullException("installed");
            }
            installed.Validate();
            if (request == InstallOperationRequest.Uninstall)
            {
                return InstallOperation.Uninstall;
            }
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }
            target.Validate();
            if (!installed.IsInstalled)
            {
                if (request == InstallOperationRequest.ExplicitDowngrade)
                {
                    throw new InvalidOperationException(
                        "Explicit downgrade requires an installed release.");
                }
                return InstallOperation.FreshInstall;
            }

            int comparison = CompareVersions(target.Version, installed.Release.Version);
            if (comparison == 0)
            {
                if (!String.Equals(
                    target.PackageFingerprint,
                    installed.Release.PackageFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Same-version package fingerprint collision.");
                }
                if (request == InstallOperationRequest.ExplicitDowngrade)
                {
                    throw new InvalidOperationException(
                        "Explicit downgrade target is not older than the installed release.");
                }
                return InstallOperation.Repair;
            }
            if (comparison > 0)
            {
                if (request == InstallOperationRequest.ExplicitDowngrade)
                {
                    throw new InvalidOperationException(
                        "Explicit downgrade target is newer than the installed release.");
                }
                return InstallOperation.Upgrade;
            }
            if (request != InstallOperationRequest.ExplicitDowngrade)
            {
                throw new InvalidOperationException(
                    "Downgrade requires explicit authorization.");
            }
            return InstallOperation.ExplicitDowngrade;
        }

        private static int CompareVersions(string left, string right)
        {
            int[] leftParts = ParseVersion(left);
            int[] rightParts = ParseVersion(right);
            for (int index = 0; index < leftParts.Length; ++index)
            {
                int comparison = leftParts[index].CompareTo(rightParts[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
            return 0;
        }

        private static int[] ParseVersion(string value)
        {
            string[] parts = value.Trim().Split('.');
            if (parts.Length < 2 || parts.Length > 4)
            {
                throw new InvalidOperationException(
                    "Release version must contain two to four numeric components.");
            }
            int[] parsed = new int[4];
            for (int index = 0; index < parts.Length; ++index)
            {
                int component;
                if (!Int32.TryParse(
                    parts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out component) ||
                    component < 0)
                {
                    throw new InvalidOperationException(
                        "Release version contains a non-numeric component.");
                }
                parsed[index] = component;
            }
            return parsed;
        }
    }

    internal static class InstallerTransactionPlan
    {
        private static readonly InstallerMutation[] InstallMutations =
        {
            InstallerMutation.CreateEscrow,
            InstallerMutation.StagePayload,
            InstallerMutation.StageDriver,
            InstallerMutation.CommitPayload,
            InstallerMutation.ActivateDriver,
            InstallerMutation.ApplyIntegrations,
            InstallerMutation.RemoveStaleOwnedAssets
        };

        private static readonly InstallerMutation[] UninstallMutations =
        {
            InstallerMutation.CreateEscrow,
            InstallerMutation.RemoveIntegrations,
            InstallerMutation.RemoveOwnedDevices,
            InstallerMutation.RemoveOwnedPackages,
            InstallerMutation.RemoveOwnedPayload
        };

        internal static InstallerMutation[] ForOperation(
            InstallOperation operation,
            InstallerRequestFlags flags)
        {
            if (flags == null)
            {
                throw new ArgumentNullException("flags");
            }
            InstallerMutation[] source =
                operation == InstallOperation.Uninstall
                    ? UninstallMutations
                    : InstallMutations;
            var result = new List<InstallerMutation>();
            foreach (InstallerMutation mutation in source)
            {
                if (!flags.InstallDriver &&
                    (mutation == InstallerMutation.StageDriver ||
                     mutation == InstallerMutation.ActivateDriver))
                {
                    continue;
                }
                if (mutation == InstallerMutation.ApplyIntegrations &&
                    !flags.CreateShortcut &&
                    !flags.CreateStartupTask)
                {
                    continue;
                }
                result.Add(mutation);
            }
            return result.ToArray();
        }

        internal static InstallerCompensationAction InverseFor(
            InstallerMutation mutation)
        {
            switch (mutation)
            {
                case InstallerMutation.CreateEscrow:
                    return InstallerCompensationAction.RetainEscrowUntilBaselineVerified;
                case InstallerMutation.StagePayload:
                    return InstallerCompensationAction.RemoveTransactionPayloadStaging;
                case InstallerMutation.StageDriver:
                    return InstallerCompensationAction.RemoveTransactionDriverStaging;
                case InstallerMutation.CommitPayload:
                case InstallerMutation.RemoveOwnedPayload:
                    return InstallerCompensationAction.RestoreBaselinePayload;
                case InstallerMutation.ActivateDriver:
                    return InstallerCompensationAction.RestoreBaselineDeviceBindings;
                case InstallerMutation.ApplyIntegrations:
                case InstallerMutation.RemoveIntegrations:
                    return InstallerCompensationAction.RestoreBaselineIntegrations;
                case InstallerMutation.RemoveOwnedDevices:
                    return InstallerCompensationAction.RestoreBaselineDevices;
                case InstallerMutation.RemoveOwnedPackages:
                    return InstallerCompensationAction.RestoreBaselineDriverPackages;
                case InstallerMutation.RemoveStaleOwnedAssets:
                    return InstallerCompensationAction.RestoreBaselineOwnedAssets;
                default:
                    throw new InvalidOperationException(
                        "No compensation action is defined for " + mutation + ".");
            }
        }
    }
}
