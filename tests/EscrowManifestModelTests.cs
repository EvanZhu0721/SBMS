using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using SBMSSetup;

internal static class EscrowManifestModelTests
{
    private static int assertions;

    public static int Main()
    {
        try
        {
            TestPresentAndAbsentBaselines();
            TestSerialization();
            TestDuplicateContent();
            TestIdentityAndHashValidation();
            TestOperationAndTargetValidation();
            TestStateAndTimestampValidation();
            Console.WriteLine(
                "EscrowManifestModelTests passed: " +
                assertions.ToString(CultureInfo.InvariantCulture) +
                " assertions");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static void TestPresentAndAbsentBaselines()
    {
        EscrowManifest present = ValidManifest();
        present.BaselinePayloadState = BaselinePayloadState.Present;
        AssertValid(present, "present baseline payload is valid");

        EscrowManifest absent = ValidManifest();
        absent.Operation = InstallOperation.FreshInstall;
        absent.BaselinePayloadState = BaselinePayloadState.Absent;
        absent.Content.Clear();
        absent.Content.Add(
            Entry(EscrowContentKind.Configuration, @"configuration\sbms.xml"));
        AssertValid(absent, "absent baseline payload is valid");

        EscrowManifest uninstall = ValidManifest();
        uninstall.Operation = InstallOperation.Uninstall;
        uninstall.Target = null;
        AssertValid(uninstall, "uninstall without target is valid");

        EscrowManifest absentWithBaseline = ValidManifest();
        absentWithBaseline.Operation = InstallOperation.FreshInstall;
        absentWithBaseline.BaselinePayloadState = BaselinePayloadState.Absent;
        AssertInvalid(
            absentWithBaseline,
            "absent baseline rejects baseline payload content");

        EscrowManifest presentWithoutBaseline = ValidManifest();
        presentWithoutBaseline.Content.RemoveAt(0);
        AssertInvalid(
            presentWithoutBaseline,
            "present baseline requires baseline payload content");

        EscrowManifest freshWithPresentBaseline = ValidManifest();
        freshWithPresentBaseline.Operation = InstallOperation.FreshInstall;
        AssertInvalid(
            freshWithPresentBaseline,
            "fresh install rejects a present baseline payload");

        EscrowManifest upgradeWithAbsentBaseline = ValidManifest();
        upgradeWithAbsentBaseline.BaselinePayloadState =
            BaselinePayloadState.Absent;
        upgradeWithAbsentBaseline.Content.RemoveAt(0);
        AssertInvalid(
            upgradeWithAbsentBaseline,
            "upgrade requires a present baseline payload");
    }

    private static void TestSerialization()
    {
        EscrowManifest original = ValidManifest();
        var serializer = new DataContractJsonSerializer(typeof(EscrowManifest));
        string json;
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, original);
            json = Encoding.UTF8.GetString(stream.ToArray());
        }
        Assert(
            json.IndexOf("\"SchemaVersion\":2", StringComparison.Ordinal) >= 0 &&
            json.IndexOf("\"Revision\":1", StringComparison.Ordinal) >= 0 &&
            json.IndexOf("\"Operation\":1", StringComparison.Ordinal) >= 0 &&
            json.IndexOf("\"BaselinePayloadState\":1", StringComparison.Ordinal) >= 0 &&
            json.IndexOf("\"Target\":", StringComparison.Ordinal) >= 0 &&
            json.IndexOf("\"Kind\":0", StringComparison.Ordinal) >= 0,
            "v2 fields are serialized");

        EscrowManifest roundTrip;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            roundTrip = (EscrowManifest)serializer.ReadObject(stream);
        }
        AssertValid(roundTrip, "serialized v2 manifest round-trips");
        Assert(
            roundTrip.Target.Version == original.Target.Version &&
            roundTrip.Content.Count == original.Content.Count &&
            roundTrip.Content[0].Kind == EscrowContentKind.BaselinePayload,
            "serialized v2 identity and content kinds round-trip");

