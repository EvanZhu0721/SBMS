using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;

namespace SBMSSetup
{
    [DataContract]
    internal sealed class PayloadNamespaceRootIdentity
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string CanonicalRootPath;

        [DataMember(Order = 3, IsRequired = true)]
        internal ulong VolumeSerialNumber;

        [DataMember(Order = 4, IsRequired = true)]
        internal string RootFileId;

        internal void Validate()
        {
            if (SchemaVersion != 1 || VolumeSerialNumber == 0)
            {
                throw new InvalidOperationException(
                    "Payload namespace root identity is incomplete.");
            }
            WindowsPathSafety.RequireCanonicalFullyQualified(
                CanonicalRootPath,
                "Payload namespace root path");
            PayloadContractValidation.RequireFileId(
                RootFileId,
                "Payload namespace root file ID");
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadNamespaceRootIdentity.v1",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        CanonicalRootPath.ToUpperInvariant(),
                        VolumeSerialNumber.ToString(
                            "x16",
                            CultureInfo.InvariantCulture),
                        RootFileId
                    });
            }
        }

        internal PayloadNamespaceRootIdentity DeepClone()
        {
            return new PayloadNamespaceRootIdentity
            {
                SchemaVersion = SchemaVersion,
                CanonicalRootPath = CanonicalRootPath,
                VolumeSerialNumber = VolumeSerialNumber,
                RootFileId = RootFileId
            };
        }
    }

    internal enum PayloadBuildEntryPhase
    {
        Pending,
        Created,
        Written,
        Flushed,
        Reopened,
        Verified
    }

    [DataContract]
    internal sealed class PayloadBuildEntryCheckpoint
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int Ordinal;

        [DataMember(Order = 2, IsRequired = true)]
        internal string RelativePath;

        [DataMember(Order = 3, IsRequired = true)]
        internal bool IsDirectory;

        [DataMember(Order = 4, IsRequired = true)]
        internal long ExpectedLength;

        [DataMember(Order = 5, IsRequired = true)]
        internal string ExpectedSha256;

        [DataMember(Order = 6, IsRequired = true)]
        internal PayloadBuildEntryPhase Phase;

        [DataMember(Order = 7, IsRequired = true)]
        internal string FileId;

        [DataMember(Order = 8, IsRequired = true)]
        internal long ObservedLength;

        [DataMember(Order = 9, IsRequired = true)]
        internal string ObservedSha256;

        internal void Validate()
        {
            if (Ordinal < 0 ||
                !Enum.IsDefined(typeof(PayloadBuildEntryPhase), Phase))
            {
                throw new InvalidOperationException(
                    "Payload build entry identity is invalid.");
            }
            PayloadContractValidation.RequirePayloadRelativePath(
                RelativePath,
                "Payload build entry path");

            if (IsDirectory)
            {
                if (ExpectedLength != 0 ||
                    !String.IsNullOrEmpty(ExpectedSha256) ||
                    Phase == PayloadBuildEntryPhase.Written ||
                    Phase == PayloadBuildEntryPhase.Flushed)
                {
                    throw new InvalidOperationException(
                        "Payload build directory has file-only state.");
                }
            }
            else
            {
                if (ExpectedLength < 0)
                {
                    throw new InvalidOperationException(
                        "Payload build file length is invalid.");
                }
                PayloadContractValidation.RequireSha256(
                    ExpectedSha256,
                    "Payload build file digest");
            }

            bool created = Phase != PayloadBuildEntryPhase.Pending;
            if (created)
            {
                PayloadContractValidation.RequireFileId(
                    FileId,
                    "Payload build entry file ID");
            }
            else if (!String.IsNullOrEmpty(FileId))
            {
                throw new InvalidOperationException(
                    "Pending payload build entry already has a file ID.");
            }

            bool reopened =
                Phase == PayloadBuildEntryPhase.Reopened ||
                Phase == PayloadBuildEntryPhase.Verified;
            if (reopened)
            {
                if (ObservedLength != ExpectedLength)
                {
                    throw new InvalidOperationException(
                        "Reopened payload build entry length is unexpected.");
                }
            }
            else if (ObservedLength != -1)
            {
                throw new InvalidOperationException(
                    "Payload build entry has an unearned observed length.");
            }

            if (Phase == PayloadBuildEntryPhase.Verified)
            {
                if (IsDirectory)
                {
                    if (!String.IsNullOrEmpty(ObservedSha256))
                    {
                        throw new InvalidOperationException(
                            "Verified directory has a file digest.");
                    }
                }
                else
                {
                    PayloadContractValidation.RequireSha256(
                        ObservedSha256,
                        "Verified payload build file digest");
                    if (!String.Equals(
                            ExpectedSha256,
                            ObservedSha256,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Verified payload build digest does not match.");
                    }
                }
            }
            else if (!String.IsNullOrEmpty(ObservedSha256))
            {
                throw new InvalidOperationException(
                    "Payload build entry has an unearned observed digest.");
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadBuildEntryCheckpoint.v1",
                    new[]
                    {
                        Ordinal.ToString(CultureInfo.InvariantCulture),
                        RelativePath,
                        IsDirectory.ToString(
                            CultureInfo.InvariantCulture),
                        ExpectedLength.ToString(
                            CultureInfo.InvariantCulture),
                        ExpectedSha256,
                        Phase.ToString(),
                        FileId,
                        ObservedLength.ToString(
                            CultureInfo.InvariantCulture),
                        ObservedSha256
                    });
            }
        }

        internal PayloadBuildEntryCheckpoint DeepClone()
        {
            return new PayloadBuildEntryCheckpoint
            {
                Ordinal = Ordinal,
                RelativePath = RelativePath,
                IsDirectory = IsDirectory,
                ExpectedLength = ExpectedLength,
                ExpectedSha256 = ExpectedSha256,
                Phase = Phase,
                FileId = FileId,
                ObservedLength = ObservedLength,
                ObservedSha256 = ObservedSha256
            };
        }
    }

    internal enum PayloadBuildStepKind
    {
        CreateRoot,
        CreateEntry,
        RewriteFileExact,
        FlushFile,
        ReopenEntry,
        VerifyEntryHash,
        SealCandidate,
        QuarantineBuild
    }

    [DataContract]
    internal sealed class PayloadBuildStepIntent
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string IntentId;

        [DataMember(Order = 3, IsRequired = true)]
        internal long JournalRevision;

        [DataMember(Order = 4, IsRequired = true)]
        internal PayloadBuildStepKind Kind;

        [DataMember(Order = 5, IsRequired = true)]
        internal int EntryOrdinal;

        [DataMember(Order = 6, IsRequired = true)]
        internal string ExpectedEntryInvariantDigest;

        [DataMember(Order = 7, IsRequired = true)]
        internal string ObservedPartialTreeInvariantDigest;

        internal void Validate(
            IList<PayloadBuildEntryCheckpoint> entries)
        {
            if (SchemaVersion != 1 ||
                JournalRevision < 0 ||
                !Enum.IsDefined(typeof(PayloadBuildStepKind), Kind))
            {
                throw new InvalidOperationException(
                    "Payload build intent identity is invalid.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                IntentId,
                "Payload build intent ID");
            PayloadContractValidation.RequireSha256(
                ObservedPartialTreeInvariantDigest,
                "Payload build intent observation digest");

            bool entryStep =
                Kind == PayloadBuildStepKind.CreateEntry ||
                Kind == PayloadBuildStepKind.RewriteFileExact ||
                Kind == PayloadBuildStepKind.FlushFile ||
                Kind == PayloadBuildStepKind.ReopenEntry ||
                Kind == PayloadBuildStepKind.VerifyEntryHash;
            if (!entryStep)
            {
                if (EntryOrdinal != -1 ||
                    !String.IsNullOrEmpty(ExpectedEntryInvariantDigest))
                {
                    throw new InvalidOperationException(
                        "Root payload build intent is bound to an entry.");
                }
                return;
            }

            if (entries == null ||
                EntryOrdinal < 0 ||
                EntryOrdinal >= entries.Count)
            {
                throw new InvalidOperationException(
                    "Payload build intent entry ordinal is invalid.");
            }
            PayloadContractValidation.RequireSha256(
                ExpectedEntryInvariantDigest,
                "Payload build intent entry digest");
            PayloadBuildEntryCheckpoint entry = entries[EntryOrdinal];
            if (entry == null ||
                entry.Ordinal != EntryOrdinal ||
                !String.Equals(
                    entry.InvariantDigest,
                    ExpectedEntryInvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload build intent is stale for its entry.");
            }
            ValidateEntryTransition(entry);
        }

        private void ValidateEntryTransition(
            PayloadBuildEntryCheckpoint entry)
        {
            bool valid =
                (Kind == PayloadBuildStepKind.CreateEntry &&
                    entry.Phase == PayloadBuildEntryPhase.Pending) ||
                (Kind == PayloadBuildStepKind.RewriteFileExact &&
                    !entry.IsDirectory &&
                    entry.Phase == PayloadBuildEntryPhase.Created) ||
                (Kind == PayloadBuildStepKind.FlushFile &&
                    !entry.IsDirectory &&
                    entry.Phase == PayloadBuildEntryPhase.Written) ||
                (Kind == PayloadBuildStepKind.ReopenEntry &&
                    ((entry.IsDirectory &&
                        entry.Phase == PayloadBuildEntryPhase.Created) ||
                     (!entry.IsDirectory &&
                        entry.Phase == PayloadBuildEntryPhase.Flushed))) ||
                (Kind == PayloadBuildStepKind.VerifyEntryHash &&
                    entry.Phase == PayloadBuildEntryPhase.Reopened);
            if (!valid)
            {
                throw new InvalidOperationException(
                    "Payload build intent skips a required proof phase.");
            }
        }

        internal string InvariantDigest
        {
            get
            {
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadBuildStepIntent.v1",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        IntentId,
                        JournalRevision.ToString(
                            CultureInfo.InvariantCulture),
                        Kind.ToString(),
                        EntryOrdinal.ToString(
                            CultureInfo.InvariantCulture),
                        ExpectedEntryInvariantDigest,
                        ObservedPartialTreeInvariantDigest
                    });
            }
        }

        internal PayloadBuildStepIntent DeepClone()
        {
            return new PayloadBuildStepIntent
            {
                SchemaVersion = SchemaVersion,
                IntentId = IntentId,
                JournalRevision = JournalRevision,
                Kind = Kind,
                EntryOrdinal = EntryOrdinal,
                ExpectedEntryInvariantDigest =
                    ExpectedEntryInvariantDigest,
                ObservedPartialTreeInvariantDigest =
                    ObservedPartialTreeInvariantDigest
            };
        }
    }

    [DataContract]
    internal sealed class PayloadPartialTreeObservation
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string BuildId;

        [DataMember(Order = 3, IsRequired = true)]
        internal string LeafName;

        [DataMember(Order = 4, IsRequired = true)]
        internal bool Exists;

        [DataMember(Order = 5, IsRequired = true)]
        internal ulong VolumeSerialNumber;

        [DataMember(Order = 6, IsRequired = true)]
        internal string RootFileId;

        [DataMember(Order = 7, IsRequired = true)]
        internal List<PayloadTreeEntryCheckpoint> Entries;

        internal PayloadPartialTreeObservation()
        {
            Entries = new List<PayloadTreeEntryCheckpoint>();
        }

        internal void Validate()
        {
            if (SchemaVersion != 1 || Entries == null)
            {
                throw new InvalidOperationException(
                    "Payload partial-tree observation is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                BuildId,
                "Payload partial-tree build ID");
            RequireOwnedLeaf(LeafName, ".SBMS.build." + BuildId);

            if (!Exists)
            {
                if (VolumeSerialNumber != 0 ||
                    !String.IsNullOrEmpty(RootFileId) ||
                    Entries.Count != 0)
                {
                    throw new InvalidOperationException(
                        "Absent payload partial tree contains native state.");
                }
                return;
            }
            if (VolumeSerialNumber == 0)
            {
                throw new InvalidOperationException(
                    "Payload partial-tree volume identity is missing.");
            }
            PayloadContractValidation.RequireFileId(
                RootFileId,
                "Payload partial-tree root file ID");
            ValidateObservedEntries(Entries, RootFileId);
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                var fields = new List<string>
                {
                    SchemaVersion.ToString(
                        CultureInfo.InvariantCulture),
                    BuildId,
                    LeafName,
                    Exists.ToString(CultureInfo.InvariantCulture),
                    VolumeSerialNumber.ToString(
                        "x16",
                        CultureInfo.InvariantCulture),
                    RootFileId,
                    Entries.Count.ToString(
                        CultureInfo.InvariantCulture)
                };
                foreach (PayloadTreeEntryCheckpoint entry in Entries)
                {
                    fields.Add(entry.InvariantDigest);
                }
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadPartialTreeObservation.v1",
                    fields);
            }
        }

        internal PayloadPartialTreeObservation DeepClone()
        {
            var clone = new PayloadPartialTreeObservation
            {
                SchemaVersion = SchemaVersion,
                BuildId = BuildId,
                LeafName = LeafName,
                Exists = Exists,
                VolumeSerialNumber = VolumeSerialNumber,
                RootFileId = RootFileId
            };
            if (Entries != null)
            {
                foreach (PayloadTreeEntryCheckpoint entry in Entries)
                {
                    clone.Entries.Add(
                        entry == null ? null : entry.DeepClone());
                }
            }
            return clone;
        }

        internal static void RequireOwnedLeaf(
            string actual,
            string expected)
        {
            if (String.IsNullOrEmpty(actual) ||
                !String.Equals(
                    actual,
                    expected,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload workspace leaf name is not deterministic.");
            }
            PayloadContractValidation.RequirePayloadRelativePath(
                actual,
                "Payload workspace leaf name");
            if (actual.IndexOf('\\') >= 0)
            {
                throw new InvalidOperationException(
                    "Payload workspace leaf name is not a single segment.");
            }
        }

        internal static void ValidateObservedEntries(
            IList<PayloadTreeEntryCheckpoint> entries,
            string rootFileId)
        {
            string previous = null;
            var paths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var identities = new HashSet<string>(
                StringComparer.Ordinal);
            identities.Add(rootFileId);
            foreach (PayloadTreeEntryCheckpoint entry in entries)
            {
                if (entry == null)
                {
                    throw new InvalidOperationException(
                        "Payload partial tree contains a null entry.");
                }
                entry.Validate();
                if (!paths.Add(entry.RelativePath) ||
                    !identities.Add(entry.FileId))
                {
                    throw new InvalidOperationException(
                        "Payload partial tree aliases a path or identity.");
                }
                if (previous != null &&
                    StringComparer.Ordinal.Compare(
                        previous,
                        entry.RelativePath) >= 0)
                {
                    throw new InvalidOperationException(
                        "Payload partial-tree entries are not ordinal-sorted.");
                }
                previous = entry.RelativePath;
            }
            foreach (PayloadTreeEntryCheckpoint entry in entries)
            {
                int separator = entry.RelativePath.LastIndexOf('\\');
                while (separator > 0)
                {
                    string ancestor =
                        entry.RelativePath.Substring(0, separator);
                    bool foundDirectory = false;
                    foreach (PayloadTreeEntryCheckpoint candidate in entries)
                    {
                        if (String.Equals(
                                candidate.RelativePath,
                                ancestor,
                                StringComparison.OrdinalIgnoreCase) &&
                            candidate.IsDirectory)
                        {
                            foundDirectory = true;
                            break;
                        }
                    }
                    if (!foundDirectory)
                    {
                        throw new InvalidOperationException(
                            "Payload partial tree omits a verified parent directory.");
                    }
                    separator = ancestor.LastIndexOf('\\');
                }
            }
        }
    }

    [DataContract]
    internal sealed class PayloadCandidateBuildJournal
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal long Revision;

        [DataMember(Order = 3, IsRequired = true)]
        internal string BuildId;

        [DataMember(Order = 4, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 5, IsRequired = true)]
        internal string RecoveryAuthorityInvariantDigest;

        [DataMember(Order = 6, IsRequired = true)]
        internal string TargetManifestInvariantDigest;

        [DataMember(Order = 7, IsRequired = true)]
        internal string SourceReceiptInvariantDigest;

        [DataMember(Order = 8, IsRequired = true)]
        internal string NamespaceRootInvariantDigest;

        [DataMember(Order = 9, IsRequired = true)]
        internal long InitialCommittedRevision;

        [DataMember(Order = 10, IsRequired = true)]
        internal string InitialCommittedInvariantDigest;

        [DataMember(Order = 11, IsRequired = true)]
        internal string BuildLeafName;

        [DataMember(Order = 12, EmitDefaultValue = false)]
        internal PayloadBuildStepIntent ActiveIntent;

        [DataMember(Order = 13, IsRequired = true)]
        internal List<PayloadBuildEntryCheckpoint> Entries;

        [DataMember(Order = 14, IsRequired = true)]
        internal ulong RootVolumeSerialNumber;

        [DataMember(Order = 15, IsRequired = true)]
        internal string RootFileId;

        internal PayloadCandidateBuildJournal()
        {
            Entries = new List<PayloadBuildEntryCheckpoint>();
        }

        internal void Validate()
        {
            if (SchemaVersion != 1 ||
                Revision < 0 ||
                InitialCommittedRevision < 0 ||
                Entries == null ||
                Entries.Count == 0)
            {
                throw new InvalidOperationException(
                    "Payload candidate build journal is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                BuildId,
                "Payload candidate build ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload candidate build transaction ID");
            PayloadContractValidation.RequireSha256(
                RecoveryAuthorityInvariantDigest,
                "Payload build authority digest");
            PayloadContractValidation.RequireSha256(
                TargetManifestInvariantDigest,
                "Payload build manifest digest");
            PayloadContractValidation.RequireSha256(
                SourceReceiptInvariantDigest,
                "Payload build source receipt digest");
            PayloadContractValidation.RequireSha256(
                NamespaceRootInvariantDigest,
                "Payload build namespace-root digest");
            PayloadContractValidation.RequireSha256(
                InitialCommittedInvariantDigest,
                "Payload build initial committed digest");
            PayloadPartialTreeObservation.RequireOwnedLeaf(
                BuildLeafName,
                ".SBMS.build." + BuildId);
            bool rootEstablished =
                RootVolumeSerialNumber != 0 ||
                !String.IsNullOrEmpty(RootFileId);
            if (rootEstablished)
            {
                if (RootVolumeSerialNumber == 0)
                {
                    throw new InvalidOperationException(
                        "Payload build root volume identity is missing.");
                }
                PayloadContractValidation.RequireFileId(
                    RootFileId,
                    "Payload build root file ID");
            }

            string previous = null;
            bool incompleteSeen = false;
            bool progressSeen = false;
            var paths = new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < Entries.Count; ++index)
            {
                PayloadBuildEntryCheckpoint entry = Entries[index];
                if (entry == null || entry.Ordinal != index)
                {
                    throw new InvalidOperationException(
                        "Payload build entries do not have canonical ordinals.");
                }
                entry.Validate();
                if (paths.ContainsKey(entry.RelativePath))
                {
                    throw new InvalidOperationException(
                        "Payload build entries contain a path collision.");
                }
                paths.Add(entry.RelativePath, entry.IsDirectory);
                if (previous != null &&
                    StringComparer.Ordinal.Compare(
                        previous,
                        entry.RelativePath) >= 0)
                {
                    throw new InvalidOperationException(
                        "Payload build entries are not ordinal-sorted.");
                }
                previous = entry.RelativePath;
                if (incompleteSeen &&
                    entry.Phase != PayloadBuildEntryPhase.Pending)
                {
                    throw new InvalidOperationException(
                        "Payload build progress is not a verified prefix.");
                }
                if (entry.Phase != PayloadBuildEntryPhase.Verified)
                {
                    incompleteSeen = true;
                }
                if (entry.Phase != PayloadBuildEntryPhase.Pending)
                {
                    progressSeen = true;
                }
            }
            if (progressSeen && !rootEstablished)
            {
                throw new InvalidOperationException(
                    "Payload build progress exists before root attestation.");
            }
            foreach (PayloadBuildEntryCheckpoint entry in Entries)
            {
                int separator = entry.RelativePath.LastIndexOf('\\');
                while (separator > 0)
                {
                    string ancestor =
                        entry.RelativePath.Substring(0, separator);
                    bool isDirectory;
                    if (!paths.TryGetValue(ancestor, out isDirectory) ||
                        !isDirectory)
                    {
                        throw new InvalidOperationException(
                            "Payload build entries omit a parent directory.");
                    }
                    separator = ancestor.LastIndexOf('\\');
                }
            }
            if (ActiveIntent != null)
            {
                if (ActiveIntent.JournalRevision != Revision)
                {
                    throw new InvalidOperationException(
                        "Payload build intent is bound to another revision.");
                }
                ActiveIntent.Validate(Entries);
                if (ActiveIntent.Kind ==
                        PayloadBuildStepKind.SealCandidate &&
                    incompleteSeen)
                {
                    throw new InvalidOperationException(
                        "Payload candidate cannot seal before all entries verify.");
                }
            }
        }

        internal bool AllEntriesVerified
        {
            get
            {
                Validate();
                foreach (PayloadBuildEntryCheckpoint entry in Entries)
                {
                    if (entry.Phase != PayloadBuildEntryPhase.Verified)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                var fields = new List<string>
                {
                    SchemaVersion.ToString(
                        CultureInfo.InvariantCulture),
                    Revision.ToString(CultureInfo.InvariantCulture),
                    BuildId,
                    TransactionId,
                    RecoveryAuthorityInvariantDigest,
                    TargetManifestInvariantDigest,
                    SourceReceiptInvariantDigest,
                    NamespaceRootInvariantDigest,
                    InitialCommittedRevision.ToString(
                        CultureInfo.InvariantCulture),
                    InitialCommittedInvariantDigest,
                    BuildLeafName,
                    ActiveIntent == null
                        ? String.Empty
                        : ActiveIntent.InvariantDigest,
                    Entries.Count.ToString(
                        CultureInfo.InvariantCulture),
                    RootVolumeSerialNumber.ToString(
                        "x16",
                        CultureInfo.InvariantCulture),
                    RootFileId
                };
                foreach (PayloadBuildEntryCheckpoint entry in Entries)
                {
                    fields.Add(entry.InvariantDigest);
                }
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadCandidateBuildJournal.v1",
                    fields);
            }
        }

        internal PayloadCandidateBuildJournal DeepClone()
        {
            var clone = new PayloadCandidateBuildJournal
            {
                SchemaVersion = SchemaVersion,
                Revision = Revision,
                BuildId = BuildId,
                TransactionId = TransactionId,
                RecoveryAuthorityInvariantDigest =
                    RecoveryAuthorityInvariantDigest,
                TargetManifestInvariantDigest =
                    TargetManifestInvariantDigest,
                SourceReceiptInvariantDigest =
                    SourceReceiptInvariantDigest,
                NamespaceRootInvariantDigest =
                    NamespaceRootInvariantDigest,
                InitialCommittedRevision = InitialCommittedRevision,
                InitialCommittedInvariantDigest =
                    InitialCommittedInvariantDigest,
                BuildLeafName = BuildLeafName,
                ActiveIntent =
                    ActiveIntent == null
                        ? null
                        : ActiveIntent.DeepClone(),
                RootVolumeSerialNumber = RootVolumeSerialNumber,
                RootFileId = RootFileId
            };
            if (Entries != null)
            {
                foreach (PayloadBuildEntryCheckpoint entry in Entries)
                {
                    clone.Entries.Add(
                        entry == null ? null : entry.DeepClone());
                }
            }
            return clone;
        }
    }

    internal enum PayloadQuarantineSourceKind
    {
        PartialBuild,
        Candidate,
        Backup
    }

    internal enum PayloadQuarantineReason
    {
        UnarmedObject,
        IdentityMismatch,
        HashMismatch,
        UnexpectedTree,
        InterruptedBuild,
        Cleanup
    }

    [DataContract]
    internal sealed class PayloadQuarantineCheckpoint
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string QuarantineId;

        [DataMember(Order = 3, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 4, IsRequired = true)]
        internal string RecoveryAuthorityInvariantDigest;

        [DataMember(Order = 5, IsRequired = true)]
        internal string NamespaceRootInvariantDigest;

        [DataMember(Order = 6, IsRequired = true)]
        internal PayloadQuarantineSourceKind SourceKind;

        [DataMember(Order = 7, IsRequired = true)]
        internal string SourceBuildId;

        [DataMember(Order = 8, IsRequired = true)]
        internal string QuarantineLeafName;

        [DataMember(Order = 9, IsRequired = true)]
        internal ulong VolumeSerialNumber;

        [DataMember(Order = 10, IsRequired = true)]
        internal string RootFileId;

        [DataMember(Order = 11, IsRequired = true)]
        internal string PartialTreeInvariantDigest;

        [DataMember(Order = 12, IsRequired = true)]
        internal PayloadQuarantineReason Reason;

        [DataMember(Order = 13, IsRequired = true)]
        internal string SourceLeafName;

        internal void Validate()
        {
            if (SchemaVersion != 1 ||
                VolumeSerialNumber == 0 ||
                !Enum.IsDefined(
                    typeof(PayloadQuarantineSourceKind),
                    SourceKind) ||
                !Enum.IsDefined(
                    typeof(PayloadQuarantineReason),
                    Reason))
            {
                throw new InvalidOperationException(
                    "Payload quarantine checkpoint is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                QuarantineId,
                "Payload quarantine ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload quarantine transaction ID");
            PayloadContractValidation.RequireSha256(
                RecoveryAuthorityInvariantDigest,
                "Payload quarantine authority digest");
            PayloadContractValidation.RequireSha256(
                NamespaceRootInvariantDigest,
                "Payload quarantine namespace-root digest");
            PayloadContractValidation.RequireFileId(
                RootFileId,
                "Payload quarantine root file ID");
            PayloadContractValidation.RequireSha256(
                PartialTreeInvariantDigest,
                "Payload quarantine partial-tree digest");
            PayloadPartialTreeObservation.RequireOwnedLeaf(
                QuarantineLeafName,
                ".SBMS.quarantine." + QuarantineId);

            bool buildSource =
                SourceKind == PayloadQuarantineSourceKind.PartialBuild;
            if (buildSource)
            {
                PayloadContractValidation.RequireCanonicalTransactionId(
                    SourceBuildId,
                    "Payload quarantine source build ID");
                PayloadPartialTreeObservation.RequireOwnedLeaf(
                    SourceLeafName,
                    ".SBMS.build." + SourceBuildId);
            }
            else
            {
                if (!String.IsNullOrEmpty(SourceBuildId))
                {
                    throw new InvalidOperationException(
                        "Committed payload quarantine has a build ID.");
                }
                PayloadDirectorySlot sourceSlot =
                    SourceKind ==
                        PayloadQuarantineSourceKind.Candidate
                        ? PayloadDirectorySlot.Candidate
                        : PayloadDirectorySlot.Backup;
                PayloadPartialTreeObservation.RequireOwnedLeaf(
                    SourceLeafName,
                    PayloadNamespaceNames.ForSlot(
                        sourceSlot,
                        TransactionId));
            }
        }

        internal string NativeRootIdentity
        {
            get
            {
                Validate();
                return VolumeSerialNumber.ToString(
                    "x16",
                    CultureInfo.InvariantCulture) + ":" + RootFileId;
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadQuarantineCheckpoint.v1",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        QuarantineId,
                        TransactionId,
                        RecoveryAuthorityInvariantDigest,
                        NamespaceRootInvariantDigest,
                        SourceKind.ToString(),
                        SourceBuildId,
                        QuarantineLeafName,
                        VolumeSerialNumber.ToString(
                            "x16",
                            CultureInfo.InvariantCulture),
                        RootFileId,
                        PartialTreeInvariantDigest,
                        Reason.ToString(),
                        SourceLeafName
                    });
            }
        }

        internal PayloadQuarantineCheckpoint DeepClone()
        {
            return new PayloadQuarantineCheckpoint
            {
                SchemaVersion = SchemaVersion,
                QuarantineId = QuarantineId,
                TransactionId = TransactionId,
                RecoveryAuthorityInvariantDigest =
                    RecoveryAuthorityInvariantDigest,
                NamespaceRootInvariantDigest =
                    NamespaceRootInvariantDigest,
                SourceKind = SourceKind,
                SourceBuildId = SourceBuildId,
                QuarantineLeafName = QuarantineLeafName,
                VolumeSerialNumber = VolumeSerialNumber,
                RootFileId = RootFileId,
                PartialTreeInvariantDigest =
                    PartialTreeInvariantDigest,
                Reason = Reason,
                SourceLeafName = SourceLeafName
            };
        }
    }

    internal enum PayloadPurgePhase
    {
        Armed,
        ObservedAbsent
    }

    [DataContract]
    internal sealed class PayloadPurgeCheckpoint
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string PurgeId;

        [DataMember(Order = 3, IsRequired = true)]
        internal string QuarantineId;

        [DataMember(Order = 4, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 5, IsRequired = true)]
        internal string RecoveryAuthorityInvariantDigest;

        [DataMember(Order = 6, IsRequired = true)]
        internal string NamespaceRootInvariantDigest;

        [DataMember(Order = 7, IsRequired = true)]
        internal string QuarantineInvariantDigest;

        [DataMember(Order = 8, IsRequired = true)]
        internal ulong VolumeSerialNumber;

        [DataMember(Order = 9, IsRequired = true)]
        internal string RootFileId;

        [DataMember(Order = 10, IsRequired = true)]
        internal PayloadPurgePhase Phase;

        [DataMember(Order = 11, IsRequired = true)]
        internal string AbsenceObservationInvariantDigest;

        [DataMember(Order = 12, IsRequired = true)]
        internal long AbsenceObservedAtWorkspaceRevision;

        internal void Validate()
        {
            if (SchemaVersion != 1 ||
                VolumeSerialNumber == 0 ||
                !Enum.IsDefined(typeof(PayloadPurgePhase), Phase))
            {
                throw new InvalidOperationException(
                    "Payload purge checkpoint is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                PurgeId,
                "Payload purge ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                QuarantineId,
                "Payload purge quarantine ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload purge transaction ID");
            PayloadContractValidation.RequireSha256(
                RecoveryAuthorityInvariantDigest,
                "Payload purge authority digest");
            PayloadContractValidation.RequireSha256(
                NamespaceRootInvariantDigest,
                "Payload purge namespace-root digest");
            PayloadContractValidation.RequireSha256(
                QuarantineInvariantDigest,
                "Payload purge quarantine digest");
            PayloadContractValidation.RequireFileId(
                RootFileId,
                "Payload purge root file ID");
            if (Phase == PayloadPurgePhase.Armed)
            {
                if (!String.IsNullOrEmpty(
                        AbsenceObservationInvariantDigest) ||
                    AbsenceObservedAtWorkspaceRevision != -1)
                {
                    throw new InvalidOperationException(
                        "Armed payload purge already claims absence.");
                }
            }
            else
            {
                PayloadContractValidation.RequireSha256(
                    AbsenceObservationInvariantDigest,
                    "Payload purge absence observation digest");
                if (AbsenceObservedAtWorkspaceRevision < 0)
                {
                    throw new InvalidOperationException(
                        "Payload purge absence observation revision is missing.");
                }
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadPurgeCheckpoint.v1",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        PurgeId,
                        QuarantineId,
                        TransactionId,
                        RecoveryAuthorityInvariantDigest,
                        NamespaceRootInvariantDigest,
                        QuarantineInvariantDigest,
                        VolumeSerialNumber.ToString(
                            "x16",
                            CultureInfo.InvariantCulture),
                        RootFileId,
                        Phase.ToString(),
                        AbsenceObservationInvariantDigest,
                        AbsenceObservedAtWorkspaceRevision.ToString(
                            CultureInfo.InvariantCulture)
                    });
            }
        }

        internal PayloadPurgeCheckpoint DeepClone()
        {
            return new PayloadPurgeCheckpoint
            {
                SchemaVersion = SchemaVersion,
                PurgeId = PurgeId,
                QuarantineId = QuarantineId,
                TransactionId = TransactionId,
                RecoveryAuthorityInvariantDigest =
                    RecoveryAuthorityInvariantDigest,
                NamespaceRootInvariantDigest =
                    NamespaceRootInvariantDigest,
                QuarantineInvariantDigest =
                    QuarantineInvariantDigest,
                VolumeSerialNumber = VolumeSerialNumber,
                RootFileId = RootFileId,
                Phase = Phase,
                AbsenceObservationInvariantDigest =
                    AbsenceObservationInvariantDigest,
                AbsenceObservedAtWorkspaceRevision =
                    AbsenceObservedAtWorkspaceRevision
            };
        }
    }

    [DataContract]
    internal sealed class PayloadBuildWorkspaceCheckpoint
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal long Revision;

        [DataMember(Order = 3, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 4, IsRequired = true)]
        internal string RecoveryAuthorityInvariantDigest;

        [DataMember(Order = 5, IsRequired = true)]
        internal PayloadNamespaceRootIdentity NamespaceRoot;

        [DataMember(Order = 6, IsRequired = true)]
        internal PayloadNamespaceCheckpoint Committed;

        [DataMember(Order = 7, EmitDefaultValue = false)]
        internal PayloadCandidateBuildJournal ActiveBuild;

        [DataMember(Order = 8, EmitDefaultValue = false)]
        internal PayloadPartialTreeObservation ActivePartialTree;

        [DataMember(Order = 9, IsRequired = true)]
        internal List<PayloadQuarantineCheckpoint> Quarantines;

        [DataMember(Order = 10, IsRequired = true)]
        internal List<PayloadPurgeCheckpoint> PendingPurges;

        internal PayloadBuildWorkspaceCheckpoint()
        {
            Quarantines = new List<PayloadQuarantineCheckpoint>();
            PendingPurges = new List<PayloadPurgeCheckpoint>();
        }

        internal void Validate()
        {
            if (SchemaVersion != 1 ||
                Revision < 0 ||
                NamespaceRoot == null ||
                Committed == null ||
                Quarantines == null ||
                PendingPurges == null)
            {
                throw new InvalidOperationException(
                    "Payload build workspace checkpoint is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload workspace transaction ID");
            PayloadContractValidation.RequireSha256(
                RecoveryAuthorityInvariantDigest,
                "Payload workspace authority digest");
            NamespaceRoot.Validate();
            Committed.Validate();
            if (!String.Equals(
                    Committed.TransactionId,
                    TransactionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload workspace committed view has another transaction.");
            }
            bool active = ActiveBuild != null;
            if (active != (ActivePartialTree != null))
            {
                throw new InvalidOperationException(
                    "Payload workspace active build is only partly represented.");
            }

            var nativeIdentities = new HashSet<string>(
                StringComparer.Ordinal);
            AddNativeIdentity(
                NamespaceRoot.VolumeSerialNumber,
                NamespaceRoot.RootFileId,
                nativeIdentities);
            AddCommittedIdentities(Committed, nativeIdentities);
            if (active)
            {
                ActiveBuild.Validate();
                ActivePartialTree.Validate();
                if (!String.Equals(
                        ActiveBuild.TransactionId,
                        TransactionId,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        ActiveBuild.RecoveryAuthorityInvariantDigest,
                        RecoveryAuthorityInvariantDigest,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        ActiveBuild.NamespaceRootInvariantDigest,
                        NamespaceRoot.InvariantDigest,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        ActiveBuild.BuildId,
                        ActivePartialTree.BuildId,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        ActiveBuild.BuildLeafName,
                        ActivePartialTree.LeafName,
                        StringComparison.Ordinal) ||
                    ActiveBuild.InitialCommittedRevision !=
                        Committed.Revision ||
                    !String.Equals(
                        ActiveBuild.InitialCommittedInvariantDigest,
                        Committed.InvariantDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Payload workspace active build bindings disagree.");
                }
                ValidateBuildObservation(
                    ActiveBuild,
                    ActivePartialTree,
                    NamespaceRoot);
                if (ActivePartialTree.Exists)
                {
                    AddNativeIdentity(
                        ActivePartialTree.VolumeSerialNumber,
                        ActivePartialTree.RootFileId,
                        nativeIdentities);
                    foreach (PayloadTreeEntryCheckpoint entry in
                        ActivePartialTree.Entries)
                    {
                        AddNativeIdentity(
                            ActivePartialTree.VolumeSerialNumber,
                            entry.FileId,
                            nativeIdentities);
                    }
                }
            }

            var quarantineIds = new Dictionary<
                string,
                PayloadQuarantineCheckpoint>(
                StringComparer.Ordinal);
            var quarantineLeaves = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            string previous = null;
            foreach (PayloadQuarantineCheckpoint quarantine in Quarantines)
            {
                if (quarantine == null)
                {
                    throw new InvalidOperationException(
                        "Payload workspace contains a null quarantine.");
                }
                quarantine.Validate();
                if (quarantine.VolumeSerialNumber !=
                    NamespaceRoot.VolumeSerialNumber)
                {
                    throw new InvalidOperationException(
                        "Payload quarantine is outside the namespace volume.");
                }
                if (active &&
                    quarantine.SourceKind ==
                        PayloadQuarantineSourceKind.PartialBuild &&
                    String.Equals(
                        quarantine.SourceBuildId,
                        ActiveBuild.BuildId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Payload workspace owns one build as active and quarantined.");
                }
                if (!String.Equals(
                        quarantine.TransactionId,
                        TransactionId,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        quarantine.RecoveryAuthorityInvariantDigest,
                        RecoveryAuthorityInvariantDigest,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        quarantine.NamespaceRootInvariantDigest,
                        NamespaceRoot.InvariantDigest,
                        StringComparison.Ordinal) ||
                    quarantineIds.ContainsKey(
                        quarantine.QuarantineId) ||
                    !quarantineLeaves.Add(
                        quarantine.QuarantineLeafName) ||
                    (previous != null &&
                        StringComparer.Ordinal.Compare(
                            previous,
                            quarantine.QuarantineId) >= 0))
                {
                    throw new InvalidOperationException(
                        "Payload workspace quarantine bindings are invalid.");
                }
                quarantineIds.Add(
                    quarantine.QuarantineId,
                    quarantine);
                previous = quarantine.QuarantineId;
                AddNativeIdentity(
                    quarantine.VolumeSerialNumber,
                    quarantine.RootFileId,
                    nativeIdentities);
            }

            var purgeIds = new HashSet<string>(
                StringComparer.Ordinal);
            var purgedQuarantines = new HashSet<string>(
                StringComparer.Ordinal);
            previous = null;
            foreach (PayloadPurgeCheckpoint purge in PendingPurges)
            {
                if (purge == null)
                {
                    throw new InvalidOperationException(
                        "Payload workspace contains a null purge.");
                }
                purge.Validate();
                PayloadQuarantineCheckpoint quarantine;
                if (!String.Equals(
                        purge.TransactionId,
                        TransactionId,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        purge.RecoveryAuthorityInvariantDigest,
                        RecoveryAuthorityInvariantDigest,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        purge.NamespaceRootInvariantDigest,
                        NamespaceRoot.InvariantDigest,
                        StringComparison.Ordinal) ||
                    !purgeIds.Add(purge.PurgeId) ||
                    !purgedQuarantines.Add(purge.QuarantineId) ||
                    !quarantineIds.TryGetValue(
                        purge.QuarantineId,
                        out quarantine) ||
                    !String.Equals(
                        purge.QuarantineInvariantDigest,
                        quarantine.InvariantDigest,
                        StringComparison.Ordinal) ||
                    purge.VolumeSerialNumber !=
                        quarantine.VolumeSerialNumber ||
                    !String.Equals(
                        purge.RootFileId,
                        quarantine.RootFileId,
                        StringComparison.Ordinal) ||
                    (previous != null &&
                        StringComparer.Ordinal.Compare(
                            previous,
                            purge.PurgeId) >= 0))
                {
                    throw new InvalidOperationException(
                        "Payload workspace purge bindings are invalid.");
                }
                previous = purge.PurgeId;
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                var fields = new List<string>
                {
                    SchemaVersion.ToString(
                        CultureInfo.InvariantCulture),
                    Revision.ToString(CultureInfo.InvariantCulture),
                    TransactionId,
                    RecoveryAuthorityInvariantDigest,
                    NamespaceRoot.InvariantDigest,
                    Committed.InvariantDigest,
                    ActiveBuild == null
                        ? String.Empty
                        : ActiveBuild.InvariantDigest,
                    ActivePartialTree == null
                        ? String.Empty
                        : ActivePartialTree.InvariantDigest,
                    Quarantines.Count.ToString(
                        CultureInfo.InvariantCulture)
                };
                foreach (PayloadQuarantineCheckpoint quarantine in
                    Quarantines)
                {
                    fields.Add(quarantine.InvariantDigest);
                }
                fields.Add(PendingPurges.Count.ToString(
                    CultureInfo.InvariantCulture));
                foreach (PayloadPurgeCheckpoint purge in PendingPurges)
                {
                    fields.Add(purge.InvariantDigest);
                }
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadBuildWorkspaceCheckpoint.v1",
                    fields);
            }
        }

        internal PayloadBuildWorkspaceCheckpoint DeepClone()
        {
            var clone = new PayloadBuildWorkspaceCheckpoint
            {
                SchemaVersion = SchemaVersion,
                Revision = Revision,
                TransactionId = TransactionId,
                RecoveryAuthorityInvariantDigest =
                    RecoveryAuthorityInvariantDigest,
                NamespaceRoot =
                    NamespaceRoot == null
                        ? null
                        : NamespaceRoot.DeepClone(),
                Committed =
                    Committed == null
                        ? null
                        : Committed.DeepClone(),
                ActiveBuild =
                    ActiveBuild == null
                        ? null
                        : ActiveBuild.DeepClone(),
                ActivePartialTree =
                    ActivePartialTree == null
                        ? null
                        : ActivePartialTree.DeepClone()
            };
            if (Quarantines != null)
            {
                foreach (PayloadQuarantineCheckpoint quarantine in
                    Quarantines)
                {
                    clone.Quarantines.Add(
                        quarantine == null
                            ? null
                            : quarantine.DeepClone());
                }
            }
            if (PendingPurges != null)
            {
                foreach (PayloadPurgeCheckpoint purge in PendingPurges)
                {
                    clone.PendingPurges.Add(
                        purge == null ? null : purge.DeepClone());
                }
            }
            return clone;
        }

        private static void AddCommittedIdentities(
            PayloadNamespaceCheckpoint committed,
            HashSet<string> identities)
        {
            AddDirectoryIdentities(committed.Current, identities);
            AddDirectoryIdentities(committed.Candidate, identities);
            AddDirectoryIdentities(committed.Backup, identities);
        }

        private static void ValidateBuildObservation(
            PayloadCandidateBuildJournal journal,
            PayloadPartialTreeObservation observation,
            PayloadNamespaceRootIdentity namespaceRoot)
        {
            bool rootEstablished =
                journal.RootVolumeSerialNumber != 0;
            PayloadBuildStepKind? intentKind =
                journal.ActiveIntent == null
                    ? (PayloadBuildStepKind?)null
                    : journal.ActiveIntent.Kind;
            bool observationChangedAfterIntent =
                journal.ActiveIntent != null &&
                !String.Equals(
                    journal.ActiveIntent.
                        ObservedPartialTreeInvariantDigest,
                    observation.InvariantDigest,
                    StringComparison.Ordinal);

            if (rootEstablished)
            {
                if (!observation.Exists ||
                    journal.RootVolumeSerialNumber !=
                        namespaceRoot.VolumeSerialNumber ||
                    observation.VolumeSerialNumber !=
                        namespaceRoot.VolumeSerialNumber ||
                    !String.Equals(
                        journal.RootFileId,
                        observation.RootFileId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Payload build root observation disagrees with its journal.");
                }
            }
            else if (observation.Exists)
            {
                if (intentKind != PayloadBuildStepKind.CreateRoot ||
                    observation.Entries.Count != 0 ||
                    observation.VolumeSerialNumber !=
                        namespaceRoot.VolumeSerialNumber)
                {
                    throw new InvalidOperationException(
                        "Unattested payload build root was not armed.");
                }
            }

            if (observationChangedAfterIntent &&
                intentKind != PayloadBuildStepKind.CreateRoot &&
                intentKind != PayloadBuildStepKind.CreateEntry &&
                intentKind != PayloadBuildStepKind.RewriteFileExact)
            {
                throw new InvalidOperationException(
                    "Payload build observation changed outside an armed physical step.");
            }

            var observed = new Dictionary<
                string,
                PayloadTreeEntryCheckpoint>(
                StringComparer.OrdinalIgnoreCase);
            foreach (PayloadTreeEntryCheckpoint entry in observation.Entries)
            {
                observed.Add(entry.RelativePath, entry);
            }
            var expectedPaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (PayloadBuildEntryCheckpoint expected in journal.Entries)
            {
                expectedPaths.Add(expected.RelativePath);
            }
            foreach (string observedPath in observed.Keys)
            {
                if (!expectedPaths.Contains(observedPath))
                {
                    throw new InvalidOperationException(
                        "Payload build observation contains an unknown entry.");
                }
            }

            foreach (PayloadBuildEntryCheckpoint expected in journal.Entries)
            {
                PayloadTreeEntryCheckpoint actual;
                bool exists = observed.TryGetValue(
                    expected.RelativePath,
                    out actual);
                bool armedCreate =
                    intentKind == PayloadBuildStepKind.CreateEntry &&
                    journal.ActiveIntent.EntryOrdinal == expected.Ordinal;
                if (expected.Phase == PayloadBuildEntryPhase.Pending)
                {
                    if (exists && !armedCreate)
                    {
                        throw new InvalidOperationException(
                            "Payload build contains an unarmed extra entry.");
                    }
                    if (exists)
                    {
                        RequireExpectedEntryShape(expected, actual, false);
                    }
                    continue;
                }

                if (!exists)
                {
                    throw new InvalidOperationException(
                        "Payload build observation omits journaled progress.");
                }
                RequireExpectedEntryShape(expected, actual, true);
                if (!String.Equals(
                        expected.FileId,
                        actual.FileId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Payload build entry identity changed after attestation.");
                }
                if (expected.Phase ==
                        PayloadBuildEntryPhase.Reopened &&
                    actual.Length != expected.ExpectedLength)
                {
                    throw new InvalidOperationException(
                        "Reopened payload build entry length changed.");
                }
                if (expected.Phase ==
                        PayloadBuildEntryPhase.Verified &&
                    (actual.Length != expected.ExpectedLength ||
                     (!expected.IsDirectory &&
                        !String.Equals(
                            actual.Sha256,
                            expected.ExpectedSha256,
                            StringComparison.Ordinal))))
                {
                    throw new InvalidOperationException(
                        "Verified payload build tree changed after proof.");
                }
            }

            if (intentKind == PayloadBuildStepKind.SealCandidate)
            {
                if (!journal.AllEntriesVerified ||
                    !observation.Exists ||
                    observationChangedAfterIntent ||
                    observed.Count != journal.Entries.Count)
                {
                    throw new InvalidOperationException(
                        "Payload candidate seal is not bound to the verified tree.");
                }
            }
        }

        private static void RequireExpectedEntryShape(
            PayloadBuildEntryCheckpoint expected,
            PayloadTreeEntryCheckpoint actual,
            bool requireKnownIdentity)
        {
            if (actual.IsDirectory != expected.IsDirectory)
            {
                throw new InvalidOperationException(
                    "Payload build entry type disagrees with its journal.");
            }
            if (requireKnownIdentity)
            {
                PayloadContractValidation.RequireFileId(
                    actual.FileId,
                    "Observed payload build entry file ID");
            }
        }

        private static void AddDirectoryIdentities(
            PayloadDirectoryCheckpoint directory,
            HashSet<string> identities)
        {
            if (directory == null)
            {
                return;
            }
            AddNativeIdentity(
                directory.VolumeSerialNumber,
                directory.FileId,
                identities);
            foreach (PayloadTreeEntryCheckpoint entry in directory.Entries)
            {
                AddNativeIdentity(
                    directory.VolumeSerialNumber,
                    entry.FileId,
                    identities);
            }
        }

        private static void AddNativeIdentity(
            ulong volume,
            string fileId,
            HashSet<string> identities)
        {
            string identity = volume.ToString(
                "x16",
                CultureInfo.InvariantCulture) + ":" + fileId;
            if (!identities.Add(identity))
            {
                throw new InvalidOperationException(
                    "Payload workspace aliases a native identity.");
            }
        }
    }

    [DataContract]
    internal sealed class PayloadWorkspaceCasToken
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 3, IsRequired = true)]
        internal long Revision;

        [DataMember(Order = 4, IsRequired = true)]
        internal string WorkspaceInvariantDigest;

        internal void Validate()
        {
            if (SchemaVersion != 1 || Revision < 0)
            {
                throw new InvalidOperationException(
                    "Payload workspace CAS token is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload workspace CAS transaction ID");
            PayloadContractValidation.RequireSha256(
                WorkspaceInvariantDigest,
                "Payload workspace CAS digest");
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadWorkspaceCasToken.v1",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        TransactionId,
                        Revision.ToString(
                            CultureInfo.InvariantCulture),
                        WorkspaceInvariantDigest
                    });
            }
        }

        internal PayloadWorkspaceCasToken DeepClone()
        {
            return new PayloadWorkspaceCasToken
            {
                SchemaVersion = SchemaVersion,
                TransactionId = TransactionId,
                Revision = Revision,
                WorkspaceInvariantDigest = WorkspaceInvariantDigest
            };
        }
    }

    [DataContract]
    internal sealed class PayloadQuarantineAbsenceObservation
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 3, IsRequired = true)]
        internal string RecoveryAuthorityInvariantDigest;

        [DataMember(Order = 4, IsRequired = true)]
        internal string NamespaceRootInvariantDigest;

        [DataMember(Order = 5, IsRequired = true)]
        internal string QuarantineId;

        [DataMember(Order = 6, IsRequired = true)]
        internal string QuarantineLeafName;

        [DataMember(Order = 7, IsRequired = true)]
        internal ulong VolumeSerialNumber;

        [DataMember(Order = 8, IsRequired = true)]
        internal string RootFileId;

        [DataMember(Order = 9, IsRequired = true)]
        internal long ObservedAtWorkspaceRevision;

        [DataMember(Order = 10, IsRequired = true)]
        internal bool Exists;

        internal void Validate()
        {
            if (SchemaVersion != 1 ||
                VolumeSerialNumber == 0 ||
                ObservedAtWorkspaceRevision < 0 ||
                Exists)
            {
                throw new InvalidOperationException(
                    "Payload quarantine absence observation is invalid.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload quarantine absence transaction ID");
            PayloadContractValidation.RequireSha256(
                RecoveryAuthorityInvariantDigest,
                "Payload quarantine absence authority digest");
            PayloadContractValidation.RequireSha256(
                NamespaceRootInvariantDigest,
                "Payload quarantine absence namespace-root digest");
            PayloadContractValidation.RequireCanonicalTransactionId(
                QuarantineId,
                "Payload quarantine absence quarantine ID");
            PayloadPartialTreeObservation.RequireOwnedLeaf(
                QuarantineLeafName,
                ".SBMS.quarantine." + QuarantineId);
            PayloadContractValidation.RequireFileId(
                RootFileId,
                "Payload quarantine absence root file ID");
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadQuarantineAbsenceObservation.v1",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        TransactionId,
                        RecoveryAuthorityInvariantDigest,
                        NamespaceRootInvariantDigest,
                        QuarantineId,
                        QuarantineLeafName,
                        VolumeSerialNumber.ToString(
                            "x16",
                            CultureInfo.InvariantCulture),
                        RootFileId,
                        ObservedAtWorkspaceRevision.ToString(
                            CultureInfo.InvariantCulture),
                        Exists.ToString(CultureInfo.InvariantCulture)
                    });
            }
        }

        internal PayloadQuarantineAbsenceObservation DeepClone()
        {
            return new PayloadQuarantineAbsenceObservation
            {
                SchemaVersion = SchemaVersion,
                TransactionId = TransactionId,
                RecoveryAuthorityInvariantDigest =
                    RecoveryAuthorityInvariantDigest,
                NamespaceRootInvariantDigest =
                    NamespaceRootInvariantDigest,
                QuarantineId = QuarantineId,
                QuarantineLeafName = QuarantineLeafName,
                VolumeSerialNumber = VolumeSerialNumber,
                RootFileId = RootFileId,
                ObservedAtWorkspaceRevision =
                    ObservedAtWorkspaceRevision,
                Exists = Exists
            };
        }
    }

    internal sealed class PayloadQuarantineReceipt
    {
        private readonly PayloadRecoveryAuthority authority;
        internal readonly PayloadBuildWorkspaceState Before;
        internal readonly PayloadBuildWorkspaceState After;
        internal readonly string QuarantineId;

        internal PayloadQuarantineReceipt(
            PayloadRecoveryAuthority trustedAuthority,
            PayloadBuildWorkspaceState before,
            PayloadBuildWorkspaceState after,
            string quarantineId)
        {
            if (trustedAuthority == null ||
                before == null ||
                after == null)
            {
                throw new ArgumentNullException(
                    trustedAuthority == null
                        ? "trustedAuthority"
                        : (before == null ? "before" : "after"));
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                quarantineId,
                "Payload quarantine receipt ID");
            ValidateQuarantineTransition(
                trustedAuthority,
                before.Checkpoint,
                after.Checkpoint,
                quarantineId);
            authority = trustedAuthority.DeepClone();
            Before = before;
            After = after;
            QuarantineId = quarantineId;
        }

        internal PayloadRecoveryAuthority Authority
        {
            get { return authority.DeepClone(); }
        }

        private static void ValidateQuarantineTransition(
            PayloadRecoveryAuthority authority,
            PayloadBuildWorkspaceCheckpoint before,
            PayloadBuildWorkspaceCheckpoint after,
            string quarantineId)
        {
            authority.Validate();
            RequireCommonTransitionBindings(
                authority,
                before,
                after);
            if (before.ActiveBuild == null ||
                before.ActivePartialTree == null ||
                !before.ActivePartialTree.Exists ||
                before.ActiveBuild.ActiveIntent == null ||
                before.ActiveBuild.ActiveIntent.Kind !=
                    PayloadBuildStepKind.QuarantineBuild ||
                before.ActiveBuild.ActiveIntent.EntryOrdinal != -1 ||
                !String.Equals(
                    before.ActiveBuild.ActiveIntent.
                        ObservedPartialTreeInvariantDigest,
                    before.ActivePartialTree.InvariantDigest,
                    StringComparison.Ordinal) ||
                after.ActiveBuild != null ||
                after.ActivePartialTree != null ||
                !String.Equals(
                    before.Committed.InvariantDigest,
                    after.Committed.InvariantDigest,
                    StringComparison.Ordinal) ||
                !SamePurgeSet(
                    before.PendingPurges,
                    after.PendingPurges))
            {
                throw new InvalidOperationException(
                    "Payload quarantine receipt changed unrelated workspace state.");
            }

            PayloadQuarantineCheckpoint added =
                RequireOneAddedQuarantine(
                    before.Quarantines,
                    after.Quarantines,
                    quarantineId);
            if (added.SourceKind !=
                    PayloadQuarantineSourceKind.PartialBuild ||
                !String.Equals(
                    added.SourceBuildId,
                    before.ActiveBuild.BuildId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    added.SourceLeafName,
                    before.ActivePartialTree.LeafName,
                    StringComparison.Ordinal) ||
                added.VolumeSerialNumber !=
                    before.ActivePartialTree.VolumeSerialNumber ||
                !String.Equals(
                    added.RootFileId,
                    before.ActivePartialTree.RootFileId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    added.PartialTreeInvariantDigest,
                    before.ActivePartialTree.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload quarantine receipt did not preserve source identity.");
            }
        }

        internal static void RequireCommonTransitionBindings(
            PayloadRecoveryAuthority authority,
            PayloadBuildWorkspaceCheckpoint before,
            PayloadBuildWorkspaceCheckpoint after)
        {
            before.Validate();
            after.Validate();
            if (after.Revision != before.Revision + 1 ||
                !String.Equals(
                    authority.TransactionId,
                    before.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    before.TransactionId,
                    after.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    authority.InvariantDigest,
                    before.RecoveryAuthorityInvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    before.RecoveryAuthorityInvariantDigest,
                    after.RecoveryAuthorityInvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    before.NamespaceRoot.InvariantDigest,
                    after.NamespaceRoot.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload workspace transition bindings are stale.");
            }
        }

        internal static PayloadQuarantineCheckpoint
            RequireOneAddedQuarantine(
                IList<PayloadQuarantineCheckpoint> before,
                IList<PayloadQuarantineCheckpoint> after,
                string quarantineId)
        {
            if (after.Count != before.Count + 1)
            {
                throw new InvalidOperationException(
                    "Payload quarantine transition did not add one entry.");
            }
            PayloadQuarantineCheckpoint added = null;
            var prior = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (PayloadQuarantineCheckpoint item in before)
            {
                prior.Add(item.QuarantineId, item.InvariantDigest);
            }
            foreach (PayloadQuarantineCheckpoint item in after)
            {
                string digest;
                if (prior.TryGetValue(item.QuarantineId, out digest))
                {
                    if (!String.Equals(
                            digest,
                            item.InvariantDigest,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Payload quarantine transition changed an existing entry.");
                    }
                    prior.Remove(item.QuarantineId);
                }
                else if (added == null &&
                    String.Equals(
                        item.QuarantineId,
                        quarantineId,
                        StringComparison.Ordinal))
                {
                    added = item;
                }
                else
                {
                    throw new InvalidOperationException(
                        "Payload quarantine transition added the wrong entry.");
                }
            }
            if (prior.Count != 0 || added == null)
            {
                throw new InvalidOperationException(
                    "Payload quarantine transition lost prior state.");
            }
            return added;
        }

        internal static bool SameQuarantineSet(
            IList<PayloadQuarantineCheckpoint> first,
            IList<PayloadQuarantineCheckpoint> second)
        {
            return SameDigestSet(
                QuarantineDigests(first),
                QuarantineDigests(second));
        }

        internal static bool SamePurgeSet(
            IList<PayloadPurgeCheckpoint> first,
            IList<PayloadPurgeCheckpoint> second)
        {
            return SameDigestSet(
                PurgeDigests(first),
                PurgeDigests(second));
        }

        private static Dictionary<string, string> QuarantineDigests(
            IList<PayloadQuarantineCheckpoint> source)
        {
            var result = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (PayloadQuarantineCheckpoint item in source)
            {
                result.Add(item.QuarantineId, item.InvariantDigest);
            }
            return result;
        }

        private static Dictionary<string, string> PurgeDigests(
            IList<PayloadPurgeCheckpoint> source)
        {
            var result = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (PayloadPurgeCheckpoint item in source)
            {
                result.Add(item.PurgeId, item.InvariantDigest);
            }
            return result;
        }

        private static bool SameDigestSet(
            Dictionary<string, string> first,
            Dictionary<string, string> second)
        {
            if (first.Count != second.Count)
            {
                return false;
            }
            foreach (KeyValuePair<string, string> item in first)
            {
                string digest;
                if (!second.TryGetValue(item.Key, out digest) ||
                    !String.Equals(
                        item.Value,
                        digest,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }
    }

    internal enum PayloadPurgeTransitionKind
    {
        Arm,
        ObserveAbsent,
        Complete
    }

    internal sealed class PayloadPurgeReceipt
    {
        private readonly PayloadRecoveryAuthority authority;
        private readonly PayloadQuarantineAbsenceObservation
            absenceObservation;
        internal readonly PayloadBuildWorkspaceState Before;
        internal readonly PayloadBuildWorkspaceState After;
        internal readonly PayloadPurgeTransitionKind Kind;
        internal readonly string PurgeId;
        internal readonly string QuarantineId;
        internal readonly bool Complete;

        internal PayloadPurgeReceipt(
            PayloadRecoveryAuthority trustedAuthority,
            PayloadBuildWorkspaceState before,
            PayloadBuildWorkspaceState after,
            PayloadPurgeTransitionKind kind,
            string purgeId,
            string quarantineId,
            PayloadQuarantineAbsenceObservation trustedAbsenceObservation)
        {
            if (trustedAuthority == null ||
                before == null ||
                after == null)
            {
                throw new ArgumentNullException(
                    trustedAuthority == null
                        ? "trustedAuthority"
                        : (before == null ? "before" : "after"));
            }
            if (!Enum.IsDefined(
                    typeof(PayloadPurgeTransitionKind),
                    kind))
            {
                throw new InvalidOperationException(
                    "Payload purge transition kind is invalid.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                purgeId,
                "Payload purge receipt ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                quarantineId,
                "Payload purge receipt quarantine ID");
            ValidatePurgeTransition(
                trustedAuthority,
                before.Checkpoint,
                after.Checkpoint,
                kind,
                purgeId,
                quarantineId,
                trustedAbsenceObservation);
            authority = trustedAuthority.DeepClone();
            absenceObservation =
                trustedAbsenceObservation == null
                    ? null
                    : trustedAbsenceObservation.DeepClone();
            Before = before;
            After = after;
            Kind = kind;
            PurgeId = purgeId;
            QuarantineId = quarantineId;
            Complete = kind == PayloadPurgeTransitionKind.Complete;
        }

        internal PayloadRecoveryAuthority Authority
        {
            get { return authority.DeepClone(); }
        }

        internal PayloadQuarantineAbsenceObservation AbsenceObservation
        {
            get
            {
                return absenceObservation == null
                    ? null
                    : absenceObservation.DeepClone();
            }
        }

        private static void ValidatePurgeTransition(
            PayloadRecoveryAuthority authority,
            PayloadBuildWorkspaceCheckpoint before,
            PayloadBuildWorkspaceCheckpoint after,
            PayloadPurgeTransitionKind kind,
            string purgeId,
            string quarantineId,
            PayloadQuarantineAbsenceObservation observation)
        {
            authority.Validate();
            PayloadQuarantineReceipt.RequireCommonTransitionBindings(
                authority,
                before,
                after);
            if (!String.Equals(
                    before.Committed.InvariantDigest,
                    after.Committed.InvariantDigest,
                    StringComparison.Ordinal) ||
                !SameActiveBuild(before, after))
            {
                throw new InvalidOperationException(
                    "Payload purge receipt changed committed or build state.");
            }
            PayloadQuarantineCheckpoint quarantine =
                FindQuarantine(before.Quarantines, quarantineId);
            if (kind == PayloadPurgeTransitionKind.Arm)
            {
                if (observation != null ||
                    !PayloadQuarantineReceipt.SameQuarantineSet(
                        before.Quarantines,
                        after.Quarantines))
                {
                    throw new InvalidOperationException(
                        "Payload purge arm changed quarantine state.");
                }
                PayloadPurgeCheckpoint added = RequireOneAddedPurge(
                    before.PendingPurges,
                    after.PendingPurges,
                    purgeId);
                RequirePurgeBinding(
                    added,
                    quarantine,
                    PayloadPurgePhase.Armed);
                return;
            }

            PayloadPurgeCheckpoint prior =
                FindPurge(before.PendingPurges, purgeId);
            RequirePurgeBinding(
                prior,
                quarantine,
                kind == PayloadPurgeTransitionKind.ObserveAbsent
                    ? PayloadPurgePhase.Armed
                    : PayloadPurgePhase.ObservedAbsent);
            if (kind == PayloadPurgeTransitionKind.ObserveAbsent)
            {
                if (observation == null)
                {
                    throw new InvalidOperationException(
                        "Payload purge absence transition lacks fresh evidence.");
                }
                observation.Validate();
                RequireAbsenceBinding(
                    observation,
                    before,
                    quarantine);
                PayloadPurgeCheckpoint changed =
                    FindPurge(after.PendingPurges, purgeId);
                if (after.PendingPurges.Count !=
                        before.PendingPurges.Count ||
                    !PayloadQuarantineReceipt.SameQuarantineSet(
                        before.Quarantines,
                        after.Quarantines) ||
                    changed.Phase !=
                        PayloadPurgePhase.ObservedAbsent ||
                    !String.Equals(
                        changed.AbsenceObservationInvariantDigest,
                        observation.InvariantDigest,
                        StringComparison.Ordinal) ||
                    changed.AbsenceObservedAtWorkspaceRevision !=
                        before.Revision ||
                    !SamePurgesExcept(
                        before.PendingPurges,
                        after.PendingPurges,
                        purgeId))
                {
                    throw new InvalidOperationException(
                        "Payload purge absence transition is not exact.");
                }
                return;
            }

            if (observation != null ||
                after.Quarantines.Count != before.Quarantines.Count - 1 ||
                after.PendingPurges.Count !=
                    before.PendingPurges.Count - 1 ||
                ContainsQuarantine(
                    after.Quarantines,
                    quarantineId) ||
                ContainsPurge(after.PendingPurges, purgeId) ||
                !SameQuarantinesExcept(
                    before.Quarantines,
                    after.Quarantines,
                    quarantineId) ||
                !SamePurgesExcept(
                    before.PendingPurges,
                    after.PendingPurges,
                    purgeId))
            {
                throw new InvalidOperationException(
                    "Payload purge completion removed the wrong state.");
            }
        }

        private static bool SameActiveBuild(
            PayloadBuildWorkspaceCheckpoint first,
            PayloadBuildWorkspaceCheckpoint second)
        {
            string firstBuild =
                first.ActiveBuild == null
                    ? String.Empty
                    : first.ActiveBuild.InvariantDigest;
            string secondBuild =
                second.ActiveBuild == null
                    ? String.Empty
                    : second.ActiveBuild.InvariantDigest;
            string firstTree =
                first.ActivePartialTree == null
                    ? String.Empty
                    : first.ActivePartialTree.InvariantDigest;
            string secondTree =
                second.ActivePartialTree == null
                    ? String.Empty
                    : second.ActivePartialTree.InvariantDigest;
            return String.Equals(
                    firstBuild,
                    secondBuild,
                    StringComparison.Ordinal) &&
                String.Equals(
                    firstTree,
                    secondTree,
                    StringComparison.Ordinal);
        }

        private static void RequireAbsenceBinding(
            PayloadQuarantineAbsenceObservation observation,
            PayloadBuildWorkspaceCheckpoint before,
            PayloadQuarantineCheckpoint quarantine)
        {
            if (!String.Equals(
                    observation.TransactionId,
                    before.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    observation.RecoveryAuthorityInvariantDigest,
                    before.RecoveryAuthorityInvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    observation.NamespaceRootInvariantDigest,
                    before.NamespaceRoot.InvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    observation.QuarantineId,
                    quarantine.QuarantineId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    observation.QuarantineLeafName,
                    quarantine.QuarantineLeafName,
                    StringComparison.Ordinal) ||
                observation.VolumeSerialNumber !=
                    quarantine.VolumeSerialNumber ||
                !String.Equals(
                    observation.RootFileId,
                    quarantine.RootFileId,
                    StringComparison.Ordinal) ||
                observation.ObservedAtWorkspaceRevision !=
                    before.Revision)
            {
                throw new InvalidOperationException(
                    "Payload purge absence evidence is stale or substituted.");
            }
        }

        private static void RequirePurgeBinding(
            PayloadPurgeCheckpoint purge,
            PayloadQuarantineCheckpoint quarantine,
            PayloadPurgePhase phase)
        {
            if (purge.Phase != phase ||
                !String.Equals(
                    purge.QuarantineId,
                    quarantine.QuarantineId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    purge.QuarantineInvariantDigest,
                    quarantine.InvariantDigest,
                    StringComparison.Ordinal) ||
                purge.VolumeSerialNumber !=
                    quarantine.VolumeSerialNumber ||
                !String.Equals(
                    purge.RootFileId,
                    quarantine.RootFileId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload purge is bound to another quarantine.");
            }
        }

        private static PayloadQuarantineCheckpoint FindQuarantine(
            IList<PayloadQuarantineCheckpoint> source,
            string quarantineId)
        {
            foreach (PayloadQuarantineCheckpoint item in source)
            {
                if (String.Equals(
                        item.QuarantineId,
                        quarantineId,
                        StringComparison.Ordinal))
                {
                    return item;
                }
            }
            throw new InvalidOperationException(
                "Payload purge quarantine is missing.");
        }

        private static PayloadPurgeCheckpoint FindPurge(
            IList<PayloadPurgeCheckpoint> source,
            string purgeId)
        {
            foreach (PayloadPurgeCheckpoint item in source)
            {
                if (String.Equals(
                        item.PurgeId,
                        purgeId,
                        StringComparison.Ordinal))
                {
                    return item;
                }
            }
            throw new InvalidOperationException(
                "Payload purge checkpoint is missing.");
        }

        private static PayloadPurgeCheckpoint RequireOneAddedPurge(
            IList<PayloadPurgeCheckpoint> before,
            IList<PayloadPurgeCheckpoint> after,
            string purgeId)
        {
            if (after.Count != before.Count + 1)
            {
                throw new InvalidOperationException(
                    "Payload purge arm did not add one checkpoint.");
            }
            PayloadPurgeCheckpoint added = FindPurge(after, purgeId);
            if (!SamePurgesExcept(before, after, purgeId))
            {
                throw new InvalidOperationException(
                    "Payload purge arm changed existing checkpoints.");
            }
            return added;
        }

        private static bool SameQuarantinesExcept(
            IList<PayloadQuarantineCheckpoint> before,
            IList<PayloadQuarantineCheckpoint> after,
            string excludedId)
        {
            return SameExcept(
                QuarantineMap(before),
                QuarantineMap(after),
                excludedId);
        }

        private static bool SamePurgesExcept(
            IList<PayloadPurgeCheckpoint> before,
            IList<PayloadPurgeCheckpoint> after,
            string excludedId)
        {
            return SameExcept(
                PurgeMap(before),
                PurgeMap(after),
                excludedId);
        }

        private static Dictionary<string, string> QuarantineMap(
            IList<PayloadQuarantineCheckpoint> source)
        {
            var result = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (PayloadQuarantineCheckpoint item in source)
            {
                result.Add(item.QuarantineId, item.InvariantDigest);
            }
            return result;
        }

        private static Dictionary<string, string> PurgeMap(
            IList<PayloadPurgeCheckpoint> source)
        {
            var result = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (PayloadPurgeCheckpoint item in source)
            {
                result.Add(item.PurgeId, item.InvariantDigest);
            }
            return result;
        }

        private static bool SameExcept(
            Dictionary<string, string> before,
            Dictionary<string, string> after,
            string excludedId)
        {
            foreach (KeyValuePair<string, string> item in before)
            {
                if (String.Equals(
                        item.Key,
                        excludedId,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                string digest;
                if (!after.TryGetValue(item.Key, out digest) ||
                    !String.Equals(
                        item.Value,
                        digest,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            foreach (KeyValuePair<string, string> item in after)
            {
                if (String.Equals(
                        item.Key,
                        excludedId,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                string digest;
                if (!before.TryGetValue(item.Key, out digest) ||
                    !String.Equals(
                        item.Value,
                        digest,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ContainsQuarantine(
            IList<PayloadQuarantineCheckpoint> source,
            string quarantineId)
        {
            foreach (PayloadQuarantineCheckpoint item in source)
            {
                if (String.Equals(
                        item.QuarantineId,
                        quarantineId,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsPurge(
            IList<PayloadPurgeCheckpoint> source,
            string purgeId)
        {
            foreach (PayloadPurgeCheckpoint item in source)
            {
                if (String.Equals(
                        item.PurgeId,
                        purgeId,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal sealed class PayloadBuildWorkspaceState
    {
        private readonly PayloadBuildWorkspaceCheckpoint checkpoint;

        internal PayloadBuildWorkspaceState(
            PayloadBuildWorkspaceCheckpoint verifiedCheckpoint)
        {
            if (verifiedCheckpoint == null)
            {
                throw new ArgumentNullException("verifiedCheckpoint");
            }
            verifiedCheckpoint.Validate();
            checkpoint = verifiedCheckpoint.DeepClone();
        }

        internal PayloadBuildWorkspaceCheckpoint Checkpoint
        {
            get { return checkpoint.DeepClone(); }
        }

        internal string InvariantDigest
        {
            get { return checkpoint.InvariantDigest; }
        }

        internal long Revision
        {
            get { return checkpoint.Revision; }
        }

        internal PayloadWorkspaceCasToken CasToken
        {
            get
            {
                return new PayloadWorkspaceCasToken
                {
                    SchemaVersion = 1,
                    TransactionId = checkpoint.TransactionId,
                    Revision = checkpoint.Revision,
                    WorkspaceInvariantDigest = checkpoint.InvariantDigest
                };
            }
        }

        internal void RequireCas(PayloadWorkspaceCasToken expected)
        {
            if (expected == null)
            {
                throw new ArgumentNullException("expected");
            }
            expected.Validate();
            if (!String.Equals(
                    expected.TransactionId,
                    checkpoint.TransactionId,
                    StringComparison.Ordinal) ||
                expected.Revision != checkpoint.Revision ||
                !String.Equals(
                    expected.WorkspaceInvariantDigest,
                    checkpoint.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload workspace compare-and-swap token is stale.");
            }
        }
    }
}
