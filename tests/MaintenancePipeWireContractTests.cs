using System;
using System.IO;
using System.Text;

namespace SBMSSetup
{
    internal static class MaintenancePipeWireContractTests
    {
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private const string TransactionId =
            "11111111111111111111111111111111";
        private const string RequestId =
            "22222222222222222222222222222222";

        internal static void Run()
        {
            CommandCodecIsCanonicalAndStrict();
            FrameLayoutAndBoundariesAreExact();
            FramePayloadsAreImmutable();
            AckBindsTheCanonicalResponsePayload();
            MalformedFrameFuzzIsBounded();
        }

        private static void CommandCodecIsCanonicalAndStrict()
        {
            PayloadBrokerCommand command = Command();
            byte[] canonical =
                PayloadBrokerCommandCodec.SerializeCanonical(command);
            PayloadBrokerCommand decoded =
                PayloadBrokerCommandCodec.DeserializeAndValidate(
                    canonical);
            Assert(
                decoded.InvariantDigest == command.InvariantDigest &&
                Equal(
                    canonical,
                    PayloadBrokerCommandCodec.SerializeCanonical(
                        decoded)),
                "Command canonical round trip changed its value or bytes.");

            RejectCommand(new byte[0], "empty");
            RejectCommand(
                new byte[
                    MaintenancePipeFrameCodec.MaxRequestPayload + 1],
                "cap");

            var bom = new byte[canonical.Length + 3];
            bom[0] = 0xEF;
            bom[1] = 0xBB;
            bom[2] = 0xBF;
            Buffer.BlockCopy(
                canonical,
                0,
                bom,
                3,
                canonical.Length);
            RejectCommand(bom, "BOM");

            byte[] invalidUtf8 = Copy(canonical);
            invalidUtf8[1] = 0xFF;
            RejectCommand(invalidUtf8, "invalid UTF-8");

            RejectCommand(
                Prefix(canonical, new byte[] { 0x20 }),
                "whitespace");
            RejectCommand(
                Suffix(canonical, new byte[] { 0x20 }),
                "trailing whitespace");
            RejectCommand(
                Suffix(canonical, new byte[] { (byte)'x' }),
                "trailing data");

            string json = StrictUtf8.GetString(canonical);
            string ordered =
                "\"SchemaVersion\":2,\"ProtocolVersion\":1";
            string reordered =
                "\"ProtocolVersion\":1,\"SchemaVersion\":2";
            Assert(
                json.IndexOf(
                    ordered,
                    StringComparison.Ordinal) >= 0,
                "Canonical command property-order fixture drifted.");
            RejectCommand(
                StrictUtf8.GetBytes(
                    ReplaceOnce(json, ordered, reordered)),
                "property order");
            RejectCommand(
                StrictUtf8.GetBytes(
                    json.Insert(1, "\"Unknown\":1,")),
                "unknown property");
            RejectCommand(
                StrictUtf8.GetBytes(
                    json.Insert(1, "\"SchemaVersion\":2,")),
                "duplicate property");
        }

