using System;
using System.Collections.Generic;

namespace SBMSGui
{
    internal delegate DisplayChoice FindDisplayModeDelegate(
        string deviceName,
        Resolution resolution,
        string listOutput);

    internal interface ITopologyRecoveryService
    {
        string LastFailure { get; }
        bool WaitForStable(
            int timeoutMs,
            int requiredStableSamples,
            Func<string, string> signatureBuilder,
            Func<int, bool> pump,
            out string stableSignature);

        bool WaitForAny(int timeoutMs, Func<int, bool> pump, out DisplayChoice source);
        bool WaitForAny(string selector, int timeoutMs, Func<int, bool> pump, out DisplayChoice source);
        bool WaitForCount(int minimumCount, int timeoutMs, Func<int, bool> pump, out List<DisplayChoice> sources);
        bool WaitForMode(string deviceName, Resolution resolution, int timeoutMs, Func<int, bool> pump, out DisplayChoice source);
        bool WaitForClear(int timeoutMs, Func<int, bool> pump);
    }

    internal sealed class TopologyRecoveryService : ITopologyRecoveryService
    {
        private const int PollIntervalMs = 500;
        private const int ClearPollIntervalMs = 250;

        private readonly Func<string> captureList;
        private readonly Func<bool> hostRunning;
        private readonly Func<string, List<DisplayChoice>> parseVirtual;
        private readonly FindDisplayModeDelegate findMode;
        private readonly Func<string> failureProbe;
        private readonly Func<int> currentTick;

        public TopologyRecoveryService(
            Func<string> captureList,
            Func<bool> hostRunning,
            Func<string, List<DisplayChoice>> parseVirtual,
            FindDisplayModeDelegate findMode)
            : this(captureList, hostRunning, parseVirtual, findMode, delegate { return Environment.TickCount; })
        {
        }

        public TopologyRecoveryService(
            Func<string> captureList,
            Func<bool> hostRunning,
            Func<string, List<DisplayChoice>> parseVirtual,
            FindDisplayModeDelegate findMode,
            Func<string> failureProbe)
            : this(captureList, hostRunning, parseVirtual, findMode, delegate { return Environment.TickCount; }, failureProbe)
        {
        }

        internal TopologyRecoveryService(
            Func<string> captureList,
            Func<bool> hostRunning,
            Func<string, List<DisplayChoice>> parseVirtual,
            FindDisplayModeDelegate findMode,
            Func<int> currentTick)
            : this(captureList, hostRunning, parseVirtual, findMode, currentTick, null)
        {
        }

        private TopologyRecoveryService(
            Func<string> captureList,
            Func<bool> hostRunning,
            Func<string, List<DisplayChoice>> parseVirtual,
            FindDisplayModeDelegate findMode,
            Func<int> currentTick,
            Func<string> failureProbe)
        {
            if (captureList == null)
            {
                throw new ArgumentNullException("captureList");
            }
            if (hostRunning == null)
            {
                throw new ArgumentNullException("hostRunning");
            }
            if (parseVirtual == null)
            {
                throw new ArgumentNullException("parseVirtual");
            }
            if (findMode == null)
            {
                throw new ArgumentNullException("findMode");
            }
            if (currentTick == null)
            {
                throw new ArgumentNullException("currentTick");
            }

            this.captureList = captureList;
            this.hostRunning = hostRunning;
            this.parseVirtual = parseVirtual;
            this.findMode = findMode;
            this.currentTick = currentTick;
            this.failureProbe = failureProbe;
            LastFailure = "";
        }

        public string LastFailure { get; private set; }

        public bool WaitForStable(
            int timeoutMs,
            int requiredStableSamples,
            Func<string, string> signatureBuilder,
            Func<int, bool> pump,
            out string stableSignature)
        {
            if (requiredStableSamples <= 0)
            {
                throw new ArgumentOutOfRangeException("requiredStableSamples");
            }
            if (signatureBuilder == null)
            {
                throw new ArgumentNullException("signatureBuilder");
            }

            stableSignature = "";
            LastFailure = "";
            string previousSignature = "";
            int stableSamples = 0;
            int startedAt = currentTick();

            while (HasTimeRemaining(startedAt, timeoutMs))
            {
                if (!hostRunning())
                {
                    return false;
                }
                if (HasFailure())
                {
                    return false;
                }

                string signature = signatureBuilder(captureList()) ?? "";
                if (signature.Length == 0)
                {
                    previousSignature = "";
                    stableSamples = 0;
                }
                else if (string.Equals(signature, previousSignature, StringComparison.Ordinal))
                {
                    ++stableSamples;
                }
                else
                {
                    previousSignature = signature;
                    stableSamples = 1;
                }

                if (stableSamples >= requiredStableSamples)
                {
                    stableSignature = signature;
                    return true;
                }
                if (!ContinueAfterDelay(pump, PollIntervalMs))
                {
                    return false;
                }
            }
            return false;
        }

