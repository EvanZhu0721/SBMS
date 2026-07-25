using System;

namespace SBMSSetup
{
    internal static class WindowsInstallerTransactionPlatformTests
    {
        private sealed class FakeTrustVerifier :
            IWindowsInstallerTrustVerifier
        {
            internal int Calls;

            public void VerifyTrustedSource(
                InstallerTransactionRequest request)
            {
                Calls++;
            }
        }

        private sealed class FakePreflightVerifier :
            IWindowsInstallerPreflightVerifier
        {
            internal int Calls;

            public void Verify(
                InstallerTransactionRequest request,
                InstallOperation operation,
                DisplayEvidence display)
            {
                Calls++;
            }
        }

        private sealed class FakeEvidenceProbe :
            IWindowsInstallerEvidenceProbe
        {
            internal PayloadEvidence Payload = AbsentPayload();
            internal DisplayEvidence Display = HealthyDisplay();
            internal bool ThrowOnDisplay;

            public PayloadEvidence InspectPayload()
            {
                return Payload;
            }

            public IntegrationEvidence InspectIntegrations()
            {
                return new IntegrationEvidence
                {
                    ShortcutFingerprint = "absent",
                    StartupTaskFingerprint = "absent"
                };
            }

            public ConfigurationEvidence InspectConfiguration()
            {
                return new ConfigurationEvidence
                {
                    SchemaVersion = "absent",
                    ContentFingerprint = "absent"
                };
            }

            public DisplayEvidence InspectDisplay()
            {
                if (ThrowOnDisplay)
                {
                    throw new InvalidOperationException(
                        "strict display capture unavailable");
                }
                return Display;
            }

            public EscrowEvidence InspectEscrow()
            {
                return new EscrowEvidence
                {
                    ManifestPath = String.Empty,
                    ManifestSha256 = String.Empty,
                    Complete = false
                };
            }
        }

        private sealed class FakeRecoveryStateProbe :
            IWindowsInstallerRecoveryStateProbe
        {
            internal int Calls;

            public MachineSnapshot InspectForRecovery()
            {
                Calls++;
                return DegradedSnapshot();
            }
        }

        private sealed class FakeInventoryProvider :
            IWindowsInventoryProvider
        {
            internal WindowsDriverInventory Inventory =
                new WindowsDriverInventory
                {
                    Packages = new DriverPackageEvidence[0],
                    Devices = new DeviceInventoryEvidence[0],
                    EvidenceDigest = "empty"
                };

            public WindowsDriverInventory Inspect(
                InstallerOwnershipPolicy policy)
            {
                return Inventory;
            }
        }

        private sealed class FakeEscrowPlanner :
            IWindowsInstallerEscrowPlanner
        {
            public string PlanEscrowLocator(string transactionId)
            {
                return @"C:\ProgramData\SBMS\Installer\transactions\" +
                    transactionId;
            }
        }

        private sealed class RecordingMutationBackend :
            IWindowsInstallerMutationBackend
        {
            internal int ApplyCalls;
            internal int VerifyAppliedCalls;
            internal int ApplyCompensationCalls;
            internal int VerifyCompensationCalls;
            internal int FinalizeCalls;
            internal int FinalizeRollbackCalls;

            public void Apply(
                InstallerMutation mutation,
                ReleaseIdentity target,
                TransactionContext context)
            {
                ApplyCalls++;
            }

            public void VerifyApplied(
                InstallerMutation mutation,
                ReleaseIdentity target,
                TransactionContext context,
                MachineSnapshot observed)
            {
                VerifyAppliedCalls++;
            }

            public void ApplyCompensation(
                InstallerCompensationAction action,
                MachineSnapshot baseline,
                TransactionJournal journal,
                MachineSnapshot observed)
            {
                ApplyCompensationCalls++;
            }

            public void VerifyCompensation(
                InstallerCompensationAction action,
                MachineSnapshot baseline,
                TransactionJournal journal,
                MachineSnapshot observed)
            {
                VerifyCompensationCalls++;
            }

            public string FinalizeCommitted(TransactionJournal journal)
            {
                FinalizeCalls++;
                return "finalized";
            }

            public string FinalizeRolledBack(TransactionJournal journal)
            {
                FinalizeRollbackCalls++;
                return "rollback-finalized";
            }
        }