        private static void FrameLayoutAndBoundariesAreExact()
        {
            byte[] payload =
                PayloadBrokerCommandCodec.SerializeCanonical(Command());
            byte[] frame = MaintenancePipeFrameCodec.Encode(
                MaintenancePipeFrameKind.Request,
                payload);
            Assert(
                frame.Length ==
                    MaintenancePipeFrameCodec.HeaderLength +
                    payload.Length &&
                frame[0] == (byte)'S' &&
                frame[1] == (byte)'B' &&
                frame[2] == (byte)'M' &&
                frame[3] == (byte)'S' &&
                frame[4] == 1 &&
                frame[5] == 0 &&
                frame[6] == 1 &&
                frame[7] == 0 &&
                ReadUInt32(frame, 8) == (uint)payload.Length &&
                ReadUInt32(frame, 12) == 0,
                "Maintenance pipe frame header layout changed.");
            MaintenancePipeFrame decoded =
                MaintenancePipeFrameCodec.Decode(frame);
            Assert(
                decoded.Kind == MaintenancePipeFrameKind.Request &&
                decoded.PayloadLength == payload.Length &&
                Equal(decoded.GetPayloadCopy(), payload),
                "Maintenance pipe request frame did not round trip.");

            AssertRoundTrip(
                MaintenancePipeFrameKind.Request,
                1);
            AssertRoundTrip(
                MaintenancePipeFrameKind.Request,
                MaintenancePipeFrameCodec.MaxRequestPayload);
            AssertRoundTrip(
                MaintenancePipeFrameKind.Response,
                1);
            AssertRoundTrip(
                MaintenancePipeFrameKind.Response,
                MaintenancePipeFrameCodec.MaxResponsePayload);

            RejectEncode(
                MaintenancePipeFrameKind.Request,
                new byte[0],
                "zero request");
            RejectEncode(
                MaintenancePipeFrameKind.Response,
                new byte[0],
                "zero response");
            RejectEncode(
                MaintenancePipeFrameKind.Request,
                new byte[
                    MaintenancePipeFrameCodec.MaxRequestPayload + 1],
                "request cap");
            RejectEncode(
                MaintenancePipeFrameKind.Response,
                new byte[
                    MaintenancePipeFrameCodec.MaxResponsePayload + 1],
                "response cap");
            RejectEncode(
                MaintenancePipeFrameKind.Ack,
                new byte[
                    MaintenancePipeFrameCodec.AckPayloadLength - 1],
                "ack length");
            RejectFrame(
                RawFrame(
                    MaintenancePipeFrameKind.Request,
                    new byte[0]),
                "decoded zero request");
            RejectFrame(
                RawFrame(
                    MaintenancePipeFrameKind.Response,
                    new byte[0]),
                "decoded zero response");
            RejectFrame(
                RawFrame(
                    MaintenancePipeFrameKind.Request,
                    new byte[
                        MaintenancePipeFrameCodec.
                            MaxRequestPayload + 1]),
                "decoded request cap");
            RejectFrame(
                RawFrame(
                    MaintenancePipeFrameKind.Response,
                    new byte[
                        MaintenancePipeFrameCodec.
                            MaxResponsePayload + 1]),
                "decoded response cap");
            RejectFrame(
                RawFrame(
                    MaintenancePipeFrameKind.Ack,
                    new byte[
                        MaintenancePipeFrameCodec.
                            AckPayloadLength - 1]),
                "decoded ack length");

            RejectFrame(Mutate(frame, 0, (byte)'X'), "magic");
            RejectFrame(Mutate(frame, 4, 2), "version");
            RejectFrame(Mutate(frame, 6, 4), "kind");
            RejectFrame(Mutate(frame, 12, 1), "reserved");

            byte[] shortLength = Copy(frame);
            WriteUInt32(
                shortLength,
                8,
                (uint)(payload.Length - 1));
            RejectFrame(shortLength, "length mismatch");
            RejectFrame(
                Suffix(frame, new byte[] { 0 }),
                "trailing frame data");
            RejectFrame(
                Suffix(frame, frame),
                "multiple frames");
            byte[] overflow = Copy(frame);
            WriteUInt32(overflow, 8, UInt32.MaxValue);
            RejectFrame(overflow, "Int32 overflow");
            RejectFrame(
                new byte[
                    MaintenancePipeFrameCodec.HeaderLength - 1],
                "short header");
        }

        private static void FramePayloadsAreImmutable()
        {
            byte[] source =
                PayloadBrokerCommandCodec.SerializeCanonical(Command());
            byte[] expected = Copy(source);
            byte[] encoded = MaintenancePipeFrameCodec.Encode(
                MaintenancePipeFrameKind.Request,
                source);
            source[0] ^= 0x01;
            MaintenancePipeFrame decoded =
                MaintenancePipeFrameCodec.Decode(encoded);
            Assert(
                Equal(decoded.GetPayloadCopy(), expected),
                "Encode retained mutable caller payload state.");
            byte[] firstCopy = decoded.GetPayloadCopy();
            firstCopy[0] ^= 0x01;
            Assert(
                Equal(decoded.GetPayloadCopy(), expected),
                "Decoded frame exposed mutable payload state.");
            byte[] encodedAgain =
                MaintenancePipeFrameCodec.Encode(decoded);
            Assert(
                Equal(encoded, encodedAgain),
                "Re-encoding an immutable frame changed its bytes.");
        }

