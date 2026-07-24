using System.Collections.Generic;

namespace SBMSGui
{
    public sealed class GuiConfigBridgePair
    {
        public bool Enabled;
        public string Mode;
        public string Target;
        public string TargetDeviceName;
        public string TargetPersistentId;
        public string Horizontal;
        public string Aspect;
        public string Orientation;
        public string Size;
        public string Strategy;
        public string Refresh;
        public string Source;
    }

    public sealed class GuiConfigFile
    {
        public const int CurrentVersion = 2;

        public int Version;
        public string SavedByBuild;
        public bool English;
        public bool LightweightMode;
        public int ConfigTabIndex;
        public int StrategyIndex;
        public int FilterIndex;
        public string SourceText;
        public string TargetText;
        public string SingleRefresh;
        public string SelectedSourceDevice;
        public string SelectedTargetDevice;
        public string SelectedTargetPersistentId;
        public string PrimaryResolution;
        public string PrimarySize;
        public string TargetResolution;
        public string TargetSize;
        public int PrimaryResolutionPresetIndex;
        public int PrimaryAspectPresetIndex;
        public int PrimaryOrientationPresetIndex;
        public int PrimarySizePresetIndex;
        public int TargetResolutionPresetIndex;
        public int TargetAspectPresetIndex;
        public int TargetOrientationPresetIndex;
        public int TargetSizePresetIndex;
        public string ManualBaseHorizontal;
        public string ManualBaseAspect;
        public int ManualBaseOrientationIndex;
        public string ManualBaseSize;
        public string ManualTargetHorizontal;
        public string ManualTargetAspect;
        public int ManualTargetOrientationIndex;
        public string ManualTargetSize;
        public bool StreamMode;
        public bool InputMapping;
        public bool WindowMove;
        public bool DeviceHost;
        public bool VSync;
        // Issue #7: persisted rollback switch for the BETA mode that absorbs
        // valid Windows Settings display edits during topology recovery.
        public bool FollowWindowsTopologyBeta;
        public int SelectedBetaGroupIndex;
        public List<GuiConfigBridgePair> BetaPairs;

        public GuiConfigFile()
        {
            Version = CurrentVersion;
            FollowWindowsTopologyBeta = true;
            BetaPairs = new List<GuiConfigBridgePair>();
        }
    }
}
