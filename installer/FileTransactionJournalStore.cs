using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace SBMSSetup
{
    internal interface IInstallerProgramDataPathProvider
    {
        string GetCommonApplicationDataPath();
    }

    internal sealed class EnvironmentInstallerProgramDataPathProvider
        : IInstallerProgramDataPathProvider
    {
        public string GetCommonApplicationDataPath()
        {
            return Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
        }
    }

    internal interface IInstallerJournalAclPolicy
    {
        void PrepareAndVerify(
            string commonApplicationDataRoot,
            string installerStateRoot,
            bool createIfMissing);
    }

    internal sealed class InstallerJournalAccessRule
    {
        internal SecurityIdentifier Identity;
        internal FileSystemRights Rights;
        internal InheritanceFlags InheritanceFlags;
        internal PropagationFlags PropagationFlags;
        internal AccessControlType AccessControlType;
        internal bool IsInherited;
    }

    internal sealed class InstallerJournalPathMetadata
    {
        internal bool Exists;
        internal bool IsDirectory;
        internal bool IsReparsePoint;
        internal bool AccessRulesProtected;
        internal SecurityIdentifier Owner;
        internal readonly List<InstallerJournalAccessRule> AccessRules =
            new List<InstallerJournalAccessRule>();
    }

    internal interface IInstallerJournalPathInspector
    {
        InstallerJournalPathMetadata Inspect(
            string path,
            bool includeSecurity);
    }

    internal sealed class WindowsInstallerJournalPathInspector
        : IInstallerJournalPathInspector
    {
        public InstallerJournalPathMetadata Inspect(
            string path,
            bool includeSecurity)
        {
            bool fileExists = File.Exists(path);
            bool directoryExists = Directory.Exists(path);
            var result = new InstallerJournalPathMetadata
            {
                Exists = fileExists || directoryExists,
                IsDirectory = directoryExists
            };
            if (!result.Exists)
            {
                return result;
            }

            FileAttributes attributes = File.GetAttributes(path);
            result.IsReparsePoint =
                (attributes & FileAttributes.ReparsePoint) != 0;
            if (!includeSecurity || !directoryExists)
            {
                return result;
            }

            DirectorySecurity security =
                new DirectoryInfo(path).GetAccessControl(
                    AccessControlSections.Access |
                    AccessControlSections.Owner);
            result.AccessRulesProtected = security.AreAccessRulesProtected;
            result.Owner =
                security.GetOwner(typeof(SecurityIdentifier))
                    as SecurityIdentifier;
            AuthorizationRuleCollection rules = security.GetAccessRules(
                true,
                true,
                typeof(SecurityIdentifier));
            foreach (AuthorizationRule authorizationRule in rules)
            {
                FileSystemAccessRule rule =
                    authorizationRule as FileSystemAccessRule;
                if (rule == null)
                {
                    throw new UnauthorizedAccessException(
                        "Installer state root has an unreadable ACL entry.");
                }
                result.AccessRules.Add(
                    new InstallerJournalAccessRule
                    {
                        Identity =
                            rule.IdentityReference as SecurityIdentifier,
                        Rights = rule.FileSystemRights,
                        InheritanceFlags = rule.InheritanceFlags,
                        PropagationFlags = rule.PropagationFlags,
                        AccessControlType = rule.AccessControlType,
                        IsInherited = rule.IsInherited
                    });
            }
            return result;
        }
    }

    internal sealed class WindowsInstallerJournalAclPolicy
        : IInstallerJournalAclPolicy
    {
        private static readonly SecurityIdentifier Administrators =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier LocalSystem =
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private readonly IInstallerJournalPathInspector inspector;

        internal WindowsInstallerJournalAclPolicy()
            : this(new WindowsInstallerJournalPathInspector())
        {
        }

        internal WindowsInstallerJournalAclPolicy(
            IInstallerJournalPathInspector inspector)
        {
            if (inspector == null)
            {
                throw new ArgumentNullException("inspector");
            }
            this.inspector = inspector;
        }

        public void PrepareAndVerify(
            string commonApplicationDataRoot,
            string installerStateRoot,
            bool createIfMissing)
        {
            if (String.IsNullOrWhiteSpace(commonApplicationDataRoot) ||
                !Path.IsPathRooted(commonApplicationDataRoot) ||
                String.IsNullOrWhiteSpace(installerStateRoot) ||
                !Path.IsPathRooted(installerStateRoot))
            {
                throw new InvalidOperationException(
                    "Installer journal roots must be absolute paths.");
            }

            string commonRoot =
                TrimTrailingSeparator(Path.GetFullPath(commonApplicationDataRoot));
            string expectedStateRoot = Path.GetFullPath(
                Path.Combine(commonRoot, "SBMS", "Installer"));
            string stateRoot =
                TrimTrailingSeparator(Path.GetFullPath(installerStateRoot));
            if (!String.Equals(
                expectedStateRoot,
                stateRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Installer state root escaped common application data.");
            }

            string sbmsRoot = Path.Combine(commonRoot, "SBMS");
            ValidateDirectoryComponent(commonRoot, false);
            ValidateDirectoryComponent(sbmsRoot, false);
            InstallerJournalPathMetadata state =
                ValidateDirectoryComponent(stateRoot, true);
            if (state.Exists)
            {
                VerifyRestrictedAcl(state);
            }
            else if (!createIfMissing)
            {
                // Even an absent leaf can be exchanged after this check and
                // before AtomicTransactionJournalStore opens it by name.
            }

            // AtomicTransactionJournalStore currently performs path-based
            // File.Move/Replace/Delete operations. .NET Framework exposes no
            // supported root-handle-relative API for those operations, so an
            // attacker able to exchange a checked directory can redirect IO
            // between this validation and the subsequent open. Do not present
            // verify-before/verify-after checks as a security boundary.
            throw new PlatformNotSupportedException(
                "Production installer journal IO is disabled until all journal " +
                "operations use trusted root-handle-relative Windows APIs.");
        }

        private InstallerJournalPathMetadata ValidateDirectoryComponent(
            string path,
            bool includeSecurity)
        {
            InstallerJournalPathMetadata metadata =
                inspector.Inspect(path, includeSecurity);
            if (!metadata.Exists)
            {
                return metadata;
            }
            if (!metadata.IsDirectory)
            {
                throw new InvalidDataException(
                    "Installer journal path component is occupied by a file.");
            }
            if (metadata.IsReparsePoint)
            {
                throw new InvalidDataException(
                    "Installer journal path components must not be reparse points.");
            }
            return metadata;
        }

        private static void VerifyRestrictedAcl(
            InstallerJournalPathMetadata metadata)
        {
            if (!metadata.AccessRulesProtected)
            {
                throw new UnauthorizedAccessException(
                    "Installer state ACL inheritance is not protected.");
            }
            if (!IsTrustedIdentity(metadata.Owner))
            {
                throw new UnauthorizedAccessException(
                    "Installer state root has an untrusted owner.");
            }

            bool administratorsHaveFullControl = false;
            bool localSystemHasFullControl = false;
            foreach (InstallerJournalAccessRule rule in metadata.AccessRules)
            {
                if (!IsTrustedIdentity(rule.Identity) ||
                    rule.AccessControlType != AccessControlType.Allow ||
                    rule.Rights != FileSystemRights.FullControl ||
                    rule.InheritanceFlags !=
                        (InheritanceFlags.ContainerInherit |
                         InheritanceFlags.ObjectInherit) ||
                    rule.PropagationFlags != PropagationFlags.None ||
                    rule.IsInherited)
                {
                    throw new UnauthorizedAccessException(
                        "Installer state root has an unexpected ACL entry.");
                }
                if (rule.Identity.Equals(Administrators))
                {
                    if (administratorsHaveFullControl)
                    {
                        throw new UnauthorizedAccessException(
                            "Installer state root has duplicate ACL entries.");
                    }
                    administratorsHaveFullControl = true;
                }
                if (rule.Identity.Equals(LocalSystem))
                {
                    if (localSystemHasFullControl)
                    {
                        throw new UnauthorizedAccessException(
                            "Installer state root has duplicate ACL entries.");
                    }
                    localSystemHasFullControl = true;
                }
            }
            if (!administratorsHaveFullControl || !localSystemHasFullControl)
            {
                throw new UnauthorizedAccessException(
                    "Installer state root is missing a required ACL entry.");
            }
        }

        private static bool IsTrustedIdentity(SecurityIdentifier identity)
        {
            return identity != null &&
                (identity.Equals(Administrators) || identity.Equals(LocalSystem));
        }

        private static string TrimTrailingSeparator(string path)
        {
            string root = Path.GetPathRoot(path);
            while (path.Length > root.Length &&
                (path[path.Length - 1] == Path.DirectorySeparatorChar ||
                 path[path.Length - 1] == Path.AltDirectorySeparatorChar))
            {
                path = path.Substring(0, path.Length - 1);
            }
            return path;
        }
    }

    internal sealed class FileTransactionJournalStore
        : ITransactionJournalStore, ITransactionExecutionLeaseProvider
    {
        internal const string ProductionMutexName =
            @"Global\SBMS.Installer.TransactionJournal.v1";
        private static readonly TimeSpan ProductionMutexTimeout =
            TimeSpan.FromSeconds(30);

        private readonly AtomicTransactionJournalStore inner;
        private readonly IInstallerJournalAclPolicy aclPolicy;
        private readonly string mutexName;
        private readonly TimeSpan mutexTimeout;
        private readonly string commonApplicationDataPath;
        private readonly string installerStateRoot;
        private readonly string transactionsDirectory;

        internal FileTransactionJournalStore()
            : this(
                new EnvironmentInstallerProgramDataPathProvider(),
                new WindowsInstallerJournalAclPolicy(),
                ProductionMutexName,
                ProductionMutexTimeout,
                null)
        {
        }

        internal FileTransactionJournalStore(
            IInstallerProgramDataPathProvider programDataPathProvider,
            IInstallerJournalAclPolicy aclPolicy,
            string mutexName,
            TimeSpan mutexTimeout,
            ITerminalRotationFaultInjector rotationFaultInjector)
        {
            if (programDataPathProvider == null)
            {
                throw new ArgumentNullException("programDataPathProvider");
            }
            if (aclPolicy == null)
            {
                throw new ArgumentNullException("aclPolicy");
            }
            if (String.IsNullOrWhiteSpace(mutexName))
            {
                throw new ArgumentException(
                    "Journal mutex name is required.",
                    "mutexName");
            }
            if (mutexTimeout <= TimeSpan.Zero ||
                mutexTimeout.TotalMilliseconds > Int32.MaxValue)
            {
                throw new ArgumentOutOfRangeException("mutexTimeout");
            }

            commonApplicationDataPath =
                programDataPathProvider.GetCommonApplicationDataPath();
            if (String.IsNullOrWhiteSpace(commonApplicationDataPath) ||
                !Path.IsPathRooted(commonApplicationDataPath))
            {
                throw new InvalidOperationException(
                    "Common application data path must be absolute.");
            }
            commonApplicationDataPath =
                Path.GetFullPath(commonApplicationDataPath);
            installerStateRoot = Path.GetFullPath(
                Path.Combine(
                    commonApplicationDataPath,
                    "SBMS",
                    "Installer"));
            EnsureDirectDescendant(
                commonApplicationDataPath,
                installerStateRoot);

            this.aclPolicy = aclPolicy;
            this.mutexName = mutexName;
            this.mutexTimeout = mutexTimeout;
            transactionsDirectory = Path.Combine(
                installerStateRoot,
                "transactions");
            inner = new AtomicTransactionJournalStore(
                Path.Combine(installerStateRoot, "journal.json"),
                rotationFaultInjector);
        }

        internal string InstallerStateRoot
        {
            get { return installerStateRoot; }
        }

        internal string JournalPath
        {
            get { return inner.JournalPath; }
        }

        internal string TransactionsDirectory
        {
            get { return transactionsDirectory; }
        }

        public void Save(TransactionJournal journal)
        {
            WithExclusiveStoreLock(
                true,
                delegate
                {
                    inner.Save(journal);
                    return 0;
                });
        }

        public TransactionJournal Load()
        {
            return WithExclusiveStoreLock(
                false,
                delegate { return inner.Load(); });
        }

        public void PrepareForNewTransaction()
        {
            WithExclusiveStoreLock(
                false,
                delegate
                {
                    inner.PrepareForNewTransaction();
                    return 0;
                });
        }

        public IDisposable AcquireTransactionLease()
        {
            var mutex = new Mutex(false, mutexName);
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(mutexTimeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                if (!acquired)
                {
                    throw new TimeoutException(
                        "Timed out waiting for the installer transaction lease.");
                }
                aclPolicy.PrepareAndVerify(
                    commonApplicationDataPath,
                    installerStateRoot,
                    false);
                return new TransactionMutexLease(mutex);
            }
            catch
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
                mutex.Dispose();
                throw;
            }
        }

        private sealed class TransactionMutexLease : IDisposable
        {
            private Mutex mutex;

            internal TransactionMutexLease(Mutex mutex)
            {
                this.mutex = mutex;
            }

            public void Dispose()
            {
                Mutex owned = mutex;
                if (owned == null)
                {
                    return;
                }
                mutex = null;
                owned.ReleaseMutex();
                owned.Dispose();
            }
        }

        private T WithExclusiveStoreLock<T>(
            bool createRootIfMissing,
            Func<T> action)
        {
            using (var mutex = new Mutex(false, mutexName))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(mutexTimeout);
                    }
                    catch (AbandonedMutexException)
                    {
                        // The abandoned owner cannot have left an in-memory
                        // critical section active. Atomic readback below still
                        // validates the durable primary/backup state.
                        acquired = true;
                    }
                    if (!acquired)
                    {
                        throw new TimeoutException(
                            "Timed out waiting for the installer journal lock.");
                    }
                    aclPolicy.PrepareAndVerify(
                        commonApplicationDataPath,
                        installerStateRoot,
                        createRootIfMissing);
                    T result = action();
                    // This catches a path exchange after an atomic swap for
                    // diagnostics/tests, but is not a trusted production
                    // boundary. WindowsInstallerJournalAclPolicy remains
                    // feature-gated until the IO itself is handle-relative.
                    aclPolicy.PrepareAndVerify(
                        commonApplicationDataPath,
                        installerStateRoot,
                        false);
                    return result;
                }
                finally
                {
                    if (acquired)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        private static void EnsureDirectDescendant(
            string commonApplicationDataPath,
            string installerStateRoot)
        {
            string expected = Path.GetFullPath(
                Path.Combine(
                    commonApplicationDataPath,
                    "SBMS",
                    "Installer"));
            if (!String.Equals(
                expected,
                installerStateRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Installer state root escaped common application data.");
            }
        }
    }
}
