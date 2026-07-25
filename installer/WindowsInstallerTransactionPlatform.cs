using System;
using System.Collections.Generic;

namespace SBMSSetup
{
    internal interface IWindowsInstallerTrustVerifier
    {
        void VerifyTrustedSource(InstallerTransactionRequest request);
    }

    internal interface IWindowsInstallerEvidenceProbe
    {
        PayloadEvidence InspectPayload();
        IntegrationEvidence InspectIntegrations();
        ConfigurationEvidence InspectConfiguration();
        DisplayEvidence InspectDisplay();
        EscrowEvidence InspectEscrow();
    }

    internal interface IWindowsInstallerPreflightVerifier
    {
        void Verify(
            InstallerTransactionRequest request,
            InstallOperation operation,
            DisplayEvidence display);
    }

    internal interface IWindowsInstallerEscrowPlanner
    {
        string PlanEscrowLocator(string transactionId);
    }

    internal interface IWindowsInstallerRecoveryStateProbe
    {
        MachineSnapshot InspectForRecovery();
    }

    internal interface IWindowsInstallerMutationBackend
    {
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
        string FinalizeRolledBack(TransactionJournal journal);
    }

    internal sealed class DisabledWindowsInstallerMutationBackend :
        IWindowsInstallerMutationBackend
    {
        public void Apply(
            InstallerMutation mutation,
            ReleaseIdentity target,
            TransactionContext context)
        {
            throw Disabled();
        }

        public void VerifyApplied(
            InstallerMutation mutation,
            ReleaseIdentity target,
            TransactionContext context,
            MachineSnapshot observed)
        {
            throw Disabled();
        }

        public void ApplyCompensation(
            InstallerCompensationAction action,
            MachineSnapshot baseline,
            TransactionJournal journal,
            MachineSnapshot observed)
        {
            throw Disabled();
        }

        public void VerifyCompensation(
            InstallerCompensationAction action,
            MachineSnapshot baseline,
            TransactionJournal journal,
            MachineSnapshot observed)
        {
            throw Disabled();
        }

        public string FinalizeCommitted(TransactionJournal journal)
        {
            throw Disabled();
        }

        public string FinalizeRolledBack(TransactionJournal journal)
        {
            throw Disabled();
        }

        internal static InvalidOperationException Disabled()
        {
            return new InvalidOperationException(
                "Windows installer mutation backend is feature-gated and disabled.");
        }
    }

    internal sealed class WindowsInstallerReadOnlyState
    {
        private readonly IWindowsInstallerTrustVerifier trustVerifier;
        private readonly IWindowsInstallerEvidenceProbe evidenceProbe;
        private readonly IWindowsInstallerPreflightVerifier preflightVerifier;
        private readonly IWindowsInventoryProvider inventoryProvider;
        private readonly InstallerOwnershipPolicy ownershipPolicy;
        private readonly IWindowsInstallerRecoveryStateProbe recoveryProbe;

        internal WindowsInstallerReadOnlyState(
            IWindowsInstallerTrustVerifier trustVerifier,
            IWindowsInstallerEvidenceProbe evidenceProbe,
            IWindowsInstallerPreflightVerifier preflightVerifier,
            IWindowsInventoryProvider inventoryProvider,
            InstallerOwnershipPolicy ownershipPolicy)
            : this(
                trustVerifier,
                evidenceProbe,
                preflightVerifier,
                inventoryProvider,
                ownershipPolicy,
                null)
        {
        }

        internal WindowsInstallerReadOnlyState(
            IWindowsInstallerTrustVerifier trustVerifier,
            IWindowsInstallerEvidenceProbe evidenceProbe,
            IWindowsInstallerPreflightVerifier preflightVerifier,
            IWindowsInventoryProvider inventoryProvider,
            InstallerOwnershipPolicy ownershipPolicy,
            IWindowsInstallerRecoveryStateProbe recoveryProbe)
        {
            if (trustVerifier == null)
            {
                throw new ArgumentNullException("trustVerifier");
            }
            if (evidenceProbe == null)
            {
                throw new ArgumentNullException("evidenceProbe");
            }
            if (preflightVerifier == null)
            {
                throw new ArgumentNullException("preflightVerifier");
            }
            if (inventoryProvider == null)
            {
                throw new ArgumentNullException("inventoryProvider");
            }
            if (ownershipPolicy == null)
            {
                throw new ArgumentNullException("ownershipPolicy");
            }
            this.trustVerifier = trustVerifier;
            this.evidenceProbe = evidenceProbe;
            this.preflightVerifier = preflightVerifier;
            this.inventoryProvider = inventoryProvider;
            this.ownershipPolicy = ownershipPolicy;
            this.recoveryProbe = recoveryProbe;
        }

