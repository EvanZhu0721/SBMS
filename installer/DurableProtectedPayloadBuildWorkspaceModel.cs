using System;
using System.Collections.Generic;
using System.IO;

namespace SBMSSetup
{
    // A production native tree must return observations obtained from handles
    // rooted below the bound payload namespace. The exclusive session must
    // remain held through the checkpoint CAS performed by this model.
    internal interface IProtectedPayloadNativeTree : IDisposable
    {
        IProtectedPayloadNativeTreeSession OpenExclusive(
            PayloadNamespaceRootIdentity expectedRoot);
    }

    internal interface IProtectedPayloadNativeTreeSession : IDisposable
    {
        void DemandNamespaceExclusionHeld();
        void ValidateCheckpoint(
            PayloadBuildWorkspaceCheckpoint checkpoint);
        PayloadBuildPhysicalResult ApplyBuildStepExact(
            PayloadBuildMutationPlan plan,
            ITrustedReleasePayloadSource source);
        void DeleteQuarantineTreeExact(
            PayloadQuarantineCheckpoint quarantine,
            PayloadPurgeCheckpoint purge);
        PayloadQuarantineAbsenceObservation
            ObserveQuarantineAbsenceExact(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                PayloadQuarantineCheckpoint quarantine);
    }

    internal sealed class PayloadBuildPhysicalResult
    {
        private readonly PayloadPartialTreeObservation partialTree;
        private readonly PayloadDirectoryCheckpoint candidate;
        private readonly PayloadQuarantineCheckpoint quarantine;

        internal PayloadBuildPhysicalResult(
            PayloadBuildStepKind step,
            PayloadPartialTreeObservation observedPartialTree,
            PayloadDirectoryCheckpoint observedCandidate,
            PayloadQuarantineCheckpoint observedQuarantine)
        {
            if (!Enum.IsDefined(typeof(PayloadBuildStepKind), step))
            {
                throw new InvalidOperationException(
                    "Payload physical result step is invalid.");
            }
            bool seal = step == PayloadBuildStepKind.SealCandidate;
            bool quarantineStep =
                step == PayloadBuildStepKind.QuarantineBuild;
            if ((!seal && !quarantineStep) !=
                    (observedPartialTree != null) ||
                seal != (observedCandidate != null) ||
                quarantineStep != (observedQuarantine != null) ||
                (observedPartialTree != null &&
                    (observedCandidate != null ||
                     observedQuarantine != null)))
            {
                throw new InvalidOperationException(
                    "Payload physical result shape does not match its step.");
            }
            if (observedPartialTree != null)
            {
                observedPartialTree.Validate();
                partialTree = observedPartialTree.DeepClone();
            }
            if (observedCandidate != null)
            {
                observedCandidate.Validate();
                candidate = observedCandidate.DeepClone();
            }
            if (observedQuarantine != null)
            {
                observedQuarantine.Validate();
                quarantine = observedQuarantine.DeepClone();
            }
            Step = step;
        }

        internal readonly PayloadBuildStepKind Step;

        internal PayloadPartialTreeObservation PartialTree
        {
            get
            {
                return partialTree == null
                    ? null
                    : partialTree.DeepClone();
            }
        }

        internal PayloadDirectoryCheckpoint Candidate
        {
            get
            {
                return candidate == null
                    ? null
                    : candidate.DeepClone();
            }
        }

        internal PayloadQuarantineCheckpoint Quarantine
        {
            get
            {
                return quarantine == null
                    ? null
                    : quarantine.DeepClone();
            }
        }
    }

