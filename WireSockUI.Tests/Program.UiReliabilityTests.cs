using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using WireSockUI.Config;
using WireSockUI.Forms;
using WireSockUI.Native;

namespace WireSockUI.Tests
{
    internal static partial class Program
    {
        private static void EditorApplicationRuleInsertionIsSectionAware()
        {
            var configuration =
                "[Interface]\r\n" +
                "AllowedApps = interface.exe\r\n" +
                "\r\n" +
                "[Peer]\r\n" +
                "AllowedApps = obsolete.exe\r\n" +
                "\r\n" +
                "[Interface]\r\n" +
                "Address = 10.0.0.2/32\r\n" +
                "\r\n" +
                "[Peer]\r\n" +
                "AllowedAppsExtra = keep-me\r\n" +
                "AllowedApps_Extra = keep-this-too\r\n" +
                "#@ws:AllowedApps = current.exe\r\n" +
                "[Trailing]\r\n" +
                "Value = unchanged\r\n";

            AssertTrue(ProfileConfigurationEditor.TryInsertOrAppendPeerValue(
                    configuration,
                    "AllowedApps",
                    "selected.exe",
                    out var updated,
                    out var selectionIndex,
                    out var diagnostic),
                diagnostic);
            AssertTrue(updated.Contains("AllowedApps = interface.exe"),
                "Expected an Interface key with the same name to remain untouched.");
            AssertTrue(updated.Contains("AllowedApps = obsolete.exe"),
                "Expected an obsolete earlier Peer section to remain untouched because the SDK uses the final section.");
            AssertTrue(updated.Contains("AllowedAppsExtra = keep-me"),
                "Expected a same-prefix key not to be mistaken for the requested key.");
            AssertTrue(updated.Contains("AllowedApps_Extra = keep-this-too"),
                "Expected punctuation after the requested key not to be treated as an assignment boundary.");
            AssertTrue(updated.Contains("#@ws:AllowedApps = current.exe, selected.exe"),
                "Expected the exact key in the final Peer section to receive the new value.");
            AssertTrue(selectionIndex == updated.IndexOf("selected.exe", StringComparison.Ordinal) +
                                        "selected.exe".Length,
                "Expected the caret to move to the end of the inserted application rule.");

            const string withoutRule = "[Peer]\nPublicKey = value\n[Other]\nValue = unchanged\n";
            AssertTrue(ProfileConfigurationEditor.TryInsertOrAppendPeerValue(
                    withoutRule,
                    "DisallowedApps",
                    "blocked.exe",
                    out var inserted,
                    out _,
                    out diagnostic),
                diagnostic);
            AssertTrue(inserted.IndexOf("#@ws:DisallowedApps = blocked.exe", StringComparison.Ordinal) <
                       inserted.IndexOf("[Other]", StringComparison.Ordinal),
                "Expected a missing rule to be inserted inside Peer, before the following section.");

            AssertFalse(ProfileConfigurationEditor.TryInsertOrAppendPeerValue(
                    "[Interface]\nAddress = 10.0.0.2/32\n",
                    "AllowedApps",
                    "selected.exe",
                    out _,
                    out _,
                    out _),
                "Expected insertion to fail rather than append outside a Peer section.");
        }

        private static void EditorApplicationRuleInsertionRejectsAmbiguousValues()
        {
            const string configuration = "[Peer]\nAllowedApps = current.exe\n";
            AssertFalse(ProfileConfigurationEditor.TryInsertOrAppendPeerValue(
                    configuration,
                    "AllowedApps",
                    @"C:\Apps, Inc\client.exe",
                    out var updated,
                    out _,
                    out var diagnostic),
                "Expected comma-containing paths to be rejected instead of split into multiple rules.");
            AssertEqual(configuration, updated);
            AssertTrue(diagnostic.IndexOf("comma", StringComparison.OrdinalIgnoreCase) >= 0,
                "Expected an actionable delimiter diagnostic.");
        }

