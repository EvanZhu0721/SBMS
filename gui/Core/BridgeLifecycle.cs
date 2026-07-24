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
        private string pendingTerminalError;

        public BridgeState State { get; private set; }
        public long Generation { get { return generation; } }
        public string LastError { get; private set; }
        public string LastCleanupError { get; private set; }
        public bool CleanupPending { get; private set; }

        public BridgeLifecycle()
        {
            State = BridgeState.Idle;
            LastError = "";
            LastCleanupError = "";
            CleanupPending = false;
            pendingTerminalError = "";
        }

        public long BeginStart()
        {
            if (CleanupPending)
            {
                throw new InvalidOperationException("Cannot start while cleanup is pending.");
            }

            if (State == BridgeState.Starting ||
                State == BridgeState.Running ||
                State == BridgeState.Recovering)
            {
                return generation;
            }

            if (State == BridgeState.Stopping)
            {
                throw new InvalidOperationException("Cannot start while bridge state is " + State + ".");
            }

            ++generation;
            State = BridgeState.Starting;
            LastError = "";
            LastCleanupError = "";
            CleanupPending = false;
            pendingTerminalError = "";
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
            return BeginStop("");
        }

        public long BeginStop(string terminalError)
        {
            if (State == BridgeState.Idle)
            {
                return generation;
            }

            if (State == BridgeState.Error)
            {
                ++generation;
                State = BridgeState.Stopping;
                LastCleanupError = "";
                CleanupPending = true;
                pendingTerminalError = "";
                CaptureTerminalError(terminalError);
                return generation;
            }

            if (State == BridgeState.Stopping)
            {
                CaptureTerminalError(terminalError);
                return generation;
            }

            ++generation;
            State = BridgeState.Stopping;
            CleanupPending = true;
            CaptureTerminalError(terminalError);
            return generation;
        }

        public bool MarkIdle(long expectedGeneration)
        {
            return CompleteStop(expectedGeneration, "");
        }

        public bool CompleteStop(long expectedGeneration, string cleanupError)
        {
            return CompleteStop(expectedGeneration, cleanupError, false);
        }

        public bool CompleteStop(
            long expectedGeneration,
            string cleanupError,
            bool cleanupPending)
        {
            if (!IsCurrent(expectedGeneration))
            {
                return false;
            }

            if (State == BridgeState.Idle || State == BridgeState.Error)
            {
                return true;
            }

            if (State != BridgeState.Stopping)
            {
                return false;
            }

            LastCleanupError = cleanupError ?? "";
            CleanupPending = cleanupPending;
            if (!string.IsNullOrWhiteSpace(pendingTerminalError) ||
                !string.IsNullOrWhiteSpace(LastCleanupError) ||
                CleanupPending)
            {
                State = BridgeState.Error;
                LastError = !string.IsNullOrWhiteSpace(pendingTerminalError)
                    ? pendingTerminalError
                    : (!string.IsNullOrWhiteSpace(LastCleanupError)
                        ? LastCleanupError
                        : "Cleanup is pending.");
            }
            else
            {
                State = BridgeState.Idle;
                LastError = "";
                LastCleanupError = "";
            }
            pendingTerminalError = "";
            return true;
        }

        public bool MarkError(long expectedGeneration, string message)
        {
            if (!IsCurrent(expectedGeneration) || State != BridgeState.Stopping)
            {
                return false;
            }

            CaptureTerminalError(message);
            return CompleteStop(expectedGeneration, "", false);
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

        private void CaptureTerminalError(string message)
        {
            if (string.IsNullOrWhiteSpace(pendingTerminalError) &&
                !string.IsNullOrWhiteSpace(message))
            {
                pendingTerminalError = message;
            }
        }
    }
}