        public bool WaitForAny(int timeoutMs, Func<int, bool> pump, out DisplayChoice source)
        {
            return WaitForAny(null, timeoutMs, pump, out source);
        }

        public bool WaitForAny(string selector, int timeoutMs, Func<int, bool> pump, out DisplayChoice source)
        {
            source = null;
            LastFailure = "";
            int startedAt = currentTick();
            while (HasTimeRemaining(startedAt, timeoutMs))
            {
                if (!hostRunning())
                {
                    return false;
                }
                if (HasFailure())
                {
                    return false;
                }

                List<DisplayChoice> sources = ParseVirtual(captureList());
                for (int i = 0; i < sources.Count; ++i)
                {
                    DisplayChoice candidate = sources[i];
                    if (candidate != null &&
                        (string.IsNullOrEmpty(selector) ||
                         string.Equals(candidate.DeviceName, selector, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(candidate.Resolution, selector, StringComparison.OrdinalIgnoreCase)))
                    {
                        source = candidate;
                        return true;
                    }
                }
                if (!ContinueAfterDelay(pump, PollIntervalMs))
                {
                    return false;
                }
            }
            return false;
        }

        public bool WaitForCount(
            int minimumCount,
            int timeoutMs,
            Func<int, bool> pump,
            out List<DisplayChoice> sources)
        {
            if (minimumCount < 0)
            {
                throw new ArgumentOutOfRangeException("minimumCount");
            }

            sources = new List<DisplayChoice>();
            LastFailure = "";
            int startedAt = currentTick();
            while (HasTimeRemaining(startedAt, timeoutMs))
            {
                if (!hostRunning())
                {
                    return false;
                }
                if (HasFailure())
                {
                    return false;
                }

                sources = ParseVirtual(captureList());
                if (sources.Count >= minimumCount)
                {
                    return true;
                }
                if (!ContinueAfterDelay(pump, PollIntervalMs))
                {
                    return false;
                }
            }
            return false;
        }

        public bool WaitForMode(
            string deviceName,
            Resolution resolution,
            int timeoutMs,
            Func<int, bool> pump,
            out DisplayChoice source)
        {
            source = null;
            LastFailure = "";
            int startedAt = currentTick();
            while (HasTimeRemaining(startedAt, timeoutMs))
            {
                if (!hostRunning())
                {
                    return false;
                }
                if (HasFailure())
                {
                    return false;
                }

                string listOutput = captureList();
                source = findMode(deviceName, resolution, listOutput);
                if (source != null)
                {
                    return true;
                }
                if (!ContinueAfterDelay(pump, PollIntervalMs))
                {
                    source = null;
                    return false;
                }
            }
            source = null;
            return false;
        }

        public bool WaitForClear(int timeoutMs, Func<int, bool> pump)
        {
            LastFailure = "";
            int startedAt = currentTick();
            while (HasTimeRemaining(startedAt, timeoutMs))
            {
                if (ParseVirtual(captureList()).Count == 0)
                {
                    return true;
                }
                if (!ContinueAfterDelay(pump, ClearPollIntervalMs))
                {
                    return false;
                }
            }
            return false;
        }

        private bool HasFailure()
        {
            if (failureProbe == null)
            {
                return false;
            }
            string failure = failureProbe() ?? "";
            if (failure.Length == 0)
            {
                return false;
            }
            LastFailure = failure;
            return true;
        }

        private List<DisplayChoice> ParseVirtual(string listOutput)
        {
            return parseVirtual(listOutput ?? "") ?? new List<DisplayChoice>();
        }

        private bool HasTimeRemaining(int startedAt, int timeoutMs)
        {
            if (timeoutMs <= 0)
            {
                return false;
            }
            int elapsed = unchecked(currentTick() - startedAt);
            return elapsed >= 0 && elapsed < timeoutMs;
        }

        private static bool ContinueAfterDelay(Func<int, bool> pump, int delayMs)
        {
            return pump == null || pump(delayMs);
        }
    }
}
