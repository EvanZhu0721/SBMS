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

        private sealed class DirectorySwapSeam : IWindowsJournalIoTestSeam
        {
            internal string HeldPath;
            internal string AttackerPath;
            internal bool SwapBlocked;

            public void AfterTrustedInstallerRootOpened(string expectedPath)
            {
                HeldPath = expectedPath + "-held";
                AttackerPath = expectedPath;
                try
                {
                    Directory.Move(expectedPath, HeldPath);
                    Directory.CreateDirectory(AttackerPath);
                }
                catch (UnauthorizedAccessException)
                {
                    SwapBlocked = true;
                }
                catch (IOException failure)
                {
                    int error = failure.HResult & 0xFFFF;
                    if (error != 5 && error != 32 && error != 33)
                    {
                        throw;
                    }
                    SwapBlocked = true;
                }
            }

            public void AfterBackupPublished()
            {
            }

            public void BeforePublishedFileIdVerification(
                string destinationRelativePath)
            {
            }

            public int BeforeNativeIo(string operation, int attempt)
            {
                return 0;
            }
        }

        private sealed class BackupCrashSeam : IWindowsJournalIoTestSeam
        {
            public void AfterTrustedInstallerRootOpened(string expectedPath)
            {
            }

            public void AfterBackupPublished()
            {
                throw new IOException("simulated backup publish crash");
            }

            public void BeforePublishedFileIdVerification(
                string destinationRelativePath)
            {
            }

            public int BeforeNativeIo(string operation, int attempt)
            {
                return 0;
            }
        }

        private sealed class PublicationVerificationFaultSeam
            : IWindowsJournalIoTestSeam
        {
            private readonly string destination;

            internal PublicationVerificationFaultSeam(string destination)
            {
                this.destination = destination;
            }

            public void AfterTrustedInstallerRootOpened(string expectedPath)
            {
            }

            public void AfterBackupPublished()
            {
            }

            public void BeforePublishedFileIdVerification(
                string destinationRelativePath)
            {
                if (String.Equals(
                    destination,
                    destinationRelativePath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "simulated persistent post-rename verification failure");
                }
            }

            public int BeforeNativeIo(string operation, int attempt)
            {
                return 0;
            }
        }

        private sealed class PersistentNativeIoFaultSeam
            : IWindowsJournalIoTestSeam
        {
            internal int RenameAttempts;

            public void AfterTrustedInstallerRootOpened(string expectedPath)
            {
            }

            public void AfterBackupPublished()
            {
            }

            public void BeforePublishedFileIdVerification(
                string destinationRelativePath)
            {
            }

            public int BeforeNativeIo(string operation, int attempt)
            {
                if (operation == "rename")
                {
                    RenameAttempts = attempt;
                    return 32;
                }
                return 0;
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
            Run("unattached production policy fails closed", TestProductionFeatureGate);
            Run("attached native policy clears feature gate", TestAttachedNativePolicy);
            Run("native rooted IO lifecycle stays below temp root", TestNativeIoLifecycle);
            Run("native rooted IO rejects hardlinked leaf", TestNativeHardlink);
            Run("single-link guard deterministically rejects multi-link leaf", TestDeterministicLinkCountGuard);
            Run("persistent native sharing violation is bounded", TestPersistentNativeRetry);
            Run("native rooted IO rejects parent directory reparse", TestNativeParentReparse);
            Run("native rooted IO rejects final directory reparse", TestNativeFinalReparse);
            Run("native rooted IO survives directory path swap", TestNativeDirectorySwap);
            Run("native backup publish crash remains recoverable", TestNativeBackupCrash);
            Run("native candidate rename reports committed publication", TestNativeCandidateRenameOutcome);
            Run("native backup rename does not report candidate publication", TestNativeBackupRenameOutcome);
            Run("native rooted IO rejects hostile SBMS parent ACL", TestNativeHostileParentAcl);
            Run("hidden pending WAL blocks a new transaction", TestHiddenPendingWal);
            Run("native rooted IO rejects unexpected ACL entry", TestNativeExtraAcl);
            Run("native rooted IO rejects inherited ACL", TestNativeInheritedAcl);
            Run("secure mutex accepts only its protected descriptor", TestSecureMutex);
            Run("secure mutex rejects low-trust precreation", TestSecureMutexPrecreation);
            Run("secure mutex preserves abandoned semantics", TestSecureMutexAbandoned);
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

        private static void TestNativeIoLifecycle()
        {
            string root = NewRoot();
            var fileSystem = new WindowsHandleRelativeJournalFileSystem(
                root,
                CurrentSecurityProfile(),
                null);
            try
            {
                fileSystem.PrepareAndVerify(true);
                WriteBytes(
                    fileSystem.CreateNewFile("journal.json.new"),
                    new byte[] { 1, 2, 3 });
                fileSystem.PublishNewFile(
                    "journal.json.new",
                    "journal.json");
                Equal(
                    3,
                    ReadBytes(fileSystem.OpenReadFile("journal.json")).Length,
                    "Native journal readback length changed.");

                WriteBytes(
                    fileSystem.CreateNewFile("journal.json.next"),
                    new byte[] { 4, 5, 6, 7 });
                fileSystem.ReplaceFile(
                    "journal.json.next",
                    "journal.json",
                    "journal.json.bak");
                Equal(
                    4,
                    ReadBytes(fileSystem.OpenReadFile("journal.json")).Length,
                    "Native replacement did not publish the candidate.");
                Equal(
                    3,
                    ReadBytes(fileSystem.OpenReadFile("journal.json.bak")).Length,
                    "Native replacement did not retain the backup.");

                fileSystem.EnsureDirectory("history");
                WriteBytes(
                    fileSystem.CreateNewFile("history\\terminal.new"),
                    new byte[] { 9 });
                fileSystem.PublishNewFile(
                    "history\\terminal.new",
                    "history\\terminal.json");
                Assert(
                    fileSystem.FileExists("history\\terminal.json"),
                    "Native history publish was not visible.");
                fileSystem.DeleteFile("history\\terminal.json");
                Assert(
                    !fileSystem.FileExists("history\\terminal.json"),
                    "Native history delete did not complete.");
            }
            finally
            {
                fileSystem.Dispose();
                DeleteRoot(root);
            }
        }

        private static void TestAttachedNativePolicy()
        {
            string root = NewRoot();
            var fileSystem = new WindowsHandleRelativeJournalFileSystem(
                root,
                CurrentSecurityProfile(),
                null);
            try
            {
                var policy = new WindowsInstallerJournalAclPolicy();
                policy.Attach(fileSystem);
                policy.PrepareAndVerify(
                    root,
                    Path.Combine(root, "SBMS", "Installer"),
                    true);
                Assert(
                    Directory.Exists(
                        Path.Combine(root, "SBMS", "Installer")),
                    "Attached native policy did not prepare the secure root.");
            }
            finally
            {
                fileSystem.Dispose();
                DeleteRoot(root);
            }
        }

        private static void TestNativeHardlink()
        {
            string root = NewRoot();
            var fileSystem = new WindowsHandleRelativeJournalFileSystem(
                root,
                CurrentSecurityProfile(),
                null);
            try
            {
                fileSystem.PrepareAndVerify(true);
                string installer = Path.Combine(root, "SBMS", "Installer");
                string source = Path.Combine(installer, "source.bin");
                string link = Path.Combine(installer, "journal.json");
                File.WriteAllBytes(source, new byte[] { 1 });
                if (!CreateHardLink(link, source, IntPtr.Zero))
                {
                    int error = System.Runtime.InteropServices.Marshal.
                        GetLastWin32Error();
                    if (error == 1 || error == 5 || error == 50 ||
                        error == 1314)
                    {
                        Console.WriteLine(
                            "CAPABILITY hardlink fixture unavailable error=" +
                            error);
                        return;
                    }
                    throw new InvalidOperationException(
                        "Unable to create isolated hardlink fixture. error=" +
                        error);
                }
                AssertThrows<InvalidDataException>(
                    delegate { fileSystem.FileExists("journal.json"); },
                    "Native journal IO accepted a multi-link leaf.");
            }
            finally
            {
                fileSystem.Dispose();
                DeleteRoot(root);
            }
        }

        private static void TestDeterministicLinkCountGuard()
        {
            AssertThrows<InvalidDataException>(
                delegate
                {
                    WindowsHandleRelativeJournalFileSystem.
                        VerifySingleLinkLeaf(false, 2);
                },
                "Deterministic multi-link metadata bypassed the leaf guard.");
            WindowsHandleRelativeJournalFileSystem.VerifySingleLinkLeaf(
                false,
                1);
        }

        private static void TestPersistentNativeRetry()
        {
            string root = NewRoot();
            var seam = new PersistentNativeIoFaultSeam();
            var fileSystem = new WindowsHandleRelativeJournalFileSystem(
                root,
                CurrentSecurityProfile(),
                seam);
            try
            {
                fileSystem.PrepareAndVerify(true);
                WriteBytes(
                    fileSystem.CreateNewFile("journal.json.new"),
                    new byte[] { 1 });
                DateTime started = DateTime.UtcNow;
                AssertThrows<IOException>(
                    delegate
                    {
                        fileSystem.PublishNewFile(
                            "journal.json.new",
                            "journal.json");
                    },
                    "Persistent sharing violation did not fail.");
                TimeSpan elapsed = DateTime.UtcNow - started;
                Assert(
                    seam.RenameAttempts > 1,
                    "Transient sharing violation was not retried.");
                Assert(
                    elapsed < TimeSpan.FromSeconds(5),
                    "Persistent sharing violation exceeded the retry bound.");
                Assert(
                    fileSystem.FileExists("journal.json.new"),
                    "Failed rename lost the candidate.");
            }
            finally
            {
                fileSystem.Dispose();
                DeleteRoot(root);
            }
        }

        private static void TestNativeDirectorySwap()
        {
            string root = NewRoot();
            var seam = new DirectorySwapSeam();
            var fileSystem = new WindowsHandleRelativeJournalFileSystem(
                root,
                CurrentSecurityProfile(),
                seam);
            try
            {
                fileSystem.PrepareAndVerify(true);
                WriteBytes(
                    fileSystem.CreateNewFile("journal.json"),
                    new byte[] { 1 });
                if (seam.SwapBlocked)
                {
                    Assert(
                        File.Exists(
                            Path.Combine(seam.AttackerPath, "journal.json")),
                        "Blocked directory swap lost rooted IO.");
                }
                else
                {
                    Assert(
                        File.Exists(Path.Combine(seam.HeldPath, "journal.json")),
                        "Rooted IO did not remain on the opened directory object.");
                    Assert(
                        !File.Exists(
                            Path.Combine(seam.AttackerPath, "journal.json")),
                        "Rooted IO followed the swapped path.");
                }
            }
            finally
            {
                fileSystem.Dispose();
                DeleteRoot(root);
            }
        }

        private static void TestNativeBackupCrash()
        {
            string root = NewRoot();
            var fileSystem = new WindowsHandleRelativeJournalFileSystem(
                root,
                CurrentSecurityProfile(),
                new BackupCrashSeam());
            try
            {
                fileSystem.PrepareAndVerify(true);
                WriteBytes(
                    fileSystem.CreateNewFile("journal.json"),
                    new byte[] { 1, 2, 3 });
                WriteBytes(
                    fileSystem.CreateNewFile("journal.json.next"),
                    new byte[] { 4, 5, 6, 7 });
                AssertThrows<IOException>(
                    delegate
                    {
                        fileSystem.ReplaceFile(
                            "journal.json.next",
                            "journal.json",
                            "journal.json.bak");
                    },
                    "Backup publish crash did not escape.");
                Assert(
                    !fileSystem.FileExists("journal.json"),
                    "Crash boundary unexpectedly retained a primary.");
                Equal(
                    3,
                    ReadBytes(
                        fileSystem.OpenReadFile("journal.json.bak")).Length,
                    "Crash boundary lost the verified old primary.");
                Assert(
                    fileSystem.FileExists("journal.json.next"),
                    "Crash boundary lost the unpublished candidate.");
                fileSystem.PublishNewFile(
                    "journal.json.next",
                    "journal.json");
                Equal(
                    4,
                    ReadBytes(fileSystem.OpenReadFile("journal.json")).Length,
                    "Recovery did not publish the retained candidate.");
            }
            finally
            {
                fileSystem.Dispose();
                DeleteRoot(root);
            }
        }

        private static void TestNativeCandidateRenameOutcome()
        {
            string root = NewRoot();
            var fileSystem = new WindowsHandleRelativeJournalFileSystem(
                root,
                CurrentSecurityProfile(),
                new PublicationVerificationFaultSeam("journal.json"));
            try
            {
                fileSystem.PrepareAndVerify(true);
                WriteBytes(
                    fileSystem.CreateNewFile("journal.json.new"),
                    new byte[] { 7, 8, 9 });
                try
                {
                    fileSystem.PublishNewFile(
                        "journal.json.new",
                        "journal.json");
                    throw new InvalidOperationException(
                        "Post-candidate verification failure did not escape.");
                }
                catch (JournalFilePublicationException failure)
                {
                    Assert(
                        failure.CandidatePublished,
                        "Committed candidate rename was reported as unpublished.");
                }
                Equal(
                    3,
                    File.ReadAllBytes(
                        Path.Combine(
                            root,
                            "SBMS",
                            "Installer",
                            "journal.json")).Length,
                    "Committed candidate was not present after verification failure.");
            }
            finally
            {
                fileSystem.Dispose();
                DeleteRoot(root);
            }
        }

        private static void TestNativeBackupRenameOutcome()
        {
            string root = NewRoot();
            var fileSystem = new WindowsHandleRelativeJournalFileSystem(
                root,
                CurrentSecurityProfile(),
                new PublicationVerificationFaultSeam("journal.json.bak"));
            try
            {
                fileSystem.PrepareAndVerify(true);
                WriteBytes(
                    fileSystem.CreateNewFile("journal.json"),
                    new byte[] { 1 });
                WriteBytes(
                    fileSystem.CreateNewFile("journal.json.new"),
                    new byte[] { 2, 3 });
                try
                {
                    fileSystem.ReplaceFile(
                        "journal.json.new",
                        "journal.json",
                        "journal.json.bak");
                    throw new InvalidOperationException(
                        "Post-backup verification failure did not escape.");
                }
                catch (JournalFilePublicationException failure)
                {
                    Assert(
                        !failure.CandidatePublished,
                        "Backup-only rename was reported as candidate publication.");
                }
                Assert(
                    fileSystem.FileExists("journal.json.bak"),
                    "Committed backup rename was not retained.");
                Assert(
                    !fileSystem.FileExists("journal.json"),
                    "Backup-only failure unexpectedly retained the primary name.");
                Assert(
                    fileSystem.FileExists("journal.json.new"),
                    "Backup-only failure lost the unpublished candidate.");
            }
            finally
            {
                fileSystem.Dispose();
                DeleteRoot(root);
            }
        }

        private static void TestNativeHostileParentAcl()
        {
            string root = NewRoot();
            Directory.CreateDirectory(Path.Combine(root, "SBMS"));
            try
            {
                using (var fileSystem =
                    new WindowsHandleRelativeJournalFileSystem(
                        root,
                        CurrentSecurityProfile(),
                        null))
                {
                    AssertThrows<UnauthorizedAccessException>(
                        delegate { fileSystem.PrepareAndVerify(true); },
                        "Native root accepted a permissive precreated SBMS parent.");
                }
                Assert(
                    !Directory.Exists(
                        Path.Combine(root, "SBMS", "Installer")),
                    "Hostile parent was used to create installer state.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestHiddenPendingWal()
        {
            string root = NewRoot();
            string installer = Path.Combine(root, "SBMS", "Installer");
            string detached = installer + "-detached";
            using (var fileSystem =
                new WindowsHandleRelativeJournalFileSystem(
                    root,
                    CurrentSecurityProfile(),
                    null))
            {
                fileSystem.PrepareAndVerify(true);
                WriteBytes(
                    fileSystem.CreateNewFile("journal.json"),
                    new byte[] { 1, 2, 3 });
            }
            Directory.Move(installer, detached);
            var native = new WindowsHandleRelativeJournalFileSystem(
                root,
                CurrentSecurityProfile(),
                null);
            var store = new FileTransactionJournalStore(
                new FixedProgramDataPathProvider(root),
                new WindowsInstallerJournalAclPolicy(),
                TestMutexName(),
                TimeSpan.FromSeconds(2),
                null,
                native,
                new UnsecuredInstallerTransactionMutexFactory());
            try
            {
                AssertThrows<InvalidDataException>(
                    delegate { store.PrepareForNewTransaction(); },
                    "A detached pending WAL was treated as an empty store.");
                Assert(
                    File.Exists(Path.Combine(detached, "journal.json")),
                    "Hidden pending WAL fixture was altered.");
            }
            finally
            {
                store.Dispose();
                DeleteRoot(root);
            }
        }

        private static void TestNativeParentReparse()
        {
            string root = NewRoot();
            string target = NewRoot();
            string link = Path.Combine(root, "SBMS");
            try
            {
                if (!CreateSymbolicLink(link, target, 3) &&
                    !CreateDirectoryJunction(link, target))
                {
                    throw new InvalidOperationException(
                        "Unable to create parent reparse fixture.");
                }
                Assert(
                    (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
                    "Parent reparse fixture is not a reparse point.");
                using (var fileSystem =
                    new WindowsHandleRelativeJournalFileSystem(
                        root,
                        CurrentSecurityProfile(),
                        null))
                {
                    AssertThrows<Exception>(
                        delegate { fileSystem.PrepareAndVerify(true); },
                        "Native root followed a parent directory reparse.");
                }
            }
            finally
            {
                DeleteReparseFixture(link);
                DeleteRoot(root);
                DeleteRoot(target);
            }
        }

        private static void TestNativeFinalReparse()
        {
            string root = NewRoot();
            string target = NewRoot();
            string link = Path.Combine(root, "SBMS", "Installer");
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "SBMS"));
                if (!CreateSymbolicLink(link, target, 3) &&
                    !CreateDirectoryJunction(link, target))
                {
                    throw new InvalidOperationException(
                        "Unable to create final reparse fixture.");
                }
                Assert(
                    (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
                    "Final reparse fixture is not a reparse point.");
                using (var fileSystem =
                    new WindowsHandleRelativeJournalFileSystem(
                        root,
                        CurrentSecurityProfile(),
                        null))
                {
                    AssertThrows<Exception>(
                        delegate { fileSystem.PrepareAndVerify(true); },
                        "Native root followed the final directory reparse.");
                }
            }
            finally
            {
                DeleteReparseFixture(link);
                DeleteRoot(root);
                DeleteRoot(target);
            }
        }

        private static void TestNativeExtraAcl()
        {
            string root = NewRoot();
            CreateNativeRoot(root);
            try
            {
                string installer = Path.Combine(root, "SBMS", "Installer");
                DirectorySecurity security =
                    new DirectoryInfo(installer).GetAccessControl();
                security.AddAccessRule(
                    new FileSystemAccessRule(
                        new SecurityIdentifier(
                            WellKnownSidType.WorldSid,
                            null),
                        FileSystemRights.Read,
                        AccessControlType.Allow));
                new DirectoryInfo(installer).SetAccessControl(security);
                using (var fileSystem =
                    new WindowsHandleRelativeJournalFileSystem(
                        root,
                        CurrentSecurityProfile(),
                        null))
                {
                    AssertThrows<UnauthorizedAccessException>(
                        delegate { fileSystem.PrepareAndVerify(false); },
                        "Native root accepted an unexpected ACL entry.");
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void TestNativeInheritedAcl()
        {
            string root = NewRoot();
            CreateNativeRoot(root);
            try
            {
                string installer = Path.Combine(root, "SBMS", "Installer");
                DirectorySecurity security =
                    new DirectoryInfo(installer).GetAccessControl();
                security.SetAccessRuleProtection(false, true);
                new DirectoryInfo(installer).SetAccessControl(security);
                using (var fileSystem =
                    new WindowsHandleRelativeJournalFileSystem(
                        root,
                        CurrentSecurityProfile(),
                        null))
                {
                    AssertThrows<UnauthorizedAccessException>(
                        delegate { fileSystem.PrepareAndVerify(false); },
                        "Native root accepted ACL inheritance.");
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void CreateNativeRoot(string root)
        {
            using (var fileSystem =
                new WindowsHandleRelativeJournalFileSystem(
                    root,
                    CurrentSecurityProfile(),
                    null))
            {
                fileSystem.PrepareAndVerify(true);
            }
        }

        private static void TestSecureMutex()
        {
            string name = TestMutexName();
            var factory = new SecureInstallerTransactionMutexFactory(
                CurrentSecurityProfile());
            using (Mutex first = factory.OpenOrCreate(name))
            using (Mutex second = factory.OpenOrCreate(name))
            {
                Assert(first.WaitOne(1000), "Secure mutex was not acquirable.");
                first.ReleaseMutex();
                Assert(second.WaitOne(1000), "Secure mutex reopen failed.");
                second.ReleaseMutex();
            }
        }

        private static void TestSecureMutexPrecreation()
        {
            string name = TestMutexName();
            using (var insecure = new Mutex(false, name))
            {
                MutexSecurity security = insecure.GetAccessControl();
                security.SetAccessRuleProtection(false, false);
                security.AddAccessRule(
                    new MutexAccessRule(
                        new SecurityIdentifier(
                            WellKnownSidType.WorldSid,
                            null),
                        MutexRights.Synchronize,
                        AccessControlType.Allow));
                insecure.SetAccessControl(security);
                var factory = new SecureInstallerTransactionMutexFactory(
                    CurrentSecurityProfile());
                AssertThrows<UnauthorizedAccessException>(
                    delegate
                    {
                        using (factory.OpenOrCreate(name))
                        {
                        }
                    },
                    "Secure mutex accepted a low-trust precreated object.");
            }
        }

        private static void TestSecureMutexAbandoned()
        {
            string name = TestMutexName();
            var factory = new SecureInstallerTransactionMutexFactory(
                CurrentSecurityProfile());
            Exception workerFailure = null;
            var worker = new Thread(delegate()
            {
                try
                {
                    Mutex mutex = factory.OpenOrCreate(name);
                    mutex.WaitOne();
                    // Deliberately exit without ReleaseMutex.
                }
                catch (Exception ex)
                {
                    workerFailure = ex;
                }
            });
            worker.Start();
            worker.Join();
            if (workerFailure != null)
            {
                throw new InvalidOperationException(
                    "Abandoned mutex fixture failed.",
                    workerFailure);
            }
            using (Mutex recovered = factory.OpenOrCreate(name))
            {
                bool acquired = false;
                try
                {
                    acquired = recovered.WaitOne(1000);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                Assert(acquired, "Secure abandoned mutex was not recoverable.");
                recovered.ReleaseMutex();
            }
        }

        private static WindowsJournalSecurityProfile CurrentSecurityProfile()
        {
            SecurityIdentifier current =
                WindowsIdentity.GetCurrent().User;
            return new WindowsJournalSecurityProfile
            {
                Owner = current,
                FullControlIdentities = new[] { current }
            };
        }

        private static void WriteBytes(Stream stream, byte[] bytes)
        {
            using (stream)
            {
                stream.Write(bytes, 0, bytes.Length);
                var file = stream as FileStream;
                if (file != null)
                {
                    file.Flush(true);
                }
                else
                {
                    stream.Flush();
                }
            }
        }

        private static byte[] ReadBytes(Stream stream)
        {
            using (stream)
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        private static bool CreateDirectoryJunction(
            string link,
            string target)
        {
            Directory.CreateDirectory(link);
            Microsoft.Win32.SafeHandles.SafeFileHandle handle =
                OpenReparseDirectory(
                    link,
                    0x40000000,
                    0,
                    IntPtr.Zero,
                    3,
                    0x02200000,
                    IntPtr.Zero);
            if (handle.IsInvalid)
            {
                Directory.Delete(link, false);
                return false;
            }
            using (handle)
            {
                string printName = Path.GetFullPath(target);
                string substituteName = @"\??\" + printName;
                byte[] substituteBytes =
                    System.Text.Encoding.Unicode.GetBytes(substituteName);
                byte[] printBytes =
                    System.Text.Encoding.Unicode.GetBytes(printName);
                int pathBytesLength =
                    substituteBytes.Length + 2 + printBytes.Length + 2;
                int dataLength = 8 + pathBytesLength;
                byte[] buffer = new byte[8 + dataLength];
                Buffer.BlockCopy(
                    BitConverter.GetBytes(unchecked((int)0xA0000003)),
                    0,
                    buffer,
                    0,
                    4);
                Buffer.BlockCopy(
                    BitConverter.GetBytes((ushort)dataLength),
                    0,
                    buffer,
                    4,
                    2);
                Buffer.BlockCopy(
                    BitConverter.GetBytes((ushort)0),
                    0,
                    buffer,
                    8,
                    2);
                Buffer.BlockCopy(
                    BitConverter.GetBytes(
                        checked((ushort)substituteBytes.Length)),
                    0,
                    buffer,
                    10,
                    2);
                Buffer.BlockCopy(
                    BitConverter.GetBytes(
                        checked((ushort)(substituteBytes.Length + 2))),
                    0,
                    buffer,
                    12,
                    2);
                Buffer.BlockCopy(
                    BitConverter.GetBytes(
                        checked((ushort)printBytes.Length)),
                    0,
                    buffer,
                    14,
                    2);
                Buffer.BlockCopy(
                    substituteBytes,
                    0,
                    buffer,
                    16,
                    substituteBytes.Length);
                Buffer.BlockCopy(
                    printBytes,
                    0,
                    buffer,
                    16 + substituteBytes.Length + 2,
                    printBytes.Length);
                int returned;
                bool success = SetReparsePoint(
                    handle,
                    0x000900A4,
                    buffer,
                    buffer.Length,
                    null,
                    0,
                    out returned,
                    IntPtr.Zero);
                if (!success)
                {
                    Directory.Delete(link, false);
                }
                return success;
            }
        }

        private static void DeleteReparseFixture(string path)
        {
            if (Directory.Exists(path) ||
                (File.Exists(path) &&
                 (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            {
                Directory.Delete(path, false);
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

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern bool CreateHardLink(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(
            string symbolicFileName,
            string targetFileName,
            int flags);

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle
            OpenReparseDirectory(
                string fileName,
                uint desiredAccess,
                uint shareMode,
                IntPtr securityAttributes,
                uint creationDisposition,
                uint flagsAndAttributes,
                IntPtr templateFile);

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            EntryPoint = "DeviceIoControl",
            SetLastError = true)]
        private static extern bool SetReparsePoint(
            Microsoft.Win32.SafeHandles.SafeFileHandle device,
            uint controlCode,
            byte[] input,
            int inputLength,
            byte[] output,
            int outputLength,
            out int bytesReturned,
            IntPtr overlapped);
    }
}
