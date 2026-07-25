using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace SBMSSetup
{
    internal static class ProtectedPayloadTransactionExecutorTests
    {
        private const string TransactionId =
            "11111111111111111111111111111111";
        private const string OtherTransactionId =
            "22222222222222222222222222222222";
        private static int passed;

        private static int Main()
        {
            Run("fresh stage binds authority manifest and before-after CAS", delegate
            {
                Fixture fixture = FreshFixture();
                using (var store = fixture.Open())
                using (var source = fixture.Source())
                {
                    PayloadCandidateReceipt receipt =
                        store.Stage(source, fixture.TargetManifest);
                    Equal(PayloadNamespaceShape.Empty, receipt.Before.Shape);
                    Equal(
                        PayloadNamespaceShape.CandidateOnly,
                        receipt.After.Shape);
                    Equal(
                        fixture.Authority.InvariantDigest,
                        receipt.Authority.InvariantDigest);
                    Equal(
                        fixture.TargetManifest.InvariantDigest,
                        receipt.Manifest.InvariantDigest);
                }
            });
            Run("fresh stage retry is revision-idempotent", delegate
            {
                Fixture fixture = FreshFixture();
                PayloadCandidateReceipt first;
                using (var store = fixture.Open())
                using (var source = fixture.Source())
                {
                    first = store.Stage(source, fixture.TargetManifest);
                }
                using (var store = fixture.Open())
                using (var source = fixture.Source())
                {
                    PayloadCandidateReceipt second =
                        store.Stage(source, fixture.TargetManifest);
                    Equal(first.After.Revision, second.After.Revision);
                    Equal(
                        first.After.InvariantDigest,
                        second.After.InvariantDigest);
                }
            });
            Run("stage crash after sealed publication reenters idempotently", delegate
            {
                Fixture fixture = FreshFixture();
                using (var crashing = fixture.Open(0, true))
                using (var source = fixture.Source())
                {
                    ExpectCrash(delegate
                    {
                        crashing.Stage(
                            source,
                            fixture.TargetManifest);
                    });
                }
                Equal(
                    PayloadNamespaceShape.CandidateOnly,
                    fixture.Raw.State.Shape);
                using (var recovered = fixture.Open())
                using (var source = fixture.Source())
                {
                    long revision = recovered.Inspect().Revision;
                    PayloadCandidateReceipt receipt = recovered.Stage(
                        source,
                        fixture.TargetManifest);
                    Equal(revision, receipt.After.Revision);
                }
            });
            Run("fresh install stage and promotion converge", delegate
            {
                Fixture fixture = FreshFixture();
                using (var store = fixture.Open())
                using (var source = fixture.Source())
                {
                    PayloadCandidateReceipt candidate =
                        store.Stage(source, fixture.TargetManifest);
                    PayloadPromotionReceipt promotion =
                        store.PromoteInstall(
                            fixture.Authority,
                            candidate.After,
                            candidate);
                    Equal(
                        PayloadNamespaceShape.CurrentOnly,
                        promotion.After.Shape);
                    True(
                        fixture.Authority.Target.Matches(
                            promotion.After.Checkpoint.Current));
                }
            });

            foreach (InstallOperation operation in new[]
            {
                InstallOperation.Upgrade,
                InstallOperation.Repair,
                InstallOperation.ExplicitDowngrade
            })
            {
                InstallOperation captured = operation;
                Run(captured + " executor stages and performs two renames", delegate
                {
                    Fixture fixture = ReplacementFixture(captured);
                    using (var store = fixture.Open())
                    using (var source = fixture.Source())
                    {
                        PayloadCandidateReceipt candidate =
                            store.Stage(source, fixture.TargetManifest);
                        PayloadPromotionReceipt promotion =
                            store.PromoteInstall(
                                fixture.Authority,
                                candidate.After,
                                candidate);
                        Equal(
                            PayloadNamespaceShape.CurrentAndBackup,
                            promotion.After.Shape);
                        True(
                            fixture.Authority.Target.Matches(
                                promotion.After.Checkpoint.Current));
                        True(
                            fixture.Authority.Baseline.Matches(
                                promotion.After.Checkpoint.Backup));
                    }
                });
            }

            Run("crash after first replacement rename reenters forward", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                PayloadCandidateReceipt candidate = fixture.Stage();
                using (var crashing = fixture.Open(1))
                {
                    ExpectCrash(delegate
                    {
                        crashing.PromoteInstall(
                            fixture.Authority,
                            candidate.After,
                            candidate);
                    });
                }
                Equal(
                    PayloadNamespaceShape.CandidateAndBackup,
                    fixture.Raw.State.Shape);
                using (var recovered = fixture.Open())
                {
                    PayloadRecoveryReceipt receipt = recovered.Recover(
                        PayloadRecoveryDecision.CompleteForward,
                        recovered.Inspect());
                    Equal(
                        PayloadNamespaceShape.CurrentAndBackup,
                        receipt.After.Shape);
                }
            });
            Run("crash after second replacement rename is terminal on retry", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                PayloadCandidateReceipt candidate = fixture.Stage();
                using (var crashing = fixture.Open(2))
                {
                    ExpectCrash(delegate
                    {
                        crashing.PromoteInstall(
                            fixture.Authority,
                            candidate.After,
                            candidate);
                    });
                }
                Equal(
                    PayloadNamespaceShape.CurrentAndBackup,
                    fixture.Raw.State.Shape);
                using (var recovered = fixture.Open())
                {
                    long revision = recovered.Inspect().Revision;
                    PayloadRecoveryReceipt receipt = recovered.Recover(
                        PayloadRecoveryDecision.CompleteForward,
                        recovered.Inspect());
                    Equal(revision, receipt.After.Revision);
                }
            });
            Run("replacement rollback reenters from backup-only", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                PayloadCandidateReceipt candidate = fixture.Stage();
                using (var crashing = fixture.Open(1))
                {
                    ExpectCrash(delegate
                    {
                        crashing.PromoteInstall(
                            fixture.Authority,
                            candidate.After,
                            candidate);
                    });
                }
                using (var rollback = fixture.Open(1))
                {
                    ExpectCrash(delegate
                    {
                        rollback.Recover(
                            PayloadRecoveryDecision.RestoreBaseline,
                            rollback.Inspect());
                    });
                }
                Equal(
                    PayloadNamespaceShape.BackupOnly,
                    fixture.Raw.State.Shape);
                using (var rollback = fixture.Open())
                {
                    PayloadRecoveryReceipt receipt = rollback.Recover(
                        PayloadRecoveryDecision.RestoreBaseline,
                        rollback.Inspect());
                    Equal(
                        PayloadNamespaceShape.CurrentOnly,
                        receipt.After.Shape);
                    True(
                        fixture.Authority.Baseline.Matches(
                            receipt.After.Checkpoint.Current));
                }
            });
            Run("committed backup cleanup is exact and idempotent", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                fixture.Promote();
                using (var store = fixture.Open())
                {
                    PayloadCleanupReceipt first = store.Cleanup(
                        PayloadCleanupKind.CommittedBackup,
                        store.Inspect());
                    Equal(
                        PayloadNamespaceShape.CurrentOnly,
                        first.After.Shape);
                    long revision = first.After.Revision;
                    PayloadCleanupReceipt second = store.Cleanup(
                        PayloadCleanupKind.CommittedBackup,
                        store.Inspect());
                    Equal(revision, second.After.Revision);
                }
            });
            Run("cleanup crash after logical removal reenters as completed", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                fixture.Promote();
                using (var crashing = fixture.Open(1))
                {
                    ExpectCrash(delegate
                    {
                        crashing.Cleanup(
                            PayloadCleanupKind.CommittedBackup,
                            crashing.Inspect());
                    });
                }
                Equal(
                    PayloadNamespaceShape.CurrentOnly,
                    fixture.Raw.State.Shape);
                using (var recovered = fixture.Open())
                {
                    long revision = recovered.Inspect().Revision;
                    PayloadCleanupReceipt receipt = recovered.Cleanup(
                        PayloadCleanupKind.CommittedBackup,
                        recovered.Inspect());
                    Equal(revision, receipt.After.Revision);
                }
            });
            Run("retryable cleanup reports incomplete without mutation", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                fixture.Promote();
                fixture.Raw.RetryNextApply = true;
                using (var store = fixture.Open())
                {
                    PayloadNamespaceState before = store.Inspect();
                    PayloadCleanupReceipt receipt = store.Cleanup(
                        PayloadCleanupKind.CommittedBackup,
                        before);
                    Equal(false, receipt.Complete);
                    Equal(
                        before.InvariantDigest,
                        receipt.After.InvariantDigest);
                }
            });
            Run("pending maintenance prevents false complete cleanup", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                fixture.Promote();
                fixture.Raw.MarkMaintenancePendingAfterDelete = true;
                using (var store = fixture.Open())
                {
                    ExpectRejected(delegate
                    {
                        store.Cleanup(
                            PayloadCleanupKind.CommittedBackup,
                            store.Inspect());
                    });
                }
                Equal(true, fixture.Raw.MaintenancePending);
                Equal(
                    PayloadNamespaceShape.CurrentOnly,
                    fixture.Raw.State.Shape);
                fixture.Raw.MaintenancePending = false;
                using (var store = fixture.Open())
                {
                    PayloadCleanupReceipt receipt = store.Cleanup(
                        PayloadCleanupKind.CommittedBackup,
                        store.Inspect());
                    Equal(true, receipt.Complete);
                }
            });
            Run("fresh candidate cleanup removes committed-view candidate", delegate
            {
                Fixture fixture = FreshFixture();
                fixture.Stage();
                using (var store = fixture.Open())
                {
                    PayloadCleanupReceipt receipt = store.Cleanup(
                        PayloadCleanupKind.Candidate,
                        store.Inspect());
                    Equal(PayloadNamespaceShape.Empty, receipt.After.Shape);
                }
            });
            Run("replacement candidate cleanup preserves baseline current", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                fixture.Stage();
                using (var store = fixture.Open())
                {
                    PayloadCleanupReceipt receipt = store.Cleanup(
                        PayloadCleanupKind.Candidate,
                        store.Inspect());
                    Equal(
                        PayloadNamespaceShape.CurrentOnly,
                        receipt.After.Shape);
                    True(
                        fixture.Authority.Baseline.Matches(
                            receipt.After.Checkpoint.Current));
                }
            });
            Run("candidate cleanup between renames preserves backup", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                PayloadCandidateReceipt candidate = fixture.Stage();
                using (var crashing = fixture.Open(1))
                {
                    ExpectCrash(delegate
                    {
                        crashing.PromoteInstall(
                            fixture.Authority,
                            candidate.After,
                            candidate);
                    });
                }
                using (var store = fixture.Open())
                {
                    PayloadCleanupReceipt receipt = store.Cleanup(
                        PayloadCleanupKind.Candidate,
                        store.Inspect());
                    Equal(
                        PayloadNamespaceShape.BackupOnly,
                        receipt.After.Shape);
                    True(
                        fixture.Authority.Baseline.Matches(
                            receipt.After.Checkpoint.Backup));
                }
            });
            Run("uninstall promote rollback and committed cleanup converge", delegate
            {
                Fixture rollbackFixture = UninstallFixture();
                using (var store = rollbackFixture.Open())
                {
                    PayloadPromotionReceipt promotion =
                        store.PromoteUninstall(
                            rollbackFixture.Authority,
                            store.Inspect());
                    Equal(
                        PayloadNamespaceShape.BackupOnly,
                        promotion.After.Shape);
                    PayloadRecoveryReceipt rollback = store.Recover(
                        PayloadRecoveryDecision.RestoreBaseline,
                        store.Inspect());
                    Equal(
                        PayloadNamespaceShape.CurrentOnly,
                        rollback.After.Shape);
                }

                Fixture commitFixture = UninstallFixture();
                using (var store = commitFixture.Open())
                {
                    store.PromoteUninstall(
                        commitFixture.Authority,
                        store.Inspect());
                    PayloadCleanupReceipt cleanup = store.Cleanup(
                        PayloadCleanupKind.CommittedBackup,
                        store.Inspect());
                    Equal(PayloadNamespaceShape.Empty, cleanup.After.Shape);
                }
            });
            Run("uninstall crash after rename reenters forward", delegate
            {
                Fixture fixture = UninstallFixture();
                using (var crashing = fixture.Open(1))
                {
                    ExpectCrash(delegate
                    {
                        crashing.PromoteUninstall(
                            fixture.Authority,
                            crashing.Inspect());
                    });
                }
                Equal(
                    PayloadNamespaceShape.BackupOnly,
                    fixture.Raw.State.Shape);
                using (var recovered = fixture.Open())
                {
                    PayloadRecoveryReceipt receipt = recovered.Recover(
                        PayloadRecoveryDecision.CompleteForward,
                        recovered.Inspect());
                    Equal(
                        PayloadNamespaceShape.BackupOnly,
                        receipt.After.Shape);
                }
            });
            Reject("stale expected state mutates nothing", delegate
            {
                Fixture fixture = FreshFixture();
                PayloadNamespaceState stale = fixture.Raw.State;
                PayloadCandidateReceipt candidate = fixture.Stage();
                long revision = fixture.Raw.State.Revision;
                try
                {
                    using (var store = fixture.Open())
                    {
                        store.PromoteInstall(
                            fixture.Authority,
                            stale,
                            candidate);
                    }
                }
                finally
                {
                    Equal(revision, fixture.Raw.State.Revision);
                }
            });
            Reject("wrong sealed authority mutates nothing", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                PayloadCandidateReceipt candidate = fixture.Stage();
                PayloadRecoveryAuthority wrong =
                    fixture.Authority.DeepClone();
                wrong.SealedEscrowManifestSha256 =
                    Sha(new byte[] { 99 });
                long revision = fixture.Raw.State.Revision;
                try
                {
                    using (var store = fixture.Open())
                    {
                        store.PromoteInstall(
                            wrong,
                            store.Inspect(),
                            candidate);
                    }
                }
                finally
                {
                    Equal(revision, fixture.Raw.State.Revision);
                }
            });
            Reject("semantic authority drift is rejected before stage publication", delegate
            {
                Fixture fixture = FreshFixture();
                PayloadRecoveryAuthority wrong =
                    fixture.Authority.DeepClone();
                wrong.Target.SemanticTreeSha256 =
                    Sha(new byte[] { 88 });
                long revision = fixture.Raw.State.Revision;
                try
                {
                    using (var store =
                        new DeterministicProtectedPayloadStoreCoordinator(
                            wrong,
                            new FakeCommittedPayloadNamespaceModel(
                                fixture.Raw,
                                0,
                                false)))
                    using (var source = fixture.Source())
                    {
                        store.Stage(source, fixture.TargetManifest);
                    }
                }
                finally
                {
                    Equal(revision, fixture.Raw.State.Revision);
                    Equal(
                        PayloadNamespaceShape.Empty,
                        fixture.Raw.State.Shape);
                }
            });
            Reject("forged candidate receipt from another native tree is stale", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                PayloadCandidateReceipt real = fixture.Stage();
                PayloadNamespaceCheckpoint forgedCheckpoint =
                    real.After.Checkpoint;
                forgedCheckpoint.Candidate.FileId = Id(900);
                forgedCheckpoint.Candidate.Entries[0].FileId = Id(901);
                forgedCheckpoint.Candidate.Entries[1].FileId = Id(902);
                forgedCheckpoint.Candidate.Entries[2].FileId = Id(903);
                PayloadNamespaceState forgedAfter =
                    new PayloadNamespaceState(forgedCheckpoint);
                PayloadCandidateReceipt forged =
                    new PayloadCandidateReceipt(
                        fixture.Authority,
                        fixture.TargetManifest,
                        real.Before,
                        forgedAfter);
                using (var store = fixture.Open())
                {
                    store.PromoteInstall(
                        fixture.Authority,
                        store.Inspect(),
                        forged);
                }
            });
            Reject("source content drift is rejected before namespace publication", delegate
            {
                Fixture fixture = FreshFixture();
                long revision = fixture.Raw.State.Revision;
                try
                {
                    using (var store = fixture.Open())
                    using (var source = fixture.Source(true))
                    {
                        store.Stage(source, fixture.TargetManifest);
                    }
                }
                finally
                {
                    Equal(revision, fixture.Raw.State.Revision);
                    Equal(
                        PayloadNamespaceShape.Empty,
                        fixture.Raw.State.Shape);
                }
            });
            Reject("source receipt for another manifest is rejected", delegate
            {
                Fixture fixture = FreshFixture();
                TargetPayloadManifest other = CreateManifest(
                    OtherTransactionId,
                    "0.3.0",
                    fixture.TargetFiles);
                using (var store = fixture.Open())
                using (var source = new FakePayloadSource(
                    other,
                    fixture.TargetFiles,
                    false))
                {
                    store.Stage(source, fixture.TargetManifest);
                }
            });
            Reject("cross-transaction backend is rejected at construction", delegate
            {
                Fixture fixture = FreshFixture();
                PayloadNamespaceCheckpoint state =
                    fixture.Raw.State.Checkpoint;
                state.TransactionId = OtherTransactionId;
                fixture.Raw.State = new PayloadNamespaceState(state);
                fixture.Open();
            });
            Reject("disposed store rejects inspection", delegate
            {
                Fixture fixture = FreshFixture();
                DeterministicProtectedPayloadStoreCoordinator store =
                    fixture.Open();
                store.Dispose();
                store.Inspect();
            });
            Reject("backend stale CAS rejects duplicate mutation plan", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                fixture.Stage();
                using (var backend = new FakeCommittedPayloadNamespaceModel(
                    fixture.Raw,
                    0,
                    false))
                {
                    PayloadNamespaceMutationPlan plan =
                        ProtectedPayloadRecoveryPlanner.NextRecovery(
                            fixture.Authority,
                            PayloadRecoveryDecision.CompleteForward,
                            backend.Inspect());
                    backend.ApplyCommitted(plan);
                    backend.ApplyCommitted(plan);
                }
            });
            Reject("mutation plan rejects backend over-application", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                fixture.Stage();
                using (var backend = new FakeCommittedPayloadNamespaceModel(
                    fixture.Raw,
                    0,
                    false))
                {
                    PayloadNamespaceMutationPlan first =
                        ProtectedPayloadRecoveryPlanner.NextRecovery(
                            fixture.Authority,
                            PayloadRecoveryDecision.CompleteForward,
                            backend.Inspect());
                    backend.ApplyCommitted(first);
                    PayloadNamespaceMutationPlan second =
                        ProtectedPayloadRecoveryPlanner.NextRecovery(
                            fixture.Authority,
                            PayloadRecoveryDecision.CompleteForward,
                            backend.Inspect());
                    PayloadNamespaceState overApplied =
                        backend.ApplyCommitted(second).State;
                    first.ValidateApplied(overApplied);
                }
            });
            Run("retryable outcome cannot hide committed mutation", delegate
            {
                Fixture fixture = ReplacementFixture(
                    InstallOperation.Upgrade);
                fixture.Stage();
                fixture.Raw.MutateThenRetryNextApply = true;
                bool detected = false;
                using (var store = fixture.Open())
                {
                    try
                    {
                        store.Recover(
                            PayloadRecoveryDecision.CompleteForward,
                            store.Inspect());
                    }
                    catch (InvalidOperationException exception)
                    {
                        detected = exception.Message.IndexOf(
                            "did not publish an exact forward state",
                            StringComparison.Ordinal) >= 0;
                    }
                }
                True(detected);
                Equal(
                    PayloadNamespaceShape.CandidateAndBackup,
                    fixture.Raw.State.Shape);
            });
            Run("candidate receipt clones authority and manifest", delegate
            {
                Fixture fixture = FreshFixture();
                PayloadCandidateReceipt receipt = fixture.Stage();
                PayloadRecoveryAuthority exposedAuthority =
                    receipt.Authority;
                TargetPayloadManifest exposedManifest =
                    receipt.Manifest;
                exposedAuthority.Target.Release.Version = "9.9.9";
                exposedManifest.Target.Version = "9.9.9";
                Equal(
                    "0.3.0",
                    receipt.Authority.Target.Release.Version);
                Equal(
                    "0.3.0",
                    receipt.Manifest.Target.Version);
            });
            Reject("candidate receipt rejects manifest authority drift", delegate
            {
                Fixture fixture = FreshFixture();
                PayloadCandidateReceipt receipt = fixture.Stage();
                TargetPayloadManifest drifted =
                    fixture.TargetManifest.DeepClone();
                drifted.ReleaseCatalogSha256 =
                    Sha(new byte[] { 55 });
                drifted.ContentSetSha256 =
                    drifted.ComputeContentSetSha256();
                new PayloadCandidateReceipt(
                    fixture.Authority,
                    drifted,
                    receipt.Before,
                    receipt.After);
            });

            Console.WriteLine(
                "Protected payload transaction executor tests passed: " +
                passed.ToString());
            return 0;
        }

        private sealed class Fixture
        {
            internal FakeRawPayloadNamespace Raw;
            internal TargetPayloadManifest TargetManifest;
            internal Dictionary<string, byte[]> TargetFiles;
            internal PayloadRecoveryAuthority Authority;

            internal DeterministicProtectedPayloadStoreCoordinator Open()
            {
                return Open(0);
            }

            internal DeterministicProtectedPayloadStoreCoordinator Open(
                int crashOnApply)
            {
                return Open(crashOnApply, false);
            }

            internal DeterministicProtectedPayloadStoreCoordinator Open(
                int crashOnApply,
                bool crashAfterStage)
            {
                return new DeterministicProtectedPayloadStoreCoordinator(
                    Authority,
                    new FakeCommittedPayloadNamespaceModel(
                        Raw,
                        crashOnApply,
                        crashAfterStage));
            }

            internal FakePayloadSource Source()
            {
                return Source(false);
            }

            internal FakePayloadSource Source(bool corrupt)
            {
                return new FakePayloadSource(
                    TargetManifest,
                    TargetFiles,
                    corrupt);
            }

            internal PayloadCandidateReceipt Stage()
            {
                using (var store = Open())
                using (var source = Source())
                {
                    return store.Stage(source, TargetManifest);
                }
            }

            internal void Promote()
            {
                PayloadCandidateReceipt candidate = Stage();
                using (var store = Open())
                {
                    store.PromoteInstall(
                        Authority,
                        candidate.After,
                        candidate);
                }
            }
        }

        private sealed class FakeRawPayloadNamespace
        {
            internal PayloadNamespaceState State;
            internal bool RetryNextApply;
            internal bool MutateThenRetryNextApply;
            internal bool MaintenancePending;
            internal bool MarkMaintenancePendingAfterDelete;
        }

        private sealed class SimulatedPayloadNamespaceCrashException :
            Exception
        {
        }

        private sealed class FakeCommittedPayloadNamespaceModel :
            ICommittedPayloadNamespaceModel
        {
            private readonly FakeRawPayloadNamespace raw;
            private readonly int crashOnApply;
            private readonly bool crashAfterStage;
            private int applies;
            private bool disposed;

            internal FakeCommittedPayloadNamespaceModel(
                FakeRawPayloadNamespace raw,
                int crashOnApply,
                bool crashAfterStage)
            {
                this.raw = raw;
                this.crashOnApply = crashOnApply;
                this.crashAfterStage = crashAfterStage;
            }

            public PayloadNamespaceState Inspect()
            {
                ThrowIfDisposed();
                return new PayloadNamespaceState(raw.State.Checkpoint);
            }

            public PayloadNamespaceState PublishSealedCandidate(
                PayloadCandidateStagePlan plan,
                ITrustedReleasePayloadSource source)
            {
                ThrowIfDisposed();
                RequireCas(plan.Observed);
                if (plan.IsTerminal || source == null)
                {
                    throw new InvalidOperationException(
                        "Fake stage received an invalid plan.");
                }
                TargetPayloadManifest manifest = plan.Expected;
                if (!String.Equals(
                    manifest.InvariantDigest,
                    source.Receipt.InvariantDigest,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Fake stage source receipt drifted.");
                }
                VerifySource(source, manifest);
                PayloadNamespaceCheckpoint next =
                    raw.State.Checkpoint;
                next.Candidate = Directory(
                    manifest,
                    PayloadDirectorySlot.Candidate,
                    200);
                next.Revision++;
                next.Shape = Shape(next);
                raw.State = new PayloadNamespaceState(next);
                if (crashAfterStage)
                {
                    throw new SimulatedPayloadNamespaceCrashException();
                }
                return Inspect();
            }

            public CommittedPayloadMutationOutcome ApplyCommitted(
                PayloadNamespaceMutationPlan plan)
            {
                ThrowIfDisposed();
                if (plan == null || plan.IsTerminal)
                {
                    throw new InvalidOperationException(
                        "Fake apply received no mutation.");
                }
                RequireCas(plan.Observed);
                if (raw.RetryNextApply)
                {
                    raw.RetryNextApply = false;
                    return new CommittedPayloadMutationOutcome(
                        CommittedPayloadMutationDisposition.RetryableNotApplied,
                        plan,
                        Inspect());
                }
                bool mutateThenRetry = raw.MutateThenRetryNextApply;
                PayloadNamespaceState retryState = null;
                if (mutateThenRetry)
                {
                    raw.MutateThenRetryNextApply = false;
                    retryState = Inspect();
                }
                PayloadNamespaceCheckpoint next =
                    raw.State.Checkpoint;
                PayloadDirectoryCheckpoint source =
                    Source(next, plan.Kind);
                if (source == null ||
                    !String.Equals(
                        source.InvariantDigest,
                        plan.ExactSource.InvariantDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Fake apply source identity drifted.");
                }
                switch (plan.Kind)
                {
                    case PayloadNamespaceMutationKind.RenameCurrentToBackup:
                        next.Current = null;
                        next.Backup = Rename(
                            source,
                            PayloadDirectorySlot.Backup);
                        break;
                    case PayloadNamespaceMutationKind.RenameCandidateToCurrent:
                        next.Candidate = null;
                        next.Current = Rename(
                            source,
                            PayloadDirectorySlot.Current);
                        break;
                    case PayloadNamespaceMutationKind.RenameBackupToCurrent:
                        next.Backup = null;
                        next.Current = Rename(
                            source,
                            PayloadDirectorySlot.Current);
                        break;
                    case PayloadNamespaceMutationKind.DeleteCurrent:
                        next.Current = null;
                        break;
                    case PayloadNamespaceMutationKind.DeleteCandidate:
                        next.Candidate = null;
                        break;
                    case PayloadNamespaceMutationKind.DeleteBackup:
                        next.Backup = null;
                        break;
                    default:
                        throw new InvalidOperationException(
                            "Fake apply received an unknown mutation.");
                }
                next.Revision++;
                next.Shape = Shape(next);
                raw.State = new PayloadNamespaceState(next);
                if (raw.MarkMaintenancePendingAfterDelete &&
                    (plan.Kind == PayloadNamespaceMutationKind.DeleteCurrent ||
                     plan.Kind ==
                        PayloadNamespaceMutationKind.DeleteCandidate ||
                     plan.Kind ==
                        PayloadNamespaceMutationKind.DeleteBackup))
                {
                    raw.MaintenancePending = true;
                }
                applies++;
                if (crashOnApply > 0 && applies == crashOnApply)
                {
                    throw new SimulatedPayloadNamespaceCrashException();
                }
                if (mutateThenRetry)
                {
                    return new CommittedPayloadMutationOutcome(
                        CommittedPayloadMutationDisposition.RetryableNotApplied,
                        plan,
                        retryState);
                }
                return new CommittedPayloadMutationOutcome(
                    CommittedPayloadMutationDisposition.Applied,
                    plan,
                    Inspect());
            }

            public bool HasPendingMaintenance
            {
                get
                {
                    ThrowIfDisposed();
                    return raw.MaintenancePending;
                }
            }

            public void Dispose()
            {
                disposed = true;
            }

            private void RequireCas(PayloadNamespaceState expected)
            {
                if (expected == null ||
                    !String.Equals(
                        expected.InvariantDigest,
                        raw.State.InvariantDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Fake namespace CAS failed.");
                }
            }

            private void ThrowIfDisposed()
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(
                        "FakeCommittedPayloadNamespaceModel");
                }
            }
        }

        private sealed class FakePayloadSource :
            ITrustedReleasePayloadSource
        {
            private readonly Dictionary<string, byte[]> files;
            private readonly bool corrupt;
            private bool disposed;

            internal FakePayloadSource(
                TargetPayloadManifest manifest,
                Dictionary<string, byte[]> files,
                bool corrupt)
            {
                Receipt = new TrustedReleasePayloadReceipt(manifest);
                this.files = files;
                this.corrupt = corrupt;
            }

            public TrustedReleasePayloadReceipt Receipt { get; private set; }

            public Stream OpenExact(TargetPayloadEntry expected)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(
                        "FakePayloadSource");
                }
                byte[] content;
                if (!files.TryGetValue(
                    expected.RelativePath,
                    out content))
                {
                    throw new FileNotFoundException(
                        expected.RelativePath);
                }
                byte[] copy = (byte[])content.Clone();
                if (corrupt && copy.Length > 0)
                {
                    copy[0] ^= 0xff;
                }
                return new MemoryStream(copy, false);
            }

            public void Dispose()
            {
                disposed = true;
            }
        }

        private static Fixture FreshFixture()
        {
            Dictionary<string, byte[]> targetFiles = TargetFiles();
            TargetPayloadManifest target = CreateManifest(
                TransactionId,
                "0.3.0",
                targetFiles);
            PayloadDirectoryCheckpoint candidate = Directory(
                target,
                PayloadDirectorySlot.Candidate,
                200);
            var authority = new PayloadRecoveryAuthority
            {
                SchemaVersion = 1,
                TransactionId = TransactionId,
                Operation = InstallOperation.FreshInstall,
                BaselineState = BaselinePayloadState.Absent,
                Baseline = null,
                Target = ContentAuthority(candidate),
                SealedEscrowManifestSha256 =
                    Sha(new byte[] { 7 })
            };
            return new Fixture
            {
                Raw = new FakeRawPayloadNamespace
                {
                    State = State(null, null, null)
                },
                TargetManifest = target,
                TargetFiles = targetFiles,
                Authority = authority
            };
        }

        private static Fixture ReplacementFixture(
            InstallOperation operation)
        {
            Fixture fixture = FreshFixture();
            Dictionary<string, byte[]> baselineFiles = BaselineFiles();
            TargetPayloadManifest baselineManifest = CreateManifest(
                TransactionId,
                "0.2.0",
                baselineFiles);
            PayloadDirectoryCheckpoint baseline = Directory(
                baselineManifest,
                PayloadDirectorySlot.Current,
                100);
            PayloadDirectoryCheckpoint target = Directory(
                fixture.TargetManifest,
                PayloadDirectorySlot.Candidate,
                200);
            fixture.Raw.State = State(baseline, null, null);
            fixture.Authority = new PayloadRecoveryAuthority
            {
                SchemaVersion = 1,
                TransactionId = TransactionId,
                Operation = operation,
                BaselineState = BaselinePayloadState.Present,
                Baseline = ContentAuthority(baseline),
                Target = ContentAuthority(target),
                SealedEscrowManifestSha256 =
                    Sha(new byte[] { 7 })
            };
            return fixture;
        }

        private static Fixture UninstallFixture()
        {
            Dictionary<string, byte[]> baselineFiles = BaselineFiles();
            TargetPayloadManifest baselineManifest = CreateManifest(
                TransactionId,
                "0.2.0",
                baselineFiles);
            PayloadDirectoryCheckpoint baseline = Directory(
                baselineManifest,
                PayloadDirectorySlot.Current,
                100);
            return new Fixture
            {
                Raw = new FakeRawPayloadNamespace
                {
                    State = State(baseline, null, null)
                },
                TargetManifest = null,
                TargetFiles = null,
                Authority = new PayloadRecoveryAuthority
                {
                    SchemaVersion = 1,
                    TransactionId = TransactionId,
                    Operation = InstallOperation.Uninstall,
                    BaselineState = BaselinePayloadState.Present,
                    Baseline = ContentAuthority(baseline),
                    Target = null,
                    SealedEscrowManifestSha256 =
                        Sha(new byte[] { 7 })
                }
            };
        }

        private static Dictionary<string, byte[]> TargetFiles()
        {
            return new Dictionary<string, byte[]>(
                StringComparer.OrdinalIgnoreCase)
            {
                { "SBMS.exe", new byte[] { 1, 2, 3, 4 } },
                { @"driver\SBMS.dll", new byte[] { 5, 6, 7 } }
            };
        }

        private static Dictionary<string, byte[]> BaselineFiles()
        {
            return new Dictionary<string, byte[]>(
                StringComparer.OrdinalIgnoreCase)
            {
                { "SBMS.exe", new byte[] { 9, 8, 7 } },
                { @"driver\SBMS.dll", new byte[] { 6, 5 } }
            };
        }

        private static TargetPayloadManifest CreateManifest(
            string transactionId,
            string version,
            Dictionary<string, byte[]> files)
        {
            var manifest = new TargetPayloadManifest
            {
                SchemaVersion = 1,
                TransactionId = transactionId,
                Target = new ReleaseIdentity(
                    version,
                    Sha(new byte[] { (byte)version.Length })),
                ReleaseCatalogSha256 = Sha(new byte[] { 10 }),
                SignedReleaseManifestSha256 = Sha(new byte[] { 11 })
            };
            foreach (string path in new[]
            {
                "SBMS.exe",
                @"driver\SBMS.dll"
            })
            {
                byte[] content = files[path];
                manifest.Content.Add(new TargetPayloadEntry
                {
                    RelativePath = path,
                    Length = content.Length,
                    Sha256 = Sha(content)
                });
            }
            manifest.ContentSetSha256 =
                manifest.ComputeContentSetSha256();
            return manifest;
        }

        private static PayloadDirectoryCheckpoint Directory(
            TargetPayloadManifest manifest,
            PayloadDirectorySlot slot,
            int identityBase)
        {
            long total = 0;
            foreach (TargetPayloadEntry entry in manifest.Content)
            {
                total += entry.Length;
            }
            return new PayloadDirectoryCheckpoint
            {
                TransactionId = manifest.TransactionId,
                Slot = slot,
                VolumeSerialNumber = 0x1234UL,
                FileId = Id(identityBase),
                Release = new ReleaseIdentity(
                    manifest.Target.Version,
                    manifest.Target.PackageFingerprint),
                ContentSetSha256 = manifest.ContentSetSha256,
                ManifestInvariantDigest = manifest.InvariantDigest,
                FileCount = manifest.Content.Count,
                TotalBytes = total,
                Entries = new List<PayloadTreeEntryCheckpoint>
                {
                    new PayloadTreeEntryCheckpoint
                    {
                        RelativePath = "SBMS.exe",
                        IsDirectory = false,
                        FileId = Id(identityBase + 1),
                        Length = manifest.Content[0].Length,
                        Sha256 = manifest.Content[0].Sha256
                    },
                    new PayloadTreeEntryCheckpoint
                    {
                        RelativePath = "driver",
                        IsDirectory = true,
                        FileId = Id(identityBase + 2),
                        Length = 0,
                        Sha256 = String.Empty
                    },
                    new PayloadTreeEntryCheckpoint
                    {
                        RelativePath = @"driver\SBMS.dll",
                        IsDirectory = false,
                        FileId = Id(identityBase + 3),
                        Length = manifest.Content[1].Length,
                        Sha256 = manifest.Content[1].Sha256
                    }
                }
            };
        }

        private static void VerifySource(
            ITrustedReleasePayloadSource source,
            TargetPayloadManifest manifest)
        {
            foreach (TargetPayloadEntry entry in manifest.Content)
            {
                using (Stream stream = source.OpenExact(entry))
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    byte[] bytes = buffer.ToArray();
                    if (bytes.Length != entry.Length ||
                        !String.Equals(
                            Sha(bytes),
                            entry.Sha256,
                            StringComparison.Ordinal) ||
                        stream.ReadByte() != -1)
                    {
                        throw new InvalidOperationException(
                            "Fake stage source content drifted.");
                    }
                }
            }
        }

        private static PayloadContentAuthority ContentAuthority(
            PayloadDirectoryCheckpoint directory)
        {
            return new PayloadContentAuthority
            {
                Release = new ReleaseIdentity(
                    directory.Release.Version,
                    directory.Release.PackageFingerprint),
                ContentSetSha256 = directory.ContentSetSha256,
                ManifestInvariantDigest =
                    directory.ManifestInvariantDigest,
                SemanticTreeSha256 =
                    directory.SemanticTreeSha256,
                FileCount = directory.FileCount,
                TotalBytes = directory.TotalBytes
            };
        }

        private static PayloadNamespaceState State(
            PayloadDirectoryCheckpoint current,
            PayloadDirectoryCheckpoint candidate,
            PayloadDirectoryCheckpoint backup)
        {
            var checkpoint = new PayloadNamespaceCheckpoint
            {
                SchemaVersion = 1,
                Revision = 1,
                TransactionId = TransactionId,
                Current = current,
                Candidate = candidate,
                Backup = backup
            };
            checkpoint.Shape = Shape(checkpoint);
            return new PayloadNamespaceState(checkpoint);
        }

        private static PayloadNamespaceShape Shape(
            PayloadNamespaceCheckpoint state)
        {
            if (state.Current != null && state.Candidate != null)
            {
                return PayloadNamespaceShape.CurrentAndCandidate;
            }
            if (state.Current != null && state.Backup != null)
            {
                return PayloadNamespaceShape.CurrentAndBackup;
            }
            if (state.Candidate != null && state.Backup != null)
            {
                return PayloadNamespaceShape.CandidateAndBackup;
            }
            if (state.Current != null)
            {
                return PayloadNamespaceShape.CurrentOnly;
            }
            if (state.Candidate != null)
            {
                return PayloadNamespaceShape.CandidateOnly;
            }
            if (state.Backup != null)
            {
                return PayloadNamespaceShape.BackupOnly;
            }
            return PayloadNamespaceShape.Empty;
        }

        private static PayloadDirectoryCheckpoint Source(
            PayloadNamespaceCheckpoint state,
            PayloadNamespaceMutationKind kind)
        {
            switch (kind)
            {
                case PayloadNamespaceMutationKind.RenameCurrentToBackup:
                case PayloadNamespaceMutationKind.DeleteCurrent:
                    return state.Current;
                case PayloadNamespaceMutationKind.RenameCandidateToCurrent:
                case PayloadNamespaceMutationKind.DeleteCandidate:
                    return state.Candidate;
                case PayloadNamespaceMutationKind.RenameBackupToCurrent:
                case PayloadNamespaceMutationKind.DeleteBackup:
                    return state.Backup;
                default:
                    return null;
            }
        }

        private static PayloadDirectoryCheckpoint Rename(
            PayloadDirectoryCheckpoint source,
            PayloadDirectorySlot destination)
        {
            PayloadDirectoryCheckpoint result = source.DeepClone();
            result.Slot = destination;
            return result;
        }

        private static string Sha(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] digest = algorithm.ComputeHash(bytes);
                var result = new System.Text.StringBuilder(64);
                foreach (byte value in digest)
                {
                    result.Append(value.ToString("x2"));
                }
                return result.ToString();
            }
        }

        private static string Id(int value)
        {
            return value.ToString("x").PadLeft(32, '0');
        }

        private static void Run(string name, Action action)
        {
            action();
            passed++;
            Console.WriteLine("[PASS] " + name);
        }

        private static void Reject(string name, Action action)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception)
            {
                rejected = true;
            }
            if (!rejected)
            {
                throw new InvalidOperationException(
                    "Expected rejection: " + name);
            }
            passed++;
            Console.WriteLine("[PASS] " + name);
        }

        private static void ExpectCrash(Action action)
        {
            bool crashed = false;
            try
            {
                action();
            }
            catch (SimulatedPayloadNamespaceCrashException)
            {
                crashed = true;
            }
            if (!crashed)
            {
                throw new InvalidOperationException(
                    "Expected simulated payload namespace crash.");
            }
        }

        private static void ExpectRejected(Action action)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception)
            {
                rejected = true;
            }
            if (!rejected)
            {
                throw new InvalidOperationException(
                    "Expected rejection.");
            }
        }

        private static void Equal(object expected, object actual)
        {
            if (!Object.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    "Expected " + expected + ", actual " + actual + ".");
            }
        }

        private static void True(bool value)
        {
            if (!value)
            {
                throw new InvalidOperationException(
                    "Expected true.");
            }
        }
    }
}
