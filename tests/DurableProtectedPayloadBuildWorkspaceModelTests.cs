using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace SBMSSetup
{
    // Narrow copies keep this isolated executable independent from the
    // production checkpoint publisher and Windows journal implementation.
    internal interface ITransactionLeaseCoordinator
    {
        IDisposable Acquire();
        void DemandHeld();
    }

    internal enum PayloadWorkspaceCheckpointReadSource
    {
        Primary,
        Backup
    }

    internal sealed class PayloadWorkspaceCheckpointReceipt
    {
        internal PayloadWorkspaceCheckpointReceipt(
            PayloadBuildWorkspaceState state)
        {
            State = new PayloadBuildWorkspaceState(state.Checkpoint);
        }

        internal readonly PayloadBuildWorkspaceState State;
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
            : base("Injected checkpoint publication failure.", innerException)
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

    internal static class DurableProtectedPayloadBuildWorkspaceModelTests
    {
        private const string TransactionId =
            "00000000000000000000000000000019";
        private const ulong Volume = 0x1020304050607080UL;
        private static int passed;
        private static int failed;

        private sealed class TestLeaseCoordinator
            : ITransactionLeaseCoordinator
        {
            private int depth;

            internal bool Held
            {
                get { return depth > 0; }
            }

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
                        "Transaction lease is not held.");
                }
            }

            private sealed class Lease : IDisposable
            {
                private TestLeaseCoordinator owner;

                internal Lease(TestLeaseCoordinator value)
                {
                    owner = value;
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

        private sealed class FakeCheckpointStore
            : IProtectedPayloadWorkspaceCheckpointStore
        {
            private readonly TestLeaseCoordinator lease;
            internal PayloadBuildWorkspaceCheckpoint State;
            internal PayloadWorkspaceCheckpointReadSource Source =
                PayloadWorkspaceCheckpointReadSource.Primary;
            internal bool RequiresRepair;
            internal bool ThrowCommittedOnNextSave;
            internal bool ThrowUncommittedOnNextSave;
            internal bool CorruptCommittedRevisionOnNextSave;
            internal bool CorruptCommittedDigestOnNextSave;
            internal Action BeforeSave;
            internal int SaveCount;
            internal int LoadCount;

            internal FakeCheckpointStore(
                TestLeaseCoordinator coordinator,
                PayloadBuildWorkspaceCheckpoint initial)
            {
                lease = coordinator;
                State = initial.DeepClone();
            }

            public PayloadWorkspaceCheckpointReceipt Initialize(
                PayloadBuildWorkspaceCheckpoint candidate)
            {
                lease.DemandHeld();
                State = candidate.DeepClone();
                return Receipt();
            }

            public PayloadWorkspaceCheckpointReadResult Load()
            {
                lease.DemandHeld();
                ++LoadCount;
                return new PayloadWorkspaceCheckpointReadResult
                {
                    Receipt = Receipt(),
                    Source = Source,
                    RequiresPrimaryRepair = RequiresRepair
                };
            }

            public PayloadWorkspaceCheckpointReceipt Save(
                PayloadWorkspaceCasToken expected,
                PayloadBuildWorkspaceCheckpoint candidate)
            {
                lease.DemandHeld();
                new PayloadBuildWorkspaceState(State).RequireCas(expected);
                candidate.Validate();
                if (candidate.Revision != checked(State.Revision + 1))
                {
                    throw new InvalidOperationException(
                        "Fake checkpoint store rejected a revision skip.");
                }
                if (BeforeSave != null)
                {
                    BeforeSave();
                }
                if (ThrowUncommittedOnNextSave)
                {
                    ThrowUncommittedOnNextSave = false;
                    throw new PayloadWorkspaceCheckpointPublicationException(
                        false,
                        new IOException(
                            "Injected pre-commit checkpoint failure."));
                }
                State = candidate.DeepClone();
                ++SaveCount;
                if (ThrowCommittedOnNextSave)
                {
                    ThrowCommittedOnNextSave = false;
                    if (CorruptCommittedRevisionOnNextSave)
                    {
                        CorruptCommittedRevisionOnNextSave = false;
                        State.Revision = checked(State.Revision + 1);
                    }
                    if (CorruptCommittedDigestOnNextSave)
                    {
                        CorruptCommittedDigestOnNextSave = false;
                        State.RecoveryGeneration =
                            checked(State.RecoveryGeneration + 1);
                    }
                    throw new PayloadWorkspaceCheckpointPublicationException(
                        true,
                        new IOException("Injected committed readback loss."));
                }
                return Receipt();
            }

            public PayloadWorkspaceCheckpointReceipt RepairPrimary(
                PayloadWorkspaceCheckpointReadResult expectedBackup)
            {
                throw new NotSupportedException();
            }

            internal void AdvanceForeignRevision()
            {
                State.Revision = checked(State.Revision + 1);
                State.Validate();
            }

            private PayloadWorkspaceCheckpointReceipt Receipt()
            {
                return new PayloadWorkspaceCheckpointReceipt(
                    new PayloadBuildWorkspaceState(State));
            }
        }

        private sealed class FakePhysicalNamespace
        {
            internal readonly List<PayloadBuildStepKind> AppliedSteps =
                new List<PayloadBuildStepKind>();
            internal bool QuarantineExists;
            internal int DeleteCount;
        }

        private sealed class FakeNativeTree : IProtectedPayloadNativeTree
        {
            private readonly TestLeaseCoordinator transactionLease;
            private readonly FakePhysicalNamespace physical;
            internal bool SessionHeld;
            internal bool Disposed;
            internal bool BreakAfterAbsence;
            internal bool ReturnForeignStep;
            internal bool ReturnForeignAbsenceIdentity;
            internal bool ReturnExistingAbsence;
            internal int ApplyCount;
            internal int ValidateCount;
            internal int OpenCount;

            internal FakeNativeTree(
                TestLeaseCoordinator coordinator,
                FakePhysicalNamespace sharedPhysical)
            {
                transactionLease = coordinator;
                physical = sharedPhysical;
            }

            public IProtectedPayloadNativeTreeSession OpenExclusive(
                PayloadNamespaceRootIdentity expectedRoot)
            {
                if (Disposed || SessionHeld)
                {
                    throw new InvalidOperationException(
                        "Fake native tree session state is invalid.");
                }
                transactionLease.DemandHeld();
                expectedRoot.Validate();
                SessionHeld = true;
                ++OpenCount;
                return new Session(this);
            }

            public void Dispose()
            {
                Disposed = true;
            }

            private sealed class Session
                : IProtectedPayloadNativeTreeSession
            {
                private FakeNativeTree owner;

                internal Session(FakeNativeTree value)
                {
                    owner = value;
                }

                public void DemandNamespaceExclusionHeld()
                {
                    RequireHeld();
                }

                public void ValidateCheckpoint(
                    PayloadBuildWorkspaceCheckpoint checkpoint)
                {
                    RequireHeld();
                    checkpoint.Validate();
                    ++owner.ValidateCount;
                }

                public PayloadBuildPhysicalResult ApplyBuildStepExact(
                    PayloadBuildMutationPlan plan,
                    ITrustedReleasePayloadSource source)
                {
                    RequireHeld();
                    ++owner.ApplyCount;
                    PayloadBuildStepKind expected =
                        plan.StepKind.Value;
                    owner.physical.AppliedSteps.Add(expected);
                    PayloadBuildStepKind returned =
                        owner.ReturnForeignStep
                            ? PayloadBuildStepKind.FlushFile
                            : expected;
                    owner.ReturnForeignStep = false;
                    if (expected == PayloadBuildStepKind.SealCandidate)
                    {
                        return new PayloadBuildPhysicalResult(
                            expected,
                            null,
                            Candidate(plan),
                            null);
                    }
                    if (expected ==
                        PayloadBuildStepKind.QuarantineBuild)
                    {
                        owner.physical.QuarantineExists = true;
                        return new PayloadBuildPhysicalResult(
                            expected,
                            null,
                            null,
                            Quarantine(plan));
                    }
                    PayloadPartialTreeObservation partial =
                        ApplyPartial(plan, source);
                    return new PayloadBuildPhysicalResult(
                        returned,
                        partial,
                        null,
                        null);
                }

                public void DeleteQuarantineTreeExact(
                    PayloadQuarantineCheckpoint quarantine,
                    PayloadPurgeCheckpoint purge)
                {
                    RequireHeld();
                    quarantine.Validate();
                    purge.Validate();
                    ++owner.physical.DeleteCount;
                    owner.physical.QuarantineExists = false;
                }

                public PayloadQuarantineAbsenceObservation
                    ObserveQuarantineAbsenceExact(
                        PayloadBuildWorkspaceCheckpoint checkpoint,
                        PayloadQuarantineCheckpoint quarantine)
                {
                    RequireHeld();
                    var observation =
                        new PayloadQuarantineAbsenceObservation
                        {
                            SchemaVersion = 1,
                            TransactionId =
                                checkpoint.TransactionId,
                            RecoveryAuthorityInvariantDigest =
                                checkpoint.
                                    RecoveryAuthorityInvariantDigest,
                            NamespaceRootInvariantDigest =
                                checkpoint.NamespaceRoot.
                                    InvariantDigest,
                            QuarantineId =
                                quarantine.QuarantineId,
                            QuarantineLeafName =
                                quarantine.QuarantineLeafName,
                            VolumeSerialNumber =
                                owner.ReturnForeignAbsenceIdentity
                                    ? quarantine.VolumeSerialNumber + 1
                                    : quarantine.VolumeSerialNumber,
                            RootFileId =
                                owner.ReturnForeignAbsenceIdentity
                                    ? Id(9999)
                                    : quarantine.RootFileId,
                            ObservedAtWorkspaceRevision =
                                checkpoint.Revision,
                            Exists =
                                owner.ReturnExistingAbsence ||
                                owner.physical.QuarantineExists
                        };
                    owner.ReturnForeignAbsenceIdentity = false;
                    owner.ReturnExistingAbsence = false;
                    if (owner.BreakAfterAbsence)
                    {
                        owner.BreakAfterAbsence = false;
                        owner.SessionHeld = false;
                    }
                    return observation;
                }

                public void Dispose()
                {
                    if (owner != null)
                    {
                        owner.SessionHeld = false;
                        owner = null;
                    }
                }

                private void RequireHeld()
                {
                    if (owner == null || !owner.SessionHeld)
                    {
                        throw new InvalidOperationException(
                            "Native namespace exclusion is not held.");
                    }
                    owner.transactionLease.DemandHeld();
                }

                private static PayloadPartialTreeObservation ApplyPartial(
                    PayloadBuildMutationPlan plan,
                    ITrustedReleasePayloadSource source)
                {
                    PayloadBuildWorkspaceCheckpoint before =
                        plan.Before.Checkpoint;
                    PayloadBuildStepKind step =
                        plan.StepKind.Value;
                    if (step == PayloadBuildStepKind.CreateRoot)
                    {
                        return new PayloadPartialTreeObservation
                        {
                            SchemaVersion = 1,
                            BuildId = plan.BuildId,
                            LeafName =
                                ".SBMS.build." + plan.BuildId,
                            Exists = true,
                            VolumeSerialNumber = Volume,
                            RootFileId = Id(500),
                            Entries =
                                new List<PayloadTreeEntryCheckpoint>()
                        };
                    }
                    PayloadPartialTreeObservation partial =
                        before.ActivePartialTree.DeepClone();
                    PayloadBuildStepIntent intent =
                        before.ActiveBuild.ActiveIntent;
                    PayloadBuildEntryCheckpoint entry =
                        before.ActiveBuild.Entries[
                            intent.EntryOrdinal];
                    PayloadTreeEntryCheckpoint observed =
                        Find(partial, entry.RelativePath);
                    if (step == PayloadBuildStepKind.CreateEntry)
                    {
                        if (observed == null)
                        {
                            observed =
                                new PayloadTreeEntryCheckpoint
                                {
                                    RelativePath =
                                        entry.RelativePath,
                                    IsDirectory =
                                        entry.IsDirectory,
                                    FileId =
                                        Id(501 + entry.Ordinal),
                                    Length = 0,
                                    Sha256 =
                                        entry.IsDirectory
                                            ? String.Empty
                                            : Sha(new byte[0])
                                };
                            partial.Entries.Add(observed);
                            partial.Entries.Sort(
                                delegate(
                                    PayloadTreeEntryCheckpoint first,
                                    PayloadTreeEntryCheckpoint second)
                                {
                                    return StringComparer.Ordinal.Compare(
                                        first.RelativePath,
                                        second.RelativePath);
                                });
                        }
                    }
                    else if (step ==
                        PayloadBuildStepKind.RewriteFileExact)
                    {
                        TargetPayloadEntry target =
                            Find(plan.Manifest, entry.RelativePath);
                        byte[] bytes;
                        using (Stream input = source.OpenExact(target))
                        using (var output = new MemoryStream())
                        {
                            input.CopyTo(output);
                            bytes = output.ToArray();
                        }
                        observed.Length = bytes.Length;
                        observed.Sha256 = Sha(bytes);
                    }
                    return partial;
                }

                private static PayloadDirectoryCheckpoint Candidate(
                    PayloadBuildMutationPlan plan)
                {
                    PayloadBuildWorkspaceCheckpoint before =
                        plan.Before.Checkpoint;
                    TargetPayloadManifest manifest =
                        plan.Manifest;
                    long total = 0;
                    foreach (TargetPayloadEntry entry in
                        manifest.Content)
                    {
                        total += entry.Length;
                    }
                    var candidate =
                        new PayloadDirectoryCheckpoint
                        {
                            TransactionId =
                                manifest.TransactionId,
                            Slot =
                                PayloadDirectorySlot.Candidate,
                            VolumeSerialNumber =
                                before.ActivePartialTree.
                                    VolumeSerialNumber,
                            FileId =
                                before.ActivePartialTree.RootFileId,
                            Release =
                                new ReleaseIdentity(
                                    manifest.Target.Version,
                                    manifest.Target.
                                        PackageFingerprint),
                            ContentSetSha256 =
                                manifest.ContentSetSha256,
                            ManifestInvariantDigest =
                                manifest.InvariantDigest,
                            FileCount = manifest.Content.Count,
                            TotalBytes = total
                        };
                    foreach (PayloadTreeEntryCheckpoint entry in
                        before.ActivePartialTree.Entries)
                    {
                        candidate.Entries.Add(entry.DeepClone());
                    }
                    return candidate;
                }

                private static PayloadQuarantineCheckpoint Quarantine(
                    PayloadBuildMutationPlan plan)
                {
                    PayloadBuildWorkspaceCheckpoint before =
                        plan.Before.Checkpoint;
                    PayloadPartialTreeObservation observed =
                        before.ActivePartialTree;
                    return new PayloadQuarantineCheckpoint
                    {
                        SchemaVersion = 1,
                        QuarantineId = plan.QuarantineId,
                        TransactionId = before.TransactionId,
                        RecoveryAuthorityInvariantDigest =
                            before.RecoveryAuthorityInvariantDigest,
                        NamespaceRootInvariantDigest =
                            before.NamespaceRoot.InvariantDigest,
                        SourceKind =
                            PayloadQuarantineSourceKind.PartialBuild,
                        SourceBuildId =
                            before.ActiveBuild.BuildId,
                        QuarantineLeafName =
                            ".SBMS.quarantine." +
                                plan.QuarantineId,
                        VolumeSerialNumber =
                            observed.VolumeSerialNumber,
                        RootFileId = observed.RootFileId,
                        PartialTreeInvariantDigest =
                            observed.InvariantDigest,
                        Reason = plan.QuarantineReason,
                        SourceLeafName = observed.LeafName,
                        TargetManifestInvariantDigest =
                            before.ActiveBuild.
                                TargetManifestInvariantDigest,
                        SourceReceiptInvariantDigest =
                            before.ActiveBuild.
                                SourceReceiptInvariantDigest
                    };
                }
            }
        }

        private sealed class FakePayloadSource
            : ITrustedReleasePayloadSource
        {
            private readonly Dictionary<string, byte[]> content;
            private bool disposed;

            internal FakePayloadSource(
                TargetPayloadManifest manifest,
                Dictionary<string, byte[]> files)
            {
                Receipt =
                    new TrustedReleasePayloadReceipt(manifest);
                content = files;
            }

            public TrustedReleasePayloadReceipt Receipt
            {
                get;
                private set;
            }

            public Stream OpenExact(TargetPayloadEntry expected)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(
                        "FakePayloadSource");
                }
                byte[] bytes;
                if (!content.TryGetValue(
                        expected.RelativePath,
                        out bytes))
                {
                    throw new FileNotFoundException(
                        expected.RelativePath);
                }
                return new MemoryStream(
                    (byte[])bytes.Clone(),
                    false);
            }

            public void Dispose()
            {
                disposed = true;
            }
        }

        private sealed class Fixture
        {
            internal TestLeaseCoordinator Lease;
            internal FakeCheckpointStore Store;
            internal FakeNativeTree Tree;
            internal FakePhysicalNamespace Physical;
            internal TargetPayloadManifest Manifest;
            internal PayloadRecoveryAuthority Authority;
            internal Dictionary<string, byte[]> Files;

            internal DurableProtectedPayloadBuildWorkspaceModel OpenModel()
            {
                return new DurableProtectedPayloadBuildWorkspaceModel(
                    Store,
                    Lease,
                    Tree);
            }

            internal FakePayloadSource Source()
            {
                return new FakePayloadSource(Manifest, Files);
            }
        }

        public static int Main()
        {
            Run("inspect validates under both leases",
                InspectValidatesUnderBothLeases);
            Run("durable model publishes a full candidate",
                DurableModelPublishesCandidate);
            Run("build pre-commit failure replays the exact physical step",
                BuildPreCommitFailureReplaysExactPhysicalStep);
            Run("purge pre-commit failure replays exact deletion",
                PurgePreCommitFailureReplaysExactDeletion);
            Run("physical commit lost return reconciles exactly",
                PhysicalCommitLostReturnReconciles);
            Run("committed recovery rejects foreign revision",
                CommittedRecoveryRejectsForeignRevision);
            Run("committed recovery rejects foreign digest",
                CommittedRecoveryRejectsForeignDigest);
            Run("committed recovery rejects non-primary reads",
                CommittedRecoveryRejectsNonPrimaryReads);
            Run("stale durable plan is rejected before native IO",
                StalePlanRejectedBeforeNativeIo);
            Run("backup checkpoint is never treated as authoritative",
                BackupCheckpointIsRejected);
            Run("purge native session spans absence through checkpoint CAS",
                PurgeSessionLifetimeSpansAbsenceThroughCas);
            Run("broken purge session lifetime blocks control commit",
                BrokenPurgeSessionLifetimeBlocksCommit);
            Run("foreign purge absence evidence is rejected",
                ForeignPurgeAbsenceEvidenceIsRejected);
            Run("foreign physical step is rejected",
                ForeignPhysicalStepIsRejected);
            Run("disposed durable model rejects access",
                DisposedModelRejectsAccess);

            Console.WriteLine(
                "Durable payload workspace model tests: " +
                passed + " passed, " + failed + " failed.");
            return failed == 0 ? 0 : 1;
        }

        private static void InspectValidatesUnderBothLeases()
        {
            Fixture fixture = FreshFixture();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            {
                PayloadBuildWorkspaceState state = model.Inspect();
                Equal(
                    fixture.Store.State.InvariantDigest,
                    state.InvariantDigest,
                    "Inspect changed the durable workspace.");
                Equal(
                    1,
                    fixture.Tree.ValidateCount,
                    "Inspect did not validate the native namespace.");
                True(
                    !fixture.Lease.Held &&
                    !fixture.Tree.SessionHeld,
                    "Inspect leaked a transaction or namespace lease.");
            }
        }

        private static void DurableModelPublishesCandidate()
        {
            Fixture fixture = FreshFixture();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            using (FakePayloadSource source = fixture.Source())
            using (
                var machine =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        model))
            {
                PayloadBuildAdvanceResult result = RunToCandidate(
                    machine,
                    source,
                    fixture.Manifest);
                Equal(
                    PayloadBuildAdvanceKind.CandidatePublished,
                    result.Kind,
                    "Durable model did not publish a candidate.");
                True(
                    fixture.Store.State.Committed.Candidate != null &&
                    fixture.Store.State.ActiveBuild == null,
                    "Durable candidate did not become terminal.");
                True(
                    SameSteps(
                        fixture.Physical.AppliedSteps,
                        new[]
                        {
                            PayloadBuildStepKind.CreateRoot,
                            PayloadBuildStepKind.CreateEntry,
                            PayloadBuildStepKind.RewriteFileExact,
                            PayloadBuildStepKind.FlushFile,
                            PayloadBuildStepKind.ReopenEntry,
                            PayloadBuildStepKind.VerifyEntryHash,
                            PayloadBuildStepKind.CreateEntry,
                            PayloadBuildStepKind.ReopenEntry,
                            PayloadBuildStepKind.VerifyEntryHash,
                            PayloadBuildStepKind.CreateEntry,
                            PayloadBuildStepKind.RewriteFileExact,
                            PayloadBuildStepKind.FlushFile,
                            PayloadBuildStepKind.ReopenEntry,
                            PayloadBuildStepKind.VerifyEntryHash,
                            PayloadBuildStepKind.SealCandidate
                        }),
                    "Native model did not execute the exact ordered step set.");
            }
        }

        private static void BuildPreCommitFailureReplaysExactPhysicalStep()
        {
            Fixture fixture = FreshFixture();
            string buildId = Id(2050);
            using (FakePayloadSource source = fixture.Source())
            {
                using (var machine =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        fixture.OpenModel()))
                {
                    machine.Advance(
                        source,
                        fixture.Manifest,
                        buildId,
                        Id(3050));
                    machine.Advance(
                        source,
                        fixture.Manifest,
                        buildId,
                        Id(3051));
                    long beforeRevision = fixture.Store.State.Revision;
                    fixture.Store.ThrowUncommittedOnNextSave = true;
                    Throws<PayloadWorkspaceCheckpointPublicationException>(
                        delegate
                        {
                            machine.Advance(
                                source,
                                fixture.Manifest,
                                buildId,
                                Id(3052));
                        },
                        "Pre-commit build failure was not surfaced.");
                    Equal(
                        beforeRevision,
                        fixture.Store.State.Revision,
                        "Pre-commit build failure advanced durable state.");
                }
                RestartTree(fixture);
                using (var restarted =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        fixture.OpenModel()))
                {
                    PayloadBuildAdvanceResult replayed =
                        restarted.Advance(
                            source,
                            fixture.Manifest,
                            buildId,
                            Id(3053));
                    Equal(
                        PayloadBuildAdvanceKind.InProgress,
                        replayed.Kind,
                        "Create-root replay did not resume the build.");
                    Equal(
                        Id(500),
                        fixture.Store.State.ActiveBuild.RootFileId,
                        "Create-root replay changed physical identity.");
                }
            }
            True(
                SameSteps(
                    fixture.Physical.AppliedSteps,
                    new[]
                    {
                        PayloadBuildStepKind.CreateRoot,
                        PayloadBuildStepKind.CreateRoot
                    }),
                "Create-root physical step was not replayed exactly once.");
        }

        private static void PurgePreCommitFailureReplaysExactDeletion()
        {
            Fixture fixture = QuarantinedFixture();
            string quarantineId =
                fixture.Store.State.Quarantines[0].QuarantineId;
            string purgeId = Id(7250);
            using (var machine =
                new DeterministicProtectedPayloadBuildStateMachine(
                    fixture.Authority,
                    fixture.OpenModel()))
            {
                machine.AdvancePurge(quarantineId, purgeId);
                long armedRevision = fixture.Store.State.Revision;
                fixture.Store.ThrowUncommittedOnNextSave = true;
                Throws<PayloadWorkspaceCheckpointPublicationException>(
                    delegate
                    {
                        machine.AdvancePurge(quarantineId, purgeId);
                    },
                    "Pre-commit purge failure was not surfaced.");
                Equal(
                    armedRevision,
                    fixture.Store.State.Revision,
                    "Pre-commit purge failure advanced durable state.");
            }
            RestartTree(fixture);
            using (var restarted =
                new DeterministicProtectedPayloadBuildStateMachine(
                    fixture.Authority,
                    fixture.OpenModel()))
            {
                PayloadPurgeAdvanceResult replayed =
                    restarted.AdvancePurge(quarantineId, purgeId);
                Equal(
                    PayloadPurgeAdvanceKind.ObservedAbsent,
                    replayed.Kind,
                    "Purge replay did not reconcile physical absence.");
            }
            Equal(
                2,
                fixture.Physical.DeleteCount,
                "Purge deletion was not replayed exactly once.");
        }

        private static void PhysicalCommitLostReturnReconciles()
        {
            Fixture fixture = FreshFixture();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            using (FakePayloadSource source = fixture.Source())
            using (
                var machine =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        model))
            {
                string buildId = Id(2000);
                machine.Advance(
                    source,
                    fixture.Manifest,
                    buildId,
                    Id(3000));
                machine.Advance(
                    source,
                    fixture.Manifest,
                    buildId,
                    Id(3001));
                fixture.Store.ThrowCommittedOnNextSave = true;
                PayloadBuildAdvanceResult completed =
                    machine.Advance(
                        source,
                        fixture.Manifest,
                        buildId,
                        Id(3002));
                Equal(
                    PayloadBuildAdvanceKind.InProgress,
                    completed.Kind,
                    "Committed physical step was not reconciled.");
                True(
                    fixture.Store.State.ActiveBuild.RootFileId ==
                        Id(500),
                    "Recovered create-root state was not durable.");
                True(
                    fixture.Store.LoadCount >= 4,
                    "Committed publication was not reloaded exactly.");
            }
        }

        private static void CommittedRecoveryRejectsForeignRevision()
        {
            Fixture fixture = FreshFixture();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            using (FakePayloadSource source = fixture.Source())
            using (
                var machine =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        model))
            {
                PrepareCreateRootIntent(
                    machine,
                    source,
                    fixture.Manifest,
                    Id(2060),
                    3060);
                fixture.Store.ThrowCommittedOnNextSave = true;
                fixture.Store.CorruptCommittedRevisionOnNextSave = true;
                Throws<InvalidDataException>(
                    delegate
                    {
                        machine.Advance(
                            source,
                            fixture.Manifest,
                            Id(2060),
                            Id(3062));
                    },
                    "Foreign committed revision was reconciled.");
            }
        }

        private static void CommittedRecoveryRejectsForeignDigest()
        {
            Fixture fixture = FreshFixture();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            using (FakePayloadSource source = fixture.Source())
            using (
                var machine =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        model))
            {
                PrepareCreateRootIntent(
                    machine,
                    source,
                    fixture.Manifest,
                    Id(2070),
                    3070);
                fixture.Store.ThrowCommittedOnNextSave = true;
                fixture.Store.CorruptCommittedDigestOnNextSave = true;
                Throws<InvalidDataException>(
                    delegate
                    {
                        machine.Advance(
                            source,
                            fixture.Manifest,
                            Id(2070),
                            Id(3072));
                    },
                    "Foreign committed digest was reconciled.");
            }
        }

        private static void CommittedRecoveryRejectsNonPrimaryReads()
        {
            AssertCommittedRecoveryReadRejected(
                PayloadWorkspaceCheckpointReadSource.Backup,
                false,
                Id(2080),
                3080);
            AssertCommittedRecoveryReadRejected(
                PayloadWorkspaceCheckpointReadSource.Primary,
                true,
                Id(2090),
                3090);
        }

        private static void AssertCommittedRecoveryReadRejected(
            PayloadWorkspaceCheckpointReadSource sourceKind,
            bool requiresRepair,
            string buildId,
            int intentBase)
        {
            Fixture fixture = FreshFixture();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            using (FakePayloadSource source = fixture.Source())
            using (
                var machine =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        model))
            {
                PrepareCreateRootIntent(
                    machine,
                    source,
                    fixture.Manifest,
                    buildId,
                    intentBase);
                fixture.Store.ThrowCommittedOnNextSave = true;
                fixture.Store.BeforeSave =
                    delegate
                    {
                        fixture.Store.Source = sourceKind;
                        fixture.Store.RequiresRepair = requiresRepair;
                    };
                Throws<InvalidDataException>(
                    delegate
                    {
                        machine.Advance(
                            source,
                            fixture.Manifest,
                            buildId,
                            Id(intentBase + 2));
                    },
                    "Non-primary committed recovery was accepted.");
            }
        }

        private static void PrepareCreateRootIntent(
            DeterministicProtectedPayloadBuildStateMachine machine,
            FakePayloadSource source,
            TargetPayloadManifest manifest,
            string buildId,
            int intentBase)
        {
            machine.Advance(
                source,
                manifest,
                buildId,
                Id(intentBase));
            machine.Advance(
                source,
                manifest,
                buildId,
                Id(intentBase + 1));
        }

        private static void StalePlanRejectedBeforeNativeIo()
        {
            Fixture fixture = FreshFixture();
            PayloadBuildWorkspaceState before =
                new PayloadBuildWorkspaceState(
                    fixture.Store.State);
            PayloadBuildMutationPlan plan =
                PayloadBuildMutationPlan.Begin(
                    fixture.Authority,
                    before,
                    fixture.Manifest,
                    new TrustedReleasePayloadReceipt(
                        fixture.Manifest),
                    Id(2100));
            fixture.Store.AdvanceForeignRevision();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            using (FakePayloadSource source = fixture.Source())
            {
                Throws<InvalidOperationException>(
                    delegate { model.ApplyExact(plan, source); },
                    "Stale durable plan reached native IO.");
                Equal(
                    0,
                    fixture.Tree.ApplyCount,
                    "Stale plan mutated the native namespace.");
            }
        }

        private static void BackupCheckpointIsRejected()
        {
            Fixture fixture = FreshFixture();
            fixture.Store.Source =
                PayloadWorkspaceCheckpointReadSource.Backup;
            fixture.Store.RequiresRepair = true;
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            {
                Throws<InvalidDataException>(
                    delegate { model.Inspect(); },
                    "Backup checkpoint was treated as authoritative.");
                Equal(
                    0,
                    fixture.Tree.OpenCount,
                    "Backup checkpoint reached native namespace IO.");
            }
        }

        private static void PurgeSessionLifetimeSpansAbsenceThroughCas()
        {
            Fixture fixture = QuarantinedFixture();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            using (
                var machine =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        model))
            {
                string quarantineId =
                    fixture.Store.State.Quarantines[0].
                        QuarantineId;
                string purgeId = Id(7200);
                machine.AdvancePurge(quarantineId, purgeId);
                int guardedSaves = 0;
                fixture.Store.BeforeSave =
                    delegate
                    {
                        if (!fixture.Tree.SessionHeld)
                        {
                            throw new InvalidOperationException(
                                "Checkpoint CAS escaped namespace exclusion.");
                        }
                        ++guardedSaves;
                    };
                machine.AdvancePurge(quarantineId, purgeId);
                machine.AdvancePurge(quarantineId, purgeId);
                Equal(
                    2,
                    guardedSaves,
                    "Purge absence transitions did not hold exclusion.");
                Equal(
                    1,
                    fixture.Store.State.CompletedPurges.Count,
                    "Purge did not publish terminal evidence.");
            }
        }

        private static void BrokenPurgeSessionLifetimeBlocksCommit()
        {
            Fixture fixture = QuarantinedFixture();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            using (
                var machine =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        model))
            {
                string quarantineId =
                    fixture.Store.State.Quarantines[0].
                        QuarantineId;
                string purgeId = Id(7300);
                machine.AdvancePurge(quarantineId, purgeId);
                long armedRevision =
                    fixture.Store.State.Revision;
                fixture.Tree.BreakAfterAbsence = true;
                Throws<InvalidOperationException>(
                    delegate
                    {
                        machine.AdvancePurge(
                            quarantineId,
                            purgeId);
                    },
                    "Broken namespace exclusion committed absence.");
                Equal(
                    armedRevision,
                    fixture.Store.State.Revision,
                    "Broken exclusion advanced the checkpoint.");
                PayloadPurgeAdvanceResult retried =
                    machine.AdvancePurge(
                        quarantineId,
                        purgeId);
                Equal(
                    PayloadPurgeAdvanceKind.ObservedAbsent,
                    retried.Kind,
                    "Delete-before-CAS crash did not reconcile.");
            }
        }

        private static void ForeignPurgeAbsenceEvidenceIsRejected()
        {
            AssertForeignPurgeAbsenceRejected(true, false, Id(7350));
            AssertForeignPurgeAbsenceRejected(false, true, Id(7360));
        }

        private static void AssertForeignPurgeAbsenceRejected(
            bool foreignIdentity,
            bool reportsExisting,
            string purgeId)
        {
            Fixture fixture = QuarantinedFixture();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            using (
                var machine =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        model))
            {
                string quarantineId =
                    fixture.Store.State.Quarantines[0].QuarantineId;
                machine.AdvancePurge(quarantineId, purgeId);
                long armedRevision = fixture.Store.State.Revision;
                fixture.Tree.ReturnForeignAbsenceIdentity =
                    foreignIdentity;
                fixture.Tree.ReturnExistingAbsence = reportsExisting;
                Throws<InvalidOperationException>(
                    delegate
                    {
                        machine.AdvancePurge(
                            quarantineId,
                            purgeId);
                    },
                    "Foreign purge absence evidence was committed.");
                Equal(
                    armedRevision,
                    fixture.Store.State.Revision,
                    "Foreign absence evidence advanced the checkpoint.");
                Equal(
                    PayloadPurgePhase.Armed,
                    fixture.Store.State.PendingPurges[0].Phase,
                    "Foreign absence evidence changed the purge phase.");
            }
        }

        private static void ForeignPhysicalStepIsRejected()
        {
            Fixture fixture = FreshFixture();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            using (FakePayloadSource source = fixture.Source())
            using (
                var machine =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        model))
            {
                string buildId = Id(2400);
                machine.Advance(
                    source,
                    fixture.Manifest,
                    buildId,
                    Id(3400));
                machine.Advance(
                    source,
                    fixture.Manifest,
                    buildId,
                    Id(3401));
                long revision = fixture.Store.State.Revision;
                fixture.Tree.ReturnForeignStep = true;
                Throws<InvalidOperationException>(
                    delegate
                    {
                        machine.Advance(
                            source,
                            fixture.Manifest,
                            buildId,
                            Id(3402));
                    },
                    "Foreign physical step was committed.");
                Equal(
                    revision,
                    fixture.Store.State.Revision,
                    "Foreign physical step advanced the checkpoint.");
            }
        }

        private static void DisposedModelRejectsAccess()
        {
            Fixture fixture = FreshFixture();
            DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel();
            model.Dispose();
            Throws<ObjectDisposedException>(
                delegate { model.Inspect(); },
                "Disposed durable model remained usable.");
            True(
                fixture.Tree.Disposed,
                "Disposed durable model retained its native tree.");
        }

        private static PayloadBuildAdvanceResult RunToCandidate(
            DeterministicProtectedPayloadBuildStateMachine machine,
            FakePayloadSource source,
            TargetPayloadManifest manifest)
        {
            string buildId = Id(2000);
            for (int attempt = 0; attempt < 40; ++attempt)
            {
                PayloadBuildAdvanceResult result =
                    machine.Advance(
                        source,
                        manifest,
                        buildId,
                        Id(3000 + attempt));
                if (result.Kind !=
                    PayloadBuildAdvanceKind.InProgress)
                {
                    return result;
                }
            }
            throw new InvalidOperationException(
                "Payload build did not reach a terminal candidate.");
        }

        private static Fixture QuarantinedFixture()
        {
            Fixture fixture = FreshFixture();
            using (DurableProtectedPayloadBuildWorkspaceModel model =
                fixture.OpenModel())
            using (FakePayloadSource source = fixture.Source())
            using (
                var machine =
                    new DeterministicProtectedPayloadBuildStateMachine(
                        fixture.Authority,
                        model))
            {
                string buildId = Id(2500);
                machine.Advance(
                    source,
                    fixture.Manifest,
                    buildId,
                    Id(3500));
                machine.Advance(
                    source,
                    fixture.Manifest,
                    buildId,
                    Id(3501));
                machine.Advance(
                    source,
                    fixture.Manifest,
                    buildId,
                    Id(3502));
                string quarantineId = Id(7100);
                machine.Quarantine(
                    source,
                    fixture.Manifest,
                    quarantineId,
                    quarantineId,
                    PayloadQuarantineReason.InterruptedBuild);
                machine.Quarantine(
                    source,
                    fixture.Manifest,
                    quarantineId,
                    quarantineId,
                    PayloadQuarantineReason.InterruptedBuild);
            }
            // The first model owns and disposes its tree. A restart creates a
            // fresh native adapter over the same physical namespace.
            RestartTree(fixture);
            return fixture;
        }

        private static Fixture FreshFixture()
        {
            Dictionary<string, byte[]> files =
                new Dictionary<string, byte[]>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    { "SBMS.exe", new byte[] { 1, 2, 3, 4 } },
                    {
                        @"driver\SBMS.dll",
                        new byte[] { 5, 6, 7 }
                    }
                };
            TargetPayloadManifest manifest =
                CreateManifest(files);
            PayloadDirectoryCheckpoint target =
                Directory(manifest);
            var authority = new PayloadRecoveryAuthority
            {
                SchemaVersion = 1,
                TransactionId = TransactionId,
                Operation = InstallOperation.FreshInstall,
                BaselineState = BaselinePayloadState.Absent,
                Baseline = null,
                Target = ContentAuthority(target),
                SealedEscrowManifestSha256 =
                    Sha(new byte[] { 7 })
            };
            var initial =
                new PayloadBuildWorkspaceCheckpoint
                {
                    SchemaVersion = 3,
                    Revision = 11,
                    RecoveryGeneration = 0,
                    TransactionId = TransactionId,
                    RecoveryAuthorityInvariantDigest =
                        authority.InvariantDigest,
                    NamespaceRoot =
                        new PayloadNamespaceRootIdentity
                        {
                            SchemaVersion = 1,
                            CanonicalRootPath =
                                @"C:\Program Files\SBMS",
                            VolumeSerialNumber = Volume,
                            RootFileId = Id(10)
                        },
                    Committed =
                        new PayloadNamespaceCheckpoint
                        {
                            SchemaVersion = 1,
                            Revision = 7,
                            TransactionId = TransactionId,
                            Shape =
                                PayloadNamespaceShape.Empty
                        }
                };
            var lease = new TestLeaseCoordinator();
            var physical = new FakePhysicalNamespace();
            return new Fixture
            {
                Lease = lease,
                Store =
                    new FakeCheckpointStore(lease, initial),
                Tree = new FakeNativeTree(lease, physical),
                Physical = physical,
                Manifest = manifest,
                Authority = authority,
                Files = files
            };
        }

        private static void RestartTree(Fixture fixture)
        {
            fixture.Tree =
                new FakeNativeTree(
                    fixture.Lease,
                    fixture.Physical);
        }

        private static bool SameSteps(
            IList<PayloadBuildStepKind> actual,
            IList<PayloadBuildStepKind> expected)
        {
            if (actual.Count != expected.Count)
            {
                return false;
            }
            for (int index = 0; index < actual.Count; ++index)
            {
                if (actual[index] != expected[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static TargetPayloadManifest CreateManifest(
            Dictionary<string, byte[]> files)
        {
            var manifest = new TargetPayloadManifest
            {
                SchemaVersion = 1,
                TransactionId = TransactionId,
                Target =
                    new ReleaseIdentity(
                        "0.3.0",
                        Sha(new byte[] { 3 })),
                ReleaseCatalogSha256 =
                    Sha(new byte[] { 10 }),
                SignedReleaseManifestSha256 =
                    Sha(new byte[] { 11 })
            };
            foreach (string path in new[]
            {
                "SBMS.exe",
                @"driver\SBMS.dll"
            })
            {
                byte[] bytes = files[path];
                manifest.Content.Add(
                    new TargetPayloadEntry
                    {
                        RelativePath = path,
                        Length = bytes.Length,
                        Sha256 = Sha(bytes)
                    });
            }
            manifest.ContentSetSha256 =
                manifest.ComputeContentSetSha256();
            return manifest;
        }

        private static PayloadDirectoryCheckpoint Directory(
            TargetPayloadManifest manifest)
        {
            var directory =
                new PayloadDirectoryCheckpoint
                {
                    TransactionId = manifest.TransactionId,
                    Slot = PayloadDirectorySlot.Candidate,
                    VolumeSerialNumber = Volume,
                    FileId = Id(500),
                    Release =
                        new ReleaseIdentity(
                            manifest.Target.Version,
                            manifest.Target.PackageFingerprint),
                    ContentSetSha256 =
                        manifest.ContentSetSha256,
                    ManifestInvariantDigest =
                        manifest.InvariantDigest,
                    FileCount = manifest.Content.Count,
                    TotalBytes =
                        manifest.Content[0].Length +
                        manifest.Content[1].Length
                };
            directory.Entries.Add(
                new PayloadTreeEntryCheckpoint
                {
                    RelativePath = "SBMS.exe",
                    IsDirectory = false,
                    FileId = Id(501),
                    Length = manifest.Content[0].Length,
                    Sha256 = manifest.Content[0].Sha256
                });
            directory.Entries.Add(
                new PayloadTreeEntryCheckpoint
                {
                    RelativePath = "driver",
                    IsDirectory = true,
                    FileId = Id(502),
                    Length = 0,
                    Sha256 = String.Empty
                });
            directory.Entries.Add(
                new PayloadTreeEntryCheckpoint
                {
                    RelativePath = @"driver\SBMS.dll",
                    IsDirectory = false,
                    FileId = Id(503),
                    Length = manifest.Content[1].Length,
                    Sha256 = manifest.Content[1].Sha256
                });
            return directory;
        }

        private static PayloadContentAuthority ContentAuthority(
            PayloadDirectoryCheckpoint directory)
        {
            return new PayloadContentAuthority
            {
                Release =
                    new ReleaseIdentity(
                        directory.Release.Version,
                        directory.Release.PackageFingerprint),
                ContentSetSha256 =
                    directory.ContentSetSha256,
                ManifestInvariantDigest =
                    directory.ManifestInvariantDigest,
                SemanticTreeSha256 =
                    directory.SemanticTreeSha256,
                FileCount = directory.FileCount,
                TotalBytes = directory.TotalBytes
            };
        }

        private static PayloadTreeEntryCheckpoint Find(
            PayloadPartialTreeObservation partial,
            string relativePath)
        {
            foreach (PayloadTreeEntryCheckpoint entry in partial.Entries)
            {
                if (String.Equals(
                        entry.RelativePath,
                        relativePath,
                        StringComparison.Ordinal))
                {
                    return entry;
                }
            }
            return null;
        }

        private static TargetPayloadEntry Find(
            TargetPayloadManifest manifest,
            string relativePath)
        {
            foreach (TargetPayloadEntry entry in manifest.Content)
            {
                if (String.Equals(
                        entry.RelativePath,
                        relativePath,
                        StringComparison.Ordinal))
                {
                    return entry;
                }
            }
            throw new InvalidOperationException(
                "Target payload entry was not found.");
        }

        private static string Id(int value)
        {
            return value.ToString("x32");
        }

        private static string Sha(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] digest = algorithm.ComputeHash(bytes);
                return BitConverter.ToString(digest).
                    Replace("-", String.Empty).
                    ToLowerInvariant();
            }
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

        private static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void Equal<T>(
            T expected,
            T actual,
            string message)
        {
            if (!EqualityComparer<T>.Default.Equals(
                    expected,
                    actual))
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
