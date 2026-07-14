using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class SBMSDisplayConfig
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;

    [StructLayout(LayoutKind.Sequential)] public struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] public struct Rational { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] public struct SourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct TargetInfo
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public int OutputTechnology;
        public uint Rotation; public uint Scaling; public Rational RefreshRate; public uint ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable; public uint StatusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] public struct PathInfo { public SourceInfo SourceInfo; public TargetInfo TargetInfo; public uint Flags; }

    public sealed class ActivePath
    {
        public long AdapterLuid { get; set; }
        public uint SourceId { get; set; }
        public uint TargetId { get; set; }
        public int OutputTechnology { get; set; }
        public bool Active { get; set; }
        public bool TargetAvailable { get; set; }
        public string Classification { get; set; }
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] PathInfo[] paths, ref uint modeCount, IntPtr modes, IntPtr topologyId);

    public static ActivePath[] GetActivePaths()
    {
        uint pathCount, modeCount;
        int error = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
        if (error != 0) throw new Win32Exception(error, "GetDisplayConfigBufferSizes failed");
        var paths = new PathInfo[pathCount];
        IntPtr modes = Marshal.AllocHGlobal(checked((int)Math.Max(1, modeCount) * 64));
        try
        {
            error = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (error != 0) throw new Win32Exception(error, "QueryDisplayConfig failed");
            var result = new List<ActivePath>();
            for (int i = 0; i < pathCount; i++)
            {
                int technology = paths[i].TargetInfo.OutputTechnology;
                string classification = (technology == 15 || technology == 16) ? "virtual" :
                    ((technology >= 0 && technology <= 14) || technology == unchecked((int)0x80000000)) ? "physical" : "unknown";
                long luid = ((long)paths[i].TargetInfo.AdapterId.HighPart << 32) | paths[i].TargetInfo.AdapterId.LowPart;
                result.Add(new ActivePath {
                    AdapterLuid = luid, SourceId = paths[i].SourceInfo.Id, TargetId = paths[i].TargetInfo.Id,
                    OutputTechnology = technology, Active = (paths[i].Flags & 1) != 0,
                    TargetAvailable = paths[i].TargetInfo.TargetAvailable, Classification = classification
                });
            }
            return result.ToArray();
        }
        finally { Marshal.FreeHGlobal(modes); }
    }
}
