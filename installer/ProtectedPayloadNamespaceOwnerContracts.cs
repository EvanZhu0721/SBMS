using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;

namespace SBMSSetup
{
    // ACL scope: standard users and non-broker processes are denied payload
    // mutation. An already-elevated administrator or LocalSystem process can
    // take ownership or stop/debug the broker and is outside this ACL threat
    // model. Any observed ACL, owner, marker, reparse, or identity drift must
    // fail closed; these contracts do not authorize automatic repair.
    internal static class PayloadNamespaceOwnerThreatModel
    {
        internal const string ProtectedBoundary =
            "StandardUserOrNonBrokerProcess";
        internal const string OutOfAclScopeBoundary =
            "ElevatedAdministratorOrLocalSystem";
        internal const string DriftDisposition = "FailClosed";
    }

    // Paths never enter a checkpoint, plan, broker command, or purge record.
    // The service derives these paths locally from the machine Program Files.
    internal static class PayloadManagedNamespaceLocation
    {
        internal const string ProductionNamespaceId =
            "SBMS.ProgramFiles.App.v1";
        internal const string ProductLeaf = "SBMS";
        internal const string ManagedLeaf = "App";
        internal const string StableServiceLeaf = "Service";

        internal static string ProgramFilesRoot
        {
            get
            {
                string root = Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles);
                WindowsPathSafety.RequireCanonicalFullyQualified(
                    root,
                    "Local Program Files root");
                return Trim(root);
            }
        }

        internal static string ManagedParentPath
        {
            get { return Path.Combine(ProgramFilesRoot, ProductLeaf); }
        }

        internal static string ManagedRootPath
        {
            get
            {
                return Path.Combine(
                    ManagedParentPath,
                    ManagedLeaf);
            }
        }

        internal static string StableServiceRootPath
        {
            get
            {
                return Path.Combine(
                    ManagedParentPath,
                    StableServiceLeaf);
            }
        }

        internal static void RequireManagedRootExact(string path)
        {
            WindowsPathSafety.RequireCanonicalFullyQualified(
                path,
                "Managed payload root");
            if (!String.Equals(
                    path,
                    ManagedRootPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Payload owner accepts only the local " +
                    "%ProgramFiles%\\SBMS\\App root.");
            }
        }

