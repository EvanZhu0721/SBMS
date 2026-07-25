using System;

namespace SBMSSetup
{
    internal enum PayloadNamespaceMutationKind
    {
        None,
        RenameCurrentToBackup,
        RenameCandidateToCurrent,
        RenameBackupToCurrent,
        DeleteCurrent,
        DeleteCandidate,
        DeleteBackup
    }

    internal sealed class PayloadNamespaceMutationPlan
    {
        private readonly PayloadDirectoryCheckpoint exactSource;

        internal PayloadNamespaceMutationPlan(
            PayloadNamespaceState observed,
            PayloadNamespaceMutationKind kind,
            PayloadDirectoryCheckpoint source)
        {
            if (observed == null ||
                !Enum.IsDefined(typeof(PayloadNamespaceMutationKind), kind))
            {
                throw new InvalidOperationException(
                    "Payload namespace mutation plan is incomplete.");
            }
            if ((kind == PayloadNamespaceMutationKind.None) !=
                (source == null))
            {
                throw new InvalidOperationException(
                    "Payload namespace mutation source disagrees with the action.");
            }
            Observed = observed;
            Kind = kind;
            exactSource = source == null ? null : source.DeepClone();
        }

        internal readonly PayloadNamespaceState Observed;
        internal readonly PayloadNamespaceMutationKind Kind;

        internal bool IsTerminal
        {
            get { return Kind == PayloadNamespaceMutationKind.None; }
        }

        internal PayloadDirectoryCheckpoint ExactSource
        {
            get
            {
                return exactSource == null
                    ? null
                    : exactSource.DeepClone();
            }
        }
    }

    internal static class ProtectedPayloadRecoveryPlanner
    {
        internal static PayloadNamespaceMutationPlan NextRecovery(
            PayloadRecoveryAuthority authority,
            PayloadRecoveryDecision decision,
            PayloadNamespaceState observed)
        {
            RequireInputs(authority, observed);
            if (!Enum.IsDefined(typeof(PayloadRecoveryDecision), decision))
            {
                throw new InvalidOperationException(
                    "Payload recovery decision is invalid.");
            }

            PayloadNamespaceCheckpoint state = observed.Checkpoint;
            if (decision == PayloadRecoveryDecision.CompleteForward)
            {
                return NextCompleteForward(authority, observed, state);
            }
            return NextRestoreBaseline(authority, observed, state);
        }

        internal static PayloadNamespaceMutationPlan NextCleanup(
            PayloadRecoveryAuthority authority,
            PayloadCleanupKind kind,
            PayloadNamespaceState observed)
        {
            RequireInputs(authority, observed);
            if (!Enum.IsDefined(typeof(PayloadCleanupKind), kind))
            {
                throw new InvalidOperationException(
                    "Payload cleanup kind is invalid.");
            }

            PayloadNamespaceCheckpoint state = observed.Checkpoint;
            if (kind == PayloadCleanupKind.Candidate)
            {
                if (state.Candidate != null)
                {
                    bool authorizedShape =
                        (authority.Operation ==
                            InstallOperation.FreshInstall &&
                         state.Shape ==
                            PayloadNamespaceShape.CandidateOnly) ||
                        (IsReplacement(authority.Operation) &&
                         (state.Shape ==
                            PayloadNamespaceShape.CurrentAndCandidate ||
                          state.Shape ==
                            PayloadNamespaceShape.CandidateAndBackup));
                    if (!authorizedShape)
                    {
                        throw Invalid("candidate cleanup shape");
                    }
                    RequireTarget(authority, state.Candidate);
                    if (state.Shape ==
                        PayloadNamespaceShape.CurrentAndCandidate)
                    {
                        RequireBaseline(authority, state.Current);
                    }
                    else if (state.Shape ==
                        PayloadNamespaceShape.CandidateAndBackup)
                    {
                        RequireBaseline(authority, state.Backup);
                    }
                    return Action(
                        observed,
                        PayloadNamespaceMutationKind.DeleteCandidate,
                        state.Candidate);
                }
                RequireCandidateCleanupTerminal(authority, state);
                return Terminal(observed);
            }

            if (state.Backup != null)
            {
                bool authorizedShape =
                    (IsReplacement(authority.Operation) &&
                     state.Shape ==
                        PayloadNamespaceShape.CurrentAndBackup) ||
                    (authority.Operation == InstallOperation.Uninstall &&
                     state.Shape == PayloadNamespaceShape.BackupOnly);
                if (!authorizedShape)
                {
                    throw Invalid("committed-backup cleanup shape");
                }
                RequireBaseline(authority, state.Backup);
                if (state.Shape ==
                    PayloadNamespaceShape.CurrentAndBackup)
                {
                    RequireTarget(authority, state.Current);
                }
                return Action(
                    observed,
                    PayloadNamespaceMutationKind.DeleteBackup,
                    state.Backup);
            }
            RequireBackupCleanupTerminal(authority, state);
            return Terminal(observed);
        }

