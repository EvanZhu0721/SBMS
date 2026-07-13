using System;
using System.Globalization;

namespace SBMSGui
{
    internal enum BridgeState
    {
        Idle,
        Starting,
        Running,
        Recovering,
        Stopping,
        Error
    }

    internal sealed class BridgeLifecycle
    {
        private long generation;

        public BridgeState State { get; private set; }
        public long Generation { get { return generation; } }
        public string LastError { get; private set; }

        public BridgeLifecycle()
        {
            State = BridgeState.Idle;
            LastError = "";
        }

        public long BeginStart()
        {
            if (State != BridgeState.Idle && State != BridgeState.Error)
            {
                throw new InvalidOperationException("Cannot start while bridge state is " + State + ".");
            }

            ++generation;
            State = BridgeState.Starting;
            LastError = "";
            return generation;
        }

        public bool MarkRunning(long expectedGeneration)
        {
            if (!IsCurrent(expectedGeneration) ||
                (State != BridgeState.Starting && State != BridgeState.Recovering))
            {
                return false;
            }

            State = BridgeState.Running;
            LastError = "";
            return true;
        }

        public bool BeginRecovery(long expectedGeneration)
        {
            if (!IsCurrent(expectedGeneration) || State != BridgeState.Running)
            {
                return false;
            }

            State = BridgeState.Recovering;
            return true;
        }

        public long BeginStop()
        {
            ++generation;
            State = BridgeState.Stopping;
            return generation;
        }

        public bool MarkIdle(long expectedGeneration)
        {
            if (!IsCurrent(expectedGeneration) || State == BridgeState.Starting || State == BridgeState.Running || State == BridgeState.Recovering)
            {
                return false;
            }

            State = BridgeState.Idle;
            LastError = "";
            return true;
        }

        public bool MarkError(long expectedGeneration, string message)
        {
            if (!IsCurrent(expectedGeneration))
            {
                return false;
            }

            State = BridgeState.Error;
            LastError = message ?? "";
            return true;
        }

        public bool IsCurrent(long expectedGeneration)
        {
            return expectedGeneration == generation;
        }

        public static string FormatTransition(BridgeState previousState, BridgeState currentState, long currentGeneration, string reason)
        {
            return "状态: " + previousState + " -> " + currentState +
                   " generation=" + currentGeneration.ToString(CultureInfo.InvariantCulture) +
                   (string.IsNullOrWhiteSpace(reason) ? "" : " // " + reason);
        }
    }
}