        private static string Trim(string path)
        {
            string root = Path.GetPathRoot(path);
            while (path.Length > root.Length &&
                (path[path.Length - 1] ==
                    Path.DirectorySeparatorChar ||
                 path[path.Length - 1] ==
                    Path.AltDirectorySeparatorChar))
            {
                path = path.Substring(0, path.Length - 1);
            }
            return path;
        }
    }

    [DataContract]
    internal sealed class PayloadNamespaceSecurityProfile
    {
        internal const string ProductionPolicyId =
            "SBMS.ProgramFiles.ServiceSid.v1";
        internal const string BrokerServiceName =
            "SBMSMaintenanceService";
        internal const string OwnerPrincipal = "LocalSystem";
        internal const string SystemRights = "ReadAndExecute";
        internal const string BrokerServiceRights = "FullControl";
        internal const string AdministratorsRights = "ReadAndExecute";
        internal const string StandardUsersRights = "ReadAndExecute";

        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string PolicyId;

        [DataMember(Order = 3, IsRequired = true)]
        internal string ServiceName;

        [DataMember(Order = 4, IsRequired = true)]
        internal string Owner;

        [DataMember(Order = 5, IsRequired = true)]
        internal string LocalSystemAccess;

        [DataMember(Order = 6, IsRequired = true)]
        internal string ServiceAccess;

        [DataMember(Order = 7, IsRequired = true)]
        internal string AdministratorsAccess;

        [DataMember(Order = 8, IsRequired = true)]
        internal string UsersAccess;

        [DataMember(Order = 9, IsRequired = true)]
        internal bool InheritanceProtected;

        [DataMember(Order = 10, IsRequired = true)]
        internal bool ContainerInherit;

        [DataMember(Order = 11, IsRequired = true)]
        internal bool ObjectInherit;

        [DataMember(Order = 12, IsRequired = true)]
        internal string PolicyDigest;

        // The service SID is derived locally from ServiceName. No SID string
        // is accepted from serialized state or broker input.
        internal void Validate()
        {
            if (SchemaVersion != 1 ||
                !String.Equals(
                    PolicyId,
                    ProductionPolicyId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    ServiceName,
                    BrokerServiceName,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    Owner,
                    OwnerPrincipal,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    LocalSystemAccess,
                    SystemRights,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    ServiceAccess,
                    BrokerServiceRights,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    AdministratorsAccess,
                    AdministratorsRights,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    UsersAccess,
                    StandardUsersRights,
                    StringComparison.Ordinal) ||
                !InheritanceProtected ||
                !ContainerInherit ||
                !ObjectInherit ||
                !String.Equals(
                    PolicyDigest,
                    ComputePolicyDigest(),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload namespace security policy differs from the " +
                    "fixed service-SID production semantics.");
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadNamespaceSecurityProfile.v2",
                    CanonicalPolicyFields(PolicyDigest));
            }
        }

        internal static string ComputePolicyDigest()
        {
            return PayloadContractValidation.ComputeDigest(
                "SBMS.PayloadNamespaceSecurityPolicyMaterial.v1",
                CanonicalPolicyFields(String.Empty));
        }

        internal static PayloadNamespaceSecurityProfile Production()
        {
            return new PayloadNamespaceSecurityProfile
            {
                SchemaVersion = 1,
                PolicyId = ProductionPolicyId,
                ServiceName = BrokerServiceName,
                Owner = OwnerPrincipal,
                LocalSystemAccess = SystemRights,
                ServiceAccess = BrokerServiceRights,
                AdministratorsAccess = AdministratorsRights,
                UsersAccess = StandardUsersRights,
                InheritanceProtected = true,
                ContainerInherit = true,
                ObjectInherit = true,
                PolicyDigest = ComputePolicyDigest()
            };
        }

        internal PayloadNamespaceSecurityProfile DeepClone()
        {
            return new PayloadNamespaceSecurityProfile
            {
                SchemaVersion = SchemaVersion,
                PolicyId = PolicyId,
                ServiceName = ServiceName,
                Owner = Owner,
                LocalSystemAccess = LocalSystemAccess,
                ServiceAccess = ServiceAccess,
                AdministratorsAccess = AdministratorsAccess,
                UsersAccess = UsersAccess,
                InheritanceProtected = InheritanceProtected,
                ContainerInherit = ContainerInherit,
                ObjectInherit = ObjectInherit,
                PolicyDigest = PolicyDigest
            };
        }

        private static string[] CanonicalPolicyFields(
            string policyDigest)
        {
            return new[]
            {
                "1",
                ProductionPolicyId,
                BrokerServiceName,
                OwnerPrincipal,
                SystemRights,
                BrokerServiceRights,
                AdministratorsRights,
                StandardUsersRights,
                Boolean.TrueString,
                Boolean.TrueString,
                Boolean.TrueString,
                policyDigest
            };
        }
    }

    internal enum PayloadNamespaceOwnershipPhase
    {
        Absent,
        ProvisionArmed,
        Present,
        RemoveArmed,
        ObservedAbsent
    }

    [DataContract]
    internal sealed class PayloadNamespaceOwnershipCasToken
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string NamespaceId;

        [DataMember(Order = 3, IsRequired = true)]
        internal long OwnershipRevision;

        [DataMember(Order = 4, IsRequired = true)]
        internal string CheckpointInvariantDigest;

        internal void Validate()
        {
            if (SchemaVersion != 1 || OwnershipRevision < 0)
            {
                throw new InvalidOperationException(
                    "Payload namespace ownership CAS is incomplete.");
            }
            RequireProductionNamespaceId(NamespaceId);
            PayloadContractValidation.RequireSha256(
                CheckpointInvariantDigest,
                "Payload namespace checkpoint digest");
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadNamespaceOwnershipCasToken.v2",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        NamespaceId,
                        OwnershipRevision.ToString(
                            CultureInfo.InvariantCulture),
                        CheckpointInvariantDigest
                    });
            }
        }

        internal PayloadNamespaceOwnershipCasToken DeepClone()
        {
            return new PayloadNamespaceOwnershipCasToken
            {
                SchemaVersion = SchemaVersion,
                NamespaceId = NamespaceId,
                OwnershipRevision = OwnershipRevision,
                CheckpointInvariantDigest = CheckpointInvariantDigest
            };
        }

        internal static void RequireProductionNamespaceId(string value)
        {
            if (!String.Equals(
                    value,
                    PayloadManagedNamespaceLocation.
                        ProductionNamespaceId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload namespace ID is not the fixed production namespace.");
            }
        }
    }

    [DataContract]
    internal sealed class PayloadNamespaceOwnershipCheckpoint
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal long OwnershipRevision;

        [DataMember(Order = 3, IsRequired = true)]
        internal string NamespaceId;

        [DataMember(Order = 4, IsRequired = true)]
        internal PayloadNamespaceOwnershipPhase Phase;

        [DataMember(Order = 5, IsRequired = true)]
        internal PayloadNamespaceSecurityProfile SecurityProfile;

        [DataMember(Order = 6, IsRequired = true)]
        internal string ActiveTransactionId;

        [DataMember(Order = 7, IsRequired = true)]
        internal string ActiveIntentId;

        [DataMember(Order = 8, IsRequired = true)]
        internal string ExpectedWorkspaceCasInvariantDigest;

        [DataMember(Order = 9, IsRequired = true)]
        internal string OwnershipMarkerDigest;

        [DataMember(Order = 10, IsRequired = true)]
        internal ulong RootVolumeSerialNumber;

        [DataMember(Order = 11, IsRequired = true)]
        internal string RootFileId;

        [DataMember(Order = 12, IsRequired = true)]
        internal string LastObservationInvariantDigest;

        internal void Validate()
        {
            if (SchemaVersion != 2 ||
                OwnershipRevision < 0 ||
                SecurityProfile == null ||
                !Enum.IsDefined(
                    typeof(PayloadNamespaceOwnershipPhase),
                    Phase))
            {
                throw new InvalidOperationException(
                    "Payload namespace ownership checkpoint is incomplete.");
            }
            PayloadNamespaceOwnershipCasToken.
                RequireProductionNamespaceId(NamespaceId);
            SecurityProfile.Validate();

            bool armed =
                Phase == PayloadNamespaceOwnershipPhase.ProvisionArmed ||
                Phase == PayloadNamespaceOwnershipPhase.RemoveArmed;
            bool identity =
                Phase == PayloadNamespaceOwnershipPhase.Present ||
                Phase == PayloadNamespaceOwnershipPhase.RemoveArmed ||
                Phase == PayloadNamespaceOwnershipPhase.ObservedAbsent;
            bool marker =
                Phase != PayloadNamespaceOwnershipPhase.Absent;
            bool observation =
                Phase == PayloadNamespaceOwnershipPhase.Present ||
                Phase == PayloadNamespaceOwnershipPhase.RemoveArmed ||
                Phase == PayloadNamespaceOwnershipPhase.ObservedAbsent;

            RequireOptionalId(
                ActiveTransactionId,
                armed,
                "Active payload namespace transaction ID");
            RequireOptionalId(
                ActiveIntentId,
                armed,
                "Active payload namespace intent ID");
            RequireOptionalDigest(
                ExpectedWorkspaceCasInvariantDigest,
                armed,
                "Expected payload workspace CAS digest");
            RequireOptionalDigest(
                OwnershipMarkerDigest,
                marker,
                "Payload namespace ownership marker digest");
            if (identity)
            {
                if (RootVolumeSerialNumber == 0)
                {
                    throw new InvalidOperationException(
                        "Payload namespace root volume identity is missing.");
                }
                PayloadContractValidation.RequireFileId(
                    RootFileId,
                    "Payload namespace root file ID");
            }
            else if (RootVolumeSerialNumber != 0 ||
                !String.IsNullOrEmpty(RootFileId))
            {
                throw new InvalidOperationException(
                    "Payload namespace phase carries an illegal root identity.");
            }
            RequireOptionalDigest(
                LastObservationInvariantDigest,
                observation,
                "Payload namespace observation digest");
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadNamespaceOwnershipCheckpoint.v2",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        OwnershipRevision.ToString(
                            CultureInfo.InvariantCulture),
                        NamespaceId,
                        Phase.ToString(),
                        SecurityProfile.InvariantDigest,
                        ActiveTransactionId,
                        ActiveIntentId,
                        ExpectedWorkspaceCasInvariantDigest,
                        OwnershipMarkerDigest,
                        RootVolumeSerialNumber.ToString(
                            "x16",
                            CultureInfo.InvariantCulture),
                        RootFileId,
                        LastObservationInvariantDigest
                    });
            }
        }

        internal PayloadNamespaceOwnershipCasToken CasToken
        {
            get
            {
                return new PayloadNamespaceOwnershipCasToken
                {
                    SchemaVersion = 1,
                    NamespaceId = NamespaceId,
                    OwnershipRevision = OwnershipRevision,
                    CheckpointInvariantDigest = InvariantDigest
                };
            }
        }

        internal PayloadNamespaceOwnershipCheckpoint DeepClone()
        {
            return new PayloadNamespaceOwnershipCheckpoint
            {
                SchemaVersion = SchemaVersion,
                OwnershipRevision = OwnershipRevision,
                NamespaceId = NamespaceId,
                Phase = Phase,
                SecurityProfile =
                    SecurityProfile == null
                        ? null
                        : SecurityProfile.DeepClone(),
                ActiveTransactionId = ActiveTransactionId,
                ActiveIntentId = ActiveIntentId,
                ExpectedWorkspaceCasInvariantDigest =
                    ExpectedWorkspaceCasInvariantDigest,
                OwnershipMarkerDigest = OwnershipMarkerDigest,
                RootVolumeSerialNumber = RootVolumeSerialNumber,
                RootFileId = RootFileId,
                LastObservationInvariantDigest =
                    LastObservationInvariantDigest
            };
        }

        private static void RequireOptionalId(
            string value,
            bool required,
            string description)
        {
            if (required)
            {
                PayloadContractValidation.RequireCanonicalTransactionId(
                    value,
                    description);
            }
            else if (!String.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(
                    description + " is legal only while Armed.");
            }
        }

        private static void RequireOptionalDigest(
            string value,
            bool required,
            string description)
        {
            if (required)
            {
                PayloadContractValidation.RequireSha256(value, description);
            }
            else if (!String.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(
                    description + " is illegal in this ownership phase.");
            }
        }
    }

    internal enum PayloadNamespaceOwnershipTransition
    {
        ProvisionArm,
        ProvisionObservePresent,
        RemoveArm,
        RemoveObserveAbsent
    }

    [DataContract]
    internal sealed class PayloadNamespaceOwnershipPlan
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal PayloadNamespaceOwnershipTransition Transition;

        [DataMember(Order = 3, IsRequired = true)]
        internal string NamespaceId;

        [DataMember(Order = 4, IsRequired = true)]
        internal string ActiveTransactionId;

        [DataMember(Order = 5, IsRequired = true)]
        internal string ActiveIntentId;

        [DataMember(Order = 6, IsRequired = true)]
        internal PayloadNamespaceOwnershipCasToken BeforeOwnershipCas;

        [DataMember(Order = 7, IsRequired = true)]
        internal PayloadWorkspaceCasToken BeforeWorkspaceCas;

        [DataMember(Order = 8, IsRequired = true)]
        internal string SecurityProfileInvariantDigest;

        [DataMember(Order = 9, IsRequired = true)]
        internal string OwnershipMarkerDigest;

        [DataMember(Order = 10, IsRequired = true)]
        internal ulong BoundRootVolumeSerialNumber;

        [DataMember(Order = 11, IsRequired = true)]
        internal string BoundRootFileId;

        internal void Validate()
        {
            if (SchemaVersion != 2 ||
                !Enum.IsDefined(
                    typeof(PayloadNamespaceOwnershipTransition),
                    Transition) ||
                BeforeOwnershipCas == null ||
                BeforeWorkspaceCas == null)
            {
                throw new InvalidOperationException(
                    "Payload namespace ownership plan is incomplete.");
            }
            PayloadNamespaceOwnershipCasToken.
                RequireProductionNamespaceId(NamespaceId);
            PayloadContractValidation.RequireCanonicalTransactionId(
                ActiveTransactionId,
                "Payload namespace plan transaction ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                ActiveIntentId,
                "Payload namespace plan intent ID");
            BeforeOwnershipCas.Validate();
            BeforeWorkspaceCas.Validate();
            PayloadContractValidation.RequireSha256(
                SecurityProfileInvariantDigest,
                "Payload namespace plan security profile digest");
            PayloadContractValidation.RequireSha256(
                OwnershipMarkerDigest,
                "Payload namespace plan ownership marker digest");
            if (!String.Equals(
                    NamespaceId,
                    BeforeOwnershipCas.NamespaceId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    ActiveTransactionId,
                    BeforeWorkspaceCas.TransactionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload namespace plan CAS identities are foreign.");
            }
            bool remove =
                Transition == PayloadNamespaceOwnershipTransition.RemoveArm ||
                Transition ==
                    PayloadNamespaceOwnershipTransition.RemoveObserveAbsent;
            if (remove)
            {
                if (BoundRootVolumeSerialNumber == 0)
                {
                    throw new InvalidOperationException(
                        "Remove plan does not bind the existing root volume.");
                }
                PayloadContractValidation.RequireFileId(
                    BoundRootFileId,
                    "Remove plan root file ID");
            }
            else if (BoundRootVolumeSerialNumber != 0 ||
                !String.IsNullOrEmpty(BoundRootFileId))
            {
                throw new InvalidOperationException(
                    "Provision plan carries a preselected root identity.");
            }
        }

        internal void ValidateAgainst(
            PayloadNamespaceOwnershipCheckpoint before)
        {
            Validate();
            if (before == null)
            {
                throw new ArgumentNullException("before");
            }
            before.Validate();
            if (!SameOwnershipCas(
                    BeforeOwnershipCas,
                    before.CasToken) ||
                !String.Equals(
                    NamespaceId,
                    before.NamespaceId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    SecurityProfileInvariantDigest,
                    before.SecurityProfile.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload namespace plan is stale or foreign.");
            }

            PayloadNamespaceOwnershipPhase expectedPhase;
            switch (Transition)
            {
                case PayloadNamespaceOwnershipTransition.ProvisionArm:
                    if (before.Phase !=
                            PayloadNamespaceOwnershipPhase.Absent &&
                        before.Phase !=
                            PayloadNamespaceOwnershipPhase.ObservedAbsent)
                    {
                        throw InvalidTransition();
                    }
                    if (before.Phase ==
                            PayloadNamespaceOwnershipPhase.ObservedAbsent &&
                        String.Equals(
                            OwnershipMarkerDigest,
                            before.OwnershipMarkerDigest,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Provision must create a new marker after a tombstone.");
                    }
                    return;
                case PayloadNamespaceOwnershipTransition.
                    ProvisionObservePresent:
                    expectedPhase =
                        PayloadNamespaceOwnershipPhase.ProvisionArmed;
                    break;
                case PayloadNamespaceOwnershipTransition.RemoveArm:
                    expectedPhase =
                        PayloadNamespaceOwnershipPhase.Present;
                    break;
                case PayloadNamespaceOwnershipTransition.
                    RemoveObserveAbsent:
                    expectedPhase =
                        PayloadNamespaceOwnershipPhase.RemoveArmed;
                    break;
                default:
                    throw InvalidTransition();
            }
            if (before.Phase != expectedPhase)
            {
                throw InvalidTransition();
            }
            if (Transition ==
                    PayloadNamespaceOwnershipTransition.
                        ProvisionObservePresent ||
                Transition ==
                    PayloadNamespaceOwnershipTransition.
                        RemoveObserveAbsent)
            {
                if (!String.Equals(
                        ActiveTransactionId,
                        before.ActiveTransactionId,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        ActiveIntentId,
                        before.ActiveIntentId,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        BeforeWorkspaceCas.InvariantDigest,
                        before.ExpectedWorkspaceCasInvariantDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Observe plan does not bind the Armed authority.");
                }
            }
            if (Transition ==
                    PayloadNamespaceOwnershipTransition.RemoveArm ||
                Transition ==
                    PayloadNamespaceOwnershipTransition.
                        RemoveObserveAbsent)
            {
                if (!String.Equals(
                        OwnershipMarkerDigest,
                        before.OwnershipMarkerDigest,
                        StringComparison.Ordinal) ||
                    BoundRootVolumeSerialNumber !=
                        before.RootVolumeSerialNumber ||
                    !String.Equals(
                        BoundRootFileId,
                        before.RootFileId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Remove plan does not bind the existing namespace.");
                }
            }
            else if (Transition ==
                    PayloadNamespaceOwnershipTransition.
                        ProvisionObservePresent &&
                !String.Equals(
                    OwnershipMarkerDigest,
                    before.OwnershipMarkerDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Provision observation plan changed the Armed marker.");
            }
        }

        internal PayloadNamespaceOwnershipCheckpoint ApplyExact(
            PayloadNamespaceOwnershipCheckpoint before,
            PayloadNamespaceOwnershipObservation observation)
        {
            ValidateAgainst(before);
            bool observes =
                Transition ==
                    PayloadNamespaceOwnershipTransition.
                        ProvisionObservePresent ||
                Transition ==
                    PayloadNamespaceOwnershipTransition.
                        RemoveObserveAbsent;
            if (observes != (observation != null))
            {
                throw new InvalidOperationException(
                    "Ownership transition observation shape is invalid.");
            }
            if (observation != null)
            {
                observation.ValidateForPlan(this);
            }

            PayloadNamespaceOwnershipCheckpoint after =
                before.DeepClone();
            after.OwnershipRevision =
                checked(before.OwnershipRevision + 1);
            switch (Transition)
            {
                case PayloadNamespaceOwnershipTransition.ProvisionArm:
                    after.Phase =
                        PayloadNamespaceOwnershipPhase.ProvisionArmed;
                    after.ActiveTransactionId = ActiveTransactionId;
                    after.ActiveIntentId = ActiveIntentId;
                    after.ExpectedWorkspaceCasInvariantDigest =
                        BeforeWorkspaceCas.InvariantDigest;
                    after.OwnershipMarkerDigest =
                        OwnershipMarkerDigest;
                    after.RootVolumeSerialNumber = 0;
                    after.RootFileId = String.Empty;
                    after.LastObservationInvariantDigest =
                        String.Empty;
                    break;
                case PayloadNamespaceOwnershipTransition.
                    ProvisionObservePresent:
                    after.Phase =
                        PayloadNamespaceOwnershipPhase.Present;
                    ClearActive(after);
                    after.RootVolumeSerialNumber =
                        observation.RootVolumeSerialNumber;
                    after.RootFileId = observation.RootFileId;
                    after.LastObservationInvariantDigest =
                        observation.InvariantDigest;
                    break;
                case PayloadNamespaceOwnershipTransition.RemoveArm:
                    after.Phase =
                        PayloadNamespaceOwnershipPhase.RemoveArmed;
                    after.ActiveTransactionId = ActiveTransactionId;
                    after.ActiveIntentId = ActiveIntentId;
                    after.ExpectedWorkspaceCasInvariantDigest =
                        BeforeWorkspaceCas.InvariantDigest;
                    break;
                case PayloadNamespaceOwnershipTransition.
                    RemoveObserveAbsent:
                    after.Phase =
                        PayloadNamespaceOwnershipPhase.ObservedAbsent;
                    ClearActive(after);
                    after.LastObservationInvariantDigest =
                        observation.InvariantDigest;
                    break;
                default:
                    throw InvalidTransition();
            }
            after.Validate();
            if (after.OwnershipRevision !=
                checked(before.OwnershipRevision + 1))
            {
                throw new InvalidOperationException(
                    "Ownership revision did not advance exactly once.");
            }
            return after;
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadNamespaceOwnershipPlan.v2",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        Transition.ToString(),
                        NamespaceId,
                        ActiveTransactionId,
                        ActiveIntentId,
                        BeforeOwnershipCas.InvariantDigest,
                        BeforeWorkspaceCas.InvariantDigest,
                        SecurityProfileInvariantDigest,
                        OwnershipMarkerDigest,
                        BoundRootVolumeSerialNumber.ToString(
                            "x16",
                            CultureInfo.InvariantCulture),
                        BoundRootFileId
                    });
            }
        }

        private static void ClearActive(
            PayloadNamespaceOwnershipCheckpoint checkpoint)
        {
            checkpoint.ActiveTransactionId = String.Empty;
            checkpoint.ActiveIntentId = String.Empty;
            checkpoint.ExpectedWorkspaceCasInvariantDigest =
                String.Empty;
        }

        private static bool SameOwnershipCas(
            PayloadNamespaceOwnershipCasToken first,
            PayloadNamespaceOwnershipCasToken second)
        {
            return String.Equals(
                first.InvariantDigest,
                second.InvariantDigest,
                StringComparison.Ordinal);
        }

        private static InvalidOperationException InvalidTransition()
        {
            return new InvalidOperationException(
                "Payload namespace ownership transition is illegal.");
        }
    }

    [DataContract]
    internal sealed class PayloadNamespaceOwnershipObservation
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal PayloadNamespaceOwnershipTransition Transition;

        [DataMember(Order = 3, IsRequired = true)]
        internal string NamespaceId;

        [DataMember(Order = 4, IsRequired = true)]
        internal string ActiveTransactionId;

        [DataMember(Order = 5, IsRequired = true)]
        internal string ActiveIntentId;

        [DataMember(Order = 6, IsRequired = true)]
        internal string PlanInvariantDigest;

        [DataMember(Order = 7, IsRequired = true)]
        internal long ObservedAtArmedOwnershipRevision;

        [DataMember(Order = 8, IsRequired = true)]
        internal string OwnershipMarkerDigest;

        [DataMember(Order = 9, IsRequired = true)]
        internal ulong RootVolumeSerialNumber;

        [DataMember(Order = 10, IsRequired = true)]
        internal string RootFileId;

        [DataMember(Order = 11, IsRequired = true)]
        internal bool Exists;

        internal void Validate()
        {
            bool provision =
                Transition ==
                    PayloadNamespaceOwnershipTransition.
                        ProvisionObservePresent;
            bool remove =
                Transition ==
                    PayloadNamespaceOwnershipTransition.
                        RemoveObserveAbsent;
            if (SchemaVersion != 2 ||
                (!provision && !remove) ||
                ObservedAtArmedOwnershipRevision < 0 ||
                RootVolumeSerialNumber == 0 ||
                Exists != provision)
            {
                throw new InvalidOperationException(
                    "Payload namespace ownership observation is incomplete.");
            }
            PayloadNamespaceOwnershipCasToken.
                RequireProductionNamespaceId(NamespaceId);
            PayloadContractValidation.RequireCanonicalTransactionId(
                ActiveTransactionId,
                "Payload namespace observation transaction ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                ActiveIntentId,
                "Payload namespace observation intent ID");
            PayloadContractValidation.RequireSha256(
                PlanInvariantDigest,
                "Payload namespace observation plan digest");
            PayloadContractValidation.RequireSha256(
                OwnershipMarkerDigest,
                "Payload namespace observation marker digest");
            PayloadContractValidation.RequireFileId(
                RootFileId,
                "Payload namespace observation root file ID");
        }

        internal void ValidateForPlan(
            PayloadNamespaceOwnershipPlan plan)
        {
            Validate();
            if (plan == null)
            {
                throw new ArgumentNullException("plan");
            }
            plan.Validate();
            if (Transition != plan.Transition ||
                ObservedAtArmedOwnershipRevision !=
                    plan.BeforeOwnershipCas.OwnershipRevision ||
                !String.Equals(
                    NamespaceId,
                    plan.NamespaceId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    ActiveTransactionId,
                    plan.ActiveTransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    ActiveIntentId,
                    plan.ActiveIntentId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    PlanInvariantDigest,
                    plan.InvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    OwnershipMarkerDigest,
                    plan.OwnershipMarkerDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Ownership observation is foreign to the Armed plan.");
            }
            if (Transition ==
                    PayloadNamespaceOwnershipTransition.
                        RemoveObserveAbsent &&
                (RootVolumeSerialNumber !=
                    plan.BoundRootVolumeSerialNumber ||
                 !String.Equals(
                    RootFileId,
                    plan.BoundRootFileId,
                    StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "Removal observation changed the deleted root identity.");
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadNamespaceOwnershipObservation.v2",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        Transition.ToString(),
                        NamespaceId,
                        ActiveTransactionId,
                        ActiveIntentId,
                        PlanInvariantDigest,
                        ObservedAtArmedOwnershipRevision.ToString(
                            CultureInfo.InvariantCulture),
                        OwnershipMarkerDigest,
                        RootVolumeSerialNumber.ToString(
                            "x16",
                            CultureInfo.InvariantCulture),
                        RootFileId,
                        Exists.ToString(CultureInfo.InvariantCulture)
                    });
            }
        }
    }
}
