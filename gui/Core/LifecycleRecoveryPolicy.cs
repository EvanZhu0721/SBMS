using System;
using System.Collections.Generic;
using System.Globalization;

namespace SBMSGui
{
    internal sealed class LifecycleRecoveryDecision
    {
        public bool ShouldRetry { get; private set; }
        public int Attempt { get; private set; }
        public int MaximumAttempts { get; private set; }
        public TimeSpan Delay { get; private set; }
        public string FirstFailure { get; private set; }
        public string LastFailure { get; private set; }
        public string TerminalFailure { get; private set; }

        public LifecycleRecoveryDecision(
            bool shouldRetry,
            int attempt,
            int maximumAttempts,
            TimeSpan delay,
            string firstFailure,
            string lastFailure,
            string terminalFailure)
        {
            ShouldRetry = shouldRetry;
            Attempt = attempt;
            MaximumAttempts = maximumAttempts;
            Delay = delay;
            FirstFailure = firstFailure ?? "";
            LastFailure = lastFailure ?? "";
            TerminalFailure = terminalFailure ?? "";
        }

        public string FormatLog()
        {
            return "recovery attempt=" + Attempt.ToString(CultureInfo.InvariantCulture) +
                   "/" + MaximumAttempts.ToString(CultureInfo.InvariantCulture) +
                   " backoffMs=" + ((long)Delay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) +
                   (string.IsNullOrWhiteSpace(LastFailure) ? "" : " failure=" + LastFailure) +
                   (string.IsNullOrWhiteSpace(TerminalFailure) ? "" : " terminal=" + TerminalFailure);
        }
    }

    internal sealed class LifecycleRecoveryPolicy
    {
        private sealed class FailureRecord
        {
            public DateTimeOffset Timestamp;
            public string Message;
        }

        private readonly int maximumAttempts;
        private readonly TimeSpan firstDelay;
        private readonly TimeSpan maximumDelay;
        private readonly TimeSpan failureWindow;
        private readonly List<FailureRecord> failures = new List<FailureRecord>();
        private long generation = -1;
        private DateTimeOffset episodeStartedAt;
        private DateTimeOffset lastSuccessfulRecovery;
        private bool recoveredSinceLastFailure;

        public LifecycleRecoveryPolicy(
            int maximumAttempts,
            TimeSpan firstDelay,
            TimeSpan maximumDelay,
            TimeSpan failureWindow)
        {
            if (maximumAttempts < 1)
            {
                throw new ArgumentOutOfRangeException("maximumAttempts");
            }
            if (firstDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException("firstDelay");
            }
            if (maximumDelay < firstDelay)
            {
                throw new ArgumentOutOfRangeException("maximumDelay");
            }
            if (failureWindow <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException("failureWindow");
            }

            this.maximumAttempts = maximumAttempts;
            this.firstDelay = firstDelay;
            this.maximumDelay = maximumDelay;
            this.failureWindow = failureWindow;
        }

        public void Reset(long currentGeneration)
        {
            generation = currentGeneration;
            failures.Clear();
            episodeStartedAt = DateTimeOffset.MinValue;
            lastSuccessfulRecovery = DateTimeOffset.MinValue;
            recoveredSinceLastFailure = false;
        }

        public void MarkRecoverySucceeded(long currentGeneration, DateTimeOffset timestamp)
        {
            if (generation != currentGeneration || failures.Count == 0)
            {
                return;
            }
            recoveredSinceLastFailure = true;
            lastSuccessfulRecovery = timestamp;
        }

        public LifecycleRecoveryDecision RegisterFailure(
            long currentGeneration,
            DateTimeOffset timestamp,
            string failure)
        {
            if (generation != currentGeneration)
            {
                Reset(currentGeneration);
            }

            if (recoveredSinceLastFailure &&
                timestamp.Subtract(lastSuccessfulRecovery) >= failureWindow)
            {
                failures.Clear();
                episodeStartedAt = DateTimeOffset.MinValue;
                recoveredSinceLastFailure = false;
            }

            string normalizedFailure = string.IsNullOrWhiteSpace(failure)
                ? "unspecified recovery failure"
                : failure.Trim();
            if (failures.Count == 0)
            {
                episodeStartedAt = timestamp;
            }
            failures.Add(new FailureRecord
            {
                Timestamp = timestamp,
                Message = normalizedFailure
            });
            recoveredSinceLastFailure = false;

            int attempt = failures.Count;
            string firstFailure = failures[0].Message;
            bool deadlineExpired = timestamp.Subtract(episodeStartedAt) >= failureWindow;
            bool shouldRetry = attempt <= maximumAttempts && !deadlineExpired;
            TimeSpan delay = shouldRetry ? CalculateDelay(attempt) : TimeSpan.Zero;
            string terminalFailure = shouldRetry
                ? ""
                : (deadlineExpired ? "recovery deadline exhausted" : "recovery attempt budget exhausted") +
                  "; first=" + firstFailure + "; last=" + normalizedFailure;

            return new LifecycleRecoveryDecision(
                shouldRetry,
                attempt,
                maximumAttempts,
                delay,
                firstFailure,
                normalizedFailure,
                terminalFailure);
        }

        private TimeSpan CalculateDelay(int attempt)
        {
            double multiplier = Math.Pow(2.0, Math.Max(0, attempt - 1));
            double milliseconds = Math.Min(
                maximumDelay.TotalMilliseconds,
                firstDelay.TotalMilliseconds * multiplier);
            return TimeSpan.FromMilliseconds(milliseconds);
        }
    }
}
