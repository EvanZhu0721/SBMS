using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Principal;
using System.Threading;

namespace SBMSSetup
{
    [Flags]
    internal enum MaintenanceTokenGroupAttributes : uint
    {
        None = 0,
        Enabled = 0x00000004,
        UseForDenyOnly = 0x00000010
    }

    internal enum MaintenanceTokenElevationType
    {
        Default,
        Full,
        Limited
    }

    internal enum MaintenanceClientTokenType
    {
        Primary,
        Impersonation
    }

    internal enum MaintenanceClientImpersonationLevel
    {
        Anonymous,
        Identification,
        Impersonation,
        Delegation
    }

    internal sealed class MaintenanceClientTokenGroupEvidence
    {
        internal MaintenanceClientTokenGroupEvidence(
            string sid,
            MaintenanceTokenGroupAttributes attributes)
        {
            Sid = CanonicalSid(sid, "Token group SID");
            Attributes = attributes;
        }

        internal string Sid { get; private set; }
        internal MaintenanceTokenGroupAttributes Attributes
        {
            get;
            private set;
        }

        internal bool IsEnabledAndNotDenyOnly
        {
            get
            {
                return
                    (Attributes &
                        MaintenanceTokenGroupAttributes.Enabled) != 0 &&
                    (Attributes &
                        MaintenanceTokenGroupAttributes.UseForDenyOnly) == 0;
            }
        }

        internal MaintenanceClientTokenGroupEvidence DeepClone()
        {
            return new MaintenanceClientTokenGroupEvidence(
                Sid,
                Attributes);
        }

        internal static string CanonicalSid(
            string sid,
            string label)
        {
            if (String.IsNullOrWhiteSpace(sid))
            {
                throw new ArgumentException(label + " is required.");
            }
            try
            {
                return new SecurityIdentifier(sid).Value;
            }
            catch (Exception exception)
            {
                throw new ArgumentException(
                    label + " is invalid.",
                    exception);
            }
        }
    }

    internal sealed class MaintenanceClientTokenEvidence
    {
        private readonly ReadOnlyCollection<
            MaintenanceClientTokenGroupEvidence> groups;

        internal MaintenanceClientTokenEvidence(
            string userSid,
            IEnumerable<MaintenanceClientTokenGroupEvidence> tokenGroups,
            bool elevated,
            MaintenanceTokenElevationType elevationType,
            int integrityRid,
            bool appContainer,
            bool restricted,
            MaintenanceClientTokenType tokenType,
            MaintenanceClientImpersonationLevel impersonationLevel,
            long authenticationId)
        {
            if (!Enum.IsDefined(
                    typeof(MaintenanceTokenElevationType),
                    elevationType))
            {
                throw new ArgumentOutOfRangeException("elevationType");
            }
            if (!Enum.IsDefined(
                    typeof(MaintenanceClientTokenType),
                    tokenType))
            {
                throw new ArgumentOutOfRangeException("tokenType");
            }
            if (!Enum.IsDefined(
                    typeof(MaintenanceClientImpersonationLevel),
                    impersonationLevel))
            {
                throw new ArgumentOutOfRangeException(
                    "impersonationLevel");
            }
            if (integrityRid < 0 || integrityRid > 0x00005000)
            {
                throw new ArgumentOutOfRangeException("integrityRid");
            }
            UserSid =
                MaintenanceClientTokenGroupEvidence.CanonicalSid(
                    userSid,
                    "Token user SID");
            if (tokenGroups == null)
            {
                throw new ArgumentNullException("tokenGroups");
            }
            var copied =
                new List<MaintenanceClientTokenGroupEvidence>();
            var seenSids =
                new HashSet<string>(StringComparer.Ordinal);
            foreach (MaintenanceClientTokenGroupEvidence group in
                tokenGroups)
            {
                if (group == null)
                {
                    throw new ArgumentException(
                        "Token group evidence contains null.");
                }
                MaintenanceClientTokenGroupEvidence clone =
                    group.DeepClone();
                if (!seenSids.Add(clone.Sid))
                {
                    throw new ArgumentException(
                        "Token group evidence contains a duplicate SID.");
                }
                copied.Add(clone);
            }
            groups = copied.AsReadOnly();
            IsElevated = elevated;
            ElevationType = elevationType;
            IntegrityRid = integrityRid;
            IsAppContainer = appContainer;
            IsRestricted = restricted;
            TokenType = tokenType;
            ImpersonationLevel = impersonationLevel;
            AuthenticationId = authenticationId;
        }

