using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace SBMSSetup
{
    internal sealed class ReplayNativeFaultSeam
        : IWindowsJournalIoTestSeam
    {
        internal bool FailNextPublishedVerification;
        public void AfterTrustedInstallerRootOpened(string expectedPath) { }
        public void AfterBackupPublished() { }
        public void BeforePublishedFileIdVerification(
            string destinationRelativePath)
        {
            if (FailNextPublishedVerification)
            {
                FailNextPublishedVerification = false;
                throw new IOException(
                    "Injected post-publication verification failure.");
            }
        }
        public int BeforeNativeIo(string operation, int attempt)
        {
            return 0;
        }
    }

    internal sealed class TestProgramDataPathProvider
        : IInstallerProgramDataPathProvider
    {
        private readonly string root;
        internal TestProgramDataPathProvider(string root)
        {
            this.root = root;
        }
        public string GetCommonApplicationDataPath()
        {
            return root;
        }
    }

    internal sealed class NoOpInstallerJournalAclPolicy
        : IInstallerJournalAclPolicy
    {
        public void PrepareAndVerify(
            string commonApplicationDataRoot,
            string installerStateRoot,
            bool createIfMissing)
        {
        }
    }

    internal sealed class BlockingInstallerJournalAclPolicy
        : IInstallerJournalAclPolicy
    {
        internal readonly ManualResetEvent Entered =
            new ManualResetEvent(false);
        internal readonly ManualResetEvent Release =
            new ManualResetEvent(false);

        public void PrepareAndVerify(
            string commonApplicationDataRoot,
            string installerStateRoot,
            bool createIfMissing)
        {
            Entered.Set();
            if (!Release.WaitOne(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "Lifecycle ACL barrier was not released.");
            }
        }
    }

    internal sealed class LifecycleBarrierFileSystem
        : IAtomicJournalFileSystem,
          IJournalStorageAuthorityDescriptor,
          IDisposable
    {
        internal bool BlockAuthority;
        internal readonly ManualResetEvent AuthorityEntered =
            new ManualResetEvent(false);
        internal readonly ManualResetEvent ReleaseAuthority =
            new ManualResetEvent(false);
        internal volatile bool Disposed;

        public string StorageAuthorityInvariantDigest
        {
            get
            {
                AuthorityEntered.Set();
                if (BlockAuthority &&
                    !ReleaseAuthority.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Lifecycle authority barrier was not released.");
                }
                return new string('a', 64);
            }
        }

        public string GetDisplayPath(string relativePath)
        {
            return relativePath;
        }
        public bool FileExists(string relativePath)
        {
            throw new NotSupportedException();
        }
        public void EnsureDirectory(string relativePath)
        {
            throw new NotSupportedException();
        }
        public Stream CreateNewFile(string relativePath)
        {
            throw new NotSupportedException();
        }
        public Stream OpenReadFile(string relativePath)
        {
            throw new NotSupportedException();
        }
        public void PublishNewFile(
            string sourceRelativePath,
            string destinationRelativePath)
        {
            throw new NotSupportedException();
        }
        public void ReplaceFile(
            string sourceRelativePath,
            string destinationRelativePath,
            string backupRelativePath)
        {
            throw new NotSupportedException();
        }
        public void DeleteFile(string relativePath)
        {
            throw new NotSupportedException();
        }
        public void Dispose()
        {
            Disposed = true;
        }
    }

    internal sealed class ThrowingReleaseLeaseSource
    {
        private readonly InstallerTransactionLeaseReleaseOutcome outcome;
        private readonly bool throwUnknown;
        private Thread ownerThread;
        internal int AcquireCalls;
        internal int ReleaseAttempts;
        internal bool UnderlyingHeld;

        internal ThrowingReleaseLeaseSource(
            InstallerTransactionLeaseReleaseOutcome releaseOutcome,
            bool useUnknownException)
        {
            outcome = releaseOutcome;
            throwUnknown = useUnknownException;
        }

        internal IDisposable Acquire()
        {
            if (UnderlyingHeld)
            {
                throw new InvalidOperationException(
                    "Test exclusion primitive is already held.");
            }
            AcquireCalls++;
            UnderlyingHeld = true;
            ownerThread = Thread.CurrentThread;
            return new ThrowingLease(this);
        }

        internal bool IsHeldByCurrentThread()
        {
            return UnderlyingHeld &&
                Object.ReferenceEquals(
                    ownerThread,
                    Thread.CurrentThread);
        }

        private sealed class ThrowingLease
            : IDisposable
        {
            private readonly ThrowingReleaseLeaseSource owner;
            private bool disposed;
            private bool failedOnce;

            internal ThrowingLease(
                ThrowingReleaseLeaseSource owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }
                if (!Object.ReferenceEquals(
                        owner.ownerThread,
                        Thread.CurrentThread))
                {
                    throw new InvalidOperationException(
                        "Test lease is thread-affine.");
                }
                owner.ReleaseAttempts++;
                if (failedOnce &&
                    owner.outcome ==
                        InstallerTransactionLeaseReleaseOutcome.
                            RejectedBeforeMutation)
                {
                    owner.UnderlyingHeld = false;
                    owner.ownerThread = null;
                    disposed = true;
                    return;
                }
                failedOnce = true;
                if (owner.outcome ==
                    InstallerTransactionLeaseReleaseOutcome.Confirmed)
                {
                    owner.UnderlyingHeld = false;
                    owner.ownerThread = null;
                    disposed = true;
                }
                if (owner.throwUnknown)
                {
                    throw new IOException(
                        "Injected unknown transaction lease release failure.");
                }
                throw new InstallerTransactionLeaseReleaseException(
                    owner.outcome,
                    "Injected typed transaction lease release failure.",
                    null);
            }
        }
    }

    internal sealed class CoordinatorReleaseFaultSeam
        : IInstallerTransactionLeaseFaultSeam
    {
        internal bool FailAfterOwnershipRecorded;
        internal bool ExhaustNextLeaseId;
        internal bool FailRelease;
        internal bool FailCleanup;
        internal int LeaseIdAllocationCalls;
        internal int OwnershipRecordedCalls;
        internal int ReleaseCalls;
        internal int CleanupCalls;

        public void AfterOwnershipRecorded()
        {
            OwnershipRecordedCalls++;
            if (FailAfterOwnershipRecorded)
            {
                FailAfterOwnershipRecorded = false;
                throw new IOException(
                    "Injected post-ownership-recorded failure.");
            }
        }

        public void BeforeLeaseIdAllocated(ref long nextLeaseId)
        {
            LeaseIdAllocationCalls++;
            if (ExhaustNextLeaseId)
            {
                ExhaustNextLeaseId = false;
                nextLeaseId = Int64.MaxValue;
            }
        }

        public void ReleaseMutex(Mutex mutex)
        {
            ReleaseCalls++;
            if (FailRelease)
            {
                FailRelease = false;
                throw new IOException(
                    "Injected real coordinator ReleaseMutex failure.");
            }
            mutex.ReleaseMutex();
        }

        public void CleanupReleasedHandle(Mutex mutex)
        {
            CleanupCalls++;
            if (FailCleanup)
            {
                FailCleanup = false;
                throw new IOException(
                    "Injected released-handle cleanup failure.");
            }
            mutex.Dispose();
        }
    }

    internal sealed class CountingInstallerTransactionMutexFactory
        : IInstallerTransactionMutexFactory
    {
        internal int OpenCalls;

        public Mutex OpenOrCreate(string name)
        {
            OpenCalls++;
            return new Mutex(false, name);
        }
    }

    internal sealed class MaintenanceReplayPostAcquireFaultSeam
        : IMaintenanceReplayPostAcquireFaultSeam
    {
        internal int Calls;

        public void AfterSharedLeaseAcquired()
        {
            Calls++;
            throw new IOException(
                "Injected post-shared-acquire failure.");
        }
    }

    internal sealed class CountingLeaseSource
    {
        private Thread ownerThread;
        internal int AcquireCalls;
        internal int ReleaseCalls;
        internal bool Held;

        internal IDisposable Acquire()
        {
            if (Held)
            {
                throw new InvalidOperationException(
                    "Counting lease is already held.");
            }
            AcquireCalls++;
            Held = true;
            ownerThread = Thread.CurrentThread;
            return new Lease(this);
        }

        internal bool IsHeldByCurrentThread()
        {
            return Held &&
                Object.ReferenceEquals(ownerThread, Thread.CurrentThread);
        }

        private sealed class Lease : IDisposable
        {
            private readonly CountingLeaseSource owner;
            private bool disposed;

            internal Lease(CountingLeaseSource owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                if (disposed) return;
                owner.ReleaseCalls++;
                owner.Held = false;
                owner.ownerThread = null;
                disposed = true;
            }
        }
    }

    internal sealed class ConcurrentReleaseLeaseSource
    {
        internal readonly ManualResetEvent ReleaseEntered =
            new ManualResetEvent(false);
        internal readonly ManualResetEvent AllowRelease =
            new ManualResetEvent(false);
        internal int ReleaseCalls;
        internal bool Held;
        private Thread ownerThread;

        internal IDisposable Acquire()
        {
            Held = true;
            ownerThread = Thread.CurrentThread;
            return new BarrierLease(this);
        }

        internal bool IsHeldByCurrentThread()
        {
            return Held &&
                Object.ReferenceEquals(ownerThread, Thread.CurrentThread);
        }

        private sealed class BarrierLease : IDisposable
        {
            private readonly ConcurrentReleaseLeaseSource owner;
            private bool disposed;

            internal BarrierLease(
                ConcurrentReleaseLeaseSource owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                if (disposed) return;
                owner.ReleaseCalls++;
                owner.ReleaseEntered.Set();
                if (!owner.AllowRelease.WaitOne(
                        TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Concurrent release barrier timed out.");
                }
                owner.Held = false;
                owner.ownerThread = null;
                disposed = true;
            }
        }
    }

    internal sealed class ThrowingAcquireLeaseSource
    {
        private readonly InstallerTransactionLeaseReleaseOutcome outcome;
        internal int AcquireCalls;

        internal ThrowingAcquireLeaseSource(
            InstallerTransactionLeaseReleaseOutcome outcome)
        {
            this.outcome = outcome;
        }

        internal IDisposable Acquire()
        {
            AcquireCalls++;
            throw new InstallerTransactionLeaseReleaseException(
                outcome,
                "Injected typed lease acquisition cleanup failure.",
                null);
        }

        internal bool IsHeldByCurrentThread()
        {
            return false;
        }
    }

    internal sealed class LeaseProbe
    {
        private readonly Func<IDisposable> acquire;
        internal readonly ManualResetEvent Started =
            new ManualResetEvent(false);
        internal readonly Thread Thread;
        internal volatile bool Acquired;
        internal Exception Failure;

        internal LeaseProbe(Func<IDisposable> acquire)
        {
            this.acquire = acquire;
            Thread = new Thread(new ThreadStart(Run));
        }

        private void Run()
        {
            try
            {
                Started.Set();
                using (IDisposable lease = acquire())
                {
                    Acquired = true;
                }
            }
            catch (Exception exception)
            {
                Failure = exception;
            }
        }
    }

    internal sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

    internal sealed class ThrowingReadFileSystem
        : IAtomicJournalFileSystem
    {
        internal readonly Exception Failure;
        internal int OpenReadCalls;
        internal ThrowingReadFileSystem(Exception failure)
        {
            Failure = failure;
        }
        public string GetDisplayPath(string relativePath)
        {
            return relativePath;
        }
        public bool FileExists(string relativePath)
        {
            return !relativePath.EndsWith(
                ".new",
                StringComparison.Ordinal);
        }
        public void EnsureDirectory(string relativePath) { }
        public Stream CreateNewFile(string relativePath)
        {
            throw new NotSupportedException();
        }
        public Stream OpenReadFile(string relativePath)
        {
            OpenReadCalls++;
            throw Failure;
        }
        public void PublishNewFile(
            string sourceRelativePath,
            string destinationRelativePath)
        {
            throw new NotSupportedException();
        }
        public void ReplaceFile(
            string sourceRelativePath,
            string destinationRelativePath,
            string backupRelativePath)
        {
            throw new NotSupportedException();
        }
        public void DeleteFile(string relativePath)
        {
            throw new NotSupportedException();
        }
    }

    internal sealed class FakeTerminator
        : IMaintenanceProcessTerminator
    {
        internal int Calls;
        internal Action OnTerminate;

        public void Terminate(string reason)
        {
            Calls++;
            if (OnTerminate != null)
            {
                OnTerminate();
            }
        }
    }

    internal sealed class CountingPipeSafeHandle
        : SafeHandle
    {
        internal CountingPipeSafeHandle()
            : base(IntPtr.Zero, true)
        {
            SetHandle(new IntPtr(44));
        }

        internal int ReleaseCalls;

        public override bool IsInvalid
        {
            get { return handle == IntPtr.Zero; }
        }

        protected override bool ReleaseHandle()
        {
            ReleaseCalls++;
            handle = IntPtr.Zero;
            return true;
        }
    }

    internal sealed class FakeNamedPipeClientNative
        : IMaintenanceNamedPipeClientNative
    {
        internal bool FailAcquire;
        internal bool FailRevert;
        internal int ThreadId = 1;
        internal int AcquireCalls;
        internal int RevertCalls;

        public int GetCurrentThreadId()
        {
            return ThreadId;
        }

        public void AcquireClient(
            IntPtr borrowedPipeHandle,
            ref bool armed,
            out int error)
        {
            AcquireCalls++;
            if (FailAcquire)
            {
                error = 5;
                return;
            }
            armed = true;
            error = 0;
        }

        public bool RevertToSelf(out int error)
        {
            RevertCalls++;
            error = FailRevert ? 5 : 0;
            return !FailRevert;
        }
    }

    internal sealed class DelegateTokenCapture
        : IMaintenanceClientTokenCapture
    {
        private readonly Func<MaintenanceClientTokenEvidence> action;

        internal DelegateTokenCapture(
            Func<MaintenanceClientTokenEvidence> action)
        {
            this.action = action;
        }

        public MaintenanceClientTokenEvidence Capture()
        {
            return action();
        }
    }

    internal sealed class SequenceImpersonationRunner
        : IMaintenanceClientImpersonationRunner
    {
        internal readonly List<string> Events;
        internal int ImpersonateCalls;
        internal int RevertCalls;
        internal bool IsImpersonating;
        internal Exception ImpersonateFailure;
        internal Exception RevertFailure;

        internal SequenceImpersonationRunner(List<string> events)
        {
            Events = events;
        }

        public MaintenanceClientTokenEvidence CaptureScoped(
            IMaintenanceClientTokenCapture capture,
            IMaintenanceProcessTerminator terminator)
        {
            ImpersonateCalls++;
            Events.Add("impersonate");
            if (ImpersonateFailure != null)
            {
                throw new UnauthorizedAccessException(
                    "Scoped impersonation setup failed.",
                    ImpersonateFailure);
            }
            IsImpersonating = true;
            MaintenanceClientTokenEvidence evidence = null;
            Exception captureFailure = null;
            try
            {
                evidence = capture.Capture();
                if (evidence == null)
                {
                    captureFailure =
                        new UnauthorizedAccessException(
                            "Scoped capture returned no evidence.");
                }
            }
            catch (Exception failure)
            {
                captureFailure = failure;
            }
            finally
            {
                RevertCalls++;
                Events.Add("revert");
                IsImpersonating = false;
                if (RevertFailure != null)
                {
                    terminator.Terminate(
                        "Scoped capture revert failed.");
                    throw new InvalidOperationException(
                        "Maintenance process terminator returned after " +
                        "scoped capture revert failure.",
                        RevertFailure);
                }
            }
            if (captureFailure != null)
            {
                throw new UnauthorizedAccessException(
                    "Scoped token capture failed.",
                    captureFailure);
            }
            return evidence;
        }
    }

    internal sealed class SequenceTokenCapture
        : IMaintenanceClientTokenCapture
    {
        private readonly SequenceImpersonationRunner impersonation;
        internal readonly List<string> Events;
        internal MaintenanceClientTokenEvidence Evidence;
        internal Exception Failure;
        internal int Calls;

        internal SequenceTokenCapture(
            SequenceImpersonationRunner impersonation,
            List<string> events)
        {
            this.impersonation = impersonation;
            Events = events;
        }

        public MaintenanceClientTokenEvidence Capture()
        {
            Calls++;
            if (!impersonation.IsImpersonating)
            {
                throw new InvalidOperationException(
                    "Token capture ran outside impersonation.");
            }
            Events.Add("capture");
            if (Failure != null)
            {
                throw Failure;
            }
            return Evidence;
        }
    }

    internal sealed class SequencePolicyAuthorizer
        : IMaintenanceClientPolicyAuthorizer
    {
        private readonly IMaintenanceClientPolicyAuthorizer inner;
        internal readonly List<string> Events;
        internal int Calls;

        internal SequencePolicyAuthorizer(
            IMaintenanceClientPolicyAuthorizer inner,
            List<string> events)
        {
            this.inner = inner;
            Events = events;
        }

        public MaintenanceAuthorizationEvidence Authorize(
            MaintenanceClientTokenEvidence evidence,
            CancellationToken cancellation)
        {
            Calls++;
            Events.Add("authorize");
            return inner.Authorize(evidence, cancellation);
        }
    }

    internal sealed class SequencePreauthorizedDispatcher
        : IMaintenancePreauthorizedCommandDispatcher
    {
        internal readonly List<string> Events;
        internal int Calls;

        internal SequencePreauthorizedDispatcher(List<string> events)
        {
            Events = events;
        }

        public PayloadBrokerResponse Dispatch(
            PayloadBrokerCommand command,
            MaintenanceAuthorizationEvidence authorization,
            CancellationToken cancellation)
        {
            Calls++;
            Events.Add("dispatch");
            if (authorization == null)
            {
                throw new InvalidOperationException(
                    "Dispatcher did not receive authorization evidence.");
            }
            return null;
        }
    }

    internal sealed class FakeWindowsTokenNative
        : IMaintenanceWindowsTokenNative
    {
        internal readonly Dictionary<
            MaintenanceTokenInformationClass,
            byte[]> Payloads =
                new Dictionary<
                    MaintenanceTokenInformationClass,
                    byte[]>();
        internal MaintenanceTokenInformationClass? ProbeFailure;
        internal MaintenanceTokenInformationClass? QueryFailure;
        internal MaintenanceTokenInformationClass? SizeRace;
        internal MaintenanceTokenInformationClass? ResizeOnce;
        internal MaintenanceTokenInformationClass? InvalidReturnedLength;
        internal MaintenanceTokenInformationClass? ProbeUnexpectedSuccess;
        internal MaintenanceTokenInformationClass? InvalidProbeLength;
        internal MaintenanceTokenInformationClass? OversizedProbe;
        internal bool FailOpen;
        internal bool TokenRestricted;
        internal bool InvalidSidPointer;
        internal int? SidPointerOffsetOverride;
        internal int? StatisticsDriftOffset;
        internal int OpenCalls;
        internal int ReleaseCalls;
        internal int InformationCalls;

        internal FakeWindowsTokenNative()
        {
            Payloads[MaintenanceTokenInformationClass.User] =
                SidPayload("S-1-5-18");
            Payloads[MaintenanceTokenInformationClass.Groups] =
                GroupsPayload(
                    new[] { "S-1-5-32-544" },
                    new[] { (uint)MaintenanceTokenGroupAttributes.Enabled });
            Payloads[MaintenanceTokenInformationClass.Elevation] =
                Int32Payload(0);
            Payloads[MaintenanceTokenInformationClass.ElevationType] =
                Int32Payload(1);
            Payloads[MaintenanceTokenInformationClass.IntegrityLevel] =
                SidPayload("S-1-16-16384");
            Payloads[MaintenanceTokenInformationClass.IsAppContainer] =
                Int32Payload(0);
            Payloads[MaintenanceTokenInformationClass.HasRestrictions] =
                Int32Payload(0);
            Payloads[MaintenanceTokenInformationClass.Type] =
                Int32Payload(2);
            Payloads[
                MaintenanceTokenInformationClass.ImpersonationLevel] =
                    Int32Payload(2);
            byte[] statistics =
                new byte[Marshal.SizeOf(
                    typeof(MaintenanceNativeTokenStatistics))];
            WriteInt64(statistics, 0, 11);
            WriteInt64(statistics, 8, 0x0102030405060708L);
            WriteInt32(
                statistics,
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "TokenType")),
                2);
            WriteInt32(
                statistics,
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "ImpersonationLevel")),
                2);
            WriteInt32(
                statistics,
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "GroupCount")),
                1);
            WriteInt64(
                statistics,
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "ModifiedId")),
                22);
            Payloads[MaintenanceTokenInformationClass.Statistics] =
                statistics;
        }

        public MaintenanceSafeTokenHandle
            OpenCurrentThreadTokenForQuery()
        {
            OpenCalls++;
            if (FailOpen)
            {
                throw new IOException("open token failure");
            }
            return new MaintenanceSafeTokenHandle(
                new IntPtr(99),
                false,
                delegate { ReleaseCalls++; });
        }

        public bool GetTokenInformation(
            MaintenanceSafeTokenHandle token,
            MaintenanceTokenInformationClass informationClass,
            IntPtr buffer,
            int bufferLength,
            out int returnLength,
            out int error)
        {
            InformationCalls++;
            byte[] payload = Payloads[informationClass];
            if (buffer == IntPtr.Zero)
            {
                if (ProbeUnexpectedSuccess == informationClass)
                {
                    returnLength = payload.Length;
                    error = 0;
                    return true;
                }
                if (ProbeFailure == informationClass)
                {
                    returnLength = 0;
                    error = 5;
                    return false;
                }
                returnLength =
                    InvalidProbeLength == informationClass
                        ? 0
                        : OversizedProbe == informationClass
                            ? (1024 * 1024) + 1
                            : payload.Length;
                error = 122;
                return false;
            }
            if (SizeRace == informationClass)
            {
                returnLength = checked(bufferLength + 8);
                error = 122;
                return false;
            }
            int queryNumber = InformationCallsFor(informationClass);
            if (ResizeOnce == informationClass &&
                queryNumber == 1)
            {
                returnLength = checked(bufferLength + 8);
                error = 122;
                return false;
            }
            if (QueryFailure == informationClass)
            {
                returnLength = payload.Length;
                error = 5;
                return false;
            }
            if (informationClass ==
                    MaintenanceTokenInformationClass.Statistics &&
                StatisticsDriftOffset.HasValue)
            {
                payload = (byte[])payload.Clone();
                if ((queryNumber & 1) == 0)
                {
                    WriteInt32(
                        payload,
                        StatisticsDriftOffset.Value,
                        checked(
                            BitConverter.ToInt32(
                                payload,
                                StatisticsDriftOffset.Value) + 1));
                }
            }
            Marshal.Copy(
                payload,
                0,
                buffer,
                Math.Min(bufferLength, payload.Length));
            PatchSidPointers(
                informationClass,
                buffer,
                payload);
            returnLength =
                InvalidReturnedLength == informationClass
                    ? checked(bufferLength + 1)
                    : payload.Length;
            error = 0;
            return true;
        }

        public byte[] CopySid(
            IntPtr sid,
            IntPtr containingBuffer,
            int containingLength)
        {
            return new MaintenanceWindowsTokenNative().CopySid(
                sid,
                containingBuffer,
                containingLength);
        }

        public bool IsTokenRestricted(
            MaintenanceSafeTokenHandle token)
        {
            return TokenRestricted;
        }

        internal static byte[] Int32Payload(int value)
        {
            return BitConverter.GetBytes(value);
        }

        internal static byte[] SidPayload(string sid)
        {
            byte[] sidBytes = SidBytes(sid);
            int fixedHeaderLength =
                Marshal.SizeOf(
                    typeof(MaintenanceNativeSidAndAttributes));
            byte[] payload =
                new byte[fixedHeaderLength + sidBytes.Length];
            Buffer.BlockCopy(
                sidBytes,
                0,
                payload,
                fixedHeaderLength,
                sidBytes.Length);
            return payload;
        }

        internal static byte[] GroupsPayload(
            string[] sids,
            uint[] attributes)
        {
            int offset =
                ((sizeof(uint) + IntPtr.Size - 1) / IntPtr.Size) *
                IntPtr.Size;
            int entrySize =
                ((IntPtr.Size + sizeof(uint) + IntPtr.Size - 1) /
                    IntPtr.Size) * IntPtr.Size;
            var sidCopies = new List<byte[]>();
            int sidBytesLength = 0;
            foreach (string sid in sids)
            {
                byte[] copy = SidBytes(sid);
                sidCopies.Add(copy);
                sidBytesLength += copy.Length;
            }
            byte[] payload = new byte[
                offset + (sids.Length * entrySize) +
                sidBytesLength];
            WriteInt32(payload, 0, sids.Length);
            int sidOffset = offset + (sids.Length * entrySize);
            for (int index = 0; index < sids.Length; ++index)
            {
                int entry = offset + (index * entrySize);
                WriteInt32(
                    payload,
                    entry + IntPtr.Size,
                    unchecked((int)attributes[index]));
                Buffer.BlockCopy(
                    sidCopies[index],
                    0,
                    payload,
                    sidOffset,
                    sidCopies[index].Length);
                sidOffset += sidCopies[index].Length;
            }
            return payload;
        }

        private readonly Dictionary<
            MaintenanceTokenInformationClass,
            int> queryCalls =
                new Dictionary<
                    MaintenanceTokenInformationClass,
                    int>();

        private int InformationCallsFor(
            MaintenanceTokenInformationClass informationClass)
        {
            int count;
            queryCalls.TryGetValue(informationClass, out count);
            count++;
            queryCalls[informationClass] = count;
            return count;
        }

        internal int CompletedQueriesFor(
            MaintenanceTokenInformationClass informationClass)
        {
            int count;
            queryCalls.TryGetValue(informationClass, out count);
            return count;
        }

        private void PatchSidPointers(
            MaintenanceTokenInformationClass informationClass,
            IntPtr buffer,
            byte[] payload)
        {
            if (informationClass ==
                    MaintenanceTokenInformationClass.User ||
                informationClass ==
                    MaintenanceTokenInformationClass.IntegrityLevel)
            {
                if (payload.Length < IntPtr.Size)
                {
                    return;
                }
                Marshal.WriteIntPtr(
                    buffer,
                    InvalidSidPointer
                        ? Add(buffer, -1)
                        : Add(
                            buffer,
                            SidPointerOffsetOverride.HasValue
                                ? SidPointerOffsetOverride.Value
                                : Marshal.SizeOf(
                                    typeof(
                                        MaintenanceNativeSidAndAttributes))));
                return;
            }
            if (informationClass !=
                MaintenanceTokenInformationClass.Groups)
            {
                return;
            }
            int count = BitConverter.ToInt32(payload, 0);
            int offset =
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenGroups),
                    "Groups"));
            int stride =
                Marshal.SizeOf(
                    typeof(MaintenanceNativeSidAndAttributes));
            long entriesEnd =
                (long)offset + ((long)count * stride);
            if (count < 0 || entriesEnd > payload.Length)
            {
                return;
            }
            int sidOffset = offset + (count * stride);
            for (int index = 0; index < count; ++index)
            {
                Marshal.WriteIntPtr(
                    buffer,
                    offset + (index * stride),
                    InvalidSidPointer
                        ? Add(buffer, -1)
                        : Add(buffer, sidOffset));
                byte subAuthorities =
                    Marshal.ReadByte(buffer, sidOffset + 1);
                sidOffset += 8 + (subAuthorities * 4);
            }
        }

        private static byte[] SidBytes(string sid)
        {
            var identifier = new SecurityIdentifier(sid);
            byte[] bytes = new byte[identifier.BinaryLength];
            identifier.GetBinaryForm(bytes, 0);
            return bytes;
        }

        private static IntPtr Add(IntPtr value, int offset)
        {
            return new IntPtr(value.ToInt64() + offset);
        }

        private static void WritePointer(
            byte[] destination,
            int offset,
            IntPtr value)
        {
            byte[] bytes =
                IntPtr.Size == 8
                    ? BitConverter.GetBytes(value.ToInt64())
                    : BitConverter.GetBytes(value.ToInt32());
            Buffer.BlockCopy(
                bytes,
                0,
                destination,
                offset,
                bytes.Length);
        }

        private static void WriteInt32(
            byte[] destination,
            int offset,
            int value)
        {
            Buffer.BlockCopy(
                BitConverter.GetBytes(value),
                0,
                destination,
                offset,
                sizeof(int));
        }

        private static void WriteInt64(
            byte[] destination,
            int offset,
            long value)
        {
            Buffer.BlockCopy(
                BitConverter.GetBytes(value),
                0,
                destination,
                offset,
                sizeof(long));
        }
    }

    internal sealed class FakeAuthorizer
        : IMaintenanceCommandAuthorizer
    {
        internal bool Grant = true;
        internal bool IgnoreCancellation;
        internal Action<
            PayloadBrokerCommand,
            CancellationToken> OnAuthorize;
        internal readonly ManualResetEvent Authorized =
            new ManualResetEvent(false);

        public MaintenanceAuthorizationEvidence Authorize(
            PayloadBrokerCommand command,
            CancellationToken cancellation)
        {
            Authorized.Set();
            if (OnAuthorize != null)
            {
                OnAuthorize(command, cancellation);
            }
            if (!IgnoreCancellation)
            {
                cancellation.ThrowIfCancellationRequested();
            }
            return Grant
                ? MaintenanceAuthorizationEvidence.
                    IssueForTrustedAdapter()
                : null;
        }
    }

    internal enum FakeStoreFault
    {
        None,
        BeforeCommit,
        AfterCommit,
        CorruptAfterCommit
    }

    internal sealed class FakeReplayStore
        : IMaintenanceReplayAtomicStore
    {
        private readonly Dictionary<string, byte[]> entries =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private int leaseThreadId;
        internal readonly Queue<FakeStoreFault> Faults =
            new Queue<FakeStoreFault>();

        public string RootAuthorityInvariantDigest
        {
            get { return new string('f', 64); }
        }

        public IMaintenanceReplayStoreLease AcquireExclusiveLease()
        {
            int threadId = Thread.CurrentThread.ManagedThreadId;
            if (Interlocked.CompareExchange(
                    ref leaseThreadId,
                    threadId,
                    0) != 0)
            {
                throw new InvalidOperationException(
                    "Fake replay store lease is non-reentrant.");
            }
            Monitor.Enter(entries);
            return new Lease(this, threadId);
        }

        private sealed class Lease
            : IMaintenanceReplayStoreLease
        {
            private readonly FakeReplayStore owner;
            private readonly int threadId;
            private bool disposed;

            internal Lease(FakeReplayStore owner, int threadId)
            {
                this.owner = owner;
                this.threadId = threadId;
            }

            public bool TryRead(string key, out byte[] bytes)
            {
                DemandThread();
                byte[] stored;
                if (owner.entries.TryGetValue(key, out stored))
                {
                    bytes = (byte[])stored.Clone();
                    return true;
                }
                bytes = null;
                return false;
            }

            public void AtomicWrite(string key, byte[] bytes)
            {
                DemandThread();
                FakeStoreFault fault =
                    owner.Faults.Count == 0
                        ? FakeStoreFault.None
                        : owner.Faults.Dequeue();
                if (fault == FakeStoreFault.BeforeCommit)
                {
                    throw new IOException(
                        "Injected failure before commit.");
                }
                byte[] stored = (byte[])bytes.Clone();
                if (fault == FakeStoreFault.CorruptAfterCommit)
                {
                    stored[stored.Length - 1] ^= 1;
                }
                owner.entries[key] = stored;
                if (fault == FakeStoreFault.AfterCommit ||
                    fault == FakeStoreFault.CorruptAfterCommit)
                {
                    throw new IOException(
                        "Injected failure after commit.");
                }
            }

            public void Dispose()
            {
                DemandThread();
                disposed = true;
                Volatile.Write(ref owner.leaseThreadId, 0);
                Monitor.Exit(owner.entries);
            }

            private void DemandThread()
            {
                if (disposed ||
                    Thread.CurrentThread.ManagedThreadId !=
                        threadId)
                {
                    throw new InvalidOperationException(
                        "Fake replay lease is thread-affine.");
                }
            }
        }
    }

    internal static class MaintenanceServiceRuntimeContractTests
    {
        private static int failures;
        private static int mutationCount;
        private static int reconcileCount;
        private const int CorEFailFast =
            unchecked((int)0x80131623);
        private const int CorEUnhandledException =
            unchecked((int)0xE0434352);
        private const int FailStopUiSetupFailed = 96;
        private const uint SemFailCriticalErrors = 0x0001;
        private const uint SemNoGpFaultErrorBox = 0x0002;
        private const uint WerFaultReportingNoUi = 32;
        private static readonly string TransactionId =
            "11111111111111111111111111111111";
        private static readonly string RequestId =
            "22222222222222222222222222222222";

        private static int Main(string[] args)
        {
            if (args != null &&
                args.Length == 3 &&
                args[0] == "--native-failstop-child")
            {
                return RunNativeFailStopChild(
                    args[1],
                    args[2]);
            }
            Run("identity reuses fixed contracts", IdentityReusesContracts);
            Run("security descriptor is exact", SecurityDescriptorIsExact);
            Run(
                "maintenance pipe wire codec is strict",
                MaintenancePipeWireContractTests.Run);
            Run("client token evidence is immutable", ClientTokenEvidenceIsImmutable);
            Run("production client policy is exact", ProductionClientPolicyIsExact);
            Run("client capture sequencing is fail closed", ClientCaptureSequencingIsFailClosed);
            Run("native scoped adapter is fail closed", NativeScopedAdapterIsFailClosed);
            Run("production SID copy is bounded and deep", ProductionSidCopyIsBoundedAndDeep);
            Run("windows token reader snapshots consistently", WindowsTokenReaderSnapshotsConsistently);
            Run("windows token reader faults are bounded", WindowsTokenReaderFaultsAreBounded);
            Run("process local thread token integration restores self", ProcessLocalThreadTokenIntegrationRestoresSelf);
            Run("lifecycle is bounded and terminal", LifecycleIsTerminal);
            Run("dispatcher is serialized and non-reentrant", DispatcherIsSafe);
            Run("dispatcher cancellation interrupts wait", DispatcherCancelsWait);
            Run("dispatcher execute cancellation clears state", DispatcherExecuteCancellationClearsState);
            Run("replay record codec is canonical", ReplayCodecIsCanonical);
            Run("prepared retry inspects or resumes authoritative state without mutation", PreparedRetryUsesAuthoritativeReconciliation);
            Run("same replay key rejects a different command", ReplayKeyRejectsDifferentCommand);
            Run("atomic write faults use exact readback", AtomicFaultMatrix);
            Run("replay read failures do not downgrade to backup", ReplayReadFailuresDoNotFallback);
            Run("production-shaped replay uses native root and shared lease", ProductionReplayUsesNativeRoot);
            Run("replay lifecycle gate closes create and acquire races", ReplayLifecycleGateClosesRaces);
            Run("throwing lease release obeys typed outcomes", ThrowingLeaseReleaseIsFailClosed);
            Run("real coordinator release seam emits typed outcomes", RealCoordinatorReleaseSeamIsTyped);
            Run("coordinator rolls back post-ownership acquisition faults", CoordinatorPostOwnershipFaultRollsBack);
            Run("typed acquisition cleanup settles parent and replay", TypedAcquisitionCleanupSettlesLifetimes);
            Run("replay settles post-shared-acquire faults", ReplayPostSharedAcquireFaultsSettle);
            Run("lifetime lease serializes concurrent dispose", LifetimeLeaseSerializesConcurrentDispose);
            Run("fake lease is thread-affine and non-reentrant", FakeLeaseIsStrict);
            Console.WriteLine(
                failures == 0
                    ? "Maintenance service runtime contract tests passed."
                    : failures + " maintenance runtime test(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        private static void IdentityReusesContracts()
        {
            Assert(
                MaintenanceServiceIdentity.ServiceName ==
                    PayloadNamespaceSecurityProfile.BrokerServiceName &&
                MaintenanceServiceIdentity.NamespaceId ==
                    PayloadManagedNamespaceLocation.ProductionNamespaceId,
                "Maintenance identity drifted from the owner contract.");
            Assert(
                PayloadManagedNamespaceLocation.ManagedRootPath !=
                    PayloadManagedNamespaceLocation.StableServiceRootPath,
                "App and Service roots were conflated.");
        }

        private static void SecurityDescriptorIsExact()
        {
            const int fileCreatePipeInstance = 0x00000004;
            const int genericWrite =
                unchecked((int)0x40000000);
            const string expectedSid =
                "S-1-5-80-2298780130-3752148843-3872957159-3129965176-2730309495";
            ProtectedRootSecurityMaterial material =
                ProtectedRootSecurityCompiler.Compile();
            Assert(
                material.ServiceSid == expectedSid,
                "Fixed service SID known vector changed.");
            var descriptor =
                new RawSecurityDescriptor(material.Sddl);
            AssertProtectedOwnerAndDacl(descriptor, 4, "App");
            AceFlags inheritedByChildren =
                AceFlags.ObjectInherit |
                AceFlags.ContainerInherit;
            AssertExactAllowAce(
                descriptor.DiscretionaryAcl,
                0,
                new SecurityIdentifier(expectedSid),
                unchecked((int)0x10000000),
                inheritedByChildren,
                "App service");
            AssertExactAllowAce(
                descriptor.DiscretionaryAcl,
                1,
                WellKnownSid(WellKnownSidType.LocalSystemSid),
                unchecked((int)0xA0000000),
                inheritedByChildren,
                "App SYSTEM");
            AssertExactAllowAce(
                descriptor.DiscretionaryAcl,
                2,
                WellKnownSid(
                    WellKnownSidType.BuiltinAdministratorsSid),
                unchecked((int)0xA0000000),
                inheritedByChildren,
                "App Administrators");
            AssertExactAllowAce(
                descriptor.DiscretionaryAcl,
                3,
                WellKnownSid(WellKnownSidType.BuiltinUsersSid),
                unchecked((int)0xA0000000),
                inheritedByChildren,
                "App Users");

            MaintenancePipeSecurityContract pipe =
                MaintenancePipeSecurityContract.Compile();
            var pipeDescriptor =
                new RawSecurityDescriptor(pipe.Sddl);
            AssertProtectedOwnerAndDacl(
                pipeDescriptor,
                3,
                "Pipe");
            AssertExactAllowAce(
                pipeDescriptor.DiscretionaryAcl,
                0,
                new SecurityIdentifier(expectedSid),
                unchecked((int)0x10000000),
                AceFlags.None,
                "Pipe service");
            AssertExactAllowAce(
                pipeDescriptor.DiscretionaryAcl,
                1,
                WellKnownSid(WellKnownSidType.LocalSystemSid),
                unchecked((int)0x10000000),
                AceFlags.None,
                "Pipe SYSTEM");
            AssertExactAllowAce(
                pipeDescriptor.DiscretionaryAcl,
                2,
                WellKnownSid(
                    WellKnownSidType.BuiltinAdministratorsSid),
                0x00000003,
                AceFlags.None,
                "Pipe Administrators");
            var pipeAdministratorsAce =
                pipeDescriptor.DiscretionaryAcl[2] as CommonAce;
            Assert(
                MaintenancePipeSecurityContract.ClientDesiredAccess ==
                    0x00000003 &&
                pipeAdministratorsAce != null &&
                (pipeAdministratorsAce.AccessMask &
                    fileCreatePipeInstance) == 0 &&
                (pipeAdministratorsAce.AccessMask &
                    genericWrite) == 0,
                "Pipe Administrators must receive only FILE_READ_DATA " +
                "and FILE_WRITE_DATA, without create-instance or " +
                "generic-write authority.");
        }

        private static void ClientTokenEvidenceIsImmutable()
        {
            var source =
                new List<MaintenanceClientTokenGroupEvidence>
                {
                    new MaintenanceClientTokenGroupEvidence(
                        "S-1-5-32-544",
                        MaintenanceTokenGroupAttributes.Enabled)
                };
            MaintenanceClientTokenGroupEvidence original = source[0];
            var evidence =
                new MaintenanceClientTokenEvidence(
                    "S-1-5-18",
                    source,
                    true,
                    MaintenanceTokenElevationType.Full,
                    0x3000,
                    false,
                    false,
                    MaintenanceClientTokenType.Impersonation,
                    MaintenanceClientImpersonationLevel.Impersonation,
                    1234);
            source.Clear();

            Assert(
                evidence.UserSid == "S-1-5-18" &&
                evidence.Groups.Count == 1 &&
                evidence.Groups[0].Sid == "S-1-5-32-544" &&
                !Object.ReferenceEquals(original, evidence.Groups[0]) &&
                evidence.IsElevated &&
                evidence.ElevationType ==
                    MaintenanceTokenElevationType.Full &&
                evidence.IntegrityRid == 0x3000 &&
                !evidence.IsAppContainer &&
                !evidence.IsRestricted &&
                evidence.TokenType ==
                    MaintenanceClientTokenType.Impersonation &&
                evidence.ImpersonationLevel ==
                    MaintenanceClientImpersonationLevel.Impersonation &&
                evidence.AuthenticationId == 1234,
                "Token evidence did not retain its immutable snapshot.");
            try
            {
                evidence.Groups.Add(
                    new MaintenanceClientTokenGroupEvidence(
                        "S-1-5-11",
                        MaintenanceTokenGroupAttributes.Enabled));
            }
            catch (NotSupportedException)
            {
                return;
            }
            throw new InvalidOperationException(
                "Token evidence exposed a mutable group collection.");
        }

        private static void ProductionClientPolicyIsExact()
        {
            const string userSid = "S-1-5-21-1-2-3-1001";
            const string administratorsSid = "S-1-5-32-544";
            string serviceSid =
                ProtectedRootSecurityCompiler.DeriveServiceSid(
                    MaintenanceServiceIdentity.ServiceName);
            var policy =
                new MaintenanceProductionClientPolicyAuthorizer();

            AllowClient(
                policy,
                ClientToken(
                    "S-1-5-18",
                    null,
                    false,
                    MaintenanceTokenElevationType.Limited,
                    0x4000,
                    false,
                    false));
            AllowClient(
                policy,
                ClientToken(
                    userSid,
                    Group(serviceSid, true, false),
                    false,
                    MaintenanceTokenElevationType.Limited,
                    0x3000,
                    false,
                    false));
            AllowClient(
                policy,
                ClientToken(
                    serviceSid,
                    null,
                    false,
                    MaintenanceTokenElevationType.Limited,
                    0x3000,
                    false,
                    false));
            AllowClient(
                policy,
                ClientToken(
                    userSid,
                    Group(administratorsSid, true, false),
                    true,
                    MaintenanceTokenElevationType.Full,
                    0x3000,
                    false,
                    false));
            AllowClient(
                policy,
                ClientToken(
                    userSid,
                    Group(administratorsSid, true, false),
                    true,
                    MaintenanceTokenElevationType.Default,
                    0x3000,
                    false,
                    false,
                    MaintenanceClientTokenType.Impersonation,
                    MaintenanceClientImpersonationLevel.Impersonation,
                    Int64.MaxValue));

            RejectClient(policy, ClientToken(
                userSid, null, false,
                MaintenanceTokenElevationType.Default,
                0x2000, false, false));
            RejectClient(policy, ClientToken(
                userSid, Group(administratorsSid, true, false), false,
                MaintenanceTokenElevationType.Limited,
                0x3000, false, false));
            RejectClient(policy, ClientToken(
                userSid, Group(administratorsSid, true, true), true,
                MaintenanceTokenElevationType.Full,
                0x3000, false, false));
            RejectClient(policy, ClientToken(
                userSid, Group(administratorsSid, true, false), true,
                MaintenanceTokenElevationType.Full,
                0x2000, false, false));
            RejectClient(policy, ClientToken(
                userSid, Group("S-1-5-80-1", true, false), false,
                MaintenanceTokenElevationType.Limited,
                0x3000, false, false));
            RejectClient(policy, ClientToken(
                userSid, Group(serviceSid, true, true), false,
                MaintenanceTokenElevationType.Limited,
                0x3000, false, false));
            RejectClient(policy, ClientToken(
                userSid, Group(serviceSid, false, false), false,
                MaintenanceTokenElevationType.Limited,
                0x3000, false, false));
            RejectClient(policy, ClientToken(
                serviceSid, null, false,
                MaintenanceTokenElevationType.Limited,
                0x2000, false, false));
            RejectClient(policy, ClientToken(
                "S-1-5-18", null, false,
                MaintenanceTokenElevationType.Default,
                0x3000, false, false));
            RejectClient(policy, ClientToken(
                "S-1-5-18", null, false,
                MaintenanceTokenElevationType.Default,
                0x4000, false, true));
            RejectClient(policy, ClientToken(
                serviceSid, null, false,
                MaintenanceTokenElevationType.Limited,
                0x3000, true, false));
            RejectClient(policy, ClientToken(
                userSid, Group(administratorsSid, true, false), true,
                MaintenanceTokenElevationType.Full,
                0x3000, false, true));
            RejectClient(policy, ClientToken(
                userSid, Group(administratorsSid, true, false), true,
                MaintenanceTokenElevationType.Full,
                0x3000, true, false));
            RejectClient(policy, ClientToken(
                "S-1-5-18", null, false,
                MaintenanceTokenElevationType.Default,
                0x4000, false, false,
                MaintenanceClientTokenType.Primary,
                MaintenanceClientImpersonationLevel.Impersonation,
                0));
            RejectClient(policy, ClientToken(
                "S-1-5-18", null, false,
                MaintenanceTokenElevationType.Default,
                0x4000, false, false,
                MaintenanceClientTokenType.Impersonation,
                MaintenanceClientImpersonationLevel.Identification,
                0));
            RejectClient(policy, ClientToken(
                "S-1-5-18", null, false,
                MaintenanceTokenElevationType.Default,
                0x4000, false, false,
                MaintenanceClientTokenType.Impersonation,
                MaintenanceClientImpersonationLevel.Anonymous,
                0));
            RejectClient(policy, ClientToken(
                userSid, Group(administratorsSid, false, false), true,
                MaintenanceTokenElevationType.Full,
                0x3000, false, false));
            RejectUnauthorized(
                delegate
                {
                    policy.Authorize(null, CancellationToken.None);
                });
            RejectArgumentOutOfRange(
                delegate
                {
                    ClientToken(
                        userSid, null, false,
                        (MaintenanceTokenElevationType)99,
                        0x3000, false, false);
                });
            RejectArgumentOutOfRange(
                delegate
                {
                    ClientToken(
                        userSid, null, false,
                        MaintenanceTokenElevationType.Full,
                        0x3000, false, false,
                        (MaintenanceClientTokenType)99,
                        MaintenanceClientImpersonationLevel.Impersonation,
                        0);
                });
            RejectArgumentOutOfRange(
                delegate
                {
                    ClientToken(
                        userSid, null, false,
                        MaintenanceTokenElevationType.Full,
                        0x3000, false, false,
                        MaintenanceClientTokenType.Impersonation,
                        (MaintenanceClientImpersonationLevel)99,
                        0);
                });
            RejectArgumentOutOfRange(
                delegate
                {
                    ClientToken(
                        userSid, null, false,
                        MaintenanceTokenElevationType.Full,
                        -1, false, false);
                });
            RejectArgumentOutOfRange(
                delegate
                {
                    ClientToken(
                        userSid, null, false,
                        MaintenanceTokenElevationType.Full,
                        0x6000, false, false);
                });
            RejectArgument(
                delegate
                {
                    new MaintenanceClientTokenEvidence(
                        userSid,
                        new[]
                        {
                            Group(
                                administratorsSid,
                                true,
                                false),
                            Group(
                                administratorsSid.ToLowerInvariant(),
                                false,
                                true)
                        },
                        true,
                        MaintenanceTokenElevationType.Full,
                        0x3000,
                        false,
                        false,
                        MaintenanceClientTokenType.Impersonation,
                        MaintenanceClientImpersonationLevel.Impersonation,
                        0);
                });
            RejectArgument(
                delegate
                {
                    new MaintenanceClientTokenEvidence(
                        userSid,
                        new[]
                        {
                            Group(
                                serviceSid,
                                true,
                                false),
                            Group(
                                serviceSid,
                                true,
                                false)
                        },
                        false,
                        MaintenanceTokenElevationType.Limited,
                        0x3000,
                        false,
                        false,
                        MaintenanceClientTokenType.Impersonation,
                        MaintenanceClientImpersonationLevel.Impersonation,
                        0);
                });
        }

        private static void ClientCaptureSequencingIsFailClosed()
        {
            var successEvents = new List<string>();
            var successImpersonation =
                new SequenceImpersonationRunner(successEvents);
            var successCapture =
                new SequenceTokenCapture(
                    successImpersonation,
                    successEvents);
            successCapture.Evidence = ClientToken(
                "S-1-5-18",
                null,
                false,
                MaintenanceTokenElevationType.Default,
                0x4000,
                false,
                false);
            var successTerminator = new FakeTerminator();
            var successPolicy =
                new SequencePolicyAuthorizer(
                    new MaintenanceProductionClientPolicyAuthorizer(),
                    successEvents);
            var successDispatcher =
                new SequencePreauthorizedDispatcher(successEvents);
            var successSequencer =
                new MaintenanceClientRequestSequencer(
                    new MaintenanceClientCaptureRunner(
                        successImpersonation,
                        successCapture,
                        successTerminator),
                    successPolicy,
                    successDispatcher);
            successSequencer.Execute(
                delegate
                {
                    successEvents.Add("parse");
                    Assert(
                        successImpersonation.RevertCalls == 1,
                        "Command parsing ran before successful revert.");
                    return Command();
                },
                CancellationToken.None);
            AssertSequence(
                successEvents,
                "impersonate",
                "capture",
                "revert",
                "authorize",
                "parse",
                "dispatch");
            Assert(
                successImpersonation.ImpersonateCalls == 1 &&
                successImpersonation.RevertCalls == 1 &&
                successPolicy.Calls == 1 &&
                successDispatcher.Calls == 1 &&
                successTerminator.Calls == 0,
                "Successful capture sequencing call counts are wrong.");

            VerifyCaptureFailure(
                new IOException("query failure"),
                false);
            VerifyCaptureFailure(null, true);
            VerifyImpersonateFailure();
            VerifyRevertFailure(false);
            VerifyRevertFailure(true);
            VerifyTerminatorSentinel();
        }

        private static void WindowsTokenReaderSnapshotsConsistently()
        {
            Assert(
                IntPtr.Size == 8 &&
                Marshal.SizeOf(
                    typeof(MaintenanceNativeLuid)) == 8 &&
                Marshal.SizeOf(
                    typeof(MaintenanceNativeSidAndAttributes)) == 16 &&
                Marshal.SizeOf(
                    typeof(MaintenanceNativeTokenStatistics)) == 56 &&
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "AuthenticationId")) == 8 &&
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "GroupCount")) == 40 &&
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "ModifiedId")) == 48 &&
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenGroups),
                    "Groups")) == 8,
                "x64 token native structure packing drifted.");
            var native = new FakeWindowsTokenNative();
            var reader =
                new MaintenanceWindowsTokenSnapshotReader(native);
            MaintenanceClientTokenEvidence evidence =
                reader.Capture();
            Assert(
                evidence.UserSid == "S-1-5-18" &&
                evidence.Groups.Count == 1 &&
                evidence.Groups[0].Sid == "S-1-5-32-544" &&
                evidence.Groups[0].Attributes ==
                    MaintenanceTokenGroupAttributes.Enabled &&
                !evidence.IsElevated &&
                evidence.ElevationType ==
                    MaintenanceTokenElevationType.Default &&
                evidence.IntegrityRid == 0x4000 &&
                !evidence.IsAppContainer &&
                !evidence.IsRestricted &&
                evidence.TokenType ==
                    MaintenanceClientTokenType.Impersonation &&
                evidence.ImpersonationLevel ==
                    MaintenanceClientImpersonationLevel.Impersonation &&
                evidence.AuthenticationId ==
                    0x0102030405060708L &&
                native.OpenCalls == 1 &&
                native.ReleaseCalls == 1,
                "Windows token snapshot did not preserve all fields.");

            native.Payloads[
                MaintenanceTokenInformationClass.Groups] =
                    FakeWindowsTokenNative.GroupsPayload(
                        new[] { "S-1-5-11" },
                        new[] { (uint)0 });
            Assert(
                evidence.Groups[0].Sid == "S-1-5-32-544",
                "Native snapshot retained mutable source storage.");

            var restrictedNative = new FakeWindowsTokenNative();
            restrictedNative.TokenRestricted = true;
            Assert(
                new MaintenanceWindowsTokenSnapshotReader(
                    restrictedNative).Capture().IsRestricted,
                "IsTokenRestricted was not merged into evidence.");
            Assert(
                restrictedNative.CompletedQueriesFor(
                    MaintenanceTokenInformationClass.
                        HasRestrictions) == 1,
                "TokenHasRestrictions was short-circuited when " +
                "IsTokenRestricted was true.");
            var restrictionFlagNative =
                new FakeWindowsTokenNative();
            restrictionFlagNative.Payloads[
                MaintenanceTokenInformationClass.HasRestrictions] =
                    FakeWindowsTokenNative.Int32Payload(1);
            Assert(
                new MaintenanceWindowsTokenSnapshotReader(
                    restrictionFlagNative).Capture().IsRestricted,
                "TokenHasRestrictions was not merged into evidence.");
        }

        private static void NativeScopedAdapterIsFailClosed()
        {
            MaintenanceClientTokenEvidence evidence =
                ClientToken(
                    "S-1-5-18",
                    null,
                    false,
                    MaintenanceTokenElevationType.Default,
                    0x4000,
                    false,
                    false);

            var successHandle = new CountingPipeSafeHandle();
            var successNative = new FakeNamedPipeClientNative();
            var successAdapter =
                new MaintenanceNamedPipeClientImpersonationAdapter(
                    successHandle,
                    successNative);
            MaintenanceClientTokenEvidence returned =
                successAdapter.CaptureScoped(
                    new DelegateTokenCapture(
                        delegate { return evidence; }),
                    new FakeTerminator());
            successHandle.Dispose();
            Assert(
                Object.ReferenceEquals(returned, evidence) &&
                successNative.AcquireCalls == 1 &&
                successNative.RevertCalls == 1 &&
                successHandle.ReleaseCalls == 1,
                "Native scoped success did not acquire, revert, and " +
                "release exactly once.");

            var setupHandle = new CountingPipeSafeHandle();
            var setupNative = new FakeNamedPipeClientNative();
            setupNative.FailAcquire = true;
            RejectUnauthorized(
                delegate
                {
                    new MaintenanceNamedPipeClientImpersonationAdapter(
                        setupHandle,
                        setupNative).CaptureScoped(
                            new DelegateTokenCapture(
                                delegate { return evidence; }),
                            new FakeTerminator());
                });
            setupHandle.Dispose();
            Assert(
                setupNative.AcquireCalls == 1 &&
                setupNative.RevertCalls == 0 &&
                setupHandle.ReleaseCalls == 1,
                "Failed native setup fabricated a revert or leaked the " +
                "SafeHandle lease.");

            var captureHandle = new CountingPipeSafeHandle();
            var captureNative = new FakeNamedPipeClientNative();
            RejectUnauthorized(
                delegate
                {
                    new MaintenanceNamedPipeClientImpersonationAdapter(
                        captureHandle,
                        captureNative).CaptureScoped(
                            new DelegateTokenCapture(
                                delegate
                                {
                                    throw new IOException(
                                        "capture fault");
                                }),
                            new FakeTerminator());
                });
            captureHandle.Dispose();
            Assert(
                captureNative.AcquireCalls == 1 &&
                captureNative.RevertCalls == 1 &&
                captureHandle.ReleaseCalls == 1,
                "Capture failure did not revert once and settle the " +
                "SafeHandle lease.");

            var reentrantHandle =
                new CountingPipeSafeHandle();
            var reentrantNative =
                new FakeNamedPipeClientNative();
            MaintenanceNamedPipeClientImpersonationAdapter
                reentrantAdapter = null;
            reentrantAdapter =
                new MaintenanceNamedPipeClientImpersonationAdapter(
                    reentrantHandle,
                    reentrantNative);
            RejectUnauthorized(
                delegate
                {
                    reentrantAdapter.CaptureScoped(
                        new DelegateTokenCapture(
                            delegate
                            {
                                return reentrantAdapter.CaptureScoped(
                                    new DelegateTokenCapture(
                                        delegate
                                        {
                                            return evidence;
                                        }),
                                    new FakeTerminator());
                            }),
                        new FakeTerminator());
                });
            returned = reentrantAdapter.CaptureScoped(
                new DelegateTokenCapture(
                    delegate { return evidence; }),
                new FakeTerminator());
            reentrantHandle.Dispose();
            Assert(
                Object.ReferenceEquals(returned, evidence) &&
                reentrantNative.AcquireCalls == 2 &&
                reentrantNative.RevertCalls == 2 &&
                reentrantHandle.ReleaseCalls == 1,
                "Reentrant capture reached native code, cleared the " +
                "outer identity, or poisoned a safely reverted adapter.");

            foreach (string mode in new[]
            {
                "wrong-return",
                "wrong-throw",
                "revert-return",
                "revert-throw"
            })
            {
                AssertNativeFailStopChild(mode);
            }
        }

        private static int RunNativeFailStopChild(
            string mode,
            string markerToken)
        {
            string uiGuardFailure;
            bool uiGuardReady =
                TryDisableFailStopUi(out uiGuardFailure);
            string markerPath = Path.Combine(
                Path.GetTempPath(),
                markerToken);
            if (!uiGuardReady)
            {
                WriteFailStopMarker(
                    markerPath,
                    "ui-guard-failed:" + uiGuardFailure);
                return FailStopUiSetupFailed;
            }
            WriteFailStopMarker(
                markerPath,
                "child-enter:" + mode);
            var handle = new CountingPipeSafeHandle();
            var native = new FakeNamedPipeClientNative();
            var terminator = new FakeTerminator();
            bool terminatorThrows =
                mode == "wrong-throw" ||
                mode == "revert-throw";
            terminator.OnTerminate =
                delegate
                {
                    WriteFailStopMarker(
                        markerPath,
                        "terminator-enter:" +
                        (terminatorThrows ? "throw" : "return"));
                    if (terminatorThrows)
                    {
                        WriteFailStopMarker(
                            markerPath,
                            "terminator-throw");
                        throw new ApplicationException(
                            "terminator child sentinel");
                    }
                    WriteFailStopMarker(
                        markerPath,
                        "terminator-return");
                };
            if (mode == "revert-return" ||
                mode == "revert-throw")
            {
                native.FailRevert = true;
            }
            var adapter =
                new MaintenanceNamedPipeClientImpersonationAdapter(
                    handle,
                    native);
            adapter.CaptureScoped(
                new DelegateTokenCapture(
                    delegate
                    {
                        WriteFailStopMarker(
                            markerPath,
                            "capture-enter:" + mode);
                        if (mode == "wrong-return" ||
                            mode == "wrong-throw")
                        {
                            native.ThreadId = 2;
                        }
                        return ClientToken(
                            "S-1-5-18",
                            null,
                            false,
                            MaintenanceTokenElevationType.Default,
                            0x4000,
                            false,
                            false);
                    }),
                terminator);
            WriteFailStopMarker(
                markerPath,
                "returned-after-failstop");
            return 97;
        }

        private static bool TryDisableFailStopUi(
            out string failure)
        {
            try
            {
                uint requiredErrorMode =
                    SemFailCriticalErrors |
                    SemNoGpFaultErrorBox;
                FailStopUiNative.SetErrorMode(
                    requiredErrorMode);
                uint actualErrorMode =
                    FailStopUiNative.GetErrorMode();
                if ((actualErrorMode & requiredErrorMode) !=
                    requiredErrorMode)
                {
                    failure =
                        "SetErrorMode verification failed: actual=0x" +
                        actualErrorMode.ToString("X");
                    return false;
                }
                int werResult =
                    FailStopUiNative.WerSetFlags(
                        WerFaultReportingNoUi);
                if (werResult != 0)
                {
                    failure =
                        "WerSetFlags failed: hresult=0x" +
                        werResult.ToString("X8");
                    return false;
                }
                failure = null;
                return true;
            }
            catch (Exception exception)
            {
                failure =
                    "UI guard exception: " +
                    exception.GetType().FullName +
                    " hresult=0x" +
                    exception.HResult.ToString("X8");
                return false;
            }
        }

        private static class FailStopUiNative
        {
            [DllImport(
                "kernel32.dll",
                EntryPoint = "SetErrorMode")]
            internal static extern uint SetErrorMode(
                uint mode);

            [DllImport(
                "kernel32.dll",
                EntryPoint = "GetErrorMode")]
            internal static extern uint GetErrorMode();

            [DllImport(
                "kernel32.dll",
                EntryPoint = "WerSetFlags")]
            internal static extern int WerSetFlags(
                uint flags);
        }

        private static void WriteFailStopMarker(
            string markerPath,
            string marker)
        {
            File.AppendAllText(
                markerPath,
                marker + Environment.NewLine);
        }

        private static void AssertNativeFailStopChild(
            string mode)
        {
            string executable =
                Environment.GetCommandLineArgs()[0];
            string markerToken =
                "SBMS-native-failstop-" +
                Guid.NewGuid().ToString("N") +
                ".marker";
            string markerPath = Path.Combine(
                Path.GetTempPath(),
                markerToken);
            var start =
                new ProcessStartInfo();
            start.FileName = executable;
            start.Arguments =
                "--native-failstop-child " + mode +
                " " + markerToken;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.ErrorDialog = false;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            try
            {
                using (Process child = Process.Start(start))
                {
                    if (!child.WaitForExit(10000))
                    {
                        child.Kill();
                        child.WaitForExit();
                        throw new TimeoutException(
                            "Native fail-stop child timed out: " +
                            mode);
                    }
                    string output =
                        child.StandardOutput.ReadToEnd() +
                        child.StandardError.ReadToEnd();
                    string[] markers = File.Exists(markerPath)
                        ? File.ReadAllLines(markerPath)
                        : new string[0];
                    bool terminatorThrows =
                        mode == "wrong-throw" ||
                        mode == "revert-throw";
                    Assert(
                        Array.IndexOf(
                            markers,
                            "child-enter:" + mode) >= 0 &&
                        Array.IndexOf(
                            markers,
                            "capture-enter:" + mode) >= 0 &&
                        Array.IndexOf(
                            markers,
                            "terminator-enter:" +
                            (terminatorThrows
                                ? "throw"
                                : "return")) >= 0 &&
                        Array.IndexOf(
                            markers,
                            terminatorThrows
                                ? "terminator-throw"
                                : "terminator-return") >= 0 &&
                        Array.IndexOf(
                            markers,
                            terminatorThrows
                                ? "terminator-return"
                                : "terminator-throw") < 0 &&
                        Array.IndexOf(
                            markers,
                            "returned-after-failstop") < 0 &&
                        Array.FindIndex(
                            markers,
                            delegate(string marker)
                            {
                                return marker.StartsWith(
                                    "ui-guard-failed:",
                                    StringComparison.Ordinal);
                            }) < 0 &&
                        child.ExitCode != FailStopUiSetupFailed &&
                        child.ExitCode == CorEFailFast &&
                        child.ExitCode != CorEUnhandledException,
                        "Unsafe native child did not prove FailFast for " +
                        mode + ": exit=" + child.ExitCode +
                        " markers=" + String.Join(",", markers) +
                        " output=" + output);
                }
            }
            finally
            {
                if (File.Exists(markerPath))
                {
                    File.Delete(markerPath);
                }
            }
        }

        private static void ProductionSidCopyIsBoundedAndDeep()
        {
            byte[] sidBytes =
                FakeWindowsTokenNative.SidPayload(
                    "S-1-5-32-544");
            int fixedHeaderLength =
                Marshal.SizeOf(
                    typeof(MaintenanceNativeSidAndAttributes));
            // SidPayload contains the complete SID_AND_ATTRIBUTES prefix.
            byte[] canonical =
                new byte[sidBytes.Length - fixedHeaderLength];
            Buffer.BlockCopy(
                sidBytes,
                fixedHeaderLength,
                canonical,
                0,
                canonical.Length);
            const int prefix = 16;
            const int suffix = 16;
            int total = checked(prefix + canonical.Length + suffix);
            IntPtr allocation = Marshal.AllocHGlobal(total);
            try
            {
                for (int index = 0; index < total; ++index)
                {
                    Marshal.WriteByte(allocation, index, 0xCC);
                }
                IntPtr sid = IntPtr.Add(allocation, prefix);
                Marshal.Copy(canonical, 0, sid, canonical.Length);
                var native = new MaintenanceWindowsTokenNative();
                byte[] copy =
                    native.CopySid(sid, allocation, total);
                Assert(
                    BytesEqual(copy, canonical),
                    "Production CopySid changed valid SID bytes.");
                Marshal.WriteByte(sid, 0, 2);
                Assert(
                    BytesEqual(copy, canonical),
                    "Production CopySid did not return a deep copy.");

                ExpectCopySidReject(
                    native,
                    IntPtr.Subtract(allocation, 1),
                    allocation,
                    total);
                ExpectCopySidReject(
                    native,
                    IntPtr.Add(allocation, total),
                    allocation,
                    total);
                ExpectCopySidReject(
                    native,
                    IntPtr.Add(allocation, total - 7),
                    allocation,
                    total);

                Marshal.Copy(canonical, 0, sid, canonical.Length);
                Marshal.WriteByte(sid, 0, 2);
                ExpectCopySidReject(native, sid, allocation, total);
                Marshal.WriteByte(sid, 0, 1);
                Marshal.WriteByte(sid, 1, 16);
                ExpectCopySidReject(native, sid, allocation, total);
            }
            finally
            {
                Marshal.FreeHGlobal(allocation);
            }
        }

        private static void ExpectCopySidReject(
            MaintenanceWindowsTokenNative native,
            IntPtr sid,
            IntPtr allocation,
            int length)
        {
            RejectAny(
                delegate
                {
                    native.CopySid(sid, allocation, length);
                });
        }

        private static void WindowsTokenReaderFaultsAreBounded()
        {
            MaintenanceTokenInformationClass[] classes =
            {
                MaintenanceTokenInformationClass.Statistics,
                MaintenanceTokenInformationClass.User,
                MaintenanceTokenInformationClass.Groups,
                MaintenanceTokenInformationClass.Elevation,
                MaintenanceTokenInformationClass.ElevationType,
                MaintenanceTokenInformationClass.IntegrityLevel,
                MaintenanceTokenInformationClass.IsAppContainer,
                MaintenanceTokenInformationClass.HasRestrictions,
                MaintenanceTokenInformationClass.Type,
                MaintenanceTokenInformationClass.ImpersonationLevel
            };
            foreach (MaintenanceTokenInformationClass informationClass
                in classes)
            {
                var probe = new FakeWindowsTokenNative();
                probe.ProbeFailure = informationClass;
                RejectAny(
                    delegate
                    {
                        new MaintenanceWindowsTokenSnapshotReader(
                            probe).Capture();
                    });
                Assert(
                    probe.ReleaseCalls == 1,
                    informationClass +
                    " probe failure leaked the token handle.");

                var query = new FakeWindowsTokenNative();
                query.QueryFailure = informationClass;
                RejectAny(
                    delegate
                    {
                        new MaintenanceWindowsTokenSnapshotReader(
                            query).Capture();
                    });
                Assert(
                    query.ReleaseCalls == 1,
                    informationClass +
                    " query failure leaked the token handle.");
            }

            MaintenanceTokenInformationClass[] exactClasses =
            {
                MaintenanceTokenInformationClass.Statistics,
                MaintenanceTokenInformationClass.Elevation,
                MaintenanceTokenInformationClass.ElevationType,
                MaintenanceTokenInformationClass.IsAppContainer,
                MaintenanceTokenInformationClass.Type,
                MaintenanceTokenInformationClass.ImpersonationLevel
            };
            foreach (MaintenanceTokenInformationClass informationClass
                in exactClasses)
            {
                var wrongExactLength =
                    new FakeWindowsTokenNative();
                byte[] original =
                    wrongExactLength.Payloads[informationClass];
                byte[] oversized = new byte[original.Length + 1];
                Buffer.BlockCopy(
                    original,
                    0,
                    oversized,
                    0,
                    original.Length);
                wrongExactLength.Payloads[informationClass] =
                    oversized;
                RejectAny(
                    delegate
                    {
                        new MaintenanceWindowsTokenSnapshotReader(
                        wrongExactLength).Capture();
                    });
            }

            var byteRestriction = new FakeWindowsTokenNative();
            byteRestriction.Payloads[
                MaintenanceTokenInformationClass.HasRestrictions] =
                    new byte[] { 1 };
            Assert(
                new MaintenanceWindowsTokenSnapshotReader(
                    byteRestriction).Capture().IsRestricted,
                "One-byte Windows 11 TokenHasRestrictions was " +
                "not accepted.");
            foreach (int invalidLength in new[] { 2, 3, 5 })
            {
                var invalidRestriction =
                    new FakeWindowsTokenNative();
                invalidRestriction.Payloads[
                    MaintenanceTokenInformationClass.
                        HasRestrictions] =
                            new byte[invalidLength];
                RejectAny(
                    delegate
                    {
                        new MaintenanceWindowsTokenSnapshotReader(
                            invalidRestriction).Capture();
                    });
            }

            var resize = new FakeWindowsTokenNative();
            resize.ResizeOnce =
                MaintenanceTokenInformationClass.User;
            new MaintenanceWindowsTokenSnapshotReader(
                resize).Capture();
            var endlessResize = new FakeWindowsTokenNative();
            endlessResize.SizeRace =
                MaintenanceTokenInformationClass.User;
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        endlessResize).Capture();
                });

            var invalidProbe = new FakeWindowsTokenNative();
            invalidProbe.InvalidProbeLength =
                MaintenanceTokenInformationClass.User;
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        invalidProbe).Capture();
                });
            var unexpectedProbe = new FakeWindowsTokenNative();
            unexpectedProbe.ProbeUnexpectedSuccess =
                MaintenanceTokenInformationClass.User;
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        unexpectedProbe).Capture();
                });
            var oversizedProbe = new FakeWindowsTokenNative();
            oversizedProbe.OversizedProbe =
                MaintenanceTokenInformationClass.User;
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        oversizedProbe).Capture();
                });
            var invalidReturned = new FakeWindowsTokenNative();
            invalidReturned.InvalidReturnedLength =
                MaintenanceTokenInformationClass.User;
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        invalidReturned).Capture();
                });

            var truncated = new FakeWindowsTokenNative();
            truncated.Payloads[
                MaintenanceTokenInformationClass.User] =
                    new byte[1];
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        truncated).Capture();
                });
            var tooManyGroups = new FakeWindowsTokenNative();
            tooManyGroups.Payloads[
                MaintenanceTokenInformationClass.Groups] =
                    FakeWindowsTokenNative.Int32Payload(4097);
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        tooManyGroups).Capture();
                });
            var countMismatch = new FakeWindowsTokenNative();
            countMismatch.Payloads[
                MaintenanceTokenInformationClass.Groups] =
                    FakeWindowsTokenNative.GroupsPayload(
                        new string[0],
                        new uint[0]);
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        countMismatch).Capture();
                });
            var outsideSid = new FakeWindowsTokenNative();
            outsideSid.InvalidSidPointer = true;
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        outsideSid).Capture();
                });
            var sidInsideFixedHeader =
                new FakeWindowsTokenNative();
            sidInsideFixedHeader.SidPointerOffsetOverride =
                IntPtr.Size;
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        sidInsideFixedHeader).Capture();
                });
            var wrongIntegrityAuthority =
                new FakeWindowsTokenNative();
            wrongIntegrityAuthority.Payloads[
                MaintenanceTokenInformationClass.IntegrityLevel] =
                    FakeWindowsTokenNative.SidPayload("S-1-5-18");
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        wrongIntegrityAuthority).Capture();
                });
            var duplicateGroups = new FakeWindowsTokenNative();
            duplicateGroups.Payloads[
                MaintenanceTokenInformationClass.Groups] =
                    FakeWindowsTokenNative.GroupsPayload(
                        new[]
                        {
                            "S-1-5-32-544",
                            "S-1-5-32-544"
                        },
                        new[]
                        {
                            (uint)MaintenanceTokenGroupAttributes.Enabled,
                            (uint)MaintenanceTokenGroupAttributes.Enabled
                        });
            SetStatisticsGroupCount(duplicateGroups, 2);
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        duplicateGroups).Capture();
                });

            int[] sandwichOffsets =
            {
                0,
                8,
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "ExpirationTime")),
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "TokenType")),
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "ImpersonationLevel")),
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "DynamicCharged")),
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "DynamicAvailable")),
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "GroupCount")),
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "PrivilegeCount")),
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    "ModifiedId"))
            };
            foreach (int offset in sandwichOffsets)
            {
                var drifting = new FakeWindowsTokenNative();
                drifting.StatisticsDriftOffset = offset;
                RejectAny(
                    delegate
                    {
                        new MaintenanceWindowsTokenSnapshotReader(
                            drifting).Capture();
                    });
                Assert(
                    drifting.ReleaseCalls == 1,
                    "Unstable statistics leaked the token handle.");
            }

            var typeDisagreement = new FakeWindowsTokenNative();
            SetStatisticsInt32(
                typeDisagreement,
                "TokenType",
                1);
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        typeDisagreement).Capture();
                });
            var levelDisagreement = new FakeWindowsTokenNative();
            SetStatisticsInt32(
                levelDisagreement,
                "ImpersonationLevel",
                1);
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        levelDisagreement).Capture();
                });

            var openFailure = new FakeWindowsTokenNative();
            openFailure.FailOpen = true;
            RejectAny(
                delegate
                {
                    new MaintenanceWindowsTokenSnapshotReader(
                        openFailure).Capture();
                });
            Assert(
                openFailure.ReleaseCalls == 0,
                "Failed OpenThreadToken fabricated ownership.");
        }

        private static void
            ProcessLocalThreadTokenIntegrationRestoresSelf()
        {
            Exception failure = null;
            var thread = new Thread(
                new ThreadStart(
                delegate
                {
                    try
                    {
                        if (!ImpersonateSelfForTest(2))
                        {
                            throw new Win32Exception(
                                Marshal.GetLastWin32Error(),
                                "ImpersonateSelf failed.");
                        }
                        try
                        {
                            MaintenanceClientTokenEvidence snapshot =
                                new MaintenanceWindowsTokenSnapshotReader().
                                    Capture();
                            Assert(
                                !String.IsNullOrWhiteSpace(
                                    snapshot.UserSid) &&
                                snapshot.TokenType ==
                                    MaintenanceClientTokenType.
                                        Impersonation &&
                                snapshot.ImpersonationLevel >=
                                    MaintenanceClientImpersonationLevel.
                                        Impersonation,
                                "Real thread-token snapshot is invalid.");
                        }
                        finally
                        {
                            if (!MaintenanceWindowsNativeMethods.
                                    RevertToSelf())
                            {
                                throw new Win32Exception(
                                    Marshal.GetLastWin32Error(),
                                    "Integration RevertToSelf failed.");
                            }
                        }

                        try
                        {
                            using (MaintenanceSafeTokenHandle unexpected =
                                new MaintenanceWindowsTokenNative().
                                    OpenCurrentThreadTokenForQuery())
                            {
                            }
                        }
                        catch (Win32Exception exception)
                        {
                            if (exception.NativeErrorCode == 1008)
                            {
                                return;
                            }
                            throw;
                        }
                        throw new InvalidOperationException(
                            "Thread token survived RevertToSelf.");
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                }));
            thread.Start();
            thread.Join();
            if (failure != null)
            {
                throw new InvalidOperationException(
                    "Process-local thread-token integration failed: " +
                    failure,
                    failure);
            }
        }

        private static void SetStatisticsGroupCount(
            FakeWindowsTokenNative native,
            int count)
        {
            SetStatisticsInt32(native, "GroupCount", count);
        }

        private static void SetStatisticsInt32(
            FakeWindowsTokenNative native,
            string field,
            int value)
        {
            byte[] statistics = native.Payloads[
                MaintenanceTokenInformationClass.Statistics];
            byte[] bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(
                bytes,
                0,
                statistics,
                checked((int)Marshal.OffsetOf(
                    typeof(MaintenanceNativeTokenStatistics),
                    field)),
                bytes.Length);
        }

        [DllImport(
            "advapi32.dll",
            EntryPoint = "ImpersonateSelf",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImpersonateSelfForTest(
            int impersonationLevel);

        private static void RejectAny(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected failure.");
        }

        private static void RejectExact(
            Action action,
            Exception expected)
        {
            try
            {
                action();
            }
            catch (Exception actual)
            {
                Assert(
                    Object.ReferenceEquals(actual, expected),
                    "Unexpected exception replaced the sentinel.");
                return;
            }
            throw new InvalidOperationException(
                "Expected sentinel failure.");
        }

        private static MaintenanceClientTokenGroupEvidence Group(
            string sid,
            bool enabled,
            bool denyOnly)
        {
            MaintenanceTokenGroupAttributes attributes =
                MaintenanceTokenGroupAttributes.None;
            if (enabled)
            {
                attributes |= MaintenanceTokenGroupAttributes.Enabled;
            }
            if (denyOnly)
            {
                attributes |=
                    MaintenanceTokenGroupAttributes.UseForDenyOnly;
            }
            return new MaintenanceClientTokenGroupEvidence(
                sid,
                attributes);
        }

        private static MaintenanceClientTokenEvidence ClientToken(
            string userSid,
            MaintenanceClientTokenGroupEvidence group,
            bool elevated,
            MaintenanceTokenElevationType elevationType,
            int integrityRid,
            bool appContainer,
            bool restricted)
        {
            return ClientToken(
                userSid,
                group,
                elevated,
                elevationType,
                integrityRid,
                appContainer,
                restricted,
                MaintenanceClientTokenType.Impersonation,
                MaintenanceClientImpersonationLevel.Impersonation,
                0);
        }

        private static MaintenanceClientTokenEvidence ClientToken(
            string userSid,
            MaintenanceClientTokenGroupEvidence group,
            bool elevated,
            MaintenanceTokenElevationType elevationType,
            int integrityRid,
            bool appContainer,
            bool restricted,
            MaintenanceClientTokenType tokenType,
            MaintenanceClientImpersonationLevel impersonationLevel,
            long authenticationId)
        {
            IEnumerable<MaintenanceClientTokenGroupEvidence> groups =
                group == null
                    ? new MaintenanceClientTokenGroupEvidence[0]
                    : new[] { group };
            return new MaintenanceClientTokenEvidence(
                userSid,
                groups,
                elevated,
                elevationType,
                integrityRid,
                appContainer,
                restricted,
                tokenType,
                impersonationLevel,
                authenticationId);
        }

        private static void AllowClient(
            IMaintenanceClientPolicyAuthorizer policy,
            MaintenanceClientTokenEvidence evidence)
        {
            Assert(
                policy.Authorize(
                    evidence,
                    CancellationToken.None) != null,
                "Expected client token authorization.");
        }

        private static void RejectClient(
            IMaintenanceClientPolicyAuthorizer policy,
            MaintenanceClientTokenEvidence evidence)
        {
            RejectUnauthorized(
                delegate
                {
                    policy.Authorize(
                        evidence,
                        CancellationToken.None);
                });
        }

        private static void VerifyCaptureFailure(
            Exception captureFailure,
            bool returnNull)
        {
            var events = new List<string>();
            var impersonation =
                new SequenceImpersonationRunner(events);
            var capture =
                new SequenceTokenCapture(impersonation, events);
            capture.Failure = captureFailure;
            capture.Evidence = returnNull
                ? null
                : ClientToken(
                    "S-1-5-18",
                    null,
                    false,
                    MaintenanceTokenElevationType.Default,
                    0x4000,
                    false,
                    false);
            var terminator = new FakeTerminator();
            var policy =
                new SequencePolicyAuthorizer(
                    new MaintenanceProductionClientPolicyAuthorizer(),
                    events);
            var dispatcher =
                new SequencePreauthorizedDispatcher(events);
            var sequencer =
                new MaintenanceClientRequestSequencer(
                    new MaintenanceClientCaptureRunner(
                        impersonation,
                        capture,
                        terminator),
                    policy,
                    dispatcher);
            RejectUnauthorized(
                delegate
                {
                    sequencer.Execute(
                        delegate
                        {
                            events.Add("parse");
                            return Command();
                        },
                        CancellationToken.None);
                });
            Assert(
                impersonation.RevertCalls == 1 &&
                policy.Calls == 0 &&
                dispatcher.Calls == 0 &&
                !events.Contains("parse") &&
                terminator.Calls == 0,
                "Capture/query failure did not deny after one revert.");
        }

        private static void VerifyImpersonateFailure()
        {
            var events = new List<string>();
            var impersonation =
                new SequenceImpersonationRunner(events);
            impersonation.ImpersonateFailure =
                new IOException("impersonation failure");
            var capture =
                new SequenceTokenCapture(impersonation, events);
            var terminator = new FakeTerminator();
            var policy =
                new SequencePolicyAuthorizer(
                    new MaintenanceProductionClientPolicyAuthorizer(),
                    events);
            var dispatcher =
                new SequencePreauthorizedDispatcher(events);
            var sequencer =
                new MaintenanceClientRequestSequencer(
                    new MaintenanceClientCaptureRunner(
                        impersonation,
                        capture,
                        terminator),
                    policy,
                    dispatcher);
            RejectUnauthorized(
                delegate
                {
                    sequencer.Execute(
                        delegate
                        {
                            events.Add("parse");
                            return Command();
                        },
                        CancellationToken.None);
                });
            Assert(
                impersonation.ImpersonateCalls == 1 &&
                impersonation.RevertCalls == 0 &&
                capture.Calls == 0 &&
                policy.Calls == 0 &&
                dispatcher.Calls == 0 &&
                !events.Contains("parse") &&
                terminator.Calls == 0,
                "Impersonation setup failure armed a revert.");
        }

        private static void VerifyRevertFailure(
            bool captureAlsoFails)
        {
            var events = new List<string>();
            var impersonation =
                new SequenceImpersonationRunner(events);
            impersonation.RevertFailure =
                new IOException("revert failure");
            var capture =
                new SequenceTokenCapture(impersonation, events);
            capture.Evidence = ClientToken(
                "S-1-5-18",
                null,
                false,
                MaintenanceTokenElevationType.Default,
                0x4000,
                false,
                false);
            if (captureAlsoFails)
            {
                capture.Failure =
                    new IOException("capture failure");
            }
            var terminator = new FakeTerminator();
            terminator.OnTerminate =
                delegate { events.Add("terminate"); };
            var policy =
                new SequencePolicyAuthorizer(
                    new MaintenanceProductionClientPolicyAuthorizer(),
                    events);
            var dispatcher =
                new SequencePreauthorizedDispatcher(events);
            var sequencer =
                new MaintenanceClientRequestSequencer(
                    new MaintenanceClientCaptureRunner(
                        impersonation,
                        capture,
                        terminator),
                    policy,
                    dispatcher);
            RejectInvalid(
                delegate
                {
                    sequencer.Execute(
                        delegate
                        {
                            events.Add("parse");
                            return Command();
                        },
                        CancellationToken.None);
                },
                "terminator returned");
            Assert(
                impersonation.RevertCalls == 1 &&
                terminator.Calls == 1 &&
                policy.Calls == 0 &&
                dispatcher.Calls == 0 &&
                !events.Contains("parse") &&
                events[events.Count - 1] == "terminate",
                "Revert failure did not terminate before continuation.");
        }

        private static void VerifyTerminatorSentinel()
        {
            var events = new List<string>();
            var impersonation =
                new SequenceImpersonationRunner(events);
            impersonation.RevertFailure =
                new IOException("revert failure");
            var capture =
                new SequenceTokenCapture(impersonation, events);
            capture.Evidence = ClientToken(
                "S-1-5-18",
                null,
                false,
                MaintenanceTokenElevationType.Default,
                0x4000,
                false,
                false);
            var sentinel =
                new ApplicationException("terminator sentinel");
            var terminator = new FakeTerminator();
            terminator.OnTerminate =
                delegate
                {
                    events.Add("terminate");
                    throw sentinel;
                };
            var policy =
                new SequencePolicyAuthorizer(
                    new MaintenanceProductionClientPolicyAuthorizer(),
                    events);
            var dispatcher =
                new SequencePreauthorizedDispatcher(events);
            var sequencer =
                new MaintenanceClientRequestSequencer(
                    new MaintenanceClientCaptureRunner(
                        impersonation,
                        capture,
                        terminator),
                    policy,
                    dispatcher);
            try
            {
                sequencer.Execute(
                    delegate
                    {
                        events.Add("parse");
                        return Command();
                    },
                    CancellationToken.None);
            }
            catch (ApplicationException exception)
            {
                Assert(
                    Object.ReferenceEquals(exception, sentinel) &&
                    impersonation.RevertCalls == 1 &&
                    terminator.Calls == 1 &&
                    policy.Calls == 0 &&
                    dispatcher.Calls == 0 &&
                    !events.Contains("parse"),
                    "Terminator sentinel did not stop continuation.");
                return;
            }
            throw new InvalidOperationException(
                "Expected terminator sentinel.");
        }

        private static void AssertSequence(
            IList<string> actual,
            params string[] expected)
        {
            Assert(
                actual.Count == expected.Length,
                "Sequence length mismatch: " +
                String.Join(",", actual));
            for (int index = 0; index < expected.Length; ++index)
            {
                Assert(
                    actual[index] == expected[index],
                    "Sequence mismatch at " + index + ": " +
                    String.Join(",", actual));
            }
        }

        private static SecurityIdentifier WellKnownSid(
            WellKnownSidType type)
        {
            return new SecurityIdentifier(type, null);
        }

        private static void AssertProtectedOwnerAndDacl(
            RawSecurityDescriptor descriptor,
            int expectedAceCount,
            string label)
        {
            Assert(
                descriptor.Owner.Value ==
                    WellKnownSid(
                        WellKnownSidType.LocalSystemSid).Value,
                label + " owner is not LocalSystem.");
            Assert(
                (descriptor.ControlFlags &
                    ControlFlags.DiscretionaryAclProtected) != 0,
                label + " DACL is not protected.");
            Assert(
                descriptor.DiscretionaryAcl != null &&
                descriptor.DiscretionaryAcl.Count ==
                    expectedAceCount,
                label + " DACL count changed.");
        }

        private static void AssertExactAllowAce(
            RawAcl dacl,
            int index,
            SecurityIdentifier expectedSid,
            int expectedMask,
            AceFlags expectedFlags,
            string label)
        {
            var ace = dacl[index] as CommonAce;
            Assert(ace != null, label + " ACE is not CommonAce.");
            if (ace == null)
            {
                return;
            }
            Assert(
                ace.AceQualifier == AceQualifier.AccessAllowed,
                label + " ACE is not AccessControlType Allow.");
            Assert(
                ace.AceFlags == expectedFlags,
                label + " ACE inheritance flags changed.");
            Assert(
                ace.AccessMask == expectedMask,
                label + " ACE access mask changed.");
            Assert(
                ace.SecurityIdentifier.Value == expectedSid.Value,
                label + " ACE principal or order changed.");
            byte[] opaque = ace.GetOpaque();
            Assert(
                !ace.IsCallback &&
                (opaque == null || opaque.Length == 0),
                label + " ACE contains unexpected callback data.");
        }

        private static void LifecycleIsTerminal()
        {
            var normal = new MaintenanceLifecycle(
                new FakeTerminator(),
                TimeSpan.FromMilliseconds(100));
            normal.Start(
                TimeSpan.FromSeconds(1),
                delegate(CancellationToken token) { });
            normal.Stop(
                TimeSpan.FromSeconds(1),
                delegate(CancellationToken token) { });
            Assert(
                normal.State == MaintenanceLifecycleState.Stopped,
                "Normal lifecycle did not stop.");

            var original = new MaintenanceLifecycle(
                new FakeTerminator(),
                TimeSpan.FromMilliseconds(100));
            RejectInvalid(delegate
            {
                original.Start(
                    TimeSpan.FromSeconds(1),
                    delegate(CancellationToken token)
                    {
                        throw new InvalidOperationException(
                            "original");
                    });
            }, "original");

            var cooperative = new MaintenanceLifecycle(
                new FakeTerminator(),
                TimeSpan.FromMilliseconds(100));
            RejectCanceled(delegate
            {
                cooperative.Start(
                    TimeSpan.FromMilliseconds(20),
                    delegate(CancellationToken token)
                    {
                        token.WaitHandle.WaitOne();
                        token.ThrowIfCancellationRequested();
                    });
            });

            var exit = new ManualResetEvent(false);
            var terminator = new FakeTerminator();
            terminator.OnTerminate = delegate { exit.Set(); };
            var uncooperative = new MaintenanceLifecycle(
                terminator,
                TimeSpan.FromMilliseconds(20));
            RejectTimeout(delegate
            {
                uncooperative.Start(
                    TimeSpan.FromMilliseconds(20),
                    delegate(CancellationToken token)
                    {
                        exit.WaitOne();
                    });
            });
            Assert(
                terminator.Calls == 1 &&
                uncooperative.State ==
                    MaintenanceLifecycleState.Faulted,
                "Uncooperative operation did not invoke terminator.");
        }

        private static void DispatcherIsSafe()
        {
            var dispatcher =
                new SerializedMaintenanceCommandDispatcher(
                    new FakeAuthorizer());
            PayloadBrokerCommand command = Command();
            dispatcher.Dispatch(
                command,
                CancellationToken.None,
                delegate(
                    PayloadBrokerCommand inner,
                    MaintenanceAuthorizationEvidence evidence,
                    CancellationToken cancellation)
                {
                    RejectInvalid(delegate
                    {
                        dispatcher.Dispatch(
                            inner,
                            cancellation,
                            delegate(
                                PayloadBrokerCommand nested,
                                MaintenanceAuthorizationEvidence grant,
                                CancellationToken token)
                            {
                                return Response(nested);
                            });
                    }, "non-reentrant");
                    return Response(inner);
                });
            Assert(
                dispatcher.MaximumObservedConcurrency == 1,
                "Dispatcher concurrency exceeded one.");

            var reentrantAuthorizer = new FakeAuthorizer();
            SerializedMaintenanceCommandDispatcher
                reentrantDispatcher = null;
            bool authorizerReentryRejected = false;
            reentrantAuthorizer.OnAuthorize =
                delegate(
                    PayloadBrokerCommand authorized,
                    CancellationToken cancellation)
                {
                    RejectInvalid(delegate
                    {
                        reentrantDispatcher.Dispatch(
                            authorized,
                            cancellation,
                            delegate(
                                PayloadBrokerCommand nested,
                                MaintenanceAuthorizationEvidence grant,
                                CancellationToken token)
                            {
                                return Response(nested);
                            });
                    }, "non-reentrant");
                    authorizerReentryRejected = true;
                };
            reentrantDispatcher =
                new SerializedMaintenanceCommandDispatcher(
                    reentrantAuthorizer);
            reentrantDispatcher.Dispatch(
                command,
                CancellationToken.None,
                delegate(
                    PayloadBrokerCommand authorized,
                    MaintenanceAuthorizationEvidence evidence,
                    CancellationToken cancellation)
                {
                    return Response(authorized);
                });
            Assert(
                authorizerReentryRejected,
                "Authorizer callback re-entry was not exercised.");

            var denied = new FakeAuthorizer();
            denied.Grant = false;
            var deniedDispatcher =
                new SerializedMaintenanceCommandDispatcher(
                    denied);
            RejectUnauthorized(delegate
            {
                deniedDispatcher.Dispatch(
                    command,
                    CancellationToken.None,
                    delegate(
                        PayloadBrokerCommand inner,
                        MaintenanceAuthorizationEvidence evidence,
                        CancellationToken token)
                    {
                        return Response(inner);
                    });
            });
            denied.Grant = true;
            deniedDispatcher.Dispatch(
                command,
                CancellationToken.None,
                delegate(
                    PayloadBrokerCommand inner,
                    MaintenanceAuthorizationEvidence evidence,
                    CancellationToken token)
                {
                    return Response(inner);
                });
        }

        private static void DispatcherCancelsWait()
        {
            var authorizer = new FakeAuthorizer();
            authorizer.IgnoreCancellation = true;
            var dispatcher =
                new SerializedMaintenanceCommandDispatcher(
                    authorizer);
            var entered = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            Exception firstFailure = null;
            var first = new Thread(new ThreadStart(delegate
            {
                try
                {
                    dispatcher.Dispatch(
                        Command(),
                        CancellationToken.None,
                        delegate(
                            PayloadBrokerCommand command,
                            MaintenanceAuthorizationEvidence evidence,
                            CancellationToken token)
                        {
                            entered.Set();
                            release.WaitOne();
                            return Response(command);
                        });
                }
                catch (Exception exception)
                {
                    firstFailure = exception;
                }
            }));
            first.Start();
            entered.WaitOne();
            authorizer.Authorized.Reset();
            using (var cancellation =
                new CancellationTokenSource())
            {
                Exception secondFailure = null;
                var second = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        dispatcher.Dispatch(
                            Command(),
                            cancellation.Token,
                            delegate(
                                PayloadBrokerCommand command,
                                MaintenanceAuthorizationEvidence evidence,
                                CancellationToken token)
                            {
                                return Response(command);
                            });
                    }
                    catch (Exception exception)
                    {
                        secondFailure = exception;
                    }
                }));
                second.Start();
                Assert(
                    authorizer.Authorized.WaitOne(
                        TimeSpan.FromSeconds(2)),
                    "Second command did not reach lock wait.");
                cancellation.Cancel();
                Assert(
                    second.Join(TimeSpan.FromSeconds(2)),
                    "Canceled lock waiter did not exit.");
                Assert(
                    secondFailure is OperationCanceledException,
                    "Canceled lock waiter did not preserve cancellation.");
            }
            release.Set();
            first.Join();
            if (firstFailure != null)
            {
                throw firstFailure;
            }
        }

        private static void DispatcherExecuteCancellationClearsState()
        {
            var dispatcher =
                new SerializedMaintenanceCommandDispatcher(
                    new FakeAuthorizer());
            using (var cancellation =
                new CancellationTokenSource())
            {
                RejectCanceled(delegate
                {
                    dispatcher.Dispatch(
                        Command(),
                        cancellation.Token,
                        delegate(
                            PayloadBrokerCommand command,
                            MaintenanceAuthorizationEvidence evidence,
                            CancellationToken token)
                        {
                            cancellation.Cancel();
                            token.ThrowIfCancellationRequested();
                            return Response(command);
                        });
                });
            }
            PayloadBrokerCommand retry = Command();
            dispatcher.Dispatch(
                retry,
                CancellationToken.None,
                delegate(
                    PayloadBrokerCommand command,
                    MaintenanceAuthorizationEvidence evidence,
                    CancellationToken token)
                {
                    return Response(command);
                });
            Assert(
                dispatcher.MaximumObservedConcurrency == 1,
                "Execute cancellation leaked dispatcher state.");
        }

        private static void ReplayCodecIsCanonical()
        {
            PayloadBrokerCommand command = Command();
            var prepared = Prepared(command);
            byte[] bytes =
                MaintenanceReplayRecordCodec.
                    SerializeCanonical(prepared);
            MaintenanceReplayRecordCodec.
                DeserializeCanonical(bytes).
                ValidateRequest(command);
            byte[] trailing = new byte[bytes.Length + 1];
            Array.Copy(bytes, trailing, bytes.Length);
            trailing[trailing.Length - 1] = (byte)' ';
            RejectReplayFormat(delegate
            {
                MaintenanceReplayRecordCodec.
                    DeserializeCanonical(trailing);
            }, null);
            bytes[bytes.Length - 1] ^= 1;
            RejectReplayFormat(delegate
            {
                MaintenanceReplayRecordCodec.
                    DeserializeCanonical(bytes);
            }, null);
            MaintenanceReplayRecord forged = Prepared(command);
            forged.StorageKeyInvariantDigest = Digest('9');
            RejectReplayFormat(delegate
            {
                MaintenanceReplayRecordCodec.
                    SerializeCanonical(forged);
            }, "storage-key");
        }

        private static void PreparedRetryUsesAuthoritativeReconciliation()
        {
            mutationCount = 0;
            reconcileCount = 0;
            var store = new FakeReplayStore();
            var executor =
                Executor(store);
            PayloadBrokerCommand command = Command();
            WriteRecord(store, command, Prepared(command));
            PayloadBrokerResponse recovered =
                executor.Execute(
                    command,
                    Mutate,
                    InspectOrResumeAuthoritativeState);
            recovered.ValidateForCommand(command);
            Assert(
                mutationCount == 0 &&
                reconcileCount == 1,
                "Prepared retry called original mutation instead of authoritative reconciliation.");
            executor.Execute(
                command,
                Mutate,
                InspectOrResumeAuthoritativeState);
            Assert(
                mutationCount == 0 &&
                reconcileCount == 1,
                "Committed retry did not exact-replay.");
        }

        private static void ReplayKeyRejectsDifferentCommand()
        {
            PayloadBrokerCommand original = Command();
            PayloadBrokerCommand different = Command();
            different.PlanInvariantDigest = Digest('4');
            different.Validate();

            foreach (MaintenanceReplayRecord record in
                new[]
                {
                    Prepared(original),
                    Committed(original)
                })
            {
                mutationCount = 0;
                reconcileCount = 0;
                var store = new FakeReplayStore();
                WriteRecord(store, original, record);
                RejectInvalid(delegate
                {
                    Executor(store).Execute(
                            different,
                            Mutate,
                            InspectOrResumeAuthoritativeState);
                }, null);
                Assert(
                    mutationCount == 0 &&
                    reconcileCount == 0,
                    "Mismatched replay key invoked mutation or reconciliation.");
            }
        }

        private static void AtomicFaultMatrix()
        {
            PayloadBrokerCommand command = Command();

            mutationCount = 0;
            reconcileCount = 0;
            var lostPreparedReturn = new FakeReplayStore();
            lostPreparedReturn.Faults.Enqueue(
                FakeStoreFault.AfterCommit);
            Executor(lostPreparedReturn).Execute(
                    command,
                    Mutate,
                    InspectOrResumeAuthoritativeState);
            Assert(
                mutationCount == 1,
                "Committed Prepared write was not recognized.");

            mutationCount = 0;
            reconcileCount = 0;
            var committedFailure = new FakeReplayStore();
            committedFailure.Faults.Enqueue(
                FakeStoreFault.None);
            committedFailure.Faults.Enqueue(
                FakeStoreFault.BeforeCommit);
            var executor =
                Executor(committedFailure);
            RejectIo(delegate
            {
                executor.Execute(
                    command,
                    Mutate,
                    InspectOrResumeAuthoritativeState);
            });
            executor.Execute(
                command,
                Mutate,
                InspectOrResumeAuthoritativeState);
            Assert(
                mutationCount == 1 &&
                reconcileCount == 1,
                "Failed Committed write caused remutation.");

            var corrupt = new FakeReplayStore();
            corrupt.Faults.Enqueue(
                FakeStoreFault.CorruptAfterCommit);
            RejectIo(delegate
            {
                Executor(corrupt).Execute(
                        command,
                        Mutate,
                        InspectOrResumeAuthoritativeState);
            });
        }

        private static void ProductionReplayUsesNativeRoot()
        {
            string commonRoot = Path.Combine(
                Path.GetTempPath(),
                "SBMS-maintenance-native-" +
                    Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(commonRoot);
            var seam = new ReplayNativeFaultSeam();
            SecurityIdentifier current =
                WindowsIdentity.GetCurrent().User;
            var profile = new WindowsJournalSecurityProfile
            {
                Owner = current,
                FullControlIdentities =
                    new[] { current }
            };
            try
            {
                using (var fileSystem =
                    new WindowsHandleRelativeJournalFileSystem(
                        commonRoot,
                        profile,
                        seam))
                {
                    string mutexName =
                        @"Local\SBMS.Maintenance.Test." +
                        Guid.NewGuid().ToString("N");
                    var journal =
                        new FileTransactionJournalStore(
                            new TestProgramDataPathProvider(commonRoot),
                            new NoOpInstallerJournalAclPolicy(),
                            mutexName,
                            TimeSpan.FromSeconds(2),
                            null,
                            fileSystem,
                            new UnsecuredInstallerTransactionMutexFactory());
                    IMaintenanceReplayAtomicStore store =
                        journal.CreateMaintenanceReplayStore();
                    Assert(
                        Object.ReferenceEquals(
                            store,
                            journal.CreateMaintenanceReplayStore()),
                        "Replay factory did not return its singleton.");
                    RejectInvalid(delegate
                    {
                        new MaintenanceWriteBeforeAckExecutor(
                            store,
                            Digest('9'));
                    }, "authority");
                    mutationCount = 0;
                    reconcileCount = 0;
                    PayloadBrokerCommand command = Command();
                    seam.FailNextPublishedVerification = true;
                    Executor(
                        store,
                        journal.
                            MaintenanceReplayRootAuthorityInvariantDigest).
                        Execute(
                            command,
                            Mutate,
                            InspectOrResumeAuthoritativeState);
                    Assert(
                        mutationCount == 1,
                        "Native replay did not commit its first mutation.");

                    string primary = Path.Combine(
                        commonRoot,
                        "SBMS",
                        "Installer",
                        "maintenance-replay",
                        "v1",
                        command.TransactionId,
                        command.RequestId + ".json");
                    string candidate = primary + ".new";
                    Assert(
                        File.Exists(primary),
                        "Fixed maintenance replay layout was not used.");
                    File.WriteAllBytes(primary, new byte[] { 1, 2, 3 });
                    File.WriteAllBytes(candidate, new byte[] { 4 });
                    Executor(
                        store,
                        journal.
                            MaintenanceReplayRootAuthorityInvariantDigest).
                        Execute(
                            command,
                            Mutate,
                            InspectOrResumeAuthoritativeState);
                    Assert(
                        mutationCount == 1 &&
                        reconcileCount == 1 &&
                        !File.Exists(candidate),
                        "Backup recovery remutated or retained stale candidate.");
                    byte[] final = File.ReadAllBytes(primary);
                    MaintenanceReplayRecord recovered =
                        MaintenanceReplayRecordCodec.
                            DeserializeCanonical(final);
                    Assert(
                        recovered.State ==
                            MaintenanceReplayRecordState.Committed,
                        "Backup recovery did not republish Committed.");
                    string backup = primary + ".bak";
                    File.WriteAllBytes(backup, final);
                    using (FileStream oversized = new FileStream(
                        primary,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        oversized.SetLength(
                            AtomicDocumentBytePublisher.
                                MaximumDocumentBytes + 1L);
                    }
                    using (IMaintenanceReplayStoreLease readLease =
                        store.AcquireExclusiveLease())
                    {
                        byte[] fallback;
                        Assert(
                            readLease.TryRead(
                                command.TransactionId + ":" +
                                    command.RequestId,
                                out fallback) &&
                            BytesEqual(final, fallback),
                            "Oversized primary did not fall back to the valid backup.");
                    }

                    AssertBidirectionalLeaseExclusion(
                        journal,
                        store);
                    IMaintenanceReplayStoreLease active =
                        store.AcquireExclusiveLease();
                    RejectInvalid(
                        journal.Dispose,
                        "active");
                    active.Dispose();
                    journal.Dispose();
                    RejectDisposed(delegate
                    {
                        store.AcquireExclusiveLease();
                    });
                    RejectDisposed(delegate
                    {
                        journal.CreateMaintenanceReplayStore();
                    });
                }
            }
            finally
            {
                if (Directory.Exists(commonRoot))
                {
                    Directory.Delete(commonRoot, true);
                }
            }
        }

        private static void ReplayReadFailuresDoNotFallback()
        {
            foreach (Exception expected in new Exception[]
                {
                    new UnauthorizedAccessException("denied"),
                    new IOException("sharing"),
                    new OutOfMemoryException("oom")
                })
            {
                var fileSystem =
                    new ThrowingReadFileSystem(expected);
                var store =
                    new MaintenanceReplayProductionStore(
                        fileSystem,
                        delegate
                        {
                            return new NoOpDisposable();
                        },
                        Digest('f'));
                using (IMaintenanceReplayStoreLease lease =
                    store.AcquireExclusiveLease())
                {
                    Exception observed = null;
                    try
                    {
                        byte[] ignored;
                        lease.TryRead(
                            TransactionId + ":" + RequestId,
                            out ignored);
                    }
                    catch (Exception exception)
                    {
                        observed = exception;
                    }
                    Assert(
                        Object.ReferenceEquals(expected, observed) &&
                        fileSystem.OpenReadCalls == 1,
                        "Primary read failure was replaced by backup fallback.");
                }
                store.DisposeFromParent();
            }
        }

        private static void ReplayLifecycleGateClosesRaces()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "SBMS-maintenance-lifecycle-" +
                    Guid.NewGuid().ToString("N"));
            var createFileSystem =
                new LifecycleBarrierFileSystem
                {
                    BlockAuthority = true
                };
            var createJournal =
                NewLifecycleJournal(
                    root,
                    new NoOpInstallerJournalAclPolicy(),
                    createFileSystem);
            IMaintenanceReplayAtomicStore created = null;
            Exception createFailure = null;
            Exception disposeFailure = null;
            var disposeCompleted = new ManualResetEvent(false);
            var createThread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    created =
                        createJournal.CreateMaintenanceReplayStore();
                }
                catch (Exception exception)
                {
                    createFailure = exception;
                }
            }));
            createThread.Start();
            Assert(
                createFileSystem.AuthorityEntered.WaitOne(
                    TimeSpan.FromSeconds(2)),
                "Create did not enter the authority barrier.");
            var disposeThread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    createJournal.Dispose();
                }
                catch (Exception exception)
                {
                    disposeFailure = exception;
                }
                finally
                {
                    disposeCompleted.Set();
                }
            }));
            disposeThread.Start();
            Assert(
                !disposeCompleted.WaitOne(100),
                "Dispose bypassed an in-progress replay factory call.");
            createFileSystem.ReleaseAuthority.Set();
            Assert(
                createThread.Join(2000) &&
                disposeThread.Join(2000) &&
                createFailure == null &&
                disposeFailure == null &&
                created != null &&
                createFileSystem.Disposed,
                "Create and Dispose did not serialize on one lifecycle gate.");
            RejectDisposed(delegate
            {
                created.AcquireExclusiveLease();
            });

            var acquireFileSystem =
                new LifecycleBarrierFileSystem();
            var blockingAcl =
                new BlockingInstallerJournalAclPolicy();
            var acquireJournal =
                NewLifecycleJournal(
                    root + "-acquire",
                    blockingAcl,
                    acquireFileSystem);
            IMaintenanceReplayAtomicStore acquireStore =
                acquireJournal.CreateMaintenanceReplayStore();
            Exception acquireFailure = null;
            Exception racedDisposeFailure = null;
            var leaseAcquired = new ManualResetEvent(false);
            var releaseLease = new ManualResetEvent(false);
            var acquireThread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    using (IMaintenanceReplayStoreLease lease =
                        acquireStore.AcquireExclusiveLease())
                    {
                        leaseAcquired.Set();
                        if (!releaseLease.WaitOne(
                                TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException(
                                "Lifecycle lease barrier was not released.");
                        }
                    }
                }
                catch (Exception exception)
                {
                    acquireFailure = exception;
                }
            }));
            acquireThread.Start();
            Assert(
                blockingAcl.Entered.WaitOne(TimeSpan.FromSeconds(2)),
                "Acquire did not enter the shared-lease barrier.");
            var racedDisposeThread =
                new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        acquireJournal.Dispose();
                    }
                    catch (Exception exception)
                    {
                        racedDisposeFailure = exception;
                    }
                }));
            racedDisposeThread.Start();
            Assert(
                racedDisposeThread.Join(2000) &&
                racedDisposeFailure is InvalidOperationException &&
                !acquireFileSystem.Disposed,
                "Parent disposal released storage during child acquisition.");
            blockingAcl.Release.Set();
            Assert(
                leaseAcquired.WaitOne(TimeSpan.FromSeconds(2)),
                "Child acquisition did not resume after its barrier.");
            releaseLease.Set();
            Assert(
                acquireThread.Join(2000) &&
                acquireFailure == null,
                "Child lease did not close cleanly after the race.");
            acquireJournal.Dispose();
            Assert(
                acquireFileSystem.Disposed,
                "Parent did not dispose storage after the child lease closed.");
        }

        private static FileTransactionJournalStore NewLifecycleJournal(
            string root,
            IInstallerJournalAclPolicy aclPolicy,
            IAtomicJournalFileSystem fileSystem)
        {
            return new FileTransactionJournalStore(
                new TestProgramDataPathProvider(root),
                aclPolicy,
                @"Local\SBMS.Maintenance.Lifecycle." +
                    Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(2),
                null,
                fileSystem,
                new UnsecuredInstallerTransactionMutexFactory());
        }

        private static FileTransactionJournalStore
            NewThrowingReleaseJournal(
                string root,
                LifecycleBarrierFileSystem fileSystem,
                ThrowingReleaseLeaseSource leaseSource)
        {
            return new FileTransactionJournalStore(
                new TestProgramDataPathProvider(root),
                new NoOpInstallerJournalAclPolicy(),
                @"Local\SBMS.Maintenance.Release." +
                    Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(2),
                null,
                fileSystem,
                new UnsecuredInstallerTransactionMutexFactory(),
                leaseSource.Acquire,
                leaseSource.IsHeldByCurrentThread);
        }

        private static void ThrowingLeaseReleaseIsFailClosed()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "SBMS-maintenance-release-" +
                    Guid.NewGuid().ToString("N"));
            var uncertainSource =
                new ThrowingReleaseLeaseSource(
                    InstallerTransactionLeaseReleaseOutcome.Uncertain,
                    false);
            var uncertainFileSystem =
                new LifecycleBarrierFileSystem();
            FileTransactionJournalStore uncertainJournal =
                NewThrowingReleaseJournal(
                    root,
                    uncertainFileSystem,
                    uncertainSource);
            IMaintenanceReplayAtomicStore uncertainStore =
                uncertainJournal.CreateMaintenanceReplayStore();
            IMaintenanceReplayStoreLease uncertainLease =
                uncertainStore.AcquireExclusiveLease();
            RejectIo(uncertainLease.Dispose);
            Assert(
                uncertainJournal.IsPoisoned &&
                uncertainSource.AcquireCalls == 1 &&
                uncertainSource.ReleaseAttempts == 1 &&
                uncertainSource.UnderlyingHeld &&
                !uncertainFileSystem.Disposed,
                "Uncertain release did not poison and retain ownership.");
            RejectInvalid(
                uncertainLease.Dispose,
                "permanently");
            RejectInvalid(
                uncertainLease.Dispose,
                "permanently");
            Assert(
                uncertainSource.ReleaseAttempts == 1 &&
                uncertainJournal.IsPoisoned,
                "Uncertain release was retried, double-released, or underflowed.");
            RejectInvalid(
                uncertainJournal.Dispose,
                "poison");
            RejectInvalid(delegate
            {
                uncertainJournal.CreateMaintenanceReplayStore();
            }, "poison");
            RejectInvalid(delegate
            {
                uncertainStore.AcquireExclusiveLease();
            }, "poison");
            RejectInvalid(delegate
            {
                uncertainJournal.AcquireTransactionLease();
            }, "poison");
            RejectInvalid(delegate
            {
                uncertainJournal.Load();
            }, "poison");
            RejectInvalid(delegate
            {
                uncertainJournal.Save(null);
            }, "poison");
            RejectInvalid(delegate
            {
                uncertainJournal.CreateProtectedEscrowManifestStore(
                    TransactionId);
            }, "poison");
            RejectInvalid(delegate
            {
                uncertainJournal.
                    CreateProtectedPayloadWorkspaceCheckpointStore(
                        TransactionId,
                        Digest('f'));
            }, "poison");
            RejectInvalid(delegate
            {
                uncertainJournal.
                    CreateDurableProtectedPayloadBuildWorkspaceModel(
                        TransactionId,
                        Digest('f'),
                        null);
            }, "poison");
            Assert(
                !uncertainFileSystem.Disposed &&
                uncertainSource.AcquireCalls == 1,
                "Poisoned journal released storage or reacquired exclusion.");

            var rejectedSource =
                new ThrowingReleaseLeaseSource(
                    InstallerTransactionLeaseReleaseOutcome.
                        RejectedBeforeMutation,
                    false);
            var rejectedFileSystem =
                new LifecycleBarrierFileSystem();
            FileTransactionJournalStore rejectedJournal =
                NewThrowingReleaseJournal(
                    root + "-rejected",
                    rejectedFileSystem,
                    rejectedSource);
            IMaintenanceReplayAtomicStore rejectedStore =
                rejectedJournal.CreateMaintenanceReplayStore();
            IMaintenanceReplayStoreLease rejectedLease =
                rejectedStore.AcquireExclusiveLease();
            RejectIo(rejectedLease.Dispose);
            Assert(
                !rejectedJournal.IsPoisoned &&
                rejectedSource.UnderlyingHeld &&
                rejectedSource.ReleaseAttempts == 1,
                "Rejected-before-mutation release changed ownership.");
            RejectInvalid(
                rejectedJournal.Dispose,
                "active");
            rejectedLease.Dispose();
            rejectedLease.Dispose();
            Assert(
                !rejectedSource.UnderlyingHeld &&
                rejectedSource.ReleaseAttempts == 2,
                "Rejected release did not permit one safe owner retry.");
            rejectedJournal.Dispose();
            Assert(
                rejectedFileSystem.Disposed,
                "Retried rejected release did not permit parent disposal.");

            var confirmedSource =
                new ThrowingReleaseLeaseSource(
                    InstallerTransactionLeaseReleaseOutcome.Confirmed,
                    false);
            var confirmedFileSystem =
                new LifecycleBarrierFileSystem();
            FileTransactionJournalStore confirmedJournal =
                NewThrowingReleaseJournal(
                    root + "-confirmed",
                    confirmedFileSystem,
                    confirmedSource);
            IMaintenanceReplayStoreLease confirmedLease =
                confirmedJournal.CreateMaintenanceReplayStore().
                    AcquireExclusiveLease();
            RejectIo(confirmedLease.Dispose);
            confirmedLease.Dispose();
            Assert(
                !confirmedJournal.IsPoisoned &&
                !confirmedSource.UnderlyingHeld &&
                confirmedSource.ReleaseAttempts == 1,
                "Confirmed release did not close exactly one reservation.");
            confirmedJournal.Dispose();
            Assert(
                confirmedFileSystem.Disposed,
                "Confirmed release incorrectly blocked parent disposal.");

            var unknownSource =
                new ThrowingReleaseLeaseSource(
                    InstallerTransactionLeaseReleaseOutcome.Confirmed,
                    true);
            var unknownFileSystem =
                new LifecycleBarrierFileSystem();
            FileTransactionJournalStore unknownJournal =
                NewThrowingReleaseJournal(
                    root + "-unknown",
                    unknownFileSystem,
                    unknownSource);
            IMaintenanceReplayStoreLease unknownLease =
                unknownJournal.CreateMaintenanceReplayStore().
                    AcquireExclusiveLease();
            RejectIo(unknownLease.Dispose);
            RejectInvalid(
                unknownLease.Dispose,
                "permanently");
            Assert(
                unknownJournal.IsPoisoned &&
                unknownSource.ReleaseAttempts == 1 &&
                !unknownFileSystem.Disposed,
                "Unknown release exception was not classified Uncertain.");
        }

        private static void RealCoordinatorReleaseSeamIsTyped()
        {
            var releaseSeam =
                new CoordinatorReleaseFaultSeam
                {
                    FailRelease = true
                };
            var uncertainCoordinator =
                new InstanceTransactionLeaseCoordinator(
                    new UnsecuredInstallerTransactionMutexFactory(),
                    @"Local\SBMS.Maintenance.Coordinator." +
                        Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(2),
                    releaseSeam);
            IDisposable uncertainLease =
                uncertainCoordinator.Acquire();
            InstallerTransactionLeaseReleaseException uncertain =
                CaptureReleaseFailure(uncertainLease.Dispose);
            Assert(
                uncertain.Outcome ==
                    InstallerTransactionLeaseReleaseOutcome.Uncertain &&
                releaseSeam.ReleaseCalls == 1 &&
                releaseSeam.CleanupCalls == 0,
                "Real coordinator did not classify ReleaseMutex failure as Uncertain.");
            RejectInvalid(delegate
            {
                uncertainCoordinator.Acquire();
            }, "poison");

            var cleanupSeam =
                new CoordinatorReleaseFaultSeam
                {
                    FailCleanup = true
                };
            var confirmedCoordinator =
                new InstanceTransactionLeaseCoordinator(
                    new UnsecuredInstallerTransactionMutexFactory(),
                    @"Local\SBMS.Maintenance.Coordinator." +
                        Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(2),
                    cleanupSeam);
            IDisposable confirmedLease =
                confirmedCoordinator.Acquire();
            InstallerTransactionLeaseReleaseException confirmed =
                CaptureReleaseFailure(confirmedLease.Dispose);
            Assert(
                confirmed.Outcome ==
                    InstallerTransactionLeaseReleaseOutcome.Confirmed &&
                cleanupSeam.ReleaseCalls == 1 &&
                cleanupSeam.CleanupCalls == 1,
                "Real coordinator did not classify post-release cleanup as Confirmed.");
            using (IDisposable recovered =
                confirmedCoordinator.Acquire())
            {
            }
            Assert(
                cleanupSeam.ReleaseCalls == 2 &&
                cleanupSeam.CleanupCalls == 2,
                "Confirmed coordinator release did not remain acquirable.");
        }

        private static InstallerTransactionLeaseReleaseException
            CaptureReleaseFailure(Action action)
        {
            try
            {
                action();
            }
            catch (InstallerTransactionLeaseReleaseException failure)
            {
                return failure;
            }
            throw new InvalidOperationException(
                "Expected a typed transaction lease release failure.");
        }

        private static void CoordinatorPostOwnershipFaultRollsBack()
        {
            var recoveredSeam =
                new CoordinatorReleaseFaultSeam
                {
                    FailAfterOwnershipRecorded = true
                };
            var recoveredCoordinator =
                new InstanceTransactionLeaseCoordinator(
                    new UnsecuredInstallerTransactionMutexFactory(),
                    @"Local\SBMS.Maintenance.PostOwnership." +
                        Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(2),
                    recoveredSeam);
            RejectIo(delegate
            {
                recoveredCoordinator.Acquire();
            });
            using (IDisposable recovered =
                recoveredCoordinator.Acquire())
            {
            }
            Assert(
                recoveredSeam.OwnershipRecordedCalls == 2 &&
                recoveredSeam.ReleaseCalls == 2 &&
                recoveredSeam.CleanupCalls == 2,
                "Successful acquisition cleanup left false nested ownership.");

            var confirmedSeam =
                new CoordinatorReleaseFaultSeam
                {
                    FailAfterOwnershipRecorded = true,
                    FailCleanup = true
                };
            var confirmedCoordinator =
                new InstanceTransactionLeaseCoordinator(
                    new UnsecuredInstallerTransactionMutexFactory(),
                    @"Local\SBMS.Maintenance.PostOwnership." +
                        Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(2),
                    confirmedSeam);
            InstallerTransactionLeaseReleaseException confirmed =
                CaptureReleaseFailure(delegate
                {
                    confirmedCoordinator.Acquire();
                });
            Assert(
                confirmed.Outcome ==
                    InstallerTransactionLeaseReleaseOutcome.Confirmed,
                "Post-ownership cleanup error was not classified Confirmed.");
            using (IDisposable recovered =
                confirmedCoordinator.Acquire())
            {
            }
            Assert(
                confirmedSeam.OwnershipRecordedCalls == 2 &&
                confirmedSeam.ReleaseCalls == 2 &&
                confirmedSeam.CleanupCalls == 2,
                "Confirmed acquisition cleanup did not roll back installed ownership.");

            var uncertainSeam =
                new CoordinatorReleaseFaultSeam
                {
                    FailAfterOwnershipRecorded = true,
                    FailRelease = true
                };
            var uncertainCoordinator =
                new InstanceTransactionLeaseCoordinator(
                    new UnsecuredInstallerTransactionMutexFactory(),
                    @"Local\SBMS.Maintenance.PostOwnership." +
                        Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(2),
                    uncertainSeam);
            InstallerTransactionLeaseReleaseException uncertain =
                CaptureReleaseFailure(delegate
                {
                    uncertainCoordinator.Acquire();
                });
            Assert(
                uncertain.Outcome ==
                    InstallerTransactionLeaseReleaseOutcome.Uncertain &&
                uncertainSeam.ReleaseCalls == 1 &&
                uncertainSeam.CleanupCalls == 0,
                "Uncertain acquisition cleanup did not retain poisoned ownership.");
            RejectInvalid(delegate
            {
                uncertainCoordinator.Acquire();
            }, "poison");

            var exhaustedFactory =
                new CountingInstallerTransactionMutexFactory();
            var exhaustedSeam =
                new CoordinatorReleaseFaultSeam
                {
                    ExhaustNextLeaseId = true
                };
            var exhaustedCoordinator =
                new InstanceTransactionLeaseCoordinator(
                    exhaustedFactory,
                    @"Local\SBMS.Maintenance.PostOwnership." +
                        Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(2),
                    exhaustedSeam);
            Exception exhausted = null;
            try
            {
                exhaustedCoordinator.Acquire();
            }
            catch (Exception failure)
            {
                exhausted = failure;
            }
            Assert(
                exhausted is OverflowException &&
                exhaustedFactory.OpenCalls == 1 &&
                exhaustedSeam.LeaseIdAllocationCalls == 1 &&
                exhaustedSeam.ReleaseCalls == 1 &&
                exhaustedSeam.CleanupCalls == 1,
                "Lease id exhaustion did not clean up its installed mutex once.");
            RejectInvalid(delegate
            {
                exhaustedCoordinator.Acquire();
            }, "poison");
            RejectInvalid(delegate
            {
                exhaustedCoordinator.Acquire();
            }, "poison");
            Assert(
                exhaustedFactory.OpenCalls == 1 &&
                exhaustedSeam.LeaseIdAllocationCalls == 1 &&
                exhaustedSeam.ReleaseCalls == 1 &&
                exhaustedSeam.CleanupCalls == 1,
                "Poisoned lease id exhaustion touched the mutex again.");
        }

        private static void TypedAcquisitionCleanupSettlesLifetimes()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "SBMS-maintenance-acquire-cleanup-" +
                    Guid.NewGuid().ToString("N"));
            var parentSource = new ThrowingAcquireLeaseSource(
                InstallerTransactionLeaseReleaseOutcome.Uncertain);
            var parentFileSystem =
                new LifecycleBarrierFileSystem();
            var parentJournal = NewAcquisitionFailureJournal(
                root,
                parentFileSystem,
                parentSource);
            InstallerTransactionLeaseReleaseException parentFailure =
                CaptureReleaseFailure(delegate
                {
                    parentJournal.AcquireTransactionLease();
                });
            Assert(
                parentFailure.Outcome ==
                    InstallerTransactionLeaseReleaseOutcome.Uncertain &&
                parentJournal.IsPoisoned &&
                parentSource.AcquireCalls == 1,
                "Uncertain inner Acquire did not poison and retain the parent reservation.");
            RejectInvalid(parentJournal.Dispose, "poison");

            var confirmedSource = new ThrowingAcquireLeaseSource(
                InstallerTransactionLeaseReleaseOutcome.Confirmed);
            var confirmedFileSystem =
                new LifecycleBarrierFileSystem();
            var confirmedJournal = NewAcquisitionFailureJournal(
                root + "-confirmed",
                confirmedFileSystem,
                confirmedSource);
            IMaintenanceReplayAtomicStore confirmedStore =
                confirmedJournal.CreateMaintenanceReplayStore();
            InstallerTransactionLeaseReleaseException confirmedFailure =
                CaptureReleaseFailure(delegate
                {
                    confirmedStore.AcquireExclusiveLease();
                });
            Assert(
                confirmedFailure.Outcome ==
                    InstallerTransactionLeaseReleaseOutcome.Confirmed &&
                !confirmedJournal.IsPoisoned,
                "Confirmed replay acquisition cleanup retained a reservation.");
            confirmedJournal.Dispose();
            Assert(
                confirmedFileSystem.Disposed,
                "Confirmed replay acquisition cleanup left child lifetime active.");

            var rejectedSource = new ThrowingAcquireLeaseSource(
                InstallerTransactionLeaseReleaseOutcome.
                    RejectedBeforeMutation);
            var rejectedFileSystem =
                new LifecycleBarrierFileSystem();
            var rejectedJournal = NewAcquisitionFailureJournal(
                root + "-rejected",
                rejectedFileSystem,
                rejectedSource);
            IMaintenanceReplayAtomicStore rejectedStore =
                rejectedJournal.CreateMaintenanceReplayStore();
            InstallerTransactionLeaseReleaseException rejectedFailure =
                CaptureReleaseFailure(delegate
                {
                    rejectedStore.AcquireExclusiveLease();
                });
            Assert(
                rejectedFailure.Outcome ==
                    InstallerTransactionLeaseReleaseOutcome.Uncertain &&
                rejectedJournal.IsPoisoned &&
                !rejectedFileSystem.Disposed,
                "Rejected replay acquisition was not upgraded to Uncertain.");
            RejectInvalid(rejectedJournal.Dispose, "poison");
        }

        private static void ReplayPostSharedAcquireFaultsSettle()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "SBMS-maintenance-post-shared-" +
                    Guid.NewGuid().ToString("N"));

            var cleanSource = new CountingLeaseSource();
            var cleanFileSystem = new LifecycleBarrierFileSystem();
            var cleanJournal = NewPostAcquireJournal(
                root,
                cleanFileSystem,
                cleanSource.Acquire,
                cleanSource.IsHeldByCurrentThread);
            var cleanSeam =
                new MaintenanceReplayPostAcquireFaultSeam();
            IMaintenanceReplayAtomicStore cleanStore =
                cleanJournal.CreateMaintenanceReplayStoreForFaultTesting(
                    cleanSeam);
            RejectIo(delegate
            {
                cleanStore.AcquireExclusiveLease();
            });
            Assert(
                cleanSeam.Calls == 1 &&
                cleanSource.AcquireCalls == 1 &&
                cleanSource.ReleaseCalls == 1 &&
                !cleanSource.Held &&
                !cleanJournal.IsPoisoned,
                "Successful post-shared cleanup retained a child or parent reservation.");
            cleanJournal.Dispose();
            Assert(
                cleanFileSystem.Disposed,
                "Successful post-shared cleanup blocked parent disposal.");

            AssertPostSharedTypedCleanup(
                root + "-confirmed",
                InstallerTransactionLeaseReleaseOutcome.Confirmed,
                false,
                false);
            AssertPostSharedTypedCleanup(
                root + "-rejected",
                InstallerTransactionLeaseReleaseOutcome.
                    RejectedBeforeMutation,
                false,
                true);
            AssertPostSharedTypedCleanup(
                root + "-uncertain",
                InstallerTransactionLeaseReleaseOutcome.Uncertain,
                false,
                true);
            AssertPostSharedTypedCleanup(
                root + "-unknown",
                InstallerTransactionLeaseReleaseOutcome.Confirmed,
                true,
                true);
        }

        private static void AssertPostSharedTypedCleanup(
            string root,
            InstallerTransactionLeaseReleaseOutcome outcome,
            bool throwUnknown,
            bool expectPoison)
        {
            var source =
                new ThrowingReleaseLeaseSource(
                    outcome,
                    throwUnknown);
            var fileSystem = new LifecycleBarrierFileSystem();
            var journal = NewPostAcquireJournal(
                root,
                fileSystem,
                source.Acquire,
                source.IsHeldByCurrentThread);
            var seam =
                new MaintenanceReplayPostAcquireFaultSeam();
            IMaintenanceReplayAtomicStore store =
                journal.CreateMaintenanceReplayStoreForFaultTesting(
                    seam);
            Exception observed = null;
            try
            {
                store.AcquireExclusiveLease();
            }
            catch (Exception failure)
            {
                observed = failure;
            }
            if (outcome ==
                    InstallerTransactionLeaseReleaseOutcome.
                        RejectedBeforeMutation &&
                !throwUnknown)
            {
                var typed =
                    observed as
                        InstallerTransactionLeaseReleaseException;
                Assert(
                    typed != null &&
                    typed.Outcome ==
                        InstallerTransactionLeaseReleaseOutcome.Uncertain,
                    "Rejected post-shared cleanup was not upgraded to Uncertain.");
            }
            else
            {
                Assert(
                    observed is IOException,
                    "Confirmed or unknown cleanup did not preserve the authoritative failure.");
            }
            Assert(
                seam.Calls == 1 &&
                source.AcquireCalls == 1 &&
                source.ReleaseAttempts == 1 &&
                journal.IsPoisoned == expectPoison,
                "Post-shared cleanup settlement or release count was incorrect.");
            if (expectPoison)
            {
                Assert(
                    ((MaintenanceReplayProductionStore)store).
                        HasActiveLease &&
                    !fileSystem.Disposed,
                    "Poisoned post-shared cleanup released child reservation or storage.");
                RejectInvalid(delegate
                {
                    store.AcquireExclusiveLease();
                }, "poison");
                RejectInvalid(journal.Dispose, "poison");
                Assert(
                    source.ReleaseAttempts == 1 &&
                    !fileSystem.Disposed,
                    "Poisoned post-shared cleanup retried or double-released.");
            }
            else
            {
                journal.Dispose();
                Assert(
                    fileSystem.Disposed &&
                    !source.UnderlyingHeld,
                    "Confirmed post-shared cleanup retained parent lifetime.");
            }
        }

        private static FileTransactionJournalStore NewPostAcquireJournal(
            string root,
            LifecycleBarrierFileSystem fileSystem,
            Func<IDisposable> acquire,
            Func<bool> demandHeld)
        {
            return new FileTransactionJournalStore(
                new TestProgramDataPathProvider(root),
                new NoOpInstallerJournalAclPolicy(),
                @"Local\SBMS.Maintenance.PostShared." +
                    Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(2),
                null,
                fileSystem,
                new UnsecuredInstallerTransactionMutexFactory(),
                acquire,
                demandHeld);
        }

        private static FileTransactionJournalStore
            NewAcquisitionFailureJournal(
                string root,
                LifecycleBarrierFileSystem fileSystem,
                ThrowingAcquireLeaseSource source)
        {
            return new FileTransactionJournalStore(
                new TestProgramDataPathProvider(root),
                new NoOpInstallerJournalAclPolicy(),
                @"Local\SBMS.Maintenance.AcquireFailure." +
                    Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(2),
                null,
                fileSystem,
                new UnsecuredInstallerTransactionMutexFactory(),
                source.Acquire,
                source.IsHeldByCurrentThread);
        }

        private static void LifetimeLeaseSerializesConcurrentDispose()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "SBMS-maintenance-concurrent-release-" +
                    Guid.NewGuid().ToString("N"));
            var fileSystem = new LifecycleBarrierFileSystem();
            var source = new ConcurrentReleaseLeaseSource();
            var journal = new FileTransactionJournalStore(
                new TestProgramDataPathProvider(root),
                new NoOpInstallerJournalAclPolicy(),
                @"Local\SBMS.Maintenance.Concurrent." +
                    Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(2),
                null,
                fileSystem,
                new UnsecuredInstallerTransactionMutexFactory(),
                source.Acquire,
                source.IsHeldByCurrentThread);
            IDisposable lease = null;
            Exception ownerFailure = null;
            Exception competitorFailure = null;
            var acquired = new ManualResetEvent(false);
            var startDispose = new ManualResetEvent(false);
            var owner = new Thread(new ThreadStart(delegate
            {
                try
                {
                    lease = journal.AcquireTransactionLease();
                    acquired.Set();
                    if (!startDispose.WaitOne(
                            TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException(
                            "Owner dispose start barrier timed out.");
                    }
                    lease.Dispose();
                }
                catch (Exception failure)
                {
                    ownerFailure = failure;
                }
            }));
            owner.Start();
            Assert(
                acquired.WaitOne(TimeSpan.FromSeconds(2)),
                "Owner did not acquire the lifetime-bound lease.");
            startDispose.Set();
            Assert(
                source.ReleaseEntered.WaitOne(
                    TimeSpan.FromSeconds(2)),
                "Owner did not enter the inner release barrier.");
            var competitor = new Thread(new ThreadStart(delegate
            {
                try
                {
                    lease.Dispose();
                }
                catch (Exception failure)
                {
                    competitorFailure = failure;
                }
            }));
            competitor.Start();
            Assert(
                competitor.Join(2000),
                "Concurrent Dispose did not terminate deterministically.");
            source.AllowRelease.Set();
            Assert(
                owner.Join(2000) &&
                ownerFailure == null &&
                competitorFailure is
                    InstallerTransactionLeaseReleaseException &&
                ((InstallerTransactionLeaseReleaseException)
                    competitorFailure).Outcome ==
                    InstallerTransactionLeaseReleaseOutcome.
                        RejectedBeforeMutation &&
                source.ReleaseCalls == 1 &&
                !journal.IsPoisoned,
                "Concurrent Dispose double-released, underflowed, or poisoned.");
            journal.Dispose();
            Assert(
                fileSystem.Disposed,
                "Concurrent Dispose left the parent lifetime active.");
        }

        private static void AssertBidirectionalLeaseExclusion(
            FileTransactionJournalStore journal,
            IMaintenanceReplayAtomicStore replay)
        {
            LeaseProbe replayProbe;
            using (IDisposable journalLease =
                journal.AcquireTransactionLease())
            {
                RejectInvalid(delegate
                {
                    replay.AcquireExclusiveLease();
                }, "nest");
                replayProbe = StartBlockedProbe(
                    delegate
                    {
                        return replay.AcquireExclusiveLease();
                    },
                    "Replay did not share the journal lock domain.");
            }
            AssertProbeCompletes(
                replayProbe,
                "Replay did not enter after journal lease release.");

            LeaseProbe journalProbe;
            using (IMaintenanceReplayStoreLease replayLease =
                replay.AcquireExclusiveLease())
            {
                RejectInvalid(delegate
                {
                    journal.AcquireTransactionLease();
                }, "non-reentrant");
                journalProbe = StartBlockedProbe(
                    delegate
                    {
                        return journal.AcquireTransactionLease();
                    },
                    "Journal did not share the replay lock domain.");
            }
            AssertProbeCompletes(
                journalProbe,
                "Journal did not enter after replay lease release.");
        }

        private static LeaseProbe StartBlockedProbe(
            Func<IDisposable> acquire,
            string message)
        {
            var probe = new LeaseProbe(acquire);
            probe.Thread.Start();
            probe.Started.WaitOne();
            Thread.Sleep(100);
            Assert(!probe.Acquired, message);
            return probe;
        }

        private static void AssertProbeCompletes(
            LeaseProbe probe,
            string message)
        {
            Assert(
                probe.Thread.Join(TimeSpan.FromSeconds(2)),
                message);
            if (probe.Failure != null)
            {
                throw probe.Failure;
            }
            Assert(probe.Acquired, message);
        }

        private static void FakeLeaseIsStrict()
        {
            var store = new FakeReplayStore();
            using (IMaintenanceReplayStoreLease lease =
                store.AcquireExclusiveLease())
            {
                RejectInvalid(delegate
                {
                    store.AcquireExclusiveLease();
                }, "non-reentrant");
                Exception threadFailure = null;
                var thread = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        byte[] ignored;
                        lease.TryRead("x", out ignored);
                    }
                    catch (Exception exception)
                    {
                        threadFailure = exception;
                    }
                }));
                thread.Start();
                thread.Join();
                Assert(
                    threadFailure is InvalidOperationException,
                    "Lease was not thread-affine.");
            }
        }

        private static MaintenanceReplayRecord Prepared(
            PayloadBrokerCommand command)
        {
            return new MaintenanceReplayRecord
            {
                SchemaVersion = 1,
                State = MaintenanceReplayRecordState.Prepared,
                TransactionId = command.TransactionId,
                RequestId = command.RequestId,
                CommandInvariantDigest = command.InvariantDigest,
                StorageKeyInvariantDigest =
                    MaintenanceReplayRecord.
                        ComputeStorageKeyInvariantDigest(
                            command.TransactionId,
                            command.RequestId),
                Response = null
            };
        }

        private static MaintenanceReplayRecord Committed(
            PayloadBrokerCommand command)
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
                Response = Response(command)
            };
        }

        private static void WriteRecord(
            FakeReplayStore store,
            PayloadBrokerCommand command,
            MaintenanceReplayRecord record)
        {
            string key =
                command.TransactionId + ":" + command.RequestId;
            using (IMaintenanceReplayStoreLease lease =
                store.AcquireExclusiveLease())
            {
                lease.AtomicWrite(
                    key,
                    MaintenanceReplayRecordCodec.
                        SerializeCanonical(record));
            }
        }

        private static MaintenanceWriteBeforeAckExecutor Executor(
            IMaintenanceReplayAtomicStore store)
        {
            return new MaintenanceWriteBeforeAckExecutor(
                store,
                Digest('f'));
        }

        private static MaintenanceWriteBeforeAckExecutor Executor(
            IMaintenanceReplayAtomicStore store,
            string expectedRootAuthorityInvariantDigest)
        {
            return new MaintenanceWriteBeforeAckExecutor(
                store,
                expectedRootAuthorityInvariantDigest);
        }

        private static PayloadBrokerResponse Mutate(
            PayloadBrokerCommand command)
        {
            mutationCount++;
            return Response(command);
        }

        private static PayloadBrokerResponse InspectOrResumeAuthoritativeState(
            PayloadBrokerCommand command)
        {
            reconcileCount++;
            return Response(command);
        }

        private static PayloadBrokerCommand Command()
        {
            var checkpoint =
                new PayloadNamespaceOwnershipCheckpoint
                {
                    SchemaVersion = 2,
                    OwnershipRevision = 0,
                    NamespaceId =
                        PayloadManagedNamespaceLocation.
                            ProductionNamespaceId,
                    Phase =
                        PayloadNamespaceOwnershipPhase.Absent,
                    SecurityProfile =
                        PayloadNamespaceSecurityProfile.Production(),
                    ActiveTransactionId = String.Empty,
                    ActiveIntentId = String.Empty,
                    ExpectedWorkspaceCasInvariantDigest =
                        String.Empty,
                    OwnershipMarkerDigest = String.Empty,
                    RootVolumeSerialNumber = 0,
                    RootFileId = String.Empty,
                    LastObservationInvariantDigest = String.Empty
                };
            return new PayloadBrokerCommand
            {
                SchemaVersion = 2,
                ProtocolVersion =
                    PayloadBrokerProtocol.ProtocolVersion,
                Operation = PayloadBrokerOperation.Inspect,
                TransactionId = TransactionId,
                RequestId = RequestId,
                CorrelationNonceDigest = Digest('1'),
                BeforeOwnershipCas = checkpoint.CasToken,
                BeforeWorkspaceCas =
                    new PayloadWorkspaceCasToken
                    {
                        SchemaVersion = 1,
                        TransactionId = TransactionId,
                        Revision = 0,
                        WorkspaceInvariantDigest = Digest('2')
                    },
                PlanInvariantDigest = Digest('3')
            };
        }

        private static PayloadBrokerResponse Response(
            PayloadBrokerCommand command)
        {
            var receipt =
                new PayloadBrokerOperationReceipt
                {
                    SchemaVersion = 2,
                    Operation = PayloadBrokerOperation.Inspect,
                    OwnershipTransitionTag =
                        PayloadBrokerOwnershipTransitionTag.None,
                    BeforeOwnershipCas =
                        command.BeforeOwnershipCas.DeepClone(),
                    AfterOwnershipCas =
                        command.BeforeOwnershipCas.DeepClone(),
                    BeforeWorkspaceCas =
                        command.BeforeWorkspaceCas.DeepClone(),
                    AfterWorkspaceCas =
                        command.BeforeWorkspaceCas.DeepClone(),
                    Observation = null,
                    AppliedPlanInvariantDigest =
                        command.PlanInvariantDigest
                };
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

        private static string Digest(char value)
        {
            return new string(value, 64);
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

        private static void RejectInvalid(
            Action action,
            string messagePart)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException exception)
            {
                if (messagePart == null ||
                    exception.Message.IndexOf(
                        messagePart,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }
                throw;
            }
            throw new InvalidOperationException(
                "Expected InvalidOperationException.");
        }

        private static void RejectReplayFormat(
            Action action,
            string messagePart)
        {
            try
            {
                action();
            }
            catch (MaintenanceReplayContentFormatException exception)
            {
                if (messagePart == null ||
                    exception.Message.IndexOf(
                        messagePart,
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (exception.InnerException != null &&
                     exception.InnerException.Message.IndexOf(
                        messagePart,
                        StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return;
                }
                throw;
            }
            throw new InvalidOperationException(
                "Expected MaintenanceReplayContentFormatException.");
        }

        private static void RejectCanceled(Action action)
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
                "Expected cancellation.");
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
                "Expected authorization rejection.");
        }

        private static void RejectArgumentOutOfRange(Action action)
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
                "Expected ArgumentOutOfRangeException.");
        }

        private static void RejectArgument(Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentException)
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected ArgumentException.");
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
                "Expected timeout.");
        }

        private static void RejectIo(Action action)
        {
            try
            {
                action();
            }
            catch (IOException)
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected IO failure.");
        }

        private static void RejectDisposed(Action action)
        {
            try
            {
                action();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected ObjectDisposedException.");
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
