using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SBMSSetup
{
    internal static class ProtectedPayloadNamespaceOwnerContractTests
    {
        private static readonly string NamespaceId =
            PayloadManagedNamespaceLocation.ProductionNamespaceId;
        private static readonly string TransactionA = Id(2);
        private static readonly string TransactionB = Id(3);
        private static readonly string TransactionC = Id(4);
        private static readonly string IntentA = Id(5);
        private static readonly string IntentB = Id(6);
        private static readonly string IntentC = Id(7);
        private static readonly string RequestId = Id(8);
        private static readonly string Marker = Digest('a');
        private static readonly string FileId = new string('b', 32);

        private static int failures;

        private static int Main()
        {
            Run("managed root is fixed locally", ManagedRootIsFixedLocally);
            Run("wire models have no path or SID", WireModelsHaveNoPathOrSid);
            Run("policy is fixed semantic material", PolicyIsFixedSemanticMaterial);
            Run("all persisted fields are required", PersistedFieldsAreRequired);
            Run("ownership lifecycle is monotonic", OwnershipLifecycleIsMonotonic);
            Run("illegal and stale transitions fail", IllegalAndStaleTransitionsFail);
            Run("remove binds existing identity", RemoveBindsExistingIdentity);
            Run("observations exact-bind Armed revision", ObservationsExactBindArmedRevision);
            Run("owner contracts round-trip", OwnerContractsRoundTrip);
            Run("broker operation matrix is exact", BrokerOperationMatrixIsExact);
            Run("broker response binds command", BrokerResponseBindsCommand);
            Run(
                "canonical replay entry is exact",
                CanonicalReplayEntryIsExact);
            Run("nonce semantics are correlation only", NonceIsCorrelationOnly);

            Console.WriteLine(
                failures == 0
                    ? "Protected payload namespace owner contract tests passed."
                    : failures + " protected payload namespace owner test(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        private static void ManagedRootIsFixedLocally()
        {
            PayloadManagedNamespaceLocation.RequireManagedRootExact(
                PayloadManagedNamespaceLocation.ManagedRootPath);
            Reject(delegate
            {
                PayloadManagedNamespaceLocation.RequireManagedRootExact(
                    PayloadManagedNamespaceLocation.ProgramFilesRoot);
            });
            Reject(delegate
            {
                PayloadManagedNamespaceLocation.RequireManagedRootExact(
                    PayloadManagedNamespaceLocation.ManagedParentPath);
            });
            Reject(delegate
            {
                PayloadManagedNamespaceLocation.RequireManagedRootExact(
                    PayloadManagedNamespaceLocation.StableServiceRootPath);
            });
            Reject(delegate
            {
                PayloadManagedNamespaceLocation.RequireManagedRootExact(
                    @"Z:\Program Files\SBMS\App");
            });
            Reject(delegate
            {
                PayloadManagedNamespaceLocation.RequireManagedRootExact(
                    @"\\server\share\SBMS\App");
            });
        }

        private static void WireModelsHaveNoPathOrSid()
        {
            Type[] wireTypes =
            {
                typeof(PayloadNamespaceOwnershipCheckpoint),
                typeof(PayloadNamespaceOwnershipPlan),
                typeof(PayloadNamespaceOwnershipObservation),
                typeof(PayloadBrokerCommand)
            };
            foreach (Type type in wireTypes)
            {
                foreach (FieldInfo field in type.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic))
                {
                    string name = field.Name.ToLowerInvariant();
                    Assert(
                        name.IndexOf("path", StringComparison.Ordinal) < 0 &&
                        name.IndexOf("sid", StringComparison.Ordinal) < 0,
                        type.Name + " exposes a path or SID field.");
                }
            }
            Assert(
                typeof(PayloadBrokerCommand).GetField(
                    "Receipt",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic) == null,
                "Broker command must not accept a receipt.");
        }

        private static void PolicyIsFixedSemanticMaterial()
        {
            PayloadNamespaceSecurityProfile profile =
                PayloadNamespaceSecurityProfile.Production();
            profile.Validate();
            Assert(
                profile.PolicyDigest ==
                    PayloadNamespaceSecurityProfile.ComputePolicyDigest(),
                "Policy digest is not reproducible.");
            Assert(
                profile.ServiceName == "SBMSMaintenanceService" &&
                profile.ServiceAccess == "FullControl" &&
                profile.LocalSystemAccess == "ReadAndExecute",
                "Production writer or SYSTEM rights changed.");

            string[] fields =
            {
                "PolicyId", "ServiceName", "Owner", "LocalSystemAccess",
                "ServiceAccess", "AdministratorsAccess", "UsersAccess"
            };
            foreach (string fieldName in fields)
            {
                PayloadNamespaceSecurityProfile changed =
                    profile.DeepClone();
                typeof(PayloadNamespaceSecurityProfile).GetField(
                    fieldName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic).SetValue(changed, "changed");
                Reject(changed.Validate);
            }
            PayloadNamespaceSecurityProfile inheritance =
                profile.DeepClone();
            inheritance.InheritanceProtected = false;
            Reject(inheritance.Validate);
            PayloadNamespaceSecurityProfile oldBroker =
                profile.DeepClone();
            oldBroker.ServiceName = "SBMSInstallerBroker";
            Reject(oldBroker.Validate);
            PayloadNamespaceSecurityProfile systemWriter =
                profile.DeepClone();
            systemWriter.LocalSystemAccess = "FullControl";
            Reject(systemWriter.Validate);
            Assert(
                PayloadNamespaceOwnerThreatModel.DriftDisposition ==
                    "FailClosed" &&
                PayloadNamespaceOwnerThreatModel.OutOfAclScopeBoundary ==
                    "ElevatedAdministratorOrLocalSystem",
                "Threat model boundary changed.");
        }

        private static void PersistedFieldsAreRequired()
        {
            Type[] types =
            {
                typeof(PayloadNamespaceSecurityProfile),
                typeof(PayloadNamespaceOwnershipCasToken),
                typeof(PayloadNamespaceOwnershipCheckpoint),
                typeof(PayloadNamespaceOwnershipPlan),
                typeof(PayloadNamespaceOwnershipObservation),
                typeof(PayloadBrokerCommand),
                typeof(PayloadBrokerOperationReceipt),
                typeof(PayloadBrokerResponse),
                typeof(PayloadBrokerReplayLedgerEntry)
            };
            foreach (Type type in types)
            {
                foreach (FieldInfo field in type.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic))
                {
                    DataMemberAttribute member =
                        (DataMemberAttribute)Attribute.GetCustomAttribute(
                            field,
                            typeof(DataMemberAttribute));
                    Assert(
                        member != null && member.IsRequired,
                        type.Name + "." + field.Name +
                        " is not a required wire field.");
                }
            }
        }

        private static void OwnershipLifecycleIsMonotonic()
        {
            PayloadNamespaceOwnershipCheckpoint absent = Absent();
            PayloadNamespaceOwnershipPlan provisionArm =
                Plan(
                    absent,
                    PayloadNamespaceOwnershipTransition.ProvisionArm,
                    TransactionA,
                    IntentA,
                    Workspace(TransactionA, 10),
                    Marker);
            PayloadNamespaceOwnershipCheckpoint armed =
                provisionArm.ApplyExact(absent, null);
            Assert(
                armed.Phase ==
                    PayloadNamespaceOwnershipPhase.ProvisionArmed &&
                armed.OwnershipRevision == 1,
                "Provision did not Arm exactly once.");

            PayloadNamespaceOwnershipPlan provisionObserve =
                Plan(
                    armed,
                    PayloadNamespaceOwnershipTransition.
                        ProvisionObservePresent,
                    TransactionA,
                    IntentA,
                    Workspace(TransactionA, 10),
                    Marker);
            PayloadNamespaceOwnershipObservation presentObservation =
                Observation(provisionObserve, true);
            PayloadNamespaceOwnershipCheckpoint present =
                provisionObserve.ApplyExact(
                    armed,
                    presentObservation);
            Assert(
                present.Phase == PayloadNamespaceOwnershipPhase.Present &&
                present.OwnershipRevision == 2 &&
                present.RootVolumeSerialNumber == 91 &&
                present.RootFileId == FileId &&
                present.ActiveTransactionId == String.Empty,
                "Provision observation was not consumed.");

            PayloadNamespaceOwnershipPlan removeArm =
                Plan(
                    present,
                    PayloadNamespaceOwnershipTransition.RemoveArm,
                    TransactionB,
                    IntentB,
                    Workspace(TransactionB, 20),
                    Marker);
            PayloadNamespaceOwnershipCheckpoint removing =
                removeArm.ApplyExact(present, null);
            Assert(
                removing.OwnershipRevision == 3 &&
                removing.ActiveTransactionId == TransactionB,
                "Present was not acquired by the next transaction.");

            PayloadNamespaceOwnershipPlan removeObserve =
                Plan(
                    removing,
                    PayloadNamespaceOwnershipTransition.
                        RemoveObserveAbsent,
                    TransactionB,
                    IntentB,
                    Workspace(TransactionB, 20),
                    Marker);
            PayloadNamespaceOwnershipCheckpoint tombstone =
                removeObserve.ApplyExact(
                    removing,
                    Observation(removeObserve, false));
            Assert(
                tombstone.Phase ==
                    PayloadNamespaceOwnershipPhase.ObservedAbsent &&
                tombstone.OwnershipRevision == 4 &&
                tombstone.RootVolumeSerialNumber == 91 &&
                tombstone.RootFileId == FileId &&
                tombstone.OwnershipMarkerDigest == Marker,
                "ObservedAbsent did not retain its retired identity.");

            PayloadNamespaceOwnershipPlan nextArm =
                Plan(
                    tombstone,
                    PayloadNamespaceOwnershipTransition.ProvisionArm,
                    TransactionC,
                    IntentC,
                    Workspace(TransactionC, 30),
                    Digest('c'));
            PayloadNamespaceOwnershipCheckpoint next =
                nextArm.ApplyExact(tombstone, null);
            Assert(
                next.OwnershipRevision == 5 &&
                next.ActiveTransactionId == TransactionC &&
                next.RootVolumeSerialNumber == 0,
                "New transaction did not advance beyond the tombstone.");
            PayloadNamespaceOwnershipPlan reusedMarker =
                Plan(
                    tombstone,
                    PayloadNamespaceOwnershipTransition.ProvisionArm,
                    TransactionC,
                    IntentC,
                    Workspace(TransactionC, 30),
                    tombstone.OwnershipMarkerDigest);
            Reject(delegate
            {
                reusedMarker.ValidateAgainst(tombstone);
            });
        }

        private static void IllegalAndStaleTransitionsFail()
        {
            PayloadNamespaceOwnershipCheckpoint absent = Absent();
            PayloadNamespaceOwnershipPlan illegal =
                Plan(
                    absent,
                    PayloadNamespaceOwnershipTransition.RemoveArm,
                    TransactionA,
                    IntentA,
                    Workspace(TransactionA, 1),
                    Marker);
            Reject(delegate { illegal.ValidateAgainst(absent); });

            PayloadNamespaceOwnershipPlan arm =
                Plan(
                    absent,
                    PayloadNamespaceOwnershipTransition.ProvisionArm,
                    TransactionA,
                    IntentA,
                    Workspace(TransactionA, 1),
                    Marker);
            PayloadNamespaceOwnershipCheckpoint armed =
                arm.ApplyExact(absent, null);
            Reject(delegate { arm.ApplyExact(armed, null); });

            PayloadNamespaceOwnershipPlan observe =
                Plan(
                    armed,
                    PayloadNamespaceOwnershipTransition.
                        ProvisionObservePresent,
                    TransactionA,
                    IntentA,
                    Workspace(TransactionA, 1),
                    Marker);
            observe.BeforeOwnershipCas.OwnershipRevision++;
            Reject(delegate { observe.ValidateAgainst(armed); });
            PayloadNamespaceOwnershipCheckpoint foreign = Absent();
            foreign.NamespaceId = "SBMS.ProgramFiles.App.parallel";
            Reject(foreign.Validate);
            PayloadNamespaceOwnershipCasToken foreignCas =
                absent.CasToken;
            foreignCas.NamespaceId = "SBMS.ProgramFiles.App.parallel";
            Reject(foreignCas.Validate);
            PayloadNamespaceOwnershipPlan foreignPlan =
                Plan(
                    absent,
                    PayloadNamespaceOwnershipTransition.ProvisionArm,
                    TransactionA,
                    IntentA,
                    Workspace(TransactionA, 1),
                    Marker);
            foreignPlan.NamespaceId =
                "SBMS.ProgramFiles.App.parallel";
            Reject(foreignPlan.Validate);
            PayloadBrokerCommand foreignCommand =
                Command(PayloadBrokerOperation.Inspect);
            foreignCommand.BeforeOwnershipCas.NamespaceId =
                "SBMS.ProgramFiles.App.parallel";
            Reject(foreignCommand.Validate);
        }

        private static void RemoveBindsExistingIdentity()
        {
            PayloadNamespaceOwnershipCheckpoint present = Present();
            PayloadNamespaceOwnershipPlan plan =
                Plan(
                    present,
                    PayloadNamespaceOwnershipTransition.RemoveArm,
                    TransactionB,
                    IntentB,
                    Workspace(TransactionB, 2),
                    Marker);
            plan.ValidateAgainst(present);
            plan.BoundRootVolumeSerialNumber++;
            Reject(delegate { plan.ValidateAgainst(present); });
            plan = Plan(
                present,
                PayloadNamespaceOwnershipTransition.RemoveArm,
                TransactionB,
                IntentB,
                Workspace(TransactionB, 2),
                Marker);
            plan.BoundRootFileId = new string('d', 32);
            Reject(delegate { plan.ValidateAgainst(present); });
            plan = Plan(
                present,
                PayloadNamespaceOwnershipTransition.RemoveArm,
                TransactionB,
                IntentB,
                Workspace(TransactionB, 2),
                Digest('e'));
            Reject(delegate { plan.ValidateAgainst(present); });
        }

        private static void ObservationsExactBindArmedRevision()
        {
            PayloadNamespaceOwnershipCheckpoint absent = Absent();
            PayloadNamespaceOwnershipPlan arm =
                Plan(
                    absent,
                    PayloadNamespaceOwnershipTransition.ProvisionArm,
                    TransactionA,
                    IntentA,
                    Workspace(TransactionA, 3),
                    Marker);
            PayloadNamespaceOwnershipCheckpoint armed =
                arm.ApplyExact(absent, null);
            PayloadNamespaceOwnershipPlan observe =
                Plan(
                    armed,
                    PayloadNamespaceOwnershipTransition.
                        ProvisionObservePresent,
                    TransactionA,
                    IntentA,
                    Workspace(TransactionA, 3),
                    Marker);
            PayloadNamespaceOwnershipObservation observation =
                Observation(observe, true);
            observation.ValidateForPlan(observe);
            observation.ObservedAtArmedOwnershipRevision--;
            Reject(delegate { observation.ValidateForPlan(observe); });
            observation = Observation(observe, true);
            observation.PlanInvariantDigest = Digest('f');
            Reject(delegate { observation.ValidateForPlan(observe); });
        }

        private static void OwnerContractsRoundTrip()
        {
            PayloadNamespaceOwnershipCheckpoint checkpoint = Present();
            PayloadNamespaceOwnershipCheckpoint copy =
                RoundTrip<PayloadNamespaceOwnershipCheckpoint>(
                    checkpoint);
            Assert(
                copy.InvariantDigest == checkpoint.InvariantDigest,
                "Checkpoint round-trip changed invariant.");
            PayloadNamespaceOwnershipPlan plan =
                Plan(
                    checkpoint,
                    PayloadNamespaceOwnershipTransition.RemoveArm,
                    TransactionB,
                    IntentB,
                    Workspace(TransactionB, 4),
                    Marker);
            PayloadNamespaceOwnershipPlan planCopy =
                RoundTrip<PayloadNamespaceOwnershipPlan>(plan);
            Assert(
                planCopy.InvariantDigest == plan.InvariantDigest,
                "Plan round-trip changed invariant.");
        }

        private static void BrokerOperationMatrixIsExact()
        {
            PayloadNamespaceOwnershipCasToken owner = Absent().CasToken;
            PayloadWorkspaceCasToken workspace =
                Workspace(TransactionA, 5);

            Receipt(
                PayloadBrokerOperation.Inspect,
                PayloadBrokerOwnershipTransitionTag.None,
                owner,
                owner.DeepClone(),
                workspace,
                workspace.DeepClone(),
                String.Empty).Validate();

            PayloadBrokerOperationReceipt badInspect =
                Receipt(
                    PayloadBrokerOperation.Inspect,
                    PayloadBrokerOwnershipTransitionTag.None,
                    owner,
                    OwnerAfter(owner, 1),
                    workspace,
                    workspace.DeepClone(),
                    String.Empty);
            Reject(badInspect.Validate);

            PayloadBrokerOperationReceipt advance =
                Receipt(
                    PayloadBrokerOperation.AdvancePayload,
                    PayloadBrokerOwnershipTransitionTag.None,
                    owner,
                    owner.DeepClone(),
                    workspace,
                    WorkspaceAfter(workspace, 1),
                    String.Empty);
            advance.Validate();
            advance.AfterWorkspaceCas = workspace.DeepClone();
            Reject(advance.Validate);
            advance.AfterWorkspaceCas = WorkspaceAfter(workspace, 2);
            Reject(advance.Validate);

            Receipt(
                PayloadBrokerOperation.AdvancePurge,
                PayloadBrokerOwnershipTransitionTag.None,
                owner,
                owner.DeepClone(),
                workspace,
                WorkspaceAfter(workspace, 1),
                String.Empty).Validate();

            Receipt(
                PayloadBrokerOperation.ProvisionNamespace,
                PayloadBrokerOwnershipTransitionTag.ProvisionArm,
                owner,
                OwnerAfter(owner, 1),
                workspace,
                workspace.DeepClone(),
                String.Empty).Validate();
            Receipt(
                PayloadBrokerOperation.ProvisionNamespace,
                PayloadBrokerOwnershipTransitionTag.
                    ProvisionObservePresent,
                owner,
                OwnerAfter(owner, 1),
                workspace,
                workspace.DeepClone(),
                Digest('1')).Validate();
            Receipt(
                PayloadBrokerOperation.RemoveNamespace,
                PayloadBrokerOwnershipTransitionTag.RemoveArm,
                owner,
                OwnerAfter(owner, 1),
                workspace,
                workspace.DeepClone(),
                String.Empty).Validate();
            Receipt(
                PayloadBrokerOperation.RemoveNamespace,
                PayloadBrokerOwnershipTransitionTag.
                    RemoveObserveAbsent,
                owner,
                OwnerAfter(owner, 1),
                workspace,
                workspace.DeepClone(),
                Digest('2')).Validate();

            PayloadBrokerOperationReceipt wrong =
                Receipt(
                    PayloadBrokerOperation.RemoveNamespace,
                    PayloadBrokerOwnershipTransitionTag.ProvisionArm,
                    owner,
                    OwnerAfter(owner, 1),
                    workspace,
                    workspace.DeepClone(),
                    String.Empty);
            Reject(wrong.Validate);

            PayloadBrokerCommand armCommand =
                Command(PayloadBrokerOperation.ProvisionNamespace);
            PayloadBrokerOperationReceipt armReceipt =
                Receipt(
                    PayloadBrokerOperation.ProvisionNamespace,
                    PayloadBrokerOwnershipTransitionTag.ProvisionArm,
                    armCommand.BeforeOwnershipCas,
                    OwnerAfter(armCommand.BeforeOwnershipCas, 1),
                    armCommand.BeforeWorkspaceCas,
                    armCommand.BeforeWorkspaceCas.DeepClone(),
                    String.Empty);
            armReceipt.AppliedPlanInvariantDigest = Digest('f');
            Reject(delegate
            {
                armReceipt.ValidateForCommand(armCommand);
            });
        }

        private static void BrokerResponseBindsCommand()
        {
            PayloadBrokerCommand command =
                Command(PayloadBrokerOperation.Inspect);
            PayloadBrokerOperationReceipt receipt =
                Receipt(
                    PayloadBrokerOperation.Inspect,
                    PayloadBrokerOwnershipTransitionTag.None,
                    command.BeforeOwnershipCas,
                    command.BeforeOwnershipCas.DeepClone(),
                    command.BeforeWorkspaceCas,
                    command.BeforeWorkspaceCas.DeepClone(),
                    String.Empty);
            PayloadBrokerResponse response =
                Response(command, receipt);
            response.ValidateForCommand(command);
            PayloadBrokerResponse copy =
                RoundTrip<PayloadBrokerResponse>(response);
            copy.ValidateForCommand(command);
            response.ResultInvariantDigest = Digest('3');
            Reject(response.Validate);

            response = Response(command, receipt);
            PayloadBrokerCommand foreign =
                Command(PayloadBrokerOperation.Inspect);
            foreign.RequestId = Id(9);
            Reject(delegate { response.ValidateForCommand(foreign); });

            PayloadBrokerCommand observedCommand =
                Command(PayloadBrokerOperation.ProvisionNamespace);
            PayloadBrokerOperationReceipt observedReceipt =
                Receipt(
                    PayloadBrokerOperation.ProvisionNamespace,
                    PayloadBrokerOwnershipTransitionTag.
                        ProvisionObservePresent,
                    observedCommand.BeforeOwnershipCas,
                    OwnerAfter(
                        observedCommand.BeforeOwnershipCas,
                        1),
                    observedCommand.BeforeWorkspaceCas,
                    observedCommand.BeforeWorkspaceCas.DeepClone(),
                    Digest('1'));
            Response(
                observedCommand,
                observedReceipt).ValidateForCommand(observedCommand);
            observedReceipt.Observation.PlanInvariantDigest =
                Digest('f');
            Reject(delegate
            {
                Response(
                    observedCommand,
                    observedReceipt).ValidateForCommand(
                        observedCommand);
            });
        }

        private static void CanonicalReplayEntryIsExact()
        {
            PayloadBrokerCommand command =
                Command(PayloadBrokerOperation.Inspect);
            PayloadBrokerOperationReceipt receipt =
                Receipt(
                    PayloadBrokerOperation.Inspect,
                    PayloadBrokerOwnershipTransitionTag.None,
                    command.BeforeOwnershipCas,
                    command.BeforeOwnershipCas.DeepClone(),
                    command.BeforeWorkspaceCas,
                    command.BeforeWorkspaceCas.DeepClone(),
                    String.Empty);
            PayloadBrokerResponse response = Response(command, receipt);
            byte[] bytes = response.GetCanonicalReplayBytes();
            PayloadBrokerResponse decoded =
                PayloadBrokerResponseCodec.DeserializeAndValidate(bytes);
            Assert(
                decoded.InvariantDigest == response.InvariantDigest &&
                decoded.Receipt.InvariantDigest ==
                    response.Receipt.InvariantDigest,
                "Canonical bytes did not decode to the typed response.");
            var entry = new PayloadBrokerReplayLedgerEntry
            {
                SchemaVersion = 1,
                TransactionId = command.TransactionId,
                RequestId = command.RequestId,
                CommandInvariantDigest = command.InvariantDigest,
                ResponseInvariantDigest = response.InvariantDigest,
                Response = response,
                CanonicalResponseBytes = bytes,
                CanonicalResponseBytesSha256 =
                    PayloadBrokerReplayLedgerEntry.
                        ComputeBytesSha256(bytes)
            };
            entry.ValidateRequest(command);
            entry.RequireByteEquivalentResult((byte[])bytes.Clone());
            PayloadBrokerReplayLedgerEntry copy =
                RoundTrip<PayloadBrokerReplayLedgerEntry>(entry);
            Assert(
                copy.InvariantDigest == entry.InvariantDigest,
                "Replay entry round-trip changed invariant.");
            Assert(
                BytesEqual(
                    entry.CanonicalResponseBytes,
                    entry.Response.GetCanonicalReplayBytes()),
                "Replay did not preserve first-response bytes.");
            Assert(
                typeof(PayloadBrokerResponse).GetField(
                    "Disposition",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic) == null,
                "Wire response still changes disposition on replay.");

            byte[] different = (byte[])bytes.Clone();
            different[different.Length - 1] ^= 1;
            Reject(delegate
            {
                entry.RequireByteEquivalentResult(different);
            });
            Reject(delegate
            {
                PayloadBrokerResponseCodec.
                    DeserializeAndValidate(different);
            });
            byte[] nonCanonical = new byte[bytes.Length + 1];
            Array.Copy(bytes, nonCanonical, bytes.Length);
            nonCanonical[nonCanonical.Length - 1] = (byte)' ';
            Reject(delegate
            {
                PayloadBrokerResponseCodec.
                    DeserializeAndValidate(nonCanonical);
            });

            PayloadBrokerReplayLedgerEntry crossed =
                RoundTrip<PayloadBrokerReplayLedgerEntry>(entry);
            crossed.Response.RequestId = Id(11);
            Reject(crossed.Validate);

            PayloadBrokerCommand advanceCommand =
                Command(PayloadBrokerOperation.AdvancePayload);
            PayloadBrokerOperationReceipt receiptA =
                Receipt(
                    PayloadBrokerOperation.AdvancePayload,
                    PayloadBrokerOwnershipTransitionTag.None,
                    advanceCommand.BeforeOwnershipCas,
                    advanceCommand.BeforeOwnershipCas.DeepClone(),
                    advanceCommand.BeforeWorkspaceCas,
                    WorkspaceAfter(
                        advanceCommand.BeforeWorkspaceCas,
                        1),
                    String.Empty);
            PayloadBrokerResponse responseA =
                Response(advanceCommand, receiptA);
            PayloadBrokerOperationReceipt receiptB =
                Receipt(
                    PayloadBrokerOperation.AdvancePayload,
                    PayloadBrokerOwnershipTransitionTag.None,
                    advanceCommand.BeforeOwnershipCas,
                    advanceCommand.BeforeOwnershipCas.DeepClone(),
                    advanceCommand.BeforeWorkspaceCas,
                    WorkspaceAfter(
                        advanceCommand.BeforeWorkspaceCas,
                        1),
                    String.Empty);
            receiptB.AfterWorkspaceCas.WorkspaceInvariantDigest =
                Digest('6');
            PayloadBrokerResponse responseB =
                Response(advanceCommand, receiptB);
            byte[] bytesB = responseB.GetCanonicalReplayBytes();
            var crossedBytes =
                new PayloadBrokerReplayLedgerEntry
                {
                    SchemaVersion = 1,
                    TransactionId = advanceCommand.TransactionId,
                    RequestId = advanceCommand.RequestId,
                    CommandInvariantDigest =
                        advanceCommand.InvariantDigest,
                    ResponseInvariantDigest =
                        responseA.InvariantDigest,
                    Response = responseA,
                    CanonicalResponseBytes = bytesB,
                    CanonicalResponseBytesSha256 =
                        PayloadBrokerReplayLedgerEntry.
                            ComputeBytesSha256(bytesB)
                };
            Reject(crossedBytes.Validate);

            PayloadBrokerCommand changed =
                Command(PayloadBrokerOperation.Inspect);
            changed.CorrelationNonceDigest = Digest('9');
            Reject(delegate { entry.ValidateRequest(changed); });
            changed = Command(PayloadBrokerOperation.Inspect);
            changed.RequestId = Id(10);
            Reject(delegate { entry.ValidateRequest(changed); });
        }

        private static void NonceIsCorrelationOnly()
        {
            Assert(
                PayloadBrokerProtocol.NonceSemantics ==
                    "CorrelationOnly",
                "Nonce was promoted to an authentication claim.");
            PayloadBrokerCommand first =
                Command(PayloadBrokerOperation.Inspect);
            PayloadBrokerCommand second =
                Command(PayloadBrokerOperation.Inspect);
            second.CorrelationNonceDigest = Digest('8');
            Assert(
                first.InvariantDigest != second.InvariantDigest,
                "Correlation value is not covered by command digest.");
        }

        private static PayloadNamespaceOwnershipCheckpoint Absent()
        {
            return new PayloadNamespaceOwnershipCheckpoint
            {
                SchemaVersion = 2,
                OwnershipRevision = 0,
                NamespaceId = NamespaceId,
                Phase = PayloadNamespaceOwnershipPhase.Absent,
                SecurityProfile =
                    PayloadNamespaceSecurityProfile.Production(),
                ActiveTransactionId = String.Empty,
                ActiveIntentId = String.Empty,
                ExpectedWorkspaceCasInvariantDigest = String.Empty,
                OwnershipMarkerDigest = String.Empty,
                RootVolumeSerialNumber = 0,
                RootFileId = String.Empty,
                LastObservationInvariantDigest = String.Empty
            };
        }

        private static PayloadNamespaceOwnershipCheckpoint Present()
        {
            PayloadNamespaceOwnershipCheckpoint checkpoint = Absent();
            checkpoint.OwnershipRevision = 2;
            checkpoint.Phase = PayloadNamespaceOwnershipPhase.Present;
            checkpoint.OwnershipMarkerDigest = Marker;
            checkpoint.RootVolumeSerialNumber = 91;
            checkpoint.RootFileId = FileId;
            checkpoint.LastObservationInvariantDigest = Digest('7');
            checkpoint.Validate();
            return checkpoint;
        }

        private static PayloadWorkspaceCasToken Workspace(
            string transactionId,
            long revision)
        {
            return new PayloadWorkspaceCasToken
            {
                SchemaVersion = 1,
                TransactionId = transactionId,
                Revision = revision,
                WorkspaceInvariantDigest = Digest('4')
            };
        }

        private static PayloadWorkspaceCasToken WorkspaceAfter(
            PayloadWorkspaceCasToken before,
            long delta)
        {
            return new PayloadWorkspaceCasToken
            {
                SchemaVersion = 1,
                TransactionId = before.TransactionId,
                Revision = before.Revision + delta,
                WorkspaceInvariantDigest = Digest('5')
            };
        }

        private static PayloadNamespaceOwnershipCasToken OwnerAfter(
            PayloadNamespaceOwnershipCasToken before,
            long delta)
        {
            return new PayloadNamespaceOwnershipCasToken
            {
                SchemaVersion = 1,
                NamespaceId = before.NamespaceId,
                OwnershipRevision =
                    before.OwnershipRevision + delta,
                CheckpointInvariantDigest = Digest('6')
            };
        }

        private static PayloadNamespaceOwnershipPlan Plan(
            PayloadNamespaceOwnershipCheckpoint before,
            PayloadNamespaceOwnershipTransition transition,
            string transactionId,
            string intentId,
            PayloadWorkspaceCasToken workspace,
            string marker)
        {
            bool remove =
                transition ==
                    PayloadNamespaceOwnershipTransition.RemoveArm ||
                transition ==
                    PayloadNamespaceOwnershipTransition.
                        RemoveObserveAbsent;
            return new PayloadNamespaceOwnershipPlan
            {
                SchemaVersion = 2,
                Transition = transition,
                NamespaceId = before.NamespaceId,
                ActiveTransactionId = transactionId,
                ActiveIntentId = intentId,
                BeforeOwnershipCas = before.CasToken,
                BeforeWorkspaceCas = workspace,
                SecurityProfileInvariantDigest =
                    before.SecurityProfile.InvariantDigest,
                OwnershipMarkerDigest = marker,
                BoundRootVolumeSerialNumber =
                    remove ? before.RootVolumeSerialNumber : 0,
                BoundRootFileId =
                    remove ? before.RootFileId : String.Empty
            };
        }

        private static PayloadNamespaceOwnershipObservation Observation(
            PayloadNamespaceOwnershipPlan plan,
            bool exists)
        {
            return new PayloadNamespaceOwnershipObservation
            {
                SchemaVersion = 2,
                Transition = plan.Transition,
                NamespaceId = plan.NamespaceId,
                ActiveTransactionId = plan.ActiveTransactionId,
                ActiveIntentId = plan.ActiveIntentId,
                PlanInvariantDigest = plan.InvariantDigest,
                ObservedAtArmedOwnershipRevision =
                    plan.BeforeOwnershipCas.OwnershipRevision,
                OwnershipMarkerDigest = plan.OwnershipMarkerDigest,
                RootVolumeSerialNumber =
                    plan.BoundRootVolumeSerialNumber == 0
                        ? 91
                        : plan.BoundRootVolumeSerialNumber,
                RootFileId =
                    String.IsNullOrEmpty(plan.BoundRootFileId)
                        ? FileId
                        : plan.BoundRootFileId,
                Exists = exists
            };
        }

        private static PayloadBrokerCommand Command(
            PayloadBrokerOperation operation)
        {
            return new PayloadBrokerCommand
            {
                SchemaVersion = 2,
                ProtocolVersion =
                    PayloadBrokerProtocol.ProtocolVersion,
                Operation = operation,
                TransactionId = TransactionA,
                RequestId = RequestId,
                CorrelationNonceDigest = Digest('0'),
                BeforeOwnershipCas = Absent().CasToken,
                BeforeWorkspaceCas = Workspace(TransactionA, 5),
                PlanInvariantDigest = Digest('3')
            };
        }

        private static PayloadBrokerOperationReceipt Receipt(
            PayloadBrokerOperation operation,
            PayloadBrokerOwnershipTransitionTag tag,
            PayloadNamespaceOwnershipCasToken beforeOwnership,
            PayloadNamespaceOwnershipCasToken afterOwnership,
            PayloadWorkspaceCasToken beforeWorkspace,
            PayloadWorkspaceCasToken afterWorkspace,
            string observation)
        {
            return new PayloadBrokerOperationReceipt
            {
                SchemaVersion = 2,
                Operation = operation,
                OwnershipTransitionTag = tag,
                BeforeOwnershipCas = beforeOwnership.DeepClone(),
                AfterOwnershipCas = afterOwnership.DeepClone(),
                BeforeWorkspaceCas = beforeWorkspace.DeepClone(),
                AfterWorkspaceCas = afterWorkspace.DeepClone(),
                Observation =
                    String.IsNullOrEmpty(observation)
                        ? null
                        : BrokerObservation(tag),
                AppliedPlanInvariantDigest = Digest('3')
            };
        }

        private static PayloadNamespaceOwnershipObservation BrokerObservation(
            PayloadBrokerOwnershipTransitionTag tag)
        {
            bool provision =
                tag ==
                    PayloadBrokerOwnershipTransitionTag.
                        ProvisionObservePresent;
            return new PayloadNamespaceOwnershipObservation
            {
                SchemaVersion = 2,
                Transition = provision
                    ? PayloadNamespaceOwnershipTransition.
                        ProvisionObservePresent
                    : PayloadNamespaceOwnershipTransition.
                        RemoveObserveAbsent,
                NamespaceId = NamespaceId,
                ActiveTransactionId = TransactionA,
                ActiveIntentId = IntentA,
                PlanInvariantDigest = Digest('3'),
                ObservedAtArmedOwnershipRevision = 0,
                OwnershipMarkerDigest = Marker,
                RootVolumeSerialNumber = 91,
                RootFileId = FileId,
                Exists = provision
            };
        }

        private static PayloadBrokerResponse Response(
            PayloadBrokerCommand command,
            PayloadBrokerOperationReceipt receipt)
        {
            return new PayloadBrokerResponse
            {
                SchemaVersion = 2,
                ProtocolVersion =
                    PayloadBrokerProtocol.ProtocolVersion,
                TransactionId = command.TransactionId,
                RequestId = command.RequestId,
                CommandInvariantDigest = command.InvariantDigest,
                Receipt = receipt,
                ResultInvariantDigest = receipt.InvariantDigest
            };
        }

        private static T RoundTrip<T>(T value)
        {
            return Deserialize<T>(Serialize(value));
        }

        private static string Serialize<T>(T value)
        {
            var serializer =
                new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Deserialize<T>(string json)
        {
            var serializer =
                new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(
                Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        private static string Id(int value)
        {
            return value.ToString("x32");
        }

        private static string Digest(char value)
        {
            return new string(value, 64);
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine(
                    "FAIL " + name + ": " + exception.Message);
            }
        }

        private static void Reject(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException)
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected contract rejection.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (first == null ||
                second == null ||
                first.Length != second.Length)
            {
                return false;
            }
            for (int index = 0; index < first.Length; ++index)
            {
                if (first[index] != second[index])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
