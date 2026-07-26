using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SBMSSetup
{
    internal static class MaintenanceServiceIdentity
    {
        internal const string PipeName = "SBMS.Maintenance.v1";

        internal static string ServiceName
        {
            get
            {
                return PayloadNamespaceSecurityProfile.BrokerServiceName;
            }
        }

        internal static string NamespaceId
        {
            get
            {
                return PayloadManagedNamespaceLocation.ProductionNamespaceId;
            }
        }
    }

    internal enum MaintenanceLifecycleState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Faulted
    }

    internal interface IMaintenanceProcessTerminator
    {
        void Terminate(string reason);
    }

    internal sealed class FailFastMaintenanceProcessTerminator
        : IMaintenanceProcessTerminator
    {
        public void Terminate(string reason)
        {
            Environment.FailFast(reason);
        }
    }

    internal sealed class MaintenanceLifecycle
    {
        private readonly object gate = new object();
        private readonly IMaintenanceProcessTerminator terminator;
        private readonly TimeSpan cancellationExitBudget;
        private MaintenanceLifecycleState state =
            MaintenanceLifecycleState.Stopped;

        internal MaintenanceLifecycle()
            : this(
                new FailFastMaintenanceProcessTerminator(),
                TimeSpan.FromSeconds(5))
        {
        }

        internal MaintenanceLifecycle(
            IMaintenanceProcessTerminator terminator,
            TimeSpan cancellationExitBudget)
        {
            if (terminator == null ||
                cancellationExitBudget <= TimeSpan.Zero ||
                cancellationExitBudget > TimeSpan.FromMinutes(1))
            {
                throw new InvalidOperationException(
                    "Maintenance lifecycle termination policy is invalid.");
            }
            this.terminator = terminator;
            this.cancellationExitBudget = cancellationExitBudget;
        }

        internal MaintenanceLifecycleState State
        {
            get
            {
                lock (gate)
                {
                    return state;
                }
            }
        }

        internal void Start(
            TimeSpan budget,
            Action<CancellationToken> start)
        {
            RunBounded(
                MaintenanceLifecycleState.Stopped,
                MaintenanceLifecycleState.Starting,
                MaintenanceLifecycleState.Running,
                budget,
                start);
        }

        internal void Stop(
            TimeSpan budget,
            Action<CancellationToken> stop)
        {
            RunBounded(
                MaintenanceLifecycleState.Running,
                MaintenanceLifecycleState.Stopping,
                MaintenanceLifecycleState.Stopped,
                budget,
                stop);
        }

        private void RunBounded(
            MaintenanceLifecycleState required,
            MaintenanceLifecycleState intermediate,
            MaintenanceLifecycleState complete,
            TimeSpan budget,
            Action<CancellationToken> operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException("operation");
            }
            if (budget <= TimeSpan.Zero ||
                budget > TimeSpan.FromMinutes(2))
            {
                throw new InvalidOperationException(
                    "Maintenance lifecycle budget is invalid.");
            }
            lock (gate)
            {
                if (state != required)
                {
                    throw new InvalidOperationException(
                        "Maintenance lifecycle transition is illegal.");
                }
                state = intermediate;
            }
            using (var cancellation = new CancellationTokenSource())
            {
                Task task = Task.Factory.StartNew(
                    delegate
                    {
                        operation(cancellation.Token);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    TaskScheduler.Default);
                try
                {
                    IAsyncResult asyncTask = task;
                    if (!asyncTask.AsyncWaitHandle.WaitOne(budget))
                    {
                        cancellation.Cancel();
                        if (!asyncTask.AsyncWaitHandle.WaitOne(
                                cancellationExitBudget))
                        {
                            lock (gate)
                            {
                                state =
                                    MaintenanceLifecycleState.Faulted;
                            }
                            terminator.Terminate(
                                "Maintenance lifecycle operation ignored cancellation.");
                            throw new TimeoutException(
                                "Maintenance lifecycle terminator returned.");
                        }
                        task.GetAwaiter().GetResult();
                        throw new TimeoutException(
                            "Maintenance lifecycle budget expired.");
                    }
                    task.GetAwaiter().GetResult();
                    lock (gate)
                    {
                        state = complete;
                    }
                }
                catch
                {
                    lock (gate)
                    {
                        state = MaintenanceLifecycleState.Faulted;
                    }
                    throw;
                }
            }
        }
    }

    internal sealed class ProtectedRootAceSemantic
    {
        internal readonly string Principal;
        internal readonly string Rights;
        internal readonly bool Allow;

        internal ProtectedRootAceSemantic(
            string principal,
            string rights,
            bool allow)
        {
            Principal = principal;
            Rights = rights;
            Allow = allow;
        }
    }

    internal sealed class ProtectedRootSecurityMaterial
    {
        internal readonly string Owner;
        internal readonly string ServiceSid;
        internal readonly string Sddl;
        internal readonly IList<ProtectedRootAceSemantic> Aces;
        internal readonly string PolicyInvariantDigest;

        internal ProtectedRootSecurityMaterial(
            string owner,
            string serviceSid,
            string sddl,
            IList<ProtectedRootAceSemantic> aces,
            string policyInvariantDigest)
        {
            Owner = owner;
            ServiceSid = serviceSid;
            Sddl = sddl;
            Aces = aces;
            PolicyInvariantDigest = policyInvariantDigest;
        }
    }

    // Pure compiler only. It does not open a directory or apply an ACL.
    internal static class ProtectedRootSecurityCompiler
    {
        internal static ProtectedRootSecurityMaterial Compile()
        {
            PayloadNamespaceSecurityProfile policy =
                PayloadNamespaceSecurityProfile.Production();
            policy.Validate();
            string serviceSid = DeriveServiceSid(policy.ServiceName);
            var aces = new List<ProtectedRootAceSemantic>
            {
                new ProtectedRootAceSemantic(
                    serviceSid,
                    policy.ServiceAccess,
                    true),
                new ProtectedRootAceSemantic(
                    "LocalSystem",
                    policy.LocalSystemAccess,
                    true),
                new ProtectedRootAceSemantic(
                    "BuiltInAdministrators",
                    policy.AdministratorsAccess,
                    true),
                new ProtectedRootAceSemantic(
                    "BuiltInUsers",
                    policy.UsersAccess,
                    true)
            };
            string sddl =
                "O:SYD:P" +
                "(A;OICI;GA;;;" + serviceSid + ")" +
                "(A;OICI;GRGX;;;SY)" +
                "(A;OICI;GRGX;;;BA)" +
                "(A;OICI;GRGX;;;BU)";
            return new ProtectedRootSecurityMaterial(
                policy.Owner,
                serviceSid,
                sddl,
                aces.AsReadOnly(),
                policy.InvariantDigest);
        }

        internal static string DeriveServiceSid(string serviceName)
        {
            if (!String.Equals(
                    serviceName,
                    MaintenanceServiceIdentity.ServiceName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Service SID derivation accepts only the fixed service.");
            }
            byte[] name = Encoding.Unicode.GetBytes(
                serviceName.ToUpperInvariant());
            byte[] hash;
            using (SHA1 algorithm = SHA1.Create())
            {
                hash = algorithm.ComputeHash(name);
            }
            var sid = new StringBuilder("S-1-5-80");
            for (int index = 0; index < 5; ++index)
            {
                uint value =
                    (uint)hash[index * 4] |
                    ((uint)hash[index * 4 + 1] << 8) |
                    ((uint)hash[index * 4 + 2] << 16) |
                    ((uint)hash[index * 4 + 3] << 24);
                sid.Append('-');
                sid.Append(
                    value.ToString(CultureInfo.InvariantCulture));
            }
            return sid.ToString();
        }
    }

    internal sealed class MaintenancePipeSecurityContract
    {
        internal const string Endpoint =
            @"\\.\pipe\SBMS.Maintenance.v1";
        internal const int FileReadData = 0x00000001;
        internal const int FileWriteData = 0x00000002;
        internal const int ClientDesiredAccess =
            FileReadData | FileWriteData;

        internal readonly string ServiceSid;
        internal readonly string Sddl;

        internal MaintenancePipeSecurityContract(
            string serviceSid,
            string sddl)
        {
            ServiceSid = serviceSid;
            Sddl = sddl;
        }

        internal static MaintenancePipeSecurityContract Compile()
        {
            string serviceSid =
                ProtectedRootSecurityCompiler.DeriveServiceSid(
                    MaintenanceServiceIdentity.ServiceName);
            return new MaintenancePipeSecurityContract(
                serviceSid,
                "O:SYD:P" +
                "(A;;GA;;;" + serviceSid + ")" +
                "(A;;GA;;;SY)" +
                "(A;;0x" +
                ClientDesiredAccess.ToString(
                    "X8",
                    CultureInfo.InvariantCulture) +
                ";;;BA)");
        }
    }

    internal sealed class MaintenanceAuthorizationEvidence
    {
        private MaintenanceAuthorizationEvidence()
        {
        }

        internal static MaintenanceAuthorizationEvidence IssueForTrustedAdapter()
        {
            return new MaintenanceAuthorizationEvidence();
        }
    }

    internal interface IMaintenanceCommandAuthorizer
    {
        MaintenanceAuthorizationEvidence Authorize(
            PayloadBrokerCommand command,
            CancellationToken cancellation);
    }

    // Production named-pipe impersonation/token inspection is deliberately
    // not wired in this slice, so there is no production authorizer factory.
    internal sealed class SerializedMaintenanceCommandDispatcher
    {
        private readonly SemaphoreSlim commandGate =
            new SemaphoreSlim(1, 1);
        private readonly IMaintenanceCommandAuthorizer authorizer;
        [ThreadStatic]
        private static bool dispatching;
        private int activeCommands;

        internal SerializedMaintenanceCommandDispatcher(
            IMaintenanceCommandAuthorizer authorizer)
        {
            if (authorizer == null)
            {
                throw new ArgumentNullException("authorizer");
            }
            this.authorizer = authorizer;
        }

        internal int MaximumObservedConcurrency { get; private set; }

        internal PayloadBrokerResponse Dispatch(
            PayloadBrokerCommand command,
            CancellationToken cancellation,
            Func<
                PayloadBrokerCommand,
                MaintenanceAuthorizationEvidence,
                CancellationToken,
                PayloadBrokerResponse> execute)
        {
            if (command == null || execute == null)
            {
                throw new ArgumentNullException(
                    "Maintenance dispatch input is missing.");
            }
            if (dispatching)
            {
                throw new InvalidOperationException(
                    "Maintenance dispatcher is non-reentrant.");
            }
            dispatching = true;
            bool enteredSemaphore = false;
            bool countedActive = false;
            try
            {
                command.Validate();
                MaintenanceAuthorizationEvidence evidence =
                    authorizer.Authorize(command, cancellation);
                if (evidence == null)
                {
                    throw new UnauthorizedAccessException(
                        "Maintenance authorizer returned no trusted evidence.");
                }
                commandGate.Wait(cancellation);
                enteredSemaphore = true;
                cancellation.ThrowIfCancellationRequested();
                activeCommands++;
                countedActive = true;
                MaximumObservedConcurrency = Math.Max(
                    MaximumObservedConcurrency,
                    activeCommands);
                PayloadBrokerResponse response =
                    execute(
                        command,
                        evidence,
                        cancellation);
                cancellation.ThrowIfCancellationRequested();
                response.ValidateForCommand(command);
                return response;
            }
            finally
            {
                if (countedActive)
                {
                    activeCommands--;
                }
                dispatching = false;
                if (enteredSemaphore)
                {
                    commandGate.Release();
                }
            }
        }
    }

    internal interface IMaintenanceReplayStoreLease : IDisposable
    {
        bool TryRead(string key, out byte[] bytes);
        void AtomicWrite(string key, byte[] bytes);
    }

    internal interface IMaintenanceReplayAtomicStore
    {
        string RootAuthorityInvariantDigest { get; }
        IMaintenanceReplayStoreLease AcquireExclusiveLease();
    }

    internal enum MaintenanceReplayRecordState
    {
        Prepared,
        Committed
    }

    internal sealed class MaintenanceReplayContentFormatException
        : IOException
    {
        internal MaintenanceReplayContentFormatException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    [DataContract]
    internal sealed class MaintenanceReplayRecord
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal MaintenanceReplayRecordState State;

        [DataMember(Order = 3, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 4, IsRequired = true)]
        internal string RequestId;

        [DataMember(Order = 5, IsRequired = true)]
        internal string CommandInvariantDigest;

        [DataMember(Order = 6, IsRequired = true)]
        internal string StorageKeyInvariantDigest;

        [DataMember(Order = 7, IsRequired = true)]
        internal PayloadBrokerResponse Response;

        internal void Validate()
        {
            if (SchemaVersion != 1 ||
                !Enum.IsDefined(
                    typeof(MaintenanceReplayRecordState),
                    State))
            {
                throw new InvalidOperationException(
                    "Maintenance replay record is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Maintenance replay transaction ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                RequestId,
                "Maintenance replay request ID");
            PayloadContractValidation.RequireSha256(
                CommandInvariantDigest,
                "Maintenance replay command digest");
            PayloadContractValidation.RequireSha256(
                StorageKeyInvariantDigest,
                "Maintenance replay storage-key digest");
            if (!String.Equals(
                    StorageKeyInvariantDigest,
                    ComputeStorageKeyInvariantDigest(
                        TransactionId,
                        RequestId),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Maintenance replay storage-key binding changed.");
            }
            if (State == MaintenanceReplayRecordState.Prepared)
            {
                if (Response != null)
                {
                    throw new InvalidOperationException(
                        "Prepared replay record carries a response.");
                }
            }
            else
            {
                if (Response == null)
                {
                    throw new InvalidOperationException(
                        "Committed replay record lacks a response.");
                }
                Response.Validate();
                if (!String.Equals(
                        TransactionId,
                        Response.TransactionId,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        RequestId,
                        Response.RequestId,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        CommandInvariantDigest,
                        Response.CommandInvariantDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Committed response differs from replay metadata.");
                }
            }
        }

        internal void ValidateRequest(PayloadBrokerCommand command)
        {
            Validate();
            command.Validate();
            if (!String.Equals(
                    TransactionId,
                    command.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    RequestId,
                    command.RequestId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    CommandInvariantDigest,
                    command.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Maintenance replay record binds a foreign request.");
            }
            if (Response != null)
            {
                Response.ValidateForCommand(command);
            }
        }

        internal static string ComputeStorageKeyInvariantDigest(
            string transactionId,
            string requestId)
        {
            return PayloadContractValidation.ComputeDigest(
                "SBMS.Maintenance.ReplayKey.v1",
                new[] { transactionId, requestId });
        }
    }

    internal static class MaintenanceReplayRecordCodec
    {
        internal static byte[] SerializeCanonical(
            MaintenanceReplayRecord record)
        {
            if (record == null)
            {
                throw new MaintenanceReplayContentFormatException(
                    "Maintenance replay record is missing.",
                    null);
            }
            try
            {
                record.Validate();
                var serializer = NewSerializer();
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, record);
                    return stream.ToArray();
                }
            }
            catch (MaintenanceReplayContentFormatException)
            {
                throw;
            }
            catch (InvalidOperationException exception)
            {
                throw FormatFailure(exception);
            }
            catch (SerializationException exception)
            {
                throw FormatFailure(exception);
            }
            catch (InvalidDataContractException exception)
            {
                throw FormatFailure(exception);
            }
        }

        internal static MaintenanceReplayRecord DeserializeCanonical(
            byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new MaintenanceReplayContentFormatException(
                    "Maintenance replay record bytes are missing.",
                    null);
            }
            MaintenanceReplayRecord record;
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                {
                    record =
                        (MaintenanceReplayRecord)NewSerializer().
                            ReadObject(stream);
                    if (record == null)
                    {
                        throw new MaintenanceReplayContentFormatException(
                            "Maintenance replay record decoded to null.",
                            null);
                    }
                    if (stream.Position != stream.Length)
                    {
                        throw new MaintenanceReplayContentFormatException(
                            "Maintenance replay record has trailing data.",
                            null);
                    }
                }
                record.Validate();
            }
            catch (MaintenanceReplayContentFormatException)
            {
                throw;
            }
            catch (InvalidOperationException exception)
            {
                throw FormatFailure(exception);
            }
            catch (SerializationException exception)
            {
                throw FormatFailure(exception);
            }
            catch (InvalidDataContractException exception)
            {
                throw FormatFailure(exception);
            }
            RequireExactBytes(
                bytes,
                SerializeCanonical(record));
            return record;
        }

        internal static void RequireExactBytes(
            byte[] first,
            byte[] second)
        {
            if (first == null ||
                second == null ||
                first.Length != second.Length)
            {
                throw new MaintenanceReplayContentFormatException(
                    "Maintenance replay bytes differ.",
                    null);
            }
            int difference = 0;
            for (int index = 0; index < first.Length; ++index)
            {
                difference |= first[index] ^ second[index];
            }
            if (difference != 0)
            {
                throw new MaintenanceReplayContentFormatException(
                    "Maintenance replay bytes differ.",
                    null);
            }
        }

        private static MaintenanceReplayContentFormatException
            FormatFailure(Exception exception)
        {
            return new MaintenanceReplayContentFormatException(
                "Maintenance replay record cannot be encoded or decoded.",
                exception);
        }

        private static DataContractJsonSerializer NewSerializer()
        {
            return new DataContractJsonSerializer(
                typeof(MaintenanceReplayRecord),
                new[]
                {
                    typeof(PayloadBrokerResponse),
                    typeof(PayloadBrokerOperationReceipt),
                    typeof(PayloadNamespaceOwnershipCasToken),
                    typeof(PayloadWorkspaceCasToken),
                    typeof(PayloadNamespaceOwnershipObservation)
                });
        }
    }

    // A production adapter must be rooted by
    // EnvironmentInstallerProgramDataPathProvider and reuse
    // FileTransactionJournalStore/WindowsHandleRelativeJournalFileSystem.
    // This slice has no production factory and never falls back to CWD/temp.
    internal sealed class MaintenanceCommittedResponse
    {
        private readonly byte[] canonicalResponse;

        private MaintenanceCommittedResponse(
            PayloadBrokerCommand command,
            PayloadBrokerResponse response,
            MaintenanceWriteBeforeAckExecutor.CommitAuthority authority)
        {
            if (!MaintenanceWriteBeforeAckExecutor.
                    IsCommitAuthority(authority))
            {
                throw new UnauthorizedAccessException(
                    "Committed response authority is missing.");
            }
            response.ValidateForCommand(command);
            canonicalResponse =
                PayloadBrokerResponseCodec.SerializeCanonical(response);
        }

        internal static MaintenanceCommittedResponse Issue(
            PayloadBrokerCommand command,
            PayloadBrokerResponse response,
            MaintenanceWriteBeforeAckExecutor.CommitAuthority authority)
        {
            return new MaintenanceCommittedResponse(
                command,
                response,
                authority);
        }

        internal PayloadBrokerResponse GetValidatedResponse()
        {
            var stable = new byte[canonicalResponse.Length];
            Buffer.BlockCopy(
                canonicalResponse,
                0,
                stable,
                0,
                stable.Length);
            return PayloadBrokerResponseCodec.
                DeserializeAndValidate(stable);
        }
    }

    internal sealed class MaintenanceWriteBeforeAckExecutor
    {
        internal sealed class CommitAuthority
        {
            internal CommitAuthority()
            {
            }
        }

        private static readonly CommitAuthority Authority =
            new CommitAuthority();
        private readonly IMaintenanceReplayAtomicStore store;

        internal static bool IsCommitAuthority(
            CommitAuthority candidate)
        {
            return Object.ReferenceEquals(candidate, Authority);
        }

        internal MaintenanceWriteBeforeAckExecutor(
            IMaintenanceReplayAtomicStore store,
            string expectedRootAuthorityInvariantDigest)
        {
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }
            PayloadContractValidation.RequireSha256(
                store.RootAuthorityInvariantDigest,
                "Maintenance replay store root authority");
            PayloadContractValidation.RequireSha256(
                expectedRootAuthorityInvariantDigest,
                "Expected maintenance replay root authority");
            if (!String.Equals(
                    store.RootAuthorityInvariantDigest,
                    expectedRootAuthorityInvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Maintenance replay root authority differs from composition.");
            }
            this.store = store;
        }

        internal PayloadBrokerResponse Execute(
            PayloadBrokerCommand command,
            Func<PayloadBrokerCommand, PayloadBrokerResponse> mutate,
            Func<PayloadBrokerCommand, PayloadBrokerResponse> reconcile)
        {
            if (command == null ||
                mutate == null ||
                reconcile == null)
            {
                throw new ArgumentNullException(
                    "Maintenance replay execution input is missing.");
            }
            command.Validate();
            string key =
                command.TransactionId + ":" + command.RequestId;
            IMaintenanceReplayStoreLease acquired =
                store.AcquireExclusiveLease();
            if (acquired == null)
            {
                throw new InvalidOperationException(
                    "Maintenance replay store did not acquire a lease.");
            }
            using (IMaintenanceReplayStoreLease lease = acquired)
            {
                byte[] existing;
                if (lease.TryRead(key, out existing))
                {
                    MaintenanceReplayRecord record =
                        MaintenanceReplayRecordCodec.
                            DeserializeCanonical(existing);
                    record.ValidateRequest(command);
                    if (record.State ==
                        MaintenanceReplayRecordState.Committed)
                    {
                        return record.Response;
                    }
                    PayloadBrokerResponse reconciled =
                        reconcile(command);
                    reconciled.ValidateForCommand(command);
                    MaintenanceReplayRecord committed =
                        Committed(command, reconciled);
                    WriteAndConfirm(lease, key, committed);
                    return reconciled;
                }

                MaintenanceReplayRecord prepared =
                    new MaintenanceReplayRecord
                    {
                        SchemaVersion = 1,
                        State =
                            MaintenanceReplayRecordState.Prepared,
                        TransactionId = command.TransactionId,
                        RequestId = command.RequestId,
                        CommandInvariantDigest =
                            command.InvariantDigest,
                        StorageKeyInvariantDigest =
                            MaintenanceReplayRecord.
                                ComputeStorageKeyInvariantDigest(
                                    command.TransactionId,
                                    command.RequestId),
                        Response = null
                    };
                prepared.ValidateRequest(command);
                WriteAndConfirm(lease, key, prepared);

                PayloadBrokerResponse response = mutate(command);
                response.ValidateForCommand(command);
                WriteAndConfirm(
                    lease,
                    key,
                    Committed(command, response));
                return response;
            }
        }

        internal MaintenanceCommittedResponse ExecuteCommitted(
            PayloadBrokerCommand command,
            Func<PayloadBrokerCommand, PayloadBrokerResponse> mutate,
            Func<PayloadBrokerCommand, PayloadBrokerResponse> reconcile)
        {
            PayloadBrokerResponse response =
                Execute(command, mutate, reconcile);
            return MaintenanceCommittedResponse.Issue(
                command,
                response,
                Authority);
        }

        private static MaintenanceReplayRecord Committed(
            PayloadBrokerCommand command,
            PayloadBrokerResponse response)
        {
            return new MaintenanceReplayRecord
            {
                SchemaVersion = 1,
                State = MaintenanceReplayRecordState.Committed,
                TransactionId = command.TransactionId,
                RequestId = command.RequestId,
                CommandInvariantDigest = command.InvariantDigest,
                StorageKeyInvariantDigest =
                    MaintenanceReplayRecord.
                        ComputeStorageKeyInvariantDigest(
                            command.TransactionId,
                            command.RequestId),
                Response = response
            };
        }

        private static void WriteAndConfirm(
            IMaintenanceReplayStoreLease lease,
            string key,
            MaintenanceReplayRecord candidate)
        {
            byte[] expected =
                MaintenanceReplayRecordCodec.
                    SerializeCanonical(candidate);
            Exception writeFailure = null;
            try
            {
                lease.AtomicWrite(key, expected);
            }
            catch (Exception exception)
            {
                writeFailure = exception;
            }
            byte[] observed;
            if (!lease.TryRead(key, out observed))
            {
                if (writeFailure != null)
                {
                    throw writeFailure;
                }
                throw new IOException(
                    "Maintenance replay write lacked fresh readback.");
            }
            try
            {
                MaintenanceReplayRecordCodec.
                    DeserializeCanonical(observed);
                MaintenanceReplayRecordCodec.
                    RequireExactBytes(expected, observed);
            }
            catch
            {
                if (writeFailure != null)
                {
                    throw writeFailure;
                }
                throw;
            }
        }
    }
}