        private static PayloadNamespaceMutationPlan NextCompleteForward(
            PayloadRecoveryAuthority authority,
            PayloadNamespaceState observed,
            PayloadNamespaceCheckpoint state)
        {
            if (authority.Operation == InstallOperation.FreshInstall)
            {
                if (state.Shape == PayloadNamespaceShape.CandidateOnly)
                {
                    RequireTarget(authority, state.Candidate);
                    return Action(
                        observed,
                        PayloadNamespaceMutationKind.RenameCandidateToCurrent,
                        state.Candidate);
                }
                if (state.Shape == PayloadNamespaceShape.CurrentOnly)
                {
                    RequireTarget(authority, state.Current);
                    return Terminal(observed);
                }
                return Reject("fresh-install forward recovery");
            }

            if (authority.Operation == InstallOperation.Uninstall)
            {
                if (state.Shape == PayloadNamespaceShape.CurrentOnly)
                {
                    RequireBaseline(authority, state.Current);
                    return Action(
                        observed,
                        PayloadNamespaceMutationKind.RenameCurrentToBackup,
                        state.Current);
                }
                if (state.Shape == PayloadNamespaceShape.BackupOnly)
                {
                    RequireBaseline(authority, state.Backup);
                    return Terminal(observed);
                }
                return Reject("uninstall forward recovery");
            }

            RequireReplacement(authority);
            if (state.Shape == PayloadNamespaceShape.CurrentAndCandidate)
            {
                RequireBaseline(authority, state.Current);
                RequireTarget(authority, state.Candidate);
                return Action(
                    observed,
                    PayloadNamespaceMutationKind.RenameCurrentToBackup,
                    state.Current);
            }
            if (state.Shape == PayloadNamespaceShape.CandidateAndBackup)
            {
                RequireTarget(authority, state.Candidate);
                RequireBaseline(authority, state.Backup);
                return Action(
                    observed,
                    PayloadNamespaceMutationKind.RenameCandidateToCurrent,
                    state.Candidate);
            }
            if (state.Shape == PayloadNamespaceShape.CurrentAndBackup)
            {
                RequireTarget(authority, state.Current);
                RequireBaseline(authority, state.Backup);
                return Terminal(observed);
            }
            return Reject("replacement forward recovery");
        }

        private static PayloadNamespaceMutationPlan NextRestoreBaseline(
            PayloadRecoveryAuthority authority,
            PayloadNamespaceState observed,
            PayloadNamespaceCheckpoint state)
        {
            if (authority.Operation == InstallOperation.FreshInstall)
            {
                if (state.Shape == PayloadNamespaceShape.CandidateOnly)
                {
                    RequireTarget(authority, state.Candidate);
                    return Action(
                        observed,
                        PayloadNamespaceMutationKind.DeleteCandidate,
                        state.Candidate);
                }
                if (state.Shape == PayloadNamespaceShape.CurrentOnly)
                {
                    RequireTarget(authority, state.Current);
                    return Action(
                        observed,
                        PayloadNamespaceMutationKind.DeleteCurrent,
                        state.Current);
                }
                if (state.Shape == PayloadNamespaceShape.Empty)
                {
                    return Terminal(observed);
                }
                return Reject("fresh-install baseline restore");
            }

            if (state.Shape == PayloadNamespaceShape.CurrentOnly)
            {
                RequireBaseline(authority, state.Current);
                return Terminal(observed);
            }
            if (state.Shape == PayloadNamespaceShape.BackupOnly)
            {
                RequireBaseline(authority, state.Backup);
                return Action(
                    observed,
                    PayloadNamespaceMutationKind.RenameBackupToCurrent,
                    state.Backup);
            }

            if (authority.Operation == InstallOperation.Uninstall)
            {
                return Reject("uninstall baseline restore");
            }

            RequireReplacement(authority);
            if (state.Shape == PayloadNamespaceShape.CurrentAndCandidate)
            {
                RequireBaseline(authority, state.Current);
                RequireTarget(authority, state.Candidate);
                return Action(
                    observed,
                    PayloadNamespaceMutationKind.DeleteCandidate,
                    state.Candidate);
            }
            if (state.Shape == PayloadNamespaceShape.CandidateAndBackup)
            {
                RequireTarget(authority, state.Candidate);
                RequireBaseline(authority, state.Backup);
                return Action(
                    observed,
                    PayloadNamespaceMutationKind.DeleteCandidate,
                    state.Candidate);
            }
            if (state.Shape == PayloadNamespaceShape.CurrentAndBackup)
            {
                RequireTarget(authority, state.Current);
                RequireBaseline(authority, state.Backup);
                return Action(
                    observed,
                    PayloadNamespaceMutationKind.DeleteCurrent,
                    state.Current);
            }
            return Reject("replacement baseline restore");
        }