        private static void AutorunClassificationCoversLegacyAndConflicts()
        {
            var canonical = FrmSettings.ClassifyAutoRunStatus(
                true,
                true,
                false,
                false,
                false,
                FrmSettings.LegacyStartupShortcutStatus.Absent,
                out var usesPathScopedTask);
            AssertTrue(canonical == FrmSettings.AutoRunStatus.Enabled && usesPathScopedTask,
                "Expected an exact path-scoped task to be recognized as canonical autorun.");

            var unverifiedLegacyShortcut = FrmSettings.ClassifyAutoRunStatus(
                false,
                false,
                false,
                false,
                false,
                FrmSettings.LegacyStartupShortcutStatus.Unverified,
                out usesPathScopedTask);
            AssertTrue(
                unverifiedLegacyShortcut == FrmSettings.AutoRunStatus.LegacyShortcutMigrationRequired &&
                !usesPathScopedTask,
                "Expected an unauthenticated Startup shortcut to require explicit migration consent.");
            AssertFalse(
                FrmSettings.ResolveRequestedAutoRun(unverifiedLegacyShortcut, false, true),
                "Expected a user-writable legacy artifact not to seed elevated autorun from persisted state.");
            AssertTrue(
                FrmSettings.ShouldPreserveLegacyShortcutUntilCommit(true, true),
                "Expected approved migration cleanup to remain deferred until all settings steps commit.");
            AssertFalse(
                FrmSettings.ShouldPreserveLegacyShortcutUntilCommit(true, false),
                "Expected an unapproved artifact never to enter migration cleanup.");
            AssertFalse(
                FrmSettings.ShouldApplyAutoRunChange(
                    unverifiedLegacyShortcut,
                    false,
                    false,
                    true,
                    true),
                "Expected confirmed cleanup to require no task mutation while autorun remains disabled.");

            var trustedLegacyTask = FrmSettings.ClassifyAutoRunStatus(
                false,
                false,
                false,
                true,
                false,
                FrmSettings.LegacyStartupShortcutStatus.Unverified,
                out usesPathScopedTask);
            AssertTrue(trustedLegacyTask == FrmSettings.AutoRunStatus.LegacyEnabled && !usesPathScopedTask,
                "Expected a validated legacy task, rather than the unauthenticated shortcut, to establish enabled state.");
            AssertFalse(
                FrmSettings.ShouldApplyAutoRunChange(
                    trustedLegacyTask,
                    true,
                    false,
                    true,
                    false),
                "Expected a routine save not to migrate a task or delete an unauthenticated shortcut.");
            AssertTrue(
                FrmSettings.ShouldApplyAutoRunChange(
                    trustedLegacyTask,
                    false,
                    false,
                    true,
                    true),
                "Expected an explicitly confirmed disable to update the task while deferring shortcut cleanup.");

            var canonicalWithUnverifiedShortcut = FrmSettings.ClassifyAutoRunStatus(
                true,
                true,
                false,
                false,
                false,
                FrmSettings.LegacyStartupShortcutStatus.Unverified,
                out usesPathScopedTask);
            AssertTrue(
                canonicalWithUnverifiedShortcut == FrmSettings.AutoRunStatus.Enabled && usesPathScopedTask,
                "Expected the validated canonical task to determine enabled state independently of the opaque shortcut.");
            AssertFalse(
                FrmSettings.ShouldApplyAutoRunChange(
                    canonicalWithUnverifiedShortcut,
                    false,
                    true,
                    true,
                    false),
                "Expected direct disable to remain blocked until opaque-shortcut deletion is confirmed.");

            var conflict = FrmSettings.ClassifyAutoRunStatus(
                true,
                true,
                false,
                false,
                false,
                FrmSettings.LegacyStartupShortcutStatus.Foreign,
                out usesPathScopedTask);
            AssertTrue(conflict == FrmSettings.AutoRunStatus.Conflict && !usesPathScopedTask,
                "Expected a foreign same-name Startup shortcut to fail closed as a conflict.");
            AssertTrue(FrmSettings.ResolveRequestedAutoRun(conflict, false, true),
                "Expected a conflict to preserve the persisted preference instead of silently changing it.");

            var unknown = FrmSettings.ClassifyAutoRunStatus(
                false,
                false,
                false,
                false,
                false,
                FrmSettings.LegacyStartupShortcutStatus.Unknown,
                out usesPathScopedTask);
            AssertTrue(unknown == FrmSettings.AutoRunStatus.Unknown,
                "Expected an unreadable Startup shortcut to leave autorun state unknown.");
        }

