using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SBMSSetup
{
    internal interface IMaintenanceReplayPostAcquireFaultSeam
    {
        void AfterSharedLeaseAcquired();
    }

    internal sealed class MaintenanceReplayProductionStore
        : IMaintenanceReplayAtomicStore,
          IInstallerJournalChildLifetime
    {
        internal const string RelativeRoot = @"maintenance-replay\v1";
        private readonly IAtomicJournalFileSystem fileSystem;
        private readonly Func<IDisposable> acquireSharedLease;
        private readonly object lifetimeGate;
        private readonly Action validateOwnerActive;
        private readonly Action<Exception> poisonOwnerAcquisition;
        private readonly IMaintenanceReplayPostAcquireFaultSeam
            postAcquireFaultSeam;
        private int leaseThreadId;
        private bool disposed;
        private bool poisoned;

        internal MaintenanceReplayProductionStore(
            IAtomicJournalFileSystem fileSystem,
            Func<IDisposable> acquireSharedLease,
            string rootAuthorityInvariantDigest)
            : this(
                fileSystem,
                acquireSharedLease,
                rootAuthorityInvariantDigest,
                new object(),
                delegate { },
                delegate(Exception ignored) { },
                null)
        {
        }

        internal MaintenanceReplayProductionStore(
            IAtomicJournalFileSystem fileSystem,
            Func<IDisposable> acquireSharedLease,
            string rootAuthorityInvariantDigest,
            object sharedLifetimeGate,
            Action validateOwnerActive,
            Action<Exception> poisonOwnerOnAcquisitionFailure,
            IMaintenanceReplayPostAcquireFaultSeam
                sharedLeaseAcquiredFaultSeam)
        {
            if (fileSystem == null ||
                acquireSharedLease == null ||
                sharedLifetimeGate == null ||
                validateOwnerActive == null ||
                poisonOwnerOnAcquisitionFailure == null)
            {
                throw new ArgumentNullException(
                    "Maintenance replay composition is incomplete.");
            }
            PayloadContractValidation.RequireSha256(
                rootAuthorityInvariantDigest,
                "Maintenance replay root authority");
            this.fileSystem = fileSystem;
            this.acquireSharedLease = acquireSharedLease;
            lifetimeGate = sharedLifetimeGate;
            this.validateOwnerActive = validateOwnerActive;
            poisonOwnerAcquisition =
                poisonOwnerOnAcquisitionFailure;
            postAcquireFaultSeam =
                sharedLeaseAcquiredFaultSeam;
            RootAuthorityInvariantDigest =
                rootAuthorityInvariantDigest;
        }

        public string RootAuthorityInvariantDigest { get; private set; }

        public IMaintenanceReplayStoreLease AcquireExclusiveLease()
        {
            int threadId = Thread.CurrentThread.ManagedThreadId;
            lock (lifetimeGate)
            {
                validateOwnerActive();
                if (disposed)
                {
                    throw new ObjectDisposedException(
                        "MaintenanceReplayProductionStore");
                }
                if (poisoned)
                {
                    throw new InvalidOperationException(
                        "Maintenance replay acquisition is poisoned.");
                }
                if (leaseThreadId != 0)
                {
                    throw new InvalidOperationException(
                        "Maintenance replay lease is non-reentrant.");
                }
                leaseThreadId = threadId;
            }
            IDisposable shared = null;
            try
            {
                shared = acquireSharedLease();
                if (shared == null)
                {
                    throw new InvalidOperationException(
                        "Shared transaction lease was not acquired.");
                }
                if (postAcquireFaultSeam != null)
                {
                    postAcquireFaultSeam.AfterSharedLeaseAcquired();
                }
                return new Lease(this, threadId, shared);
            }
            catch (Exception acquisitionFailure)
            {
                if (shared == null)
                {
                    InstallerTransactionLeaseReleaseException typed =
                        acquisitionFailure as
                            InstallerTransactionLeaseReleaseException;
                    if (typed == null ||
                        typed.Outcome ==
                            InstallerTransactionLeaseReleaseOutcome.Confirmed)
                    {
                        ClearAcquisitionReservation();
                    }
                    else
                    {
                        Exception uncertain =
                            RequireUncertainAcquisitionFailure(typed);
                        PoisonAcquisition(uncertain);
                        throw uncertain;
                    }
                }
                else
                {
                    try
                    {
                        shared.Dispose();
                        ClearAcquisitionReservation();
                    }
                    catch (InstallerTransactionLeaseReleaseException failure)
                    {
                        if (failure.Outcome ==
                            InstallerTransactionLeaseReleaseOutcome.Confirmed)
                        {
                            ClearAcquisitionReservation();
                        }
                        else
                        {
                            Exception uncertain =
                                RequireUncertainAcquisitionFailure(failure);
                            PoisonAcquisition(uncertain);
                            throw uncertain;
                        }
                    }
                    catch (Exception cleanupFailure)
                    {
                        PoisonAcquisition(cleanupFailure);
                        throw;
                    }
                }
                throw;
            }
        }

        private Exception RequireUncertainAcquisitionFailure(
            InstallerTransactionLeaseReleaseException failure)
        {
            if (failure.Outcome ==
                InstallerTransactionLeaseReleaseOutcome.Uncertain)
            {
                return failure;
            }
            return new InstallerTransactionLeaseReleaseException(
                InstallerTransactionLeaseReleaseOutcome.Uncertain,
                "Rejected replay acquisition cleanup cannot return a " +
                "lease for owner retry.",
                failure);
        }

        private void PoisonAcquisition(Exception failure)
        {
            lock (lifetimeGate)
            {
                poisoned = true;
            }
            poisonOwnerAcquisition(failure);
        }

        private void ClearAcquisitionReservation()
        {
            lock (lifetimeGate)
            {
                leaseThreadId = 0;
            }
        }

        public bool IsLeaseHeldByCurrentThread
        {
            get
            {
                lock (lifetimeGate)
                {
                    return leaseThreadId ==
                        Thread.CurrentThread.ManagedThreadId;
                }
            }
        }

        public bool HasActiveLease
        {
            get
            {
                lock (lifetimeGate)
                {
                    return leaseThreadId != 0;
                }
            }
        }

        public void DisposeFromParent()
        {
            lock (lifetimeGate)
            {
                if (leaseThreadId != 0)
                {
                    throw new InvalidOperationException(
                        "Cannot dispose journal while a replay lease is active.");
                }
                disposed = true;
            }
        }

        private sealed class Lease : IMaintenanceReplayStoreLease
        {
            private readonly MaintenanceReplayProductionStore owner;
            private readonly int threadId;
            private readonly IDisposable shared;
            private readonly HashSet<string> recoveredBackup =
                new HashSet<string>(StringComparer.Ordinal);
            private bool disposed;

            internal Lease(
                MaintenanceReplayProductionStore owner,
                int threadId,
                IDisposable shared)
            {
                this.owner = owner;
                this.threadId = threadId;
                this.shared = shared;
            }

            public bool TryRead(string key, out byte[] bytes)
            {
                DemandThread();
                string primary = ResolvePrimary(key);
                var publisher =
                    new AtomicDocumentBytePublisher(
                        owner.fileSystem, primary);
                string candidate = primary + ".new";
                if (owner.fileSystem.FileExists(candidate))
                {
                    owner.fileSystem.DeleteFile(candidate);
                }
                Exception primaryFailure = null;
                if (publisher.PrimaryExists)
                {
                    try
                    {
                        AtomicDocumentReadResult primaryDocument =
                            publisher.ReadPrimary();
                        MaintenanceReplayRecordCodec.
                            DeserializeCanonical(
                                primaryDocument.Bytes);
                        bytes = primaryDocument.Bytes;
                        recoveredBackup.Remove(key);
                        return true;
                    }
                    catch (MaintenanceReplayContentFormatException exception)
                    {
                        primaryFailure = exception;
                    }
                    catch (AtomicDocumentFormatException exception)
                    {
                        primaryFailure = exception;
                    }
                }
                if (publisher.BackupExists)
                {
                    try
                    {
                        AtomicDocumentReadResult backup =
                            publisher.ReadBackup();
                        MaintenanceReplayRecordCodec.
                            DeserializeCanonical(backup.Bytes);
                        bytes = backup.Bytes;
                        recoveredBackup.Add(key);
                        return true;
                    }
                    catch (MaintenanceReplayContentFormatException)
                    {
                        if (primaryFailure != null) throw primaryFailure;
                        throw;
                    }
                    catch (AtomicDocumentFormatException)
                    {
                        if (primaryFailure != null) throw primaryFailure;
                        throw;
                    }
                }
                if (primaryFailure != null) throw primaryFailure;
                bytes = null;
                return false;
            }

            public void AtomicWrite(string key, byte[] bytes)
            {
                DemandThread();
                if (bytes == null || bytes.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Maintenance replay bytes are missing.");
                }
                string primary = ResolvePrimary(key);
                owner.fileSystem.EnsureDirectory(
                    Path.GetDirectoryName(primary));
                var publisher =
                    new AtomicDocumentBytePublisher(
                        owner.fileSystem, primary);
                publisher.Publish(
                    bytes,
                    publisher.PrimaryExists &&
                        !recoveredBackup.Contains(key),
                    null, null, null);
                recoveredBackup.Remove(key);
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }
                DemandThread();
                try
                {
                    shared.Dispose();
                }
                catch (InstallerTransactionLeaseReleaseException failure)
                {
                    if (failure.Outcome ==
                        InstallerTransactionLeaseReleaseOutcome.Confirmed)
                    {
                        CompleteDispose();
                    }
                    throw;
                }
                CompleteDispose();
            }

            private void CompleteDispose()
            {
                lock (owner.lifetimeGate)
                {
                    owner.leaseThreadId = 0;
                }
                disposed = true;
            }

            private string ResolvePrimary(string key)
            {
                string[] parts =
                    key == null ? new string[0] : key.Split(':');
                if (parts.Length != 2 ||
                    !CanonicalId(parts[0]) ||
                    !CanonicalId(parts[1]) ||
                    !String.Equals(
                        key,
                        parts[0] + ":" + parts[1],
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Maintenance replay key is not canonical.");
                }
                return Path.Combine(
                    RelativeRoot,
                    parts[0],
                    parts[1] + ".json");
            }

            private void DemandThread()
            {
                if (disposed ||
                    Thread.CurrentThread.ManagedThreadId != threadId)
                {
                    throw new InvalidOperationException(
                        "Maintenance replay lease is thread-affine.");
                }
            }
        }

        private static bool CanonicalId(string value)
        {
            if (value == null || value.Length != 32) return false;
            for (int index = 0; index < value.Length; ++index)
            {
                char item = value[index];
                if (!((item >= '0' && item <= '9') ||
                      (item >= 'a' && item <= 'f')))
                {
                    return false;
                }
            }
            Guid parsed;
            return Guid.TryParseExact(value, "N", out parsed);
        }

    }
}