        AssertMissingRequiredRejected(
            json,
            "\"Operation\":1,",
            "missing operation is rejected during deserialization");
        AssertMissingRequiredRejected(
            json,
            "\"Sealed\":true,",
            "missing sealed flag is rejected during deserialization");
        AssertMissingRequiredRejected(
            json,
            "\"Kind\":0,",
            "missing content kind is rejected during deserialization");
    }

    private static void TestDuplicateContent()
    {
        EscrowManifest duplicate = ValidManifest();
        duplicate.Content.Add(
            Entry(EscrowContentKind.BaselinePayload, @"payload\SBMS.exe"));
        AssertInvalid(duplicate, "duplicate kind and path is rejected");

        EscrowManifest caseCollision = ValidManifest();
        caseCollision.Content.Add(
            Entry(EscrowContentKind.BaselinePayload, @"PAYLOAD\sbms.EXE"));
        AssertInvalid(caseCollision, "content path collision is case-insensitive");

        EscrowManifest targetPayload = ValidManifest();
        targetPayload.Content.Add(
            Entry(EscrowContentKind.TargetPayload, @"PAYLOAD\sbms.EXE"));
        AssertInvalid(
            targetPayload,
            "sealed rollback manifest rejects target payload content");

        EscrowManifest differentKind = ValidManifest();
        differentKind.Content.Add(
            Entry(EscrowContentKind.Configuration, @"PAYLOAD\sbms.EXE"));
        AssertValid(
            differentKind,
            "same relative path in a distinct content namespace is valid");
        Assert(
            !String.Equals(
                differentKind.Content[0].StorageRelativePath,
                differentKind.Content[2].StorageRelativePath,
                StringComparison.OrdinalIgnoreCase),
            "distinct content kinds map to fixed disjoint storage roots");

        EscrowManifest absolutePath = ValidManifest();
        absolutePath.Content[0].RelativePath = @"C:\restore\SBMS.exe";
        AssertInvalid(absolutePath, "absolute restore path is rejected");

        EscrowManifest undefinedKind = ValidManifest();
        undefinedKind.Content[0].Kind = (EscrowContentKind)999;
        AssertInvalid(undefinedKind, "undefined content kind is rejected");
    }

    private static void TestIdentityAndHashValidation()
    {
        EscrowManifest badTransaction = ValidManifest();
        badTransaction.TransactionId = "not-a-transaction";
        AssertInvalid(badTransaction, "malformed transaction id is rejected");

        EscrowManifest nonCanonicalTransaction = ValidManifest();
        nonCanonicalTransaction.TransactionId =
            nonCanonicalTransaction.TransactionId.ToUpperInvariant();
        AssertInvalid(
            nonCanonicalTransaction,
            "non-canonical transaction id is rejected");

        EscrowManifest badBaseline = ValidManifest();
        badBaseline.BaselineEvidenceDigest = Repeat("0", 63);
        AssertInvalid(badBaseline, "short baseline digest is rejected");

        EscrowManifest nonHexBaseline = ValidManifest();
        nonHexBaseline.BaselineEvidenceDigest = Repeat("z", 64);
        AssertInvalid(nonHexBaseline, "non-hex baseline digest is rejected");

        EscrowManifest badContentHash = ValidManifest();
        badContentHash.Content[0].Sha256 = Repeat("g", 64);
        AssertInvalid(badContentHash, "non-hex content hash is rejected");

        EscrowManifest uppercaseContentHash = ValidManifest();
        uppercaseContentHash.Content[0].Sha256 =
            uppercaseContentHash.Content[0].Sha256.ToUpperInvariant();
        AssertInvalid(
            uppercaseContentHash,
            "non-canonical uppercase content hash is rejected");

        EscrowManifest uppercaseBaselineHash = ValidManifest();
        uppercaseBaselineHash.BaselineEvidenceDigest =
            uppercaseBaselineHash.BaselineEvidenceDigest.ToUpperInvariant();
        AssertInvalid(
            uppercaseBaselineHash,
            "non-canonical uppercase baseline hash is rejected");

        EscrowManifest badRevision = ValidManifest();
        badRevision.Revision = 0;
        AssertInvalid(badRevision, "non-positive revision is rejected");

        EscrowManifest badSchema = ValidManifest();
        badSchema.SchemaVersion = 1;
        AssertInvalid(badSchema, "legacy manifest schema is rejected");
    }

    private static void TestOperationAndTargetValidation()
    {
        EscrowManifest missingTarget = ValidManifest();
        missingTarget.Target = null;
        AssertInvalid(missingTarget, "non-uninstall operation requires target");

        EscrowManifest uninstallTarget = ValidManifest();
        uninstallTarget.Operation = InstallOperation.Uninstall;
        AssertInvalid(uninstallTarget, "uninstall operation rejects target");

        EscrowManifest badTarget = ValidManifest();
        badTarget.Target.PackageFingerprint = "";
        AssertInvalid(badTarget, "invalid target identity is rejected");

        EscrowManifest undefinedOperation = ValidManifest();
        undefinedOperation.Operation = (InstallOperation)999;
        AssertInvalid(undefinedOperation, "undefined operation is rejected");

        EscrowManifest undefinedBaseline = ValidManifest();
        undefinedBaseline.BaselinePayloadState = (BaselinePayloadState)999;
        AssertInvalid(
            undefinedBaseline,
            "undefined baseline payload state is rejected");
    }

    private static void TestStateAndTimestampValidation()
    {
        EscrowManifest building = ValidManifest();
        building.Sealed = false;
        building.SealedUtc = null;
        building.RetentionState = EscrowRetentionState.Building;
        AssertValid(building, "unsealed building state is valid");

        EscrowManifest unsealedTimestamp = ValidManifest();
        unsealedTimestamp.Sealed = false;
        unsealedTimestamp.RetentionState = EscrowRetentionState.Building;
        AssertInvalid(unsealedTimestamp, "unsealed manifest rejects seal timestamp");

        EscrowManifest unsealedRetention = ValidManifest();
        unsealedRetention.Sealed = false;
        unsealedRetention.SealedUtc = null;
        unsealedRetention.RetentionState =
            EscrowRetentionState.FinalizationPending;
        AssertInvalid(
            unsealedRetention,
            "unsealed manifest rejects retention lifecycle state");

        EscrowManifest sealedBuilding = ValidManifest();
        sealedBuilding.RetentionState = EscrowRetentionState.Building;
        AssertInvalid(sealedBuilding, "sealed manifest rejects building state");

        EscrowManifest badTimestamp = ValidManifest();
        badTimestamp.SealedUtc = "tomorrow";
        AssertInvalid(badTimestamp, "unparseable seal timestamp is rejected");

        EscrowManifest nonUtcTimestamp = ValidManifest();
        nonUtcTimestamp.SealedUtc = "2026-07-25T01:02:03.0000000+08:00";
        AssertInvalid(nonUtcTimestamp, "non-UTC seal timestamp is rejected");

        EscrowManifest unspecifiedTimestamp = ValidManifest();
        unspecifiedTimestamp.SealedUtc = "2026-07-25T01:02:03.0000000";
        AssertInvalid(
            unspecifiedTimestamp,
            "timestamp without an explicit UTC designator is rejected");

        EscrowManifest nonCanonicalUtcTimestamp = ValidManifest();
        nonCanonicalUtcTimestamp.SealedUtc =
            "2026-07-25T01:02:03.0000000+00:00";
        AssertInvalid(
            nonCanonicalUtcTimestamp,
            "UTC timestamp must use the canonical Z representation");

        EscrowManifest undefinedRetention = ValidManifest();
        undefinedRetention.RetentionState = (EscrowRetentionState)999;
        AssertInvalid(undefinedRetention, "undefined retention state is rejected");

        EscrowManifest finalizedWithoutEvidence = ValidManifest();
        finalizedWithoutEvidence.RetentionState =
            EscrowRetentionState.Finalized;
        AssertInvalid(
            finalizedWithoutEvidence,
            "finalized manifest requires finalization evidence");

        EscrowManifest finalized = ValidManifest();
        finalized.RetentionState = EscrowRetentionState.Finalized;
        finalized.FinalizationEvidence = Repeat("d4", 32);
        AssertValid(finalized, "finalized manifest accepts bounded evidence");

        EscrowManifest prematureEvidence = ValidManifest();
        prematureEvidence.FinalizationEvidence = Repeat("d4", 32);
        AssertInvalid(
            prematureEvidence,
            "sealed rollback manifest rejects premature finalization evidence");

        EscrowManifest prematureWhitespaceEvidence = ValidManifest();
        prematureWhitespaceEvidence.FinalizationEvidence = "   ";
        AssertInvalid(
            prematureWhitespaceEvidence,
            "sealed rollback manifest rejects whitespace evidence");

        EscrowManifest unsafeEvidence = ValidManifest();
        unsafeEvidence.RetentionState = EscrowRetentionState.Finalized;
        unsafeEvidence.FinalizationEvidence = "line1\nline2";
        AssertInvalid(
            unsafeEvidence,
            "finalization evidence rejects control characters");

        EscrowManifest whitespaceEvidence = ValidManifest();
        whitespaceEvidence.RetentionState = EscrowRetentionState.Finalized;
        whitespaceEvidence.FinalizationEvidence = "   ";
        AssertInvalid(
            whitespaceEvidence,
            "finalized manifest rejects whitespace-only evidence");

        EscrowManifest oversizedEvidence = ValidManifest();
        oversizedEvidence.RetentionState = EscrowRetentionState.Finalized;
        oversizedEvidence.FinalizationEvidence = Repeat("x", 513);
        AssertInvalid(
            oversizedEvidence,
            "finalization evidence is bounded");
    }

    private static EscrowManifest ValidManifest()
    {
        var manifest = new EscrowManifest
        {
            SchemaVersion = 2,
            Revision = 1,
            TransactionId = Guid.NewGuid().ToString("N"),
            Operation = InstallOperation.Upgrade,
            BaselineEvidenceDigest = Repeat("a1", 32),
            BaselinePayloadState = BaselinePayloadState.Present,
            Target = new ReleaseIdentity("0.4.0", Repeat("b2", 32)),
            Sealed = true,
            SealedUtc = "2026-07-25T01:02:03.0000000Z",
            RetentionState = EscrowRetentionState.SealedForRollback,
            FinalizationEvidence = null
        };
        manifest.Content.Add(
            Entry(EscrowContentKind.BaselinePayload, @"payload\SBMS.exe"));
        manifest.Content.Add(
            Entry(EscrowContentKind.Configuration, @"configuration\sbms.xml"));
        return manifest;
    }

    private static EscrowContentEntry Entry(
        EscrowContentKind kind,
        string path)
    {
        return new EscrowContentEntry
        {
            RelativePath = path,
            Kind = kind,
            Length = 123,
            Sha256 = Repeat("c3", 32)
        };
    }

    private static string Repeat(string value, int count)
    {
        string result = String.Empty;
        for (int index = 0; index < count; ++index)
        {
            result += value;
        }
        return result;
    }

    private static void AssertMissingRequiredRejected(
        string json,
        string token,
        string message)
    {
        ++assertions;
        string incomplete = json.Replace(token, String.Empty);
        if (incomplete.Length == json.Length)
        {
            throw new InvalidOperationException(
                "Required-field fixture token was not found: " + token);
        }
        var serializer = new DataContractJsonSerializer(
            typeof(EscrowManifest));
        try
        {
            using (var stream = new MemoryStream(
                Encoding.UTF8.GetBytes(incomplete)))
            {
                serializer.ReadObject(stream);
            }
        }
        catch (System.Runtime.Serialization.SerializationException)
        {
            return;
        }
        throw new InvalidOperationException(
            "Expected invalid: " + message + ".");
    }

    private static void AssertValid(EscrowManifest manifest, string message)
    {
        ++assertions;
        try
        {
            manifest.Validate();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Expected valid: " + message + ". " + ex.Message,
                ex);
        }
    }

    private static void Assert(bool condition, string message)
    {
        ++assertions;
        if (!condition)
        {
            throw new InvalidOperationException(
                "Assertion failed: " + message + ".");
        }
    }

    private static void AssertInvalid(EscrowManifest manifest, string message)
    {
        ++assertions;
        try
        {
            manifest.Validate();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(
            "Expected invalid: " + message + ".");
    }
}