        internal static int Main()
        {
            try
            {
                TestReadOnlyStateMapping();
                TestStagedDriverMapping();
                TestActiveDriverMapping();
                TestUnownedBindingFailsClosed();
                TestPreflightRequiresPhysicalDisplay();
                TestRecoveryInspectionAllowsDegradedDisplay();
                TestUninstallPreflightAllowsNullTarget();
                TestPlatformDelegation();
                TestDisabledBackend();
                Console.WriteLine(
                    "Windows installer transaction platform tests passed: 9");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                Console.Error.WriteLine(exception.StackTrace);
                return 1;
            }
        }

        private static void TestReadOnlyStateMapping()
        {
            DriverPackageEvidence package = Package();
            var trust = new FakeTrustVerifier();
            var evidence = new FakeEvidenceProbe();
            var preflight = new FakePreflightVerifier();
            var inventory = new FakeInventoryProvider();
            var state = new WindowsInstallerReadOnlyState(
                trust,
                evidence,
                preflight,
                inventory,
                Policy(package));
            InstallerTransactionRequest request = Request();

            state.VerifyTrustedSource(request);
            Assert(trust.Calls == 1, "Trust verifier was not called.");
            Assert(
                !state.InspectInstalledRelease().IsInstalled,
                "Absent payload was classified as installed.");

            evidence.Payload = new PayloadEvidence
            {
                Present = true,
                ReleaseVersion = "0.3.0",
                PackageFingerprint = new string('a', 64)
            };
            InstalledReleaseState installed =
                state.InspectInstalledRelease();
            Assert(
                installed.IsInstalled &&
                installed.Release.Version == "0.3.0",
                "Installed release was not derived from payload evidence.");

            MachineSnapshot first = state.Inspect();
            MachineSnapshot second = state.Inspect();
            Assert(
                WindowsInstallerReadOnlyState.Equivalent(first, second),
                "Equal snapshots were not equivalent.");
            second.Configuration.ContentFingerprint = "changed";
            Assert(
                !WindowsInstallerReadOnlyState.Equivalent(first, second),
                "Different snapshots were equivalent.");
        }

        private static void TestStagedDriverMapping()
        {
            DriverPackageEvidence package = Package();
            DriverEvidence evidence = WindowsDriverEvidenceMapper.Map(
                new WindowsDriverInventory
                {
                    Packages = new[] { package },
                    Devices = new DeviceInventoryEvidence[0]
                },
                Policy(package));
            evidence.Validate();
            Assert(
                evidence.PackagePresent &&
                !evidence.Present &&
                evidence.PackageSetFingerprint.Length == 64,
                "A staged driver package was not represented without a binding.");
        }

        private static void TestActiveDriverMapping()
        {
            DriverPackageEvidence package = Package();
            DriverEvidence evidence = WindowsDriverEvidenceMapper.Map(
                new WindowsDriverInventory
                {
                    Packages = new[] { package },
                    Devices = new[] { Device(package) }
                },
                Policy(package));
            evidence.Validate();
            Assert(
                evidence.PackagePresent &&
                evidence.Present &&
                evidence.ActivePublishedInf == package.PublishedInf &&
                evidence.BindingFingerprint.Length == 64 &&
                evidence.DeviceInstanceFingerprint.Length == 64,
                "An active owned binding was not mapped.");
        }

        private static void TestUnownedBindingFailsClosed()
        {
            DriverPackageEvidence package = Package();
            DeviceInventoryEvidence device = Device(package);
            device.BindingContentIdentity = new string('f', 64);
            AssertThrows(delegate
            {
                WindowsDriverEvidenceMapper.Map(
                    new WindowsDriverInventory
                    {
                        Packages = new[] { package },
                        Devices = new[] { device }
                    },
                    Policy(package));
            }, "strictly owned");
        }

