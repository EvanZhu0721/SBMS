using System;
using System.Collections.Generic;
using System.Text;

namespace SBMSSetup
{
    internal static class WindowsInstallerMutationExecutionTests
    {
        private const string TransactionId =
            "11111111111111111111111111111111";

        private sealed class OutcomeCase
        {
            internal string Name;
            internal WindowsMutationExecutionOutcome Outcome;
            internal bool PartialEffect;
        }

        private sealed class ScriptedRunner : IWindowsMutationStepRunner
        {
            internal OutcomeCase Scenario;
            internal int Calls;
            internal bool EffectObserved;
            internal Exception Failure;

            public WindowsMutationStepResult Run(
                WindowsMutationStepDescriptor step)
            {
                Calls++;
                if (Failure != null)
                {
                    throw Failure;
                }
                if (Scenario.PartialEffect)
                {
                    EffectObserved = true;
                }
                WindowsMutationOutputEvidence stdout =
                    WindowsMutationOutputEvidence.FromText(
                        "stdout-secret-" + step.OperationId);
                WindowsMutationOutputEvidence stderr =
                    WindowsMutationOutputEvidence.FromText(
                        "stderr-secret-" + step.OperationId);
                if (Scenario.Outcome ==
                    WindowsMutationExecutionOutcome.Success)
                {
                    return WindowsMutationStepResult.Success(
                        17,
                        stdout,
                        stderr);
                }
                if (Scenario.Outcome ==
                    WindowsMutationExecutionOutcome.NonZeroExit)
                {
                    return WindowsMutationStepResult.NonZero(
                        23,
                        31,
                        stdout,
                        stderr);
                }
                return WindowsMutationStepResult.Timeout(
                    101,
                    stdout,
                    stderr);
            }
        }

        private static int passed;
        private static int failed;

        private static void Main()
        {
            Run("descriptor matrices remain exhaustive", TestDescriptorMatrices);
            Run("all forward mutations preserve indeterminate failure semantics",
                TestForwardMatrix);
            Run("all compensations preserve indeterminate failure semantics",
                TestCompensationMatrix);
            Run("both finalizers preserve indeterminate failure semantics",
                TestFinalizationMatrix);
            Run("runner exceptions persist bounded evidence without raw output",
                TestRunnerExceptionEvidence);
            Run("persistent output evidence is bounded and hashes all text",
                TestBoundedOutputEvidence);
            Run("descriptor identity and timeout bounds fail before dispatch",
                TestDescriptorBounds);
            Run("descriptor mismatch fails before native dispatch",
                TestDescriptorMismatch);

            Console.WriteLine(
                "Windows mutation execution contract: " + passed +
                " passed, " + failed + " failed");
            Environment.ExitCode = failed == 0 ? 0 : 1;
        }

        private static void TestDescriptorMatrices()
        {
            InstallerMutation[] mutations =
                (InstallerMutation[])Enum.GetValues(
                    typeof(InstallerMutation));
            InstallerCompensationAction[] compensations =
                (InstallerCompensationAction[])Enum.GetValues(
                    typeof(InstallerCompensationAction));
            Assert(mutations.Length == 11,
                "Forward matrix must cover exactly 11 mutations.");
            Assert(compensations.Length == 9,
                "Compensation matrix must cover exactly 9 actions.");

            var operationIds = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (InstallerMutation mutation in mutations)
            {
                WindowsMutationStepDescriptor step =
                    WindowsMutationStepDescriptor.Forward(
                        "forward:" + mutation,
                        TransactionId,
                        mutation,
                        5000);
                Assert(step.Phase == WindowsMutationExecutionPhase.Forward &&
                       step.Mutation == mutation &&
                       !step.Compensation.HasValue,
                    "Forward descriptor lost structured identity.");
                Assert(operationIds.Add(step.OperationId),
                    "Forward operation ID was not unique.");
            }
            foreach (InstallerCompensationAction action in compensations)
            {
                WindowsMutationStepDescriptor step =
                    WindowsMutationStepDescriptor.CompensationStep(
                        "compensation:" + action,
                        TransactionId,
                        action,
                        5000);
                Assert(
                    step.Phase ==
                        WindowsMutationExecutionPhase.Compensation &&
                    step.Compensation == action &&
                    !step.Mutation.HasValue,
                    "Compensation descriptor lost structured identity.");
                Assert(operationIds.Add(step.OperationId),
                    "Compensation operation ID was not unique.");
            }
        }

        private static void TestForwardMatrix()
        {
            foreach (InstallerMutation mutation in
                (InstallerMutation[])Enum.GetValues(
                    typeof(InstallerMutation)))
            {
                foreach (OutcomeCase scenario in Scenarios())
                {
                    var runner = new ScriptedRunner
                    {
                        Scenario = scenario
                    };
                    var contract =
                        new WindowsInstallerMutationExecutionContract(
                            runner);
                    WindowsMutationStepDescriptor step =
                        WindowsMutationStepDescriptor.Forward(
                            "forward:" + mutation + ":" + scenario.Name,
                            TransactionId,
                            mutation,
                            5000);
                    AssertOutcome(
                        delegate { return contract.Apply(mutation, step); },
                        runner,
                        scenario,
                        step);
                }
            }
        }