        internal string UserSid { get; private set; }
        internal IList<MaintenanceClientTokenGroupEvidence> Groups
        {
            get { return groups; }
        }
        internal bool IsElevated { get; private set; }
        internal MaintenanceTokenElevationType ElevationType
        {
            get;
            private set;
        }
        internal int IntegrityRid { get; private set; }
        internal bool IsAppContainer { get; private set; }
        internal bool IsRestricted { get; private set; }
        internal MaintenanceClientTokenType TokenType
        {
            get;
            private set;
        }
        internal MaintenanceClientImpersonationLevel ImpersonationLevel
        {
            get;
            private set;
        }

        // Audit correlation only. Production authorization must not consult it.
        internal long AuthenticationId { get; private set; }

        internal bool HasEnabledGroup(string sid)
        {
            string canonical =
                MaintenanceClientTokenGroupEvidence.CanonicalSid(
                    sid,
                    "Required group SID");
            foreach (MaintenanceClientTokenGroupEvidence group in groups)
            {
                if (String.Equals(
                        group.Sid,
                        canonical,
                        StringComparison.Ordinal) &&
                    group.IsEnabledAndNotDenyOnly)
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal interface IMaintenanceClientPolicyAuthorizer
    {
        MaintenanceAuthorizationEvidence Authorize(
            MaintenanceClientTokenEvidence evidence,
            CancellationToken cancellation);
    }

    internal sealed class MaintenanceProductionClientPolicyAuthorizer
        : IMaintenanceClientPolicyAuthorizer
    {
        private const int HighIntegrityRid = 0x00003000;
        private const int SystemIntegrityRid = 0x00004000;
        private static readonly string LocalSystemSid =
            new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null).Value;
        private static readonly string AdministratorsSid =
            new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null).Value;
        private readonly string serviceSid;

        internal MaintenanceProductionClientPolicyAuthorizer()
        {
            serviceSid =
                ProtectedRootSecurityCompiler.DeriveServiceSid(
                    MaintenanceServiceIdentity.ServiceName);
        }

        public MaintenanceAuthorizationEvidence Authorize(
            MaintenanceClientTokenEvidence evidence,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (evidence == null ||
                evidence.TokenType !=
                    MaintenanceClientTokenType.Impersonation ||
                evidence.ImpersonationLevel <
                    MaintenanceClientImpersonationLevel.Impersonation ||
                evidence.IntegrityRid < HighIntegrityRid ||
                evidence.IsRestricted ||
                evidence.IsAppContainer)
            {
                throw Denied();
            }

            bool isLocalSystem =
                String.Equals(
                    evidence.UserSid,
                    LocalSystemSid,
                    StringComparison.Ordinal);
            bool isServiceIdentity =
                String.Equals(
                    evidence.UserSid,
                    serviceSid,
                    StringComparison.Ordinal) ||
                evidence.HasEnabledGroup(serviceSid);
            bool allowed =
                (isLocalSystem &&
                 evidence.IntegrityRid >= SystemIntegrityRid) ||
                isServiceIdentity ||
                (evidence.HasEnabledGroup(AdministratorsSid) &&
                 evidence.IsElevated &&
                 (evidence.ElevationType ==
                        MaintenanceTokenElevationType.Full ||
                  evidence.ElevationType ==
                        MaintenanceTokenElevationType.Default) &&
                 evidence.IntegrityRid >= HighIntegrityRid);
            if (!allowed)
            {
                throw Denied();
            }
            return MaintenanceAuthorizationEvidence.
                IssueForTrustedAdapter();
        }

        private static UnauthorizedAccessException Denied()
        {
            return new UnauthorizedAccessException(
                "Maintenance client token is not authorized.");
        }
    }

