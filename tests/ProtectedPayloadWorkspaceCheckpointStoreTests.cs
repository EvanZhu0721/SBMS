using System;
using System.IO;

namespace SBMSSetup
{
    // The production definition lives in ProtectedEscrowManifestStore.cs.
    // This narrow contract copy keeps the isolated checkpoint-store test
    // executable independent from the escrow domain model.
    internal interface ITransactionLeaseCoordinator
    {
        IDisposable Acquire();
        void DemandHeld();
    }

    internal static class ProtectedPayloadWorkspaceCheckpointStoreTests
    {
        private const string TransactionId =
            "00000000000000000000000000000011";
        private const string AuthorityDigest =
            "1111111111111111111111111111111111111111111111111111111111111111";
        private static int passed;
        private static int failed;

        private sealed class TestLeaseCoordinator :
            ITransactionLeaseCoordinator
        {
            private int depth;

            public IDisposable Acquire()
            {
                ++depth;
                return new Lease(this);
            }

            public void DemandHeld()
            {
                if (depth <= 0)
                {
                    throw new InvalidOperationException(
                        "Test transaction lease is not held.");
                }
            }

            private sealed class Lease : IDisposable
            {
                private TestLeaseCoordinator owner;

                internal Lease(TestLeaseCoordinator owner)
                {
                    this.owner = owner;
                }

                public void Dispose()
                {
                    if (owner != null)
                    {
                        --owner.depth;
                        owner = null;
                    }
                }
            }
        }

        private sealed class FailingReadFileSystem :
            IAtomicJournalFileSystem
        {
            private readonly IAtomicJournalFileSystem inner;
            internal string FailingRelativePath;
            internal bool FailOnceAfterPublish;
            internal bool FailEveryRead;
            private bool publishedReadPending;

            internal FailingReadFileSystem(
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
                if (publishedReadPending &&
                    String.Equals(
                        relativePath,
                        FailingRelativePath,
                        StringComparison.Ordinal))
                {
                    publishedReadPending = false;
                    throw new IOException(
                        "Injected committed checkpoint readback loss.");
                }
                if (FailEveryRead &&
                    String.Equals(
                        relativePath,
                        FailingRelativePath,
                        StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException(
                        "Injected checkpoint read denial.");
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
                if (FailOnceAfterPublish)
                {
                    publishedReadPending = true;
                }
            }

            public void ReplaceFile(
                string sourceRelativePath,
                string destinationRelativePath,
                string backupRelativePath)
            {
                inner.ReplaceFile(
                    sourceRelativePath,
                    destinationRelativePath,
                    backupRelativePath);
                if (FailOnceAfterPublish)
                {
                    publishedReadPending = true;
                }
            }

            public void DeleteFile(string relativePath)
            {
                inner.DeleteFile(relativePath);
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Root;
            internal readonly TestLeaseCoordinator Lease;
            internal readonly PathAtomicJournalFileSystem FileSystem;
            internal readonly ProtectedPayloadWorkspaceCheckpointStore Store;

            internal Fixture()
            {
                Root = Path.Combine(
                    Path.GetTempPath(),
                    "SBMS-payload-workspace-store-" +
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                Lease = new TestLeaseCoordinator();
                FileSystem = new PathAtomicJournalFileSystem(Root);
                Store = new ProtectedPayloadWorkspaceCheckpointStore(
                    FileSystem,
                    Lease,
                    TransactionId,
                    AuthorityDigest);
            }

            public void Dispose()
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, true);
                }
            }
        }

        internal static int Main()
        {
            Run("lease is mandatory", LeaseIsMandatory);
            Run("initialize and exact load", InitializeAndLoad);
            Run("save enforces exact CAS", SaveEnforcesCas);
            Run("immutable root identity is enforced", RootIdentityIsImmutable);
            Run("recovery generation is repair-only",
                RecoveryGenerationIsRepairOnly);
            Run("corrupt primary uses explicit backup repair",
                CorruptPrimaryUsesBackupRepair);
            Run("filesystem IO failure never falls back",
                IoFailureNeverFallsBack);
            Run("committed readback loss remains recoverable",
                CommittedReadbackLossIsRecoverable);
            Run("save committed readback loss remains recoverable",
                SaveCommittedReadbackLossIsRecoverable);
            Run("primary backup revision inversion is rejected",
                RevisionInversionIsRejected);
            Run("missing required checkpoint members are rejected",
                MissingRequiredMembersAreRejected);
            Run("trailing JSON document is rejected",
                TrailingJsonIsRejected);
            Run("double checkpoint corruption is rejected",
                DoubleCorruptionIsRejected);
            Run("foreign store identity is rejected",
                ForeignIdentityIsRejected);

            Console.WriteLine(
                "Protected payload workspace checkpoint store tests: " +
                passed + " passed, " + failed + " failed.");
            return failed == 0 ? 0 : 1;
        }

        private static void LeaseIsMandatory()
        {
            using (var fixture = new Fixture())
            {
                Throws<InvalidOperationException>(
                    delegate
                    {
                        fixture.Store.Initialize(Checkpoint(11));
                    },
                    "Checkpoint initialization did not require the lease.");
            }
        }

        private static void InitializeAndLoad()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                PayloadBuildWorkspaceCheckpoint expected =
                    Checkpoint(11);
                PayloadWorkspaceCheckpointReceipt initialized =
                    fixture.Store.Initialize(expected);
                PayloadWorkspaceCheckpointReadResult loaded =
                    fixture.Store.Load();
                Equal(
                    expected.InvariantDigest,
                    initialized.State.InvariantDigest,
                    "Initialize did not return exact persisted state.");
                Equal(
                    initialized.State.InvariantDigest,
                    loaded.Receipt.State.InvariantDigest,
                    "Load changed the persisted checkpoint.");
                Equal(
                    PayloadWorkspaceCheckpointReadSource.Primary,
                    loaded.Source,
                    "Fresh checkpoint did not load from primary.");
                True(
                    !loaded.RequiresPrimaryRepair,
                    "Fresh checkpoint unexpectedly requires repair.");
            }
        }

