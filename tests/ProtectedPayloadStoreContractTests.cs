using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SBMSSetup
{
    internal static class ProtectedPayloadStoreContractTests
    {
        private const string TransactionId =
            "11111111111111111111111111111111";
        private const string HashA =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string HashB =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string HashC =
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        private const string FileIdA =
            "00112233445566778899aabbccddeeff";
        private const string FileIdB =
            "ffeeddccbbaa99887766554433221100";
        private static int passed;

        private static int Main()
        {
            Run("valid target manifest is accepted", delegate
            {
                CreateManifest().Validate();
            });
            Run("fixed payload names are transaction-bound", delegate
            {
                Equal(
                    "SBMS",
                    PayloadNamespaceNames.ForSlot(
                        PayloadDirectorySlot.Current,
                        TransactionId));
                Equal(
                    ".SBMS.candidate." + TransactionId,
                    PayloadNamespaceNames.ForSlot(
                        PayloadDirectorySlot.Candidate,
                        TransactionId));
                Equal(
                    ".SBMS.backup." + TransactionId,
                    PayloadNamespaceNames.ForSlot(
                        PayloadDirectorySlot.Backup,
                        TransactionId));
            });
            Reject("non-canonical transaction identity is rejected", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.TransactionId =
                    "11111111-1111-1111-1111-111111111111";
                manifest.Validate();
            });
            Reject("empty target payload is rejected", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.Content.Clear();
                manifest.ContentSetSha256 =
                    manifest.ComputeContentSetSha256();
                manifest.Validate();
            });
            Reject("release identity whitespace is rejected before cloning", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.Target.Version = " 0.3.0 ";
                manifest.ComputeContentSetSha256();
            });
            Reject("unsafe target path is rejected", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.Content[0].RelativePath = @"..\outside.exe";
                manifest.ContentSetSha256 =
                    manifest.ComputeContentSetSha256();
                manifest.Validate();
            });
            Reject("trailing-dot target path is rejected", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.Content[0].RelativePath = "SBMS.exe.";
                manifest.ComputeContentSetSha256();
            });
            Reject("reserved DOS target path is rejected", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.Content[0].RelativePath = "NUL.txt";
                manifest.ComputeContentSetSha256();
            });
            Reject("invalid Windows target character is rejected", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.Content[0].RelativePath = "SBMS?.exe";
                manifest.ComputeContentSetSha256();
            });
            Reject("file-directory prefix conflict is rejected", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.Content = new List<TargetPayloadEntry>
                {
                    new TargetPayloadEntry
                    {
                        RelativePath = "node",
                        Length = 1,
                        Sha256 = HashA
                    },
                    new TargetPayloadEntry
                    {
                        RelativePath = @"node\child",
                        Length = 1,
                        Sha256 = HashB
                    }
                };
                manifest.ContentSetSha256 =
                    manifest.ComputeContentSetSha256();
                manifest.Validate();
            });
            Reject("case-insensitive duplicate target path is rejected", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.Content.Add(new TargetPayloadEntry
                {
                    RelativePath = "SBMS.EXE",
                    Length = 3,
                    Sha256 = HashC
                });
                manifest.ContentSetSha256 =
                    manifest.ComputeContentSetSha256();
                manifest.Validate();
            });
            Reject("non-ordinal target order is rejected", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.Content.Reverse();
                manifest.ContentSetSha256 =
                    manifest.ComputeContentSetSha256();
                manifest.Validate();
            });
            Reject("content mutation invalidates target digest", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.Content[0].Length++;
                manifest.Validate();
            });
            Reject("target identity mutation invalidates target digest", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.Target.Version = "0.3.1";
                manifest.Validate();
            });
            Reject("catalog mutation invalidates target digest", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.ReleaseCatalogSha256 = HashC;
                manifest.Validate();
            });
            Reject("signed manifest mutation invalidates target digest", delegate
            {
                TargetPayloadManifest manifest = CreateManifest();
                manifest.SignedReleaseManifestSha256 = HashA;
                manifest.Validate();
            });
            Run("target digest is deterministic across deep clones", delegate
            {
                TargetPayloadManifest first = CreateManifest();
                TargetPayloadManifest second = first.DeepClone();
                Equal(first.ContentSetSha256, second.ComputeContentSetSha256());
                Equal(first.InvariantDigest, second.InvariantDigest);
            });
            Run("trusted receipt does not expose mutable authority", delegate
            {
                var receipt =
                    new TrustedReleasePayloadReceipt(CreateManifest());
                TargetPayloadManifest exposed = receipt.Manifest;
                exposed.Target.Version = "9.9.9";
                Equal("0.3.0", receipt.Manifest.Target.Version);
                Equal(2, receipt.FileCount);
                Equal(30L, receipt.TotalBytes);
            });
            Run("directory checkpoint binds native identity", delegate
            {
                Directory(PayloadDirectorySlot.Current, FileIdA).Validate();
            });
            Reject("directory checkpoint rejects an empty owned tree", delegate
            {
                PayloadDirectoryCheckpoint checkpoint =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                checkpoint.FileCount = 0;
                checkpoint.Validate();
            });
            Reject("directory checkpoint rejects malformed file ID", delegate
            {
                PayloadDirectoryCheckpoint checkpoint =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                checkpoint.FileId = "abc";
                checkpoint.Validate();
            });
            Reject("directory checkpoint requires verified parent directories", delegate
            {
                PayloadDirectoryCheckpoint checkpoint =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                checkpoint.Entries.RemoveAt(1);
                checkpoint.Validate();
            });
            Reject("directory checkpoint rejects aliased tree identities", delegate
            {
                PayloadDirectoryCheckpoint checkpoint =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                checkpoint.Entries[1].FileId =
                    checkpoint.Entries[0].FileId;
                checkpoint.Validate();
            });
            Run("all seven legal namespace shapes are accepted", delegate
            {
                Namespace(
                    PayloadNamespaceShape.Empty,
                    null,
                    null,
                    null).Validate();
                Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    Directory(PayloadDirectorySlot.Current, FileIdA),
                    null,
                    null).Validate();
                Namespace(
                    PayloadNamespaceShape.CandidateOnly,
                    null,
                    Directory(PayloadDirectorySlot.Candidate, FileIdA),
                    null).Validate();
                Namespace(
                    PayloadNamespaceShape.BackupOnly,
                    null,
                    null,
                    Directory(PayloadDirectorySlot.Backup, FileIdA)).Validate();
                Namespace(
                    PayloadNamespaceShape.CurrentAndCandidate,
                    Directory(PayloadDirectorySlot.Current, FileIdA),
                    Directory(PayloadDirectorySlot.Candidate, FileIdB),
                    null).Validate();
                Namespace(
                    PayloadNamespaceShape.CurrentAndBackup,
                    Directory(PayloadDirectorySlot.Current, FileIdA),
                    null,
                    Directory(PayloadDirectorySlot.Backup, FileIdB)).Validate();
                Namespace(
                    PayloadNamespaceShape.CandidateAndBackup,
                    null,
                    Directory(PayloadDirectorySlot.Candidate, FileIdA),
                    Directory(PayloadDirectorySlot.Backup, FileIdB)).Validate();
            });
            Reject("declared shape cannot disagree with slots", delegate
            {
                Namespace(
                    PayloadNamespaceShape.Empty,
                    Directory(PayloadDirectorySlot.Current, FileIdA),
                    null,
                    null).Validate();
            });
            Reject("all three owned slots are an illegal checkpoint", delegate
            {
                Namespace(
                    PayloadNamespaceShape.CurrentAndBackup,
                    Directory(PayloadDirectorySlot.Current, FileIdA),
                    Directory(PayloadDirectorySlot.Candidate, FileIdB),
                    Directory(
                        PayloadDirectorySlot.Backup,
                        "11112222333344445555666677778888")).Validate();
            });
            Reject("slot identity cannot be relabeled", delegate
            {
                PayloadDirectoryCheckpoint candidate =
                    Directory(PayloadDirectorySlot.Candidate, FileIdA);
                Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    candidate,
                    null,
                    null).Validate();
            });
            Reject("slot cannot belong to another transaction", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                current.TransactionId =
                    "22222222222222222222222222222222";
                Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    current,
                    null,
                    null).Validate();
            });
            Run("namespace state is immutable by clone", delegate
            {
                PayloadNamespaceCheckpoint checkpoint = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    Directory(PayloadDirectorySlot.Current, FileIdA),
                    null,
                    null);
                var state = new PayloadNamespaceState(checkpoint);
                string digest = state.InvariantDigest;
                PayloadNamespaceCheckpoint exposed = state.Checkpoint;
                exposed.Current.FileId = FileIdB;
                Equal(digest, state.InvariantDigest);
            });
            Run("revision participates in namespace CAS digest", delegate
            {
                PayloadNamespaceCheckpoint first = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    Directory(PayloadDirectorySlot.Current, FileIdA),
                    null,
                    null);
                PayloadNamespaceCheckpoint second = first.DeepClone();
                second.Revision++;
                NotEqual(first.InvariantDigest, second.InvariantDigest);
            });
            Run("directory identity participates in namespace CAS digest", delegate
            {
                PayloadNamespaceCheckpoint first = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    Directory(PayloadDirectorySlot.Current, FileIdA),
                    null,
                    null);
                PayloadNamespaceCheckpoint second = first.DeepClone();
                second.Current.FileId = FileIdB;
                NotEqual(first.InvariantDigest, second.InvariantDigest);
            });
            Run("namespace checkpoint survives a JSON round trip", delegate
            {
                PayloadNamespaceCheckpoint original = Namespace(
                    PayloadNamespaceShape.CurrentAndCandidate,
                    Directory(PayloadDirectorySlot.Current, FileIdA),
                    Directory(PayloadDirectorySlot.Candidate, FileIdB),
                    null);
                string digest = original.InvariantDigest;
                PayloadNamespaceCheckpoint roundTrip =
                    RoundTrip(original);
                roundTrip.Validate();
                Equal(digest, roundTrip.InvariantDigest);
            });
            Run("recovery authority survives a JSON round trip", delegate
            {
                PayloadDirectoryCheckpoint baseline =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadDirectoryCheckpoint target =
                    Directory(PayloadDirectorySlot.Candidate, FileIdB);
                PayloadRecoveryAuthority original = Authority(
                    InstallOperation.Repair,
                    baseline,
                    target);
                PayloadRecoveryAuthority roundTrip =
                    RoundTripAuthority(original);
                roundTrip.Validate();
                Equal(original.TransactionId, roundTrip.TransactionId);
                Equal(original.Operation, roundTrip.Operation);
                Equal(
                    original.Baseline.ContentSetSha256,
                    roundTrip.Baseline.ContentSetSha256);
                Equal(
                    original.Target.ManifestInvariantDigest,
                    roundTrip.Target.ManifestInvariantDigest);
                Equal(
                    original.Target.SemanticTreeSha256,
                    roundTrip.Target.SemanticTreeSha256);
            });
            Reject("recovery authority requires serialized operation", delegate
            {
                DeserializeAuthority(
                    "{\"SchemaVersion\":1," +
                    "\"TransactionId\":\"" + TransactionId + "\"," +
                    "\"BaselineState\":0," +
                    "\"Target\":null," +
                    "\"SealedEscrowManifestSha256\":\"" + HashC + "\"}");
            });
            Reject("negative namespace revision is rejected", delegate
            {
                PayloadNamespaceCheckpoint checkpoint = Namespace(
                    PayloadNamespaceShape.Empty,
                    null,
                    null,
                    null);
                checkpoint.Revision = -1;
                checkpoint.Validate();
            });
            Reject("candidate receipt requires a candidate slot", delegate
            {
                new PayloadCandidateReceipt(
                    new PayloadNamespaceState(
                        Namespace(
                            PayloadNamespaceShape.CurrentOnly,
                            Directory(
                                PayloadDirectorySlot.Current,
                                FileIdA),
                            null,
                            null)));
            });
            Run("fresh promotion receipt preserves candidate identity", delegate
            {
                PayloadDirectoryCheckpoint candidate =
                    Directory(PayloadDirectorySlot.Candidate, FileIdA);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CandidateOnly,
                    null,
                    candidate,
                    null);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    Rename(candidate, PayloadDirectorySlot.Current),
                    null,
                    null);
                after.Revision = before.Revision + 1;
                new PayloadPromotionReceipt(
                    Authority(
                        InstallOperation.FreshInstall,
                        null,
                        candidate),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            });
            Run("upgrade promotion receipt preserves both rename identities", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadDirectoryCheckpoint candidate =
                    Directory(PayloadDirectorySlot.Candidate, FileIdB);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CurrentAndCandidate,
                    current,
                    candidate,
                    null);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.CurrentAndBackup,
                    Rename(candidate, PayloadDirectorySlot.Current),
                    null,
                    Rename(current, PayloadDirectorySlot.Backup));
                after.Revision = before.Revision + 2;
                new PayloadPromotionReceipt(
                    Authority(
                        InstallOperation.Upgrade,
                        current,
                        candidate),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            });
            Run("uninstall promotion receipt preserves baseline identity", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    current,
                    null,
                    null);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.BackupOnly,
                    null,
                    null,
                    Rename(current, PayloadDirectorySlot.Backup));
                after.Revision = before.Revision + 1;
                new PayloadPromotionReceipt(
                    Authority(
                        InstallOperation.Uninstall,
                        current,
                        null),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            });
            Reject("promotion receipt rejects ABA revision", delegate
            {
                PayloadDirectoryCheckpoint candidate =
                    Directory(PayloadDirectorySlot.Candidate, FileIdA);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CandidateOnly,
                    null,
                    candidate,
                    null);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    Rename(candidate, PayloadDirectorySlot.Current),
                    null,
                    null);
                after.Revision = before.Revision;
                new PayloadPromotionReceipt(
                    Authority(
                        InstallOperation.FreshInstall,
                        null,
                        candidate),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            });
            Reject("promotion receipt rejects forged native identity", delegate
            {
                PayloadDirectoryCheckpoint candidate =
                    Directory(PayloadDirectorySlot.Candidate, FileIdA);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CandidateOnly,
                    null,
                    candidate,
                    null);
                PayloadDirectoryCheckpoint forged =
                    Rename(candidate, PayloadDirectorySlot.Current);
                forged.FileId = FileIdB;
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    forged,
                    null,
                    null);
                after.Revision = before.Revision + 1;
                new PayloadPromotionReceipt(
                    Authority(
                        InstallOperation.FreshInstall,
                        null,
                        candidate),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            });
            Reject("upgrade promotion cannot publish without baseline", delegate
            {
                PayloadDirectoryCheckpoint candidate =
                    Directory(PayloadDirectorySlot.Candidate, FileIdA);
                PayloadDirectoryCheckpoint baseline =
                    Directory(PayloadDirectorySlot.Current, FileIdB);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CandidateOnly,
                    null,
                    candidate,
                    null);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    Rename(candidate, PayloadDirectorySlot.Current),
                    null,
                    null);
                after.Revision = before.Revision + 1;
                new PayloadPromotionReceipt(
                    Authority(
                        InstallOperation.Upgrade,
                        baseline,
                        candidate),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            });
            Run("promotion receipt does not expose mutable authority", delegate
            {
                PayloadDirectoryCheckpoint candidate =
                    Directory(PayloadDirectorySlot.Candidate, FileIdA);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CandidateOnly,
                    null,
                    candidate,
                    null);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    Rename(candidate, PayloadDirectorySlot.Current),
                    null,
                    null);
                after.Revision = before.Revision + 1;
                PayloadPromotionReceipt receipt =
                    new PayloadPromotionReceipt(
                        Authority(
                            InstallOperation.FreshInstall,
                            null,
                            candidate),
                        new PayloadNamespaceState(before),
                        new PayloadNamespaceState(after));
                PayloadRecoveryAuthority exposed = receipt.Authority;
                exposed.Target.Release.Version = "9.9.9";
                Equal(
                    "0.3.0",
                    receipt.Authority.Target.Release.Version);
            });
            Reject("incomplete cleanup cannot hide namespace mutation", delegate
            {
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CandidateOnly,
                    null,
                    Directory(PayloadDirectorySlot.Candidate, FileIdA),
                    null);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.Empty,
                    null,
                    null,
                    null);
                after.Revision = before.Revision + 1;
                new PayloadCleanupReceipt(
                    PayloadCleanupKind.Candidate,
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after),
                    false);
            });
            Run("fresh recovery may remove only the exact target current", delegate
            {
                PayloadDirectoryCheckpoint target =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    target,
                    null,
                    null);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.Empty,
                    null,
                    null,
                    null);
                after.Revision = before.Revision + 1;
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.RestoreBaseline,
                    Authority(
                        InstallOperation.FreshInstall,
                        null,
                        target),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            });
            Reject("fresh recovery rejects deletion of non-target current", delegate
            {
                PayloadDirectoryCheckpoint target =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadDirectoryCheckpoint unknown = target.DeepClone();
                unknown.Release = new ReleaseIdentity("0.2.0", HashB);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    unknown,
                    null,
                    null);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.Empty,
                    null,
                    null,
                    null);
                after.Revision = before.Revision + 1;
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.RestoreBaseline,
                    Authority(
                        InstallOperation.FreshInstall,
                        null,
                        target),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            });
            Run("upgrade recovery preserves an already-restored baseline", delegate
            {
                PayloadDirectoryCheckpoint baseline =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadDirectoryCheckpoint target =
                    Directory(PayloadDirectorySlot.Candidate, FileIdB);
                PayloadNamespaceCheckpoint checkpoint = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    baseline,
                    null,
                    null);
                PayloadNamespaceState state =
                    new PayloadNamespaceState(checkpoint);
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.RestoreBaseline,
                    Authority(
                        InstallOperation.Upgrade,
                        baseline,
                        target),
                    state,
                    state);
            });
            Reject("upgrade recovery cannot delete CurrentOnly baseline", delegate
            {
                PayloadDirectoryCheckpoint baseline =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadDirectoryCheckpoint target =
                    Directory(PayloadDirectorySlot.Candidate, FileIdB);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    baseline,
                    null,
                    null);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.Empty,
                    null,
                    null,
                    null);
                after.Revision = before.Revision + 1;
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.RestoreBaseline,
                    Authority(
                        InstallOperation.Upgrade,
                        baseline,
                        target),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            });
            Reject("upgrade recovery cannot publish target after baseline loss", delegate
            {
                PayloadDirectoryCheckpoint baseline =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadDirectoryCheckpoint candidate =
                    Directory(PayloadDirectorySlot.Candidate, FileIdB);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.CandidateOnly,
                    null,
                    candidate,
                    null);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    Rename(candidate, PayloadDirectorySlot.Current),
                    null,
                    null);
                after.Revision = before.Revision + 1;
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.CompleteForward,
                    Authority(
                        InstallOperation.Upgrade,
                        baseline,
                        candidate),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            });
            Run("repair and downgrade reject forward recovery after baseline loss", delegate
            {
                ExpectBaselineLossRejected(InstallOperation.Repair);
                ExpectBaselineLossRejected(
                    InstallOperation.ExplicitDowngrade);
            });
            Run("fresh forward recovery is idempotent after publish", delegate
            {
                PayloadDirectoryCheckpoint target =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadNamespaceCheckpoint checkpoint = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    target,
                    null,
                    null);
                PayloadNamespaceState state =
                    new PayloadNamespaceState(checkpoint);
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.CompleteForward,
                    Authority(
                        InstallOperation.FreshInstall,
                        null,
                        target),
                    state,
                    state);
            });
            Reject("fresh forward rejects entry hash drift behind trusted labels", delegate
            {
                PayloadDirectoryCheckpoint expected =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadDirectoryCheckpoint tampered =
                    expected.DeepClone();
                tampered.Entries[0].Sha256 = HashC;
                PayloadNamespaceCheckpoint checkpoint = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    tampered,
                    null,
                    null);
                PayloadNamespaceState state =
                    new PayloadNamespaceState(checkpoint);
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.CompleteForward,
                    Authority(
                        InstallOperation.FreshInstall,
                        null,
                        expected),
                    state,
                    state);
            });
            Reject("fresh forward rejects entry path drift behind trusted labels", delegate
            {
                PayloadDirectoryCheckpoint expected =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadDirectoryCheckpoint tampered =
                    expected.DeepClone();
                tampered.Entries[0].RelativePath = "SBMS2.exe";
                PayloadNamespaceCheckpoint checkpoint = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    tampered,
                    null,
                    null);
                PayloadNamespaceState state =
                    new PayloadNamespaceState(checkpoint);
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.CompleteForward,
                    Authority(
                        InstallOperation.FreshInstall,
                        null,
                        expected),
                    state,
                    state);
            });
            Run("upgrade forward recovery is idempotent after second rename", delegate
            {
                PayloadDirectoryCheckpoint target =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadDirectoryCheckpoint baseline =
                    Directory(PayloadDirectorySlot.Backup, FileIdB);
                PayloadNamespaceCheckpoint checkpoint = Namespace(
                    PayloadNamespaceShape.CurrentAndBackup,
                    target,
                    null,
                    baseline);
                PayloadNamespaceState state =
                    new PayloadNamespaceState(checkpoint);
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.CompleteForward,
                    Authority(
                        InstallOperation.Upgrade,
                        baseline,
                        target),
                    state,
                    state);
            });
            Run("repair forward recovery is idempotent after second rename", delegate
            {
                PayloadDirectoryCheckpoint target =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadDirectoryCheckpoint baseline =
                    Directory(PayloadDirectorySlot.Backup, FileIdB);
                PayloadNamespaceCheckpoint checkpoint = Namespace(
                    PayloadNamespaceShape.CurrentAndBackup,
                    target,
                    null,
                    baseline);
                PayloadNamespaceState state =
                    new PayloadNamespaceState(checkpoint);
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.CompleteForward,
                    Authority(
                        InstallOperation.Repair,
                        baseline,
                        target),
                    state,
                    state);
            });
            Run("explicit downgrade forward recovery is idempotent after second rename", delegate
            {
                PayloadDirectoryCheckpoint target =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadDirectoryCheckpoint baseline =
                    Directory(PayloadDirectorySlot.Backup, FileIdB);
                PayloadNamespaceCheckpoint checkpoint = Namespace(
                    PayloadNamespaceShape.CurrentAndBackup,
                    target,
                    null,
                    baseline);
                PayloadNamespaceState state =
                    new PayloadNamespaceState(checkpoint);
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.CompleteForward,
                    Authority(
                        InstallOperation.ExplicitDowngrade,
                        baseline,
                        target),
                    state,
                    state);
            });
            Run("uninstall recovery restores exact backup baseline", delegate
            {
                PayloadDirectoryCheckpoint backup =
                    Directory(PayloadDirectorySlot.Backup, FileIdA);
                PayloadNamespaceCheckpoint before = Namespace(
                    PayloadNamespaceShape.BackupOnly,
                    null,
                    null,
                    backup);
                PayloadNamespaceCheckpoint after = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    Rename(backup, PayloadDirectorySlot.Current),
                    null,
                    null);
                after.Revision = before.Revision + 1;
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.RestoreBaseline,
                    Authority(
                        InstallOperation.Uninstall,
                        backup,
                        null),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            });
            Run("uninstall forward recovery is idempotent after rename", delegate
            {
                PayloadDirectoryCheckpoint backup =
                    Directory(PayloadDirectorySlot.Backup, FileIdA);
                PayloadNamespaceCheckpoint checkpoint = Namespace(
                    PayloadNamespaceShape.BackupOnly,
                    null,
                    null,
                    backup);
                PayloadNamespaceState state =
                    new PayloadNamespaceState(checkpoint);
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.CompleteForward,
                    Authority(
                        InstallOperation.Uninstall,
                        backup,
                        null),
                    state,
                    state);
            });
            Run("recovery receipt does not expose mutable authority", delegate
            {
                PayloadDirectoryCheckpoint target =
                    Directory(PayloadDirectorySlot.Current, FileIdA);
                PayloadNamespaceCheckpoint checkpoint = Namespace(
                    PayloadNamespaceShape.CurrentOnly,
                    target,
                    null,
                    null);
                PayloadNamespaceState state =
                    new PayloadNamespaceState(checkpoint);
                PayloadRecoveryReceipt receipt =
                    new PayloadRecoveryReceipt(
                        PayloadRecoveryDecision.CompleteForward,
                        Authority(
                            InstallOperation.FreshInstall,
                            null,
                            target),
                        state,
                        state);
                PayloadRecoveryAuthority exposed = receipt.Authority;
                exposed.Target.Release.Version = "9.9.9";
                Equal(
                    "0.3.0",
                    receipt.Authority.Target.Release.Version);
            });

            Console.WriteLine(
                "Protected payload contract tests passed: " +
                passed.ToString());
            return 0;
        }

        private static TargetPayloadManifest CreateManifest()
        {
            var manifest = new TargetPayloadManifest
            {
                SchemaVersion = 1,
                TransactionId = TransactionId,
                Target = new ReleaseIdentity("0.3.0", HashA),
                ReleaseCatalogSha256 = HashA,
                SignedReleaseManifestSha256 = HashB,
                Content = new List<TargetPayloadEntry>
                {
                    new TargetPayloadEntry
                    {
                        RelativePath = "SBMS.exe",
                        Length = 10,
                        Sha256 = HashA
                    },
                    new TargetPayloadEntry
                    {
                        RelativePath = @"driver\SBMS.dll",
                        Length = 20,
                        Sha256 = HashB
                    }
                }
            };
            manifest.ContentSetSha256 =
                manifest.ComputeContentSetSha256();
            return manifest;
        }

        private static PayloadDirectoryCheckpoint Directory(
            PayloadDirectorySlot slot,
            string fileId)
        {
            TargetPayloadManifest manifest = CreateManifest();
            string slotPrefix =
                ((int)slot + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            return new PayloadDirectoryCheckpoint
            {
                TransactionId = TransactionId,
                Slot = slot,
                VolumeSerialNumber = 0x1234UL,
                FileId = fileId,
                Release = new ReleaseIdentity("0.3.0", HashA),
                ContentSetSha256 = manifest.ContentSetSha256,
                ManifestInvariantDigest = manifest.InvariantDigest,
                FileCount = manifest.Content.Count,
                TotalBytes = 30,
                Entries = new List<PayloadTreeEntryCheckpoint>
                {
                    new PayloadTreeEntryCheckpoint
                    {
                        RelativePath = "SBMS.exe",
                        IsDirectory = false,
                        FileId =
                            slotPrefix +
                            "0000000000000000000000000000001",
                        Length = 10,
                        Sha256 = HashA
                    },
                    new PayloadTreeEntryCheckpoint
                    {
                        RelativePath = "driver",
                        IsDirectory = true,
                        FileId =
                            slotPrefix +
                            "0000000000000000000000000000002",
                        Length = 0,
                        Sha256 = String.Empty
                    },
                    new PayloadTreeEntryCheckpoint
                    {
                        RelativePath = @"driver\SBMS.dll",
                        IsDirectory = false,
                        FileId =
                            slotPrefix +
                            "0000000000000000000000000000003",
                        Length = 20,
                        Sha256 = HashB
                    }
                }
            };
        }

        private static PayloadDirectoryCheckpoint Rename(
            PayloadDirectoryCheckpoint source,
            PayloadDirectorySlot destination)
        {
            PayloadDirectoryCheckpoint result = source.DeepClone();
            result.Slot = destination;
            return result;
        }

        private static PayloadContentAuthority ContentAuthority(
            PayloadDirectoryCheckpoint directory)
        {
            return new PayloadContentAuthority
            {
                Release =
                    new ReleaseIdentity(
                        directory.Release.Version,
                        directory.Release.PackageFingerprint),
                ContentSetSha256 = directory.ContentSetSha256,
                ManifestInvariantDigest =
                    directory.ManifestInvariantDigest,
                SemanticTreeSha256 =
                    directory.SemanticTreeSha256,
                FileCount = directory.FileCount,
                TotalBytes = directory.TotalBytes
            };
        }

        private static PayloadRecoveryAuthority Authority(
            InstallOperation operation,
            PayloadDirectoryCheckpoint baseline,
            PayloadDirectoryCheckpoint target)
        {
            return new PayloadRecoveryAuthority
            {
                SchemaVersion = 1,
                TransactionId = TransactionId,
                Operation = operation,
                BaselineState = baseline == null
                    ? BaselinePayloadState.Absent
                    : BaselinePayloadState.Present,
                Baseline = baseline == null
                    ? null
                    : ContentAuthority(baseline),
                Target = target == null
                    ? null
                    : ContentAuthority(target),
                SealedEscrowManifestSha256 = HashC
            };
        }

        private static void ExpectBaselineLossRejected(
            InstallOperation operation)
        {
            PayloadDirectoryCheckpoint baseline =
                Directory(PayloadDirectorySlot.Current, FileIdA);
            PayloadDirectoryCheckpoint candidate =
                Directory(PayloadDirectorySlot.Candidate, FileIdB);
            PayloadNamespaceCheckpoint before = Namespace(
                PayloadNamespaceShape.CandidateOnly,
                null,
                candidate,
                null);
            PayloadNamespaceCheckpoint after = Namespace(
                PayloadNamespaceShape.CurrentOnly,
                Rename(candidate, PayloadDirectorySlot.Current),
                null,
                null);
            after.Revision = before.Revision + 1;
            bool rejected = false;
            try
            {
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.CompleteForward,
                    Authority(operation, baseline, candidate),
                    new PayloadNamespaceState(before),
                    new PayloadNamespaceState(after));
            }
            catch (Exception)
            {
                rejected = true;
            }
            if (!rejected)
            {
                throw new InvalidOperationException(
                    operation +
                    " accepted target publication after baseline loss.");
            }
        }

        private static PayloadNamespaceCheckpoint Namespace(
            PayloadNamespaceShape shape,
            PayloadDirectoryCheckpoint current,
            PayloadDirectoryCheckpoint candidate,
            PayloadDirectoryCheckpoint backup)
        {
            return new PayloadNamespaceCheckpoint
            {
                SchemaVersion = 1,
                Revision = 1,
                TransactionId = TransactionId,
                Shape = shape,
                Current = current,
                Candidate = candidate,
                Backup = backup
            };
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

        private static PayloadNamespaceCheckpoint RoundTrip(
            PayloadNamespaceCheckpoint source)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(PayloadNamespaceCheckpoint));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, source);
                byte[] bytes = stream.ToArray();
                string json = Encoding.UTF8.GetString(bytes);
                if (!json.Contains("\"Revision\"") ||
                    !json.Contains("\"FileId\""))
                {
                    throw new InvalidOperationException(
                        "Checkpoint JSON omitted durable CAS fields.");
                }
                stream.Position = 0;
                return (PayloadNamespaceCheckpoint)
                    serializer.ReadObject(stream);
            }
        }

        private static PayloadRecoveryAuthority RoundTripAuthority(
            PayloadRecoveryAuthority source)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(PayloadRecoveryAuthority));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, source);
                stream.Position = 0;
                return (PayloadRecoveryAuthority)
                    serializer.ReadObject(stream);
            }
        }

        private static PayloadRecoveryAuthority DeserializeAuthority(
            string json)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(PayloadRecoveryAuthority));
            using (var stream = new MemoryStream(
                Encoding.UTF8.GetBytes(json)))
            {
                return (PayloadRecoveryAuthority)
                    serializer.ReadObject(stream);
            }
        }
    }
}
