using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SBMSSetup
{
    internal static class ProtectedPayloadBuildContractTests
    {
        private const string TransactionId =
            "00000000000000000000000000000001";
        private const string BuildId =
            "00000000000000000000000000000002";
        private const string QuarantineId =
            "00000000000000000000000000000003";
        private const string PurgeId =
            "00000000000000000000000000000004";
        private const string IntentId =
            "00000000000000000000000000000005";
        private static readonly string HashA = new string('a', 64);
        private static readonly string HashB = new string('b', 64);
        private static readonly string HashC = new string('c', 64);
        private static readonly string HashD = new string('d', 64);
        private static readonly string FileIdA = new string('1', 32);
        private static readonly string FileIdB = new string('2', 32);
        private static readonly string FileIdC = new string('3', 32);
        private static readonly string FileIdD = new string('4', 32);
        private static readonly string FileIdE = new string('5', 32);
        private static int passed;

        public static int Main()
        {
            Run(
                "namespace root identity round-trips",
                NamespaceRootRoundTrips);
            Reject(
                "namespace root rejects a relative path",
                delegate
                {
                    PayloadNamespaceRootIdentity root = NamespaceRoot();
                    root.CanonicalRootPath = @"Program Files";
                    root.Validate();
                });
            Run(
                "pending build entry has no native proof",
                PendingEntryHasNoNativeProof);
            Reject(
                "pending entry rejects a native identity",
                delegate
                {
                    PayloadBuildEntryCheckpoint entry = Entry(
                        0,
                        "SBMS.exe",
                        PayloadBuildEntryPhase.Pending);
                    entry.FileId = FileIdA;
                    entry.Validate();
                });
            Reject(
                "directory rejects file-only write phase",
                delegate
                {
                    PayloadBuildEntryCheckpoint entry = DirectoryEntry(
                        0,
                        "bin",
                        PayloadBuildEntryPhase.Written);
                    entry.Validate();
                });
            Reject(
                "verified file rejects mismatched digest",
                delegate
                {
                    PayloadBuildEntryCheckpoint entry = Entry(
                        0,
                        "SBMS.exe",
                        PayloadBuildEntryPhase.Verified);
                    entry.ObservedSha256 = HashB;
                    entry.Validate();
                });
            Run(
                "verified file binds reopen length and hash",
                VerifiedFileBindsProof);
            Reject(
                "build journal rejects progress after an incomplete entry",
                BuildJournalRejectsNonPrefixProgress);
            Reject(
                "build journal rejects case-colliding paths",
                BuildJournalRejectsCaseCollision);
            Reject(
                "build journal rejects a missing parent directory",
                BuildJournalRejectsMissingParent);
            Reject(
                "build intent rejects skipped write and flush",
                BuildIntentRejectsSkippedProof);
            Reject(
                "seal intent rejects an incomplete tree",
                SealIntentRejectsIncompleteTree);
            Run(
                "seal intent accepts a fully verified tree",
                SealIntentAcceptsVerifiedTree);
            Reject(
                "seal intent rejects a stale tree observation",
                SealIntentRejectsStaleObservation);
            Reject(
                "verified journal rejects changed observed bytes",
                VerifiedJournalRejectsChangedBytes);
            Reject(
                "verified journal rejects a missing observed entry",
                VerifiedJournalRejectsMissingEntry);
            Run(
                "armed root creation admits its exact crash state",
                ArmedRootCreationAdmitsCrashState);
            Reject(
                "absent partial tree rejects native state",
                AbsentPartialTreeRejectsNativeState);
            Reject(
                "partial tree rejects case-colliding paths",
                PartialTreeRejectsCaseCollision);
            Reject(
                "partial tree rejects native identity aliases",
                PartialTreeRejectsIdentityAlias);
            Run(
                "partial tree digest binds observed bytes",
                PartialTreeDigestBindsObservedBytes);
            Reject(
                "workspace rejects one-sided active build",
                WorkspaceRejectsPartialActivePair);
            Reject(
                "workspace rejects changed committed view during build",
                WorkspaceRejectsChangedCommittedView);
            Run(
                "partial build remains outside committed candidate",
                PartialBuildRemainsOutsideCandidate);
            Run(
                "workspace digest binds partial observation",
                WorkspaceDigestBindsPartialObservation);
            Run(
                "workspace digest binds quarantine",
                WorkspaceDigestBindsQuarantine);
            Run(
                "workspace digest binds purge tombstone",
                WorkspaceDigestBindsPurge);
            Reject(
                "workspace rejects active and quarantine identity alias",
                WorkspaceRejectsActiveQuarantineAlias);
            Reject(
                "workspace rejects namespace-root identity alias",
                WorkspaceRejectsNamespaceRootAlias);
            Reject(
                "workspace rejects active build on another volume",
                WorkspaceRejectsCrossVolumeBuild);
            Reject(
                "workspace rejects quarantine on another volume",
                WorkspaceRejectsCrossVolumeQuarantine);
            Reject(
                "workspace rejects one build active and quarantined",
                WorkspaceRejectsActiveQuarantineBuildAlias);
            Reject(
                "workspace rejects unsorted quarantines",
                WorkspaceRejectsUnsortedQuarantines);
            Reject(
                "workspace rejects purge for another quarantine",
                WorkspaceRejectsForeignPurge);
            Reject(
                "workspace rejects purge identity substitution",
                WorkspaceRejectsPurgeIdentitySubstitution);
            Run(
                "whole-workspace CAS accepts exact snapshot",
                WorkspaceCasAcceptsExactSnapshot);
            Reject(
                "whole-workspace CAS rejects changed observation",
                WorkspaceCasRejectsChangedObservation);
            Reject(
                "whole-workspace CAS rejects stale revision",
                WorkspaceCasRejectsStaleRevision);
            Reject(
                "whole-workspace CAS rejects another transaction",
                WorkspaceCasRejectsAnotherTransaction);
            Run(
                "workspace state deep-clones mutable data",
                WorkspaceStateDeepClones);
            Run(
                "workspace checkpoint JSON round-trips",
                WorkspaceCheckpointRoundTrips);
            Reject(
                "CAS JSON rejects missing required revision",
                CasJsonRejectsMissingRevision);
            Run(
                "CAS digest binds full workspace digest",
                CasDigestBindsWorkspaceDigest);
            Run(
                "quarantine name is deterministic",
                QuarantineNameIsDeterministic);
            Reject(
                "quarantine committed source rejects build ID",
                CommittedQuarantineRejectsBuildId);
            Reject(
                "quarantine rejects a substituted source leaf",
                QuarantineRejectsSubstitutedSourceLeaf);
            Reject(
                "purge phase rejects unknown value",
                PurgeRejectsUnknownPhase);
            Reject(
                "observed-absent purge requires bound evidence",
                ObservedAbsentPurgeRequiresEvidence);
            Run(
                "observed-absent purge binds evidence and revision",
                ObservedAbsentPurgeBindsEvidence);
            Run(
                "quarantine receipt binds exact source rename",
                QuarantineReceiptBindsExactSource);
            Reject(
                "quarantine receipt rejects an unarmed rename",
                QuarantineReceiptRejectsUnarmedRename);
            Reject(
                "quarantine receipt rejects stale armed observation",
                QuarantineReceiptRejectsStaleIntent);
            Reject(
                "quarantine receipt rejects source identity substitution",
                QuarantineReceiptRejectsIdentitySubstitution);
            Reject(
                "quarantine receipt rejects committed mutation",
                QuarantineReceiptRejectsCommittedMutation);
            Run(
                "purge arm receipt adds one identity-bound tombstone",
                PurgeArmReceiptBindsTombstone);
            Run(
                "purge absence receipt binds fresh absent observation",
                PurgeAbsenceReceiptBindsFreshObservation);
            Reject(
                "purge absence receipt rejects stale evidence",
                PurgeAbsenceReceiptRejectsStaleEvidence);
            Run(
                "purge completion removes exact tombstone and quarantine",
                PurgeCompletionRemovesExactState);
            Reject(
                "purge completion rejects an armed-only tombstone",
                PurgeCompletionRejectsArmedTombstone);

            Console.WriteLine(
                "Protected payload build contracts passed: " +
                passed + " tests.");
            return 0;
        }

        private static void NamespaceRootRoundTrips()
        {
            PayloadNamespaceRootIdentity root = NamespaceRoot();
            string digest = root.InvariantDigest;
            PayloadNamespaceRootIdentity clone = root.DeepClone();
            Equal(digest, clone.InvariantDigest);
            clone.RootFileId = FileIdB;
            NotEqual(digest, clone.InvariantDigest);
        }

        private static void PendingEntryHasNoNativeProof()
        {
            PayloadBuildEntryCheckpoint entry = Entry(
                0,
                "SBMS.exe",
                PayloadBuildEntryPhase.Pending);
            entry.Validate();
            Equal(String.Empty, entry.FileId);
            Equal(-1L, entry.ObservedLength);
            Equal(String.Empty, entry.ObservedSha256);
        }

        private static void VerifiedFileBindsProof()
        {
            PayloadBuildEntryCheckpoint entry = Entry(
                0,
                "SBMS.exe",
                PayloadBuildEntryPhase.Verified);
            entry.Validate();
            Equal(4L, entry.ObservedLength);
            Equal(HashA, entry.ObservedSha256);
        }

        private static void BuildJournalRejectsNonPrefixProgress()
        {
            PayloadCandidateBuildJournal journal = Journal(false);
            journal.Entries[0] = DirectoryEntry(
                0,
                "bin",
                PayloadBuildEntryPhase.Pending);
            journal.Entries[1] = Entry(
                1,
                @"bin\SBMS.exe",
                PayloadBuildEntryPhase.Created);
            AttestRoot(journal);
            journal.Validate();
        }

        private static void BuildJournalRejectsCaseCollision()
        {
            PayloadCandidateBuildJournal journal = Journal(false);
            journal.Entries = new List<PayloadBuildEntryCheckpoint>
            {
                DirectoryEntry(
                    0,
                    "Bin",
                    PayloadBuildEntryPhase.Pending),
                DirectoryEntry(
                    1,
                    "bin",
                    PayloadBuildEntryPhase.Pending)
            };
            journal.Validate();
        }

        private static void BuildJournalRejectsMissingParent()
        {
            PayloadCandidateBuildJournal journal = Journal(false);
            journal.Entries = new List<PayloadBuildEntryCheckpoint>
            {
                Entry(
                    0,
                    @"bin\SBMS.exe",
                    PayloadBuildEntryPhase.Pending)
            };
            journal.Validate();
        }

        private static void BuildIntentRejectsSkippedProof()
        {
            PayloadCandidateBuildJournal journal = Journal(false);
            journal.Entries[0] = DirectoryEntry(
                0,
                "bin",
                PayloadBuildEntryPhase.Verified);
            journal.Entries[1] = Entry(
                1,
                @"bin\SBMS.exe",
                PayloadBuildEntryPhase.Created);
            AttestRoot(journal);
            journal.ActiveIntent = Intent(
                journal,
                PayloadBuildStepKind.VerifyEntryHash,
                1,
                AbsentObservation().InvariantDigest);
            journal.Validate();
        }

        private static void SealIntentRejectsIncompleteTree()
        {
            PayloadCandidateBuildJournal journal = Journal(false);
            journal.ActiveIntent = Intent(
                journal,
                PayloadBuildStepKind.SealCandidate,
                -1,
                AbsentObservation().InvariantDigest);
            journal.Validate();
        }

        private static void SealIntentAcceptsVerifiedTree()
        {
            PayloadCandidateBuildJournal journal = Journal(true);
            journal.ActiveIntent = Intent(
                journal,
                PayloadBuildStepKind.SealCandidate,
                -1,
                PresentObservation().InvariantDigest);
            journal.Validate();
            Equal(true, journal.AllEntriesVerified);
        }

        private static void SealIntentRejectsStaleObservation()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(true, false, false);
            AttachPresentObservation(workspace);
            workspace.ActiveBuild.ActiveIntent = Intent(
                workspace.ActiveBuild,
                PayloadBuildStepKind.SealCandidate,
                -1,
                HashC);
            workspace.Validate();
        }

        private static void VerifiedJournalRejectsChangedBytes()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(true, false, false);
            AttachPresentObservation(workspace);
            workspace.ActivePartialTree.Entries[1].Sha256 = HashB;
            workspace.Validate();
        }

        private static void VerifiedJournalRejectsMissingEntry()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(true, false, false);
            AttachPresentObservation(workspace);
            workspace.ActivePartialTree.Entries.RemoveAt(1);
            workspace.Validate();
        }

        private static void ArmedRootCreationAdmitsCrashState()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(true, false, false);
            PayloadPartialTreeObservation before =
                workspace.ActivePartialTree.DeepClone();
            workspace.ActiveBuild.ActiveIntent = Intent(
                workspace.ActiveBuild,
                PayloadBuildStepKind.CreateRoot,
                -1,
                before.InvariantDigest);
            workspace.ActivePartialTree = EmptyPresentObservation();
            workspace.Validate();
        }

        private static void AbsentPartialTreeRejectsNativeState()
        {
            PayloadPartialTreeObservation observation =
                AbsentObservation();
            observation.RootFileId = FileIdA;
            observation.Validate();
        }

        private static void PartialTreeRejectsCaseCollision()
        {
            PayloadPartialTreeObservation observation =
                PresentObservation();
            observation.Entries = new List<PayloadTreeEntryCheckpoint>
            {
                TreeEntry("Bin", true, FileIdB, 0, String.Empty),
                TreeEntry("bin", true, FileIdC, 0, String.Empty)
            };
            observation.Validate();
        }

        private static void PartialTreeRejectsIdentityAlias()
        {
            PayloadPartialTreeObservation observation =
                PresentObservation();
            observation.Entries[0].FileId = observation.RootFileId;
            observation.Validate();
        }

        private static void PartialTreeDigestBindsObservedBytes()
        {
            PayloadPartialTreeObservation first =
                PresentObservation();
            PayloadPartialTreeObservation second =
                first.DeepClone();
            second.Entries[1].Sha256 = HashB;
            NotEqual(first.InvariantDigest, second.InvariantDigest);
        }

        private static void WorkspaceRejectsPartialActivePair()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(false, false, false);
            workspace.ActiveBuild = Journal(false);
            workspace.Validate();
        }

        private static void WorkspaceRejectsChangedCommittedView()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(true, false, false);
            workspace.Committed.Revision++;
            workspace.Validate();
        }

        private static void PartialBuildRemainsOutsideCandidate()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(true, false, false);
            workspace.Validate();
            Equal(null, workspace.Committed.Candidate);
            if (workspace.ActiveBuild.BuildLeafName.Contains(
                    ".candidate."))
            {
                throw new InvalidOperationException(
                    "Partial build reused the sealed candidate namespace.");
            }
        }

        private static void WorkspaceDigestBindsPartialObservation()
        {
            PayloadBuildWorkspaceCheckpoint first =
                Workspace(true, false, false);
            PayloadBuildWorkspaceCheckpoint second =
                first.DeepClone();
            AttachPresentObservation(second);
            NotEqual(first.InvariantDigest, second.InvariantDigest);
        }

        private static void WorkspaceDigestBindsQuarantine()
        {
            PayloadBuildWorkspaceCheckpoint first =
                Workspace(false, false, false);
            PayloadBuildWorkspaceCheckpoint second =
                Workspace(false, true, false);
            NotEqual(first.InvariantDigest, second.InvariantDigest);
        }

        private static void WorkspaceDigestBindsPurge()
        {
            PayloadBuildWorkspaceCheckpoint first =
                Workspace(false, true, false);
            PayloadBuildWorkspaceCheckpoint second =
                Workspace(false, true, true);
            NotEqual(first.InvariantDigest, second.InvariantDigest);
        }

        private static void WorkspaceRejectsActiveQuarantineAlias()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(true, true, false);
            AttachPresentObservation(workspace);
            workspace.Quarantines[0].RootFileId =
                workspace.ActivePartialTree.RootFileId;
            workspace.Quarantines[0].VolumeSerialNumber =
                workspace.ActivePartialTree.VolumeSerialNumber;
            workspace.Validate();
        }

        private static void WorkspaceRejectsNamespaceRootAlias()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(true, false, false);
            AttachPresentObservation(workspace);
            workspace.ActiveBuild.RootFileId =
                workspace.NamespaceRoot.RootFileId;
            workspace.ActivePartialTree.RootFileId =
                workspace.NamespaceRoot.RootFileId;
            workspace.Validate();
        }

        private static void WorkspaceRejectsCrossVolumeBuild()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(true, false, false);
            AttachPresentObservation(workspace);
            workspace.ActiveBuild.RootVolumeSerialNumber = 0x9999UL;
            workspace.ActivePartialTree.VolumeSerialNumber = 0x9999UL;
            workspace.Validate();
        }

        private static void WorkspaceRejectsCrossVolumeQuarantine()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(false, true, false);
            workspace.Quarantines[0].VolumeSerialNumber = 0x9999UL;
            workspace.Validate();
        }

        private static void WorkspaceRejectsActiveQuarantineBuildAlias()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(true, true, false);
            workspace.Quarantines[0].SourceBuildId = BuildId;
            workspace.Validate();
        }

        private static void WorkspaceRejectsUnsortedQuarantines()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(false, true, false);
            PayloadQuarantineCheckpoint second = Quarantine();
            second.QuarantineId =
                "00000000000000000000000000000002";
            second.QuarantineLeafName =
                ".SBMS.quarantine." + second.QuarantineId;
            second.RootFileId = FileIdC;
            workspace.Quarantines.Add(second);
            workspace.Validate();
        }

        private static void WorkspaceRejectsForeignPurge()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(false, true, true);
            workspace.PendingPurges[0].QuarantineId =
                "00000000000000000000000000000009";
            workspace.Validate();
        }

        private static void WorkspaceRejectsPurgeIdentitySubstitution()
        {
            PayloadBuildWorkspaceCheckpoint workspace =
                Workspace(false, true, true);
            workspace.PendingPurges[0].RootFileId = FileIdC;
            workspace.Validate();
        }

        private static void WorkspaceCasAcceptsExactSnapshot()
        {
            var state = new PayloadBuildWorkspaceState(
                Workspace(true, true, true));
            state.RequireCas(state.CasToken);
        }

        private static void WorkspaceCasRejectsChangedObservation()
        {
            var state = new PayloadBuildWorkspaceState(
                Workspace(true, false, false));
            PayloadWorkspaceCasToken token = state.CasToken;
            PayloadBuildWorkspaceCheckpoint changed = state.Checkpoint;
            AttachPresentObservation(changed);
            var changedState = new PayloadBuildWorkspaceState(changed);
            changedState.RequireCas(token);
        }

        private static void WorkspaceCasRejectsStaleRevision()
        {
            var state = new PayloadBuildWorkspaceState(
                Workspace(false, false, false));
            PayloadWorkspaceCasToken token = state.CasToken;
            token.Revision--;
            state.RequireCas(token);
        }

        private static void WorkspaceCasRejectsAnotherTransaction()
        {
            var state = new PayloadBuildWorkspaceState(
                Workspace(false, false, false));
            PayloadWorkspaceCasToken token = state.CasToken;
            token.TransactionId =
                "00000000000000000000000000000009";
            state.RequireCas(token);
        }

        private static void WorkspaceStateDeepClones()
        {
            var state = new PayloadBuildWorkspaceState(
                Workspace(true, true, true));
            string digest = state.InvariantDigest;
            PayloadBuildWorkspaceCheckpoint exposed =
                state.Checkpoint;
            exposed.ActiveBuild.Revision++;
            exposed.Quarantines[0].Reason =
                PayloadQuarantineReason.Cleanup;
            Equal(digest, state.InvariantDigest);
        }

        private static void WorkspaceCheckpointRoundTrips()
        {
            PayloadBuildWorkspaceCheckpoint source =
                Workspace(true, true, true);
            string digest = source.InvariantDigest;
            PayloadBuildWorkspaceCheckpoint roundTrip =
                RoundTrip(source);
            Equal(digest, roundTrip.InvariantDigest);
        }

        private static void CasJsonRejectsMissingRevision()
        {
            string json =
                "{\"SchemaVersion\":1," +
                "\"TransactionId\":\"" + TransactionId + "\"," +
                "\"WorkspaceInvariantDigest\":\"" + HashA + "\"}";
            var serializer = new DataContractJsonSerializer(
                typeof(PayloadWorkspaceCasToken));
            using (var stream = new MemoryStream(
                Encoding.UTF8.GetBytes(json)))
            {
                serializer.ReadObject(stream);
            }
        }

        private static void CasDigestBindsWorkspaceDigest()
        {
            PayloadBuildWorkspaceState first =
                new PayloadBuildWorkspaceState(
                    Workspace(false, false, false));
            PayloadWorkspaceCasToken second =
                first.CasToken.DeepClone();
            second.WorkspaceInvariantDigest = HashB;
            NotEqual(
                first.CasToken.InvariantDigest,
                second.InvariantDigest);
        }

        private static void QuarantineNameIsDeterministic()
        {
            PayloadQuarantineCheckpoint quarantine = Quarantine();
            quarantine.Validate();
            Equal(
                ".SBMS.quarantine." + QuarantineId,
                quarantine.QuarantineLeafName);
        }

        private static void CommittedQuarantineRejectsBuildId()
        {
            PayloadQuarantineCheckpoint quarantine = Quarantine();
            quarantine.SourceKind =
                PayloadQuarantineSourceKind.Candidate;
            quarantine.Validate();
        }

        private static void QuarantineRejectsSubstitutedSourceLeaf()
        {
            PayloadQuarantineCheckpoint quarantine = Quarantine();
            quarantine.SourceLeafName =
                ".SBMS.build.00000000000000000000000000000009";
            quarantine.Validate();
        }

        private static void PurgeRejectsUnknownPhase()
        {
            PayloadPurgeCheckpoint purge = Purge(Quarantine());
            purge.Phase = (PayloadPurgePhase)42;
            purge.Validate();
        }

        private static void ObservedAbsentPurgeRequiresEvidence()
        {
            PayloadPurgeCheckpoint purge = Purge(Quarantine());
            purge.Phase = PayloadPurgePhase.ObservedAbsent;
            purge.Validate();
        }

        private static void ObservedAbsentPurgeBindsEvidence()
        {
            PayloadPurgeCheckpoint purge = Purge(Quarantine());
            purge.Phase = PayloadPurgePhase.ObservedAbsent;
            purge.AbsenceObservationInvariantDigest = HashC;
            purge.AbsenceObservedAtWorkspaceRevision = 12;
            purge.Validate();
            Equal(HashC, purge.AbsenceObservationInvariantDigest);
            Equal(12L, purge.AbsenceObservedAtWorkspaceRevision);
        }

        private static void QuarantineReceiptBindsExactSource()
        {
            PayloadBuildWorkspaceCheckpoint beforeCheckpoint =
                Workspace(true, false, false);
            AttachPresentObservation(beforeCheckpoint);
            ArmQuarantine(beforeCheckpoint);
            PayloadBuildWorkspaceCheckpoint afterCheckpoint =
                QuarantineAfter(beforeCheckpoint);
            var receipt = new PayloadQuarantineReceipt(
                Authority(),
                new PayloadBuildWorkspaceState(beforeCheckpoint),
                new PayloadBuildWorkspaceState(afterCheckpoint),
                QuarantineId);
            Equal(QuarantineId, receipt.QuarantineId);
        }

        private static void QuarantineReceiptRejectsUnarmedRename()
        {
            PayloadBuildWorkspaceCheckpoint beforeCheckpoint =
                Workspace(true, false, false);
            AttachPresentObservation(beforeCheckpoint);
            PayloadBuildWorkspaceCheckpoint afterCheckpoint =
                QuarantineAfter(beforeCheckpoint);
            new PayloadQuarantineReceipt(
                Authority(),
                new PayloadBuildWorkspaceState(beforeCheckpoint),
                new PayloadBuildWorkspaceState(afterCheckpoint),
                QuarantineId);
        }

        private static void QuarantineReceiptRejectsStaleIntent()
        {
            PayloadBuildWorkspaceCheckpoint beforeCheckpoint =
                Workspace(true, false, false);
            AttachPresentObservation(beforeCheckpoint);
            ArmQuarantine(beforeCheckpoint);
            beforeCheckpoint.ActiveBuild.ActiveIntent.
                ObservedPartialTreeInvariantDigest = HashB;
            PayloadBuildWorkspaceCheckpoint afterCheckpoint =
                QuarantineAfter(beforeCheckpoint);
            new PayloadQuarantineReceipt(
                Authority(),
                new PayloadBuildWorkspaceState(beforeCheckpoint),
                new PayloadBuildWorkspaceState(afterCheckpoint),
                QuarantineId);
        }

        private static void QuarantineReceiptRejectsIdentitySubstitution()
        {
            PayloadBuildWorkspaceCheckpoint beforeCheckpoint =
                Workspace(true, false, false);
            AttachPresentObservation(beforeCheckpoint);
            ArmQuarantine(beforeCheckpoint);
            PayloadBuildWorkspaceCheckpoint afterCheckpoint =
                QuarantineAfter(beforeCheckpoint);
            afterCheckpoint.Quarantines[0].RootFileId = FileIdE;
            new PayloadQuarantineReceipt(
                Authority(),
                new PayloadBuildWorkspaceState(beforeCheckpoint),
                new PayloadBuildWorkspaceState(afterCheckpoint),
                QuarantineId);
        }

        private static void QuarantineReceiptRejectsCommittedMutation()
        {
            PayloadBuildWorkspaceCheckpoint beforeCheckpoint =
                Workspace(true, false, false);
            AttachPresentObservation(beforeCheckpoint);
            ArmQuarantine(beforeCheckpoint);
            PayloadBuildWorkspaceCheckpoint afterCheckpoint =
                QuarantineAfter(beforeCheckpoint);
            afterCheckpoint.Committed.Revision++;
            new PayloadQuarantineReceipt(
                Authority(),
                new PayloadBuildWorkspaceState(beforeCheckpoint),
                new PayloadBuildWorkspaceState(afterCheckpoint),
                QuarantineId);
        }

        private static void PurgeArmReceiptBindsTombstone()
        {
            PayloadBuildWorkspaceCheckpoint beforeCheckpoint =
                Workspace(false, true, false);
            PayloadBuildWorkspaceCheckpoint afterCheckpoint =
                beforeCheckpoint.DeepClone();
            afterCheckpoint.Revision++;
            afterCheckpoint.PendingPurges.Add(
                Purge(afterCheckpoint.Quarantines[0]));
            var receipt = new PayloadPurgeReceipt(
                Authority(),
                new PayloadBuildWorkspaceState(beforeCheckpoint),
                new PayloadBuildWorkspaceState(afterCheckpoint),
                PayloadPurgeTransitionKind.Arm,
                PurgeId,
                QuarantineId,
                null);
            Equal(false, receipt.Complete);
        }

        private static void PurgeAbsenceReceiptBindsFreshObservation()
        {
            PayloadBuildWorkspaceCheckpoint beforeCheckpoint =
                Workspace(false, true, true);
            PayloadQuarantineAbsenceObservation observation =
                Absence(beforeCheckpoint);
            PayloadBuildWorkspaceCheckpoint afterCheckpoint =
                ObserveAbsentAfter(
                    beforeCheckpoint,
                    observation);
            var receipt = new PayloadPurgeReceipt(
                Authority(),
                new PayloadBuildWorkspaceState(beforeCheckpoint),
                new PayloadBuildWorkspaceState(afterCheckpoint),
                PayloadPurgeTransitionKind.ObserveAbsent,
                PurgeId,
                QuarantineId,
                observation);
            Equal(false, receipt.Complete);
            Equal(
                observation.InvariantDigest,
                receipt.AbsenceObservation.InvariantDigest);
        }

        private static void PurgeAbsenceReceiptRejectsStaleEvidence()
        {
            PayloadBuildWorkspaceCheckpoint beforeCheckpoint =
                Workspace(false, true, true);
            PayloadQuarantineAbsenceObservation observation =
                Absence(beforeCheckpoint);
            PayloadBuildWorkspaceCheckpoint afterCheckpoint =
                ObserveAbsentAfter(
                    beforeCheckpoint,
                    observation);
            observation.ObservedAtWorkspaceRevision--;
            new PayloadPurgeReceipt(
                Authority(),
                new PayloadBuildWorkspaceState(beforeCheckpoint),
                new PayloadBuildWorkspaceState(afterCheckpoint),
                PayloadPurgeTransitionKind.ObserveAbsent,
                PurgeId,
                QuarantineId,
                observation);
        }

        private static void PurgeCompletionRemovesExactState()
        {
            PayloadBuildWorkspaceCheckpoint armed =
                Workspace(false, true, true);
            PayloadQuarantineAbsenceObservation observation =
                Absence(armed);
            PayloadBuildWorkspaceCheckpoint beforeCheckpoint =
                ObserveAbsentAfter(armed, observation);
            PayloadBuildWorkspaceCheckpoint afterCheckpoint =
                beforeCheckpoint.DeepClone();
            afterCheckpoint.Revision++;
            PayloadQuarantineCheckpoint quarantine =
                beforeCheckpoint.Quarantines[0];
            PayloadQuarantineAbsenceObservation completionObservation =
                Absence(beforeCheckpoint);
            afterCheckpoint.PendingPurges.Clear();
            afterCheckpoint.Quarantines.Clear();
            afterCheckpoint.CompletedPurges.Add(
                new PayloadCompletedPurgeCheckpoint
                {
                    SchemaVersion = 1,
                    PurgeId = PurgeId,
                    QuarantineId = QuarantineId,
                    TransactionId = TransactionId,
                    RecoveryAuthorityInvariantDigest =
                        Authority().InvariantDigest,
                    NamespaceRootInvariantDigest =
                        NamespaceRoot().InvariantDigest,
                    Quarantine = quarantine.DeepClone(),
                    AbsenceObservation =
                        completionObservation.DeepClone(),
                    CompletedAtWorkspaceRevision =
                        afterCheckpoint.Revision
                });
            var receipt = new PayloadPurgeReceipt(
                Authority(),
                new PayloadBuildWorkspaceState(beforeCheckpoint),
                new PayloadBuildWorkspaceState(afterCheckpoint),
                PayloadPurgeTransitionKind.Complete,
                PurgeId,
                QuarantineId,
                completionObservation);
            Equal(true, receipt.Complete);
        }

        private static void PurgeCompletionRejectsArmedTombstone()
        {
            PayloadBuildWorkspaceCheckpoint beforeCheckpoint =
                Workspace(false, true, true);
            PayloadBuildWorkspaceCheckpoint afterCheckpoint =
                beforeCheckpoint.DeepClone();
            afterCheckpoint.Revision++;
            afterCheckpoint.PendingPurges.Clear();
            afterCheckpoint.Quarantines.Clear();
            new PayloadPurgeReceipt(
                Authority(),
                new PayloadBuildWorkspaceState(beforeCheckpoint),
                new PayloadBuildWorkspaceState(afterCheckpoint),
                PayloadPurgeTransitionKind.Complete,
                PurgeId,
                QuarantineId,
                null);
        }

        private static PayloadNamespaceRootIdentity NamespaceRoot()
        {
            return new PayloadNamespaceRootIdentity
            {
                SchemaVersion = 1,
                CanonicalRootPath = @"C:\Program Files",
                VolumeSerialNumber = 0x1234UL,
                RootFileId = FileIdA
            };
        }

        private static PayloadRecoveryAuthority Authority()
        {
            return new PayloadRecoveryAuthority
            {
                SchemaVersion = 1,
                TransactionId = TransactionId,
                Operation = InstallOperation.FreshInstall,
                BaselineState = BaselinePayloadState.Absent,
                Baseline = null,
                Target = new PayloadContentAuthority
                {
                    Release = new ReleaseIdentity("0.3.0", HashA),
                    ContentSetSha256 = HashA,
                    ManifestInvariantDigest = HashB,
                    SemanticTreeSha256 = HashC,
                    FileCount = 1,
                    TotalBytes = 4
                },
                SealedEscrowManifestSha256 = HashD
            };
        }

        private static PayloadBuildWorkspaceCheckpoint QuarantineAfter(
            PayloadBuildWorkspaceCheckpoint before)
        {
            PayloadBuildWorkspaceCheckpoint after =
                before.DeepClone();
            after.Revision++;
            PayloadPartialTreeObservation source =
                before.ActivePartialTree;
            after.ActiveBuild = null;
            after.ActivePartialTree = null;
            PayloadQuarantineCheckpoint quarantine = Quarantine();
            quarantine.SourceBuildId = before.ActiveBuild.BuildId;
            quarantine.SourceLeafName = source.LeafName;
            quarantine.VolumeSerialNumber = source.VolumeSerialNumber;
            quarantine.RootFileId = source.RootFileId;
            quarantine.PartialTreeInvariantDigest =
                source.InvariantDigest;
            after.Quarantines.Add(quarantine);
            return after;
        }

        private static PayloadQuarantineAbsenceObservation Absence(
            PayloadBuildWorkspaceCheckpoint before)
        {
            PayloadQuarantineCheckpoint quarantine =
                before.Quarantines[0];
            return new PayloadQuarantineAbsenceObservation
            {
                SchemaVersion = 1,
                TransactionId = before.TransactionId,
                RecoveryAuthorityInvariantDigest =
                    before.RecoveryAuthorityInvariantDigest,
                NamespaceRootInvariantDigest =
                    before.NamespaceRoot.InvariantDigest,
                QuarantineId = quarantine.QuarantineId,
                QuarantineLeafName =
                    quarantine.QuarantineLeafName,
                VolumeSerialNumber =
                    quarantine.VolumeSerialNumber,
                RootFileId = quarantine.RootFileId,
                ObservedAtWorkspaceRevision = before.Revision,
                Exists = false
            };
        }

        private static PayloadBuildWorkspaceCheckpoint
            ObserveAbsentAfter(
                PayloadBuildWorkspaceCheckpoint before,
                PayloadQuarantineAbsenceObservation observation)
        {
            PayloadBuildWorkspaceCheckpoint after =
                before.DeepClone();
            after.Revision++;
            PayloadPurgeCheckpoint purge =
                after.PendingPurges[0];
            purge.Phase = PayloadPurgePhase.ObservedAbsent;
            purge.AbsenceObservationInvariantDigest =
                observation.InvariantDigest;
            purge.AbsenceObservedAtWorkspaceRevision =
                before.Revision;
            return after;
        }

        private static PayloadNamespaceCheckpoint EmptyCommitted()
        {
            return new PayloadNamespaceCheckpoint
            {
                SchemaVersion = 1,
                Revision = 7,
                TransactionId = TransactionId,
                Shape = PayloadNamespaceShape.Empty
            };
        }

        private static PayloadBuildEntryCheckpoint DirectoryEntry(
            int ordinal,
            string path,
            PayloadBuildEntryPhase phase)
        {
            bool created =
                phase != PayloadBuildEntryPhase.Pending;
            bool reopened =
                phase == PayloadBuildEntryPhase.Reopened ||
                phase == PayloadBuildEntryPhase.Verified;
            return new PayloadBuildEntryCheckpoint
            {
                Ordinal = ordinal,
                RelativePath = path,
                IsDirectory = true,
                ExpectedLength = 0,
                ExpectedSha256 = String.Empty,
                Phase = phase,
                FileId = created ? FileIdB : String.Empty,
                ObservedLength = reopened ? 0 : -1,
                ObservedSha256 = String.Empty
            };
        }

        private static PayloadBuildEntryCheckpoint Entry(
            int ordinal,
            string path,
            PayloadBuildEntryPhase phase)
        {
            bool created =
                phase != PayloadBuildEntryPhase.Pending;
            bool reopened =
                phase == PayloadBuildEntryPhase.Reopened ||
                phase == PayloadBuildEntryPhase.Verified;
            return new PayloadBuildEntryCheckpoint
            {
                Ordinal = ordinal,
                RelativePath = path,
                IsDirectory = false,
                ExpectedLength = 4,
                ExpectedSha256 = HashA,
                Phase = phase,
                FileId = created ? FileIdC : String.Empty,
                ObservedLength = reopened ? 4 : -1,
                ObservedSha256 =
                    phase == PayloadBuildEntryPhase.Verified
                        ? HashA
                        : String.Empty
            };
        }

        private static PayloadCandidateBuildJournal Journal(
            bool verified)
        {
            PayloadBuildEntryPhase directoryPhase =
                verified
                    ? PayloadBuildEntryPhase.Verified
                    : PayloadBuildEntryPhase.Pending;
            PayloadBuildEntryPhase filePhase =
                verified
                    ? PayloadBuildEntryPhase.Verified
                    : PayloadBuildEntryPhase.Pending;
            return new PayloadCandidateBuildJournal
            {
                SchemaVersion = 1,
                Revision = 3,
                BuildId = BuildId,
                TransactionId = TransactionId,
                RecoveryAuthorityInvariantDigest =
                    Authority().InvariantDigest,
                TargetManifestInvariantDigest = HashA,
                SourceReceiptInvariantDigest = HashB,
                NamespaceRootInvariantDigest =
                    NamespaceRoot().InvariantDigest,
                InitialCommittedRevision = 7,
                InitialCommittedInvariantDigest =
                    EmptyCommitted().InvariantDigest,
                BuildLeafName = ".SBMS.build." + BuildId,
                RootVolumeSerialNumber =
                    verified ? 0x1234UL : 0,
                RootFileId =
                    verified ? FileIdD : String.Empty,
                Entries = new List<PayloadBuildEntryCheckpoint>
                {
                    DirectoryEntry(0, "bin", directoryPhase),
                    Entry(1, @"bin\SBMS.exe", filePhase)
                }
            };
        }

        private static PayloadBuildStepIntent Intent(
            PayloadCandidateBuildJournal journal,
            PayloadBuildStepKind kind,
            int ordinal,
            string observationDigest)
        {
            return new PayloadBuildStepIntent
            {
                SchemaVersion = 1,
                IntentId = IntentId,
                JournalRevision = journal.Revision,
                Kind = kind,
                EntryOrdinal = ordinal,
                ExpectedEntryInvariantDigest =
                    ordinal < 0
                        ? String.Empty
                        : journal.Entries[ordinal].InvariantDigest,
                ObservedPartialTreeInvariantDigest =
                    observationDigest
            };
        }

        private static PayloadPartialTreeObservation AbsentObservation()
        {
            return new PayloadPartialTreeObservation
            {
                SchemaVersion = 1,
                BuildId = BuildId,
                LeafName = ".SBMS.build." + BuildId,
                Exists = false,
                VolumeSerialNumber = 0,
                RootFileId = String.Empty,
                Entries = new List<PayloadTreeEntryCheckpoint>()
            };
        }

        private static PayloadPartialTreeObservation PresentObservation()
        {
            return new PayloadPartialTreeObservation
            {
                SchemaVersion = 1,
                BuildId = BuildId,
                LeafName = ".SBMS.build." + BuildId,
                Exists = true,
                VolumeSerialNumber = 0x1234UL,
                RootFileId = FileIdD,
                Entries = new List<PayloadTreeEntryCheckpoint>
                {
                    TreeEntry("bin", true, FileIdB, 0, String.Empty),
                    TreeEntry(
                        @"bin\SBMS.exe",
                        false,
                        FileIdC,
                        4,
                        HashA)
                }
            };
        }

        private static PayloadPartialTreeObservation EmptyPresentObservation()
        {
            return new PayloadPartialTreeObservation
            {
                SchemaVersion = 1,
                BuildId = BuildId,
                LeafName = ".SBMS.build." + BuildId,
                Exists = true,
                VolumeSerialNumber = 0x1234UL,
                RootFileId = FileIdD,
                Entries = new List<PayloadTreeEntryCheckpoint>()
            };
        }

        private static PayloadTreeEntryCheckpoint TreeEntry(
            string path,
            bool directory,
            string fileId,
            long length,
            string sha256)
        {
            return new PayloadTreeEntryCheckpoint
            {
                RelativePath = path,
                IsDirectory = directory,
                FileId = fileId,
                Length = length,
                Sha256 = sha256
            };
        }

        private static PayloadQuarantineCheckpoint Quarantine()
        {
            return new PayloadQuarantineCheckpoint
            {
                SchemaVersion = 1,
                QuarantineId = QuarantineId,
                TransactionId = TransactionId,
                RecoveryAuthorityInvariantDigest =
                    Authority().InvariantDigest,
                NamespaceRootInvariantDigest =
                    NamespaceRoot().InvariantDigest,
                SourceKind =
                    PayloadQuarantineSourceKind.PartialBuild,
                SourceBuildId = BuildId,
                QuarantineLeafName =
                    ".SBMS.quarantine." + QuarantineId,
                VolumeSerialNumber = 0x1234UL,
                RootFileId = FileIdD,
                PartialTreeInvariantDigest =
                    PresentObservation().InvariantDigest,
                Reason = PayloadQuarantineReason.HashMismatch,
                SourceLeafName = ".SBMS.build." + BuildId,
                TargetManifestInvariantDigest = HashA,
                SourceReceiptInvariantDigest = HashB
            };
        }

        private static PayloadPurgeCheckpoint Purge(
            PayloadQuarantineCheckpoint quarantine)
        {
            return new PayloadPurgeCheckpoint
            {
                SchemaVersion = 1,
                PurgeId = PurgeId,
                QuarantineId = quarantine.QuarantineId,
                TransactionId = TransactionId,
                RecoveryAuthorityInvariantDigest =
                    Authority().InvariantDigest,
                NamespaceRootInvariantDigest =
                    NamespaceRoot().InvariantDigest,
                QuarantineInvariantDigest =
                    quarantine.InvariantDigest,
                VolumeSerialNumber = quarantine.VolumeSerialNumber,
                RootFileId = quarantine.RootFileId,
                Phase = PayloadPurgePhase.Armed,
                AbsenceObservationInvariantDigest = String.Empty,
                AbsenceObservedAtWorkspaceRevision = -1
            };
        }

        private static PayloadBuildWorkspaceCheckpoint Workspace(
            bool active,
            bool quarantine,
            bool purge)
        {
            var checkpoint = new PayloadBuildWorkspaceCheckpoint
            {
                SchemaVersion = 2,
                Revision = 11,
                TransactionId = TransactionId,
                RecoveryAuthorityInvariantDigest =
                    Authority().InvariantDigest,
                NamespaceRoot = NamespaceRoot(),
                Committed = EmptyCommitted(),
                ActiveBuild = active ? Journal(false) : null,
                ActivePartialTree =
                    active ? AbsentObservation() : null
            };
            if (quarantine)
            {
                PayloadQuarantineCheckpoint item = Quarantine();
                if (active)
                {
                    item.SourceBuildId =
                        "00000000000000000000000000000006";
                    item.SourceLeafName =
                        ".SBMS.build." + item.SourceBuildId;
                    item.RootFileId = FileIdE;
                }
                checkpoint.Quarantines.Add(item);
                if (purge)
                {
                    checkpoint.PendingPurges.Add(Purge(item));
                }
            }
            return checkpoint;
        }

        private static void AttestRoot(
            PayloadCandidateBuildJournal journal)
        {
            journal.RootVolumeSerialNumber = 0x1234UL;
            journal.RootFileId = FileIdD;
        }

        private static void AttachPresentObservation(
            PayloadBuildWorkspaceCheckpoint workspace)
        {
            workspace.ActiveBuild = Journal(true);
            workspace.ActivePartialTree = PresentObservation();
        }

        private static void ArmQuarantine(
            PayloadBuildWorkspaceCheckpoint workspace)
        {
            workspace.ActiveBuild.ActiveIntent = Intent(
                workspace.ActiveBuild,
                PayloadBuildStepKind.QuarantineBuild,
                -1,
                workspace.ActivePartialTree.InvariantDigest);
        }

        private static PayloadBuildWorkspaceCheckpoint RoundTrip(
            PayloadBuildWorkspaceCheckpoint source)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(PayloadBuildWorkspaceCheckpoint));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, source);
                string json = Encoding.UTF8.GetString(
                    stream.ToArray());
                if (!json.Contains("\"Revision\"") ||
                    !json.Contains(
                        "\"RecoveryAuthorityInvariantDigest\"") ||
                    !json.Contains("\"NamespaceRoot\"") ||
                    !json.Contains("\"QuarantineId\"") ||
                    !json.Contains("\"PurgeId\""))
                {
                    throw new InvalidOperationException(
                        "Workspace JSON omitted a durable CAS binding.");
                }
                stream.Position = 0;
                return (PayloadBuildWorkspaceCheckpoint)
                    serializer.ReadObject(stream);
            }
        }

        private static void Run(string name, Action action)
        {
            action();
            passed++;
            Console.WriteLine("[PASS] " + name);
        }

        private static void Reject(string name, Action action)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception)
            {
                rejected = true;
            }
            if (!rejected)
            {
                throw new InvalidOperationException(
                    "Expected rejection: " + name);
            }
            passed++;
            Console.WriteLine("[PASS] " + name);
        }

        private static void Equal(object expected, object actual)
        {
            if (!Object.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    "Expected " + expected + ", actual " + actual + ".");
            }
        }

        private static void NotEqual(string first, string second)
        {
            if (String.Equals(first, second, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Expected distinct digest values.");
            }
        }
    }
}