        private static void AckBindsTheCanonicalResponsePayload()
        {
            PayloadBrokerCommand command = Command();
            PayloadBrokerResponse response = Response(command);
            byte[] responsePayload =
                PayloadBrokerResponseCodec.SerializeCanonical(
                    response);
            PayloadBrokerResponse decodedResponse =
                PayloadBrokerResponseCodec.DeserializeAndValidate(
                    responsePayload);
            Assert(
                decodedResponse.InvariantDigest ==
                    response.InvariantDigest &&
                Equal(
                    responsePayload,
                    decodedResponse.GetCanonicalReplayBytes()),
                "Acknowledgement fixture is not a canonical response.");
            byte[] ack =
                MaintenancePipeFrameCodec.EncodeAck(responsePayload);
            MaintenancePipeFrame decoded =
                MaintenancePipeFrameCodec.Decode(ack);
            Assert(
                decoded.Kind == MaintenancePipeFrameKind.Ack &&
                decoded.PayloadLength ==
                    MaintenancePipeFrameCodec.AckPayloadLength,
                "Acknowledgement kind or SHA-256 length changed.");
            MaintenancePipeFrameCodec.DecodeAckAndVerify(
                ack,
                responsePayload);

            byte[] changedResponse = Copy(responsePayload);
            int schemaValue = IndexOf(
                changedResponse,
                StrictUtf8.GetBytes("\"SchemaVersion\":2"));
            Assert(
                schemaValue >= 0,
                "Canonical response mutation fixture drifted.");
            changedResponse[
                schemaValue + "\"SchemaVersion\":".Length] =
                    (byte)'3';
            Reject(
                delegate
                {
                    PayloadBrokerResponseCodec.
                        DeserializeAndValidate(changedResponse);
                },
                "mutated canonical response");
            Reject(
                delegate
                {
                    MaintenancePipeFrameCodec.DecodeAckAndVerify(
                        ack,
                        changedResponse);
                },
                "ack hash");
            Reject(
                delegate
                {
                    MaintenancePipeFrameCodec.DecodeAckAndVerify(
                        MaintenancePipeFrameCodec.Encode(
                            MaintenancePipeFrameKind.Request,
                            decoded.GetPayloadCopy()),
                        responsePayload);
                },
                "ack kind");
            Reject(
                delegate
                {
                    MaintenancePipeFrameCodec.EncodeAck(new byte[0]);
                },
                "empty ack response");
            Reject(
                delegate
                {
                    MaintenancePipeFrameCodec.EncodeAck(
                        new byte[
                            MaintenancePipeFrameCodec.
                                MaxResponsePayload + 1]);
                },
                "ack response cap");
        }

