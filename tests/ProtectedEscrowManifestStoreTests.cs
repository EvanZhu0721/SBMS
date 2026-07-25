using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SBMSSetup
{
    internal static class ProtectedEscrowManifestStoreTests
    {
        private static int passed;
        private static int failed;

        private sealed class RecordingVerifier : IEscrowContentVerifier
        {
            internal int Calls;
            internal bool Fail;

            public void Verify(
                string transactionId,
                string escrowDirectoryRelativePath,
                EscrowManifest manifest)
            {
                ++Calls;
                Assert(
                    escrowDirectoryRelativePath == Path.Combine(
                        "transactions",
                        transactionId,
                        "escrow"),
                    "Verifier received an unexpected escrow directory.");
                if (Fail)
                {
                    throw new InvalidDataException(
                        "Injected escrow content verification failure.");
                }
            }
        }

        private sealed class PublicationFaultFileSystem
            : IAtomicJournalFileSystem
        {
            private readonly IAtomicJournalFileSystem inner;
            internal bool FailAfterInitialPublish;
            internal int ReplaceCalls;
            internal int FailBeforeReplaceCall;
            internal string FailReadRelativePath;
            internal int BackupReadCalls;
            internal int DeleteCalls;

            internal PublicationFaultFileSystem(
                IAtomicJournalFileSystem inner)
            {
                this.inner = inner;
            }

            public string GetDisplayPath(string relativePath)
            {
                return inner.GetDisplayPath(relativePath);
            }

            public bool FileExists(string relativePath)
            {
                return inner.FileExists(relativePath);
            }

            public void EnsureDirectory(string relativePath)
            {
                inner.EnsureDirectory(relativePath);
            }

            public Stream CreateNewFile(string relativePath)
            {
                return inner.CreateNewFile(relativePath);
            }

            public Stream OpenReadFile(string relativePath)
            {
                if (relativePath.EndsWith(
                    "manifest.json.bak",
                    StringComparison.Ordinal))
                {
                    ++BackupReadCalls;
                }
                if (String.Equals(
                    relativePath,
                    FailReadRelativePath,
                    StringComparison.Ordinal))
                {
                    throw new IOException(
                        "Injected primary escrow manifest read failure.");
                }
                return inner.OpenReadFile(relativePath);
            }

            public void PublishNewFile(
                string sourceRelativePath,
                string destinationRelativePath)
            {
                inner.PublishNewFile(
                    sourceRelativePath,
                    destinationRelativePath);
                if (FailAfterInitialPublish)
                {
                    throw new JournalFilePublicationException(
                        true,
                        new IOException(
                            "Injected committed initial publication."));
                }
            }

            public void ReplaceFile(
                string sourceRelativePath,
                string destinationRelativePath,
                string backupRelativePath)
            {
                ++ReplaceCalls;
                if (FailBeforeReplaceCall == ReplaceCalls)
                {
                    throw new JournalFilePublicationException(
                        false,
                        new IOException(
                            "Injected pre-publication replacement failure."));
                }
                inner.ReplaceFile(
                    sourceRelativePath,
                    destinationRelativePath,
                    backupRelativePath);
            }

            public void DeleteFile(string relativePath)
            {
                ++DeleteCalls;
                inner.DeleteFile(relativePath);
            }
        }

        private sealed class ProgramDataProvider
            : IInstallerProgramDataPathProvider
        {
            private readonly string root;

            internal ProgramDataProvider(string root)
            {
                this.root = root;
            }

            public string GetCommonApplicationDataPath()
            {
                return root;
            }
        }

        private sealed class PermissiveAclPolicy
            : IInstallerJournalAclPolicy
        {
            public void PrepareAndVerify(
                string commonApplicationDataRoot,
                string installerStateRoot,
                bool createIfMissing)
            {
            }
        }

        private sealed class TestContext : IDisposable
        {
            internal readonly string Root;
            internal readonly string TransactionId;
            internal readonly InstanceTransactionLeaseCoordinator Coordinator;
            internal readonly RecordingVerifier Verifier;
            internal readonly ProtectedEscrowManifestStore Store;

            internal TestContext()
            {
                Root = Path.Combine(
                    Path.GetTempPath(),
                    "SBMS-ProtectedEscrow-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                TransactionId = Guid.NewGuid().ToString("N");
                Coordinator = new InstanceTransactionLeaseCoordinator(
                    new UnsecuredInstallerTransactionMutexFactory(),
                    @"Local\SBMS.ProtectedEscrow.Tests." +
                        Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(2));
                Verifier = new RecordingVerifier();
                Store = new ProtectedEscrowManifestStore(
                    new PathAtomicJournalFileSystem(Root),
                    Coordinator,
                    Verifier,
                    TransactionId);
            }

            internal string PrimaryPath
            {
                get
                {
                    return Path.Combine(
                        Root,
                        "transactions",
                        TransactionId,
                        "escrow",
                        "manifest.json");
                }
            }

            internal string BackupPath
            {
                get { return PrimaryPath + ".bak"; }
            }

            public void Dispose()
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, true);
                }
            }
        }

        private static int Main()
        {
            Run("all escrow methods demand the outer lease", TestLeaseDemand);
            Run("initial publish uses fixed layout and exact receipt", TestInitial);
            Run("committed publication failure preserves readable primary", TestCandidatePublished);
            Run("save enforces CAS identity and retention", TestSaveCas);
            Run("canonical transaction identity blocks path injection", TestCanonicalId);
            Run("manifest reads reject trailing bytes", TestStrictEof);
            Run("backup fallback is explicit and blocks ordinary save", TestBackupFallback);
            Run("primary IO failure never falls back or repairs", TestPrimaryIoFailure);
            Run("explicit repair restores a readable primary", TestRepair);
            Run("seal verifies content and publishes two sealed revisions", TestSeal);
            Run("sealed timestamp and content identity are frozen", TestSealedIdentity);
            Run("interrupted seal resumes from its sealed primary", TestSealResume);
            Run("content verification failure publishes nothing", TestSealVerifierFailure);
            Run("transaction lease is thread-affine", TestThreadAffinity);
            Run("abandoned in-process lease poisons the coordinator", TestAbandonedLease);
            Run("file store factory shares its instance lease", TestFactoryLease);
            Console.WriteLine(
                "RESULT passed=" + passed.ToString(CultureInfo.InvariantCulture) +
                " failed=" + failed.ToString(CultureInfo.InvariantCulture));
            return failed == 0 ? 0 : 1;
        }

        private static void TestLeaseDemand()
        {
            using (var context = new TestContext())
            {
                AssertThrows<InvalidOperationException>(
                    delegate
                    {
                        context.Store.Initialize(
                            Building(context.TransactionId, 1));
                    },
                    "Initialize did not demand the transaction lease.");
                AssertThrows<InvalidOperationException>(
                    delegate { context.Store.Load(); },
                    "Load did not demand the transaction lease.");
                AssertThrows<InvalidOperationException>(
                    delegate
                    {
                        context.Store.Save(
                            null,
                            Building(context.TransactionId, 2));
                    },
                    "Save did not demand the transaction lease.");
                AssertThrows<InvalidOperationException>(
                    delegate
                    {
                        context.Store.RepairPrimary(null);
                    },
                    "Repair did not demand the transaction lease.");
                AssertThrows<InvalidOperationException>(
                    delegate
                    {
                        context.Store.SealForRollback(
                            null,
                            Sealed(context.TransactionId, 2));
                    },
                    "Seal did not demand the transaction lease.");
            }
        }

        private static void TestInitial()
        {
            using (var context = new TestContext())
            using (context.Coordinator.Acquire())
            {
                EscrowManifestReceipt receipt = context.Store.Initialize(
                    Building(context.TransactionId, 1));
                Assert(
                    receipt.RelativePath == Path.Combine(
                        "transactions",
                        context.TransactionId,
                        "escrow",
                        "manifest.json"),
                    "Initial manifest relative path is not fixed.");
                Assert(
                    receipt.DisplayPath == context.PrimaryPath,
                    "Initial manifest display path is not derived.");
                Assert(
                    receipt.Length == new FileInfo(context.PrimaryPath).Length,
                    "Receipt length does not describe published bytes.");
                Assert(
                    receipt.Sha256 == FileSha256(context.PrimaryPath),
                    "Receipt hash does not describe published bytes.");
                Assert(
                    !File.Exists(context.PrimaryPath + ".new"),
                    "Initial manifest candidate was not cleaned.");
            }
        }

        private static void TestSaveCas()
        {
            using (var context = new TestContext())
            using (context.Coordinator.Acquire())
            {
                EscrowManifestReceipt initial = context.Store.Initialize(
                    Building(context.TransactionId, 1));
                EscrowManifest next = Building(context.TransactionId, 2);
                next.Content.Add(
                    new EscrowContentEntry
                    {
                        Kind = EscrowContentKind.Configuration,
                        RelativePath = "config.xml",
                        Length = 4,
                        Sha256 = Hash('a')
                    });
                EscrowManifestReceipt receipt = context.Store.Save(
                    initial,
                    next);
                Assert(
                    receipt.Manifest.Revision == 2,
                    "Save did not publish the expected revision.");
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        context.Store.Save(
                            initial,
                            Building(context.TransactionId, 2));
                    },
                    "Stale CAS revision was accepted.");
                EscrowManifest changedIdentity =
                    Building(context.TransactionId, 3);
                changedIdentity.BaselineEvidenceDigest = Hash('b');
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        context.Store.Save(
                            receipt,
                            changedIdentity);
                    },
                    "Immutable escrow identity was allowed to change.");
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        context.Store.Save(
                            receipt,
                            Sealed(context.TransactionId, 3));
                    },
                    "Ordinary save was allowed to seal escrow.");
            }
        }

        private static void TestCandidatePublished()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "SBMS-EscrowPublish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string transactionId = Guid.NewGuid().ToString("N");
                var fileSystem = new PublicationFaultFileSystem(
                    new PathAtomicJournalFileSystem(root));
                var coordinator = new InstanceTransactionLeaseCoordinator(
                    new UnsecuredInstallerTransactionMutexFactory(),
                    @"Local\SBMS.EscrowPublish.Tests." +
                        Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(2));
                var store = new ProtectedEscrowManifestStore(
                    fileSystem,
                    coordinator,
                    new RecordingVerifier(),
                    transactionId);
                using (coordinator.Acquire())
                {
                    fileSystem.FailAfterInitialPublish = true;
                    try
                    {
                        store.Initialize(
                            Building(transactionId, 1));
                        throw new InvalidOperationException(
                            "Committed publication fault did not escape.");
                    }
                    catch (JournalFilePublicationException failure)
                    {
                        Assert(
                            failure.CandidatePublished,
                            "Committed publication was reported as uncommitted.");
                    }
                    fileSystem.FailAfterInitialPublish = false;
                    EscrowManifestReadResult loaded =
                        store.Load();
                    Assert(
                        loaded.Source == EscrowManifestReadSource.Primary &&
                        loaded.Receipt.Manifest.Revision == 1,
                        "Committed publication did not leave a readable primary.");
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestCanonicalId()
        {
            using (var context = new TestContext())
            using (context.Coordinator.Acquire())
            {
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        new ProtectedEscrowManifestStore(
                            new PathAtomicJournalFileSystem(context.Root),
                            context.Coordinator,
                            context.Verifier,
                            context.TransactionId.ToUpperInvariant());
                    },
                    "Non-canonical transaction ID was accepted.");
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        new ProtectedEscrowManifestStore(
                            new PathAtomicJournalFileSystem(context.Root),
                            context.Coordinator,
                            context.Verifier,
                            context.TransactionId + "\\..\\escape");
                    },
                    "Transaction path injection was accepted.");
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        context.Store.Initialize(
                            Building(
                                Guid.NewGuid().ToString("N"),
                                1));
                    },
                    "Transaction-bound store accepted another transaction.");
            }
        }

        private static void TestBackupFallback()
        {
            using (var context = new TestContext())
            using (context.Coordinator.Acquire())
            {
                EscrowManifestReceipt initial = context.Store.Initialize(
                    Building(context.TransactionId, 1));
                context.Store.Save(
                    initial,
                    Building(context.TransactionId, 2));
                File.WriteAllText(context.PrimaryPath, "{broken");
                EscrowManifestReadResult result =
                    context.Store.Load();
                Assert(
                    result.Source == EscrowManifestReadSource.Backup &&
                    result.RequiresPrimaryRepair &&
                    result.Receipt.Manifest.Revision == 1,
                    "Backup fallback was not explicit.");
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        context.Store.Save(
                            result.Receipt,
                            Building(context.TransactionId, 2));
                    },
                    "Ordinary save continued from degraded backup.");
                File.WriteAllText(context.BackupPath, "{also-broken");
                AssertThrows<InvalidDataException>(
                    delegate { context.Store.Load(); },
                    "Two invalid manifests did not fail closed.");
            }
        }

        private static void TestStrictEof()
        {
            using (var context = new TestContext())
            using (context.Coordinator.Acquire())
            {
                context.Store.Initialize(
                    Building(context.TransactionId, 1));
                using (var stream = new FileStream(
                    context.PrimaryPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.None))
                {
                    stream.WriteByte((byte)' ');
                    stream.Flush(true);
                }
                AssertThrows<InvalidDataException>(
                    delegate { context.Store.Load(); },
                    "Manifest trailing bytes were accepted.");
            }
        }

        private static void TestPrimaryIoFailure()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "SBMS-EscrowPrimaryIo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string transactionId = Guid.NewGuid().ToString("N");
                var fileSystem = new PublicationFaultFileSystem(
                    new PathAtomicJournalFileSystem(root));
                var coordinator = new InstanceTransactionLeaseCoordinator(
                    new UnsecuredInstallerTransactionMutexFactory(),
                    @"Local\SBMS.EscrowPrimaryIo.Tests." +
                        Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(2));
                var store = new ProtectedEscrowManifestStore(
                    fileSystem,
                    coordinator,
                    new RecordingVerifier(),
                    transactionId);
                using (coordinator.Acquire())
                {
                    EscrowManifestReceipt initial = store.Initialize(
                        Building(transactionId, 1));
                    store.Save(
                        initial,
                        Building(transactionId, 2));
                    fileSystem.FailReadRelativePath = Path.Combine(
                        "transactions",
                        transactionId,
                        "escrow",
                        "manifest.json");
                    int deleteCallsBeforeLoad = fileSystem.DeleteCalls;
                    AssertThrows<IOException>(
                        delegate { store.Load(); },
                        "Primary IO failure was treated as manifest corruption.");
                    Assert(
                        fileSystem.BackupReadCalls == 0,
                        "Primary IO failure fell back to the backup manifest.");
                    Assert(
                        fileSystem.DeleteCalls == deleteCallsBeforeLoad,
                        "Primary IO failure deleted or repaired durable state.");
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestRepair()
        {
            using (var context = new TestContext())
            using (context.Coordinator.Acquire())
            {
                EscrowManifestReceipt initial = context.Store.Initialize(
                    Building(context.TransactionId, 1));
                context.Store.Save(
                    initial,
                    Building(context.TransactionId, 2));
                File.WriteAllText(context.PrimaryPath, "{broken");
                EscrowManifestReadResult degraded = context.Store.Load();
                EscrowManifestReceipt repaired =
                    context.Store.RepairPrimary(degraded);
                Assert(
                    repaired.Manifest.Revision == 2,
                    "Repair did not advance from the verified backup.");
                EscrowManifestReadResult loaded =
                    context.Store.Load();
                Assert(
                    loaded.Source == EscrowManifestReadSource.Primary &&
                    !loaded.RequiresPrimaryRepair &&
                    loaded.Receipt.Manifest.Revision == 2,
                    "Repair did not restore a valid primary.");
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        context.Store.RepairPrimary(degraded);
                    },
                    "Repair was allowed without degraded state.");
            }
        }

        private static void TestSeal()
        {
            using (var context = new TestContext())
            using (context.Coordinator.Acquire())
            {
                EscrowManifestReceipt initial = context.Store.Initialize(
                    Building(context.TransactionId, 1));
                byte[] contentBytes = new byte[] { 1, 2, 3, 4 };
                string contentPath = Path.Combine(
                    context.Root,
                    "transactions",
                    context.TransactionId,
                    "escrow",
                    "baseline",
                    "configuration",
                    "config.xml");
                Directory.CreateDirectory(
                    Path.GetDirectoryName(contentPath));
                File.WriteAllBytes(contentPath, contentBytes);
                EscrowManifest sealCandidate =
                    Sealed(context.TransactionId, 2);
                sealCandidate.Content.Add(
                    new EscrowContentEntry
                    {
                        Kind = EscrowContentKind.Configuration,
                        RelativePath = "config.xml",
                        Length = contentBytes.LongLength,
                        Sha256 = BytesSha256(contentBytes)
                    });
                EscrowManifestReceipt sealedReceipt =
                    context.Store.SealForRollback(
                        initial,
                        sealCandidate);
                Assert(
                    context.Verifier.Calls == 1,
                    "Seal did not invoke content verification exactly once.");
                Assert(
                    sealedReceipt.Manifest.Sealed &&
                    sealedReceipt.Manifest.Revision == 3,
                    "Seal did not publish the redundant sealed revision.");
                EscrowManifest primary = ReadManifest(context.PrimaryPath);
                EscrowManifest backup = ReadManifest(context.BackupPath);
                Assert(
                    primary.Sealed &&
                    backup.Sealed &&
                    primary.Revision == 3 &&
                    backup.Revision == 2,
                    "Primary and backup are not adjacent sealed revisions.");
            }
        }

        private static void TestSealVerifierFailure()
        {
            using (var context = new TestContext())
            using (context.Coordinator.Acquire())
            {
                EscrowManifestReceipt initial = context.Store.Initialize(
                    Building(context.TransactionId, 1));
                context.Verifier.Fail = true;
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        context.Store.SealForRollback(
                            initial,
                            Sealed(context.TransactionId, 2));
                    },
                    "Content verification failure did not escape.");
                EscrowManifestReadResult loaded =
                    context.Store.Load();
                Assert(
                    loaded.Receipt.Manifest.Revision == 1 &&
                    !loaded.Receipt.Manifest.Sealed,
                    "Failed verification changed the durable manifest.");
            }
        }

        private static void TestSealedIdentity()
        {
            using (var context = new TestContext())
            using (context.Coordinator.Acquire())
            {
                EscrowManifestReceipt initial = context.Store.Initialize(
                    Building(context.TransactionId, 1));
                EscrowManifestReceipt sealedReceipt =
                    context.Store.SealForRollback(
                        initial,
                        Sealed(context.TransactionId, 2));
                EscrowManifest changedTimestamp =
                    ReadManifest(context.PrimaryPath);
                changedTimestamp.Revision = 4;
                changedTimestamp.RetentionState =
                    EscrowRetentionState.FinalizationPending;
                changedTimestamp.SealedUtc = DateTime.UtcNow.AddSeconds(1)
                    .ToString("o", CultureInfo.InvariantCulture);
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        context.Store.Save(
                            sealedReceipt,
                            changedTimestamp);
                    },
                    "Sealed timestamp was allowed to change.");

                EscrowManifest changedContent =
                    ReadManifest(context.PrimaryPath);
                changedContent.Revision = 4;
                changedContent.RetentionState =
                    EscrowRetentionState.FinalizationPending;
                changedContent.Content.Add(
                    new EscrowContentEntry
                    {
                        Kind = EscrowContentKind.Configuration,
                        RelativePath = "late.xml",
                        Length = 0,
                        Sha256 = Hash('c')
                    });
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        context.Store.Save(
                            sealedReceipt,
                            changedContent);
                    },
                    "Sealed content was allowed to change.");
            }
        }

        private static void TestSealResume()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "SBMS-EscrowSealResume-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string transactionId = Guid.NewGuid().ToString("N");
                var fileSystem = new PublicationFaultFileSystem(
                    new PathAtomicJournalFileSystem(root));
                var coordinator = new InstanceTransactionLeaseCoordinator(
                    new UnsecuredInstallerTransactionMutexFactory(),
                    @"Local\SBMS.EscrowSealResume.Tests." +
                        Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(2));
                var verifier = new RecordingVerifier();
                var store = new ProtectedEscrowManifestStore(
                    fileSystem,
                    coordinator,
                    verifier,
                    transactionId);
                using (coordinator.Acquire())
                {
                    EscrowManifestReceipt initial = store.Initialize(
                        Building(transactionId, 1));
                    fileSystem.FailBeforeReplaceCall = 2;
                    AssertThrows<JournalFilePublicationException>(
                        delegate
                        {
                            store.SealForRollback(
                                initial,
                                Sealed(transactionId, 2));
                        },
                        "Second seal publication fault did not escape.");
                    EscrowManifestReadResult interrupted =
                        store.Load();
                    Assert(
                        interrupted.Receipt.Manifest.Sealed &&
                        interrupted.Receipt.Manifest.Revision == 2,
                        "Interrupted seal lost its first sealed primary.");
                    fileSystem.FailBeforeReplaceCall = 0;
                    EscrowManifest resume =
                        ReadManifest(
                            Path.Combine(
                                root,
                                "transactions",
                                transactionId,
                                "escrow",
                                "manifest.json"));
                    resume.Revision = 3;
                    EscrowManifestReceipt completed =
                        store.SealForRollback(
                            interrupted.Receipt,
                            resume);
                    Assert(
                        completed.Manifest.Sealed &&
                        completed.Manifest.Revision == 4,
                        "Resumed seal did not rebuild redundant revisions.");
                    EscrowManifest backup = ReadManifest(
                        Path.Combine(
                            root,
                            "transactions",
                            transactionId,
                            "escrow",
                            "manifest.json.bak"));
                    Assert(
                        backup.Sealed && backup.Revision == 3,
                        "Resumed seal did not leave a sealed backup.");
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestThreadAffinity()
        {
            using (var context = new TestContext())
            using (context.Coordinator.Acquire())
            {
                Exception failure = null;
                var thread = new Thread(
                    new ThreadStart(
                    delegate
                    {
                        try
                        {
                            context.Store.Load();
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }
                    }));
                thread.Start();
                thread.Join();
                Assert(
                    failure is InvalidOperationException,
                    "Another thread inherited the transaction lease.");
            }
        }

        private static void TestAbandonedLease()
        {
            var coordinator = new InstanceTransactionLeaseCoordinator(
                new UnsecuredInstallerTransactionMutexFactory(),
                @"Local\SBMS.EscrowAbandon.Tests." +
                    Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(2));
            Exception workerFailure = null;
            var thread = new Thread(
                new ThreadStart(
                delegate
                {
                    try
                    {
                        coordinator.Acquire();
                    }
                    catch (Exception ex)
                    {
                        workerFailure = ex;
                    }
                }));
            thread.Start();
            thread.Join();
            Assert(
                workerFailure == null,
                "Worker did not acquire the transaction lease.");
            AssertThrows<InvalidOperationException>(
                delegate { coordinator.Acquire(); },
                "Abandoned in-process lease was accepted.");
            AssertThrows<InvalidOperationException>(
                delegate { coordinator.Acquire(); },
                "Poisoned coordinator accepted a later lease.");
        }

        private static void TestFactoryLease()
        {
            string common = Path.Combine(
                Path.GetTempPath(),
                "SBMS-EscrowFactory-" + Guid.NewGuid().ToString("N"));
            string stateRoot = Path.Combine(common, "SBMS", "Installer");
            Directory.CreateDirectory(common);
            try
            {
                var fileStore = new FileTransactionJournalStore(
                    new ProgramDataProvider(common),
                    new PermissiveAclPolicy(),
                    @"Local\SBMS.EscrowFactory.Tests." +
                        Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(2),
                    null,
                    new PathAtomicJournalFileSystem(stateRoot),
                    new UnsecuredInstallerTransactionMutexFactory());
                try
                {
                    string transactionId = Guid.NewGuid().ToString("N");
                    IProtectedEscrowManifestStore escrow =
                        fileStore.CreateProtectedEscrowManifestStore(
                            transactionId);
                    AssertThrows<InvalidOperationException>(
                        delegate
                        {
                            escrow.Initialize(
                                Building(transactionId, 1));
                        },
                        "Factory escrow bypassed the file store lease.");
                    using (fileStore.AcquireTransactionLease())
                    {
                        EscrowManifestReceipt receipt = escrow.Initialize(
                            Building(transactionId, 1));
                        Assert(
                            receipt.Manifest.TransactionId == transactionId,
                            "Factory escrow did not share the held lease.");
                    }
                }
                finally
                {
                    fileStore.Dispose();
                }
            }
            finally
            {
                if (Directory.Exists(common))
                {
                    Directory.Delete(common, true);
                }
            }
        }

        private static EscrowManifest Building(
            string transactionId,
            int revision)
        {
            return new EscrowManifest
            {
                SchemaVersion = 2,
                Revision = revision,
                TransactionId = transactionId,
                Operation = InstallOperation.FreshInstall,
                BaselineEvidenceDigest = Hash('0'),
                BaselinePayloadState = BaselinePayloadState.Absent,
                Target = new ReleaseIdentity("1.0.0", "package"),
                Content = new List<EscrowContentEntry>(),
                Sealed = false,
                SealedUtc = null,
                RetentionState = EscrowRetentionState.Building,
                FinalizationEvidence = null
            };
        }

        private static EscrowManifest Sealed(
            string transactionId,
            int revision)
        {
            EscrowManifest manifest = Building(transactionId, revision);
            manifest.Sealed = true;
            manifest.SealedUtc = DateTime.UtcNow.ToString(
                "o",
                CultureInfo.InvariantCulture);
            manifest.RetentionState =
                EscrowRetentionState.SealedForRollback;
            return manifest;
        }

        private static EscrowManifest ReadManifest(string path)
        {
            var serializer =
                new System.Runtime.Serialization.Json.DataContractJsonSerializer(
                    typeof(EscrowManifest));
            using (Stream stream = File.OpenRead(path))
            {
                return serializer.ReadObject(stream) as EscrowManifest;
            }
        }

        private static string FileSha256(string path)
        {
            using (Stream stream = File.OpenRead(path))
            using (SHA256 algorithm = SHA256.Create())
            {
                return Hex(algorithm.ComputeHash(stream));
            }
        }

        private static string BytesSha256(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                return Hex(algorithm.ComputeHash(bytes));
            }
        }

        private static string Hash(char value)
        {
            return new String(value, 64);
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                ++passed;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception failure)
            {
                ++failed;
                Console.WriteLine(
                    "FAIL " + name + ": " +
                    failure.GetType().Name + ": " + failure.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertThrows<T>(
            Action action,
            string message)
            where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            throw new InvalidOperationException(message);
        }
    }
}
