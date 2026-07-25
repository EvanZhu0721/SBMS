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
        void Apply(
            InstallerMutation mutation,
            ReleaseIdentity target,
            TransactionContext context);
        void VerifyApplied(
            InstallerMutation mutation,
            ReleaseIdentity target,
            TransactionContext context,
            MachineSnapshot observed);
        void Reconcile(
            MachineSnapshot baseline,
            TransactionJournal journal,
            MachineSnapshot observed);
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
            RecoverPending();

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
                    InstallerTransactionPlan.ForOperation(operation);
                for (int index = 0; index < mutations.Length; ++index)
                {
                    InstallerMutation mutation = mutations[index];
                    var intent = new CompensationIntent
                    {
                        Sequence = index,
                        Mutation = mutation,
                        Status = CompensationIntentStatus.Prepared
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
                    journal.AddStage(
                        "MutationVerified",
                        mutation,
                        "Applied",
                        observed,
                        String.Empty);
                    journalStore.Save(journal);
                }
                journal.Status = TransactionStatus.Committed;
                journal.AddStage(
                    "Commit",
                    null,
                    "Committed",
                    platform.Inspect(),
                    String.Empty);
                journalStore.Save(journal);
                return journal;
            }
            catch (SimulatedTransactionProcessCrashException)
            {
                // Test-only stand-in for process termination. A real crash
                // cannot execute in-process compensation, so leave the
                // prepared journal for RecoverPending().
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
            TransactionJournal journal = journalStore.Load();
            if (journal == null ||
                journal.Status == TransactionStatus.Committed ||
                journal.Status == TransactionStatus.RolledBack)
            {
                return false;
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
                MachineSnapshot observed = platform.Inspect();
                observed.Validate();
                journal.Status = TransactionStatus.RollingBack;
                journal.LastError = transactionFailure.Message;
                journal.OriginalError = transactionFailure.ToString();
                journal.RollbackResult = "InProgress";
                journal.AddStage(
                    "RollbackStarted",
                    null,
                    "Observed",
                    observed,
                    transactionFailure.Message);
                journalStore.Save(journal);

                // Reconcile from observed state rather than replaying only
                // actions which returned successfully. Native tools may
                // partially mutate state before reporting failure.
                platform.Reconcile(journal.Baseline, journal, observed);
                MachineSnapshot restored = platform.Inspect();
                if (!platform.Equivalent(journal.Baseline, restored))
                {
                    throw new InvalidOperationException(
                        "Rollback verification did not match the captured baseline.");
                }
                journal.Status = TransactionStatus.RolledBack;
                journal.RollbackResult = "Verified";
                journal.RecoveryError = String.Empty;
                journal.AddStage(
                    "RollbackVerified",
                    null,
                    "Restored",
                    restored,
                    String.Empty);
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
    }
}