        private static void MalformedFrameFuzzIsBounded()
        {
            var random = new Random(19);
            byte[] requestPayload =
                PayloadBrokerCommandCodec.SerializeCanonical(Command());
            byte[] responsePayload =
                PayloadBrokerResponseCodec.SerializeCanonical(
                    Response(Command()));
            byte[][] validSeeds =
            {
                MaintenancePipeFrameCodec.Encode(
                    MaintenancePipeFrameKind.Request,
                    requestPayload),
                MaintenancePipeFrameCodec.Encode(
                    MaintenancePipeFrameKind.Response,
                    responsePayload),
                MaintenancePipeFrameCodec.EncodeAck(responsePayload)
            };
            string[] mutationNames =
            {
                "magic",
                "version",
                "kind",
                "reserved",
                "length",
                "truncation",
                "trailing",
                "payload"
            };
            var mutationCoverage =
                new int[mutationNames.Length];
            int validSeedCount = 0;
            int rejectedStructuredMutations = 0;
            int payloadMutationReached = 0;

            foreach (byte[] seed in validSeeds)
            {
                MaintenancePipeFrameCodec.Decode(seed);
                validSeedCount++;
                for (int category = 0;
                    category < mutationNames.Length;
                    ++category)
                {
                    ExerciseSeedMutation(
                        seed,
                        category,
                        random,
                        mutationCoverage,
                        ref rejectedStructuredMutations,
                        ref payloadMutationReached);
                }
            }
            for (int iteration = 0;
                iteration < 1200;
                ++iteration)
            {
                byte[] seed =
                    validSeeds[random.Next(validSeeds.Length)];
                int category =
                    random.Next(mutationNames.Length);
                ExerciseSeedMutation(
                    seed,
                    category,
                    random,
                    mutationCoverage,
                    ref rejectedStructuredMutations,
                    ref payloadMutationReached);
            }

            RejectFrame(
                RawFrame(
                    MaintenancePipeFrameKind.Request,
                    new byte[
                        MaintenancePipeFrameCodec.
                            MaxRequestPayload + 1]),
                "fuzz request cap");
            RejectFrame(
                RawFrame(
                    MaintenancePipeFrameKind.Response,
                    new byte[
                        MaintenancePipeFrameCodec.
                            MaxResponsePayload + 1]),
                "fuzz response cap");

            for (int iteration = 0;
                iteration < 2000;
                ++iteration)
            {
                var candidate =
                    new byte[random.Next(0, 257)];
                random.NextBytes(candidate);
                try
                {
                    MaintenancePipeFrame frame =
                        MaintenancePipeFrameCodec.Decode(candidate);
                    byte[] encoded =
                        MaintenancePipeFrameCodec.Encode(frame);
                    Assert(
                        Equal(candidate, encoded),
                        "Accepted fuzz frame was not byte-canonical.");
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "Malformed frame escaped the bounded decoder with " +
                        exception.GetType().FullName + ".",
                        exception);
                }
            }
            Assert(
                validSeedCount == 3 &&
                rejectedStructuredMutations > 0 &&
                payloadMutationReached > 0,
                "Structured frame fuzz did not reach valid seeds, " +
                "deep header rejection, and payload decoding.");
            for (int category = 0;
                category < mutationCoverage.Length;
                ++category)
            {
                Assert(
                    mutationCoverage[category] > 0,
                    "Structured frame fuzz missed " +
                    mutationNames[category] + ".");
            }
        }

        private static void ExerciseSeedMutation(
            byte[] seed,
            int category,
            Random random,
            int[] mutationCoverage,
            ref int rejectedStructuredMutations,
            ref int payloadMutationReached)
        {
            byte[] mutated;
            if (category == 0)
            {
                mutated = Copy(seed);
                int index = random.Next(0, 4);
                mutated[index] ^= 0x5A;
            }
            else if (category == 1)
            {
                mutated = Copy(seed);
                mutated[4] = 2;
            }
            else if (category == 2)
            {
                mutated = Copy(seed);
                mutated[6] = 0xFF;
                mutated[7] = 0xFF;
            }
            else if (category == 3)
            {
                mutated = Copy(seed);
                mutated[12 + random.Next(0, 4)] = 1;
            }
            else if (category == 4)
            {
                mutated = Copy(seed);
                WriteUInt32(
                    mutated,
                    8,
                    random.Next(0, 2) == 0
                        ? UInt32.MaxValue
                        : ReadUInt32(mutated, 8) + 1);
            }
            else if (category == 5)
            {
                int retained =
                    random.Next(0, seed.Length);
                mutated = new byte[retained];
                Buffer.BlockCopy(
                    seed,
                    0,
                    mutated,
                    0,
                    retained);
            }
            else if (category == 6)
            {
                mutated = Suffix(
                    seed,
                    new byte[]
                    {
                        (byte)random.Next(0, 256)
                    });
            }
            else
            {
                mutated = Copy(seed);
                int payloadLength =
                    seed.Length -
                    MaintenancePipeFrameCodec.HeaderLength;
                int payloadIndex =
                    MaintenancePipeFrameCodec.HeaderLength +
                    random.Next(payloadLength);
                mutated[payloadIndex] ^= 0x01;
            }
            mutationCoverage[category]++;
            if (category == 7)
            {
                MaintenancePipeFrame original =
                    MaintenancePipeFrameCodec.Decode(seed);
                MaintenancePipeFrame changed =
                    MaintenancePipeFrameCodec.Decode(mutated);
                Assert(
                    !Equal(
                        original.GetPayloadCopy(),
                        changed.GetPayloadCopy()),
                    "Payload mutation did not reach decoded payload.");
                payloadMutationReached++;
                return;
            }
            RejectFrame(
                mutated,
                "structured " + category);
            rejectedStructuredMutations++;
        }

