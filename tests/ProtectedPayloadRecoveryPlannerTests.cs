using System;
using System.Collections.Generic;

namespace SBMSSetup
{
    internal static class ProtectedPayloadRecoveryPlannerTests
    {
        private const string TransactionId =
            "11111111111111111111111111111111";
        private const string OtherTransactionId =
            "22222222222222222222222222222222";
        private const string HashA =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string HashB =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string HashC =
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        private const string HashD =
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        private static int passed;

        private static int Main()
        {
            Run("fresh install forward converges after publication crash", delegate
            {
                PayloadDirectoryCheckpoint candidate =
                    Target(PayloadDirectorySlot.Candidate);
                PayloadRecoveryAuthority authority =
                    Authority(InstallOperation.FreshInstall, null, candidate);
                AssertRecoveryConverges(
                    authority,
                    PayloadRecoveryDecision.CompleteForward,
                    State(null, candidate, null),
                    PayloadNamespaceShape.CurrentOnly);
            });
            Run("fresh install rollback converges before publication", delegate
            {
                PayloadDirectoryCheckpoint candidate =
                    Target(PayloadDirectorySlot.Candidate);
                AssertRecoveryConverges(
                    Authority(InstallOperation.FreshInstall, null, candidate),
                    PayloadRecoveryDecision.RestoreBaseline,
                    State(null, candidate, null),
                    PayloadNamespaceShape.Empty);
            });
            Run("fresh install rollback converges after publication", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Target(PayloadDirectorySlot.Current);
                AssertRecoveryConverges(
                    Authority(InstallOperation.FreshInstall, null, current),
                    PayloadRecoveryDecision.RestoreBaseline,
                    State(current, null, null),
                    PayloadNamespaceShape.Empty);
            });

            foreach (InstallOperation operation in new[]
            {
                InstallOperation.Upgrade,
                InstallOperation.Repair,
                InstallOperation.ExplicitDowngrade
            })
            {
                InstallOperation captured = operation;
                Run(captured + " forward converges across both rename crashes", delegate
                {
                    PayloadDirectoryCheckpoint current =
                        Baseline(PayloadDirectorySlot.Current);
                    PayloadDirectoryCheckpoint candidate =
                        Target(PayloadDirectorySlot.Candidate);
                    AssertRecoveryConverges(
                        Authority(captured, current, candidate),
                        PayloadRecoveryDecision.CompleteForward,
                        State(current, candidate, null),
                        PayloadNamespaceShape.CurrentAndBackup);
                });
                Run(captured + " rollback converges before first rename", delegate
                {
                    PayloadDirectoryCheckpoint current =
                        Baseline(PayloadDirectorySlot.Current);
                    PayloadDirectoryCheckpoint candidate =
                        Target(PayloadDirectorySlot.Candidate);
                    AssertRecoveryConverges(
                        Authority(captured, current, candidate),
                        PayloadRecoveryDecision.RestoreBaseline,
                        State(current, candidate, null),
                        PayloadNamespaceShape.CurrentOnly);
                });
                Run(captured + " rollback converges after first rename", delegate
                {
                    PayloadDirectoryCheckpoint backup =
                        Baseline(PayloadDirectorySlot.Backup);
                    PayloadDirectoryCheckpoint candidate =
                        Target(PayloadDirectorySlot.Candidate);
                    AssertRecoveryConverges(
                        Authority(captured, backup, candidate),
                        PayloadRecoveryDecision.RestoreBaseline,
                        State(null, candidate, backup),
                        PayloadNamespaceShape.CurrentOnly);
                });
                Run(captured + " rollback converges after second rename", delegate
                {
                    PayloadDirectoryCheckpoint backup =
                        Baseline(PayloadDirectorySlot.Backup);
                    PayloadDirectoryCheckpoint current =
                        Target(PayloadDirectorySlot.Current);
                    AssertRecoveryConverges(
                        Authority(captured, backup, current),
                        PayloadRecoveryDecision.RestoreBaseline,
                        State(current, null, backup),
                        PayloadNamespaceShape.CurrentOnly);
                });
                Run(captured + " rollback reenters from backup-only crash", delegate
                {
                    PayloadDirectoryCheckpoint backup =
                        Baseline(PayloadDirectorySlot.Backup);
                    AssertRecoveryConverges(
                        Authority(captured, backup, Target(
                            PayloadDirectorySlot.Candidate)),
                        PayloadRecoveryDecision.RestoreBaseline,
                        State(null, null, backup),
                        PayloadNamespaceShape.CurrentOnly);
                });
            }

