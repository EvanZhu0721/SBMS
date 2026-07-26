using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SBMSSetup
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MaintenanceNativeLuid
    {
        internal uint LowPart;
        internal int HighPart;

        internal long Value
        {
            get
            {
                return ((long)HighPart << 32) | LowPart;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MaintenanceNativeSidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MaintenanceNativeTokenGroups
    {
        internal uint GroupCount;
        internal MaintenanceNativeSidAndAttributes Groups;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MaintenanceNativeTokenStatistics
    {
        internal MaintenanceNativeLuid TokenId;
        internal MaintenanceNativeLuid AuthenticationId;
        internal long ExpirationTime;
        internal int TokenType;
        internal int ImpersonationLevel;
        internal uint DynamicCharged;
        internal uint DynamicAvailable;
        internal uint GroupCount;
        internal uint PrivilegeCount;
        internal MaintenanceNativeLuid ModifiedId;
    }

    internal enum MaintenanceTokenInformationClass
    {
        User = 1,
        Groups = 2,
        Type = 8,
        ImpersonationLevel = 9,
        Statistics = 10,
        ElevationType = 18,
        Elevation = 20,
        IntegrityLevel = 25,
        HasRestrictions = 21,
        IsAppContainer = 29
    }

    internal sealed class MaintenanceSafeTokenHandle
        : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly bool closeNative;
        private readonly Action<IntPtr> releaseForTest;

        private MaintenanceSafeTokenHandle()
            : base(true)
        {
            closeNative = true;
        }

        internal MaintenanceSafeTokenHandle(
            IntPtr value,
            bool ownsNativeHandle)
            : this(value, ownsNativeHandle, null)
        {
        }

        internal MaintenanceSafeTokenHandle(
            IntPtr value,
            bool ownsNativeHandle,
            Action<IntPtr> releaseForTest)
            : base(true)
        {
            SetHandle(value);
            closeNative = ownsNativeHandle;
            this.releaseForTest = releaseForTest;
        }

        [ReliabilityContract(
            Consistency.WillNotCorruptState,
            Cer.Success)]
        protected override bool ReleaseHandle()
        {
            if (releaseForTest != null)
            {
                releaseForTest(handle);
                return true;
            }
            return !closeNative ||
                MaintenanceWindowsNativeMethods.CloseHandle(handle);
        }
    }

    internal interface IMaintenanceWindowsTokenNative
    {
        MaintenanceSafeTokenHandle OpenCurrentThreadTokenForQuery();

        bool GetTokenInformation(
            MaintenanceSafeTokenHandle token,
            MaintenanceTokenInformationClass informationClass,
            IntPtr buffer,
            int bufferLength,
            out int returnLength,
            out int error);

        byte[] CopySid(
            IntPtr sid,
            IntPtr containingBuffer,
            int containingLength);
        bool IsTokenRestricted(
            MaintenanceSafeTokenHandle token);
    }

    internal sealed class MaintenanceWindowsTokenNative
        : IMaintenanceWindowsTokenNative
    {
        private const uint TokenQuery = 0x0008;

        public MaintenanceSafeTokenHandle
            OpenCurrentThreadTokenForQuery()
        {
            MaintenanceSafeTokenHandle token;
            if (!MaintenanceWindowsNativeMethods.OpenThreadToken(
                    MaintenanceWindowsNativeMethods.GetCurrentThread(),
                    TokenQuery,
                    true,
                    out token))
            {
                int error = Marshal.GetLastWin32Error();
                if (token != null)
                {
                    token.Dispose();
                }
                throw new Win32Exception(
                    error,
                    "OpenThreadToken(TOKEN_QUERY) failed.");
            }
            if (token == null || token.IsInvalid)
            {
                if (token != null)
                {
                    token.Dispose();
                }
                throw new InvalidDataException(
                    "OpenThreadToken returned an invalid handle.");
            }
            return token;
        }

        public bool GetTokenInformation(
            MaintenanceSafeTokenHandle token,
            MaintenanceTokenInformationClass informationClass,
            IntPtr buffer,
            int bufferLength,
            out int returnLength,
            out int error)
        {
            bool result =
                MaintenanceWindowsNativeMethods.GetTokenInformation(
                    token,
                    (int)informationClass,
                    buffer,
                    bufferLength,
                    out returnLength);
            error = result ? 0 : Marshal.GetLastWin32Error();
            return result;
        }

        public byte[] CopySid(
            IntPtr sid,
            IntPtr containingBuffer,
            int containingLength)
        {
            if (containingBuffer == IntPtr.Zero ||
                containingLength <= 0)
            {
                throw new InvalidDataException(
                    "SID containing buffer is invalid.");
            }
            long start = containingBuffer.ToInt64();
            long end;
            try
            {
                end = checked(start + containingLength);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "SID containing buffer overflows.",
                    exception);
            }
            long address = sid.ToInt64();
            if (sid == IntPtr.Zero ||
                address < start ||
                address >= end)
            {
                throw new InvalidDataException(
                    "Token SID is invalid or outside its buffer.");
            }
            long sidEnd;
            try
            {
                if (checked(address + 8) > end)
                {
                    throw new InvalidDataException(
                        "Token SID header is truncated.");
                }
                byte revision = Marshal.ReadByte(sid);
                byte subAuthorityCount =
                    Marshal.ReadByte(sid, 1);
                if (revision != 1 || subAuthorityCount > 15)
                {
                    throw new InvalidDataException(
                        "Token SID header is invalid.");
                }
                int expectedLength =
                    checked(8 + (subAuthorityCount * 4));
                sidEnd = checked(address + expectedLength);
                if (expectedLength < 8 ||
                    expectedLength > 68 ||
                    sidEnd > end ||
                    !MaintenanceWindowsNativeMethods.IsValidSid(sid))
                {
                    throw new InvalidDataException(
                        "Token SID is invalid.");
                }
                int length =
                    MaintenanceWindowsNativeMethods.GetLengthSid(sid);
                if (length != expectedLength)
                {
                    throw new InvalidDataException(
                        "Token SID length disagrees with its header.");
                }
                byte[] copy = new byte[length];
                Marshal.Copy(sid, copy, 0, length);
                return copy;
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "Token SID length overflows.",
                    exception);
            }
        }

        public bool IsTokenRestricted(
            MaintenanceSafeTokenHandle token)
        {
            return MaintenanceWindowsNativeMethods.
                IsTokenRestricted(token);
        }
    }

    internal sealed class MaintenanceWindowsTokenSnapshotReader
        : IMaintenanceClientTokenCapture
    {
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorBadLength = 24;
        private const int MaximumTokenInformationLength =
            1024 * 1024;
        private const int MaximumGroups = 4096;
        private const int MaximumSnapshotAttempts = 3;
        private const int MaximumResizeAttempts = 3;
        private readonly IMaintenanceWindowsTokenNative native;

        internal MaintenanceWindowsTokenSnapshotReader()
            : this(new MaintenanceWindowsTokenNative())
        {
        }

        internal MaintenanceWindowsTokenSnapshotReader(
            IMaintenanceWindowsTokenNative native)
        {
            if (native == null)
            {
                throw new ArgumentNullException("native");
            }
            this.native = native;
        }

        public MaintenanceClientTokenEvidence Capture()
        {
            using (MaintenanceSafeTokenHandle token =
                native.OpenCurrentThreadTokenForQuery())
            {
                if (token == null ||
                    token.IsInvalid ||
                    token.IsClosed)
                {
                    throw new InvalidDataException(
                        "Thread token reader received no valid handle.");
                }
                for (int attempt = 0;
                    attempt < MaximumSnapshotAttempts;
                    ++attempt)
                {
                    MaintenanceNativeTokenStatistics before =
                        ReadStatistics(token);
                    string userSid = ReadUserSid(token);
                    IList<MaintenanceClientTokenGroupEvidence> groups =
                        ReadGroups(token, before.GroupCount);
                    bool elevated =
                        ReadBoolean(
                            token,
                            MaintenanceTokenInformationClass.Elevation);
                    MaintenanceTokenElevationType elevationType =
                        ReadElevationType(token);
                    int integrityRid = ReadIntegrityRid(token);
                    bool appContainer =
                        ReadBoolean(
                            token,
                            MaintenanceTokenInformationClass.
                                IsAppContainer);
                    bool nativeRestricted =
                        native.IsTokenRestricted(token);
                    bool informationRestricted =
                        ReadRestrictionBoolean(
                            token,
                            MaintenanceTokenInformationClass.
                                HasRestrictions);
                    bool restricted =
                        nativeRestricted | informationRestricted;
                    MaintenanceClientTokenType tokenType =
                        ReadTokenType(token);
                    MaintenanceClientImpersonationLevel level =
                        ReadImpersonationLevel(token);
                    MaintenanceNativeTokenStatistics after =
                        ReadStatistics(token);
                    if (!SameSnapshot(before, after))
                    {
                        continue;
                    }
                    RequireIndependentTokenIdentity(
                        after,
                        tokenType,
                        level);
                    return new MaintenanceClientTokenEvidence(
                        userSid,
                        groups,
                        elevated,
                        elevationType,
                        integrityRid,
                        appContainer,
                        restricted,
                        tokenType,
                        level,
                        after.AuthenticationId.Value);
                }
                throw new IOException(
                    "Token snapshot changed during all bounded reads.");
            }
        }

        private string ReadUserSid(
            MaintenanceSafeTokenHandle token)
        {
            using (SafeHGlobalBuffer buffer =
                Query(token, MaintenanceTokenInformationClass.User))
            {
                int fixedHeaderLength =
                    Marshal.SizeOf(
                        typeof(MaintenanceNativeSidAndAttributes));
                RequireLength(
                    buffer,
                    fixedHeaderLength,
                    "TokenUser");
                return ReadSid(
                    Marshal.ReadIntPtr(
                        buffer.DangerousGetHandle()),
                    buffer,
                    fixedHeaderLength);
            }
        }

        private IList<MaintenanceClientTokenGroupEvidence> ReadGroups(
            MaintenanceSafeTokenHandle token,
            uint expectedGroupCount)
        {
            using (SafeHGlobalBuffer buffer =
                Query(token, MaintenanceTokenInformationClass.Groups))
            {
                RequireLength(buffer, sizeof(uint), "TokenGroups");
                uint count =
                    unchecked((uint)Marshal.ReadInt32(
                        buffer.DangerousGetHandle()));
                int offset =
                    checked((int)Marshal.OffsetOf(
                        typeof(MaintenanceNativeTokenGroups),
                        "Groups"));
                int entrySize =
                    Marshal.SizeOf(
                        typeof(MaintenanceNativeSidAndAttributes));
                long required =
                    (long)offset + ((long)count * entrySize);
                if (count > MaximumGroups ||
                    count != expectedGroupCount ||
                    required > buffer.Length)
                {
                    throw new InvalidDataException(
                        "TokenGroups length is invalid.");
                }
                var groups =
                    new List<MaintenanceClientTokenGroupEvidence>(
                        (int)count);
                for (int index = 0; index < (int)count; ++index)
                {
                    IntPtr entry =
                        Add(
                            buffer.DangerousGetHandle(),
                            offset + (index * entrySize));
                    IntPtr sid = Marshal.ReadIntPtr(entry);
                    uint attributes =
                        unchecked((uint)Marshal.ReadInt32(
                            entry,
                            IntPtr.Size));
                    groups.Add(
                        new MaintenanceClientTokenGroupEvidence(
                            ReadSid(
                                sid,
                                buffer,
                                checked(
                                    offset +
                                    ((int)count * entrySize))),
                            (MaintenanceTokenGroupAttributes)
                                attributes));
                }
                return groups;
            }
        }

        private bool ReadBoolean(
            MaintenanceSafeTokenHandle token,
            MaintenanceTokenInformationClass informationClass)
        {
            int value = ReadInt32(token, informationClass);
            if (value != 0 && value != 1)
            {
                throw new InvalidDataException(
                    informationClass + " boolean is invalid.");
            }
            return value == 1;
        }

        private bool ReadRestrictionBoolean(
            MaintenanceSafeTokenHandle token,
            MaintenanceTokenInformationClass informationClass)
        {
            using (SafeHGlobalBuffer buffer =
                Query(token, informationClass))
            {
                // TOKEN_HAS_RESTRICTIONS is documented as DWORD. Current
                // Windows 11 also returns a one-byte BOOLEAN for class 21.
                // Accept only those two observed ABI widths; neither a
                // truncated DWORD nor an arbitrary oversized value is valid.
                if (buffer.Length != 1 &&
                    buffer.Length != sizeof(int))
                {
                    throw new InvalidDataException(
                        "GetTokenInformation raw class=" +
                        informationClass +
                        " returnLength=" + buffer.Length +
                        " error=0 expected=1-or-4.");
                }
                int value =
                    buffer.Length == 1
                        ? Marshal.ReadByte(
                            buffer.DangerousGetHandle())
                        : Marshal.ReadInt32(
                            buffer.DangerousGetHandle());
                if (value != 0 && value != 1)
                {
                    throw new InvalidDataException(
                        informationClass +
                        " boolean is invalid.");
                }
                return value == 1;
            }
        }

        private MaintenanceTokenElevationType ReadElevationType(
            MaintenanceSafeTokenHandle token)
        {
            int value =
                ReadInt32(
                    token,
                    MaintenanceTokenInformationClass.ElevationType);
            switch (value)
            {
                case 1:
                    return MaintenanceTokenElevationType.Default;
                case 2:
                    return MaintenanceTokenElevationType.Full;
                case 3:
                    return MaintenanceTokenElevationType.Limited;
                default:
                    throw new InvalidDataException(
                        "TokenElevationType is invalid.");
            }
        }

        private int ReadIntegrityRid(
            MaintenanceSafeTokenHandle token)
        {
            using (SafeHGlobalBuffer buffer =
                Query(
                    token,
                    MaintenanceTokenInformationClass.IntegrityLevel))
            {
                int fixedHeaderLength =
                    Marshal.SizeOf(
                        typeof(MaintenanceNativeSidAndAttributes));
                RequireLength(
                    buffer,
                    fixedHeaderLength,
                    "TokenIntegrityLevel");
                string sid =
                    ReadSid(
                        Marshal.ReadIntPtr(
                            buffer.DangerousGetHandle()),
                        buffer,
                        fixedHeaderLength);
                const string prefix = "S-1-16-";
                if (!sid.StartsWith(
                        prefix,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Integrity SID is not S-1-16-RID.");
                }
                string ridText = sid.Substring(prefix.Length);
                uint rid;
                if (ridText.Length == 0 ||
                    ridText.IndexOf('-') >= 0 ||
                    !UInt32.TryParse(ridText, out rid) ||
                    rid > Int32.MaxValue)
                {
                    throw new InvalidDataException(
                        "Integrity SID RID is invalid.");
                }
                return (int)rid;
            }
        }

        private MaintenanceClientTokenType ReadTokenType(
            MaintenanceSafeTokenHandle token)
        {
            int value =
                ReadInt32(
                    token,
                    MaintenanceTokenInformationClass.Type);
            switch (value)
            {
                case 1:
                    return MaintenanceClientTokenType.Primary;
                case 2:
                    return MaintenanceClientTokenType.Impersonation;
                default:
                    throw new InvalidDataException(
                        "TokenType is invalid.");
            }
        }

        private MaintenanceClientImpersonationLevel
            ReadImpersonationLevel(
                MaintenanceSafeTokenHandle token)
        {
            int value =
                ReadInt32(
                    token,
                    MaintenanceTokenInformationClass.
                        ImpersonationLevel);
            if (value < 0 || value > 3)
            {
                throw new InvalidDataException(
                    "TokenImpersonationLevel is invalid.");
            }
            return (MaintenanceClientImpersonationLevel)value;
        }

        private MaintenanceNativeTokenStatistics ReadStatistics(
            MaintenanceSafeTokenHandle token)
        {
            using (SafeHGlobalBuffer buffer =
                Query(
                    token,
                    MaintenanceTokenInformationClass.Statistics))
            {
                RequireExactLength(
                    buffer,
                    Marshal.SizeOf(
                        typeof(MaintenanceNativeTokenStatistics)),
                    "TokenStatistics");
                return
                    (MaintenanceNativeTokenStatistics)
                    Marshal.PtrToStructure(
                        buffer.DangerousGetHandle(),
                        typeof(MaintenanceNativeTokenStatistics));
            }
        }

        private string ReadSid(
            IntPtr sid,
            SafeHGlobalBuffer buffer,
            int minimumSidOffset)
        {
            long minimumSidAddress =
                checked(
                    buffer.DangerousGetHandle().ToInt64() +
                    minimumSidOffset);
            if (minimumSidOffset < 0 ||
                sid.ToInt64() < minimumSidAddress)
            {
                throw new InvalidDataException(
                    "Token SID points into its fixed native header.");
            }
            byte[] copy =
                native.CopySid(
                    sid,
                    buffer.DangerousGetHandle(),
                    buffer.Length);
            if (copy == null || copy.Length < 8 || copy.Length > 68)
            {
                throw new InvalidDataException(
                    "Native SID copy is invalid.");
            }
            try
            {
                return new SecurityIdentifier(copy, 0).Value;
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "Native SID copy cannot be parsed.",
                    exception);
            }
        }

        private static bool SameSnapshot(
            MaintenanceNativeTokenStatistics first,
            MaintenanceNativeTokenStatistics second)
        {
            return
                first.TokenId.Value == second.TokenId.Value &&
                first.AuthenticationId.Value ==
                    second.AuthenticationId.Value &&
                first.ExpirationTime == second.ExpirationTime &&
                first.ModifiedId.Value == second.ModifiedId.Value &&
                first.GroupCount == second.GroupCount &&
                first.PrivilegeCount == second.PrivilegeCount &&
                first.DynamicCharged == second.DynamicCharged &&
                first.DynamicAvailable == second.DynamicAvailable &&
                first.TokenType == second.TokenType &&
                first.ImpersonationLevel ==
                    second.ImpersonationLevel;
        }

        private static void RequireIndependentTokenIdentity(
            MaintenanceNativeTokenStatistics statistics,
            MaintenanceClientTokenType tokenType,
            MaintenanceClientImpersonationLevel impersonationLevel)
        {
            int expectedType =
                tokenType == MaintenanceClientTokenType.Primary
                    ? 1
                    : 2;
            if (statistics.TokenType != expectedType ||
                statistics.ImpersonationLevel !=
                    (int)impersonationLevel)
            {
                throw new InvalidDataException(
                    "Independent token type or impersonation level " +
                    "disagrees with TokenStatistics.");
            }
        }

        private int ReadInt32(
            MaintenanceSafeTokenHandle token,
            MaintenanceTokenInformationClass informationClass)
        {
            using (SafeHGlobalBuffer buffer =
                Query(token, informationClass))
            {
                RequireExactLength(
                    buffer,
                    sizeof(int),
                    informationClass.ToString());
                return Marshal.ReadInt32(
                    buffer.DangerousGetHandle());
            }
        }

        private SafeHGlobalBuffer Query(
            MaintenanceSafeTokenHandle token,
            MaintenanceTokenInformationClass informationClass)
        {
            int required;
            int error;
            bool probe =
                native.GetTokenInformation(
                    token,
                    informationClass,
                    IntPtr.Zero,
                    0,
                    out required,
                    out error);
            if (probe ||
                (error != ErrorInsufficientBuffer &&
                 error != ErrorBadLength) ||
                required <= 0 ||
                required > MaximumTokenInformationLength)
            {
                throw new InvalidDataException(
                    informationClass +
                    " size probe returned an invalid contract. " +
                    "success=" + probe +
                    " error=" + error +
                    " required=" + required);
            }

            for (int attempt = 0;
                attempt < MaximumResizeAttempts;
                ++attempt)
            {
                if (required <= 0 ||
                    required > MaximumTokenInformationLength)
                {
                    throw new InvalidDataException(
                        informationClass +
                        " requested an invalid resize length.");
                }
                var buffer = new SafeHGlobalBuffer(required);
                try
                {
                    int returned;
                    if (native.GetTokenInformation(
                            token,
                            informationClass,
                            buffer.DangerousGetHandle(),
                            required,
                            out returned,
                            out error))
                    {
                        if (returned <= 0 || returned > required)
                        {
                            throw new InvalidDataException(
                                informationClass +
                                " returned an invalid length.");
                        }
                        buffer.SetLength(returned);
                        return buffer;
                    }
                    if ((error != ErrorInsufficientBuffer &&
                         error != ErrorBadLength) ||
                        returned <= required ||
                        returned > MaximumTokenInformationLength)
                    {
                        throw new Win32Exception(
                            error,
                            informationClass +
                            " query failed after size probe.");
                    }
                    required = returned;
                }
                catch
                {
                    buffer.Dispose();
                    throw;
                }
                buffer.Dispose();
            }
            throw new IOException(
                informationClass +
                " exceeded bounded buffer resize attempts.");
        }

        private static void RequireLength(
            SafeHGlobalBuffer buffer,
            int required,
            string label)
        {
            if (required < 0 || buffer.Length < required)
            {
                throw new InvalidDataException(
                    label + " buffer is truncated.");
            }
        }

        private static void RequireExactLength(
            SafeHGlobalBuffer buffer,
            int required,
            string label)
        {
            if (required < 0 || buffer.Length != required)
            {
                throw new InvalidDataException(
                    "GetTokenInformation raw class=" + label +
                    " returnLength=" + buffer.Length +
                    " error=0 expected=" + required + ".");
            }
        }

        private static IntPtr Add(IntPtr pointer, int offset)
        {
            return new IntPtr(
                checked(pointer.ToInt64() + offset));
        }
    }

    internal interface IMaintenanceNamedPipeClientNative
    {
        int GetCurrentThreadId();

        void AcquireClient(
            IntPtr borrowedPipeHandle,
            ref bool armed,
            out int error);

        bool RevertToSelf(out int error);
    }

    internal sealed class MaintenanceNamedPipeClientNative
        : IMaintenanceNamedPipeClientNative
    {
        public int GetCurrentThreadId()
        {
            return unchecked(
                (int)MaintenanceWindowsNativeMethods.
                    GetCurrentThreadIdValue());
        }

        public void AcquireClient(
            IntPtr borrowedPipeHandle,
            ref bool armed,
            out int error)
        {
            bool succeeded = false;
            int nativeError = 0;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
            }
            finally
            {
                // The P/Invoke, success observation, and ownership arm are
                // one CER finally. No managed allocation can separate them.
                if (MaintenanceWindowsNativeMethods.
                        ImpersonateNamedPipeClient(
                            borrowedPipeHandle))
                {
                    succeeded = true;
                    armed = true;
                }
                else
                {
                    nativeError = Marshal.GetLastWin32Error();
                }
            }
            error = succeeded ? 0 : nativeError;
        }

        public bool RevertToSelf(out int error)
        {
            bool reverted =
                MaintenanceWindowsNativeMethods.RevertToSelf();
            error = reverted ? 0 : Marshal.GetLastWin32Error();
            return reverted;
        }
    }

    internal sealed class
        MaintenanceNamedPipeClientImpersonationAdapter
        : IMaintenanceClientImpersonationRunner
    {
        private readonly object gate = new object();
        private readonly SafeHandle pipeHandle;
        private readonly IMaintenanceNamedPipeClientNative native;
        private bool active;

        internal MaintenanceNamedPipeClientImpersonationAdapter(
            SafeHandle pipeHandle)
            : this(
                pipeHandle,
                new MaintenanceNamedPipeClientNative())
        {
        }

        internal MaintenanceNamedPipeClientImpersonationAdapter(
            SafeHandle pipeHandle,
            IMaintenanceNamedPipeClientNative native)
        {
            if (pipeHandle == null ||
                pipeHandle.IsInvalid ||
                pipeHandle.IsClosed)
            {
                throw new ArgumentException(
                    "A valid connected pipe handle is required.",
                    "pipeHandle");
            }
            if (native == null)
            {
                throw new ArgumentNullException("native");
            }
            // The connected pipe handle is borrowed. Every native use is
            // scoped by DangerousAddRef/DangerousRelease; this adapter never
            // closes or retains raw-handle ownership.
            this.pipeHandle = pipeHandle;
            this.native = native;
        }

        public MaintenanceClientTokenEvidence CaptureScoped(
            IMaintenanceClientTokenCapture capture,
            IMaintenanceProcessTerminator terminator)
        {
            if (capture == null || terminator == null)
            {
                throw new ArgumentNullException(
                    "Scoped named-pipe capture composition is " +
                    "incomplete.");
            }
            lock (gate)
            {
                if (active)
                {
                    throw new InvalidOperationException(
                        "Named-pipe client scoped capture is already " +
                        "active.");
                }
                active = true;
                bool pipeReference = false;
                bool armed = false;
                bool failStopAttempted = false;
                int ownerThreadId = 0;
                RuntimeHelpers.PrepareConstrainedRegions();
                try
                {
                    RuntimeHelpers.PrepareConstrainedRegions();
                    try
                    {
                    }
                    finally
                    {
                        pipeHandle.DangerousAddRef(
                            ref pipeReference);
                    }
                    IntPtr borrowedPipeHandle =
                        pipeHandle.DangerousGetHandle();
                    ownerThreadId = unchecked(
                        native.GetCurrentThreadId());

                    int impersonateError = 0;
                    MaintenanceClientTokenEvidence evidence = null;
                    Exception captureFailure = null;
                    RuntimeHelpers.PrepareConstrainedRegions();
                    try
                    {
                        native.AcquireClient(
                            borrowedPipeHandle,
                            ref armed,
                            out impersonateError);
                        if (!armed)
                        {
                            throw new UnauthorizedAccessException(
                                "Named-pipe client impersonation setup " +
                                "failed.",
                                new Win32Exception(
                                    impersonateError,
                                    "ImpersonateNamedPipeClient " +
                                    "failed."));
                        }

                        try
                        {
                            evidence = capture.Capture();
                            if (evidence == null)
                            {
                                captureFailure =
                                    new UnauthorizedAccessException(
                                    "Named-pipe client token capture " +
                                    "returned no evidence.");
                            }
                        }
                        catch (Exception failure)
                        {
                            captureFailure = failure;
                        }
                    }
                    finally
                    {
                        if (armed)
                        {
                            if (ownerThreadId != unchecked(
                                    native.GetCurrentThreadId()))
                            {
                                TerminateUnsafeImpersonation(
                                    terminator,
                                    ref failStopAttempted,
                                    "Named-pipe client impersonation " +
                                    "left its owner thread.",
                                    null);
                            }
                            int revertError;
                            bool reverted =
                                native.RevertToSelf(
                                    out revertError);
                            if (!reverted)
                            {
                                TerminateUnsafeImpersonation(
                                    terminator,
                                    ref failStopAttempted,
                                    "Named-pipe client impersonation " +
                                    "revert failed.",
                                    new Win32Exception(revertError));
                            }
                            armed = false;
                            ownerThreadId = 0;
                        }
                    }
                    if (captureFailure != null)
                    {
                        throw new UnauthorizedAccessException(
                            "Named-pipe client token capture failed.",
                            captureFailure);
                    }
                    return evidence;
                }
                finally
                {
                    try
                    {
                        if (armed && !failStopAttempted)
                        {
                            TerminateUnsafeImpersonation(
                                terminator,
                                ref failStopAttempted,
                                "Named-pipe client impersonation " +
                                "escaped its scoped revert.",
                                null);
                        }
                    }
                    finally
                    {
                        if (pipeReference)
                        {
                            pipeHandle.DangerousRelease();
                        }
                        if (!armed)
                        {
                            active = false;
                        }
                    }
                }
            }
        }

        private static void TerminateUnsafeImpersonation(
            IMaintenanceProcessTerminator terminator,
            ref bool failStopAttempted,
            string reason,
            Exception cause)
        {
            failStopAttempted = true;
            Exception failStopCause = cause;
            try
            {
                terminator.Terminate(reason);
            }
            catch (Exception terminatorFailure)
            {
                failStopCause = terminatorFailure;
            }
            Environment.FailFast(reason, failStopCause);
            throw new InvalidOperationException(
                "Environment.FailFast returned while native client " +
                "impersonation remained armed.",
                failStopCause);
        }
    }

    [SecurityPermission(
        SecurityAction.InheritanceDemand,
        UnmanagedCode = true)]
    internal sealed class SafeHGlobalBuffer
        : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeHGlobalBuffer(int length)
            : base(true)
        {
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException("length");
            }
            SetHandle(Marshal.AllocHGlobal(length));
            Length = length;
        }

        internal int Length { get; private set; }

        internal void SetLength(int length)
        {
            if (length <= 0 || length > Length)
            {
                throw new ArgumentOutOfRangeException("length");
            }
            Length = length;
        }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    internal static class MaintenanceWindowsNativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentThread();

        [DllImport(
            "advapi32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenThreadToken(
            IntPtr thread,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool openAsSelf,
            out MaintenanceSafeTokenHandle token);

        [DllImport(
            "advapi32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            MaintenanceSafeTokenHandle token,
            int informationClass,
            IntPtr buffer,
            int bufferLength,
            out int returnLength);

        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsValidSid(IntPtr sid);

        [DllImport("advapi32.dll")]
        internal static extern int GetLengthSid(IntPtr sid);

        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsTokenRestricted(
            MaintenanceSafeTokenHandle token);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport(
            "advapi32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ImpersonateNamedPipeClient(
            IntPtr pipeHandle);

        [DllImport(
            "advapi32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RevertToSelf();

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetCurrentThreadId")]
        internal static extern uint GetCurrentThreadIdValue();
    }
}
