using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SBMSSetup
{
    internal interface IWindowsJournalIoTestSeam
    {
        void AfterTrustedInstallerRootOpened(string expectedPath);
        void AfterBackupPublished();
        void BeforePublishedFileIdVerification(string destinationRelativePath);
        int BeforeNativeIo(string operation, int attempt);
    }

    internal sealed class WindowsJournalSecurityProfile
    {
        internal SecurityIdentifier Owner;
        internal SecurityIdentifier[] FullControlIdentities;

        internal static WindowsJournalSecurityProfile Production()
        {
            return new WindowsJournalSecurityProfile
            {
                Owner = new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    null),
                FullControlIdentities = new[]
                {
                    new SecurityIdentifier(
                        WellKnownSidType.BuiltinAdministratorsSid,
                        null),
                    new SecurityIdentifier(
                        WellKnownSidType.LocalSystemSid,
                        null)
                }
            };
        }
    }

    // Every journal leaf operation is rooted at a no-follow installer directory
    // handle. No path-based File/Directory mutation occurs below that handle.
    internal sealed class WindowsHandleRelativeJournalFileSystem
        : IAtomicJournalFileSystem,
          IJournalStorageAuthorityDescriptor,
          IDisposable
    {
        private sealed class JournalRenameCommittedException : IOException
        {
            internal JournalRenameCommittedException(Exception innerException)
                : base(
                    "Journal leaf rename committed but verification failed.",
                    innerException)
            {
            }
        }

        private const uint FileReadData = 0x0001;
        private const uint FileWriteData = 0x0002;
        private const uint FileAppendData = 0x0004;
        private const uint FileTraverse = 0x0020;
        private const uint FileReadAttributes = 0x0080;
        private const uint FileWriteAttributes = 0x0100;
        private const uint DeleteAccess = 0x00010000;
        private const uint ReadControl = 0x00020000;
        private const uint WriteDac = 0x00040000;
        private const uint WriteOwner = 0x00080000;
        private const uint Synchronize = 0x00100000;

        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint ShareDelete = 0x00000004;

        private const uint FileOpen = 0x00000001;
        private const uint FileCreate = 0x00000002;
        private const uint FileOpenIf = 0x00000003;
        private const uint FileDirectoryFile = 0x00000001;
        private const uint FileWriteThrough = 0x00000002;
        private const uint FileSynchronousIoNonAlert = 0x00000020;
        private const uint FileNonDirectoryFile = 0x00000040;
        private const uint FileOpenReparsePoint = 0x00200000;

        private const uint ObjCaseInsensitive = 0x00000040;
        private const uint ObjDontReparse = 0x00001000;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const int StatusNoSuchFile = unchecked((int)0xC000000F);
        private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
        private const int StatusNoMoreFiles = unchecked((int)0x80000006);
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorSharingViolation = 32;
        private const int ErrorLockViolation = 33;
        private const int NativeRetryDeadlineMilliseconds = 2000;
        private const int NativeRetryDelayMilliseconds = 25;

        private readonly string commonRootPath;
        private readonly string installerRootPath;
        private readonly WindowsJournalSecurityProfile securityProfile;
        private readonly IWindowsJournalIoTestSeam testSeam;
        private SafeFileHandle installerRootHandle;
        private bool journalNamespaceExpected;

        internal WindowsHandleRelativeJournalFileSystem(
            string commonRootPath,
            WindowsJournalSecurityProfile securityProfile,
            IWindowsJournalIoTestSeam testSeam)
        {
            if (String.IsNullOrWhiteSpace(commonRootPath) ||
                !Path.IsPathRooted(commonRootPath))
            {
                throw new ArgumentException(
                    "Common application data root must be absolute.",
                    "commonRootPath");
            }
            if (securityProfile == null ||
                securityProfile.Owner == null ||
                securityProfile.FullControlIdentities == null ||
                securityProfile.FullControlIdentities.Length == 0)
            {
                throw new ArgumentException(
                    "A complete journal security profile is required.",
                    "securityProfile");
            }
            this.commonRootPath = TrimTrailingSeparator(
                Path.GetFullPath(commonRootPath));
            installerRootPath = Path.Combine(
                this.commonRootPath,
                "SBMS",
                "Installer");
            this.securityProfile = securityProfile;
            this.testSeam = testSeam;
        }

        public string StorageAuthorityInvariantDigest
        {
            get
            {
                var fields = new System.Collections.Generic.List<string>
                {
                    "WindowsHandleRelativeJournalFileSystem",
                    commonRootPath,
                    installerRootPath,
                    securityProfile.Owner.Value
                };
                foreach (SecurityIdentifier identity in
                    securityProfile.FullControlIdentities)
                {
                    fields.Add(identity.Value);
                }
                return PayloadContractValidation.ComputeDigest(
                    "SBMS.Journal.StorageAuthority.v1",
                    fields);
            }
        }

        public string GetDisplayPath(string relativePath)
        {
            return ResolveDisplayPath(relativePath);
        }

        public bool FileExists(string relativePath)
        {
            SafeFileHandle parent = null;
            SafeFileHandle leaf = null;
            try
            {
                parent = OpenParent(relativePath, false);
                if (parent == null)
                {
                    return false;
                }
                string leafName = GetLeafName(relativePath);
                leaf = TryOpenLeaf(
                    parent,
                    leafName,
                    FileReadData | FileReadAttributes | ReadControl |
                        Synchronize,
                    FileOpen);
                return leaf != null;
            }
            finally
            {
                if (leaf != null)
                {
                    leaf.Dispose();
                }
                DisposeIfNotInstallerRoot(parent);
            }
        }

        public void EnsureDirectory(string relativePath)
        {
            EnsureTrustedInstallerRoot(true);
            if (String.IsNullOrEmpty(relativePath))
            {
                return;
            }
            using (SafeFileHandle directory =
                OpenRelativeDirectoryPath(
                    installerRootHandle,
                    relativePath,
                    true,
                    false))
            {
                VerifyDirectoryHandle(directory, ResolveDisplayPath(relativePath));
            }
        }

        public Stream CreateNewFile(string relativePath)
        {
            SafeFileHandle parent = OpenParent(relativePath, true);
            try
            {
                RejectNamedReparse(parent, GetLeafName(relativePath));
                SafeFileHandle leaf = OpenRelative(
                    parent,
                    GetLeafName(relativePath),
                    FileWriteData | FileAppendData | FileReadAttributes |
                        FileWriteAttributes | ReadControl | DeleteAccess |
                        Synchronize,
                    FileCreate,
                    FileNonDirectoryFile | FileWriteThrough |
                        FileSynchronousIoNonAlert | FileOpenReparsePoint,
                    null);
                try
                {
                    VerifyLeafHandle(leaf, true);
                    return new FileStream(
                        leaf,
                        FileAccess.Write,
                        4096,
                        false);
                }
                catch
                {
                    leaf.Dispose();
                    throw;
                }
            }
            finally
            {
                DisposeIfNotInstallerRoot(parent);
            }
        }

        public Stream OpenReadFile(string relativePath)
        {
            SafeFileHandle parent = OpenParent(relativePath, false);
            if (parent == null)
            {
                throw new FileNotFoundException(
                    "Journal file was not found.",
                    ResolveDisplayPath(relativePath));
            }
            try
            {
                RejectNamedReparse(parent, GetLeafName(relativePath));
                SafeFileHandle leaf = OpenRelative(
                    parent,
                    GetLeafName(relativePath),
                    FileReadData | FileReadAttributes | ReadControl |
                        Synchronize,
                    FileOpen,
                    FileNonDirectoryFile | FileSynchronousIoNonAlert |
                        FileOpenReparsePoint,
                    null);
                try
                {
                    VerifyLeafHandle(leaf, false);
                    return new FileStream(
                        leaf,
                        FileAccess.Read,
                        4096,
                        false);
                }
                catch
                {
                    leaf.Dispose();
                    throw;
                }
            }
            finally
            {
                DisposeIfNotInstallerRoot(parent);
            }
        }

        public void PublishNewFile(
            string sourceRelativePath,
            string destinationRelativePath)
        {
            try
            {
                RenameAndVerify(
                    sourceRelativePath,
                    destinationRelativePath,
                    false);
            }
            catch (JournalRenameCommittedException failure)
            {
                throw new JournalFilePublicationException(true, failure);
            }
        }

        public void ReplaceFile(
            string sourceRelativePath,
            string destinationRelativePath,
            string backupRelativePath)
        {
            if (FileExists(backupRelativePath))
            {
                DeleteFile(backupRelativePath);
            }
            if (FileExists(destinationRelativePath))
            {
                try
                {
                    RenameAndVerify(
                        destinationRelativePath,
                        backupRelativePath,
                        false);
                }
                catch (JournalRenameCommittedException failure)
                {
                    throw new JournalFilePublicationException(false, failure);
                }
                if (testSeam != null)
                {
                    testSeam.AfterBackupPublished();
                }
            }
            try
            {
                RenameAndVerify(
                    sourceRelativePath,
                    destinationRelativePath,
                    false);
            }
            catch (JournalRenameCommittedException failure)
            {
                throw new JournalFilePublicationException(true, failure);
            }
        }

        public void DeleteFile(string relativePath)
        {
            SafeFileHandle parent = OpenParent(relativePath, false);
            if (parent == null)
            {
                return;
            }
            using (parent == installerRootHandle ? null : parent)
            using (SafeFileHandle leaf = TryOpenLeaf(
                parent,
                GetLeafName(relativePath),
                DeleteAccess | FileReadAttributes | ReadControl | Synchronize,
                FileOpen))
            {
                if (leaf == null)
                {
                    return;
                }
                VerifyLeafHandle(leaf, false);
                var disposition = new FileDispositionInfo
                {
                    DeleteFile = true
                };
                int attempt = 0;
                Stopwatch deadline = Stopwatch.StartNew();
                while (true)
                {
                    ++attempt;
                    int injected = GetInjectedNativeError("delete", attempt);
                    bool deleted = injected == 0 &&
                        SetFileInformationByHandle(
                            leaf,
                            4,
                            ref disposition,
                            Marshal.SizeOf(typeof(FileDispositionInfo)));
                    if (deleted)
                    {
                        break;
                    }
                    int error = injected != 0
                        ? injected
                        : Marshal.GetLastWin32Error();
                    if (!ShouldRetryNativeIo(error, deadline))
                    {
                        throw new Win32Exception(
                            error,
                            "Unable to delete journal leaf.");
                    }
                    Thread.Sleep(NativeRetryDelayMilliseconds);
                }
            }
        }

        public void Dispose()
        {
            SafeFileHandle handle = installerRootHandle;
            installerRootHandle = null;
            if (handle != null)
            {
                handle.Dispose();
            }
        }

        internal void PrepareAndVerify(bool createIfMissing)
        {
            EnsureTrustedInstallerRoot(createIfMissing);
            if (installerRootHandle != null)
            {
                VerifyRestrictedAcl(installerRootHandle);
            }
        }

        private void EnsureTrustedInstallerRoot(bool createIfMissing)
        {
            if (installerRootHandle != null &&
                !installerRootHandle.IsInvalid &&
                !installerRootHandle.IsClosed)
            {
                return;
            }

            if (createIfMissing)
            {
                journalNamespaceExpected = true;
            }

            SafeFileHandle common = OpenAbsoluteDirectoryNoFollow(commonRootPath);
            try
            {
                VerifyDirectoryHandle(common, commonRootPath);
                using (SafeFileHandle sbms = OpenOrCreateSecureDirectory(
                    common,
                    "SBMS",
                    Path.Combine(commonRootPath, "SBMS"),
                    createIfMissing))
                {
                    if (sbms == null)
                    {
                        if (journalNamespaceExpected)
                        {
                            throw new InvalidDataException(
                                "Expected SBMS journal namespace is missing.");
                        }
                        return;
                    }
                    journalNamespaceExpected = true;
                    SafeFileHandle installer = OpenOrCreateSecureDirectory(
                        sbms,
                        "Installer",
                        installerRootPath,
                        createIfMissing);
                    if (installer == null)
                    {
                        throw new InvalidDataException(
                            "Expected SBMS installer journal directory is missing.");
                    }
                    SafeFileHandle anchor = null;
                    try
                    {
                        VerifyRestrictedAcl(installer);
                        byte[] installerId = GetFileId(installer);
                        anchor = OpenRelative(
                            sbms,
                            "Installer",
                            FileReadData | FileTraverse |
                                FileReadAttributes | ReadControl | Synchronize,
                            FileOpen,
                            FileDirectoryFile |
                                FileSynchronousIoNonAlert |
                                FileOpenReparsePoint,
                            null);
                        VerifyDirectoryHandle(anchor, installerRootPath);
                        VerifyRestrictedAcl(anchor);
                        if (!BytesEqual(installerId, GetFileId(anchor)))
                        {
                            throw new InvalidDataException(
                                "Installer journal anchor changed while reopening.");
                        }
                        installerRootHandle = anchor;
                        anchor = null;
                        installer.Dispose();
                        installer = null;
                        if (testSeam != null)
                        {
                            testSeam.AfterTrustedInstallerRootOpened(
                                installerRootPath);
                        }
                    }
                    finally
                    {
                        if (anchor != null)
                        {
                            anchor.Dispose();
                        }
                        if (installer != null)
                        {
                            installer.Dispose();
                        }
                    }
                }
            }
            finally
            {
                common.Dispose();
            }
        }

        private SafeFileHandle OpenOrCreateSecureDirectory(
            SafeFileHandle root,
            string name,
            string expectedPath,
            bool createIfMissing)
        {
            RejectNamedReparse(root, name);
            byte[] descriptor = BuildDirectorySecurityDescriptor();
            SafeFileHandle directory = TryOpenRelativeDirectory(
                root,
                name,
                FileOpen,
                null);
            if (directory == null && createIfMissing)
            {
                directory = OpenRelative(
                    root,
                    name,
                    FileReadData | FileReadAttributes | ReadControl | WriteDac |
                        WriteOwner | DeleteAccess | Synchronize,
                    FileOpenIf,
                    FileDirectoryFile | FileSynchronousIoNonAlert |
                        FileOpenReparsePoint,
                    descriptor);
            }
            if (directory == null)
            {
                return null;
            }
            try
            {
                VerifyDirectoryHandle(directory, expectedPath);
                VerifyRestrictedAcl(directory);
                return directory;
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        }

        private SafeFileHandle OpenParent(
            string relativePath,
            bool createDirectories)
        {
            ValidateRelativePath(relativePath, false);
            EnsureTrustedInstallerRoot(createDirectories);
            if (installerRootHandle == null)
            {
                return null;
            }
            string parent = Path.GetDirectoryName(relativePath);
            if (String.IsNullOrEmpty(parent))
            {
                return installerRootHandle;
            }
            return OpenRelativeDirectoryPath(
                installerRootHandle,
                parent,
                createDirectories,
                false);
        }

        private SafeFileHandle OpenRelativeDirectoryPath(
            SafeFileHandle initialRoot,
            string relativePath,
            bool create,
            bool disposeInitial)
        {
            ValidateRelativePath(relativePath, true);
            SafeFileHandle current = initialRoot;
            bool ownsCurrent = disposeInitial;
            try
            {
                string[] segments = relativePath.Split('\\');
                string display = installerRootPath;
                foreach (string segment in segments)
                {
                    display = Path.Combine(display, segment);
                    SafeFileHandle next = OpenOrCreateSecureDirectory(
                        current,
                        segment,
                        display,
                        create);
                    if (next == null)
                    {
                        if (ownsCurrent)
                        {
                            current.Dispose();
                        }
                        return null;
                    }
                    if (ownsCurrent)
                    {
                        current.Dispose();
                    }
                    current = next;
                    ownsCurrent = true;
                }
                return current;
            }
            catch
            {
                if (ownsCurrent)
                {
                    current.Dispose();
                }
                throw;
            }
        }

        private void RenameAndVerify(
            string sourceRelativePath,
            string destinationRelativePath,
            bool replace)
        {
            ValidateSameParent(sourceRelativePath, destinationRelativePath);
            SafeFileHandle parent = OpenParent(sourceRelativePath, false);
            if (parent == null)
            {
                throw new FileNotFoundException(
                    "Journal rename source parent was not found.");
            }
            bool nameCommitted = false;
            try
            {
                RejectNamedReparse(
                    parent,
                    GetLeafName(sourceRelativePath));
                byte[] expectedId;
                using (SafeFileHandle source = OpenRelative(
                    parent,
                    GetLeafName(sourceRelativePath),
                    DeleteAccess | FileReadAttributes | ReadControl |
                        Synchronize,
                    FileOpen,
                    FileNonDirectoryFile | FileSynchronousIoNonAlert |
                        FileOpenReparsePoint,
                    null))
                {
                    VerifyLeafHandle(source, false);
                    expectedId = GetFileId(source);
                    RenameRelative(
                        source,
                        GetLeafName(destinationRelativePath),
                        replace);
                    nameCommitted = true;
                }
                if (testSeam != null)
                {
                    testSeam.BeforePublishedFileIdVerification(
                        destinationRelativePath);
                }
                RejectNamedReparse(
                    parent,
                    GetLeafName(destinationRelativePath));
                using (SafeFileHandle published = OpenRelative(
                    parent,
                    GetLeafName(destinationRelativePath),
                    FileReadAttributes | ReadControl | Synchronize,
                    FileOpen,
                    FileNonDirectoryFile | FileSynchronousIoNonAlert |
                        FileOpenReparsePoint,
                    null))
                {
                    VerifyLeafHandle(published, false);
                    if (!BytesEqual(expectedId, GetFileId(published)))
                    {
                        throw new IOException(
                            "Published journal FileId does not match candidate.");
                    }
                }
            }
            catch (Exception failure)
            {
                if (nameCommitted)
                {
                    throw new JournalRenameCommittedException(failure);
                }
                throw;
            }
            finally
            {
                DisposeIfNotInstallerRoot(parent);
            }
        }

        private static SafeFileHandle OpenAbsoluteDirectoryNoFollow(string path)
        {
            SafeFileHandle handle = CreateFile(
                path,
                FileReadData | FileReadAttributes | ReadControl | Synchronize,
                ShareRead | ShareWrite | ShareDelete,
                IntPtr.Zero,
                3,
                0x02200000,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                ThrowLastWin32("Unable to open common application data root.");
            }
            return handle;
        }

        private SafeFileHandle TryOpenRelativeDirectory(
            SafeFileHandle root,
            string name,
            uint disposition,
            byte[] securityDescriptor)
        {
            try
            {
                return OpenRelative(
                    root,
                    name,
                    FileReadData | FileReadAttributes | ReadControl | WriteDac |
                        WriteOwner | DeleteAccess | Synchronize,
                    disposition,
                    FileSynchronousIoNonAlert |
                        FileOpenReparsePoint,
                    securityDescriptor);
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == ErrorFileNotFound ||
                    ex.NativeErrorCode == ErrorPathNotFound)
                {
                    return null;
                }
                throw;
            }
        }

        private SafeFileHandle TryOpenLeaf(
            SafeFileHandle root,
            string name,
            uint access,
            uint disposition)
        {
            RejectNamedReparse(root, name);
            SafeFileHandle handle = null;
            try
            {
                handle = OpenRelative(
                    root,
                    name,
                    access,
                    disposition,
                    FileNonDirectoryFile | FileSynchronousIoNonAlert |
                        FileOpenReparsePoint,
                    null);
                VerifyLeafHandle(handle, false);
                return handle;
            }
            catch (Win32Exception ex)
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
                if (ex.NativeErrorCode == ErrorFileNotFound ||
                    ex.NativeErrorCode == ErrorPathNotFound)
                {
                    return null;
                }
                throw;
            }
            catch
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
                throw;
            }
        }

        private SafeFileHandle OpenRelative(
            SafeFileHandle root,
            string name,
            uint access,
            uint disposition,
            uint options,
            byte[] securityDescriptor)
        {
            ValidateSimpleName(name);
            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr unicodeBuffer = IntPtr.Zero;
            GCHandle descriptorPin = new GCHandle();
            bool descriptorPinned = false;
            try
            {
                nameBuffer = Marshal.StringToHGlobalUni(name);
                var unicode = new UnicodeString
                {
                    Length = checked((ushort)(name.Length * 2)),
                    MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                    Buffer = nameBuffer
                };
                unicodeBuffer = Marshal.AllocHGlobal(
                    Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(unicode, unicodeBuffer, false);
                IntPtr descriptor = IntPtr.Zero;
                if (securityDescriptor != null)
                {
                    descriptorPin = GCHandle.Alloc(
                        securityDescriptor,
                        GCHandleType.Pinned);
                    descriptorPinned = true;
                    descriptor = descriptorPin.AddrOfPinnedObject();
                }
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf(typeof(ObjectAttributes)),
                    RootDirectory = root.DangerousGetHandle(),
                    ObjectName = unicodeBuffer,
                    Attributes = ObjCaseInsensitive | ObjDontReparse,
                    SecurityDescriptor = descriptor,
                    SecurityQualityOfService = IntPtr.Zero
                };
                int attempt = 0;
                Stopwatch deadline = Stopwatch.StartNew();
                while (true)
                {
                    ++attempt;
                    IoStatusBlock ioStatus;
                    SafeFileHandle handle = null;
                    int injected = GetInjectedNativeError("open", attempt);
                    int status = injected == 0
                        ? NtCreateFile(
                            out handle,
                            access,
                            ref attributes,
                            out ioStatus,
                            IntPtr.Zero,
                            0x00000080,
                            ShareRead | ShareWrite | ShareDelete,
                            disposition,
                            options,
                            IntPtr.Zero,
                            0)
                        : -1;
                    if (status >= 0)
                    {
                        return handle;
                    }
                    if (handle != null)
                    {
                        handle.Dispose();
                    }
                    int error = injected != 0
                        ? injected
                        : unchecked((int)RtlNtStatusToDosError(status));
                    if (!ShouldRetryNativeIo(error, deadline))
                    {
                        throw new Win32Exception(error);
                    }
                    Thread.Sleep(NativeRetryDelayMilliseconds);
                }
            }
            finally
            {
                if (descriptorPinned)
                {
                    descriptorPin.Free();
                }
                if (unicodeBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(unicodeBuffer);
                }
                if (nameBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nameBuffer);
                }
            }
        }

        private static void RejectNamedReparse(
            SafeFileHandle parent,
            string name)
        {
            ValidateSimpleName(name);
            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr output = IntPtr.Zero;
            try
            {
                nameBuffer = Marshal.StringToHGlobalUni(name);
                var unicode = new UnicodeString
                {
                    Length = checked((ushort)(name.Length * 2)),
                    MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                    Buffer = nameBuffer
                };
                output = Marshal.AllocHGlobal(1024);
                IoStatusBlock ioStatus;
                int status = NtQueryDirectoryFile(
                    parent,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out ioStatus,
                    output,
                    1024,
                    1,
                    true,
                    ref unicode,
                    true);
                if (status == StatusNoSuchFile ||
                    status == StatusObjectNameNotFound ||
                    status == StatusNoMoreFiles)
                {
                    return;
                }
                if (status < 0)
                {
                    throw new Win32Exception(
                        unchecked((int)RtlNtStatusToDosError(status)));
                }
                uint attributes = unchecked((uint)Marshal.ReadInt32(
                    output,
                    56));
                int returnedNameLength = Marshal.ReadInt32(output, 60);
                string returnedName = Marshal.PtrToStringUni(
                    IntPtr.Add(output, 64),
                    returnedNameLength / 2);
                if (!String.Equals(
                    returnedName,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Journal directory query returned an unexpected entry.");
                }
                if ((attributes & FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Installer journal path component is a reparse point.");
                }
            }
            finally
            {
                if (output != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(output);
                }
                if (nameBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nameBuffer);
                }
            }
        }

        private byte[] BuildDirectorySecurityDescriptor()
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(securityProfile.Owner);
            foreach (SecurityIdentifier identity in
                securityProfile.FullControlIdentities)
            {
                security.AddAccessRule(
                    new FileSystemAccessRule(
                        identity,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit |
                            InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
            }
            return security.GetSecurityDescriptorBinaryForm();
        }

        private void VerifyRestrictedAcl(SafeFileHandle handle)
        {
            IntPtr owner;
            IntPtr group;
            IntPtr dacl;
            IntPtr sacl;
            IntPtr descriptor;
            uint error = GetSecurityInfo(
                handle,
                1,
                0x00000001 | 0x00000004,
                out owner,
                out group,
                out dacl,
                out sacl,
                out descriptor);
            if (error != 0)
            {
                throw new Win32Exception(unchecked((int)error));
            }
            try
            {
                int length = unchecked((int)GetSecurityDescriptorLength(
                    descriptor));
                var bytes = new byte[length];
                Marshal.Copy(descriptor, bytes, 0, length);
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorBinaryForm(
                    bytes,
                    AccessControlSections.Access |
                        AccessControlSections.Owner);
                if (!security.AreAccessRulesProtected)
                {
                    throw new UnauthorizedAccessException(
                        "Installer state ACL inheritance is not protected.");
                }
                var actualOwner = security.GetOwner(
                    typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (actualOwner == null ||
                    !actualOwner.Equals(securityProfile.Owner))
                {
                    throw new UnauthorizedAccessException(
                        "Installer state root has an untrusted owner.");
                }
                AuthorizationRuleCollection rules = security.GetAccessRules(
                    true,
                    true,
                    typeof(SecurityIdentifier));
                if (rules.Count != securityProfile.FullControlIdentities.Length)
                {
                    throw new UnauthorizedAccessException(
                        "Installer state root has unexpected ACL entries.");
                }
                foreach (SecurityIdentifier required in
                    securityProfile.FullControlIdentities)
                {
                    bool found = false;
                    foreach (AuthorizationRule authorizationRule in rules)
                    {
                        var rule = authorizationRule as FileSystemAccessRule;
                        var identity = rule == null
                            ? null
                            : rule.IdentityReference as SecurityIdentifier;
                        if (identity != null && identity.Equals(required))
                        {
                            if (found ||
                                rule.AccessControlType != AccessControlType.Allow ||
                                rule.FileSystemRights != FileSystemRights.FullControl ||
                                rule.InheritanceFlags !=
                                    (InheritanceFlags.ContainerInherit |
                                     InheritanceFlags.ObjectInherit) ||
                                rule.PropagationFlags != PropagationFlags.None ||
                                rule.IsInherited)
                            {
                                throw new UnauthorizedAccessException(
                                    "Installer state root has an invalid ACL entry.");
                            }
                            found = true;
                        }
                    }
                    if (!found)
                    {
                        throw new UnauthorizedAccessException(
                            "Installer state root is missing a required ACL.");
                    }
                }
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        private static void VerifyDirectoryHandle(
            SafeFileHandle handle,
            string expectedPath)
        {
            FileAttributeTagInfo attributes;
            if (!GetFileInformationByHandleEx(
                handle,
                9,
                out attributes,
                Marshal.SizeOf(typeof(FileAttributeTagInfo))))
            {
                ThrowLastWin32("Unable to inspect journal directory handle.");
            }
            if ((attributes.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Installer journal directory is a reparse point.");
            }
            FileStandardInfo standard;
            if (!GetFileInformationByHandleEx(
                handle,
                1,
                out standard,
                Marshal.SizeOf(typeof(FileStandardInfo))) ||
                !standard.Directory)
            {
                throw new InvalidDataException(
                    "Installer journal path component is not a directory.");
            }
            string actualPath = GetFinalPath(handle);
            if (!String.Equals(
                NormalizeFinalPath(expectedPath),
                NormalizeFinalPath(actualPath),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Installer journal directory handle resolved unexpectedly.");
            }
        }

        private static void VerifyLeafHandle(
            SafeFileHandle handle,
            bool newlyCreated)
        {
            FileAttributeTagInfo attributes;
            if (!GetFileInformationByHandleEx(
                handle,
                9,
                out attributes,
                Marshal.SizeOf(typeof(FileAttributeTagInfo))))
            {
                ThrowLastWin32("Unable to inspect journal leaf attributes.");
            }
            if ((attributes.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Installer journal leaves must not be reparse points.");
            }
            FileStandardInfo standard;
            if (!GetFileInformationByHandleEx(
                handle,
                1,
                out standard,
                Marshal.SizeOf(typeof(FileStandardInfo))))
            {
                ThrowLastWin32("Unable to inspect journal leaf link count.");
            }
            VerifySingleLinkLeaf(
                standard.Directory,
                standard.NumberOfLinks);
        }

        internal static void VerifySingleLinkLeaf(
            bool isDirectory,
            uint numberOfLinks)
        {
            if (isDirectory || numberOfLinks != 1)
            {
                throw new InvalidDataException(
                    "Installer journal leaves must be single-link regular files.");
            }
        }

        private static byte[] GetFileId(SafeFileHandle handle)
        {
            FileIdInfo info;
            if (!GetFileInformationByHandleEx(
                handle,
                18,
                out info,
                Marshal.SizeOf(typeof(FileIdInfo))))
            {
                ThrowLastWin32("Unable to read journal FileId.");
            }
            var result = new byte[24];
            Buffer.BlockCopy(
                BitConverter.GetBytes(info.VolumeSerialNumber),
                0,
                result,
                0,
                8);
            Buffer.BlockCopy(info.FileId.Identifier, 0, result, 8, 16);
            return result;
        }

        private void RenameRelative(
            SafeFileHandle source,
            string destinationName,
            bool replace)
        {
            ValidateSimpleName(destinationName);
            byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(
                destinationName);
            int rootOffset = IntPtr.Size == 8 ? 8 : 4;
            int lengthOffset = rootOffset + IntPtr.Size;
            int nameOffset = lengthOffset + 4;
            IntPtr buffer = Marshal.AllocHGlobal(nameOffset + nameBytes.Length);
            try
            {
                for (int index = 0; index < nameOffset + nameBytes.Length; ++index)
                {
                    Marshal.WriteByte(buffer, index, 0);
                }
                Marshal.WriteInt32(buffer, 0, replace ? 1 : 0);
                // ValidateSameParent has already constrained this operation to
                // the source directory. A simple name with RootDirectory=NULL
                // avoids the I/O manager reopening an already anchored target
                // directory and creating a self-induced sharing conflict.
                Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
                Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
                Marshal.Copy(
                    nameBytes,
                    0,
                    IntPtr.Add(buffer, nameOffset),
                    nameBytes.Length);
                int attempt = 0;
                Stopwatch deadline = Stopwatch.StartNew();
                while (true)
                {
                    ++attempt;
                    IoStatusBlock ioStatus;
                    int injected = GetInjectedNativeError("rename", attempt);
                    int status = injected == 0
                        ? NtSetInformationFile(
                            source,
                            out ioStatus,
                            buffer,
                            unchecked((uint)(nameOffset + nameBytes.Length)),
                            10)
                        : -1;
                    if (status >= 0)
                    {
                        break;
                    }
                    int error = injected != 0
                        ? injected
                        : unchecked((int)RtlNtStatusToDosError(status));
                    if (!ShouldRetryNativeIo(error, deadline))
                    {
                        throw new IOException(
                            "Unable to publish journal leaf.",
                            new Win32Exception(error));
                    }
                    Thread.Sleep(NativeRetryDelayMilliseconds);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private int GetInjectedNativeError(string operation, int attempt)
        {
            return testSeam == null
                ? 0
                : testSeam.BeforeNativeIo(operation, attempt);
        }

        private static bool ShouldRetryNativeIo(
            int error,
            Stopwatch deadline)
        {
            return (error == ErrorSharingViolation ||
                    error == ErrorLockViolation) &&
                deadline.ElapsedMilliseconds <
                    NativeRetryDeadlineMilliseconds;
        }

        private string ResolveDisplayPath(string relativePath)
        {
            if (String.IsNullOrEmpty(relativePath))
            {
                return installerRootPath;
            }
            ValidateRelativePath(relativePath, true);
            return Path.Combine(installerRootPath, relativePath);
        }

        private static string GetLeafName(string relativePath)
        {
            string leaf = Path.GetFileName(relativePath);
            ValidateSimpleName(leaf);
            return leaf;
        }

        private static void ValidateSameParent(string first, string second)
        {
            ValidateRelativePath(first, false);
            ValidateRelativePath(second, false);
            if (!String.Equals(
                Path.GetDirectoryName(first) ?? String.Empty,
                Path.GetDirectoryName(second) ?? String.Empty,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Atomic journal rename must remain in one trusted directory.");
            }
        }

        private static void ValidateRelativePath(
            string relativePath,
            bool allowDirectory)
        {
            if (relativePath == null ||
                Path.IsPathRooted(relativePath) ||
                relativePath.IndexOf('/') >= 0)
            {
                throw new InvalidDataException(
                    "Journal path must be canonical and relative.");
            }
            if (relativePath.Length == 0)
            {
                if (allowDirectory)
                {
                    return;
                }
                throw new InvalidDataException("Journal leaf path is empty.");
            }
            string[] segments = relativePath.Split('\\');
            foreach (string segment in segments)
            {
                ValidateSimpleName(segment);
            }
            string canonical = String.Join("\\", segments);
            if (!String.Equals(
                canonical,
                relativePath,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Journal relative path is not canonical.");
            }
        }

        private static void ValidateSimpleName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) ||
                name == "." ||
                name == ".." ||
                name.IndexOf('\\') >= 0 ||
                name.IndexOf('/') >= 0 ||
                name.IndexOf(':') >= 0 ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException(
                    "Journal path contains an unsafe component.");
            }
        }

        private static string GetFinalPath(SafeFileHandle handle)
        {
            var builder = new System.Text.StringBuilder(512);
            uint length = GetFinalPathNameByHandle(
                handle,
                builder,
                builder.Capacity,
                0);
            if (length == 0)
            {
                ThrowLastWin32("Unable to resolve journal directory handle.");
            }
            if (length >= builder.Capacity)
            {
                builder.Capacity = checked((int)length + 1);
                length = GetFinalPathNameByHandle(
                    handle,
                    builder,
                    builder.Capacity,
                    0);
                if (length == 0 || length >= builder.Capacity)
                {
                    ThrowLastWin32(
                        "Unable to resolve complete journal directory path.");
                }
            }
            return builder.ToString();
        }

        private static string NormalizeFinalPath(string path)
        {
            const string extended = @"\\?\";
            if (path.StartsWith(extended, StringComparison.Ordinal))
            {
                path = path.Substring(extended.Length);
            }
            return TrimTrailingSeparator(Path.GetFullPath(path));
        }

        private static string TrimTrailingSeparator(string path)
        {
            string root = Path.GetPathRoot(path);
            while (path.Length > root.Length &&
                (path[path.Length - 1] == '\\' || path[path.Length - 1] == '/'))
            {
                path = path.Substring(0, path.Length - 1);
            }
            return path;
        }

        private void DisposeIfNotInstallerRoot(SafeFileHandle handle)
        {
            if (handle != null &&
                !Object.ReferenceEquals(handle, installerRootHandle))
            {
                handle.Dispose();
            }
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (first == null || second == null ||
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

        private static void ThrowLastWin32(string message)
        {
            throw new IOException(
                message,
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            internal ushort Length;
            internal ushort MaximumLength;
            internal IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            internal int Length;
            internal IntPtr RootDirectory;
            internal IntPtr ObjectName;
            internal uint Attributes;
            internal IntPtr SecurityDescriptor;
            internal IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            internal IntPtr Status;
            internal IntPtr Information;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo
        {
            internal uint FileAttributes;
            internal uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileStandardInfo
        {
            internal long AllocationSize;
            internal long EndOfFile;
            internal uint NumberOfLinks;
            [MarshalAs(UnmanagedType.U1)]
            internal bool DeletePending;
            [MarshalAs(UnmanagedType.U1)]
            internal bool Directory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInfo
        {
            internal ulong VolumeSerialNumber;
            internal FileId128 FileId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileId128
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            internal byte[] Identifier;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo
        {
            [MarshalAs(UnmanagedType.Bool)]
            internal bool DeleteFile;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(
            out SafeFileHandle fileHandle,
            uint desiredAccess,
            ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock,
            IntPtr allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            IntPtr eaBuffer,
            uint eaLength);

        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);

        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryDirectoryFile(
            SafeFileHandle fileHandle,
            IntPtr eventHandle,
            IntPtr apcRoutine,
            IntPtr apcContext,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass,
            [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
            ref UnicodeString fileName,
            [MarshalAs(UnmanagedType.U1)] bool restartScan);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int informationClass,
            out FileAttributeTagInfo information,
            int bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int informationClass,
            out FileStandardInfo information,
            int bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int informationClass,
            out FileIdInfo information,
            int bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int informationClass,
            IntPtr information,
            int bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int informationClass,
            ref FileDispositionInfo information,
            int bufferSize);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            System.Text.StringBuilder path,
            int pathLength,
            uint flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint GetSecurityInfo(
            SafeFileHandle handle,
            int objectType,
            uint securityInformation,
            out IntPtr owner,
            out IntPtr group,
            out IntPtr dacl,
            out IntPtr sacl,
            out IntPtr securityDescriptor);

        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityDescriptorLength(
            IntPtr securityDescriptor);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
