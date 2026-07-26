using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SBMSSetup
{
    internal sealed class TransportOperationPlan
    {
        internal MaintenancePipeOperationKind Kind;
        internal MaintenancePipeBeginDisposition Begin;
        internal MaintenancePipeCompletionStatus Status;
        internal byte[] Payload;
        internal int ReportedBytes = -1;
        internal int ShortBy;
        internal Exception WaitFailure;
        internal Action OnWait;
        internal Exception CancelFailure;
        internal Exception BeginFailure;
        internal Exception IdentityFailure;
        internal Exception DrainFailure;
        internal Exception DisposeFailure;
        internal bool TerminalBeforeCancel;
        internal MaintenancePipeCancelResult CancelResult =
            MaintenancePipeCancelResult.Cancelled;
        internal MaintenancePipeCompletionStatus DrainStatus =
            MaintenancePipeCompletionStatus.Aborted;
        internal long CompletionGeneration;
    }

    internal sealed class TransportFakeOperation
        : IMaintenancePipeOperation
    {
        private readonly TransportOperationPlan plan;
        private readonly byte[] buffer;
        private readonly int offset;
        private readonly int count;
        private readonly List<string> events;
        private readonly MaintenancePipeOperationIdentity identity;
        private bool began;
        private bool terminal;
        private bool drained;
        private bool disposed;
        private bool rejectedBeforeIssue;

        internal TransportFakeOperation(
            MaintenancePipeOperationIdentity identity,
            TransportOperationPlan plan,
            byte[] buffer,
            int offset,
            int count,
            List<string> events)
        {
            this.identity = identity;
            this.plan = plan;
            this.buffer = buffer;
            this.offset = offset;
            this.count = count;
            this.events = events;
        }

        public MaintenancePipeOperationIdentity Identity
        {
            get
            {
                if (plan.IdentityFailure != null)
                {
                    throw plan.IdentityFailure;
                }
                return identity;
            }
        }
        private MaintenancePipeOperationKind Kind
        {
            get { return identity.Kind; }
        }
        internal bool IsDisposed { get { return disposed; } }

        public MaintenancePipeBeginResult Begin()
        {
            if (began)
            {
                throw new ApplicationException(
                    "Operation began more than once.");
            }
            began = true;
            events.Add("Begin-" + Kind);
            if (plan.BeginFailure != null)
            {
                throw plan.BeginFailure;
            }
            if (plan.Begin ==
                MaintenancePipeBeginDisposition.RejectedBeforeIssue)
            {
                rejectedBeforeIssue = true;
            }
            if (plan.Begin ==
                MaintenancePipeBeginDisposition.Pending)
            {
                return new MaintenancePipeBeginResult(
                    Identity,
                    plan.Begin,
                    null);
            }
            MaintenancePipeCompletion completion = Complete(
                plan.Status);
            return new MaintenancePipeBeginResult(
                Identity,
                plan.Begin,
                completion);
        }

        public MaintenancePipeCompletion Wait(
            TimeSpan timeout,
            CancellationToken cancellation)
        {
            events.Add(
                "Wait-" + Kind + "-" +
                timeout.Ticks);
            if (plan.OnWait != null)
            {
                plan.OnWait();
            }
            if (plan.TerminalBeforeCancel)
            {
                terminal = true;
            }
            if (plan.WaitFailure != null)
            {
                throw plan.WaitFailure;
            }
            return Complete(plan.Status);
        }

        public MaintenancePipeCancellation Cancel()
        {
            events.Add("Cancel-" + Kind);
            if (!began || disposed)
            {
                throw new ApplicationException(
                    "Cancellation targeted an invalid operation.");
            }
            if (plan.CancelFailure != null)
            {
                throw plan.CancelFailure;
            }
            return new MaintenancePipeCancellation(
                Identity,
                plan.CancelResult);
        }

        public MaintenancePipeCompletion Drain()
        {
            events.Add("Drain-" + Kind);
            if (!began || disposed)
            {
                throw new ApplicationException(
                    "Drain targeted an invalid operation.");
            }
            if (plan.DrainFailure != null)
            {
                throw plan.DrainFailure;
            }
            terminal = true;
            drained = true;
            return NewCompletion(
                plan.TerminalBeforeCancel
                    ? plan.Status
                    : plan.DrainStatus);
        }

        public void Dispose()
        {
            events.Add("Dispose-" + Kind);
            if (disposed)
            {
                throw new ApplicationException(
                    "Operation disposed more than once.");
            }
            if (!rejectedBeforeIssue &&
                began &&
                (!terminal || !drained))
            {
                throw new ApplicationException(
                    "Operation disposed before terminal drain.");
            }
            disposed = true;
            if (plan.DisposeFailure != null)
            {
                throw plan.DisposeFailure;
            }
        }

        private MaintenancePipeCompletion Complete(
            MaintenancePipeCompletionStatus status)
        {
            terminal = true;
            drained = true;
            if (buffer != null &&
                plan.Payload != null &&
                (status == MaintenancePipeCompletionStatus.Success ||
                 status == MaintenancePipeCompletionStatus.MoreData))
            {
                int copied = Math.Min(
                    count,
                    plan.Payload.Length);
                Buffer.BlockCopy(
                    plan.Payload,
                    0,
                    buffer,
                    offset,
                    copied);
            }
            events.Add(
                Kind == MaintenancePipeOperationKind.Read
                    ? "read"
                    : Kind == MaintenancePipeOperationKind.Write
                        ? "write"
                        : Kind == MaintenancePipeOperationKind.Ack
                            ? "ack"
                            : "connect");
            return NewCompletion(status);
        }

        private MaintenancePipeCompletion NewCompletion(
            MaintenancePipeCompletionStatus status)
        {
            int bytes;
            if (plan.ReportedBytes >= 0)
            {
                bytes = plan.ReportedBytes;
            }
            else if (Kind ==
                MaintenancePipeOperationKind.Write)
            {
                bytes = Math.Max(0, count - plan.ShortBy);
            }
            else
            {
                bytes =
                    plan.Payload == null
                        ? 0
                        : plan.Payload.Length;
            }
            return new MaintenancePipeCompletion(
                plan.CompletionGeneration == 0
                    ? identity
                    : new MaintenancePipeOperationIdentity(
                        plan.CompletionGeneration,
                        identity.OperationGeneration,
                        identity.Kind),
                status,
                bytes);
        }
    }

    internal sealed class TransportFakeConnection
        : IMaintenancePipeConnection
    {
        private readonly Queue<TransportOperationPlan> plans;
        private readonly List<TransportFakeOperation> operations =
            new List<TransportFakeOperation>();
        private readonly Exception disconnectFailure;
        private readonly Exception disposeFailure;
        private long operationGeneration;
        internal readonly List<string> Events;
        internal readonly List<byte[]> Writes =
            new List<byte[]>();
        internal int ReadCreates;
        internal int AckCreates;
        internal int WriteCreates;
        internal int DisconnectCalls;
        internal int DisposeCalls;

        internal TransportFakeConnection(
            long generation,
            IEnumerable<TransportOperationPlan> values,
            List<string> events,
            Exception disconnectFailure,
            Exception disposeFailure)
        {
            Generation = generation;
            plans =
                new Queue<TransportOperationPlan>(values);
            Events = events;
            this.disconnectFailure = disconnectFailure;
            this.disposeFailure = disposeFailure;
        }

        public long Generation { get; private set; }
        public MaintenancePipeTransferMode TransferMode
        {
            get
            {
                return MaintenancePipeTransferMode.
                    MessageModeSingleMessage;
            }
        }

        public IMaintenancePipeOperation CreateConnectOperation()
        {
            return Create(
                MaintenancePipeOperationKind.Connect,
                null,
                0,
                0);
        }

        public IMaintenancePipeOperation CreateReadOperation(
            byte[] buffer,
            int offset,
            int count,
            MaintenancePipeOperationKind kind)
        {
            if (kind == MaintenancePipeOperationKind.Read)
            {
                ReadCreates++;
            }
            else if (kind == MaintenancePipeOperationKind.Ack)
            {
                AckCreates++;
            }
            else
            {
                throw new InvalidOperationException(
                    "Invalid fake read operation kind.");
            }
            return Create(kind, buffer, offset, count);
        }

        public IMaintenancePipeOperation CreateWriteOperation(
            byte[] buffer,
            int offset,
            int count)
        {
            WriteCreates++;
            var copy = new byte[count];
            Buffer.BlockCopy(
                buffer,
                offset,
                copy,
                0,
                count);
            Writes.Add(copy);
            return Create(
                MaintenancePipeOperationKind.Write,
                buffer,
                offset,
                count);
        }

        public void Disconnect()
        {
            Events.Add("disconnect");
            DisconnectCalls++;
            if (DisconnectCalls != 1)
            {
                throw new ApplicationException(
                    "Connection disconnected more than once.");
            }
            foreach (TransportFakeOperation operation in operations)
            {
                if (!operation.IsDisposed)
                {
                    throw new ApplicationException(
                        "Connection disconnected before operation disposal: " +
                        operation.Identity.Kind + " events=" +
                        String.Join(",", Events));
                }
            }
            if (disconnectFailure != null)
            {
                throw disconnectFailure;
            }
        }

        public void Dispose()
        {
            Events.Add("connection-dispose");
            DisposeCalls++;
            if (DisposeCalls != 1 ||
                DisconnectCalls != 1)
            {
                throw new ApplicationException(
                    "Connection lifetime was not exactly once.");
            }
            if (disposeFailure != null)
            {
                throw disposeFailure;
            }
        }

        private IMaintenancePipeOperation Create(
            MaintenancePipeOperationKind kind,
            byte[] buffer,
            int offset,
            int count)
        {
            if (plans.Count == 0)
            {
                throw new InvalidOperationException(
                    "Unexpected pipe operation " + kind + ".");
            }
            TransportOperationPlan plan = plans.Dequeue();
            if (plan.Kind != kind)
            {
                throw new InvalidOperationException(
                    "Expected " + plan.Kind + " but created " + kind + ".");
            }
            var operation =
                new TransportFakeOperation(
                    new MaintenancePipeOperationIdentity(
                        Generation,
                        checked(++operationGeneration),
                        kind),
                    plan,
                    buffer,
                    offset,
                    count,
                    Events);
            operations.Add(operation);
            return operation;
        }
    }

    internal sealed class TransportFakeConnectionFactory
        : IMaintenancePipeConnectionFactory
    {
        private readonly Queue<
            IList<TransportOperationPlan>> scripts =
                new Queue<IList<TransportOperationPlan>>();
        private readonly List<string> events;
        internal readonly List<TransportFakeConnection> Connections =
            new List<TransportFakeConnection>();
        internal Exception DisconnectFailure;
        internal Exception DisposeFailure;
        internal Exception CreateFailure;

        internal TransportFakeConnectionFactory(
            List<string> events)
        {
            this.events = events;
        }

        internal void Add(
            IList<TransportOperationPlan> plans)
        {
            scripts.Enqueue(plans);
        }

        public IMaintenancePipeConnection Create(long generation)
        {
            events.Add("create");
            if (CreateFailure != null)
            {
                throw CreateFailure;
            }
            if (scripts.Count == 0)
            {
                throw new InvalidOperationException(
                    "Missing fake connection script.");
            }
            var connection =
                new TransportFakeConnection(
                    generation,
                    scripts.Dequeue(),
                    events,
                    DisconnectFailure,
                    DisposeFailure);
            Connections.Add(connection);
            return connection;
        }
    }

    internal sealed class TransportRequestParser
        : IMaintenancePipeRequestParser
    {
        private readonly List<string> events;
        private readonly MaintenancePipeRequestParser inner =
            new MaintenancePipeRequestParser();

        internal TransportRequestParser(List<string> events)
        {
            this.events = events;
        }

        public PayloadBrokerCommand Parse(byte[] frozenRequest)
        {
            events.Add("parse");
            return inner.Parse(frozenRequest);
        }
    }

    internal sealed class TransportDispatcher
        : IMaintenancePreauthorizedCommandDispatcher
    {
        private readonly List<string> events;
        private readonly Func<
                PayloadBrokerCommand,
                PayloadBrokerResponse> execute;
        private readonly MaintenanceWriteBeforeAckExecutor replay;
        internal int Calls;

        internal TransportDispatcher(
            List<string> events,
            Func<
                PayloadBrokerCommand,
                PayloadBrokerResponse> execute)
        {
            this.events = events;
            this.execute = execute;
            var store = new FakeReplayStore();
            replay =
                new MaintenanceWriteBeforeAckExecutor(
                    store,
                    store.RootAuthorityInvariantDigest);
        }

        public MaintenanceCommittedResponse Dispatch(
            PayloadBrokerCommand command,
            MaintenanceAuthorizationEvidence authorization,
            CancellationToken cancellation)
        {
            Calls++;
            events.Add("dispatch");
            if (authorization == null)
            {
                throw new InvalidOperationException(
                    "Transport dispatcher lost typed authorization.");
            }
            return replay.ExecuteCommitted(
                command,
                execute,
                execute);
        }
    }

    internal sealed class BlockingTransportCapture
        : IMaintenanceClientTokenCapture
    {
        private readonly List<string> events;
        private readonly ManualResetEvent entered;
        private readonly ManualResetEvent release;
        private readonly MaintenanceClientTokenEvidence evidence;

        internal BlockingTransportCapture(
            List<string> events,
            ManualResetEvent entered,
            ManualResetEvent release,
            MaintenanceClientTokenEvidence evidence)
        {
            this.events = events;
            this.entered = entered;
            this.release = release;
            this.evidence = evidence;
        }

        public MaintenanceClientTokenEvidence Capture()
        {
            events.Add("capture");
            entered.Set();
            if (!release.WaitOne(5000))
            {
                throw new TimeoutException(
                    "Blocking capture was not released.");
            }
            return evidence;
        }
    }

    internal sealed class ThrowingTransportCapture
        : IMaintenanceClientTokenCapture
    {
        private readonly List<string> events;

        internal ThrowingTransportCapture(List<string> events)
        {
            this.events = events;
        }

        public MaintenanceClientTokenEvidence Capture()
        {
            events.Add("capture");
            throw new IOException("capture failure");
        }
    }

    internal sealed class MaintenancePipeTransportHarness
    {
        internal readonly List<string> Events;
        internal readonly TransportFakeConnectionFactory Factory;
        internal readonly TransportDispatcher Dispatcher;
        internal readonly SequenceImpersonationRunner Impersonation;
        internal readonly FakeTerminator Terminator;
        internal readonly MaintenancePipeTransportSlot Slot;

        internal MaintenancePipeTransportHarness(
            IList<TransportOperationPlan> plans,
            MaintenanceClientTokenEvidence evidence,
            Func<PayloadBrokerCommand, PayloadBrokerResponse> execute,
            IMaintenanceClientTokenCapture capture,
            List<string> sharedEvents)
        {
            Events = sharedEvents ?? new List<string>();
            Factory =
                new TransportFakeConnectionFactory(Events);
            Factory.Add(plans);
            Impersonation =
                new SequenceImpersonationRunner(Events);
            IMaintenanceClientTokenCapture actualCapture = capture;
            if (actualCapture == null)
            {
                var sequenceCapture =
                    new SequenceTokenCapture(
                        Impersonation,
                        Events);
                sequenceCapture.Evidence = evidence;
                actualCapture = sequenceCapture;
            }
            var captureRunner =
                new MaintenanceClientCaptureRunner(
                    Impersonation,
                    actualCapture,
                    new FakeTerminator());
            Terminator = new FakeTerminator();
            var policy =
                new SequencePolicyAuthorizer(
                    new MaintenanceProductionClientPolicyAuthorizer(),
                    Events);
            Dispatcher =
                new TransportDispatcher(
                    Events,
                    execute);
            var sequencer =
                new MaintenanceClientRequestSequencer(
                    captureRunner,
                    policy,
                    Dispatcher);
            var executor =
                new MaintenanceConnectedRequestExecutor(
                    sequencer);
            Slot =
                new MaintenancePipeTransportSlot(
                    Factory,
                    executor,
                    Timeouts(),
                    new TransportRequestParser(Events),
                    Terminator,
                    true);
        }

        internal static MaintenancePipeTransportTimeouts Timeouts()
        {
            return new MaintenancePipeTransportTimeouts(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(4));
        }
    }

    internal static class MaintenancePipeTransportContractTests
    {
        internal static void Run()
        {
            RunCase(
                "begin modes and timeouts",
                BeginModesAndTimeoutsAreBounded);
            RunCase(
                "read and authorization ordering",
                ReadAndAuthorizationOrderingIsStrict);
            RunCase(
                "cancellation drain",
                CancellationAlwaysDrainsExactOperation);
            RunCase(
                "concurrency and poison",
                ConcurrencyAndPoisonAreFailClosed);
            RunCase(
                "write ack and generation",
                WriteAckAndGenerationFailuresAreBounded);
            RunCase(
                "uncertain lifetime fail-stop",
                UncertainLifetimeAlwaysFailStops);
            RunCase(
                "capture and parse failures",
                CaptureAndParseFailuresAreFailClosed);
            RunCase(
                "capture and dispatch cancellation",
                CaptureAndDispatchAreNotDisconnectedConcurrently);
            RunCase(
                "lost response replay",
                LostResponseWriteReplaysWithoutMutation);
        }

        private static void RunCase(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    name + ": " + exception.Message,
                    exception);
            }
        }

        private static void BeginModesAndTimeoutsAreBounded()
        {
            foreach (MaintenancePipeBeginDisposition begin in
                new[]
                {
                    MaintenancePipeBeginDisposition.Immediate,
                    MaintenancePipeBeginDisposition.Pending,
                    MaintenancePipeBeginDisposition.PipeConnected
                })
            {
                IList<TransportOperationPlan> plans =
                    SuccessPlans();
                plans[0].Begin = begin;
                var harness = Harness(plans);
                harness.Slot.RunOne(CancellationToken.None);
                Assert(
                    harness.Slot.State ==
                        MaintenancePipeSlotState.Complete &&
                    harness.Factory.Connections[0].
                        DisconnectCalls == 1,
                    "Connect begin mode did not complete: " + begin);
            }

            IList<TransportOperationPlan> pending =
                SuccessPlans();
            for (int index = 0; index < pending.Count; ++index)
            {
                pending[index].Begin =
                    MaintenancePipeBeginDisposition.Pending;
            }
            var timed = Harness(pending);
            timed.Slot.RunOne(CancellationToken.None);
            Assert(
                ContainsWaitDeadline(
                    timed.Events,
                    MaintenancePipeOperationKind.Connect,
                    1) &&
                ContainsWaitDeadline(
                    timed.Events,
                    MaintenancePipeOperationKind.Read,
                    2) &&
                ContainsWaitDeadline(
                    timed.Events,
                    MaintenancePipeOperationKind.Write,
                    3) &&
                ContainsWaitDeadline(
                    timed.Events,
                    MaintenancePipeOperationKind.Ack,
                    4),
                "Operation waits did not receive fixed bounded deadlines.");

            IList<TransportOperationPlan> fatal =
                SuccessPlans();
            fatal[0].Begin =
                MaintenancePipeBeginDisposition.RejectedBeforeIssue;
            fatal[0].Status =
                MaintenancePipeCompletionStatus.Success;
            var failed = Harness(fatal);
            Reject(
                delegate
                {
                    failed.Slot.RunOne(
                        CancellationToken.None);
                },
                "fatal connect");
            Assert(
                failed.Dispatcher.Calls == 0 &&
                failed.Slot.State ==
                    MaintenancePipeSlotState.Faulted,
                "Fatal connect reached dispatch.");

            RejectArgument(
                delegate
                {
                    new MaintenancePipeTransportTimeouts(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(1));
                });
            RejectArgument(
                delegate
                {
                    new MaintenancePipeTransportTimeouts(
                        TimeSpan.FromSeconds(31),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(1));
                });

            IList<TransportOperationPlan> identityGetter =
                SuccessPlans();
            identityGetter[0].IdentityFailure =
                new IOException("identity getter failure");
            var identityHarness = Harness(identityGetter);
            Reject(
                delegate
                {
                    identityHarness.Slot.RunOne(
                        CancellationToken.None);
                },
                "identity getter failure");
            Assert(
                IndexOf(identityHarness.Events, "Begin-Connect") < 0 &&
                CountOf(identityHarness.Events, "Dispose-Connect") == 1 &&
                identityHarness.Factory.Connections[0].
                    DisconnectCalls == 1 &&
                identityHarness.Factory.Connections[0].
                    DisposeCalls == 1,
                "Identity getter failure began the operation or did not " +
                "dispose and clean the connection exactly once.");
        }

        private static void ReadAndAuthorizationOrderingIsStrict()
        {
            var harness = Harness(SuccessPlans());
            harness.Slot.RunOne(CancellationToken.None);
            RequireOrder(
                harness.Events,
                new[]
                {
                    "read",
                    "capture",
                    "revert",
                    "authorize",
                    "parse",
                    "dispatch",
                    "write",
                    "ack"
                });
            TransportFakeConnection connection =
                harness.Factory.Connections[0];
            Assert(
                connection.ReadCreates == 1 &&
                connection.WriteCreates == 1 &&
                connection.AckCreates == 1,
                "One request used extra pipe I/O.");

            foreach (TransportOperationPlan read in
                new[]
                {
                    ReadFailure(
                        MaintenancePipeCompletionStatus.MoreData,
                        new byte[] { 1 },
                        -1),
                    ReadFailure(
                        MaintenancePipeCompletionStatus.Success,
                        null,
                        0),
                    ReadFailure(
                        MaintenancePipeCompletionStatus.BrokenPipe,
                        null,
                        0),
                    ReadFailure(
                        MaintenancePipeCompletionStatus.MoreData,
                        new byte[
                            MaintenancePipeTransportSlot.
                                MaxRequestFrameLength],
                        -1)
                })
            {
                IList<TransportOperationPlan> plans =
                    SuccessPlans();
                plans[1] = read;
                var rejected = Harness(plans);
                Reject(
                    delegate
                    {
                        rejected.Slot.RunOne(
                            CancellationToken.None);
                    },
                    "invalid read");
                Assert(
                    rejected.Dispatcher.Calls == 0 &&
                    rejected.Factory.Connections[0].
                        ReadCreates == 1 &&
                    rejected.Factory.Connections[0].
                        WriteCreates == 0,
                    "Invalid read dispatched or retried pipe I/O.");
            }

            var unauthorized = Harness(
                SuccessPlans(),
                DeniedEvidence(),
                null);
            RejectUnauthorized(
                delegate
                {
                    unauthorized.Slot.RunOne(
                        CancellationToken.None);
                });
            Assert(
                unauthorized.Dispatcher.Calls == 0 &&
                IndexOf(unauthorized.Events, "parse") < 0 &&
                unauthorized.Factory.Connections[0].
                    WriteCreates == 0,
                "Unauthorized request parsed or dispatched.");

            IList<TransportOperationPlan> malformed =
                SuccessPlans();
            malformed[1].Payload = new byte[] { 1, 2, 3, 4 };
            var malformedHarness = Harness(malformed);
            Reject(
                delegate
                {
                    malformedHarness.Slot.RunOne(
                        CancellationToken.None);
                },
                "malformed request frame");
            RequireOrder(
                malformedHarness.Events,
                new[] { "capture", "revert", "authorize", "parse" });
            Assert(
                malformedHarness.Dispatcher.Calls == 0 &&
                malformedHarness.Factory.Connections[0].
                    WriteCreates == 0,
                "Malformed request dispatched or wrote a response.");
        }

        private static void CancellationAlwaysDrainsExactOperation()
        {
            MaintenancePipeOperationKind[] stages =
            {
                MaintenancePipeOperationKind.Connect,
                MaintenancePipeOperationKind.Read,
                MaintenancePipeOperationKind.Write,
                MaintenancePipeOperationKind.Ack
            };
            for (int index = 0; index < stages.Length; ++index)
            {
                var cancellation = new CancellationTokenSource();
                IList<TransportOperationPlan> plans =
                    SuccessPlans();
                TransportOperationPlan target =
                    plans[index];
                target.Begin =
                    MaintenancePipeBeginDisposition.Pending;
                target.OnWait = cancellation.Cancel;
                target.WaitFailure =
                    new OperationCanceledException(
                        cancellation.Token);
                target.CancelResult =
                    index % 2 == 0
                        ? MaintenancePipeCancelResult.Cancelled
                        : MaintenancePipeCancelResult.NotFound;
                target.TerminalBeforeCancel =
                    target.CancelResult ==
                        MaintenancePipeCancelResult.NotFound;
                target.DrainStatus =
                    target.TerminalBeforeCancel
                        ? MaintenancePipeCompletionStatus.Success
                        : MaintenancePipeCompletionStatus.Aborted;
                var harness = Harness(plans);
                RejectCancellation(
                    delegate
                    {
                        harness.Slot.RunOne(
                            cancellation.Token);
                    });
                string suffix = stages[index].ToString();
                RequireOrder(
                    harness.Events,
                    new[]
                    {
                        "Cancel-" + suffix,
                        "Drain-" + suffix,
                        "Dispose-" + suffix,
                        "disconnect"
                    });
                Assert(
                    harness.Slot.State ==
                        MaintenancePipeSlotState.Stopping,
                    "Cancellation did not stop stage " +
                    stages[index] + ".");
            }

            var completedReadCancellation =
                new CancellationTokenSource();
            IList<TransportOperationPlan> completedRead =
                SuccessPlans();
            completedRead[1].Begin =
                MaintenancePipeBeginDisposition.Pending;
            completedRead[1].OnWait =
                completedReadCancellation.Cancel;
            var completedReadHarness = Harness(completedRead);
            RejectCancellation(
                delegate
                {
                    completedReadHarness.Slot.RunOne(
                        completedReadCancellation.Token);
                });
            Assert(
                IndexOf(completedReadHarness.Events, "capture") < 0 &&
                completedReadHarness.Dispatcher.Calls == 0 &&
                completedReadHarness.Factory.Connections[0].
                    DisconnectCalls == 1,
                "Cancellation observed after read completion captured or " +
                "dispatched a client.");

            foreach (bool throwCancel in new[] { true, false })
            {
                IList<TransportOperationPlan> cancelFault =
                    SuccessPlans();
                cancelFault[1].Begin =
                    MaintenancePipeBeginDisposition.Pending;
                cancelFault[1].WaitFailure =
                    new TimeoutException("read timeout");
                if (throwCancel)
                {
                    cancelFault[1].CancelFailure =
                        new IOException("cancel failure");
                }
                else
                {
                    cancelFault[1].CancelResult =
                        (MaintenancePipeCancelResult)99;
                }
                var cancelFaultHarness = Harness(cancelFault);
                Reject(
                    delegate
                    {
                        cancelFaultHarness.Slot.RunOne(
                            CancellationToken.None);
                    },
                    "cancel fault");
                RequireOrder(
                    cancelFaultHarness.Events,
                    new[]
                    {
                        "Cancel-Read",
                        "Drain-Read",
                        "Dispose-Read",
                        "disconnect"
                    });
            }
        }

        private static void ConcurrencyAndPoisonAreFailClosed()
        {
            var entered = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var events = new List<string>();
            var concurrentHarness =
                HarnessWithEvents(
                    SuccessPlans(),
                    TrustedEvidence(),
                    null,
                    new BlockingTransportCapture(
                        events,
                        entered,
                        release,
                        TrustedEvidence()),
                    events);
            Exception firstFailure = null;
            var thread = new Thread(
                new ThreadStart(
                    delegate
                    {
                        try
                        {
                            concurrentHarness.Slot.RunOne(
                                CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            firstFailure = exception;
                        }
                    }));
            thread.Start();
            Assert(
                entered.WaitOne(5000),
                "Concurrent slot test did not enter capture.");
            Reject(
                delegate
                {
                    concurrentHarness.Slot.RunOne(
                        CancellationToken.None);
                },
                "concurrent RunOne");
            release.Set();
            Assert(
                thread.Join(5000) &&
                firstFailure == null &&
                concurrentHarness.Factory.Connections.Count == 1,
                "Concurrent RunOne escaped the atomic slot guard.");

        }

        private static void WriteAckAndGenerationFailuresAreBounded()
        {
            IList<TransportOperationPlan> shortWrite =
                SuccessPlans();
            shortWrite[2].ShortBy = 1;
            var shortHarness = Harness(shortWrite);
            Reject(
                delegate
                {
                    shortHarness.Slot.RunOne(
                        CancellationToken.None);
                },
                "short write");
            Assert(
                shortHarness.Slot.LastResponseCommitted &&
                shortHarness.Factory.Connections[0].
                    AckCreates == 0,
                "Short write reached acknowledgement or lost commit.");

            IList<TransportOperationPlan> wrongAck =
                SuccessPlans();
            byte[] badAck = Copy(wrongAck[3].Payload);
            badAck[badAck.Length - 1] ^= 1;
            wrongAck[3].Payload = badAck;
            var ackHarness = Harness(wrongAck);
            Reject(
                delegate
                {
                    ackHarness.Slot.RunOne(
                        CancellationToken.None);
                },
                "ack mismatch");

            IList<TransportOperationPlan> timeout =
                SuccessPlans();
            timeout[3].Begin =
                MaintenancePipeBeginDisposition.Pending;
            timeout[3].WaitFailure =
                new TimeoutException("ack timeout");
            var timeoutHarness = Harness(timeout);
            RejectTimeout(
                delegate
                {
                    timeoutHarness.Slot.RunOne(
                        CancellationToken.None);
                });
            RequireOrder(
                timeoutHarness.Events,
                new[]
                {
                    "Cancel-Ack",
                    "Drain-Ack",
                    "Dispose-Ack",
                    "disconnect"
                });

            var monotonic = Harness(SuccessPlans());
            monotonic.Factory.Add(SuccessPlans());
            monotonic.Slot.RunOne(CancellationToken.None);
            long first = monotonic.Slot.Generation;
            monotonic.Slot.RunOne(CancellationToken.None);
            Assert(
                first == 1 &&
                monotonic.Slot.Generation == 2,
                "Slot generation was not monotonic.");
        }

        private static void UncertainLifetimeAlwaysFailStops()
        {
            foreach (string mode in new[]
            {
                "transport-begin-return",
                "transport-begin-throw",
                "transport-drain-return",
                "transport-drain-throw",
                "transport-dispose-return",
                "transport-dispose-throw",
                "transport-stale-return",
                "transport-stale-throw",
                "transport-invalid-return",
                "transport-invalid-throw",
                "transport-pipeconnected-return",
                "transport-pipeconnected-throw",
                "transport-factory-return",
                "transport-factory-throw",
                "transport-disconnect-return",
                "transport-disconnect-throw",
                "transport-connection-dispose-return",
                "transport-connection-dispose-throw"
            })
            {
                MaintenanceServiceRuntimeContractTests.
                    AssertNativeFailStopChild(mode);
            }
        }

        internal static int RunFailStopChild(
            string mode,
            string markerPath)
        {
            IList<TransportOperationPlan> plans = SuccessPlans();
            if (mode.StartsWith(
                    "transport-begin-",
                    StringComparison.Ordinal))
            {
                plans[0].BeginFailure =
                    new IOException("begin issue uncertainty");
            }
            else if (mode.StartsWith(
                    "transport-drain-",
                    StringComparison.Ordinal))
            {
                plans[1].Begin =
                    MaintenancePipeBeginDisposition.Pending;
                plans[1].WaitFailure =
                    new TimeoutException("read timeout");
                plans[1].DrainFailure =
                    new IOException("drain failure");
            }
            else if (mode.StartsWith(
                    "transport-dispose-",
                    StringComparison.Ordinal))
            {
                plans[0].DisposeFailure =
                    new IOException("dispose failure");
            }
            else if (mode.StartsWith(
                    "transport-stale-",
                    StringComparison.Ordinal))
            {
                plans[1].CompletionGeneration = 99;
            }
            else if (mode.StartsWith(
                    "transport-invalid-",
                    StringComparison.Ordinal))
            {
                plans[0].Begin =
                    (MaintenancePipeBeginDisposition)99;
            }
            else if (mode.StartsWith(
                    "transport-pipeconnected-",
                    StringComparison.Ordinal))
            {
                plans[1].Begin =
                    MaintenancePipeBeginDisposition.PipeConnected;
            }
            else if (mode.StartsWith(
                        "transport-factory-",
                        StringComparison.Ordinal) ||
                    mode.StartsWith(
                        "transport-disconnect-",
                        StringComparison.Ordinal) ||
                    mode.StartsWith(
                        "transport-connection-dispose-",
                        StringComparison.Ordinal))
            {
            }
            else
            {
                throw new InvalidOperationException(
                    "Unknown transport fail-stop child mode.");
            }

            var harness = Harness(plans);
            if (mode.StartsWith(
                    "transport-factory-",
                    StringComparison.Ordinal))
            {
                harness.Factory.CreateFailure =
                    new IOException("factory ownership uncertainty");
            }
            if (mode.StartsWith(
                    "transport-disconnect-",
                    StringComparison.Ordinal))
            {
                harness.Factory.DisconnectFailure =
                    new IOException("disconnect failure");
            }
            if (mode.StartsWith(
                    "transport-connection-dispose-",
                    StringComparison.Ordinal))
            {
                harness.Factory.DisposeFailure =
                    new IOException("connection dispose failure");
            }
            bool terminatorThrows =
                mode.EndsWith("-throw", StringComparison.Ordinal);
            harness.Terminator.OnTerminate =
                delegate
                {
                    File.AppendAllText(
                        markerPath,
                        "transport-events:" +
                        String.Join(",", harness.Events) +
                        Environment.NewLine);
                    bool connectionCleanupMode =
                        mode.StartsWith(
                            "transport-disconnect-",
                            StringComparison.Ordinal) ||
                        mode.StartsWith(
                            "transport-connection-dispose-",
                            StringComparison.Ordinal);
                    if (!connectionCleanupMode &&
                        harness.Events.Contains("disconnect"))
                    {
                        File.AppendAllText(
                            markerPath,
                            "unsafe-cleanup-before-terminate" +
                            Environment.NewLine);
                    }
                    bool operationUncertainMode =
                        mode.StartsWith(
                            "transport-begin-",
                            StringComparison.Ordinal) ||
                        mode.StartsWith(
                            "transport-drain-",
                            StringComparison.Ordinal) ||
                        mode.StartsWith(
                            "transport-stale-",
                            StringComparison.Ordinal) ||
                        mode.StartsWith(
                            "transport-invalid-",
                            StringComparison.Ordinal) ||
                        mode.StartsWith(
                            "transport-pipeconnected-",
                            StringComparison.Ordinal);
                    if (operationUncertainMode &&
                        harness.Events.Contains(
                            mode.StartsWith(
                                    "transport-begin-",
                                    StringComparison.Ordinal)
                                || mode.StartsWith(
                                    "transport-invalid-",
                                    StringComparison.Ordinal)
                                ? "Dispose-Connect"
                                : "Dispose-Read"))
                    {
                        File.AppendAllText(
                            markerPath,
                            "unsafe-operation-dispose-before-terminate" +
                            Environment.NewLine);
                    }
                    File.AppendAllText(
                        markerPath,
                        "terminator-enter:" +
                        (terminatorThrows ? "throw" : "return") +
                        Environment.NewLine);
                    if (terminatorThrows)
                    {
                        File.AppendAllText(
                            markerPath,
                            "terminator-throw" +
                            Environment.NewLine);
                        throw new ApplicationException(
                            "transport terminator sentinel");
                    }
                    File.AppendAllText(
                        markerPath,
                        "terminator-return" +
                        Environment.NewLine);
                };
            harness.Slot.RunOne(CancellationToken.None);
            File.AppendAllText(
                markerPath,
                "returned-after-failstop" + Environment.NewLine);
            return 98;
        }

        private static void CaptureAndDispatchAreNotDisconnectedConcurrently()
        {
            var captureEntered = new ManualResetEvent(false);
            var captureRelease = new ManualResetEvent(false);
            var events = new List<string>();
            var evidence = TrustedEvidence();
            var blockingCapture =
                new BlockingTransportCapture(
                    events,
                    captureEntered,
                    captureRelease,
                    evidence);
            var captureHarness =
                HarnessWithEvents(
                    SuccessPlans(),
                    evidence,
                    null,
                    blockingCapture,
                    events);
            var captureCancellation =
                new CancellationTokenSource();
            Exception captureFailure = null;
            var captureThread =
                new Thread(
                    new ThreadStart(
                        delegate
                        {
                            try
                            {
                                captureHarness.Slot.RunOne(
                                    captureCancellation.Token);
                            }
                            catch (Exception exception)
                            {
                                captureFailure = exception;
                            }
                        }));
            captureThread.Start();
            Assert(
                captureEntered.WaitOne(5000),
                "Capture stage did not block.");
            captureCancellation.Cancel();
            Assert(
                captureHarness.Factory.Connections[0].
                    DisconnectCalls == 0,
                "Cancellation disconnected during capture.");
            captureRelease.Set();
            Assert(
                captureThread.Join(5000) &&
                captureFailure is OperationCanceledException &&
                captureHarness.Factory.Connections[0].
                    DisconnectCalls == 1,
                "Capture cancellation did not close after return.");

            var dispatchEntered = new ManualResetEvent(false);
            var dispatchRelease = new ManualResetEvent(false);
            var dispatchCancellation =
                new CancellationTokenSource();
            MaintenancePipeTransportHarness dispatchHarness = null;
            dispatchHarness =
                Harness(
                    SuccessPlans(),
                    TrustedEvidence(),
                    delegate(PayloadBrokerCommand command)
                    {
                        dispatchEntered.Set();
                        if (!dispatchRelease.WaitOne(5000))
                        {
                            throw new TimeoutException(
                                "Dispatch was not released.");
                        }
                        return MaintenancePipeWireContractTests.
                            Response(command);
                    });
            Exception dispatchFailure = null;
            var dispatchThread =
                new Thread(
                    new ThreadStart(
                        delegate
                        {
                            try
                            {
                                dispatchHarness.Slot.RunOne(
                                    dispatchCancellation.Token);
                            }
                            catch (Exception exception)
                            {
                                dispatchFailure = exception;
                            }
                        }));
            dispatchThread.Start();
            Assert(
                dispatchEntered.WaitOne(5000),
                "Dispatch stage did not block.");
            dispatchCancellation.Cancel();
            Assert(
                dispatchHarness.Factory.Connections[0].
                    DisconnectCalls == 0,
                "Cancellation disconnected during dispatch.");
            dispatchRelease.Set();
            Assert(
                dispatchThread.Join(5000) &&
                dispatchFailure is OperationCanceledException &&
                dispatchHarness.Factory.Connections[0].
                    DisconnectCalls == 1,
                "Dispatch cancellation did not close after return.");

            IList<TransportOperationPlan> revertPlans =
                SuccessPlans();
            var revert = Harness(revertPlans);
            revert.Impersonation.RevertFailure =
                new IOException("revert failure");
            Reject(
                delegate
                {
                    revert.Slot.RunOne(
                        CancellationToken.None);
                },
                "revert fail-stop simulation");
            RequireOrder(
                revert.Events,
                new[] { "revert", "disconnect" });
            Assert(
                IndexOf(revert.Events, "parse") < 0 &&
                revert.Dispatcher.Calls == 0,
                "Revert failure parsed or dispatched.");
        }

        private static void CaptureAndParseFailuresAreFailClosed()
        {
            var events = new List<string>();
            var captureHarness =
                HarnessWithEvents(
                    SuccessPlans(),
                    TrustedEvidence(),
                    null,
                    new ThrowingTransportCapture(events),
                    events);
            Reject(
                delegate
                {
                    captureHarness.Slot.RunOne(
                        CancellationToken.None);
                },
                "capture failure");
            RequireOrder(
                captureHarness.Events,
                new[] { "capture", "revert", "disconnect" });
            Assert(
                IndexOf(captureHarness.Events, "parse") < 0 &&
                captureHarness.Dispatcher.Calls == 0,
                "Capture failure parsed or dispatched.");
        }

        private static void LostResponseWriteReplaysWithoutMutation()
        {
            int mutations = 0;
            Func<PayloadBrokerCommand, PayloadBrokerResponse> execute =
                delegate(PayloadBrokerCommand command)
                {
                    mutations++;
                    return MaintenancePipeWireContractTests.
                        Response(command);
                };

            IList<TransportOperationPlan> lost =
                SuccessPlans();
            lost[2].Status =
                MaintenancePipeCompletionStatus.BrokenPipe;
            var harness =
                Harness(
                    lost,
                    TrustedEvidence(),
                    execute);
            harness.Factory.Add(SuccessPlans());
            Reject(
                delegate
                {
                    harness.Slot.RunOne(
                        CancellationToken.None);
                },
                "lost response write");
            Assert(
                harness.Slot.LastResponseCommitted &&
                mutations == 1,
                "Lost write was not committed before transport failure.");
            PayloadBrokerResponse replayed =
                harness.Slot.RunOne(
                    CancellationToken.None);
            Assert(
                mutations == 1 &&
                replayed.InvariantDigest ==
                    MaintenancePipeWireContractTests.
                        Response(
                            MaintenancePipeWireContractTests.
                                Command()).InvariantDigest &&
                Equal(
                    harness.Factory.Connections[0].Writes[0],
                    harness.Factory.Connections[1].Writes[0]),
                "Retry remutated or changed the exact response frame.");
        }

        private static MaintenancePipeTransportHarness Harness(
            IList<TransportOperationPlan> plans)
        {
            return Harness(
                plans,
                TrustedEvidence(),
                null);
        }

        private static MaintenancePipeTransportHarness Harness(
            IList<TransportOperationPlan> plans,
            MaintenanceClientTokenEvidence evidence,
            Func<PayloadBrokerCommand, PayloadBrokerResponse> execute)
        {
            return new MaintenancePipeTransportHarness(
                plans,
                evidence,
                execute ??
                    delegate(PayloadBrokerCommand command)
                    {
                        return MaintenancePipeWireContractTests.
                            Response(command);
                    },
                null,
                null);
        }

        private static MaintenancePipeTransportHarness HarnessWithEvents(
            IList<TransportOperationPlan> plans,
            MaintenanceClientTokenEvidence evidence,
            Func<PayloadBrokerCommand, PayloadBrokerResponse> execute,
            IMaintenanceClientTokenCapture capture,
            List<string> events)
        {
            var harness =
                new MaintenancePipeTransportHarness(
                    plans,
                    evidence,
                    execute ??
                        delegate(PayloadBrokerCommand command)
                        {
                            return MaintenancePipeWireContractTests.
                                Response(command);
                    },
                    capture,
                    events);
            return harness;
        }

        private static IList<TransportOperationPlan> SuccessPlans()
        {
            PayloadBrokerCommand command =
                MaintenancePipeWireContractTests.Command();
            byte[] requestPayload =
                PayloadBrokerCommandCodec.SerializeCanonical(command);
            byte[] requestFrame =
                MaintenancePipeFrameCodec.Encode(
                    MaintenancePipeFrameKind.Request,
                    requestPayload);
            byte[] responsePayload =
                PayloadBrokerResponseCodec.SerializeCanonical(
                    MaintenancePipeWireContractTests.
                        Response(command));
            byte[] ack =
                MaintenancePipeFrameCodec.EncodeAck(
                    responsePayload);
            return new List<TransportOperationPlan>
            {
                new TransportOperationPlan
                {
                    Kind = MaintenancePipeOperationKind.Connect,
                    Begin = MaintenancePipeBeginDisposition.Immediate,
                    Status = MaintenancePipeCompletionStatus.Success
                },
                new TransportOperationPlan
                {
                    Kind = MaintenancePipeOperationKind.Read,
                    Begin = MaintenancePipeBeginDisposition.Immediate,
                    Status = MaintenancePipeCompletionStatus.Success,
                    Payload = requestFrame
                },
                new TransportOperationPlan
                {
                    Kind = MaintenancePipeOperationKind.Write,
                    Begin = MaintenancePipeBeginDisposition.Immediate,
                    Status = MaintenancePipeCompletionStatus.Success
                },
                new TransportOperationPlan
                {
                    Kind = MaintenancePipeOperationKind.Ack,
                    Begin = MaintenancePipeBeginDisposition.Immediate,
                    Status = MaintenancePipeCompletionStatus.Success,
                    Payload = ack
                }
            };
        }

        private static TransportOperationPlan ReadFailure(
            MaintenancePipeCompletionStatus status,
            byte[] payload,
            int reportedBytes)
        {
            return new TransportOperationPlan
            {
                Kind = MaintenancePipeOperationKind.Read,
                Begin = MaintenancePipeBeginDisposition.Immediate,
                Status = status,
                Payload = payload,
                ReportedBytes = reportedBytes
            };
        }

        private static MaintenanceClientTokenEvidence TrustedEvidence()
        {
            return new MaintenanceClientTokenEvidence(
                "S-1-5-18",
                new MaintenanceClientTokenGroupEvidence[0],
                false,
                MaintenanceTokenElevationType.Default,
                0x00004000,
                false,
                false,
                MaintenanceClientTokenType.Impersonation,
                MaintenanceClientImpersonationLevel.Impersonation,
                1);
        }

        private static MaintenanceClientTokenEvidence DeniedEvidence()
        {
            return new MaintenanceClientTokenEvidence(
                "S-1-5-21-1-2-3-1001",
                new MaintenanceClientTokenGroupEvidence[0],
                false,
                MaintenanceTokenElevationType.Default,
                0x00002000,
                false,
                false,
                MaintenanceClientTokenType.Impersonation,
                MaintenanceClientImpersonationLevel.Impersonation,
                2);
        }

        private static bool ContainsWaitDeadline(
            IList<string> events,
            MaintenancePipeOperationKind kind,
            int seconds)
        {
            long expected = TimeSpan.FromSeconds(seconds).Ticks;
            return IndexOf(
                events,
                "Wait-" + kind + "-" + expected) >= 0;
        }

        private static void RequireOrder(
            IList<string> events,
            string[] ordered)
        {
            int previous = -1;
            foreach (string item in ordered)
            {
                int current = IndexOf(events, item);
                Assert(
                    current > previous,
                    "Missing or out-of-order event " + item +
                    ": " + String.Join(",", events));
                previous = current;
            }
        }

        private static int IndexOf(
            IList<string> events,
            string value)
        {
            for (int index = 0; index < events.Count; ++index)
            {
                if (String.Equals(
                        events[index],
                        value,
                        StringComparison.Ordinal))
                {
                    return index;
                }
            }
            return -1;
        }

        private static int CountOf(
            IList<string> events,
            string value)
        {
            int count = 0;
            for (int index = 0; index < events.Count; ++index)
            {
                if (String.Equals(
                        events[index],
                        value,
                        StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return count;
        }

        private static byte[] Copy(byte[] value)
        {
            var copy = new byte[value.Length];
            Buffer.BlockCopy(
                value,
                0,
                copy,
                0,
                value.Length);
            return copy;
        }

        private static bool Equal(byte[] first, byte[] second)
        {
            if (first == null ||
                second == null ||
                first.Length != second.Length)
            {
                return false;
            }
            int difference = 0;
            for (int index = 0; index < first.Length; ++index)
            {
                difference |= first[index] ^ second[index];
            }
            return difference == 0;
        }

        private static void Reject(Action action, string label)
        {
            try
            {
                action();
            }
            catch (IOException)
            {
                return;
            }
            catch (InvalidDataException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected transport rejection: " + label + ".");
        }

        private static void RejectUnauthorized(Action action)
        {
            try
            {
                action();
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected unauthorized transport rejection.");
        }

        private static void RejectCancellation(Action action)
        {
            try
            {
                action();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected transport cancellation.");
        }

        private static void RejectTimeout(Action action)
        {
            try
            {
                action();
            }
            catch (TimeoutException)
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected transport timeout.");
        }

        private static void RejectArgument(Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected bounded timeout rejection.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
