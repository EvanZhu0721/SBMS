using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Xml;

namespace SBMSSetup
{
    internal enum PayloadWorkspaceCheckpointReadSource
    {
        Primary,
        Backup
    }

    internal sealed class PayloadWorkspaceCheckpointReceipt
    {
        internal PayloadWorkspaceCheckpointReceipt(
            PayloadBuildWorkspaceState state,
            string relativePath,
            string displayPath,
            long length,
            string sha256)
        {
            if (state == null ||
                String.IsNullOrWhiteSpace(relativePath) ||
                String.IsNullOrWhiteSpace(displayPath) ||
                length < 0 ||
                String.IsNullOrWhiteSpace(sha256))
            {
                throw new InvalidDataException(
                    "Payload workspace checkpoint receipt is incomplete.");
            }
            PayloadContractValidation.RequireSha256(
                sha256,
                "Payload workspace checkpoint document digest");
            State = new PayloadBuildWorkspaceState(state.Checkpoint);
            RelativePath = relativePath;
            DisplayPath = displayPath;
            Length = length;
            Sha256 = sha256.ToLowerInvariant();
        }

        internal readonly PayloadBuildWorkspaceState State;
        internal readonly string RelativePath;
        internal readonly string DisplayPath;
        internal readonly long Length;
        internal readonly string Sha256;
    }

    internal sealed class PayloadWorkspaceCheckpointReadResult
    {
        internal PayloadWorkspaceCheckpointReceipt Receipt;
        internal PayloadWorkspaceCheckpointReadSource Source;
        internal bool RequiresPrimaryRepair;
    }

    internal sealed class PayloadWorkspaceCheckpointPublicationException
        : IOException
    {
        internal PayloadWorkspaceCheckpointPublicationException(
            bool candidatePublished,
            Exception innerException)
            : base(
                candidatePublished
                    ? "Payload workspace checkpoint committed but exact " +
                      "readback failed."
                    : "Payload workspace checkpoint publication failed " +
                      "before commit.",
                innerException)
        {
            CandidatePublished = candidatePublished;
        }

        internal bool CandidatePublished { get; private set; }
    }

    internal interface IProtectedPayloadWorkspaceCheckpointStore
    {
        PayloadWorkspaceCheckpointReceipt Initialize(
            PayloadBuildWorkspaceCheckpoint candidate);
        PayloadWorkspaceCheckpointReadResult Load();
        PayloadWorkspaceCheckpointReceipt Save(
            PayloadWorkspaceCasToken expected,
            PayloadBuildWorkspaceCheckpoint candidate);
        PayloadWorkspaceCheckpointReceipt RepairPrimary(
            PayloadWorkspaceCheckpointReadResult expectedBackup);
    }

