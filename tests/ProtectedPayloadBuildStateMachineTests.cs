using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace SBMSSetup
{
    internal static class ProtectedPayloadBuildStateMachineTests
    {
        private const string TransactionId =
            "00000000000000000000000000000001";
        private const string BuildId =
            "00000000000000000000000000000002";
        private const ulong Volume = 0x1234UL;
        private static int passed;
        private static int failed;

        private sealed class SimulatedCrashException : Exception
        {
        }

        private sealed class Fixture
        {
            internal RawWorkspace Raw;
            internal TargetPayloadManifest Manifest;
            internal Dictionary<string, byte[]> Files;
            internal PayloadRecoveryAuthority Authority;

            internal DeterministicProtectedPayloadBuildStateMachine Open()
            {
                return new DeterministicProtectedPayloadBuildStateMachine(
                    Authority,
                    new FakeWorkspaceModel(Raw));
            }

            internal FakePayloadSource Source()
            {
                return new FakePayloadSource(Manifest, Files);
            }

            internal PayloadBuildAdvanceResult Advance(
                string intentId)
            {
                using (FakePayloadSource source = Source())
                using (
                    DeterministicProtectedPayloadBuildStateMachine machine =
                        Open())
                {
                    return machine.Advance(
                        source,
                        Manifest,
                        BuildId,
                        intentId);
                }
            }

            internal PayloadBuildAdvanceResult RunToTerminal()
            {
                for (int index = 0; index < 100; ++index)
                {
                    PayloadBuildAdvanceResult result =
                        Advance(Id(1000 + index));
                    if (result.Kind ==
                            PayloadBuildAdvanceKind.CandidatePublished ||
                        result.Kind ==
                            PayloadBuildAdvanceKind.CandidateAlreadyPresent ||
                        result.Kind ==
                            PayloadBuildAdvanceKind.Quarantined ||
                        result.Kind ==
                            PayloadBuildAdvanceKind.QuarantineAlreadyPresent)
                    {
                        return result;
                    }
                }
                throw new InvalidOperationException(
                    "Payload build did not converge.");
            }

            internal void ReachArmedStep(
                PayloadBuildStepKind kind)
            {
                for (int index = 0; index < 100; ++index)
                {
                    PayloadBuildWorkspaceCheckpoint checkpoint =
                        Raw.State;
                    if (checkpoint.ActiveBuild != null &&
                        checkpoint.ActiveBuild.ActiveIntent != null &&
                        checkpoint.ActiveBuild.ActiveIntent.Kind == kind)
                    {
                        return;
                    }
                    Advance(Id(2000 + index));
                }
                throw new InvalidOperationException(
                    "Payload build did not arm " + kind + ".");
            }
        }

        private sealed class RawWorkspace
        {
            internal PayloadBuildWorkspaceCheckpoint State;
            internal bool RetryNext;
            internal bool MutateThenRetryNext;
            internal bool ReturnWrongStateNext;
            internal bool OverApplyNext;
            internal bool ForeignCasReceiptNext;
            internal bool PublishUnrelatedHigherNext;
            internal bool WrongQuarantineReasonNext;
            internal bool CrashAfterPublishNext;
            internal PayloadBuildStepKind? CrashPhysicalStep;
            internal PayloadBuildStepKind? CrashAfterLogicalStep;
            internal bool PurgeRetryNext;
            internal bool PurgeMutateThenRetryNext;
            internal bool CrashPurgeDeleteNext;
            internal bool CrashPurgeAfterLogicalNext;
            internal bool PurgeUnrelatedHigherNext;
            internal bool WrongAbsenceRevisionNext;
            internal bool PhysicalCandidatePresent;
            internal readonly HashSet<string> PhysicalQuarantines =
                new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<PayloadBuildStepKind>
                CompletedSteps = new List<PayloadBuildStepKind>();
        }

        private sealed class FakeWorkspaceModel :
            IProtectedPayloadBuildWorkspaceModel
        {
            private readonly RawWorkspace raw;
            private bool disposed;

            internal FakeWorkspaceModel(RawWorkspace rawState)
            {
                raw = rawState;
            }

            public void Dispose()
            {
                disposed = true;
            }

            public PayloadBuildWorkspaceState Inspect()
            {
                ThrowIfDisposed();
                return new PayloadBuildWorkspaceState(
                    raw.State.DeepClone());
            }

            public PayloadBuildMutationOutcome ApplyExact(
                PayloadBuildMutationPlan plan,
                ITrustedReleasePayloadSource source)
            {
                ThrowIfDisposed();
                new PayloadBuildWorkspaceState(
                    raw.State).RequireCas(plan.ExpectedCas);
                PayloadBuildWorkspaceState before =
                    new PayloadBuildWorkspaceState(
                        raw.State.DeepClone());

                if (raw.RetryNext)
                {
                    raw.RetryNext = false;
                    return new PayloadBuildMutationOutcome(
                        PayloadBuildMutationDisposition.
                            RetryableNotApplied,
                        before,
                        null);
                }

                PayloadBuildWorkspaceCheckpoint applied =
                    ApplyPlan(plan, source);
                if (raw.OverApplyNext)
                {
                    raw.OverApplyNext = false;
                    applied.Revision =
                        checked(applied.Revision + 1);
                }
                raw.State = applied;
                if (raw.CrashAfterLogicalStep.HasValue &&
                    plan.StepKind.HasValue &&
                    raw.CrashAfterLogicalStep.Value ==
                        plan.StepKind.Value)
                {
                    raw.CrashAfterLogicalStep = null;
                    throw new SimulatedCrashException();
                }

                if (raw.CrashAfterPublishNext &&
                    plan.Kind ==
                        PayloadBuildMutationKind.PublishIntent)
                {
                    raw.CrashAfterPublishNext = false;
                    throw new SimulatedCrashException();
                }
                if (raw.MutateThenRetryNext)
                {
                    raw.MutateThenRetryNext = false;
                    return new PayloadBuildMutationOutcome(
                        PayloadBuildMutationDisposition.
                            RetryableNotApplied,
                        new PayloadBuildWorkspaceState(
                            raw.State.DeepClone()),
                        null);
                }

                PayloadBuildWorkspaceState committed =
                    new PayloadBuildWorkspaceState(
                        raw.State.DeepClone());
                PayloadWorkspaceCasToken receiptExpected =
                    plan.ExpectedCas;
                if (raw.ForeignCasReceiptNext)
                {
                    raw.ForeignCasReceiptNext = false;
                    receiptExpected = plan.ExpectedCas.DeepClone();
                    receiptExpected.WorkspaceInvariantDigest =
                        Sha(new byte[] { 77 });
                }
                var receipt = new PayloadBuildCasReceipt(
                    receiptExpected,
                    receiptExpected,
                    committed.CasToken);
                if (raw.PublishUnrelatedHigherNext)
                {
                    raw.PublishUnrelatedHigherNext = false;
                    PayloadBuildWorkspaceCheckpoint higher =
                        raw.State.DeepClone();
                    higher.Revision =
                        checked(higher.Revision + 1);
                    raw.State = higher;
                }
                PayloadBuildWorkspaceState returned = committed;
                if (raw.ReturnWrongStateNext)
                {
                    raw.ReturnWrongStateNext = false;
                    returned = before;
                }
                return new PayloadBuildMutationOutcome(
                    PayloadBuildMutationDisposition.Applied,
                    returned,
                    receipt);
            }

            public PayloadPurgeMutationOutcome ApplyPurgeExact(
                PayloadPurgeMutationPlan plan)
            {
                ThrowIfDisposed();
                new PayloadBuildWorkspaceState(
                    raw.State).RequireCas(plan.ExpectedCas);
                PayloadBuildWorkspaceState before =
                    new PayloadBuildWorkspaceState(
                        raw.State.DeepClone());
                if (raw.PurgeRetryNext)
                {
                    raw.PurgeRetryNext = false;
                    return new PayloadPurgeMutationOutcome(
                        PayloadBuildMutationDisposition.
                            RetryableNotApplied,
                        before,
                        null,
                        null);
                }
                PayloadQuarantineAbsenceObservation absence;
                PayloadBuildWorkspaceCheckpoint applied =
                    ApplyPurge(plan, out absence);
                raw.State = applied;
                if (raw.CrashPurgeAfterLogicalNext)
                {
                    raw.CrashPurgeAfterLogicalNext = false;
                    throw new SimulatedCrashException();
                }
                if (raw.PurgeMutateThenRetryNext)
                {
                    raw.PurgeMutateThenRetryNext = false;
                    return new PayloadPurgeMutationOutcome(
                        PayloadBuildMutationDisposition.
                            RetryableNotApplied,
                        new PayloadBuildWorkspaceState(
                            raw.State.DeepClone()),
                        null,
                        null);
                }
                PayloadBuildWorkspaceState committed =
                    new PayloadBuildWorkspaceState(
                        raw.State.DeepClone());
                var receipt = new PayloadBuildCasReceipt(
                    plan.ExpectedCas,
                    plan.ExpectedCas,
                    committed.CasToken);
                if (raw.PurgeUnrelatedHigherNext)
                {
                    raw.PurgeUnrelatedHigherNext = false;
                    PayloadBuildWorkspaceCheckpoint higher =
                        raw.State.DeepClone();
                    higher.Revision =
                        checked(higher.Revision + 1);
                    raw.State = higher;
                }
                return new PayloadPurgeMutationOutcome(
                    PayloadBuildMutationDisposition.Applied,
                    committed,
                    receipt,
                    absence);
            }

            private PayloadBuildWorkspaceCheckpoint ApplyPurge(
                PayloadPurgeMutationPlan plan,
                out PayloadQuarantineAbsenceObservation absence)
            {
                PayloadBuildWorkspaceCheckpoint checkpoint =
                    raw.State.DeepClone();
                checkpoint.Revision =
                    checked(checkpoint.Revision + 1);
                PayloadQuarantineCheckpoint quarantine =
                    FindQuarantine(
                        checkpoint,
                        plan.QuarantineId);
                absence = null;
                if (plan.Kind == PayloadPurgeTransitionKind.Arm)
                {
                    checkpoint.PendingPurges.Add(
                        new PayloadPurgeCheckpoint
                        {
                            SchemaVersion = 1,
                            PurgeId = plan.PurgeId,
                            QuarantineId =
                                plan.QuarantineId,
                            TransactionId =
                                checkpoint.TransactionId,
                            RecoveryAuthorityInvariantDigest =
                                checkpoint.
                                    RecoveryAuthorityInvariantDigest,
                            NamespaceRootInvariantDigest =
                                checkpoint.NamespaceRoot.
                                    InvariantDigest,
                            QuarantineInvariantDigest =
                                quarantine.InvariantDigest,
                            VolumeSerialNumber =
                                quarantine.VolumeSerialNumber,
                            RootFileId =
                                quarantine.RootFileId,
                            Phase = PayloadPurgePhase.Armed,
                            AbsenceObservationInvariantDigest =
                                String.Empty,
                            AbsenceObservedAtWorkspaceRevision =
                                -1
                        });
                    checkpoint.PendingPurges.Sort(
                        delegate(
                            PayloadPurgeCheckpoint first,
                            PayloadPurgeCheckpoint second)
                        {
                            return StringComparer.Ordinal.Compare(
                                first.PurgeId,
                                second.PurgeId);
                        });
                    return checkpoint;
                }
                PayloadPurgeCheckpoint purge =
                    FindPurge(checkpoint, plan.PurgeId);
                if (plan.Kind ==
                    PayloadPurgeTransitionKind.ObserveAbsent)
                {
                    raw.PhysicalQuarantines.Remove(
                        quarantine.QuarantineId);
                    if (raw.CrashPurgeDeleteNext)
                    {
                        raw.CrashPurgeDeleteNext = false;
                        throw new SimulatedCrashException();
                    }
                    absence =
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
                                quarantine.VolumeSerialNumber,
                            RootFileId =
                                quarantine.RootFileId,
                            ObservedAtWorkspaceRevision =
                                raw.WrongAbsenceRevisionNext
                                    ? plan.Before.Revision - 1
                                    : plan.Before.Revision,
                            Exists = raw.PhysicalQuarantines.Contains(
                                quarantine.QuarantineId)
                        };
                    raw.WrongAbsenceRevisionNext = false;
                    purge.Phase =
                        PayloadPurgePhase.ObservedAbsent;
                    purge.AbsenceObservationInvariantDigest =
                        absence.InvariantDigest;
                    purge.AbsenceObservedAtWorkspaceRevision =
                        plan.Before.Revision;
                    return checkpoint;
                }
                absence =
                    new PayloadQuarantineAbsenceObservation
                    {
                        SchemaVersion = 1,
                        TransactionId = checkpoint.TransactionId,
                        RecoveryAuthorityInvariantDigest =
                            checkpoint.
                                RecoveryAuthorityInvariantDigest,
                        NamespaceRootInvariantDigest =
                            checkpoint.NamespaceRoot.InvariantDigest,
                        QuarantineId = quarantine.QuarantineId,
                        QuarantineLeafName =
                            quarantine.QuarantineLeafName,
                        VolumeSerialNumber =
                            quarantine.VolumeSerialNumber,
                        RootFileId = quarantine.RootFileId,
                        ObservedAtWorkspaceRevision =
                            raw.WrongAbsenceRevisionNext
                                ? plan.Before.Revision - 1
                                : plan.Before.Revision,
                        Exists = raw.PhysicalQuarantines.Contains(
                            quarantine.QuarantineId)
                    };
                raw.WrongAbsenceRevisionNext = false;
                checkpoint.PendingPurges.Remove(purge);
                checkpoint.Quarantines.Remove(quarantine);
                checkpoint.CompletedPurges.Add(
                    new PayloadCompletedPurgeCheckpoint
                    {
                        SchemaVersion = 1,
                        PurgeId = plan.PurgeId,
                        QuarantineId = plan.QuarantineId,
                        TransactionId = checkpoint.TransactionId,
                        RecoveryAuthorityInvariantDigest =
                            checkpoint.
                                RecoveryAuthorityInvariantDigest,
                        NamespaceRootInvariantDigest =
                            checkpoint.NamespaceRoot.InvariantDigest,
                        Quarantine = quarantine.DeepClone(),
                        AbsenceObservation =
                            absence.DeepClone(),
                        CompletedAtWorkspaceRevision =
                            checkpoint.Revision
                    });
                checkpoint.CompletedPurges.Sort(
                    delegate(
                        PayloadCompletedPurgeCheckpoint first,
                        PayloadCompletedPurgeCheckpoint second)
                    {
                        return StringComparer.Ordinal.Compare(
                            first.PurgeId,
                            second.PurgeId);
                    });
                return checkpoint;
            }

            private PayloadBuildWorkspaceCheckpoint ApplyPlan(
                PayloadBuildMutationPlan plan,
                ITrustedReleasePayloadSource source)
            {
                if (plan.Kind ==
                        PayloadBuildMutationKind.BeginBuild ||
                    plan.Kind ==
                        PayloadBuildMutationKind.PublishIntent)
                {
                    return plan.ExpectedControlAfter.
                        Checkpoint;
                }

                PayloadBuildWorkspaceCheckpoint checkpoint =
                    raw.State.DeepClone();
                PayloadBuildStepKind step =
                    plan.StepKind.Value;
                ApplyPhysical(checkpoint, step, source, plan.Manifest);
                if (raw.CrashPhysicalStep.HasValue &&
                    raw.CrashPhysicalStep.Value == step &&
                    step != PayloadBuildStepKind.SealCandidate &&
                    step != PayloadBuildStepKind.QuarantineBuild)
                {
                    raw.CrashPhysicalStep = null;
                    raw.State = checkpoint;
                    throw new SimulatedCrashException();
                }

                checkpoint.Revision =
                    checked(checkpoint.Revision + 1);
                if (step == PayloadBuildStepKind.SealCandidate)
                {
                    Seal(checkpoint, plan.Manifest);
                }
                else if (step ==
                    PayloadBuildStepKind.QuarantineBuild)
                {
                    Quarantine(checkpoint, plan);
                }
                else
                {
                    CompleteEntryStep(checkpoint, step);
                }
                raw.CompletedSteps.Add(step);
                return checkpoint;
            }

            private static void ApplyPhysical(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                PayloadBuildStepKind step,
                ITrustedReleasePayloadSource source,
                TargetPayloadManifest manifest)
            {
                PayloadBuildStepIntent intent =
                    checkpoint.ActiveBuild.ActiveIntent;
                if (step == PayloadBuildStepKind.CreateRoot)
                {
                    if (!checkpoint.ActivePartialTree.Exists)
                    {
                        checkpoint.ActivePartialTree.Exists = true;
                        checkpoint.ActivePartialTree.
                            VolumeSerialNumber = Volume;
                        checkpoint.ActivePartialTree.RootFileId =
                            Id(500);
                    }
                    return;
                }
                if (step == PayloadBuildStepKind.CreateEntry)
                {
                    PayloadBuildEntryCheckpoint expected =
                        checkpoint.ActiveBuild.
                            Entries[intent.EntryOrdinal];
                    if (Find(
                            checkpoint.ActivePartialTree,
                            expected.RelativePath) == null)
                    {
                        checkpoint.ActivePartialTree.Entries.Add(
                            new PayloadTreeEntryCheckpoint
                            {
                                RelativePath =
                                    expected.RelativePath,
                                IsDirectory =
                                    expected.IsDirectory,
                                FileId =
                                    Id(501 + expected.Ordinal),
                                Length = 0,
                                Sha256 =
                                    expected.IsDirectory
                                        ? String.Empty
                                        : Sha(new byte[0])
                            });
                        checkpoint.ActivePartialTree.Entries.Sort(
                            delegate(
                                PayloadTreeEntryCheckpoint first,
                                PayloadTreeEntryCheckpoint second)
                            {
                                return StringComparer.Ordinal.Compare(
                                    first.RelativePath,
                                    second.RelativePath);
                            });
                    }
                    return;
                }
                if (step ==
                    PayloadBuildStepKind.RewriteFileExact)
                {
                    PayloadBuildEntryCheckpoint expected =
                        checkpoint.ActiveBuild.
                            Entries[intent.EntryOrdinal];
                    TargetPayloadEntry target =
                        Find(manifest, expected.RelativePath);
                    byte[] bytes;
                    using (Stream input = source.OpenExact(target))
                    using (var output = new MemoryStream())
                    {
                        input.CopyTo(output);
                        bytes = output.ToArray();
                    }
                    PayloadTreeEntryCheckpoint observed =
                        Find(
                            checkpoint.ActivePartialTree,
                            expected.RelativePath);
                    observed.Length = bytes.Length;
                    observed.Sha256 = Sha(bytes);
                }
            }

            private static void CompleteEntryStep(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                PayloadBuildStepKind step)
            {
                PayloadBuildStepIntent intent =
                    checkpoint.ActiveBuild.ActiveIntent;
                checkpoint.ActiveBuild.Revision =
                    checked(checkpoint.ActiveBuild.Revision + 1);
                checkpoint.ActiveBuild.ActiveIntent = null;
                if (step == PayloadBuildStepKind.CreateRoot)
                {
                    checkpoint.ActiveBuild.
                        RootVolumeSerialNumber =
                            checkpoint.ActivePartialTree.
                                VolumeSerialNumber;
                    checkpoint.ActiveBuild.RootFileId =
                        checkpoint.ActivePartialTree.RootFileId;
                    return;
                }
                PayloadBuildEntryCheckpoint entry =
                    checkpoint.ActiveBuild.
                        Entries[intent.EntryOrdinal];
                PayloadTreeEntryCheckpoint observed =
                    Find(
                        checkpoint.ActivePartialTree,
                        entry.RelativePath);
                switch (step)
                {
                    case PayloadBuildStepKind.CreateEntry:
                        entry.Phase =
                            PayloadBuildEntryPhase.Created;
                        entry.FileId = observed.FileId;
                        break;
                    case PayloadBuildStepKind.RewriteFileExact:
                        entry.Phase =
                            PayloadBuildEntryPhase.Written;
                        break;
                    case PayloadBuildStepKind.FlushFile:
                        entry.Phase =
                            PayloadBuildEntryPhase.Flushed;
                        break;
                    case PayloadBuildStepKind.ReopenEntry:
                        entry.Phase =
                            PayloadBuildEntryPhase.Reopened;
                        entry.ObservedLength =
                            entry.ExpectedLength;
                        break;
                    case PayloadBuildStepKind.VerifyEntryHash:
                        entry.Phase =
                            PayloadBuildEntryPhase.Verified;
                        entry.ObservedSha256 =
                            entry.IsDirectory
                                ? String.Empty
                                : entry.ExpectedSha256;
                        break;
                    default:
                        throw new InvalidOperationException(
                            "Unexpected fake payload build step.");
                }
            }

            private void Seal(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                TargetPayloadManifest manifest)
            {
                raw.PhysicalCandidatePresent = true;
                if (raw.CrashPhysicalStep.HasValue &&
                    raw.CrashPhysicalStep.Value ==
                        PayloadBuildStepKind.SealCandidate)
                {
                    raw.CrashPhysicalStep = null;
                    throw new SimulatedCrashException();
                }
                PayloadPartialTreeObservation observed =
                    checkpoint.ActivePartialTree;
                long total = 0;
                foreach (TargetPayloadEntry entry in manifest.Content)
                {
                    total += entry.Length;
                }
                var candidate = new PayloadDirectoryCheckpoint
                {
                    TransactionId = manifest.TransactionId,
                    Slot = PayloadDirectorySlot.Candidate,
                    VolumeSerialNumber =
                        observed.VolumeSerialNumber,
                    FileId = observed.RootFileId,
                    Release = new ReleaseIdentity(
                        manifest.Target.Version,
                        manifest.Target.PackageFingerprint),
                    ContentSetSha256 =
                        manifest.ContentSetSha256,
                    ManifestInvariantDigest =
                        manifest.InvariantDigest,
                    FileCount = manifest.Content.Count,
                    TotalBytes = total
                };
                foreach (PayloadTreeEntryCheckpoint entry in
                    observed.Entries)
                {
                    candidate.Entries.Add(entry.DeepClone());
                }
                checkpoint.Committed.Revision =
                    checked(checkpoint.Committed.Revision + 1);
                checkpoint.Committed.Candidate = candidate;
                checkpoint.Committed.Shape =
                    checkpoint.Committed.Current == null
                        ? PayloadNamespaceShape.CandidateOnly
                        : PayloadNamespaceShape.
                            CurrentAndCandidate;
                checkpoint.ActiveBuild = null;
                checkpoint.ActivePartialTree = null;
            }

            private void Quarantine(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                PayloadBuildMutationPlan plan)
            {
                raw.PhysicalQuarantines.Add(plan.QuarantineId);
                if (raw.CrashPhysicalStep.HasValue &&
                    raw.CrashPhysicalStep.Value ==
                        PayloadBuildStepKind.QuarantineBuild)
                {
                    raw.CrashPhysicalStep = null;
                    throw new SimulatedCrashException();
                }
                PayloadPartialTreeObservation observed =
                    checkpoint.ActivePartialTree;
                var quarantine =
                    new PayloadQuarantineCheckpoint
                    {
                        SchemaVersion = 1,
                        QuarantineId = plan.QuarantineId,
                        TransactionId =
                            checkpoint.TransactionId,
                        RecoveryAuthorityInvariantDigest =
                            checkpoint.
                                RecoveryAuthorityInvariantDigest,
                        NamespaceRootInvariantDigest =
                            checkpoint.NamespaceRoot.
                                InvariantDigest,
                        SourceKind =
                            PayloadQuarantineSourceKind.
                                PartialBuild,
                        SourceBuildId =
                            checkpoint.ActiveBuild.BuildId,
                        QuarantineLeafName =
                            ".SBMS.quarantine." +
                                plan.QuarantineId,
                        VolumeSerialNumber =
                            observed.VolumeSerialNumber,
                        RootFileId = observed.RootFileId,
                        PartialTreeInvariantDigest =
                            observed.InvariantDigest,
                        Reason =
                            raw.WrongQuarantineReasonNext
                                ? PayloadQuarantineReason.Cleanup
                                : plan.QuarantineReason,
                        SourceLeafName = observed.LeafName,
                        TargetManifestInvariantDigest =
                            checkpoint.ActiveBuild.
                                TargetManifestInvariantDigest,
                        SourceReceiptInvariantDigest =
                            checkpoint.ActiveBuild.
                                SourceReceiptInvariantDigest
                    };
                raw.WrongQuarantineReasonNext = false;
                checkpoint.Quarantines.Add(quarantine);
                checkpoint.Quarantines.Sort(
                    delegate(
                        PayloadQuarantineCheckpoint first,
                        PayloadQuarantineCheckpoint second)
                    {
                        return StringComparer.Ordinal.Compare(
                            first.QuarantineId,
                            second.QuarantineId);
                    });
                checkpoint.ActiveBuild = null;
                checkpoint.ActivePartialTree = null;
            }

            private void ThrowIfDisposed()
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(
                        "FakeWorkspaceModel");
                }
            }

            private static PayloadQuarantineCheckpoint
                FindQuarantine(
                    PayloadBuildWorkspaceCheckpoint checkpoint,
                    string quarantineId)
            {
                foreach (PayloadQuarantineCheckpoint item in
                    checkpoint.Quarantines)
                {
                    if (String.Equals(
                            item.QuarantineId,
                            quarantineId,
                            StringComparison.Ordinal))
                    {
                        return item;
                    }
                }
                return null;
            }

            private static PayloadPurgeCheckpoint FindPurge(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                string purgeId)
            {
                foreach (PayloadPurgeCheckpoint item in
                    checkpoint.PendingPurges)
                {
                    if (String.Equals(
                            item.PurgeId,
                            purgeId,
                            StringComparison.Ordinal))
                    {
                        return item;
                    }
                }
                return null;
            }
        }

        private sealed class FakePayloadSource :
            ITrustedReleasePayloadSource
        {
            private readonly Dictionary<string, byte[]> files;
            private bool disposed;

            internal FakePayloadSource(
                TargetPayloadManifest manifest,
                Dictionary<string, byte[]> content)
            {
                Receipt =
                    new TrustedReleasePayloadReceipt(manifest);
                files = content;
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
                if (!files.TryGetValue(
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

        public static int Main()
        {
            Run(
                "happy path publishes exact candidate",
                HappyPathPublishesCandidate);
            Run(
                "candidate terminal restart is idempotent",
                CandidateTerminalIsIdempotent);
            Run(
                "candidate terminal enforces target authority",
                CandidateTerminalEnforcesAuthority);
            Run(
                "published intent survives process crash",
                PublishedIntentSurvivesCrash);
            Run(
                "create root physical crash reconciles",
                CreateRootCrashReconciles);
            Run(
                "create entry physical crash reconciles",
                CreateEntryCrashReconciles);
            Run(
                "rewrite physical crash reconciles",
                RewriteCrashReconciles);
            Run(
                "flush physical crash reconciles",
                FlushCrashReconciles);
            Run(
                "reopen physical crash reconciles",
                ReopenCrashReconciles);
            Run(
                "verify physical crash reconciles",
                VerifyCrashReconciles);
            Run(
                "seal commit lost return is terminal",
                SealLostReturnIsTerminal);
            Run(
                "seal rename before journal reconciles",
                SealRenameCrashReconciles);
            Run(
                "retryable outcome preserves full state",
                RetryablePreservesState);
            Run(
                "mutate then retry is rejected",
                MutateThenRetryIsRejected);
            Run(
                "wrong returned state is rejected",
                WrongReturnedStateIsRejected);
            Run(
                "over apply is rejected",
                OverApplyIsRejected);
            Run(
                "foreign CAS receipt is rejected",
                ForeignCasReceiptIsRejected);
            Run(
                "higher revision readback is rejected",
                HigherRevisionReadbackIsRejected);
            Run(
                "quarantine restarts through Advance",
                QuarantineRestartsThroughAdvance);
            Run(
                "quarantine reason substitution is rejected",
                QuarantineReasonSubstitutionIsRejected);
            Run(
                "quarantine commit lost return is terminal",
                QuarantineLostReturnIsTerminal);
            Run(
                "quarantine rename before journal reconciles",
                QuarantineRenameCrashReconciles);
            Run(
                "quarantine terminal enforces content identity",
                QuarantineTerminalEnforcesContentIdentity);
            Run(
                "purge advances arm observe complete",
                PurgeHappyPath);
            Run(
                "purge delete crash retries safely",
                PurgeDeleteCrashRetries);
            Run(
                "purge mutate then retry is rejected",
                PurgeMutateThenRetryIsRejected);
            Run(
                "purge stale absence is rejected",
                PurgeStaleAbsenceIsRejected);
            Run(
                "purge completion rejects recreated leaf",
                PurgeCompletionRejectsRecreatedLeaf);
            Run(
                "purge higher revision readback is rejected",
                PurgeHigherRevisionReadbackIsRejected);
            Run(
                "completed purge restart uses durable marker",
                CompletedPurgeRestartUsesMarker);
            Run(
                "completed purge rejects forged preimage",
                CompletedPurgeRejectsForgedPreimage);
            Run(
                "completed purge ledger exceeds legacy cap",
                CompletedPurgeLedgerExceedsLegacyCap);
            Run(
                "purge commit lost return uses durable marker",
                PurgeLostReturnUsesMarker);
            Run(
                "source receipt mismatch is rejected",
                SourceMismatchIsRejected);
            Run(
                "disposed state machine rejects access",
                DisposedStateMachineRejectsAccess);

            Console.WriteLine(
                "Protected payload build state-machine tests: " +
                passed + " passed, " + failed + " failed.");
            return failed == 0 ? 0 : 1;
        }

        private static void HappyPathPublishesCandidate()
        {
            Fixture fixture = FreshFixture();
            PayloadBuildAdvanceResult result =
                fixture.RunToTerminal();
            Equal(
                PayloadBuildAdvanceKind.CandidatePublished,
                result.Kind,
                "Build did not publish a candidate receipt.");
            True(
                result.CandidateReceipt != null,
                "Candidate receipt is missing.");
            PayloadBuildWorkspaceCheckpoint state =
                fixture.Raw.State;
            True(
                state.ActiveBuild == null &&
                state.ActivePartialTree == null,
                "Sealed build remained active.");
            Equal(
                fixture.Manifest.InvariantDigest,
                state.Committed.Candidate.
                    ManifestInvariantDigest,
                "Candidate manifest changed.");
            True(
                fixture.Raw.CompletedSteps.Contains(
                    PayloadBuildStepKind.CreateRoot) &&
                fixture.Raw.CompletedSteps.Contains(
                    PayloadBuildStepKind.CreateEntry) &&
                fixture.Raw.CompletedSteps.Contains(
                    PayloadBuildStepKind.RewriteFileExact) &&
                fixture.Raw.CompletedSteps.Contains(
                    PayloadBuildStepKind.FlushFile) &&
                fixture.Raw.CompletedSteps.Contains(
                    PayloadBuildStepKind.ReopenEntry) &&
                fixture.Raw.CompletedSteps.Contains(
                    PayloadBuildStepKind.VerifyEntryHash) &&
                fixture.Raw.CompletedSteps.Contains(
                    PayloadBuildStepKind.SealCandidate),
                "Happy path skipped a required build proof.");
        }

        private static void CandidateTerminalIsIdempotent()
        {
            Fixture fixture = FreshFixture();
            fixture.RunToTerminal();
            long revision = fixture.Raw.State.Revision;
            PayloadBuildAdvanceResult result =
                fixture.Advance(Id(3000));
            Equal(
                PayloadBuildAdvanceKind.
                    CandidateAlreadyPresent,
                result.Kind,
                "Restart did not recognize the candidate.");
            Equal(
                revision,
                fixture.Raw.State.Revision,
                "Terminal restart mutated workspace revision.");
        }

        private static void CandidateTerminalEnforcesAuthority()
        {
            Fixture fixture = FreshFixture();
            fixture.RunToTerminal();
            PayloadDirectoryCheckpoint candidate =
                fixture.Raw.State.Committed.Candidate;
            candidate.Release =
                new ReleaseIdentity(
                    "9.9.9",
                    candidate.Release.PackageFingerprint);
            Throws<InvalidOperationException>(
                delegate { fixture.Advance(Id(3050)); },
                "Candidate outside target authority was accepted.");
        }

        private static void PublishedIntentSurvivesCrash()
        {
            Fixture fixture = FreshFixture();
            fixture.Advance(Id(3100));
            fixture.Raw.CrashAfterPublishNext = true;
            Throws<SimulatedCrashException>(
                delegate { fixture.Advance(Id(3101)); },
                "Intent publish crash was not injected.");
            True(
                fixture.Raw.State.ActiveBuild.
                    ActiveIntent != null,
                "Published intent was lost.");
            fixture.RunToTerminal();
        }

        private static void CreateRootCrashReconciles()
        {
            CrashAndConverge(PayloadBuildStepKind.CreateRoot);
        }

        private static void CreateEntryCrashReconciles()
        {
            CrashAndConverge(PayloadBuildStepKind.CreateEntry);
        }

        private static void RewriteCrashReconciles()
        {
            CrashAndConverge(
                PayloadBuildStepKind.RewriteFileExact);
        }

        private static void FlushCrashReconciles()
        {
            CrashAndConverge(PayloadBuildStepKind.FlushFile);
        }

        private static void ReopenCrashReconciles()
        {
            CrashAndConverge(PayloadBuildStepKind.ReopenEntry);
        }

        private static void VerifyCrashReconciles()
        {
            CrashAndConverge(
                PayloadBuildStepKind.VerifyEntryHash);
        }

        private static void SealRenameCrashReconciles()
        {
            CrashAndConverge(
                PayloadBuildStepKind.SealCandidate);
        }

        private static void SealLostReturnIsTerminal()
        {
            Fixture fixture = FreshFixture();
            fixture.ReachArmedStep(
                PayloadBuildStepKind.SealCandidate);
            fixture.Raw.CrashAfterLogicalStep =
                PayloadBuildStepKind.SealCandidate;
            Throws<SimulatedCrashException>(
                delegate { fixture.Advance(Id(3250)); },
                "Seal lost-return crash was not injected.");
            PayloadBuildAdvanceResult result =
                fixture.Advance(Id(3251));
            Equal(
                PayloadBuildAdvanceKind.
                    CandidateAlreadyPresent,
                result.Kind,
                "Restart did not recognize committed candidate.");
        }

        private static void CrashAndConverge(
            PayloadBuildStepKind step)
        {
            Fixture fixture = FreshFixture();
            fixture.ReachArmedStep(step);
            fixture.Raw.CrashPhysicalStep = step;
            Throws<SimulatedCrashException>(
                delegate { fixture.Advance(Id(3200)); },
                "Physical crash was not injected for " + step + ".");
            True(
                fixture.Raw.State.ActiveBuild.
                    ActiveIntent != null,
                "Crash cleared durable intent for " + step + ".");
            PayloadBuildAdvanceResult result =
                fixture.RunToTerminal();
            Equal(
                PayloadBuildAdvanceKind.CandidatePublished,
                result.Kind,
                "Crash did not converge for " + step + ".");
        }

        private static void RetryablePreservesState()
        {
            Fixture fixture = FreshFixture();
            string before =
                fixture.Raw.State.InvariantDigest;
            fixture.Raw.RetryNext = true;
            PayloadBuildAdvanceResult result =
                fixture.Advance(Id(3300));
            Equal(
                PayloadBuildAdvanceKind.InProgress,
                result.Kind,
                "Retryable result was not in progress.");
            Equal(
                before,
                fixture.Raw.State.InvariantDigest,
                "Retryable result changed workspace.");
        }

        private static void MutateThenRetryIsRejected()
        {
            Fixture fixture = FreshFixture();
            fixture.Raw.MutateThenRetryNext = true;
            Throws<InvalidOperationException>(
                delegate { fixture.Advance(Id(3400)); },
                "Mutate-then-retry backend was accepted.");
        }

        private static void WrongReturnedStateIsRejected()
        {
            Fixture fixture = FreshFixture();
            fixture.Raw.ReturnWrongStateNext = true;
            Throws<InvalidOperationException>(
                delegate { fixture.Advance(Id(3500)); },
                "Backend returned-state substitution was accepted.");
        }

        private static void OverApplyIsRejected()
        {
            Fixture fixture = FreshFixture();
            fixture.Raw.OverApplyNext = true;
            Throws<InvalidOperationException>(
                delegate { fixture.Advance(Id(3600)); },
                "Workspace revision over-apply was accepted.");
        }

        private static void ForeignCasReceiptIsRejected()
        {
            Fixture fixture = FreshFixture();
            fixture.Raw.ForeignCasReceiptNext = true;
            Throws<InvalidOperationException>(
                delegate { fixture.Advance(Id(3700)); },
                "Foreign CAS receipt was accepted.");
        }

        private static void HigherRevisionReadbackIsRejected()
        {
            Fixture fixture = FreshFixture();
            fixture.Raw.PublishUnrelatedHigherNext = true;
            Throws<InvalidOperationException>(
                delegate { fixture.Advance(Id(3750)); },
                "Unproven higher build revision was accepted.");
        }

        private static void QuarantineRestartsThroughAdvance()
        {
            Fixture fixture = FreshFixture();
            fixture.ReachArmedStep(
                PayloadBuildStepKind.CreateEntry);
            fixture.Advance(Id(3800));
            string quarantineId = Id(3801);
            using (FakePayloadSource source = fixture.Source())
                using (
                    DeterministicProtectedPayloadBuildStateMachine machine =
                        fixture.Open())
            {
                PayloadBuildAdvanceResult armed =
                    machine.Quarantine(
                        source,
                        fixture.Manifest,
                        quarantineId,
                        quarantineId,
                        PayloadQuarantineReason.
                            InterruptedBuild);
                Equal(
                    PayloadBuildAdvanceKind.InProgress,
                    armed.Kind,
                    "Quarantine intent did not arm.");
            }
            PayloadBuildAdvanceResult completed =
                fixture.Advance(Id(3802));
            Equal(
                PayloadBuildAdvanceKind.Quarantined,
                completed.Kind,
                "Advance did not resume quarantine intent.");
            True(
                completed.QuarantineReceipt != null,
                "Quarantine receipt is missing.");
        }

        private static void QuarantineReasonSubstitutionIsRejected()
        {
            Fixture fixture = FreshFixture();
            fixture.ReachArmedStep(
                PayloadBuildStepKind.CreateEntry);
            fixture.Advance(Id(3900));
            string quarantineId = Id(3901);
            using (FakePayloadSource source = fixture.Source())
                using (
                    DeterministicProtectedPayloadBuildStateMachine machine =
                        fixture.Open())
            {
                machine.Quarantine(
                    source,
                    fixture.Manifest,
                    quarantineId,
                    quarantineId,
                    PayloadQuarantineReason.InterruptedBuild);
            }
            fixture.Raw.WrongQuarantineReasonNext = true;
            Throws<InvalidOperationException>(
                delegate { fixture.Advance(Id(3902)); },
                "Quarantine reason substitution was accepted.");
        }

        private static void QuarantineLostReturnIsTerminal()
        {
            Fixture fixture = FreshFixture();
            fixture.ReachArmedStep(
                PayloadBuildStepKind.CreateEntry);
            fixture.Advance(Id(3950));
            string quarantineId = Id(3951);
            using (FakePayloadSource source = fixture.Source())
                using (
                    DeterministicProtectedPayloadBuildStateMachine machine =
                        fixture.Open())
            {
                machine.Quarantine(
                    source,
                    fixture.Manifest,
                    quarantineId,
                    quarantineId,
                    PayloadQuarantineReason.InterruptedBuild);
            }
            fixture.Raw.CrashAfterLogicalStep =
                PayloadBuildStepKind.QuarantineBuild;
            Throws<SimulatedCrashException>(
                delegate { fixture.Advance(Id(3952)); },
                "Quarantine lost-return crash was not injected.");
            PayloadBuildAdvanceResult result =
                fixture.Advance(Id(3953));
            Equal(
                PayloadBuildAdvanceKind.
                    QuarantineAlreadyPresent,
                result.Kind,
                "Restart did not recognize durable quarantine.");
        }

        private static void QuarantineRenameCrashReconciles()
        {
            Fixture fixture = FreshFixture();
            fixture.ReachArmedStep(
                PayloadBuildStepKind.CreateEntry);
            fixture.Advance(Id(3960));
            string quarantineId = Id(3961);
            using (FakePayloadSource source = fixture.Source())
                using (
                    DeterministicProtectedPayloadBuildStateMachine machine =
                        fixture.Open())
            {
                machine.Quarantine(
                    source,
                    fixture.Manifest,
                    quarantineId,
                    quarantineId,
                    PayloadQuarantineReason.InterruptedBuild);
            }
            fixture.Raw.CrashPhysicalStep =
                PayloadBuildStepKind.QuarantineBuild;
            Throws<SimulatedCrashException>(
                delegate { fixture.Advance(Id(3962)); },
                "Quarantine rename crash was not injected.");
            True(
                fixture.Raw.PhysicalQuarantines.Contains(
                    quarantineId),
                "Quarantine rename was not physically modeled.");
            Equal(
                PayloadBuildAdvanceKind.Quarantined,
                fixture.Advance(Id(3963)).Kind,
                "Quarantine rename did not reconcile.");
        }

        private static void QuarantineTerminalEnforcesContentIdentity()
        {
            Fixture fixture = QuarantinedFixture();
            fixture.Raw.State.Quarantines[0].
                TargetManifestInvariantDigest =
                    Sha(new byte[] { 88 });
            Throws<InvalidOperationException>(
                delegate { fixture.Advance(Id(3970)); },
                "Quarantine for different content was accepted.");
        }

        private static void PurgeHappyPath()
        {
            Fixture fixture = QuarantinedFixture();
            string quarantineId =
                fixture.Raw.State.Quarantines[0].QuarantineId;
            string purgeId = Id(4100);
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                Equal(
                    PayloadPurgeAdvanceKind.Armed,
                    machine.AdvancePurge(
                        quarantineId,
                        purgeId).Kind,
                    "Purge did not arm.");
                Equal(
                    PayloadPurgeAdvanceKind.ObservedAbsent,
                    machine.AdvancePurge(
                        quarantineId,
                        purgeId).Kind,
                    "Purge did not record fresh absence.");
                Equal(
                    PayloadPurgeAdvanceKind.Complete,
                    machine.AdvancePurge(
                        quarantineId,
                        purgeId).Kind,
                    "Purge did not complete.");
            }
            True(
                fixture.Raw.State.Quarantines.Count == 0 &&
                fixture.Raw.State.PendingPurges.Count == 0,
                "Purge left logical maintenance state.");
        }

        private static void PurgeDeleteCrashRetries()
        {
            Fixture fixture = QuarantinedFixture();
            string quarantineId =
                fixture.Raw.State.Quarantines[0].QuarantineId;
            string purgeId = Id(4200);
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                machine.AdvancePurge(quarantineId, purgeId);
            }
            fixture.Raw.CrashPurgeDeleteNext = true;
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                Throws<SimulatedCrashException>(
                    delegate
                    {
                        machine.AdvancePurge(
                            quarantineId,
                            purgeId);
                    },
                    "Purge delete crash was not injected.");
            }
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                Equal(
                    PayloadPurgeAdvanceKind.ObservedAbsent,
                    machine.AdvancePurge(
                        quarantineId,
                        purgeId).Kind,
                    "Purge did not retry absence observation.");
            }
        }

        private static void PurgeMutateThenRetryIsRejected()
        {
            Fixture fixture = QuarantinedFixture();
            fixture.Raw.PurgeMutateThenRetryNext = true;
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                Throws<InvalidOperationException>(
                    delegate
                    {
                        machine.AdvancePurge(
                            fixture.Raw.State.
                                Quarantines[0].QuarantineId,
                            Id(4300));
                    },
                    "Purge mutate-then-retry was accepted.");
            }
        }

        private static void PurgeStaleAbsenceIsRejected()
        {
            Fixture fixture = QuarantinedFixture();
            string quarantineId =
                fixture.Raw.State.Quarantines[0].QuarantineId;
            string purgeId = Id(4400);
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                machine.AdvancePurge(quarantineId, purgeId);
            }
            fixture.Raw.WrongAbsenceRevisionNext = true;
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                Throws<InvalidOperationException>(
                    delegate
                    {
                        machine.AdvancePurge(
                            quarantineId,
                            purgeId);
                    },
                    "Stale purge absence was accepted.");
            }
        }

        private static void PurgeCompletionRejectsRecreatedLeaf()
        {
            Fixture fixture = QuarantinedFixture();
            string quarantineId =
                fixture.Raw.State.Quarantines[0].QuarantineId;
            string purgeId = Id(4500);
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                machine.AdvancePurge(quarantineId, purgeId);
                machine.AdvancePurge(quarantineId, purgeId);
            }
            fixture.Raw.PhysicalQuarantines.Add(quarantineId);
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                Throws<InvalidOperationException>(
                    delegate
                    {
                        machine.AdvancePurge(
                            quarantineId,
                            purgeId);
                    },
                    "Recreated quarantine leaf was purged as absent.");
            }
        }

        private static void PurgeHigherRevisionReadbackIsRejected()
        {
            Fixture fixture = QuarantinedFixture();
            fixture.Raw.PurgeUnrelatedHigherNext = true;
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                Throws<InvalidOperationException>(
                    delegate
                    {
                        machine.AdvancePurge(
                            fixture.Raw.State.
                                Quarantines[0].QuarantineId,
                            Id(4510));
                    },
                    "Unproven higher purge revision was accepted.");
            }
        }

        private static void CompletedPurgeRestartUsesMarker()
        {
            Fixture fixture = QuarantinedFixture();
            string quarantineId =
                fixture.Raw.State.Quarantines[0].QuarantineId;
            string purgeId = Id(4520);
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                machine.AdvancePurge(quarantineId, purgeId);
                machine.AdvancePurge(quarantineId, purgeId);
                machine.AdvancePurge(quarantineId, purgeId);
            }
            long revision = fixture.Raw.State.Revision;
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                PayloadPurgeAdvanceResult result =
                    machine.AdvancePurge(
                        quarantineId,
                        purgeId);
                Equal(
                    PayloadPurgeAdvanceKind.AlreadyComplete,
                    result.Kind,
                    "Completed purge marker was not recognized.");
                True(
                    result.Receipt == null,
                    "Restart forged a purge transition receipt.");
                Equal(
                    revision,
                    fixture.Raw.State.Revision,
                    "Completed purge restart mutated state.");
            }
        }

        private static void PurgeLostReturnUsesMarker()
        {
            Fixture fixture = QuarantinedFixture();
            string quarantineId =
                fixture.Raw.State.Quarantines[0].QuarantineId;
            string purgeId = Id(4530);
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                machine.AdvancePurge(quarantineId, purgeId);
                machine.AdvancePurge(quarantineId, purgeId);
            }
            fixture.Raw.CrashPurgeAfterLogicalNext = true;
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                Throws<SimulatedCrashException>(
                    delegate
                    {
                        machine.AdvancePurge(
                            quarantineId,
                            purgeId);
                    },
                    "Purge lost-return crash was not injected.");
            }
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                Equal(
                    PayloadPurgeAdvanceKind.AlreadyComplete,
                    machine.AdvancePurge(
                        quarantineId,
                        purgeId).Kind,
                    "Lost purge return did not converge from marker.");
            }
        }

        private static void CompletedPurgeRejectsForgedPreimage()
        {
            Fixture fixture = CompletedPurgeFixture(4540);
            fixture.Raw.State.CompletedPurges[0].
                AbsenceObservation.RootFileId = Id(7999);
            Throws<InvalidOperationException>(
                delegate
                {
                    using (
                        DeterministicProtectedPayloadBuildStateMachine
                            machine = fixture.Open())
                    {
                    }
                },
                "Forged completed purge preimage was accepted.");
        }

        private static void CompletedPurgeLedgerExceedsLegacyCap()
        {
            Fixture fixture = CompletedPurgeFixture(4550);
            PayloadCompletedPurgeCheckpoint template =
                fixture.Raw.State.CompletedPurges[0];
            fixture.Raw.State.CompletedPurges.Clear();
            for (int index = 0; index < 300; ++index)
            {
                string purgeId = Id(5000 + index);
                string quarantineId = Id(6000 + index);
                PayloadCompletedPurgeCheckpoint completed =
                    template.DeepClone();
                completed.PurgeId = purgeId;
                completed.QuarantineId = quarantineId;
                completed.Quarantine.QuarantineId =
                    quarantineId;
                completed.Quarantine.QuarantineLeafName =
                    ".SBMS.quarantine." + quarantineId;
                completed.AbsenceObservation.QuarantineId =
                    quarantineId;
                completed.AbsenceObservation.
                    QuarantineLeafName =
                        ".SBMS.quarantine." + quarantineId;
                fixture.Raw.State.CompletedPurges.Add(
                    completed);
            }
            string digest = fixture.Raw.State.InvariantDigest;
            True(
                !String.IsNullOrEmpty(digest),
                "Large completed purge ledger is invalid.");
        }

        private static Fixture CompletedPurgeFixture(int seed)
        {
            Fixture fixture = QuarantinedFixture();
            string quarantineId =
                fixture.Raw.State.Quarantines[0].QuarantineId;
            string purgeId = Id(seed);
            using (
                DeterministicProtectedPayloadBuildStateMachine machine =
                    fixture.Open())
            {
                machine.AdvancePurge(quarantineId, purgeId);
                machine.AdvancePurge(quarantineId, purgeId);
                machine.AdvancePurge(quarantineId, purgeId);
            }
            return fixture;
        }

        private static void SourceMismatchIsRejected()
        {
            Fixture fixture = FreshFixture();
            Dictionary<string, byte[]> otherFiles = TargetFiles();
            otherFiles["SBMS.exe"] =
                new byte[] { 9, 9, 9 };
            TargetPayloadManifest other =
                CreateManifest("0.3.1", otherFiles);
            using (
                var source =
                    new FakePayloadSource(other, otherFiles))
                using (
                    DeterministicProtectedPayloadBuildStateMachine machine =
                        fixture.Open())
            {
                Throws<InvalidOperationException>(
                    delegate
                    {
                        machine.Advance(
                            source,
                            fixture.Manifest,
                            BuildId,
                            Id(4000));
                    },
                    "Foreign source receipt was accepted.");
            }
        }

        private static void DisposedStateMachineRejectsAccess()
        {
            Fixture fixture = FreshFixture();
            DeterministicProtectedPayloadBuildStateMachine machine =
                fixture.Open();
            machine.Dispose();
            Throws<ObjectDisposedException>(
                delegate { machine.Inspect(); },
                "Disposed state machine remained usable.");
        }

        private static Fixture FreshFixture()
        {
            Dictionary<string, byte[]> files = TargetFiles();
            TargetPayloadManifest manifest =
                CreateManifest("0.3.0", files);
            PayloadDirectoryCheckpoint candidate =
                Directory(manifest, PayloadDirectorySlot.Candidate);
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
            var committed = new PayloadNamespaceCheckpoint
            {
                SchemaVersion = 1,
                Revision = 7,
                TransactionId = TransactionId,
                Shape = PayloadNamespaceShape.Empty,
                Current = null,
                Candidate = null,
                Backup = null
            };
            return new Fixture
            {
                Files = files,
                Manifest = manifest,
                Authority = authority,
                Raw = new RawWorkspace
                {
                    State =
                        new PayloadBuildWorkspaceCheckpoint
                        {
                            SchemaVersion = 2,
                            Revision = 11,
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
                            Committed = committed,
                            ActiveBuild = null,
                            ActivePartialTree = null
                        }
                }
            };
        }

        private static Fixture QuarantinedFixture()
        {
            Fixture fixture = FreshFixture();
            fixture.ReachArmedStep(
                PayloadBuildStepKind.CreateEntry);
            fixture.Advance(Id(4600));
            string quarantineId = Id(4601);
            using (FakePayloadSource source = fixture.Source())
                using (
                    DeterministicProtectedPayloadBuildStateMachine machine =
                        fixture.Open())
            {
                machine.Quarantine(
                    source,
                    fixture.Manifest,
                    quarantineId,
                    quarantineId,
                    PayloadQuarantineReason.InterruptedBuild);
            }
            fixture.Advance(Id(4602));
            return fixture;
        }

        private static Dictionary<string, byte[]> TargetFiles()
        {
            return new Dictionary<string, byte[]>(
                StringComparer.OrdinalIgnoreCase)
            {
                {
                    "SBMS.exe",
                    new byte[] { 1, 2, 3, 4 }
                },
                {
                    @"driver\SBMS.dll",
                    new byte[] { 5, 6, 7 }
                }
            };
        }

        private static TargetPayloadManifest CreateManifest(
            string version,
            Dictionary<string, byte[]> files)
        {
            var manifest = new TargetPayloadManifest
            {
                SchemaVersion = 1,
                TransactionId = TransactionId,
                Target = new ReleaseIdentity(
                    version,
                    Sha(new byte[]
                    {
                        (byte)version.Length
                    })),
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
            TargetPayloadManifest manifest,
            PayloadDirectorySlot slot)
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
                VolumeSerialNumber = Volume,
                FileId = Id(500),
                Release = new ReleaseIdentity(
                    manifest.Target.Version,
                    manifest.Target.PackageFingerprint),
                ContentSetSha256 =
                    manifest.ContentSetSha256,
                ManifestInvariantDigest =
                    manifest.InvariantDigest,
                FileCount = manifest.Content.Count,
                TotalBytes = total,
                Entries =
                    new List<PayloadTreeEntryCheckpoint>
                    {
                        new PayloadTreeEntryCheckpoint
                        {
                            RelativePath = "SBMS.exe",
                            IsDirectory = false,
                            FileId = Id(501),
                            Length =
                                manifest.Content[0].Length,
                            Sha256 =
                                manifest.Content[0].Sha256
                        },
                        new PayloadTreeEntryCheckpoint
                        {
                            RelativePath = "driver",
                            IsDirectory = true,
                            FileId = Id(502),
                            Length = 0,
                            Sha256 = String.Empty
                        },
                        new PayloadTreeEntryCheckpoint
                        {
                            RelativePath =
                                @"driver\SBMS.dll",
                            IsDirectory = false,
                            FileId = Id(503),
                            Length =
                                manifest.Content[1].Length,
                            Sha256 =
                                manifest.Content[1].Sha256
                        }
                    }
            };
        }

        private static PayloadContentAuthority ContentAuthority(
            PayloadDirectoryCheckpoint directory)
        {
            return new PayloadContentAuthority
            {
                Release = new ReleaseIdentity(
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
            PayloadPartialTreeObservation observation,
            string path)
        {
            foreach (PayloadTreeEntryCheckpoint entry in
                observation.Entries)
            {
                if (String.Equals(
                        entry.RelativePath,
                        path,
                        StringComparison.Ordinal))
                {
                    return entry;
                }
            }
            return null;
        }

        private static TargetPayloadEntry Find(
            TargetPayloadManifest manifest,
            string path)
        {
            foreach (TargetPayloadEntry entry in manifest.Content)
            {
                if (String.Equals(
                        entry.RelativePath,
                        path,
                        StringComparison.Ordinal))
                {
                    return entry;
                }
            }
            throw new InvalidOperationException(
                "Missing target payload entry " + path + ".");
        }

        private static string Sha(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] digest = algorithm.ComputeHash(bytes);
                var result =
                    new System.Text.StringBuilder(64);
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

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                ++passed;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                ++failed;
                Console.WriteLine(
                    "FAIL " + name + ": " +
                    error.GetType().Name + ": " +
                    error.Message);
            }
        }

        private static void True(
            bool condition,
            string message)
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
                    message + " Expected " + expected +
                    ", got " + actual + ".");
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
