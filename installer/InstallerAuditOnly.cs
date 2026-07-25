using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace SBMSSetup
{
    internal sealed class InstallerAuditState
    {
        internal bool JournalExists;
        internal bool EscrowExists;
        internal string InstallerStateFingerprint;
        internal string PayloadFingerprint;
        internal string ConfigurationFingerprint;
        internal string IntegrationFingerprint;
        internal int ActivePhysicalDisplayPathCount;
        internal string[] ActivePhysicalDisplayPaths;
        internal string DisplayTopologyFingerprint;

        internal string EvidenceDigest
        {
            get
            {
                return WindowsInventoryProvider.Sha256Text(
                    String.Join("|", new[]
                    {
                        JournalExists.ToString(
                            CultureInfo.InvariantCulture),
                        EscrowExists.ToString(
                            CultureInfo.InvariantCulture),
                        InstallerStateFingerprint,
                        PayloadFingerprint,
                        ConfigurationFingerprint,
                        IntegrationFingerprint,
                        ActivePhysicalDisplayPathCount.ToString(
                            CultureInfo.InvariantCulture),
                        String.Join(
                            "\n",
                            ActivePhysicalDisplayPaths ??
                                new string[0]),
                        DisplayTopologyFingerprint
                    }));
            }
        }
    }

    internal interface IInstallerAuditStateProbe
    {
        InstallerAuditState Inspect();
    }

    internal sealed class DisplayTopologyEvidence
    {
        internal int ActivePhysicalPathCount;
        internal string[] PhysicalPaths;
        internal string Fingerprint;
    }

    internal sealed class InstallerAuditReport
    {
        internal WindowsDriverInventory BeforeInventory;
        internal WindowsDriverInventory AfterInventory;
        internal InstallerAuditState BeforeState;
        internal InstallerAuditState AfterState;
        internal DriverPackageEvidence[] OwnedPackages;
        internal DeviceInventoryEvidence[] OwnedResidualDevices;
        internal DeviceInventoryEvidence[] HealthyActiveOwnedDevices;
        internal bool Unchanged;
    }

    internal sealed class InstallerAuditOnly
    {
        private readonly IWindowsInventoryProvider inventoryProvider;
        private readonly IInstallerAuditStateProbe stateProbe;

        internal InstallerAuditOnly(
            IWindowsInventoryProvider inventoryProvider,
            IInstallerAuditStateProbe stateProbe)
        {
            if (inventoryProvider == null)
            {
                throw new ArgumentNullException("inventoryProvider");
            }
            if (stateProbe == null)
            {
                throw new ArgumentNullException("stateProbe");
            }
            this.inventoryProvider = inventoryProvider;
            this.stateProbe = stateProbe;
        }

        internal InstallerAuditReport Run(
            InstallerOwnershipPolicy ownershipPolicy)
        {
            if (ownershipPolicy == null)
            {
                throw new ArgumentNullException("ownershipPolicy");
            }

            InstallerAuditState beforeState = stateProbe.Inspect();
            WindowsDriverInventory beforeInventory =
                inventoryProvider.Inspect(ownershipPolicy);
            WindowsDriverInventory afterInventory =
                inventoryProvider.Inspect(ownershipPolicy);
            InstallerAuditState afterState = stateProbe.Inspect();
            bool unchanged =
                String.Equals(
                    beforeInventory.EvidenceDigest,
                    afterInventory.EvidenceDigest,
                    StringComparison.Ordinal) &&
                String.Equals(
                    beforeState.EvidenceDigest,
                    afterState.EvidenceDigest,
                    StringComparison.Ordinal);
            if (!unchanged)
            {
                throw new InvalidOperationException(
                    "AuditOnly observed machine state changing during the audit.");
            }

            return new InstallerAuditReport
            {
                BeforeInventory = beforeInventory,
                AfterInventory = afterInventory,
                BeforeState = beforeState,
                AfterState = afterState,
                OwnedPackages = InstallerOwnership.OwnedPackages(
                    afterInventory,
                    ownershipPolicy),
                OwnedResidualDevices =
                    InstallerOwnership.OwnedResidualDevices(
                    afterInventory,
                    ownershipPolicy),
                HealthyActiveOwnedDevices =
                    InstallerOwnership.HealthyActiveOwnedDevices(
                    afterInventory,
                    ownershipPolicy),
                Unchanged = true
            };
        }
    }

    internal sealed class WindowsInstallerAuditStateProbe :
        IInstallerAuditStateProbe
    {
        private readonly string journalPath;
        private readonly string escrowRoot;
        private readonly string installerStateRoot;
        private readonly string payloadRoot;
        private readonly string configurationRoot;
        private readonly string[] integrationRoots;
        private readonly Func<DisplayTopologyEvidence>
            displayTopologyProbe;

        internal WindowsInstallerAuditStateProbe(
            string journalPath,
            string escrowRoot,
            string installerStateRoot,
            string payloadRoot,
            string configurationRoot,
            string[] integrationRoots,
            Func<DisplayTopologyEvidence> displayTopologyProbe)
        {
            this.journalPath = Path.GetFullPath(journalPath);
            this.escrowRoot = Path.GetFullPath(escrowRoot);
            this.installerStateRoot = Path.GetFullPath(installerStateRoot);
            this.payloadRoot = Path.GetFullPath(payloadRoot);
            this.configurationRoot =
                Path.GetFullPath(configurationRoot);
            if (integrationRoots == null ||
                integrationRoots.Length == 0)
            {
                throw new ArgumentException(
                    "At least one integration evidence path is required.",
                    "integrationRoots");
            }
            this.integrationRoots =
                new string[integrationRoots.Length];
            for (int index = 0;
                index < integrationRoots.Length;
                ++index)
            {
                this.integrationRoots[index] =
                    Path.GetFullPath(integrationRoots[index]);
            }
            if (displayTopologyProbe == null)
            {
                throw new ArgumentNullException(
                    "displayTopologyProbe");
            }
            this.displayTopologyProbe = displayTopologyProbe;
        }

        public InstallerAuditState Inspect()
        {
            DisplayTopologyEvidence display =
                displayTopologyProbe();
            if (display == null ||
                display.ActivePhysicalPathCount < 1 ||
                display.PhysicalPaths == null ||
                display.PhysicalPaths.Length !=
                    display.ActivePhysicalPathCount ||
                String.IsNullOrWhiteSpace(display.Fingerprint))
            {
                throw new InvalidOperationException(
                    "At least one active physical display path is required.");
            }
            return new InstallerAuditState
            {
                JournalExists = File.Exists(journalPath),
                EscrowExists = Directory.Exists(escrowRoot),
                InstallerStateFingerprint =
                    ReadOnlyTreeFingerprint(installerStateRoot),
                PayloadFingerprint =
                    ReadOnlyTreeFingerprint(payloadRoot),
                ConfigurationFingerprint =
                    ReadOnlyTreeFingerprint(configurationRoot),
                IntegrationFingerprint =
                    CompositeFingerprint(integrationRoots),
                ActivePhysicalDisplayPathCount =
                    display.ActivePhysicalPathCount,
                ActivePhysicalDisplayPaths =
                    (string[])display.PhysicalPaths.Clone(),
                DisplayTopologyFingerprint =
                    display.Fingerprint
            };
        }

        internal static string ReadOnlyTreeFingerprint(string root)
        {
            FileAttributes rootAttributes;
            try
            {
                rootAttributes = File.GetAttributes(root);
            }
            catch (FileNotFoundException)
            {
                return "ABSENT";
            }
            catch (DirectoryNotFoundException)
            {
                return "ABSENT";
            }
            if ((rootAttributes & FileAttributes.Directory) == 0)
            {
                string parent = Path.GetDirectoryName(
                    Path.GetFullPath(root));
                using (var leases = new ReadOnlyLeaseSet())
                {
                    ReadOnlyPathLease parentLease = leases.Add(
                        ReadOnlyPathLease.OpenRootDirectory(parent));
                    ReadOnlyPathLease fileLease = leases.Add(
                        ReadOnlyPathLease.OpenFile(
                            root,
                            parentLease));
                    return "FILE|" +
                        WindowsInventoryProvider.Sha256Lease(
                            fileLease);
                }
            }

            using (var leases = new ReadOnlyLeaseSet())
            {
                ReadOnlyPathLease rootLease = leases.Add(
                    ReadOnlyPathLease.OpenRootDirectory(root));
                var lines = new List<string>();
                var pending = new Stack<ReadOnlyPathLease>();
                pending.Push(rootLease);
                while (pending.Count > 0)
                {
                    ReadOnlyPathLease current = pending.Pop();
                    string[] entries =
                        Directory.GetFileSystemEntries(
                            current.RequestedPath,
                            "*",
                            SearchOption.TopDirectoryOnly);
                    Array.Sort(
                        entries,
                        StringComparer.OrdinalIgnoreCase);
                    foreach (string entry in entries)
                    {
                        FileAttributes attributes =
                            File.GetAttributes(entry);
                        string relative =
                            Relative(
                                rootLease.RequestedPath,
                                Path.GetFullPath(entry))
                                .ToUpperInvariant();
                        if ((attributes &
                            FileAttributes.Directory) != 0)
                        {
                            ReadOnlyPathLease directory =
                                leases.Add(
                                    ReadOnlyPathLease.OpenDirectory(
                                        entry,
                                        rootLease));
                            lines.Add("D|" + relative);
                            pending.Push(directory);
                        }
                        else
                        {
                            ReadOnlyPathLease file = leases.Add(
                                ReadOnlyPathLease.OpenFile(
                                    entry,
                                    rootLease));
                            using (FileStream stream =
                                file.OpenReadStream())
                            {
                                lines.Add(String.Join("|", new[]
                                {
                                    "F",
                                    relative,
                                    stream.Length.ToString(
                                        CultureInfo.InvariantCulture),
                                    WindowsInventoryProvider
                                        .Sha256Lease(file)
                                }));
                            }
                        }
                    }
                }
                lines.Sort(StringComparer.Ordinal);
                return WindowsInventoryProvider.Sha256Text(
                    String.Join("\n", lines.ToArray()));
            }
        }

        internal static void RejectReparseAttributes(
            string path,
            FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Audit evidence path contains a reparse point: " +
                    path);
            }
        }

        private static string CompositeFingerprint(string[] paths)
        {
            var lines = new List<string>();
            foreach (string path in paths)
            {
                lines.Add(
                    path.ToUpperInvariant() + "|" +
                    ReadOnlyTreeFingerprint(path));
            }
            lines.Sort(StringComparer.Ordinal);
            return WindowsInventoryProvider.Sha256Text(
                String.Join("\n", lines.ToArray()));
        }

        private static string Relative(string root, string path)
        {
            string prefix = root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!path.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Audit path escaped its configured root.");
            }
            return path.Substring(prefix.Length);
        }
    }

    internal static class WindowsDisplayTopologyProbe
    {
        private const uint QdcOnlyActivePaths = 0x2;
        private const uint QdcVirtualModeAware = 0x10;
        private const int ErrorInsufficientBuffer = 122;
        private const uint DisplayConfigPathActive = 0x1;
        private const uint ModeInfoTypeSource = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            internal uint LowPart;
            internal int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rational
        {
            internal uint Numerator;
            internal uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PointL
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SourceMode
        {
            internal uint Width;
            internal uint Height;
            internal int PixelFormat;
            internal PointL Position;
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct ModeInfo
        {
            [FieldOffset(0)] internal uint InfoType;
            [FieldOffset(4)] internal uint Id;
            [FieldOffset(8)] internal Luid AdapterId;
            [FieldOffset(16)] internal SourceMode SourceMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SourceInfo
        {
            internal Luid AdapterId;
            internal uint Id;
            internal uint ModeInfoIdx;
            internal uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TargetInfo
        {
            internal Luid AdapterId;
            internal uint Id;
            internal uint ModeInfoIdx;
            internal int OutputTechnology;
            internal uint Rotation;
            internal uint Scaling;
            internal Rational RefreshRate;
            internal uint ScanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool TargetAvailable;
            internal uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PathInfo
        {
            internal SourceInfo SourceInfo;
            internal TargetInfo TargetInfo;
            internal uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfoHeader
        {
            internal uint Type;
            internal uint Size;
            internal Luid AdapterId;
            internal uint Id;
        }

        [StructLayout(
            LayoutKind.Sequential,
            CharSet = CharSet.Unicode)]
        private struct TargetDeviceName
        {
            internal DeviceInfoHeader Header;
            internal uint Flags;
            internal int OutputTechnology;
            internal ushort EdidManufactureId;
            internal ushort EdidProductCodeId;
            internal uint ConnectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            internal string MonitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string MonitorDevicePath;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint pathCount,
            out uint modeCount);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint pathCount,
            [Out] PathInfo[] paths,
            ref uint modeCount,
            [Out] ModeInfo[] modes,
            IntPtr topologyId);

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode)]
        private static extern int DisplayConfigGetDeviceInfo(
            ref TargetDeviceName request);

        internal static DisplayTopologyEvidence Capture()
        {
            uint flags =
                QdcOnlyActivePaths | QdcVirtualModeAware;
            for (int attempt = 0; attempt < 4; ++attempt)
            {
                uint pathCount;
                uint modeCount;
                int error = GetDisplayConfigBufferSizes(
                    flags,
                    out pathCount,
                    out modeCount);
                if (error != 0)
                {
                    throw new InvalidOperationException(
                        "GetDisplayConfigBufferSizes failed: " + error);
                }
                var paths = new PathInfo[pathCount];
                var modes = new ModeInfo[modeCount];
                error = QueryDisplayConfig(
                    flags,
                    ref pathCount,
                    paths,
                    ref modeCount,
                    modes,
                    IntPtr.Zero);
                if (error == ErrorInsufficientBuffer)
                {
                    continue;
                }
                if (error != 0)
                {
                    throw new InvalidOperationException(
                        "QueryDisplayConfig failed: " + error);
                }
                var evidence = new List<string>();
                for (int index = 0;
                    index < pathCount;
                    ++index)
                {
                    PathInfo path = paths[index];
                    if ((path.Flags & DisplayConfigPathActive) == 0 ||
                        !IsUsablePhysicalPath(
                            path.TargetInfo.OutputTechnology,
                            path.TargetInfo.TargetAvailable,
                            path.SourceInfo.StatusFlags,
                            path.TargetInfo.StatusFlags))
                    {
                        continue;
                    }
                    var target = new TargetDeviceName();
                    target.Header.Type = 2;
                    target.Header.Size = (uint)Marshal.SizeOf(
                        typeof(TargetDeviceName));
                    target.Header.AdapterId =
                        path.TargetInfo.AdapterId;
                    target.Header.Id = path.TargetInfo.Id;
                    error = DisplayConfigGetDeviceInfo(ref target);
                    if (error != 0 ||
                        String.IsNullOrWhiteSpace(
                            target.MonitorDevicePath))
                    {
                        throw new InvalidOperationException(
                            "Physical display target identity is unavailable.");
                    }
                    SourceMode mode = FindSourceMode(
                        path.SourceInfo,
                        modes,
                        modeCount);
                    evidence.Add(String.Join("|", new[]
                    {
                        ToInt64(path.TargetInfo.AdapterId).ToString(
                            CultureInfo.InvariantCulture),
                        path.SourceInfo.Id.ToString(
                            CultureInfo.InvariantCulture),
                        path.TargetInfo.Id.ToString(
                            CultureInfo.InvariantCulture),
                        path.TargetInfo.OutputTechnology.ToString(
                            CultureInfo.InvariantCulture),
                        target.MonitorDevicePath,
                        target.ConnectorInstance.ToString(
                            CultureInfo.InvariantCulture),
                        path.SourceInfo.StatusFlags.ToString(
                            CultureInfo.InvariantCulture),
                        path.TargetInfo.StatusFlags.ToString(
                            CultureInfo.InvariantCulture),
                        path.TargetInfo.TargetAvailable.ToString(
                            CultureInfo.InvariantCulture),
                        mode.Width.ToString(
                            CultureInfo.InvariantCulture),
                        mode.Height.ToString(
                            CultureInfo.InvariantCulture),
                        mode.Position.X.ToString(
                            CultureInfo.InvariantCulture),
                        mode.Position.Y.ToString(
                            CultureInfo.InvariantCulture),
                        path.TargetInfo.RefreshRate.Numerator.ToString(
                            CultureInfo.InvariantCulture),
                        path.TargetInfo.RefreshRate.Denominator.ToString(
                            CultureInfo.InvariantCulture),
                        path.TargetInfo.Rotation.ToString(
                            CultureInfo.InvariantCulture),
                        path.TargetInfo.Scaling.ToString(
                            CultureInfo.InvariantCulture)
                    }));
                }
                if (evidence.Count < 1)
                {
                    throw new InvalidOperationException(
                        "No active physical display path was observed.");
                }
                evidence.Sort(StringComparer.Ordinal);
                return new DisplayTopologyEvidence
                {
                    ActivePhysicalPathCount = evidence.Count,
                    PhysicalPaths = evidence.ToArray(),
                    Fingerprint =
                        WindowsInventoryProvider.Sha256Text(
                            String.Join(
                                "\n",
                                evidence.ToArray()))
                };
            }
            throw new InvalidOperationException(
                "DisplayConfig buffers changed repeatedly.");
        }

        internal static bool IsPhysical(int technology)
        {
            return (technology >= 0 && technology <= 14) ||
                technology == 18 ||
                technology == unchecked((int)0x80000000);
        }

        internal static bool IsUsablePhysicalPath(
            int technology,
            bool targetAvailable,
            uint sourceStatusFlags,
            uint targetStatusFlags)
        {
            const uint InUse = 0x1;
            return IsPhysical(technology) &&
                targetAvailable &&
                (sourceStatusFlags & InUse) != 0 &&
                (targetStatusFlags & InUse) != 0;
        }

        private static SourceMode FindSourceMode(
            SourceInfo source,
            ModeInfo[] modes,
            uint count)
        {
            uint virtualIndex = source.ModeInfoIdx >> 16;
            if (virtualIndex != 0xFFFF &&
                virtualIndex < count &&
                modes[virtualIndex].InfoType == ModeInfoTypeSource)
            {
                return modes[virtualIndex].SourceMode;
            }
            for (int index = 0; index < count; ++index)
            {
                if (modes[index].InfoType == ModeInfoTypeSource &&
                    modes[index].Id == source.Id &&
                    SameLuid(
                        modes[index].AdapterId,
                        source.AdapterId))
                {
                    return modes[index].SourceMode;
                }
            }
            throw new InvalidOperationException(
                "Physical display source mode is unavailable.");
        }

        private static bool SameLuid(Luid left, Luid right)
        {
            return left.LowPart == right.LowPart &&
                left.HighPart == right.HighPart;
        }

        private static long ToInt64(Luid value)
        {
            return ((long)value.HighPart << 32) | value.LowPart;
        }
    }
}
