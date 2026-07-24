using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SBMSSetup
{
    internal static class DriverCatalogVerifier
    {
        private static readonly Guid DriverActionVerify =
            new Guid("F750E6C3-38EE-11D1-85E5-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustCatalogInfo
        {
            internal uint cbStruct;
            internal uint dwCatalogVersion;
            [MarshalAs(UnmanagedType.LPWStr)] internal string pcwszCatalogFilePath;
            [MarshalAs(UnmanagedType.LPWStr)] internal string pcwszMemberTag;
            [MarshalAs(UnmanagedType.LPWStr)] internal string pcwszMemberFilePath;
            internal IntPtr hMemberFile;
            internal IntPtr pbCalculatedFileHash;
            internal uint cbCalculatedFileHash;
            internal IntPtr pcCatalogContext;
            internal IntPtr hCatAdmin;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            internal uint cbStruct;
            internal IntPtr pPolicyCallbackData;
            internal IntPtr pSIPClientData;
            internal uint dwUIChoice;
            internal uint fdwRevocationChecks;
            internal uint dwUnionChoice;
            internal IntPtr pInfoStruct;
            internal uint dwStateAction;
            internal IntPtr hWVTStateData;
            [MarshalAs(UnmanagedType.LPWStr)] internal string pwszURLReference;
            internal uint dwProvFlags;
            internal uint dwUIContext;
            internal IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATAdminAcquireContext2(
            out IntPtr phCatAdmin,
            ref Guid pgSubsystem,
            string pwszHashAlgorithm,
            IntPtr pStrongHashPolicy,
            uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATAdminCalcHashFromFileHandle2(
            IntPtr hCatAdmin,
            IntPtr hFile,
            ref uint pcbHash,
            [Out] byte[] pbHash,
            uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptCATAdminReleaseContext(
            IntPtr hCatAdmin,
            uint dwFlags);

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WinVerifyTrust(
            IntPtr hwnd,
            [In] ref Guid pgActionID,
            IntPtr pWVTData);

        internal static void VerifyPackageOrThrow(
            string catalogPath,
            params string[] memberPaths)
        {
            if (memberPaths == null || memberPaths.Length == 0)
            {
                throw new ArgumentException("Driver catalog verification requires payload members.", "memberPaths");
            }
            foreach (string memberPath in memberPaths)
            {
                VerifyMemberOrThrow(catalogPath, memberPath);
            }
        }

        private static void VerifyMemberOrThrow(string catalogPath, string memberPath)
        {
            string catalog = Path.GetFullPath(catalogPath);
            string member = Path.GetFullPath(memberPath);
            if (!File.Exists(catalog)) throw new FileNotFoundException("Driver catalog is missing.", catalog);
            if (!File.Exists(member)) throw new FileNotFoundException("Driver catalog member is missing.", member);

            IntPtr catAdmin = IntPtr.Zero;
            Guid action = DriverActionVerify;
            if (!CryptCATAdminAcquireContext2(
                    out catAdmin,
                    ref action,
                    "SHA256",
                    IntPtr.Zero,
                    0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to acquire the Windows driver catalog context.");
            }

            try
            {
                using (FileStream stream = new FileStream(
                    member,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    SafeFileHandle safeHandle = stream.SafeFileHandle;
                    uint hashSize = 0;
                    if (!CryptCATAdminCalcHashFromFileHandle2(
                            catAdmin,
                            safeHandle.DangerousGetHandle(),
                            ref hashSize,
                            null,
                            0) ||
                        hashSize == 0)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Unable to size the driver catalog member hash.");
                    }
                    byte[] hash = new byte[hashSize];
                    if (!CryptCATAdminCalcHashFromFileHandle2(
                            catAdmin,
                            safeHandle.DangerousGetHandle(),
                            ref hashSize,
                            hash,
                            0))
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Unable to calculate the driver catalog member hash.");
                    }
                    string memberTag = BitConverter.ToString(hash).Replace("-", String.Empty);
                    GCHandle hashHandle = GCHandle.Alloc(hash, GCHandleType.Pinned);
                    IntPtr catalogInfoPointer = IntPtr.Zero;
                    IntPtr trustDataPointer = IntPtr.Zero;
                    try
                    {
                        WinTrustCatalogInfo catalogInfo = new WinTrustCatalogInfo
                        {
                            cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustCatalogInfo)),
                            dwCatalogVersion = 0,
                            pcwszCatalogFilePath = catalog,
                            pcwszMemberTag = memberTag,
                            pcwszMemberFilePath = member,
                            hMemberFile = safeHandle.DangerousGetHandle(),
                            pbCalculatedFileHash = hashHandle.AddrOfPinnedObject(),
                            cbCalculatedFileHash = hashSize,
                            pcCatalogContext = IntPtr.Zero,
                            hCatAdmin = catAdmin
                        };
                        catalogInfoPointer = Marshal.AllocCoTaskMem(
                            Marshal.SizeOf(typeof(WinTrustCatalogInfo)));
                        Marshal.StructureToPtr(catalogInfo, catalogInfoPointer, false);

                        WinTrustData trustData = new WinTrustData
                        {
                            cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                            dwUIChoice = 2,
                            fdwRevocationChecks = 1,
                            dwUnionChoice = 2,
                            pInfoStruct = catalogInfoPointer,
                            dwStateAction = 0,
                            dwProvFlags = 0x00000080,
                            dwUIContext = 0
                        };
                        trustDataPointer = Marshal.AllocCoTaskMem(
                            Marshal.SizeOf(typeof(WinTrustData)));
                        Marshal.StructureToPtr(trustData, trustDataPointer, false);

                        int status = WinVerifyTrust(
                            new IntPtr(-1),
                            ref action,
                            trustDataPointer);
                        if (status != 0)
                        {
                            throw new InvalidDataException(
                                "Windows driver policy rejected catalog membership for '" +
                                member + "' (0x" + status.ToString("X8") + ").");
                        }
                    }
                    finally
                    {
                        if (trustDataPointer != IntPtr.Zero)
                        {
                            Marshal.DestroyStructure(
                                trustDataPointer,
                                typeof(WinTrustData));
                            Marshal.FreeCoTaskMem(trustDataPointer);
                        }
                        if (catalogInfoPointer != IntPtr.Zero)
                        {
                            Marshal.DestroyStructure(
                                catalogInfoPointer,
                                typeof(WinTrustCatalogInfo));
                            Marshal.FreeCoTaskMem(catalogInfoPointer);
                        }
                        if (hashHandle.IsAllocated) hashHandle.Free();
                    }
                }
            }
            finally
            {
                if (catAdmin != IntPtr.Zero)
                {
                    CryptCATAdminReleaseContext(catAdmin, 0);
                }
            }
        }
    }
}
