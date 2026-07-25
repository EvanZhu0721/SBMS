using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SBMSSetup
{
    internal interface ITransactionLeaseCoordinator
    {
        IDisposable Acquire();
        void DemandHeld();
    }

    internal sealed class InstanceTransactionLeaseCoordinator
        : ITransactionLeaseCoordinator
    {
        private readonly IInstallerTransactionMutexFactory mutexFactory;
        private readonly string mutexName;
        private readonly TimeSpan timeout;
        private readonly object sync = new object();
        private readonly Stack<long> leaseIds = new Stack<long>();
        private Mutex ownedMutex;
        private Thread ownerThread;
        private long nextLeaseId;
        private bool poisoned;

        internal InstanceTransactionLeaseCoordinator(
            IInstallerTransactionMutexFactory mutexFactory,
            string mutexName,
            TimeSpan timeout)
        {
            if (mutexFactory == null)
            {
                throw new ArgumentNullException("mutexFactory");
            }
            if (String.IsNullOrWhiteSpace(mutexName))
            {
                throw new ArgumentException(
                    "Transaction mutex name is required.",
                    "mutexName");
            }
            if (timeout <= TimeSpan.Zero ||
                timeout.TotalMilliseconds > Int32.MaxValue)
            {
                throw new ArgumentOutOfRangeException("timeout");
            }
            this.mutexFactory = mutexFactory;
            this.mutexName = mutexName;
            this.timeout = timeout;
        }

        public IDisposable Acquire()
        {
            Thread currentThread = Thread.CurrentThread;
            lock (sync)
            {
                if (poisoned)
                {
                    throw new InvalidOperationException(
                        "The installer transaction lease coordinator is poisoned.");
                }
                if (ownedMutex != null &&
                    Object.ReferenceEquals(ownerThread, currentThread))
                {
                    long nestedId = checked(++nextLeaseId);
                    leaseIds.Push(nestedId);
                    return new Lease(this, nestedId, currentThread);
                }
            }

            Mutex candidate = mutexFactory.OpenOrCreate(mutexName);
            bool acquired = false;
            bool abandoned = false;
            try
            {
                try
                {
                    acquired = candidate.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                    abandoned = true;
                }
                if (!acquired)
                {
                    throw new TimeoutException(
                        "Timed out waiting for the installer transaction lease.");
                }
                lock (sync)
                {
                    if (ownedMutex != null)
                    {
                        if (!abandoned)
                        {
                            throw new InvalidOperationException(
                                "Transaction lease coordinator ownership is inconsistent.");
                        }
                        // A thread that terminated without Dispose leaves
                        // process-local bookkeeping behind even though Windows
                        // correctly transfers the abandoned named mutex.
                        ownedMutex.Dispose();
                        ownedMutex = null;
                        ownerThread = null;
                        leaseIds.Clear();
                        poisoned = true;
                        throw new InvalidOperationException(
                            "An in-process transaction lease owner terminated " +
                            "without releasing the lease.");
                    }
                    ownedMutex = candidate;
                    ownerThread = currentThread;
                    long leaseId = checked(++nextLeaseId);
                    leaseIds.Push(leaseId);
                    return new Lease(this, leaseId, currentThread);
                }
            }
            catch
            {
                if (acquired)
                {
                    candidate.ReleaseMutex();
                }
                candidate.Dispose();
                throw;
            }
        }

        public void DemandHeld()
        {
            Thread currentThread = Thread.CurrentThread;
            lock (sync)
            {
                if (poisoned ||
                    ownedMutex == null ||
                    !Object.ReferenceEquals(ownerThread, currentThread) ||
                    leaseIds.Count == 0)
                {
                    throw new InvalidOperationException(
                        "The current thread does not hold this installer " +
                        "transaction lease.");
                }
            }
        }

        private void Release(long leaseId, Thread thread)
        {
            Mutex release = null;
            lock (sync)
            {
                if (ownedMutex == null ||
                    !Object.ReferenceEquals(ownerThread, thread) ||
                    !Object.ReferenceEquals(Thread.CurrentThread, thread) ||
                    leaseIds.Count == 0 ||
                    leaseIds.Peek() != leaseId)
                {
                    throw new InvalidOperationException(
                        "Transaction leases must be released on the owning " +
                        "thread in reverse acquisition order.");
                }
                leaseIds.Pop();
                if (leaseIds.Count == 0)
                {
                    release = ownedMutex;
                    ownedMutex = null;
                    ownerThread = null;
                }
            }
            if (release != null)
            {
                release.ReleaseMutex();
                release.Dispose();
            }
        }

        private sealed class Lease : IDisposable
        {
            private InstanceTransactionLeaseCoordinator owner;
            private readonly long leaseId;
            private readonly Thread thread;

            internal Lease(
                InstanceTransactionLeaseCoordinator owner,
                long leaseId,
                Thread thread)
            {
                this.owner = owner;
                this.leaseId = leaseId;
                this.thread = thread;
            }

            public void Dispose()
            {
                InstanceTransactionLeaseCoordinator current = owner;
                if (current == null)
                {
                    return;
                }
                current.Release(leaseId, thread);
                owner = null;
            }
        }
    }

    internal interface IEscrowContentVerifier
    {
        void Verify(
            string transactionId,
            string escrowDirectoryRelativePath,
            EscrowManifest manifest);
    }

    internal sealed class AnchoredEscrowContentVerifier
        : IEscrowContentVerifier
    {
        private readonly IAtomicJournalFileSystem fileSystem;

        internal AnchoredEscrowContentVerifier(
            IAtomicJournalFileSystem fileSystem)
        {
            if (fileSystem == null)
            {
                throw new ArgumentNullException("fileSystem");
            }
            this.fileSystem = fileSystem;
        }

        public void Verify(
            string transactionId,
            string escrowDirectoryRelativePath,
            EscrowManifest manifest)
        {
            if (String.IsNullOrWhiteSpace(transactionId) ||
                String.IsNullOrWhiteSpace(escrowDirectoryRelativePath) ||
                manifest == null)
            {
                throw new InvalidDataException(
                    "Escrow content verification input is incomplete.");
            }
            ProtectedEscrowManifestStore.VerifyContentWithAnchoredIo(
                fileSystem,
                escrowDirectoryRelativePath,
                manifest);
        }
    }

    internal enum EscrowManifestReadSource
    {
        Primary,
        Backup
    }

    internal sealed class EscrowManifestReceipt
    {
        internal readonly EscrowManifest Manifest;
        internal readonly string RelativePath;
        internal readonly string DisplayPath;
        internal readonly long Length;
        internal readonly string Sha256;
        internal readonly int CommittedRevision;
        internal readonly string CommittedTransactionId;

        internal EscrowManifestReceipt(
            EscrowManifest manifest,
            string relativePath,
            string displayPath,
            long length,
            string sha256)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException("manifest");
            }
            Manifest = manifest;
            RelativePath = relativePath;
            DisplayPath = displayPath;
            Length = length;
            Sha256 = sha256;
            CommittedRevision = manifest.Revision;
            CommittedTransactionId = manifest.TransactionId;
        }
    }

    internal sealed class EscrowManifestReadResult
    {
        internal EscrowManifestReceipt Receipt;
        internal EscrowManifestReadSource Source;
        internal bool RequiresPrimaryRepair;
    }

    internal interface IProtectedEscrowManifestStore
    {
        EscrowManifestReceipt Initialize(EscrowManifest candidate);
        EscrowManifestReadResult Load();
        EscrowManifestReceipt Save(
            EscrowManifestReceipt expected,
            EscrowManifest candidate);
        EscrowManifestReceipt RepairPrimary(
            EscrowManifestReadResult expectedBackup);
        EscrowManifestReceipt SealForRollback(
            EscrowManifestReceipt expected,
            EscrowManifest candidate);
    }

    internal sealed class ProtectedEscrowManifestStore
        : IProtectedEscrowManifestStore
    {
        private readonly IAtomicJournalFileSystem fileSystem;
        private readonly ITransactionLeaseCoordinator leaseCoordinator;
        private readonly IEscrowContentVerifier contentVerifier;
        private readonly string transactionId;
        private readonly Paths paths;

        internal ProtectedEscrowManifestStore(
            IAtomicJournalFileSystem fileSystem,
            ITransactionLeaseCoordinator leaseCoordinator,
            IEscrowContentVerifier contentVerifier,
            string transactionId)
        {
            if (fileSystem == null)
            {
                throw new ArgumentNullException("fileSystem");
            }
            if (leaseCoordinator == null)
            {
                throw new ArgumentNullException("leaseCoordinator");
            }
            if (contentVerifier == null)
            {
                throw new ArgumentNullException("contentVerifier");
            }
            ValidateTransactionId(transactionId);
            this.fileSystem = fileSystem;
            this.leaseCoordinator = leaseCoordinator;
            this.contentVerifier = contentVerifier;
            this.transactionId = transactionId;
            paths = Paths.For(transactionId);
        }

        public EscrowManifestReceipt Initialize(EscrowManifest candidate)
        {
            leaseCoordinator.DemandHeld();
            EscrowManifest snapshot = Clone(candidate);
            EscrowManifestReadResult existing =
                TryLoad();
            if (existing != null)
            {
                throw new InvalidDataException(
                    "Escrow manifest already exists for this transaction.");
            }
            if (snapshot.Revision != 1 ||
                snapshot.Sealed ||
                snapshot.RetentionState != EscrowRetentionState.Building)
            {
                throw new InvalidDataException(
                    "Initial escrow manifest must be unsealed revision one.");
            }
            ValidateCandidateIdentity(transactionId, snapshot);
            return Publish(paths, snapshot, false);
        }

        public EscrowManifestReadResult Load()
        {
            leaseCoordinator.DemandHeld();
            EscrowManifestReadResult result = TryLoad();
            if (result == null)
            {
                throw new FileNotFoundException(
                    "Escrow manifest was not found.");
            }
            return result;
        }

        public EscrowManifestReceipt Save(
            EscrowManifestReceipt expected,
            EscrowManifest candidate)
        {
            leaseCoordinator.DemandHeld();
            EscrowManifest snapshot = Clone(candidate);
            EscrowManifestReadResult current = RequirePrimary(expected);
            ValidateCandidateIdentity(transactionId, snapshot);
            if (snapshot.Revision !=
                checked(current.Receipt.Manifest.Revision + 1))
            {
                throw new InvalidDataException(
                    "Escrow manifest revision is not the next committed revision.");
            }
            ValidateIdentityInvariant(
                current.Receipt.Manifest,
                snapshot);
            ValidateTransition(
                current.Receipt.Manifest,
                snapshot,
                false);
            return Publish(paths, snapshot, true);
        }

        public EscrowManifestReceipt RepairPrimary(
            EscrowManifestReadResult expectedBackup)
        {
            leaseCoordinator.DemandHeld();
            EscrowManifestReadSource expectedSource =
                expectedBackup == null
                    ? EscrowManifestReadSource.Primary
                    : expectedBackup.Source;
            bool expectedRequiresRepair =
                expectedBackup != null &&
                expectedBackup.RequiresPrimaryRepair;
            ReceiptToken expectedToken = CaptureReceipt(
                expectedBackup == null
                    ? null
                    : expectedBackup.Receipt);
            EscrowManifestReadResult current = TryLoad();
            if (expectedBackup == null ||
                expectedToken == null ||
                expectedSource != EscrowManifestReadSource.Backup ||
                !expectedRequiresRepair ||
                current == null ||
                current.Source != EscrowManifestReadSource.Backup ||
                !current.RequiresPrimaryRepair ||
                !ReceiptMatches(
                    current.Receipt,
                    expectedToken))
            {
                throw new InvalidDataException(
                    "Primary repair requires the expected valid backup revision.");
            }
            EscrowManifest repaired = Clone(current.Receipt.Manifest);
            repaired.Revision = checked(
                current.Receipt.Manifest.Revision + 1);
            if (repaired.Sealed)
            {
                VerifyContentWithAnchoredIo(
                    fileSystem,
                    paths.Directory,
                    repaired);
                contentVerifier.Verify(
                    transactionId,
                    paths.Directory,
                    Clone(repaired));
            }
            // Keep the known-good backup intact. A corrupt primary is not a
            // valid predecessor and must never replace the recovery copy.
            if (fileSystem.FileExists(paths.Primary))
            {
                fileSystem.DeleteFile(paths.Primary);
            }
            return Publish(paths, repaired, false);
        }

        public EscrowManifestReceipt SealForRollback(
            EscrowManifestReceipt expected,
            EscrowManifest candidate)
        {
            leaseCoordinator.DemandHeld();
            EscrowManifest snapshot = Clone(candidate);
            EscrowManifestReadResult current = RequirePrimary(expected);
            ValidateCandidateIdentity(transactionId, snapshot);
            if (snapshot.Revision !=
                    checked(current.Receipt.Manifest.Revision + 1) ||
                !snapshot.Sealed ||
                snapshot.RetentionState !=
                    EscrowRetentionState.SealedForRollback)
            {
                throw new InvalidDataException(
                    "Rollback seal must be the next sealed rollback revision.");
            }
            ValidateIdentityInvariant(
                current.Receipt.Manifest,
                snapshot);
            ValidateTransition(
                current.Receipt.Manifest,
                snapshot,
                true);
            VerifyContentWithAnchoredIo(
                fileSystem,
                paths.Directory,
                snapshot);
            contentVerifier.Verify(
                transactionId,
                paths.Directory,
                Clone(snapshot));
            EscrowManifestReceipt first = Publish(
                paths,
                snapshot,
                true);
            EscrowManifest replica = Clone(first.Manifest);
            replica.Revision = checked(first.Manifest.Revision + 1);
            ValidateTransition(first.Manifest, replica, true);
            return Publish(paths, replica, true);
        }

        internal static void VerifyContentWithAnchoredIo(
            IAtomicJournalFileSystem fileSystem,
            string escrowDirectoryRelativePath,
            EscrowManifest manifest)
        {
            if (fileSystem == null)
            {
                throw new ArgumentNullException("fileSystem");
            }
            foreach (EscrowContentEntry entry in manifest.Content)
            {
                entry.Validate();
                string relativePath = Path.Combine(
                    escrowDirectoryRelativePath,
                    entry.StorageRelativePath);
                long length = 0;
                string sha256;
                using (Stream stream =
                    fileSystem.OpenReadFile(relativePath))
                using (SHA256 algorithm = SHA256.Create())
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(
                        buffer,
                        0,
                        buffer.Length)) != 0)
                    {
                        length = checked(length + read);
                        algorithm.TransformBlock(
                            buffer,
                            0,
                            read,
                            buffer,
                            0);
                    }
                    algorithm.TransformFinalBlock(
                        new byte[0],
                        0,
                        0);
                    sha256 = Hex(algorithm.Hash);
                }
                if (length != entry.Length ||
                    !String.Equals(
                        sha256,
                        entry.Sha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Escrow content readback does not match its manifest.");
                }
            }
        }

        private EscrowManifestReadResult RequirePrimary(
            EscrowManifestReceipt expected)
        {
            ReceiptToken expectedToken = CaptureReceipt(expected);
            if (expectedToken == null ||
                !String.Equals(
                    expectedToken.RelativePath,
                    paths.Primary,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    expectedToken.TransactionId,
                    transactionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Escrow manifest compare-and-swap receipt is invalid.");
            }
            EscrowManifestReadResult result = TryLoad();
            if (result == null ||
                result.Source != EscrowManifestReadSource.Primary ||
                result.RequiresPrimaryRepair)
            {
                throw new InvalidDataException(
                    "A valid primary escrow manifest is required.");
            }
            if (!ReceiptMatches(result.Receipt, expectedToken))
            {
                throw new InvalidDataException(
                    "Escrow manifest compare-and-swap revision is stale.");
            }
            return result;
        }

        private static bool ReceiptMatches(
            EscrowManifestReceipt actual,
            ReceiptToken expected)
        {
            return actual != null &&
                expected != null &&
                actual.Manifest != null &&
                actual.CommittedRevision == expected.Revision &&
                actual.Length == expected.Length &&
                String.Equals(
                    actual.RelativePath,
                    expected.RelativePath,
                    StringComparison.Ordinal) &&
                String.Equals(
                    actual.Sha256,
                    expected.Sha256,
                    StringComparison.Ordinal);
        }

        private static ReceiptToken CaptureReceipt(
            EscrowManifestReceipt receipt)
        {
            if (receipt == null || receipt.Manifest == null)
            {
                return null;
            }
            return new ReceiptToken
            {
                Revision = receipt.CommittedRevision,
                TransactionId = receipt.CommittedTransactionId,
                RelativePath = receipt.RelativePath,
                Length = receipt.Length,
                Sha256 = receipt.Sha256
            };
        }

        private EscrowManifestReadResult TryLoad()
        {
            var publisher = new AtomicDocumentBytePublisher(
                fileSystem,
                paths.Primary);
            Exception primaryFailure = null;
            if (publisher.PrimaryExists)
            {
                try
                {
                    return Result(
                        Receipt(
                            transactionId,
                            paths.Primary,
                            publisher.ReadPrimary()),
                        EscrowManifestReadSource.Primary,
                        false);
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
                        Receipt(
                            transactionId,
                            paths.Backup,
                            publisher.ReadBackup()),
                        EscrowManifestReadSource.Backup,
                        true);
                }
                catch (AtomicDocumentFormatException backupFailure)
                {
                    throw new InvalidDataException(
                        "Both primary and backup escrow manifests are invalid.",
                        new AggregateException(
                            primaryFailure,
                            backupFailure));
                }
            }
            if (primaryFailure != null)
            {
                throw new InvalidDataException(
                    "Primary escrow manifest is invalid and no backup exists.",
                    primaryFailure);
            }
            return null;
        }

        private EscrowManifestReceipt Publish(
            Paths paths,
            EscrowManifest candidate,
            bool replace)
        {
            ValidateCandidateIdentity(transactionId, candidate);
            byte[] bytes = Serialize(candidate);
            fileSystem.EnsureDirectory(paths.Directory);
            var publisher = new AtomicDocumentBytePublisher(
                fileSystem,
                paths.Primary);
            AtomicDocumentReadResult persisted = publisher.Publish(
                bytes,
                replace,
                null,
                null,
                null);
            return Receipt(
                transactionId,
                paths.Primary,
                persisted);
        }

        private EscrowManifestReceipt Receipt(
            string expectedTransactionId,
            string relativePath,
            AtomicDocumentReadResult document)
        {
            // Resolve the native-anchor display path outside the format
            // boundary. ACL, sharing and other filesystem failures must never
            // be reclassified as recoverable document corruption.
            string displayPath = fileSystem.GetDisplayPath(relativePath);
            try
            {
                if (document == null ||
                    document.Bytes == null ||
                    String.IsNullOrWhiteSpace(document.Sha256))
                {
                    throw new InvalidDataException(
                        "Escrow manifest document receipt is incomplete.");
                }
                EscrowManifest manifest = Deserialize(document.Bytes);
                ValidateCandidateIdentity(expectedTransactionId, manifest);
                return new EscrowManifestReceipt(
                    manifest,
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
                    "Escrow manifest document is malformed or invalid.",
                    failure);
            }
        }

        private static EscrowManifestReadResult Result(
            EscrowManifestReceipt receipt,
            EscrowManifestReadSource source,
            bool requiresRepair)
        {
            return new EscrowManifestReadResult
            {
                Receipt = receipt,
                Source = source,
                RequiresPrimaryRepair = requiresRepair
            };
        }

        private static void ValidateCandidateIdentity(
            string expectedTransactionId,
            EscrowManifest manifest)
        {
            if (manifest == null)
            {
                throw new InvalidDataException(
                    "Escrow manifest is missing.");
            }
            manifest.Validate();
            if (!String.Equals(
                expectedTransactionId,
                manifest.TransactionId,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Escrow manifest transaction identity is inconsistent.");
            }
        }

        private static void ValidateIdentityInvariant(
            EscrowManifest previous,
            EscrowManifest candidate)
        {
            bool targetsMatch =
                previous.Target == null && candidate.Target == null;
            if (previous.Target != null && candidate.Target != null)
            {
                targetsMatch =
                    String.Equals(
                        previous.Target.Version,
                        candidate.Target.Version,
                        StringComparison.Ordinal) &&
                    String.Equals(
                        previous.Target.PackageFingerprint,
                        candidate.Target.PackageFingerprint,
                        StringComparison.Ordinal);
            }
            if (!String.Equals(
                    previous.TransactionId,
                    candidate.TransactionId,
                    StringComparison.Ordinal) ||
                previous.SchemaVersion != candidate.SchemaVersion ||
                previous.Operation != candidate.Operation ||
                !String.Equals(
                    previous.BaselineEvidenceDigest,
                    candidate.BaselineEvidenceDigest,
                    StringComparison.Ordinal) ||
                previous.BaselinePayloadState !=
                    candidate.BaselinePayloadState ||
                !targetsMatch)
            {
                throw new InvalidDataException(
                    "Escrow manifest immutable identity changed.");
            }
            if (previous.Sealed &&
                (!String.Equals(
                    previous.SealedUtc,
                    candidate.SealedUtc,
                    StringComparison.Ordinal) ||
                 !ContentEquals(previous.Content, candidate.Content)))
            {
                throw new InvalidDataException(
                    "Sealed escrow manifest identity or content changed.");
            }
        }

        private static void ValidateTransition(
            EscrowManifest previous,
            EscrowManifest candidate,
            bool sealing)
        {
            if (sealing &&
                !previous.Sealed &&
                previous.RetentionState == EscrowRetentionState.Building &&
                candidate.Sealed &&
                candidate.RetentionState ==
                    EscrowRetentionState.SealedForRollback)
            {
                return;
            }
            if (sealing &&
                previous.Sealed &&
                candidate.Sealed &&
                previous.RetentionState ==
                    EscrowRetentionState.SealedForRollback &&
                candidate.RetentionState ==
                    EscrowRetentionState.SealedForRollback &&
                ManifestPayloadEquals(previous, candidate))
            {
                return;
            }
            if (!sealing &&
                !previous.Sealed &&
                !candidate.Sealed &&
                previous.RetentionState == EscrowRetentionState.Building &&
                candidate.RetentionState == EscrowRetentionState.Building)
            {
                return;
            }
            if (!sealing &&
                previous.Sealed &&
                candidate.Sealed &&
                ((previous.RetentionState ==
                      EscrowRetentionState.SealedForRollback &&
                  candidate.RetentionState ==
                      EscrowRetentionState.FinalizationPending) ||
                 (previous.RetentionState ==
                      EscrowRetentionState.FinalizationPending &&
                  (candidate.RetentionState ==
                       EscrowRetentionState.Finalized ||
                   candidate.RetentionState ==
                       EscrowRetentionState.RetainedAfterCleanupFailure))))
            {
                return;
            }
            throw new InvalidDataException(
                "Escrow manifest retention transition is invalid.");
        }

        private static bool ManifestPayloadEquals(
            EscrowManifest left,
            EscrowManifest right)
        {
            return left.Sealed == right.Sealed &&
                String.Equals(
                    left.SealedUtc,
                    right.SealedUtc,
                    StringComparison.Ordinal) &&
                left.RetentionState == right.RetentionState &&
                String.Equals(
                    left.FinalizationEvidence,
                    right.FinalizationEvidence,
                    StringComparison.Ordinal) &&
                ContentEquals(left.Content, right.Content);
        }

        private static bool ContentEquals(
            IList<EscrowContentEntry> left,
            IList<EscrowContentEntry> right)
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }
            for (int index = 0; index < left.Count; ++index)
            {
                EscrowContentEntry a = left[index];
                EscrowContentEntry b = right[index];
                if (a == null || b == null ||
                    a.Kind != b.Kind ||
                    a.Length != b.Length ||
                    !String.Equals(
                        a.RelativePath,
                        b.RelativePath,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        a.Sha256,
                        b.Sha256,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static void ValidateTransactionId(string transactionId)
        {
            Guid parsed;
            if (!Guid.TryParseExact(transactionId, "N", out parsed) ||
                !String.Equals(
                    transactionId,
                    parsed.ToString("N"),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Escrow transaction ID must be canonical N format.");
            }
        }

        private static byte[] Serialize(EscrowManifest manifest)
        {
            var serializer =
                new DataContractJsonSerializer(typeof(EscrowManifest));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, manifest);
                return stream.ToArray();
            }
        }

        private static EscrowManifest Deserialize(byte[] bytes)
        {
            var serializer =
                new DataContractJsonSerializer(typeof(EscrowManifest));
            using (var stream = new MemoryStream(bytes, false))
            {
                EscrowManifest manifest =
                    serializer.ReadObject(stream) as EscrowManifest;
                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException(
                        "Escrow manifest contains trailing bytes.");
                }
                byte[] canonical = Serialize(manifest);
                if (!BytesEqual(bytes, canonical))
                {
                    throw new InvalidDataException(
                        "Escrow manifest bytes are not canonical.");
                }
                return manifest;
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null ||
                left.Length != right.Length)
            {
                return false;
            }
            for (int index = 0; index < left.Length; ++index)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                text.Append(value.ToString("x2"));
            }
            return text.ToString();
        }

        private static EscrowManifest Clone(EscrowManifest manifest)
        {
            if (manifest == null)
            {
                throw new InvalidDataException(
                    "Escrow manifest is missing.");
            }
            return Deserialize(Serialize(manifest));
        }

        private sealed class ReceiptToken
        {
            internal int Revision;
            internal string TransactionId;
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
                ValidateTransactionId(transactionId);
                string directory = Path.Combine(
                    "transactions",
                    transactionId,
                    "escrow");
                string primary = Path.Combine(
                    directory,
                    "manifest.json");
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
