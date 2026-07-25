using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SBMSSetup
{
    internal static class InstallerTransactionTests
    {
        private static int passed;
        private static int failed;

        private sealed class FakeMachine
        {
            internal string State = "empty";
            internal InstalledReleaseState Installed = Absent();
            internal bool DriverPresent;
            internal EscrowEvidence Escrow = EmptyEscrow();
        }

        private sealed class FakePlatform : IInstallerTransactionPlatform
        {
            private readonly ITransactionJournalStore journalStore;
            private readonly FakeMachine machine;
            internal InstallerMutation? FaultBefore;
            internal InstallerMutation? FaultAfter;
            internal InstallerMutation? CrashAfter;
            internal InstallerMutation? NoOp;
            internal InstallerMutation? WrongBinding;
            internal InstallerMutation? HasProblem;
            internal bool RejectTrust;
            internal bool RejectPreflight;
            internal bool FailReconcile;
            internal int FailInspectAtCount;
            internal int InspectCount;
            internal int InstalledInspectCount;
            internal int ReconcileCount;
            internal int ApplyCount;
            internal bool RequirePreparedIntent;
            internal bool SawCompleteContext;
            internal bool MutateReceivedContext;

            internal FakePlatform(
                FakeMachine machine,
                ITransactionJournalStore journalStore)
            {
                this.machine = machine;
                this.journalStore = journalStore;
            }

            public void VerifyTrustedSource(InstallerTransactionRequest request)
            {
                if (RejectTrust)
                {
                    throw new InvalidOperationException("untrusted source");
                }
            }

            public InstalledReleaseState InspectInstalledRelease()
            {
                ++InstalledInspectCount;
                return CloneInstalled(machine.Installed);
            }

            public void Preflight(
                InstallerTransactionRequest request,
                InstallOperation operation)
            {
                if (RejectPreflight)
                {
                    throw new InvalidOperationException("preflight rejected");
                }
            }

            public string PlanEscrowLocator(string transactionId)
            {
                return Path.Combine(
                    Path.GetTempPath(),
                    "SBMS-fake-escrow",
                    transactionId);
            }

            public MachineSnapshot Inspect()
            {
                ++InspectCount;
                if (FailInspectAtCount == InspectCount)
                {
                    throw new InvalidOperationException("inspection failed");
                }
                return Snapshot(machine);
            }

            public void Apply(
                InstallerMutation mutation,
                ReleaseIdentity target,
                TransactionContext context)
            {
                ++ApplyCount;
                context.Validate();
                if (MutateReceivedContext)
                {
                    context.EscrowLocator = Path.Combine(
                        Path.GetTempPath(),
                        "attacker",
                        context.TransactionId);
                }
                SawCompleteContext =
                    context.TransactionId.Length > 0 &&
                    Path.GetFileName(context.EscrowLocator) ==
                        context.TransactionId;
                if (RequirePreparedIntent)
                {
                    TransactionJournal durable = journalStore.Load();
                    Assert(
                        durable.Intents.Count > 0 &&
                        durable.Intents[durable.Intents.Count - 1].Mutation ==
                            mutation &&
                        durable.Intents[durable.Intents.Count - 1].Status ==
                            CompensationIntentStatus.Prepared,
                        "Mutation ran before its WAL intent was durable.");
                }
                if (FaultBefore == mutation)
                {
                    throw new InvalidOperationException("fault-before-" + mutation);
                }
                if (NoOp != mutation)
                {
                    machine.State += "|" + mutation;
                }
                if (mutation == InstallerMutation.CreateEscrow &&
                    NoOp != mutation)
                {
                    machine.Escrow = new EscrowEvidence
                    {
                        ManifestPath = Path.Combine(
                            PlanEscrowLocator(context.TransactionId),
                            "escrow-manifest.json"),
                        ManifestSha256 = new String('a', 64),
                        Complete = true,
                        DriverPackageCount = machine.DriverPresent ? 1 : 0,
                        PayloadFileCount = machine.Installed.IsInstalled ? 4 : 0,
                        ConfigurationFileCount = 1,
                        IntegrationCount = 2
                    };
                }
                if (mutation == InstallerMutation.StageDriver)
                {
                    machine.DriverPresent = true;
                }
                if (WrongBinding == mutation)
                {
                    machine.State += "|wrong-binding";
                }
                if (HasProblem == mutation)
                {
                    machine.State += "|HasProblem";
                }
                if (mutation == InstallerMutation.RemoveStaleOwnedAssets)
                {
                    machine.Installed = Present(target);
                }
                if (mutation == InstallerMutation.RemoveOwnedPayload)
                {
                    machine.Installed = Absent();
                }
                if (mutation == InstallerMutation.RemoveOwnedPackages)
                {
                    machine.DriverPresent = false;
                }
                if (CrashAfter == mutation)
                {
                    throw new SimulatedTransactionProcessCrashException(
                        "crash-after-" + mutation);
                }
                if (FaultAfter == mutation)
                {
                    throw new InvalidOperationException("fault-after-" + mutation);
                }
            }

            public void VerifyApplied(
                InstallerMutation mutation,
                ReleaseIdentity target,
                TransactionContext context,
                MachineSnapshot observed)
            {
                context.Validate();
                string evidence = observed.EvidenceDigest;
                if (mutation == InstallerMutation.CreateEscrow &&
                    (!observed.Escrow.Complete ||
                     !observed.Escrow.ManifestPath.StartsWith(
                        context.EscrowLocator +
                            Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) ||
                     observed.Escrow.ManifestSha256.Length != 64))
                {
                    throw new InvalidOperationException(
                        "escrow manifest evidence is incomplete");
                }
                if (evidence.IndexOf(
                        "|" + mutation,
                        StringComparison.Ordinal) < 0)
                {
                    throw new InvalidOperationException("target state missing");
                }
                if (evidence.IndexOf("wrong-binding", StringComparison.Ordinal) >= 0)
                {
                    throw new InvalidOperationException("wrong binding");
                }
                if (evidence.IndexOf("HasProblem", StringComparison.Ordinal) >= 0)
                {
                    throw new InvalidOperationException("device HasProblem");
                }
            }

            public void Reconcile(
                MachineSnapshot baseline,
                TransactionJournal journal,
                MachineSnapshot observed)
            {
                ++ReconcileCount;
                journal.Context.Validate();
                if (FailReconcile)
                {
                    throw new InvalidOperationException("reconcile failed");
                }
                machine.State = StateFromSnapshot(baseline);
                machine.Installed = baseline.Payload.Present
                    ? Present(new ReleaseIdentity(
                        baseline.Payload.ReleaseVersion,
                        baseline.Payload.PackageFingerprint))
                    : Absent();
                machine.DriverPresent = baseline.Driver.Present;
                machine.Escrow = CloneEscrow(baseline.Escrow);
            }

            public bool Equivalent(
                MachineSnapshot expected,
                MachineSnapshot actual)
            {
                return String.Equals(
                    expected.EvidenceDigest,
                    actual.EvidenceDigest,
                    StringComparison.Ordinal);
            }
        }

        private sealed class FaultingStore : ITransactionJournalStore
        {
            private readonly ITransactionJournalStore inner;
            internal bool FailRollingBackOnce;

            internal FaultingStore(ITransactionJournalStore inner)
            {
                this.inner = inner;
            }

            public void Save(TransactionJournal journal)
            {
                if (FailRollingBackOnce &&
                    journal.Status == TransactionStatus.RollingBack)
                {
                    FailRollingBackOnce = false;
                    throw new IOException("rolling back save failed");
                }
                inner.Save(journal);
            }

            public TransactionJournal Load()
            {
                return inner.Load();
            }

            public void PrepareForNewTransaction()
            {
                inner.PrepareForNewTransaction();
            }
        }

        private sealed class RotationCrashInjector :
            ITerminalRotationFaultInjector
        {
            private readonly TerminalRotationCrashPoint point;
            private bool fired;

            internal RotationCrashInjector(TerminalRotationCrashPoint point)
            {
                this.point = point;
            }

            public void After(TerminalRotationCrashPoint current)
            {
                if (!fired && current == point)
                {
                    fired = true;
                    throw new IOException("rotation-crash-" + current);
                }
            }
        }

        private static void Main()
        {
            Run("authoritative operation classification", TestClassification);
            Run("request cannot forge installed state", TestAuthoritativeInstalledState);
            Run("trust and preflight reject with zero journal write", TestZeroWriteGates);
            Run("escrow is first WAL mutation with durable context", TestEscrowWalContext);
            Run("applied-state verification rejects unsafe outcomes", TestAppliedVerification);
            Run("all before and after faults restore baseline", TestFaultMatrix);
            Run("pending transaction auto-recovers before new transaction", TestAutomaticRecovery);
            Run("failed recovery blocks a new transaction", TestRecoveryFailureBlocks);
            Run("one store supports full lifecycle and archives history", TestLifecycleHistory);
            Run("terminal rotation is crash-safe and idempotent", TestRotationCrashWindows);
            Run("terminal rotation rebuilds a torn archive temp", TestTornArchiveTemp);
            Run("crash recovery uses journal context and escrow", TestContextCrashRecovery);
            Run("platform receives a detached transaction context", TestDetachedContext);
            Run("journal revision and torn backup are durable", TestJournalRevisionBackup);
            Run("journal and structured snapshot validation fail closed", TestStrictValidation);
            Run("driver evidence presence semantics fail closed", TestDriverEvidenceSemantics);
            Run("rollback setup failures preserve both errors", TestRollbackFailureEvidence);

            Console.WriteLine(
                "Installer transaction contract: " + passed +
                " passed, " + failed + " failed");
            Environment.ExitCode = failed == 0 ? 0 : 1;
        }

        private static void TestClassification()
        {
            Equal(
                InstallOperation.FreshInstall,
                InstallOperationClassifier.Classify(
                    InstallOperationRequest.Auto,
                    Absent(),
                    Release("1.0.0", "one")),
                "fresh");
            Equal(
                InstallOperation.Upgrade,
                InstallOperationClassifier.Classify(
                    InstallOperationRequest.Auto,
                    Present(Release("1.0.0", "one")),
                    Release("2.0.0", "two")),
                "upgrade");
            Equal(
                InstallOperation.Repair,
                InstallOperationClassifier.Classify(
                    InstallOperationRequest.Auto,
                    Present(Release("2.0.0", "two")),
                    Release("2.0.0.0", "TWO")),
                "repair");
            Equal(
                InstallOperation.ExplicitDowngrade,
                InstallOperationClassifier.Classify(
                    InstallOperationRequest.ExplicitDowngrade,
                    Present(Release("2.0.0", "two")),
                    Release("1.0.0", "one")),
                "downgrade");
            Equal(
                InstallOperation.Uninstall,
                InstallOperationClassifier.Classify(
                    InstallOperationRequest.Uninstall,
                    Present(Release("2.0.0", "two")),
                    null),
                "uninstall");
        }

        private static void TestAuthoritativeInstalledState()
        {
            Assert(
                typeof(InstallerTransactionRequest).GetField(
                    "Installed",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic) == null,
                "Request still carries forgeable installed state.");

            string root = NewRoot();
            try
            {
                var store = Store(root);
                var machine = new FakeMachine();
                machine.Installed = Present(Release("2.0.0", "authoritative"));
                var platform = new FakePlatform(machine, store);
                var engine = new InstallerTransactionEngine(platform, store);
                AssertAnyThrows(
                    delegate
                    {
                        engine.Execute(Request(
                            InstallOperationRequest.Auto,
                            Release("2.0.0", "forged")));
                    },
                    "Authoritative same-version collision was bypassed.");
                AssertNoJournal(store);
                Assert(platform.InstalledInspectCount == 1,
                    "Engine did not inspect authoritative installed state.");

                AssertAnyThrows(
                    delegate
                    {
                        engine.Execute(Request(
                            InstallOperationRequest.Auto,
                            Release("1.0.0", "old")));
                    },
                    "Authoritative downgrade was bypassed.");
                AssertNoJournal(store);
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestZeroWriteGates()
        {
            foreach (bool rejectTrust in new[] { true, false })
            {
                string root = NewRoot();
                try
                {
                    var store = Store(root);
                    var platform = new FakePlatform(new FakeMachine(), store);
                    platform.RejectTrust = rejectTrust;
                    platform.RejectPreflight = !rejectTrust;
                    var engine = new InstallerTransactionEngine(platform, store);
                    AssertAnyThrows(
                        delegate
                        {
                            engine.Execute(Request(
                                InstallOperationRequest.Auto,
                                Release("1.0.0", "one")));
                        },
                        "Rejected gate was accepted.");
                    AssertNoJournal(store);
                    Assert(platform.ApplyCount == 0, "Rejected gate mutated platform.");
                }
                finally
                {
                    DeleteRoot(root);
                }
            }
        }

        private static void TestEscrowWalContext()
        {
            string root = NewRoot();
            try
            {
                var store = Store(root);
                var platform = new FakePlatform(new FakeMachine(), store);
                platform.RequirePreparedIntent = true;
                TransactionJournal journal = new InstallerTransactionEngine(
                    platform,
                    store).Execute(Request(
                        InstallOperationRequest.Auto,
                        Release("1.0.0", "one")));
                Assert(journal.Intents[0].Mutation == InstallerMutation.CreateEscrow,
                    "CreateEscrow is not the first mutation.");
                Assert(platform.SawCompleteContext,
                    "Platform did not receive complete context.");
                Assert(journal.Context.Baseline != null &&
                       !String.IsNullOrWhiteSpace(journal.Context.EscrowLocator),
                    "Journal context is incomplete.");
                Assert(journal.StageEvents.Exists(
                    delegate(TransactionStageEvent stage)
                    {
                        return stage.Mutation == "CreateEscrow" &&
                               stage.ObservedEvidence.IndexOf(
                                   "escrow-manifest.json",
                                   StringComparison.OrdinalIgnoreCase) >= 0;
                    }),
                    "CreateEscrow observed evidence is missing.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestAppliedVerification()
        {
            foreach (string mode in new[] { "noop", "wrong", "problem" })
            {
                string root = NewRoot();
                try
                {
                    var store = Store(root);
                    var platform = new FakePlatform(new FakeMachine(), store);
                    if (mode == "noop")
                    {
                        platform.NoOp = InstallerMutation.StageDriver;
                    }
                    else if (mode == "wrong")
                    {
                        platform.WrongBinding = InstallerMutation.ActivateDriver;
                    }
                    else
                    {
                        platform.HasProblem = InstallerMutation.ActivateDriver;
                    }
                    AssertRolledBack(
                        new InstallerTransactionEngine(platform, store),
                        Request(
                            InstallOperationRequest.Auto,
                            Release("1.0.0", "one")));
                    Assert(store.Load().Status == TransactionStatus.RolledBack,
                        "Unsafe applied state was not durably rolled back.");
                }
                finally
                {
                    DeleteRoot(root);
                }
            }
        }

        private static void TestFaultMatrix()
        {
            foreach (InstallOperation operation in new[]
            {
                InstallOperation.FreshInstall,
                InstallOperation.Upgrade,
                InstallOperation.Repair,
                InstallOperation.ExplicitDowngrade,
                InstallOperation.Uninstall
            })
            {
                foreach (InstallerMutation mutation in
                    InstallerTransactionPlan.ForOperation(operation))
                {
                    foreach (bool after in new[] { false, true })
                    {
                        string root = NewRoot();
                        try
                        {
                            var store = Store(root);
                            var machine = MachineFor(operation);
                            string baseline = Snapshot(machine).EvidenceDigest;
                            var platform = new FakePlatform(machine, store);
                            if (after)
                            {
                                platform.FaultAfter = mutation;
                            }
                            else
                            {
                                platform.FaultBefore = mutation;
                            }
                            AssertRolledBack(
                                new InstallerTransactionEngine(platform, store),
                                RequestFor(operation));
                            Assert(
                                Snapshot(machine).EvidenceDigest == baseline,
                                "Fault did not restore baseline: " +
                                operation + "/" + mutation + "/" + after);
                        }
                        finally
                        {
                            DeleteRoot(root);
                        }
                    }
                }
            }
        }

        private static void TestAutomaticRecovery()
        {
            string root = NewRoot();
            try
            {
                var store = Store(root);
                var machine = MachineFor(InstallOperation.Upgrade);
                var crashing = new FakePlatform(machine, store);
                crashing.CrashAfter = InstallerMutation.CommitPayload;
                AssertCrash(
                    new InstallerTransactionEngine(crashing, store),
                    RequestFor(InstallOperation.Upgrade));

                var resumed = new FakePlatform(machine, store);
                TransactionJournal result = new InstallerTransactionEngine(
                    resumed,
                    store).Execute(RequestFor(InstallOperation.Upgrade));
                Assert(resumed.ReconcileCount == 1,
                    "Pending transaction was not automatically recovered.");
                Assert(result.Status == TransactionStatus.Committed,
                    "New transaction did not run after verified recovery.");
                Assert(Directory.GetFiles(store.HistoryDirectory, "*.json").Length == 1,
                    "Recovered transaction was not archived.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestRecoveryFailureBlocks()
        {
            string root = NewRoot();
            try
            {
                var store = Store(root);
                var machine = MachineFor(InstallOperation.Upgrade);
                var crashing = new FakePlatform(machine, store);
                crashing.CrashAfter = InstallerMutation.StageDriver;
                AssertCrash(
                    new InstallerTransactionEngine(crashing, store),
                    RequestFor(InstallOperation.Upgrade));

                var blocked = new FakePlatform(machine, store);
                blocked.FailReconcile = true;
                AssertAnyThrows(
                    delegate
                    {
                        new InstallerTransactionEngine(blocked, store).Execute(
                            RequestFor(InstallOperation.Upgrade));
                    },
                    "New transaction ran despite failed recovery.");
                Assert(blocked.ApplyCount == 0,
                    "Failed recovery allowed a new mutation plan.");
                Assert(store.Load().Status == TransactionStatus.RecoveryFailed,
                    "Recovery failure was not durable.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestLifecycleHistory()
        {
            string root = NewRoot();
            try
            {
                var store = Store(root);
                var machine = new FakeMachine();
                var platform = new FakePlatform(machine, store);
                var engine = new InstallerTransactionEngine(platform, store);
                var ids = new HashSet<string>(StringComparer.Ordinal);

                ids.Add(engine.Execute(Request(
                    InstallOperationRequest.Auto,
                    Release("1.0.0", "one"))).TransactionId);
                ids.Add(engine.Execute(Request(
                    InstallOperationRequest.Auto,
                    Release("2.0.0", "two"))).TransactionId);
                ids.Add(engine.Execute(Request(
                    InstallOperationRequest.Auto,
                    Release("2.0.0", "two"))).TransactionId);
                ids.Add(engine.Execute(Request(
                    InstallOperationRequest.Uninstall,
                    null)).TransactionId);
                ids.Add(engine.Execute(Request(
                    InstallOperationRequest.Auto,
                    Release("1.0.0", "one"))).TransactionId);

                Assert(ids.Count == 5, "Lifecycle reused transaction identity.");
                Assert(Directory.GetFiles(store.HistoryDirectory, "*.json").Length == 4,
                    "Terminal history was overwritten or lost.");
                Assert(store.Load().Status == TransactionStatus.Committed,
                    "Current lifecycle transaction is not committed.");
                Assert(machine.Installed.IsInstalled &&
                       machine.Installed.Release.Version == "1.0.0",
                    "Reinstall did not become authoritative installed state.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestRotationCrashWindows()
        {
            foreach (TerminalRotationCrashPoint point in
                (TerminalRotationCrashPoint[])Enum.GetValues(
                    typeof(TerminalRotationCrashPoint)))
            {
                string root = NewRoot();
                try
                {
                    var initialStore = Store(root);
                    var machine = new FakeMachine();
                    new InstallerTransactionEngine(
                        new FakePlatform(machine, initialStore),
                        initialStore).Execute(Request(
                            InstallOperationRequest.Auto,
                            Release("1.0.0", "one")));

                    var crashingStore = new AtomicTransactionJournalStore(
                        initialStore.JournalPath,
                        new RotationCrashInjector(point));
                    AssertAnyThrows(
                        delegate { crashingStore.PrepareForNewTransaction(); },
                        "Rotation crash window did not fire: " + point);

                    var resumedStore = Store(root);
                    TransactionJournal activeBeforeRetry = resumedStore.Load();
                    if (activeBeforeRetry != null)
                    {
                        Assert(
                            activeBeforeRetry.Status ==
                                TransactionStatus.Committed,
                            "Stale Applying backup became active at " + point);
                    }
                    resumedStore.PrepareForNewTransaction();
                    Assert(resumedStore.Load() == null,
                        "Terminal rotation retry left an active journal.");
                    Assert(
                        Directory.GetFiles(
                            resumedStore.HistoryDirectory,
                            "*.json").Length == 1,
                        "Terminal archive was duplicated or lost at " + point);
                }
                finally
                {
                    DeleteRoot(root);
                }
            }
        }

        private static void TestTornArchiveTemp()
        {
            string root = NewRoot();
            try
            {
                var store = Store(root);
                new InstallerTransactionEngine(
                    new FakePlatform(new FakeMachine(), store),
                    store).Execute(Request(
                        InstallOperationRequest.Auto,
                        Release("1.0.0", "one")));
                TransactionJournal terminal = store.Load();
                Directory.CreateDirectory(store.HistoryDirectory);
                string archivePath = Path.Combine(
                    store.HistoryDirectory,
                    terminal.TransactionId + "-r" + terminal.Revision +
                    "-" + terminal.Status + ".json");
                File.WriteAllText(archivePath + ".new", "{\"schemaVersion\":");

                store.PrepareForNewTransaction();

                Assert(store.Load() == null,
                    "Torn archive temp retry left an active terminal.");
                Assert(File.Exists(archivePath),
                    "Torn archive temp was not rebuilt and published.");
                Assert(!File.Exists(archivePath + ".new"),
                    "Torn archive temp survived successful rotation.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestContextCrashRecovery()
        {
            string root = NewRoot();
            try
            {
                var store = Store(root);
                var machine = MachineFor(InstallOperation.Upgrade);
                var first = new FakePlatform(machine, store);
                first.CrashAfter = InstallerMutation.ActivateDriver;
                AssertCrash(
                    new InstallerTransactionEngine(first, store),
                    RequestFor(InstallOperation.Upgrade));
                TransactionJournal pending = store.Load();
                Assert(pending.Intents[0].Mutation == InstallerMutation.CreateEscrow &&
                       pending.Intents[0].Status ==
                           CompensationIntentStatus.Applied,
                    "Crash journal lost escrow completion.");

                var newProcessPlatform = new FakePlatform(machine, store);
                Assert(
                    new InstallerTransactionEngine(
                        newProcessPlatform,
                        store).RecoverPending(),
                    "New process did not recover from journal alone.");
                Assert(newProcessPlatform.SawCompleteContext == false,
                    "Unexpected apply occurred during recovery.");
                Assert(newProcessPlatform.ReconcileCount == 1,
                    "Journal context was not used for reconcile.");
                Assert(store.Load().RollbackResult == "Verified",
                    "Context recovery was not verified.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestDetachedContext()
        {
            string root = NewRoot();
            try
            {
                var store = Store(root);
                var platform = new FakePlatform(new FakeMachine(), store);
                platform.MutateReceivedContext = true;
                TransactionJournal journal = new InstallerTransactionEngine(
                    platform,
                    store).Execute(Request(
                        InstallOperationRequest.Auto,
                        Release("1.0.0", "one")));
                Assert(
                    Path.GetFileName(journal.Context.EscrowLocator) ==
                        journal.TransactionId,
                    "Platform changed durable context through a shared reference.");
                Assert(
                    journal.Context.InvariantDigest ==
                        store.Load().Context.InvariantDigest,
                    "Context invariant changed across persistence.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestJournalRevisionBackup()
        {
            string root = NewRoot();
            try
            {
                var store = Store(root);
                TransactionJournal journal = NewJournal(
                    InstallOperation.Upgrade,
                    Snapshot(MachineFor(InstallOperation.Upgrade)),
                    Release("2.0.0", "two"));
                store.Save(journal);
                Assert(journal.Revision == 1, "Initial revision is not one.");
                journal.Status = TransactionStatus.Applying;
                journal.Intents.Add(new CompensationIntent
                {
                    Sequence = 0,
                    Mutation = InstallerMutation.CreateEscrow,
                    Status = CompensationIntentStatus.Prepared
                });
                journal.AddStage(
                    "MutationPrepared",
                    InstallerMutation.CreateEscrow,
                    "Prepared",
                    null,
                    "");
                store.Save(journal);
                Assert(journal.Revision == 2, "Revision did not advance.");
                File.WriteAllText(store.JournalPath, "{\"schemaVersion\":");
                TransactionJournal backup = store.Load();
                Assert(backup.Revision == 1,
                    "Torn primary did not return prior durable revision.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestStrictValidation()
        {
            MachineSnapshot good = Snapshot(new FakeMachine());
            Action[] corruptions =
            {
                delegate { Snapshot(new FakeMachine()).Payload = null; },
                delegate
                {
                    MachineSnapshot value = Snapshot(new FakeMachine());
                    value.Driver.Present = true;
                    value.Driver.PackageSetFingerprint = "package";
                    value.Driver.DeviceInstanceFingerprint = "device";
                    value.Driver.BindingFingerprint = "";
                    value.Validate();
                },
                delegate
                {
                    MachineSnapshot value = Snapshot(new FakeMachine());
                    value.Integrations.StartupTaskFingerprint = "";
                    value.Validate();
                },
                delegate
                {
                    MachineSnapshot value = Snapshot(new FakeMachine());
                    value.Configuration.ContentFingerprint = "";
                    value.Validate();
                },
                delegate
                {
                    MachineSnapshot value = Snapshot(new FakeMachine());
                    value.Display.ActivePhysicalPathFingerprint = "";
                    value.Validate();
                }
            };
            // The first corruption needs an explicit validation call.
            AssertAnyThrows(
                delegate
                {
                    MachineSnapshot value = Snapshot(new FakeMachine());
                    value.Payload = null;
                    value.Validate();
                },
                "Missing payload evidence was accepted.");
            for (int index = 1; index < corruptions.Length; ++index)
            {
                AssertAnyThrows(corruptions[index],
                    "Independent structured evidence corruption was accepted.");
            }

            MachineSnapshot changed = Snapshot(new FakeMachine());
            changed.Display.ActivePhysicalPathFingerprint = "changed";
            var platform = new FakePlatform(
                new FakeMachine(),
                new NullJournalStore());
            Assert(!platform.Equivalent(good, changed),
                "Independent evidence change was ignored.");

            string root = NewRoot();
            try
            {
                var store = Store(root);
                TransactionJournal invalid = NewJournal(
                    InstallOperation.Upgrade,
                    good,
                    Release("2.0.0", "two"));
                invalid.Operation = (InstallOperation)999;
                AssertAnyThrows(
                    delegate { store.Save(invalid); },
                    "Invalid operation enum was accepted.");

                invalid = NewJournal(
                    InstallOperation.Uninstall,
                    good,
                    null);
                invalid.Context.EscrowLocator = "";
                AssertAnyThrows(
                    delegate { store.Save(invalid); },
                    "Missing escrow locator was accepted.");

                invalid = NewJournal(
                    InstallOperation.Upgrade,
                    good,
                    Release("2.0.0", "two"));
                invalid.CreatedUtc = "not-time";
                AssertAnyThrows(
                    delegate { store.Save(invalid); },
                    "Invalid timestamp was accepted.");

                invalid = NewJournal(
                    InstallOperation.Upgrade,
                    good,
                    Release("2.0.0", "two"));
                store.Save(invalid);
                string json = File.ReadAllText(store.JournalPath);
                File.WriteAllText(
                    store.JournalPath,
                    json.Replace("config-schema-1", "config-schema-9"));
                AssertAnyThrows(
                    delegate { store.Load(); },
                    "Journal field corruption bypassed content digest.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestDriverEvidenceSemantics()
        {
            AssertAnyThrows(
                delegate
                {
                    DriverEvidence value = Snapshot(
                        new FakeMachine()).Driver;
                    value.HasProblem = true;
                    value.ProblemCode = 43;
                    value.Validate();
                },
                "Absent driver accepted a problem state.");
            AssertAnyThrows(
                delegate
                {
                    DriverEvidence value = Snapshot(
                        new FakeMachine()).Driver;
                    value.ActivePublishedInf = "oem1.inf";
                    value.Validate();
                },
                "Absent driver accepted an active published INF.");
            AssertAnyThrows(
                delegate
                {
                    var machine = new FakeMachine();
                    machine.DriverPresent = true;
                    DriverEvidence value = Snapshot(machine).Driver;
                    value.ActivePublishedInf = "";
                    value.Validate();
                },
                "Present driver accepted an empty active published INF.");
            AssertAnyThrows(
                delegate
                {
                    var machine = new FakeMachine();
                    machine.DriverPresent = true;
                    DriverEvidence value = Snapshot(machine).Driver;
                    value.HasProblem = false;
                    value.ProblemCode = 31;
                    value.Validate();
                },
                "Driver accepted inconsistent problem code evidence.");
        }

        private sealed class NullJournalStore : ITransactionJournalStore
        {
            public void Save(TransactionJournal journal) { }
            public TransactionJournal Load() { return null; }
            public void PrepareForNewTransaction() { }
        }

        private static void TestRollbackFailureEvidence()
        {
            foreach (bool failSave in new[] { false, true })
            {
                string root = NewRoot();
                try
                {
                    var atomic = Store(root);
                    ITransactionJournalStore store = atomic;
                    if (failSave)
                    {
                        var faulting = new FaultingStore(atomic);
                        faulting.FailRollingBackOnce = true;
                        store = faulting;
                    }
                    var platform = new FakePlatform(new FakeMachine(), store);
                    platform.FaultBefore = InstallerMutation.CreateEscrow;
                    if (!failSave)
                    {
                        platform.FailInspectAtCount = 2;
                    }
                    AssertAnyThrows(
                        delegate
                        {
                            new InstallerTransactionEngine(platform, store).Execute(
                                Request(
                                    InstallOperationRequest.Auto,
                                    Release("1.0.0", "one")));
                        },
                        "Rollback setup failure did not escape.");
                    TransactionJournal journal = atomic.Load();
                    Assert(journal.Status == TransactionStatus.RecoveryFailed &&
                           !String.IsNullOrWhiteSpace(journal.OriginalError) &&
                           !String.IsNullOrWhiteSpace(journal.RecoveryError) &&
                           journal.RollbackResult == "Failed",
                        "Rollback failure evidence is incomplete.");
                }
                finally
                {
                    DeleteRoot(root);
                }
            }
        }

        private static TransactionJournal NewJournal(
            InstallOperation operation,
            MachineSnapshot baseline,
            ReleaseIdentity target)
        {
            string id = Guid.NewGuid().ToString("N");
            TransactionJournal journal = TransactionJournal.Create(
                id,
                operation,
                baseline,
                target,
                Flags(),
                Path.Combine(
                    Path.GetTempPath(),
                    "SBMS-fake-escrow",
                    id));
            journal.AddStage("BaselineCaptured", null, "Verified", baseline, "");
            return journal;
        }

        private static InstallerTransactionRequest Request(
            InstallOperationRequest operation,
            ReleaseIdentity target)
        {
            return new InstallerTransactionRequest
            {
                RequestedOperation = operation,
                Target = target,
                Flags = Flags()
            };
        }

        private static InstallerRequestFlags Flags()
        {
            return new InstallerRequestFlags
            {
                InstallDriver = true,
                CreateShortcut = true,
                CreateStartupTask = false,
                PreserveConfiguration = true
            };
        }

        private static InstallerTransactionRequest RequestFor(
            InstallOperation operation)
        {
            switch (operation)
            {
                case InstallOperation.FreshInstall:
                    return Request(
                        InstallOperationRequest.Auto,
                        Release("1.0.0", "one"));
                case InstallOperation.Upgrade:
                    return Request(
                        InstallOperationRequest.Auto,
                        Release("2.0.0", "two"));
                case InstallOperation.Repair:
                    return Request(
                        InstallOperationRequest.Auto,
                        Release("2.0.0", "two"));
                case InstallOperation.ExplicitDowngrade:
                    return Request(
                        InstallOperationRequest.ExplicitDowngrade,
                        Release("1.0.0", "one"));
                case InstallOperation.Uninstall:
                    return Request(InstallOperationRequest.Uninstall, null);
                default:
                    throw new InvalidOperationException("Unknown operation.");
            }
        }

        private static FakeMachine MachineFor(InstallOperation operation)
        {
            var machine = new FakeMachine();
            if (operation == InstallOperation.Upgrade)
            {
                machine.Installed = Present(Release("1.0.0", "one"));
                machine.State = "installed-one";
                machine.DriverPresent = true;
            }
            else if (operation == InstallOperation.Repair ||
                     operation == InstallOperation.Uninstall)
            {
                machine.Installed = Present(Release("2.0.0", "two"));
                machine.State = "installed-two";
                machine.DriverPresent = true;
            }
            else if (operation == InstallOperation.ExplicitDowngrade)
            {
                machine.Installed = Present(Release("2.0.0", "two"));
                machine.State = "installed-two";
                machine.DriverPresent = true;
            }
            return machine;
        }

        private static MachineSnapshot Snapshot(FakeMachine machine)
        {
            string state = machine.State;
            string version = machine.Installed.IsInstalled
                ? machine.Installed.Release.Version
                : "";
            string package = machine.Installed.IsInstalled
                ? machine.Installed.Release.PackageFingerprint
                : "";
            return new MachineSnapshot
            {
                Payload = new PayloadEvidence
                {
                    Present = machine.Installed.IsInstalled,
                    ReleaseVersion = version,
                    PackageFingerprint = package
                },
                Driver = new DriverEvidence
                {
                    Present = machine.DriverPresent,
                    PackageSetFingerprint = machine.DriverPresent
                        ? "packages-state:" + state
                        : "",
                    ActivePublishedInf = machine.DriverPresent
                        ? "oem-state-" + state + ".inf"
                        : "",
                    BindingFingerprint = machine.DriverPresent
                        ? "binding-state:" + state
                        : "",
                    DeviceInstanceFingerprint = machine.DriverPresent
                        ? "devices-state:" + state
                        : "",
                    HasProblem = machine.DriverPresent && state.IndexOf(
                        "HasProblem",
                        StringComparison.Ordinal) >= 0,
                    ProblemCode = machine.DriverPresent && state.IndexOf(
                        "HasProblem",
                        StringComparison.Ordinal) >= 0 ? 43 : 0
                },
                Integrations = new IntegrationEvidence
                {
                    ShortcutFingerprint = "shortcut-state:" + state,
                    StartupTaskFingerprint = "task-state:" + state
                },
                Configuration = new ConfigurationEvidence
                {
                    SchemaVersion = "config-schema-1",
                    ContentFingerprint = "config-state:" + state
                },
                Display = new DisplayEvidence
                {
                    ActivePhysicalPathCount = 1,
                    ActivePhysicalPathFingerprint = "display-state:" + state
                },
                Escrow = CloneEscrow(machine.Escrow)
            };
        }

        private static string StateFromSnapshot(MachineSnapshot snapshot)
        {
            const string prefix = "config-state:";
            string value = snapshot.Configuration.ContentFingerprint;
            if (!value.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Fake baseline state is missing.");
            }
            return value.Substring(prefix.Length);
        }

        private static EscrowEvidence EmptyEscrow()
        {
            return new EscrowEvidence
            {
                ManifestPath = "",
                ManifestSha256 = "",
                Complete = false,
                DriverPackageCount = 0,
                PayloadFileCount = 0,
                ConfigurationFileCount = 0,
                IntegrationCount = 0
            };
        }

        private static EscrowEvidence CloneEscrow(EscrowEvidence source)
        {
            return new EscrowEvidence
            {
                ManifestPath = source.ManifestPath,
                ManifestSha256 = source.ManifestSha256,
                Complete = source.Complete,
                DriverPackageCount = source.DriverPackageCount,
                PayloadFileCount = source.PayloadFileCount,
                ConfigurationFileCount = source.ConfigurationFileCount,
                IntegrationCount = source.IntegrationCount
            };
        }

        private static InstalledReleaseState Present(ReleaseIdentity release)
        {
            return new InstalledReleaseState
            {
                IsInstalled = true,
                Release = new ReleaseIdentity(
                    release.Version,
                    release.PackageFingerprint)
            };
        }

        private static InstalledReleaseState Absent()
        {
            return new InstalledReleaseState
            {
                IsInstalled = false,
                Release = null
            };
        }

        private static InstalledReleaseState CloneInstalled(
            InstalledReleaseState value)
        {
            return value.IsInstalled ? Present(value.Release) : Absent();
        }

        private static ReleaseIdentity Release(string version, string fingerprint)
        {
            return new ReleaseIdentity(version, fingerprint);
        }

        private static AtomicTransactionJournalStore Store(string root)
        {
            return new AtomicTransactionJournalStore(
                Path.Combine(root, "transaction.json"));
        }

        private static string NewRoot()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "SBMS-installer-transaction-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void DeleteRoot(string root)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        private static void AssertNoJournal(AtomicTransactionJournalStore store)
        {
            Assert(
                !File.Exists(store.JournalPath) &&
                !File.Exists(store.BackupPath),
                "Rejected request wrote a journal.");
        }

        private static void AssertRolledBack(
            InstallerTransactionEngine engine,
            InstallerTransactionRequest request)
        {
            bool rolledBack = false;
            try
            {
                engine.Execute(request);
            }
            catch (TransactionRolledBackException)
            {
                rolledBack = true;
            }
            Assert(rolledBack, "Expected verified rollback.");
        }

        private static void AssertCrash(
            InstallerTransactionEngine engine,
            InstallerTransactionRequest request)
        {
            bool crashed = false;
            try
            {
                engine.Execute(request);
            }
            catch (SimulatedTransactionProcessCrashException)
            {
                crashed = true;
            }
            Assert(crashed, "Expected simulated process crash.");
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
                ++passed;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                ++failed;
                Console.WriteLine("FAIL " + name + ": " + ex);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    message + " expected=" + expected + " actual=" + actual);
            }
        }

        private static void AssertAnyThrows(Action action, string message)
        {
            bool threw = false;
            try
            {
                action();
            }
            catch (Exception)
            {
                threw = true;
            }
            Assert(threw, message);
        }
    }
}