        internal static PayloadBrokerCommand Command()
        {
            var checkpoint =
                new PayloadNamespaceOwnershipCheckpoint
                {
                    SchemaVersion = 2,
                    OwnershipRevision = 0,
                    NamespaceId =
                        PayloadManagedNamespaceLocation.
                            ProductionNamespaceId,
                    Phase =
                        PayloadNamespaceOwnershipPhase.Absent,
                    SecurityProfile =
                        PayloadNamespaceSecurityProfile.Production(),
                    ActiveTransactionId = String.Empty,
                    ActiveIntentId = String.Empty,
                    ExpectedWorkspaceCasInvariantDigest =
                        String.Empty,
                    OwnershipMarkerDigest = String.Empty,
                    RootVolumeSerialNumber = 0,
                    RootFileId = String.Empty,
                    LastObservationInvariantDigest = String.Empty
                };
            return new PayloadBrokerCommand
            {
                SchemaVersion = 2,
                ProtocolVersion =
                    PayloadBrokerProtocol.ProtocolVersion,
                Operation = PayloadBrokerOperation.Inspect,
                TransactionId = TransactionId,
                RequestId = RequestId,
                CorrelationNonceDigest = Digest('1'),
                BeforeOwnershipCas = checkpoint.CasToken,
                BeforeWorkspaceCas =
                    new PayloadWorkspaceCasToken
                    {
                        SchemaVersion = 1,
                        TransactionId = TransactionId,
                        Revision = 0,
                        WorkspaceInvariantDigest = Digest('2')
                    },
                PlanInvariantDigest = Digest('3')
            };
        }

        internal static PayloadBrokerResponse Response(
            PayloadBrokerCommand command)
        {
            var receipt =
                new PayloadBrokerOperationReceipt
                {
                    SchemaVersion = 2,
                    Operation = PayloadBrokerOperation.Inspect,
                    OwnershipTransitionTag =
                        PayloadBrokerOwnershipTransitionTag.None,
                    BeforeOwnershipCas =
                        command.BeforeOwnershipCas.DeepClone(),
                    AfterOwnershipCas =
                        command.BeforeOwnershipCas.DeepClone(),
                    BeforeWorkspaceCas =
                        command.BeforeWorkspaceCas.DeepClone(),
                    AfterWorkspaceCas =
                        command.BeforeWorkspaceCas.DeepClone(),
                    Observation = null,
                    AppliedPlanInvariantDigest =
                        command.PlanInvariantDigest
                };
            return new PayloadBrokerResponse
            {
                SchemaVersion = 2,
                ProtocolVersion =
                    PayloadBrokerProtocol.ProtocolVersion,
                TransactionId = command.TransactionId,
                RequestId = command.RequestId,
                CommandInvariantDigest = command.InvariantDigest,
                Receipt = receipt,
                ResultInvariantDigest = receipt.InvariantDigest
            };
        }

        private static string Digest(char value)
        {
            return new String(value, 64);
        }

        private static void AssertRoundTrip(
            MaintenancePipeFrameKind kind,
            int payloadLength)
        {
            var payload = new byte[payloadLength];
            payload[0] = 0x5A;
            payload[payload.Length - 1] = 0xA5;
            byte[] encoded =
                MaintenancePipeFrameCodec.Encode(kind, payload);
            MaintenancePipeFrame decoded =
                MaintenancePipeFrameCodec.Decode(encoded);
            Assert(
                decoded.Kind == kind &&
                Equal(decoded.GetPayloadCopy(), payload),
                "Frame boundary did not round trip for " +
                kind + " length " + payloadLength + ".");
        }

