using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace SBMSSetup
{
    internal enum TerminalRotationCrashPoint
    {
        ArchiveTempFlushed,
        ArchivePublished,
        StaleBackupDeleted,
        ActivePrimaryDeleted
    }

    internal interface ITerminalRotationFaultInjector
    {
        void After(TerminalRotationCrashPoint point);
    }

    internal enum JournalSaveCrashPoint
    {
        CandidateFlushed,
        PrimaryPublished,
        PrimaryReadback
    }

    internal interface IJournalSaveFaultInjector
    {
        void After(JournalSaveCrashPoint point);
    }

    internal interface ITransactionJournalStore
    {
        void Save(TransactionJournal journal);
        TransactionJournal Load();
        void PrepareForNewTransaction();
    }

    internal interface ITransactionExecutionLeaseProvider
    {
        IDisposable AcquireTransactionLease();
    }

    internal sealed class AtomicTransactionJournalStore : ITransactionJournalStore
    {
        private readonly string journalPath;
        private readonly string backupPath;
        private readonly string historyDirectory;
        private readonly ITerminalRotationFaultInjector rotationFaultInjector;
        private readonly IJournalSaveFaultInjector saveFaultInjector;

        internal AtomicTransactionJournalStore(string journalPath)
            : this(journalPath, null, null)
        {
        }

        internal AtomicTransactionJournalStore(
            string journalPath,
            ITerminalRotationFaultInjector rotationFaultInjector)
            : this(journalPath, rotationFaultInjector, null)
        {
        }

        internal AtomicTransactionJournalStore(
            string journalPath,
            ITerminalRotationFaultInjector rotationFaultInjector,
            IJournalSaveFaultInjector saveFaultInjector)
        {
            if (String.IsNullOrWhiteSpace(journalPath))
            {
                throw new ArgumentException("Journal path is required.", "journalPath");
            }
            this.journalPath = Path.GetFullPath(journalPath);
            backupPath = this.journalPath + ".bak";
            historyDirectory = Path.Combine(
                Path.GetDirectoryName(this.journalPath),
                "history");
            this.rotationFaultInjector = rotationFaultInjector;
            this.saveFaultInjector = saveFaultInjector;
        }

        internal string JournalPath
        {
            get { return journalPath; }
        }

        internal string BackupPath
        {
            get { return backupPath; }
        }

        internal string HistoryDirectory
        {
            get { return historyDirectory; }
        }

        public void PrepareForNewTransaction()
        {
            TransactionJournal existing = Load();
            if (existing == null)
            {
                return;
            }
            if (existing.Status != TransactionStatus.Committed &&
                existing.Status != TransactionStatus.RolledBack)
            {
                throw new InvalidOperationException(
                    "A non-terminal installer transaction blocks a new transaction.");
            }
            if (existing.Status == TransactionStatus.Committed &&
                existing.FinalizationStatus !=
                    TransactionFinalizationStatus.Complete)
            {
                throw new InvalidOperationException(
                    "Committed installer finalization blocks journal rotation.");
            }
            Directory.CreateDirectory(historyDirectory);
            string archivePath = Path.Combine(
                historyDirectory,
                existing.TransactionId + "-r" +
                existing.Revision.ToString(CultureInfo.InvariantCulture) +
                "-" + existing.Status.ToString() + ".json");
            if (File.Exists(archivePath))
            {
                TransactionJournal archivedExisting = ReadExact(archivePath);
                ValidateReadback(existing, archivedExisting);
            }
            else
            {
                string archiveTemporaryPath = archivePath + ".new";
                if (File.Exists(archiveTemporaryPath))
                {
                    TransactionJournal temporaryExisting = null;
                    try
                    {
                        temporaryExisting = ReadExact(archiveTemporaryPath);
                    }
                    catch
                    {
                        // The active terminal journal was already validated
                        // above. A torn, unreadable archive temp is therefore
                        // safe to discard and deterministically rebuild.
                        File.Delete(archiveTemporaryPath);
                        WriteExact(archiveTemporaryPath, existing);
                    }
                    if (temporaryExisting != null)
                    {
                        // A readable temp must describe this exact terminal;
                        // never replace a valid but conflicting archive.
                        ValidateReadback(existing, temporaryExisting);
                    }
                }
                else
                {
                    WriteExact(archiveTemporaryPath, existing);
                }
                InjectRotationFault(
                    TerminalRotationCrashPoint.ArchiveTempFlushed);
                File.Move(archiveTemporaryPath, archivePath);
                InjectRotationFault(
                    TerminalRotationCrashPoint.ArchivePublished);
            }
            string staleArchiveTemporaryPath = archivePath + ".new";
            if (File.Exists(staleArchiveTemporaryPath))
            {
                File.Delete(staleArchiveTemporaryPath);
            }
            // Delete the stale backup first. At every crash boundary the
            // terminal primary remains authoritative until no Applying backup
            // can be mistaken for the active transaction.
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            InjectRotationFault(
                TerminalRotationCrashPoint.StaleBackupDeleted);
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
            InjectRotationFault(
                TerminalRotationCrashPoint.ActivePrimaryDeleted);
        }

        public void Save(TransactionJournal journal)
        {
            if (journal == null)
            {
                Validate(journal, false);
            }
            TransactionJournal candidate = Clone(journal);
            candidate.UpdatedUtc = DateTime.UtcNow.ToString(
                "o",
                CultureInfo.InvariantCulture);
            Validate(candidate, false);
            string directory = Path.GetDirectoryName(journalPath);
            if (String.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Journal directory is missing.");
            }
            Directory.CreateDirectory(directory);
            string temporaryPath = journalPath + ".new." + Guid.NewGuid().ToString("N");
            TransactionJournal previous = null;
            bool primaryIsValid = false;
            if (File.Exists(journalPath))
            {
                try
                {
                    previous = ReadExact(journalPath);
                    primaryIsValid = true;
                }
                catch
                {
                    if (File.Exists(backupPath))
                    {
                        previous = ReadExact(backupPath);
                    }
                }
            }
            else if (File.Exists(backupPath))
            {
                previous = ReadExact(backupPath);
            }
            if (previous != null)
            {
                if (!String.Equals(
                    previous.TransactionId,
                    candidate.TransactionId,
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Refusing to overwrite a different transaction journal.");
                }
                if (candidate.Revision != previous.Revision)
                {
                    throw new InvalidDataException(
                        "Refusing to save a stale transaction journal revision.");
                }
            }
            else if (candidate.Revision != 0)
            {
                throw new InvalidDataException(
                    "Initial transaction journal revision must be zero.");
            }
            candidate.Revision = previous == null
                ? 1
                : checked(previous.Revision + 1);
            candidate.ContentDigest = ComputeContentDigest(candidate);
            bool primaryPublished = false;
            try
            {
                WriteExact(temporaryPath, candidate);
                InjectSaveFault(JournalSaveCrashPoint.CandidateFlushed);
                if (primaryIsValid)
                {
                    File.Replace(temporaryPath, journalPath, backupPath, true);
                }
                else
                {
                    if (File.Exists(journalPath))
                    {
                        File.Delete(journalPath);
                    }
                    File.Move(temporaryPath, journalPath);
                }
                primaryPublished = true;
                // Once the atomic publication API returns, this revision is
                // the authoritative primary even if verification IO is
                // temporarily unavailable. Keep the caller aligned so a
                // later recovery save cannot present a stale revision.
                CopyPersistedFields(candidate, journal);
                InjectSaveFault(JournalSaveCrashPoint.PrimaryPublished);
                InjectSaveFault(JournalSaveCrashPoint.PrimaryReadback);
                TransactionJournal persisted = ReadExact(journalPath);
                ValidateReadback(candidate, persisted);
            }
            catch
            {
                if (primaryPublished)
                {
                    TrySynchronizePublishedCandidate(candidate, journal);
                }
                throw;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private void TrySynchronizePublishedCandidate(
            TransactionJournal candidate,
            TransactionJournal caller)
        {
            try
            {
                InjectSaveFault(JournalSaveCrashPoint.PrimaryReadback);
                TransactionJournal persisted = ReadExact(journalPath);
                ValidateReadback(candidate, persisted);
                CopyPersistedFields(candidate, caller);
            }
            catch
            {
                // The caller retains the last known durable persistence fields.
                // A later Save will resolve an invalid primary through the
                // verified backup rather than inheriting an unverified revision.
            }
        }

        private static void CopyPersistedFields(
            TransactionJournal source,
            TransactionJournal destination)
        {
            destination.Revision = source.Revision;
            destination.UpdatedUtc = source.UpdatedUtc;
            destination.ContentDigest = source.ContentDigest;
        }

        private static TransactionJournal Clone(TransactionJournal journal)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(TransactionJournal));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, journal);
                stream.Position = 0;
                return serializer.ReadObject(stream) as TransactionJournal;
            }
        }

        public TransactionJournal Load()
        {
            Exception primaryFailure = null;
            if (File.Exists(journalPath))
            {
                try
                {
                    return ReadExact(journalPath);
                }
                catch (Exception ex)
                {
                    primaryFailure = ex;
                }
            }
            if (File.Exists(backupPath))
            {
                try
                {
                    return ReadExact(backupPath);
                }
                catch (Exception backupFailure)
                {
                    throw new InvalidDataException(
                        "Both primary and backup transaction journals are invalid.",
                        new AggregateException(primaryFailure, backupFailure));
                }
            }
            if (primaryFailure != null)
            {
                throw new InvalidDataException(
                    "Primary transaction journal is invalid and no backup exists.",
                    primaryFailure);
            }
            return null;
        }

        private static void WriteExact(string path, TransactionJournal journal)
        {
            var serializer = new DataContractJsonSerializer(typeof(TransactionJournal));
            using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                serializer.WriteObject(stream, journal);
                stream.Flush(true);
            }
        }

        private static TransactionJournal ReadExact(string path)
        {
            var serializer = new DataContractJsonSerializer(typeof(TransactionJournal));
            TransactionJournal journal;
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                journal = serializer.ReadObject(stream) as TransactionJournal;
            }
            Validate(journal, true);
            string expectedDigest = ComputeContentDigest(journal);
            if (!String.Equals(
                journal.ContentDigest,
                expectedDigest,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Transaction journal content checksum mismatch.");
            }
            return journal;
        }

        private static void Validate(
            TransactionJournal journal,
            bool requirePersistedRevision)
        {
            if (journal == null)
            {
                throw new InvalidDataException("Transaction journal is empty.");
            }
            if (journal.SchemaVersion != 3)
            {
                throw new InvalidDataException(
                    "Unsupported transaction journal schema.");
            }
            if (String.IsNullOrWhiteSpace(journal.TransactionId))
            {
                throw new InvalidDataException(
                    "Transaction journal identity is missing.");
            }
            if (!Enum.IsDefined(typeof(InstallOperation), journal.Operation) ||
                !Enum.IsDefined(typeof(TransactionStatus), journal.Status))
            {
                throw new InvalidDataException(
                    "Transaction journal enum value is invalid.");
            }
            if (journal.Baseline == null)
            {
                throw new InvalidDataException(
                    "Transaction journal baseline is missing.");
            }
            journal.Baseline.Validate();
            if (journal.Context == null)
            {
                throw new InvalidDataException(
                    "Transaction context is missing.");
            }
            journal.Context.Validate();
            if (!String.Equals(
                    journal.Context.TransactionId,
                    journal.TransactionId,
                    StringComparison.Ordinal) ||
                journal.Context.Operation != journal.Operation ||
                !String.Equals(
                    journal.Context.Baseline.EvidenceDigest,
                    journal.Baseline.EvidenceDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Transaction context does not match the journal.");
            }
            if (journal.Operation == InstallOperation.Uninstall)
            {
                if (journal.Target != null)
                {
                    throw new InvalidDataException(
                        "Uninstall journal must not carry a target release.");
                }
            }
            else
            {
                if (journal.Target == null)
                {
                    throw new InvalidDataException(
                        "Install journal target is missing.");
                }
                journal.Target.Validate();
            }
            if (journal.Intents == null)
            {
                throw new InvalidDataException(
                    "Transaction journal compensation intents are missing.");
            }
            if (journal.StageEvents == null)
            {
                throw new InvalidDataException(
                    "Transaction journal stage events are missing.");
            }
            if (String.IsNullOrWhiteSpace(journal.CreatedUtc) ||
                String.IsNullOrWhiteSpace(journal.UpdatedUtc))
            {
                throw new InvalidDataException(
                    "Transaction journal timestamps are missing.");
            }
            if ((requirePersistedRevision && journal.Revision < 1) ||
                (!requirePersistedRevision && journal.Revision < 0))
            {
                throw new InvalidDataException(
                    "Transaction journal revision is invalid.");
            }
            Guid transactionGuid;
            if (!Guid.TryParseExact(
                journal.TransactionId,
                "N",
                out transactionGuid))
            {
                throw new InvalidDataException(
                    "Transaction identity is not an N-format GUID.");
            }
            DateTime created;
            DateTime updated;
            if (!DateTime.TryParse(
                    journal.CreatedUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out created) ||
                !DateTime.TryParse(
                    journal.UpdatedUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out updated) ||
                updated < created)
            {
                throw new InvalidDataException(
                    "Transaction journal timestamps are invalid.");
            }
            for (int index = 0; index < journal.Intents.Count; ++index)
            {
                CompensationIntent intent = journal.Intents[index];
                InstallerMutation[] expectedPlan =
                    InstallerTransactionPlan.ForOperation(
                        journal.Operation,
                        journal.Context.RequestFlags);
                if (intent == null ||
                    intent.Sequence != index ||
                    index >= expectedPlan.Length ||
                    intent.Mutation != expectedPlan[index] ||
                    !Enum.IsDefined(typeof(InstallerMutation), intent.Mutation) ||
                    !Enum.IsDefined(
                        typeof(CompensationIntentStatus),
                        intent.Status) ||
                    !Enum.IsDefined(
                        typeof(InstallerCompensationAction),
                        intent.InverseAction) ||
                    intent.InverseAction !=
                        InstallerTransactionPlan.InverseFor(intent.Mutation))
                {
                    throw new InvalidDataException(
                        "Transaction compensation intent is invalid.");
                }
            }
            if (journal.Intents.Count > 0 &&
                journal.Intents[0].Mutation != InstallerMutation.CreateEscrow)
            {
                throw new InvalidDataException(
                    "Escrow creation is not the first WAL mutation.");
            }
            DateTime previousStageTime = created;
            for (int index = 0; index < journal.StageEvents.Count; ++index)
            {
                TransactionStageEvent stage = journal.StageEvents[index];
                DateTime stageTime;
                if (stage == null ||
                    stage.Sequence != index ||
                    String.IsNullOrWhiteSpace(stage.Stage) ||
                    String.IsNullOrWhiteSpace(stage.Outcome) ||
                    !DateTime.TryParse(
                        stage.TimestampUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out stageTime))
                {
                    throw new InvalidDataException(
                        "Transaction stage event is invalid.");
                }
                if (stageTime < created || stageTime > updated)
                {
                    throw new InvalidDataException(
                        "Transaction stage event timestamp is outside the journal lifetime.");
                }
                if (stageTime < previousStageTime)
                {
                    throw new InvalidDataException(
                        "Transaction stage event timestamps are not monotonic.");
                }
                previousStageTime = stageTime;
                if (!String.IsNullOrWhiteSpace(stage.Mutation))
                {
                    try
                    {
                        object parsed = Enum.Parse(
                            typeof(InstallerMutation),
                            stage.Mutation,
                            false);
                        if (!Enum.IsDefined(
                            typeof(InstallerMutation),
                            parsed))
                        {
                            throw new InvalidDataException(
                                "Transaction stage mutation is invalid.");
                        }
                    }
                    catch (ArgumentException ex)
                    {
                        throw new InvalidDataException(
                            "Transaction stage mutation is invalid.",
                            ex);
                    }
                }
            }
            if (journal.Status == TransactionStatus.Created &&
                journal.Intents.Count != 0)
            {
                throw new InvalidDataException(
                    "Created journal already contains mutation intents.");
            }
            if (journal.Status == TransactionStatus.Applying &&
                journal.Intents.Count == 0)
            {
                throw new InvalidDataException(
                    "Applying journal contains no mutation intent.");
            }
            if (journal.Status == TransactionStatus.RollingBack &&
                journal.RollbackResult != "InProgress")
            {
                throw new InvalidDataException(
                    "RollingBack journal result is inconsistent.");
            }
            if (journal.Status == TransactionStatus.RolledBack &&
                (journal.RollbackResult != "Verified" ||
                 String.IsNullOrWhiteSpace(journal.OriginalError)))
            {
                throw new InvalidDataException(
                    "RolledBack journal is not verified.");
            }
            if (journal.Status == TransactionStatus.RolledBack)
            {
                foreach (CompensationIntent intent in journal.Intents)
                {
                    if (intent.Status !=
                        CompensationIntentStatus.Restored)
                    {
                        throw new InvalidDataException(
                            "RolledBack journal contains an unverified compensation.");
                    }
                }
            }
            if (journal.Status == TransactionStatus.RecoveryFailed &&
                (journal.RollbackResult != "Failed" ||
                 String.IsNullOrWhiteSpace(journal.OriginalError) ||
                 String.IsNullOrWhiteSpace(journal.RecoveryError)))
            {
                throw new InvalidDataException(
                    "RecoveryFailed journal evidence is incomplete.");
            }
            if (journal.Status == TransactionStatus.Committed)
            {
                InstallerMutation[] plan =
                    InstallerTransactionPlan.ForOperation(
                        journal.Operation,
                        journal.Context.RequestFlags);
                if (journal.Intents.Count != plan.Length)
                {
                    throw new InvalidDataException(
                        "Committed journal does not contain the full operation plan.");
                }
                for (int index = 0; index < plan.Length; ++index)
                {
                    if (journal.Intents[index].Mutation != plan[index] ||
                        journal.Intents[index].Status !=
                            CompensationIntentStatus.Applied)
                    {
                        throw new InvalidDataException(
                            "Committed journal plan is incomplete.");
                    }
                }
                if (journal.FinalizationStatus !=
                        TransactionFinalizationStatus.Pending &&
                    journal.FinalizationStatus !=
                        TransactionFinalizationStatus.Complete &&
                    journal.FinalizationStatus !=
                        TransactionFinalizationStatus.Failed)
                {
                    throw new InvalidDataException(
                        "Committed journal finalization state is invalid.");
                }
                if (journal.FinalizationStatus ==
                        TransactionFinalizationStatus.Complete &&
                    String.IsNullOrWhiteSpace(
                        journal.FinalizationEvidence))
                {
                    throw new InvalidDataException(
                        "Completed finalization has no readback evidence.");
                }
            }
        }

        private static void ValidateReadback(
            TransactionJournal expected,
            TransactionJournal actual)
        {
            bool targetMatches =
                expected.Target == null && actual.Target == null;
            if (expected.Target != null && actual.Target != null)
            {
                targetMatches =
                    String.Equals(
                        expected.Target.Version,
                        actual.Target.Version,
                        StringComparison.Ordinal) &&
                    String.Equals(
                        expected.Target.PackageFingerprint,
                        actual.Target.PackageFingerprint,
                        StringComparison.Ordinal);
            }
            if (!String.Equals(
                    actual.TransactionId,
                    expected.TransactionId,
                    StringComparison.Ordinal) ||
                actual.Revision != expected.Revision ||
                actual.Status != expected.Status ||
                actual.Operation != expected.Operation ||
                actual.Intents.Count != expected.Intents.Count ||
                actual.StageEvents.Count != expected.StageEvents.Count ||
                actual.Context == null ||
                expected.Context == null ||
                !String.Equals(
                    actual.Context.TransactionId,
                    expected.Context.TransactionId,
                    StringComparison.Ordinal) ||
                actual.Context.Operation != expected.Context.Operation ||
                !String.Equals(
                    actual.Context.EscrowLocator,
                    expected.Context.EscrowLocator,
                    StringComparison.Ordinal) ||
                actual.Context.RequestFlags.InstallDriver !=
                    expected.Context.RequestFlags.InstallDriver ||
                actual.Context.RequestFlags.CreateShortcut !=
                    expected.Context.RequestFlags.CreateShortcut ||
                actual.Context.RequestFlags.CreateStartupTask !=
                    expected.Context.RequestFlags.CreateStartupTask ||
                actual.Context.RequestFlags.PreserveConfiguration !=
                    expected.Context.RequestFlags.PreserveConfiguration ||
                !String.Equals(
                    actual.Baseline.EvidenceDigest,
                    expected.Baseline.EvidenceDigest,
                    StringComparison.Ordinal) ||
                !targetMatches ||
                !String.Equals(
                    actual.RollbackResult,
                    expected.RollbackResult,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    actual.CreatedUtc,
                    expected.CreatedUtc,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    actual.UpdatedUtc,
                    expected.UpdatedUtc,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    actual.OriginalError,
                    expected.OriginalError,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    actual.RecoveryError,
                    expected.RecoveryError,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    actual.LastError,
                    expected.LastError,
                    StringComparison.Ordinal) ||
                actual.FinalizationStatus != expected.FinalizationStatus ||
                !String.Equals(
                    actual.FinalizationEvidence,
                    expected.FinalizationEvidence,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    actual.FinalizationError,
                    expected.FinalizationError,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    actual.ContentDigest,
                    expected.ContentDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Persisted transaction journal content mismatch.");
            }
            for (int index = 0; index < expected.Intents.Count; ++index)
            {
                CompensationIntent expectedIntent = expected.Intents[index];
                CompensationIntent actualIntent = actual.Intents[index];
                if (actualIntent.Sequence != expectedIntent.Sequence ||
                    actualIntent.Mutation != expectedIntent.Mutation ||
                    actualIntent.Status != expectedIntent.Status ||
                    actualIntent.InverseAction != expectedIntent.InverseAction ||
                    !String.Equals(
                        actualIntent.BeforeEvidence,
                        expectedIntent.BeforeEvidence,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        actualIntent.AfterEvidence,
                        expectedIntent.AfterEvidence,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        actualIntent.RecoveryError,
                        expectedIntent.RecoveryError,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        actualIntent.CompensationBeforeEvidence,
                        expectedIntent.CompensationBeforeEvidence,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Persisted compensation intent mismatch.");
                }
            }
            for (int index = 0; index < expected.StageEvents.Count; ++index)
            {
                TransactionStageEvent expectedEvent = expected.StageEvents[index];
                TransactionStageEvent actualEvent = actual.StageEvents[index];
                if (actualEvent.Sequence != expectedEvent.Sequence ||
                    !String.Equals(
                        actualEvent.TimestampUtc,
                        expectedEvent.TimestampUtc,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        actualEvent.Stage,
                        expectedEvent.Stage,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        actualEvent.Mutation,
                        expectedEvent.Mutation,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        actualEvent.Outcome,
                        expectedEvent.Outcome,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        actualEvent.ObservedEvidence,
                        expectedEvent.ObservedEvidence,
                        StringComparison.Ordinal) ||
                    !String.Equals(
                        actualEvent.Detail,
                        expectedEvent.Detail,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Persisted transaction stage event mismatch.");
                }
            }
        }

        private void InjectRotationFault(TerminalRotationCrashPoint point)
        {
            if (rotationFaultInjector != null)
            {
                rotationFaultInjector.After(point);
            }
        }

        private void InjectSaveFault(JournalSaveCrashPoint point)
        {
            if (saveFaultInjector != null)
            {
                saveFaultInjector.After(point);
            }
        }

        private static string ComputeContentDigest(TransactionJournal journal)
        {
            var builder = new StringBuilder();
            Append(builder, journal.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            Append(builder, journal.TransactionId);
            Append(builder, journal.Operation.ToString());
            Append(builder, journal.Status.ToString());
            Append(builder, journal.Baseline.EvidenceDigest);
            Append(builder, journal.Target == null ? "" : journal.Target.Version);
            Append(builder, journal.Target == null ? "" : journal.Target.PackageFingerprint);
            Append(builder, journal.Revision.ToString(CultureInfo.InvariantCulture));
            Append(builder, journal.CreatedUtc);
            Append(builder, journal.UpdatedUtc);
            Append(builder, journal.LastError);
            Append(builder, journal.OriginalError);
            Append(builder, journal.RecoveryError);
            Append(builder, journal.RollbackResult);
            Append(builder, journal.FinalizationStatus.ToString());
            Append(builder, journal.FinalizationEvidence);
            Append(builder, journal.FinalizationError);
            Append(builder, journal.Context.InvariantDigest);
            foreach (CompensationIntent intent in journal.Intents)
            {
                Append(builder, intent.Sequence.ToString(CultureInfo.InvariantCulture));
                Append(builder, intent.Mutation.ToString());
                Append(builder, intent.Status.ToString());
                Append(builder, intent.InverseAction.ToString());
                Append(builder, intent.BeforeEvidence);
                Append(builder, intent.AfterEvidence);
                Append(builder, intent.RecoveryError);
                Append(builder, intent.CompensationBeforeEvidence);
            }
            foreach (TransactionStageEvent stage in journal.StageEvents)
            {
                Append(builder, stage.Sequence.ToString(CultureInfo.InvariantCulture));
                Append(builder, stage.TimestampUtc);
                Append(builder, stage.Stage);
                Append(builder, stage.Mutation);
                Append(builder, stage.Outcome);
                Append(builder, stage.ObservedEvidence);
                Append(builder, stage.Detail);
            }
            byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] digest = algorithm.ComputeHash(bytes);
                var text = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest)
                {
                    text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }
                return text.ToString();
            }
        }

        private static void Append(StringBuilder builder, string value)
        {
            string safe = value ?? String.Empty;
            builder.Append(safe.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(safe);
            builder.Append('|');
        }
    }
}
