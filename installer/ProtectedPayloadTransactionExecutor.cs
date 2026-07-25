using System;

namespace SBMSSetup
{
    internal sealed class PayloadCandidateStagePlan
    {
        private readonly PayloadRecoveryAuthority authority;
        private readonly TargetPayloadManifest expected;

        internal PayloadCandidateStagePlan(
            PayloadRecoveryAuthority authority,
            PayloadNamespaceState observed,
            TargetPayloadManifest expected,
            bool isTerminal)
        {
            if (authority == null || observed == null || expected == null)
            {
                throw new ArgumentNullException(
                    authority == null
                        ? "authority"
                        : (observed == null ? "observed" : "expected"));
            }
            authority.Validate();
            expected.Validate();
            this.authority = authority.DeepClone();
            Observed = observed;
            this.expected = expected.DeepClone();
            IsTerminal = isTerminal;
        }

        internal readonly PayloadNamespaceState Observed;
        internal readonly bool IsTerminal;

        internal PayloadRecoveryAuthority Authority
        {
            get { return authority.DeepClone(); }
        }

        internal TargetPayloadManifest Expected
        {
            get { return expected.DeepClone(); }
        }

        internal void ValidateApplied(PayloadNamespaceState after)
        {
            if (IsTerminal || after == null ||
                after.Revision != checked(Observed.Revision + 1))
            {
                throw new InvalidOperationException(
                    "Payload candidate stage result is incomplete.");
            }
            new PayloadCandidateReceipt(
                authority,
                expected,
                Observed,
                after);
        }
    }

    internal static class ProtectedPayloadStagePlanner
    {
        internal static PayloadCandidateStagePlan Next(
            PayloadRecoveryAuthority authority,
            PayloadNamespaceState observed,
            TargetPayloadManifest expected,
            TrustedReleasePayloadReceipt sourceReceipt)
        {
            if (authority == null ||
                observed == null ||
                expected == null ||
                sourceReceipt == null)
            {
                throw new ArgumentNullException(
                    authority == null
                        ? "authority"
                        : (observed == null
                            ? "observed"
                            : (expected == null
                                ? "expected"
                                : "sourceReceipt")));
            }
            authority.Validate();
            expected.Validate();
            if (authority.Operation == InstallOperation.Uninstall ||
                authority.Target == null ||
                !String.Equals(
                    authority.TransactionId,
                    observed.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    authority.TransactionId,
                    expected.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    expected.InvariantDigest,
                    sourceReceipt.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw Invalid("authority, namespace, and trusted source");
            }
            RequireManifestAuthority(authority.Target, expected);

            PayloadNamespaceCheckpoint state = observed.Checkpoint;
            if (authority.Operation == InstallOperation.FreshInstall)
            {
                if (state.Shape == PayloadNamespaceShape.Empty)
                {
                    return new PayloadCandidateStagePlan(
                        authority,
                        observed,
                        expected,
                        false);
                }
                if (state.Shape == PayloadNamespaceShape.CandidateOnly &&
                    authority.Target.Matches(state.Candidate))
                {
                    return new PayloadCandidateStagePlan(
                        authority,
                        observed,
                        expected,
                        true);
                }
                throw Invalid("fresh-install stage shape");
            }

            if (!IsReplacement(authority.Operation) ||
                authority.Baseline == null)
            {
                throw Invalid("replacement stage operation");
            }
            if (state.Shape == PayloadNamespaceShape.CurrentOnly &&
                authority.Baseline.Matches(state.Current))
            {
                return new PayloadCandidateStagePlan(
                    authority,
                    observed,
                    expected,
                    false);
            }
            if (state.Shape ==
                    PayloadNamespaceShape.CurrentAndCandidate &&
                authority.Baseline.Matches(state.Current) &&
                authority.Target.Matches(state.Candidate))
            {
                return new PayloadCandidateStagePlan(
                    authority,
                    observed,
                    expected,
                    true);
            }
            throw Invalid("replacement stage shape");
        }

        private static void RequireManifestAuthority(
            PayloadContentAuthority authority,
            TargetPayloadManifest expected)
        {
            long totalBytes = 0;
            foreach (TargetPayloadEntry entry in expected.Content)
            {
                totalBytes = checked(totalBytes + entry.Length);
            }
            if (!String.Equals(
                    authority.Release.Version,
                    expected.Target.Version,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    authority.Release.PackageFingerprint,
                    expected.Target.PackageFingerprint,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    authority.ContentSetSha256,
                    expected.ContentSetSha256,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    authority.ManifestInvariantDigest,
                    expected.InvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    authority.SemanticTreeSha256,
                    expected.ComputeExpectedSemanticTreeSha256(),
                    StringComparison.Ordinal) ||
                authority.FileCount != expected.Content.Count ||
                authority.TotalBytes != totalBytes)
            {
                throw Invalid("target manifest authority");
            }
        }

        private static bool IsReplacement(InstallOperation operation)
        {
            return operation == InstallOperation.Upgrade ||
                operation == InstallOperation.Repair ||
                operation == InstallOperation.ExplicitDowngrade;
        }

        private static InvalidOperationException Invalid(string context)
        {
            return new InvalidOperationException(
                "Payload stage planner rejected " + context + ".");
        }
    }