        private static void SaveEnforcesCas()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                PayloadWorkspaceCheckpointReceipt initial =
                    fixture.Store.Initialize(Checkpoint(11));
                PayloadBuildWorkspaceCheckpoint next =
                    initial.State.Checkpoint;
                next.Revision = 12;
                PayloadWorkspaceCheckpointReceipt saved =
                    fixture.Store.Save(initial.State.CasToken, next);
                Equal(
                    12L,
                    saved.State.Revision,
                    "Save did not advance exactly one revision.");
                Throws<InvalidOperationException>(
                    delegate
                    {
                        PayloadBuildWorkspaceCheckpoint stale =
                            saved.State.Checkpoint;
                        stale.Revision = 13;
                        fixture.Store.Save(
                            initial.State.CasToken,
                            stale);
                    },
                    "Stale workspace CAS was accepted.");
                Equal(
                    saved.State.InvariantDigest,
                    fixture.Store.Load().Receipt.State.InvariantDigest,
                    "Rejected stale CAS changed durable state.");
            }
        }

        private static void RootIdentityIsImmutable()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                PayloadWorkspaceCheckpointReceipt initial =
                    fixture.Store.Initialize(Checkpoint(11));
                PayloadBuildWorkspaceCheckpoint changed =
                    initial.State.Checkpoint;
                changed.Revision = 12;
                changed.NamespaceRoot.RootFileId =
                    "ffffffffffffffffffffffffffffffff";
                Throws<InvalidDataException>(
                    delegate
                    {
                        fixture.Store.Save(
                            initial.State.CasToken,
                            changed);
                    },
                    "Workspace root identity changed across checkpoint CAS.");
            }
        }

        private static void RecoveryGenerationIsRepairOnly()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                PayloadBuildWorkspaceCheckpoint source = Checkpoint(11);
                source.RecoveryGeneration = 2;
                PayloadWorkspaceCheckpointReceipt initial =
                    fixture.Store.Initialize(source);
                PayloadBuildWorkspaceCheckpoint increased =
                    initial.State.Checkpoint;
                increased.Revision = 12;
                increased.RecoveryGeneration = 3;
                Throws<InvalidDataException>(
                    delegate
                    {
                        fixture.Store.Save(
                            initial.State.CasToken,
                            increased);
                    },
                    "Ordinary Save increased the repair-only recovery generation.");
                PayloadBuildWorkspaceCheckpoint decreased =
                    initial.State.Checkpoint;
                decreased.Revision = 12;
                decreased.RecoveryGeneration = 1;
                Throws<InvalidDataException>(
                    delegate
                    {
                        fixture.Store.Save(
                            initial.State.CasToken,
                            decreased);
                    },
                    "Ordinary Save decreased the repair-only recovery generation.");
                Equal(
                    initial.State.InvariantDigest,
                    fixture.Store.Load().Receipt.State.InvariantDigest,
                    "Rejected recovery-generation changes altered durable state.");
            }
        }

        private static void CorruptPrimaryUsesBackupRepair()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                PayloadWorkspaceCheckpointReceipt initial =
                    fixture.Store.Initialize(Checkpoint(11));
                PayloadBuildWorkspaceCheckpoint next =
                    initial.State.Checkpoint;
                next.Revision = 12;
                PayloadWorkspaceCheckpointReceipt saved =
                    fixture.Store.Save(initial.State.CasToken, next);
                string primary = fixture.FileSystem.GetDisplayPath(
                    RelativePrimary());
                File.WriteAllText(primary, "{broken");

                PayloadWorkspaceCheckpointReadResult backup =
                    fixture.Store.Load();
                Equal(
                    PayloadWorkspaceCheckpointReadSource.Backup,
                    backup.Source,
                    "Corrupt primary did not select valid backup.");
                True(
                    backup.RequiresPrimaryRepair,
                    "Backup load did not require explicit repair.");
                Equal(
                    11L,
                    backup.Receipt.State.Revision,
                    "Backup did not preserve the prior committed revision.");
                PayloadBuildWorkspaceCheckpoint blocked =
                    backup.Receipt.State.Checkpoint;
                blocked.Revision = 12;
                Throws<InvalidDataException>(
                    delegate
                    {
                        fixture.Store.Save(
                            backup.Receipt.State.CasToken,
                            blocked);
                    },
                    "Backup-mode checkpoint was accepted for Save.");

                PayloadWorkspaceCheckpointReceipt repaired =
                    fixture.Store.RepairPrimary(backup);
                Equal(
                    12L,
                    repaired.State.Revision,
                    "Primary repair revived the old checkpoint revision.");
                True(
                    !String.Equals(
                        backup.Receipt.State.InvariantDigest,
                        repaired.State.InvariantDigest,
                        StringComparison.Ordinal),
                    "Primary repair revived the old workspace CAS token.");
                PayloadBuildWorkspaceCheckpoint afterRepair =
                    repaired.State.Checkpoint;
                afterRepair.Revision = 13;
                Throws<InvalidOperationException>(
                    delegate
                    {
                        fixture.Store.Save(
                            backup.Receipt.State.CasToken,
                            afterRepair);
                    },
                    "Primary repair allowed the old backup CAS token.");
                Throws<InvalidOperationException>(
                    delegate
                    {
                        fixture.Store.Save(
                            saved.State.CasToken,
                            afterRepair);
                    },
                    "Primary repair revived the formerly committed CAS token.");
                Equal(
                    PayloadWorkspaceCheckpointReadSource.Primary,
                    fixture.Store.Load().Source,
                    "Repair did not restore primary authority.");
                Throws<InvalidDataException>(
                    delegate
                    {
                        fixture.Store.RepairPrimary(backup);
                    },
                    "Stale backup repair receipt was accepted.");
            }
        }

        private static void IoFailureNeverFallsBack()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                PayloadWorkspaceCheckpointReceipt initial =
                    fixture.Store.Initialize(Checkpoint(11));
                PayloadBuildWorkspaceCheckpoint next =
                    initial.State.Checkpoint;
                next.Revision = 12;
                fixture.Store.Save(initial.State.CasToken, next);

                var failing = new FailingReadFileSystem(
                    fixture.FileSystem);
                failing.FailingRelativePath = RelativePrimary();
                failing.FailEveryRead = true;
                var store = new ProtectedPayloadWorkspaceCheckpointStore(
                    failing,
                    fixture.Lease,
                    TransactionId,
                    AuthorityDigest);
                Throws<UnauthorizedAccessException>(
                    delegate { store.Load(); },
                    "Filesystem access failure fell back to stale backup.");
            }
        }

        private static void ForeignIdentityIsRejected()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                fixture.Store.Initialize(Checkpoint(11));
                var foreign = new ProtectedPayloadWorkspaceCheckpointStore(
                    fixture.FileSystem,
                    fixture.Lease,
                    "00000000000000000000000000000012",
                    AuthorityDigest);
                Throws<FileNotFoundException>(
                    delegate { foreign.Load(); },
                    "Foreign transaction reused another checkpoint path.");

                var wrongAuthority =
                    new ProtectedPayloadWorkspaceCheckpointStore(
                        fixture.FileSystem,
                        fixture.Lease,
                        TransactionId,
                        "2222222222222222222222222222222222222222222222222222222222222222");
                Throws<InvalidDataException>(
                    delegate { wrongAuthority.Load(); },
                    "Foreign authority accepted the persisted checkpoint.");
            }
        }

        private static void CommittedReadbackLossIsRecoverable()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                var failing = new FailingReadFileSystem(
                    fixture.FileSystem);
                failing.FailingRelativePath = RelativePrimary();
                failing.FailOnceAfterPublish = true;
                var store = new ProtectedPayloadWorkspaceCheckpointStore(
                    failing,
                    fixture.Lease,
                    TransactionId,
                    AuthorityDigest);
                try
                {
                    store.Initialize(Checkpoint(11));
                    throw new InvalidOperationException(
                        "Committed checkpoint readback loss was hidden.");
                }
                catch (
                    PayloadWorkspaceCheckpointPublicationException
                        failure)
                {
                    True(
                        failure.CandidatePublished,
                        "Committed checkpoint loss was misclassified.");
                }
                failing.FailingRelativePath = null;
                Equal(
                    Checkpoint(11).InvariantDigest,
                    store.Load().Receipt.State.InvariantDigest,
                    "Committed checkpoint could not be recovered by reload.");
            }
        }

        private static void SaveCommittedReadbackLossIsRecoverable()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                PayloadWorkspaceCheckpointReceipt initial =
                    fixture.Store.Initialize(Checkpoint(11));
                var failing = new FailingReadFileSystem(
                    fixture.FileSystem);
                failing.FailingRelativePath = RelativePrimary();
                failing.FailOnceAfterPublish = true;
                var store = new ProtectedPayloadWorkspaceCheckpointStore(
                    failing,
                    fixture.Lease,
                    TransactionId,
                    AuthorityDigest);
                PayloadBuildWorkspaceCheckpoint next =
                    initial.State.Checkpoint;
                next.Revision = 12;
                try
                {
                    store.Save(initial.State.CasToken, next);
                    throw new InvalidOperationException(
                        "Committed save readback loss was hidden.");
                }
                catch (
                    PayloadWorkspaceCheckpointPublicationException
                        failure)
                {
                    True(
                        failure.CandidatePublished,
                        "Committed save loss was misclassified.");
                }
                failing.FailingRelativePath = null;
                Equal(
                    12L,
                    store.Load().Receipt.State.Revision,
                    "Committed save could not be recovered by reload.");
            }
        }

        private static void RevisionInversionIsRejected()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                PayloadWorkspaceCheckpointReceipt initial =
                    fixture.Store.Initialize(Checkpoint(11));
                PayloadBuildWorkspaceCheckpoint next =
                    initial.State.Checkpoint;
                next.Revision = 12;
                fixture.Store.Save(initial.State.CasToken, next);

                string primary = fixture.FileSystem.GetDisplayPath(
                    RelativePrimary());
                string backup = primary + ".bak";
                byte[] primaryBytes = File.ReadAllBytes(primary);
                byte[] backupBytes = File.ReadAllBytes(backup);
                File.WriteAllBytes(primary, backupBytes);
                File.WriteAllBytes(backup, primaryBytes);

                Throws<InvalidDataException>(
                    delegate { fixture.Store.Load(); },
                    "Valid older primary hid a newer backup revision.");
            }
        }

        private static void MissingRequiredMembersAreRejected()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                fixture.Store.Initialize(Checkpoint(11));
                File.WriteAllText(
                    fixture.FileSystem.GetDisplayPath(
                        RelativePrimary()),
                    "{}");
                Throws<InvalidDataException>(
                    delegate { fixture.Store.Load(); },
                    "Checkpoint missing required members was accepted.");
            }
        }

        private static void TrailingJsonIsRejected()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                fixture.Store.Initialize(Checkpoint(11));
                string primary = fixture.FileSystem.GetDisplayPath(
                    RelativePrimary());
                File.AppendAllText(primary, "{}");
                Throws<InvalidDataException>(
                    delegate { fixture.Store.Load(); },
                    "Trailing checkpoint JSON was accepted.");
            }
        }

        private static void DoubleCorruptionIsRejected()
        {
            using (var fixture = new Fixture())
            using (fixture.Lease.Acquire())
            {
                PayloadWorkspaceCheckpointReceipt initial =
                    fixture.Store.Initialize(Checkpoint(11));
                PayloadBuildWorkspaceCheckpoint next =
                    initial.State.Checkpoint;
                next.Revision = 12;
                fixture.Store.Save(initial.State.CasToken, next);
                string primary = fixture.FileSystem.GetDisplayPath(
                    RelativePrimary());
                File.WriteAllText(primary, "{broken");
                File.WriteAllText(primary + ".bak", "{also-broken");
                Throws<InvalidDataException>(
                    delegate { fixture.Store.Load(); },
                    "Two corrupt checkpoint copies were accepted.");
            }
        }

        private static PayloadBuildWorkspaceCheckpoint Checkpoint(
            long revision)
        {
            return new PayloadBuildWorkspaceCheckpoint
            {
                SchemaVersion = 3,
                Revision = revision,
                TransactionId = TransactionId,
                RecoveryAuthorityInvariantDigest = AuthorityDigest,
                NamespaceRoot = new PayloadNamespaceRootIdentity
                {
                    SchemaVersion = 1,
                    CanonicalRootPath = @"C:\Program Files\SBMS",
                    VolumeSerialNumber = 0x1234UL,
                    RootFileId =
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                },
                Committed = new PayloadNamespaceCheckpoint
                {
                    SchemaVersion = 1,
                    Revision = 7,
                    TransactionId = TransactionId,
                    Shape = PayloadNamespaceShape.Empty,
                    Current = null,
                    Candidate = null,
                    Backup = null
                },
                ActiveBuild = null,
                ActivePartialTree = null
            };
        }

        private static string RelativePrimary()
        {
            return Path.Combine(
                "transactions",
                TransactionId,
                "payload",
                "workspace.json");
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
                    "FAIL " + name + ": " + failure);
            }
        }

        private static void True(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void Equal<T>(
            T expected,
            T actual,
            string message)
        {
            if (!Object.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    message + " expected=" + expected +
                    " actual=" + actual);
            }
        }

        private static void Throws<T>(
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
