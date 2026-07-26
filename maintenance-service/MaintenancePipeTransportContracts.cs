using System;
using System.IO;
using System.Threading;

namespace SBMSSetup
{
    internal enum MaintenancePipeSlotState
    {
        Creating,
        Connecting,
        Reading,
        CapturingDispatching,
        Parsing,
        Writing,
        AwaitingAck,
        Disconnecting,
        Complete,
        Stopping,
        Faulted
    }

    internal enum MaintenancePipeOperationKind
    {
        Connect,
        Read,
        Write,
        Ack
    }

    internal enum MaintenancePipeBeginDisposition
    {
        Immediate,
        Pending,
        PipeConnected,
        RejectedBeforeIssue,
        UncertainAfterIssue
    }

    internal enum MaintenancePipeCompletionStatus
    {
        Success,
        MoreData,
        BrokenPipe,
        Aborted,
        Faulted
    }

    internal enum MaintenancePipeCancelResult
    {
        Cancelled,
        NotFound
    }

    internal enum MaintenancePipeTransferMode
    {
        MessageModeSingleMessage
    }

    internal sealed class MaintenancePipeOperationIdentity
    {
        internal MaintenancePipeOperationIdentity(
            long connectionGeneration,
            long operationGeneration,
            MaintenancePipeOperationKind kind)
        {
            if (connectionGeneration <= 0 ||
                operationGeneration <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "Pipe operation generations must be positive.");
            }
            ConnectionGeneration = connectionGeneration;
            OperationGeneration = operationGeneration;
            Kind = kind;
        }

