using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace SBMSSetup
{
    internal static class PayloadContractValidation
    {
        internal static void RequireCanonicalTransactionId(
            string transactionId,
            string description)
        {
            Guid parsed;
            if (String.IsNullOrEmpty(transactionId) ||
                !Guid.TryParseExact(transactionId, "N", out parsed) ||
                !String.Equals(
                    transactionId,
                    parsed.ToString("N"),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    description + " is not a canonical N-format GUID.");
            }
        }

        internal static void RequireSha256(
            string value,
            string description)
        {
            if (!EscrowManifestValidation.IsSha256(value))
            {
                throw new InvalidOperationException(
                    description + " is not a lowercase SHA-256 digest.");
            }
        }

        internal static void RequireFileId(
            string value,
            string description)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 32)
            {
                throw new InvalidOperationException(
                    description + " is not a 128-bit file identity.");
            }
            for (int index = 0; index < value.Length; ++index)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    throw new InvalidOperationException(
                        description + " is not lowercase hexadecimal.");
                }
            }
        }

        internal static void RequireCanonicalRelease(
            ReleaseIdentity release,
            string description)
        {
            if (release == null)
            {
                throw new InvalidOperationException(
                    description + " is missing.");
            }
            release.Validate();
            if (!String.Equals(
                    release.Version,
                    release.Version.Trim(),
                    StringComparison.Ordinal) ||
                !String.Equals(
                    release.PackageFingerprint,
                    release.PackageFingerprint.Trim(),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    description + " contains non-canonical whitespace.");
            }
            RequireSha256(
                release.PackageFingerprint,
                description + " package fingerprint");
        }

        internal static void RequirePayloadRelativePath(
            string value,
            string description)
        {
            WindowsPathSafety.RequireCanonicalRelative(value, description);
            string[] segments = value.Split('\\');
            foreach (string segment in segments)
            {
                if (segment.Length == 0 ||
                    segment.Length > 255 ||
                    segment[segment.Length - 1] == ' ' ||
                    segment[segment.Length - 1] == '.')
                {
                    throw new InvalidOperationException(
                        description + " contains a non-canonical segment.");
                }
                foreach (char character in segment)
                {
                    if (character < 32 ||
                        "<>:\"/\\|?*".IndexOf(character) >= 0)
                    {
                        throw new InvalidOperationException(
                            description + " contains an invalid Windows character.");
                    }
                }
                string baseName = segment;
                int dot = baseName.IndexOf('.');
                if (dot >= 0)
                {
                    baseName = baseName.Substring(0, dot);
                }
                string upper = baseName.ToUpperInvariant();
                if (upper == "CON" ||
                    upper == "PRN" ||
                    upper == "AUX" ||
                    upper == "NUL" ||
                    upper == "CLOCK$" ||
                    IsNumberedDevice(upper, "COM") ||
                    IsNumberedDevice(upper, "LPT"))
                {
                    throw new InvalidOperationException(
                        description + " contains a reserved DOS device name.");
                }
            }
        }

        internal static bool IsAncestorPath(
            string possibleAncestor,
            string path)
        {
            return path.Length > possibleAncestor.Length &&
                path.StartsWith(
                    possibleAncestor,
                    StringComparison.OrdinalIgnoreCase) &&
                path[possibleAncestor.Length] == '\\';
        }

        private static bool IsNumberedDevice(
            string value,
            string prefix)
        {
            return value.Length == prefix.Length + 1 &&
                value.StartsWith(prefix, StringComparison.Ordinal) &&
                value[prefix.Length] >= '1' &&
                value[prefix.Length] <= '9';
        }

        internal static ReleaseIdentity CloneRelease(ReleaseIdentity source)
        {
            if (source == null)
            {
                return null;
            }
            RequireCanonicalRelease(source, "Release identity");
            return new ReleaseIdentity(
                source.Version,
                source.PackageFingerprint);
        }

        internal static string ComputeDigest(
            string domain,
            IEnumerable<string> fields)
        {
            using (var buffer = new MemoryStream())
            {
                Append(buffer, domain);
                foreach (string field in fields)
                {
                    Append(buffer, field);
                }
                using (SHA256 algorithm = SHA256.Create())
                {
                    return ToLowerHex(
                        algorithm.ComputeHash(buffer.ToArray()));
                }
            }
        }

        private static void Append(Stream destination, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? String.Empty);
            byte[] length =
            {
                (byte)((bytes.Length >> 24) & 0xff),
                (byte)((bytes.Length >> 16) & 0xff),
                (byte)((bytes.Length >> 8) & 0xff),
                (byte)(bytes.Length & 0xff)
            };
            destination.Write(length, 0, length.Length);
            destination.Write(bytes, 0, bytes.Length);
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(
                    value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }

    [DataContract]
    internal sealed class TargetPayloadEntry
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal string RelativePath;

        [DataMember(Order = 2, IsRequired = true)]
        internal long Length;

        [DataMember(Order = 3, IsRequired = true)]
        internal string Sha256;

        internal void Validate()
        {
            PayloadContractValidation.RequirePayloadRelativePath(
                RelativePath,
                "Target payload entry path");
            if (Length < 0)
            {
                throw new InvalidOperationException(
                    "Target payload entry length cannot be negative.");
            }
            PayloadContractValidation.RequireSha256(
                Sha256,
                "Target payload entry digest");
        }

        internal TargetPayloadEntry DeepClone()
        {
            return new TargetPayloadEntry
            {
                RelativePath = RelativePath,
                Length = Length,
                Sha256 = Sha256
            };
        }
    }

    [DataContract]
    internal sealed class TargetPayloadManifest
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 3, IsRequired = true)]
        internal ReleaseIdentity Target;

        [DataMember(Order = 4, IsRequired = true)]
        internal string ReleaseCatalogSha256;

        [DataMember(Order = 5, IsRequired = true)]
        internal string SignedReleaseManifestSha256;

        [DataMember(Order = 6, IsRequired = true)]
        internal List<TargetPayloadEntry> Content;

        [DataMember(Order = 7, IsRequired = true)]
        internal string ContentSetSha256;

        internal TargetPayloadManifest()
        {
            Content = new List<TargetPayloadEntry>();
        }

        internal void Validate()
        {
            if (SchemaVersion != 1 ||
                Target == null ||
                Content == null ||
                Content.Count == 0)
            {
                throw new InvalidOperationException(
                    "Target payload manifest identity is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Target payload transaction ID");
            PayloadContractValidation.RequireCanonicalRelease(
                Target,
                "Target release");
            PayloadContractValidation.RequireSha256(
                ReleaseCatalogSha256,
                "Release catalog digest");
            PayloadContractValidation.RequireSha256(
                SignedReleaseManifestSha256,
                "Signed release manifest digest");

            string previous = null;
            var unique = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (TargetPayloadEntry entry in Content)
            {
                if (entry == null)
                {
                    throw new InvalidOperationException(
                        "Target payload manifest contains a null entry.");
                }
                entry.Validate();
                if (!unique.Add(entry.RelativePath))
                {
                    throw new InvalidOperationException(
                        "Target payload manifest contains duplicate paths.");
                }
                if (previous != null &&
                    StringComparer.Ordinal.Compare(
                        previous,
                        entry.RelativePath) >= 0)
                {
                    throw new InvalidOperationException(
                        "Target payload manifest entries are not ordinal-sorted.");
                }
                previous = entry.RelativePath;
            }
            foreach (TargetPayloadEntry entry in Content)
            {
                int separator = entry.RelativePath.LastIndexOf('\\');
                while (separator > 0)
                {
                    string ancestor =
                        entry.RelativePath.Substring(0, separator);
                    if (unique.Contains(ancestor))
                    {
                        throw new InvalidOperationException(
                            "A target payload path is both a file and a directory.");
                    }
                    separator = ancestor.LastIndexOf('\\');
                }
            }
            PayloadContractValidation.RequireSha256(
                ContentSetSha256,
                "Target payload content-set digest");
            if (!String.Equals(
                ContentSetSha256,
                ComputeContentSetSha256(),
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Target payload content-set digest does not match content.");
            }
        }

        internal string ComputeContentSetSha256()
        {
            if (Target == null || Content == null)
            {
                throw new InvalidOperationException(
                    "Cannot digest an incomplete target payload manifest.");
            }
            PayloadContractValidation.RequireCanonicalRelease(
                Target,
                "Target release");
            var fields = new List<string>
            {
                Target.Version,
                Target.PackageFingerprint,
                ReleaseCatalogSha256,
                SignedReleaseManifestSha256,
                Content.Count.ToString(CultureInfo.InvariantCulture)
            };
            foreach (TargetPayloadEntry entry in Content)
            {
                if (entry == null)
                {
                    throw new InvalidOperationException(
                        "Cannot digest a null target payload entry.");
                }
                entry.Validate();
                fields.Add(entry.RelativePath);
                fields.Add(
                    entry.Length.ToString(CultureInfo.InvariantCulture));
                fields.Add(entry.Sha256);
            }
            return PayloadContractValidation.ComputeDigest(
                "SBMS.TargetPayloadContent.v1",
                fields);
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.TargetPayloadManifest.v1",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        TransactionId,
                        ContentSetSha256
                    });
            }
        }

        internal TargetPayloadManifest DeepClone()
        {
            var clone = new TargetPayloadManifest
            {
                SchemaVersion = SchemaVersion,
                TransactionId = TransactionId,
                Target =
                    PayloadContractValidation.CloneRelease(Target),
                ReleaseCatalogSha256 = ReleaseCatalogSha256,
                SignedReleaseManifestSha256 =
                    SignedReleaseManifestSha256,
                ContentSetSha256 = ContentSetSha256
            };
            if (Content != null)
            {
                foreach (TargetPayloadEntry entry in Content)
                {
                    clone.Content.Add(
                        entry == null ? null : entry.DeepClone());
                }
            }
            return clone;
        }
    }

    internal sealed class TrustedReleasePayloadReceipt
    {
        private readonly TargetPayloadManifest manifest;

        internal TrustedReleasePayloadReceipt(
            TargetPayloadManifest verifiedManifest)
        {
            if (verifiedManifest == null)
            {
                throw new ArgumentNullException("verifiedManifest");
            }
            verifiedManifest.Validate();
            manifest = verifiedManifest.DeepClone();
        }

        internal TargetPayloadManifest Manifest
        {
            get { return manifest.DeepClone(); }
        }

        internal string TransactionId
        {
            get { return manifest.TransactionId; }
        }

        internal string InvariantDigest
        {
            get { return manifest.InvariantDigest; }
        }

        internal int FileCount
        {
            get { return manifest.Content.Count; }
        }

        internal long TotalBytes
        {
            get
            {
                long total = 0;
                foreach (TargetPayloadEntry entry in manifest.Content)
                {
                    total = checked(total + entry.Length);
                }
                return total;
            }
        }
    }

    [DataContract]
    internal sealed class PayloadContentAuthority
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal ReleaseIdentity Release;

        [DataMember(Order = 2, IsRequired = true)]
        internal string ContentSetSha256;

        [DataMember(Order = 3, IsRequired = true)]
        internal string ManifestInvariantDigest;

        [DataMember(Order = 4, IsRequired = true)]
        internal string SemanticTreeSha256;

        [DataMember(Order = 5, IsRequired = true)]
        internal int FileCount;

        [DataMember(Order = 6, IsRequired = true)]
        internal long TotalBytes;

        internal void Validate()
        {
            PayloadContractValidation.RequireCanonicalRelease(
                Release,
                "Payload content authority release");
            PayloadContractValidation.RequireSha256(
                ContentSetSha256,
                "Payload content authority content-set digest");
            PayloadContractValidation.RequireSha256(
                ManifestInvariantDigest,
                "Payload content authority manifest digest");
            PayloadContractValidation.RequireSha256(
                SemanticTreeSha256,
                "Payload content authority semantic tree digest");
            if (FileCount <= 0 || TotalBytes < 0)
            {
                throw new InvalidOperationException(
                    "Payload content authority totals are invalid.");
            }
        }

        internal bool Matches(PayloadDirectoryCheckpoint directory)
        {
            Validate();
            if (directory == null)
            {
                return false;
            }
            directory.Validate();
            return String.Equals(
                    Release.Version,
                    directory.Release.Version,
                    StringComparison.Ordinal) &&
                String.Equals(
                    Release.PackageFingerprint,
                    directory.Release.PackageFingerprint,
                    StringComparison.Ordinal) &&
                String.Equals(
                    ContentSetSha256,
                    directory.ContentSetSha256,
                    StringComparison.Ordinal) &&
                String.Equals(
                    ManifestInvariantDigest,
                    directory.ManifestInvariantDigest,
                    StringComparison.Ordinal) &&
                String.Equals(
                    SemanticTreeSha256,
                    directory.SemanticTreeSha256,
                    StringComparison.Ordinal) &&
                FileCount == directory.FileCount &&
                TotalBytes == directory.TotalBytes;
        }

        internal PayloadContentAuthority DeepClone()
        {
            return new PayloadContentAuthority
            {
                Release =
                    PayloadContractValidation.CloneRelease(Release),
                ContentSetSha256 = ContentSetSha256,
                ManifestInvariantDigest = ManifestInvariantDigest,
                SemanticTreeSha256 = SemanticTreeSha256,
                FileCount = FileCount,
                TotalBytes = TotalBytes
            };
        }
    }

    [DataContract]
    internal sealed class PayloadRecoveryAuthority
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 3, IsRequired = true)]
        internal InstallOperation Operation;

        [DataMember(Order = 4, IsRequired = true)]
        internal BaselinePayloadState BaselineState;

        [DataMember(Order = 5, EmitDefaultValue = false)]
        internal PayloadContentAuthority Baseline;

        [DataMember(Order = 6, EmitDefaultValue = false)]
        internal PayloadContentAuthority Target;

        [DataMember(Order = 7, IsRequired = true)]
        internal string SealedEscrowManifestSha256;

        internal void Validate()
        {
            if (SchemaVersion != 1 ||
                !Enum.IsDefined(typeof(InstallOperation), Operation) ||
                !Enum.IsDefined(
                    typeof(BaselinePayloadState),
                    BaselineState))
            {
                throw new InvalidOperationException(
                    "Payload recovery authority identity is invalid.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload recovery transaction ID");
            PayloadContractValidation.RequireSha256(
                SealedEscrowManifestSha256,
                "Sealed escrow manifest digest");
            bool baselineRequired =
                Operation != InstallOperation.FreshInstall;
            bool targetRequired =
                Operation != InstallOperation.Uninstall;
            if ((BaselineState == BaselinePayloadState.Present) !=
                    baselineRequired ||
                (Baseline != null) != baselineRequired ||
                (Target != null) != targetRequired)
            {
                throw new InvalidOperationException(
                    "Payload recovery authority disagrees with the operation.");
            }
            if (Baseline != null)
            {
                Baseline.Validate();
            }
            if (Target != null)
            {
                Target.Validate();
            }
        }

        internal PayloadRecoveryAuthority DeepClone()
        {
            return new PayloadRecoveryAuthority
            {
                SchemaVersion = SchemaVersion,
                TransactionId = TransactionId,
                Operation = Operation,
                BaselineState = BaselineState,
                Baseline =
                    Baseline == null ? null : Baseline.DeepClone(),
                Target =
                    Target == null ? null : Target.DeepClone(),
                SealedEscrowManifestSha256 =
                    SealedEscrowManifestSha256
            };
        }
    }

    internal interface ITrustedReleasePayloadSource : IDisposable
    {
        TrustedReleasePayloadReceipt Receipt { get; }
        Stream OpenExact(TargetPayloadEntry expected);
    }

    internal enum PayloadDirectorySlot
    {
        Current,
        Candidate,
        Backup
    }

    internal enum PayloadNamespaceShape
    {
        Empty,
        CurrentOnly,
        CandidateOnly,
        BackupOnly,
        CurrentAndCandidate,
        CurrentAndBackup,
        CandidateAndBackup
    }

    [DataContract]
    internal sealed class PayloadTreeEntryCheckpoint
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal string RelativePath;

        [DataMember(Order = 2, IsRequired = true)]
        internal bool IsDirectory;

        [DataMember(Order = 3, IsRequired = true)]
        internal string FileId;

        [DataMember(Order = 4, IsRequired = true)]
        internal long Length;

        [DataMember(Order = 5, IsRequired = true)]
        internal string Sha256;

        internal void Validate()
        {
            PayloadContractValidation.RequirePayloadRelativePath(
                RelativePath,
                "Payload tree entry path");
            PayloadContractValidation.RequireFileId(
                FileId,
                "Payload tree entry file ID");
            if (IsDirectory)
            {
                if (Length != 0 || !String.IsNullOrEmpty(Sha256))
                {
                    throw new InvalidOperationException(
                        "Payload directory entry carries file content metadata.");
                }
            }
            else
            {
                if (Length < 0)
                {
                    throw new InvalidOperationException(
                        "Payload file entry length cannot be negative.");
                }
                PayloadContractValidation.RequireSha256(
                    Sha256,
                    "Payload file entry digest");
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadTreeEntryCheckpoint.v1",
                    new[]
                    {
                        RelativePath,
                        IsDirectory.ToString(
                            CultureInfo.InvariantCulture),
                        FileId,
                        Length.ToString(CultureInfo.InvariantCulture),
                        Sha256
                    });
            }
        }

        internal string SemanticDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadTreeEntrySemantic.v1",
                    new[]
                    {
                        RelativePath,
                        IsDirectory.ToString(
                            CultureInfo.InvariantCulture),
                        Length.ToString(CultureInfo.InvariantCulture),
                        Sha256
                    });
            }
        }

        internal PayloadTreeEntryCheckpoint DeepClone()
        {
            return new PayloadTreeEntryCheckpoint
            {
                RelativePath = RelativePath,
                IsDirectory = IsDirectory,
                FileId = FileId,
                Length = Length,
                Sha256 = Sha256
            };
        }
    }

    [DataContract]
    internal sealed class PayloadDirectoryCheckpoint
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 2, IsRequired = true)]
        internal PayloadDirectorySlot Slot;

        [DataMember(Order = 3, IsRequired = true)]
        internal ulong VolumeSerialNumber;

        [DataMember(Order = 4, IsRequired = true)]
        internal string FileId;

        [DataMember(Order = 5, IsRequired = true)]
        internal ReleaseIdentity Release;

        [DataMember(Order = 6, IsRequired = true)]
        internal string ContentSetSha256;

        [DataMember(Order = 7, IsRequired = true)]
        internal string ManifestInvariantDigest;

        [DataMember(Order = 8, IsRequired = true)]
        internal int FileCount;

        [DataMember(Order = 9, IsRequired = true)]
        internal long TotalBytes;

        [DataMember(Order = 10, IsRequired = true)]
        internal List<PayloadTreeEntryCheckpoint> Entries;

        internal PayloadDirectoryCheckpoint()
        {
            Entries = new List<PayloadTreeEntryCheckpoint>();
        }

        internal void Validate()
        {
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload directory transaction ID");
            if (!Enum.IsDefined(typeof(PayloadDirectorySlot), Slot) ||
                FileCount <= 0 ||
                TotalBytes < 0 ||
                Entries == null)
            {
                throw new InvalidOperationException(
                    "Payload directory checkpoint is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalRelease(
                Release,
                "Payload directory release");
            PayloadContractValidation.RequireFileId(
                FileId,
                "Payload directory file ID");
            PayloadContractValidation.RequireSha256(
                ContentSetSha256,
                "Payload directory content-set digest");
            PayloadContractValidation.RequireSha256(
                ManifestInvariantDigest,
                "Payload directory manifest digest");

            int files = 0;
            long bytes = 0;
            string previous = null;
            var paths = new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase);
            var identities = new HashSet<string>(
                StringComparer.Ordinal);
            identities.Add(FileId);
            foreach (PayloadTreeEntryCheckpoint entry in Entries)
            {
                if (entry == null)
                {
                    throw new InvalidOperationException(
                        "Payload directory checkpoint contains a null tree entry.");
                }
                entry.Validate();
                if (!paths.ContainsKey(entry.RelativePath))
                {
                    paths.Add(entry.RelativePath, entry.IsDirectory);
                }
                else
                {
                    throw new InvalidOperationException(
                        "Payload directory checkpoint contains duplicate paths.");
                }
                if (!identities.Add(entry.FileId))
                {
                    throw new InvalidOperationException(
                        "Payload directory checkpoint aliases a file identity.");
                }
                if (previous != null &&
                    StringComparer.Ordinal.Compare(
                        previous,
                        entry.RelativePath) >= 0)
                {
                    throw new InvalidOperationException(
                        "Payload directory entries are not ordinal-sorted.");
                }
                previous = entry.RelativePath;
                if (!entry.IsDirectory)
                {
                    files++;
                    bytes = checked(bytes + entry.Length);
                }
            }
            foreach (PayloadTreeEntryCheckpoint entry in Entries)
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
                            "Payload tree omits a verified parent directory.");
                    }
                    separator = ancestor.LastIndexOf('\\');
                }
            }
            if (files != FileCount || bytes != TotalBytes)
            {
                throw new InvalidOperationException(
                    "Payload directory totals disagree with its tree entries.");
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                var fields = new List<string>
                {
                    TransactionId,
                    Slot.ToString(),
                    VolumeSerialNumber.ToString(
                        "x16",
                        CultureInfo.InvariantCulture),
                    FileId,
                    Release.Version,
                    Release.PackageFingerprint,
                    ContentSetSha256,
                    ManifestInvariantDigest,
                    FileCount.ToString(CultureInfo.InvariantCulture),
                    TotalBytes.ToString(CultureInfo.InvariantCulture),
                    Entries.Count.ToString(CultureInfo.InvariantCulture)
                };
                foreach (PayloadTreeEntryCheckpoint entry in Entries)
                {
                    fields.Add(entry.InvariantDigest);
                }
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadDirectoryCheckpoint.v1",
                    fields);
            }
        }

        internal string SemanticTreeSha256
        {
            get
            {
                Validate();
                var fields = new List<string>
                {
                    Entries.Count.ToString(
                        CultureInfo.InvariantCulture)
                };
                foreach (PayloadTreeEntryCheckpoint entry in Entries)
                {
                    fields.Add(entry.SemanticDigest);
                }
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadSemanticTree.v1",
                    fields);
            }
        }

        internal PayloadDirectoryCheckpoint DeepClone()
        {
            var clone = new PayloadDirectoryCheckpoint
            {
                TransactionId = TransactionId,
                Slot = Slot,
                VolumeSerialNumber = VolumeSerialNumber,
                FileId = FileId,
                Release =
                    PayloadContractValidation.CloneRelease(Release),
                ContentSetSha256 = ContentSetSha256,
                ManifestInvariantDigest = ManifestInvariantDigest,
                FileCount = FileCount,
                TotalBytes = TotalBytes
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
    }

    [DataContract]
    internal sealed class PayloadNamespaceCheckpoint
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal long Revision;

        [DataMember(Order = 3, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 4, IsRequired = true)]
        internal PayloadNamespaceShape Shape;

        [DataMember(Order = 5, EmitDefaultValue = false)]
        internal PayloadDirectoryCheckpoint Current;

        [DataMember(Order = 6, EmitDefaultValue = false)]
        internal PayloadDirectoryCheckpoint Candidate;

        [DataMember(Order = 7, EmitDefaultValue = false)]
        internal PayloadDirectoryCheckpoint Backup;

        internal void Validate()
        {
            if (SchemaVersion != 1 || Revision < 0 ||
                !Enum.IsDefined(typeof(PayloadNamespaceShape), Shape))
            {
                throw new InvalidOperationException(
                    "Payload namespace checkpoint identity is invalid.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload namespace transaction ID");
            ValidateSlot(Current, PayloadDirectorySlot.Current);
            ValidateSlot(Candidate, PayloadDirectorySlot.Candidate);
            ValidateSlot(Backup, PayloadDirectorySlot.Backup);
            var nativeIdentities = new HashSet<string>(
                StringComparer.Ordinal);
            AddNativeIdentities(Current, nativeIdentities);
            AddNativeIdentities(Candidate, nativeIdentities);
            AddNativeIdentities(Backup, nativeIdentities);

            bool current = Current != null;
            bool candidate = Candidate != null;
            bool backup = Backup != null;
            PayloadNamespaceShape actual = ShapeFor(
                current,
                candidate,
                backup);
            if (Shape != actual)
            {
                throw new InvalidOperationException(
                    "Payload namespace shape disagrees with its slots.");
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadNamespaceCheckpoint.v1",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        Revision.ToString(CultureInfo.InvariantCulture),
                        TransactionId,
                        Shape.ToString(),
                        Current == null
                            ? String.Empty
                            : Current.InvariantDigest,
                        Candidate == null
                            ? String.Empty
                            : Candidate.InvariantDigest,
                        Backup == null
                            ? String.Empty
                            : Backup.InvariantDigest
                    });
            }
        }

        internal PayloadNamespaceCheckpoint DeepClone()
        {
            return new PayloadNamespaceCheckpoint
            {
                SchemaVersion = SchemaVersion,
                Revision = Revision,
                TransactionId = TransactionId,
                Shape = Shape,
                Current =
                    Current == null ? null : Current.DeepClone(),
                Candidate =
                    Candidate == null ? null : Candidate.DeepClone(),
                Backup =
                    Backup == null ? null : Backup.DeepClone()
            };
        }

        private void ValidateSlot(
            PayloadDirectoryCheckpoint checkpoint,
            PayloadDirectorySlot expectedSlot)
        {
            if (checkpoint == null)
            {
                return;
            }
            checkpoint.Validate();
            if (checkpoint.Slot != expectedSlot ||
                !String.Equals(
                    checkpoint.TransactionId,
                    TransactionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload directory checkpoint is bound to the wrong slot " +
                    "or transaction.");
            }
        }

        private static void AddNativeIdentities(
            PayloadDirectoryCheckpoint checkpoint,
            HashSet<string> identities)
        {
            if (checkpoint == null)
            {
                return;
            }
            string prefix = checkpoint.VolumeSerialNumber.ToString(
                "x16",
                CultureInfo.InvariantCulture) + ":";
            if (!identities.Add(prefix + checkpoint.FileId))
            {
                throw new InvalidOperationException(
                    "Payload namespace aliases a native root identity.");
            }
            foreach (PayloadTreeEntryCheckpoint entry in checkpoint.Entries)
            {
                if (!identities.Add(prefix + entry.FileId))
                {
                    throw new InvalidOperationException(
                        "Payload namespace aliases a native tree identity.");
                }
            }
        }

        private static PayloadNamespaceShape ShapeFor(
            bool current,
            bool candidate,
            bool backup)
        {
            if (current && candidate && backup)
            {
                throw new InvalidOperationException(
                    "All three payload slots cannot be simultaneously owned.");
            }
            if (current && candidate)
            {
                return PayloadNamespaceShape.CurrentAndCandidate;
            }
            if (current && backup)
            {
                return PayloadNamespaceShape.CurrentAndBackup;
            }
            if (candidate && backup)
            {
                return PayloadNamespaceShape.CandidateAndBackup;
            }
            if (current)
            {
                return PayloadNamespaceShape.CurrentOnly;
            }
            if (candidate)
            {
                return PayloadNamespaceShape.CandidateOnly;
            }
            if (backup)
            {
                return PayloadNamespaceShape.BackupOnly;
            }
            return PayloadNamespaceShape.Empty;
        }
    }

    internal static class PayloadNamespaceNames
    {
        internal static string ForSlot(
            PayloadDirectorySlot slot,
            string transactionId)
        {
            PayloadContractValidation.RequireCanonicalTransactionId(
                transactionId,
                "Payload namespace transaction ID");
            switch (slot)
            {
                case PayloadDirectorySlot.Current:
                    return "SBMS";
                case PayloadDirectorySlot.Candidate:
                    return ".SBMS.candidate." + transactionId;
                case PayloadDirectorySlot.Backup:
                    return ".SBMS.backup." + transactionId;
                default:
                    throw new InvalidOperationException(
                        "Payload directory slot is invalid.");
            }
        }
    }

    internal sealed class PayloadNamespaceState
    {
        private readonly PayloadNamespaceCheckpoint checkpoint;

        internal PayloadNamespaceState(
            PayloadNamespaceCheckpoint verifiedCheckpoint)
        {
            if (verifiedCheckpoint == null)
            {
                throw new ArgumentNullException("verifiedCheckpoint");
            }
            verifiedCheckpoint.Validate();
            checkpoint = verifiedCheckpoint.DeepClone();
        }

        internal PayloadNamespaceCheckpoint Checkpoint
        {
            get { return checkpoint.DeepClone(); }
        }

        internal string InvariantDigest
        {
            get { return checkpoint.InvariantDigest; }
        }

        internal string TransactionId
        {
            get { return checkpoint.TransactionId; }
        }

        internal long Revision
        {
            get { return checkpoint.Revision; }
        }

        internal PayloadNamespaceShape Shape
        {
            get { return checkpoint.Shape; }
        }
    }

    internal enum PayloadRecoveryDecision
    {
        CompleteForward,
        RestoreBaseline
    }

    internal enum PayloadCleanupKind
    {
        Candidate,
        CommittedBackup
    }

    internal sealed class PayloadCandidateReceipt
    {
        internal readonly PayloadNamespaceState State;
        internal readonly string TransactionId;
        internal readonly string CandidateInvariantDigest;

        internal PayloadCandidateReceipt(PayloadNamespaceState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException("state");
            }
            PayloadNamespaceCheckpoint checkpoint = state.Checkpoint;
            if (checkpoint.Candidate == null ||
                (checkpoint.Shape !=
                    PayloadNamespaceShape.CandidateOnly &&
                 checkpoint.Shape !=
                    PayloadNamespaceShape.CurrentAndCandidate))
            {
                throw new InvalidOperationException(
                    "Candidate receipt does not identify a staged candidate.");
            }
            State = state;
            TransactionId = checkpoint.TransactionId;
            CandidateInvariantDigest =
                checkpoint.Candidate.InvariantDigest;
        }
    }

    internal sealed class PayloadPromotionReceipt
    {
        private readonly PayloadRecoveryAuthority authority;
        internal readonly PayloadNamespaceState Before;
        internal readonly PayloadNamespaceState After;

        internal PayloadPromotionReceipt(
            PayloadRecoveryAuthority authority,
            PayloadNamespaceState before,
            PayloadNamespaceState after)
        {
            if (authority == null || before == null || after == null)
            {
                throw new ArgumentNullException(
                    authority == null
                        ? "authority"
                        : (before == null ? "before" : "after"));
            }
            PayloadReceiptValidation.ValidatePromotion(
                authority,
                before,
                after);
            this.authority = authority.DeepClone();
            Before = before;
            After = after;
        }

        internal PayloadRecoveryAuthority Authority
        {
            get { return authority.DeepClone(); }
        }
    }

    internal sealed class PayloadRecoveryReceipt
    {
        private readonly PayloadRecoveryAuthority authority;
        internal readonly PayloadRecoveryDecision Decision;
        internal readonly PayloadNamespaceState Before;
        internal readonly PayloadNamespaceState After;

        internal PayloadRecoveryReceipt(
            PayloadRecoveryDecision decision,
            PayloadRecoveryAuthority authority,
            PayloadNamespaceState before,
            PayloadNamespaceState after)
        {
            if (!Enum.IsDefined(
                    typeof(PayloadRecoveryDecision),
                    decision) ||
                authority == null ||
                before == null ||
                after == null)
            {
                throw new InvalidOperationException(
                    "Payload recovery receipt is incomplete.");
            }
            PayloadReceiptValidation.ValidateRecovery(
                decision,
                authority,
                before,
                after);
            Decision = decision;
            this.authority = authority.DeepClone();
            Before = before;
            After = after;
        }

        internal PayloadRecoveryAuthority Authority
        {
            get { return authority.DeepClone(); }
        }
    }

    internal sealed class PayloadCleanupReceipt
    {
        private readonly PayloadRecoveryAuthority authority;
        internal readonly PayloadCleanupKind Kind;
        internal readonly PayloadNamespaceState Before;
        internal readonly PayloadNamespaceState After;
        internal readonly bool Complete;

        internal PayloadCleanupReceipt(
            PayloadRecoveryAuthority authority,
            PayloadCleanupKind kind,
            PayloadNamespaceState before,
            PayloadNamespaceState after,
            bool complete)
        {
            if (!Enum.IsDefined(typeof(PayloadCleanupKind), kind) ||
                authority == null ||
                before == null ||
                after == null)
            {
                throw new InvalidOperationException(
                    "Payload cleanup receipt is incomplete.");
            }
            PayloadReceiptValidation.ValidateCleanup(
                authority,
                kind,
                before,
                after,
                complete);
            this.authority = authority.DeepClone();
            Kind = kind;
            Before = before;
            After = after;
            Complete = complete;
        }

        internal PayloadRecoveryAuthority Authority
        {
            get { return authority.DeepClone(); }
        }
    }

    internal static class PayloadReceiptValidation
    {
        internal static void ValidatePromotion(
            PayloadRecoveryAuthority authority,
            PayloadNamespaceState before,
            PayloadNamespaceState after)
        {
            authority.Validate();
            if (!String.Equals(
                    authority.TransactionId,
                    before.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    authority.TransactionId,
                    after.TransactionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload promotion authority crosses transactions.");
            }
            RequireForwardRevision(before, after);
            PayloadNamespaceCheckpoint first = before.Checkpoint;
            PayloadNamespaceCheckpoint second = after.Checkpoint;
            bool valid = false;
            if (authority.Operation == InstallOperation.FreshInstall &&
                authority.BaselineState == BaselinePayloadState.Absent &&
                authority.Target != null &&
                first.Shape == PayloadNamespaceShape.CandidateOnly &&
                second.Shape == PayloadNamespaceShape.CurrentOnly)
            {
                valid =
                    authority.Target.Matches(first.Candidate) &&
                    SameDirectoryAfterRename(
                        first.Candidate,
                        second.Current,
                        PayloadDirectorySlot.Current);
            }
            else if (IsReplacementOperation(authority.Operation) &&
                authority.BaselineState == BaselinePayloadState.Present &&
                authority.Baseline != null &&
                authority.Target != null &&
                first.Shape ==
                    PayloadNamespaceShape.CurrentAndCandidate &&
                second.Shape ==
                    PayloadNamespaceShape.CurrentAndBackup)
            {
                valid =
                    authority.Baseline.Matches(first.Current) &&
                    authority.Target.Matches(first.Candidate) &&
                    SameDirectoryAfterRename(
                        first.Current,
                        second.Backup,
                        PayloadDirectorySlot.Backup) &&
                    SameDirectoryAfterRename(
                        first.Candidate,
                        second.Current,
                        PayloadDirectorySlot.Current);
            }
            else if (authority.Operation == InstallOperation.Uninstall &&
                authority.BaselineState == BaselinePayloadState.Present &&
                authority.Baseline != null &&
                first.Shape == PayloadNamespaceShape.CurrentOnly &&
                second.Shape == PayloadNamespaceShape.BackupOnly)
            {
                valid =
                    authority.Baseline.Matches(first.Current) &&
                    SameDirectoryAfterRename(
                        first.Current,
                        second.Backup,
                        PayloadDirectorySlot.Backup);
            }
            if (!valid)
            {
                throw new InvalidOperationException(
                    "Payload promotion receipt is not a valid exact rename transition.");
            }
        }

        internal static void ValidateRecovery(
            PayloadRecoveryDecision decision,
            PayloadRecoveryAuthority authority,
            PayloadNamespaceState before,
            PayloadNamespaceState after)
        {
            authority.Validate();
            if (!String.Equals(
                    authority.TransactionId,
                    before.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    authority.TransactionId,
                    after.TransactionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload recovery authority crosses transactions.");
            }
            bool unchanged = String.Equals(
                before.InvariantDigest,
                after.InvariantDigest,
                StringComparison.Ordinal);
            if (!unchanged)
            {
                RequireForwardRevision(before, after);
            }
            PayloadNamespaceShape first = before.Shape;
            PayloadNamespaceShape second = after.Shape;
            PayloadNamespaceCheckpoint firstCheckpoint =
                before.Checkpoint;
            PayloadNamespaceCheckpoint secondCheckpoint =
                after.Checkpoint;
            bool valid;
            if (decision == PayloadRecoveryDecision.CompleteForward)
            {
                valid =
                    (authority.Operation ==
                        InstallOperation.FreshInstall &&
                     authority.BaselineState ==
                        BaselinePayloadState.Absent &&
                     first == PayloadNamespaceShape.CandidateOnly &&
                     second == PayloadNamespaceShape.CurrentOnly &&
                     authority.Target != null &&
                     authority.Target.Matches(
                        firstCheckpoint.Candidate) &&
                     SameDirectoryAfterRename(
                        firstCheckpoint.Candidate,
                        secondCheckpoint.Current,
                        PayloadDirectorySlot.Current)) ||
                    (IsReplacementOperation(authority.Operation) &&
                     authority.BaselineState ==
                        BaselinePayloadState.Present &&
                     first ==
                        PayloadNamespaceShape.CurrentAndCandidate &&
                     second ==
                        PayloadNamespaceShape.CurrentAndBackup &&
                     authority.Baseline != null &&
                     authority.Target != null &&
                     authority.Baseline.Matches(
                        firstCheckpoint.Current) &&
                     authority.Target.Matches(
                        firstCheckpoint.Candidate) &&
                     SameDirectoryAfterRename(
                        firstCheckpoint.Current,
                        secondCheckpoint.Backup,
                        PayloadDirectorySlot.Backup) &&
                     SameDirectoryAfterRename(
                        firstCheckpoint.Candidate,
                        secondCheckpoint.Current,
                        PayloadDirectorySlot.Current)) ||
                    (IsReplacementOperation(authority.Operation) &&
                     authority.BaselineState ==
                        BaselinePayloadState.Present &&
                     first ==
                        PayloadNamespaceShape.CandidateAndBackup &&
                     second ==
                        PayloadNamespaceShape.CurrentAndBackup &&
                     authority.Baseline != null &&
                     authority.Target != null &&
                     authority.Baseline.Matches(
                        firstCheckpoint.Backup) &&
                     authority.Target.Matches(
                        firstCheckpoint.Candidate) &&
                     SameDirectoryAfterRename(
                        firstCheckpoint.Candidate,
                        secondCheckpoint.Current,
                        PayloadDirectorySlot.Current) &&
                     SameDirectoryUnchanged(
                        firstCheckpoint.Backup,
                        secondCheckpoint.Backup)) ||
                    (unchanged &&
                     authority.Operation ==
                        InstallOperation.FreshInstall &&
                     first == PayloadNamespaceShape.CurrentOnly &&
                     second == PayloadNamespaceShape.CurrentOnly &&
                     authority.Target != null &&
                     authority.Target.Matches(
                        firstCheckpoint.Current)) ||
                    (unchanged &&
                     (authority.Operation == InstallOperation.Upgrade ||
                      authority.Operation == InstallOperation.Repair ||
                      authority.Operation ==
                        InstallOperation.ExplicitDowngrade) &&
                     first == PayloadNamespaceShape.CurrentAndBackup &&
                     second == PayloadNamespaceShape.CurrentAndBackup &&
                     authority.Target != null &&
                     authority.Baseline != null &&
                     authority.Target.Matches(
                        firstCheckpoint.Current) &&
                     authority.Baseline.Matches(
                        firstCheckpoint.Backup)) ||
                    (unchanged &&
                     authority.Operation ==
                        InstallOperation.Uninstall &&
                     first == PayloadNamespaceShape.BackupOnly &&
                     second == PayloadNamespaceShape.BackupOnly &&
                     authority.Baseline != null &&
                     authority.Baseline.Matches(
                        firstCheckpoint.Backup)) ||
                    (authority.Operation ==
                        InstallOperation.Uninstall &&
                     authority.BaselineState ==
                        BaselinePayloadState.Present &&
                     first == PayloadNamespaceShape.CurrentOnly &&
                     second == PayloadNamespaceShape.BackupOnly &&
                     authority.Baseline != null &&
                     authority.Baseline.Matches(
                        firstCheckpoint.Current) &&
                     SameDirectoryAfterRename(
                        firstCheckpoint.Current,
                        secondCheckpoint.Backup,
                        PayloadDirectorySlot.Backup));
            }
            else
            {
                if (authority.BaselineState ==
                    BaselinePayloadState.Absent)
                {
                    valid =
                        authority.Operation ==
                            InstallOperation.FreshInstall &&
                        authority.Target != null &&
                        ((((first ==
                                PayloadNamespaceShape.CandidateOnly &&
                             authority.Target.Matches(
                                firstCheckpoint.Candidate)) ||
                            (first ==
                                PayloadNamespaceShape.CurrentOnly &&
                             authority.Target.Matches(
                                firstCheckpoint.Current))) &&
                           second == PayloadNamespaceShape.Empty) ||
                         (unchanged &&
                          first == PayloadNamespaceShape.Empty &&
                          second == PayloadNamespaceShape.Empty));
                }
                else
                {
                    valid =
                        authority.Baseline != null &&
                        ((unchanged &&
                          first == PayloadNamespaceShape.CurrentOnly &&
                          second == PayloadNamespaceShape.CurrentOnly &&
                          authority.Baseline.Matches(
                            firstCheckpoint.Current)) ||
                         (first ==
                            PayloadNamespaceShape.CurrentAndCandidate &&
                          second ==
                            PayloadNamespaceShape.CurrentOnly &&
                          authority.Baseline.Matches(
                            firstCheckpoint.Current) &&
                          authority.Target != null &&
                          authority.Target.Matches(
                            firstCheckpoint.Candidate) &&
                          SameDirectoryUnchanged(
                            firstCheckpoint.Current,
                            secondCheckpoint.Current)) ||
                         ((first ==
                                PayloadNamespaceShape.CandidateAndBackup ||
                            first ==
                                PayloadNamespaceShape.CurrentAndBackup ||
                            first == PayloadNamespaceShape.BackupOnly) &&
                          second ==
                            PayloadNamespaceShape.CurrentOnly &&
                          authority.Baseline.Matches(
                            firstCheckpoint.Backup) &&
                          ((first ==
                                PayloadNamespaceShape.CandidateAndBackup &&
                            authority.Target != null &&
                            authority.Target.Matches(
                                firstCheckpoint.Candidate)) ||
                           (first ==
                                PayloadNamespaceShape.CurrentAndBackup &&
                            authority.Target != null &&
                            authority.Target.Matches(
                                firstCheckpoint.Current)) ||
                           (first == PayloadNamespaceShape.BackupOnly &&
                            (authority.Operation ==
                                InstallOperation.Uninstall ||
                             IsReplacementOperation(
                                authority.Operation)))) &&
                          SameDirectoryAfterRename(
                            firstCheckpoint.Backup,
                            secondCheckpoint.Current,
                            PayloadDirectorySlot.Current)));
                }
            }
            if (!valid)
            {
                throw new InvalidOperationException(
                    "Payload recovery receipt has an illegal namespace transition.");
            }
        }

        internal static void ValidateCleanup(
            PayloadRecoveryAuthority authority,
            PayloadCleanupKind kind,
            PayloadNamespaceState before,
            PayloadNamespaceState after,
            bool complete)
        {
            if (authority == null)
            {
                throw new ArgumentNullException("authority");
            }
            authority.Validate();
            if (!String.Equals(
                    authority.TransactionId,
                    before.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                before.TransactionId,
                after.TransactionId,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload cleanup receipt crosses transactions.");
            }
            bool unchanged = String.Equals(
                before.InvariantDigest,
                after.InvariantDigest,
                StringComparison.Ordinal);
            if (!complete)
            {
                if (!unchanged ||
                    !CleanupStateAuthorized(
                        authority,
                        kind,
                        before.Checkpoint))
                {
                    throw new InvalidOperationException(
                        "Incomplete cleanup is mutated or unauthorized.");
                }
                return;
            }
            if (unchanged)
            {
                if (!CleanupTerminalAuthorized(
                    authority,
                    kind,
                    before.Checkpoint))
                {
                    throw new InvalidOperationException(
                        "Completed cleanup terminal state is unauthorized.");
                }
                return;
            }
            RequireForwardRevision(before, after);
            bool valid;
            if (kind == PayloadCleanupKind.Candidate)
            {
                valid =
                    (authority.Operation ==
                        InstallOperation.FreshInstall &&
                     before.Shape ==
                        PayloadNamespaceShape.CandidateOnly &&
                     authority.Target != null &&
                     authority.Target.Matches(
                        before.Checkpoint.Candidate) &&
                     after.Shape == PayloadNamespaceShape.Empty) ||
                    (IsReplacementOperation(authority.Operation) &&
                     before.Shape ==
                        PayloadNamespaceShape.CurrentAndCandidate &&
                     authority.Baseline != null &&
                     authority.Baseline.Matches(
                        before.Checkpoint.Current) &&
                     authority.Target != null &&
                     authority.Target.Matches(
                        before.Checkpoint.Candidate) &&
                     after.Shape ==
                        PayloadNamespaceShape.CurrentOnly &&
                     SameDirectoryUnchanged(
                        before.Checkpoint.Current,
                        after.Checkpoint.Current)) ||
                    (IsReplacementOperation(authority.Operation) &&
                     before.Shape ==
                        PayloadNamespaceShape.CandidateAndBackup &&
                     authority.Target != null &&
                     authority.Target.Matches(
                        before.Checkpoint.Candidate) &&
                     authority.Baseline != null &&
                     authority.Baseline.Matches(
                        before.Checkpoint.Backup) &&
                     after.Shape == PayloadNamespaceShape.BackupOnly &&
                     SameDirectoryUnchanged(
                        before.Checkpoint.Backup,
                        after.Checkpoint.Backup));
            }
            else
            {
                valid =
                    (IsReplacementOperation(authority.Operation) &&
                     before.Shape ==
                        PayloadNamespaceShape.CurrentAndBackup &&
                     authority.Target != null &&
                     authority.Target.Matches(
                        before.Checkpoint.Current) &&
                     authority.Baseline != null &&
                     authority.Baseline.Matches(
                        before.Checkpoint.Backup) &&
                     after.Shape ==
                        PayloadNamespaceShape.CurrentOnly &&
                     SameDirectoryUnchanged(
                        before.Checkpoint.Current,
                        after.Checkpoint.Current)) ||
                    (authority.Operation ==
                        InstallOperation.Uninstall &&
                     before.Shape ==
                        PayloadNamespaceShape.BackupOnly &&
                     authority.Baseline != null &&
                     authority.Baseline.Matches(
                        before.Checkpoint.Backup) &&
                     after.Shape == PayloadNamespaceShape.Empty);
            }
            if (!valid)
            {
                throw new InvalidOperationException(
                    "Payload cleanup receipt has an illegal namespace transition.");
            }
        }

        private static bool CleanupStateAuthorized(
            PayloadRecoveryAuthority authority,
            PayloadCleanupKind kind,
            PayloadNamespaceCheckpoint state)
        {
            if (kind == PayloadCleanupKind.Candidate)
            {
                return
                    (authority.Operation ==
                        InstallOperation.FreshInstall &&
                     state.Shape == PayloadNamespaceShape.CandidateOnly &&
                     authority.Target != null &&
                     authority.Target.Matches(state.Candidate)) ||
                    (IsReplacementOperation(authority.Operation) &&
                     state.Shape ==
                        PayloadNamespaceShape.CurrentAndCandidate &&
                     authority.Baseline != null &&
                     authority.Baseline.Matches(state.Current) &&
                     authority.Target != null &&
                     authority.Target.Matches(state.Candidate)) ||
                    (IsReplacementOperation(authority.Operation) &&
                     state.Shape ==
                        PayloadNamespaceShape.CandidateAndBackup &&
                     authority.Target != null &&
                     authority.Target.Matches(state.Candidate) &&
                     authority.Baseline != null &&
                     authority.Baseline.Matches(state.Backup)) ||
                    CleanupTerminalAuthorized(authority, kind, state);
            }
            return
                (IsReplacementOperation(authority.Operation) &&
                 state.Shape == PayloadNamespaceShape.CurrentAndBackup &&
                 authority.Target != null &&
                 authority.Target.Matches(state.Current) &&
                 authority.Baseline != null &&
                 authority.Baseline.Matches(state.Backup)) ||
                (authority.Operation == InstallOperation.Uninstall &&
                 state.Shape == PayloadNamespaceShape.BackupOnly &&
                 authority.Baseline != null &&
                 authority.Baseline.Matches(state.Backup)) ||
                CleanupTerminalAuthorized(authority, kind, state);
        }

        private static bool CleanupTerminalAuthorized(
            PayloadRecoveryAuthority authority,
            PayloadCleanupKind kind,
            PayloadNamespaceCheckpoint state)
        {
            if (kind == PayloadCleanupKind.CommittedBackup)
            {
                return
                    (IsReplacementOperation(authority.Operation) &&
                     state.Shape == PayloadNamespaceShape.CurrentOnly &&
                     authority.Target != null &&
                     authority.Target.Matches(state.Current)) ||
                    (authority.Operation == InstallOperation.Uninstall &&
                     state.Shape == PayloadNamespaceShape.Empty);
            }
            return
                (authority.Operation == InstallOperation.FreshInstall &&
                 state.Shape == PayloadNamespaceShape.Empty) ||
                (IsReplacementOperation(authority.Operation) &&
                 state.Shape == PayloadNamespaceShape.CurrentOnly &&
                 ((authority.Baseline != null &&
                   authority.Baseline.Matches(state.Current)) ||
                  (authority.Target != null &&
                   authority.Target.Matches(state.Current)))) ||
                (IsReplacementOperation(authority.Operation) &&
                 state.Shape == PayloadNamespaceShape.BackupOnly &&
                 authority.Baseline != null &&
                 authority.Baseline.Matches(state.Backup)) ||
                (IsReplacementOperation(authority.Operation) &&
                 state.Shape == PayloadNamespaceShape.CurrentAndBackup &&
                 authority.Target != null &&
                 authority.Target.Matches(state.Current) &&
                 authority.Baseline != null &&
                 authority.Baseline.Matches(state.Backup)) ||
                (authority.Operation == InstallOperation.Uninstall &&
                 state.Shape == PayloadNamespaceShape.CurrentOnly &&
                 authority.Baseline != null &&
                 authority.Baseline.Matches(state.Current));
        }

        private static void RequireForwardRevision(
            PayloadNamespaceState before,
            PayloadNamespaceState after)
        {
            if (!String.Equals(
                    before.TransactionId,
                    after.TransactionId,
                    StringComparison.Ordinal) ||
                after.Revision <= before.Revision)
            {
                throw new InvalidOperationException(
                    "Payload receipt is cross-transaction or stale.");
            }
        }

        private static bool SameDirectoryAfterRename(
            PayloadDirectoryCheckpoint source,
            PayloadDirectoryCheckpoint destination,
            PayloadDirectorySlot destinationSlot)
        {
            if (source == null || destination == null)
            {
                return false;
            }
            PayloadDirectoryCheckpoint expected = source.DeepClone();
            expected.Slot = destinationSlot;
            return String.Equals(
                expected.InvariantDigest,
                destination.InvariantDigest,
                StringComparison.Ordinal);
        }

        private static bool SameDirectoryUnchanged(
            PayloadDirectoryCheckpoint first,
            PayloadDirectoryCheckpoint second)
        {
            return first != null &&
                second != null &&
                String.Equals(
                    first.InvariantDigest,
                    second.InvariantDigest,
                    StringComparison.Ordinal);
        }

        private static bool IsReplacementOperation(
            InstallOperation operation)
        {
            return operation == InstallOperation.Upgrade ||
                operation == InstallOperation.Repair ||
                operation == InstallOperation.ExplicitDowngrade;
        }
    }

    internal interface IProtectedPayloadStore : IDisposable
    {
        PayloadNamespaceState Inspect();
        PayloadCandidateReceipt Stage(
            ITrustedReleasePayloadSource source,
            TargetPayloadManifest expected);
        PayloadPromotionReceipt PromoteInstall(
            PayloadRecoveryAuthority authority,
            PayloadNamespaceState expected,
            PayloadCandidateReceipt candidate);
        PayloadPromotionReceipt PromoteUninstall(
            PayloadRecoveryAuthority authority,
            PayloadNamespaceState expected);
        PayloadRecoveryReceipt Recover(
            PayloadRecoveryDecision decision,
            PayloadNamespaceState expected);
        PayloadCleanupReceipt Cleanup(
            PayloadCleanupKind kind,
            PayloadNamespaceState expected);
    }
}
