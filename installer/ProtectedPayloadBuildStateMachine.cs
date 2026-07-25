using System;
using System.Collections.Generic;

namespace SBMSSetup
{
    internal enum PayloadBuildMutationKind
    {
        BeginBuild,
        PublishIntent,
        CompleteIntent
    }

    internal enum PayloadBuildMutationDisposition
    {
        Applied,
        RetryableNotApplied
    }

    internal enum PayloadBuildAdvanceKind
    {
        InProgress,
        CandidatePublished,
        CandidateAlreadyPresent,
        Quarantined,
        QuarantineAlreadyPresent
    }

    internal sealed class PayloadBuildCasReceipt
    {
        internal PayloadBuildCasReceipt(
            PayloadWorkspaceCasToken expectedBefore,
            PayloadWorkspaceCasToken observedBefore,
            PayloadWorkspaceCasToken committedAfter)
        {
            if (expectedBefore == null ||
                observedBefore == null ||
                committedAfter == null)
            {
                throw new InvalidOperationException(
                    "Payload build CAS receipt is incomplete.");
            }
            expectedBefore.Validate();
            observedBefore.Validate();
            committedAfter.Validate();
            if (!SameToken(expectedBefore, observedBefore) ||
                !String.Equals(
                    expectedBefore.TransactionId,
                    committedAfter.TransactionId,
                    StringComparison.Ordinal) ||
                committedAfter.Revision !=
                    checked(expectedBefore.Revision + 1))
            {
                throw new InvalidOperationException(
                    "Payload build CAS receipt did not commit the inspected revision.");
            }
            ExpectedBefore = expectedBefore.DeepClone();
            ObservedBefore = observedBefore.DeepClone();
            CommittedAfter = committedAfter.DeepClone();
        }

        internal readonly PayloadWorkspaceCasToken ExpectedBefore;
        internal readonly PayloadWorkspaceCasToken ObservedBefore;
        internal readonly PayloadWorkspaceCasToken CommittedAfter;

        internal void Require(
            PayloadWorkspaceCasToken expected,
            PayloadWorkspaceCasToken committed)
        {
            if (!SameToken(ExpectedBefore, expected) ||
                !SameToken(ObservedBefore, expected) ||
                !SameToken(CommittedAfter, committed))
            {
                throw new InvalidOperationException(
                    "Payload build backend returned a foreign CAS receipt.");
            }
        }

        private static bool SameToken(
            PayloadWorkspaceCasToken first,
            PayloadWorkspaceCasToken second)
        {
            return first.Revision == second.Revision &&
                String.Equals(
                    first.TransactionId,
                    second.TransactionId,
                    StringComparison.Ordinal) &&
                String.Equals(
                    first.WorkspaceInvariantDigest,
                    second.WorkspaceInvariantDigest,
                    StringComparison.Ordinal);
        }
    }

    internal sealed class PayloadBuildMutationOutcome
    {
        internal PayloadBuildMutationOutcome(
            PayloadBuildMutationDisposition disposition,
            PayloadBuildWorkspaceState state,
            PayloadBuildCasReceipt casReceipt)
        {
            if (!Enum.IsDefined(
                    typeof(PayloadBuildMutationDisposition),
                    disposition) ||
                state == null)
            {
                throw new InvalidOperationException(
                    "Payload build mutation outcome is incomplete.");
            }
            if ((disposition ==
                    PayloadBuildMutationDisposition.Applied) !=
                (casReceipt != null))
            {
                throw new InvalidOperationException(
                    "Payload build mutation CAS evidence is inconsistent.");
            }
            Disposition = disposition;
            State = state;
            CasReceipt = casReceipt;
        }

        internal readonly PayloadBuildMutationDisposition Disposition;
        internal readonly PayloadBuildWorkspaceState State;
        internal readonly PayloadBuildCasReceipt CasReceipt;
    }

    internal enum PayloadPurgeAdvanceKind
    {
        Armed,
        ObservedAbsent,
        Complete,
        AlreadyComplete,
        Retryable
    }

    internal sealed class PayloadPurgeMutationOutcome
    {
        internal PayloadPurgeMutationOutcome(
            PayloadBuildMutationDisposition disposition,
            PayloadBuildWorkspaceState state,
            PayloadBuildCasReceipt casReceipt,
            PayloadQuarantineAbsenceObservation absenceObservation)
        {
            if (!Enum.IsDefined(
                    typeof(PayloadBuildMutationDisposition),
                    disposition) ||
                state == null ||
                ((disposition ==
                    PayloadBuildMutationDisposition.Applied) !=
                    (casReceipt != null)))
            {
                throw new InvalidOperationException(
                    "Payload purge mutation outcome is incomplete.");
            }
            Disposition = disposition;
            State = state;
            CasReceipt = casReceipt;
            AbsenceObservation =
                absenceObservation == null
                    ? null
                    : absenceObservation.DeepClone();
        }

        internal readonly PayloadBuildMutationDisposition Disposition;
        internal readonly PayloadBuildWorkspaceState State;
        internal readonly PayloadBuildCasReceipt CasReceipt;
        internal readonly PayloadQuarantineAbsenceObservation
            AbsenceObservation;
    }

    internal sealed class PayloadPurgeAdvanceResult
    {
        internal PayloadPurgeAdvanceResult(
            PayloadPurgeAdvanceKind kind,
            PayloadBuildWorkspaceState state,
            PayloadPurgeReceipt receipt)
        {
            if (!Enum.IsDefined(typeof(PayloadPurgeAdvanceKind), kind) ||
                state == null ||
                (((kind == PayloadPurgeAdvanceKind.Retryable ||
                    kind == PayloadPurgeAdvanceKind.AlreadyComplete)) !=
                    (receipt == null)))
            {
                throw new InvalidOperationException(
                    "Payload purge advance result is incomplete.");
            }
            Kind = kind;
            State = state;
            Receipt = receipt;
        }

        internal readonly PayloadPurgeAdvanceKind Kind;
        internal readonly PayloadBuildWorkspaceState State;
        internal readonly PayloadPurgeReceipt Receipt;
    }

    internal sealed class PayloadPurgeMutationPlan
    {
        private readonly PayloadRecoveryAuthority authority;

