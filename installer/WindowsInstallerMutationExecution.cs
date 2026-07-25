using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SBMSSetup
{
    internal enum WindowsMutationExecutionPhase
    {
        Forward,
        Compensation,
        FinalizeCommitted,
        FinalizeRolledBack
    }

    internal enum WindowsMutationExecutionOutcome
    {
        Success,
        NonZeroExit,
        Timeout
    }

    internal sealed class WindowsMutationOutputEvidence
    {
        private const int HashCharacterBufferSize = 1024;

        internal long OriginalByteLength { get; private set; }
        internal long HashedByteLength { get; private set; }
        internal string Sha256 { get; private set; }
        internal bool Truncated { get; private set; }

        private WindowsMutationOutputEvidence()
        {
        }

        internal static WindowsMutationOutputEvidence FromText(string value)
        {
            string text = value ?? String.Empty;
            char[] characters = new char[HashCharacterBufferSize];
            byte[] bytes = new byte[
                Encoding.UTF8.GetMaxByteCount(HashCharacterBufferSize)];
            Encoder encoder = Encoding.UTF8.GetEncoder();
            long byteLength = 0;
            byte[] digest;
            using (SHA256 algorithm = SHA256.Create())
            {
                int textOffset = 0;
                do
                {
                    int characterCount = Math.Min(
                        characters.Length,
                        text.Length - textOffset);
                    if (characterCount > 0)
                    {
                        text.CopyTo(
                            textOffset,
                            characters,
                            0,
                            characterCount);
                    }
                    bool flush = textOffset + characterCount >= text.Length;
                    int characterOffset = 0;
                    bool completed;
                    do
                    {
                        int charactersUsed;
                        int bytesUsed;
                        encoder.Convert(
                            characters,
                            characterOffset,
                            characterCount - characterOffset,
                            bytes,
                            0,
                            bytes.Length,
                            flush,
                            out charactersUsed,
                            out bytesUsed,
                            out completed);
                        if (bytesUsed > 0)
                        {
                            algorithm.TransformBlock(
                                bytes,
                                0,
                                bytesUsed,
                                bytes,
                                0);
                            byteLength += bytesUsed;
                        }
                        characterOffset += charactersUsed;
                    }
                    while (!completed);
                    textOffset += characterCount;
                }
                while (textOffset < text.Length);
                algorithm.TransformFinalBlock(new byte[0], 0, 0);
                digest = algorithm.Hash;
            }
            return new WindowsMutationOutputEvidence
            {
                OriginalByteLength = byteLength,
                HashedByteLength = byteLength,
                Sha256 = ToHex(digest),
                Truncated = false
            };
        }

        internal string ToEvidenceString(string name)
        {
            return name + "Length=" +
                OriginalByteLength.ToString(CultureInfo.InvariantCulture) +
                " " + name + "HashedLength=" +
                HashedByteLength.ToString(CultureInfo.InvariantCulture) +
                " " + name + "Sha256=" + Sha256 +
                " " + name + "Truncated=" +
                Truncated.ToString().ToLowerInvariant();
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(
                    value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }

    internal sealed class WindowsMutationStepDescriptor
    {
        internal const int MaximumTimeoutMilliseconds = 15 * 60 * 1000;

        internal string OperationId { get; private set; }
        internal string TransactionId { get; private set; }
        internal WindowsMutationExecutionPhase Phase { get; private set; }
        internal InstallerMutation? Mutation { get; private set; }
        internal InstallerCompensationAction? Compensation { get; private set; }
        internal int TimeoutMilliseconds { get; private set; }

        private WindowsMutationStepDescriptor()
        {
        }

        internal static WindowsMutationStepDescriptor Forward(
            string operationId,
            string transactionId,
            InstallerMutation mutation,
            int timeoutMilliseconds)
        {
            ValidateOperationId(operationId);
            ValidateTransactionId(transactionId);
            ValidateTimeout(timeoutMilliseconds);
            if (!Enum.IsDefined(typeof(InstallerMutation), mutation))
            {
                throw new ArgumentOutOfRangeException("mutation");
            }
            return new WindowsMutationStepDescriptor
            {
                OperationId = operationId,
                TransactionId = transactionId,
                Phase = WindowsMutationExecutionPhase.Forward,
                Mutation = mutation,
                Compensation = null,
                TimeoutMilliseconds = timeoutMilliseconds
            };
        }

        internal static WindowsMutationStepDescriptor CompensationStep(
            string operationId,
            string transactionId,
            InstallerCompensationAction compensation,
            int timeoutMilliseconds)
        {
            ValidateOperationId(operationId);
            ValidateTransactionId(transactionId);
            ValidateTimeout(timeoutMilliseconds);
            if (!Enum.IsDefined(
                    typeof(InstallerCompensationAction),
                    compensation))
            {
                throw new ArgumentOutOfRangeException("compensation");
            }
            return new WindowsMutationStepDescriptor
            {
                OperationId = operationId,
                TransactionId = transactionId,
                Phase = WindowsMutationExecutionPhase.Compensation,
                Mutation = null,
                Compensation = compensation,
                TimeoutMilliseconds = timeoutMilliseconds
            };
        }

        internal static WindowsMutationStepDescriptor Finalization(
            string operationId,
            string transactionId,
            bool rolledBack,
            int timeoutMilliseconds)
        {
            ValidateOperationId(operationId);
            ValidateTransactionId(transactionId);
            ValidateTimeout(timeoutMilliseconds);
            return new WindowsMutationStepDescriptor
            {
                OperationId = operationId,
                TransactionId = transactionId,
                Phase = rolledBack
                    ? WindowsMutationExecutionPhase.FinalizeRolledBack
                    : WindowsMutationExecutionPhase.FinalizeCommitted,
                Mutation = null,
                Compensation = null,
                TimeoutMilliseconds = timeoutMilliseconds
            };
        }

        internal string StepName
        {
            get
            {
                if (Mutation.HasValue)
                {
                    return Mutation.Value.ToString();
                }
                if (Compensation.HasValue)
                {
                    return Compensation.Value.ToString();
                }
                return Phase.ToString();
            }
        }

        private static void ValidateOperationId(string operationId)
        {
            if (String.IsNullOrWhiteSpace(operationId) ||
                operationId.Length > 128)
            {
                throw new ArgumentException(
                    "A bounded operation ID is required.",
                    "operationId");
            }
            foreach (char value in operationId)
            {
                if (!(Char.IsLetterOrDigit(value) ||
                      value == '.' ||
                      value == '_' ||
                      value == ':' ||
                      value == '-'))
                {
                    throw new ArgumentException(
                        "Operation ID contains an unsafe character.",
                        "operationId");
                }
            }
        }

        private static void ValidateTransactionId(string transactionId)
        {
            Guid parsed;
            if (String.IsNullOrWhiteSpace(transactionId) ||
                !Guid.TryParseExact(transactionId, "N", out parsed))
            {
                throw new ArgumentException(
                    "Transaction ID must be a canonical N-format GUID.",
                    "transactionId");
            }
        }

        private static void ValidateTimeout(int timeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0 ||
                timeoutMilliseconds > MaximumTimeoutMilliseconds)
            {
                throw new ArgumentOutOfRangeException(
                    "timeoutMilliseconds");
            }
        }
    }

    internal sealed class WindowsMutationStepResult
    {
        internal WindowsMutationExecutionOutcome Outcome { get; private set; }
        internal int? ExitCode { get; private set; }
        internal long ElapsedMilliseconds { get; private set; }
        internal WindowsMutationOutputEvidence StandardOutput { get; private set; }
        internal WindowsMutationOutputEvidence StandardError { get; private set; }

        internal bool StateMayHaveChanged
        {
            get { return true; }
        }

        internal bool TimedOut
        {
            get { return Outcome == WindowsMutationExecutionOutcome.Timeout; }
        }

        private WindowsMutationStepResult()
        {
        }

        internal static WindowsMutationStepResult Success(
            long elapsedMilliseconds,
            WindowsMutationOutputEvidence standardOutput,
            WindowsMutationOutputEvidence standardError)
        {
            return Create(
                WindowsMutationExecutionOutcome.Success,
                0,
                elapsedMilliseconds,
                standardOutput,
                standardError);
        }

        internal static WindowsMutationStepResult NonZero(
            int exitCode,
            long elapsedMilliseconds,
            WindowsMutationOutputEvidence standardOutput,
            WindowsMutationOutputEvidence standardError)
        {
            if (exitCode == 0)
            {
                throw new ArgumentOutOfRangeException("exitCode");
            }
            return Create(
                WindowsMutationExecutionOutcome.NonZeroExit,
                exitCode,
                elapsedMilliseconds,
                standardOutput,
                standardError);
        }

        internal static WindowsMutationStepResult Timeout(
            long elapsedMilliseconds,
            WindowsMutationOutputEvidence standardOutput,
            WindowsMutationOutputEvidence standardError)
        {
            return Create(
                WindowsMutationExecutionOutcome.Timeout,
                null,
                elapsedMilliseconds,
                standardOutput,
                standardError);
        }

        internal string ToEvidenceString(WindowsMutationStepDescriptor step)
        {
            return "operationId=" + step.OperationId +
                " transactionId=" + step.TransactionId +
                " phase=" + step.Phase +
                " step=" + step.StepName +
                " outcome=" + Outcome +
                " exit=" + (ExitCode.HasValue
                    ? ExitCode.Value.ToString(CultureInfo.InvariantCulture)
                    : "none") +
                " timeout=" + TimedOut.ToString().ToLowerInvariant() +
                " elapsedMs=" +
                ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                " stateMayHaveChanged=true " +
                StandardOutput.ToEvidenceString("stdout") + " " +
                StandardError.ToEvidenceString("stderr");
        }

        private static WindowsMutationStepResult Create(
            WindowsMutationExecutionOutcome outcome,
            int? exitCode,
            long elapsedMilliseconds,
            WindowsMutationOutputEvidence standardOutput,
            WindowsMutationOutputEvidence standardError)
        {
            if (elapsedMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    "elapsedMilliseconds");
            }
            if (standardOutput == null)
            {
                throw new ArgumentNullException("standardOutput");
            }
            if (standardError == null)
            {
                throw new ArgumentNullException("standardError");
            }
            return new WindowsMutationStepResult
            {
                Outcome = outcome,
                ExitCode = exitCode,
                ElapsedMilliseconds = elapsedMilliseconds,
                StandardOutput = standardOutput,
                StandardError = standardError
            };
        }
    }

    internal interface IWindowsMutationStepRunner
    {
        WindowsMutationStepResult Run(WindowsMutationStepDescriptor step);
    }

    internal sealed class WindowsMutationExecutionException : Exception
    {
        internal WindowsMutationStepDescriptor Step { get; private set; }
        internal WindowsMutationStepResult Result { get; private set; }
        internal string RunnerFailureType { get; private set; }
        internal int? RunnerFailureHResult { get; private set; }
        internal string RunnerFailureDigest { get; private set; }
        internal bool StateMayHaveChanged { get { return true; } }

        internal WindowsMutationExecutionException(
            WindowsMutationStepDescriptor step,
            WindowsMutationStepResult result)
            : base(BuildResultMessage(step, result))
        {
            Step = step;
            Result = result;
            RunnerFailureType = null;
            RunnerFailureHResult = null;
            RunnerFailureDigest = null;
        }

        internal WindowsMutationExecutionException(
            WindowsMutationStepDescriptor step,
            Exception runnerFailure)
            : base(BuildRunnerFailureMessage(step, runnerFailure))
        {
            Step = step;
            Result = null;
            RunnerFailureType = runnerFailure.GetType().FullName;
            RunnerFailureHResult = runnerFailure.HResult;
            RunnerFailureDigest =
                WindowsMutationOutputEvidence.FromText(
                    runnerFailure.ToString()).Sha256;
        }

        private static string BuildResultMessage(
            WindowsMutationStepDescriptor step,
            WindowsMutationStepResult result)
        {
            if (step == null)
            {
                throw new ArgumentNullException("step");
            }
            if (result == null)
            {
                throw new ArgumentNullException("result");
            }
            return "Windows mutation command did not complete successfully: " +
                result.ToEvidenceString(step);
        }

        private static string BuildRunnerFailureMessage(
            WindowsMutationStepDescriptor step,
            Exception runnerFailure)
        {
            if (step == null)
            {
                throw new ArgumentNullException("step");
            }
            if (runnerFailure == null)
            {
                throw new ArgumentNullException("runnerFailure");
            }
            WindowsMutationOutputEvidence failureEvidence =
                WindowsMutationOutputEvidence.FromText(
                    runnerFailure.ToString());
            WindowsMutationOutputEvidence empty =
                WindowsMutationOutputEvidence.FromText(String.Empty);
            return "Windows mutation runner failed with indeterminate state: " +
                "operationId=" + step.OperationId +
                " transactionId=" + step.TransactionId +
                " phase=" + step.Phase +
                " step=" + step.StepName +
                " outcome=RunnerException exit=unknown timeout=unknown " +
                "elapsedMs=unknown stateMayHaveChanged=true " +
                "runnerFailureType=" +
                runnerFailure.GetType().FullName +
                " runnerFailureHResult=" +
                runnerFailure.HResult.ToString(
                    CultureInfo.InvariantCulture) + " " +
                empty.ToEvidenceString("stdout") + " " +
                empty.ToEvidenceString("stderr") + " " +
                failureEvidence.ToEvidenceString("runnerFailure");
        }
    }

    internal sealed class WindowsInstallerMutationExecutionContract
    {
        private readonly IWindowsMutationStepRunner runner;

        internal WindowsInstallerMutationExecutionContract(
            IWindowsMutationStepRunner runner)
        {
            if (runner == null)
            {
                throw new ArgumentNullException("runner");
            }
            this.runner = runner;
        }

        internal WindowsMutationStepResult Apply(
            InstallerMutation mutation,
            WindowsMutationStepDescriptor step)
        {
            RequireStep(step, WindowsMutationExecutionPhase.Forward);
            if (!step.Mutation.HasValue || step.Mutation.Value != mutation)
            {
                throw new InvalidOperationException(
                    "Forward descriptor does not match the mutation.");
            }
            return Execute(step);
        }

        internal WindowsMutationStepResult ApplyCompensation(
            InstallerCompensationAction action,
            WindowsMutationStepDescriptor step)
        {
            RequireStep(step, WindowsMutationExecutionPhase.Compensation);
            if (!step.Compensation.HasValue ||
                step.Compensation.Value != action)
            {
                throw new InvalidOperationException(
                    "Compensation descriptor does not match the action.");
            }
            return Execute(step);
        }

        internal WindowsMutationStepResult FinalizeCommitted(
            WindowsMutationStepDescriptor step)
        {
            RequireStep(
                step,
                WindowsMutationExecutionPhase.FinalizeCommitted);
            return Execute(step);
        }

        internal WindowsMutationStepResult FinalizeRolledBack(
            WindowsMutationStepDescriptor step)
        {
            RequireStep(
                step,
                WindowsMutationExecutionPhase.FinalizeRolledBack);
            return Execute(step);
        }

        private WindowsMutationStepResult Execute(
            WindowsMutationStepDescriptor step)
        {
            WindowsMutationStepResult result;
            try
            {
                result = runner.Run(step);
            }
            catch (Exception failure)
            {
                throw new WindowsMutationExecutionException(step, failure);
            }
            if (result == null)
            {
                throw new WindowsMutationExecutionException(
                    step,
                    new InvalidOperationException(
                        "Mutation runner returned no result."));
            }
            if (result.Outcome != WindowsMutationExecutionOutcome.Success)
            {
                throw new WindowsMutationExecutionException(step, result);
            }
            // Command success is transport evidence only. The transaction
            // engine must still call Inspect() and VerifyApplied().
            return result;
        }

        private static void RequireStep(
            WindowsMutationStepDescriptor step,
            WindowsMutationExecutionPhase expected)
        {
            if (step == null)
            {
                throw new ArgumentNullException("step");
            }
            if (step.Phase != expected)
            {
                throw new InvalidOperationException(
                    "Mutation descriptor phase mismatch.");
            }
        }
    }
}