    internal interface IMaintenanceClientImpersonationRunner
    {
        MaintenanceClientTokenEvidence CaptureScoped(
            IMaintenanceClientTokenCapture capture,
            IMaintenanceProcessTerminator terminator);
    }

    internal interface IMaintenanceClientTokenCapture
    {
        MaintenanceClientTokenEvidence Capture();
    }

    internal sealed class MaintenanceClientCaptureRunner
    {
        private readonly IMaintenanceClientImpersonationRunner impersonation;
        private readonly IMaintenanceClientTokenCapture capture;
        private readonly IMaintenanceProcessTerminator terminator;

        internal MaintenanceClientCaptureRunner(
            IMaintenanceClientImpersonationRunner impersonation,
            IMaintenanceClientTokenCapture capture,
            IMaintenanceProcessTerminator terminator)
        {
            if (impersonation == null ||
                capture == null ||
                terminator == null)
            {
                throw new ArgumentNullException(
                    "Maintenance capture composition is incomplete.");
            }
            this.impersonation = impersonation;
            this.capture = capture;
            this.terminator = terminator;
        }

        internal MaintenanceClientTokenEvidence CaptureAndRevert()
        {
            MaintenanceClientTokenEvidence evidence =
                impersonation.CaptureScoped(
                    capture,
                    terminator);
            if (evidence == null)
            {
                throw new UnauthorizedAccessException(
                    "Maintenance scoped token capture returned no " +
                    "evidence.");
            }
            return evidence;
        }
    }

    internal interface IMaintenancePreauthorizedCommandDispatcher
    {
        MaintenanceCommittedResponse Dispatch(
            PayloadBrokerCommand command,
            MaintenanceAuthorizationEvidence authorization,
            CancellationToken cancellation);
    }

    internal sealed class MaintenanceClientRequestSequencer
    {
        private readonly MaintenanceClientCaptureRunner captureRunner;
        private readonly IMaintenanceClientPolicyAuthorizer authorizer;
        private readonly IMaintenancePreauthorizedCommandDispatcher
            dispatcher;

        internal MaintenanceClientRequestSequencer(
            MaintenanceClientCaptureRunner captureRunner,
            IMaintenanceClientPolicyAuthorizer authorizer,
            IMaintenancePreauthorizedCommandDispatcher dispatcher)
        {
            if (captureRunner == null ||
                authorizer == null ||
                dispatcher == null)
            {
                throw new ArgumentNullException(
                    "Maintenance request sequencing is incomplete.");
            }
            this.captureRunner = captureRunner;
            this.authorizer = authorizer;
            this.dispatcher = dispatcher;
        }

        internal MaintenanceCommittedResponse Execute(
            Func<PayloadBrokerCommand> parseCommand,
            CancellationToken cancellation)
        {
            if (parseCommand == null)
            {
                throw new ArgumentNullException("parseCommand");
            }
            MaintenanceClientTokenEvidence evidence =
                captureRunner.CaptureAndRevert();
            MaintenanceAuthorizationEvidence authorization =
                authorizer.Authorize(evidence, cancellation);
            cancellation.ThrowIfCancellationRequested();
            PayloadBrokerCommand command = parseCommand();
            if (command == null)
            {
                throw new InvalidDataException(
                    "Maintenance command parser returned no command.");
            }
            command.Validate();
            cancellation.ThrowIfCancellationRequested();
            MaintenanceCommittedResponse committed =
                dispatcher.Dispatch(
                command,
                authorization,
                cancellation);
            if (committed == null)
            {
                throw new InvalidDataException(
                    "Maintenance dispatcher returned no committed response.");
            }
            PayloadBrokerResponse response =
                committed.GetValidatedResponse();
            response.ValidateForCommand(command);
            return committed;
        }
    }
}