        private static void TestCompensationMatrix()
        {
            foreach (InstallerCompensationAction action in
                (InstallerCompensationAction[])Enum.GetValues(
                    typeof(InstallerCompensationAction)))
            {
                foreach (OutcomeCase scenario in Scenarios())
                {
                    var runner = new ScriptedRunner
                    {
                        Scenario = scenario
                    };
                    var contract =
                        new WindowsInstallerMutationExecutionContract(
                            runner);
                    WindowsMutationStepDescriptor step =
                        WindowsMutationStepDescriptor.CompensationStep(
                            "compensation:" + action + ":" + scenario.Name,
                            TransactionId,
                            action,
                            5000);
                    AssertOutcome(
                        delegate
                        {
                            return contract.ApplyCompensation(action, step);
                        },
                        runner,
                        scenario,
                        step);
                }
            }
        }

        private static void TestFinalizationMatrix()
        {
            foreach (bool rolledBack in new[] { false, true })
            {
                foreach (OutcomeCase scenario in Scenarios())
                {
                    var runner = new ScriptedRunner
                    {
                        Scenario = scenario
                    };
                    var contract =
                        new WindowsInstallerMutationExecutionContract(
                            runner);
                    WindowsMutationStepDescriptor step =
                        WindowsMutationStepDescriptor.Finalization(
                            (rolledBack
                                ? "finalize-rollback:"
                                : "finalize-commit:") + scenario.Name,
                            TransactionId,
                            rolledBack,
                            5000);
                    AssertOutcome(
                        delegate
                        {
                            return rolledBack
                                ? contract.FinalizeRolledBack(step)
                                : contract.FinalizeCommitted(step);
                        },
                        runner,
                        scenario,
                        step);
                }
            }
        }

        private static void TestRunnerExceptionEvidence()
        {
            const string raw = "RAW-COMMAND-OUTPUT-MUST-NOT-PERSIST";
            var runner = new ScriptedRunner
            {
                Scenario = Scenarios()[0],
                Failure = new InvalidOperationException(raw)
            };
            var contract =
                new WindowsInstallerMutationExecutionContract(runner);
            WindowsMutationStepDescriptor step =
                WindowsMutationStepDescriptor.Forward(
                    "forward:runner-exception",
                    TransactionId,
                    InstallerMutation.CreateEscrow,
                    5000);
            WindowsMutationExecutionException failure =
                AssertExecutionThrows(
                    delegate
                    {
                        return contract.Apply(
                            InstallerMutation.CreateEscrow,
                            step);
                    });
            string persisted = failure.ToString();
            Assert(failure.StateMayHaveChanged &&
                   failure.Result == null &&
                   failure.RunnerFailureType ==
                       typeof(InvalidOperationException).FullName &&
                   failure.RunnerFailureHResult.HasValue &&
                   failure.RunnerFailureDigest.Length == 64,
                "Runner exception guessed that state was unchanged.");
            Assert(persisted.IndexOf(raw, StringComparison.Ordinal) < 0,
                "Typed exception persisted raw runner output.");
            AssertContainsEvidence(persisted, step, "RunnerException");
            Assert(persisted.IndexOf("runnerFailureLength=",
                       StringComparison.Ordinal) >= 0 &&
                   persisted.IndexOf("runnerFailureSha256=",
                       StringComparison.Ordinal) >= 0,
                "Runner exception lost bounded failure evidence.");
        }

        private static void TestBoundedOutputEvidence()
        {
            string sharedPrefix = new String('x', 4096);
            string raw = sharedPrefix + "-first-tail";
            string differentTail = sharedPrefix + "-other-tail";
            WindowsMutationOutputEvidence evidence =
                WindowsMutationOutputEvidence.FromText(raw);
            WindowsMutationOutputEvidence differentEvidence =
                WindowsMutationOutputEvidence.FromText(differentTail);
            Assert(
                evidence.OriginalByteLength ==
                    Encoding.UTF8.GetByteCount(raw),
                "Output evidence lost original byte length.");
            Assert(
                evidence.HashedByteLength ==
                    evidence.OriginalByteLength &&
                !evidence.Truncated &&
                evidence.Sha256.Length == 64,
                "Output evidence did not hash the complete output.");
            Assert(
                !String.Equals(
                    evidence.Sha256,
                    differentEvidence.Sha256,
                    StringComparison.Ordinal),
                "Output evidence ignored content after a shared prefix.");
            Assert(
                evidence.ToEvidenceString("stdout").IndexOf(
                    raw,
                    StringComparison.Ordinal) < 0,
                "Output evidence retained raw command output.");
        }

