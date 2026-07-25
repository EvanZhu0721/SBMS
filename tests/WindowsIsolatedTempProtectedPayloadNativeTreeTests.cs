using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SBMSSetup
{
    internal static class WindowsIsolatedTempProtectedPayloadNativeTreeTests
    {
        private const string TransactionId =
            "00000000000000000000000000000031";
        private static int passed;
        private static int failed;

        public static int Main()
        {
            Run("guard accepts only a direct random temp child",
                GuardAcceptsOnlyRandomTempChild);
            Run("guard rejects temp-root reparse fixture",
                GuardRejectsReparseFixture);
            Run("root identity is handle-bound",
                RootIdentityIsHandleBound);
            Run("unknown namespace root entries fail closed",
                UnknownNamespaceRootEntryFailsClosed);
            Run("CreateRoot is handle-relative and fail-closed on replay",
                CreateRootIsHandleRelativeAndReplayFailsClosed);
            Run("durable model publishes a complete native candidate",
                DurableModelPublishesCompleteNativeCandidate);
            Run("hardlinked build entries fail closed",
                HardlinkedBuildEntryFailsClosed);
            Console.WriteLine(
                "Windows isolated temp payload native tree tests: " +
                passed + " passed, " + failed + " failed.");
            return failed == 0 ? 0 : 1;
        }

        private static void GuardAcceptsOnlyRandomTempChild()
        {
            string root = NewRootPath();
            try
            {
                using (
                    WindowsIsolatedTempProtectedPayloadNativeTree tree =
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(root))
                {
                    Equal(
                        Path.GetFullPath(root),
                        tree.RootIdentity.CanonicalRootPath,
                        "Accepted temp root path changed.");
                }
                Throws<Exception>(
                    delegate
                    {
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(Path.GetTempPath());
                    },
                    "The %TEMP% root itself was accepted.");
                Throws<Exception>(
                    delegate
                    {
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(
                                Path.Combine(
                                    Path.GetTempPath(),
                                    "nested",
                                    "SBMS.PayloadTests." +
                                    Guid.NewGuid().ToString("N")));
                    },
                    "A nested temp path was accepted.");
                Throws<Exception>(
                    delegate
                    {
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(
                                Path.Combine(
                                    Environment.GetFolderPath(
                                        Environment.SpecialFolder.
                                            ProgramFiles),
                                    "SBMS.PayloadTests." +
                                    Guid.NewGuid().ToString("N")));
                    },
                    "A Program Files path was accepted.");
                Throws<Exception>(
                    delegate
                    {
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(
                                Path.Combine(
                                    Environment.GetFolderPath(
                                        Environment.SpecialFolder.
                                            CommonApplicationData),
                                    "SBMS.PayloadTests." +
                                    Guid.NewGuid().ToString("N")));
                    },
                    "A ProgramData path was accepted.");
                Throws<Exception>(
                    delegate
                    {
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(
                                Path.Combine(
                                    Path.GetTempPath(),
                                    "SBMS.PayloadTests." +
                                    Guid.NewGuid().ToString("N").
                                        ToUpperInvariant()));
                    },
                    "A noncanonical random suffix was accepted.");
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void GuardRejectsReparseFixture()
        {
            string target = NewRootPath();
            string link = NewRootPath();
            Directory.CreateDirectory(target);
            try
            {
                if (!CreateSymbolicLink(link, target, 3) &&
                    !CreateDirectoryJunction(link, target))
                {
                    throw new InvalidOperationException(
                        "Unable to create isolated reparse fixture.");
                }
                True(
                    (File.GetAttributes(link) &
                        FileAttributes.ReparsePoint) != 0,
                    "Isolated reparse fixture is not a reparse point.");
                Throws<Exception>(
                    delegate
                    {
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(link);
                    },
                    "A reparse-point temp root was accepted.");
            }
            finally
            {
                DeleteLink(link);
                DeleteRoot(target);
            }
        }

        private static void RootIdentityIsHandleBound()
        {
            string root = NewRootPath();
            try
            {
                using (
                    WindowsIsolatedTempProtectedPayloadNativeTree tree =
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(root))
                {
                    PayloadNamespaceRootIdentity identity =
                        tree.RootIdentity;
                    identity.Validate();
                    using (
                        IProtectedPayloadNativeTreeSession session =
                            tree.OpenExclusive(identity))
                    {
                        session.DemandNamespaceExclusionHeld();
                    }
                    PayloadNamespaceRootIdentity foreign =
                        tree.RootIdentity;
                    foreign.RootFileId =
                        "ffffffffffffffffffffffffffffffff";
                    Throws<InvalidDataException>(
                        delegate { tree.OpenExclusive(foreign); },
                        "A foreign root FileId was accepted.");
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void
            UnknownNamespaceRootEntryFailsClosed()
        {
            string root = NewRootPath();
            try
            {
                using (
                    WindowsIsolatedTempProtectedPayloadNativeTree tree =
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(root))
                {
                    BuildFixture fixture = Build(tree.RootIdentity);
                    File.WriteAllText(
                        Path.Combine(root, "foreign.txt"),
                        "foreign");
                    using (
                        IProtectedPayloadNativeTreeSession session =
                            tree.OpenExclusive(tree.RootIdentity))
                    {
                        Throws<InvalidDataException>(
                            delegate
                            {
                                session.ValidateCheckpoint(
                                    fixture.Initial.Checkpoint);
                            },
                            "An unknown namespace root entry was ignored.");
                    }
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void
            CreateRootIsHandleRelativeAndReplayFailsClosed()
        {
            string root = NewRootPath();
            try
            {
                using (
                    WindowsIsolatedTempProtectedPayloadNativeTree tree =
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(root))
                {
                    BuildFixture fixture = Build(tree.RootIdentity);
                    PayloadBuildMutationPlan begin =
                        PayloadBuildMutationPlan.Begin(
                            fixture.Authority,
                            fixture.Initial,
                            fixture.Manifest,
                            fixture.SourceReceipt,
                            fixture.BuildId);
                    PayloadBuildMutationPlan publish =
                        PayloadBuildMutationPlan.Publish(
                            fixture.Authority,
                            begin.ExpectedControlAfter,
                            fixture.Manifest,
                            fixture.SourceReceipt,
                            PayloadBuildStepKind.CreateRoot,
                            -1,
                            Id(3102),
                            String.Empty,
                            PayloadQuarantineReason.InterruptedBuild);
                    PayloadBuildMutationPlan complete =
                        PayloadBuildMutationPlan.Complete(
                            fixture.Authority,
                            publish.ExpectedControlAfter,
                            fixture.Manifest,
                            fixture.SourceReceipt,
                            String.Empty,
                            PayloadQuarantineReason.InterruptedBuild);
                    using (
                        IProtectedPayloadNativeTreeSession session =
                            tree.OpenExclusive(tree.RootIdentity))
                    using (FakeSource source =
                        new FakeSource(
                            fixture.Manifest,
                            fixture.Bytes))
                    {
                        session.ValidateCheckpoint(
                            publish.ExpectedControlAfter.Checkpoint);
                        PayloadBuildPhysicalResult result =
                            session.ApplyBuildStepExact(
                                complete,
                                source);
                        True(
                            result.PartialTree.Exists &&
                            result.PartialTree.Entries.Count == 0,
                            "CreateRoot did not return an empty owned root.");
                    }

                    string buildPath = Path.Combine(
                        root,
                        ".SBMS.build." + fixture.BuildId);
                    True(
                        Directory.Exists(buildPath),
                        "CreateRoot did not create its deterministic leaf.");
                    AssertAllPathsStayInside(root);

                    using (
                        IProtectedPayloadNativeTreeSession session =
                            tree.OpenExclusive(tree.RootIdentity))
                    {
                        Throws<InvalidDataException>(
                            delegate
                            {
                                session.ValidateCheckpoint(
                                    publish.ExpectedControlAfter.Checkpoint);
                            },
                            "Physical-ahead CreateRoot was accepted without " +
                            "a durable ownership marker.");
                    }
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void DurableModelPublishesCompleteNativeCandidate()
        {
            string root = NewRootPath();
            string buildLeaf = null;
            string candidatePath = null;
            PayloadBuildWorkspaceCheckpoint terminal = null;
            var lease = new MemoryLeaseCoordinator();
            MemoryCheckpointStore store = null;
            BuildFixture fixture;
            try
            {
                using (
                    WindowsIsolatedTempProtectedPayloadNativeTree tree =
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(root))
                {
                    fixture = Build(tree.RootIdentity);
                    store = new MemoryCheckpointStore(
                        lease,
                        fixture.Initial.Checkpoint);
                    PayloadBuildAdvanceResult result = null;
                    using (
                        var model =
                            new DurableProtectedPayloadBuildWorkspaceModel(
                                store,
                                lease,
                                tree))
                    using (FakeSource source =
                        new FakeSource(
                            fixture.Manifest,
                            fixture.Bytes))
                    using (
                        var machine =
                            new DeterministicProtectedPayloadBuildStateMachine(
                                fixture.Authority,
                                model))
                    {
                        for (int attempt = 0; attempt < 40; ++attempt)
                        {
                            result = machine.Advance(
                                source,
                                fixture.Manifest,
                                fixture.BuildId,
                                Id(3200 + attempt));
                            if (result.Kind !=
                                PayloadBuildAdvanceKind.InProgress)
                            {
                                break;
                            }
                        }
                        Equal(
                            PayloadBuildAdvanceKind.CandidatePublished,
                            result.Kind,
                            "Native build did not publish a candidate.");
                    }
                    terminal = store.State;
                    buildLeaf = Path.Combine(
                        root,
                        ".SBMS.build." + fixture.BuildId);
                    candidatePath = Path.Combine(
                        root,
                        PayloadNamespaceNames.ForSlot(
                            PayloadDirectorySlot.Candidate,
                            TransactionId));
                }

                True(
                    terminal != null &&
                    terminal.Committed.Candidate != null &&
                    terminal.ActiveBuild == null &&
                    terminal.ActivePartialTree == null,
                    "Native candidate checkpoint is not terminal.");
                True(
                    !Directory.Exists(buildLeaf),
                    "Published candidate retained its old build leaf.");
                True(
                    Directory.Exists(candidatePath),
                    "Published candidate directory is absent.");
                string payloadPath = Path.Combine(
                    candidatePath,
                    "SBMS.exe");
                byte[] actual = File.ReadAllBytes(payloadPath);
                True(
                    BytesEqual(fixture.Bytes, actual),
                    "Published native payload bytes changed.");
                Equal(
                    fixture.Manifest.Content[0].Sha256,
                    Sha(actual),
                    "Published native payload hash changed.");
                Equal(
                    terminal.Committed.Candidate.FileId,
                    ReadDirectoryFileId(candidatePath),
                    "Candidate checkpoint FileId differs from the native root.");
                Equal(
                    terminal.Committed.Candidate.Entries[0].FileId,
                    ReadFileId(payloadPath, false),
                    "Candidate entry FileId differs from the native file.");
                string nestedPayloadPath = Path.Combine(
                    candidatePath,
                    "driver",
                    "SBMS.dll");
                True(
                    BytesEqual(
                        fixture.Bytes,
                        File.ReadAllBytes(nestedPayloadPath)),
                    "Nested native payload bytes changed.");
                Equal(
                    FindEntry(
                        terminal.Committed.Candidate,
                        @"driver\SBMS.dll").FileId,
                    ReadFileId(nestedPayloadPath, false),
                    "Nested candidate FileId differs from the native file.");
                AssertAllPathsStayInside(root);

                // Reopen the adapter and durable model over the terminal
                // checkpoint. This must re-enumerate and hash the committed
                // candidate rather than trusting the durable envelope alone.
                using (
                    WindowsIsolatedTempProtectedPayloadNativeTree restarted =
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(root))
                using (
                    var reopened =
                        new DurableProtectedPayloadBuildWorkspaceModel(
                            store,
                            lease,
                            restarted))
                {
                    PayloadBuildWorkspaceState inspected =
                        reopened.Inspect();
                    True(
                        inspected.Checkpoint.Committed.Candidate != null &&
                        inspected.Checkpoint.ActiveBuild == null,
                        "Restarted durable model rejected terminal candidate.");
                }

                File.WriteAllBytes(payloadPath, new byte[] { 9 });
                using (
                    WindowsIsolatedTempProtectedPayloadNativeTree tampered =
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(root))
                using (
                    var reopened =
                        new DurableProtectedPayloadBuildWorkspaceModel(
                            store,
                            lease,
                            tampered))
                {
                    Throws<InvalidDataException>(
                        delegate { reopened.Inspect(); },
                        "Committed candidate tampering escaped native " +
                        "re-enumeration.");
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static void HardlinkedBuildEntryFailsClosed()
        {
            string root = NewRootPath();
            string external = NewRootPath();
            Directory.CreateDirectory(external);
            try
            {
                var lease = new MemoryLeaseCoordinator();
                using (
                    WindowsIsolatedTempProtectedPayloadNativeTree tree =
                        WindowsIsolatedTempProtectedPayloadNativeTree.
                            CreateForIsolatedTests(root))
                {
                    BuildFixture fixture = Build(tree.RootIdentity);
                    var store = new MemoryCheckpointStore(
                        lease,
                        fixture.Initial.Checkpoint);
                    using (
                        var model =
                            new DurableProtectedPayloadBuildWorkspaceModel(
                                store,
                                lease,
                                tree))
                    using (FakeSource source =
                        new FakeSource(
                            fixture.Manifest,
                            fixture.Bytes))
                    using (
                        var machine =
                            new DeterministicProtectedPayloadBuildStateMachine(
                                fixture.Authority,
                                model))
                    {
                        for (int advance = 0; advance < 5; ++advance)
                        {
                            Equal(
                                PayloadBuildAdvanceKind.InProgress,
                                machine.Advance(
                                    source,
                                    fixture.Manifest,
                                    fixture.BuildId,
                                    Id(3300 + advance)).Kind,
                                "Native build advanced unexpectedly before " +
                                "the hardlink injection point.");
                        }
                        string file = Path.Combine(
                            root,
                            ".SBMS.build." + fixture.BuildId,
                            "SBMS.exe");
                        True(
                            File.Exists(file),
                            "Native build file was not created.");
                        True(
                            CreateHardLink(
                                Path.Combine(external, "linked.exe"),
                                file,
                                IntPtr.Zero),
                            "Unable to create an isolated hardlink fixture.");
                        Throws<InvalidDataException>(
                            delegate
                            {
                                machine.Advance(
                                    source,
                                    fixture.Manifest,
                                    fixture.BuildId,
                                    Id(3310));
                            },
                            "A multi-link payload file escaped native " +
                            "checkpoint validation.");
                    }
                }
            }
            finally
            {
                DeleteRoot(root);
                DeleteRoot(external);
            }
        }

        private static BuildFixture Build(
            PayloadNamespaceRootIdentity root)
        {
            byte[] bytes = { 1, 2, 3, 4 };
            var manifest = new TargetPayloadManifest
            {
                SchemaVersion = 1,
                TransactionId = TransactionId,
                Target = new ReleaseIdentity(
                    "0.3.0",
                    Sha(new byte[] { 3 })),
                ReleaseCatalogSha256 = Sha(new byte[] { 10 }),
                SignedReleaseManifestSha256 =
                    Sha(new byte[] { 11 })
            };
            manifest.Content.Add(new TargetPayloadEntry
            {
                RelativePath = "SBMS.exe",
                Length = bytes.Length,
                Sha256 = Sha(bytes)
            });
            manifest.Content.Add(new TargetPayloadEntry
            {
                RelativePath = @"driver\SBMS.dll",
                Length = bytes.Length,
                Sha256 = Sha(bytes)
            });
            manifest.ContentSetSha256 =
                manifest.ComputeContentSetSha256();
            var directory = new PayloadDirectoryCheckpoint
            {
                TransactionId = TransactionId,
                Slot = PayloadDirectorySlot.Candidate,
                VolumeSerialNumber = root.VolumeSerialNumber,
                FileId = Id(3190),
                Release = new ReleaseIdentity(
                    manifest.Target.Version,
                    manifest.Target.PackageFingerprint),
                ContentSetSha256 = manifest.ContentSetSha256,
                ManifestInvariantDigest = manifest.InvariantDigest,
                FileCount = 2,
                TotalBytes = bytes.Length * 2
            };
            directory.Entries.Add(new PayloadTreeEntryCheckpoint
            {
                RelativePath = "SBMS.exe",
                IsDirectory = false,
                FileId = Id(3191),
                Length = bytes.Length,
                Sha256 = Sha(bytes)
            });
            directory.Entries.Add(new PayloadTreeEntryCheckpoint
            {
                RelativePath = "driver",
                IsDirectory = true,
                FileId = Id(3192),
                Length = 0,
                Sha256 = String.Empty
            });
            directory.Entries.Add(new PayloadTreeEntryCheckpoint
            {
                RelativePath = @"driver\SBMS.dll",
                IsDirectory = false,
                FileId = Id(3193),
                Length = bytes.Length,
                Sha256 = Sha(bytes)
            });
            var authority = new PayloadRecoveryAuthority
            {
                SchemaVersion = 1,
                TransactionId = TransactionId,
                Operation = InstallOperation.FreshInstall,
                BaselineState = BaselinePayloadState.Absent,
                Baseline = null,
                Target = new PayloadContentAuthority
                {
                    Release = new ReleaseIdentity(
                        directory.Release.Version,
                        directory.Release.PackageFingerprint),
                    ContentSetSha256 =
                        directory.ContentSetSha256,
                    ManifestInvariantDigest =
                        directory.ManifestInvariantDigest,
                    SemanticTreeSha256 =
                        directory.SemanticTreeSha256,
                    FileCount = directory.FileCount,
                    TotalBytes = directory.TotalBytes
                },
                SealedEscrowManifestSha256 =
                    Sha(new byte[] { 7 })
            };
            var checkpoint = new PayloadBuildWorkspaceCheckpoint
            {
                SchemaVersion = 3,
                Revision = 1,
                RecoveryGeneration = 0,
                TransactionId = TransactionId,
                RecoveryAuthorityInvariantDigest =
                    authority.InvariantDigest,
                NamespaceRoot = root,
                Committed = new PayloadNamespaceCheckpoint
                {
                    SchemaVersion = 1,
                    Revision = 1,
                    TransactionId = TransactionId,
                    Shape = PayloadNamespaceShape.Empty
                }
            };
            return new BuildFixture
            {
                Authority = authority,
                Manifest = manifest,
                SourceReceipt =
                    new TrustedReleasePayloadReceipt(manifest),
                Initial =
                    new PayloadBuildWorkspaceState(checkpoint),
                BuildId = Id(3100),
                Bytes = bytes
            };
        }

        private static PayloadTreeEntryCheckpoint FindEntry(
            PayloadDirectoryCheckpoint directory,
            string relativePath)
        {
            foreach (PayloadTreeEntryCheckpoint entry in directory.Entries)
            {
                if (String.Equals(
                        entry.RelativePath,
                        relativePath,
                        StringComparison.Ordinal))
                {
                    return entry;
                }
            }
            throw new InvalidDataException(
                "Candidate checkpoint entry is missing: " + relativePath);
        }

        private static void AssertAllPathsStayInside(string root)
        {
            string prefix = Path.GetFullPath(root).
                TrimEnd('\\') + "\\";
            foreach (string path in Directory.GetFileSystemEntries(
                root,
                "*",
                SearchOption.AllDirectories))
            {
                True(
                    Path.GetFullPath(path).StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase),
                    "Native-tree test emitted a path outside its temp root.");
            }
        }

        private static string NewRootPath()
        {
            return Path.Combine(
                Path.GetTempPath(),
                "SBMS.PayloadTests." +
                Guid.NewGuid().ToString("N"));
        }

        private static void DeleteRoot(string path)
        {
            string root = Path.GetFullPath(path);
            string temp = Path.GetFullPath(Path.GetTempPath()).
                TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            string expectedPrefix = temp +
                Path.DirectorySeparatorChar +
                "SBMS.PayloadTests.";
            if (!root.StartsWith(
                    expectedPrefix,
                    StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(
                    Path.GetDirectoryName(root).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    temp,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to recursively delete outside the isolated " +
                    "payload test root.");
            }
            if (Directory.Exists(path))
            {
                FileAttributes attributes = File.GetAttributes(root);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(root, false);
                }
                else
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void DeleteLink(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                return;
            }
            try
            {
                Directory.Delete(path);
            }
            catch
            {
                File.Delete(path);
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

        private static string Id(int value)
        {
            return value.ToString("x32");
        }

        private static string Sha(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                return BitConverter.ToString(
                    algorithm.ComputeHash(bytes)).
                    Replace("-", String.Empty).
                    ToLowerInvariant();
            }
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (first == null || second == null ||
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

        private static string ReadDirectoryFileId(string path)
        {
            return ReadFileId(path, true);
        }

        private static string ReadFileId(string path, bool directory)
        {
            SafeFileHandleForTest handle = CreateFileForTest(
                path,
                0x0080,
                0x00000001 | 0x00000002 | 0x00000004,
                IntPtr.Zero,
                3,
                directory ? 0x02000000U : 0U,
                IntPtr.Zero);
            if (handle == null || handle.IsInvalid)
            {
                throw new InvalidOperationException(
                    "Unable to open native candidate identity.");
            }
            using (handle)
            {
                FileIdInfoForTest info;
                if (!GetFileInformationByHandleExForTest(
                    handle,
                    18,
                    out info,
                    Marshal.SizeOf(typeof(FileIdInfoForTest))))
                {
                    throw new InvalidOperationException(
                        "Unable to read native candidate FileId.");
                }
                return BitConverter.ToString(
                    info.FileId.Identifier).
                    Replace("-", String.Empty).
                    ToLowerInvariant();
            }
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                ++passed;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception failure)
            {
                ++failed;
                Console.WriteLine(
                    "FAIL " + name + ": " + failure);
            }
        }

        private static void True(bool condition, string message)
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
            if (!EqualityComparer<T>.Default.Equals(
                    expected,
                    actual))
            {
                throw new InvalidOperationException(
                    message + " expected=" + expected +
                    " actual=" + actual);
            }
        }

        private static void Throws<T>(
            Action action,
            string message)
            where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            throw new InvalidOperationException(message);
        }

        private sealed class BuildFixture
        {
            internal PayloadRecoveryAuthority Authority;
            internal TargetPayloadManifest Manifest;
            internal TrustedReleasePayloadReceipt SourceReceipt;
            internal PayloadBuildWorkspaceState Initial;
            internal string BuildId;
            internal byte[] Bytes;
        }

        private sealed class FakeSource : ITrustedReleasePayloadSource
        {
            private readonly byte[] bytes;

            internal FakeSource(
                TargetPayloadManifest manifest,
                byte[] content)
            {
                Receipt = new TrustedReleasePayloadReceipt(manifest);
                bytes = (byte[])content.Clone();
            }

            public TrustedReleasePayloadReceipt Receipt { get; private set; }

            public Stream OpenExact(TargetPayloadEntry expected)
            {
                return new MemoryStream(
                    (byte[])bytes.Clone(),
                    false);
            }

            public void Dispose()
            {
            }
        }

        private sealed class MemoryLeaseCoordinator
            : ITransactionLeaseCoordinator
        {
            private int depth;

            public IDisposable Acquire()
            {
                ++depth;
                return new Lease(this);
            }

            public void DemandHeld()
            {
                if (depth <= 0)
                {
                    throw new InvalidOperationException(
                        "In-memory transaction lease is not held.");
                }
            }

            private sealed class Lease : IDisposable
            {
                private MemoryLeaseCoordinator owner;

                internal Lease(MemoryLeaseCoordinator value)
                {
                    owner = value;
                }

                public void Dispose()
                {
                    if (owner != null)
                    {
                        --owner.depth;
                        owner = null;
                    }
                }
            }
        }

        private sealed class MemoryCheckpointStore
            : IProtectedPayloadWorkspaceCheckpointStore
        {
            private readonly MemoryLeaseCoordinator lease;
            internal PayloadBuildWorkspaceCheckpoint State;

            internal MemoryCheckpointStore(
                MemoryLeaseCoordinator coordinator,
                PayloadBuildWorkspaceCheckpoint initial)
            {
                lease = coordinator;
                State = initial.DeepClone();
            }

            public PayloadWorkspaceCheckpointReceipt Initialize(
                PayloadBuildWorkspaceCheckpoint candidate)
            {
                lease.DemandHeld();
                State = candidate.DeepClone();
                return Receipt();
            }

            public PayloadWorkspaceCheckpointReadResult Load()
            {
                lease.DemandHeld();
                return new PayloadWorkspaceCheckpointReadResult
                {
                    Receipt = Receipt(),
                    Source =
                        PayloadWorkspaceCheckpointReadSource.Primary,
                    RequiresPrimaryRepair = false
                };
            }

            public PayloadWorkspaceCheckpointReceipt Save(
                PayloadWorkspaceCasToken expected,
                PayloadBuildWorkspaceCheckpoint candidate)
            {
                lease.DemandHeld();
                new PayloadBuildWorkspaceState(State).
                    RequireCas(expected);
                candidate.Validate();
                if (candidate.Revision !=
                    checked(State.Revision + 1))
                {
                    throw new InvalidOperationException(
                        "In-memory checkpoint revision skipped.");
                }
                State = candidate.DeepClone();
                return Receipt();
            }

            public PayloadWorkspaceCheckpointReceipt RepairPrimary(
                PayloadWorkspaceCheckpointReadResult expectedBackup)
            {
                throw new NotSupportedException();
            }

            private PayloadWorkspaceCheckpointReceipt Receipt()
            {
                return new PayloadWorkspaceCheckpointReceipt(
                    new PayloadBuildWorkspaceState(State),
                    "memory\\checkpoint.json",
                    "memory checkpoint",
                    1,
                    new string('0', 64));
            }
        }

        private sealed class SafeFileHandleForTest
            : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
        {
            private SafeFileHandleForTest()
                : base(true)
            {
            }

            protected override bool ReleaseHandle()
            {
                return CloseHandleForTest(handle);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInfoForTest
        {
            internal ulong VolumeSerialNumber;
            internal FileId128ForTest FileId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileId128ForTest
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            internal byte[] Identifier;
        }

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(
            string symbolicLink,
            string target,
            int flags);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            CharSet = CharSet.Unicode,
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

        [DllImport(
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

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern bool CreateHardLink(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandleForTest CreateFileForTest(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFileInformationByHandleEx",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleExForTest(
            SafeFileHandleForTest file,
            int informationClass,
            out FileIdInfoForTest information,
            int bufferSize);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CloseHandle",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandleForTest(IntPtr handle);
    }
}
