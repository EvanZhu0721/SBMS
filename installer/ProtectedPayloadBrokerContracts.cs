using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace SBMSSetup
{
    internal static class PayloadBrokerProtocol
    {
        internal const int ProtocolVersion = 1;
        internal const string NonceSemantics = "CorrelationOnly";
    }

    internal enum PayloadBrokerOperation
    {
        Inspect = 0,
        ProvisionNamespace = 1,
        AdvancePayload = 2,
        AdvancePurge = 3,
        RemoveNamespace = 4
    }

    [DataContract]
    internal sealed class PayloadBrokerCommand
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal int ProtocolVersion;

        [DataMember(Order = 3, IsRequired = true)]
        internal PayloadBrokerOperation Operation;

        [DataMember(Order = 4, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 5, IsRequired = true)]
        internal string RequestId;

        // This nonce is only an opaque correlation value. It is not an
        // authenticator and does not provide replay protection.
        [DataMember(Order = 6, IsRequired = true)]
        internal string CorrelationNonceDigest;

        [DataMember(Order = 7, IsRequired = true)]
        internal PayloadNamespaceOwnershipCasToken BeforeOwnershipCas;

        [DataMember(Order = 8, IsRequired = true)]
        internal PayloadWorkspaceCasToken BeforeWorkspaceCas;

        [DataMember(Order = 9, IsRequired = true)]
        internal string PlanInvariantDigest;

        internal void Validate()
        {
            if (SchemaVersion != 2 ||
                ProtocolVersion != PayloadBrokerProtocol.ProtocolVersion ||
                !Enum.IsDefined(typeof(PayloadBrokerOperation), Operation) ||
                BeforeOwnershipCas == null ||
                BeforeWorkspaceCas == null)
            {
                throw new InvalidOperationException(
                    "Payload broker command is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload broker transaction ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                RequestId,
                "Payload broker request ID");
            PayloadContractValidation.RequireSha256(
                CorrelationNonceDigest,
                "Payload broker correlation nonce digest");
            PayloadContractValidation.RequireSha256(
                PlanInvariantDigest,
                "Payload broker plan digest");
            BeforeOwnershipCas.Validate();
            BeforeWorkspaceCas.Validate();
            if (!String.Equals(
                    TransactionId,
                    BeforeWorkspaceCas.TransactionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker command binds a foreign workspace.");
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadBrokerCommand.v2",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        ProtocolVersion.ToString(
                            CultureInfo.InvariantCulture),
                        Operation.ToString(),
                        TransactionId,
                        RequestId,
                        CorrelationNonceDigest,
                        BeforeOwnershipCas.InvariantDigest,
                        BeforeWorkspaceCas.InvariantDigest,
                        PlanInvariantDigest
                    });
            }
        }
    }

    internal enum PayloadBrokerOwnershipTransitionTag
    {
        None = 0,
        ProvisionArm = 1,
        ProvisionObservePresent = 2,
        RemoveArm = 3,
        RemoveObserveAbsent = 4
    }

    [DataContract]
    internal sealed class PayloadBrokerOperationReceipt
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal PayloadBrokerOperation Operation;

        [DataMember(Order = 3, IsRequired = true)]
        internal PayloadBrokerOwnershipTransitionTag OwnershipTransitionTag;

        [DataMember(Order = 4, IsRequired = true)]
        internal PayloadNamespaceOwnershipCasToken BeforeOwnershipCas;

        [DataMember(Order = 5, IsRequired = true)]
        internal PayloadNamespaceOwnershipCasToken AfterOwnershipCas;

        [DataMember(Order = 6, IsRequired = true)]
        internal PayloadWorkspaceCasToken BeforeWorkspaceCas;

        [DataMember(Order = 7, IsRequired = true)]
        internal PayloadWorkspaceCasToken AfterWorkspaceCas;

        [DataMember(Order = 8, IsRequired = true)]
        internal PayloadNamespaceOwnershipObservation Observation;

        [DataMember(Order = 9, IsRequired = true)]
        internal string AppliedPlanInvariantDigest;

        internal void Validate()
        {
            if (SchemaVersion != 2 ||
                !Enum.IsDefined(typeof(PayloadBrokerOperation), Operation) ||
                !Enum.IsDefined(
                    typeof(PayloadBrokerOwnershipTransitionTag),
                    OwnershipTransitionTag) ||
                BeforeOwnershipCas == null ||
                AfterOwnershipCas == null ||
                BeforeWorkspaceCas == null ||
                AfterWorkspaceCas == null)
            {
                throw new InvalidOperationException(
                    "Payload broker operation receipt is incomplete.");
            }
            BeforeOwnershipCas.Validate();
            AfterOwnershipCas.Validate();
            BeforeWorkspaceCas.Validate();
            AfterWorkspaceCas.Validate();
            PayloadContractValidation.RequireSha256(
                AppliedPlanInvariantDigest,
                "Payload broker applied plan digest");
            RequireSameOwnershipIdentity();
            RequireSameWorkspaceIdentity();

            bool observationRequired =
                OwnershipTransitionTag ==
                    PayloadBrokerOwnershipTransitionTag.
                        ProvisionObservePresent ||
                OwnershipTransitionTag ==
                    PayloadBrokerOwnershipTransitionTag.
                        RemoveObserveAbsent;
            if (observationRequired)
            {
                if (Observation == null)
                {
                    throw new InvalidOperationException(
                        "Payload broker receipt is missing its observation.");
                }
                Observation.Validate();
                PayloadNamespaceOwnershipTransition expected =
                    OwnershipTransitionTag ==
                        PayloadBrokerOwnershipTransitionTag.
                            ProvisionObservePresent
                        ? PayloadNamespaceOwnershipTransition.
                            ProvisionObservePresent
                        : PayloadNamespaceOwnershipTransition.
                            RemoveObserveAbsent;
                if (Observation.Transition != expected)
                {
                    throw new InvalidOperationException(
                        "Payload broker receipt observation type differs.");
                }
            }
            else if (Observation != null)
            {
                throw new InvalidOperationException(
                    "Payload broker receipt has an unexpected observation.");
            }

            switch (Operation)
            {
                case PayloadBrokerOperation.Inspect:
                    RequireTag(
                        PayloadBrokerOwnershipTransitionTag.None);
                    RequireUnchangedOwnership();
                    RequireUnchangedWorkspace();
                    break;
                case PayloadBrokerOperation.AdvancePayload:
                case PayloadBrokerOperation.AdvancePurge:
                    RequireTag(
                        PayloadBrokerOwnershipTransitionTag.None);
                    RequireUnchangedOwnership();
                    RequireWorkspaceAdvance();
                    break;
                case PayloadBrokerOperation.ProvisionNamespace:
                    if (OwnershipTransitionTag !=
                            PayloadBrokerOwnershipTransitionTag.
                                ProvisionArm &&
                        OwnershipTransitionTag !=
                            PayloadBrokerOwnershipTransitionTag.
                                ProvisionObservePresent)
                    {
                        throw InvalidTransition();
                    }
                    RequireOwnershipAdvance();
                    RequireUnchangedWorkspace();
                    break;
                case PayloadBrokerOperation.RemoveNamespace:
                    if (OwnershipTransitionTag !=
                            PayloadBrokerOwnershipTransitionTag.RemoveArm &&
                        OwnershipTransitionTag !=
                            PayloadBrokerOwnershipTransitionTag.
                                RemoveObserveAbsent)
                    {
                        throw InvalidTransition();
                    }
                    RequireOwnershipAdvance();
                    RequireUnchangedWorkspace();
                    break;
                default:
                    throw InvalidTransition();
            }
        }

        internal void ValidateForCommand(PayloadBrokerCommand command)
        {
            Validate();
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }
            command.Validate();
            if (Operation != command.Operation ||
                !SameOwnership(
                    BeforeOwnershipCas,
                    command.BeforeOwnershipCas) ||
                !SameWorkspace(
                    BeforeWorkspaceCas,
                    command.BeforeWorkspaceCas))
            {
                throw new InvalidOperationException(
                    "Payload broker receipt does not bind the command CAS.");
            }
            if (!String.Equals(
                    AppliedPlanInvariantDigest,
                    command.PlanInvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker receipt applied a foreign plan.");
            }
            if (Observation != null &&
                !String.Equals(
                    Observation.PlanInvariantDigest,
                    command.PlanInvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker observation binds a foreign plan.");
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadBrokerOperationReceipt.v2",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        Operation.ToString(),
                        OwnershipTransitionTag.ToString(),
                        BeforeOwnershipCas.InvariantDigest,
                        AfterOwnershipCas.InvariantDigest,
                        BeforeWorkspaceCas.InvariantDigest,
                        AfterWorkspaceCas.InvariantDigest,
                        Observation == null
                            ? String.Empty
                            : Observation.InvariantDigest,
                        AppliedPlanInvariantDigest
                    });
            }
        }

        private void RequireSameOwnershipIdentity()
        {
            if (!String.Equals(
                    BeforeOwnershipCas.NamespaceId,
                    AfterOwnershipCas.NamespaceId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker ownership CAS identity changed.");
            }
        }

        private void RequireSameWorkspaceIdentity()
        {
            if (!String.Equals(
                    BeforeWorkspaceCas.TransactionId,
                    AfterWorkspaceCas.TransactionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker workspace CAS identity changed.");
            }
        }

        private void RequireTag(
            PayloadBrokerOwnershipTransitionTag expected)
        {
            if (OwnershipTransitionTag != expected)
            {
                throw InvalidTransition();
            }
        }

        private void RequireUnchangedOwnership()
        {
            if (!SameOwnership(
                    BeforeOwnershipCas,
                    AfterOwnershipCas))
            {
                throw InvalidTransition();
            }
        }

        private void RequireUnchangedWorkspace()
        {
            if (!SameWorkspace(
                    BeforeWorkspaceCas,
                    AfterWorkspaceCas))
            {
                throw InvalidTransition();
            }
        }

        private void RequireOwnershipAdvance()
        {
            if (AfterOwnershipCas.OwnershipRevision !=
                    checked(
                        BeforeOwnershipCas.OwnershipRevision + 1))
            {
                throw InvalidTransition();
            }
        }

        private void RequireWorkspaceAdvance()
        {
            if (AfterWorkspaceCas.Revision !=
                    checked(BeforeWorkspaceCas.Revision + 1))
            {
                throw InvalidTransition();
            }
        }

        private static bool SameOwnership(
            PayloadNamespaceOwnershipCasToken first,
            PayloadNamespaceOwnershipCasToken second)
        {
            return String.Equals(
                first.InvariantDigest,
                second.InvariantDigest,
                StringComparison.Ordinal);
        }

        private static bool SameWorkspace(
            PayloadWorkspaceCasToken first,
            PayloadWorkspaceCasToken second)
        {
            return String.Equals(
                first.InvariantDigest,
                second.InvariantDigest,
                StringComparison.Ordinal);
        }

        private static InvalidOperationException InvalidTransition()
        {
            return new InvalidOperationException(
                "Payload broker receipt is not the exact operation transition.");
        }
    }

    [DataContract]
    internal sealed class PayloadBrokerResponse
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal int ProtocolVersion;

        [DataMember(Order = 3, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 4, IsRequired = true)]
        internal string RequestId;

        [DataMember(Order = 5, IsRequired = true)]
        internal string CommandInvariantDigest;

        [DataMember(Order = 6, IsRequired = true)]
        internal PayloadBrokerOperationReceipt Receipt;

        [DataMember(Order = 7, IsRequired = true)]
        internal string ResultInvariantDigest;

        internal void Validate()
        {
            if (SchemaVersion != 2 ||
                ProtocolVersion != PayloadBrokerProtocol.ProtocolVersion ||
                Receipt == null)
            {
                throw new InvalidOperationException(
                    "Payload broker response is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload broker response transaction ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                RequestId,
                "Payload broker response request ID");
            PayloadContractValidation.RequireSha256(
                CommandInvariantDigest,
                "Payload broker response command digest");
            Receipt.Validate();
            if (!String.Equals(
                    ResultInvariantDigest,
                    Receipt.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker result digest is not canonical.");
            }
        }

        internal void ValidateForCommand(PayloadBrokerCommand command)
        {
            Validate();
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }
            command.Validate();
            if (!String.Equals(
                    TransactionId,
                    command.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    RequestId,
                    command.RequestId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    CommandInvariantDigest,
                    command.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker response binds a foreign command.");
            }
            Receipt.ValidateForCommand(command);
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadBrokerResponse.v2",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        ProtocolVersion.ToString(
                            CultureInfo.InvariantCulture),
                        TransactionId,
                        RequestId,
                        CommandInvariantDigest,
                        Receipt.InvariantDigest,
                        ResultInvariantDigest
                    });
            }
        }

        // A replay returns these original bytes unchanged. Entry hit/miss is
        // transport/store-local state and never mutates the wire response.
        internal byte[] GetCanonicalReplayBytes()
        {
            return PayloadBrokerResponseCodec.SerializeCanonical(this);
        }
    }

    internal static class PayloadBrokerCommandCodec
    {
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        internal static byte[] SerializeCanonical(
            PayloadBrokerCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }
            command.Validate();
            using (var stream = new MemoryStream())
            {
                NewSerializer().WriteObject(stream, command);
                byte[] bytes = stream.ToArray();
                RequirePayloadLength(bytes);
                return bytes;
            }
        }

        internal static PayloadBrokerCommand DeserializeAndValidate(
            byte[] bytes)
        {
            RequirePayloadLength(bytes);
            byte[] stableBytes = CopyBytes(bytes);
            if (stableBytes.Length >= 3 &&
                stableBytes[0] == 0xEF &&
                stableBytes[1] == 0xBB &&
                stableBytes[2] == 0xBF)
            {
                throw new InvalidOperationException(
                    "Payload broker command must not contain a UTF-8 BOM.");
            }
            try
            {
                StrictUtf8.GetCharCount(stableBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidOperationException(
                    "Payload broker command is not strict UTF-8.",
                    exception);
            }

            PayloadBrokerCommand command;
            try
            {
                using (var stream =
                    new MemoryStream(stableBytes, false))
                {
                    command =
                        (PayloadBrokerCommand)NewSerializer().
                            ReadObject(stream);
                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidOperationException(
                            "Payload broker command has trailing data.");
                    }
                }
                if (command == null)
                {
                    throw new InvalidOperationException(
                        "Payload broker command decoded to null.");
                }
                command.Validate();
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Payload broker command cannot be decoded.",
                    exception);
            }
            PayloadBrokerResponseCodec.RequireExactBytes(
                stableBytes,
                SerializeCanonical(command),
                "Payload broker command bytes are not canonical.");
            return command;
        }

        private static void RequirePayloadLength(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException(
                    "Payload broker command bytes are missing.");
            }
            if (bytes.Length >
                MaintenancePipeFrameCodec.MaxRequestPayload)
            {
                throw new InvalidOperationException(
                    "Payload broker command exceeds the request cap.");
            }
        }

        private static byte[] CopyBytes(byte[] bytes)
        {
            var copy = new byte[bytes.Length];
            Buffer.BlockCopy(
                bytes,
                0,
                copy,
                0,
                bytes.Length);
            return copy;
        }

        private static DataContractJsonSerializer NewSerializer()
        {
            return new DataContractJsonSerializer(
                typeof(PayloadBrokerCommand),
                new[]
                {
                    typeof(PayloadNamespaceOwnershipCasToken),
                    typeof(PayloadWorkspaceCasToken)
                });
        }
    }

    // This slice defines only the canonical replay entry and wire codec. It
    // does not claim persistence, write-before-ack, or recovery semantics.
    internal static class PayloadBrokerResponseCodec
    {
        internal static byte[] SerializeCanonical(
            PayloadBrokerResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException("response");
            }
            response.Validate();
            var serializer = NewSerializer();
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, response);
                return stream.ToArray();
            }
        }

        internal static PayloadBrokerResponse DeserializeAndValidate(
            byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException(
                    "Canonical replay response bytes are missing.");
            }
            PayloadBrokerResponse response;
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                {
                    response =
                        (PayloadBrokerResponse)NewSerializer().
                            ReadObject(stream);
                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidOperationException(
                            "Canonical replay response has trailing data.");
                    }
                }
                response.Validate();
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Canonical replay response cannot be decoded.",
                    exception);
            }
            RequireExactBytes(
                bytes,
                SerializeCanonical(response),
                "Replay response bytes are not canonical.");
            return response;
        }

        internal static void RequireExactBytes(
            byte[] first,
            byte[] second,
            string message)
        {
            if (first == null ||
                second == null ||
                first.Length != second.Length)
            {
                throw new InvalidOperationException(message);
            }
            int difference = 0;
            for (int index = 0; index < first.Length; ++index)
            {
                difference |= first[index] ^ second[index];
            }
            if (difference != 0)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static DataContractJsonSerializer NewSerializer()
        {
            return new DataContractJsonSerializer(
                typeof(PayloadBrokerResponse),
                new[]
                {
                    typeof(PayloadBrokerOperationReceipt),
                    typeof(PayloadNamespaceOwnershipCasToken),
                    typeof(PayloadWorkspaceCasToken),
                    typeof(PayloadNamespaceOwnershipObservation)
                });
        }
    }

    internal enum MaintenancePipeFrameKind : ushort
    {
        Request = 1,
        Response = 2,
        Ack = 3
    }

    internal sealed class MaintenancePipeFrame
    {
        private readonly byte[] payload;

        internal MaintenancePipeFrame(
            MaintenancePipeFrameKind kind,
            byte[] value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }
            Kind = kind;
            payload = new byte[value.Length];
            Buffer.BlockCopy(
                value,
                0,
                payload,
                0,
                value.Length);
        }

        internal readonly MaintenancePipeFrameKind Kind;

        internal int PayloadLength
        {
            get { return payload.Length; }
        }

        internal byte[] GetPayloadCopy()
        {
            var copy = new byte[payload.Length];
            Buffer.BlockCopy(
                payload,
                0,
                copy,
                0,
                payload.Length);
            return copy;
        }
    }

    internal static class MaintenancePipeFrameCodec
    {
        internal const int HeaderLength = 16;
        internal const int MaxRequestPayload = 64 * 1024;
        internal const int MaxResponsePayload = 1024 * 1024;
        internal const int AckPayloadLength = 32;
        private const ushort Version = 1;

        internal static byte[] Encode(
            MaintenancePipeFrameKind kind,
            byte[] payload)
        {
            ValidatePayload(kind, payload);
            int payloadLength = payload.Length;
            var frame = new byte[HeaderLength + payloadLength];
            frame[0] = (byte)'S';
            frame[1] = (byte)'B';
            frame[2] = (byte)'M';
            frame[3] = (byte)'S';
            WriteUInt16(frame, 4, Version);
            WriteUInt16(frame, 6, (ushort)kind);
            WriteUInt32(frame, 8, (uint)payloadLength);
            WriteUInt32(frame, 12, 0);
            Buffer.BlockCopy(
                payload,
                0,
                frame,
                HeaderLength,
                payloadLength);
            return frame;
        }

        internal static byte[] Encode(MaintenancePipeFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException("frame");
            }
            return Encode(
                frame.Kind,
                frame.GetPayloadCopy());
        }

        internal static MaintenancePipeFrame Decode(byte[] frameBytes)
        {
            if (frameBytes == null ||
                frameBytes.Length < HeaderLength)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe frame header is incomplete.");
            }
            if (frameBytes[0] != (byte)'S' ||
                frameBytes[1] != (byte)'B' ||
                frameBytes[2] != (byte)'M' ||
                frameBytes[3] != (byte)'S')
            {
                throw new InvalidOperationException(
                    "Maintenance pipe frame magic is invalid.");
            }
            if (ReadUInt16(frameBytes, 4) != Version)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe frame version is unsupported.");
            }
            ushort rawKind = ReadUInt16(frameBytes, 6);
            if (rawKind < (ushort)MaintenancePipeFrameKind.Request ||
                rawKind > (ushort)MaintenancePipeFrameKind.Ack)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe frame kind is unsupported.");
            }
            if (ReadUInt32(frameBytes, 12) != 0)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe frame reserved field is nonzero.");
            }
            uint rawPayloadLength =
                ReadUInt32(frameBytes, 8);
            if (rawPayloadLength > Int32.MaxValue)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe frame payload length overflows Int32.");
            }
            var kind = (MaintenancePipeFrameKind)rawKind;
            int payloadLength = (int)rawPayloadLength;
            ValidatePayloadLength(kind, payloadLength);
            long expectedLength =
                (long)HeaderLength + payloadLength;
            if (expectedLength != frameBytes.Length)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe frame length does not match its header.");
            }
            var payload = new byte[payloadLength];
            Buffer.BlockCopy(
                frameBytes,
                HeaderLength,
                payload,
                0,
                payloadLength);
            return new MaintenancePipeFrame(kind, payload);
        }

        internal static byte[] EncodeAck(
            byte[] canonicalResponsePayload)
        {
            byte[] stablePayload =
                CopyResponsePayload(canonicalResponsePayload);
            using (SHA256 algorithm = SHA256.Create())
            {
                return Encode(
                    MaintenancePipeFrameKind.Ack,
                    algorithm.ComputeHash(stablePayload));
            }
        }

        internal static void DecodeAckAndVerify(
            byte[] ackFrameBytes,
            byte[] canonicalResponsePayload)
        {
            MaintenancePipeFrame frame = Decode(ackFrameBytes);
            if (frame.Kind != MaintenancePipeFrameKind.Ack)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe acknowledgement kind is invalid.");
            }
            byte[] stablePayload =
                CopyResponsePayload(canonicalResponsePayload);
            byte[] expected;
            using (SHA256 algorithm = SHA256.Create())
            {
                expected = algorithm.ComputeHash(stablePayload);
            }
            byte[] actual = frame.GetPayloadCopy();
            if (!FixedTimeEquals(actual, expected))
            {
                throw new InvalidOperationException(
                    "Maintenance pipe acknowledgement digest differs.");
            }
        }

        private static byte[] CopyResponsePayload(byte[] payload)
        {
            if (payload == null ||
                payload.Length == 0 ||
                payload.Length > MaxResponsePayload)
            {
                throw new InvalidOperationException(
                    "Canonical response payload length is invalid.");
            }
            var copy = new byte[payload.Length];
            Buffer.BlockCopy(
                payload,
                0,
                copy,
                0,
                payload.Length);
            return copy;
        }

        private static void ValidatePayload(
            MaintenancePipeFrameKind kind,
            byte[] payload)
        {
            if (payload == null)
            {
                throw new InvalidOperationException(
                    "Maintenance pipe frame payload is missing.");
            }
            ValidatePayloadLength(kind, payload.Length);
        }

        private static void ValidatePayloadLength(
            MaintenancePipeFrameKind kind,
            int payloadLength)
        {
            if (kind == MaintenancePipeFrameKind.Request)
            {
                if (payloadLength == 0 ||
                    payloadLength > MaxRequestPayload)
                {
                    throw new InvalidOperationException(
                        "Maintenance pipe request payload length is invalid.");
                }
                return;
            }
            if (kind == MaintenancePipeFrameKind.Response)
            {
                if (payloadLength == 0 ||
                    payloadLength > MaxResponsePayload)
                {
                    throw new InvalidOperationException(
                        "Maintenance pipe response payload length is invalid.");
                }
                return;
            }
            if (kind == MaintenancePipeFrameKind.Ack)
            {
                if (payloadLength != AckPayloadLength)
                {
                    throw new InvalidOperationException(
                        "Maintenance pipe acknowledgement payload length is invalid.");
                }
                return;
            }
            throw new InvalidOperationException(
                "Maintenance pipe frame kind is unsupported.");
        }

        private static bool FixedTimeEquals(
            byte[] first,
            byte[] second)
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

        private static ushort ReadUInt16(
            byte[] value,
            int offset)
        {
            return (ushort)(
                value[offset] |
                (value[offset + 1] << 8));
        }

        private static uint ReadUInt32(
            byte[] value,
            int offset)
        {
            return
                (uint)value[offset] |
                ((uint)value[offset + 1] << 8) |
                ((uint)value[offset + 2] << 16) |
                ((uint)value[offset + 3] << 24);
        }

        private static void WriteUInt16(
            byte[] value,
            int offset,
            ushort item)
        {
            value[offset] = (byte)item;
            value[offset + 1] = (byte)(item >> 8);
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

    [DataContract]
    internal sealed class PayloadBrokerReplayLedgerEntry
    {
        [DataMember(Order = 1, IsRequired = true)]
        internal int SchemaVersion;

        [DataMember(Order = 2, IsRequired = true)]
        internal string TransactionId;

        [DataMember(Order = 3, IsRequired = true)]
        internal string RequestId;

        [DataMember(Order = 4, IsRequired = true)]
        internal string CommandInvariantDigest;

        [DataMember(Order = 5, IsRequired = true)]
        internal string ResponseInvariantDigest;

        [DataMember(Order = 6, IsRequired = true)]
        internal PayloadBrokerResponse Response;

        [DataMember(Order = 7, IsRequired = true)]
        internal byte[] CanonicalResponseBytes;

        [DataMember(Order = 8, IsRequired = true)]
        internal string CanonicalResponseBytesSha256;

        internal void Validate()
        {
            if (SchemaVersion != 1 ||
                Response == null ||
                CanonicalResponseBytes == null ||
                CanonicalResponseBytes.Length == 0)
            {
                throw new InvalidOperationException(
                    "Payload broker canonical replay entry is incomplete.");
            }
            PayloadContractValidation.RequireCanonicalTransactionId(
                TransactionId,
                "Payload broker replay transaction ID");
            PayloadContractValidation.RequireCanonicalTransactionId(
                RequestId,
                "Payload broker replay request ID");
            PayloadContractValidation.RequireSha256(
                CommandInvariantDigest,
                "Payload broker replay command digest");
            PayloadContractValidation.RequireSha256(
                ResponseInvariantDigest,
                "Payload broker replay response digest");
            Response.Validate();
            PayloadBrokerResponse wireResponse =
                PayloadBrokerResponseCodec.DeserializeAndValidate(
                    CanonicalResponseBytes);
            if (!String.Equals(
                    TransactionId,
                    Response.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    RequestId,
                    Response.RequestId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    CommandInvariantDigest,
                    Response.CommandInvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    ResponseInvariantDigest,
                    Response.InvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    TransactionId,
                    wireResponse.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    RequestId,
                    wireResponse.RequestId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    CommandInvariantDigest,
                    wireResponse.CommandInvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    ResponseInvariantDigest,
                    wireResponse.InvariantDigest,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    Response.InvariantDigest,
                    wireResponse.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker replay metadata differs from its response.");
            }
            PayloadBrokerResponseCodec.RequireExactBytes(
                CanonicalResponseBytes,
                Response.GetCanonicalReplayBytes(),
                "Payload broker replay bytes differ from its response.");
            if (!String.Equals(
                    CanonicalResponseBytesSha256,
                    ComputeBytesSha256(CanonicalResponseBytes),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker replay bytes digest differs.");
            }
        }

        internal void ValidateRequest(PayloadBrokerCommand command)
        {
            Validate();
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }
            command.Validate();
            if (!String.Equals(
                    TransactionId,
                    command.TransactionId,
                    StringComparison.Ordinal) ||
                !String.Equals(
                    RequestId,
                    command.RequestId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker replay key differs.");
            }
            if (!String.Equals(
                    CommandInvariantDigest,
                    command.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker replay key was reused with a different command.");
            }
            Response.ValidateForCommand(command);
        }

        internal void RequireByteEquivalentResult(byte[] candidate)
        {
            Validate();
            if (candidate == null ||
                candidate.Length != CanonicalResponseBytes.Length ||
                !String.Equals(
                    ComputeBytesSha256(candidate),
                    CanonicalResponseBytesSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Payload broker replay result is not byte-equivalent.");
            }
            int difference = 0;
            for (int index = 0;
                index < CanonicalResponseBytes.Length;
                ++index)
            {
                difference |=
                    CanonicalResponseBytes[index] ^ candidate[index];
            }
            if (difference != 0)
            {
                throw new InvalidOperationException(
                    "Payload broker replay result is not byte-equivalent.");
            }
        }

        internal string InvariantDigest
        {
            get
            {
                Validate();
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.PayloadBrokerReplayLedgerEntry.v1",
                    new[]
                    {
                        SchemaVersion.ToString(
                            CultureInfo.InvariantCulture),
                        TransactionId,
                        RequestId,
                        CommandInvariantDigest,
                        ResponseInvariantDigest,
                        CanonicalResponseBytesSha256
                    });
            }
        }

        internal static string ComputeBytesSha256(byte[] value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] digest = algorithm.ComputeHash(value);
                var builder = new StringBuilder(digest.Length * 2);
                foreach (byte item in digest)
                {
                    builder.Append(
                        item.ToString(
                            "x2",
                            CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }

    }
}
