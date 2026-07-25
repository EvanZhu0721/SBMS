using System;

namespace SBMSSetup
{
    internal interface IInstallerTransactionPlatform
    {
        void VerifyTrustedSource(InstallerTransactionRequest request);
        InstalledReleaseState InspectInstalledRelease();
        void Preflight(
            InstallerTransactionRequest request,
            InstallOperation operation);
        string PlanEscrowLocator(string transactionId);
        MachineSnapshot Inspect();
        MachineSnapshot InspectForRecovery();
        void Apply(
            InstallerMutation mutation,
            ReleaseIdentity target,
            TransactionContext context);
        void VerifyApplied(
            InstallerMutation mutation,
            ReleaseIdentity target,
            TransactionContext context,
            MachineSnapshot observed);
        void ApplyCompensation(
            InstallerCompensationAction action,
            MachineSnapshot baseline,
            TransactionJournal journal,
            MachineSnapshot observed);
        void VerifyCompensation(
            InstallerCompensationAction action,
            MachineSnapshot baseline,
            TransactionJournal journal,
            MachineSnapshot observed);
        string FinalizeCommitted(TransactionJournal journal);
        bool EquivalentForRollback(
            MachineSnapshot expected,
            MachineSnapshot actual);
        string FinalizeRolledBack(TransactionJournal journal);
        bool Equivalent(MachineSnapshot expected, MachineSnapshot actual);
    }

    internal sealed class SimulatedTransactionProcessCrashException : Exception
    {
        internal SimulatedTransactionProcessCrashException(string message)
            : base(message)
        {
        }
    }

    internal sealed class TransactionRolledBackException : Exception
    {
        internal TransactionRolledBackException(
            string message,
            Exception transactionFailure)
            : base(message, transactionFailure)
        {
        }
    }

    internal sealed class TransactionCommittedFinalizationException : Exception
    {
        internal TransactionCommittedFinalizationException(
            string message,
            Exception finalizationFailure)
            : base(message, finalizationFailure)
        {
        }
    }

    internal sealed class InstallerTransactionEngine
    {
        private readonly IInstallerTransactionPlatform platform;
        private readonly ITransactionJournalStore journalStore;

        internal InstallerTransactionEngine(
            IInstallerTransactionPlatform platform,
            ITransactionJournalStore journalStore)
        {
            if (platform == null)
            {
                throw new ArgumentNullException("platform");
            }
            if (journalStore == null)
            {
                throw new ArgumentNullException("journalStore");
            }
            this.platform = platform;
            this.journalStore = journalStore;
        }

        internal TransactionJournal Execute(InstallerTransactionRequest request)
        {
            ITransactionExecutionLeaseProvider leaseProvider =
                journalStore as ITransactionExecutionLeaseProvider;
            using (IDisposable lease = leaseProvider == null
                ? null
                : leaseProvider.AcquireTransactionLease())
            {
                return ExecuteUnderLease(request);
            }
        }

        private TransactionJournal ExecuteUnderLease(
            InstallerTransactionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }
            if (request.Flags == null)
            {
                throw new InvalidOperationException(
                    "Installer request flags are missing.");
            }

            // An untrusted executable may not mutate or recover installer
            // state. Once trust is established, an incomplete prior
            // transaction is recovered before any new classification.
            platform.VerifyTrustedSource(request);
            RecoverPendingUnderLease();

            InstalledReleaseState installed =
                platform.InspectInstalledRelease();
            InstallOperation operation = InstallOperationClassifier.Classify(
                request.RequestedOperation,
                installed,
                request.Target);

            // Preflight still precedes all writes for the new transaction.
            platform.Preflight(request, operation);

            MachineSnapshot baseline = platform.Inspect();
            baseline.Validate();
            string transactionId = Guid.NewGuid().ToString("N");
            string escrowLocator =
                platform.PlanEscrowLocator(transactionId);
            TransactionJournal journal =
                TransactionJournal.Create(
                    transactionId,
                    operation,
                    baseline,
                    request.Target,
                    request.Flags,
                    escrowLocator);
            journal.AddStage(
                "BaselineCaptured",
                null,
                "Verified",
                baseline,
                String.Empty);
            journalStore.PrepareForNewTransaction();
            journalStore.Save(journal);