        private static void ProcessSnapshotsAreCachedSerializedAndSidBased()
        {
            var calls = 0;
            var activeFactories = 0;
            var maximumActiveFactories = 0;
            ProcessEntry[] latestFactorySnapshot = null;
            var cache = new ProcessSnapshotCache(cancellationToken =>
            {
                var active = Interlocked.Increment(ref activeFactories);
                UpdateMaximum(ref maximumActiveFactories, active);
                try
                {
                    Thread.Sleep(25);
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref calls);
                    latestFactorySnapshot = new[]
                    {
                        new ProcessEntry(1, "one.exe", @"C:\one.exe", "S-1-5-21-1")
                    };
                    return latestFactorySnapshot;
                }
                finally
                {
                    Interlocked.Decrement(ref activeFactories);
                }
            });

            Task.WaitAll(
                cache.GetSnapshotAsync(true, CancellationToken.None),
                cache.GetSnapshotAsync(true, CancellationToken.None));
            AssertTrue(maximumActiveFactories == 1,
                "Expected process snapshot factories to be serialized.");
            AssertTrue(calls == 2,
                "Expected explicit refreshes to each obtain a fresh serialized snapshot.");

            var cached = cache.GetSnapshotAsync(false, CancellationToken.None).GetAwaiter().GetResult();
            AssertTrue(calls == 2 && cached.Count == 1,
                "Expected filter-only refreshes to reuse the most recent process snapshot.");
            AssertFalse(cached is ProcessEntry[],
                "Expected callers never to receive the cache's mutable backing array.");

            var mutableView = cached as IList<ProcessEntry>;
            AssertTrue(mutableView != null,
                "Expected the snapshot wrapper to expose standard read-only list semantics.");
            AssertThrows<NotSupportedException>(
                () => mutableView[0] = new ProcessEntry(
                    99,
                    "mutated.exe",
                    @"C:\mutated.exe",
                    "S-1-5-21-99"),
                string.Empty);
            latestFactorySnapshot[0] = new ProcessEntry(
                98,
                "factory-mutated.exe",
                @"C:\factory-mutated.exe",
                "S-1-5-21-98");

            var cachedAfterMutationAttempt =
                cache.GetSnapshotAsync(false, CancellationToken.None).GetAwaiter().GetResult();
            AssertTrue(ReferenceEquals(cached, cachedAfterMutationAttempt),
                "Expected cached reads to reuse one immutable wrapper without per-read copying.");
            AssertEqual("one.exe", cachedAfterMutationAttempt[0].Name);

            var refreshed =
                cache.GetSnapshotAsync(true, CancellationToken.None).GetAwaiter().GetResult();
            AssertTrue(calls == 3 && !ReferenceEquals(cached, refreshed),
                "Expected an explicit refresh to atomically replace the immutable cached snapshot.");
            AssertTrue(ReferenceEquals(
                    refreshed,
                    cache.GetSnapshotAsync(false, CancellationToken.None).GetAwaiter().GetResult()),
                "Expected reads after refresh to reuse the replacement immutable wrapper.");

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                AssertThrows<OperationCanceledException>(
                    () => cache.GetSnapshotAsync(true, cancellation.Token).GetAwaiter().GetResult(),
                    string.Empty);
            }

            var matching = new ProcessEntry(2, "matching.exe", null, "S-1-5-21-1");
            var accountName = new ProcessEntry(3, "account.exe", null, @"DOMAIN\User");
            AssertTrue(TaskManager.ShouldIncludeProcessForUser(matching, true, "S-1-5-21-1"),
                "Expected raw matching owner SIDs to pass the current-user filter.");
            AssertFalse(TaskManager.ShouldIncludeProcessForUser(accountName, true, "S-1-5-21-1"),
                "Expected account names not to trigger an implicit SID lookup during filtering.");

            int currentProcessId;
            using (var process = Process.GetCurrentProcess())
                currentProcessId = process.Id;
            var currentProcess = ProcessList.GetProcessList()
                .FirstOrDefault(process => process.ProcessId == currentProcessId);
            AssertTrue(currentProcess != null && !string.IsNullOrWhiteSpace(currentProcess.User),
                "Expected the test process owner SID to be available.");
            var parsedSid = new SecurityIdentifier(currentProcess.User);
            AssertEqual(currentProcess.User, parsedSid.Value);
        }

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            int observed;
            do
            {
                observed = maximum;
                if (candidate <= observed)
                    return;
            } while (Interlocked.CompareExchange(ref maximum, candidate, observed) != observed);
        }
    }
}