        private static void RejectCommand(
            byte[] bytes,
            string label)
        {
            Reject(
                delegate
                {
                    PayloadBrokerCommandCodec.
                        DeserializeAndValidate(bytes);
                },
                label);
        }

        private static void RejectFrame(
            byte[] bytes,
            string label)
        {
            Reject(
                delegate
                {
                    MaintenancePipeFrameCodec.Decode(bytes);
                },
                label);
        }

        private static void RejectEncode(
            MaintenancePipeFrameKind kind,
            byte[] payload,
            string label)
        {
            Reject(
                delegate
                {
                    MaintenancePipeFrameCodec.Encode(
                        kind,
                        payload);
                },
                label);
        }

        private static void Reject(Action action, string label)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException)
            {
                return;
            }
            throw new InvalidOperationException(
                "Strict wire codec accepted " + label + ".");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static byte[] Copy(byte[] value)
        {
            var copy = new byte[value.Length];
            Buffer.BlockCopy(
                value,
                0,
                copy,
                0,
                value.Length);
            return copy;
        }

        private static byte[] Prefix(
            byte[] value,
            byte[] prefix)
        {
            var result = new byte[prefix.Length + value.Length];
            Buffer.BlockCopy(
                prefix,
                0,
                result,
                0,
                prefix.Length);
            Buffer.BlockCopy(
                value,
                0,
                result,
                prefix.Length,
                value.Length);
            return result;
        }

        private static byte[] Suffix(
            byte[] value,
            byte[] suffix)
        {
            var result = new byte[value.Length + suffix.Length];
            Buffer.BlockCopy(
                value,
                0,
                result,
                0,
                value.Length);
            Buffer.BlockCopy(
                suffix,
                0,
                result,
                value.Length,
                suffix.Length);
            return result;
        }

        private static byte[] Mutate(
            byte[] value,
            int index,
            byte replacement)
        {
            byte[] result = Copy(value);
            result[index] = replacement;
            return result;
        }

        private static byte[] RawFrame(
            MaintenancePipeFrameKind kind,
            byte[] payload)
        {
            var frame =
                new byte[
                    MaintenancePipeFrameCodec.HeaderLength +
                    payload.Length];
            frame[0] = (byte)'S';
            frame[1] = (byte)'B';
            frame[2] = (byte)'M';
            frame[3] = (byte)'S';
            frame[4] = 1;
            frame[6] = (byte)kind;
            WriteUInt32(frame, 8, (uint)payload.Length);
            Buffer.BlockCopy(
                payload,
                0,
                frame,
                MaintenancePipeFrameCodec.HeaderLength,
                payload.Length);
            return frame;
        }

        private static string ReplaceOnce(
            string value,
            string oldValue,
            string newValue)
        {
            int index = value.IndexOf(
                oldValue,
                StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException(
                    "Wire fixture replacement target is missing.");
            }
            return value.Substring(0, index) +
                newValue +
                value.Substring(index + oldValue.Length);
        }

        private static int IndexOf(
            byte[] value,
            byte[] pattern)
        {
            if (value == null ||
                pattern == null ||
                pattern.Length == 0 ||
                pattern.Length > value.Length)
            {
                return -1;
            }
            for (int start = 0;
                start <= value.Length - pattern.Length;
                ++start)
            {
                bool matches = true;
                for (int offset = 0;
                    offset < pattern.Length;
                    ++offset)
                {
                    if (value[start + offset] != pattern[offset])
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches)
                {
                    return start;
                }
            }
            return -1;
        }

        private static bool Equal(byte[] first, byte[] second)
        {
            if (first == null ||
                second == null ||
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

        private static uint ReadUInt32(byte[] value, int offset)
        {
            return
                (uint)value[offset] |
                ((uint)value[offset + 1] << 8) |
                ((uint)value[offset + 2] << 16) |
                ((uint)value[offset + 3] << 24);
        }

        private static void WriteUInt32(
            byte[] value,
            int offset,
            uint item)
        {
            value[offset] = (byte)item;
            value[offset + 1] = (byte)(item >> 8);
            value[offset + 2] = (byte)(item >> 16);
            value[offset + 3] = (byte)(item >> 24);
        }
    }
}
