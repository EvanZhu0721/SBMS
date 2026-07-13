using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SBMSGui
{
    internal interface ITopologyDiscoveryService
    {
        List<DisplayChoice> Parse(string output, Func<string, DisplayRuntimeMode> runtimeModeProvider);
        bool TryParseLine(string line, Func<string, DisplayRuntimeMode> runtimeModeProvider, out DisplayChoice display);
        List<DisplayChoice> ParseVirtualSources(string output);
        DisplayChoice FindVirtualSourceMode(string output, string deviceName, Resolution resolution, Func<string, DisplayRuntimeMode> runtimeModeProvider);
        string BuildSignature(string output);
    }

    internal sealed class TopologyDiscoveryService : ITopologyDiscoveryService
    {
        private const int DefaultOrientation = 0;
        private static readonly Regex DisplayLinePattern = new Regex(
            @"^(\\\\\.\\DISPLAY\d+)( primary)?\: pos=[^ ]+ mode=(\d+x\d+)@(\d+)(?: sunshine=(\{[0-9a-fA-F-]{36}\}))? name=(.+)$",
            RegexOptions.Compiled);

        public List<DisplayChoice> Parse(string output, Func<string, DisplayRuntimeMode> runtimeModeProvider)
        {
            var displays = new List<DisplayChoice>();
            foreach (string rawLine in (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                DisplayChoice display;
                if (TryParseLine(rawLine.Trim(), runtimeModeProvider, out display))
                {
                    display.Number = displays.Count + 1;
                    displays.Add(display);
                }
            }
            return displays;
        }

        public bool TryParseLine(string line, Func<string, DisplayRuntimeMode> runtimeModeProvider, out DisplayChoice display)
        {
            display = null;
            Match match = DisplayLinePattern.Match(line ?? "");
            if (!match.Success)
            {
                return false;
            }

            DisplayRuntimeMode runtimeMode = runtimeModeProvider == null ? null : runtimeModeProvider(match.Groups[1].Value);
            string name = match.Groups[6].Value.Trim();
            display = new DisplayChoice
            {
                DeviceName = match.Groups[1].Value,
                Primary = match.Groups[2].Success,
                Resolution = runtimeMode == null ? match.Groups[3].Value : ResolutionMath.Format(runtimeMode.Resolution),
                Refresh = runtimeMode != null && !string.IsNullOrWhiteSpace(runtimeMode.Refresh) ? runtimeMode.Refresh : match.Groups[4].Value,
                SunshineId = match.Groups[5].Success ? match.Groups[5].Value : "",
                Name = name,
                Orientation = runtimeMode == null ? DefaultOrientation : runtimeMode.Orientation,
                Virtual = IsVirtualDisplayName(name)
            };
            return true;
        }

        public List<DisplayChoice> ParseVirtualSources(string output)
        {
            List<DisplayChoice> all = Parse(output, null);
            return all.FindAll(delegate(DisplayChoice display) { return display.Virtual; });
        }

        public DisplayChoice FindVirtualSourceMode(string output, string deviceName, Resolution resolution, Func<string, DisplayRuntimeMode> runtimeModeProvider)
        {
            string resolutionText = ResolutionMath.Format(resolution);
            foreach (DisplayChoice display in Parse(output, runtimeModeProvider))
            {
                if (display.Virtual &&
                    string.Equals(display.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(display.Resolution, resolutionText, StringComparison.OrdinalIgnoreCase))
                {
                    return display;
                }
            }
            return null;
        }

        public string BuildSignature(string output)
        {
            var signature = new List<string>();
            foreach (DisplayChoice display in Parse(output, null))
            {
                signature.Add(display.DeviceName + "|" + display.Resolution + "|" + display.Refresh + "|" + display.Name);
            }
            return string.Join(";", signature.ToArray());
        }

        private static bool IsVirtualDisplayName(string name)
        {
            string lower = (name ?? "").ToLowerInvariant();
            return lower.Contains("iddsample") || lower.Contains("displaybridge") || lower.Contains("sbms");
        }
    }
}