        internal readonly long ConnectionGeneration;
        internal readonly long OperationGeneration;
        internal readonly MaintenancePipeOperationKind Kind;
    }

    internal sealed class MaintenancePipeCompletion
    {
        internal MaintenancePipeCompletion(
            MaintenancePipeOperationIdentity identity,
            MaintenancePipeCompletionStatus status,
            int bytesTransferred)
        {
            Identity = identity;
            Status = status;
            BytesTransferred = bytesTransferred;
        }

        internal readonly MaintenancePipeOperationIdentity Identity;
        internal readonly MaintenancePipeCompletionStatus Status;
        internal readonly int BytesTransferred;
    }

    internal sealed class MaintenancePipeBeginResult
    {
        internal MaintenancePipeBeginResult(
            MaintenancePipeOperationIdentity identity,
            MaintenancePipeBeginDisposition disposition,
            MaintenancePipeCompletion completion)
        {
            Identity = identity;
            Disposition = disposition;
            Completion = completion;
        }

        internal readonly MaintenancePipeOperationIdentity Identity;
        internal readonly MaintenancePipeBeginDisposition Disposition;
        internal readonly MaintenancePipeCompletion Completion;
    }

    internal sealed class MaintenancePipeCancellation
    {
        internal MaintenancePipeCancellation(
            MaintenancePipeOperationIdentity identity,
            MaintenancePipeCancelResult result)
        {
            Identity = identity;
            Result = result;
        }

        internal readonly MaintenancePipeOperationIdentity Identity;
        internal readonly MaintenancePipeCancelResult Result;
    }

    internal interface IMaintenancePipeOperation : IDisposable
    {
        MaintenancePipeOperationIdentity Identity { get; }
        MaintenancePipeBeginResult Begin();
        MaintenancePipeCompletion Wait(
            TimeSpan timeout,
            CancellationToken cancellation);
        MaintenancePipeCancellation Cancel();
        MaintenancePipeCompletion Drain();
    }

    internal interface IMaintenancePipeConnection : IDisposable
    {
        long Generation { get; }
        MaintenancePipeTransferMode TransferMode { get; }
        IMaintenancePipeOperation CreateConnectOperation();
        IMaintenancePipeOperation CreateReadOperation(
            byte[] buffer,
            int offset,
            int count,
            MaintenancePipeOperationKind kind);
        IMaintenancePipeOperation CreateWriteOperation(
            byte[] buffer,
            int offset,
            int count);
        void Disconnect();
    }

    internal interface IMaintenancePipeConnectionFactory
    {
        IMaintenancePipeConnection Create(long generation);
    }

    internal interface IMaintenancePipeRequestParser
    {
        PayloadBrokerCommand Parse(byte[] frozenRequest);
    }

    internal sealed class MaintenancePipeRequestParser
        : IMaintenancePipeRequestParser
    {
        public PayloadBrokerCommand Parse(byte[] frozenRequest)
        {
            MaintenancePipeFrame requestFrame =
                MaintenancePipeFrameCodec.Decode(frozenRequest);
            if (requestFrame.Kind !=
                MaintenancePipeFrameKind.Request)
            {
                throw new InvalidDataException(
                    "Maintenance pipe expected a request frame.");
            }
            return PayloadBrokerCommandCodec.DeserializeAndValidate(
                requestFrame.GetPayloadCopy());
        }
    }

    internal sealed class MaintenancePipeTransportTimeouts
    {
        private static readonly TimeSpan MaximumTimeout =
            TimeSpan.FromSeconds(30);

        internal MaintenancePipeTransportTimeouts(
            TimeSpan connect,
            TimeSpan read,
            TimeSpan write,
            TimeSpan ack)
        {
            RequireBounded(connect, "connect");
            RequireBounded(read, "read");
            RequireBounded(write, "write");
            RequireBounded(ack, "ack");
            Connect = connect;
            Read = read;
            Write = write;
            Ack = ack;
        }

        internal readonly TimeSpan Connect;
        internal readonly TimeSpan Read;
        internal readonly TimeSpan Write;
        internal readonly TimeSpan Ack;

        private static void RequireBounded(
            TimeSpan value,
            string label)
        {
            if (value <= TimeSpan.Zero ||
                value > MaximumTimeout)
            {
                throw new ArgumentOutOfRangeException(
                    label,
                    "Maintenance pipe timeout must be positive and no " +
                    "greater than 30 seconds.");
            }
        }
    }

    internal sealed class MaintenanceConnectedRequestExecutor
    {
        private readonly MaintenanceClientRequestSequencer sequencer;

        internal MaintenanceConnectedRequestExecutor(
            MaintenanceClientRequestSequencer value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }
            sequencer = value;
        }

        public MaintenanceCommittedResponse Execute(
            Func<PayloadBrokerCommand> parseCommand,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return sequencer.Execute(
                parseCommand,
                cancellation);
        }
    }

    internal sealed class MaintenancePipeTransportSlot
    {
        internal const int MaxRequestFrameLength =
            MaintenancePipeFrameCodec.HeaderLength +
            MaintenancePipeFrameCodec.MaxRequestPayload;
        internal const int ExactAckFrameLength =
            MaintenancePipeFrameCodec.HeaderLength +
            MaintenancePipeFrameCodec.AckPayloadLength;

        private readonly IMaintenancePipeConnectionFactory factory;
        private readonly MaintenanceConnectedRequestExecutor executor;
        private readonly MaintenancePipeTransportTimeouts timeouts;
        private readonly IMaintenancePipeRequestParser parser;
        private readonly IMaintenanceProcessTerminator terminator;
        private long generation;
        private int running;
        private bool poisoned;

        internal MaintenancePipeTransportSlot(
            IMaintenancePipeConnectionFactory factory,
            MaintenanceConnectedRequestExecutor executor,
            MaintenancePipeTransportTimeouts timeouts,
            IMaintenancePipeRequestParser parser,
            IMaintenanceProcessTerminator terminator,
            bool requireMessageModeSingleMessage)
        {
            if (factory == null ||
                executor == null ||
                timeouts == null ||
                parser == null ||
                terminator == null ||
                !requireMessageModeSingleMessage)
            {
                throw new ArgumentNullException(
                    "Maintenance pipe slot composition is incomplete.");
            }
            this.factory = factory;
            this.executor = executor;
            this.timeouts = timeouts;
            this.parser = parser;
            this.terminator = terminator;
        }

        internal MaintenancePipeSlotState State { get; private set; }
        internal long Generation { get { return generation; } }
        internal bool LastResponseCommitted { get; private set; }

        internal PayloadBrokerResponse RunOne(
            CancellationToken cancellation)
        {
            if (Interlocked.CompareExchange(
                    ref running,
                    1,
                    0) != 0)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe slot is already running.");
            }
            try
            {
                if (poisoned)
                {
                    throw new InvalidOperationException(
                        "Maintenance pipe slot is poisoned.");
                }
                LastResponseCommitted = false;
                generation = checked(generation + 1);
            }
            catch
            {
                Interlocked.Exchange(ref running, 0);
                throw;
            }
            long activeGeneration = generation;
            IMaintenancePipeConnection connection = null;
            bool success = false;
            bool stopping = false;
            bool cleanupFailed = false;
            bool lifetimeUncertain = false;
            long operationGeneration = 0;
            try
            {
                SetState(
                    MaintenancePipeSlotState.Creating,
                    activeGeneration);
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    connection = factory.Create(activeGeneration);
                }
                catch (Exception exception)
                {
                    FailStopUncertainLifetime(
                        ref lifetimeUncertain,
                        "Maintenance pipe connection factory ownership is " +
                        "uncertain.",
                        exception);
                }
                if (connection == null ||
                    connection.Generation != activeGeneration ||
                    connection.TransferMode !=
                        MaintenancePipeTransferMode.
                            MessageModeSingleMessage)
                {
                    throw new InvalidOperationException(
                        "Maintenance pipe connection generation differs.");
                }

                SetState(
                    MaintenancePipeSlotState.Connecting,
                    activeGeneration);
                MaintenancePipeCompletion connected =
                    CompleteOperation(
                        connection.CreateConnectOperation(),
                        MaintenancePipeOperationKind.Connect,
                        timeouts.Connect,
                        cancellation,
                        activeGeneration,
                        checked(++operationGeneration),
                        ref lifetimeUncertain);
                RequireSuccess(
                    connected,
                    activeGeneration,
                    "connect");

                SetState(
                    MaintenancePipeSlotState.Reading,
                    activeGeneration);
                byte[] frozenRequest =
                    ReadRequest(
                        connection,
                        cancellation,
                        activeGeneration,
                        ref operationGeneration,
                        ref lifetimeUncertain);

                SetState(
                    MaintenancePipeSlotState.CapturingDispatching,
                    activeGeneration);
                MaintenanceCommittedResponse committed =
                    executor.Execute(
                        delegate
                        {
                            SetState(
                                MaintenancePipeSlotState.Parsing,
                                activeGeneration);
                            return parser.Parse(frozenRequest);
                        },
                        cancellation);
                if (committed == null)
                {
                    throw new InvalidDataException(
                        "Maintenance request executor returned no committed " +
                        "response.");
                }
                PayloadBrokerResponse response =
                    committed.GetValidatedResponse();
                LastResponseCommitted = true;
                cancellation.ThrowIfCancellationRequested();

                byte[] canonicalResponse =
                    PayloadBrokerResponseCodec.SerializeCanonical(
                        response);
                byte[] responseFrame =
                    MaintenancePipeFrameCodec.Encode(
                        MaintenancePipeFrameKind.Response,
                        canonicalResponse);
                SetState(
                    MaintenancePipeSlotState.Writing,
                    activeGeneration);
                MaintenancePipeCompletion written =
                    CompleteOperation(
                        connection.CreateWriteOperation(
                            responseFrame,
                            0,
                            responseFrame.Length),
                        MaintenancePipeOperationKind.Write,
                        timeouts.Write,
                        cancellation,
                        activeGeneration,
                        checked(++operationGeneration),
                        ref lifetimeUncertain);
                RequireSuccess(
                    written,
                    activeGeneration,
                    "write");
                if (written.BytesTransferred !=
                    responseFrame.Length)
                {
                    throw new IOException(
                        "Maintenance pipe response write was short.");
                }

                SetState(
                    MaintenancePipeSlotState.AwaitingAck,
                    activeGeneration);
                byte[] ackFrame =
                    ReadExactAck(
                        connection,
                        cancellation,
                        activeGeneration,
                        ref operationGeneration,
                        ref lifetimeUncertain);
                MaintenancePipeFrameCodec.DecodeAckAndVerify(
                    ackFrame,
                    canonicalResponse);
                success = true;
                return response;
            }
            catch (OperationCanceledException)
            {
                stopping = true;
                throw;
            }
            finally
            {
                try
                {
                    if (connection != null &&
                        !lifetimeUncertain)
                    {
                        SetState(
                            MaintenancePipeSlotState.Disconnecting,
                            activeGeneration);
                        try
                        {
                            connection.Disconnect();
                        }
                        catch (Exception exception)
                        {
                            cleanupFailed = true;
                            FailStopUncertainLifetime(
                                ref lifetimeUncertain,
                                "Maintenance pipe connection disconnect " +
                                "failed.",
                                exception);
                        }
                        try
                        {
                            connection.Dispose();
                        }
                        catch (Exception exception)
                        {
                            cleanupFailed = true;
                            FailStopUncertainLifetime(
                                ref lifetimeUncertain,
                                "Maintenance pipe connection disposal " +
                                "failed.",
                                exception);
                        }
                    }
                }
                finally
                {
                    if (cleanupFailed || lifetimeUncertain)
                    {
                        SetState(
                            MaintenancePipeSlotState.Faulted,
                            activeGeneration);
                    }
                    else if (success)
                    {
                        SetState(
                            MaintenancePipeSlotState.Complete,
                            activeGeneration);
                    }
                    else if (stopping ||
                        cancellation.IsCancellationRequested)
                    {
                        SetState(
                            MaintenancePipeSlotState.Stopping,
                            activeGeneration);
                    }
                    else
                    {
                        SetState(
                            MaintenancePipeSlotState.Faulted,
                            activeGeneration);
                    }
                    if (cleanupFailed || lifetimeUncertain)
                    {
                        poisoned = true;
                    }
                    Interlocked.Exchange(ref running, 0);
                }
            }
        }

        private byte[] ReadRequest(
            IMaintenancePipeConnection connection,
            CancellationToken cancellation,
            long activeGeneration,
            ref long operationGeneration,
            ref bool lifetimeUncertain)
        {
            var buffer = new byte[MaxRequestFrameLength];
            MaintenancePipeCompletion completion =
                CompleteOperation(
                    connection.CreateReadOperation(
                        buffer,
                        0,
                        buffer.Length,
                        MaintenancePipeOperationKind.Read),
                    MaintenancePipeOperationKind.Read,
                    timeouts.Read,
                    cancellation,
                    activeGeneration,
                    checked(++operationGeneration),
                    ref lifetimeUncertain);
            RequireReadable(
                completion,
                activeGeneration,
                buffer.Length,
                "request");
            if (completion.Status !=
                MaintenancePipeCompletionStatus.Success)
            {
                throw new InvalidDataException(
                    "Maintenance pipe request must fit one message read.");
            }
            var frozen =
                new byte[completion.BytesTransferred];
            Buffer.BlockCopy(
                buffer,
                0,
                frozen,
                0,
                completion.BytesTransferred);
            return frozen;
        }

        private byte[] ReadExactAck(
            IMaintenancePipeConnection connection,
            CancellationToken cancellation,
            long activeGeneration,
            ref long operationGeneration,
            ref bool lifetimeUncertain)
        {
            var buffer = new byte[ExactAckFrameLength];
            MaintenancePipeCompletion completion =
                CompleteOperation(
                    connection.CreateReadOperation(
                        buffer,
                        0,
                        buffer.Length,
                        MaintenancePipeOperationKind.Ack),
                    MaintenancePipeOperationKind.Ack,
                    timeouts.Ack,
                    cancellation,
                    activeGeneration,
                    checked(++operationGeneration),
                    ref lifetimeUncertain);
            RequireReadable(
                completion,
                activeGeneration,
                buffer.Length,
                "acknowledgement");
            if (completion.Status !=
                    MaintenancePipeCompletionStatus.Success ||
                completion.BytesTransferred != buffer.Length)
            {
                throw new InvalidDataException(
                    "Maintenance pipe acknowledgement must be exactly " +
                    ExactAckFrameLength + " bytes.");
            }
            return buffer;
        }

        private MaintenancePipeCompletion CompleteOperation(
            IMaintenancePipeOperation operation,
            MaintenancePipeOperationKind expectedKind,
            TimeSpan timeout,
            CancellationToken cancellation,
            long activeGeneration,
            long expectedOperationGeneration,
            ref bool lifetimeUncertain)
        {
            if (operation == null)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe operation is missing.");
            }
            bool beginEntered = false;
            bool safeToDispose = true;
            try
            {
                MaintenancePipeOperationIdentity identity =
                    operation.Identity;
                RequireIdentity(
                    identity,
                    activeGeneration,
                    expectedOperationGeneration,
                    expectedKind);
                cancellation.ThrowIfCancellationRequested();
                beginEntered = true;
                MaintenancePipeBeginResult started;
                try
                {
                    started = operation.Begin();
                }
                catch (Exception exception)
                {
                    safeToDispose = false;
                    FailStopUncertainLifetime(
                        ref lifetimeUncertain,
                        "Maintenance pipe Begin failed after ownership " +
                        "transfer.",
                        exception);
                    throw;
                }
                if (started == null ||
                    !Object.ReferenceEquals(
                        started.Identity,
                        identity))
                {
                    safeToDispose = false;
                    FailStopUncertainLifetime(
                        ref lifetimeUncertain,
                        "Maintenance pipe Begin identity is uncertain.",
                        null);
                }
                if (started.Disposition ==
                    MaintenancePipeBeginDisposition.RejectedBeforeIssue)
                {
                    throw new IOException(
                        "Maintenance pipe operation was rejected before issue.");
                }
                if (started.Disposition ==
                    MaintenancePipeBeginDisposition.UncertainAfterIssue)
                {
                    safeToDispose = false;
                    FailStopUncertainLifetime(
                        ref lifetimeUncertain,
                        "Maintenance pipe Begin reported uncertain issue.",
                        null);
                }

                MaintenancePipeCompletion completion;
                if (started.Disposition ==
                    MaintenancePipeBeginDisposition.Pending)
                {
                    safeToDispose = false;
                    try
                    {
                        completion = operation.Wait(
                            timeout,
                            cancellation);
                    }
                    catch
                    {
                        Exception cancellationFailure;
                        completion = CancelAndDrain(
                            operation,
                            identity,
                            ref lifetimeUncertain,
                            out cancellationFailure);
                        safeToDispose = true;
                        if (cancellationFailure != null)
                        {
                            throw new InvalidOperationException(
                                "Maintenance pipe operation cancellation " +
                                "failed after exact drain.",
                                cancellationFailure);
                        }
                        throw;
                    }
                    RequireCompletionIdentity(
                        completion,
                        identity,
                        ref lifetimeUncertain);
                    safeToDispose = true;
                }
                else if (started.Disposition ==
                    MaintenancePipeBeginDisposition.Immediate)
                {
                    completion = started.Completion;
                    RequireCompletionIdentity(
                        completion,
                        identity,
                        ref lifetimeUncertain);
                }
                else if (started.Disposition ==
                        MaintenancePipeBeginDisposition.PipeConnected &&
                    expectedKind ==
                        MaintenancePipeOperationKind.Connect)
                {
                    completion = started.Completion;
                    RequireCompletionIdentity(
                        completion,
                        identity,
                        ref lifetimeUncertain);
                }
                else
                {
                    safeToDispose = false;
                    FailStopUncertainLifetime(
                        ref lifetimeUncertain,
                        "Maintenance pipe begin disposition leaves issue " +
                        "ownership uncertain.",
                        null);
                    throw new InvalidOperationException(
                        "Unreachable uncertain begin disposition.");
                }
                return completion;
            }
            finally
            {
                if (!lifetimeUncertain && safeToDispose)
                {
                    try
                    {
                        operation.Dispose();
                    }
                    catch (Exception exception)
                    {
                        FailStopUncertainLifetime(
                            ref lifetimeUncertain,
                            "Maintenance pipe operation disposal failed.",
                            exception);
                    }
                }
                else if (!beginEntered && !lifetimeUncertain)
                {
                    operation.Dispose();
                }
            }
        }

        private MaintenancePipeCompletion CancelAndDrain(
            IMaintenancePipeOperation operation,
            MaintenancePipeOperationIdentity identity,
            ref bool lifetimeUncertain,
            out Exception cancellationFailure)
        {
            cancellationFailure = null;
            try
            {
                MaintenancePipeCancellation cancellation =
                    operation.Cancel();
                if (cancellation == null ||
                    !Object.ReferenceEquals(
                        cancellation.Identity,
                        identity) ||
                    (cancellation.Result !=
                        MaintenancePipeCancelResult.Cancelled &&
                    cancellation.Result !=
                        MaintenancePipeCancelResult.NotFound))
                {
                    throw new InvalidOperationException(
                        "Maintenance pipe cancellation identity or result " +
                        "is invalid.");
                }
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }
            MaintenancePipeCompletion drained;
            try
            {
                drained = operation.Drain();
            }
            catch (Exception exception)
            {
                FailStopUncertainLifetime(
                    ref lifetimeUncertain,
                    "Maintenance pipe operation could not be drained.",
                    exception);
                throw;
            }
            RequireCompletionIdentity(
                drained,
                identity,
                ref lifetimeUncertain);
            return drained;
        }

        private static void RequireIdentity(
            MaintenancePipeOperationIdentity identity,
            long activeGeneration,
            long expectedOperationGeneration,
            MaintenancePipeOperationKind expectedKind)
        {
            if (identity == null ||
                identity.ConnectionGeneration != activeGeneration ||
                identity.OperationGeneration !=
                    expectedOperationGeneration ||
                identity.Kind != expectedKind)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe operation identity differs.");
            }
        }

        private void RequireCompletionIdentity(
            MaintenancePipeCompletion completion,
            MaintenancePipeOperationIdentity identity,
            ref bool lifetimeUncertain)
        {
            if (completion == null ||
                !Object.ReferenceEquals(
                    completion.Identity,
                    identity))
            {
                FailStopUncertainLifetime(
                    ref lifetimeUncertain,
                    "Maintenance pipe completion identity is uncertain.",
                    null);
            }
        }

        private static void RequireSuccess(
            MaintenancePipeCompletion completion,
            long activeGeneration,
            string label)
        {
            if (completion == null ||
                completion.Identity == null ||
                completion.Identity.ConnectionGeneration !=
                    activeGeneration)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe completion generation is stale.");
            }
            if (completion.Status !=
                MaintenancePipeCompletionStatus.Success)
            {
                throw new IOException(
                    "Maintenance pipe " + label + " failed with " +
                    completion.Status + ".");
            }
        }

        private static void RequireReadable(
            MaintenancePipeCompletion completion,
            long activeGeneration,
            int requested,
            string label)
        {
            if (completion == null ||
                completion.Identity == null ||
                completion.Identity.ConnectionGeneration !=
                    activeGeneration)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe completion generation is stale.");
            }
            if ((completion.Status !=
                    MaintenancePipeCompletionStatus.Success &&
                completion.Status !=
                    MaintenancePipeCompletionStatus.MoreData) ||
                completion.BytesTransferred <= 0 ||
                completion.BytesTransferred > requested)
            {
                throw new IOException(
                    "Maintenance pipe " + label +
                    " read did not complete with bounded data.");
            }
        }

        private void SetState(
            MaintenancePipeSlotState value,
            long activeGeneration)
        {
            State = value;
        }

        private void FailStopUncertainLifetime(
            ref bool lifetimeUncertain,
            string reason,
            Exception cause)
        {
            lifetimeUncertain = true;
            poisoned = true;
            Exception failStopCause = cause;
            try
            {
                terminator.Terminate(reason);
            }
            catch (Exception terminatorFailure)
            {
                failStopCause = terminatorFailure;
            }
            Environment.FailFast(reason, failStopCause);
            throw new InvalidOperationException(
                "Environment.FailFast returned after uncertain pipe " +
                "operation lifetime.",
                failStopCause);
        }
    }
}
