using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace SBMSSetup
{
    internal static class FileTransactionJournalStoreTests
    {
        private sealed class FixedProgramDataPathProvider
            : IInstallerProgramDataPathProvider
        {
            private readonly string path;

            internal FixedProgramDataPathProvider(string path)
            {
                this.path = path;
            }

            public string GetCommonApplicationDataPath()
            {
                return path;
            }
        }

        private sealed class RecordingAclPolicy : IInstallerJournalAclPolicy
        {
            internal int CallCount;
            internal bool LastCreateIfMissing;
            internal string LastRoot;
            internal Exception Failure;
            internal int FailOnCall;

            public void PrepareAndVerify(
                string commonApplicationDataRoot,
                string installerStateRoot,
                bool createIfMissing)
            {
                ++CallCount;
                LastRoot = installerStateRoot;
                LastCreateIfMissing = createIfMissing;
                if (Failure != null &&
                    (FailOnCall == 0 || FailOnCall == CallCount))
                {
                    throw Failure;
                }
            }
        }

        private sealed class FakePathInspector
            : IInstallerJournalPathInspector
        {
            internal readonly Dictionary<string, InstallerJournalPathMetadata>
                Entries =
                    new Dictionary<string, InstallerJournalPathMetadata>(
                        StringComparer.OrdinalIgnoreCase);

            public InstallerJournalPathMetadata Inspect(
                string path,
                bool includeSecurity)
            {
                InstallerJournalPathMetadata metadata;
                if (Entries.TryGetValue(Path.GetFullPath(path), out metadata))
                {
                    return metadata;
                }
                return new InstallerJournalPathMetadata();
            }
        }

        private static int passed;
        private static int failed;

        public static int Main()
        {
            Run("production path is fixed below ProgramData", TestFixedPath);
            Run("read-only load does not request root creation", TestLoadSeam);
            Run("save requests a secured root before journal IO", TestSaveSeam);
            Run("ACL failure blocks access before filesystem IO", TestAclFailClosed);
            Run("named mutex serializes journal access", TestNamedMutex);
            Run("mutex timeout fails before ACL or filesystem IO", TestMutexTimeout);
            Run("transaction lease spans multiple journal operations", TestTransactionLease);
            Run("ProgramData SBMS parent reparse fails closed", TestParentReparse);
            Run("final state root reparse fails closed", TestFinalReparse);
            Run("ACL inheritance flags fail closed", TestAclInheritanceFlags);
            Run("verify-after-swap rejection escapes", TestVerifyAfterSwap);
            Run("production policy remains feature gated", TestProductionFeatureGate);
            Console.WriteLine(
                "RESULT passed=" + passed + " failed=" + failed);
            return failed == 0 ? 0 : 1;
        }

        private static void TestFixedPath()
        {
            string root = NewRoot();
            try
            {
                var acl = new RecordingAclPolicy();
                FileTransactionJournalStore store = Store(
                    root,
                    acl,
                    TimeSpan.FromSeconds(2));
                Equal(
                    Path.Combine(root, "SBMS", "Installer"),
                    store.InstallerStateRoot,
                    "Unexpected installer state root.");
                Equal(
                    Path.Combine(root, "SBMS", "Installer", "journal.json"),
                    store.JournalPath,
                    "Unexpected journal path.");
                Equal(
                    Path.Combine(root, "SBMS", "Installer", "transactions"),
                    store.TransactionsDirectory,
                    "Unexpected transaction directory.");
                Assert(
                    !Directory.Exists(
                        Path.Combine(root, "SBMS", "Installer")),
                    "Constructing the store mutated ProgramData.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestLoadSeam()
        {
            string root = NewRoot();
            try
            {
                var acl = new RecordingAclPolicy();
                FileTransactionJournalStore store = Store(
                    root,
                    acl,
                    TimeSpan.FromSeconds(2));
                TransactionJournal journal = store.Load();
                Assert(journal == null, "Empty store returned a journal.");
                Equal(2, acl.CallCount, "ACL seam did not bracket the load.");
                Assert(
                    !acl.LastCreateIfMissing,
                    "Read-only load requested directory creation.");
                Equal(
                    store.InstallerStateRoot,
                    acl.LastRoot,
                    "ACL seam received the wrong root.");
                Assert(
                    !Directory.Exists(store.InstallerStateRoot),
                    "Empty load created installer state.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestAclFailClosed()
        {
            string root = NewRoot();
            try
            {
                var acl = new RecordingAclPolicy
                {
                    Failure = new UnauthorizedAccessException("fake insecure ACL")
                };
                FileTransactionJournalStore store = Store(
                    root,
                    acl,
                    TimeSpan.FromSeconds(2));
                AssertThrows<UnauthorizedAccessException>(
                    delegate { store.Load(); },
                    "ACL rejection did not escape.");
                Assert(
                    !Directory.Exists(store.InstallerStateRoot),
                    "ACL rejection allowed filesystem mutation.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestSaveSeam()
        {
            string root = NewRoot();
            try
            {
                var acl = new RecordingAclPolicy();
                FileTransactionJournalStore store = Store(
                    root,
                    acl,
                    TimeSpan.FromSeconds(2));
                AssertThrows<InvalidDataException>(
                    delegate { store.Save(null); },
                    "Atomic journal validation unexpectedly accepted null.");
                Equal(1, acl.CallCount, "ACL seam was not called once.");
                Assert(
                    acl.LastCreateIfMissing,
                    "Save did not request a secured installer state root.");
                Assert(
                    !Directory.Exists(store.InstallerStateRoot),
                    "Fake ACL seam unexpectedly mutated installer state.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestNamedMutex()
        {
            string root = NewRoot();
            string mutexName = TestMutexName();
            try
            {
                var acl = new RecordingAclPolicy();
                var store = new FileTransactionJournalStore(
                    new FixedProgramDataPathProvider(root),
                    acl,
                    mutexName,
                    TimeSpan.FromSeconds(2),
                    null);
                Exception workerFailure = null;
                using (var held = new Mutex(false, mutexName))
                using (var started = new ManualResetEvent(false))
                using (var finished = new ManualResetEvent(false))
                {
                    held.WaitOne();
                    var worker = new Thread(
                        new ThreadStart(
                        delegate
                        {
                            try
                            {
                                started.Set();
                                store.Load();
                            }
                            catch (Exception ex)
                            {
                                workerFailure = ex;
                            }
                            finally
                            {
                                finished.Set();
                            }
                        }));
                    worker.IsBackground = true;
                    worker.Start();
                    Assert(started.WaitOne(1000), "Worker did not start.");
                    Assert(
                        !finished.WaitOne(150),
                        "Journal access bypassed the named mutex.");
                    Equal(
                        0,
                        acl.CallCount,
                        "ACL check ran before the journal lock was acquired.");
                    held.ReleaseMutex();
                    Assert(
                        finished.WaitOne(2000),
                        "Journal access did not resume after mutex release.");
                    worker.Join();
                }
                if (workerFailure != null)
                {
                    throw new InvalidOperationException(
                        "Serialized journal access failed.",
                        workerFailure);
                }
                Equal(
                    2,
                    acl.CallCount,
                    "Serialized access did not bracket filesystem IO.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestMutexTimeout()
        {
            string root = NewRoot();
            string mutexName = TestMutexName();
            try
            {
                var acl = new RecordingAclPolicy();
                var store = new FileTransactionJournalStore(
                    new FixedProgramDataPathProvider(root),
                    acl,
                    mutexName,
                    TimeSpan.FromMilliseconds(100),
                    null);
                Exception workerFailure = null;
                using (var held = new Mutex(false, mutexName))
                {
                    held.WaitOne();
                    var worker = new Thread(
                        new ThreadStart(
                        delegate
                        {
                            try
                            {
                                store.Load();
                            }
                            catch (Exception ex)
                            {
                                workerFailure = ex;
                            }
                        }));
                    worker.IsBackground = true;
                    worker.Start();
                    worker.Join(2000);
                    held.ReleaseMutex();
                    Assert(!worker.IsAlive, "Timed-out worker remained blocked.");
                }
                Assert(
                    workerFailure is TimeoutException,
                    "Mutex timeout did not fail with TimeoutException.");
                Equal(
                    0,
                    acl.CallCount,
                    "Timed-out access reached the ACL/filesystem boundary.");
                Assert(
                    !Directory.Exists(store.InstallerStateRoot),
                    "Mutex timeout allowed filesystem mutation.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestTransactionLease()
        {
            string root = NewRoot();
            try
            {
                var acl = new RecordingAclPolicy();
                FileTransactionJournalStore store = Store(
                    root,
                    acl,
                    TimeSpan.FromSeconds(2));
                Exception workerFailure = null;
                using (IDisposable lease =
                    store.AcquireTransactionLease())
                using (var finished = new ManualResetEvent(false))
                {
                    Assert(store.Load() == null,
                        "Lease owner could not perform nested journal IO.");
                    var worker = new Thread(delegate()
                    {
                        try
                        {
                            store.Load();
                        }
                        catch (Exception failure)
                        {
                            workerFailure = failure;
                        }
                        finally
                        {
                            finished.Set();
                        }
                    });
                    worker.IsBackground = true;
                    worker.Start();
                    Assert(!finished.WaitOne(150),
                        "A second transaction bypassed the execution lease.");
                    lease.Dispose();
                    Assert(finished.WaitOne(2000),
                        "Journal access did not resume after lease release.");
                    worker.Join();
                }
                if (workerFailure != null)
                {
                    throw new InvalidOperationException(
                        "Transaction lease handoff failed.",
                        workerFailure);
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestParentReparse()
        {
            string root = NewRoot();
            try
            {
                var inspector = SecureTree(root);
                inspector.Entries[Path.Combine(root, "SBMS")].IsReparsePoint =
                    true;
                var policy = new WindowsInstallerJournalAclPolicy(inspector);
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        policy.PrepareAndVerify(
                            root,
                            Path.Combine(root, "SBMS", "Installer"),
                            false);
                    },
                    "A parent junction was accepted.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestFinalReparse()
        {
            string root = NewRoot();
            try
            {
                var inspector = SecureTree(root);
                inspector.Entries[
                    Path.Combine(root, "SBMS", "Installer")].IsReparsePoint =
                        true;
                var policy = new WindowsInstallerJournalAclPolicy(inspector);
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        policy.PrepareAndVerify(
                            root,
                            Path.Combine(root, "SBMS", "Installer"),
                            false);
                    },
                    "A final reparse point was accepted.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestAclInheritanceFlags()
        {
            string root = NewRoot();
            try
            {
                var inspector = SecureTree(root);
                InstallerJournalPathMetadata state = inspector.Entries[
                    Path.Combine(root, "SBMS", "Installer")];
                state.AccessRules[0].InheritanceFlags =
                    InheritanceFlags.ContainerInherit;
                var policy = new WindowsInstallerJournalAclPolicy(inspector);
                AssertThrows<UnauthorizedAccessException>(
                    delegate
                    {
                        policy.PrepareAndVerify(
                            root,
                            Path.Combine(root, "SBMS", "Installer"),
                            false);
                    },
                    "An incomplete ACL inheritance scope was accepted.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestVerifyAfterSwap()
        {
            string root = NewRoot();
            try
            {
                var acl = new RecordingAclPolicy
                {
                    Failure =
                        new InvalidDataException("fake post-swap reparse"),
                    FailOnCall = 2
                };
                FileTransactionJournalStore store = Store(
                    root,
                    acl,
                    TimeSpan.FromSeconds(2));
                AssertThrows<InvalidDataException>(
                    delegate { store.Load(); },
                    "Post-swap path rejection did not escape.");
                Equal(2, acl.CallCount, "Post-swap verification did not run.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestProductionFeatureGate()
        {
            string root = NewRoot();
            try
            {
                var inspector = SecureTree(root);
                var policy = new WindowsInstallerJournalAclPolicy(inspector);
                AssertThrows<PlatformNotSupportedException>(
                    delegate
                    {
                        policy.PrepareAndVerify(
                            root,
                            Path.Combine(root, "SBMS", "Installer"),
                            false);
                    },
                    "Path-based production journal IO was enabled.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static FakePathInspector SecureTree(string root)
        {
            var inspector = new FakePathInspector();
            inspector.Entries[Path.GetFullPath(root)] = DirectoryMetadata(false);
            inspector.Entries[Path.Combine(root, "SBMS")] =
                DirectoryMetadata(false);
            inspector.Entries[Path.Combine(root, "SBMS", "Installer")] =
                SecureInstallerMetadata();
            return inspector;
        }

        private static InstallerJournalPathMetadata DirectoryMetadata(
            bool reparse)
        {
            return new InstallerJournalPathMetadata
            {
                Exists = true,
                IsDirectory = true,
                IsReparsePoint = reparse
            };
        }

        private static InstallerJournalPathMetadata SecureInstallerMetadata()
        {
            var metadata = DirectoryMetadata(false);
            metadata.AccessRulesProtected = true;
            metadata.Owner = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            metadata.AccessRules.Add(FullControlRule(
                WellKnownSidType.BuiltinAdministratorsSid));
            metadata.AccessRules.Add(FullControlRule(
                WellKnownSidType.LocalSystemSid));
            return metadata;
        }

        private static InstallerJournalAccessRule FullControlRule(
            WellKnownSidType sidType)
        {
            return new InstallerJournalAccessRule
            {
                Identity = new SecurityIdentifier(sidType, null),
                Rights = FileSystemRights.FullControl,
                InheritanceFlags =
                    InheritanceFlags.ContainerInherit |
                    InheritanceFlags.ObjectInherit,
                PropagationFlags = PropagationFlags.None,
                AccessControlType = AccessControlType.Allow,
                IsInherited = false
            };
        }

        private static FileTransactionJournalStore Store(
            string root,
            IInstallerJournalAclPolicy acl,
            TimeSpan timeout)
        {
            return new FileTransactionJournalStore(
                new FixedProgramDataPathProvider(root),
                acl,
                TestMutexName(),
                timeout,
                null);
        }

        private static string TestMutexName()
        {
            return @"Local\SBMS.FileJournal.Tests." +
                Guid.NewGuid().ToString("N");
        }

        private static string NewRoot()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "SBMS-file-journal-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void DeleteRoot(string root)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
                ++passed;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                ++failed;
                Console.WriteLine("FAIL " + name + ": " + ex);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void Equal<T>(
            T expected,
            T actual,
            string message)
        {
            if (!Object.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    message + " expected=" + expected + " actual=" + actual);
            }
        }

        private static void AssertThrows<TException>(
            Action action,
            string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            throw new InvalidOperationException(message);
        }
    }
}
