using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class SBMSDisplayConfig
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint QDC_VIRTUAL_MODE_AWARE = 0x10;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
    private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;

    [StructLayout(LayoutKind.Sequential)] public struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] public struct Rational { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] public struct PointL { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SourceMode { public uint Width; public uint Height; public int PixelFormat; public PointL Position; }
    [StructLayout(LayoutKind.Sequential)] public struct Region { public uint Cx; public uint Cy; }
    [StructLayout(LayoutKind.Sequential)] public struct VideoSignalInfo
    {
        public ulong PixelRate; public Rational HSyncFreq; public Rational VSyncFreq;
        public Region ActiveSize; public Region TotalSize; public uint VideoStandard; public uint ScanLineOrdering;
    }
    [StructLayout(LayoutKind.Sequential)] public struct TargetMode { public VideoSignalInfo TargetVideoSignalInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] public struct ModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;
        [FieldOffset(16)] public TargetMode TargetMode;
        [FieldOffset(16)] public SourceMode SourceMode;
    }
    [StructLayout(LayoutKind.Sequential)] public struct SourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct TargetInfo
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public int OutputTechnology;
        public uint Rotation; public uint Scaling; public Rational RefreshRate; public uint ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable; public uint StatusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] public struct PathInfo { public SourceInfo SourceInfo; public TargetInfo TargetInfo; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] public struct DeviceInfoHeader { public uint Type; public uint Size; public Luid AdapterId; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct TargetDeviceName
    {
        public DeviceInfoHeader Header; public uint Flags; public int OutputTechnology;
        public ushort EdidManufactureId; public ushort EdidProductCodeId; public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    public sealed class ActivePath
    {
        public long AdapterLuid { get; set; }
        public uint SourceId { get; set; }
        public uint TargetId { get; set; }
        public string SourceName { get; set; }
        public string TargetName { get; set; }
        public string MonitorDevicePath { get; set; }
        public int OutputTechnology { get; set; }
        public bool Active { get; set; }
        public bool TargetAvailable { get; set; }
        public string Classification { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        public int PositionX { get; set; }
        public int PositionY { get; set; }
        public uint RefreshNumerator { get; set; }
        public uint RefreshDenominator { get; set; }
        public uint Rotation { get; set; }
        public uint Scaling { get; set; }
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topologyId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);

    private static long ToInt64(Luid value) { return ((long)value.HighPart << 32) | value.LowPart; }
    private static bool SameLuid(Luid left, Luid right) { return left.LowPart == right.LowPart && left.HighPart == right.HighPart; }

    private static string GetSourceName(SourceInfo source)
    {
        var request = new SourceDeviceName();
        request.Header.Type = 1; request.Header.Size = (uint)Marshal.SizeOf(typeof(SourceDeviceName));
        request.Header.AdapterId = source.AdapterId; request.Header.Id = source.Id;
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request.ViewGdiDeviceName : null;
    }

    private static TargetDeviceName GetTargetName(TargetInfo target)
    {
        var request = new TargetDeviceName();
        request.Header.Type = 2; request.Header.Size = (uint)Marshal.SizeOf(typeof(TargetDeviceName));
        request.Header.AdapterId = target.AdapterId; request.Header.Id = target.Id;
        if (DisplayConfigGetDeviceInfo(ref request) != 0) return new TargetDeviceName();
        return request;
    }

    private static SourceMode? FindSourceMode(SourceInfo source, ModeInfo[] modes, uint modeCount)
    {
        uint virtualIndex = source.ModeInfoIdx >> 16;
        if (virtualIndex != 0xFFFF && virtualIndex < modeCount && modes[virtualIndex].InfoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            return modes[virtualIndex].SourceMode;
        for (int i = 0; i < modeCount; i++)
            if (modes[i].InfoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE && modes[i].Id == source.Id && SameLuid(modes[i].AdapterId, source.AdapterId))
                return modes[i].SourceMode;
        return null;
    }

    public static ActivePath[] GetActivePaths()
    {
        uint flags = QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_MODE_AWARE;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint pathCount, modeCount;
            int error = GetDisplayConfigBufferSizes(flags, out pathCount, out modeCount);
            if (error != 0) throw new Win32Exception(error, "GetDisplayConfigBufferSizes failed");
            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            error = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (error == ERROR_INSUFFICIENT_BUFFER) continue;
            if (error != 0) throw new Win32Exception(error, "QueryDisplayConfig failed");
            var result = new List<ActivePath>();
            for (int i = 0; i < pathCount; i++)
            {
                int technology = paths[i].TargetInfo.OutputTechnology;
                string classification = (technology == 15 || technology == 16) ? "virtual" :
                    ((technology >= 0 && technology <= 14) || technology == unchecked((int)0x80000000)) ? "physical" : "unknown";
                var target = GetTargetName(paths[i].TargetInfo);
                SourceMode? sourceMode = FindSourceMode(paths[i].SourceInfo, modes, modeCount);
                result.Add(new ActivePath {
                    AdapterLuid = ToInt64(paths[i].TargetInfo.AdapterId), SourceId = paths[i].SourceInfo.Id, TargetId = paths[i].TargetInfo.Id,
                    SourceName = GetSourceName(paths[i].SourceInfo), TargetName = target.MonitorFriendlyDeviceName,
                    MonitorDevicePath = target.MonitorDevicePath, OutputTechnology = technology,
                    Active = (paths[i].Flags & DISPLAYCONFIG_PATH_ACTIVE) != 0, TargetAvailable = paths[i].TargetInfo.TargetAvailable,
                    Classification = classification, Width = sourceMode.HasValue ? sourceMode.Value.Width : 0,
                    Height = sourceMode.HasValue ? sourceMode.Value.Height : 0, PositionX = sourceMode.HasValue ? sourceMode.Value.Position.X : 0,
                    PositionY = sourceMode.HasValue ? sourceMode.Value.Position.Y : 0,
                    RefreshNumerator = paths[i].TargetInfo.RefreshRate.Numerator, RefreshDenominator = paths[i].TargetInfo.RefreshRate.Denominator,
                    Rotation = paths[i].TargetInfo.Rotation, Scaling = paths[i].TargetInfo.Scaling
                });
            }
            return result.ToArray();
        }
        throw new Win32Exception(ERROR_INSUFFICIENT_BUFFER, "DisplayConfig buffer changed repeatedly");
    }
}