    // Persists only the payload workspace control checkpoint. Program Files
    // tree mutation, identity-bound namespace exclusion and physical crash
    // reconciliation remain responsibilities of the native workspace model.
    internal sealed class ProtectedPayloadWorkspaceCheckpointStore
        : IProtectedPayloadWorkspaceCheckpointStore
    {
        private readonly IAtomicJournalFileSystem fileSystem;
        private readonly ITransactionLeaseCoordinator leaseCoordinator;
        private readonly string transactionId;
        private readonly string authorityDigest;
        private readonly Paths paths;

        internal ProtectedPayloadWorkspaceCheckpointStore(
            IAtomicJournalFileSystem fileSystem,
            ITransactionLeaseCoordinator leaseCoordinator,
            string transactionId,
            string recoveryAuthorityInvariantDigest)
        {
            if (fileSystem == null)
            {
                throw new ArgumentNullException("fileSystem");
            }
            if (leaseCoordinator == null)
            {
                throw new ArgumentNullException("leaseCoordinator");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                transactionId,
                "Payload workspace transaction ID");
            PayloadContractValidation.RequireSha256(
                recoveryAuthorityInvariantDigest,
                "Payload workspace authority digest");
            this.fileSystem = fileSystem;
            this.leaseCoordinator = leaseCoordinator;
            this.transactionId = transactionId;
            authorityDigest =
                recoveryAuthorityInvariantDigest.ToLowerInvariant();
            paths = Paths.For(transactionId);
        }

        public PayloadWorkspaceCheckpointReceipt Initialize(
            PayloadBuildWorkspaceCheckpoint candidate)
        {
            leaseCoordinator.DemandHeld();
            PayloadBuildWorkspaceCheckpoint snapshot =
                ValidateAndClone(candidate);
            if (TryLoad() != null)
            {
                throw new InvalidDataException(
                    "Payload workspace checkpoint already exists.");
            }
            return Publish(snapshot, false);
        }

        public PayloadWorkspaceCheckpointReadResult Load()
        {
            leaseCoordinator.DemandHeld();
            PayloadWorkspaceCheckpointReadResult result = TryLoad();
            if (result == null)
            {
                throw new FileNotFoundException(
                    "Payload workspace checkpoint was not found.");
            }
            return result;
        }

        public PayloadWorkspaceCheckpointReceipt Save(
            PayloadWorkspaceCasToken expected,
            PayloadBuildWorkspaceCheckpoint candidate)
        {
            leaseCoordinator.DemandHeld();
            if (expected == null)
            {
                throw new ArgumentNullException("expected");
            }
            expected.Validate();
            PayloadBuildWorkspaceCheckpoint snapshot =
                ValidateAndClone(candidate);
            PayloadWorkspaceCheckpointReadResult current = TryLoad();
            if (current == null ||
                current.Source !=
                    PayloadWorkspaceCheckpointReadSource.Primary ||
                current.RequiresPrimaryRepair)
            {
                throw new InvalidDataException(
                    "A valid primary payload workspace checkpoint is required.");
            }
            current.Receipt.State.RequireCas(expected);
            PayloadBuildWorkspaceCheckpoint before =
                current.Receipt.State.Checkpoint;
            if (snapshot.Revision != checked(before.Revision + 1))
            {
                throw new InvalidDataException(
                    "Payload workspace checkpoint revision is not the next revision.");
            }
            RequireImmutableIdentity(before, snapshot);
            return Publish(snapshot, true);
        }

        public PayloadWorkspaceCheckpointReceipt RepairPrimary(
            PayloadWorkspaceCheckpointReadResult expectedBackup)
        {
            leaseCoordinator.DemandHeld();
            ReceiptToken expected = Capture(expectedBackup);
            PayloadWorkspaceCheckpointReadResult current = TryLoad();
            if (expected == null ||
                expectedBackup.Source !=
                    PayloadWorkspaceCheckpointReadSource.Backup ||
                !expectedBackup.RequiresPrimaryRepair ||
                current == null ||
                current.Source !=
                    PayloadWorkspaceCheckpointReadSource.Backup ||
                !current.RequiresPrimaryRepair ||
                !Matches(current.Receipt, expected))
            {
                throw new InvalidDataException(
                    "Primary repair requires the expected valid backup checkpoint.");
            }
            PayloadBuildWorkspaceCheckpoint repaired =
                current.Receipt.State.Checkpoint;
            repaired.Revision = checked(repaired.Revision + 1);
            repaired.RecoveryGeneration =
                checked(repaired.RecoveryGeneration + 1);
            if (fileSystem.FileExists(paths.Primary))
            {
                fileSystem.DeleteFile(paths.Primary);
            }
            return Publish(repaired, false);
        }

        private PayloadWorkspaceCheckpointReadResult TryLoad()
        {
            var publisher = new AtomicDocumentBytePublisher(
                fileSystem,
                paths.Primary);
            Exception primaryFailure = null;
            if (publisher.PrimaryExists)
            {
                try
                {
                    PayloadWorkspaceCheckpointReadResult primary =
                        Result(
                        Receipt(paths.Primary, publisher.ReadPrimary()),
                        PayloadWorkspaceCheckpointReadSource.Primary,
                        false);
                    RejectDetectableRevisionInversion(
                        publisher,
                        primary.Receipt);
                    return primary;
                }
                catch (AtomicDocumentFormatException failure)
                {
                    primaryFailure = failure;
                }
            }
            if (publisher.BackupExists)
            {
                try
                {
                    return Result(
                        Receipt(paths.Backup, publisher.ReadBackup()),
                        PayloadWorkspaceCheckpointReadSource.Backup,
                        true);
                }
                catch (AtomicDocumentFormatException backupFailure)
                {
                    throw new InvalidDataException(
                        "Both primary and backup payload workspace " +
                        "checkpoints are invalid.",
                        new AggregateException(
                            primaryFailure,
                            backupFailure));
                }
            }
            if (primaryFailure != null)
            {
                throw new InvalidDataException(
                    "Primary payload workspace checkpoint is invalid and " +
                    "no backup exists.",
                    primaryFailure);
            }
            return null;
        }

        private PayloadWorkspaceCheckpointReceipt Publish(
            PayloadBuildWorkspaceCheckpoint checkpoint,
            bool replace)
        {
            PayloadBuildWorkspaceCheckpoint snapshot =
                ValidateAndClone(checkpoint);
            byte[] bytes = Serialize(snapshot);
            fileSystem.EnsureDirectory(paths.Directory);
            var publisher = new AtomicDocumentBytePublisher(
                fileSystem,
                paths.Primary);
            AtomicDocumentReadResult persisted;
            try
            {
                persisted = publisher.Publish(
                    bytes,
                    replace,
                    null,
                    null,
                    null);
            }
            catch (JournalFilePublicationException failure)
            {
                throw new PayloadWorkspaceCheckpointPublicationException(
                    failure.CandidatePublished,
                    failure);
            }
            PayloadWorkspaceCheckpointReceipt receipt =
                Receipt(paths.Primary, persisted);
            if (!String.Equals(
                    snapshot.InvariantDigest,
                    receipt.State.InvariantDigest,
                    StringComparison.Ordinal) ||
                snapshot.Revision != receipt.State.Revision)
            {
                throw new InvalidDataException(
                    "Payload workspace checkpoint exact readback changed.");
            }
            return receipt;
        }

        private void RejectDetectableRevisionInversion(
            AtomicDocumentBytePublisher publisher,
            PayloadWorkspaceCheckpointReceipt primary)
        {
            if (!publisher.BackupExists)
            {
                return;
            }
            PayloadWorkspaceCheckpointReceipt backup;
            try
            {
                backup = Receipt(paths.Backup, publisher.ReadBackup());
            }
            catch (AtomicDocumentFormatException)
            {
                // A valid primary remains authoritative. The next successful
                // replace rotates it over an unusable backup.
                return;
            }
            if (backup.State.Revision >= primary.State.Revision)
            {
                throw new InvalidDataException(
                    "Payload workspace primary/backup revisions are inverted.");
            }
        }

        private PayloadWorkspaceCheckpointReceipt Receipt(
            string relativePath,
            AtomicDocumentReadResult document)
        {
            string displayPath = fileSystem.GetDisplayPath(relativePath);
            try
            {
                if (document == null ||
                    document.Bytes == null ||
                    String.IsNullOrWhiteSpace(document.Sha256))
                {
                    throw new InvalidDataException(
                        "Payload workspace checkpoint document is incomplete.");
                }
                PayloadBuildWorkspaceCheckpoint checkpoint =
                    Deserialize(document.Bytes);
                ValidateBoundIdentity(checkpoint);
                return new PayloadWorkspaceCheckpointReceipt(
                    new PayloadBuildWorkspaceState(checkpoint),
                    relativePath,
                    displayPath,
                    document.Bytes.LongLength,
                    document.Sha256);
            }
            catch (AtomicDocumentFormatException)
            {
                throw;
            }
            catch (Exception failure)
            {
                if (failure is OutOfMemoryException ||
                    failure is AccessViolationException ||
                    failure is ThreadAbortException)
                {
                    throw;
                }
                throw new AtomicDocumentFormatException(
                    "Payload workspace checkpoint document is malformed or invalid.",
                    failure);
            }
        }

        private PayloadBuildWorkspaceCheckpoint ValidateAndClone(
            PayloadBuildWorkspaceCheckpoint checkpoint)
        {
            ValidateBoundIdentity(checkpoint);
            return checkpoint.DeepClone();
        }

        private void ValidateBoundIdentity(
            PayloadBuildWorkspaceCheckpoint checkpoint)
        {
            if (checkpoint == null)
            {
                throw new InvalidDataException(
                    "Payload workspace checkpoint is missing.");
            }
            checkpoint.Validate();
            if (!String.Equals(
                    checkpoint.TransactionId,
                    transactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    checkpoint.RecoveryAuthorityInvariantDigest,
                    authorityDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Payload workspace checkpoint belongs to another transaction.");
            }
        }

        private static void RequireImmutableIdentity(
            PayloadBuildWorkspaceCheckpoint before,
            PayloadBuildWorkspaceCheckpoint after)
        {
            if (!String.Equals(
                    before.TransactionId,
                    after.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    before.RecoveryAuthorityInvariantDigest,
                    after.RecoveryAuthorityInvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    before.NamespaceRoot.InvariantDigest,
                    after.NamespaceRoot.InvariantDigest,
                    StringComparison.Ordinal) ||
                before.RecoveryGeneration != after.RecoveryGeneration)
            {
                throw new InvalidDataException(
                    "Payload workspace immutable identity changed.");
            }
        }

        private static byte[] Serialize(
            PayloadBuildWorkspaceCheckpoint checkpoint)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(PayloadBuildWorkspaceCheckpoint));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, checkpoint);
                return stream.ToArray();
            }
        }

        private static PayloadBuildWorkspaceCheckpoint Deserialize(
            byte[] bytes)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(PayloadBuildWorkspaceCheckpoint));
            using (XmlDictionaryReader reader =
                JsonReaderWriterFactory.CreateJsonReader(
                    bytes,
                    XmlDictionaryReaderQuotas.Max))
            {
                var checkpoint =
                    serializer.ReadObject(reader) as
                        PayloadBuildWorkspaceCheckpoint;
                if (checkpoint == null ||
                    reader.MoveToContent() != XmlNodeType.None)
                {
                    throw new InvalidDataException(
                        "Payload workspace checkpoint JSON is incomplete.");
                }
                byte[] canonical = Serialize(checkpoint);
                if (!BytesEqual(canonical, bytes))
                {
                    throw new InvalidDataException(
                        "Payload workspace checkpoint JSON is not canonical.");
                }
                return checkpoint;
            }
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (first == null ||
                second == null ||
                first.Length != second.Length)
            {
                return false;
            }
            for (int index = 0; index < first.Length; ++index)
            {
                if (first[index] != second[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static PayloadWorkspaceCheckpointReadResult Result(
            PayloadWorkspaceCheckpointReceipt receipt,
            PayloadWorkspaceCheckpointReadSource source,
            bool repair)
        {
            return new PayloadWorkspaceCheckpointReadResult
            {
                Receipt = receipt,
                Source = source,
                RequiresPrimaryRepair = repair
            };
        }

        private static ReceiptToken Capture(
            PayloadWorkspaceCheckpointReadResult result)
        {
            if (result == null || result.Receipt == null)
            {
                return null;
            }
            return new ReceiptToken
            {
                TransactionId =
                    result.Receipt.State.Checkpoint.TransactionId,
                Revision = result.Receipt.State.Revision,
                WorkspaceInvariantDigest =
                    result.Receipt.State.InvariantDigest,
                RelativePath = result.Receipt.RelativePath,
                Length = result.Receipt.Length,
                Sha256 = result.Receipt.Sha256
            };
        }

        private static bool Matches(
            PayloadWorkspaceCheckpointReceipt actual,
            ReceiptToken expected)
        {
            return actual != null &&
                expected != null &&
                actual.State.Revision == expected.Revision &&
                actual.Length == expected.Length &&
                String.Equals(
                    actual.State.Checkpoint.TransactionId,
                    expected.TransactionId,
                    StringComparison.Ordinal) &&
                String.Equals(
                    actual.State.InvariantDigest,
                    expected.WorkspaceInvariantDigest,
                    StringComparison.Ordinal) &&
                String.Equals(
                    actual.RelativePath,
                    expected.RelativePath,
                    StringComparison.Ordinal) &&
                String.Equals(
                    actual.Sha256,
                    expected.Sha256,
                    StringComparison.Ordinal);
        }

        private sealed class ReceiptToken
        {
            internal string TransactionId;
            internal long Revision;
            internal string WorkspaceInvariantDigest;
            internal string RelativePath;
            internal long Length;
            internal string Sha256;
        }

        private sealed class Paths
        {
            internal string Directory;
            internal string Primary;
            internal string Backup;

            internal static Paths For(string transactionId)
            {
                string directory = Path.Combine(
                    "transactions",
                    transactionId,
                    "payload");
                string primary = Path.Combine(
                    directory,
                    "workspace.json");
                return new Paths
                {
                    Directory = directory,
                    Primary = primary,
                    Backup = primary + ".bak"
                };
            }
        }
    }
}
