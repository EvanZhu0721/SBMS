using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SBMSGui
{
    internal interface IDisplayModeService
    {
        bool TryGetCurrentMode(string deviceName, out DisplayRuntimeMode mode);
        bool TryApplyMode(
            string deviceName,
            Resolution resolution,
            string refreshText,
            int orientation,
            out Resolution appliedResolution,
            out string appliedRefresh,
            out string message);
        int NormalizeOrientation(int orientation);
    }

    internal sealed class DisplayModeService : IDisplayModeService
    {
        private const int EnumCurrentSettings = -1;
        private const int DispChangeSuccessful = 0;
        private const int DmPelsWidth = 0x00080000;
        private const int DmPelsHeight = 0x00100000;
        private const int DmDisplayFrequency = 0x00400000;
        private const int DmDisplayOrientation = 0x00000080;
        private const int OrientationDefault = 0;
        private const int Orientation90 = 1;
        private const int Orientation180 = 2;
        private const int Orientation270 = 3;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode devMode, IntPtr hwnd, int flags, IntPtr lParam);

        private sealed class DisplayModeCandidate
        {
            public Resolution Resolution;
            public int Refresh;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        public bool TryGetCurrentMode(string deviceName, out DisplayRuntimeMode mode)
        {
            mode = null;
            var devMode = new DevMode();
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DevMode));
            if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref devMode) ||
                devMode.dmPelsWidth <= 0 ||
                devMode.dmPelsHeight <= 0)
            {
                return false;
            }

            mode = new DisplayRuntimeMode
            {
                Resolution = new Resolution { Width = devMode.dmPelsWidth, Height = devMode.dmPelsHeight },
                Refresh = devMode.dmDisplayFrequency > 0
                    ? devMode.dmDisplayFrequency.ToString(CultureInfo.InvariantCulture)
                    : "",
                Orientation = NormalizeOrientation(devMode.dmDisplayOrientation)
            };
            return true;
        }

        public bool TryApplyMode(
            string deviceName,
            Resolution resolution,
            string refreshText,
            int orientation,
            out Resolution appliedResolution,
            out string appliedRefresh,
            out string message)
        {
            appliedResolution = resolution;
            appliedRefresh = refreshText;
            Resolution selectedResolution;
            string selectedRefreshText;
            string snapMessage;
            if (!TrySelectSupportedMode(deviceName, resolution, refreshText, out selectedResolution, out selectedRefreshText, out snapMessage))
            {
                selectedResolution = resolution;
                selectedRefreshText = refreshText;
                snapMessage = "";
            }

            var devMode = new DevMode();
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DevMode));
            if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref devMode))
            {
                message = "读取虚拟显示器当前模式失败: " + deviceName;
                return false;
            }

            int refresh;
            bool hasRefresh = int.TryParse(selectedRefreshText, out refresh) && refresh > 0;
            devMode.dmPelsWidth = selectedResolution.Width;
            devMode.dmPelsHeight = selectedResolution.Height;
            devMode.dmDisplayOrientation = orientation;
            devMode.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayOrientation;
            if (hasRefresh)
            {
                devMode.dmDisplayFrequency = refresh;
                devMode.dmFields |= DmDisplayFrequency;
            }

            int result = ChangeDisplaySettingsEx(deviceName, ref devMode, IntPtr.Zero, 0, IntPtr.Zero);
            if (result == DispChangeSuccessful)
            {
                appliedResolution = selectedResolution;
                appliedRefresh = hasRefresh ? refresh.ToString(CultureInfo.InvariantCulture) : selectedRefreshText;
                message = (snapMessage.Length > 0 ? snapMessage + "; " : "") +
                          "虚拟模式切换成功: " + deviceName + " -> " + ResolutionMath.Format(selectedResolution) + (hasRefresh ? "@" + refresh.ToString(CultureInfo.InvariantCulture) : "") + " orientation=" + orientation;
                return true;
            }

            if (hasRefresh)
            {
                devMode.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayOrientation;
                result = ChangeDisplaySettingsEx(deviceName, ref devMode, IntPtr.Zero, 0, IntPtr.Zero);
                if (result == DispChangeSuccessful)
                {
                    appliedResolution = selectedResolution;
                    appliedRefresh = "";
                    message = (snapMessage.Length > 0 ? snapMessage + "; " : "") +
                              "虚拟模式切换成功: " + deviceName + " -> " + ResolutionMath.Format(selectedResolution) + " orientation=" + orientation;
                    return true;
                }
            }

            message = (snapMessage.Length > 0 ? snapMessage + "; " : "") +
                      "虚拟模式切换失败: " + deviceName + " -> " + ResolutionMath.Format(selectedResolution) + " result=" + result;
            return false;
        }

        public int NormalizeOrientation(int orientation)
        {
            switch (orientation)
            {
                case Orientation90:
                case Orientation180:
                case Orientation270:
                    return orientation;
                default:
                    return OrientationDefault;
            }
        }

        internal static bool SameResolution(Resolution left, Resolution right)
        {
            return left.Width == right.Width && left.Height == right.Height;
        }

        private static List<DisplayModeCandidate> GetModeCandidates(string deviceName)
        {
            var candidates = new List<DisplayModeCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int modeIndex = 0; modeIndex < 1024; ++modeIndex)
            {
                var mode = new DevMode();
                mode.dmSize = (short)Marshal.SizeOf(typeof(DevMode));
                if (!EnumDisplaySettings(deviceName, modeIndex, ref mode))
                {
                    break;
                }
                if (mode.dmPelsWidth <= 0 || mode.dmPelsHeight <= 0)
                {
                    continue;
                }

                string key = mode.dmPelsWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                             mode.dmPelsHeight.ToString(CultureInfo.InvariantCulture) + "@" +
                             mode.dmDisplayFrequency.ToString(CultureInfo.InvariantCulture);
                if (seen.Contains(key))
                {
                    continue;
                }

                seen.Add(key);
                candidates.Add(new DisplayModeCandidate
                {
                    Resolution = new Resolution { Width = mode.dmPelsWidth, Height = mode.dmPelsHeight },
                    Refresh = mode.dmDisplayFrequency
                });
            }
            return candidates;
        }

        private static bool TrySelectSupportedMode(
            string deviceName,
            Resolution requestedResolution,
            string requestedRefreshText,
            out Resolution selectedResolution,
            out string selectedRefreshText,
            out string snapMessage)
        {
            selectedResolution = requestedResolution;
            selectedRefreshText = requestedRefreshText;
            snapMessage = "";

            List<DisplayModeCandidate> candidates = GetModeCandidates(deviceName);
            if (candidates.Count == 0)
            {
                return false;
            }

            int requestedRefresh;
            bool hasRequestedRefresh = int.TryParse(requestedRefreshText, out requestedRefresh) && requestedRefresh > 0;
            double requestedAspect = requestedResolution.Height > 0
                ? requestedResolution.Width / (double)requestedResolution.Height
                : 0.0;
            DisplayModeCandidate best = null;
            double bestScore = double.MaxValue;

            for (int i = 0; i < candidates.Count; ++i)
            {
                DisplayModeCandidate candidate = candidates[i];
                bool exactResolution = SameResolution(candidate.Resolution, requestedResolution);
                double aspect = candidate.Resolution.Height > 0
                    ? candidate.Resolution.Width / (double)candidate.Resolution.Height
                    : 0.0;
                double aspectError = requestedAspect > 0.0
                    ? Math.Abs(aspect - requestedAspect) / requestedAspect
                    : 0.0;
                if (!exactResolution && aspectError > 0.02)
                {
                    continue;
                }

                double sizeError = Math.Abs(candidate.Resolution.Width - requestedResolution.Width) +
                                   Math.Abs(candidate.Resolution.Height - requestedResolution.Height);
                double refreshError = 0.0;
                if (hasRequestedRefresh && candidate.Refresh > 0)
                {
                    refreshError = Math.Abs(candidate.Refresh - requestedRefresh) * 0.05;
                }
                else if (!hasRequestedRefresh && candidate.Refresh > 0)
                {
                    refreshError = -candidate.Refresh * 0.001;
                }
                double score = (exactResolution ? 0.0 : sizeError + aspectError * 10000.0) + refreshError;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best == null)
            {
                return false;
            }

            selectedResolution = best.Resolution;
            selectedRefreshText = best.Refresh > 0
                ? best.Refresh.ToString(CultureInfo.InvariantCulture)
                : requestedRefreshText;

            if (!SameResolution(selectedResolution, requestedResolution) ||
                (hasRequestedRefresh && best.Refresh > 0 && best.Refresh != requestedRefresh))
            {
                snapMessage = "虚拟模式贴合可用模式: requested=" + ResolutionMath.Format(requestedResolution) +
                              (hasRequestedRefresh ? "@" + requestedRefresh.ToString(CultureInfo.InvariantCulture) : "") +
                              " applied=" + ResolutionMath.Format(selectedResolution) +
                              (best.Refresh > 0 ? "@" + best.Refresh.ToString(CultureInfo.InvariantCulture) : "");
            }
            return true;
        }
    }
}
