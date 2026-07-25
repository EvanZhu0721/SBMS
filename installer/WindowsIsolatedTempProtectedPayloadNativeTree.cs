using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SBMSSetup
{
    internal interface IWindowsIsolatedTempPayloadNativeTreeTestSeam
    {
        void BeforeOwnershipReplayFlush(PayloadBuildStepKind step);
        void AfterSealMarkerFlushed();
        void AfterQuarantineMarkerFlushed();
        void AfterPurgeChildDeleted();
        void AfterPurgeRootDeleted();
    }

    // This adapter is deliberately limited to a random, isolated child of
    // %TEMP%. Its lease excludes only cooperating adapters in this process.
    // It is native-I/O evidence, not a production Program Files namespace
    // exclusion mechanism, and it does not prove rename power-loss durability.
    internal sealed class WindowsIsolatedTempProtectedPayloadNativeTree
        : IProtectedPayloadNativeTree
    {
        private const string TestLeafPrefix = "SBMS.PayloadTests.";
        private static readonly object LeaseMapGuard = new object();
        private static readonly Dictionary<string, NamespaceLeaseEntry>
            LeaseMap =
                new Dictionary<string, NamespaceLeaseEntry>(
                    StringComparer.OrdinalIgnoreCase);

        private readonly string canonicalRootPath;
        private readonly ulong volumeSerialNumber;
        private readonly string rootFileId;
        private readonly NamespaceLeaseEntry leaseEntry;
        private readonly object namespaceLease;
        private readonly IWindowsIsolatedTempPayloadNativeTreeTestSeam testSeam;
        private SafeFileHandle rootHandle;
        private bool disposed;

        private WindowsIsolatedTempProtectedPayloadNativeTree(
            string rootPath,
            SafeFileHandle handle,
            NativeIdentity identity,
            IWindowsIsolatedTempPayloadNativeTreeTestSeam seam)
        {
            canonicalRootPath = rootPath;
            rootHandle = handle;
            volumeSerialNumber = identity.VolumeSerialNumber;
            rootFileId = identity.FileId;
            testSeam = seam;
            lock (LeaseMapGuard)
            {
                NamespaceLeaseEntry entry;
                if (!LeaseMap.TryGetValue(rootPath, out entry))
                {
                    entry = new NamespaceLeaseEntry();
                    LeaseMap.Add(rootPath, entry);
                }
                entry.RefCount = checked(entry.RefCount + 1);
                leaseEntry = entry;
                namespaceLease = entry.Sync;
            }
        }

        internal static WindowsIsolatedTempProtectedPayloadNativeTree
            CreateForIsolatedTests(string isolatedRootPath)
        {
            return CreateForIsolatedTests(isolatedRootPath, null);
        }

        internal static WindowsIsolatedTempProtectedPayloadNativeTree
            CreateForIsolatedTests(
                string isolatedRootPath,
                IWindowsIsolatedTempPayloadNativeTreeTestSeam seam)
        {
            string root = RequireIsolatedTempRoot(isolatedRootPath);
            Directory.CreateDirectory(root);
            SafeFileHandle handle = null;
            try
            {
                handle = NativeIo.OpenAbsoluteDirectory(root);
                NativeIo.RequireDirectory(handle, root);
                NativeIdentity identity = NativeIo.Identity(handle);
                return new WindowsIsolatedTempProtectedPayloadNativeTree(
                    root,
                    handle,
                    identity,
                    seam);
            }
            catch
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
                throw;
            }
        }

        internal PayloadNamespaceRootIdentity RootIdentity
        {
            get
            {
                ThrowIfDisposed();
                return new PayloadNamespaceRootIdentity
                {
                    SchemaVersion = 1,
                    CanonicalRootPath = canonicalRootPath,
                    VolumeSerialNumber = volumeSerialNumber,
                    RootFileId = rootFileId
                };
            }
        }

        public IProtectedPayloadNativeTreeSession OpenExclusive(
            PayloadNamespaceRootIdentity expectedRoot)
        {
            ThrowIfDisposed();
            if (expectedRoot == null)
            {
                throw new ArgumentNullException("expectedRoot");
            }
            expectedRoot.Validate();
            Monitor.Enter(namespaceLease);
            try
            {
                ThrowIfDisposed();
                DemandExpectedRoot(expectedRoot);
                NativeIo.RequireIdentity(
                    rootHandle,
                    volumeSerialNumber,
                    rootFileId,
                    "Isolated payload root identity changed.");
                return new Session(this);
            }
            catch
            {
                Monitor.Exit(namespaceLease);
                throw;
            }
        }

        public void Dispose()
        {
            if (Monitor.IsEntered(namespaceLease))
            {
                throw new InvalidOperationException(
                    "Cannot dispose the isolated payload tree while its " +
                    "namespace session is active.");
            }
            lock (namespaceLease)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                SafeFileHandle release = rootHandle;
                rootHandle = null;
                if (release != null)
                {
                    release.Dispose();
                }
                lock (LeaseMapGuard)
                {
                    leaseEntry.RefCount =
                        checked(leaseEntry.RefCount - 1);
                    if (leaseEntry.RefCount == 0)
                    {
                        LeaseMap.Remove(canonicalRootPath);
                    }
                }
            }
            // The caller owns the isolated root lifetime so evidence remains
            // inspectable after this handle anchor is released.
        }

        private void DemandExpectedRoot(PayloadNamespaceRootIdentity expected)
        {
            if (!String.Equals(
                    canonicalRootPath,
                    Path.GetFullPath(expected.CanonicalRootPath),
                    StringComparison.OrdinalIgnoreCase) ||
                volumeSerialNumber != expected.VolumeSerialNumber ||
                !String.Equals(
                    rootFileId,
                    expected.RootFileId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Payload checkpoint is bound to another native root.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    "WindowsIsolatedTempProtectedPayloadNativeTree");
            }
        }

        private static string RequireIsolatedTempRoot(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
            {
                throw new ArgumentException(
                    "Isolated payload test root must be absolute.",
                    "isolatedRootPath");
            }
            string root = TrimSeparator(Path.GetFullPath(value));
            string temp = TrimSeparator(Path.GetFullPath(Path.GetTempPath()));
            string prefix = temp + Path.DirectorySeparatorChar;
            if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Isolated payload native tree is restricted to %TEMP%.");
            }
            string relative = root.Substring(prefix.Length);
            if (relative.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                relative.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                !relative.StartsWith(
                    TestLeafPrefix,
                    StringComparison.Ordinal) ||
                relative.Length != TestLeafPrefix.Length + 32 ||
                !IsLowerHex(relative.Substring(TestLeafPrefix.Length)))
            {
                throw new InvalidDataException(
                    "Isolated payload root must be a direct random " +
                    "%TEMP%\\SBMS.PayloadTests.<GUID> child.");
            }
            if (NativeIo.PathIsReparsePoint(root))
            {
                throw new InvalidDataException(
                    "Isolated payload root must not be a reparse point.");
            }
            return root;
        }

        private static bool IsLowerHex(string value)
        {
            foreach (char item in value)
            {
                if (!((item >= '0' && item <= '9') ||
                      (item >= 'a' && item <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private static string TrimSeparator(string value)
        {
            return value.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private sealed class NamespaceLeaseEntry
        {
            internal readonly object Sync = new object();
            internal int RefCount;
        }

        private sealed class Session : IProtectedPayloadNativeTreeSession
        {
            private const string OwnershipMarkerDomain =
                "SBMS.Payload.NativeOwnership.v1";
        private const string QuarantineMarkerDomain =
            "SBMS.Payload.NativeQuarantine.v1";
        private const string PurgeMarkerDomain =
            "SBMS.Payload.NativePurge.v1";
            private WindowsIsolatedTempProtectedPayloadNativeTree owner;

            internal Session(
                WindowsIsolatedTempProtectedPayloadNativeTree value)
            {
                owner = value;
            }

            public void DemandNamespaceExclusionHeld()
            {
                RequireHeld();
            }

            public void ValidateCheckpoint(
                PayloadBuildWorkspaceCheckpoint checkpoint)
            {
                RequireHeld();
                if (checkpoint == null)
                {
                    throw new ArgumentNullException("checkpoint");
                }
                checkpoint.Validate();
                owner.DemandExpectedRoot(checkpoint.NamespaceRoot);
                NativeIo.RequireIdentity(
                    owner.rootHandle,
                    owner.volumeSerialNumber,
                    owner.rootFileId,
                    "Payload namespace root identity changed.");
                ValidateKnownTree(checkpoint);
            }

            public PayloadBuildPhysicalResult ApplyBuildStepExact(
                PayloadBuildMutationPlan plan,
                ITrustedReleasePayloadSource source)
            {
                RequireHeld();
                if (plan == null || source == null ||
                    !plan.StepKind.HasValue)
                {
                    throw new ArgumentException(
                        "A physical payload build plan and source are required.");
                }
                PayloadBuildWorkspaceCheckpoint before =
                    plan.Before.Checkpoint;
                ValidateCheckpoint(before);
                PayloadBuildStepKind step = plan.StepKind.Value;
                switch (step)
                {
                    case PayloadBuildStepKind.CreateRoot:
                        CreateBuildRoot(before);
                        return Partial(step, before);
                    case PayloadBuildStepKind.CreateEntry:
                        CreateEntry(before);
                        return Partial(step, before);
                    case PayloadBuildStepKind.RewriteFileExact:
                        RewriteFile(before, plan, source);
                        return Partial(step, before);
                    case PayloadBuildStepKind.FlushFile:
                        FlushFile(before);
                        return Partial(step, before);
                    case PayloadBuildStepKind.ReopenEntry:
                    case PayloadBuildStepKind.VerifyEntryHash:
                        return Partial(step, before);
                    case PayloadBuildStepKind.SealCandidate:
                        return SealCandidate(before, plan);
                    case PayloadBuildStepKind.QuarantineBuild:
                        return QuarantineBuild(before, plan);
                    default:
                        throw new NotSupportedException(
                            "The isolated temp backend does not yet support " +
                            step + ".");
                }
            }

            public void DeleteQuarantineTreeExact(
                PayloadQuarantineCheckpoint quarantine,
                PayloadPurgeCheckpoint purge)
            {
                RequireHeld();
                RequirePurgeBinding(quarantine, purge);
                byte[] marker = CreatePurgeMarker(purge);
                SafeFileHandle directory =
                    NativeIo.TryOpenRelativeForPurge(
                        owner.rootHandle,
                        quarantine.QuarantineLeafName,
                        true,
                        true);
                if (directory == null)
                {
                    RequireFreshRelativeAbsence(
                        quarantine.QuarantineLeafName);
                    return;
                }
                try
                {
                    NativeIo.RequireIdentity(
                        directory,
                        quarantine.VolumeSerialNumber,
                        quarantine.RootFileId,
                        "Payload purge root identity changed.");
                    NativeIo.RequireQuarantineMarker(
                        directory,
                        CreateQuarantineMarker(quarantine));
                    if (!NativeIo.HasPurgeMarker(directory))
                    {
                        RequireQuarantineTreeExact(
                            quarantine,
                            directory);
                    }
                    NativeIo.PreparePurgeMarker(directory, marker);
                    DeleteDirectoryChildrenExact(directory);
                    NativeIo.MarkDeleteOnClose(directory);
                }
                finally
                {
                    directory.Dispose();
                }
                RequireFreshRelativeAbsence(
                    quarantine.QuarantineLeafName);
                if (owner.testSeam != null)
                {
                    owner.testSeam.AfterPurgeRootDeleted();
                }
            }

            public PayloadQuarantineAbsenceObservation
                ObserveQuarantineAbsenceExact(
                    PayloadBuildWorkspaceCheckpoint checkpoint,
                    PayloadQuarantineCheckpoint quarantine)
            {
                RequireHeld();
                checkpoint.Validate();
                quarantine.Validate();
                SafeFileHandle existing = NativeIo.TryOpenRelative(
                    owner.rootHandle,
                    quarantine.QuarantineLeafName,
                    true,
                    false);
                if (existing != null)
                {
                    existing.Dispose();
                    throw new InvalidDataException(
                        "Payload quarantine still exists.");
                }
                RequireFreshRelativeAbsence(
                    quarantine.QuarantineLeafName);
                var observation =
                    new PayloadQuarantineAbsenceObservation
                    {
                        SchemaVersion = 1,
                        TransactionId = checkpoint.TransactionId,
                        RecoveryAuthorityInvariantDigest =
                            checkpoint.RecoveryAuthorityInvariantDigest,
                        NamespaceRootInvariantDigest =
                            checkpoint.NamespaceRoot.InvariantDigest,
                        QuarantineId = quarantine.QuarantineId,
                        QuarantineLeafName =
                            quarantine.QuarantineLeafName,
                        VolumeSerialNumber =
                            quarantine.VolumeSerialNumber,
                        RootFileId = quarantine.RootFileId,
                        ObservedAtWorkspaceRevision =
                            checkpoint.Revision,
                        Exists = false
                    };
                observation.Validate();
                return observation;
            }

            public void Dispose()
            {
                WindowsIsolatedTempProtectedPayloadNativeTree release = owner;
                if (release == null)
                {
                    return;
                }
                if (!Monitor.IsEntered(release.namespaceLease))
                {
                    throw new InvalidOperationException(
                        "The isolated payload session must be disposed on " +
                        "the thread that acquired it.");
                }
                owner = null;
                Monitor.Exit(release.namespaceLease);
            }

            private void RequireHeld()
            {
                if (owner == null ||
                    !Monitor.IsEntered(owner.namespaceLease))
                {
                    throw new InvalidOperationException(
                        "Cooperative payload namespace lease is not held.");
                }
                owner.ThrowIfDisposed();
            }

            private void ValidateKnownTree(
                PayloadBuildWorkspaceCheckpoint checkpoint)
            {
                ValidateNamespaceChildren(checkpoint);
                ValidateCommittedDirectory(
                    checkpoint.Committed.Current);
                ValidateCommittedDirectory(
                    checkpoint.Committed.Candidate);
                ValidateCommittedDirectory(
                    checkpoint.Committed.Backup);
                foreach (PayloadQuarantineCheckpoint quarantine in
                    checkpoint.Quarantines)
                {
                    PayloadPurgeCheckpoint purge =
                        FindPurgeForQuarantine(
                            checkpoint,
                            quarantine.QuarantineId);
                    if (purge == null)
                    {
                        ValidateStoredQuarantine(quarantine);
                    }
                    else
                    {
                        ValidatePendingPurge(quarantine, purge);
                    }
                }
                PayloadPartialTreeObservation partial =
                    checkpoint.ActivePartialTree;
                if (partial != null &&
                    IsActiveIntent(
                        checkpoint,
                        PayloadBuildStepKind.QuarantineBuild))
                {
                    ValidateQuarantinePhysicalShape(checkpoint);
                    return;
                }
                if (partial != null &&
                    IsActiveIntent(
                        checkpoint,
                        PayloadBuildStepKind.SealCandidate))
                {
                    ValidateSealPhysicalShape(checkpoint);
                    return;
                }
                if (partial == null)
                {
                    if (IsActiveIntent(
                            checkpoint,
                            PayloadBuildStepKind.CreateRoot))
                    {
                        RequireCreateRootAheadOrAbsent(checkpoint);
                    }
                    return;
                }
                PayloadPartialTreeObservation observed =
                    ObservePartial(partial.BuildId, partial.LeafName);
                if (String.Equals(
                        observed.InvariantDigest,
                        partial.InvariantDigest,
                        StringComparison.Ordinal))
                {
                    return;
                }
                if (!partial.Exists &&
                    IsActiveIntent(
                        checkpoint,
                        PayloadBuildStepKind.CreateRoot))
                {
                    RequireCreateRootAheadOrAbsent(checkpoint);
                    return;
                }
                if (IsActiveIntent(
                        checkpoint,
                        PayloadBuildStepKind.CreateEntry) &&
                    IsExactCreateEntryAhead(checkpoint, observed))
                {
                    return;
                }
                throw new InvalidDataException(
                    "Isolated native payload tree differs from checkpoint.");
            }

            private static PayloadPurgeCheckpoint FindPurgeForQuarantine(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                string quarantineId)
            {
                PayloadPurgeCheckpoint found = null;
                foreach (PayloadPurgeCheckpoint purge in
                    checkpoint.PendingPurges)
                {
                    if (String.Equals(
                            purge.QuarantineId,
                            quarantineId,
                            StringComparison.Ordinal))
                    {
                        if (found != null)
                        {
                            throw new InvalidDataException(
                                "Multiple purges target one quarantine.");
                        }
                        found = purge;
                    }
                }
                return found;
            }

            private void ValidatePendingPurge(
                PayloadQuarantineCheckpoint quarantine,
                PayloadPurgeCheckpoint purge)
            {
                RequirePurgeBinding(quarantine, purge);
                SafeFileHandle directory =
                    NativeIo.TryOpenRelativeForPurge(
                        owner.rootHandle,
                        quarantine.QuarantineLeafName,
                        true,
                        false);
                if (purge.Phase == PayloadPurgePhase.ObservedAbsent)
                {
                    if (directory != null)
                    {
                        directory.Dispose();
                        throw new InvalidDataException(
                            "Observed-absent quarantine reappeared.");
                    }
                    RequireFreshRelativeAbsence(
                        quarantine.QuarantineLeafName);
                    return;
                }
                if (directory == null)
                {
                    RequireFreshRelativeAbsence(
                        quarantine.QuarantineLeafName);
                    return;
                }
                using (directory)
                {
                    NativeIo.RequireIdentity(
                        directory,
                        quarantine.VolumeSerialNumber,
                        quarantine.RootFileId,
                        "Pending purge root identity changed.");
                    NativeIo.RequireQuarantineMarker(
                        directory,
                        CreateQuarantineMarker(quarantine));
                    byte[] marker = CreatePurgeMarker(purge);
                    if (NativeIo.HasPurgeMarker(directory))
                    {
                        NativeIo.RequirePurgeMarker(directory, marker);
                        ValidatePurgeRemainder(directory);
                    }
                    else
                    {
                        RequireQuarantineTreeExact(
                            quarantine,
                            directory);
                    }
                }
            }

            private static void RequirePurgeBinding(
                PayloadQuarantineCheckpoint quarantine,
                PayloadPurgeCheckpoint purge)
            {
                quarantine.Validate();
                purge.Validate();
                if (purge.Phase != PayloadPurgePhase.Armed &&
                    purge.Phase != PayloadPurgePhase.ObservedAbsent)
                {
                    throw new InvalidDataException(
                        "Payload purge phase is unsupported.");
                }
                if (!String.Equals(
                        purge.QuarantineId,
                        quarantine.QuarantineId,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        purge.TransactionId,
                        quarantine.TransactionId,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        purge.RecoveryAuthorityInvariantDigest,
                        quarantine.RecoveryAuthorityInvariantDigest,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        purge.NamespaceRootInvariantDigest,
                        quarantine.NamespaceRootInvariantDigest,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        purge.QuarantineInvariantDigest,
                        quarantine.InvariantDigest,
                        StringComparison.Ordinal) ||
                    purge.VolumeSerialNumber !=
                        quarantine.VolumeSerialNumber ||
                    !String.Equals(
                        purge.RootFileId,
                        quarantine.RootFileId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Payload purge does not bind the quarantine exactly.");
                }
            }

            private void DeleteDirectoryChildrenExact(
                SafeFileHandle directory)
            {
                while (true)
                {
                    IList<NativeDirectoryEntry> entries =
                        NativeIo.Enumerate(directory);
                    if (entries.Count == 0)
                    {
                        return;
                    }
                    foreach (NativeDirectoryEntry entry in entries)
                    {
                        SafeFileHandle child =
                            NativeIo.OpenRelativeForPurge(
                                directory,
                                entry.Name,
                                entry.IsDirectory,
                                false);
                        try
                        {
                            NativeIdentity identity =
                                NativeIo.Identity(child);
                            NativeIo.RequireEnumeratedEntry(
                                directory,
                                entry,
                                identity);
                            if (entry.IsDirectory)
                            {
                                DeleteDirectoryChildrenExact(child);
                            }
                            else
                            {
                                NativeIo.RequireSingleLinkFile(child);
                            }
                            NativeIo.MarkDeleteOnClose(child);
                        }
                        finally
                        {
                            child.Dispose();
                        }
                        NativeIo.RequireRelativeAbsent(
                            directory,
                            entry.Name);
                        if (owner.testSeam != null)
                        {
                            owner.testSeam.AfterPurgeChildDeleted();
                        }
                    }
                }
            }

            private static void ValidatePurgeRemainder(
                SafeFileHandle directory)
            {
                foreach (NativeDirectoryEntry entry in
                    NativeIo.Enumerate(directory))
                {
                    using (SafeFileHandle child =
                        NativeIo.OpenRelativeForPurge(
                            directory,
                            entry.Name,
                            entry.IsDirectory,
                            false))
                    {
                        NativeIdentity identity =
                            NativeIo.Identity(child);
                        NativeIo.RequireEnumeratedEntry(
                            directory,
                            entry,
                            identity);
                        if (entry.IsDirectory)
                        {
                            ValidatePurgeRemainder(child);
                        }
                        else
                        {
                            NativeIo.RequireSingleLinkFile(child);
                        }
                    }
                }
            }

            private void RequireFreshRelativeAbsence(string name)
            {
                NativeIo.RequireRelativeAbsent(owner.rootHandle, name);
            }

            private void ValidateNamespaceChildren(
                PayloadBuildWorkspaceCheckpoint checkpoint)
            {
                var allowed = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                AddCommittedLeaf(
                    allowed,
                    checkpoint.Committed.Current);
                AddCommittedLeaf(
                    allowed,
                    checkpoint.Committed.Candidate);
                AddCommittedLeaf(
                    allowed,
                    checkpoint.Committed.Backup);
                foreach (PayloadQuarantineCheckpoint quarantine in
                    checkpoint.Quarantines)
                {
                    allowed.Add(quarantine.QuarantineLeafName);
                }
                if (checkpoint.ActivePartialTree != null)
                {
                    allowed.Add(
                        checkpoint.ActivePartialTree.LeafName);
                    if (IsActiveIntent(
                            checkpoint,
                            PayloadBuildStepKind.SealCandidate))
                    {
                        allowed.Add(PayloadNamespaceNames.ForSlot(
                            PayloadDirectorySlot.Candidate,
                            checkpoint.TransactionId));
                    }
                    else if (IsActiveIntent(
                            checkpoint,
                            PayloadBuildStepKind.QuarantineBuild))
                    {
                        allowed.Add(
                            ".SBMS.quarantine." +
                            checkpoint.ActiveBuild.ActiveIntent.IntentId);
                    }
                }
                else if (IsActiveIntent(
                    checkpoint,
                    PayloadBuildStepKind.CreateRoot))
                {
                    allowed.Add(checkpoint.ActiveBuild.BuildLeafName);
                }
                foreach (NativeDirectoryEntry child in
                    NativeIo.Enumerate(owner.rootHandle))
                {
                    if (!child.IsDirectory ||
                        !allowed.Contains(child.Name))
                    {
                        throw new InvalidDataException(
                            "The isolated payload namespace contains an " +
                            "unknown root entry.");
                    }
                }
            }

            private void ValidateQuarantinePhysicalShape(
                PayloadBuildWorkspaceCheckpoint checkpoint)
            {
                PayloadQuarantineCheckpoint quarantine =
                    BuildProjectedQuarantine(
                        checkpoint,
                        checkpoint.ActiveBuild.ActiveIntent.IntentId,
                        PayloadQuarantineReason.InterruptedBuild);
                byte[] marker = CreateQuarantineMarker(quarantine);
                SafeFileHandle source = null;
                SafeFileHandle destination = null;
                try
                {
                    source = NativeIo.TryOpenRelative(
                        owner.rootHandle,
                        quarantine.SourceLeafName,
                        true,
                        false);
                    destination = NativeIo.TryOpenRelative(
                        owner.rootHandle,
                        quarantine.QuarantineLeafName,
                        true,
                        false);
                    if ((source == null) == (destination == null))
                    {
                        throw new InvalidDataException(
                            "Payload quarantine requires exactly one of " +
                            "source or destination.");
                    }
                    SafeFileHandle actual =
                        source == null ? destination : source;
                    NativeIo.RequireIdentity(
                        actual,
                        quarantine.VolumeSerialNumber,
                        quarantine.RootFileId,
                        "Payload quarantine directory identity changed.");
                    RequireQuarantineTreeExact(quarantine, actual);
                    if (source != null)
                    {
                        NativeIo.RequireOptionalQuarantineMarker(
                            source,
                            marker);
                    }
                    else
                    {
                        NativeIo.RequireQuarantineMarker(
                            destination,
                            marker);
                    }
                }
                finally
                {
                    if (source != null)
                    {
                        source.Dispose();
                    }
                    if (destination != null)
                    {
                        destination.Dispose();
                    }
                }
            }

            private void ValidateStoredQuarantine(
                PayloadQuarantineCheckpoint quarantine)
            {
                if (quarantine.SourceKind !=
                    PayloadQuarantineSourceKind.PartialBuild)
                {
                    throw new NotSupportedException(
                        "The isolated temp backend only validates partial-" +
                        "build quarantines.");
                }
                using (SafeFileHandle directory = NativeIo.OpenRelative(
                    owner.rootHandle,
                    quarantine.QuarantineLeafName,
                    true,
                    false,
                    false))
                {
                    NativeIo.RequireIdentity(
                        directory,
                        quarantine.VolumeSerialNumber,
                        quarantine.RootFileId,
                        "Stored payload quarantine identity changed.");
                    NativeIo.RequireQuarantineMarker(
                        directory,
                        CreateQuarantineMarker(quarantine));
                    RequireQuarantineTreeExact(quarantine, directory);
                }
            }

            private void RequireQuarantineTreeExact(
                PayloadQuarantineCheckpoint quarantine,
                SafeFileHandle directory)
            {
                NativeIdentity identity = NativeIo.Identity(directory);
                var observed = new PayloadPartialTreeObservation
                {
                    SchemaVersion = 1,
                    BuildId = quarantine.SourceBuildId,
                    LeafName = quarantine.SourceLeafName,
                    Exists = true,
                    VolumeSerialNumber = identity.VolumeSerialNumber,
                    RootFileId = identity.FileId,
                    Entries = ObserveEntries(directory)
                };
                if (!String.Equals(
                        observed.InvariantDigest,
                        quarantine.PartialTreeInvariantDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Payload quarantine tree differs from its exact " +
                        "partial-build observation.");
                }
            }

            private void ValidateSealPhysicalShape(
                PayloadBuildWorkspaceCheckpoint checkpoint)
            {
                PayloadPartialTreeObservation partial =
                    checkpoint.ActivePartialTree;
                string destinationName = PayloadNamespaceNames.ForSlot(
                    PayloadDirectorySlot.Candidate,
                    checkpoint.TransactionId);
                byte[] marker = CreateOwnershipMarker(
                    checkpoint,
                    "SealCandidate",
                    destinationName,
                    -1,
                    true);
                SafeFileHandle source = null;
                SafeFileHandle destination = null;
                try
                {
                    source = NativeIo.TryOpenRelative(
                        owner.rootHandle,
                        partial.LeafName,
                        true,
                        false);
                    destination = NativeIo.TryOpenRelative(
                        owner.rootHandle,
                        destinationName,
                        true,
                        false);
                    if ((source == null) == (destination == null))
                    {
                        throw new InvalidDataException(
                            "Payload seal requires exactly one of source " +
                            "or destination.");
                    }
                    SafeFileHandle actual =
                        source == null ? destination : source;
                    NativeIo.RequireIdentity(
                        actual,
                        partial.VolumeSerialNumber,
                        partial.RootFileId,
                        "Payload seal directory identity changed.");
                    PayloadPartialTreeObservation observed =
                        ObservePartialHandle(partial, actual);
                    if (!String.Equals(
                            observed.InvariantDigest,
                            partial.InvariantDigest,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Payload seal content differs from checkpoint.");
                    }
                    if (source != null)
                    {
                        NativeIo.RequireOptionalSealMarker(source, marker);
                    }
                    else
                    {
                        NativeIo.RequireSealMarker(destination, marker);
                    }
                }
                finally
                {
                    if (source != null)
                    {
                        source.Dispose();
                    }
                    if (destination != null)
                    {
                        destination.Dispose();
                    }
                }
            }

            private static void AddCommittedLeaf(
                ISet<string> allowed,
                PayloadDirectoryCheckpoint directory)
            {
                if (directory != null)
                {
                    allowed.Add(
                        PayloadNamespaceNames.ForSlot(
                            directory.Slot,
                            directory.TransactionId));
                }
            }

            private void ValidateCommittedDirectory(
                PayloadDirectoryCheckpoint expected)
            {
                if (expected == null)
                {
                    return;
                }
                string leaf = PayloadNamespaceNames.ForSlot(
                    expected.Slot,
                    expected.TransactionId);
                using (SafeFileHandle directory =
                    NativeIo.OpenRelative(
                        owner.rootHandle,
                        leaf,
                        true,
                        false,
                        false))
                {
                    NativeIo.RequireIdentity(
                        directory,
                        expected.VolumeSerialNumber,
                        expected.FileId,
                        "Committed payload directory identity changed.");
                    List<PayloadTreeEntryCheckpoint> entries =
                        ObserveEntries(directory);
                    int files = 0;
                    long bytes = 0;
                    foreach (PayloadTreeEntryCheckpoint entry in entries)
                    {
                        if (!entry.IsDirectory)
                        {
                            ++files;
                            bytes = checked(bytes + entry.Length);
                        }
                    }
                    PayloadDirectoryCheckpoint observed =
                        expected.DeepClone();
                    observed.Entries = entries;
                    observed.FileCount = files;
                    observed.TotalBytes = bytes;
                    observed.Validate();
                    if (!String.Equals(
                            observed.InvariantDigest,
                            expected.InvariantDigest,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Committed payload directory differs from " +
                            "its durable checkpoint.");
                    }
                }
            }

            private void CreateBuildRoot(
                PayloadBuildWorkspaceCheckpoint checkpoint)
            {
                PayloadPartialTreeObservation partial =
                    checkpoint.ActivePartialTree;
                byte[] marker = CreateOwnershipMarker(
                    checkpoint,
                    "CreateRoot",
                    partial.LeafName,
                    -1,
                    true);
                SafeFileHandle handle = NativeIo.TryOpenRelative(
                    owner.rootHandle,
                    partial.LeafName,
                    true,
                    true);
                bool createdNew = handle == null;
                if (handle == null)
                {
                    handle = NativeIo.CreateRelativeExclusive(
                        owner.rootHandle,
                        partial.LeafName,
                        true,
                        true,
                        marker);
                }
                using (handle)
                {
                    NativeIo.RequireDirectory(
                        handle,
                        Path.Combine(
                            owner.canonicalRootPath,
                            partial.LeafName));
                    NativeIo.RequireOwnershipMarker(handle, marker);
                    if (NativeIo.Enumerate(handle).Count != 0)
                    {
                        throw new InvalidDataException(
                            "Owned payload build root is not empty.");
                    }
                    if (!createdNew && owner.testSeam != null)
                    {
                        owner.testSeam.BeforeOwnershipReplayFlush(
                            PayloadBuildStepKind.CreateRoot);
                    }
                    NativeIo.Flush(handle);
                }
            }

            private void CreateEntry(
                PayloadBuildWorkspaceCheckpoint checkpoint)
            {
                PayloadBuildStepIntent intent =
                    checkpoint.ActiveBuild.ActiveIntent;
                PayloadBuildEntryCheckpoint entry =
                    checkpoint.ActiveBuild.Entries[intent.EntryOrdinal];
                byte[] marker = CreateOwnershipMarker(
                    checkpoint,
                    "CreateEntry",
                    entry.RelativePath,
                    entry.Ordinal,
                    entry.IsDirectory);
                using (SafeFileHandle build = OpenBuild(checkpoint))
                {
                    string parentPath =
                        Path.GetDirectoryName(entry.RelativePath);
                    SafeFileHandle parent = OpenDirectoryPath(
                        build,
                        parentPath,
                        false);
                    try
                    {
                        string name = Path.GetFileName(entry.RelativePath);
                        SafeFileHandle created =
                            NativeIo.TryOpenRelative(
                                parent,
                                name,
                                entry.IsDirectory,
                                true);
                        bool createdNew = created == null;
                        if (created == null)
                        {
                            created = NativeIo.CreateRelativeExclusive(
                                parent,
                                name,
                                entry.IsDirectory,
                                true,
                                marker);
                        }
                        using (created)
                        {
                            NativeIo.RequireType(created, entry.IsDirectory);
                            if (!entry.IsDirectory)
                            {
                                NativeIo.RequireSingleLinkFile(created);
                                NativeIo.RequireEmptyFile(created);
                            }
                            NativeIo.RequireOwnershipMarker(created, marker);
                            if (entry.IsDirectory &&
                                NativeIo.Enumerate(created).Count != 0)
                            {
                                throw new InvalidDataException(
                                    "Owned payload directory is not empty.");
                            }
                            if (!createdNew && owner.testSeam != null)
                            {
                                owner.testSeam.BeforeOwnershipReplayFlush(
                                    PayloadBuildStepKind.CreateEntry);
                            }
                            NativeIo.Flush(created);
                        }
                    }
                    finally
                    {
                        if (parent != build)
                        {
                            parent.Dispose();
                        }
                    }
                }
            }

            private void RequireCreateRootAheadOrAbsent(
                PayloadBuildWorkspaceCheckpoint checkpoint)
            {
                string leaf = checkpoint.ActiveBuild.BuildLeafName;
                using (SafeFileHandle build = NativeIo.TryOpenRelative(
                    owner.rootHandle,
                    leaf,
                    true,
                    false))
                {
                    if (build == null)
                    {
                        return;
                    }
                    NativeIo.RequireOwnershipMarker(
                        build,
                        CreateOwnershipMarker(
                            checkpoint,
                            "CreateRoot",
                            leaf,
                            -1,
                            true));
                    if (NativeIo.Enumerate(build).Count != 0)
                    {
                        throw new InvalidDataException(
                            "Physical-ahead payload build root is not empty.");
                    }
                }
            }

            private bool IsExactCreateEntryAhead(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                PayloadPartialTreeObservation observed)
            {
                PayloadPartialTreeObservation expected =
                    checkpoint.ActivePartialTree;
                PayloadBuildStepIntent intent =
                    checkpoint.ActiveBuild.ActiveIntent;
                PayloadBuildEntryCheckpoint entry =
                    checkpoint.ActiveBuild.Entries[intent.EntryOrdinal];
                if (!observed.Exists ||
                    observed.VolumeSerialNumber !=
                        expected.VolumeSerialNumber ||
                    !String.Equals(
                        observed.RootFileId,
                        expected.RootFileId,
                        StringComparison.Ordinal) ||
                    observed.Entries.Count != expected.Entries.Count + 1)
                {
                    return false;
                }
                foreach (PayloadTreeEntryCheckpoint prior in
                    expected.Entries)
                {
                    PayloadTreeEntryCheckpoint current =
                        FindObservedEntry(
                            observed.Entries,
                            prior.RelativePath);
                    if (current == null ||
                        !String.Equals(
                            current.InvariantDigest,
                            prior.InvariantDigest,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
                PayloadTreeEntryCheckpoint added =
                    FindObservedEntry(
                        observed.Entries,
                        entry.RelativePath);
                if (added == null ||
                    added.IsDirectory != entry.IsDirectory ||
                    added.Length != 0 ||
                    (!entry.IsDirectory &&
                        !String.Equals(
                            added.Sha256,
                            "e3b0c44298fc1c149afbf4c8996fb924" +
                                "27ae41e4649b934ca495991b7852b855",
                            StringComparison.Ordinal)) ||
                    (entry.IsDirectory &&
                        !String.IsNullOrEmpty(added.Sha256)))
                {
                    return false;
                }
                using (SafeFileHandle build = OpenBuild(checkpoint))
                using (SafeFileHandle addedHandle = OpenEntry(
                    build,
                    entry.RelativePath,
                    entry.IsDirectory,
                    false,
                    false))
                {
                    NativeIo.RequireOwnershipMarker(
                        addedHandle,
                        CreateOwnershipMarker(
                            checkpoint,
                            "CreateEntry",
                            entry.RelativePath,
                            entry.Ordinal,
                            entry.IsDirectory));
                    if (!entry.IsDirectory)
                    {
                        NativeIo.RequireSingleLinkFile(addedHandle);
                    }
                    else if (NativeIo.Enumerate(addedHandle).Count != 0)
                    {
                        return false;
                    }
                }
                return true;
            }

            private static PayloadTreeEntryCheckpoint FindObservedEntry(
                IList<PayloadTreeEntryCheckpoint> entries,
                string relativePath)
            {
                foreach (PayloadTreeEntryCheckpoint entry in entries)
                {
                    if (String.Equals(
                        entry.RelativePath,
                        relativePath,
                        StringComparison.Ordinal))
                    {
                        return entry;
                    }
                }
                return null;
            }

            private static bool IsActiveIntent(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                PayloadBuildStepKind kind)
            {
                return checkpoint.ActiveBuild != null &&
                    checkpoint.ActiveBuild.ActiveIntent != null &&
                    checkpoint.ActiveBuild.ActiveIntent.Kind == kind;
            }

            private static byte[] CreateOwnershipMarker(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                string operation,
                string relativePath,
                int ordinal,
                bool directory)
            {
                PayloadCandidateBuildJournal build =
                    checkpoint.ActiveBuild;
                PayloadBuildStepIntent intent =
                    build == null ? null : build.ActiveIntent;
                if (intent == null)
                {
                    throw new InvalidOperationException(
                        "Payload ownership marker requires an active intent.");
                }
                var body = new List<byte>();
                AppendMarkerField(body, OwnershipMarkerDomain);
                AppendMarkerField(body, operation);
                AppendMarkerField(body, checkpoint.TransactionId);
                AppendMarkerField(body, build.BuildId);
                AppendMarkerField(body, intent.IntentId);
                AppendMarkerField(
                    body,
                    intent.JournalRevision.ToString(
                        CultureInfo.InvariantCulture));
                AppendMarkerField(body, build.InvariantDigest);
                AppendMarkerField(body, intent.InvariantDigest);
                AppendMarkerField(
                    body,
                    checkpoint.NamespaceRoot.InvariantDigest);
                AppendMarkerField(
                    body,
                    intent.ObservedPartialTreeInvariantDigest);
                AppendMarkerField(
                    body,
                    ordinal.ToString(CultureInfo.InvariantCulture));
                AppendMarkerField(body, relativePath);
                AppendMarkerField(
                    body,
                    directory ? "directory" : "file");
                using (SHA256 hash = SHA256.Create())
                {
                    body.AddRange(hash.ComputeHash(body.ToArray()));
                }
                return body.ToArray();
            }

            private static void AppendMarkerField(
                IList<byte> output,
                string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? String.Empty);
                byte[] length = BitConverter.GetBytes(bytes.Length);
                foreach (byte item in length)
                {
                    output.Add(item);
                }
                foreach (byte item in bytes)
                {
                    output.Add(item);
                }
            }

            private static PayloadQuarantineCheckpoint
                BuildProjectedQuarantine(
                    PayloadBuildWorkspaceCheckpoint checkpoint,
                    string quarantineId,
                    PayloadQuarantineReason reason)
            {
                PayloadCandidateBuildJournal build =
                    checkpoint.ActiveBuild;
                PayloadPartialTreeObservation partial =
                    checkpoint.ActivePartialTree;
                var quarantine = new PayloadQuarantineCheckpoint
                {
                    SchemaVersion = 1,
                    QuarantineId = quarantineId,
                    TransactionId = checkpoint.TransactionId,
                    RecoveryAuthorityInvariantDigest =
                        checkpoint.RecoveryAuthorityInvariantDigest,
                    NamespaceRootInvariantDigest =
                        checkpoint.NamespaceRoot.InvariantDigest,
                    SourceKind =
                        PayloadQuarantineSourceKind.PartialBuild,
                    SourceBuildId = build.BuildId,
                    QuarantineLeafName =
                        ".SBMS.quarantine." + quarantineId,
                    VolumeSerialNumber = partial.VolumeSerialNumber,
                    RootFileId = partial.RootFileId,
                    PartialTreeInvariantDigest =
                        partial.InvariantDigest,
                    Reason = reason,
                    SourceLeafName = partial.LeafName,
                    TargetManifestInvariantDigest =
                        build.TargetManifestInvariantDigest,
                    SourceReceiptInvariantDigest =
                        build.SourceReceiptInvariantDigest
                };
                quarantine.Validate();
                return quarantine;
            }

            private static byte[] CreateQuarantineMarker(
                PayloadQuarantineCheckpoint quarantine)
            {
                quarantine.Validate();
                var body = new List<byte>();
                AppendMarkerField(body, QuarantineMarkerDomain);
                AppendMarkerField(body, quarantine.TransactionId);
                AppendMarkerField(
                    body,
                    quarantine.RecoveryAuthorityInvariantDigest);
                AppendMarkerField(
                    body,
                    quarantine.NamespaceRootInvariantDigest);
                AppendMarkerField(body, quarantine.QuarantineId);
                AppendMarkerField(body, quarantine.SourceKind.ToString());
                AppendMarkerField(body, quarantine.SourceBuildId);
                AppendMarkerField(body, quarantine.SourceLeafName);
                AppendMarkerField(body, quarantine.QuarantineLeafName);
                AppendMarkerField(
                    body,
                    quarantine.VolumeSerialNumber.ToString(
                        "x16",
                        CultureInfo.InvariantCulture));
                AppendMarkerField(body, quarantine.RootFileId);
                AppendMarkerField(
                    body,
                    quarantine.PartialTreeInvariantDigest);
                AppendMarkerField(body, quarantine.Reason.ToString());
                AppendMarkerField(
                    body,
                    quarantine.TargetManifestInvariantDigest);
                AppendMarkerField(
                    body,
                    quarantine.SourceReceiptInvariantDigest);
                using (SHA256 sha = SHA256.Create())
                {
                    body.AddRange(sha.ComputeHash(body.ToArray()));
                }
                return body.ToArray();
            }

            private static byte[] CreatePurgeMarker(
                PayloadPurgeCheckpoint purge)
            {
                purge.Validate();
                var body = new List<byte>();
                AppendMarkerField(body, PurgeMarkerDomain);
                AppendMarkerField(body, purge.PurgeId);
                AppendMarkerField(body, purge.QuarantineId);
                AppendMarkerField(body, purge.TransactionId);
                AppendMarkerField(
                    body,
                    purge.RecoveryAuthorityInvariantDigest);
                AppendMarkerField(
                    body,
                    purge.NamespaceRootInvariantDigest);
                AppendMarkerField(
                    body,
                    purge.QuarantineInvariantDigest);
                AppendMarkerField(
                    body,
                    purge.VolumeSerialNumber.ToString(
                        "x16",
                        CultureInfo.InvariantCulture));
                AppendMarkerField(body, purge.RootFileId);
                using (SHA256 sha = SHA256.Create())
                {
                    body.AddRange(sha.ComputeHash(body.ToArray()));
                }
                return body.ToArray();
            }

            private void RewriteFile(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                PayloadBuildMutationPlan plan,
                ITrustedReleasePayloadSource source)
            {
                PayloadBuildStepIntent intent =
                    checkpoint.ActiveBuild.ActiveIntent;
                PayloadBuildEntryCheckpoint entry =
                    checkpoint.ActiveBuild.Entries[intent.EntryOrdinal];
                TargetPayloadEntry expected =
                    FindManifestEntry(plan.Manifest, entry.RelativePath);
                using (SafeFileHandle build = OpenBuild(checkpoint))
                using (SafeFileHandle file = OpenEntry(
                    build,
                    entry.RelativePath,
                    false,
                    false,
                    true))
                {
                    NativeIo.RequireIdentity(
                        file,
                        checkpoint.ActivePartialTree.VolumeSerialNumber,
                        entry.FileId,
                        "Payload rewrite target identity changed.");
                    NativeIo.RequireSingleLinkFile(file);
                    using (Stream input = source.OpenExact(expected))
                    using (SafeFileHandle streamHandle =
                        NativeIo.Duplicate(file))
                    using (var output = new FileStream(
                        streamHandle,
                        FileAccess.Write,
                        65536,
                        false))
                    using (SHA256 hash = SHA256.Create())
                    {
                        output.SetLength(0);
                        byte[] buffer = new byte[65536];
                        long length = 0;
                        int read;
                        while ((read = input.Read(
                            buffer,
                            0,
                            buffer.Length)) != 0)
                        {
                            output.Write(buffer, 0, read);
                            hash.TransformBlock(
                                buffer,
                                0,
                                read,
                                null,
                                0);
                            length = checked(length + read);
                        }
                        hash.TransformFinalBlock(
                            new byte[0],
                            0,
                            0);
                        string digest = NativeIo.Hex(hash.Hash);
                        if (length != expected.Length ||
                            !String.Equals(
                                digest,
                                expected.Sha256,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "Trusted payload source returned unexpected bytes.");
                        }
                    }
                    NativeIo.RequireIdentity(
                        file,
                        checkpoint.ActivePartialTree.VolumeSerialNumber,
                        entry.FileId,
                        "Payload rewrite target identity changed after write.");
                    NativeIo.RequireSingleLinkFile(file);
                }
            }

            private void FlushFile(
                PayloadBuildWorkspaceCheckpoint checkpoint)
            {
                PayloadBuildStepIntent intent =
                    checkpoint.ActiveBuild.ActiveIntent;
                PayloadBuildEntryCheckpoint entry =
                    checkpoint.ActiveBuild.Entries[intent.EntryOrdinal];
                using (SafeFileHandle build = OpenBuild(checkpoint))
                using (SafeFileHandle file = OpenEntry(
                    build,
                    entry.RelativePath,
                    false,
                    false,
                    true))
                {
                    NativeIo.RequireIdentity(
                        file,
                        checkpoint.ActivePartialTree.VolumeSerialNumber,
                        entry.FileId,
                        "Payload flush target identity changed.");
                    NativeIo.RequireSingleLinkFile(file);
                    using (SafeFileHandle streamHandle =
                        NativeIo.Duplicate(file))
                    using (var stream = new FileStream(
                        streamHandle,
                        FileAccess.Write,
                        4096,
                        false))
                    {
                        stream.Flush(true);
                    }
                    NativeIo.RequireIdentity(
                        file,
                        checkpoint.ActivePartialTree.VolumeSerialNumber,
                        entry.FileId,
                        "Payload flush target identity changed after flush.");
                    NativeIo.RequireSingleLinkFile(file);
                }
            }

            private PayloadBuildPhysicalResult SealCandidate(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                PayloadBuildMutationPlan plan)
            {
                string sourceName =
                    checkpoint.ActivePartialTree.LeafName;
                string destinationName = PayloadNamespaceNames.ForSlot(
                    PayloadDirectorySlot.Candidate,
                    checkpoint.TransactionId);
                byte[] marker = CreateOwnershipMarker(
                    checkpoint,
                    "SealCandidate",
                    destinationName,
                    -1,
                    true);
                SafeFileHandle source = null;
                SafeFileHandle destination = null;
                try
                {
                    source = NativeIo.TryOpenRelative(
                        owner.rootHandle,
                        sourceName,
                        true,
                        true);
                    destination = NativeIo.TryOpenRelative(
                        owner.rootHandle,
                        destinationName,
                        true,
                        false);
                    if ((source == null) == (destination == null))
                    {
                        throw new InvalidDataException(
                            "Payload seal requires exactly one of source or " +
                            "destination.");
                    }
                    if (source != null)
                    {
                        NativeIo.RequireIdentity(
                            source,
                            checkpoint.ActivePartialTree.VolumeSerialNumber,
                            checkpoint.ActivePartialTree.RootFileId,
                            "Payload seal source identity changed.");
                        NativeIo.PrepareSealMarker(source, marker);
                        if (owner.testSeam != null)
                        {
                            owner.testSeam.AfterSealMarkerFlushed();
                        }
                        NativeIo.RenameSameParent(
                            source,
                            destinationName);
                    }
                    else
                    {
                        NativeIo.RequireIdentity(
                            destination,
                            checkpoint.ActivePartialTree.VolumeSerialNumber,
                            checkpoint.ActivePartialTree.RootFileId,
                            "Payload seal destination identity changed.");
                        NativeIo.RequireSealMarker(destination, marker);
                    }
                }
                finally
                {
                    if (source != null)
                    {
                        source.Dispose();
                    }
                    if (destination != null)
                    {
                        destination.Dispose();
                    }
                }
                using (SafeFileHandle candidate = NativeIo.OpenRelative(
                    owner.rootHandle,
                    destinationName,
                    true,
                    false,
                    false))
                {
                    NativeIo.RequireIdentity(
                        candidate,
                        checkpoint.ActivePartialTree.VolumeSerialNumber,
                        checkpoint.ActivePartialTree.RootFileId,
                        "Payload candidate rename changed identity.");
                    NativeIo.RequireSealMarker(candidate, marker);
                    List<PayloadTreeEntryCheckpoint> entries =
                        ObserveEntries(candidate);
                    TargetPayloadManifest manifest = plan.Manifest;
                    int files = 0;
                    long bytes = 0;
                    foreach (PayloadTreeEntryCheckpoint entry in entries)
                    {
                        if (!entry.IsDirectory)
                        {
                            ++files;
                            bytes = checked(bytes + entry.Length);
                        }
                    }
                    var result = new PayloadDirectoryCheckpoint
                    {
                        TransactionId = checkpoint.TransactionId,
                        Slot = PayloadDirectorySlot.Candidate,
                        VolumeSerialNumber =
                            checkpoint.ActivePartialTree.VolumeSerialNumber,
                        FileId =
                            checkpoint.ActivePartialTree.RootFileId,
                        Release = new ReleaseIdentity(
                            manifest.Target.Version,
                            manifest.Target.PackageFingerprint),
                        ContentSetSha256 = manifest.ContentSetSha256,
                        ManifestInvariantDigest = manifest.InvariantDigest,
                        FileCount = files,
                        TotalBytes = bytes,
                        Entries = entries
                    };
                    return new PayloadBuildPhysicalResult(
                        PayloadBuildStepKind.SealCandidate,
                        null,
                        result,
                        null);
                }
            }

            private PayloadBuildPhysicalResult QuarantineBuild(
                PayloadBuildWorkspaceCheckpoint checkpoint,
                PayloadBuildMutationPlan plan)
            {
                PayloadQuarantineCheckpoint quarantine =
                    BuildProjectedQuarantine(
                        checkpoint,
                        plan.QuarantineId,
                        plan.QuarantineReason);
                byte[] marker = CreateQuarantineMarker(quarantine);
                SafeFileHandle source = null;
                SafeFileHandle destination = null;
                try
                {
                    source = NativeIo.TryOpenRelative(
                        owner.rootHandle,
                        quarantine.SourceLeafName,
                        true,
                        true);
                    destination = NativeIo.TryOpenRelative(
                        owner.rootHandle,
                        quarantine.QuarantineLeafName,
                        true,
                        false);
                    if ((source == null) == (destination == null))
                    {
                        throw new InvalidDataException(
                            "Payload quarantine requires exactly one of " +
                            "source or destination.");
                    }
                    if (source != null)
                    {
                        NativeIo.RequireIdentity(
                            source,
                            quarantine.VolumeSerialNumber,
                            quarantine.RootFileId,
                            "Payload quarantine source identity changed.");
                        RequireQuarantineTreeExact(quarantine, source);
                        NativeIo.PrepareQuarantineMarker(source, marker);
                        if (owner.testSeam != null)
                        {
                            owner.testSeam.
                                AfterQuarantineMarkerFlushed();
                        }
                        NativeIo.RenameSameParent(
                            source,
                            quarantine.QuarantineLeafName);
                    }
                    else
                    {
                        NativeIo.RequireIdentity(
                            destination,
                            quarantine.VolumeSerialNumber,
                            quarantine.RootFileId,
                            "Payload quarantine destination identity changed.");
                        NativeIo.RequireQuarantineMarker(
                            destination,
                            marker);
                        RequireQuarantineTreeExact(
                            quarantine,
                            destination);
                    }
                }
                finally
                {
                    if (source != null)
                    {
                        source.Dispose();
                    }
                    if (destination != null)
                    {
                        destination.Dispose();
                    }
                }
                using (SafeFileHandle quarantined = NativeIo.OpenRelative(
                    owner.rootHandle,
                    quarantine.QuarantineLeafName,
                    true,
                    false,
                    false))
                {
                    NativeIo.RequireIdentity(
                        quarantined,
                        quarantine.VolumeSerialNumber,
                        quarantine.RootFileId,
                        "Payload quarantine rename changed identity.");
                    NativeIo.RequireQuarantineMarker(
                        quarantined,
                        marker);
                    RequireQuarantineTreeExact(
                        quarantine,
                        quarantined);
                }
                return new PayloadBuildPhysicalResult(
                    PayloadBuildStepKind.QuarantineBuild,
                    null,
                    null,
                    quarantine);
            }

            private PayloadBuildPhysicalResult Partial(
                PayloadBuildStepKind step,
                PayloadBuildWorkspaceCheckpoint checkpoint)
            {
                PayloadPartialTreeObservation before =
                    checkpoint.ActivePartialTree;
                return new PayloadBuildPhysicalResult(
                    step,
                    ObservePartial(before.BuildId, before.LeafName),
                    null,
                    null);
            }

            private PayloadPartialTreeObservation ObservePartial(
                string buildId,
                string leafName)
            {
                SafeFileHandle build = NativeIo.TryOpenRelative(
                    owner.rootHandle,
                    leafName,
                    true,
                    false);
                if (build == null)
                {
                    return new PayloadPartialTreeObservation
                    {
                        SchemaVersion = 1,
                        BuildId = buildId,
                        LeafName = leafName,
                        Exists = false,
                        VolumeSerialNumber = 0,
                        RootFileId = String.Empty,
                        Entries = new List<PayloadTreeEntryCheckpoint>()
                    };
                }
                using (build)
                {
                    NativeIdentity identity = NativeIo.Identity(build);
                    return new PayloadPartialTreeObservation
                    {
                        SchemaVersion = 1,
                        BuildId = buildId,
                        LeafName = leafName,
                        Exists = true,
                        VolumeSerialNumber = identity.VolumeSerialNumber,
                        RootFileId = identity.FileId,
                        Entries = ObserveEntries(build)
                    };
                }
            }

            private PayloadPartialTreeObservation ObservePartialHandle(
                PayloadPartialTreeObservation expected,
                SafeFileHandle handle)
            {
                NativeIdentity identity = NativeIo.Identity(handle);
                return new PayloadPartialTreeObservation
                {
                    SchemaVersion = 1,
                    BuildId = expected.BuildId,
                    LeafName = expected.LeafName,
                    Exists = true,
                    VolumeSerialNumber = identity.VolumeSerialNumber,
                    RootFileId = identity.FileId,
                    Entries = ObserveEntries(handle)
                };
            }

            private List<PayloadTreeEntryCheckpoint> ObserveEntries(
                SafeFileHandle root)
            {
                var result = new List<PayloadTreeEntryCheckpoint>();
                ObserveDirectory(root, String.Empty, result);
                result.Sort(delegate(
                    PayloadTreeEntryCheckpoint first,
                    PayloadTreeEntryCheckpoint second)
                {
                    return StringComparer.Ordinal.Compare(
                        first.RelativePath,
                        second.RelativePath);
                });
                return result;
            }

            private void ObserveDirectory(
                SafeFileHandle directory,
                string prefix,
                IList<PayloadTreeEntryCheckpoint> result)
            {
                foreach (NativeDirectoryEntry child in
                    NativeIo.Enumerate(directory))
                {
                    string relative = String.IsNullOrEmpty(prefix)
                        ? child.Name
                        : prefix + "\\" + child.Name;
                    using (SafeFileHandle handle = NativeIo.OpenRelative(
                        directory,
                        child.Name,
                        child.IsDirectory,
                        false,
                        false))
                    {
                        NativeIo.RequireType(handle, child.IsDirectory);
                        NativeIdentity identity = NativeIo.Identity(handle);
                        if (child.IsDirectory)
                        {
                            result.Add(new PayloadTreeEntryCheckpoint
                            {
                                RelativePath = relative,
                                IsDirectory = true,
                                FileId = identity.FileId,
                                Length = 0,
                                Sha256 = String.Empty
                            });
                            ObserveDirectory(handle, relative, result);
                        }
                        else
                        {
                            NativeIo.RequireSingleLinkFile(handle);
                            long length;
                            string digest;
                            using (SafeFileHandle streamHandle =
                                NativeIo.Duplicate(handle))
                            using (var stream = new FileStream(
                                streamHandle,
                                FileAccess.Read,
                                65536,
                                false))
                            using (SHA256 hash = SHA256.Create())
                            {
                                length = stream.Length;
                                digest = NativeIo.Hex(
                                    hash.ComputeHash(stream));
                            }
                            NativeIo.RequireIdentity(
                                handle,
                                identity.VolumeSerialNumber,
                                identity.FileId,
                                "Payload file identity changed while hashing.");
                            NativeIo.RequireSingleLinkFile(handle);
                            result.Add(new PayloadTreeEntryCheckpoint
                            {
                                RelativePath = relative,
                                IsDirectory = false,
                                FileId = identity.FileId,
                                Length = length,
                                Sha256 = digest
                            });
                        }
                    }
                }
            }

            private SafeFileHandle OpenBuild(
                PayloadBuildWorkspaceCheckpoint checkpoint)
            {
                PayloadPartialTreeObservation partial =
                    checkpoint.ActivePartialTree;
                SafeFileHandle build = NativeIo.OpenRelative(
                    owner.rootHandle,
                    partial.LeafName,
                    true,
                    false,
                    false);
                try
                {
                    NativeIo.RequireIdentity(
                        build,
                        partial.VolumeSerialNumber,
                        partial.RootFileId,
                        "Payload build root identity changed.");
                    return build;
                }
                catch
                {
                    build.Dispose();
                    throw;
                }
            }

            private static SafeFileHandle OpenDirectoryPath(
                SafeFileHandle root,
                string relativePath,
                bool create)
            {
                if (String.IsNullOrEmpty(relativePath))
                {
                    return root;
                }
                SafeFileHandle current = root;
                bool owns = false;
                try
                {
                    foreach (string segment in relativePath.Split('\\'))
                    {
                        SafeFileHandle next = NativeIo.OpenRelative(
                            current,
                            segment,
                            true,
                            create,
                            create);
                        if (owns)
                        {
                            current.Dispose();
                        }
                        current = next;
                        owns = true;
                    }
                    return current;
                }
                catch
                {
                    if (owns)
                    {
                        current.Dispose();
                    }
                    throw;
                }
            }

            private static SafeFileHandle OpenEntry(
                SafeFileHandle build,
                string relativePath,
                bool directory,
                bool create,
                bool write)
            {
                string parentPath = Path.GetDirectoryName(relativePath);
                SafeFileHandle parent = OpenDirectoryPath(
                    build,
                    parentPath,
                    create);
                try
                {
                    return NativeIo.OpenRelative(
                        parent,
                        Path.GetFileName(relativePath),
                        directory,
                        create,
                        write);
                }
                finally
                {
                    if (parent != build)
                    {
                        parent.Dispose();
                    }
                }
            }

            private static TargetPayloadEntry FindManifestEntry(
                TargetPayloadManifest manifest,
                string relativePath)
            {
                foreach (TargetPayloadEntry entry in manifest.Content)
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
                    "Payload build file is absent from its manifest.");
            }
        }

        private sealed class NativeDirectoryEntry
        {
            internal string Name;
            internal bool IsDirectory;
        }

        private struct NativeIdentity
        {
            internal ulong VolumeSerialNumber;
            internal string FileId;
        }

        private static class NativeIo
        {
            private const string OwnershipEaName =
                "SBMS.Payload.Owner.v1";
            private const string SealEaName =
                "SBMS.Payload.Seal.v1";
            private const string QuarantineEaName =
                "SBMS.Payload.Quarantine.v1";
            private const string PurgeEaName =
                "SBMS.Payload.Purge.v1";
            private const uint FileReadData = 0x0001;
            private const uint FileWriteData = 0x0002;
            private const uint FileAppendData = 0x0004;
            private const uint FileReadEa = 0x0008;
            private const uint FileWriteEa = 0x0010;
            private const uint FileTraverse = 0x0020;
            private const uint FileReadAttributes = 0x0080;
            private const uint FileWriteAttributes = 0x0100;
            private const uint DeleteAccess = 0x00010000;
            private const uint ReadControl = 0x00020000;
            private const uint Synchronize = 0x00100000;
            private const uint ShareRead = 0x00000001;
            private const uint ShareWrite = 0x00000002;
            private const uint ShareDelete = 0x00000004;
            private const uint FileOpen = 0x00000001;
            private const uint FileCreate = 0x00000002;
            private const uint FileOpenIf = 0x00000003;
            private const uint FileDirectoryFile = 0x00000001;
            private const uint FileWriteThrough = 0x00000002;
            private const uint FileSynchronousIoNonAlert = 0x00000020;
            private const uint FileNonDirectoryFile = 0x00000040;
            private const uint FileOpenReparsePoint = 0x00200000;
            private const uint ObjCaseInsensitive = 0x00000040;
            private const uint ObjDontReparse = 0x00001000;
            private const uint FileAttributeReparsePoint = 0x00000400;
            private const uint FileAttributeDirectory = 0x00000010;
            private const int StatusNoMoreFiles =
                unchecked((int)0x80000006);
            private const int StatusNonexistentEaEntry =
                unchecked((int)0xC0000051);
            private const int StatusNoEasOnFile =
                unchecked((int)0xC0000052);
            private const int ErrorFileNotFound = 2;
            private const int ErrorPathNotFound = 3;

            internal static SafeFileHandle OpenAbsoluteDirectory(string path)
            {
                SafeFileHandle handle = CreateFile(
                    path,
                    FileReadData | FileTraverse | FileReadAttributes |
                        ReadControl | Synchronize,
                    ShareRead | ShareWrite | ShareDelete,
                    IntPtr.Zero,
                    3,
                    0x02200000,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    ThrowLastWin32("Unable to open isolated payload root.");
                }
                return handle;
            }

            internal static bool PathIsReparsePoint(string path)
            {
                uint attributes = GetFileAttributes(path);
                if (attributes == 0xffffffff)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorFileNotFound ||
                        error == ErrorPathNotFound)
                    {
                        return false;
                    }
                    throw new Win32Exception(error);
                }
                return (attributes & FileAttributeReparsePoint) != 0;
            }

            internal static SafeFileHandle OpenRelative(
                SafeFileHandle root,
                string name,
                bool directory,
                bool create,
                bool write)
            {
                ValidateName(name);
                uint access = FileReadData | FileReadEa |
                    FileReadAttributes |
                    ReadControl | Synchronize;
                if (directory)
                {
                    access |= FileTraverse | DeleteAccess;
                }
                if (write)
                {
                    access |= FileWriteData | FileAppendData |
                        FileWriteEa | FileWriteAttributes;
                }
                uint options = FileSynchronousIoNonAlert |
                    FileOpenReparsePoint |
                    (directory
                        ? FileDirectoryFile
                        : FileNonDirectoryFile);
                SafeFileHandle handle = OpenNative(
                    root,
                    name,
                    access,
                    create ? FileOpenIf : FileOpen,
                    options,
                    null);
                try
                {
                    RequireType(handle, directory);
                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            internal static SafeFileHandle OpenRelativeForPurge(
                SafeFileHandle root,
                string name,
                bool directory,
                bool writeEa)
            {
                ValidateName(name);
                uint access = FileReadData | FileReadEa |
                    FileReadAttributes | DeleteAccess |
                    ReadControl | Synchronize;
                if (directory)
                {
                    access |= FileTraverse;
                }
                if (writeEa)
                {
                    access |= FileWriteData | FileWriteEa;
                }
                SafeFileHandle handle = OpenNativeWithShare(
                    root,
                    name,
                    access,
                    FileOpen,
                    FileSynchronousIoNonAlert |
                    FileOpenReparsePoint |
                    (directory
                        ? FileDirectoryFile
                        : FileNonDirectoryFile),
                    null,
                    ShareRead | ShareWrite);
                try
                {
                    RequireType(handle, directory);
                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            internal static SafeFileHandle TryOpenRelativeForPurge(
                SafeFileHandle root,
                string name,
                bool directory,
                bool writeEa)
            {
                try
                {
                    return OpenRelativeForPurge(
                        root,
                        name,
                        directory,
                        writeEa);
                }
                catch (Win32Exception failure)
                {
                    if (failure.NativeErrorCode == ErrorFileNotFound ||
                        failure.NativeErrorCode == ErrorPathNotFound)
                    {
                        return null;
                    }
                    throw;
                }
            }

            private static SafeFileHandle OpenRelativeForIdentityCheck(
                SafeFileHandle root,
                string name,
                bool directory)
            {
                ValidateName(name);
                uint access = FileReadData | FileReadEa |
                    FileReadAttributes | ReadControl | Synchronize;
                if (directory)
                {
                    access |= FileTraverse;
                }
                SafeFileHandle handle = OpenNative(
                    root,
                    name,
                    access,
                    FileOpen,
                    FileSynchronousIoNonAlert |
                    FileOpenReparsePoint |
                    (directory
                        ? FileDirectoryFile
                        : FileNonDirectoryFile),
                    null);
                try
                {
                    RequireType(handle, directory);
                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            internal static SafeFileHandle CreateRelativeExclusive(
                SafeFileHandle root,
                string name,
                bool directory,
                bool write,
                byte[] ownershipMarker)
            {
                ValidateName(name);
                if (ownershipMarker == null ||
                    ownershipMarker.Length == 0)
                {
                    throw new ArgumentException(
                        "An ownership marker is required.",
                        "ownershipMarker");
                }
                uint access = FileReadData | FileReadEa | FileWriteEa |
                    FileReadAttributes |
                    ReadControl | Synchronize |
                    (directory
                        ? FileTraverse | DeleteAccess
                        : 0);
                if (write)
                {
                    access |= FileWriteData | FileAppendData |
                        FileWriteAttributes;
                }
                SafeFileHandle handle = OpenNative(
                    root,
                    name,
                    access,
                    FileCreate,
                    FileSynchronousIoNonAlert |
                    FileOpenReparsePoint |
                    (directory
                        ? FileDirectoryFile
                        : FileNonDirectoryFile),
                    BuildExtendedAttribute(
                        OwnershipEaName,
                        ownershipMarker));
                try
                {
                    RequireType(handle, directory);
                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            internal static void RequireOwnershipMarker(
                SafeFileHandle handle,
                byte[] expected)
            {
                byte[] actual = QueryExtendedAttribute(
                    handle,
                    OwnershipEaName);
                if (!BytesEqual(actual, expected))
                {
                    throw new InvalidDataException(
                        "Payload native ownership marker does not match " +
                        "the active durable intent.");
                }
            }

            internal static void RequireOptionalSealMarker(
                SafeFileHandle handle,
                byte[] expected)
            {
                byte[] actual = QueryExtendedAttribute(handle, SealEaName);
                if (actual != null && !BytesEqual(actual, expected))
                {
                    throw new InvalidDataException(
                        "Payload seal marker belongs to a different " +
                        "durable intent.");
                }
            }

            internal static void RequireSealMarker(
                SafeFileHandle handle,
                byte[] expected)
            {
                byte[] actual = QueryExtendedAttribute(handle, SealEaName);
                if (!BytesEqual(actual, expected))
                {
                    throw new InvalidDataException(
                        "Payload seal marker does not match the active " +
                        "durable intent.");
                }
            }

            internal static void PrepareSealMarker(
                SafeFileHandle handle,
                byte[] expected)
            {
                byte[] actual = QueryExtendedAttribute(handle, SealEaName);
                if (actual != null && !BytesEqual(actual, expected))
                {
                    throw new InvalidDataException(
                        "Payload seal marker belongs to a different " +
                        "durable intent.");
                }
                if (actual == null)
                {
                    SetExtendedAttribute(handle, SealEaName, expected);
                }
                RequireSealMarker(handle, expected);
                Flush(handle);
            }

            internal static void RequireOptionalQuarantineMarker(
                SafeFileHandle handle,
                byte[] expected)
            {
                byte[] actual = QueryExtendedAttribute(
                    handle,
                    QuarantineEaName);
                if (actual != null && !BytesEqual(actual, expected))
                {
                    throw new InvalidDataException(
                        "Payload quarantine marker belongs to a different " +
                        "durable intent.");
                }
            }

            internal static void RequireQuarantineMarker(
                SafeFileHandle handle,
                byte[] expected)
            {
                byte[] actual = QueryExtendedAttribute(
                    handle,
                    QuarantineEaName);
                if (!BytesEqual(actual, expected))
                {
                    throw new InvalidDataException(
                        "Payload quarantine marker does not match the " +
                        "durable quarantine record.");
                }
            }

            internal static void PrepareQuarantineMarker(
                SafeFileHandle handle,
                byte[] expected)
            {
                byte[] actual = QueryExtendedAttribute(
                    handle,
                    QuarantineEaName);
                if (actual != null && !BytesEqual(actual, expected))
                {
                    throw new InvalidDataException(
                        "Payload quarantine marker belongs to a different " +
                        "durable intent.");
                }
                if (actual == null)
                {
                    SetExtendedAttribute(
                        handle,
                        QuarantineEaName,
                        expected);
                }
                RequireQuarantineMarker(handle, expected);
                Flush(handle);
            }

            internal static bool HasPurgeMarker(SafeFileHandle handle)
            {
                return QueryExtendedAttribute(handle, PurgeEaName) != null;
            }

            internal static void RequirePurgeMarker(
                SafeFileHandle handle,
                byte[] expected)
            {
                byte[] actual = QueryExtendedAttribute(
                    handle,
                    PurgeEaName);
                if (!BytesEqual(actual, expected))
                {
                    throw new InvalidDataException(
                        "Payload purge marker does not match the active " +
                        "durable purge.");
                }
            }

            internal static void PreparePurgeMarker(
                SafeFileHandle handle,
                byte[] expected)
            {
                byte[] actual = QueryExtendedAttribute(
                    handle,
                    PurgeEaName);
                if (actual != null && !BytesEqual(actual, expected))
                {
                    throw new InvalidDataException(
                        "Payload purge marker belongs to a different " +
                        "durable purge.");
                }
                if (actual == null)
                {
                    SetExtendedAttribute(handle, PurgeEaName, expected);
                }
                RequirePurgeMarker(handle, expected);
                Flush(handle);
            }

            internal static void Flush(SafeFileHandle handle)
            {
                IoStatusBlock io;
                int status = NtFlushBuffersFile(handle, out io);
                if (status < 0)
                {
                    throw new Win32Exception(
                        unchecked((int)RtlNtStatusToDosError(status)));
                }
            }

            internal static SafeFileHandle TryOpenRelative(
                SafeFileHandle root,
                string name,
                bool directory,
                bool write)
            {
                try
                {
                    return OpenRelative(
                        root,
                        name,
                        directory,
                        false,
                        write);
                }
                catch (Win32Exception failure)
                {
                    if (failure.NativeErrorCode == ErrorFileNotFound ||
                        failure.NativeErrorCode == ErrorPathNotFound)
                    {
                        return null;
                    }
                    throw;
                }
            }

            internal static IList<NativeDirectoryEntry> Enumerate(
                SafeFileHandle directory)
            {
                var result = new List<NativeDirectoryEntry>();
                IntPtr buffer = Marshal.AllocHGlobal(65536);
                try
                {
                    bool restart = true;
                    while (true)
                    {
                        IoStatusBlock io;
                        int status = NtQueryDirectoryFile(
                            directory,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            out io,
                            buffer,
                            65536,
                            1,
                            false,
                            IntPtr.Zero,
                            restart);
                        restart = false;
                        if (status == StatusNoMoreFiles)
                        {
                            break;
                        }
                        if (status < 0)
                        {
                            throw new Win32Exception(
                                unchecked((int)RtlNtStatusToDosError(status)));
                        }
                        int available = checked((int)io.Information.ToInt64());
                        if (available <= 0 || available > 65536)
                        {
                            throw new InvalidDataException(
                                "Native directory enumeration returned an " +
                                "invalid byte count.");
                        }
                        int offset = 0;
                        while (offset < available)
                        {
                            int remaining = available - offset;
                            if (remaining < 64)
                            {
                                throw new InvalidDataException(
                                    "Native directory enumeration record " +
                                    "was truncated.");
                            }
                            int next = Marshal.ReadInt32(buffer, offset);
                            if (next != 0 &&
                                (next < 64 ||
                                 (next & 7) != 0 ||
                                 next > remaining))
                            {
                                throw new InvalidDataException(
                                    "Native directory enumeration offset " +
                                    "was malformed.");
                            }
                            uint attributes = unchecked((uint)
                                Marshal.ReadInt32(buffer, offset + 56));
                            int nameBytes =
                                Marshal.ReadInt32(buffer, offset + 60);
                            int recordLength =
                                next == 0 ? remaining : next;
                            if (nameBytes < 0 || (nameBytes & 1) != 0 ||
                                nameBytes > recordLength - 64)
                            {
                                throw new InvalidDataException(
                                    "Native directory enumeration was malformed.");
                            }
                            string name = Marshal.PtrToStringUni(
                                IntPtr.Add(buffer, offset + 64),
                                nameBytes / 2);
                            if (name != "." && name != "..")
                            {
                                if ((attributes &
                                    FileAttributeReparsePoint) != 0)
                                {
                                    throw new InvalidDataException(
                                        "Payload tree contains a reparse point.");
                                }
                                ValidateName(name);
                                result.Add(new NativeDirectoryEntry
                                {
                                    Name = name,
                                    IsDirectory =
                                        (attributes &
                                            FileAttributeDirectory) != 0
                                });
                            }
                            if (next == 0)
                            {
                                break;
                            }
                            offset += next;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
                return result;
            }

            internal static void RequireEnumeratedEntry(
                SafeFileHandle parent,
                NativeDirectoryEntry expected,
                NativeIdentity openedIdentity)
            {
                bool found = false;
                foreach (NativeDirectoryEntry current in Enumerate(parent))
                {
                    if (String.Equals(
                            current.Name,
                            expected.Name,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (found ||
                            current.IsDirectory != expected.IsDirectory)
                        {
                            throw new InvalidDataException(
                                "Payload purge entry changed after open.");
                        }
                        found = true;
                    }
                }
                if (!found ||
                    openedIdentity.VolumeSerialNumber == 0 ||
                    String.IsNullOrEmpty(openedIdentity.FileId))
                {
                    throw new InvalidDataException(
                        "Payload purge entry identity is no longer bound " +
                        "to the enumerated name.");
                }
                using (SafeFileHandle rebound =
                    OpenRelativeForIdentityCheck(
                        parent,
                        expected.Name,
                        expected.IsDirectory))
                {
                    NativeIdentity reboundIdentity = Identity(rebound);
                    if (reboundIdentity.VolumeSerialNumber !=
                            openedIdentity.VolumeSerialNumber ||
                        !String.Equals(
                            reboundIdentity.FileId,
                            openedIdentity.FileId,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Payload purge entry FileId changed after " +
                            "enumeration and open.");
                    }
                }
            }

            internal static void RequireRelativeAbsent(
                SafeFileHandle parent,
                string name)
            {
                ValidateName(name);
                foreach (NativeDirectoryEntry entry in Enumerate(parent))
                {
                    if (String.Equals(
                            entry.Name,
                            name,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Payload purge deletion was not freshly " +
                            "observed absent.");
                    }
                }
            }

            internal static void MarkDeleteOnClose(
                SafeFileHandle handle)
            {
                IntPtr buffer = Marshal.AllocHGlobal(1);
                try
                {
                    Marshal.WriteByte(buffer, 0, 1);
                    IoStatusBlock io;
                    int status = NtSetInformationFile(
                        handle,
                        out io,
                        buffer,
                        1,
                        13);
                    if (status < 0)
                    {
                        throw new Win32Exception(
                            unchecked((int)RtlNtStatusToDosError(status)));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            internal static void RenameSameParent(
                SafeFileHandle source,
                string destinationName)
            {
                ValidateName(destinationName);
                byte[] name = Encoding.Unicode.GetBytes(destinationName);
                int rootOffset = IntPtr.Size == 8 ? 8 : 4;
                int lengthOffset = rootOffset + IntPtr.Size;
                int nameOffset = lengthOffset + 4;
                IntPtr buffer = Marshal.AllocHGlobal(
                    nameOffset + name.Length);
                try
                {
                    for (int index = 0;
                        index < nameOffset + name.Length;
                        ++index)
                    {
                        Marshal.WriteByte(buffer, index, 0);
                    }
                    Marshal.WriteInt32(buffer, 0, 0);
                    Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
                    Marshal.WriteInt32(buffer, lengthOffset, name.Length);
                    Marshal.Copy(
                        name,
                        0,
                        IntPtr.Add(buffer, nameOffset),
                        name.Length);
                    IoStatusBlock io;
                    int status = NtSetInformationFile(
                        source,
                        out io,
                        buffer,
                        unchecked((uint)(nameOffset + name.Length)),
                        10);
                    if (status < 0)
                    {
                        throw new Win32Exception(
                            unchecked((int)RtlNtStatusToDosError(status)));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            internal static void RequireDirectory(
                SafeFileHandle handle,
                string expectedPath)
            {
                RequireType(handle, true);
                string final = GetFinalPath(handle);
                if (!String.Equals(
                        NormalizePath(expectedPath),
                        NormalizePath(final),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Isolated payload root resolved unexpectedly.");
                }
            }

            internal static void RequireType(
                SafeFileHandle handle,
                bool directory)
            {
                FileAttributeTagInfo attributes;
                if (!GetFileInformationByHandleEx(
                    handle,
                    9,
                    out attributes,
                    Marshal.SizeOf(typeof(FileAttributeTagInfo))))
                {
                    ThrowLastWin32(
                        "Unable to inspect payload entry attributes.");
                }
                if ((attributes.FileAttributes &
                        FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Payload entry is a reparse point.");
                }
                FileStandardInfo standard;
                if (!GetFileInformationByHandleEx(
                    handle,
                    1,
                    out standard,
                    Marshal.SizeOf(typeof(FileStandardInfo))))
                {
                    ThrowLastWin32(
                        "Unable to inspect payload entry type.");
                }
                if (standard.Directory != directory)
                {
                    throw new InvalidDataException(
                        "Payload entry type changed.");
                }
            }

            internal static void RequireSingleLinkFile(
                SafeFileHandle handle)
            {
                FileStandardInfo standard;
                if (!GetFileInformationByHandleEx(
                    handle,
                    1,
                    out standard,
                    Marshal.SizeOf(typeof(FileStandardInfo))))
                {
                    ThrowLastWin32(
                        "Unable to inspect payload file links.");
                }
                if (standard.Directory ||
                    standard.NumberOfLinks != 1)
                {
                    throw new InvalidDataException(
                        "Payload files must be single-link regular files.");
                }
            }

            internal static void RequireEmptyFile(
                SafeFileHandle handle)
            {
                FileStandardInfo standard;
                if (!GetFileInformationByHandleEx(
                    handle,
                    1,
                    out standard,
                    Marshal.SizeOf(typeof(FileStandardInfo))))
                {
                    ThrowLastWin32(
                        "Unable to inspect payload file length.");
                }
                if (standard.Directory || standard.EndOfFile != 0)
                {
                    throw new InvalidDataException(
                        "New payload files must remain empty until rewrite.");
                }
            }

            internal static NativeIdentity Identity(
                SafeFileHandle handle)
            {
                FileIdInfo info;
                if (!GetFileInformationByHandleEx(
                    handle,
                    18,
                    out info,
                    Marshal.SizeOf(typeof(FileIdInfo))))
                {
                    ThrowLastWin32(
                        "Unable to read payload FileId.");
                }
                return new NativeIdentity
                {
                    VolumeSerialNumber = info.VolumeSerialNumber,
                    FileId = Hex(info.FileId.Identifier)
                };
            }

            internal static void RequireIdentity(
                SafeFileHandle handle,
                ulong volume,
                string fileId,
                string message)
            {
                NativeIdentity actual = Identity(handle);
                if (actual.VolumeSerialNumber != volume ||
                    !String.Equals(
                        actual.FileId,
                        fileId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(message);
                }
            }

            internal static SafeFileHandle Duplicate(
                SafeFileHandle source)
            {
                bool sourceAddRef = false;
                try
                {
                    source.DangerousAddRef(ref sourceAddRef);
                    IntPtr process = GetCurrentProcess();
                    SafeFileHandle duplicate;
                    if (!DuplicateHandle(
                            process,
                            source.DangerousGetHandle(),
                            process,
                            out duplicate,
                            0,
                            false,
                            0x00000002))
                    {
                        ThrowLastWin32(
                            "Unable to duplicate a payload file handle.");
                    }
                    return duplicate;
                }
                finally
                {
                    if (sourceAddRef)
                    {
                        source.DangerousRelease();
                    }
                }
            }

            internal static string Hex(byte[] bytes)
            {
                var text = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes)
                {
                    text.Append(value.ToString(
                        "x2",
                        CultureInfo.InvariantCulture));
                }
                return text.ToString();
            }

            private static byte[] BuildExtendedAttribute(
                string attributeName,
                byte[] value)
            {
                byte[] name = Encoding.ASCII.GetBytes(attributeName);
                if (name.Length > Byte.MaxValue ||
                    value.Length > UInt16.MaxValue)
                {
                    throw new InvalidOperationException(
                        "Payload ownership marker exceeds NTFS EA limits.");
                }
                var result = new byte[
                    checked(8 + name.Length + 1 + value.Length)];
                result[4] = 0;
                result[5] = checked((byte)name.Length);
                Buffer.BlockCopy(
                    BitConverter.GetBytes(checked((ushort)value.Length)),
                    0,
                    result,
                    6,
                    2);
                Buffer.BlockCopy(name, 0, result, 8, name.Length);
                Buffer.BlockCopy(
                    value,
                    0,
                    result,
                    8 + name.Length + 1,
                    value.Length);
                return result;
            }

            private static void SetExtendedAttribute(
                SafeFileHandle handle,
                string attributeName,
                byte[] value)
            {
                byte[] buffer = BuildExtendedAttribute(
                    attributeName,
                    value);
                GCHandle pinned = GCHandle.Alloc(
                    buffer,
                    GCHandleType.Pinned);
                try
                {
                    IoStatusBlock io;
                    int status = NtSetEaFile(
                        handle,
                        out io,
                        pinned.AddrOfPinnedObject(),
                        unchecked((uint)buffer.Length));
                    if (status < 0)
                    {
                        throw new Win32Exception(
                            unchecked((int)RtlNtStatusToDosError(status)));
                    }
                }
                finally
                {
                    pinned.Free();
                }
            }

            private static byte[] QueryExtendedAttribute(
                SafeFileHandle handle,
                string expectedName)
            {
                IntPtr output = Marshal.AllocHGlobal(65536);
                try
                {
                    IoStatusBlock io;
                    int status = NtQueryEaFile(
                        handle,
                        out io,
                        output,
                        65536,
                        false,
                        IntPtr.Zero,
                        0,
                        IntPtr.Zero,
                        true);
                    if (status == StatusNoEasOnFile)
                    {
                        return null;
                    }
                    if (status < 0)
                    {
                        throw new Win32Exception(
                            unchecked((int)RtlNtStatusToDosError(status)));
                    }
                    int available =
                        checked((int)io.Information.ToInt64());
                    if (available <= 0 || available > 65536)
                    {
                        throw new InvalidDataException(
                            "Payload ownership EA response is malformed.");
                    }
                    byte[] found = null;
                    int offset = 0;
                    while (offset < available)
                    {
                        int remaining = available - offset;
                        if (remaining < 9)
                        {
                            throw new InvalidDataException(
                                "Payload EA response was truncated.");
                        }
                        int next = Marshal.ReadInt32(output, offset);
                        int nameLength = Marshal.ReadByte(
                            output,
                            offset + 5);
                        int valueLength = unchecked((ushort)
                            Marshal.ReadInt16(output, offset + 6));
                        int required = checked(
                            8 + nameLength + 1 + valueLength);
                        int recordLength =
                            next == 0 ? remaining : next;
                        if (required > recordLength ||
                            (next != 0 &&
                                (next < required ||
                                 (next & 3) != 0 ||
                                 next > remaining)) ||
                            Marshal.ReadByte(
                                output,
                                offset + 8 + nameLength) != 0)
                        {
                            throw new InvalidDataException(
                                "Payload EA response was malformed.");
                        }
                        var returnedName = new byte[nameLength];
                        Marshal.Copy(
                            IntPtr.Add(output, offset + 8),
                            returnedName,
                            0,
                            returnedName.Length);
                        if (String.Equals(
                                Encoding.ASCII.GetString(returnedName),
                                expectedName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            if (found != null)
                            {
                                throw new InvalidDataException(
                                    "Payload EA response contains duplicate " +
                                    "marker names.");
                            }
                            found = new byte[valueLength];
                            Marshal.Copy(
                                IntPtr.Add(
                                    output,
                                    offset + 8 + nameLength + 1),
                                found,
                                0,
                                found.Length);
                        }
                        if (next == 0)
                        {
                            if (required != remaining)
                            {
                                throw new InvalidDataException(
                                    "Payload EA response has trailing data.");
                            }
                            break;
                        }
                        offset += next;
                    }
                    return found;
                }
                finally
                {
                    Marshal.FreeHGlobal(output);
                }
            }

            private static bool BytesEqual(byte[] first, byte[] second)
            {
                if (first == null || second == null ||
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

            private static SafeFileHandle OpenNative(
                SafeFileHandle root,
                string name,
                uint access,
                uint disposition,
                uint options,
                byte[] extendedAttributes)
            {
                return OpenNativeWithShare(
                    root,
                    name,
                    access,
                    disposition,
                    options,
                    extendedAttributes,
                    ShareRead | ShareWrite | ShareDelete);
            }

            private static SafeFileHandle OpenNativeWithShare(
                SafeFileHandle root,
                string name,
                uint access,
                uint disposition,
                uint options,
                byte[] extendedAttributes,
                uint shareAccess)
            {
                IntPtr nameBuffer = IntPtr.Zero;
                IntPtr unicodeBuffer = IntPtr.Zero;
                bool rootAddRef = false;
                GCHandle eaPin = new GCHandle();
                bool eaPinned = false;
                try
                {
                    root.DangerousAddRef(ref rootAddRef);
                    nameBuffer = Marshal.StringToHGlobalUni(name);
                    var unicode = new UnicodeString
                    {
                        Length = checked((ushort)(name.Length * 2)),
                        MaximumLength =
                            checked((ushort)((name.Length + 1) * 2)),
                        Buffer = nameBuffer
                    };
                    unicodeBuffer = Marshal.AllocHGlobal(
                        Marshal.SizeOf(typeof(UnicodeString)));
                    Marshal.StructureToPtr(
                        unicode,
                        unicodeBuffer,
                        false);
                    var attributes = new ObjectAttributes
                    {
                        Length =
                            Marshal.SizeOf(typeof(ObjectAttributes)),
                        RootDirectory = root.DangerousGetHandle(),
                        ObjectName = unicodeBuffer,
                        Attributes =
                            ObjCaseInsensitive | ObjDontReparse,
                        SecurityDescriptor = IntPtr.Zero,
                        SecurityQualityOfService = IntPtr.Zero
                    };
                    IntPtr eaBuffer = IntPtr.Zero;
                    uint eaLength = 0;
                    if (extendedAttributes != null)
                    {
                        eaPin = GCHandle.Alloc(
                            extendedAttributes,
                            GCHandleType.Pinned);
                        eaPinned = true;
                        eaBuffer = eaPin.AddrOfPinnedObject();
                        eaLength =
                            checked((uint)extendedAttributes.Length);
                    }
                    IoStatusBlock io;
                    SafeFileHandle handle;
                    int status = NtCreateFile(
                        out handle,
                        access,
                        ref attributes,
                        out io,
                        IntPtr.Zero,
                        0x00000080,
                        shareAccess,
                        disposition,
                        options,
                        eaBuffer,
                        eaLength);
                    if (status < 0)
                    {
                        if (handle != null)
                        {
                            handle.Dispose();
                        }
                        throw new Win32Exception(
                            unchecked((int)RtlNtStatusToDosError(status)));
                    }
                    return handle;
                }
                finally
                {
                    if (eaPinned)
                    {
                        eaPin.Free();
                    }
                    if (unicodeBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(unicodeBuffer);
                    }
                    if (nameBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(nameBuffer);
                    }
                    if (rootAddRef)
                    {
                        root.DangerousRelease();
                    }
                }
            }

            private static void ValidateName(string name)
            {
                if (String.IsNullOrWhiteSpace(name) ||
                    name == "." || name == ".." ||
                    name.IndexOf('\\') >= 0 ||
                    name.IndexOf('/') >= 0 ||
                    name.IndexOf(':') >= 0 ||
                    name.IndexOfAny(
                        Path.GetInvalidFileNameChars()) >= 0)
                {
                    throw new InvalidDataException(
                        "Payload path contains an unsafe component.");
                }
            }

            private static string GetFinalPath(SafeFileHandle handle)
            {
                var buffer = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    buffer.Capacity,
                    0);
                if (length == 0 || length >= buffer.Capacity)
                {
                    ThrowLastWin32(
                        "Unable to resolve payload root handle.");
                }
                return buffer.ToString();
            }

            private static string NormalizePath(string value)
            {
                string path = value;
                if (path.StartsWith(
                    @"\\?\",
                    StringComparison.Ordinal))
                {
                    path = path.Substring(4);
                }
                return path.TrimEnd('\\', '/');
            }

            private static void ThrowLastWin32(string message)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    message);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct UnicodeString
            {
                internal ushort Length;
                internal ushort MaximumLength;
                internal IntPtr Buffer;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ObjectAttributes
            {
                internal int Length;
                internal IntPtr RootDirectory;
                internal IntPtr ObjectName;
                internal uint Attributes;
                internal IntPtr SecurityDescriptor;
                internal IntPtr SecurityQualityOfService;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IoStatusBlock
            {
                internal IntPtr Status;
                internal IntPtr Information;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct FileAttributeTagInfo
            {
                internal uint FileAttributes;
                internal uint ReparseTag;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct FileStandardInfo
            {
                internal long AllocationSize;
                internal long EndOfFile;
                internal uint NumberOfLinks;
                [MarshalAs(UnmanagedType.U1)]
                internal bool DeletePending;
                [MarshalAs(UnmanagedType.U1)]
                internal bool Directory;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct FileIdInfo
            {
                internal ulong VolumeSerialNumber;
                internal FileId128 FileId;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct FileId128
            {
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
                internal byte[] Identifier;
            }

            [DllImport("ntdll.dll")]
            private static extern int NtCreateFile(
                out SafeFileHandle fileHandle,
                uint desiredAccess,
                ref ObjectAttributes objectAttributes,
                out IoStatusBlock ioStatusBlock,
                IntPtr allocationSize,
                uint fileAttributes,
                uint shareAccess,
                uint createDisposition,
                uint createOptions,
                IntPtr eaBuffer,
                uint eaLength);

            [DllImport("ntdll.dll")]
            private static extern uint RtlNtStatusToDosError(int status);

            [DllImport("ntdll.dll")]
            private static extern int NtSetInformationFile(
                SafeFileHandle fileHandle,
                out IoStatusBlock ioStatusBlock,
                IntPtr fileInformation,
                uint length,
                int fileInformationClass);

            [DllImport("ntdll.dll")]
            private static extern int NtQueryEaFile(
                SafeFileHandle fileHandle,
                out IoStatusBlock ioStatusBlock,
                IntPtr buffer,
                uint length,
                [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
                IntPtr eaList,
                uint eaListLength,
                IntPtr eaIndex,
                [MarshalAs(UnmanagedType.U1)] bool restartScan);

            [DllImport("ntdll.dll")]
            private static extern int NtSetEaFile(
                SafeFileHandle fileHandle,
                out IoStatusBlock ioStatusBlock,
                IntPtr buffer,
                uint length);

            [DllImport("ntdll.dll")]
            private static extern int NtFlushBuffersFile(
                SafeFileHandle fileHandle,
                out IoStatusBlock ioStatusBlock);

            [DllImport("ntdll.dll")]
            private static extern int NtQueryDirectoryFile(
                SafeFileHandle fileHandle,
                IntPtr eventHandle,
                IntPtr apcRoutine,
                IntPtr apcContext,
                out IoStatusBlock ioStatusBlock,
                IntPtr fileInformation,
                uint length,
                int fileInformationClass,
                [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
                IntPtr fileName,
                [MarshalAs(UnmanagedType.U1)] bool restartScan);

            [DllImport(
                "kernel32.dll",
                CharSet = CharSet.Unicode,
                SetLastError = true)]
            private static extern uint GetFileAttributes(string fileName);

            [DllImport(
                "kernel32.dll",
                CharSet = CharSet.Unicode,
                SetLastError = true)]
            private static extern SafeFileHandle CreateFile(
                string fileName,
                uint desiredAccess,
                uint shareMode,
                IntPtr securityAttributes,
                uint creationDisposition,
                uint flagsAndAttributes,
                IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetFileInformationByHandleEx(
                SafeFileHandle file,
                int informationClass,
                out FileAttributeTagInfo information,
                int bufferSize);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetFileInformationByHandleEx(
                SafeFileHandle file,
                int informationClass,
                out FileStandardInfo information,
                int bufferSize);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetFileInformationByHandleEx(
                SafeFileHandle file,
                int informationClass,
                out FileIdInfo information,
                int bufferSize);

            [DllImport(
                "kernel32.dll",
                CharSet = CharSet.Unicode,
                SetLastError = true)]
            private static extern uint GetFinalPathNameByHandle(
                SafeFileHandle file,
                StringBuilder path,
                int pathLength,
                uint flags);

            [DllImport("kernel32.dll")]
            private static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool DuplicateHandle(
                IntPtr sourceProcess,
                IntPtr sourceHandle,
                IntPtr targetProcess,
                out SafeFileHandle targetHandle,
                uint desiredAccess,
                [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
                uint options);
        }
    }
}