        private static void TestPreflightRequiresPhysicalDisplay()
        {
            DriverPackageEvidence package = Package();
            var evidence = new FakeEvidenceProbe
            {
                Display = new DisplayEvidence
                {
                    ActivePhysicalPathCount = 0,
                    ActivePhysicalPathFingerprint = String.Empty
                }
            };
            var preflight = new FakePreflightVerifier();
            var state = new WindowsInstallerReadOnlyState(
                new FakeTrustVerifier(),
                evidence,
                preflight,
                new FakeInventoryProvider(),
                Policy(package));
            AssertThrows(delegate
            {
                state.Preflight(Request(), InstallOperation.FreshInstall);
            }, "physical display");
            Assert(
                preflight.Calls == 0,
                "Platform preflight ran after invalid display evidence.");
        }

        private static void TestRecoveryInspectionAllowsDegradedDisplay()
        {
            DriverPackageEvidence package = Package();
            var evidence = new FakeEvidenceProbe
            {
                ThrowOnDisplay = true,
                Display = new DisplayEvidence
                {
                    ActivePhysicalPathCount = 0,
                    ActivePhysicalPathFingerprint = String.Empty
                }
            };
            var recovery = new FakeRecoveryStateProbe();
            var state = new WindowsInstallerReadOnlyState(
                new FakeTrustVerifier(),
                evidence,
                new FakePreflightVerifier(),
                new FakeInventoryProvider(),
                Policy(package),
                recovery);
            AssertThrows(
                delegate { state.Inspect(); },
                "strict display capture");
            MachineSnapshot observed = state.InspectForRecovery();
            observed.ValidateForRecovery();
            Assert(recovery.Calls == 1,
                "Dedicated recovery probe was not used.");
            AssertThrows(
                delegate { observed.Validate(); },
                "physical display");
        }

        private static void TestUninstallPreflightAllowsNullTarget()
        {
            DriverPackageEvidence package = Package();
            var preflight = new FakePreflightVerifier();
            var state = new WindowsInstallerReadOnlyState(
                new FakeTrustVerifier(),
                new FakeEvidenceProbe(),
                preflight,
                new FakeInventoryProvider(),
                Policy(package));
            InstallerTransactionRequest request = Request();
            request.RequestedOperation = InstallOperationRequest.Uninstall;
            request.Target = null;
            state.Preflight(request, InstallOperation.Uninstall);
            Assert(preflight.Calls == 1,
                "Uninstall preflight rejected a targetless request.");
        }

        private static void TestDisabledBackend()
        {
            WindowsInstallerTransactionPlatform platform =
                Platform(null);
            MachineSnapshot snapshot = platform.Inspect();
            TransactionJournal journal = Journal(snapshot);
            AssertThrows(
                delegate
                {
                    platform.Apply(
                        InstallerMutation.StagePayload,
                        Request().Target,
                        journal.Context);
                },
                "feature-gated");
            AssertThrows(
                delegate
                {
                    platform.ApplyCompensation(
                        InstallerCompensationAction
                            .RemoveTransactionPayloadStaging,
                        snapshot,
                        journal,
                        snapshot);
                },
                "feature-gated");
            AssertThrows(
                delegate
                {
                    platform.FinalizeCommitted(journal);
                },
                "feature-gated");
            AssertThrows(
                delegate
                {
                    platform.FinalizeRolledBack(journal);
                },
                "feature-gated");
        }