        private static void RequireCandidateCleanupTerminal(
            PayloadRecoveryAuthority authority,
            PayloadNamespaceCheckpoint state)
        {
            if (authority.Operation == InstallOperation.FreshInstall &&
                state.Shape == PayloadNamespaceShape.Empty)
            {
                return;
            }
            if (IsReplacement(authority.Operation) &&
                state.Shape == PayloadNamespaceShape.CurrentOnly &&
                (Matches(authority.Baseline, state.Current) ||
                 Matches(authority.Target, state.Current)))
            {
                return;
            }
            if (IsReplacement(authority.Operation) &&
                state.Shape == PayloadNamespaceShape.BackupOnly)
            {
                RequireBaseline(authority, state.Backup);
                return;
            }
            if (IsReplacement(authority.Operation) &&
                state.Shape == PayloadNamespaceShape.CurrentAndBackup)
            {
                RequireTarget(authority, state.Current);
                RequireBaseline(authority, state.Backup);
                return;
            }
            if (authority.Operation == InstallOperation.Uninstall &&
                state.Shape == PayloadNamespaceShape.CurrentOnly)
            {
                RequireBaseline(authority, state.Current);
                return;
            }
            throw Invalid("candidate cleanup terminal");
        }

        private static void RequireBackupCleanupTerminal(
            PayloadRecoveryAuthority authority,
            PayloadNamespaceCheckpoint state)
        {
            if (state.Shape == PayloadNamespaceShape.Empty &&
                authority.Operation == InstallOperation.Uninstall)
            {
                return;
            }
            if (IsReplacement(authority.Operation) &&
                state.Shape == PayloadNamespaceShape.CurrentOnly)
            {
                RequireTarget(authority, state.Current);
                return;
            }
            throw Invalid("committed-backup cleanup terminal");
        }

        private static void RequireInputs(
            PayloadRecoveryAuthority authority,
            PayloadNamespaceState observed)
        {
            if (authority == null || observed == null)
            {
                throw new ArgumentNullException(
                    authority == null ? "authority" : "observed");
            }
            authority.Validate();
            if (!String.Equals(
                authority.TransactionId,
                observed.TransactionId,
                StringComparison.Ordinal))
            {
                throw Invalid("cross-transaction namespace");
            }
        }

        private static void RequireReplacement(
            PayloadRecoveryAuthority authority)
        {
            if (!IsReplacement(authority.Operation))
            {
                throw Invalid("replacement operation");
            }
        }

        private static bool IsReplacement(InstallOperation operation)
        {
            return operation == InstallOperation.Upgrade ||
                operation == InstallOperation.Repair ||
                operation == InstallOperation.ExplicitDowngrade;
        }

        private static void RequireBaseline(
            PayloadRecoveryAuthority authority,
            PayloadDirectoryCheckpoint directory)
        {
            if (!Matches(authority.Baseline, directory))
            {
                throw Invalid("baseline payload identity");
            }
        }

        private static void RequireTarget(
            PayloadRecoveryAuthority authority,
            PayloadDirectoryCheckpoint directory)
        {
            if (!Matches(authority.Target, directory))
            {
                throw Invalid("target payload identity");
            }
        }

        private static bool Matches(
            PayloadContentAuthority expected,
            PayloadDirectoryCheckpoint actual)
        {
            return expected != null &&
                actual != null &&
                expected.Matches(actual);
        }

        private static PayloadNamespaceMutationPlan Terminal(
            PayloadNamespaceState observed)
        {
            return new PayloadNamespaceMutationPlan(
                observed,
                PayloadNamespaceMutationKind.None,
                null);
        }

        private static PayloadNamespaceMutationPlan Action(
            PayloadNamespaceState observed,
            PayloadNamespaceMutationKind kind,
            PayloadDirectoryCheckpoint source)
        {
            return new PayloadNamespaceMutationPlan(observed, kind, source);
        }

        private static PayloadNamespaceMutationPlan Reject(string context)
        {
            throw Invalid(context);
        }

        private static InvalidOperationException Invalid(string context)
        {
            return new InvalidOperationException(
                "Payload recovery planner rejected " + context + ".");
        }
    }
}