        private static void TestDescriptorBounds()
        {
            AssertAnyThrows(
                delegate
                {
                    WindowsMutationStepDescriptor.Forward(
                        "forward:invalid-transaction",
                        "not-a-transaction-id",
                        InstallerMutation.CreateEscrow,
                        5000);
                },
                "Unsafe transaction identity was accepted.");
            AssertAnyThrows(
                delegate
                {
                    WindowsMutationStepDescriptor.Forward(
                        "forward:unbounded-timeout",
                        TransactionId,
                        InstallerMutation.CreateEscrow,
                        WindowsMutationStepDescriptor
                            .MaximumTimeoutMilliseconds + 1);
                },
                "Unbounded native timeout was accepted.");
        }

        private static void TestDescriptorMismatch()
        {
            var runner = new ScriptedRunner
            {
                Scenario = Scenarios()[0]
            };
            var contract =
                new WindowsInstallerMutationExecutionContract(runner);
            WindowsMutationStepDescriptor step =
                WindowsMutationStepDescriptor.Forward(
                    "forward:mismatch",
                    TransactionId,
                    InstallerMutation.StagePayload,
                    5000);
            AssertAnyThrows(
                delegate
                {
                    contract.Apply(InstallerMutation.StageDriver, step);
                },
                "Mutation mismatch reached native dispatch.");
            AssertAnyThrows(
                delegate
                {
                    contract.FinalizeCommitted(step);
                },
                "Phase mismatch reached native dispatch.");
            Assert(runner.Calls == 0,
                "Invalid descriptor reached native runner.");
        }

        private static OutcomeCase[] Scenarios()
        {
            return new[]
            {
                new OutcomeCase
                {
                    Name = "success",
                    Outcome = WindowsMutationExecutionOutcome.Success
                },
                new OutcomeCase
                {
                    Name = "nonzero",
                    Outcome = WindowsMutationExecutionOutcome.NonZeroExit
                },
                new OutcomeCase
                {
                    Name = "timeout",
                    Outcome = WindowsMutationExecutionOutcome.Timeout
                },
                new OutcomeCase
                {
                    Name = "partial-nonzero",
                    Outcome = WindowsMutationExecutionOutcome.NonZeroExit,
                    PartialEffect = true
                },
                new OutcomeCase
                {
                    Name = "partial-timeout",
                    Outcome = WindowsMutationExecutionOutcome.Timeout,
                    PartialEffect = true
                }
            };
        }

        private static void AssertOutcome(
            Func<WindowsMutationStepResult> action,
            ScriptedRunner runner,
            OutcomeCase scenario,
            WindowsMutationStepDescriptor step)
        {
            if (scenario.Outcome ==
                WindowsMutationExecutionOutcome.Success)
            {
                WindowsMutationStepResult result = action();
                Assert(result.Outcome == scenario.Outcome &&
                       result.StateMayHaveChanged &&
                       !result.TimedOut,
                    "Success result guessed authoritative machine state.");
            }
            else
            {
                WindowsMutationExecutionException failure =
                    AssertExecutionThrows(action);
                Assert(failure.Result != null &&
                       failure.Result.Outcome == scenario.Outcome &&
                       failure.StateMayHaveChanged &&
                       failure.Result.StateMayHaveChanged,
                    "Failure result guessed that machine state was unchanged.");
                AssertContainsEvidence(
                    failure.Message,
                    step,
                    scenario.Outcome.ToString());
                Assert(
                    failure.Message.IndexOf(
                        "stdout-secret-",
                        StringComparison.Ordinal) < 0 &&
                    failure.Message.IndexOf(
                        "stderr-secret-",
                        StringComparison.Ordinal) < 0,
                    "Typed exception persisted raw command output.");
            }
            Assert(runner.Calls == 1,
                "Orchestrator dispatched a step more than once.");
            Assert(runner.EffectObserved == scenario.PartialEffect,
                "Partial-effect fixture did not execute as declared.");
        }

        private static void AssertContainsEvidence(
            string value,
            WindowsMutationStepDescriptor step,
            string outcome)
        {
            foreach (string token in new[]
            {
                "operationId=" + step.OperationId,
                "transactionId=" + step.TransactionId,
                "outcome=" + outcome,
                "exit=",
                "timeout=",
                "elapsedMs=",
                "stateMayHaveChanged=true",
                "stdoutLength=",
                "stdoutSha256=",
                "stderrLength=",
                "stderrSha256="
            })
            {
                Assert(value.IndexOf(token, StringComparison.Ordinal) >= 0,
                    "Persistent exception evidence omitted " + token + ".");
            }
        }

        private static WindowsMutationExecutionException
            AssertExecutionThrows(Func<WindowsMutationStepResult> action)
        {
            try
            {
                action();
            }
            catch (WindowsMutationExecutionException failure)
            {
                return failure;
            }
            throw new InvalidOperationException(
                "Expected typed execution failure.");
        }

        private static void AssertAnyThrows(
            Action action,
            string message)
        {
            try
            {
                action();
            }
            catch
            {
                return;
            }
            throw new InvalidOperationException(message);
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception failure)
            {
                failed++;
                Console.Error.WriteLine(
                    "FAIL " + name + ": " + failure);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