        private static void TestPlatformDelegation()
        {
            var backend = new RecordingMutationBackend();
            WindowsInstallerTransactionPlatform platform =
                Platform(backend);
            MachineSnapshot snapshot = platform.Inspect();
            TransactionJournal journal = Journal(snapshot);

            Assert(
                platform.PlanEscrowLocator("tx-1").EndsWith(
                    @"\tx-1",
                    StringComparison.Ordinal),
                "Escrow planning was not delegated.");
            platform.Apply(
                InstallerMutation.StagePayload,
                Request().Target,
                journal.Context);
            platform.VerifyApplied(
                InstallerMutation.StagePayload,
                Request().Target,
                journal.Context,
                snapshot);
            platform.ApplyCompensation(
                InstallerCompensationAction
                    .RemoveTransactionPayloadStaging,
                snapshot,
                journal,
                snapshot);
            platform.VerifyCompensation(
                InstallerCompensationAction
                    .RemoveTransactionPayloadStaging,
                snapshot,
                journal,
                snapshot);
            Assert(
                platform.FinalizeCommitted(journal) == "finalized",
                "Finalization evidence was not returned.");
            Assert(
                platform.FinalizeRolledBack(journal) ==
                    "rollback-finalized",
                "Rollback finalization evidence was not returned.");
            Assert(
                backend.ApplyCalls == 1 &&
                backend.VerifyAppliedCalls == 1 &&
                backend.ApplyCompensationCalls == 1 &&
                backend.VerifyCompensationCalls == 1 &&
                backend.FinalizeCalls == 1 &&
                backend.FinalizeRollbackCalls == 1,
                "Mutation backend calls were not mapped exactly once.");
        }

        private static WindowsInstallerTransactionPlatform Platform(
            IWindowsInstallerMutationBackend backend)
        {
            DriverPackageEvidence package = Package();
            var state = new WindowsInstallerReadOnlyState(
                new FakeTrustVerifier(),
                new FakeEvidenceProbe(),
                new FakePreflightVerifier(),
                new FakeInventoryProvider(),
                Policy(package));
            return backend == null
                ? new WindowsInstallerTransactionPlatform(
                    state,
                    new FakeEscrowPlanner())
                : new WindowsInstallerTransactionPlatform(
                    state,
                    new FakeEscrowPlanner(),
                    backend);
        }

        private static TransactionJournal Journal(
            MachineSnapshot baseline)
        {
            const string transactionId =
                "11111111111111111111111111111111";
            return TransactionJournal.Create(
                transactionId,
                InstallOperation.FreshInstall,
                baseline,
                Request().Target,
                Request().Flags,
                @"C:\ProgramData\SBMS\Installer\transactions\" +
                    transactionId);
        }

        private static InstallerTransactionRequest Request()
        {
            return new InstallerTransactionRequest
            {
                RequestedOperation = InstallOperationRequest.Auto,
                Target = new ReleaseIdentity(
                    "0.3.0",
                    new string('a', 64)),
                Flags = new InstallerRequestFlags()
            };
        }

        private static PayloadEvidence AbsentPayload()
        {
            return new PayloadEvidence
            {
                Present = false,
                ReleaseVersion = String.Empty,
                PackageFingerprint = String.Empty
            };
        }

        private static DisplayEvidence HealthyDisplay()
        {
            return new DisplayEvidence
            {
                ActivePhysicalPathCount = 1,
                ActivePhysicalPathFingerprint = new string('d', 64)
            };
        }

        private static MachineSnapshot DegradedSnapshot()
        {
            return new MachineSnapshot
            {
                Payload = AbsentPayload(),
                Driver = new DriverEvidence
                {
                    Present = false,
                    PackagePresent = false,
                    PackageSetFingerprint = String.Empty,
                    ActivePublishedInf = String.Empty,
                    BindingFingerprint = String.Empty,
                    DeviceInstanceFingerprint = String.Empty
                },
                Integrations = new IntegrationEvidence
                {
                    ShortcutFingerprint = "unavailable",
                    StartupTaskFingerprint = "unavailable"
                },
                Configuration = new ConfigurationEvidence
                {
                    SchemaVersion = "unavailable",
                    ContentFingerprint = "unavailable"
                },
                Display = new DisplayEvidence
                {
                    ActivePhysicalPathCount = 0,
                    ActivePhysicalPathFingerprint = String.Empty
                },
                Escrow = new EscrowEvidence
                {
                    ManifestPath = String.Empty,
                    ManifestSha256 = String.Empty,
                    Complete = false
                }
            };
        }

