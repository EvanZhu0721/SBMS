using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;

namespace SBMSSetup
{
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
        Applied
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

        internal void Validate()
        {
            if (Present)
            {
                Require(PackageSetFingerprint, "driver package set fingerprint");
                Require(ActivePublishedInf, "active published INF");
                Require(BindingFingerprint, "driver binding fingerprint");
                Require(DeviceInstanceFingerprint, "device instance fingerprint");
            }
            else if (!String.IsNullOrEmpty(PackageSetFingerprint) ||
                     !String.IsNullOrEmpty(ActivePublishedInf) ||
                     !String.IsNullOrEmpty(BindingFingerprint) ||
                     !String.IsNullOrEmpty(DeviceInstanceFingerprint))
            {
                throw new InvalidOperationException(
                    "Absent driver carries package or binding identity.");
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
            if (!Path.IsPathRooted(EscrowLocator))
            {
                throw new InvalidOperationException(
                    "Escrow locator is not an absolute path.");
            }
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
                    ProblemCode = source.Driver.ProblemCode
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
                SchemaVersion = 2,
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

        internal static InstallerMutation[] ForOperation(InstallOperation operation)
        {
            InstallerMutation[] source =
                operation == InstallOperation.Uninstall
                    ? UninstallMutations
                    : InstallMutations;
            InstallerMutation[] result = new InstallerMutation[source.Length];
            Array.Copy(source, result, source.Length);
            return result;
        }
    }
}
