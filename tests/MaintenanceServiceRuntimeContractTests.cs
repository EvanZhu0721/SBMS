using System;
using System.Collections.Generic;
using System.IO;
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
        private static readonly string TransactionId =
            "11111111111111111111111111111111";
        private static readonly string RequestId =
            "22222222222222222222222222222222";

        private static int Main()
        {
            Run("identity reuses fixed contracts", IdentityReusesContracts);
            Run("security descriptor is exact", SecurityDescriptorIsExact);
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
                unchecked((int)0xC0000000),
                AceFlags.None,
                "Pipe Administrators");
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