    internal enum CommittedPayloadMutationDisposition
    {
        Applied,
        RetryableNotApplied
    }

    internal sealed class CommittedPayloadMutationOutcome
    {
        internal CommittedPayloadMutationOutcome(
            CommittedPayloadMutationDisposition disposition,
            PayloadNamespaceMutationPlan plan,
            PayloadNamespaceState state)
        {
            if (!Enum.IsDefined(
                    typeof(CommittedPayloadMutationDisposition),
                    disposition) ||
                plan == null ||
                state == null)
            {
                throw new InvalidOperationException(
                    "Committed payload mutation outcome is incomplete.");
            }
            if (disposition ==
                CommittedPayloadMutationDisposition.Applied)
            {
                plan.ValidateApplied(state);
            }
            else if (!String.Equals(
                plan.Observed.InvariantDigest,
                state.InvariantDigest,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Retryable payload mutation changed committed state.");
            }
            Disposition = disposition;
            State = state;
        }

        internal readonly CommittedPayloadMutationDisposition Disposition;
        internal readonly PayloadNamespaceState State;
    }

    // A committed-view model seam only. PublishSealedCandidate represents the
    // point after a candidate has already been copied, flushed, reopened,
    // verified, and sealed. ApplyCommitted represents one logical namespace
    // mutation. No production implementation exists; partial builds and
    // quarantine maintenance require separate durable contracts.
    internal interface ICommittedPayloadNamespaceModel : IDisposable
    {
        PayloadNamespaceState Inspect();
        PayloadNamespaceState PublishSealedCandidate(
            PayloadCandidateStagePlan plan,
            ITrustedReleasePayloadSource source);
        CommittedPayloadMutationOutcome ApplyCommitted(
            PayloadNamespaceMutationPlan plan);
        bool HasPendingMaintenance { get; }
    }

