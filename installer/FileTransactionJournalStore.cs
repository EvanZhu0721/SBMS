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
        private WindowsHandleRelativeJournalFileSystem nativeFileSystem;

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

        internal void Attach(
            WindowsHandleRelativeJournalFileSystem nativeFileSystem)
        {
            if (nativeFileSystem == null)
            {
                throw new ArgumentNullException("nativeFileSystem");
            }
            if (this.nativeFileSystem != null)
            {
                throw new InvalidOperationException(
                    "Installer journal policy is already attached.");
            }
            this.nativeFileSystem = nativeFileSystem;
        }

        public void PrepareAndVerify(
            string commonApplicationDataRoot,
            string installerStateRoot,
            bool createIfMissing)
        {
            if (nativeFileSystem != null)
            {
                nativeFileSystem.PrepareAndVerify(createIfMissing);
                return;
            }
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

            // An unattached policy can provide diagnostic metadata only. The
            // production constructor attaches the native handle-relative
            // filesystem before this method is reachable; any unsupported or
            // partial construction remains fail-closed.
            throw new PlatformNotSupportedException(
                "Installer journal policy has no trusted native IO capability.");
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

    internal interface IInstallerTransactionMutexFactory
    {
        Mutex OpenOrCreate(string name);
    }

    internal sealed class UnsecuredInstallerTransactionMutexFactory
        : IInstallerTransactionMutexFactory
    {
        public Mutex OpenOrCreate(string name)
        {
            return new Mutex(false, name);
        }
    }

    internal sealed class SecureInstallerTransactionMutexFactory
        : IInstallerTransactionMutexFactory
    {
        private readonly WindowsJournalSecurityProfile profile;

        internal SecureInstallerTransactionMutexFactory(
            WindowsJournalSecurityProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }
            this.profile = profile;
        }

        public Mutex OpenOrCreate(string name)
        {
            MutexSecurity requested = BuildSecurity();
            bool created;
            Mutex mutex = new Mutex(false, name, out created, requested);
            try
            {
                Verify(mutex.GetAccessControl());
                return mutex;
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }

        private MutexSecurity BuildSecurity()
        {
            var security = new MutexSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(profile.Owner);
            foreach (SecurityIdentifier identity in
                profile.FullControlIdentities)
            {
                security.AddAccessRule(
                    new MutexAccessRule(
                        identity,
                        MutexRights.FullControl,
                        AccessControlType.Allow));
            }
            return security;
        }

        private void Verify(MutexSecurity security)
        {
            if (!security.AreAccessRulesProtected)
            {
                throw new UnauthorizedAccessException(
                    "Installer transaction mutex ACL inheritance is enabled.");
            }
            var owner = security.GetOwner(
                typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || !owner.Equals(profile.Owner))
            {
                throw new UnauthorizedAccessException(
                    "Installer transaction mutex has an untrusted owner.");
            }
            AuthorizationRuleCollection rules = security.GetAccessRules(
                true,
                true,
                typeof(SecurityIdentifier));
            if (rules.Count != profile.FullControlIdentities.Length)
            {
                throw new UnauthorizedAccessException(
                    "Installer transaction mutex has unexpected ACL entries.");
            }
            foreach (SecurityIdentifier required in
                profile.FullControlIdentities)
            {
                bool found = false;
                foreach (AuthorizationRule authorizationRule in rules)
                {
                    var rule = authorizationRule as MutexAccessRule;
                    var identity = rule == null
                        ? null
                        : rule.IdentityReference as SecurityIdentifier;
                    if (identity != null && identity.Equals(required))
                    {
                        if (found ||
                            rule.AccessControlType != AccessControlType.Allow ||
                            rule.MutexRights != MutexRights.FullControl ||
                            rule.IsInherited)
                        {
                            throw new UnauthorizedAccessException(
                                "Installer transaction mutex ACL is invalid.");
                        }
                        found = true;
                    }
                }
                if (!found)
                {
                    throw new UnauthorizedAccessException(
                        "Installer transaction mutex is missing a required ACL.");
                }
            }
        }
    }

    internal sealed partial class FileTransactionJournalStore
        : ITransactionJournalStore, ITransactionExecutionLeaseProvider,
          ITransactionJournalRepairState,
          IDisposable
    {
        internal const string ProductionMutexName =
            @"Global\SBMS.Installer.TransactionJournal.v1";
        private static readonly TimeSpan ProductionMutexTimeout =
            TimeSpan.FromSeconds(30);

        private readonly AtomicTransactionJournalStore inner;
        private readonly IAtomicJournalFileSystem journalFileSystem;
        private readonly IInstallerJournalAclPolicy aclPolicy;
        private readonly InstanceTransactionLeaseCoordinator
            transactionLeaseCoordinator;
        private readonly Func<IDisposable> acquireTransactionLease;
        private readonly Func<bool> isTransactionLeaseHeldByCurrentThread;
        private readonly string commonApplicationDataPath;
        private readonly string installerStateRoot;
        private readonly string transactionsDirectory;
        private readonly string transactionLeaseIdentity;
        private readonly object lifetimeGate = new object();
        private IInstallerJournalChildLifetime childLifetime;
        private int activeTransactionLeaseCount;
        private bool disposing;
        private bool disposed;
        private bool poisoned;
        private Exception poisonFailure;
        private LifetimeBoundTransactionLease poisonedLease;

        internal FileTransactionJournalStore()
            : this(
                new EnvironmentInstallerProgramDataPathProvider(),
                new WindowsInstallerJournalAclPolicy(),
                ProductionMutexName,
                ProductionMutexTimeout,
                null,
                null,
                new SecureInstallerTransactionMutexFactory(
                    WindowsJournalSecurityProfile.Production()))
        {
        }

        internal FileTransactionJournalStore(
            IInstallerProgramDataPathProvider programDataPathProvider,
            IInstallerJournalAclPolicy aclPolicy,
            string mutexName,
            TimeSpan mutexTimeout,
            ITerminalRotationFaultInjector rotationFaultInjector)
            : this(
                programDataPathProvider,
                aclPolicy,
                mutexName,
                mutexTimeout,
                rotationFaultInjector,
                null,
                new UnsecuredInstallerTransactionMutexFactory())
        {
        }

        internal FileTransactionJournalStore(
            IInstallerProgramDataPathProvider programDataPathProvider,
            IInstallerJournalAclPolicy aclPolicy,
            string mutexName,
            TimeSpan mutexTimeout,
            ITerminalRotationFaultInjector rotationFaultInjector,
            IAtomicJournalFileSystem journalFileSystem,
            IInstallerTransactionMutexFactory mutexFactory)
        {
            if (programDataPathProvider == null)
            {
                throw new ArgumentNullException("programDataPathProvider");
            }
            if (aclPolicy == null)
            {
                throw new ArgumentNullException("aclPolicy");
            }
            if (mutexFactory == null)
            {
                throw new ArgumentNullException("mutexFactory");
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
            transactionLeaseCoordinator =
                new InstanceTransactionLeaseCoordinator(
                    mutexFactory,
                    mutexName,
                    mutexTimeout);
            acquireTransactionLease =
                transactionLeaseCoordinator.Acquire;
            isTransactionLeaseHeldByCurrentThread =
                delegate
                {
                    return transactionLeaseCoordinator.
                        IsHeldByCurrentThread;
                };
            transactionLeaseIdentity = mutexName;
            transactionsDirectory = Path.Combine(
                installerStateRoot,
                "transactions");
            IAtomicJournalFileSystem selectedFileSystem = journalFileSystem;
            WindowsInstallerJournalAclPolicy windowsPolicy =
                aclPolicy as WindowsInstallerJournalAclPolicy;
            if (windowsPolicy != null)
            {
                WindowsHandleRelativeJournalFileSystem nativeFileSystem =
                    selectedFileSystem as
                        WindowsHandleRelativeJournalFileSystem;
                if (nativeFileSystem == null)
                {
                    if (selectedFileSystem != null)
                    {
                        throw new InvalidOperationException(
                            "Production journal policy requires native " +
                            "handle-relative storage.");
                    }
                    nativeFileSystem =
                        new WindowsHandleRelativeJournalFileSystem(
                            commonApplicationDataPath,
                            WindowsJournalSecurityProfile.Production(),
                            null);
                }
                windowsPolicy.Attach(nativeFileSystem);
                selectedFileSystem = nativeFileSystem;
            }
            if (selectedFileSystem == null)
            {
                selectedFileSystem = new PathAtomicJournalFileSystem(
                    installerStateRoot);
            }
            this.journalFileSystem = selectedFileSystem;
            inner = new AtomicTransactionJournalStore(
                Path.Combine(installerStateRoot, "journal.json"),
                selectedFileSystem,
                rotationFaultInjector,
                null);
        }

        internal FileTransactionJournalStore(
            IInstallerProgramDataPathProvider programDataPathProvider,
            IInstallerJournalAclPolicy aclPolicy,
            string mutexName,
            TimeSpan mutexTimeout,
            ITerminalRotationFaultInjector rotationFaultInjector,
            IAtomicJournalFileSystem journalFileSystem,
            IInstallerTransactionMutexFactory mutexFactory,
            Func<IDisposable> transactionLeaseAcquireOverride,
            Func<bool> transactionLeaseHeldByCurrentThreadOverride)
            : this(
                programDataPathProvider,
                aclPolicy,
                mutexName,
                mutexTimeout,
                rotationFaultInjector,
                journalFileSystem,
                mutexFactory)
        {
            if (transactionLeaseAcquireOverride == null ||
                transactionLeaseHeldByCurrentThreadOverride == null)
            {
                throw new ArgumentNullException(
                    "Transaction lease test composition is incomplete.");
            }
            acquireTransactionLease =
                transactionLeaseAcquireOverride;
            isTransactionLeaseHeldByCurrentThread =
                transactionLeaseHeldByCurrentThreadOverride;
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

        internal bool IsPoisoned
        {
            get
            {
                lock (lifetimeGate)
                {
                    return poisoned;
                }
            }
        }

        public bool RequiresPrimaryRepair
        {
            get { return inner.RequiresPrimaryRepair; }
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
            lock (lifetimeGate)
            {
                ThrowIfDisposed();
                if (childLifetime != null &&
                    childLifetime.IsLeaseHeldByCurrentThread)
                {
                    throw new InvalidOperationException(
                        "Journal and maintenance replay leases are non-reentrant.");
                }
                activeTransactionLeaseCount++;
            }
            return AcquireReservedTransactionLease(true);
        }

        private IDisposable AcquireTransactionLeaseCore()
        {
            return acquireTransactionLease();
        }

        private IDisposable AcquireTransactionLeaseForMaintenanceReplay()
        {
            lock (lifetimeGate)
            {
                ThrowIfDisposed();
                if (isTransactionLeaseHeldByCurrentThread())
                {
                    throw new InvalidOperationException(
                        "Maintenance replay cannot nest inside a journal lease.");
                }
                activeTransactionLeaseCount++;
            }
            return AcquireReservedTransactionLease(true);
        }

        private IDisposable AcquireTransactionLeaseForStoreOperation()
        {
            lock (lifetimeGate)
            {
                ThrowIfDisposed();
                if (childLifetime != null &&
                    childLifetime.IsLeaseHeldByCurrentThread)
                {
                    throw new InvalidOperationException(
                        "Journal and maintenance replay leases are non-reentrant.");
                }
                activeTransactionLeaseCount++;
            }
            return AcquireReservedTransactionLease(false);
        }

        private IDisposable AcquireReservedTransactionLease(
            bool prepareAndVerifyRoot)
        {
            LifetimeBoundTransactionLease lease = null;
            try
            {
                lease = new LifetimeBoundTransactionLease(
                    this,
                    AcquireTransactionLeaseCore());
                if (prepareAndVerifyRoot)
                {
                    aclPolicy.PrepareAndVerify(
                        commonApplicationDataPath,
                        installerStateRoot,
                        false);
                }
                return lease;
            }
            catch (Exception acquisitionFailure)
            {
                if (lease == null)
                {
                    InstallerTransactionLeaseReleaseException typed =
                        acquisitionFailure as
                            InstallerTransactionLeaseReleaseException;
                    if (typed == null ||
                        typed.Outcome ==
                            InstallerTransactionLeaseReleaseOutcome.Confirmed)
                    {
                        ReleaseTransactionLeaseReservation();
                    }
                    else
                    {
                        Exception uncertain = typed;
                        if (typed.Outcome ==
                            InstallerTransactionLeaseReleaseOutcome.
                                RejectedBeforeMutation)
                        {
                            uncertain =
                                new InstallerTransactionLeaseReleaseException(
                                    InstallerTransactionLeaseReleaseOutcome.
                                        Uncertain,
                                    "Rejected acquisition cleanup cannot " +
                                    "return a lease for owner retry.",
                                    typed);
                        }
                        MarkTransactionLeaseAcquisitionPoisoned(
                            uncertain);
                        throw uncertain;
                    }
                }
                else
                {
                    // A throwing release poisons and retains the reservation.
                    // Its failure replaces the acquisition failure because
                    // exclusion ownership is now the authoritative hazard.
                    try
                    {
                        lease.Dispose();
                    }
                    catch (InstallerTransactionLeaseReleaseException failure)
                    {
                        if (failure.Outcome ==
                            InstallerTransactionLeaseReleaseOutcome.
                                RejectedBeforeMutation)
                        {
                            var uncertain =
                                new InstallerTransactionLeaseReleaseException(
                                    InstallerTransactionLeaseReleaseOutcome.
                                        Uncertain,
                                    "Rejected cleanup cannot be returned to " +
                                    "an owner for retry.",
                                    failure);
                            lease.PoisonWithoutRetry(uncertain);
                            throw uncertain;
                        }
                        throw;
                    }
                }
                throw;
            }
        }

        private void ReleaseTransactionLeaseReservation()
        {
            lock (lifetimeGate)
            {
                if (activeTransactionLeaseCount <= 0)
                {
                    throw new InvalidOperationException(
                        "Transaction lease lifetime accounting underflowed.");
                }
                activeTransactionLeaseCount--;
            }
        }

        private void MarkTransactionLeasePoisoned(
            LifetimeBoundTransactionLease lease,
            Exception failure)
        {
            lock (lifetimeGate)
            {
                if (lease == null || failure == null)
                {
                    throw new ArgumentNullException(
                        "Lease poison evidence is incomplete.");
                }
                if (poisoned &&
                    !Object.ReferenceEquals(poisonedLease, lease))
                {
                    throw new InvalidOperationException(
                        "Another transaction lease already poisoned the journal.",
                        poisonFailure);
                }
                poisoned = true;
                poisonedLease = lease;
                poisonFailure = failure;
            }
        }

        private void MarkTransactionLeaseAcquisitionPoisoned(
            Exception failure)
        {
            lock (lifetimeGate)
            {
                if (failure == null)
                {
                    throw new ArgumentNullException("failure");
                }
                if (!poisoned)
                {
                    poisoned = true;
                    poisonedLease = null;
                    poisonFailure = failure;
                }
            }
        }

        internal void PoisonFromMaintenanceReplayAcquisition(
            Exception failure)
        {
            MarkTransactionLeaseAcquisitionPoisoned(failure);
        }

        private void CompleteTransactionLeaseRelease(
            LifetimeBoundTransactionLease lease)
        {
            lock (lifetimeGate)
            {
                if (activeTransactionLeaseCount <= 0)
                {
                    throw new InvalidOperationException(
                        "Transaction lease lifetime accounting underflowed.");
                }
                if (poisoned &&
                    !Object.ReferenceEquals(poisonedLease, lease))
                {
                    throw new InvalidOperationException(
                        "A foreign lease cannot recover the poisoned journal.",
                        poisonFailure);
                }
                activeTransactionLeaseCount--;
                if (Object.ReferenceEquals(poisonedLease, lease))
                {
                    poisoned = false;
                    poisonedLease = null;
                    poisonFailure = null;
                }
            }
        }

        private void RegisterChildLifetime(
            IInstallerJournalChildLifetime child)
        {
            lock (lifetimeGate)
            {
                ThrowIfDisposed();
                if (child == null)
                {
                    throw new ArgumentNullException("child");
                }
                if (childLifetime != null &&
                    !Object.ReferenceEquals(childLifetime, child))
                {
                    throw new InvalidOperationException(
                        "Installer journal already owns another child lifetime.");
                }
                childLifetime = child;
            }
        }

        internal IProtectedEscrowManifestStore
            CreateProtectedEscrowManifestStore(
                string transactionId)
        {
            lock (lifetimeGate)
            {
                ThrowIfDisposed();
                return new ProtectedEscrowManifestStore(
                    journalFileSystem,
                    transactionLeaseCoordinator,
                    new AnchoredEscrowContentVerifier(
                        journalFileSystem),
                    transactionId);
            }
        }

        internal IProtectedPayloadWorkspaceCheckpointStore
            CreateProtectedPayloadWorkspaceCheckpointStore(
                string transactionId,
                string recoveryAuthorityInvariantDigest)
        {
            lock (lifetimeGate)
            {
                ThrowIfDisposed();
                return new ProtectedPayloadWorkspaceCheckpointStore(
                    journalFileSystem,
                    transactionLeaseCoordinator,
                    transactionId,
                    recoveryAuthorityInvariantDigest);
            }
        }

        internal IProtectedPayloadBuildWorkspaceModel
            CreateDurableProtectedPayloadBuildWorkspaceModel(
                string transactionId,
                string recoveryAuthorityInvariantDigest,
                IProtectedPayloadNativeTree nativeTree)
        {
            lock (lifetimeGate)
            {
                ThrowIfDisposed();
                if (nativeTree == null)
                {
                    throw new ArgumentNullException("nativeTree");
                }
                // A successful factory call transfers nativeTree ownership to
                // the returned model. The model must not outlive this store.
                return new DurableProtectedPayloadBuildWorkspaceModel(
                    CreateProtectedPayloadWorkspaceCheckpointStore(
                        transactionId,
                        recoveryAuthorityInvariantDigest),
                    transactionLeaseCoordinator,
                    nativeTree);
            }
        }

        private T WithExclusiveStoreLock<T>(
            bool createRootIfMissing,
            Func<T> action)
        {
            using (IDisposable lease =
                AcquireTransactionLeaseForStoreOperation())
            {
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
        }

        public void Dispose()
        {
            lock (lifetimeGate)
            {
                if (disposed)
                {
                    return;
                }
                if (poisoned)
                {
                    throw new InvalidOperationException(
                        "Cannot dispose a poisoned installer journal as successful.",
                        poisonFailure);
                }
                if (disposing)
                {
                    throw new InvalidOperationException(
                        "Installer journal disposal is already in progress.");
                }
                disposing = true;
                if (activeTransactionLeaseCount != 0 ||
                    (childLifetime != null &&
                     childLifetime.HasActiveLease))
                {
                    disposing = false;
                    throw new InvalidOperationException(
                        "Cannot dispose journal while a transaction or replay lease is active.");
                }
                if (childLifetime != null)
                {
                    childLifetime.DisposeFromParent();
                }
                try
                {
                    IDisposable disposable =
                        journalFileSystem as IDisposable;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }
                }
                finally
                {
                    disposed = true;
                    disposing = false;
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed || disposing)
            {
                throw new ObjectDisposedException(
                    "FileTransactionJournalStore");
            }
            if (poisoned)
            {
                throw new InvalidOperationException(
                    "File transaction journal is poisoned by an unproven lease release.",
                    poisonFailure);
            }
        }

        private sealed class LifetimeBoundTransactionLease : IDisposable
        {
            private enum ReleaseState
            {
                Active,
                Releasing,
                Released,
                Poisoned
            }

            private readonly object releaseGate = new object();
            private readonly Thread leaseOwnerThread;
            private FileTransactionJournalStore owner;
            private IDisposable innerLease;
            private ReleaseState releaseState;

            internal LifetimeBoundTransactionLease(
                FileTransactionJournalStore owner,
                IDisposable innerLease)
            {
                this.owner = owner;
                this.innerLease = innerLease;
                leaseOwnerThread = Thread.CurrentThread;
                releaseState = ReleaseState.Active;
            }

            internal void PoisonWithoutRetry(Exception failure)
            {
                lock (releaseGate)
                {
                    if (owner == null ||
                        innerLease == null ||
                        releaseState == ReleaseState.Released)
                    {
                        throw new InvalidOperationException(
                            "A completed lease cannot be poisoned.");
                    }
                    releaseState = ReleaseState.Poisoned;
                    owner.MarkTransactionLeasePoisoned(
                        this,
                        failure);
                    Monitor.PulseAll(releaseGate);
                }
            }

            public void Dispose()
            {
                FileTransactionJournalStore releaseOwner;
                IDisposable releaseLease;
                lock (releaseGate)
                {
                    if (releaseState == ReleaseState.Released)
                    {
                        return;
                    }
                    if (releaseState == ReleaseState.Poisoned)
                    {
                        throw PermanentPoisonFailure();
                    }
                    if (!Object.ReferenceEquals(
                            leaseOwnerThread,
                            Thread.CurrentThread))
                    {
                        throw new InstallerTransactionLeaseReleaseException(
                            InstallerTransactionLeaseReleaseOutcome.
                                RejectedBeforeMutation,
                            "Transaction lease disposal is restricted to " +
                            "its acquiring thread.",
                            null);
                    }
                    if (releaseState == ReleaseState.Releasing)
                    {
                        throw new InstallerTransactionLeaseReleaseException(
                            InstallerTransactionLeaseReleaseOutcome.
                                RejectedBeforeMutation,
                            "Transaction lease release is already in progress.",
                            null);
                    }
                    releaseState = ReleaseState.Releasing;
                    releaseOwner = owner;
                    releaseLease = innerLease;
                }
                try
                {
                    releaseLease.Dispose();
                }
                catch (InstallerTransactionLeaseReleaseException failure)
                {
                    if (failure.Outcome ==
                        InstallerTransactionLeaseReleaseOutcome.
                            RejectedBeforeMutation)
                    {
                        ResetActive();
                        throw;
                    }
                    if (failure.Outcome ==
                        InstallerTransactionLeaseReleaseOutcome.
                            Confirmed)
                    {
                        releaseOwner.
                            CompleteTransactionLeaseRelease(this);
                        MarkReleased();
                        throw;
                    }
                    MarkPoisoned(releaseOwner, failure);
                    throw;
                }
                catch (Exception failure)
                {
                    MarkPoisoned(releaseOwner, failure);
                    throw;
                }
                releaseOwner.CompleteTransactionLeaseRelease(this);
                MarkReleased();
            }

            private void ResetActive()
            {
                lock (releaseGate)
                {
                    releaseState = ReleaseState.Active;
                    Monitor.PulseAll(releaseGate);
                }
            }

            private void MarkReleased()
            {
                lock (releaseGate)
                {
                    innerLease = null;
                    owner = null;
                    releaseState = ReleaseState.Released;
                    Monitor.PulseAll(releaseGate);
                }
            }

            private void MarkPoisoned(
                FileTransactionJournalStore releaseOwner,
                Exception failure)
            {
                lock (releaseGate)
                {
                    releaseOwner.MarkTransactionLeasePoisoned(
                        this,
                        failure);
                    releaseState = ReleaseState.Poisoned;
                    Monitor.PulseAll(releaseGate);
                }
            }

            private InvalidOperationException PermanentPoisonFailure()
            {
                return new InvalidOperationException(
                    "Transaction lease release remains uncertain; " +
                    "the journal is permanently fail-closed.",
                    owner == null ? null : owner.poisonFailure);
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