        private static DriverPackageEvidence Package()
        {
            return new DriverPackageEvidence
            {
                PublishedInf = "oem56.inf",
                OriginalInf = "SBMSIndirectDisplay.inf",
                Provider = "SBMS",
                ClassName = "Display",
                ClassGuid =
                    "{4D36E968-E325-11CE-BFC1-08002BE10318}",
                DriverDateAndVersion = "07/25/2026 0.3.0.0",
                CatalogFile = "sbmsindirectdisplay.cat",
                Signer =
                    "Microsoft Windows Hardware Compatibility Publisher",
                WhcpVersion = "10.0",
                CatalogAttributes = new[] { "Declarative" },
                Files = new DriverFileEvidence[0],
                CatalogTrustVerified = true,
                CatalogMembershipVerified = true,
                TimestampVerified = true,
                SignerThumbprint = new string('2', 40),
                TimestampThumbprint = new string('3', 40),
                TimestampUtc = "2026-07-25T00:00:00.0000000+00:00",
                TimestampType = "RFC3161",
                TimestampOid =
                    WindowsDriverSignatureInspector.Rfc3161Oid,
                TimestampChainValid = true,
                TimestampChainStatus = "NoError",
                SignatureProvenance =
                    "WinVerifyTrust+CatalogMembership+Authenticode",
                ContentIdentity = new string('1', 64)
            };
        }

        private static DeviceInventoryEvidence Device(
            DriverPackageEvidence package)
        {
            return new DeviceInventoryEvidence
            {
                InstanceId = @"SWD\SBMS\VirtualDisplay-0001",
                Present = true,
                HardwareIds = new[] { @"SBMS\IndirectDisplay" },
                Service = "WUDFRd",
                BindingPublishedInf = package.PublishedInf,
                BindingContentIdentity = package.ContentIdentity,
                ContainerId =
                    "{00000000-0000-0000-0000-000000000001}",
                Parent = @"HTREE\ROOT\0",
                DevNodeStatus = 2,
                ProblemCode = 0
            };
        }

        private static InstallerOwnershipPolicy Policy(
            DriverPackageEvidence package)
        {
            return new InstallerOwnershipPolicy
            {
                OriginalInf = package.OriginalInf,
                Provider = package.Provider,
                ClassGuid = package.ClassGuid,
                Services = new[] { "WUDFRd" },
                InstanceIdPrefixes =
                    new[] { @"SWD\SBMS\", @"ROOT\SBMSINDIRECTDISPLAY\" },
                HardwareIds = new[]
                {
                    @"SBMS\IndirectDisplay",
                    @"Root\SBMSIndirectDisplay"
                },
                ContainerIds = new[]
                {
                    "{00000000-0000-0000-0000-000000000001}"
                },
                ParentInstanceIds = new[] { @"HTREE\ROOT\0" },
                ParentInstancePrefixes = new string[0],
                ExpectedPackageFiles = new[]
                {
                    "SBMSIndirectDisplay.inf",
                    "SBMSIndirectDisplay.dll",
                    "sbmsindirectdisplay.cat"
                },
                ApprovedPackageContentIdentities =
                    new[] { package.ContentIdentity },
                ApprovedSigners = new[] { package.Signer }
            };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertThrows(
            Action action,
            string messageFragment)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Assert(
                    exception.Message.IndexOf(
                        messageFragment,
                        StringComparison.OrdinalIgnoreCase) >= 0,
                    "Unexpected failure: " + exception.Message);
                return;
            }
            throw new InvalidOperationException(
                "Expected failure containing: " + messageFragment);
        }
    }
}