    // Composes the durable ProgramData checkpoint with an identity-bound
    // native payload namespace. This class deliberately owns no path-based
    // filesystem fallback.
    internal sealed class DurableProtectedPayloadBuildWorkspaceModel
        : IProtectedPayloadBuildWorkspaceModel
    {
        private readonly IProtectedPayloadWorkspaceCheckpointStore
            checkpointStore;
        private readonly ITransactionLeaseCoordinator leaseCoordinator;
        private IProtectedPayloadNativeTree nativeTree;
        private bool disposed;

        internal DurableProtectedPayloadBuildWorkspaceModel(
            IProtectedPayloadWorkspaceCheckpointStore durableCheckpointStore,
            ITransactionLeaseCoordinator transactionLeaseCoordinator,
            IProtectedPayloadNativeTree protectedNativeTree)
        {
            if (durableCheckpointStore == null ||
                transactionLeaseCoordinator == null ||
                protectedNativeTree == null)
            {
                throw new ArgumentNullException(
                    durableCheckpointStore == null
                        ? "durableCheckpointStore"
                        : (transactionLeaseCoordinator == null
                            ? "transactionLeaseCoordinator"
                            : "protectedNativeTree"));
            }
            checkpointStore = durableCheckpointStore;
            leaseCoordinator = transactionLeaseCoordinator;
            nativeTree = protectedNativeTree;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            IProtectedPayloadNativeTree release = nativeTree;
            nativeTree = null;
            if (release != null)
            {
                release.Dispose();
            }
        }

        public PayloadBuildWorkspaceState Inspect()
        {
            ThrowIfDisposed();
            using (leaseCoordinator.Acquire())
            {
                PayloadWorkspaceCheckpointReceipt current =
                    LoadAuthoritative();
                using (IProtectedPayloadNativeTreeSession session =
                    nativeTree.OpenExclusive(
                        current.State.Checkpoint.NamespaceRoot))
                {
                    session.DemandNamespaceExclusionHeld();
                    session.ValidateCheckpoint(
                        current.State.Checkpoint);
                    return new PayloadBuildWorkspaceState(
                        current.State.Checkpoint);
                }
            }
        }

        public PayloadBuildMutationOutcome ApplyExact(
            PayloadBuildMutationPlan plan,
            ITrustedReleasePayloadSource source)
        {
            ThrowIfDisposed();
            if (plan == null || source == null)
            {
                throw new ArgumentNullException(
                    plan == null ? "plan" : "source");
            }
            using (leaseCoordinator.Acquire())
            {
                PayloadWorkspaceCheckpointReceipt current =
                    RequireCurrent(plan.Before, plan.ExpectedCas);
                using (IProtectedPayloadNativeTreeSession session =
                    nativeTree.OpenExclusive(
                        current.State.Checkpoint.NamespaceRoot))
                {
                    session.DemandNamespaceExclusionHeld();
                    session.ValidateCheckpoint(
                        current.State.Checkpoint);
                    if (plan.Kind ==
                            PayloadBuildMutationKind.BeginBuild ||
                        plan.Kind ==
                            PayloadBuildMutationKind.PublishIntent)
                    {
                        PayloadBuildWorkspaceCheckpoint control =
                            plan.ExpectedControlAfter.Checkpoint;
                        plan.ValidateApplied(
                            new PayloadBuildWorkspaceState(control));
                        session.DemandNamespaceExclusionHeld();
                        return Applied(
                            plan.ExpectedCas,
                            current.State.CasToken,
                            SaveExact(
                                plan.ExpectedCas,
                                control).State);
                    }

                    PayloadBuildPhysicalResult physical =
                        session.ApplyBuildStepExact(plan, source);
                    PayloadBuildWorkspaceCheckpoint candidate =
                        CompletePhysicalStep(plan, physical);
                    PayloadBuildWorkspaceState candidateState =
                        new PayloadBuildWorkspaceState(candidate);
                    plan.ValidateApplied(candidateState);
                    session.DemandNamespaceExclusionHeld();
                    PayloadWorkspaceCheckpointReceipt committed =
                        SaveExact(plan.ExpectedCas, candidate);
                    return Applied(
                        plan.ExpectedCas,
                        current.State.CasToken,
                        committed.State);
                }
            }
        }

        public PayloadPurgeMutationOutcome ApplyPurgeExact(
            PayloadPurgeMutationPlan plan)
        {
            ThrowIfDisposed();
            if (plan == null)
            {
                throw new ArgumentNullException("plan");
            }
            using (leaseCoordinator.Acquire())
            {
                PayloadWorkspaceCheckpointReceipt current =
                    RequireCurrent(plan.Before, plan.ExpectedCas);
                using (IProtectedPayloadNativeTreeSession session =
                    nativeTree.OpenExclusive(
                        current.State.Checkpoint.NamespaceRoot))
                {
                    session.DemandNamespaceExclusionHeld();
                    session.ValidateCheckpoint(
                        current.State.Checkpoint);
                    if (plan.Kind ==
                        PayloadPurgeTransitionKind.Arm)
                    {
                        PayloadBuildWorkspaceCheckpoint armed =
                            ArmPurge(
                                current.State.Checkpoint,
                                plan);
                        PayloadBuildWorkspaceState armedState =
                            new PayloadBuildWorkspaceState(armed);
                        plan.ValidateApplied(armedState, null);
                        session.DemandNamespaceExclusionHeld();
                        PayloadWorkspaceCheckpointReceipt armedCommitted =
                            SaveExact(plan.ExpectedCas, armed);
                        return PurgeApplied(
                            plan.ExpectedCas,
                            current.State.CasToken,
                            armedCommitted.State,
                            null);
                    }

                    PayloadQuarantineCheckpoint quarantine =
                        FindQuarantine(
                            current.State.Checkpoint,
                            plan.QuarantineId);
                    PayloadPurgeCheckpoint purge =
                        FindPurge(
                            current.State.Checkpoint,
                            plan.PurgeId);
                    if (plan.Kind ==
                        PayloadPurgeTransitionKind.ObserveAbsent)
                    {
                        session.DeleteQuarantineTreeExact(
                            quarantine,
                            purge);
                    }
                    PayloadQuarantineAbsenceObservation absence =
                        session.ObserveQuarantineAbsenceExact(
                            current.State.Checkpoint,
                            quarantine);
                    PayloadBuildWorkspaceCheckpoint candidate =
                        plan.Kind ==
                            PayloadPurgeTransitionKind.ObserveAbsent
                            ? ObservePurgeAbsent(
                                current.State.Checkpoint,
                                purge,
                                absence)
                            : CompletePurge(
                                current.State.Checkpoint,
                                quarantine,
                                purge,
                                absence);
                    PayloadBuildWorkspaceState candidateState =
                        new PayloadBuildWorkspaceState(candidate);
                    plan.ValidateApplied(candidateState, absence);
                    // This is the critical absence-to-CAS boundary. A native
                    // implementation must fail this demand if exclusion was
                    // broken or released.
                    session.DemandNamespaceExclusionHeld();
                    PayloadWorkspaceCheckpointReceipt committed =
                        SaveExact(plan.ExpectedCas, candidate);
                    return PurgeApplied(
                        plan.ExpectedCas,
                        current.State.CasToken,
                        committed.State,
                        absence);
                }
            }
        }

        private PayloadWorkspaceCheckpointReceipt RequireCurrent(
            PayloadBuildWorkspaceState expectedState,
            PayloadWorkspaceCasToken expectedCas)
        {
            PayloadWorkspaceCheckpointReceipt current =
                LoadAuthoritative();
            current.State.RequireCas(expectedCas);
            expectedState.RequireCas(expectedCas);
            if (!String.Equals(
                    current.State.InvariantDigest,
                    expectedState.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload workspace plan is stale for the durable checkpoint.");
            }
            return current;
        }

        private PayloadWorkspaceCheckpointReceipt LoadAuthoritative()
        {
            PayloadWorkspaceCheckpointReadResult loaded =
                checkpointStore.Load();
            if (loaded == null ||
                loaded.Receipt == null ||
                loaded.Source !=
                    PayloadWorkspaceCheckpointReadSource.Primary ||
                loaded.RequiresPrimaryRepair)
            {
                throw new InvalidDataException(
                    "An authoritative payload workspace primary is required.");
            }
            return loaded.Receipt;
        }

        private PayloadWorkspaceCheckpointReceipt SaveExact(
            PayloadWorkspaceCasToken expected,
            PayloadBuildWorkspaceCheckpoint candidate)
        {
            try
            {
                return checkpointStore.Save(expected, candidate);
            }
            catch (PayloadWorkspaceCheckpointPublicationException failure)
            {
                if (!failure.CandidatePublished)
                {
                    throw;
                }
                PayloadWorkspaceCheckpointReceipt recovered =
                    LoadAuthoritative();
                if (recovered.State.Revision != candidate.Revision ||
                    !String.Equals(
                        recovered.State.InvariantDigest,
                        new PayloadBuildWorkspaceState(candidate).
                            InvariantDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Committed payload checkpoint publication could not " +
                        "be reconciled exactly.",
                        failure);
                }
                return recovered;
            }
        }

        private static PayloadBuildMutationOutcome Applied(
            PayloadWorkspaceCasToken expected,
            PayloadWorkspaceCasToken observed,
            PayloadBuildWorkspaceState committed)
        {
            return new PayloadBuildMutationOutcome(
                PayloadBuildMutationDisposition.Applied,
                committed,
                new PayloadBuildCasReceipt(
                    expected,
                    observed,
                    committed.CasToken));
        }

        private static PayloadPurgeMutationOutcome PurgeApplied(
            PayloadWorkspaceCasToken expected,
            PayloadWorkspaceCasToken observed,
            PayloadBuildWorkspaceState committed,
            PayloadQuarantineAbsenceObservation absence)
        {
            return new PayloadPurgeMutationOutcome(
                PayloadBuildMutationDisposition.Applied,
                committed,
                new PayloadBuildCasReceipt(
                    expected,
                    observed,
                    committed.CasToken),
                absence);
        }

        private static PayloadBuildWorkspaceCheckpoint CompletePhysicalStep(
            PayloadBuildMutationPlan plan,
            PayloadBuildPhysicalResult physical)
        {
            if (physical == null ||
                !plan.StepKind.HasValue ||
                physical.Step != plan.StepKind.Value)
            {
                throw new InvalidOperationException(
                    "Native payload tree returned a foreign physical step.");
            }
            PayloadBuildWorkspaceCheckpoint checkpoint =
                plan.Before.Checkpoint;
            checkpoint.Revision = checked(checkpoint.Revision + 1);
            PayloadBuildStepKind step = plan.StepKind.Value;
            if (step == PayloadBuildStepKind.SealCandidate)
            {
                PayloadDirectoryCheckpoint candidate =
                    physical.Candidate;
                checkpoint.Committed.Revision =
                    checked(checkpoint.Committed.Revision + 1);
                checkpoint.Committed.Candidate = candidate;
                checkpoint.Committed.Shape =
                    checkpoint.Committed.Current == null
                        ? PayloadNamespaceShape.CandidateOnly
                        : PayloadNamespaceShape.CurrentAndCandidate;
                checkpoint.ActiveBuild = null;
                checkpoint.ActivePartialTree = null;
                return checkpoint;
            }
            if (step == PayloadBuildStepKind.QuarantineBuild)
            {
                checkpoint.Quarantines.Add(physical.Quarantine);
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
                return checkpoint;
            }

            PayloadPartialTreeObservation observation =
                physical.PartialTree;
            checkpoint.ActivePartialTree = observation;
            checkpoint.ActiveBuild.Revision =
                checked(checkpoint.ActiveBuild.Revision + 1);
            PayloadBuildStepIntent intent =
                checkpoint.ActiveBuild.ActiveIntent;
            checkpoint.ActiveBuild.ActiveIntent = null;
            if (step == PayloadBuildStepKind.CreateRoot)
            {
                checkpoint.ActiveBuild.RootVolumeSerialNumber =
                    observation.VolumeSerialNumber;
                checkpoint.ActiveBuild.RootFileId =
                    observation.RootFileId;
                return checkpoint;
            }

            PayloadBuildEntryCheckpoint entry =
                checkpoint.ActiveBuild.Entries[intent.EntryOrdinal];
            PayloadTreeEntryCheckpoint observed =
                FindObserved(observation, entry.RelativePath);
            if (observed == null)
            {
                throw new InvalidOperationException(
                    "Native payload tree omitted the intended entry.");
            }
            switch (step)
            {
                case PayloadBuildStepKind.CreateEntry:
                    entry.Phase = PayloadBuildEntryPhase.Created;
                    entry.FileId = observed.FileId;
                    break;
                case PayloadBuildStepKind.RewriteFileExact:
                    entry.Phase = PayloadBuildEntryPhase.Written;
                    break;
                case PayloadBuildStepKind.FlushFile:
                    entry.Phase = PayloadBuildEntryPhase.Flushed;
                    break;
                case PayloadBuildStepKind.ReopenEntry:
                    entry.Phase = PayloadBuildEntryPhase.Reopened;
                    entry.ObservedLength = observed.Length;
                    break;
                case PayloadBuildStepKind.VerifyEntryHash:
                    entry.Phase = PayloadBuildEntryPhase.Verified;
                    entry.ObservedSha256 = entry.IsDirectory
                        ? String.Empty
                        : observed.Sha256;
                    break;
                default:
                    throw new InvalidOperationException(
                        "Native payload tree returned an unsupported step.");
            }
            return checkpoint;
        }

        private static PayloadBuildWorkspaceCheckpoint ArmPurge(
            PayloadBuildWorkspaceCheckpoint before,
            PayloadPurgeMutationPlan plan)
        {
            PayloadBuildWorkspaceCheckpoint checkpoint =
                before.DeepClone();
            PayloadQuarantineCheckpoint quarantine =
                FindQuarantine(checkpoint, plan.QuarantineId);
            checkpoint.Revision = checked(checkpoint.Revision + 1);
            checkpoint.PendingPurges.Add(
                new PayloadPurgeCheckpoint
                {
                    SchemaVersion = 1,
                    PurgeId = plan.PurgeId,
                    QuarantineId = plan.QuarantineId,
                    TransactionId = checkpoint.TransactionId,
                    RecoveryAuthorityInvariantDigest =
                        checkpoint.RecoveryAuthorityInvariantDigest,
                    NamespaceRootInvariantDigest =
                        checkpoint.NamespaceRoot.InvariantDigest,
                    QuarantineInvariantDigest =
                        quarantine.InvariantDigest,
                    VolumeSerialNumber =
                        quarantine.VolumeSerialNumber,
                    RootFileId = quarantine.RootFileId,
                    Phase = PayloadPurgePhase.Armed,
                    AbsenceObservationInvariantDigest =
                        String.Empty,
                    AbsenceObservedAtWorkspaceRevision = -1
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

        private static PayloadBuildWorkspaceCheckpoint ObservePurgeAbsent(
            PayloadBuildWorkspaceCheckpoint before,
            PayloadPurgeCheckpoint purge,
            PayloadQuarantineAbsenceObservation absence)
        {
            PayloadBuildWorkspaceCheckpoint checkpoint =
                before.DeepClone();
            PayloadPurgeCheckpoint mutable =
                FindPurge(checkpoint, purge.PurgeId);
            checkpoint.Revision = checked(checkpoint.Revision + 1);
            mutable.Phase = PayloadPurgePhase.ObservedAbsent;
            mutable.AbsenceObservationInvariantDigest =
                absence.InvariantDigest;
            mutable.AbsenceObservedAtWorkspaceRevision =
                before.Revision;
            return checkpoint;
        }

        private static PayloadBuildWorkspaceCheckpoint CompletePurge(
            PayloadBuildWorkspaceCheckpoint before,
            PayloadQuarantineCheckpoint quarantine,
            PayloadPurgeCheckpoint purge,
            PayloadQuarantineAbsenceObservation absence)
        {
            PayloadBuildWorkspaceCheckpoint checkpoint =
                before.DeepClone();
            PayloadQuarantineCheckpoint mutableQuarantine =
                FindQuarantine(
                    checkpoint,
                    quarantine.QuarantineId);
            PayloadPurgeCheckpoint mutablePurge =
                FindPurge(checkpoint, purge.PurgeId);
            checkpoint.Revision = checked(checkpoint.Revision + 1);
            checkpoint.PendingPurges.Remove(mutablePurge);
            checkpoint.Quarantines.Remove(mutableQuarantine);
            checkpoint.CompletedPurges.Add(
                new PayloadCompletedPurgeCheckpoint
                {
                    SchemaVersion = 1,
                    PurgeId = purge.PurgeId,
                    QuarantineId = quarantine.QuarantineId,
                    TransactionId = checkpoint.TransactionId,
                    RecoveryAuthorityInvariantDigest =
                        checkpoint.RecoveryAuthorityInvariantDigest,
                    NamespaceRootInvariantDigest =
                        checkpoint.NamespaceRoot.InvariantDigest,
                    Quarantine = quarantine.DeepClone(),
                    AbsenceObservation = absence.DeepClone(),
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

        private static PayloadQuarantineCheckpoint FindQuarantine(
            PayloadBuildWorkspaceCheckpoint checkpoint,
            string quarantineId)
        {
            foreach (PayloadQuarantineCheckpoint quarantine in
                checkpoint.Quarantines)
            {
                if (String.Equals(
                        quarantine.QuarantineId,
                        quarantineId,
                        StringComparison.Ordinal))
                {
                    return quarantine;
                }
            }
            throw new InvalidOperationException(
                "Payload quarantine checkpoint was not found.");
        }

        private static PayloadPurgeCheckpoint FindPurge(
            PayloadBuildWorkspaceCheckpoint checkpoint,
            string purgeId)
        {
            foreach (PayloadPurgeCheckpoint purge in
                checkpoint.PendingPurges)
            {
                if (String.Equals(
                        purge.PurgeId,
                        purgeId,
                        StringComparison.Ordinal))
                {
                    return purge;
                }
            }
            throw new InvalidOperationException(
                "Payload purge checkpoint was not found.");
        }

        private static PayloadTreeEntryCheckpoint FindObserved(
            PayloadPartialTreeObservation observation,
            string relativePath)
        {
            foreach (PayloadTreeEntryCheckpoint entry in
                observation.Entries)
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

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    "DurableProtectedPayloadBuildWorkspaceModel");
            }
        }
    }
}
