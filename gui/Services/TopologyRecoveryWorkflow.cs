using System;

namespace SBMSGui
{
    internal enum TopologyRecoveryOutcome
    {
        Recovered,
        TopologyDidNotSettle,
        NativeRestartFailed
    }

    internal sealed class TopologyRecoveryWorkflow
    {
        public TopologyRecoveryOutcome Recover(
            Func<bool> waitForStableTopology,
            Func<bool> restartNativeOutput)
        {
            if (waitForStableTopology == null)
            {
                throw new ArgumentNullException("waitForStableTopology");
            }
            if (restartNativeOutput == null)
            {
                throw new ArgumentNullException("restartNativeOutput");
            }

            if (!waitForStableTopology())
            {
                return TopologyRecoveryOutcome.TopologyDidNotSettle;
            }
            if (!restartNativeOutput())
            {
                return TopologyRecoveryOutcome.NativeRestartFailed;
            }
            return TopologyRecoveryOutcome.Recovered;
        }
    }
}