            try
            {
                InstallerMutation[] mutations =
                    InstallerTransactionPlan.ForOperation(
                        operation,
                        request.Flags);
                for (int index = 0; index < mutations.Length; ++index)
                {
                    InstallerMutation mutation = mutations[index];
                    MachineSnapshot before = platform.Inspect();
                    before.Validate();
                    var intent = new CompensationIntent
                    {
                        Sequence = index,
                        Mutation = mutation,
                        Status = CompensationIntentStatus.Prepared,
                        InverseAction =
                            InstallerTransactionPlan.InverseFor(mutation),
                        BeforeEvidence = before.EvidenceDigest,
                        AfterEvidence = String.Empty,
                        RecoveryError = String.Empty,
                        CompensationBeforeEvidence = String.Empty
                    };
                    journal.Intents.Add(intent);
                    journal.Status = TransactionStatus.Applying;
                    journal.AddStage(
                        "MutationPrepared",
                        mutation,
                        "Prepared",
                        null,
                        String.Empty);

                    // Write-ahead invariant: a durable compensation intent
                    // always exists before the platform may mutate anything.
                    journalStore.Save(journal);

                    string contextInvariant =
                        journal.Context.InvariantDigest;
                    platform.Apply(
                        mutation,
                        request.Target,
                        journal.Context.DeepClone());
                    AssertContextInvariant(journal, contextInvariant);

                    // A successful return is not evidence that the requested
                    // state exists. PnP and filesystem adapters must expose
                    // their actual state through Inspect().
                    MachineSnapshot observed = platform.Inspect();
                    observed.Validate();
                    journal.AddStage(
                        "MutationObserved",
                        mutation,
                        "Observed",
                        observed,
                        String.Empty);
                    journalStore.Save(journal);
                    platform.VerifyApplied(
                        mutation,
                        request.Target,
                        journal.Context.DeepClone(),
                        observed);
                    AssertContextInvariant(journal, contextInvariant);
                    intent.Status = CompensationIntentStatus.Applied;
                    intent.AfterEvidence = observed.EvidenceDigest;
                    journal.AddStage(
                        "MutationVerified",
                        mutation,
                        "Applied",
                        observed,
                        String.Empty);
                    journalStore.Save(journal);
                }
                MachineSnapshot committed = platform.Inspect();
                committed.Validate();
                journal.Status = TransactionStatus.Committed;
                journal.FinalizationStatus =
                    TransactionFinalizationStatus.Pending;
                journal.AddStage(
                    "Commit",
                    null,
                    "Committed",
                    committed,
                    String.Empty);
                journalStore.Save(journal);
                return FinalizeCommitted(journal);
            }
            catch (SimulatedTransactionProcessCrashException)
            {
                // Test-only stand-in for process termination. A real crash
                // cannot execute in-process compensation, so leave the
                // prepared journal for RecoverPending().
                throw;
            }
            catch (TransactionCommittedFinalizationException)
            {
                // The requested state is already durably committed. It must
                // never be rolled back merely because post-commit escrow
                // cleanup failed.
                throw;
            }
            catch (Exception failure)
            {
                RollBack(journal, failure);
                throw new TransactionRolledBackException(
                    "Installer transaction failed and the baseline was restored.",
                    failure);
            }
        }

