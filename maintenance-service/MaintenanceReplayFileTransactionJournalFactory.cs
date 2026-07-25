using System;

namespace SBMSSetup
{
    internal sealed partial class FileTransactionJournalStore
    {
        private MaintenanceReplayProductionStore
            maintenanceReplayStore;

        internal string MaintenanceReplayRootAuthorityInvariantDigest
        {
            get
            {
                lock (lifetimeGate)
                {
                    ThrowIfDisposed();
                    return ComputeMaintenanceReplayRootAuthority();
                }
            }
        }

        internal IMaintenanceReplayAtomicStore
            CreateMaintenanceReplayStore()
        {
            lock (lifetimeGate)
            {
                ThrowIfDisposed();
                if (maintenanceReplayStore == null)
                {
                    maintenanceReplayStore =
                        new MaintenanceReplayProductionStore(
                            journalFileSystem,
                            AcquireTransactionLeaseForMaintenanceReplay,
                            ComputeMaintenanceReplayRootAuthority(),
                            lifetimeGate,
                            ThrowIfDisposed);
                    RegisterChildLifetime(
                        maintenanceReplayStore);
                }
                return maintenanceReplayStore;
            }
        }

        private string ComputeMaintenanceReplayRootAuthority()
        {
            IJournalStorageAuthorityDescriptor storageAuthority =
                journalFileSystem as
                    IJournalStorageAuthorityDescriptor;
            if (storageAuthority == null)
            {
                throw new InvalidOperationException(
                    "Maintenance replay requires an exact storage authority descriptor.");
            }
            return PayloadContractValidation.ComputeDigest(
                "SBMS.Maintenance.RootAuthority.v1",
                new[]
                {
                    installerStateRoot.ToUpperInvariant(),
                    journalFileSystem.GetType().FullName,
                    storageAuthority.StorageAuthorityInvariantDigest,
                    aclPolicy.GetType().FullName,
                    transactionLeaseIdentity,
                    MaintenanceReplayProductionStore.RelativeRoot
                });
        }
    }
}