        private PayloadPurgeMutationPlan(
            PayloadRecoveryAuthority trustedAuthority,
            PayloadBuildWorkspaceState before,
            PayloadPurgeTransitionKind kind,
            string purgeId,
            string quarantineId)
        {
            if (trustedAuthority == null || before == null ||
                !Enum.IsDefined(typeof(PayloadPurgeTransitionKind), kind))
            {
                throw new InvalidOperationException(
                    "Payload purge mutation plan is incomplete.");
            }
            trustedAuthority.Validate();
            PayloadContractValidation.RequireCanonicalTransactionId(
                purgeId,
                "Payload purge ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                quarantineId,
                "Payload purge quarantine ID");
            PayloadBuildWorkspaceCheckpoint checkpoint =
                before.Checkpoint;
            if (!String.Equals(
                    checkpoint.RecoveryAuthorityInvariantDigest,
                    trustedAuthority.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload purge plan belongs to another authority.");
            }
            authority = trustedAuthority.DeepClone();
            Before = before;
            ExpectedCas = before.CasToken;
            Kind = kind;
            PurgeId = purgeId;
            QuarantineId = quarantineId;
        }

        internal readonly PayloadBuildWorkspaceState Before;
        internal readonly PayloadWorkspaceCasToken ExpectedCas;
        internal readonly PayloadPurgeTransitionKind Kind;
        internal readonly string PurgeId;
        internal readonly string QuarantineId;

        internal static PayloadPurgeMutationPlan Next(
            PayloadRecoveryAuthority authority,
            PayloadBuildWorkspaceState before,
            string quarantineId,
            string purgeId)
        {
            PayloadBuildWorkspaceCheckpoint checkpoint =
                before.Checkpoint;
            PayloadQuarantineCheckpoint quarantine =
                FindQuarantine(checkpoint, quarantineId);
            PayloadPurgeCheckpoint purge =
                FindPurge(checkpoint, purgeId);
            if (quarantine == null)
            {
                throw new InvalidOperationException(
                    "Completed payload purge lacks durable terminal evidence.");
            }
            if (purge == null)
            {
                return new PayloadPurgeMutationPlan(
                    authority,
                    before,
                    PayloadPurgeTransitionKind.Arm,
                    purgeId,
                    quarantineId);
            }
            if (!String.Equals(
                    purge.QuarantineId,
                    quarantineId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload purge ID is bound to another quarantine.");
            }
            return new PayloadPurgeMutationPlan(
                authority,
                before,
                purge.Phase == PayloadPurgePhase.Armed
                    ? PayloadPurgeTransitionKind.ObserveAbsent
                    : PayloadPurgeTransitionKind.Complete,
                purgeId,
                quarantineId);
        }

        internal PayloadPurgeReceipt ValidateApplied(
            PayloadBuildWorkspaceState after,
            PayloadQuarantineAbsenceObservation absenceObservation)
        {
            if (after == null ||
                after.Revision != checked(Before.Revision + 1))
            {
                throw new InvalidOperationException(
                    "Payload purge did not advance one workspace revision.");
            }
            return new PayloadPurgeReceipt(
                authority,
                Before,
                after,
                Kind,
                PurgeId,
                QuarantineId,
                absenceObservation);
        }

        private static PayloadQuarantineCheckpoint FindQuarantine(
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

    internal sealed class PayloadBuildAdvanceResult
    {
        internal PayloadBuildAdvanceResult(
            PayloadBuildAdvanceKind kind,
            PayloadBuildWorkspaceState state,
            PayloadBuildMutationPlan plan,
            PayloadCandidateReceipt candidateReceipt,
            PayloadQuarantineReceipt quarantineReceipt)
        {
            if (!Enum.IsDefined(typeof(PayloadBuildAdvanceKind), kind) ||
                state == null)
            {
                throw new InvalidOperationException(
                    "Payload build advance result is incomplete.");
            }
            bool candidate =
                kind == PayloadBuildAdvanceKind.CandidatePublished;
            bool quarantine =
                kind == PayloadBuildAdvanceKind.Quarantined;
            if (candidate != (candidateReceipt != null) ||
                quarantine != (quarantineReceipt != null) ||
                ((kind == PayloadBuildAdvanceKind.CandidateAlreadyPresent ||
                  kind == PayloadBuildAdvanceKind.QuarantineAlreadyPresent) &&
                    plan != null))
            {
                throw new InvalidOperationException(
                    "Payload build terminal evidence is inconsistent.");
            }
            Kind = kind;
            State = state;
            Plan = plan;
            CandidateReceipt = candidateReceipt;
            QuarantineReceipt = quarantineReceipt;
        }

        internal readonly PayloadBuildAdvanceKind Kind;
        internal readonly PayloadBuildWorkspaceState State;
        internal readonly PayloadBuildMutationPlan Plan;
        internal readonly PayloadCandidateReceipt CandidateReceipt;
        internal readonly PayloadQuarantineReceipt QuarantineReceipt;
    }

    // This is a durable-model seam, not a production filesystem adapter.
    // ApplyExact performs one control-plane mutation. A native implementation
    // must separately close rename-before-journal crash reconciliation.
    // ApplyPurgeExact has a stronger physical contract: ObserveAbsent and
    // Complete must obtain the absence observation while holding an
    // identity-bound namespace lease that excludes same-name recreation
    // through the exact CAS commit. An adapter that cannot preserve that
    // critical section must fail without applying the control mutation.
    // The fake backend models this requirement; no production adapter exists
    // yet, so these contracts are not filesystem crash-safety evidence.
    internal interface IProtectedPayloadBuildWorkspaceModel : IDisposable
    {
        PayloadBuildWorkspaceState Inspect();
        PayloadBuildMutationOutcome ApplyExact(
            PayloadBuildMutationPlan plan,
            ITrustedReleasePayloadSource source);
        PayloadPurgeMutationOutcome ApplyPurgeExact(
            PayloadPurgeMutationPlan plan);
    }

    internal sealed class PayloadBuildMutationPlan
    {
        private readonly PayloadRecoveryAuthority authority;
        private readonly TargetPayloadManifest manifest;
        private readonly TrustedReleasePayloadReceipt sourceReceipt;

        private PayloadBuildMutationPlan(
            PayloadRecoveryAuthority trustedAuthority,
            TargetPayloadManifest expectedManifest,
            TrustedReleasePayloadReceipt trustedSourceReceipt,
            PayloadBuildMutationKind kind,
            PayloadBuildStepKind? stepKind,
            PayloadBuildWorkspaceState before,
            PayloadBuildWorkspaceState expectedControlAfter,
            string buildId,
            string intentId,
            int entryOrdinal,
            string quarantineId,
            PayloadQuarantineReason quarantineReason)
        {
            if (trustedAuthority == null ||
                expectedManifest == null ||
                trustedSourceReceipt == null ||
                before == null ||
                !Enum.IsDefined(typeof(PayloadBuildMutationKind), kind))
            {
                throw new InvalidOperationException(
                    "Payload build mutation plan is incomplete.");
            }
            trustedAuthority.Validate();
            expectedManifest.Validate();
            trustedSourceReceipt.Manifest.Validate();
            before.Checkpoint.Validate();
            ExpectedCas = before.CasToken;
            authority = trustedAuthority.DeepClone();
            manifest = expectedManifest.DeepClone();
            sourceReceipt =
                new TrustedReleasePayloadReceipt(
                    trustedSourceReceipt.Manifest);
            Kind = kind;
            StepKind = stepKind;
            Before = before;
            ExpectedControlAfter = expectedControlAfter;
            BuildId = buildId;
            IntentId = intentId;
            EntryOrdinal = entryOrdinal;
            QuarantineId = quarantineId;
            QuarantineReason = quarantineReason;
            ValidateIdentityBindings();
        }

        internal readonly PayloadBuildMutationKind Kind;
        internal readonly PayloadBuildStepKind? StepKind;
        internal readonly PayloadBuildWorkspaceState Before;
        internal readonly PayloadBuildWorkspaceState ExpectedControlAfter;
        internal readonly PayloadWorkspaceCasToken ExpectedCas;
        internal readonly string BuildId;
        internal readonly string IntentId;
        internal readonly int EntryOrdinal;
        internal readonly string QuarantineId;
        internal readonly PayloadQuarantineReason QuarantineReason;

        internal PayloadRecoveryAuthority Authority
        {
            get { return authority.DeepClone(); }
        }

        internal TargetPayloadManifest Manifest
        {
            get { return manifest.DeepClone(); }
        }

        internal TrustedReleasePayloadReceipt SourceReceipt
        {
            get
            {
                return new TrustedReleasePayloadReceipt(
                    sourceReceipt.Manifest);
            }
        }

        internal static PayloadBuildMutationPlan Begin(
            PayloadRecoveryAuthority authority,
            PayloadBuildWorkspaceState before,
            TargetPayloadManifest manifest,
            TrustedReleasePayloadReceipt sourceReceipt,
            string buildId)
        {
            PayloadContractValidation.RequireCanonicalTransactionId(
                buildId,
                "Payload build ID");
            PayloadBuildWorkspaceCheckpoint checkpoint = before.Checkpoint;
            if (checkpoint.ActiveBuild != null ||
                checkpoint.ActivePartialTree != null ||
                checkpoint.Committed.Candidate != null)
            {
                throw new InvalidOperationException(
                    "Payload build cannot begin in a non-empty staging workspace.");
            }
            PayloadBuildWorkspaceCheckpoint expected =
                checkpoint.DeepClone();
            expected.Revision = checked(expected.Revision + 1);
            expected.ActiveBuild = CreateInitialJournal(
                authority,
                checkpoint,
                manifest,
                sourceReceipt,
                buildId);
            expected.ActivePartialTree =
                new PayloadPartialTreeObservation
                {
                    SchemaVersion = 1,
                    BuildId = buildId,
                    LeafName = ".SBMS.build." + buildId,
                    Exists = false,
                    VolumeSerialNumber = 0,
                    RootFileId = String.Empty,
                    Entries = new List<PayloadTreeEntryCheckpoint>()
                };
            PayloadBuildWorkspaceState expectedState =
                new PayloadBuildWorkspaceState(expected);
            return new PayloadBuildMutationPlan(
                authority,
                manifest,
                sourceReceipt,
                PayloadBuildMutationKind.BeginBuild,
                null,
                before,
                expectedState,
                buildId,
                String.Empty,
                -1,
                String.Empty,
                PayloadQuarantineReason.InterruptedBuild);
        }

        internal static PayloadBuildMutationPlan Publish(
            PayloadRecoveryAuthority authority,
            PayloadBuildWorkspaceState before,
            TargetPayloadManifest manifest,
            TrustedReleasePayloadReceipt sourceReceipt,
            PayloadBuildStepKind stepKind,
            int entryOrdinal,
            string intentId,
            string quarantineId,
            PayloadQuarantineReason quarantineReason)
        {
            PayloadContractValidation.RequireCanonicalTransactionId(
                intentId,
                "Payload build intent ID");
            PayloadBuildWorkspaceCheckpoint checkpoint = before.Checkpoint;
            RequireActiveBindings(
                authority,
                checkpoint,
                manifest,
                sourceReceipt);
            if (checkpoint.ActiveBuild.ActiveIntent != null)
            {
                throw new InvalidOperationException(
                    "Payload build already has an active intent.");
            }
            if (stepKind == PayloadBuildStepKind.QuarantineBuild)
            {
                PayloadContractValidation.RequireCanonicalTransactionId(
                    quarantineId,
                    "Payload build quarantine ID");
                if (!String.Equals(
                        quarantineId,
                        intentId,
                        StringComparison.Ordinal) ||
                    quarantineReason !=
                        PayloadQuarantineReason.InterruptedBuild)
                {
                    throw new InvalidOperationException(
                        "Payload build quarantine identity is not restart-stable.");
                }
                if (!Enum.IsDefined(
                        typeof(PayloadQuarantineReason),
                        quarantineReason) ||
                    !checkpoint.ActivePartialTree.Exists)
                {
                    throw new InvalidOperationException(
                        "Payload build quarantine intent is incomplete.");
                }
            }
            else if (!String.IsNullOrEmpty(quarantineId))
            {
                throw new InvalidOperationException(
                    "Non-quarantine build intent has a quarantine ID.");
            }

            PayloadBuildWorkspaceCheckpoint expected =
                checkpoint.DeepClone();
            expected.Revision = checked(expected.Revision + 1);
            expected.ActiveBuild.Revision =
                checked(expected.ActiveBuild.Revision + 1);
            expected.ActiveBuild.ActiveIntent =
                new PayloadBuildStepIntent
                {
                    SchemaVersion = 1,
                    IntentId = intentId,
                    JournalRevision =
                        expected.ActiveBuild.Revision,
                    Kind = stepKind,
                    EntryOrdinal = entryOrdinal,
                    ExpectedEntryInvariantDigest =
                        entryOrdinal < 0
                            ? String.Empty
                            : expected.ActiveBuild.
                                Entries[entryOrdinal].InvariantDigest,
                    ObservedPartialTreeInvariantDigest =
                        checkpoint.ActivePartialTree.InvariantDigest
                };
            PayloadBuildWorkspaceState expectedState =
                new PayloadBuildWorkspaceState(expected);
            return new PayloadBuildMutationPlan(
                authority,
                manifest,
                sourceReceipt,
                PayloadBuildMutationKind.PublishIntent,
                stepKind,
                before,
                expectedState,
                checkpoint.ActiveBuild.BuildId,
                intentId,
                entryOrdinal,
                quarantineId,
                quarantineReason);
        }

        internal static PayloadBuildMutationPlan Complete(
            PayloadRecoveryAuthority authority,
            PayloadBuildWorkspaceState before,
            TargetPayloadManifest manifest,
            TrustedReleasePayloadReceipt sourceReceipt,
            string quarantineId,
            PayloadQuarantineReason quarantineReason)
        {
            PayloadBuildWorkspaceCheckpoint checkpoint = before.Checkpoint;
            RequireActiveBindings(
                authority,
                checkpoint,
                manifest,
                sourceReceipt);
            PayloadBuildStepIntent intent =
                checkpoint.ActiveBuild.ActiveIntent;
            if (intent == null)
            {
                throw new InvalidOperationException(
                    "Payload build has no intent to complete.");
            }
            if (intent.Kind == PayloadBuildStepKind.QuarantineBuild)
            {
                PayloadContractValidation.RequireCanonicalTransactionId(
                    quarantineId,
                    "Payload build quarantine ID");
                if (!String.Equals(
                        quarantineId,
                        intent.IntentId,
                        StringComparison.Ordinal) ||
                    quarantineReason !=
                        PayloadQuarantineReason.InterruptedBuild)
                {
                    throw new InvalidOperationException(
                        "Payload build quarantine completion changed durable identity.");
                }
            }
            else if (!String.IsNullOrEmpty(quarantineId))
            {
                throw new InvalidOperationException(
                    "Non-quarantine completion has a quarantine ID.");
            }
            return new PayloadBuildMutationPlan(
                authority,
                manifest,
                sourceReceipt,
                PayloadBuildMutationKind.CompleteIntent,
                intent.Kind,
                before,
                null,
                checkpoint.ActiveBuild.BuildId,
                intent.IntentId,
                intent.EntryOrdinal,
                quarantineId,
                quarantineReason);
        }

        internal void ValidateApplied(
            PayloadBuildWorkspaceState after)
        {
            if (after == null)
            {
                throw new InvalidOperationException(
                    "Payload build backend returned no applied state.");
            }
            after.Checkpoint.Validate();
            Before.RequireCas(ExpectedCas);
            if (Kind == PayloadBuildMutationKind.BeginBuild ||
                Kind == PayloadBuildMutationKind.PublishIntent)
            {
                if (ExpectedControlAfter == null ||
                    !String.Equals(
                        ExpectedControlAfter.InvariantDigest,
                        after.InvariantDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Payload build backend changed the control transition.");
                }
                return;
            }
            ValidateCompletedIntent(after);
        }

        private void ValidateIdentityBindings()
        {
            PayloadBuildWorkspaceCheckpoint checkpoint =
                Before.Checkpoint;
            if (!String.Equals(
                    checkpoint.RecoveryAuthorityInvariantDigest,
                    authority.InvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    checkpoint.TransactionId,
                    manifest.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    manifest.InvariantDigest,
                    sourceReceipt.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload build plan authority or source is not bound.");
            }
        }

        private void ValidateCompletedIntent(
            PayloadBuildWorkspaceState after)
        {
            PayloadBuildWorkspaceCheckpoint beforeCheckpoint =
                Before.Checkpoint;
            PayloadBuildWorkspaceCheckpoint afterCheckpoint =
                after.Checkpoint;
            PayloadBuildStepIntent intent =
                beforeCheckpoint.ActiveBuild.ActiveIntent;
            if (intent == null ||
                !StepKind.HasValue ||
                intent.Kind != StepKind.Value ||
                afterCheckpoint.Revision !=
                    checked(beforeCheckpoint.Revision + 1) ||
                !SameWorkspaceEnvelope(
                    beforeCheckpoint,
                    afterCheckpoint))
            {
                throw new InvalidOperationException(
                    "Payload build completion changed its workspace envelope.");
            }

            if (intent.Kind == PayloadBuildStepKind.SealCandidate)
            {
                ValidateSeal(beforeCheckpoint, afterCheckpoint, after);
                return;
            }
            if (intent.Kind == PayloadBuildStepKind.QuarantineBuild)
            {
                new PayloadQuarantineReceipt(
                    authority,
                    Before,
                    after,
                    QuarantineId);
                PayloadQuarantineCheckpoint added = null;
                foreach (PayloadQuarantineCheckpoint quarantine in
                    afterCheckpoint.Quarantines)
                {
                    if (String.Equals(
                            quarantine.QuarantineId,
                            QuarantineId,
                            StringComparison.Ordinal))
                    {
                        added = quarantine;
                        break;
                    }
                }
                if (added == null ||
                    added.Reason != QuarantineReason)
                {
                    throw new InvalidOperationException(
                        "Payload build quarantine changed its bound reason.");
                }
                return;
            }

            if (afterCheckpoint.ActiveBuild == null ||
                afterCheckpoint.ActivePartialTree == null)
            {
                throw new InvalidOperationException(
                    "Payload build completion removed the active build.");
            }
            PayloadBuildWorkspaceCheckpoint expected =
                beforeCheckpoint.DeepClone();
            expected.Revision = checked(expected.Revision + 1);
            expected.ActiveBuild.Revision =
                checked(expected.ActiveBuild.Revision + 1);
            expected.ActiveBuild.ActiveIntent = null;

            switch (intent.Kind)
            {
                case PayloadBuildStepKind.CreateRoot:
                    CompleteCreateRoot(expected, afterCheckpoint);
                    break;
                case PayloadBuildStepKind.CreateEntry:
                    CompleteCreateEntry(
                        expected,
                        afterCheckpoint,
                        intent.EntryOrdinal);
                    break;
                case PayloadBuildStepKind.RewriteFileExact:
                    CompleteRewrite(
                        expected,
                        afterCheckpoint,
                        intent.EntryOrdinal);
                    break;
                case PayloadBuildStepKind.FlushFile:
                    CompleteFlush(
                        expected,
                        beforeCheckpoint,
                        intent.EntryOrdinal);
                    break;
                case PayloadBuildStepKind.ReopenEntry:
                    CompleteReopen(
                        expected,
                        beforeCheckpoint,
                        intent.EntryOrdinal);
                    break;
                case PayloadBuildStepKind.VerifyEntryHash:
                    CompleteVerify(
                        expected,
                        beforeCheckpoint,
                        intent.EntryOrdinal);
                    break;
                default:
                    throw new InvalidOperationException(
                        "Payload build completion kind is unsupported.");
            }
            if (!String.Equals(
                    new PayloadBuildWorkspaceState(expected).
                        InvariantDigest,
                    after.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload build backend did not publish the exact completed step.");
            }
        }

        private void ValidateSeal(
            PayloadBuildWorkspaceCheckpoint before,
            PayloadBuildWorkspaceCheckpoint after,
            PayloadBuildWorkspaceState afterState)
        {
            if (after.ActiveBuild != null ||
                after.ActivePartialTree != null ||
                !SameQuarantines(
                    before.Quarantines,
                    after.Quarantines) ||
                !SamePurges(
                    before.PendingPurges,
                    after.PendingPurges))
            {
                throw new InvalidOperationException(
                    "Payload seal changed unrelated workspace state.");
            }
            var committedBefore =
                new PayloadNamespaceState(before.Committed);
            var committedAfter =
                new PayloadNamespaceState(after.Committed);
            new PayloadCandidateReceipt(
                authority,
                manifest,
                committedBefore,
                committedAfter);

            PayloadDirectoryCheckpoint candidate =
                after.Committed.Candidate;
            PayloadPartialTreeObservation observed =
                before.ActivePartialTree;
            if (candidate == null ||
                !observed.Exists ||
                candidate.VolumeSerialNumber !=
                    observed.VolumeSerialNumber ||
                !String.Equals(
                    candidate.FileId,
                    observed.RootFileId,
                    StringComparison.Ordinal) ||
                candidate.Entries.Count != observed.Entries.Count)
            {
                throw new InvalidOperationException(
                    "Payload seal did not preserve the verified build identity.");
            }
            for (int index = 0;
                index < candidate.Entries.Count;
                ++index)
            {
                if (!String.Equals(
                        candidate.Entries[index].InvariantDigest,
                        observed.Entries[index].InvariantDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Payload seal published another tree.");
                }
            }
        }

        private static void CompleteCreateRoot(
            PayloadBuildWorkspaceCheckpoint expected,
            PayloadBuildWorkspaceCheckpoint actual)
        {
            PayloadPartialTreeObservation observation =
                actual.ActivePartialTree;
            if (!observation.Exists ||
                observation.Entries.Count != 0 ||
                observation.VolumeSerialNumber !=
                    expected.NamespaceRoot.VolumeSerialNumber)
            {
                throw new InvalidOperationException(
                    "Payload build root completion is not an empty owned root.");
            }
            expected.ActivePartialTree = observation.DeepClone();
            expected.ActiveBuild.RootVolumeSerialNumber =
                observation.VolumeSerialNumber;
            expected.ActiveBuild.RootFileId =
                observation.RootFileId;
        }

        private static void CompleteCreateEntry(
            PayloadBuildWorkspaceCheckpoint expected,
            PayloadBuildWorkspaceCheckpoint actual,
            int ordinal)
        {
            PayloadBuildEntryCheckpoint entry =
                expected.ActiveBuild.Entries[ordinal];
            PayloadTreeEntryCheckpoint observed =
                FindObserved(actual.ActivePartialTree, entry.RelativePath);
            if (observed == null ||
                observed.IsDirectory != entry.IsDirectory)
            {
                throw new InvalidOperationException(
                    "Payload build entry creation was not observed.");
            }
            expected.ActivePartialTree =
                actual.ActivePartialTree.DeepClone();
            entry.Phase = PayloadBuildEntryPhase.Created;
            entry.FileId = observed.FileId;
        }

        private static void CompleteRewrite(
            PayloadBuildWorkspaceCheckpoint expected,
            PayloadBuildWorkspaceCheckpoint actual,
            int ordinal)
        {
            PayloadBuildEntryCheckpoint entry =
                expected.ActiveBuild.Entries[ordinal];
            PayloadTreeEntryCheckpoint observed =
                FindObserved(actual.ActivePartialTree, entry.RelativePath);
            if (observed == null ||
                observed.IsDirectory ||
                !String.Equals(
                    observed.FileId,
                    entry.FileId,
                    StringComparison.Ordinal) ||
                observed.Length != entry.ExpectedLength ||
                !String.Equals(
                    observed.Sha256,
                    entry.ExpectedSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload build rewrite did not produce exact bytes.");
            }
            expected.ActivePartialTree =
                actual.ActivePartialTree.DeepClone();
            entry.Phase = PayloadBuildEntryPhase.Written;
        }

        private static void CompleteFlush(
            PayloadBuildWorkspaceCheckpoint expected,
            PayloadBuildWorkspaceCheckpoint before,
            int ordinal)
        {
            RequireObservationUnchanged(expected, before);
            expected.ActiveBuild.Entries[ordinal].Phase =
                PayloadBuildEntryPhase.Flushed;
        }

        private static void CompleteReopen(
            PayloadBuildWorkspaceCheckpoint expected,
            PayloadBuildWorkspaceCheckpoint before,
            int ordinal)
        {
            RequireObservationUnchanged(expected, before);
            PayloadBuildEntryCheckpoint entry =
                expected.ActiveBuild.Entries[ordinal];
            PayloadTreeEntryCheckpoint observed =
                FindObserved(
                    before.ActivePartialTree,
                    entry.RelativePath);
            if (observed == null ||
                !String.Equals(
                    observed.FileId,
                    entry.FileId,
                    StringComparison.Ordinal) ||
                observed.Length != entry.ExpectedLength)
            {
                throw new InvalidOperationException(
                    "Payload build reopen proof changed identity or length.");
            }
            entry.Phase = PayloadBuildEntryPhase.Reopened;
            entry.ObservedLength = entry.ExpectedLength;
        }

        private static void CompleteVerify(
            PayloadBuildWorkspaceCheckpoint expected,
            PayloadBuildWorkspaceCheckpoint before,
            int ordinal)
        {
            RequireObservationUnchanged(expected, before);
            PayloadBuildEntryCheckpoint entry =
                expected.ActiveBuild.Entries[ordinal];
            PayloadTreeEntryCheckpoint observed =
                FindObserved(
                    before.ActivePartialTree,
                    entry.RelativePath);
            if (observed == null ||
                observed.Length != entry.ExpectedLength ||
                (!entry.IsDirectory &&
                    !String.Equals(
                        observed.Sha256,
                        entry.ExpectedSha256,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "Payload build verification did not prove exact content.");
            }
            entry.Phase = PayloadBuildEntryPhase.Verified;
            entry.ObservedSha256 =
                entry.IsDirectory
                    ? String.Empty
                    : entry.ExpectedSha256;
        }

        private static void RequireObservationUnchanged(
            PayloadBuildWorkspaceCheckpoint expected,
            PayloadBuildWorkspaceCheckpoint before)
        {
            if (!String.Equals(
                    expected.ActivePartialTree.InvariantDigest,
                    before.ActivePartialTree.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload build observation unexpectedly changed.");
            }
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

        private static bool SameWorkspaceEnvelope(
            PayloadBuildWorkspaceCheckpoint before,
            PayloadBuildWorkspaceCheckpoint after)
        {
            return String.Equals(
                    before.TransactionId,
                    after.TransactionId,
                    StringComparison.Ordinal) &&
                String.Equals(
                    before.RecoveryAuthorityInvariantDigest,
                    after.RecoveryAuthorityInvariantDigest,
                    StringComparison.Ordinal) &&
                String.Equals(
                    before.NamespaceRoot.InvariantDigest,
                    after.NamespaceRoot.InvariantDigest,
                    StringComparison.Ordinal);
        }

        private static bool SameQuarantines(
            IList<PayloadQuarantineCheckpoint> first,
            IList<PayloadQuarantineCheckpoint> second)
        {
            if (first.Count != second.Count)
            {
                return false;
            }
            for (int index = 0; index < first.Count; ++index)
            {
                if (!String.Equals(
                        first[index].InvariantDigest,
                        second[index].InvariantDigest,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SamePurges(
            IList<PayloadPurgeCheckpoint> first,
            IList<PayloadPurgeCheckpoint> second)
        {
            if (first.Count != second.Count)
            {
                return false;
            }
            for (int index = 0; index < first.Count; ++index)
            {
                if (!String.Equals(
                        first[index].InvariantDigest,
                        second[index].InvariantDigest,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static void RequireActiveBindings(
            PayloadRecoveryAuthority authority,
            PayloadBuildWorkspaceCheckpoint checkpoint,
            TargetPayloadManifest manifest,
            TrustedReleasePayloadReceipt sourceReceipt)
        {
            if (checkpoint.ActiveBuild == null ||
                checkpoint.ActivePartialTree == null ||
                !String.Equals(
                    checkpoint.ActiveBuild.RecoveryAuthorityInvariantDigest,
                    authority.InvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    checkpoint.ActiveBuild.TargetManifestInvariantDigest,
                    manifest.InvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    checkpoint.ActiveBuild.SourceReceiptInvariantDigest,
                    sourceReceipt.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload active build is bound to another source.");
            }
        }

        private static PayloadCandidateBuildJournal CreateInitialJournal(
            PayloadRecoveryAuthority authority,
            PayloadBuildWorkspaceCheckpoint workspace,
            TargetPayloadManifest manifest,
            TrustedReleasePayloadReceipt sourceReceipt,
            string buildId)
        {
            var entries =
                new SortedDictionary<
                    string,
                    PayloadBuildEntryCheckpoint>(
                    StringComparer.Ordinal);
            foreach (TargetPayloadEntry file in manifest.Content)
            {
                int separator = file.RelativePath.LastIndexOf('\\');
                while (separator > 0)
                {
                    string directory =
                        file.RelativePath.Substring(0, separator);
                    if (!entries.ContainsKey(directory))
                    {
                        entries.Add(
                            directory,
                            new PayloadBuildEntryCheckpoint
                            {
                                RelativePath = directory,
                                IsDirectory = true,
                                ExpectedLength = 0,
                                ExpectedSha256 = String.Empty,
                                Phase = PayloadBuildEntryPhase.Pending,
                                FileId = String.Empty,
                                ObservedLength = -1,
                                ObservedSha256 = String.Empty
                            });
                    }
                    separator = directory.LastIndexOf('\\');
                }
                entries.Add(
                    file.RelativePath,
                    new PayloadBuildEntryCheckpoint
                    {
                        RelativePath = file.RelativePath,
                        IsDirectory = false,
                        ExpectedLength = file.Length,
                        ExpectedSha256 = file.Sha256,
                        Phase = PayloadBuildEntryPhase.Pending,
                        FileId = String.Empty,
                        ObservedLength = -1,
                        ObservedSha256 = String.Empty
                    });
            }
            int ordinal = 0;
            var canonical =
                new List<PayloadBuildEntryCheckpoint>();
            foreach (PayloadBuildEntryCheckpoint entry in entries.Values)
            {
                entry.Ordinal = ordinal++;
                canonical.Add(entry);
            }
            return new PayloadCandidateBuildJournal
            {
                SchemaVersion = 1,
                Revision = 0,
                BuildId = buildId,
                TransactionId = workspace.TransactionId,
                RecoveryAuthorityInvariantDigest =
                    authority.InvariantDigest,
                TargetManifestInvariantDigest =
                    manifest.InvariantDigest,
                SourceReceiptInvariantDigest =
                    sourceReceipt.InvariantDigest,
                NamespaceRootInvariantDigest =
                    workspace.NamespaceRoot.InvariantDigest,
                InitialCommittedRevision =
                    workspace.Committed.Revision,
                InitialCommittedInvariantDigest =
                    workspace.Committed.InvariantDigest,
                BuildLeafName = ".SBMS.build." + buildId,
                ActiveIntent = null,
                Entries = canonical,
                RootVolumeSerialNumber = 0,
                RootFileId = String.Empty
            };
        }
    }

    internal static class ProtectedPayloadBuildPlanner
    {
        internal static PayloadBuildMutationPlan Next(
            PayloadRecoveryAuthority authority,
            PayloadBuildWorkspaceState state,
            TargetPayloadManifest manifest,
            TrustedReleasePayloadReceipt sourceReceipt,
            string buildId,
            string intentId)
        {
            if (authority == null ||
                state == null ||
                manifest == null ||
                sourceReceipt == null)
            {
                throw new ArgumentNullException(
                    authority == null
                        ? "authority"
                        : (state == null
                            ? "state"
                            : (manifest == null
                                ? "manifest"
                                : "sourceReceipt")));
            }
            authority.Validate();
            manifest.Validate();
            state.Checkpoint.Validate();
            PayloadBuildWorkspaceCheckpoint checkpoint =
                state.Checkpoint;
            if (checkpoint.ActiveBuild == null)
            {
                return PayloadBuildMutationPlan.Begin(
                    authority,
                    state,
                    manifest,
                    sourceReceipt,
                    buildId);
            }
            if (checkpoint.ActiveBuild.ActiveIntent != null)
            {
                if (checkpoint.ActiveBuild.ActiveIntent.Kind ==
                    PayloadBuildStepKind.QuarantineBuild)
                {
                    return PayloadBuildMutationPlan.Complete(
                        authority,
                        state,
                        manifest,
                        sourceReceipt,
                        checkpoint.ActiveBuild.ActiveIntent.IntentId,
                        PayloadQuarantineReason.InterruptedBuild);
                }
                return PayloadBuildMutationPlan.Complete(
                    authority,
                    state,
                    manifest,
                    sourceReceipt,
                    String.Empty,
                    PayloadQuarantineReason.InterruptedBuild);
            }
            int ordinal;
            PayloadBuildStepKind step =
                SelectNextStep(checkpoint.ActiveBuild, out ordinal);
            return PayloadBuildMutationPlan.Publish(
                authority,
                state,
                manifest,
                sourceReceipt,
                step,
                ordinal,
                intentId,
                String.Empty,
                PayloadQuarantineReason.InterruptedBuild);
        }

        internal static PayloadBuildMutationPlan QuarantineNext(
            PayloadRecoveryAuthority authority,
            PayloadBuildWorkspaceState state,
            TargetPayloadManifest manifest,
            TrustedReleasePayloadReceipt sourceReceipt,
            string intentId,
            string quarantineId,
            PayloadQuarantineReason reason)
        {
            PayloadBuildWorkspaceCheckpoint checkpoint =
                state.Checkpoint;
            if (checkpoint.ActiveBuild == null)
            {
                throw new InvalidOperationException(
                    "No active payload build can be quarantined.");
            }
            if (checkpoint.ActiveBuild.ActiveIntent == null)
            {
                return PayloadBuildMutationPlan.Publish(
                    authority,
                    state,
                    manifest,
                    sourceReceipt,
                    PayloadBuildStepKind.QuarantineBuild,
                    -1,
                    intentId,
                    quarantineId,
                    reason);
            }
            if (checkpoint.ActiveBuild.ActiveIntent.Kind !=
                PayloadBuildStepKind.QuarantineBuild)
            {
                throw new InvalidOperationException(
                    "Another payload build intent must finish before quarantine.");
            }
            return PayloadBuildMutationPlan.Complete(
                authority,
                state,
                manifest,
                sourceReceipt,
                quarantineId,
                reason);
        }

        private static PayloadBuildStepKind SelectNextStep(
            PayloadCandidateBuildJournal journal,
            out int ordinal)
        {
            if (journal.RootVolumeSerialNumber == 0)
            {
                ordinal = -1;
                return PayloadBuildStepKind.CreateRoot;
            }
            foreach (PayloadBuildEntryCheckpoint entry in journal.Entries)
            {
                if (entry.Phase == PayloadBuildEntryPhase.Verified)
                {
                    continue;
                }
                ordinal = entry.Ordinal;
                switch (entry.Phase)
                {
                    case PayloadBuildEntryPhase.Pending:
                        return PayloadBuildStepKind.CreateEntry;
                    case PayloadBuildEntryPhase.Created:
                        return entry.IsDirectory
                            ? PayloadBuildStepKind.ReopenEntry
                            : PayloadBuildStepKind.RewriteFileExact;
                    case PayloadBuildEntryPhase.Written:
                        return PayloadBuildStepKind.FlushFile;
                    case PayloadBuildEntryPhase.Flushed:
                        return PayloadBuildStepKind.ReopenEntry;
                    case PayloadBuildEntryPhase.Reopened:
                        return PayloadBuildStepKind.VerifyEntryHash;
                    default:
                        throw new InvalidOperationException(
                            "Payload build entry phase cannot advance.");
                }
            }
            ordinal = -1;
            return PayloadBuildStepKind.SealCandidate;
        }
    }

    internal sealed class DeterministicProtectedPayloadBuildStateMachine :
        IDisposable
    {
        private readonly PayloadRecoveryAuthority authority;
        private readonly string authorityDigest;
        private IProtectedPayloadBuildWorkspaceModel workspace;
        private bool disposed;

        internal DeterministicProtectedPayloadBuildStateMachine(
            PayloadRecoveryAuthority boundAuthority,
            IProtectedPayloadBuildWorkspaceModel workspaceModel)
        {
            if (boundAuthority == null || workspaceModel == null)
            {
                throw new ArgumentNullException(
                    boundAuthority == null
                        ? "boundAuthority"
                        : "workspaceModel");
            }
            boundAuthority.Validate();
            authority = boundAuthority.DeepClone();
            authorityDigest = boundAuthority.InvariantDigest;
            workspace = workspaceModel;
            RequireOwned(CheckedInspect());
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (workspace != null)
            {
                workspace.Dispose();
                workspace = null;
            }
        }

        internal PayloadBuildWorkspaceState Inspect()
        {
            ThrowIfDisposed();
            return CheckedInspect();
        }

        internal PayloadBuildAdvanceResult Advance(
            ITrustedReleasePayloadSource source,
            TargetPayloadManifest expected,
            string buildId,
            string intentId)
        {
            ThrowIfDisposed();
            ValidateSource(source, expected);
            PayloadBuildWorkspaceState before = CheckedInspect();
            if (HasExpectedCandidate(before, expected))
            {
                return new PayloadBuildAdvanceResult(
                    PayloadBuildAdvanceKind.CandidateAlreadyPresent,
                    before,
                    null,
                    null,
                    null);
            }
            if (HasExpectedQuarantine(
                    before,
                    buildId,
                    expected,
                    source.Receipt))
            {
                return new PayloadBuildAdvanceResult(
                    PayloadBuildAdvanceKind.QuarantineAlreadyPresent,
                    before,
                    null,
                    null,
                    null);
            }
            PayloadBuildMutationPlan plan =
                ProtectedPayloadBuildPlanner.Next(
                    authority,
                    before,
                    expected,
                    source.Receipt,
                    buildId,
                    intentId);
            return Apply(plan, source, expected);
        }

        internal PayloadBuildAdvanceResult Quarantine(
            ITrustedReleasePayloadSource source,
            TargetPayloadManifest expected,
            string intentId,
            string quarantineId,
            PayloadQuarantineReason reason)
        {
            ThrowIfDisposed();
            ValidateSource(source, expected);
            PayloadBuildWorkspaceState before = CheckedInspect();
            PayloadBuildMutationPlan plan =
                ProtectedPayloadBuildPlanner.QuarantineNext(
                    authority,
                    before,
                    expected,
                    source.Receipt,
                    intentId,
                    quarantineId,
                    reason);
            return Apply(plan, source, expected);
        }

        internal PayloadPurgeAdvanceResult AdvancePurge(
            string quarantineId,
            string purgeId)
        {
            ThrowIfDisposed();
            PayloadBuildWorkspaceState before = CheckedInspect();
            if (HasCompletedPurge(before, quarantineId, purgeId))
            {
                return new PayloadPurgeAdvanceResult(
                    PayloadPurgeAdvanceKind.AlreadyComplete,
                    before,
                    null);
            }
            PayloadPurgeMutationPlan plan =
                PayloadPurgeMutationPlan.Next(
                    authority,
                    before,
                    quarantineId,
                    purgeId);
            PayloadPurgeMutationOutcome outcome =
                workspace.ApplyPurgeExact(plan);
            if (outcome == null || outcome.State == null)
            {
                throw new InvalidOperationException(
                    "Payload purge backend returned no mutation outcome.");
            }
            RequireOwned(outcome.State);
            if (outcome.Disposition ==
                PayloadBuildMutationDisposition.RetryableNotApplied)
            {
                PayloadBuildWorkspaceState observedRetry =
                    CheckedInspect();
                if (!String.Equals(
                        before.InvariantDigest,
                        observedRetry.InvariantDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Retryable payload purge changed durable state.");
                }
                return new PayloadPurgeAdvanceResult(
                    PayloadPurgeAdvanceKind.Retryable,
                    observedRetry,
                    null);
            }

            PayloadPurgeReceipt receipt =
                plan.ValidateApplied(
                    outcome.State,
                    outcome.AbsenceObservation);
            outcome.CasReceipt.Require(
                plan.ExpectedCas,
                outcome.State.CasToken);
            PayloadBuildWorkspaceState observed = CheckedInspect();
            if (!String.Equals(
                    outcome.State.InvariantDigest,
                    observed.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload purge backend returned a state it did not publish.");
            }
            PayloadPurgeAdvanceKind resultKind =
                plan.Kind == PayloadPurgeTransitionKind.Arm
                    ? PayloadPurgeAdvanceKind.Armed
                    : (plan.Kind ==
                            PayloadPurgeTransitionKind.ObserveAbsent
                        ? PayloadPurgeAdvanceKind.ObservedAbsent
                        : PayloadPurgeAdvanceKind.Complete);
            return new PayloadPurgeAdvanceResult(
                resultKind,
                observed,
                receipt);
        }

        private PayloadBuildAdvanceResult Apply(
            PayloadBuildMutationPlan plan,
            ITrustedReleasePayloadSource source,
            TargetPayloadManifest expected)
        {
            PayloadBuildMutationOutcome outcome =
                workspace.ApplyExact(plan, source);
            if (outcome == null || outcome.State == null)
            {
                throw new InvalidOperationException(
                    "Payload build backend returned no mutation outcome.");
            }
            RequireOwned(outcome.State);
            if (outcome.Disposition ==
                PayloadBuildMutationDisposition.RetryableNotApplied)
            {
                PayloadBuildWorkspaceState observedRetry =
                    CheckedInspect();
                if (!String.Equals(
                        plan.Before.InvariantDigest,
                        observedRetry.InvariantDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Retryable payload build mutation changed durable state.");
                }
                return new PayloadBuildAdvanceResult(
                    PayloadBuildAdvanceKind.InProgress,
                    observedRetry,
                    plan,
                    null,
                    null);
            }
            plan.ValidateApplied(outcome.State);
            outcome.CasReceipt.Require(
                plan.ExpectedCas,
                outcome.State.CasToken);

            PayloadBuildWorkspaceState observed = CheckedInspect();
            if (!String.Equals(
                    outcome.State.InvariantDigest,
                    observed.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload build backend returned a state it did not publish.");
            }

            if (plan.StepKind ==
                    PayloadBuildStepKind.SealCandidate &&
                observed.Checkpoint.ActiveBuild == null)
            {
                var receipt = new PayloadCandidateReceipt(
                    authority,
                    expected,
                    new PayloadNamespaceState(
                        plan.Before.Checkpoint.Committed),
                    new PayloadNamespaceState(
                        observed.Checkpoint.Committed));
                return new PayloadBuildAdvanceResult(
                    PayloadBuildAdvanceKind.CandidatePublished,
                    observed,
                    plan,
                    receipt,
                    null);
            }
            if (plan.StepKind ==
                    PayloadBuildStepKind.QuarantineBuild &&
                observed.Checkpoint.ActiveBuild == null)
            {
                var receipt = new PayloadQuarantineReceipt(
                    authority,
                    plan.Before,
                    observed,
                    plan.QuarantineId);
                return new PayloadBuildAdvanceResult(
                    PayloadBuildAdvanceKind.Quarantined,
                    observed,
                    plan,
                    null,
                    receipt);
            }
            return new PayloadBuildAdvanceResult(
                PayloadBuildAdvanceKind.InProgress,
                observed,
                plan,
                null,
                null);
        }

        private static void ValidateSource(
            ITrustedReleasePayloadSource source,
            TargetPayloadManifest expected)
        {
            if (source == null || expected == null)
            {
                throw new ArgumentNullException(
                    source == null ? "source" : "expected");
            }
            expected.Validate();
            TrustedReleasePayloadReceipt receipt = source.Receipt;
            if (receipt == null ||
                !String.Equals(
                    receipt.InvariantDigest,
                    expected.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload build source does not match the expected manifest.");
            }
        }

        private bool HasExpectedCandidate(
            PayloadBuildWorkspaceState state,
            TargetPayloadManifest expected)
        {
            PayloadBuildWorkspaceCheckpoint checkpoint =
                state.Checkpoint;
            PayloadDirectoryCheckpoint candidate =
                checkpoint.Committed.Candidate;
            if (checkpoint.ActiveBuild != null || candidate == null)
            {
                return false;
            }
            if (authority.Target == null ||
                candidate.Slot != PayloadDirectorySlot.Candidate ||
                !String.Equals(
                    candidate.TransactionId,
                    authority.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    candidate.ManifestInvariantDigest,
                    expected.InvariantDigest,
                    StringComparison.Ordinal) ||
                !authority.Target.Matches(candidate))
            {
                throw new InvalidOperationException(
                    "Payload workspace already contains another candidate.");
            }
            return true;
        }

        private static bool HasExpectedQuarantine(
            PayloadBuildWorkspaceState state,
            string buildId,
            TargetPayloadManifest expected,
            TrustedReleasePayloadReceipt sourceReceipt)
        {
            PayloadContractValidation.RequireCanonicalTransactionId(
                buildId,
                "Payload build ID");
            PayloadBuildWorkspaceCheckpoint checkpoint =
                state.Checkpoint;
            if (checkpoint.ActiveBuild != null)
            {
                return false;
            }
            foreach (PayloadQuarantineCheckpoint quarantine in
                checkpoint.Quarantines)
            {
                if (quarantine.SourceKind ==
                        PayloadQuarantineSourceKind.PartialBuild &&
                    String.Equals(
                        quarantine.SourceBuildId,
                        buildId,
                        StringComparison.Ordinal))
                {
                    if (!String.Equals(
                            quarantine.TargetManifestInvariantDigest,
                            expected.InvariantDigest,
                            StringComparison.Ordinal) ||
                        !String.Equals(
                            quarantine.SourceReceiptInvariantDigest,
                            sourceReceipt.InvariantDigest,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Payload build ID is quarantined for different content.");
                    }
                    return true;
                }
            }
            return false;
        }

        private static bool HasCompletedPurge(
            PayloadBuildWorkspaceState state,
            string quarantineId,
            string purgeId)
        {
            PayloadContractValidation.RequireCanonicalTransactionId(
                quarantineId,
                "Payload purge quarantine ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                purgeId,
                "Payload purge ID");
            foreach (PayloadCompletedPurgeCheckpoint completed in
                state.Checkpoint.CompletedPurges)
            {
                if (!String.Equals(
                        completed.PurgeId,
                        purgeId,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (!String.Equals(
                        completed.QuarantineId,
                        quarantineId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Completed payload purge ID belongs to another quarantine.");
                }
                return true;
            }
            return false;
        }

        private PayloadBuildWorkspaceState CheckedInspect()
        {
            PayloadBuildWorkspaceState state = workspace.Inspect();
            if (state == null)
            {
                throw new InvalidOperationException(
                    "Payload build backend returned no inspection state.");
            }
            RequireOwned(state);
            return state;
        }

        private void RequireOwned(PayloadBuildWorkspaceState state)
        {
            PayloadBuildWorkspaceCheckpoint checkpoint =
                state.Checkpoint;
            if (!String.Equals(
                    checkpoint.RecoveryAuthorityInvariantDigest,
                    authorityDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload build workspace belongs to another authority.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    "DeterministicProtectedPayloadBuildStateMachine");
            }
        }
    }
}