            Run("uninstall forward retains recoverable backup", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Baseline(PayloadDirectorySlot.Current);
                AssertRecoveryConverges(
                    Authority(InstallOperation.Uninstall, current, null),
                    PayloadRecoveryDecision.CompleteForward,
                    State(current, null, null),
                    PayloadNamespaceShape.BackupOnly);
            });
            Run("uninstall rollback restores renamed baseline", delegate
            {
                PayloadDirectoryCheckpoint backup =
                    Baseline(PayloadDirectorySlot.Backup);
                AssertRecoveryConverges(
                    Authority(InstallOperation.Uninstall, backup, null),
                    PayloadRecoveryDecision.RestoreBaseline,
                    State(null, null, backup),
                    PayloadNamespaceShape.CurrentOnly);
            });
            Run("candidate cleanup is crash-idempotent", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Baseline(PayloadDirectorySlot.Current);
                PayloadDirectoryCheckpoint candidate =
                    Target(PayloadDirectorySlot.Candidate);
                PayloadRecoveryAuthority authority =
                    Authority(InstallOperation.Upgrade, current, candidate);
                AssertCleanupConverges(
                    authority,
                    PayloadCleanupKind.Candidate,
                    State(current, candidate, null),
                    PayloadNamespaceShape.CurrentOnly);
            });
            Run("committed backup cleanup is crash-idempotent", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Target(PayloadDirectorySlot.Current);
                PayloadDirectoryCheckpoint backup =
                    Baseline(PayloadDirectorySlot.Backup);
                PayloadRecoveryAuthority authority =
                    Authority(InstallOperation.Upgrade, backup, current);
                AssertCleanupConverges(
                    authority,
                    PayloadCleanupKind.CommittedBackup,
                    State(current, null, backup),
                    PayloadNamespaceShape.CurrentOnly);
            });
            Run("uninstall backup cleanup converges to empty", delegate
            {
                PayloadDirectoryCheckpoint backup =
                    Baseline(PayloadDirectorySlot.Backup);
                AssertCleanupConverges(
                    Authority(InstallOperation.Uninstall, backup, null),
                    PayloadCleanupKind.CommittedBackup,
                    State(null, null, backup),
                    PayloadNamespaceShape.Empty);
            });
            Run("fresh candidate cleanup terminal receipt is idempotent", delegate
            {
                PayloadDirectoryCheckpoint target =
                    Target(PayloadDirectorySlot.Candidate);
                PayloadRecoveryAuthority authority =
                    Authority(InstallOperation.FreshInstall, null, target);
                PayloadNamespaceState empty = State(null, null, null);
                new PayloadCleanupReceipt(
                    authority,
                    PayloadCleanupKind.Candidate,
                    empty,
                    empty,
                    true);
            });
            Run("replacement backup cleanup terminal receipt is idempotent", delegate
            {
                PayloadDirectoryCheckpoint baseline =
                    Baseline(PayloadDirectorySlot.Backup);
                PayloadDirectoryCheckpoint current =
                    Target(PayloadDirectorySlot.Current);
                PayloadRecoveryAuthority authority =
                    Authority(InstallOperation.Upgrade, baseline, current);
                PayloadNamespaceState committed =
                    State(current, null, null);
                new PayloadCleanupReceipt(
                    authority,
                    PayloadCleanupKind.CommittedBackup,
                    committed,
                    committed,
                    true);
            });
            Run("fresh rollback terminal receipt is idempotent", delegate
            {
                PayloadDirectoryCheckpoint target =
                    Target(PayloadDirectorySlot.Candidate);
                PayloadRecoveryAuthority authority =
                    Authority(InstallOperation.FreshInstall, null, target);
                PayloadNamespaceState empty = State(null, null, null);
                new PayloadRecoveryReceipt(
                    PayloadRecoveryDecision.RestoreBaseline,
                    authority,
                    empty,
                    empty);
            });
            Reject("backup cleanup rejects candidate-and-backup", delegate
            {
                PayloadDirectoryCheckpoint backup =
                    Baseline(PayloadDirectorySlot.Backup);
                PayloadDirectoryCheckpoint candidate =
                    Target(PayloadDirectorySlot.Candidate);
                ProtectedPayloadRecoveryPlanner.NextCleanup(
                    Authority(InstallOperation.Upgrade, backup, candidate),
                    PayloadCleanupKind.CommittedBackup,
                    State(null, candidate, backup));
            });
            Reject("replacement backup cleanup rejects backup-only", delegate
            {
                PayloadDirectoryCheckpoint backup =
                    Baseline(PayloadDirectorySlot.Backup);
                ProtectedPayloadRecoveryPlanner.NextCleanup(
                    Authority(
                        InstallOperation.Upgrade,
                        backup,
                        Target(PayloadDirectorySlot.Current)),
                    PayloadCleanupKind.CommittedBackup,
                    State(null, null, backup));
            });
            Reject("backup cleanup rejects baseline in current slot", delegate
            {
                PayloadDirectoryCheckpoint backup =
                    Baseline(PayloadDirectorySlot.Backup);
                PayloadDirectoryCheckpoint wrongCurrent =
                    BaselineAlternate(PayloadDirectorySlot.Current);
                ProtectedPayloadRecoveryPlanner.NextCleanup(
                    Authority(
                        InstallOperation.Upgrade,
                        backup,
                        Target(PayloadDirectorySlot.Current)),
                    PayloadCleanupKind.CommittedBackup,
                    State(wrongCurrent, null, backup));
            });
            Reject("fresh candidate cleanup rejects published current", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Target(PayloadDirectorySlot.Current);
                ProtectedPayloadRecoveryPlanner.NextCleanup(
                    Authority(InstallOperation.FreshInstall, null, current),
                    PayloadCleanupKind.Candidate,
                    State(current, null, null));
            });
            Reject("uninstall candidate cleanup rejects backup-only", delegate
            {
                PayloadDirectoryCheckpoint backup =
                    Baseline(PayloadDirectorySlot.Backup);
                ProtectedPayloadRecoveryPlanner.NextCleanup(
                    Authority(InstallOperation.Uninstall, backup, null),
                    PayloadCleanupKind.Candidate,
                    State(null, null, backup));
            });
            Reject("candidate cleanup rejects baseline current beside backup", delegate
            {
                PayloadDirectoryCheckpoint backup =
                    Baseline(PayloadDirectorySlot.Backup);
                PayloadDirectoryCheckpoint wrongCurrent =
                    BaselineAlternate(PayloadDirectorySlot.Current);
                ProtectedPayloadRecoveryPlanner.NextCleanup(
                    Authority(
                        InstallOperation.Upgrade,
                        backup,
                        Target(PayloadDirectorySlot.Current)),
                    PayloadCleanupKind.Candidate,
                    State(wrongCurrent, null, backup));
            });
            Reject("fresh committed-backup cleanup rejects published current", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Target(PayloadDirectorySlot.Current);
                ProtectedPayloadRecoveryPlanner.NextCleanup(
                    Authority(InstallOperation.FreshInstall, null, current),
                    PayloadCleanupKind.CommittedBackup,
                    State(current, null, null));
            });
            Reject("replacement forward rejects lost baseline", delegate
            {
                PayloadDirectoryCheckpoint baseline =
                    Baseline(PayloadDirectorySlot.Current);
                PayloadDirectoryCheckpoint candidate =
                    Target(PayloadDirectorySlot.Candidate);
                ProtectedPayloadRecoveryPlanner.NextRecovery(
                    Authority(InstallOperation.Upgrade, baseline, candidate),
                    PayloadRecoveryDecision.CompleteForward,
                    State(null, candidate, null));
            });
            Reject("semantic target drift is rejected", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Baseline(PayloadDirectorySlot.Current);
                PayloadDirectoryCheckpoint expected =
                    Target(PayloadDirectorySlot.Candidate);
                PayloadDirectoryCheckpoint drifted = expected.DeepClone();
                drifted.Entries[0].Sha256 = HashD;
                ProtectedPayloadRecoveryPlanner.NextRecovery(
                    Authority(InstallOperation.Upgrade, current, expected),
                    PayloadRecoveryDecision.CompleteForward,
                    State(current, drifted, null));
            });
            Reject("cross-transaction namespace is rejected", delegate
            {
                PayloadDirectoryCheckpoint candidate =
                    Target(PayloadDirectorySlot.Candidate);
                PayloadNamespaceCheckpoint checkpoint =
                    State(null, candidate, null).Checkpoint;
                checkpoint.TransactionId = OtherTransactionId;
                checkpoint.Candidate.TransactionId = OtherTransactionId;
                ProtectedPayloadRecoveryPlanner.NextRecovery(
                    Authority(InstallOperation.FreshInstall, null, candidate),
                    PayloadRecoveryDecision.CompleteForward,
                    new PayloadNamespaceState(checkpoint));
            });
            Run("stale mutation plan is rejected by CAS fake", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Baseline(PayloadDirectorySlot.Current);
                PayloadDirectoryCheckpoint candidate =
                    Target(PayloadDirectorySlot.Candidate);
                PayloadRecoveryAuthority authority =
                    Authority(InstallOperation.Upgrade, current, candidate);
                PayloadNamespaceState state = State(current, candidate, null);
                PayloadNamespaceMutationPlan stale =
                    ProtectedPayloadRecoveryPlanner.NextRecovery(
                        authority,
                        PayloadRecoveryDecision.CompleteForward,
                        state);
                PayloadNamespaceState changed = Apply(state, stale);
                ExpectRejected(delegate { Apply(changed, stale); });
            });
            Run("terminal planning is mutation-free and repeatable", delegate
            {
                PayloadDirectoryCheckpoint current =
                    Target(PayloadDirectorySlot.Current);
                PayloadRecoveryAuthority authority =
                    Authority(InstallOperation.FreshInstall, null, current);
                PayloadNamespaceState state = State(current, null, null);
                PayloadNamespaceMutationPlan first =
                    ProtectedPayloadRecoveryPlanner.NextRecovery(
                        authority,
                        PayloadRecoveryDecision.CompleteForward,
                        state);
                PayloadNamespaceMutationPlan second =
                    ProtectedPayloadRecoveryPlanner.NextRecovery(
                        authority,
                        PayloadRecoveryDecision.CompleteForward,
                        state);
                Equal(true, first.IsTerminal);
                Equal(true, second.IsTerminal);
                Equal(state.InvariantDigest, first.Observed.InvariantDigest);
            });

            Console.WriteLine(
                "Protected payload recovery planner tests passed: " +
                passed.ToString());
            return 0;
        }

        private static void AssertRecoveryConverges(
            PayloadRecoveryAuthority authority,
            PayloadRecoveryDecision decision,
            PayloadNamespaceState initial,
            PayloadNamespaceShape expectedShape)
        {
            PayloadNamespaceState state = initial;
            int mutations = 0;
            while (true)
            {
                PayloadNamespaceMutationPlan plan =
                    ProtectedPayloadRecoveryPlanner.NextRecovery(
                        authority,
                        decision,
                        state);
                if (plan.IsTerminal)
                {
                    break;
                }
                state = Apply(state, plan);
                mutations++;
                if (mutations > 3)
                {
                    throw new InvalidOperationException(
                        "Recovery planner did not converge.");
                }
            }
            Equal(expectedShape, state.Shape);
            new PayloadRecoveryReceipt(decision, authority, initial, state);
        }

        private static void AssertCleanupConverges(
            PayloadRecoveryAuthority authority,
            PayloadCleanupKind kind,
            PayloadNamespaceState initial,
            PayloadNamespaceShape expectedShape)
        {
            PayloadNamespaceState state = initial;
            PayloadNamespaceMutationPlan plan =
                ProtectedPayloadRecoveryPlanner.NextCleanup(
                    authority,
                    kind,
                    state);
            if (!plan.IsTerminal)
            {
                state = Apply(state, plan);
            }
            PayloadNamespaceMutationPlan terminal =
                ProtectedPayloadRecoveryPlanner.NextCleanup(
                    authority,
                    kind,
                    state);
            Equal(true, terminal.IsTerminal);
            Equal(expectedShape, state.Shape);
            new PayloadCleanupReceipt(
                authority,
                kind,
                initial,
                state,
                true);
        }

        private static PayloadNamespaceState Apply(
            PayloadNamespaceState current,
            PayloadNamespaceMutationPlan plan)
        {
            if (!String.Equals(
                current.InvariantDigest,
                plan.Observed.InvariantDigest,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Fake namespace rejected stale mutation plan.");
            }
            if (plan.IsTerminal)
            {
                return current;
            }

            PayloadNamespaceCheckpoint next = current.Checkpoint;
            PayloadDirectoryCheckpoint source = Source(next, plan.Kind);
            if (source == null ||
                !String.Equals(
                    source.InvariantDigest,
                    plan.ExactSource.InvariantDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Fake namespace rejected source identity drift.");
            }

            switch (plan.Kind)
            {
                case PayloadNamespaceMutationKind.RenameCurrentToBackup:
                    next.Current = null;
                    next.Backup = Rename(
                        source,
                        PayloadDirectorySlot.Backup);
                    break;
                case PayloadNamespaceMutationKind.RenameCandidateToCurrent:
                    next.Candidate = null;
                    next.Current = Rename(
                        source,
                        PayloadDirectorySlot.Current);
                    break;
                case PayloadNamespaceMutationKind.RenameBackupToCurrent:
                    next.Backup = null;
                    next.Current = Rename(
                        source,
                        PayloadDirectorySlot.Current);
                    break;
                case PayloadNamespaceMutationKind.DeleteCurrent:
                    next.Current = null;
                    break;
                case PayloadNamespaceMutationKind.DeleteCandidate:
                    next.Candidate = null;
                    break;
                case PayloadNamespaceMutationKind.DeleteBackup:
                    next.Backup = null;
                    break;
                default:
                    throw new InvalidOperationException(
                        "Fake namespace received an invalid mutation.");
            }
            next.Revision++;
            next.Shape = Shape(next);
            return new PayloadNamespaceState(next);
        }

        private static PayloadDirectoryCheckpoint Source(
            PayloadNamespaceCheckpoint state,
            PayloadNamespaceMutationKind kind)
        {
            switch (kind)
            {
                case PayloadNamespaceMutationKind.RenameCurrentToBackup:
                case PayloadNamespaceMutationKind.DeleteCurrent:
                    return state.Current;
                case PayloadNamespaceMutationKind.RenameCandidateToCurrent:
                case PayloadNamespaceMutationKind.DeleteCandidate:
                    return state.Candidate;
                case PayloadNamespaceMutationKind.RenameBackupToCurrent:
                case PayloadNamespaceMutationKind.DeleteBackup:
                    return state.Backup;
                default:
                    return null;
            }
        }

        private static PayloadNamespaceShape Shape(
            PayloadNamespaceCheckpoint state)
        {
            if (state.Current != null && state.Candidate != null)
            {
                return PayloadNamespaceShape.CurrentAndCandidate;
            }
            if (state.Current != null && state.Backup != null)
            {
                return PayloadNamespaceShape.CurrentAndBackup;
            }
            if (state.Candidate != null && state.Backup != null)
            {
                return PayloadNamespaceShape.CandidateAndBackup;
            }
            if (state.Current != null)
            {
                return PayloadNamespaceShape.CurrentOnly;
            }
            if (state.Candidate != null)
            {
                return PayloadNamespaceShape.CandidateOnly;
            }
            if (state.Backup != null)
            {
                return PayloadNamespaceShape.BackupOnly;
            }
            return PayloadNamespaceShape.Empty;
        }

        private static PayloadNamespaceState State(
            PayloadDirectoryCheckpoint current,
            PayloadDirectoryCheckpoint candidate,
            PayloadDirectoryCheckpoint backup)
        {
            var checkpoint = new PayloadNamespaceCheckpoint
            {
                SchemaVersion = 1,
                Revision = 1,
                TransactionId = TransactionId,
                Current = current,
                Candidate = candidate,
                Backup = backup
            };
            checkpoint.Shape = Shape(checkpoint);
            return new PayloadNamespaceState(checkpoint);
        }

        private static PayloadDirectoryCheckpoint Baseline(
            PayloadDirectorySlot slot)
        {
            return Directory(
                slot,
                "0.2.0",
                HashC,
                HashC,
                HashD,
                "10000000000000000000000000000000");
        }

        private static PayloadDirectoryCheckpoint Target(
            PayloadDirectorySlot slot)
        {
            return Directory(
                slot,
                "0.3.0",
                HashA,
                HashA,
                HashB,
                "20000000000000000000000000000000");
        }

        private static PayloadDirectoryCheckpoint BaselineAlternate(
            PayloadDirectorySlot slot)
        {
            return Directory(
                slot,
                "0.2.0",
                HashC,
                HashC,
                HashD,
                "30000000000000000000000000000000");
        }

        private static PayloadDirectoryCheckpoint Directory(
            PayloadDirectorySlot slot,
            string version,
            string packageHash,
            string executableHash,
            string driverHash,
            string identityStem)
        {
            return new PayloadDirectoryCheckpoint
            {
                TransactionId = TransactionId,
                Slot = slot,
                VolumeSerialNumber = 0x1234UL,
                FileId = identityStem,
                Release = new ReleaseIdentity(version, packageHash),
                ContentSetSha256 = packageHash,
                ManifestInvariantDigest = driverHash,
                FileCount = 2,
                TotalBytes = 30,
                Entries = new List<PayloadTreeEntryCheckpoint>
                {
                    new PayloadTreeEntryCheckpoint
                    {
                        RelativePath = "SBMS.exe",
                        IsDirectory = false,
                        FileId = identityStem.Substring(0, 31) + "1",
                        Length = 10,
                        Sha256 = executableHash
                    },
                    new PayloadTreeEntryCheckpoint
                    {
                        RelativePath = "driver",
                        IsDirectory = true,
                        FileId = identityStem.Substring(0, 31) + "2",
                        Length = 0,
                        Sha256 = String.Empty
                    },
                    new PayloadTreeEntryCheckpoint
                    {
                        RelativePath = @"driver\SBMS.dll",
                        IsDirectory = false,
                        FileId = identityStem.Substring(0, 31) + "3",
                        Length = 20,
                        Sha256 = driverHash
                    }
                }
            };
        }

        private static PayloadDirectoryCheckpoint Rename(
            PayloadDirectoryCheckpoint source,
            PayloadDirectorySlot destination)
        {
            PayloadDirectoryCheckpoint result = source.DeepClone();
            result.Slot = destination;
            return result;
        }

        private static PayloadRecoveryAuthority Authority(
            InstallOperation operation,
            PayloadDirectoryCheckpoint baseline,
            PayloadDirectoryCheckpoint target)
        {
            return new PayloadRecoveryAuthority
            {
                SchemaVersion = 1,
                TransactionId = TransactionId,
                Operation = operation,
                BaselineState = baseline == null
                    ? BaselinePayloadState.Absent
                    : BaselinePayloadState.Present,
                Baseline = ContentAuthority(baseline),
                Target = ContentAuthority(target),
                SealedEscrowManifestSha256 = HashD
            };
        }

        private static PayloadContentAuthority ContentAuthority(
            PayloadDirectoryCheckpoint directory)
        {
            if (directory == null)
            {
                return null;
            }
            return new PayloadContentAuthority
            {
                Release = new ReleaseIdentity(
                    directory.Release.Version,
                    directory.Release.PackageFingerprint),
                ContentSetSha256 = directory.ContentSetSha256,
                ManifestInvariantDigest = directory.ManifestInvariantDigest,
                SemanticTreeSha256 = directory.SemanticTreeSha256,
                FileCount = directory.FileCount,
                TotalBytes = directory.TotalBytes
            };
        }

        private static void Run(string name, Action action)
        {
            action();
            passed++;
            Console.WriteLine("[PASS] " + name);
        }

        private static void Reject(string name, Action action)
        {
            ExpectRejected(action);
            passed++;
            Console.WriteLine("[PASS] " + name);
        }

        private static void ExpectRejected(Action action)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception)
            {
                rejected = true;
            }
            if (!rejected)
            {
                throw new InvalidOperationException(
                    "Expected rejection.");
            }
        }

        private static void Equal(object expected, object actual)
        {
            if (!Object.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    "Expected " + expected + ", actual " + actual + ".");
            }
        }
    }
}