        internal void VerifyTrustedSource(
            InstallerTransactionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }
            trustVerifier.VerifyTrustedSource(request);
        }

        internal InstalledReleaseState InspectInstalledRelease()
        {
            PayloadEvidence payload = evidenceProbe.InspectPayload();
            if (payload == null)
            {
                throw new InvalidOperationException(
                    "Payload evidence probe returned no evidence.");
            }
            payload.Validate();
            var state = new InstalledReleaseState
            {
                IsInstalled = payload.Present,
                Release = payload.Present
                    ? new ReleaseIdentity(
                        payload.ReleaseVersion,
                        payload.PackageFingerprint)
                    : null
            };
            state.Validate();
            return state;
        }

        internal void Preflight(
            InstallerTransactionRequest request,
            InstallOperation operation)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }
            if (request.Flags == null ||
                (operation != InstallOperation.Uninstall &&
                 request.Target == null))
            {
                throw new InvalidOperationException(
                    "Installer request is incomplete.");
            }
            if (request.Target != null)
            {
                request.Target.Validate();
            }
            DisplayEvidence display = evidenceProbe.InspectDisplay();
            if (display == null)
            {
                throw new InvalidOperationException(
                    "Display evidence probe returned no evidence.");
            }
            display.Validate();
            preflightVerifier.Verify(request, operation, display);
        }

        internal MachineSnapshot Inspect()
        {
            PayloadEvidence payload = evidenceProbe.InspectPayload();
            IntegrationEvidence integrations =
                evidenceProbe.InspectIntegrations();
            ConfigurationEvidence configuration =
                evidenceProbe.InspectConfiguration();
            DisplayEvidence display = evidenceProbe.InspectDisplay();
            EscrowEvidence escrow = evidenceProbe.InspectEscrow();
            WindowsDriverInventory inventory =
                inventoryProvider.Inspect(ownershipPolicy);

            var snapshot = new MachineSnapshot
            {
                Payload = payload,
                Driver = WindowsDriverEvidenceMapper.Map(
                    inventory,
                    ownershipPolicy),
                Integrations = integrations,
                Configuration = configuration,
                Display = display,
                Escrow = escrow
            };
            // Do not require healthy display/device invariants here. The
            // engine applies strict Validate() on normal paths and the
            // degraded envelope on recovery paths.
            snapshot.ValidateForRecovery();
            return snapshot;
        }

        internal MachineSnapshot InspectForRecovery()
        {
            if (recoveryProbe == null)
            {
                throw new InvalidOperationException(
                    "A dedicated degraded-state recovery probe is required.");
            }
            MachineSnapshot snapshot = recoveryProbe.InspectForRecovery();
            if (snapshot == null)
            {
                throw new InvalidOperationException(
                    "Recovery state probe returned no evidence.");
            }
            snapshot.ValidateForRecovery();
            return snapshot;
        }

        internal static bool Equivalent(
            MachineSnapshot expected,
            MachineSnapshot actual)
        {
            if (expected == null || actual == null)
            {
                return false;
            }
            return String.Equals(
                expected.EvidenceDigest,
                actual.EvidenceDigest,
                StringComparison.Ordinal);
        }

        internal static bool EquivalentForRollback(
            MachineSnapshot expected,
            MachineSnapshot actual)
        {
            if (expected == null || actual == null)
            {
                return false;
            }
            MachineSnapshot normalized = SnapshotClone.Clone(actual);
            normalized.Escrow = new EscrowEvidence
            {
                ManifestPath = expected.Escrow.ManifestPath,
                ManifestSha256 = expected.Escrow.ManifestSha256,
                Complete = expected.Escrow.Complete,
                DriverPackageCount = expected.Escrow.DriverPackageCount,
                PayloadFileCount = expected.Escrow.PayloadFileCount,
                ConfigurationFileCount =
                    expected.Escrow.ConfigurationFileCount,
                IntegrationCount = expected.Escrow.IntegrationCount
            };
            return Equivalent(expected, normalized);
        }
    }

    internal sealed class WindowsInstallerTransactionPlatform :
        IInstallerTransactionPlatform
    {
        private readonly WindowsInstallerReadOnlyState state;
        private readonly IWindowsInstallerEscrowPlanner escrowPlanner;
        private readonly IWindowsInstallerMutationBackend mutationBackend;

        internal WindowsInstallerTransactionPlatform(
            WindowsInstallerReadOnlyState state,
            IWindowsInstallerEscrowPlanner escrowPlanner)
            : this(
                state,
                escrowPlanner,
                new DisabledWindowsInstallerMutationBackend())
        {
        }

        internal WindowsInstallerTransactionPlatform(
            WindowsInstallerReadOnlyState state,
            IWindowsInstallerEscrowPlanner escrowPlanner,
            IWindowsInstallerMutationBackend mutationBackend)
        {
            if (state == null)
            {
                throw new ArgumentNullException("state");
            }
            if (escrowPlanner == null)
            {
                throw new ArgumentNullException("escrowPlanner");
            }
            if (mutationBackend == null)
            {
                throw new ArgumentNullException("mutationBackend");
            }
            this.state = state;
            this.escrowPlanner = escrowPlanner;
            this.mutationBackend = mutationBackend;
        }

        public void VerifyTrustedSource(
            InstallerTransactionRequest request)
        {
            state.VerifyTrustedSource(request);
        }

        public InstalledReleaseState InspectInstalledRelease()
        {
            return state.InspectInstalledRelease();
        }

        public void Preflight(
            InstallerTransactionRequest request,
            InstallOperation operation)
        {
            state.Preflight(request, operation);
        }

        public string PlanEscrowLocator(string transactionId)
        {
            if (String.IsNullOrWhiteSpace(transactionId))
            {
                throw new ArgumentException(
                    "Transaction ID is required.",
                    "transactionId");
            }
            string locator =
                escrowPlanner.PlanEscrowLocator(transactionId);
            if (String.IsNullOrWhiteSpace(locator))
            {
                throw new InvalidOperationException(
                    "Escrow planner returned no locator.");
            }
            return locator;
        }

        public MachineSnapshot Inspect()
        {
            return state.Inspect();
        }

        public MachineSnapshot InspectForRecovery()
        {
            return state.InspectForRecovery();
        }

        public void Apply(
            InstallerMutation mutation,
            ReleaseIdentity target,
            TransactionContext context)
        {
            mutationBackend.Apply(mutation, target, context);
        }

        public void VerifyApplied(
            InstallerMutation mutation,
            ReleaseIdentity target,
            TransactionContext context,
            MachineSnapshot observed)
        {
            if (observed == null)
            {
                throw new ArgumentNullException("observed");
            }
            observed.ValidateForRecovery();
            mutationBackend.VerifyApplied(
                mutation,
                target,
                context,
                observed);
        }

        public void ApplyCompensation(
            InstallerCompensationAction action,
            MachineSnapshot baseline,
            TransactionJournal journal,
            MachineSnapshot observed)
        {
            ValidateCompensationInputs(baseline, journal, observed);
            mutationBackend.ApplyCompensation(
                action,
                baseline,
                journal,
                observed);
        }

        public void VerifyCompensation(
            InstallerCompensationAction action,
            MachineSnapshot baseline,
            TransactionJournal journal,
            MachineSnapshot observed)
        {
            ValidateCompensationInputs(baseline, journal, observed);
            mutationBackend.VerifyCompensation(
                action,
                baseline,
                journal,
                observed);
        }

        public string FinalizeCommitted(TransactionJournal journal)
        {
            if (journal == null)
            {
                throw new ArgumentNullException("journal");
            }
            return mutationBackend.FinalizeCommitted(journal);
        }

        public bool EquivalentForRollback(
            MachineSnapshot expected,
            MachineSnapshot actual)
        {
            return WindowsInstallerReadOnlyState.EquivalentForRollback(
                expected,
                actual);
        }

        public string FinalizeRolledBack(TransactionJournal journal)
        {
            if (journal == null)
            {
                throw new ArgumentNullException("journal");
            }
            return mutationBackend.FinalizeRolledBack(journal);
        }

        public bool Equivalent(
            MachineSnapshot expected,
            MachineSnapshot actual)
        {
            return WindowsInstallerReadOnlyState.Equivalent(
                expected,
                actual);
        }

        private static void ValidateCompensationInputs(
            MachineSnapshot baseline,
            TransactionJournal journal,
            MachineSnapshot observed)
        {
            if (baseline == null)
            {
                throw new ArgumentNullException("baseline");
            }
            if (journal == null)
            {
                throw new ArgumentNullException("journal");
            }
            if (observed == null)
            {
                throw new ArgumentNullException("observed");
            }
            baseline.Validate();
            observed.ValidateForRecovery();
        }
    }

    internal static class WindowsDriverEvidenceMapper
    {
        internal static DriverEvidence Map(
            WindowsDriverInventory inventory,
            InstallerOwnershipPolicy policy)
        {
            if (inventory == null)
            {
                throw new ArgumentNullException("inventory");
            }
            if (policy == null)
            {
                throw new ArgumentNullException("policy");
            }

            DriverPackageEvidence[] packages =
                InstallerOwnership.OwnedPackages(inventory, policy);
            DeviceInventoryEvidence[] devices =
                InstallerOwnership.OwnedResidualDevices(inventory, policy);
            Array.Sort(
                packages,
                delegate(
                    DriverPackageEvidence left,
                    DriverPackageEvidence right)
                {
                    return StringComparer.OrdinalIgnoreCase.Compare(
                        left.PublishedInf,
                        right.PublishedInf);
                });
            Array.Sort(
                devices,
                delegate(
                    DeviceInventoryEvidence left,
                    DeviceInventoryEvidence right)
                {
                    return StringComparer.OrdinalIgnoreCase.Compare(
                        left.InstanceId,
                        right.InstanceId);
                });

            string packageFingerprint = packages.Length == 0
                ? String.Empty
                : FingerprintPackages(packages);
            if (devices.Length == 0)
            {
                return new DriverEvidence
                {
                    Present = false,
                    PackagePresent = packages.Length != 0,
                    PackageSetFingerprint = packageFingerprint,
                    ActivePublishedInf = String.Empty,
                    BindingFingerprint = String.Empty,
                    DeviceInstanceFingerprint = String.Empty,
                    HasProblem = false,
                    ProblemCode = 0
                };
            }

            var packagesByPublished =
                new Dictionary<string, DriverPackageEvidence>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (DriverPackageEvidence package in packages)
            {
                packagesByPublished.Add(
                    package.PublishedInf,
                    package);
            }

            var published = new SortedSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var bindings = new List<string>();
            var instanceIds = new List<string>();
            int problemCode = 0;
            foreach (DeviceInventoryEvidence device in devices)
            {
                DriverPackageEvidence package;
                if (!packagesByPublished.TryGetValue(
                        device.BindingPublishedInf,
                        out package) ||
                    !String.Equals(
                        device.BindingContentIdentity,
                        package.ContentIdentity,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "An owned device is not bound to a strictly owned driver package.");
                }
                published.Add(package.PublishedInf);
                instanceIds.Add(device.InstanceId);
                bindings.Add(String.Join("|", new[]
                {
                    device.InstanceId,
                    package.PublishedInf,
                    package.ContentIdentity,
                    device.Service
                }));
                if (problemCode == 0 && device.ProblemCode != 0)
                {
                    problemCode = checked((int)device.ProblemCode);
                }
            }

            return new DriverEvidence
            {
                Present = true,
                PackagePresent = true,
                PackageSetFingerprint = packageFingerprint,
                ActivePublishedInf = String.Join("\n", published),
                BindingFingerprint =
                    WindowsInventoryProvider.Sha256Text(
                        String.Join("\n", bindings)),
                DeviceInstanceFingerprint =
                    WindowsInventoryProvider.Sha256Text(
                        String.Join("\n", instanceIds)),
                HasProblem = problemCode != 0,
                ProblemCode = problemCode
            };
        }

        private static string FingerprintPackages(
            DriverPackageEvidence[] packages)
        {
            var identities = new List<string>();
            foreach (DriverPackageEvidence package in packages)
            {
                identities.Add(String.Join("|", new[]
                {
                    package.PublishedInf,
                    package.ContentIdentity
                }));
            }
            return WindowsInventoryProvider.Sha256Text(
                String.Join("\n", identities));
        }
    }
}