        private static void AssertContextInvariant(
            TransactionJournal journal,
            string expectedDigest)
        {
            if (!String.Equals(
                journal.Context.InvariantDigest,
                expectedDigest,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Platform mutation changed the durable transaction context.");
            }
        }

        internal bool RecoverPending()
        {
            ITransactionExecutionLeaseProvider leaseProvider =
                journalStore as ITransactionExecutionLeaseProvider;
            using (IDisposable lease = leaseProvider == null
                ? null
                : leaseProvider.AcquireTransactionLease())
            {
                return RecoverPendingUnderLease();
            }
        }

        private bool RecoverPendingUnderLease()
        {
            TransactionJournal journal = journalStore.Load();
            if (journal == null ||
                journal.Status == TransactionStatus.RolledBack)
            {
                return false;
            }
            if (journal.Status == TransactionStatus.Committed)
            {
                if (journal.FinalizationStatus ==
                    TransactionFinalizationStatus.Complete)
                {
                    return false;
                }
                FinalizeCommitted(journal);
                return true;
            }
            RollBack(
                journal,
                new InvalidOperationException(
                    "Recovering an incomplete installer transaction."));
            return true;
        }

        private void RollBack(
            TransactionJournal journal,
            Exception transactionFailure)
        {
            try
            {
                MachineSnapshot observed = platform.InspectForRecovery();
                observed.ValidateForRecovery();
                journal.Status = TransactionStatus.RollingBack;
                journal.LastError = transactionFailure.Message;
                journal.OriginalError = transactionFailure.ToString();
                journal.RollbackResult = "InProgress";
                journal.AddRecoveryStage(
                    "RollbackStarted",
                    null,
                    "Observed",
                    observed,
                    transactionFailure.Message);
                journalStore.Save(journal);

                // Prepared intents are compensated too: a native operation
                // can partially mutate state before returning an error.
                for (int index = journal.Intents.Count - 1;
                     index >= 0;
                     --index)
                {
                    CompensationIntent intent = journal.Intents[index];
                    if (intent.Status == CompensationIntentStatus.Restored)
                    {
                        continue;
                    }
                    intent.Status =
                        CompensationIntentStatus.RestorePrepared;
                    intent.CompensationBeforeEvidence =
                        observed.RecoveryEvidenceDigest;
                    intent.RecoveryError = String.Empty;
                    journal.AddRecoveryStage(
                        "CompensationPrepared",
                        intent.Mutation,
                        intent.InverseAction.ToString(),
                        observed,
                        String.Empty);
                    journalStore.Save(journal);

                    try
                    {
                        platform.ApplyCompensation(
                            intent.InverseAction,
                            journal.Baseline,
                            journal,
                            observed);
                        observed = platform.InspectForRecovery();
                        observed.ValidateForRecovery();
                        intent.AfterEvidence =
                            observed.RecoveryEvidenceDigest;
                        journal.AddRecoveryStage(
                            "CompensationObserved",
                            intent.Mutation,
                            "Observed",
                            observed,
                            String.Empty);
                        journalStore.Save(journal);
                        platform.VerifyCompensation(
                            intent.InverseAction,
                            journal.Baseline,
                            journal,
                            observed);
                        intent.Status = CompensationIntentStatus.Restored;
                        journal.AddRecoveryStage(
                            "CompensationVerified",
                            intent.Mutation,
                            "Restored",
                            observed,
                            String.Empty);
                        journalStore.Save(journal);
                    }
                    catch (Exception compensationFailure)
                    {
                        intent.Status =
                            CompensationIntentStatus.RestoreFailed;
                        intent.RecoveryError =
                            compensationFailure.ToString();
                        journalStore.Save(journal);
                        throw;
                    }
                }
                MachineSnapshot restored = platform.Inspect();
                restored.Validate();
                if (!platform.EquivalentForRollback(
                        journal.Baseline,
                        restored))
                {
                    throw new InvalidOperationException(
                        "Rollback verification did not match the captured baseline.");
                }
                string rollbackFinalization =
                    platform.FinalizeRolledBack(journal);
                if (String.IsNullOrWhiteSpace(rollbackFinalization))
                {
                    throw new InvalidOperationException(
                        "Rollback escrow finalization returned no readback evidence.");
                }
                MachineSnapshot finalized = platform.Inspect();
                finalized.Validate();
                if (!platform.Equivalent(journal.Baseline, finalized))
                {
                    throw new InvalidOperationException(
                        "Rollback escrow cleanup did not restore exact baseline evidence.");
                }
                journal.Status = TransactionStatus.RolledBack;
                journal.RollbackResult = "Verified";
                journal.RecoveryError = String.Empty;
                journal.AddStage(
                    "RollbackVerified",
                    null,
                    "Restored",
                    finalized,
                    rollbackFinalization);
                journalStore.Save(journal);
            }
            catch (Exception recoveryFailure)
            {
                journal.Status = TransactionStatus.RecoveryFailed;
                journal.OriginalError = transactionFailure.ToString();
                journal.RecoveryError = recoveryFailure.ToString();
                journal.RollbackResult = "Failed";
                journal.LastError =
                    transactionFailure.Message + " | rollback=" +
                    recoveryFailure.Message;
                journal.AddStage(
                    "RollbackFailed",
                    null,
                    "Failed",
                    null,
                    recoveryFailure.Message);
                try
                {
                    journalStore.Save(journal);
                }
                catch (Exception persistenceFailure)
                {
                    throw new InvalidOperationException(
                        "Installer rollback failed and RecoveryFailed evidence could not be persisted.",
                        new AggregateException(
                            transactionFailure,
                            recoveryFailure,
                            persistenceFailure));
                }
                throw new InvalidOperationException(
                    "Installer transaction and rollback both failed.",
                    new AggregateException(transactionFailure, recoveryFailure));
            }
        }

        private TransactionJournal FinalizeCommitted(
            TransactionJournal journal)
        {
            try
            {
                string evidence = platform.FinalizeCommitted(journal);
                if (String.IsNullOrWhiteSpace(evidence))
                {
                    throw new InvalidOperationException(
                        "Committed finalization returned no readback evidence.");
                }
                journal.FinalizationStatus =
                    TransactionFinalizationStatus.Complete;
                journal.FinalizationEvidence = evidence;
                journal.FinalizationError = String.Empty;
                journal.AddStage(
                    "Finalization",
                    null,
                    "Complete",
                    null,
                    evidence);
                journalStore.Save(journal);
                return journal;
            }
            catch (Exception failure)
            {
                journal.FinalizationStatus =
                    TransactionFinalizationStatus.Failed;
                journal.FinalizationError = failure.ToString();
                journal.AddStage(
                    "Finalization",
                    null,
                    "Failed",
                    null,
                    failure.Message);
                try
                {
                    journalStore.Save(journal);
                }
                catch (Exception persistenceFailure)
                {
                    throw new TransactionCommittedFinalizationException(
                        "Installer committed, but finalization failed and its evidence could not be persisted.",
                        new AggregateException(
                            failure,
                            persistenceFailure));
                }
                throw new TransactionCommittedFinalizationException(
                    "Installer committed, but escrow finalization failed. A new transaction is blocked until cleanup readback succeeds.",
                    failure);
            }
        }
    }
}