    internal sealed class DeterministicProtectedPayloadStoreCoordinator :
        IProtectedPayloadStore
    {
        private readonly PayloadRecoveryAuthority authority;
        private readonly string authorityDigest;
        private ICommittedPayloadNamespaceModel payloadNamespace;
        private bool disposed;

        internal DeterministicProtectedPayloadStoreCoordinator(
            PayloadRecoveryAuthority boundAuthority,
            ICommittedPayloadNamespaceModel payloadNamespace)
        {
            if (boundAuthority == null || payloadNamespace == null)
            {
                throw new ArgumentNullException(
                    boundAuthority == null
                        ? "boundAuthority"
                        : "payloadNamespace");
            }
            boundAuthority.Validate();
            authority = boundAuthority.DeepClone();
            authorityDigest = boundAuthority.InvariantDigest;
            this.payloadNamespace = payloadNamespace;
            RequireOwned(CheckedInspect());
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (payloadNamespace != null)
            {
                payloadNamespace.Dispose();
                payloadNamespace = null;
            }
        }

        public PayloadNamespaceState Inspect()
        {
            ThrowIfDisposed();
            return CheckedInspect();
        }

        public PayloadCandidateReceipt Stage(
            ITrustedReleasePayloadSource source,
            TargetPayloadManifest expected)
        {
            ThrowIfDisposed();
            if (source == null || expected == null)
            {
                throw new ArgumentNullException(
                    source == null ? "source" : "expected");
            }
            PayloadNamespaceState before = CheckedInspect();
            PayloadCandidateStagePlan plan =
                ProtectedPayloadStagePlanner.Next(
                    authority,
                    before,
                    expected,
                    source.Receipt);
            PayloadNamespaceState after = before;
            if (!plan.IsTerminal)
            {
                PayloadNamespaceState published =
                    payloadNamespace.PublishSealedCandidate(plan, source);
                plan.ValidateApplied(published);
                after = RequirePublished(published);
            }
            return new PayloadCandidateReceipt(
                authority,
                expected,
                before,
                after);
        }

        public PayloadPromotionReceipt PromoteInstall(
            PayloadRecoveryAuthority suppliedAuthority,
            PayloadNamespaceState expected,
            PayloadCandidateReceipt candidate)
        {
            ThrowIfDisposed();
            RequireAuthority(suppliedAuthority);
            if (authority.Operation == InstallOperation.Uninstall ||
                candidate == null)
            {
                throw new InvalidOperationException(
                    "Install promotion is not authorized.");
            }
            RequireAuthority(candidate.Authority);
            PayloadNamespaceState before = RequireExpected(expected);
            if (!String.Equals(
                    candidate.State.InvariantDigest,
                    before.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Candidate receipt is stale or belongs to another namespace.");
            }
            PayloadNamespaceState after = ExecuteRecovery(
                PayloadRecoveryDecision.CompleteForward,
                before);
            return new PayloadPromotionReceipt(
                authority,
                before,
                after);
        }

        public PayloadPromotionReceipt PromoteUninstall(
            PayloadRecoveryAuthority suppliedAuthority,
            PayloadNamespaceState expected)
        {
            ThrowIfDisposed();
            RequireAuthority(suppliedAuthority);
            if (authority.Operation != InstallOperation.Uninstall)
            {
                throw new InvalidOperationException(
                    "Uninstall promotion is not authorized.");
            }
            PayloadNamespaceState before = RequireExpected(expected);
            PayloadNamespaceState after = ExecuteRecovery(
                PayloadRecoveryDecision.CompleteForward,
                before);
            return new PayloadPromotionReceipt(
                authority,
                before,
                after);
        }

        public PayloadRecoveryReceipt Recover(
            PayloadRecoveryDecision decision,
            PayloadNamespaceState expected)
        {
            ThrowIfDisposed();
            PayloadNamespaceState before = RequireExpected(expected);
            PayloadNamespaceState after =
                ExecuteRecovery(decision, before);
            return new PayloadRecoveryReceipt(
                decision,
                authority,
                before,
                after);
        }

        public PayloadCleanupReceipt Cleanup(
            PayloadCleanupKind kind,
            PayloadNamespaceState expected)
        {
            ThrowIfDisposed();
            PayloadNamespaceState before = RequireExpected(expected);
            PayloadNamespaceState state = before;
            for (int step = 0; step < 2; ++step)
            {
                PayloadNamespaceMutationPlan plan =
                    ProtectedPayloadRecoveryPlanner.NextCleanup(
                        authority,
                        kind,
                        state);
                if (plan.IsTerminal)
                {
                    if (payloadNamespace.HasPendingMaintenance)
                    {
                        throw new InvalidOperationException(
                            "Payload cleanup cannot report complete while maintenance remains pending.");
                    }
                    return new PayloadCleanupReceipt(
                        authority,
                        kind,
                        before,
                        state,
                        true);
                }
                CommittedPayloadMutationOutcome outcome =
                    ApplyExact(plan);
                if (outcome.Disposition ==
                    CommittedPayloadMutationDisposition.RetryableNotApplied)
                {
                    PayloadNamespaceState unchanged =
                        RequirePublished(outcome.State);
                    return new PayloadCleanupReceipt(
                        authority,
                        kind,
                        before,
                        unchanged,
                        false);
                }
                state = RequirePublished(outcome.State);
            }
            throw new InvalidOperationException(
                "Payload cleanup exceeded its bounded transition count.");
        }

        private PayloadNamespaceState ExecuteRecovery(
            PayloadRecoveryDecision decision,
            PayloadNamespaceState initial)
        {
            PayloadNamespaceState state = initial;
            for (int step = 0; step < 4; ++step)
            {
                PayloadNamespaceMutationPlan plan =
                    ProtectedPayloadRecoveryPlanner.NextRecovery(
                        authority,
                        decision,
                        state);
                if (plan.IsTerminal)
                {
                    return state;
                }
                CommittedPayloadMutationOutcome outcome =
                    ApplyExact(plan);
                PayloadNamespaceState published =
                    RequirePublished(outcome.State);
                if (outcome.Disposition !=
                    CommittedPayloadMutationDisposition.Applied)
                {
                    throw new InvalidOperationException(
                        "Payload recovery mutation was not committed.");
                }
                state = published;
            }
            throw new InvalidOperationException(
                "Payload recovery exceeded its bounded transition count.");
        }

        private PayloadNamespaceState RequireExpected(
            PayloadNamespaceState expected)
        {
            if (expected == null)
            {
                throw new ArgumentNullException("expected");
            }
            RequireOwned(expected);
            PayloadNamespaceState observed = CheckedInspect();
            if (!String.Equals(
                expected.InvariantDigest,
                observed.InvariantDigest,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload namespace expected state is stale.");
            }
            return observed;
        }

        private CommittedPayloadMutationOutcome ApplyExact(
            PayloadNamespaceMutationPlan plan)
        {
            CommittedPayloadMutationOutcome outcome =
                payloadNamespace.ApplyCommitted(plan);
            if (outcome == null)
            {
                throw new InvalidOperationException(
                    "Payload namespace model returned no mutation outcome.");
            }
            return outcome;
        }

        private PayloadNamespaceState RequirePublished(
            PayloadNamespaceState published)
        {
            if (published == null)
            {
                throw new InvalidOperationException(
                    "Payload namespace backend returned no committed state.");
            }
            RequireOwned(published);
            PayloadNamespaceState observed = CheckedInspect();
            if (!String.Equals(
                    published.InvariantDigest,
                    observed.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload namespace backend did not publish an exact forward state.");
            }
            return observed;
        }

        private PayloadNamespaceState CheckedInspect()
        {
            PayloadNamespaceState state = payloadNamespace.Inspect();
            if (state == null)
            {
                throw new InvalidOperationException(
                    "Payload namespace backend returned no inspection state.");
            }
            RequireOwned(state);
            return state;
        }

        private void RequireOwned(PayloadNamespaceState state)
        {
            if (state == null ||
                !String.Equals(
                    authority.TransactionId,
                    state.TransactionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload namespace crosses the bound transaction.");
            }
        }

        private void RequireAuthority(
            PayloadRecoveryAuthority supplied)
        {
            if (supplied == null ||
                !String.Equals(
                    authorityDigest,
                    supplied.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload store authority does not match its binding.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    "DeterministicProtectedPayloadStoreCoordinator");
            }
        }
    }
}
