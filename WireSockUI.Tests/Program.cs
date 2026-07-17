using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using WireSockUI;
using WireSockUI.Config;
using WireSockUI.Diagnostics;
using WireSockUI.Extensions;
using WireSockUI.Forms;
using WireSockUI.Native;

namespace WireSockUI.Tests
{
    internal static class Program
    {
        private static readonly string PrivateKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
        private static readonly string PublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());
        private const int SymbolicLinkFlagFile = 0;
        private const int SymbolicLinkFlagAllowUnprivilegedCreate = 2;
        private const int TestTimeoutMilliseconds = 120000;
        private const int MoveFileWriteThrough = 0x8;

        private sealed class TestExecutionResult
        {
            private TestExecutionResult(bool timedOut, Exception exception)
            {
                TimedOut = timedOut;
                Exception = exception;
            }

            internal bool TimedOut { get; }
            internal Exception Exception { get; }

            internal static TestExecutionResult Success()
            {
                return new TestExecutionResult(false, null);
            }

            internal static TestExecutionResult Timeout()
            {
                return new TestExecutionResult(true, null);
            }

            internal static TestExecutionResult Failure(Exception exception)
            {
                return new TestExecutionResult(false,
                    exception ?? throw new ArgumentNullException(nameof(exception)));
            }
        }

        private static bool TestKillSwitch
        {
            get => PrivilegedSettingsStore.EnableKillSwitch;
            set
            {
                var current = PrivilegedSettingsStore.Capture();
                PrivilegedSettingsStore.SetForTests(new PrivilegedSettingsSnapshot(
                    current.AutoConnect,
                    current.LastProfile,
                    current.UseAdapter,
                    value));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(
            string lpSymlinkFileName,
            string lpTargetFileName,
            int dwFlags);

        [DllImport("kernel32.dll", EntryPoint = "SetLastError", SetLastError = true)]
        private static extern void SetLastErrorForTest(uint errorCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newFileName, string existingFileName,
            IntPtr securityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        private static int Main(string[] args)
        {
            if (args?.Any(arg => string.Equals(arg, "--sdk-integration", StringComparison.OrdinalIgnoreCase)) == true)
                return RunSdkIntegrationSmoke();

            var tests = new Dictionary<string, Action>
            {
                { "Profile rejects empty required values", ProfileRejectsEmptyRequiredValues },
                { "Profile rejects empty address list items", ProfileRejectsEmptyAddressListItems },
                { "Profile validates Windows-safe profile names", ProfileValidatesWindowsSafeNames },
                { "Profile path rejects unsafe names", ProfilePathRejectsUnsafeNames },
                { "Profile reports configured script hooks", ProfileReportsConfiguredScriptHooks },
                { "Script hook warning preserves and escapes complete commands", ScriptHookWarningPreservesAndEscapesCompleteCommands },
                { "Profile enumeration accepts uppercase conf extension", ProfileEnumerationAcceptsUppercaseConfExtension },
                { "Profile enumeration creates missing overridden config folder", ProfileEnumerationCreatesMissingOverriddenConfigFolder },
                { "Profile enumeration rejects oversized folders", ProfileEnumerationRejectsOversizedFolders },
                { "Profile catalog reports enumeration failures without replacing data", ProfileCatalogReportsEnumerationFailures },
                { "Profile catalog rejects case-insensitive duplicates", ProfileCatalogRejectsCaseInsensitiveDuplicates },
                { "Profile rejects oversized installed files", ProfileRejectsOversizedInstalledFiles },
                { "Profile rejects directory profile paths", ProfileRejectsDirectoryProfilePaths },
                { "Profile rejects reparse point profile files", ProfileRejectsReparsePointProfileFiles },
                { "Profile reports missing profile paths clearly", ProfileReportsMissingProfilePathsClearly },
                { "Profile reports malformed profile paths consistently", ProfileReportsMalformedProfilePathsConsistently },
                { "Parser accepts only exact WireSock directive prefixes", ParserAcceptsOnlyExactWireSockDirectivePrefixes },
                { "Parser matches SDK casing", ParserMatchesSdkCasing },
                { "Parser matches SDK last-section-wins behavior", ParserMatchesSdkLastSectionWinsBehavior },
                { "Parser rejects malformed lines", ParserRejectsMalformedLines },
                { "Parser matches SDK duplicate-key projection", ParserMatchesSdkDuplicateKeyProjection },
                { "Parser rejects SDK-incompatible byte-order marks", ParserRejectsSdkIncompatibleByteOrderMarks },
                { "Parser rejects malformed UTF-8", ParserRejectsMalformedUtf8 },
                { "Parser rejects keys before sections", ParserRejectsKeysBeforeSections },
                { "Parser rejects empty section names", ParserRejectsEmptySectionNames },
                { "Parser trims section names", ParserTrimsSectionNames },
                { "Profile accepts Amnezia passthrough options", ProfileAcceptsAmneziaPassthroughOptions },
                { "Profile rejects invalid Amnezia passthrough options", ProfileRejectsInvalidAmneziaPassthroughOptions },
                { "Profile validates Amnezia option groups", ProfileValidatesAmneziaOptionGroups },
                { "Profile validates protocol imitation combinations", ProfileValidatesProtocolImitationCombinations },
                { "Profile validates current SDK numeric ranges", ProfileValidatesCurrentSdkNumericRanges },
                { "Profile rejects SDK casing mismatches", ProfileRejectsSdkCasingMismatches },
                { "Profile rejects unsupported direct-DLL directives", ProfileRejectsUnsupportedDirectDllDirectives },
                { "Interface extension validation rules are shared", InterfaceExtensionValidationRulesAreShared },
                { "Stats formatting handles extreme values", StatsFormattingHandlesExtremeValues },
                { "Stats formatting handles missing handshakes", StatsFormattingHandlesMissingHandshakes },
                { "Time formatting uses plural hours", TimeFormattingUsesPluralHours },
                { "Time formatting uses singular hour for partial second hour", TimeFormattingUsesSingularHourForPartialSecondHour },
                { "Time formatting handles future values", TimeFormattingHandlesFutureValues },
                { "Global config folder containment handles drive roots", GlobalConfigFolderContainmentHandlesDriveRoots },
                { "Global rejects unsecured config folder overrides by default", GlobalRejectsUnsecuredConfigFolderOverridesByDefault },
                { "Global fails closed on configuration directory reparse points", GlobalFailsClosedOnConfigurationDirectoryReparsePoints },
                { "Global bounds secured tree enumeration", GlobalBoundsSecuredTreeEnumeration },
                { "Global removes configuration file reparse points by handle", GlobalRemovesConfigurationFileReparsePointsByHandle },
                { "Profile rejects user-writable secured files", ProfileRejectsUserWritableSecuredFiles },
                { "Release version parser handles SemVer tags", ReleaseVersionParserHandlesSemVerTags },
                { "Bounded response reader rejects declared and streamed overflow", BoundedResponseReaderRejectsDeclaredAndStreamedOverflow },
                { "Program path normalization preserves drive roots", ProgramPathNormalizationPreservesDriveRoots },
                { "Program rejects untrusted application payloads", ProgramRejectsUntrustedApplicationPayloads },
                { "Program enumerates nested application payloads", ProgramEnumeratesNestedApplicationPayloads },
                { "Program bounds application payload enumeration", ProgramBoundsApplicationPayloadEnumeration },
                { "Program distinguishes x64 and ARM64 PE images", ProgramDistinguishesBinaryArchitectures },
                { "Program rejects user-writable WireSock library directories", ProgramRejectsUserWritableWireSockLibraryDirectories },
                { "Program detects user-writable WireSock library files", ProgramDetectsUserWritableWireSockLibraryFiles },
                { "Program bounds WireSock SDK companion enumeration", ProgramBoundsWireSockSdkCompanionEnumeration },
                { "Program reports attribute inspection failures", ProgramReportsAttributeInspectionFailures },
                { "Program rejects an untrusted WireSock crash handler", ProgramRejectsUntrustedWireSockCrashHandler },
                { "Program distinguishes read-only and writable ACLs", ProgramDistinguishesReadOnlyAndWritableAcls },
                { "Program recognizes administrative owner SIDs", ProgramRecognizesAdministrativeOwnerSids },
                { "Program rejects replaceable trusted path ancestors", ProgramRejectsReplaceableTrustedPathAncestors },
                { "Autorun rejects untrusted executable paths", AutoRunRejectsUntrustedExecutablePaths },
                { "Autorun rejects reparse point executable folders", AutoRunRejectsReparsePointExecutableFolders },
                { "Profile import rejects oversized files", ProfileImportRejectsOversizedFiles },
                { "Profile import preserves pre-existing destination on copy failure", ProfileImportPreservesExistingDestinationOnCopyFailure },
                { "Profile import rejects reparse point sources", ProfileImportRejectsReparsePointSources },
                { "Profile import rejects directory sources", ProfileImportRejectsDirectorySources },
                { "Profile import reports malformed source paths consistently", ProfileImportReportsMalformedSourcePathsConsistently },
                { "Legacy migration quarantines valid profiles", LegacyMigrationQuarantinesValidProfiles },
                { "Legacy migration accepts uppercase conf extensions", LegacyMigrationAcceptsUppercaseConfExtensions },
                { "Legacy migration bounds catalog enumeration", LegacyMigrationBoundsCatalogEnumeration },
                { "Legacy migration preserves modified sources on completion", LegacyMigrationPreservesModifiedSourcesOnCompletion },
                { "Legacy migration preserves approved duplicates", LegacyMigrationPreservesApprovedDuplicates },
                { "Legacy migration rejects oversized files", LegacyMigrationRejectsOversizedFiles },
                { "Legacy migration rejects reparse point sources", LegacyMigrationRejectsReparsePointSources },
                { "Legacy migration accepts scripts only into quarantine", LegacyMigrationAcceptsScriptsOnlyIntoQuarantine },
                { "Legacy migration completion removes staged sources", LegacyMigrationCompletionRemovesStagedSources },
                { "Native recovery marker cleanup removes directory markers", NativeRecoveryMarkerCleanupRemovesDirectoryMarkers },
                { "Native recovery marker replacement does not follow hard links", NativeRecoveryMarkerReplacementDoesNotFollowHardLinks },
                { "Native recovery marker leases preserve newer failures", NativeRecoveryMarkerLeasesPreserveNewerFailures },
                { "Native recovery markers bound diagnostics", NativeRecoveryMarkersBoundDiagnostics },
                { "Native recovery marker replacement preserves the previous record on failure", NativeRecoveryMarkerReplacementPreservesPreviousRecordOnFailure },
                { "Secure filesystem delete handles block concurrent writes", SecureFileSystemDeleteHandlesBlockConcurrentWrites },
                { "Secure filesystem snapshots permit shell-link inspection", SecureFileSystemSnapshotsPermitShellLinkInspection },
                { "Secure filesystem reads text through validated handles", SecureFileSystemReadsTextThroughValidatedHandles },
                { "Secure filesystem rejects writable hard links", SecureFileSystemRejectsWritableHardLinks },
                { "Tunnel session coordinator enforces recovery invariants", TunnelSessionCoordinatorEnforcesRecoveryInvariants },
                { "Tunnel session coordinator waits for pending operations", TunnelSessionCoordinatorWaitsForPendingOperations },
                { "Tunnel monitor stops after a bounded query timeout", TunnelMonitorStopsAfterBoundedQueryTimeout },
                { "Tunnel monitor preserves statistics query timeouts", TunnelMonitorPreservesStatisticsQueryTimeouts },
                { "Tunnel monitor suppresses canceled query updates", TunnelMonitorSuppressesCanceledQueryUpdates },
                { "Tunnel monitor classifies unexpected statistics failures", TunnelMonitorClassifiesUnexpectedStatisticsFailures },
                { "Tunnel monitor UI dispatch awaits marshaled updates", TunnelMonitorUiDispatchAwaitsMarshaledUpdates },
                { "WireSock manager bounds native log backpressure", WireSockManagerBoundsNativeLogBackpressure },
                { "WireSock manager bounds retained log records", WireSockManagerBoundsRetainedLogRecords },
                { "UI log buffering coalesces and bounds dispatch", UiLogBufferingCoalescesAndBoundsDispatch },
                { "Diagnostic logging redacts credentials", DiagnosticLoggingRedactsCredentials },
                { "Diagnostic logging bounds oversized records", DiagnosticLoggingBoundsOversizedRecords },
                { "Native query distinguishes error sentinels", NativeQueryDistinguishesErrorSentinels },
                { "Settings upgrade runs exactly once", SettingsUpgradeRunsExactlyOnce },
                { "Protected settings require consent and persist securely", ProtectedSettingsRequireConsentAndPersist },
                { "Protected settings reject malformed or oversized data", ProtectedSettingsRejectMalformedOrOversizedData },
                { "Protected settings recover interrupted saves", ProtectedSettingsRecoverInterruptedSaves },
                { "Application settings use protected connection values", ApplicationSettingsUseProtectedConnectionValues },
                { "Settings transaction compensates failures in reverse order", SettingsTransactionCompensatesFailuresInReverseOrder },
                { "Settings coordinator owns update sequencing", SettingsCoordinatorOwnsUpdateSequencing },
                { "Settings rollback identifies native recovery requirements", SettingsRollbackIdentifiesNativeRecoveryRequirements },
                { "Tunnel commands distinguish activation from deactivation", TunnelCommandsDistinguishActivationFromDeactivation },
                { "Native timeout policy defers cleanup until completion", NativeTimeoutPolicyDefersCleanupUntilCompletion },
                { "Autorun preserves persisted state when status is unknown", AutorunPreservesPersistedStateWhenStatusIsUnknown },
                { "Curve25519 matches RFC 7748 public-key vectors", Curve25519MatchesRfc7748PublicKeyVectors },
                { "Editor validates Amnezia options", EditorValidatesAmneziaOptions },
                { "Editor bounds synchronous syntax highlighting", EditorBoundsSynchronousSyntaxHighlighting },
                { "AppUserModelID is path seeded", AppUserModelIdIsPathSeeded },
                { "Notification shortcut name is path seeded", NotificationShortcutNameIsPathSeeded },
                { "Notification image paths use file URIs", NotificationImagePathsUseFileUris },
                { "Shell link HRESULT validation uses signed failure semantics", ShellLinkHresultValidationUsesSignedFailureSemantics },
                { "Windows compatibility manifest enables modern behavior", WindowsCompatibilityManifestEnablesModernBehavior },
                { "Autorun task name is path seeded", AutoRunTaskNameIsPathSeeded },
                { "Autorun validates the complete task definition", AutoRunValidatesCompleteTaskDefinition },
                { "Process picker preserves executable match names", ProcessPickerPreservesExecutableMatchNames },
                { "WireSock disconnect forwards network-lock preservation", WireSockDisconnectForwardsNetworkLockPreservation },
                { "Lifecycle resets a preserved lock after handle creation fails", LifecycleResetsPreservedLockAfterHandleCreationFails },
                { "Lifecycle tracks late disconnect completion after timeout", LifecycleTracksLateDisconnectCompletionAfterTimeout },
                { "Lifecycle shutdown avoids synchronization-context deadlocks", LifecycleShutdownAvoidsSynchronizationContextDeadlocks },
                { "WireSock manager surfaces native query failures", WireSockManagerSurfacesNativeQueryFailures },
                { "WireSock manager cleans up failed starts", WireSockManagerCleansUpFailedStarts },
                { "WireSock manager retains handles when cleanup fails", WireSockManagerRetainsHandlesWhenCleanupFails },
                { "WireSock manager retries release without dropping twice", WireSockManagerRetriesReleaseWithoutDroppingTwice },
                { "WireSock manager quarantines dropped handles", WireSockManagerQuarantinesDroppedHandles },
                { "WireSock manager rolls back failed log-level changes", WireSockManagerRollsBackFailedLogLevelChanges },
                { "SDK smoke rejects unsafe integration profiles", SdkSmokeRejectsUnsafeIntegrationProfiles },
                { "SDK smoke cleans up failed tunnel creation", SdkSmokeCleansUpFailedTunnelCreation },
                { "SDK smoke runs final cleanup after failures", SdkSmokeRunsFinalCleanupAfterFailures },
                { "Profile rename commits and rolls back transactionally", ProfileRenameCommitsAndRollsBackTransactionally },
                { "Profile rename recovery completes interrupted transactions", ProfileRenameRecoveryCompletesInterruptedTransactions },
                { "Profile rename recovery rejects ambiguous states", ProfileRenameRecoveryRejectsAmbiguousStates },
                { "Profile rename recovery rejects active XML content", ProfileRenameRecoveryRejectsActiveXmlContent },
                { "Profile transaction recovery removes orphaned temporary files", ProfileTransactionRecoveryRemovesOrphanedTemporaryFiles },
                { "Test execution timeout policy is bounded", TestExecutionTimeoutPolicyIsBounded },
                { "Single-instance event rejects broad access", SingleInstanceEventRejectsBroadAccess },
                { "Tunnel profile state matches selections case-insensitively", TunnelProfileStateMatchesSelectionsCaseInsensitively },
                { "Network lock enum matches wgbooster ABI", NetworkLockEnumMatchesWgboosterAbi },
                { "WireSock exports use restricted DLL search", WireSockExportsUseRestrictedDllSearch },
                { "WireSock handle booleans match the C++ ABI", WireSockHandleBooleansMatchCppAbi },
                { "WireSock log callback decodes UTF-8 explicitly", WireSockLogCallbackDecodesUtf8Explicitly },
                { "Stats struct matches wgbooster ABI", StatsStructMatchesWgboosterAbi }
            };

            if (args?.Any(arg => string.Equals(arg, "--list-tests", StringComparison.OrdinalIgnoreCase)) == true)
            {
                foreach (var testName in tests.Keys)
                    Console.WriteLine(testName);
                return 0;
            }

            var filter = GetCommandLineOption(args, "--filter");
            if (!string.IsNullOrWhiteSpace(filter))
            {
                tests = tests
                    .Where(test => test.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToDictionary(test => test.Key, test => test.Value);
                if (tests.Count == 0)
                {
                    Console.WriteLine($"FAIL no tests matched filter '{filter}'.");
                    return 1;
                }
            }

            var failures = 0;
            foreach (var test in tests)
            {
                var stopwatch = Stopwatch.StartNew();
                var result = ExecuteTestWithTimeout(test.Value, TestTimeoutMilliseconds);
                if (result.TimedOut)
                {
                    failures++;
                    Console.WriteLine(
                        $"FAIL {test.Key}: exceeded the {TestTimeoutMilliseconds} ms per-test timeout.");
                    return 1;
                }

                if (result.Exception != null)
                {
                    failures++;
                    Console.WriteLine($"FAIL {test.Key}:{Environment.NewLine}{result.Exception}");
                    continue;
                }

                Console.WriteLine($"PASS {test.Key} ({stopwatch.ElapsedMilliseconds} ms)");
            }

            return failures == 0 ? 0 : 1;
        }

        private static TestExecutionResult ExecuteTestWithTimeout(Action test, int timeoutMilliseconds)
        {
            if (test == null) throw new ArgumentNullException(nameof(test));
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

            var task = Task.Run(test);
            try
            {
                if (task.Wait(timeoutMilliseconds))
                    return TestExecutionResult.Success();

                task.ContinueWith(faultedTask =>
                        GC.KeepAlive(faultedTask.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return TestExecutionResult.Timeout();
            }
            catch (AggregateException ex)
            {
                return TestExecutionResult.Failure(ex.GetBaseException());
            }
            catch (Exception ex)
            {
                return TestExecutionResult.Failure(ex);
            }
        }

        private static string GetCommandLineOption(IReadOnlyList<string> args, string optionName)
        {
            if (args == null || string.IsNullOrWhiteSpace(optionName))
                return null;

            for (var index = 0; index < args.Count; index++)
            {
                if (!string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
                    throw new ArgumentException($"{optionName} requires a value.");

                return args[index + 1];
            }

            return null;
        }

        private static int RunSdkIntegrationSmoke()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                {
                    Console.WriteLine("FAIL --sdk-integration requires an elevated runner token.");
                    return 1;
                }
            }

            var libraryPath = Environment.GetEnvironmentVariable("WIRESOCKUI_WGBOOSTER_PATH");
            if (string.IsNullOrWhiteSpace(libraryPath))
            {
                Console.WriteLine("FAIL WIRESOCKUI_WGBOOSTER_PATH is required for --sdk-integration.");
                return 1;
            }

            try
            {
                libraryPath = Path.GetFullPath(libraryPath);
                var libraryDirectory = Path.GetDirectoryName(libraryPath);
                if (!WireSockUI.Program.TryValidateWireSockLibraryDirectory(
                        libraryDirectory, out var validatedDirectory) ||
                    !string.Equals(Path.GetFullPath(validatedDirectory ?? string.Empty), libraryDirectory,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The configured SDK directory is not administrator-owned or is missing required files.");

                if (!WireSockUI.Program.TryConfigureRestrictedDllSearchPath(
                        libraryDirectory, libraryPath, out var loaderDiagnostic))
                    throw new InvalidOperationException(loaderDiagnostic ?? "Unable to load wgbooster.dll.");

                var api = new WireSockNativeApi();
                WireguardBoosterExports.LogPrinter logPrinter = message =>
                {
                    try
                    {
                        var text = WireguardBoosterExports.DecodeLogMessage(message);
                        if (!string.IsNullOrWhiteSpace(text))
                            Console.WriteLine($"SDK {text}");
                    }
                    catch
                    {
                        // A managed exception must never cross the native logging callback boundary.
                    }
                };

                var transparentProfile = GetRequiredSdkProfilePath(
                    "WIRESOCKUI_TEST_PROFILE_TRANSPARENT", "WIRESOCKUI_TEST_PROFILE");
                var virtualAdapterProfile = GetRequiredSdkProfilePath(
                    "WIRESOCKUI_TEST_PROFILE_VIRTUAL_ADAPTER", "WIRESOCKUI_TEST_PROFILE");
                var amneziaProfile = GetRequiredSdkProfilePath("WIRESOCKUI_TEST_PROFILE_AMNEZIA");
                ValidateAmneziaSdkIntegrationProfile(amneziaProfile);

                EnsureGlobalNetworkLockInactive(false);
                RunWithFinalCleanup(
                    () =>
                    {
                        RunSdkModeSmoke(api, WireSockManager.Mode.Transparent, transparentProfile, logPrinter);
                        EnsureGlobalNetworkLockInactive(true);
                        RunSdkModeSmoke(api, WireSockManager.Mode.VirtualAdapter, virtualAdapterProfile, logPrinter);
                        EnsureGlobalNetworkLockInactive(true);
                        RunSdkModeSmoke(api, WireSockManager.Mode.Transparent, amneziaProfile, logPrinter);
                        EnsureGlobalNetworkLockInactive(true);
                    },
                    () => EnsureGlobalNetworkLockInactive(true));

                Console.WriteLine(
                    "PASS real wgbooster.dll transparent, virtual-adapter, Kill Switch, and Amnezia lifecycle smoke tests.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL real wgbooster.dll smoke test:{Environment.NewLine}{ex}");
                return 1;
            }
        }

        private static string GetRequiredSdkProfilePath(string variableName, string fallbackVariableName = null)
        {
            var sourceVariableName = variableName;
            var profilePath = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(profilePath) && !string.IsNullOrWhiteSpace(fallbackVariableName))
            {
                profilePath = Environment.GetEnvironmentVariable(fallbackVariableName);
                sourceVariableName = fallbackVariableName;
            }
            if (string.IsNullOrWhiteSpace(profilePath))
                throw new InvalidOperationException(
                    $"{variableName} is required for the real SDK lifecycle smoke test.");

            profilePath = Path.GetFullPath(profilePath);
            if (!File.Exists(profilePath))
                throw new FileNotFoundException($"The SDK integration profile from {sourceVariableName} was not found.",
                    profilePath);

            if (!WireSockUI.Program.TryValidateTrustedFilePath(profilePath,
                    $"SDK integration profile from {sourceVariableName}", out var diagnostic))
                throw new InvalidOperationException(diagnostic ??
                                                    $"The SDK integration profile from {sourceVariableName} is not trusted.");

            ValidateSdkIntegrationProfileContents(profilePath, sourceVariableName);

            return profilePath;
        }

        private static void ValidateSdkIntegrationProfileContents(string profilePath, string sourceVariableName)
        {
            var profile = new Profile(profilePath);
            var scriptHooks = profile.GetConfiguredScriptHooks();
            if (scriptHooks.Count != 0)
                throw new InvalidOperationException(
                    $"The SDK integration profile from {sourceVariableName} contains script hooks. " +
                    "Use a dedicated non-production profile without PreUp, PostUp, PreDown, or PostDown commands.");
        }

        private static void ValidateAmneziaSdkIntegrationProfile(string profilePath)
        {
            var parser = ParseConfig(profilePath);
            if (!parser.Sections.TryGetValue("Interface", out var interfaceSection))
                throw new InvalidOperationException("The Amnezia SDK profile has no Interface section.");

            var requiredFields = new[] { "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4", "Id", "Ip", "Ib" };
            var missingFields = requiredFields.Where(field => !interfaceSection.Contains(field)).ToArray();
            if (missingFields.Length != 0)
                throw new InvalidOperationException(
                    $"The Amnezia SDK profile must exercise all current fields. Missing: {string.Join(", ", missingFields)}.");
        }

        private static void RunSdkModeSmoke(IWireSockNativeApi api, WireSockManager.Mode mode, string profilePath,
            WireguardBoosterExports.LogPrinter logPrinter)
        {
            var handle = IntPtr.Zero;
            var tunnelCreationAttempted = false;
            var tunnelStarted = false;
            Exception operationException = null;

            try
            {
                handle = api.CreateHandle(mode, logPrinter, WireguardBoosterExports.WgbLogLevel.Error, false, false);
                if (handle == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"wgbooster failed to create a {mode} handle.");

                SetLastErrorForTest(0);
                var networkLockMode = api.GetNetworkLockMode(mode, handle);
                var networkLockError = Marshal.GetLastWin32Error();
                if (networkLockError != 0)
                    throw new Win32Exception(networkLockError,
                        $"wgbooster failed to query the {mode} network-lock mode.");
                if (networkLockMode != WireguardBoosterExports.WgbNetworkLockMode.Disabled &&
                    networkLockMode != WireguardBoosterExports.WgbNetworkLockMode.Enabled)
                    throw new InvalidOperationException(
                        $"wgbooster returned unknown {mode} network-lock mode {(int)networkLockMode}.");

                if (!api.SetNetworkLockMode(mode, handle, WireguardBoosterExports.WgbNetworkLockMode.Disabled))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"wgbooster failed to disable the {mode} network-lock mode.");

                if (!api.SetNetworkLockMode(mode, handle, WireguardBoosterExports.WgbNetworkLockMode.Enabled))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"wgbooster failed to enable the {mode} network-lock mode.");
                SetLastErrorForTest(0);
                var enabledNetworkLockMode = api.GetNetworkLockMode(mode, handle);
                var enabledNetworkLockError = Marshal.GetLastWin32Error();
                if (enabledNetworkLockError != 0 ||
                    enabledNetworkLockMode != WireguardBoosterExports.WgbNetworkLockMode.Enabled)
                    throw new InvalidOperationException(
                        $"wgbooster did not preserve the enabled {mode} network-lock mode.");
                if (!api.SetNetworkLockMode(mode, handle, WireguardBoosterExports.WgbNetworkLockMode.Disabled))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"wgbooster failed to reset the {mode} network-lock mode.");
                SetLastErrorForTest(0);
                var resetNetworkLockMode = api.GetNetworkLockMode(mode, handle);
                var resetNetworkLockError = Marshal.GetLastWin32Error();
                if (resetNetworkLockError != 0 ||
                    resetNetworkLockMode != WireguardBoosterExports.WgbNetworkLockMode.Disabled)
                    throw new InvalidOperationException(
                        $"wgbooster did not reset the {mode} network-lock mode.");

                tunnelCreationAttempted = true;
                if (!api.CreateTunnelFromFile(mode, handle, profilePath))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"wgbooster failed to create the {mode} integration tunnel.");

                if (!api.StartTunnel(mode, handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"wgbooster failed to start the {mode} integration tunnel.");
                tunnelStarted = true;

                if (!api.GetTunnelActive(mode, handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"wgbooster did not report the {mode} integration tunnel active.");

                SetLastErrorForTest(0);
                api.GetTunnelState(mode, handle);
                var stateError = Marshal.GetLastWin32Error();
                if (stateError != 0)
                    throw new Win32Exception(stateError,
                        $"wgbooster failed to query the {mode} integration tunnel state.");
            }
            catch (Exception ex)
            {
                operationException = ex;
            }

            var cleanupException = CleanupSdkModeSmoke(api, mode, handle, tunnelCreationAttempted, tunnelStarted);
            GC.KeepAlive(logPrinter);

            if (operationException != null && cleanupException != null)
                throw new AggregateException(operationException, cleanupException);
            if (operationException != null)
                ExceptionDispatchInfo.Capture(operationException).Throw();
            if (cleanupException != null)
                ExceptionDispatchInfo.Capture(cleanupException).Throw();

            Console.WriteLine($"PASS real wgbooster.dll {mode} lifecycle.");
        }

        private static Exception CleanupSdkModeSmoke(IWireSockNativeApi api, WireSockManager.Mode mode,
            IntPtr handle, bool tunnelCreationAttempted, bool tunnelStarted)
        {
            if (handle == IntPtr.Zero)
                return null;

            var failures = new List<Exception>();
            if (tunnelStarted)
            {
                try
                {
                    if (!api.StopTunnel(mode, handle))
                        failures.Add(new Win32Exception(Marshal.GetLastWin32Error(),
                            $"wgbooster failed to stop the {mode} integration tunnel."));
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            var safeToRelease = !tunnelCreationAttempted;
            if (tunnelCreationAttempted)
            {
                try
                {
                    if (!api.SetNetworkLockMode(mode, handle, WireguardBoosterExports.WgbNetworkLockMode.Disabled))
                        failures.Add(new Win32Exception(Marshal.GetLastWin32Error(),
                            $"wgbooster failed to disable the {mode} network lock during cleanup."));
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }

                try
                {
                    safeToRelease = api.DropTunnel(mode, handle, false);
                    if (!safeToRelease)
                        failures.Add(new Win32Exception(Marshal.GetLastWin32Error(),
                            $"wgbooster failed to drop the {mode} integration tunnel."));
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            if (safeToRelease)
            {
                try
                {
                    api.ReleaseHandle(mode, handle);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            return failures.Count == 0 ? null : new AggregateException(failures);
        }

        private static void EnsureGlobalNetworkLockInactive(bool resetIfActive)
        {
            if (!WireSockManager.TryIsNetworkLockActive(out var active, out var diagnostic))
                throw new InvalidOperationException(diagnostic ?? "Unable to query the global network-lock state.");
            if (!active)
                return;

            if (resetIfActive)
            {
                if (WireSockManager.TryResetNetworkLock(out var resetDiagnostic))
                    throw new InvalidOperationException(
                        "The SDK integration test unexpectedly left the global network lock active; it was reset.");

                throw new InvalidOperationException(resetDiagnostic ??
                                                    "The SDK integration test left the global network lock active and reset failed.");
            }

            throw new InvalidOperationException(
                "The global network lock was already active before the SDK integration test.");
        }

        private static void RunWithFinalCleanup(Action operation, Action cleanup)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));

            Exception operationException = null;
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                operationException = ex;
            }

            Exception cleanupException = null;
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                cleanupException = ex;
            }

            if (operationException != null && cleanupException != null)
                throw new AggregateException(operationException, cleanupException);
            if (operationException != null)
                ExceptionDispatchInfo.Capture(operationException).Throw();
            if (cleanupException != null)
                ExceptionDispatchInfo.Capture(cleanupException).Throw();
        }

        private static void ProfileRejectsEmptyRequiredValues()
        {
            var path = WriteConfig(
                "[Interface]\n" +
                "PrivateKey = \n" +
                "Address = 10.0.0.2/32\n" +
                "\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0\n");

            AssertThrows<ArgumentException>(() => new Profile(path), "empty \"PrivateKey\"");
        }

        private static void ProfileRejectsEmptyAddressListItems()
        {
            var path = WriteConfig(
                "[Interface]\n" +
                $"PrivateKey = {PrivateKey}\n" +
                "Address = 10.0.0.2/32\n" +
                "\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0,\n");

            AssertThrows<FormatException>(() => new Profile(path), "AllowedIPs");
        }

        private static void ProfileValidatesWindowsSafeNames()
        {
            AssertTrue(Profile.IsValidProfileName("office-vpn"), "Expected a simple profile name to be valid.");
            AssertFalse(Profile.IsValidProfileName("CON"), "Reserved DOS device names must be rejected.");
            AssertFalse(Profile.IsValidProfileName("CON.txt"),
                "Reserved DOS device names must be rejected even with extensions.");
            AssertFalse(Profile.IsValidProfileName("COM1.conf"),
                "Reserved COM device names must be rejected even with extensions.");
            AssertFalse(Profile.IsValidProfileName("office "), "Trailing spaces must be rejected.");
            AssertFalse(Profile.IsValidProfileName("office."), "Trailing dots must be rejected.");
            AssertFalse(Profile.IsValidProfileName(@"nested\office"), "Path separators must be rejected.");
            AssertTrue(Profile.IsValidProfileName(new string('a', Profile.MaxProfileNameLength)),
                "Expected the maximum filesystem-safe profile name length to be accepted.");
            AssertFalse(Profile.IsValidProfileName(new string('a', Profile.MaxProfileNameLength + 1)),
                "Profile names that exceed a filesystem component must be rejected.");
            AssertFalse(Profile.IsValidProfileName("CONIN$"), "Console device aliases must be rejected.");
            AssertFalse(Profile.IsValidProfileName("LPT\u00B9"), "Superscript DOS device aliases must be rejected.");
        }

        private static void ProfilePathRejectsUnsafeNames()
        {
            WithTemporaryConfigFolder(() =>
            {
                AssertThrows<ArgumentException>(() => Profile.GetProfilePath(@"..\office"), "Profile name");
                AssertThrows<ArgumentException>(() => Profile.GetProfilePath("CON"), "Profile name");

                var profilePath = Profile.GetProfilePath("office");
                var configRoot = Path.GetFullPath(Global.ConfigsFolder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                AssertTrue(profilePath.StartsWith(configRoot, StringComparison.OrdinalIgnoreCase),
                    "Expected profile path to stay inside the configured profile folder.");
            });
        }

        private static void ProfileCatalogReportsEnumerationFailures()
        {
            var expected = new IOException("profile storage unavailable");
            var failedCatalog = new ProfileCatalog(() => throw expected);
            var failedResult = failedCatalog.Load();

            AssertFalse(failedResult.Succeeded, "Expected profile enumeration failures to remain observable.");
            AssertTrue(ReferenceEquals(expected, failedResult.Exception),
                "Expected the original profile enumeration exception to be preserved for diagnostics.");
            AssertEqual(0, failedResult.Profiles.Count);

            var catalog = new ProfileCatalog(() => new[] { "zeta", "Alpha" });
            var result = catalog.Load();
            AssertTrue(result.Succeeded, "Expected a successful profile catalog result.");
            AssertEqual("Alpha,zeta", string.Join(",", result.Profiles));
        }

        private static void ProfileCatalogRejectsCaseInsensitiveDuplicates()
        {
            var catalog = new ProfileCatalog(() => new[] { "Office", "office" });
            var result = catalog.Load();

            AssertFalse(result.Succeeded,
                "Expected profile names that differ only by case to make the catalog ambiguous.");
            AssertTrue(result.Exception is InvalidDataException,
                "Expected a clear data-integrity error for case-insensitive duplicate profile names.");
            AssertTrue(result.Exception.Message.Contains("Office") && result.Exception.Message.Contains("office"),
                "Expected the duplicate-profile diagnostic to identify both files.");
        }

        private static void ProfileRejectsOversizedInstalledFiles()
        {
            WithTemporaryConfigFolder(() =>
            {
                var path = Profile.GetProfilePath("oversized");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.SetLength(Profile.MaxProfileSizeBytes + 1);

                AssertThrows<InvalidDataException>(() => new Profile(path), "maximum supported size");
                AssertFalse(Profile.GetProfiles().Contains("oversized", StringComparer.OrdinalIgnoreCase),
                    "Expected oversized installed profiles not to enter the selectable catalog.");

                var nativeApi = new FakeWireSockNativeApi();
                using (var manager = new WireSockManager(nativeApi))
                {
                    AssertFalse(manager.Connect("oversized"),
                        "Expected the final manager activation boundary to reject oversized profiles.");
                    AssertEqual(0, nativeApi.GetHandleCount);
                    AssertTrue(manager.LastError?.Contains("maximum supported size") == true,
                        "Expected a useful oversized-profile activation diagnostic.");
                }
            });
        }

        private static void ProfileReportsConfiguredScriptHooks()
        {
            var path = WriteConfig(
                "[Interface]\n" +
                $"PrivateKey = {PrivateKey}\n" +
                "Address = 10.0.0.2/32\n" +
                "PostUp = powershell.exe -NoProfile -Command Write-Host test\n" +
                "\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0\n");

            var hooks = new Profile(path).GetConfiguredScriptHooks();

            AssertEqual(1, hooks.Count);
            AssertEqual("PostUp", hooks[0].Key);
            AssertTrue(hooks[0].Value.Contains("powershell.exe"), "Expected the script command to be reported.");
        }

        private static void ScriptHookWarningPreservesAndEscapesCompleteCommands()
        {
            var hiddenSuffix = "Remove-Item C:\\important";
            var longCommand = new string('a', 200) + hiddenSuffix;
            var hooks = new[]
            {
                new KeyValuePair<string, string>("PreUp", longCommand),
                new KeyValuePair<string, string>("PostDown", "first\r\nsecond\t\u202Ehidden\u2028line")
            };

            var summary = ProfileScriptWarning.FormatHookSummary(hooks);
            AssertTrue(summary.Contains(ProfileScriptWarning.EscapeForDisplay(hiddenSuffix)),
                "Expected the warning to display the complete command rather than truncate its suffix.");
            AssertTrue(summary.Contains(@"first\r\nsecond\t\u202Ehidden\u2028line"),
                "Expected line breaks, tabs, separators, and bidirectional controls to be rendered visibly.");
            AssertFalse(summary.Contains("\u202E"),
                "Expected the warning not to contain an active bidirectional override character.");
            AssertFalse(summary.Contains("\u2028"),
                "Expected the warning not to contain an active Unicode line separator.");

            var literalEscape = ProfileScriptWarning.EscapeForDisplay(@"echo \u202E");
            AssertEqual(@"echo \\u202E", literalEscape);
        }

        private static void ProfileEnumerationAcceptsUppercaseConfExtension()
        {
            WithTemporaryConfigFolder(() =>
            {
                File.WriteAllText(Path.Combine(Global.ConfigsFolder, "Office.CONF"), string.Empty);
                File.WriteAllText(Path.Combine(Global.ConfigsFolder, "Archive.config"), string.Empty);
                File.WriteAllText(Path.Combine(Global.ConfigsFolder, "Backup.conf-old"), string.Empty);

                var profiles = Profile.GetProfiles().ToList();
                AssertTrue(profiles.Contains("Office"), "Expected .CONF profiles to be listed on Windows.");
                AssertEqual(1, profiles.Count);
            });
        }

        private static void ProfileEnumerationCreatesMissingOverriddenConfigFolder()
        {
            var originalConfigsFolder = Global.ConfigsFolder;
            var originalOverride = Global.AllowUnsecuredConfigFolderOverrideForTests;
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"),
                "Configs");

            try
            {
                Global.ConfigsFolder = directory;
                Global.AllowUnsecuredConfigFolderOverrideForTests = true;

                var profiles = Profile.GetProfiles().ToList();

                AssertEqual(0, profiles.Count);
                AssertTrue(Directory.Exists(directory),
                    "Expected profile enumeration to create a missing overridden config folder.");
            }
            finally
            {
                Global.ConfigsFolder = originalConfigsFolder;
                Global.AllowUnsecuredConfigFolderOverrideForTests = originalOverride;

                try
                {
                    var root = Path.GetDirectoryName(directory);
                    if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch
                {
                    // Best-effort cleanup must not hide the original test failure.
                }
            }
        }

        private static void ProfileEnumerationRejectsOversizedFolders()
        {
            WithTemporaryConfigFolder(() =>
            {
                for (var index = 0; index <= Profile.MaxProfileCatalogEntries; index++)
                    File.WriteAllText(Path.Combine(Global.ConfigsFolder, $"entry-{index:D4}.tmp"), string.Empty);

                AssertThrows<InvalidDataException>(
                    () => Profile.GetProfiles(),
                    $"more than {Profile.MaxProfileCatalogEntries}");
            });
        }

        private static void ProfileRejectsReparsePointProfileFiles()
        {
            WithTemporaryConfigFolder(() =>
            {
                var target = Path.Combine(Global.ConfigsFolder, "target.conf");
                var link = Path.Combine(Global.ConfigsFolder, "linked.conf");
                File.WriteAllText(target, ValidConfig());

                if (!TryCreateProfileReparsePoint(link, target, out var isFileLink))
                {
                    SkipOrFail("profile reparse point creation unavailable; reparse profile check not exercised.");
                    return;
                }

                AssertTrue(Profile.ProfilePathExists(link),
                    "Expected profile path existence checks to detect reparse point profile paths.");
                if (isFileLink)
                {
                    var profiles = Profile.GetProfiles().ToList();
                    AssertFalse(profiles.Contains("linked", StringComparer.OrdinalIgnoreCase),
                        "Expected reparse point profiles to be excluded from enumeration.");
                }
                AssertThrows<IOException>(() => new Profile(link), "reparse point");
            });
        }

        private static void ProfileRejectsDirectoryProfilePaths()
        {
            WithTemporaryConfigFolder(() =>
            {
                var profileDirectory = Path.Combine(Global.ConfigsFolder, "folder.conf");
                Directory.CreateDirectory(profileDirectory);

                var profiles = Profile.GetProfiles().ToList();
                AssertFalse(profiles.Contains("folder", StringComparer.OrdinalIgnoreCase),
                    "Expected directory profile paths to be excluded from enumeration.");
                AssertThrows<IOException>(() => Profile.EnsureRegularProfileFile(profileDirectory), "directory");
                AssertThrows<IOException>(() => new Profile(profileDirectory), "directory");
            });
        }

        private static void ProfileReportsMissingProfilePathsClearly()
        {
            WithTemporaryConfigFolder(() =>
            {
                var missingProfile = Path.Combine(Global.ConfigsFolder, "missing.conf");

                AssertFalse(Profile.IsRegularProfileFile(missingProfile, out var diagnostic),
                    "Expected missing profile paths to be rejected.");
                AssertTrue(diagnostic.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected a clear missing-file diagnostic, got '{diagnostic}'.");
            });
        }

        private static void ProfileReportsMalformedProfilePathsConsistently()
        {
            var malformedPath = "invalid\0profile.conf";

            AssertFalse(Profile.IsRegularProfileFile(malformedPath, out var diagnostic),
                "Expected malformed profile paths to be rejected.");
            AssertTrue(!string.IsNullOrWhiteSpace(diagnostic),
                "Expected malformed profile paths to produce a diagnostic.");
            AssertFalse(diagnostic.Contains("\0"),
                $"Expected malformed profile diagnostics to escape embedded NULs, got '{diagnostic}'.");
            AssertTrue(diagnostic.Contains("\\0"),
                $"Expected malformed profile diagnostics to include the escaped NUL marker, got '{diagnostic}'.");
            AssertThrows<IOException>(() => Profile.EnsureRegularProfileFile(malformedPath), "profile");
        }

        private static void ParserAcceptsOnlyExactWireSockDirectivePrefixes()
        {
            var path = WriteConfig(
                "[Interface]\n" +
                "#@ws:BypassLanTraffic = true\n" +
                "#@ws VirtualAdapterMode = false\n" +
                "#@WS:VirtualAdapterMode = true\n");

            var section = ParseConfig(path).GetSection("Interface");

            AssertTrue(section.ContainsKey("BypassLanTraffic"), "Expected #@ws: directive to become a normal key.");
            AssertFalse(section.ContainsKey("VirtualAdapterMode"),
                "Expected non-SDK WireSock directive prefixes to remain comments.");
            AssertEqual("true", section["BypassLanTraffic"]);
        }

        private static void ParserMatchesSdkCasing()
        {
            var path = WriteConfig("[interface]\nprivatekey = value\n");
            var parser = ParseConfig(path);

            AssertTrue(parser.GetSectionNames().Contains("interface", StringComparer.Ordinal),
                "Expected the parser to preserve section casing.");
            AssertFalse(parser.GetSectionNames().Contains("Interface", StringComparer.Ordinal),
                "Expected section lookup to match the case-sensitive SDK parser.");
            AssertTrue(parser.GetSection("interface").ContainsKey("privatekey"),
                "Expected the parser to preserve key casing.");
            AssertFalse(parser.GetSection("interface").ContainsKey("PrivateKey"),
                "Expected key lookup to match the case-sensitive SDK parser.");
        }

        private static void ParserMatchesSdkLastSectionWinsBehavior()
        {
            var path = WriteConfig(
                "[Interface]\n" +
                $"PrivateKey = {PrivateKey}\n" +
                "Address = 10.0.0.2/32\n" +
                "\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0\n" +
                "\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = backup.example.com:51820\n" +
                "AllowedIPs = ::/0\n");

            try
            {
                var peer = ParseConfig(path).GetSection("Peer");
                AssertEqual("backup.example.com:51820", peer["Endpoint"]);
                AssertEqual("::/0", peer["AllowedIPs"]);
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static void ParserRejectsMalformedLines()
        {
            var path = WriteConfig(
                "[Interface]\n" +
                "PrivateKey\n");

            try
            {
                AssertThrows<FormatException>(() => ParseConfig(path), "line 2");
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static void ParserRejectsKeysBeforeSections()
        {
            var path = WriteConfig("PrivateKey = value\n");

            try
            {
                AssertThrows<FormatException>(() => ParseConfig(path), "before any section");
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static void ParserRejectsEmptySectionNames()
        {
            var path = WriteConfig("[]\n");

            try
            {
                AssertThrows<FormatException>(() => ParseConfig(path), "section name is empty");
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static void ParserTrimsSectionNames()
        {
            var path = WriteConfig(
                "[Interface ]\n" +
                $"PrivateKey = {PrivateKey}\n" +
                "Address = 10.0.0.2/32\n" +
                "\n" +
                "[ Peer ]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0\n");

            try
            {
                var parser = ParseConfig(path);
                AssertTrue(parser.GetSectionNames().Contains("Interface", StringComparer.OrdinalIgnoreCase),
                    "Expected parser to trim trailing whitespace in section names.");
                AssertTrue(parser.GetSectionNames().Contains("Peer", StringComparer.OrdinalIgnoreCase),
                    "Expected parser to trim leading and trailing whitespace in section names.");
                new Profile(path);
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static void ProfileAcceptsAmneziaPassthroughOptions()
        {
            var path = WriteConfig(
                "[Interface]\n" +
                $"PrivateKey = {PrivateKey}\n" +
                "Address = 10.0.0.2/32\n" +
                "#@ws:H1 = 10-14\n" +
                "#@ws:H2 = 16-32\n" +
                "#@ws:H3 = 40\n" +
                "#@ws:H4 =\n" +
                "#@ws:Jmin = 4\n" +
                "#@ws:Jmax = 10\n" +
                "#@ws:S1 = 1279\n" +
                "#@ws:S2 = 0\n" +
                "#@ws:S3 = 1279\n" +
                "#@ws:S4 = 32\n" +
                "#@ws:Id = example.com\n" +
                "#@ws:Ib = chrome\n" +
                "#@ws:Ip = quic\n" +
                "\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0, ::/0\n");

            var parser = ParseConfig(path);
            var interfaceSection = parser.GetSection("Interface");

            AssertEqual("10-14", interfaceSection["H1"]);
            AssertEqual("16-32", interfaceSection["H2"]);
            AssertEqual(string.Empty, interfaceSection["H4"]);
            AssertEqual("1279", interfaceSection["S1"]);
            AssertEqual("chrome", interfaceSection["Ib"]);
            new Profile(path);
        }

        private static void ProfileRejectsInvalidAmneziaPassthroughOptions()
        {
            AssertProfileRejectsInterfaceOption("#@ws:H1 = 4-1", "H1");
            AssertProfileRejectsInterfaceOption("#@ws:H1 = 0x10", "H1");
            AssertProfileRejectsInterfaceOption("#@ws:S1 = 1280", "S1");
            AssertProfileRejectsInterfaceOption("#@ws:S1 = 0x20", "S1");
            AssertProfileRejectsInterfaceOption("#@ws:Jc = +1", "Jc");
            AssertProfileRejectsInterfaceOption("#@ws:S3 = 1280", "S3");
            AssertProfileRejectsInterfaceOption("#@ws:Ip = ftp", "Ip");
            AssertProfileRejectsInterfaceOption("#@ws:Ib = safari", "Ib");
            AssertProfileRejectsInterfaceOption("#@ws:Jmin = 10\n#@ws:Jmax = 4", "Jmin");
            AssertProfileRejectsInterfaceOption("#@ws:Jmin = 10\n#@ws:Jmax = 10", "less than");
        }

        private static void ParserRejectsSdkIncompatibleByteOrderMarks()
        {
            var path = WriteConfig(string.Empty);
            try
            {
                File.WriteAllText(path, ValidConfig(), new UTF8Encoding(true));
                AssertThrows<FormatException>(() => ParseConfig(path), "BOM");
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static void ParserMatchesSdkDuplicateKeyProjection()
        {
            var path = WriteConfig(
                "[Interface]\n" +
                "PrivateKey = invalid-first-value\n" +
                $"PrivateKey = {PrivateKey}\n" +
                "Address = 10.0.0.2/32\n" +
                "Address = fd00::2/128\n" +
                "MTU = invalid-first-value\n" +
                "MTU = 1400\n" +
                "PreUp = first.cmd\n" +
                "PreUp = second.cmd\n\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = invalid-first-value\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0\n" +
                "AllowedIPs = ::/0\n");

            try
            {
                var parser = ParseConfig(path);
                var interfaceSection = parser.GetSection("Interface");
                var peerSection = parser.GetSection("Peer");

                AssertEqual(PrivateKey, interfaceSection["PrivateKey"]);
                AssertEqual("1400", interfaceSection["MTU"]);
                AssertEqual("10.0.0.2/32, fd00::2/128", interfaceSection["Address"]);
                AssertEqual("first.cmd, second.cmd", interfaceSection["PreUp"]);
                AssertEqual("example.com:51820", peerSection["Endpoint"]);
                AssertEqual("0.0.0.0/0, ::/0", peerSection["AllowedIPs"]);
                new Profile(path);
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static void ParserRejectsMalformedUtf8()
        {
            var path = WriteConfig(string.Empty);
            try
            {
                var validPrefix = Encoding.UTF8.GetBytes("[Interface]\nPrivateKey = ");
                var bytes = validPrefix.Concat(new byte[] { 0xc3, 0x28 }).ToArray();
                File.WriteAllBytes(path, bytes);
                AssertThrows<DecoderFallbackException>(
                    () => ParseConfig(path), null);
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static void ProfileValidatesAmneziaOptionGroups()
        {
            AssertProfileRejectsInterfaceOption("#@ws:S1 = 1", "incomplete");
            AssertProfileRejectsInterfaceOption("#@ws:S3 = 1", "incomplete");
            AssertProfileRejectsInterfaceOption("#@ws:Jmin = 10", "specified together");
            AssertProfileRejectsInterfaceOption("#@ws:Jd = 10", "require");
        }

        private static void ProfileValidatesProtocolImitationCombinations()
        {
            var chromiumPath = WriteProfileWithInterfaceOptions(
                "#@ws:Id = ***\n#@ws:Ip = quic\n#@ws:Ib = chromium");
            var firefoxPath = WriteProfileWithInterfaceOptions(
                "#@ws:Id = example.com\n#@ws:Ip = quic\n#@ws:Ib = ff");
            var aliasPath = WriteProfileWithInterfaceOptions(
                "#@ws:Id = example.com\n#@ws:Ip = stun_request");

            try
            {
                new Profile(chromiumPath);
                new Profile(firefoxPath);
                new Profile(aliasPath);
            }
            finally
            {
                TryDeleteFile(chromiumPath);
                TryDeleteFile(firefoxPath);
                TryDeleteFile(aliasPath);
            }

            AssertProfileRejectsInterfaceOption("#@ws:Ip = quic", "require");
            AssertProfileRejectsInterfaceOption("#@ws:Id = a..b\n#@ws:Ip = sip", "SIP imitation host");
            AssertProfileRejectsInterfaceOption("#@ws:Id = a..b\n#@ws:Ip = sip_request", "SIP imitation host");
            AssertProfileRejectsInterfaceOption(
                $"#@ws:Id = {new string('a', 64)}.com\n#@ws:Ip = sip", "SIP imitation host");

            AssertProfileRejectsInterfaceOption(
                "#@ws:S1 = 0\n#@ws:S2 = 0\n#@ws:H1 = 10-20\n#@ws:H2 = 20-30\n#@ws:H3 = 40\n#@ws:H4 = 50",
                "overlapping");
            AssertProfileRejectsInterfaceOption(
                "#@ws:S1 = 0\n#@ws:S2 = 0\n#@ws:H1 =\n#@ws:H2 = 1\n#@ws:H3 = 3\n#@ws:H4 = 4",
                "overlapping");
        }

        private static void ProfileRejectsUnsupportedDirectDllDirectives()
        {
            AssertProfileRejectsInterfaceOption("#@ws:BypassLanTraffic = true", "DisallowedIPs");
            AssertProfileRejectsInterfaceOption("Table = auto", "not supported");
            AssertProfileRejectsInterfaceOption("#@ws:I1 = value", "not supported");

            var legacyUsernamePath = WriteConfig(
                "[Interface]\n" +
                $"PrivateKey = {PrivateKey}\n" +
                "Address = 10.0.0.2/32\n\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0\n" +
                "#@ws:Socks5Username = user\n");
            try
            {
                AssertThrows<FormatException>(() => new Profile(legacyUsernamePath), "Socks5ProxyUsername");
            }
            finally
            {
                TryDeleteFile(legacyUsernamePath);
            }
        }

        private static void ProfileValidatesCurrentSdkNumericRanges()
        {
            var path = WriteConfig(
                "[Interface]\n" +
                $"PrivateKey = {PrivateKey}\n" +
                "Address = 10.0.0.2/32\n" +
                "ListenPort = 0\n" +
                "ScriptExecTimeout = 4294967295\n" +
                "EnableDefaultGateway = true\n\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0\n" +
                "PersistentKeepalive = 4294967295\n");

            try
            {
                new Profile(path);
            }
            finally
            {
                TryDeleteFile(path);
            }

            AssertProfileRejectsInterfaceOption("ScriptExecTimeout = 4294967296", "4294967295");
            AssertProfileRejectsInterfaceOption("ListenPort = 65536", "65535");
            AssertProfileRejectsInterfaceOption("EnableDefaultGateway = TRUE", "exactly");
        }

        private static void ProfileRejectsSdkCasingMismatches()
        {
            AssertProfileRejectsInterfaceOption("#@ws:jc = 1", "expects \"Jc\"");

            var path = WriteConfig(
                "[interface]\n" +
                $"PrivateKey = {PrivateKey}\n" +
                "Address = 10.0.0.2/32\n\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0\n");
            try
            {
                AssertThrows<ArgumentException>(() => new Profile(path), "Interface");
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static void InterfaceExtensionValidationRulesAreShared()
        {
            AssertTrue(ConfigValueValidator.TryGetInterfaceExtensionRule("h1", out var h1),
                "Expected H1 to be registered as a shared interface extension rule.");
            AssertTrue(h1.IsValid("1-4"), "Expected H1 to accept ascending ranges.");
            AssertFalse(h1.IsValid("4-1"), "Expected H1 to reject descending ranges.");

            AssertTrue(ConfigValueValidator.TryGetInterfaceExtensionRule("Ib", out var ib),
                "Expected Ib to be registered as a shared interface extension rule.");
            AssertTrue(ib.IsValid("chrome"), "Expected Ib to accept supported browser profiles.");
            AssertTrue(ib.IsValid("chromium"), "Expected Ib to accept the SDK Chromium alias.");
            AssertTrue(ib.IsValid("ff"), "Expected Ib to accept the SDK Firefox alias.");
            AssertFalse(ib.IsValid("safari"), "Expected Ib to reject unsupported browser profiles.");

            AssertTrue(ConfigValueValidator.TryGetInterfaceExtensionRule("Id", out var id),
                "Expected Id to be registered as a shared interface extension rule.");
            AssertTrue(id.IsValid("***"), "Expected non-SIP Id values to follow the SDK byte-length contract.");
        }

        private static void AssertProfileRejectsInterfaceOption(string optionLine, string messagePart)
        {
            var path = WriteProfileWithInterfaceOptions(optionLine);

            try
            {
                AssertThrows<FormatException>(() => new Profile(path), messagePart);
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static string WriteProfileWithInterfaceOptions(string optionLines)
        {
            return WriteConfig(
                "[Interface]\n" +
                $"PrivateKey = {PrivateKey}\n" +
                "Address = 10.0.0.2/32\n" +
                optionLines + "\n" +
                "\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0\n");
        }

        private static void StatsFormattingHandlesExtremeValues()
        {
            AssertFalse(string.IsNullOrWhiteSpace(ulong.MaxValue.AsHumanReadable()),
                "Expected large byte counters to format without overflowing the suffix list.");
            AssertFalse(string.IsNullOrWhiteSpace(long.MaxValue.AsTimeAgo()),
                "Expected large handshake ages to format without narrowing to Int32.");
        }

        private static void StatsFormattingHandlesMissingHandshakes()
        {
            var value = (-1L).AsHandshakeAge();
            AssertFalse(value.Contains("1"),
                $"Expected the native no-handshake sentinel not to be rendered as one second, got '{value}'.");
            AssertFalse(string.IsNullOrWhiteSpace(value), "Expected a localized no-handshake status.");
        }

        private static void TimeFormattingUsesPluralHours()
        {
            var value = TimeSpan.FromHours(2).AsTimeAgo();

            AssertTrue(value.Contains("2"), "Expected two-hour durations to include the hour count.");
            AssertTrue(value.IndexOf("hours", StringComparison.OrdinalIgnoreCase) >= 0,
                $"Expected two-hour durations to use a plural hour label, got '{value}'.");
        }

        private static void TimeFormattingUsesSingularHourForPartialSecondHour()
        {
            var value = TimeSpan.FromMinutes(90).AsTimeAgo();

            AssertTrue(value.IndexOf("an hour", StringComparison.OrdinalIgnoreCase) >= 0,
                $"Expected 90-minute durations to use the singular hour label, got '{value}'.");
        }

        private static void TimeFormattingHandlesFutureValues()
        {
            var value = TimeSpan.FromHours(-2).AsTimeAgo();

            AssertTrue(!value.Contains("-"), $"Expected future durations to format without a negative sign, got '{value}'.");
            AssertTrue(value.Contains("2"), "Expected future two-hour durations to include the absolute hour count.");
            AssertTrue(value.IndexOf("hours", StringComparison.OrdinalIgnoreCase) >= 0,
                $"Expected future two-hour durations to use a plural hour label, got '{value}'.");
        }

        private static void GlobalConfigFolderContainmentHandlesDriveRoots()
        {
            var isSameOrChildPath = typeof(Global).GetMethod("IsSameOrChildPath",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (isSameOrChildPath == null)
                throw new InvalidOperationException("IsSameOrChildPath helper was not found.");

            var root = Path.GetPathRoot(Environment.SystemDirectory);
            var child = Path.Combine(root, "Windows");

            AssertTrue((bool)isSameOrChildPath.Invoke(null, new object[] { root, root }),
                "Expected a root path to be treated as itself.");
            AssertTrue((bool)isSameOrChildPath.Invoke(null, new object[] { child, root }),
                "Expected a child of a drive root to be detected.");
        }

        private static void GlobalRejectsUnsecuredConfigFolderOverridesByDefault()
        {
            var originalConfigsFolder = Global.ConfigsFolder;
            var originalOverride = Global.AllowUnsecuredConfigFolderOverrideForTests;
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));

            try
            {
                Global.ConfigsFolder = directory;
                Global.AllowUnsecuredConfigFolderOverrideForTests = false;

                AssertThrows<InvalidOperationException>(() => Global.EnsureConfigsFolder(), "secured folder");
            }
            finally
            {
                Global.ConfigsFolder = originalConfigsFolder;
                Global.AllowUnsecuredConfigFolderOverrideForTests = originalOverride;
            }
        }

        private static void ReleaseVersionParserHandlesSemVerTags()
        {
            AssertTrue(ReleaseVersionParser.TryParseReleaseTag("V1.2.3", out var upperV),
                "Expected uppercase V-prefixed tags to parse.");
            AssertEqual("1.2.3", upperV.ToString());

            AssertTrue(ReleaseVersionParser.TryParseReleaseTag("v1.2.3-beta.1", out var prerelease),
                "Expected prerelease tags to parse by comparing their numeric release version.");
            AssertEqual("1.2.3", prerelease.ToString());

            AssertTrue(ReleaseVersionParser.TryParseReleaseTag("1.2.3+build.5", out var metadata),
                "Expected build metadata tags to parse by comparing their numeric release version.");
            AssertEqual("1.2.3", metadata.ToString());

            AssertFalse(ReleaseVersionParser.TryParseReleaseTag("not-a-version", out _),
                "Expected invalid release tags to be rejected.");
        }

        private static void BoundedResponseReaderRejectsDeclaredAndStreamedOverflow()
        {
            var exact = Encoding.UTF8.GetBytes("12345678");
            using (var stream = new MemoryStream(exact, false))
                AssertEqual("12345678", BoundedStreamReader.ReadUtf8ToEnd(stream, exact.Length));

            using (var oversized = new MemoryStream(new byte[9], false))
                AssertThrows<InvalidDataException>(
                    () => BoundedStreamReader.ReadUtf8ToEnd(oversized, 8),
                    "maximum supported size");

            using (var chunked = new ChunkedReadStream(new byte[9], 3))
                AssertThrows<InvalidDataException>(
                    () => BoundedStreamReader.ReadUtf8ToEnd(chunked, 8),
                    "maximum supported size");
        }

        private static void ProgramPathNormalizationPreservesDriveRoots()
        {
            var normalize = typeof(WireSockUI.Program).GetMethod("NormalizePathDirectory",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (normalize == null)
                throw new InvalidOperationException("NormalizePathDirectory helper was not found.");

            var root = Path.GetPathRoot(Environment.SystemDirectory);
            var normalizedRoot = (string)normalize.Invoke(null, new object[] { root });
            var normalizedWithQuotes = (string)normalize.Invoke(null, new object[] { $"\"{root}\"" });
            var normalizedChild = (string)normalize.Invoke(null, new object[] { Path.Combine(root, "Windows") + "\\" });

            AssertEqual(root, normalizedRoot);
            AssertEqual(root, normalizedWithQuotes);
            AssertEqual(Path.Combine(root, "Windows"), normalizedChild);
        }

        private static void ProgramRejectsUntrustedApplicationPayloads()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var executable = Path.Combine(directory, "WireSockUI.exe");

            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(executable, string.Empty);
                File.WriteAllText(executable + ".config", "<configuration />");
                File.WriteAllText(Path.Combine(directory, "dependency.dll"), string.Empty);

                AssertFalse(WireSockUI.Program.TryValidateApplicationPayload(executable, out var diagnostic),
                    "Expected an elevated application payload in a user-writable directory to be rejected.");
                AssertTrue(!string.IsNullOrWhiteSpace(diagnostic) &&
                           diagnostic.IndexOf("writable", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected an actionable application trust diagnostic, got '{diagnostic}'.");
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }

        private static void ProgramEnumeratesNestedApplicationPayloads()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var localeDirectory = Path.Combine(directory, "zh-Hant");
            var resourceAssembly = Path.Combine(localeDirectory, "Microsoft.Win32.TaskScheduler.resources.dll");

            try
            {
                Directory.CreateDirectory(localeDirectory);
                File.WriteAllText(Path.Combine(directory, "WireSockUI.exe"), string.Empty);
                File.WriteAllText(resourceAssembly, string.Empty);

                AssertTrue(WireSockUI.Program.TryEnumerateApplicationPayloadEntries(
                        directory, out var files, out var directories, out var diagnostic),
                    diagnostic ?? "Expected nested application payload enumeration to succeed.");
                AssertTrue(files.Contains(resourceAssembly, StringComparer.OrdinalIgnoreCase),
                    "Expected nested resource assemblies to be included in payload validation.");
                AssertTrue(directories.Contains(localeDirectory, StringComparer.OrdinalIgnoreCase),
                    "Expected locale directories to be included in payload validation.");
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }

        private static void ProgramBoundsApplicationPayloadEnumeration()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(directory);
                for (var index = 0; index <= WireSockUI.Program.MaxApplicationPayloadEntries; index++)
                    File.WriteAllText(Path.Combine(directory, $"payload-{index:D5}.txt"), string.Empty);

                AssertFalse(WireSockUI.Program.TryEnumerateApplicationPayloadEntries(
                        directory, out _, out _, out var diagnostic),
                    "Expected oversized application payload enumeration to fail closed.");
                AssertTrue(diagnostic?.IndexOf("more than", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected an actionable payload limit diagnostic, got '{diagnostic}'.");
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }

        private static void ProgramDistinguishesBinaryArchitectures()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var x64Path = Path.Combine(directory, "x64.dll");
            var arm64Path = Path.Combine(directory, "arm64.dll");

            try
            {
                Directory.CreateDirectory(directory);
                WriteMinimalPortableExecutable(x64Path, 0x8664);
                WriteMinimalPortableExecutable(arm64Path, 0xaa64);

                AssertTrue(WindowsBinaryArchitectureInfo.TryReadPortableExecutableArchitecture(
                        x64Path, out var x64, out var x64Diagnostic),
                    x64Diagnostic ?? "Expected the x64 image to parse.");
                AssertTrue(WindowsBinaryArchitectureInfo.TryReadPortableExecutableArchitecture(
                        arm64Path, out var arm64, out var arm64Diagnostic),
                    arm64Diagnostic ?? "Expected the ARM64 image to parse.");

                AssertEqual((int)WindowsBinaryArchitecture.X64, (int)x64);
                AssertEqual((int)WindowsBinaryArchitecture.Arm64, (int)arm64);
                AssertTrue(WindowsBinaryArchitectureInfo.AreCompatible(x64, x64),
                    "Expected matching PE architectures to be compatible.");
                AssertFalse(WindowsBinaryArchitectureInfo.AreCompatible(x64, arm64),
                    "Expected x64 and ARM64 images to be rejected as incompatible.");
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }

        private static void WriteMinimalPortableExecutable(string path, ushort machine)
        {
            var image = new byte[70];
            image[0] = (byte)'M';
            image[1] = (byte)'Z';
            BitConverter.GetBytes(64).CopyTo(image, 0x3c);
            image[64] = (byte)'P';
            image[65] = (byte)'E';
            BitConverter.GetBytes(machine).CopyTo(image, 68);
            File.WriteAllBytes(path, image);
        }

        private static void ProgramRejectsUserWritableWireSockLibraryDirectories()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                File.WriteAllText(Path.Combine(directory, "wgbooster.dll"), string.Empty);

                try
                {
                    var security = Directory.GetAccessControl(directory);
                    security.AddAccessRule(new FileSystemAccessRule(
                        WindowsIdentity.GetCurrent().User,
                        FileSystemRights.Modify,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                    Directory.SetAccessControl(directory, security);
                }
                catch (UnauthorizedAccessException)
                {
                    // Temporary test folders are normally user-writable already; explicit ACL setup is best effort.
                }

                var accepted = WireSockUI.Program.TryValidateWireSockLibraryDirectory(
                    directory, out var validatedDirectory);

                AssertFalse(accepted, "Expected user-writable WireSock library directories to be rejected.");
                AssertTrue(validatedDirectory == null,
                    "Expected rejected WireSock library directories not to return a path.");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    // Best-effort cleanup must not hide the original test failure.
                }
            }
        }

        private static void ProgramDetectsUserWritableWireSockLibraryFiles()
        {
            var isPotentiallyUserWritableFile = typeof(WireSockUI.Program).GetMethod("IsPotentiallyUserWritableFile",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (isPotentiallyUserWritableFile == null)
                throw new InvalidOperationException("IsPotentiallyUserWritableFile helper was not found.");

            var file = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", $"{Guid.NewGuid():N}.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(file));

            try
            {
                File.WriteAllText(file, string.Empty);

                var userWritable = (bool)isPotentiallyUserWritableFile.Invoke(null, new object[] { file });

                AssertTrue(userWritable, "Expected user-writable WireSock library files to be detected.");
            }
            finally
            {
                try
                {
                    if (File.Exists(file))
                        File.Delete(file);
                }
                catch
                {
                    // Best-effort cleanup must not hide the original test failure.
                }
            }
        }

        private static void ProgramRecognizesAdministrativeOwnerSids()
        {
            var hasTrustedOwner = typeof(WireSockUI.Program).GetMethod("HasTrustedOwner",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (hasTrustedOwner == null)
                throw new InvalidOperationException("HasTrustedOwner helper was not found.");

            var isTrustedAdministrativeSid = typeof(WireSockUI.Program).GetMethod("IsTrustedAdministrativeSid",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (isTrustedAdministrativeSid == null)
                throw new InvalidOperationException("IsTrustedAdministrativeSid helper was not found.");

            var accountDomainSid = new SecurityIdentifier("S-1-5-21-1000000001-1000000002-1000000003");
            var administratorSid = new SecurityIdentifier(
                WellKnownSidType.AccountAdministratorSid,
                accountDomainSid);
            var domainAdminsSid = new SecurityIdentifier(
                WellKnownSidType.AccountDomainAdminsSid,
                accountDomainSid);
            var ordinaryUserSid = new SecurityIdentifier($"{accountDomainSid.Value}-1100");

            var security = new DirectorySecurity();
            security.SetOwner(administratorSid);
            AssertTrue((bool)hasTrustedOwner.Invoke(null, new object[] { security }),
                "Expected the built-in account-domain Administrator owner to be trusted.");

            security.SetOwner(domainAdminsSid);
            AssertTrue((bool)hasTrustedOwner.Invoke(null, new object[] { security }),
                "Expected the account-domain Domain Admins owner to be trusted.");

            security.SetOwner(ordinaryUserSid);
            AssertFalse((bool)hasTrustedOwner.Invoke(null, new object[] { security }),
                "Expected an ordinary account-domain owner to remain untrusted.");

            AssertFalse((bool)isTrustedAdministrativeSid.Invoke(null, new object[]
                { new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null) }),
                "Expected LocalService not to be trusted as an elevated SDK library writer.");
            AssertFalse((bool)isTrustedAdministrativeSid.Invoke(null, new object[]
                { new SecurityIdentifier("S-1-5-80-100-200-300-400-500") }),
                "Expected arbitrary service SIDs not to be trusted as elevated SDK library writers.");
            AssertTrue((bool)isTrustedAdministrativeSid.Invoke(null, new object[]
                {
                    new SecurityIdentifier(
                        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
                }), "Expected the exact TrustedInstaller SID to remain trusted.");
        }

        private static void GlobalFailsClosedOnConfigurationDirectoryReparsePoints()
        {
            var root = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var target = root + ".target";
            var link = Path.Combine(root, "unsafe-child");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(target);

            try
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                if (!TryCreateDirectoryJunction(link, target))
                {
                    SkipOrFail("configuration directory reparse point creation unavailable; fail-closed check not exercised.");
                    return;
                }

                AssertThrows<IOException>(
                    () => Global.SecureExistingChildren(root, null, 0), "reparse point");
            }
            finally
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = false;
                TryDeleteDirectory(link, false);
                TryDeleteDirectory(root, true);
                TryDeleteDirectory(target, true);
            }
        }

        private static void GlobalBoundsSecuredTreeEnumeration()
        {
            var root = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                for (var index = 0; index <= Global.MaxSecuredTreeEntries; index++)
                    File.WriteAllText(Path.Combine(root, $"entry-{index:D5}.txt"), string.Empty);

                AssertThrows<InvalidDataException>(
                    () => Global.SecureExistingChildren(root, null, 0), "more than");
            }
            finally
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = false;
                TryDeleteDirectory(root, true);
            }
        }

        private static void ProgramRejectsUntrustedWireSockCrashHandler()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                File.WriteAllText(Path.Combine(directory, "crashpad_handler.exe"), string.Empty);
                AssertFalse(WireSockUI.Program.TryValidateTrustedWireSockCompanionFiles(
                        directory, Path.Combine(directory, "wgbooster.dll"), out var diagnostic),
                    "Expected an explicitly user-writable crash handler to be rejected.");
                AssertTrue(diagnostic?.IndexOf("non-administrative", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected an actionable crash-handler trust diagnostic, got '{diagnostic}'.");
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }

        private static void ProgramBoundsWireSockSdkCompanionEnumeration()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var libraryPath = Path.Combine(directory, "wgbooster.dll");
            Directory.CreateDirectory(directory);

            try
            {
                File.WriteAllText(libraryPath, string.Empty);
                for (var index = 0; index < WireSockUI.Program.MaxWireSockSdkDirectoryEntries; index++)
                    File.WriteAllText(Path.Combine(directory, $"entry-{index:D5}.tmp"), string.Empty);

                AssertFalse(WireSockUI.Program.TryValidateTrustedWireSockCompanionFiles(
                        directory, libraryPath, out var diagnostic),
                    "Expected oversized WireSock SDK companion enumeration to fail closed.");
                AssertTrue(diagnostic?.IndexOf("more than", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected an actionable SDK directory limit diagnostic, got '{diagnostic}'.");
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }

        private static void ProgramDistinguishesReadOnlyAndWritableAcls()
        {
            var inspect = typeof(WireSockUI.Program).GetMethod("IsPotentiallyUserWritableSecurity",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (inspect == null)
                throw new InvalidOperationException("IsPotentiallyUserWritableSecurity helper was not found.");

            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var readOnlySecurity = new DirectorySecurity();
            readOnlySecurity.SetOwner(administrators);
            readOnlySecurity.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            readOnlySecurity.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.InheritOnly,
                AccessControlType.Allow));

            AssertFalse((bool)inspect.Invoke(null, new object[] { readOnlySecurity }),
                "Expected read-only and inherited-only non-administrative ACEs to remain trusted.");

            readOnlySecurity.AddAccessRule(new FileSystemAccessRule(
                users, FileSystemRights.Modify, AccessControlType.Allow));
            AssertTrue((bool)inspect.Invoke(null, new object[] { readOnlySecurity }),
                "Expected a non-administrative modify ACE to be rejected.");
        }

        private static void ProfileRejectsUserWritableSecuredFiles()
        {
            var originalConfigsFolder = Global.ConfigsFolder;
            var originalOverride = Global.AllowUnsecuredConfigFolderOverrideForTests;
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(directory);
                Global.ConfigsFolder = directory;
                Global.AllowUnsecuredConfigFolderOverrideForTests = false;
                var profilePath = Profile.GetProfilePath("unsafe");
                File.WriteAllText(profilePath, ValidConfig());

                AssertFalse(Profile.IsRegularProfileFile(profilePath, out var diagnostic),
                    "Expected elevated activation to reject a user-writable profile.");
                AssertTrue(diagnostic?.IndexOf("non-administrative", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected an ACL diagnostic, got '{diagnostic}'.");
            }
            finally
            {
                Global.ConfigsFolder = originalConfigsFolder;
                Global.AllowUnsecuredConfigFolderOverrideForTests = originalOverride;
                TryDeleteDirectory(directory, true);
            }
        }

        private static void ProgramRejectsReplaceableTrustedPathAncestors()
        {
            var inspectAncestor = typeof(WireSockUI.Program).GetMethod("IsPotentiallyUserReplaceableAncestor",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (inspectAncestor == null)
                throw new InvalidOperationException("IsPotentiallyUserReplaceableAncestor helper was not found.");

            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                AssertTrue((bool)inspectAncestor.Invoke(null, new object[] { directory }),
                    "Expected a user-owned temporary ancestor to be replaceable and therefore untrusted.");
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }

        private static void AutoRunRejectsUntrustedExecutablePaths()
        {
            var validate = typeof(FrmSettings).GetMethod("IsExecutablePathTrustedForAutoRun",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (validate == null)
                throw new InvalidOperationException("IsExecutablePathTrustedForAutoRun helper was not found.");

            var file = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", $"{Guid.NewGuid():N}.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(file));

            try
            {
                File.WriteAllText(file, string.Empty);

                var args = new object[] { file, null };
                var trusted = (bool)validate.Invoke(null, args);

                AssertFalse(trusted, "Expected elevated autorun to reject a user-writable executable path.");
                AssertTrue(args[1] is string diagnostic &&
                           diagnostic.IndexOf("non-administrative", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected an actionable autorun trust diagnostic, got '{args[1]}'.");
            }
            finally
            {
                TryDeleteFile(file);
            }
        }

        private static void AutoRunRejectsReparsePointExecutableFolders()
        {
            var validate = typeof(FrmSettings).GetMethod("IsExecutablePathTrustedForAutoRun",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (validate == null)
                throw new InvalidOperationException("IsExecutablePathTrustedForAutoRun helper was not found.");

            var root = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var targetDirectory = Path.Combine(root, "target");
            var linkDirectory = Path.Combine(root, "link");
            var targetFile = Path.Combine(targetDirectory, "WireSockUI.exe");
            var linkedFile = Path.Combine(linkDirectory, "WireSockUI.exe");

            try
            {
                Directory.CreateDirectory(targetDirectory);
                File.WriteAllText(targetFile, string.Empty);

                if (!TryCreateDirectoryJunction(linkDirectory, targetDirectory))
                {
                    SkipOrFail("autorun directory reparse point creation unavailable; autorun reparse check not exercised.");
                    return;
                }

                var args = new object[] { linkedFile, null };
                var trusted = (bool)validate.Invoke(null, args);

                AssertFalse(trusted, "Expected elevated autorun to reject executable paths through reparse point folders.");
                AssertTrue(args[1] is string diagnostic &&
                           diagnostic.IndexOf("reparse point", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected an actionable autorun reparse diagnostic, got '{args[1]}'.");
            }
            finally
            {
                TryDeleteDirectory(linkDirectory, false);
                TryDeleteDirectory(root, true);
            }
        }

        private static void ProfileImportRejectsOversizedFiles()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var source = Path.Combine(directory, "oversized.conf");
            var destination = Path.Combine(directory, "oversized.tmp");

            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.SetLength(1024 * 1024 + 1);
                }

                AssertThrows<InvalidOperationException>(
                    () => ProfileImportService.CopyProfileToTemporaryFile(source, destination),
                    "too large");
                AssertFalse(File.Exists(destination), "Expected oversized profile imports not to create a temp copy.");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    // Best-effort cleanup must not hide the original test failure.
                }
            }
        }

        private static void ProfileImportPreservesExistingDestinationOnCopyFailure()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var source = Path.Combine(directory, "oversized.conf");
            var destination = Path.Combine(directory, "existing.tmp");
            const string destinationContents = "keep me";

            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.SetLength(2);
                }

                File.WriteAllText(destination, destinationContents);

                AssertThrows<InvalidOperationException>(
                    () => RegularFileSource.CopyToTemporaryFile(source, destination, 1, "profile", "too large"),
                    "too large");
                AssertEqual(destinationContents, File.ReadAllText(destination));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    // Best-effort cleanup must not hide the original test failure.
                }
            }
        }

        private static void ProfileImportRejectsReparsePointSources()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var target = Path.Combine(directory, "target.conf");
            var link = Path.Combine(directory, "linked.conf");
            var destination = Path.Combine(directory, "linked.tmp");

            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(target, ValidConfig());

                if (!TryCreateProfileReparsePoint(link, target, out _))
                {
                    SkipOrFail("profile reparse point creation unavailable; profile import reparse check not exercised.");
                    return;
                }

                AssertThrows<IOException>(
                    () => ProfileImportService.CopyProfileToTemporaryFile(link, destination),
                    "reparse point");
                AssertFalse(File.Exists(destination),
                    "Expected reparse point profile imports not to create a temp copy.");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    // Best-effort cleanup must not hide the original test failure.
                }
            }
        }

        private static void ProfileImportRejectsDirectorySources()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(directory);

                try
                {
                    using (RegularFileSource.OpenForRead(directory + Path.DirectorySeparatorChar, "profile"))
                    {
                    }
                }
                catch (IOException ex)
                {
                    AssertTrue(ex.Message.IndexOf("directory", StringComparison.OrdinalIgnoreCase) >= 0,
                        $"Expected directory source diagnostic, got '{ex.Message}'.");
                    AssertTrue(ex.Message.Contains(Path.GetFileName(directory)),
                        $"Expected directory source diagnostic to include the selected folder name, got '{ex.Message}'.");
                    return;
                }

                throw new InvalidOperationException("Expected directory source imports to be rejected.");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    // Best-effort cleanup must not hide the original test failure.
                }
            }
        }

        private static void ProfileImportReportsMalformedSourcePathsConsistently()
        {
            var invalidPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                              Path.DirectorySeparatorChar + "<invalid>.conf";

            AssertThrows<IOException>(
                () =>
                {
                    using (RegularFileSource.OpenForRead(invalidPath, "profile"))
                    {
                    }
                },
                "Unable to open");
        }

        private static void LegacyMigrationRejectsOversizedFiles()
        {
            WithTemporaryLegacyMigrationFolders((legacyFolder, pendingFolder) =>
            {
                var source = Path.Combine(legacyFolder, "oversized.conf");
                using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.SetLength(1024 * 1024 + 1);

                LegacyProfileMigrationService.StageLegacyProfiles();

                AssertFalse(File.Exists(Path.Combine(pendingFolder, "oversized.conf")),
                    "Expected oversized legacy profiles not to enter quarantine.");
                AssertTrue(File.Exists(source),
                    "Expected a rejected legacy profile to remain available for manual recovery.");
            });
        }

        private static void GlobalRemovesConfigurationFileReparsePointsByHandle()
        {
            WithTemporaryLegacyMigrationFolders((legacyFolder, pendingFolder) =>
            {
                var target = Path.Combine(Global.ConfigsFolder, "target.conf");
                var link = Path.Combine(Global.ConfigsFolder, "linked.conf");
                File.WriteAllText(target, ValidConfig());

                if (!TryCreateProfileReparsePoint(link, target, out var isFileLink) || !isFileLink)
                {
                    SkipOrFail("file symbolic-link creation unavailable; handle-based cleanup not exercised.");
                    return;
                }

                Global.EnsureConfigsFolder();

                AssertFalse(File.Exists(link), "Expected startup hardening to remove the reparse point itself.");
                AssertTrue(File.Exists(target), "Expected reparse-point cleanup to preserve the target file.");
            });
        }

        private static void LegacyMigrationRejectsReparsePointSources()
        {
            WithTemporaryLegacyMigrationFolders((legacyFolder, pendingFolder) =>
            {
                var target = Path.Combine(legacyFolder, "target.txt");
                var link = Path.Combine(legacyFolder, "linked.conf");
                File.WriteAllText(target, ValidConfig());

                if (!TryCreateProfileReparsePoint(link, target, out _))
                {
                    SkipOrFail("profile reparse point creation unavailable; legacy migration reparse check not exercised.");
                    return;
                }

                LegacyProfileMigrationService.StageLegacyProfiles();
                AssertFalse(File.Exists(Path.Combine(pendingFolder, "linked.conf")),
                    "Expected a reparse-point legacy profile not to enter quarantine.");
            });
        }

        private static void LegacyMigrationQuarantinesValidProfiles()
        {
            WithTemporaryLegacyMigrationFolders((legacyFolder, pendingFolder) =>
            {
                var source = Path.Combine(legacyFolder, "office.conf");
                File.WriteAllText(source, ValidConfig());

                LegacyProfileMigrationService.StageLegacyProfiles();

                AssertTrue(File.Exists(Path.Combine(pendingFolder, "office.conf")),
                    "Expected a valid legacy profile to be staged for explicit review.");
                AssertTrue(File.Exists(source),
                    "Expected staging not to delete the user-controlled legacy source before approval.");
                AssertFalse(File.Exists(Profile.GetProfilePath("office")),
                    "Expected staging not to promote or activate the legacy profile.");
            });
        }

        private static void LegacyMigrationAcceptsUppercaseConfExtensions()
        {
            WithTemporaryLegacyMigrationFolders((legacyFolder, pendingFolder) =>
            {
                var source = Path.Combine(legacyFolder, "office.CONF");
                File.WriteAllText(source, ValidConfig());

                LegacyProfileMigrationService.StageLegacyProfiles();

                AssertTrue(File.Exists(Path.Combine(pendingFolder, "office.conf")),
                    "Expected case-insensitive legacy profile extensions to enter quarantine.");

                LegacyProfileMigrationService.CompleteApprovedMigration("office");
                AssertFalse(File.Exists(source),
                    "Expected approval to remove the original source with its exact extension casing.");
            });
        }

        private static void LegacyMigrationBoundsCatalogEnumeration()
        {
            WithTemporaryLegacyMigrationFolders((legacyFolder, pendingFolder) =>
            {
                for (var index = 0; index <= LegacyProfileMigrationService.MaxLegacyCatalogEntries; index++)
                    File.WriteAllText(Path.Combine(legacyFolder, $"entry-{index:D5}.tmp"), string.Empty);

                AssertThrows<InvalidDataException>(
                    LegacyProfileMigrationService.StageLegacyProfiles, "more than");

                for (var index = 0; index <= LegacyProfileMigrationService.MaxLegacyCatalogEntries; index++)
                    File.WriteAllText(Path.Combine(pendingFolder, $"entry-{index:D5}.tmp"), string.Empty);

                AssertThrows<InvalidDataException>(
                    () => LegacyProfileMigrationService.GetPendingProfileNames(), "more than");
            });
        }

        private static void LegacyMigrationPreservesModifiedSourcesOnCompletion()
        {
            WithTemporaryLegacyMigrationFolders((legacyFolder, pendingFolder) =>
            {
                var source = Path.Combine(legacyFolder, "office.conf");
                var pending = Path.Combine(pendingFolder, "office.conf");
                File.WriteAllText(source, ValidConfig());
                LegacyProfileMigrationService.StageLegacyProfiles();
                File.WriteAllText(source, ValidConfig().Replace("10.0.0.2/32", "10.0.0.3/32"));

                AssertThrows<IOException>(
                    () => LegacyProfileMigrationService.CompleteApprovedMigration("office"),
                    "no longer matches");
                AssertTrue(File.Exists(source),
                    "Expected a legacy source modified after staging to be preserved.");
                AssertTrue(File.Exists(pending),
                    "Expected quarantine metadata to remain available after a source mismatch.");
            });
        }

        private static void LegacyMigrationPreservesApprovedDuplicates()
        {
            WithTemporaryLegacyMigrationFolders((legacyFolder, pendingFolder) =>
            {
                var contents = ValidConfig();
                var source = Path.Combine(legacyFolder, "office.conf");
                File.WriteAllText(source, contents);
                File.WriteAllText(Profile.GetProfilePath("office"), contents);

                LegacyProfileMigrationService.StageLegacyProfiles();

                AssertTrue(File.Exists(source),
                    "Expected migration not to delete an unapproved legacy source even when contents match.");
                AssertFalse(File.Exists(Path.Combine(pendingFolder, "office.conf")),
                    "Expected an already approved duplicate not to be staged again.");
            });
        }

        private static void NativeRecoveryMarkerCleanupRemovesDirectoryMarkers()
        {
            WithTemporarySecureMainFolder(() =>
            {
                Directory.CreateDirectory(Global.NativeRecoveryMarkerPath);

                var snapshot = Global.NativeRecoveryMarkers.Capture();
                AssertTrue(snapshot?.Contents.IndexOf("directory", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected directory marker diagnostic, got '{snapshot?.Contents}'.");

                AssertTrue(Global.NativeRecoveryMarkers.TryDelete(snapshot),
                    "Expected the unchanged invalid marker to be deleted through its startup snapshot.");
                AssertFalse(Directory.Exists(Global.NativeRecoveryMarkerPath),
                    "Expected recovery marker cleanup to remove directory markers.");
            });
        }

        private static void NativeRecoveryMarkerReplacementDoesNotFollowHardLinks()
        {
            WithTemporarySecureMainFolder(() =>
            {
                var originalOwnerWriteFailure = SecureFileSystem.AllowOwnerWriteFailureForTests;
                var target = Path.Combine(Global.SecureMainFolder, "hard-link-target.txt");
                try
                {
                    SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                    File.WriteAllText(target, "unchanged");
                    if (!CreateHardLink(Global.NativeRecoveryMarkerPath, target, IntPtr.Zero))
                    {
                        SkipOrFail("hard-link creation unavailable; recovery marker replacement not exercised.");
                        return;
                    }

                    var lease = Global.NativeRecoveryMarkers.Write("test marker", "test diagnostic");

                    AssertTrue(lease != null, "Expected recovery marker replacement to succeed.");
                    AssertEqual("unchanged", File.ReadAllText(target));
                    AssertTrue(File.ReadAllText(Global.NativeRecoveryMarkerPath).Contains("Context: test marker"),
                        "Expected recovery marker content to be written to a newly created file.");
                }
                finally
                {
                    SecureFileSystem.AllowOwnerWriteFailureForTests = originalOwnerWriteFailure;
                }
            });
        }

        private static void NativeRecoveryMarkerLeasesPreserveNewerFailures()
        {
            WithTemporarySecureMainFolder(() =>
            {
                var originalOwnerWriteFailure = SecureFileSystem.AllowOwnerWriteFailureForTests;
                try
                {
                    SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                    var firstLease = Global.NativeRecoveryMarkers.Write("first operation", "first diagnostic");
                    var firstSnapshot = Global.NativeRecoveryMarkers.Capture();
                    var secondLease = Global.NativeRecoveryMarkers.Write("second operation", "second diagnostic");

                    AssertTrue(firstLease != null && firstSnapshot != null && secondLease != null,
                        "Expected every recovery marker operation to create an ownership token.");
                    AssertFalse(Global.NativeRecoveryMarkers.TryDelete(firstLease),
                        "Expected an older lease not to delete a newer marker.");
                    AssertFalse(Global.NativeRecoveryMarkers.TryUpdate(firstLease, "stale failure", "stale diagnostic"),
                        "Expected an older lease not to overwrite a newer marker.");
                    AssertFalse(Global.NativeRecoveryMarkers.TryDelete(firstSnapshot),
                        "Expected an older startup snapshot not to delete a newer marker.");

                    var contents = Global.NativeRecoveryMarkers.Read();
                    AssertTrue(contents.Contains("Context: second operation"),
                        "Expected the newer recovery marker to remain intact.");
                    AssertTrue(Global.NativeRecoveryMarkers.TryDelete(secondLease),
                        "Expected the current marker owner to delete its marker.");
                    AssertFalse(File.Exists(Global.NativeRecoveryMarkerPath),
                        "Expected the owned recovery marker to be removed.");
                }
                finally
                {
                    SecureFileSystem.AllowOwnerWriteFailureForTests = originalOwnerWriteFailure;
                }
            });
        }

        private static void NativeRecoveryMarkersBoundDiagnostics()
        {
            WithTemporarySecureMainFolder(() =>
            {
                var originalOwnerWriteFailure = SecureFileSystem.AllowOwnerWriteFailureForTests;
                try
                {
                    SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                    var lease = Global.NativeRecoveryMarkers.Write(
                        new string('c', 100 * 1024),
                        new string('d', 200 * 1024));

                    AssertTrue(lease != null, "Expected the bounded recovery marker write to succeed.");
                    AssertTrue(new FileInfo(Global.NativeRecoveryMarkerPath).Length <= 64 * 1024,
                        "Expected the recovery marker to stay within its read limit.");
                    AssertTrue(Global.NativeRecoveryMarkers.Read().Contains("Diagnostic: "),
                        "Expected the bounded marker to retain its diagnostic field.");
                    AssertTrue(Global.NativeRecoveryMarkers.TryDelete(lease),
                        "Expected the bounded marker to remain owned and deletable.");

                    using (var stream = new FileStream(Global.NativeRecoveryMarkerPath, FileMode.CreateNew,
                               FileAccess.Write, FileShare.None))
                        stream.SetLength(64 * 1024 + 1);

                    var oversizedSnapshot = Global.NativeRecoveryMarkers.Capture();
                    AssertTrue(oversizedSnapshot?.Contents.Contains("could not be read") == true,
                        "Expected an oversized external marker to produce a bounded diagnostic.");
                    AssertTrue(Global.NativeRecoveryMarkers.TryDelete(oversizedSnapshot),
                        "Expected scoped cleanup to remove the unchanged oversized marker.");
                    AssertFalse(File.Exists(Global.NativeRecoveryMarkerPath),
                        "Expected the oversized marker to be removed through its validated handle.");
                }
                finally
                {
                    SecureFileSystem.AllowOwnerWriteFailureForTests = originalOwnerWriteFailure;
                }
            });
        }

        private static void NativeRecoveryMarkerReplacementPreservesPreviousRecordOnFailure()
        {
            WithTemporarySecureMainFolder(() =>
            {
                var originalOwnerWriteFailure = SecureFileSystem.AllowOwnerWriteFailureForTests;
                try
                {
                    SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                    var firstLease = Global.NativeRecoveryMarkers.Write("preserved operation", "preserved diagnostic");
                    AssertTrue(firstLease != null, "Expected the initial recovery marker write to succeed.");
                    var previousContents = File.ReadAllText(Global.NativeRecoveryMarkerPath);

                    using (new FileStream(Global.NativeRecoveryMarkerPath, FileMode.Open, FileAccess.Read,
                               FileShare.Read))
                    {
                        var failedLease = Global.NativeRecoveryMarkers.Write("replacement operation",
                            "replacement diagnostic");
                        AssertTrue(failedLease == null,
                            "Expected atomic marker replacement to report the sharing violation.");
                    }

                    AssertEqual(previousContents, File.ReadAllText(Global.NativeRecoveryMarkerPath));
                    AssertFalse(Directory.GetFiles(Global.SecureMainFolder, "*.tmp").Any(),
                        "Expected a failed atomic replacement to remove its temporary file.");
                }
                finally
                {
                    SecureFileSystem.AllowOwnerWriteFailureForTests = originalOwnerWriteFailure;
                }
            });
        }

        private static void SecureFileSystemRejectsWritableHardLinks()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var target = Path.Combine(directory, "target.txt");
            var link = Path.Combine(directory, "link.txt");
            var originalOwnerWriteFailure = SecureFileSystem.AllowOwnerWriteFailureForTests;
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(target, "contents");
                if (!CreateHardLink(link, target, IntPtr.Zero))
                {
                    SkipOrFail("hard-link creation unavailable; writable-file rejection not exercised.");
                    return;
                }

                SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                AssertThrows<IOException>(() =>
                {
                    using (SecureFileSystem.OpenFile(link, true))
                    {
                    }
                }, "hard-linked");
            }
            finally
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = originalOwnerWriteFailure;
                TryDeleteDirectory(directory, true);
            }
        }

        private static void SecureFileSystemReadsTextThroughValidatedHandles()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "profile.conf");
            var hardLinkPath = Path.Combine(directory, "profile-hard-link.conf");
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, "[Interface]\r\nPrivateKey = test", new UTF8Encoding(true));

                AssertEqual("[Interface]\r\nPrivateKey = test", SecureFileSystem.ReadAllText(path, 1024));
                if (!CreateHardLink(hardLinkPath, path, IntPtr.Zero))
                {
                    SkipOrFail("hard-link creation unavailable; validated content-read rejection not exercised.");
                    return;
                }

                AssertThrows<IOException>(() => SecureFileSystem.ReadAllText(hardLinkPath, 1024), "hard-linked");
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }

        private static void SecureFileSystemDeleteHandlesBlockConcurrentWrites()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "delete-target.txt");
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, "contents");

                using (SecureFileSystem.OpenFileForDelete(path))
                {
                    using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.Read | FileShare.Write | FileShare.Delete))
                        AssertEqual((int)'c', reader.ReadByte());

                    AssertThrows<IOException>(() =>
                    {
                        using (new FileStream(path, FileMode.Open, FileAccess.Write,
                                   FileShare.Read | FileShare.Write | FileShare.Delete))
                        {
                        }
                    }, null);
                }
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }

        private static void SecureFileSystemSnapshotsPermitShellLinkInspection()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var shortcutPath = Path.Combine(directory, "startup.lnk");
            var snapshotPath = Path.Combine(directory, "startup-snapshot.lnk");
            var targetPath = Assembly.GetExecutingAssembly().Location;
            try
            {
                Directory.CreateDirectory(directory);
                using (var shortcut = new ShellLink { TargetPath = targetPath })
                    shortcut.Save(shortcutPath);

                using (var shortcutFile = SecureFileSystem.OpenFileForReadAndDelete(shortcutPath))
                {
                    shortcutFile.CopyToNewFile(snapshotPath, 1024 * 1024);
                    using (var shortcut = new ShellLink(snapshotPath))
                        AssertTrue(string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(shortcut.TargetPath),
                                StringComparison.OrdinalIgnoreCase),
                            "Expected the validated shortcut snapshot to preserve its target.");
                    shortcutFile.Delete();
                }

                AssertFalse(File.Exists(shortcutPath), "Expected the validated source shortcut to be deleted.");
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }

        private static void NativeQueryDistinguishesErrorSentinels()
        {
            var cleared = false;
            var succeeded = NativeCall.TryQuery(
                () => false,
                result => !result,
                () => cleared = true,
                () => 5,
                out var queryValue,
                out var diagnostic);

            AssertTrue(cleared, "Expected stale native error state to be cleared before the query.");
            AssertFalse(succeeded, "Expected a false sentinel accompanied by a native error to fail.");
            AssertFalse(queryValue, "Expected the original query value to be preserved.");
            AssertTrue(diagnostic?.Contains("5") == true, "Expected the native error code in the diagnostic.");

            succeeded = NativeCall.TryQuery(
                () => false,
                result => !result,
                () => { },
                () => 0,
                out queryValue,
                out diagnostic);
            AssertTrue(succeeded, "Expected an error sentinel with ERROR_SUCCESS to remain a valid inactive state.");
            AssertTrue(diagnostic == null, "Expected no diagnostic for a valid inactive state.");

            succeeded = NativeCall.TryQuery(
                () => true,
                result => !result,
                () => { },
                () => 5,
                out queryValue,
                out diagnostic);
            AssertTrue(succeeded, "Expected a non-sentinel value to ignore stale native error state.");
        }

        private static void SettingsUpgradeRunsExactlyOnce()
        {
            var calls = new List<string>();
            WireSockUI.Program.RunSettingsUpgrade(
                true,
                () => calls.Add("upgrade"),
                () => calls.Add("complete"),
                () => calls.Add("save"));

            AssertEqual("upgrade,complete,save", string.Join(",", calls));

            WireSockUI.Program.RunSettingsUpgrade(
                false,
                () => throw new InvalidOperationException("Upgrade should not run."),
                () => throw new InvalidOperationException("Completion should not run."),
                () => throw new InvalidOperationException("Save should not run."));
        }

        private static void ProtectedSettingsRequireConsentAndPersist()
        {
            var originalSettings = PrivilegedSettingsStore.Capture();
            var originalAllowOwnerFailure = SecureFileSystem.AllowOwnerWriteFailureForTests;
            try
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                WithTemporarySecureMainFolder(() =>
                {
                    var legacy = new PrivilegedSettingsSnapshot(true, "office", true, true);
                    var confirmations = 0;
                    PrivilegedSettingsStore.Initialize(legacy, settings =>
                    {
                        confirmations++;
                        AssertEqual("office", settings.LastProfile);
                        return false;
                    });

                    AssertEqual(1, confirmations);
                    AssertFalse(PrivilegedSettingsStore.AutoConnect,
                        "Expected rejected legacy settings to use safe defaults.");
                    AssertFalse(PrivilegedSettingsStore.EnableKillSwitch,
                        "Expected rejected legacy settings not to alter the Kill Switch preference.");
                    AssertTrue(File.Exists(PrivilegedSettingsStore.SettingsFilePath),
                        "Expected protected defaults to be persisted after migration was declined.");

                    PrivilegedSettingsStore.Apply(legacy);
                    PrivilegedSettingsStore.Save();
                    PrivilegedSettingsStore.SetForTests(new PrivilegedSettingsSnapshot(false, string.Empty,
                        false, false));
                    PrivilegedSettingsStore.Initialize(
                        new PrivilegedSettingsSnapshot(false, string.Empty, false, false),
                        _ => throw new InvalidOperationException("Existing protected settings must not prompt."));

                    AssertTrue(PrivilegedSettingsStore.AutoConnect,
                        "Expected the protected auto-connect setting to round-trip.");
                    AssertEqual("office", PrivilegedSettingsStore.LastProfile);
                    AssertTrue(PrivilegedSettingsStore.UseAdapter,
                        "Expected the protected adapter mode to round-trip.");
                    AssertTrue(PrivilegedSettingsStore.EnableKillSwitch,
                        "Expected the protected Kill Switch preference to round-trip.");
                });
            }
            finally
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = originalAllowOwnerFailure;
                PrivilegedSettingsStore.SetForTests(originalSettings);
            }
        }

        private static void ProtectedSettingsRejectMalformedOrOversizedData()
        {
            AssertThrows<XmlException>(
                () => PrivilegedSettingsStore.Parse(
                    "<!DOCTYPE settings [<!ENTITY x 'true'>]><PrivilegedSettings Version='1'><AutoConnect>&x;</AutoConnect><LastProfile/><UseAdapter>false</UseAdapter><EnableKillSwitch>false</EnableKillSwitch></PrivilegedSettings>"),
                "DTD");
            AssertThrows<FormatException>(
                () => PrivilegedSettingsStore.Parse(
                    "<PrivilegedSettings Version='1'><AutoConnect>false</AutoConnect><LastProfile/><UseAdapter>false</UseAdapter><EnableKillSwitch>false</EnableKillSwitch><Unexpected/></PrivilegedSettings>"),
                "structure");
            AssertThrows<FormatException>(
                () => PrivilegedSettingsStore.Parse(
                    "<PrivilegedSettings Version='1'><AutoConnect>false</AutoConnect><LastProfile>..</LastProfile><UseAdapter>false</UseAdapter><EnableKillSwitch>false</EnableKillSwitch></PrivilegedSettings>"),
                "invalid value");

            var originalSettings = PrivilegedSettingsStore.Capture();
            var originalAllowOwnerFailure = SecureFileSystem.AllowOwnerWriteFailureForTests;
            try
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                WithTemporarySecureMainFolder(() =>
                {
                    File.WriteAllText(PrivilegedSettingsStore.SettingsFilePath, new string('x', 64 * 1024 + 1));
                    AssertThrows<InvalidDataException>(
                        () => PrivilegedSettingsStore.Initialize(
                            new PrivilegedSettingsSnapshot(false, string.Empty, false, false), _ => false),
                        "maximum supported size");

                    File.WriteAllBytes(PrivilegedSettingsStore.SettingsFilePath, new byte[] { 0xc3, 0x28 });
                    AssertThrows<DecoderFallbackException>(
                        () => PrivilegedSettingsStore.Initialize(
                            new PrivilegedSettingsSnapshot(false, string.Empty, false, false), _ => false),
                        string.Empty);

                    File.Delete(PrivilegedSettingsStore.SettingsFilePath);
                    Directory.CreateDirectory(PrivilegedSettingsStore.SettingsFilePath);
                    AssertThrows<IOException>(
                        () => PrivilegedSettingsStore.Initialize(
                            new PrivilegedSettingsSnapshot(false, string.Empty, false, false), _ => false),
                        "regular file");
                });
            }
            finally
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = originalAllowOwnerFailure;
                PrivilegedSettingsStore.SetForTests(originalSettings);
            }
        }

        private static void ProtectedSettingsRecoverInterruptedSaves()
        {
            var originalSettings = PrivilegedSettingsStore.Capture();
            var originalAllowOwnerFailure = SecureFileSystem.AllowOwnerWriteFailureForTests;
            try
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                WithTemporarySecureMainFolder(() =>
                {
                    var backupPath = Path.Combine(Global.SecureMainFolder, "PrivilegedSettings.xml.backup");
                    var backupOnly = new PrivilegedSettingsSnapshot(true, "restored", false, true);
                    File.WriteAllText(backupPath, PrivilegedSettingsStore.Serialize(backupOnly),
                        new UTF8Encoding(false, true));

                    PrivilegedSettingsStore.Initialize(
                        new PrivilegedSettingsSnapshot(false, string.Empty, false, false),
                        _ => throw new InvalidOperationException("Backup recovery must not prompt."));

                    AssertEqual("restored", PrivilegedSettingsStore.LastProfile);
                    AssertTrue(File.Exists(PrivilegedSettingsStore.SettingsFilePath),
                        "Expected a backup-only interrupted save to restore the settings file.");
                    AssertFalse(File.Exists(backupPath),
                        "Expected the restored backup to be consumed.");

                    var committed = new PrivilegedSettingsSnapshot(false, "committed", true, false);
                    File.WriteAllText(PrivilegedSettingsStore.SettingsFilePath,
                        PrivilegedSettingsStore.Serialize(committed), new UTF8Encoding(false, true));
                    File.WriteAllText(backupPath, PrivilegedSettingsStore.Serialize(backupOnly),
                        new UTF8Encoding(false, true));

                    PrivilegedSettingsStore.Initialize(
                        new PrivilegedSettingsSnapshot(false, string.Empty, false, false),
                        _ => throw new InvalidOperationException("Committed settings recovery must not prompt."));

                    AssertEqual("committed", PrivilegedSettingsStore.LastProfile);
                    AssertTrue(PrivilegedSettingsStore.UseAdapter,
                        "Expected a committed settings file to win over its stale backup.");
                    AssertFalse(File.Exists(backupPath),
                        "Expected a stale backup to be deleted after the committed file was verified.");

                    File.WriteAllText(PrivilegedSettingsStore.SettingsFilePath, "invalid protected settings");
                    File.WriteAllText(backupPath, PrivilegedSettingsStore.Serialize(backupOnly),
                        new UTF8Encoding(false, true));

                    PrivilegedSettingsStore.Initialize(
                        new PrivilegedSettingsSnapshot(false, string.Empty, false, false),
                        _ => throw new InvalidOperationException("Invalid committed settings recovery must not prompt."));

                    AssertEqual("restored", PrivilegedSettingsStore.LastProfile);
                    AssertFalse(File.Exists(backupPath),
                        "Expected a valid backup to replace an invalid committed settings file.");
                    AssertFalse(Directory.GetFiles(Global.SecureMainFolder, "*.invalid").Any(),
                        "Expected the invalid committed settings file to be removed after recovery.");

                    File.Delete(PrivilegedSettingsStore.SettingsFilePath);
                    Directory.CreateDirectory(PrivilegedSettingsStore.SettingsFilePath);
                    File.WriteAllText(backupPath, PrivilegedSettingsStore.Serialize(backupOnly),
                        new UTF8Encoding(false, true));
                    AssertThrows<IOException>(
                        () => PrivilegedSettingsStore.Initialize(
                            new PrivilegedSettingsSnapshot(false, string.Empty, false, false), _ => false),
                        "regular file");
                    AssertTrue(File.Exists(backupPath),
                        "Expected a trust-boundary failure to leave the recovery backup untouched.");
                });
            }
            finally
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = originalAllowOwnerFailure;
                PrivilegedSettingsStore.SetForTests(originalSettings);
            }
        }

        private static void ApplicationSettingsUseProtectedConnectionValues()
        {
            var originalProtectedSettings = PrivilegedSettingsStore.Capture();
            var originalLegacyAutoConnect = WireSockUI.Properties.Settings.Default.AutoConnect;
            var originalLegacyUseAdapter = WireSockUI.Properties.Settings.Default.UseAdapter;
            var originalLegacyKillSwitch = WireSockUI.Properties.Settings.Default.EnableKillSwitch;
            try
            {
                PrivilegedSettingsStore.SetForTests(new PrivilegedSettingsSnapshot(true, "office", true, true));
                WireSockUI.Properties.Settings.Default.AutoConnect = false;
                WireSockUI.Properties.Settings.Default.UseAdapter = false;
                WireSockUI.Properties.Settings.Default.EnableKillSwitch = false;

                var captured = ApplicationSettingsSnapshot.Capture();
                AssertTrue(captured.AutoConnect,
                    "Expected connection settings capture to ignore the legacy user-scoped value.");
                AssertTrue(captured.UseAdapter,
                    "Expected adapter mode capture to come from protected storage.");
                AssertTrue(captured.EnableKillSwitch,
                    "Expected Kill Switch capture to come from protected storage.");

                new ApplicationSettingsSnapshot(captured.AutoRun, false, captured.AutoMinimize,
                    captured.AutoUpdate, false, captured.EnableNotifications, false, captured.LogLevel).Apply();
                AssertEqual("office", PrivilegedSettingsStore.LastProfile);
                AssertFalse(PrivilegedSettingsStore.AutoConnect,
                    "Expected applying settings to update protected auto-connect state.");
                AssertFalse(PrivilegedSettingsStore.UseAdapter,
                    "Expected applying settings to update protected adapter state.");
                AssertFalse(PrivilegedSettingsStore.EnableKillSwitch,
                    "Expected applying settings to update protected Kill Switch state.");
            }
            finally
            {
                WireSockUI.Properties.Settings.Default.AutoConnect = originalLegacyAutoConnect;
                WireSockUI.Properties.Settings.Default.UseAdapter = originalLegacyUseAdapter;
                WireSockUI.Properties.Settings.Default.EnableKillSwitch = originalLegacyKillSwitch;
                PrivilegedSettingsStore.SetForTests(originalProtectedSettings);
            }
        }

        private static void SettingsTransactionCompensatesFailuresInReverseOrder()
        {
            var calls = new List<string>();
            var result = CompensatingTransaction.ApplyAsync(new[]
                {
                    new CompensatingTransactionStep("log-level",
                        () =>
                        {
                            calls.Add("apply-log-level");
                            return Task.FromResult(true);
                        },
                        () =>
                        {
                            calls.Add("rollback-log-level");
                            return Task.FromResult(true);
                        }),
                    new CompensatingTransactionStep("kill-switch",
                        () =>
                        {
                            calls.Add("apply-kill-switch");
                            return Task.FromResult(false);
                        },
                        () =>
                        {
                            calls.Add("rollback-kill-switch");
                            return Task.FromResult(false);
                        }),
                    new CompensatingTransactionStep("persistence",
                        () =>
                        {
                            calls.Add("apply-persistence");
                            return Task.FromResult(true);
                        },
                        () => Task.FromResult(true))
                })
                .GetAwaiter().GetResult();

            AssertFalse(result.Succeeded, "Expected a failed Kill Switch update to fail the transaction.");
            AssertEqual("kill-switch", result.FailedStep);
            AssertEqual("apply-log-level,apply-kill-switch,rollback-kill-switch,rollback-log-level",
                string.Join(",", calls));
            AssertEqual("kill-switch", string.Join(",", result.RollbackFailures));

            var expectedException = new InvalidOperationException("autorun access denied");
            var exceptionResult = CompensatingTransaction.ApplyAsync(new[]
                {
                    new CompensatingTransactionStep("autorun task",
                        () => throw expectedException,
                        () => Task.FromResult(true))
                })
                .GetAwaiter().GetResult();

            AssertFalse(exceptionResult.Succeeded, "Expected an autorun exception to fail the transaction.");
            AssertTrue(ReferenceEquals(expectedException, exceptionResult.Exception),
                "Expected the transaction result to retain the actionable autorun exception.");
        }

        private static void SettingsCoordinatorOwnsUpdateSequencing()
        {
            var calls = new List<string>();
            var previous = new ApplicationSettingsSnapshot(false, false, false, false,
                false, false, false, "Info");
            var requested = new ApplicationSettingsSnapshot(true, true, true, true,
                true, true, true, "Debug");
            var coordinator = new SettingsUpdateCoordinator(
                logLevel =>
                {
                    calls.Add("log:" + logLevel);
                    return Task.FromResult(true);
                },
                (enabled, applyNativeState) =>
                {
                    calls.Add($"kill:{enabled}:{applyNativeState}");
                    return Task.FromResult(true);
                },
                settings =>
                {
                    calls.Add(ReferenceEquals(settings, requested) ? "persist:requested" : "persist:previous");
                    return Task.FromResult(true);
                });

            var result = coordinator.ApplyAsync(
                    previous,
                    requested,
                    true,
                    () =>
                    {
                        calls.Add("autorun:apply");
                        return Task.FromResult(true);
                    },
                    () =>
                    {
                        calls.Add("autorun:rollback");
                        return Task.FromResult(true);
                    })
                .GetAwaiter().GetResult();

            AssertTrue(result.Succeeded, "Expected the settings coordinator transaction to succeed.");
            AssertEqual("autorun:apply,log:Debug,persist:requested,kill:True:True", string.Join(",", calls));
        }

        private static void SettingsRollbackIdentifiesNativeRecoveryRequirements()
        {
            var noRollbackFailure = new CompensatingTransactionResult(true);
            AssertFalse(noRollbackFailure.RollbackFailed("native Kill Switch"),
                "Expected omitted rollback failures to normalize to an empty list.");

            var nativeFailure = new CompensatingTransactionResult(false, "native Kill Switch", null,
                new[] { "native Kill Switch: access denied" });
            AssertTrue(SettingsUpdateCoordinator.FailureRequiresNativeRecovery(nativeFailure),
                "Expected a failed native Kill Switch rollback to require recovery.");

            var persistenceFailure = new CompensatingTransactionResult(false, "settings persistence", null,
                new[] { "settings persistence: disk full" });
            AssertFalse(SettingsUpdateCoordinator.FailureRequiresNativeRecovery(persistenceFailure),
                "Expected a non-native rollback failure not to be mislabeled as driver recovery.");

            var similarlyNamedFailure = new CompensatingTransactionResult(false, "settings persistence", null,
                new[] { "native Kill Switch backup: disk full" });
            AssertFalse(SettingsUpdateCoordinator.FailureRequiresNativeRecovery(similarlyNamedFailure),
                "Expected only the exact native Kill Switch transaction step to require driver recovery.");
        }

        private static void TunnelCommandsDistinguishActivationFromDeactivation()
        {
            AssertTrue(TunnelCommandPolicy.IsDisconnectOnly(FrmMain.ConnectionState.Connected,
                    TunnelCommand.ToggleSelectedProfile),
                "Expected the connected Activate/Deactivate button to request disconnect only.");
            AssertFalse(TunnelCommandPolicy.IsDisconnectOnly(FrmMain.ConnectionState.Disconnected,
                    TunnelCommand.ToggleSelectedProfile),
                "Expected the disconnected button to activate the selected profile.");
            AssertFalse(TunnelCommandPolicy.IsDisconnectOnly(FrmMain.ConnectionState.Connected,
                    TunnelCommand.ActivateSelectedProfile),
                "Expected profile activation to switch or reconnect instead of acting as Deactivate.");
        }

        private static void NativeTimeoutPolicyDefersCleanupUntilCompletion()
        {
            var pending = Task.FromResult(NativeOperationResult<bool>.Success(true));
            var timedOut = NativeOperationResult<bool>.Timeout("timeout", pending);
            AssertTrue(NativeOperationRecoveryPolicy.MustDeferCleanup(timedOut),
                "Expected cleanup to wait for the timed-out native query.");
            AssertFalse(NativeOperationRecoveryPolicy.MustDeferCleanup(NativeOperationResult<bool>.Failure("failed")),
                "Expected a completed failure to permit immediate cleanup.");
            AssertTrue(NativeOperationRecoveryPolicy.CanRestorePreviousState(
                    NativeOperationResult<bool>.Success(true)),
                "Expected a verified late success to restore the previous UI state.");
            AssertFalse(NativeOperationRecoveryPolicy.CanRestorePreviousState(
                    NativeOperationResult<bool>.Failure("failed")),
                "Expected a late failure to retain recovery state.");

            var missingCompletion = NativeOperationRecoveryPolicy.NormalizeCompletion<bool>(null,
                "native state query");
            AssertFalse(missingCompletion.Succeeded,
                "Expected a missing late native result to retain recovery state.");
            AssertTrue(missingCompletion.Diagnostic.Contains("completed without a result"),
                "Expected a missing late native result to provide an actionable diagnostic.");
            AssertEqual("existing additional",
                NativeOperationRecoveryPolicy.AppendDiagnostic("existing", "additional"));
            AssertEqual("additional", NativeOperationRecoveryPolicy.AppendDiagnostic(null, "additional"));
            AssertEqual("existing", NativeOperationRecoveryPolicy.AppendDiagnostic("existing", null));
        }

        private static void AutorunPreservesPersistedStateWhenStatusIsUnknown()
        {
            AssertTrue(FrmSettings.ResolveRequestedAutoRun(FrmSettings.AutoRunStatus.Unknown, false, true),
                "Expected an unknown Task Scheduler state to preserve the persisted preference.");
            AssertFalse(FrmSettings.ResolveRequestedAutoRun(FrmSettings.AutoRunStatus.Disabled, false, true),
                "Expected a verified disabled state to override a stale persisted preference.");
            AssertTrue(FrmSettings.ResolveRequestedAutoRun(FrmSettings.AutoRunStatus.Enabled, true, false),
                "Expected a known enabled state to use the requested value.");
        }

        private static void Curve25519MatchesRfc7748PublicKeyVectors()
        {
            var privateKey = ParseHex(
                "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
            Curve25519.ClampPrivateKeyInline(privateKey);
            var publicKey = Curve25519.GetPublicKey(privateKey);
            AssertEqual(
                "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a",
                ToHex(publicKey));

            var generatedPrivateKey = Curve25519.CreateRandomPrivateKey();
            AssertEqual(32, generatedPrivateKey.Length);
            AssertEqual(0, generatedPrivateKey[0] & 7);
            AssertEqual(0x40, generatedPrivateKey[31] & 0x40);
            AssertEqual(0, generatedPrivateKey[31] & 0x80);
        }

        private static void EditorValidatesAmneziaOptions()
        {
            AssertTrue(ConfigValueValidator.IsUIntOrRange("1-4", 0, uint.MaxValue),
                "Expected decimal H ranges to be accepted.");
            AssertFalse(ConfigValueValidator.IsUIntOrRange("0x10-0x20", 0, uint.MaxValue),
                "Expected hexadecimal H ranges to be rejected like the SDK parser.");
            AssertFalse(ConfigValueValidator.IsUIntOrRange("4-1", 0, uint.MaxValue),
                "Expected descending H ranges to be rejected.");
            AssertTrue(ConfigValueValidator.IsUIntDecimalInRange("1279", 0, ConfigValueValidator.MaximumAmneziaPadding),
                "Expected maximum Amnezia padding to be accepted.");
            AssertFalse(ConfigValueValidator.IsUIntDecimalInRange("1280", 0, ConfigValueValidator.MaximumAmneziaPadding),
                "Expected oversized S1/S2 padding to be rejected.");
            AssertFalse(ConfigValueValidator.IsUIntDecimalInRange("+1", 0, uint.MaxValue),
                "Expected signed unsigned values to be rejected like std::from_chars.");
            AssertTrue(ConfigValueValidator.IsOneOf("quic", "quic", "dns", "sip", "stun"),
                "Expected known Ip values to be accepted.");
            AssertFalse(ConfigValueValidator.IsOneOf("invalid", "chrome", "firefox", "curl", "random"),
                "Expected unknown Ib values to be rejected.");
            AssertTrue(ConfigValueValidator.IsSipImitationHost("xn--e1afmkfd.xn--p1ai"),
                "Expected an ACE/Punycode SIP host to be accepted.");
            AssertFalse(ConfigValueValidator.IsSipImitationHost("a..b"),
                "Expected empty SIP hostname labels to be rejected.");
        }

        private static void EditorBoundsSynchronousSyntaxHighlighting()
        {
            AssertTrue(FrmEdit.ShouldApplySyntaxHighlighting(FrmEdit.MaximumSyntaxHighlightCharacters),
                "Expected the editor to highlight content at the configured boundary.");
            AssertFalse(FrmEdit.ShouldApplySyntaxHighlighting(FrmEdit.MaximumSyntaxHighlightCharacters + 1),
                "Expected oversized profiles not to be reformatted synchronously on the UI thread.");
            AssertFalse(FrmEdit.ShouldApplySyntaxHighlighting(-1),
                "Expected invalid text lengths not to enter syntax highlighting.");
        }

        private static void LegacyMigrationAcceptsScriptsOnlyIntoQuarantine()
        {
            WithTemporaryLegacyMigrationFolders((legacyFolder, pendingFolder) =>
            {
                var source = Path.Combine(legacyFolder, "scripted.conf");
                File.WriteAllText(source, "[Interface]\n" +
                                          $"PrivateKey = {PrivateKey}\n" +
                                          "Address = 10.0.0.2/32\n" +
                                          "PostUp = powershell.exe -NoProfile -Command Write-Host test\n" +
                                          "\n" +
                                          "[Peer]\n" +
                                          $"PublicKey = {PublicKey}\n" +
                                          "Endpoint = example.com:51820\n" +
                                          "AllowedIPs = 0.0.0.0/0\n");

                LegacyProfileMigrationService.StageLegacyProfiles();

                AssertTrue(File.Exists(Path.Combine(pendingFolder, "scripted.conf")),
                    "Expected scripts to remain reviewable in quarantine.");
                AssertFalse(File.Exists(Profile.GetProfilePath("scripted")),
                    "Expected a scripted legacy profile never to be promoted without editor confirmation.");
            });
        }

        private static void TunnelSessionCoordinatorEnforcesRecoveryInvariants()
        {
            var coordinator = new TunnelSessionCoordinator();

            AssertTrue(coordinator.TryBeginOperation(out var blockReason),
                "Expected the first tunnel operation to start.");
            AssertEqual((int)TunnelOperationBlockReason.None, (int)blockReason);
            AssertFalse(coordinator.TryBeginOperation(out blockReason),
                "Expected overlapping tunnel operations to be rejected.");
            AssertEqual((int)TunnelOperationBlockReason.OperationPending, (int)blockReason);

            coordinator.EndOperation();
            var generation = coordinator.AdvanceGeneration();
            AssertTrue(coordinator.TryMarkConnectionTimedOut(generation),
                "Expected the active generation to accept one timeout transition.");
            AssertFalse(coordinator.TryMarkConnectionTimedOut(generation),
                "Expected a duplicate timeout transition to be rejected.");
            AssertTrue(coordinator.IsConnectionTimedOut(generation),
                "Expected the active generation to remain marked as timed out.");

            coordinator.AdvanceGeneration();
            AssertFalse(coordinator.IsConnectionTimedOut(generation),
                "Expected advancing the generation to clear the timeout marker.");

            coordinator.BeginCleanup();
            coordinator.BeginCleanup();
            AssertFalse(coordinator.TryBeginOperation(out blockReason),
                "Expected pending cleanup to block new operations.");
            AssertEqual((int)TunnelOperationBlockReason.CleanupPending, (int)blockReason);
            AssertFalse(coordinator.EndCleanup(),
                "Expected the first completion to retain overlapping cleanup ownership.");
            AssertTrue(coordinator.CleanupPending,
                "Expected cleanup to remain pending until every owner completes.");
            AssertTrue(coordinator.EndCleanup(),
                "Expected the final completion to release cleanup ownership.");
            AssertFalse(coordinator.EndCleanup(),
                "Expected unmatched cleanup completion not to report an ownership release.");

            AssertTrue(coordinator.RequireRecovery(), "Expected the first recovery transition to be observable.");
            AssertFalse(coordinator.RequireRecovery(), "Expected duplicate recovery transitions to be idempotent.");
            AssertFalse(coordinator.TryBeginOperation(out blockReason),
                "Expected recovery mode to block new operations.");
            AssertEqual((int)TunnelOperationBlockReason.RecoveryRequired, (int)blockReason);

            AssertTrue(coordinator.TryBeginRecoveryOperation(out blockReason),
                "Expected an explicit recovery operation to be allowed during recovery mode.");
            AssertFalse(coordinator.TryBeginRecoveryOperation(out blockReason),
                "Expected overlapping recovery operations to be rejected.");
            AssertEqual((int)TunnelOperationBlockReason.OperationPending, (int)blockReason);
            coordinator.EndOperation();

            coordinator.BeginCleanup();
            AssertFalse(coordinator.TryBeginRecoveryOperation(out blockReason),
                "Expected pending cleanup to block explicit recovery operations.");
            AssertEqual((int)TunnelOperationBlockReason.CleanupPending, (int)blockReason);
            coordinator.EndCleanup();

            coordinator.ClearRecovery();
            AssertTrue(coordinator.TryBeginOperation(out blockReason),
                "Expected operations to resume after explicit recovery reset.");
            coordinator.EndOperation();
        }

        private static void TunnelSessionCoordinatorWaitsForPendingOperations()
        {
            var coordinator = new TunnelSessionCoordinator();
            AssertTrue(coordinator.TryBeginOperation(out _),
                "Expected the initial tunnel operation to start.");

            var waitingOperation = coordinator.WaitToBeginOperationAsync(() => false, 1);
            Thread.Sleep(20);
            AssertFalse(waitingOperation.IsCompleted,
                "Expected recovery handling to wait while another tunnel operation owns the coordinator.");

            coordinator.EndOperation();
            AssertTrue(waitingOperation.Wait(2000),
                "Expected recovery handling to acquire the coordinator after the pending operation completed.");
            AssertTrue(waitingOperation.Result,
                "Expected the waiting tunnel operation to report successful acquisition.");
            coordinator.EndOperation();

            AssertTrue(coordinator.TryBeginOperation(out _),
                "Expected a second initial operation to start.");
            AssertFalse(coordinator.WaitToBeginOperationAsync(() => true, 1).GetAwaiter().GetResult(),
                "Expected obsolete recovery handling to stop instead of waiting indefinitely.");
            coordinator.EndOperation();
        }

        private static void DiagnosticLoggingRedactsCredentials()
        {
            var redacted = SecureRollingTraceListener.Redact(
                "PrivateKey = secret\nPresharedKey=another\nSocks5ProxyPassword = password\n" +
                "https://user:password@example.com/path");

            AssertFalse(redacted.Contains("secret"), "Expected private keys to be redacted.");
            AssertFalse(redacted.Contains("another"), "Expected preshared keys to be redacted.");
            AssertFalse(redacted.Contains("password"), "Expected proxy credentials to be redacted.");
            AssertTrue(redacted.Contains("PrivateKey = [REDACTED]"),
                "Expected diagnostic output to preserve the redacted setting name.");
            AssertTrue(redacted.Contains("https://[REDACTED]@example.com/path"),
                "Expected URI user information to be redacted.");
        }

        private static void DiagnosticLoggingBoundsOversizedRecords()
        {
            WithTemporarySecureMainFolder(() =>
            {
                const int maximumBytes = 1024;
                var originalOwnerWriteFailure = SecureFileSystem.AllowOwnerWriteFailureForTests;
                var originalAutoFlush = Trace.AutoFlush;
                var logPath = Path.Combine(Global.SecureMainFolder, "bounded.log");
                try
                {
                    SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                    using (var listener = new SecureRollingTraceListener(logPath, maximumBytes, 1))
                        listener.WriteLine(new string('\u20ac', maximumBytes));

                    var bytes = File.ReadAllBytes(logPath);
                    AssertTrue(bytes.Length <= maximumBytes,
                        $"Expected a diagnostic record no larger than {maximumBytes} bytes, got {bytes.Length}.");
                    AssertTrue(File.ReadAllText(logPath).Contains("[truncated]"),
                        "Expected the oversized diagnostic record to be marked as truncated.");

                    Trace.AutoFlush = false;
                    var bufferedLogPath = Path.Combine(Global.SecureMainFolder, "buffered.log");
                    using (var listener = new SecureRollingTraceListener(bufferedLogPath, maximumBytes, 1))
                    {
                        listener.WriteLine(new string('a', 700));
                        listener.WriteLine(new string('b', 700));
                    }

                    AssertTrue(File.Exists(bufferedLogPath + ".1"),
                        "Expected buffered records to rotate before exceeding the configured size.");
                    AssertTrue(new FileInfo(bufferedLogPath).Length <= maximumBytes,
                        "Expected the active buffered diagnostic log to remain bounded.");
                    AssertTrue(new FileInfo(bufferedLogPath + ".1").Length <= maximumBytes,
                        "Expected the buffered diagnostic archive to remain bounded.");

                    var formattingLogPath = Path.Combine(Global.SecureMainFolder, "formatting.log");
                    using (var listener = new SecureRollingTraceListener(formattingLogPath, maximumBytes, 1))
                    {
                        listener.TraceData(null, "test", TraceEventType.Warning, 1, new ThrowingToStringValue());
                        listener.TraceEvent(null, "test", TraceEventType.Warning, 2, "Value: {0}",
                            new ThrowingToStringValue());
                    }

                    var formattingLog = File.ReadAllText(formattingLogPath);
                    AssertTrue(formattingLog.Contains("diagnostic value formatting failed"),
                        "Expected diagnostic value formatting failures to be contained.");
                    AssertTrue(formattingLog.Contains("diagnostic formatting failed"),
                        "Expected diagnostic composite-format failures to be contained.");

                    var invalidLogPath = Path.Combine(Global.SecureMainFolder, "invalid.log");
                    Directory.CreateDirectory(invalidLogPath);
                    using (var listener = new SecureRollingTraceListener(invalidLogPath, maximumBytes, 1))
                        AssertThrows<IOException>(listener.PrepareForUse, "not a regular file");
                }
                finally
                {
                    Trace.AutoFlush = originalAutoFlush;
                    SecureFileSystem.AllowOwnerWriteFailureForTests = originalOwnerWriteFailure;
                }
            });
        }

        private static void LegacyMigrationCompletionRemovesStagedSources()
        {
            WithTemporaryLegacyMigrationFolders((legacyFolder, pendingFolder) =>
            {
                var legacy = Path.Combine(legacyFolder, "office.conf");
                var pending = Path.Combine(pendingFolder, "office.conf");
                File.WriteAllText(legacy, ValidConfig());
                LegacyProfileMigrationService.StageLegacyProfiles();
                AssertTrue(File.Exists(pending), "Expected the profile to be staged before completion.");

                LegacyProfileMigrationService.CompleteApprovedMigration("office");
                AssertFalse(File.Exists(pending), "Expected approval to remove the staged copy.");
                AssertFalse(File.Exists(legacy), "Expected approval to remove the original legacy copy.");
            });
        }

        private static void AppUserModelIdIsPathSeeded()
        {
            var buildDefaultAppUserModelId = typeof(WindowsApplicationContext).GetMethod(
                "BuildDefaultAppUserModelId", BindingFlags.NonPublic | BindingFlags.Static);
            if (buildDefaultAppUserModelId == null)
                throw new InvalidOperationException("BuildDefaultAppUserModelId helper was not found.");

            var first = (string)buildDefaultAppUserModelId.Invoke(null,
                new object[] { "WireSock UI", @"C:\Program Files\WireSockUI\WireSockUI.exe" });
            var firstAgain = (string)buildDefaultAppUserModelId.Invoke(null,
                new object[] { "WireSock UI", @"C:\Program Files\WireSockUI\WireSockUI.exe" });
            var second = (string)buildDefaultAppUserModelId.Invoke(null,
                new object[] { "WireSock UI", @"D:\Tools\WireSockUI\WireSockUI.exe" });

            AssertEqual(first, firstAgain);
            AssertFalse(string.Equals(first, second, StringComparison.Ordinal),
                "Expected AppUserModelID to differ for side-by-side executable paths.");
            AssertTrue(first.Length <= 128, "Expected AppUserModelID to fit the Windows shell length limit.");
        }

        private static void NotificationShortcutNameIsPathSeeded()
        {
            var first = WindowsApplicationContext.BuildShortcutFileName(
                "WireSockUI", @"C:\Program Files\WireSockUI\WireSockUI.exe");
            var second = WindowsApplicationContext.BuildShortcutFileName(
                "WireSockUI", @"D:\Tools\WireSockUI\WireSockUI.exe");

            AssertFalse(string.Equals(first, second, StringComparison.Ordinal),
                "Expected side-by-side installs to use different notification shortcuts.");
            AssertTrue(first.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase),
                "Expected a shell shortcut filename.");

            var untrustedName = WindowsApplicationContext.BuildShortcutFileName(
                @"..\WireSockUI/Bad:Name", @"C:\Program Files\WireSockUI\WireSockUI.exe");
            AssertFalse(untrustedName.Contains("..") || untrustedName.Contains("\\") || untrustedName.Contains("/") ||
                        untrustedName.Contains(":"),
                "Expected shortcut filenames to remove path and device-name metacharacters.");
        }

        private static void NotificationImagePathsUseFileUris()
        {
            var path = Path.Combine(Path.GetTempPath(), "WireSock UI", "WireSock.ico");
            var uri = NotificationContent.BuildLocalImageUri(path);

            AssertTrue(uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase),
                $"Expected a desktop notification file URI, got '{uri}'.");
            AssertTrue(uri.Contains("WireSock%20UI"), "Expected spaces in notification image paths to be escaped.");
            AssertThrows<ArgumentException>(() => NotificationContent.BuildLocalImageUri(" "), "path");
        }

        private static void WindowsCompatibilityManifestEnablesModernBehavior()
        {
            var manifest = XDocument.Load(FindRepositoryFile("WireSockUI", "Properties", "app.manifest"));
            var elements = manifest.Descendants().ToArray();
            AssertTrue(elements.Any(element => element.Name.LocalName == "supportedOS" &&
                                               string.Equals((string)element.Attribute("Id"),
                                                   "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}",
                                                   StringComparison.OrdinalIgnoreCase)),
                "Expected the Windows 10/11 compatibility declaration to be enabled.");
            AssertTrue(elements.Any(element => element.Name.LocalName == "dpiAwareness" &&
                                               element.Value.Contains("PerMonitorV2")),
                "Expected PerMonitorV2 DPI awareness to be enabled.");
            AssertTrue(elements.Any(element => element.Name.LocalName == "longPathAware" &&
                                               string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase)),
                "Expected long-path awareness to be enabled.");
            AssertTrue(elements.Any(element => element.Name.LocalName == "assemblyIdentity" &&
                                               string.Equals((string)element.Attribute("name"),
                                                   "Microsoft.Windows.Common-Controls",
                                                   StringComparison.OrdinalIgnoreCase)),
                "Expected the modern common-controls dependency to be enabled.");

            var appConfig = XDocument.Load(FindRepositoryFile("WireSockUI", "app.config"));
            AssertTrue(appConfig.Descendants("add").Any(element =>
                    string.Equals((string)element.Attribute("key"), "DpiAwareness", StringComparison.Ordinal) &&
                    string.Equals((string)element.Attribute("value"), "PerMonitorV2", StringComparison.Ordinal)),
                "Expected the .NET Framework WinForms DPI switch to match the manifest.");

            var configurationMap = new ExeConfigurationFileMap
            {
                ExeConfigFilename = FindRepositoryFile("WireSockUI", "app.config")
            };
            var configuration = ConfigurationManager.OpenMappedExeConfiguration(configurationMap,
                ConfigurationUserLevel.None);
            AssertTrue(configuration.GetSection("System.Windows.Forms.ApplicationConfigurationSection") != null,
                "Expected .NET Framework to recognize the WinForms application configuration section.");

            var unicodePathCapacity = typeof(ShellLink).GetField("UnicodePathCapacity",
                BindingFlags.NonPublic | BindingFlags.Static);
            AssertTrue(unicodePathCapacity != null, "Expected the long shell-link path buffer constant.");
            AssertEqual(32768, (int)unicodePathCapacity.GetRawConstantValue());
        }

        private static void AutoRunTaskNameIsPathSeeded()
        {
            var buildAutoRunTaskName = typeof(FrmSettings).GetMethod(
                "BuildAutoRunTaskName", BindingFlags.NonPublic | BindingFlags.Static);
            if (buildAutoRunTaskName == null)
                throw new InvalidOperationException("BuildAutoRunTaskName helper was not found.");

            var isSameExecutablePath = typeof(FrmSettings).GetMethod(
                "IsSameExecutablePath", BindingFlags.NonPublic | BindingFlags.Static);
            if (isSameExecutablePath == null)
                throw new InvalidOperationException("IsSameExecutablePath helper was not found.");

            var first = (string)buildAutoRunTaskName.Invoke(null,
                new object[] { @"C:\Program Files\WireSockUI\WireSockUI.exe" });
            var firstAgain = (string)buildAutoRunTaskName.Invoke(null,
                new object[] { @"C:\Program Files\WireSockUI\WireSockUI.exe" });
            var second = (string)buildAutoRunTaskName.Invoke(null,
                new object[] { @"D:\Tools\WireSockUI\WireSockUI.exe" });

            AssertEqual(first, firstAgain);
            AssertFalse(string.Equals(first, second, StringComparison.Ordinal),
                "Expected autorun task names to differ for side-by-side executable paths.");
            AssertTrue(first.StartsWith("WireSockUI-", StringComparison.Ordinal),
                $"Expected autorun task name to include the application prefix, got '{first}'.");
            AssertTrue((bool)isSameExecutablePath.Invoke(null, new object[]
                {
                    @"C:\Program Files\WireSockUI\WireSockUI.exe",
                    @"""c:\program files\wiresockui\wiresockui.exe"""
                }),
                "Expected autorun ownership checks to tolerate quoted task action paths.");
            AssertFalse((bool)isSameExecutablePath.Invoke(null, new object[]
                {
                    string.Empty,
                    @"C:\Program Files\WireSockUI\WireSockUI.exe"
                }),
                "Expected empty autorun paths not to match the current executable.");
        }

        private static void WireSockDisconnectForwardsNetworkLockPreservation()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = TestKillSwitch;
                var nativeApi = new FakeWireSockNativeApi();
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        TestKillSwitch = false;
                        File.WriteAllText(Profile.GetProfilePath("office"), ValidConfig());

                        AssertTrue(manager.Connect("office"), "Expected the fake tunnel to connect.");
                        AssertTrue(manager.Disconnect(true),
                            "Expected fake disconnect with preserved network lock to succeed.");
                        AssertEqual(1, nativeApi.ReleaseCount);
                        AssertTrue(nativeApi.LastPreserveNetworkLock == true,
                            "Expected preserved reconnect cleanup to pass preserveNetworkLock=true to wgbooster.");

                        AssertTrue(manager.Connect("office"), "Expected the fake tunnel to reconnect.");
                        AssertTrue(manager.Disconnect(), "Expected fake default disconnect to succeed.");
                        AssertEqual(2, nativeApi.ReleaseCount);
                        AssertTrue(nativeApi.LastPreserveNetworkLock == false,
                            "Expected explicit disconnect cleanup to pass preserveNetworkLock=false to wgbooster.");
                    }
                    finally
                    {
                        TestKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void LifecycleResetsPreservedLockAfterHandleCreationFails()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = TestKillSwitch;
                var nativeApi = new FakeWireSockNativeApi { CreateHandleResult = IntPtr.Zero };
                var networkLockApi = new FakeNetworkLockApi { Active = true };
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        TestKillSwitch = true;
                        File.WriteAllText(Profile.GetProfilePath("office"), ValidConfig());
                        var controller = new TunnelLifecycleController(manager, networkLockApi);

                        var result = controller.ConnectAsync("office", true, 1000).GetAwaiter().GetResult();

                        AssertFalse(result.Succeeded, "Expected handle creation failure to fail the connection.");
                        AssertFalse(result.TimedOut, "Expected the failed fake connection to complete normally.");
                        AssertFalse(result.Value.RecoveryRequired,
                            "Expected a successful preserved-lock reset not to require recovery.");
                        AssertEqual(1, networkLockApi.ResetCount);
                        AssertFalse(networkLockApi.Active,
                            "Expected failed reconnect cleanup to release the preserved network lock.");

                        networkLockApi.Active = true;
                        networkLockApi.ResetResult = false;
                        var failedReset = controller.ConnectAsync("office", true, 1000).GetAwaiter().GetResult();
                        AssertTrue(failedReset.Value.RecoveryRequired,
                            "Expected an unreset preserved lock to require explicit recovery.");
                        AssertTrue(failedReset.Diagnostic?.Contains("simulated reset failure") == true,
                            "Expected the preserved-lock reset diagnostic to be retained.");
                    }
                    finally
                    {
                        TestKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void LifecycleTracksLateDisconnectCompletionAfterTimeout()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = TestKillSwitch;
                var nativeApi = new FakeWireSockNativeApi
                {
                    DropEntered = new ManualResetEventSlim(false),
                    ContinueDrop = new ManualResetEventSlim(false)
                };
                using (nativeApi.DropEntered)
                using (nativeApi.ContinueDrop)
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        TestKillSwitch = false;
                        File.WriteAllText(Profile.GetProfilePath("office"), ValidConfig());
                        var controller = new TunnelLifecycleController(manager, new FakeNetworkLockApi());
                        AssertTrue(manager.Connect("office"), "Expected the fake tunnel to connect.");

                        var result = controller.DisconnectAsync(null, false, 50).GetAwaiter().GetResult();
                        AssertTrue(result.TimedOut, "Expected the blocked native disconnect to time out.");
                        AssertTrue(result.PendingCompletion != null,
                            "Expected timed-out native cleanup to retain its late completion task.");
                        AssertTrue(nativeApi.DropEntered.Wait(1000),
                            "Expected the fake native drop operation to start.");

                        nativeApi.ContinueDrop.Set();
                        var lateResult = result.PendingCompletion.GetAwaiter().GetResult();
                        AssertTrue(lateResult.Succeeded, "Expected late native disconnect cleanup to succeed.");
                        AssertFalse(manager.HasTunnelHandle,
                            "Expected late disconnect completion to release the native handle.");
                    }
                    finally
                    {
                        nativeApi.ContinueDrop.Set();
                        TestKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void LifecycleShutdownAvoidsSynchronizationContextDeadlocks()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = TestKillSwitch;
                var nativeApi = new FakeWireSockNativeApi
                {
                    DropEntered = new ManualResetEventSlim(false),
                    ContinueDrop = new ManualResetEventSlim(false)
                };
                using (nativeApi.DropEntered)
                using (nativeApi.ContinueDrop)
                using (var completion = new ManualResetEventSlim(false))
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        TestKillSwitch = false;
                        File.WriteAllText(Profile.GetProfilePath("office"), ValidConfig());
                        var controller = new TunnelLifecycleController(manager, new FakeNetworkLockApi());
                        AssertTrue(manager.Connect("office"), "Expected the fake tunnel to connect.");

                        NativeOperationResult<bool> shutdownResult = null;
                        Exception shutdownException = null;
                        var shutdownThread = new Thread(() =>
                        {
                            SynchronizationContext.SetSynchronizationContext(
                                new NonPumpingSynchronizationContext());
                            try
                            {
                                shutdownResult = controller.ShutdownAsync(100).GetAwaiter().GetResult();
                            }
                            catch (Exception ex)
                            {
                                shutdownException = ex;
                            }
                            finally
                            {
                                SynchronizationContext.SetSynchronizationContext(null);
                                completion.Set();
                            }
                        })
                        {
                            IsBackground = true
                        };

                        shutdownThread.Start();
                        AssertTrue(nativeApi.DropEntered.Wait(1000),
                            "Expected the fake native shutdown cleanup to start.");

                        var completedWithoutPumping = completion.Wait(2000);
                        nativeApi.ContinueDrop.Set();

                        AssertTrue(completedWithoutPumping,
                            "Expected shutdown timeout handling not to require a synchronization-context pump.");
                        if (shutdownException != null)
                            throw new InvalidOperationException("The shutdown workflow failed unexpectedly.",
                                shutdownException);

                        AssertTrue(shutdownResult != null && shutdownResult.TimedOut,
                            "Expected the blocked native shutdown cleanup to return a timeout result.");
                        AssertTrue(shutdownResult.PendingCompletion != null,
                            "Expected the timed-out shutdown to retain its late completion task.");
                        AssertTrue(shutdownResult.PendingCompletion.GetAwaiter().GetResult().Succeeded,
                            "Expected the released native shutdown cleanup to complete successfully.");
                        AssertTrue(shutdownThread.Join(1000),
                            "Expected the synchronous shutdown caller to exit after receiving the timeout result.");
                    }
                    finally
                    {
                        nativeApi.ContinueDrop.Set();
                        TestKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void WireSockManagerSurfacesNativeQueryFailures()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = TestKillSwitch;
                var nativeApi = new FakeWireSockNativeApi();
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        TestKillSwitch = false;
                        File.WriteAllText(Profile.GetProfilePath("office"), ValidConfig());
                        AssertTrue(manager.Connect("office"), "Expected the fake tunnel to connect.");

                        nativeApi.TunnelActive = false;
                        nativeApi.TunnelActiveError = 5;
                        AssertFalse(manager.TryGetConnected(out _, out var activeDiagnostic),
                            "Expected the manager to reject a false tunnel sentinel with a native error.");
                        AssertTrue(activeDiagnostic?.Contains("5") == true,
                            "Expected the tunnel query diagnostic to retain the native error.");

                        nativeApi.NetworkLockMode = WireguardBoosterExports.WgbNetworkLockMode.Disabled;
                        nativeApi.NetworkLockModeError = 6;
                        AssertFalse(manager.TryGetKillSwitchEnabled(out _, out var lockDiagnostic),
                            "Expected the manager to reject a disabled lock sentinel with a native error.");
                        AssertTrue(lockDiagnostic?.Contains("6") == true,
                            "Expected the lock query diagnostic to retain the native error.");

                        nativeApi.NetworkLockMode = (WireguardBoosterExports.WgbNetworkLockMode)99;
                        nativeApi.NetworkLockModeError = 0;
                        AssertFalse(manager.TryGetKillSwitchEnabled(out _, out var invalidModeDiagnostic),
                            "Expected the manager to reject unknown SDK network-lock enum values.");
                        AssertTrue(invalidModeDiagnostic?.Contains("99") == true,
                            "Expected the invalid SDK enum value in the diagnostic.");

                        nativeApi.TunnelState = new WireguardBoosterExports.WgbStats();
                        nativeApi.TunnelStateError = 21;
                        AssertFalse(manager.TryGetState(out _, out var statsDiagnostic),
                            "Expected the manager to reject empty statistics with a native error.");
                        AssertTrue(statsDiagnostic?.Contains("21") == true,
                            "Expected the statistics diagnostic to retain the native error.");
                    }
                    finally
                    {
                        nativeApi.TunnelActiveError = 0;
                        nativeApi.NetworkLockModeError = 0;
                        nativeApi.TunnelStateError = 0;
                        TestKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void WireSockManagerCleansUpFailedStarts()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = TestKillSwitch;
                var nativeApi = new FakeWireSockNativeApi
                {
                    StartResult = false,
                    StartError = 31
                };

                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        TestKillSwitch = false;
                        File.WriteAllText(Profile.GetProfilePath("office"), ValidConfig());

                        AssertFalse(manager.Connect("office"), "Expected the failed native start to fail connect.");
                        AssertFalse(manager.HasTunnelHandle, "Expected failed connect cleanup to clear the handle.");
                        AssertTrue(nativeApi.DropCount == 1, "Expected failed connect cleanup to drop the tunnel once.");
                        AssertTrue(nativeApi.ReleaseCount == 1,
                            "Expected failed connect cleanup to release its independently owned SDK handle.");
                        AssertFalse(nativeApi.LastEnableAnalytics,
                            "Expected WireSock UI to disable SDK analytics unless the user explicitly opts in.");
                        AssertTrue(manager.LastError?.Contains("31") == true,
                            "Expected the native start error in the connection diagnostic.");
                    }
                    finally
                    {
                        TestKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void WireSockManagerRetainsHandlesWhenCleanupFails()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = TestKillSwitch;
                var nativeApi = new FakeWireSockNativeApi
                {
                    StartResult = false,
                    StartError = 31,
                    DropResult = false,
                    DropError = 32
                };
                var manager = new WireSockManager(nativeApi);

                try
                {
                    TestKillSwitch = false;
                    File.WriteAllText(Profile.GetProfilePath("office"), ValidConfig());

                    AssertFalse(manager.Connect("office"), "Expected the failed native start to fail connect.");
                    AssertTrue(manager.HasTunnelHandle,
                        "Expected failed cleanup to retain the native handle and prevent duplicate ownership.");
                    AssertTrue(manager.LastError?.Contains("blocked") == true,
                        "Expected the connection diagnostic to explain that replacement connections are blocked.");
                    AssertTrue(manager.LastError?.Contains("32") == true,
                        "Expected the native drop error in the retained-handle diagnostic.");

                    AssertFalse(manager.Connect("office"),
                        "Expected a second connect to stop when the retained handle still cannot be dropped.");
                    AssertTrue(nativeApi.GetHandleCount == 1,
                        "Expected the manager not to allocate a replacement native handle after failed cleanup.");

                    nativeApi.DropResult = true;
                    nativeApi.DropError = 0;
                    AssertTrue(manager.Disconnect(), "Expected retained-handle cleanup to be retryable.");
                    AssertFalse(manager.HasTunnelHandle, "Expected successful retry cleanup to clear the handle.");
                    AssertEqual(1, nativeApi.ReleaseCount);
                }
                finally
                {
                    nativeApi.DropResult = true;
                    nativeApi.DropError = 0;
                    manager.Dispose();
                    TestKillSwitch = originalKillSwitch;
                }
            });
        }

        private static void WireSockManagerRetriesReleaseWithoutDroppingTwice()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = TestKillSwitch;
                var nativeApi = new FakeWireSockNativeApi { ReleaseFailuresRemaining = 1 };
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        TestKillSwitch = false;
                        File.WriteAllText(Profile.GetProfilePath("office"), ValidConfig());

                        AssertTrue(manager.Connect("office"), "Expected the fake tunnel to connect.");
                        AssertFalse(manager.Disconnect(),
                            "Expected the first release attempt to retain the independently owned handle.");
                        AssertTrue(manager.HasTunnelHandle,
                            "Expected a failed release_handle call to keep the handle available for retry.");
                        AssertEqual(1, nativeApi.DropCount);

                        AssertTrue(manager.Disconnect(), "Expected the release-only retry to succeed.");
                        AssertFalse(manager.HasTunnelHandle, "Expected the successful release retry to clear the handle.");
                        AssertEqual(1, nativeApi.DropCount);
                        AssertEqual(2, nativeApi.ReleaseCount);
                    }
                    finally
                    {
                        TestKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void WireSockManagerQuarantinesDroppedHandles()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = TestKillSwitch;
                var nativeApi = new FakeWireSockNativeApi { ReleaseFailuresRemaining = 1 };
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        TestKillSwitch = false;
                        File.WriteAllText(Profile.GetProfilePath("office"), ValidConfig());

                        AssertThrows<ArgumentOutOfRangeException>(
                            () => manager.TunnelMode = WireSockManager.Mode.Undefined, "must be");
                        AssertTrue(manager.Connect("office"), "Expected the fake tunnel to connect.");
                        AssertFalse(manager.Disconnect(), "Expected the first release attempt to fail.");

                        AssertFalse(manager.TryGetConnected(out _, out var connectedDiagnostic),
                            "Expected active-state queries to reject a dropped handle.");
                        AssertTrue(connectedDiagnostic.Contains("already dropped"),
                            "Expected a useful dropped-handle active-state diagnostic.");
                        AssertFalse(manager.TryGetState(out _, out var stateDiagnostic),
                            "Expected statistics queries to reject a dropped handle.");
                        AssertTrue(stateDiagnostic.Contains("already dropped"),
                            "Expected a useful dropped-handle statistics diagnostic.");
                        AssertFalse(manager.TryGetKillSwitchEnabled(out _, out var lockDiagnostic),
                            "Expected network-lock queries to reject a dropped handle.");
                        AssertTrue(lockDiagnostic.Contains("already dropped"),
                            "Expected a useful dropped-handle network-lock diagnostic.");
                        AssertThrows<InvalidOperationException>(
                            () => manager.LogLevel = WireguardBoosterExports.WgbLogLevel.Debug, "already dropped");
                        AssertThrows<InvalidOperationException>(() => manager.KillSwitchEnabled = true,
                            "already dropped");

                        AssertEqual(0, nativeApi.TunnelActiveQueryCount);
                        AssertEqual(0, nativeApi.TunnelStateQueryCount);
                        AssertEqual(0, nativeApi.NetworkLockQueryCount);
                        AssertEqual(0, nativeApi.SetLogLevelCount);
                        AssertEqual(0, nativeApi.SetNetworkLockCount);

                        AssertTrue(manager.Disconnect(), "Expected a release-only retry to clean up the handle.");
                        manager.Dispose();
                        AssertFalse(manager.TryGetConnected(out _, out var disposedDiagnostic),
                            "Expected queries on a disposed manager to fail explicitly.");
                        AssertTrue(disposedDiagnostic.Contains("disposed"),
                            "Expected a useful disposed-manager diagnostic.");
                    }
                    finally
                    {
                        TestKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void TunnelMonitorStopsAfterBoundedQueryTimeout()
        {
            var generation = 1;
            var queryCount = 0;
            var pendingQuery = new TaskCompletionSource<NativeOperationResult<bool>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var updateSource = new TaskCompletionSource<TunnelMonitorUpdate>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using (var monitor = new TunnelMonitor(
                       _ =>
                       {
                           Interlocked.Increment(ref queryCount);
                           return Task.FromResult(NativeOperationResult<bool>.Timeout(
                               "simulated native query timeout", pendingQuery.Task));
                       },
                       _ => Task.FromResult(NativeOperationResult<WireguardBoosterExports.WgbStats>.Success(
                           new WireguardBoosterExports.WgbStats())),
                       () => generation,
                       update =>
                       {
                           updateSource.TrySetResult(update);
                           return Task.CompletedTask;
                       },
                       10,
                       100,
                       1,
                       1))
            {
                monitor.StartConnected(generation);
                AssertTrue(updateSource.Task.Wait(2000), "Expected the monitor to report the timed-out query.");

                var update = updateSource.Task.GetAwaiter().GetResult();
                AssertEqual((int)TunnelMonitorUpdateKind.QueryFailed, (int)update.Kind);
                AssertTrue(update.ConnectionQuery?.TimedOut == true,
                    "Expected the complete timeout result, including pending completion, to be preserved.");

                Thread.Sleep(25);
                AssertEqual(1, Volatile.Read(ref queryCount));
                pendingQuery.TrySetResult(NativeOperationResult<bool>.Failure("simulated completion"));
            }
        }

        private static void ProgramReportsAttributeInspectionFailures()
        {
            var missingPath = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", $"{Guid.NewGuid():N}.dll");
            AssertFalse(WireSockUI.Program.TryGetExistingAttributes(
                    missingPath, out _, out var missingDiagnostic),
                "Expected missing attribute inspection targets to fail.");
            AssertTrue(missingDiagnostic == null,
                $"Expected missing attribute inspection targets to remain silent, got '{missingDiagnostic}'.");

            var malformedPath = "invalid\0wgbooster.dll";

            AssertFalse(WireSockUI.Program.TryGetExistingAttributes(
                    malformedPath, out _, out var diagnostic),
                "Expected malformed attribute inspection to fail.");
            AssertTrue(!string.IsNullOrWhiteSpace(diagnostic),
                "Expected unexpected attribute inspection failures to produce a diagnostic.");
            AssertTrue(diagnostic.IndexOf("Unable to inspect file system attributes",
                           StringComparison.OrdinalIgnoreCase) >= 0,
                $"Expected an actionable attribute inspection diagnostic, got '{diagnostic}'.");
            AssertFalse(diagnostic.Contains("\0"),
                $"Expected attribute inspection diagnostics to escape embedded NULs, got '{diagnostic}'.");
            AssertTrue(diagnostic.Contains("\\0"),
                $"Expected attribute inspection diagnostics to include the escaped NUL marker, got '{diagnostic}'.");

            AssertFalse(WireSockUI.Program.TryValidateApplicationPayloadDirectory(
                    malformedPath, out var payloadDiagnostic),
                "Expected malformed application payload paths to fail validation.");
            AssertTrue(payloadDiagnostic.IndexOf("Unable to inspect file system attributes",
                           StringComparison.OrdinalIgnoreCase) >= 0,
                $"Expected payload validation to preserve the attribute diagnostic, got '{payloadDiagnostic}'.");
            AssertFalse(payloadDiagnostic.Contains("\0"),
                $"Expected payload diagnostics to escape embedded NULs, got '{payloadDiagnostic}'.");
            AssertTrue(payloadDiagnostic.Contains("\\0"),
                $"Expected payload diagnostics to include the escaped NUL marker, got '{payloadDiagnostic}'.");

            const string sdkDirectory = "sdk\nfolder";
            AssertFalse(WireSockUI.Program.TryValidateTrustedWireSockCompanionFiles(
                    sdkDirectory, "wgbooster.dll", out var sdkDiagnostic),
                "Expected missing SDK directories to fail validation.");
            AssertFalse(sdkDiagnostic.Contains("\n"),
                $"Expected SDK diagnostics to escape embedded line breaks, got '{sdkDiagnostic}'.");
            AssertTrue(sdkDiagnostic.Contains("\\n"),
                $"Expected SDK diagnostics to include the escaped line-break marker, got '{sdkDiagnostic}'.");

            const string surrogateSdkDirectory = "sdk\uD800folder";
            AssertFalse(WireSockUI.Program.TryValidateTrustedWireSockCompanionFiles(
                    surrogateSdkDirectory, "wgbooster.dll", out var surrogateDiagnostic),
                "Expected malformed SDK directories to fail validation.");
            AssertFalse(surrogateDiagnostic.Any(char.IsSurrogate),
                $"Expected SDK diagnostics to escape surrogate code units, got '{surrogateDiagnostic}'.");
            AssertTrue(surrogateDiagnostic.Contains("\\uD800"),
                $"Expected SDK diagnostics to include the escaped surrogate marker, got '{surrogateDiagnostic}'.");
        }

        private static void TunnelMonitorPreservesStatisticsQueryTimeouts()
        {
            var generation = 1;
            var pendingQuery = new TaskCompletionSource<NativeOperationResult<WireguardBoosterExports.WgbStats>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var updateSource = new TaskCompletionSource<TunnelMonitorUpdate>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using (var monitor = new TunnelMonitor(
                       _ => Task.FromResult(NativeOperationResult<bool>.Success(true)),
                       _ => Task.FromResult(NativeOperationResult<WireguardBoosterExports.WgbStats>.Timeout(
                           "simulated statistics timeout", pendingQuery.Task)),
                       () => generation,
                       update =>
                       {
                           updateSource.TrySetResult(update);
                           return Task.CompletedTask;
                       },
                       10,
                       100,
                       1,
                       1))
            {
                monitor.StartConnected(generation);
                AssertTrue(updateSource.Task.Wait(2000),
                    "Expected the monitor to report the timed-out statistics query.");

                var update = updateSource.Task.GetAwaiter().GetResult();
                AssertEqual((int)TunnelMonitorUpdateKind.QueryFailed, (int)update.Kind);
                AssertTrue(update.StatisticsQuery?.TimedOut == true,
                    "Expected the statistics timeout and pending completion to be preserved.");
                pendingQuery.TrySetResult(
                    NativeOperationResult<WireguardBoosterExports.WgbStats>.Failure("simulated completion"));
            }
        }

        private static void TunnelMonitorSuppressesCanceledQueryUpdates()
        {
            var generation = 1;
            var queryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queryCompletion = new TaskCompletionSource<NativeOperationResult<bool>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var updateCount = 0;

            using (var monitor = new TunnelMonitor(
                       _ =>
                       {
                           queryStarted.TrySetResult(true);
                           return queryCompletion.Task;
                       },
                       _ => Task.FromResult(NativeOperationResult<WireguardBoosterExports.WgbStats>.Success(
                           new WireguardBoosterExports.WgbStats())),
                       () => generation,
                       _ =>
                       {
                           Interlocked.Increment(ref updateCount);
                           return Task.CompletedTask;
                       },
                       100,
                       100,
                       1,
                       1))
            {
                monitor.StartConnected(generation);
                AssertTrue(queryStarted.Task.Wait(2000), "Expected the native query to start.");

                monitor.Cancel();
                queryCompletion.TrySetResult(NativeOperationResult<bool>.Success(false));
                Thread.Sleep(25);

                AssertEqual(0, Volatile.Read(ref updateCount));
            }
        }

        private static void TunnelMonitorClassifiesUnexpectedStatisticsFailures()
        {
            var generation = 1;
            var updateSource = new TaskCompletionSource<TunnelMonitorUpdate>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using (var monitor = new TunnelMonitor(
                       _ => Task.FromResult(NativeOperationResult<bool>.Success(true)),
                       _ => Task.FromException<NativeOperationResult<WireguardBoosterExports.WgbStats>>(
                           new InvalidOperationException("simulated statistics failure")),
                       () => generation,
                       update =>
                       {
                           updateSource.TrySetResult(update);
                           return Task.CompletedTask;
                       },
                       100,
                       100,
                       1,
                       1))
            {
                monitor.StartConnected(generation);
                AssertTrue(updateSource.Task.Wait(2000),
                    "Expected the monitor to report the unexpected statistics failure.");

                var update = updateSource.Task.GetAwaiter().GetResult();
                AssertTrue(update.ConnectionQuery == null,
                    "Expected a statistics failure not to be classified as a connection query failure.");
                AssertTrue(update.StatisticsQuery?.Succeeded == false,
                    "Expected the unexpected failure to retain statistics-query context.");
                AssertTrue(update.StatisticsQuery.Diagnostic.Contains(nameof(InvalidOperationException)),
                    "Expected the unexpected failure diagnostic to preserve the exception type.");
                AssertTrue(update.StatisticsQuery.Diagnostic.Contains("simulated statistics failure"),
                    "Expected the original statistics failure diagnostic to be preserved.");
            }

            var emptyMessageDiagnostic = TunnelMonitor.FormatUnexpectedFailureDiagnostic(
                "Tunnel state monitor", new InvalidOperationException(string.Empty));
            AssertEqual("Tunnel state monitor stopped unexpectedly (InvalidOperationException).",
                emptyMessageDiagnostic);

            var malformedMessageDiagnostic = TunnelMonitor.FormatUnexpectedFailureDiagnostic(
                "Tunnel state monitor", new InvalidOperationException("invalid\0state"));
            AssertFalse(malformedMessageDiagnostic.Contains("\0"),
                "Expected unexpected monitor failure diagnostics to escape embedded NULs.");
            AssertTrue(malformedMessageDiagnostic.Contains("invalid\\0state"),
                "Expected unexpected monitor failure diagnostics to preserve the escaped message.");
        }

        private static void TunnelMonitorUiDispatchAwaitsMarshaledUpdates()
        {
            var actionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var actionRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var synchronizer = new QueuedSynchronizeInvoke();
            try
            {
                var dispatchTask = FrmMain.InvokeOnUiThreadAsync(synchronizer, async () =>
                {
                    actionStarted.TrySetResult(true);
                    await actionRelease.Task;
                });

                AssertFalse(actionStarted.Task.IsCompleted, "Expected the queued UI callback not to run early.");

                synchronizer.RunCallback();
                AssertTrue(actionStarted.Task.Wait(2000), "Expected the marshaled UI callback to start.");
                AssertFalse(dispatchTask.IsCompleted,
                    "Expected the dispatch task to wait for the asynchronous UI callback.");

                actionRelease.TrySetResult(true);
                AssertTrue(dispatchTask.Wait(2000),
                    "Expected the dispatch task to complete with the UI callback.");
            }
            finally
            {
                actionRelease.TrySetResult(true);
            }
        }

        private static void WireSockManagerBoundsNativeLogBackpressure()
        {
            var callbackEntered = new ManualResetEventSlim();
            var releaseCallback = new ManualResetEventSlim();
            var finalMessageReceived = new ManualResetEventSlim();
            var messages = new List<string>();
            var syncRoot = new object();

            using (var manager = new WireSockManager(
                       new FakeWireSockNativeApi(),
                       message =>
                       {
                           lock (syncRoot)
                               messages.Add(message.Message);

                           if (message.Message == "blocked")
                           {
                               callbackEntered.Set();
                               releaseCallback.Wait(5000);
                           }
                           else if (message.Message == "message-2499")
                           {
                               finalMessageReceived.Set();
                           }
                       }))
            {
                var printLog = typeof(WireSockManager).GetMethod(
                    "PrintLog",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                AssertTrue(printLog != null, "Expected the private log queue entry point to exist.");

                printLog.Invoke(manager, new object[] { "blocked" });
                AssertTrue(callbackEntered.Wait(2000), "Expected the log worker callback to start.");

                for (var index = 0; index < 2500; index++)
                    printLog.Invoke(manager, new object[] { $"message-{index}" });

                releaseCallback.Set();
                AssertTrue(finalMessageReceived.Wait(5000), "Expected the bounded log queue to drain.");

                string[] captured;
                lock (syncRoot)
                    captured = messages.ToArray();

                AssertEqual(1002, captured.Length);
                AssertTrue(captured[1].Contains("1500 messages dropped"),
                    "Expected the manager queue to report its exact drop count.");
                AssertEqual("message-1500", captured[2]);
                AssertEqual("message-2499", captured[captured.Length - 1]);
            }
        }

        private static void WireSockManagerBoundsRetainedLogRecords()
        {
            AssertEqual(string.Empty, WireSockManager.BoundLogMessage(null));
            AssertEqual("short message", WireSockManager.BoundLogMessage("short message"));

            var oversizedMessage = new string('x', WireSockManager.MaximumRetainedLogMessageCharacters + 100);
            var boundedMessage = WireSockManager.BoundLogMessage(oversizedMessage);
            AssertEqual(WireSockManager.MaximumRetainedLogMessageCharacters, boundedMessage.Length);
            AssertTrue(boundedMessage.EndsWith("[truncated]", StringComparison.Ordinal),
                "Expected oversized native records to carry an explicit truncation diagnostic.");

            const string suffix = " ... [truncated]";
            var retainedLength = WireSockManager.MaximumRetainedLogMessageCharacters - suffix.Length;
            var surrogateBoundaryMessage = new string('x', retainedLength - 1) +
                                           char.ConvertFromUtf32(0x1F600) +
                                           new string('y', suffix.Length + 10);
            var surrogateSafeMessage = WireSockManager.BoundLogMessage(surrogateBoundaryMessage);
            new UTF8Encoding(false, true).GetBytes(surrogateSafeMessage);
            AssertTrue(surrogateSafeMessage.Length <= WireSockManager.MaximumRetainedLogMessageCharacters,
                "Expected truncation not to retain a dangling UTF-16 surrogate.");
        }

        private static void UiLogBufferingCoalescesAndBoundsDispatch()
        {
            var scheduled = new Queue<Action>();
            var consumed = new List<WireSockManager.LogMessage>();
            var scheduleCount = 0;
            using (var buffer = new UiLogMessageBuffer(
                       1000,
                       128,
                       action =>
                       {
                           scheduleCount++;
                           scheduled.Enqueue(action);
                           return true;
                       },
                       batch => consumed.AddRange(batch)))
            {
                for (var index = 0; index < 10000; index++)
                    buffer.Enqueue(new WireSockManager.LogMessage { Message = $"message-{index}" });

                AssertEqual(1, scheduleCount);
                AssertEqual(1, scheduled.Count);

                while (scheduled.Count > 0)
                    scheduled.Dequeue()();

                AssertTrue(scheduleCount <= 9,
                    $"Expected batched UI dispatch, but {scheduleCount} callbacks were scheduled.");
                AssertEqual(1001, consumed.Count);
                AssertTrue(consumed[0].Message.Contains("dropped 9000"),
                    "Expected the bounded UI queue to report the exact number of dropped messages.");
                AssertEqual("message-9000", consumed[1].Message);
                AssertEqual("message-9999", consumed[consumed.Count - 1].Message);

                buffer.Dispose();
                buffer.Enqueue(new WireSockManager.LogMessage { Message = "late message" });
                AssertEqual(0, scheduled.Count);
            }

            var recoveryDispatches = new Queue<Action>();
            var recoveredMessages = new List<WireSockManager.LogMessage>();
            var failFirstBatch = true;
            using (var buffer = new UiLogMessageBuffer(
                       4,
                       2,
                       action =>
                       {
                           recoveryDispatches.Enqueue(action);
                           return true;
                       },
                       batch =>
                       {
                           if (failFirstBatch)
                           {
                               failFirstBatch = false;
                               throw new InvalidOperationException("consumer failed");
                           }

                           recoveredMessages.AddRange(batch);
                       }))
            {
                buffer.Enqueue(new WireSockManager.LogMessage { Message = "first" });
                buffer.Enqueue(new WireSockManager.LogMessage { Message = "second" });
                buffer.Enqueue(new WireSockManager.LogMessage { Message = "third" });

                AssertThrows<InvalidOperationException>(() => recoveryDispatches.Dequeue()(), "consumer failed");
                AssertEqual(1, recoveryDispatches.Count);
                recoveryDispatches.Dequeue()();
                AssertEqual(1, recoveredMessages.Count);
                AssertEqual("third", recoveredMessages[0].Message);
            }

            var retryDispatches = new Queue<Action>();
            var retriedMessages = new List<WireSockManager.LogMessage>();
            var schedulingAvailable = false;
            using (var buffer = new UiLogMessageBuffer(
                       4,
                       2,
                       action =>
                       {
                           if (!schedulingAvailable)
                               return false;

                           retryDispatches.Enqueue(action);
                           return true;
                       },
                       batch => retriedMessages.AddRange(batch)))
            {
                buffer.Enqueue(new WireSockManager.LogMessage { Message = "before-handle" });
                AssertEqual(0, retryDispatches.Count);

                schedulingAvailable = true;
                buffer.RetryPendingDispatch();
                AssertEqual(1, retryDispatches.Count);
                retryDispatches.Dequeue()();
                AssertEqual(1, retriedMessages.Count);
                AssertEqual("before-handle", retriedMessages[0].Message);
            }
        }

        private static void WireSockManagerRollsBackFailedLogLevelChanges()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = TestKillSwitch;
                var nativeApi = new FakeWireSockNativeApi();
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        TestKillSwitch = false;
                        File.WriteAllText(Profile.GetProfilePath("office"), ValidConfig());
                        manager.LogLevel = WireguardBoosterExports.WgbLogLevel.Info;
                        AssertTrue(manager.Connect("office"), "Expected the fake tunnel to connect.");

                        nativeApi.SetLogLevelFailuresRemaining = 1;
                        AssertThrows<InvalidOperationException>(
                            () => manager.LogLevel = WireguardBoosterExports.WgbLogLevel.Debug,
                            "Simulated set_log_level failure");
                        AssertEqual((int)WireguardBoosterExports.WgbLogLevel.Info, (int)manager.LogLevel);

                        AssertTrue(manager.Disconnect(), "Expected the fake tunnel to disconnect.");
                        AssertTrue(manager.Connect("office"), "Expected the fake tunnel to reconnect.");
                        AssertEqual((int)WireguardBoosterExports.WgbLogLevel.Info,
                            (int)nativeApi.LastCreateLogLevel);
                    }
                    finally
                    {
                        TestKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void ProfileRenameCommitsAndRollsBackTransactionally()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var original = Path.Combine(directory, "original.conf");
            var destination = Path.Combine(directory, "renamed.conf");
            var temporary = Path.Combine(directory, "profile.tmp");

            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(original, "old");
                File.WriteAllText(temporary, "new");

                ProfileFileTransaction.Commit(temporary, destination, original);
                AssertFalse(File.Exists(original), "Expected the old profile name to disappear after commit.");
                AssertEqual("new", File.ReadAllText(destination));
                AssertFalse(File.Exists(temporary), "Expected the temporary profile to be consumed.");

                var distinctOriginal = Path.Combine(directory, "identity-original.conf");
                var distinctDestination = Path.Combine(directory, "identity-destination.conf");
                File.WriteAllText(distinctOriginal, "identity-original");
                File.WriteAllText(distinctDestination, "identity-destination");
                AssertThrows<IOException>(
                    () => ProfileFileTransaction.ValidateCaseOnlyRenameDestination(
                        distinctOriginal, distinctDestination),
                    "different file");
                AssertEqual("identity-original", File.ReadAllText(distinctOriginal));
                AssertEqual("identity-destination", File.ReadAllText(distinctDestination));

                var caseOriginal = Path.Combine(directory, "Office.conf");
                var caseDestination = Path.Combine(directory, "office.conf");
                var caseTemporary = Path.Combine(directory, "case-profile.tmp");
                File.WriteAllText(caseOriginal, "old-case");
                File.WriteAllText(caseTemporary, "new-case");
                ProfileFileTransaction.Commit(caseTemporary, caseDestination, caseOriginal);
                AssertEqual("new-case", File.ReadAllText(caseDestination));
                AssertEqual("office.conf", Path.GetFileName(Directory.GetFiles(directory, "office.conf").Single()));

                var caseRollbackOriginal = Path.Combine(directory, "Rollback.conf");
                var caseRollbackDestination = Path.Combine(directory, "rollback.conf");
                var caseRollbackTemporary = Path.Combine(directory, "case-rollback.tmp");
                File.WriteAllText(caseRollbackOriginal, "case-preserved");
                File.WriteAllText(caseRollbackTemporary, "case-replacement");
                using (new FileStream(caseRollbackTemporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    AssertThrows<IOException>(
                        () => ProfileFileTransaction.Commit(caseRollbackTemporary, caseRollbackDestination,
                            caseRollbackOriginal),
                        string.Empty);
                }

                AssertEqual("case-preserved", File.ReadAllText(caseRollbackOriginal));
                AssertEqual("Rollback.conf",
                    Path.GetFileName(Directory.GetFiles(directory, "rollback.conf").Single()));
                AssertEqual("case-replacement", File.ReadAllText(caseRollbackTemporary));

                var rollbackOriginal = Path.Combine(directory, "rollback-original.conf");
                var rollbackDestination = Path.Combine(directory, "rollback-renamed.conf");
                var missingTemporary = Path.Combine(directory, "missing.tmp");
                File.WriteAllText(rollbackOriginal, "preserved");

                AssertThrows<IOException>(
                    () => ProfileFileTransaction.Commit(missingTemporary, rollbackDestination, rollbackOriginal),
                    "does not exist");
                AssertEqual("preserved", File.ReadAllText(rollbackOriginal));
                AssertFalse(File.Exists(rollbackDestination),
                    "Expected a failed replacement to restore the original profile name.");

                var lockedOriginal = Path.Combine(directory, "locked-original.conf");
                var lockedDestination = Path.Combine(directory, "locked-renamed.conf");
                var lockedTemporary = Path.Combine(directory, "locked.tmp");
                File.WriteAllText(lockedOriginal, "locked");
                File.WriteAllText(lockedTemporary, "replacement");
                using (new FileStream(lockedOriginal, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    AssertThrows<IOException>(
                        () => ProfileFileTransaction.Commit(
                            lockedTemporary, lockedDestination, lockedOriginal),
                        string.Empty);
                }

                AssertEqual("locked", File.ReadAllText(lockedOriginal));
                AssertFalse(File.Exists(lockedDestination),
                    "Expected a failed original deletion to remove the new profile destination.");
                AssertEqual("replacement", File.ReadAllText(lockedTemporary));

                var invalidTemporary = Path.Combine(directory, "invalid.tmp");
                var invalidDestination = Path.Combine(directory, "invalid.conf");
                Directory.CreateDirectory(invalidTemporary);
                AssertThrows<IOException>(
                    () => ProfileFileTransaction.Commit(invalidTemporary, invalidDestination),
                    "directory");
                AssertTrue(Directory.Exists(invalidTemporary),
                    "Expected temporary-path validation to leave the invalid source untouched.");
                AssertFalse(File.Exists(invalidDestination),
                    "Expected temporary-path validation to run before mutating the destination.");

                var existingDestination = Path.Combine(directory, "existing.conf");
                var createTemporary = Path.Combine(directory, "create.tmp");
                File.WriteAllText(existingDestination, "existing");
                File.WriteAllText(createTemporary, "replacement");
                AssertThrows<IOException>(
                    () => ProfileFileTransaction.Commit(createTemporary, existingDestination),
                    "already exists");
                AssertEqual("existing", File.ReadAllText(existingDestination));
                AssertEqual("replacement", File.ReadAllText(createTemporary));

                var editedProfile = Path.Combine(directory, "edited.conf");
                var editTemporary = Path.Combine(directory, "edit.tmp");
                File.WriteAllText(editedProfile, "before edit");
                File.WriteAllText(editTemporary, "after edit");
                ProfileFileTransaction.Commit(editTemporary, editedProfile, editedProfile);
                AssertEqual("after edit", File.ReadAllText(editedProfile));
                AssertFalse(File.Exists(editTemporary),
                    "Expected a same-name profile edit to consume its temporary file.");
            }
            finally
            {
                TryDeleteDirectory(directory, true);
            }
        }

        private static void ProfileRenameRecoveryCompletesInterruptedTransactions()
        {
            WithTemporaryConfigFolder(() =>
            {
                var original = Profile.GetProfilePath("original");
                var destination = Profile.GetProfilePath("renamed");
                File.WriteAllText(original, "old");
                var temporary = ProfileFileTransaction.WriteTemporaryProfile("new");
                var journal = ProfileFileTransaction.CreateRenameJournalForTests(original, destination, temporary);

                File.Move(original, destination);
                ProfileFileTransaction.RecoverInterruptedTransactions();

                AssertFalse(File.Exists(original),
                    "Expected interrupted rename recovery to keep the original name retired.");
                AssertEqual("new", File.ReadAllText(destination));
                AssertFalse(File.Exists(temporary),
                    "Expected interrupted rename recovery to consume the staged profile.");
                AssertFalse(File.Exists(journal),
                    "Expected interrupted rename recovery to remove its journal.");

                var preparedOriginal = Profile.GetProfilePath("prepared");
                var preparedDestination = Profile.GetProfilePath("prepared-renamed");
                File.WriteAllText(preparedOriginal, "preserved");
                var preparedTemporary = ProfileFileTransaction.WriteTemporaryProfile("uncommitted");
                var preparedJournal = ProfileFileTransaction.CreateRenameJournalForTests(
                    preparedOriginal, preparedDestination, preparedTemporary);

                ProfileFileTransaction.RecoverInterruptedTransactions();

                AssertEqual("preserved", File.ReadAllText(preparedOriginal));
                AssertFalse(File.Exists(preparedDestination),
                    "Expected recovery to abandon a rename that had not moved the original profile.");
                AssertFalse(File.Exists(preparedTemporary),
                    "Expected recovery to remove abandoned staged profile contents.");
                AssertFalse(File.Exists(preparedJournal),
                    "Expected recovery to remove an abandoned rename journal.");

                var uppercaseActualPath = Path.Combine(Global.ConfigsFolder, "UPPER.CONF");
                var uppercaseLookupPath = Profile.GetProfilePath("UPPER");
                var uppercaseDestination = Profile.GetProfilePath("upper-renamed");
                File.WriteAllText(uppercaseActualPath, "uppercase preserved");
                var uppercaseTemporary = ProfileFileTransaction.WriteTemporaryProfile("uppercase replacement");
                var uppercaseJournal = ProfileFileTransaction.CreateRenameJournalForTests(
                    uppercaseLookupPath, uppercaseDestination, uppercaseTemporary);

                ProfileFileTransaction.RecoverInterruptedTransactions();

                AssertEqual("uppercase preserved", File.ReadAllText(uppercaseActualPath));
                AssertFalse(File.Exists(uppercaseDestination),
                    "Expected a prepared uppercase-extension rename to remain uncommitted.");
                AssertFalse(File.Exists(uppercaseTemporary),
                    "Expected uppercase-extension recovery to remove abandoned staged contents.");
                AssertFalse(File.Exists(uppercaseJournal),
                    "Expected uppercase-extension recovery to remove its journal.");

                var caseOriginal = Profile.GetProfilePath("Office");
                var caseDestination = Profile.GetProfilePath("office");
                File.WriteAllText(caseOriginal, "case-old");
                var caseTemporary = ProfileFileTransaction.WriteTemporaryProfile("case-new");
                var caseJournal = ProfileFileTransaction.CreateRenameJournalForTests(
                    caseOriginal, caseDestination, caseTemporary);
                AssertTrue(MoveFileEx(caseOriginal, caseDestination, MoveFileWriteThrough),
                    "Expected the test to reach the interrupted case-only rename state.");

                ProfileFileTransaction.RecoverInterruptedTransactions();

                AssertEqual("case-new", File.ReadAllText(caseDestination));
                AssertEqual("office.conf",
                    Path.GetFileName(Directory.GetFiles(Global.ConfigsFolder, "office.conf").Single()));
                AssertFalse(File.Exists(caseJournal),
                    "Expected case-only rename recovery to remove its journal.");
            });
        }

        private static void ProfileRenameRecoveryRejectsAmbiguousStates()
        {
            WithTemporaryConfigFolder(() =>
            {
                var original = Profile.GetProfilePath("original");
                var destination = Profile.GetProfilePath("renamed");
                File.WriteAllText(original, "original");
                File.WriteAllText(destination, "unrelated destination");
                var temporary = ProfileFileTransaction.WriteTemporaryProfile("replacement");
                var journal = ProfileFileTransaction.CreateRenameJournalForTests(original, destination, temporary);

                AssertThrows<InvalidDataException>(
                    () => ProfileFileTransaction.RecoverInterruptedTransactions(),
                    "both");
                AssertEqual("original", File.ReadAllText(original));
                AssertEqual("unrelated destination", File.ReadAllText(destination));
                AssertEqual("replacement", File.ReadAllText(temporary));
                AssertTrue(File.Exists(journal),
                    "Expected ambiguous recovery to retain its journal for diagnosis.");
            });
        }

        private static void ProfileTransactionRecoveryRemovesOrphanedTemporaryFiles()
        {
            WithTemporaryConfigFolder(() =>
            {
                var managedTemporary = ProfileFileTransaction.WriteTemporaryProfile("orphaned");
                var legacyTemporary = Path.Combine(Global.ConfigsFolder, Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(legacyTemporary, "legacy orphan");
                File.WriteAllText(Profile.GetProfilePath("office"), ValidConfig());

                ProfileFileTransaction.RecoverInterruptedTransactions();
                var profiles = Profile.GetProfiles();

                AssertEqual("office", profiles.Single());
                AssertFalse(File.Exists(managedTemporary),
                    "Expected startup recovery to remove managed orphaned profile contents.");
                AssertFalse(File.Exists(legacyTemporary),
                    "Expected startup recovery to remove temporary files left by older WireSock UI versions.");
            });
        }

        private static void ProfileRenameRecoveryRejectsActiveXmlContent()
        {
            WithTemporaryConfigFolder(() =>
            {
                Global.EnsureProfileTransactionsFolderExists();
                var journal = Path.Combine(Global.ProfileTransactionsFolder,
                    "rename-" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(journal,
                    "<!DOCTYPE ProfileRename [<!ENTITY source 'original.conf'>]>" +
                    "<ProfileRename Version='1'><Original>&source;</Original>" +
                    "<Destination>renamed.conf</Destination><Temporary>" +
                    Guid.NewGuid().ToString("N") + ".profile.tmp</Temporary></ProfileRename>");

                AssertThrows<XmlException>(
                    () => ProfileFileTransaction.RecoverInterruptedTransactions(),
                    "DTD");
                AssertTrue(File.Exists(journal),
                    "Expected rejected recovery metadata to remain available for diagnosis.");
            });
        }

        private static void TestExecutionTimeoutPolicyIsBounded()
        {
            var success = ExecuteTestWithTimeout(() => { }, 1000);
            AssertFalse(success.TimedOut, "Expected a completed test not to time out.");
            AssertTrue(success.Exception == null, "Expected a completed test not to report an exception.");

            var failure = ExecuteTestWithTimeout(() => throw new InvalidOperationException("expected"), 1000);
            AssertFalse(failure.TimedOut, "Expected a failed test not to be mislabeled as timed out.");
            AssertTrue(failure.Exception is InvalidOperationException,
                "Expected the timeout wrapper to preserve the original test exception.");

            using (var release = new ManualResetEventSlim(false))
            using (var finished = new ManualResetEventSlim(false))
            {
                var timeout = ExecuteTestWithTimeout(() =>
                {
                    try
                    {
                        release.Wait();
                    }
                    finally
                    {
                        finished.Set();
                    }
                }, 20);

                AssertTrue(timeout.TimedOut, "Expected a blocked test to hit the configured timeout.");
                AssertTrue(timeout.Exception == null,
                    "Expected a timeout not to fabricate an unrelated test exception.");
                release.Set();
                AssertTrue(finished.Wait(1000), "Expected the timed-out test worker to finish after release.");
            }
        }

        private static void SingleInstanceEventRejectsBroadAccess()
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var security = new EventWaitHandleSecurity();
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new EventWaitHandleAccessRule(
                administrators, EventWaitHandleRights.FullControl, AccessControlType.Allow));

            AssertTrue(FrmMain.IsSingleInstanceEventSecurityTrusted(security, currentUser, out var diagnostic),
                diagnostic ?? "Expected an administrator-only event ACL to be trusted.");

            security.AddAccessRule(new EventWaitHandleAccessRule(
                everyone, EventWaitHandleRights.Synchronize, AccessControlType.Allow));
            AssertFalse(FrmMain.IsSingleInstanceEventSecurityTrusted(security, currentUser, out diagnostic),
                "Expected a globally writable/openable ownership event to be rejected.");
            AssertTrue(diagnostic?.IndexOf("untrusted identity", StringComparison.OrdinalIgnoreCase) >= 0,
                $"Expected an actionable event ACL diagnostic, got '{diagnostic}'.");
        }

        private static void TunnelProfileStateMatchesSelectionsCaseInsensitively()
        {
            AssertTrue(FrmMain.IsTunnelProfileSelected("Office", "office"),
                "Expected profile selection matching to follow the case-insensitive Windows filename contract.");
            AssertFalse(FrmMain.IsTunnelProfileSelected("Home", "office"),
                "Expected a different selected profile not to display the active tunnel state.");
            AssertFalse(FrmMain.IsTunnelProfileSelected(null, "office"),
                "Expected a missing selection not to display the active tunnel state.");
            AssertFalse(FrmMain.IsTunnelProfileSelected("Office", null),
                "Expected a missing active profile not to mark a selection as active.");
        }

        private static void NetworkLockEnumMatchesWgboosterAbi()
        {
            AssertEqual(0, (int)WireguardBoosterExports.WgbNetworkLockMode.Disabled);
            AssertEqual(1, (int)WireguardBoosterExports.WgbNetworkLockMode.Enabled);
        }

        private static void WireSockExportsUseRestrictedDllSearch()
        {
            var methods = typeof(WireguardBoosterExports)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method =>
                {
                    var dllImport = method.GetCustomAttributes(typeof(DllImportAttribute), false)
                        .OfType<DllImportAttribute>()
                        .SingleOrDefault();
                    return string.Equals(dllImport?.Value, "wgbooster.dll", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            AssertTrue(methods.Count > 0, "Expected wgbooster export methods to be discovered.");

            foreach (var method in methods)
            {
                var attribute = method.GetCustomAttributes(typeof(DefaultDllImportSearchPathsAttribute), false)
                    .OfType<DefaultDllImportSearchPathsAttribute>()
                    .SingleOrDefault();

                if (attribute == null)
                    throw new InvalidOperationException(
                        $"Expected wgbooster export '{method.Name}' to declare restricted DLL search paths.");

                var paths = attribute.Paths;
                AssertTrue((paths & DllImportSearchPath.UserDirectories) != 0,
                    $"Expected '{method.Name}' to search only explicitly added user directories.");
                AssertTrue((paths & DllImportSearchPath.System32) != 0,
                    $"Expected '{method.Name}' to allow System32 dependency resolution.");
                AssertFalse((paths & DllImportSearchPath.AssemblyDirectory) != 0,
                    $"Expected '{method.Name}' not to fall back to the executable directory.");
            }
        }

        private static void WireSockHandleBooleansMatchCppAbi()
        {
            foreach (var methodName in new[] { "wgb_get_handle_ex", "wgbp_get_handle_ex" })
            {
                var method = typeof(WireguardBoosterExports).GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                    throw new InvalidOperationException($"Expected export '{methodName}' to exist.");

                var parameters = method.GetParameters();
                AssertEqual(5, parameters.Length);
                foreach (var parameter in parameters.Skip(3))
                {
                    var marshalAs = parameter.GetCustomAttributes(typeof(MarshalAsAttribute), false)
                        .OfType<MarshalAsAttribute>()
                        .SingleOrDefault();
                    AssertTrue(marshalAs?.Value == UnmanagedType.I1,
                        $"Expected '{methodName}' parameter '{parameter.Name}' to marshal C++ bool as one byte.");
                }
            }
        }

        private static void WireSockLogCallbackDecodesUtf8Explicitly()
        {
            var parameter = typeof(WireguardBoosterExports.LogPrinter).GetMethod("Invoke")?.GetParameters().Single();

            AssertTrue(parameter?.ParameterType == typeof(IntPtr),
                "Expected the native log callback to receive the char* as an IntPtr on .NET Framework.");
            AssertFalse(parameter.GetCustomAttributes(typeof(MarshalAsAttribute), false).Any(),
                "Expected UTF-8 callback decoding to avoid runtime string marshaling.");

            const string expected = "wgbooster: \u041F\u0440\u0438\u0432\u0435\u0442 \u4E16\u754C";
            var bytes = Encoding.UTF8.GetBytes(expected);
            var message = Marshal.AllocHGlobal(bytes.Length + 1);
            try
            {
                Marshal.Copy(bytes, 0, message, bytes.Length);
                Marshal.WriteByte(message, bytes.Length, 0);
                AssertEqual(expected, WireguardBoosterExports.DecodeLogMessage(message));
                AssertEqual(string.Empty, WireguardBoosterExports.DecodeLogMessage(IntPtr.Zero));
            }
            finally
            {
                Marshal.FreeHGlobal(message);
            }

            var unterminatedBytes = Enumerable.Repeat((byte)'x',
                WireguardBoosterExports.MaxLogMessageBytes + 1).ToArray();
            var unterminatedMessage = Marshal.AllocHGlobal(unterminatedBytes.Length);
            try
            {
                Marshal.Copy(unterminatedBytes, 0, unterminatedMessage, unterminatedBytes.Length);
                AssertThrows<ArgumentException>(
                    () => WireguardBoosterExports.DecodeLogMessage(unterminatedMessage),
                    "not null-terminated");
            }
            finally
            {
                Marshal.FreeHGlobal(unterminatedMessage);
            }
        }

        private static void StatsStructMatchesWgboosterAbi()
        {
            AssertEqual(32, Marshal.SizeOf<WireguardBoosterExports.WgbStats>());
            AssertEqual(0, Marshal.OffsetOf<WireguardBoosterExports.WgbStats>("time_since_last_handshake").ToInt32());
            AssertEqual(8, Marshal.OffsetOf<WireguardBoosterExports.WgbStats>("tx_bytes").ToInt32());
            AssertEqual(16, Marshal.OffsetOf<WireguardBoosterExports.WgbStats>("rx_bytes").ToInt32());
            AssertEqual(24, Marshal.OffsetOf<WireguardBoosterExports.WgbStats>("estimated_loss").ToInt32());
            AssertEqual(28, Marshal.OffsetOf<WireguardBoosterExports.WgbStats>("estimated_rtt").ToInt32());
        }

        private static void SdkSmokeRejectsUnsafeIntegrationProfiles()
        {
            var profilePath = WriteConfig(
                "[Interface]\n" +
                $"PrivateKey = {PrivateKey}\n" +
                "Address = 10.0.0.2/32\n" +
                "PreUp = cmd.exe /c echo unsafe\n" +
                "\n" +
                "[Peer]\n" +
                $"PublicKey = {PublicKey}\n" +
                "Endpoint = example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0\n");

            AssertThrows<InvalidOperationException>(
                () => ValidateSdkIntegrationProfileContents(profilePath, "WIRESOCKUI_TEST_PROFILE"),
                "script hooks");

            const string variableName = "WIRESOCKUI_TEST_PROFILE_UNTRUSTED_TEST";
            var originalValue = Environment.GetEnvironmentVariable(variableName);
            try
            {
                Environment.SetEnvironmentVariable(variableName, profilePath);
                AssertThrows<InvalidOperationException>(
                    () => GetRequiredSdkProfilePath(variableName),
                    "writable by or owned by non-administrative users");
            }
            finally
            {
                Environment.SetEnvironmentVariable(variableName, originalValue);
            }
        }

        private static void SdkSmokeCleansUpFailedTunnelCreation()
        {
            var api = new FakeWireSockNativeApi { CreateTunnelResult = false };
            WireguardBoosterExports.LogPrinter logPrinter = message => { };

            AssertThrows<Win32Exception>(
                () => RunSdkModeSmoke(api, WireSockManager.Mode.VirtualAdapter, "test.conf", logPrinter),
                "failed to create");
            AssertEqual(1, api.DropCount);
            AssertEqual(1, api.ReleaseCount);
            AssertTrue(api.LastPreserveNetworkLock == false,
                "Expected failed SDK smoke cleanup to release rather than preserve network lock state.");

            var failedLockCleanupApi = new FakeWireSockNativeApi
            {
                CreateTunnelResult = false,
                SetNetworkLockFailureOnCall = 4,
                SetNetworkLockError = 5
            };
            AssertThrows<AggregateException>(
                () => RunSdkModeSmoke(failedLockCleanupApi, WireSockManager.Mode.Transparent, "test.conf", logPrinter),
                "");
            AssertEqual(1, failedLockCleanupApi.DropCount);
            AssertEqual(1, failedLockCleanupApi.ReleaseCount);
        }

        private static void SdkSmokeRunsFinalCleanupAfterFailures()
        {
            var cleanupCalled = false;
            AssertThrows<InvalidOperationException>(
                () => RunWithFinalCleanup(
                    () => throw new InvalidOperationException("mode failed"),
                    () => cleanupCalled = true),
                "mode failed");
            AssertTrue(cleanupCalled, "Expected final SDK cleanup to run after a mode failure.");

            AssertThrows<AggregateException>(
                () => RunWithFinalCleanup(
                    () => throw new InvalidOperationException("mode failed"),
                    () => throw new InvalidOperationException("cleanup failed")),
                "");
        }

        private sealed class FakeWireSockNativeApi : IWireSockNativeApi
        {
            public IntPtr CreateHandleResult { get; set; } = new IntPtr(1234);
            public bool CreateTunnelResult { get; set; } = true;
            public bool StartResult { get; set; } = true;
            public int StartError { get; set; }
            public bool TunnelActive { get; set; } = true;
            public int TunnelActiveError { get; set; }
            public WireguardBoosterExports.WgbStats TunnelState { get; set; } =
                new WireguardBoosterExports.WgbStats { time_since_last_handshake = -1, estimated_rtt = -1 };
            public int TunnelStateError { get; set; }
            public WireguardBoosterExports.WgbNetworkLockMode NetworkLockMode { get; set; }
            public int NetworkLockModeError { get; set; }
            public bool DropResult { get; set; } = true;
            public int DropError { get; set; }
            public bool? LastPreserveNetworkLock { get; private set; }
            public int DropCount { get; private set; }
            public int GetHandleCount { get; private set; }
            public int ReleaseCount { get; private set; }
            public int ReleaseFailuresRemaining { get; set; }
            public bool LastEnableAnalytics { get; private set; }
            public int TunnelActiveQueryCount { get; private set; }
            public int TunnelStateQueryCount { get; private set; }
            public int NetworkLockQueryCount { get; private set; }
            public int SetLogLevelCount { get; private set; }
            public int SetLogLevelFailuresRemaining { get; set; }
            public WireguardBoosterExports.WgbLogLevel LastCreateLogLevel { get; private set; }
            public int SetNetworkLockCount { get; private set; }
            public int SetNetworkLockFailureOnCall { get; set; }
            public int SetNetworkLockError { get; set; }
            public ManualResetEventSlim DropEntered { get; set; }
            public ManualResetEventSlim ContinueDrop { get; set; }

            public IntPtr CreateHandle(WireSockManager.Mode mode, WireguardBoosterExports.LogPrinter logPrinter,
                WireguardBoosterExports.WgbLogLevel logLevel, bool enableTrafficCapture, bool enableAnalytics)
            {
                SetLastErrorForTest(0);
                GetHandleCount++;
                LastEnableAnalytics = enableAnalytics;
                LastCreateLogLevel = logLevel;
                return CreateHandleResult;
            }

            public void ReleaseHandle(WireSockManager.Mode mode, IntPtr handle)
            {
                SetLastErrorForTest(0);
                ReleaseCount++;
                if (ReleaseFailuresRemaining > 0)
                {
                    ReleaseFailuresRemaining--;
                    throw new InvalidOperationException("Simulated release_handle failure.");
                }
            }

            public void SetLogLevel(WireSockManager.Mode mode, IntPtr handle,
                WireguardBoosterExports.WgbLogLevel logLevel)
            {
                SetLastErrorForTest(0);
                SetLogLevelCount++;
                if (SetLogLevelFailuresRemaining > 0)
                {
                    SetLogLevelFailuresRemaining--;
                    throw new InvalidOperationException("Simulated set_log_level failure.");
                }
            }

            public bool CreateTunnelFromFile(WireSockManager.Mode mode, IntPtr handle, string fileName)
            {
                SetLastErrorForTest(0);
                return CreateTunnelResult;
            }

            public bool StartTunnel(WireSockManager.Mode mode, IntPtr handle)
            {
                SetLastErrorForTest((uint)StartError);
                return StartResult;
            }

            public bool StopTunnel(WireSockManager.Mode mode, IntPtr handle)
            {
                SetLastErrorForTest(0);
                return true;
            }

            public bool DropTunnel(WireSockManager.Mode mode, IntPtr handle, bool preserveNetworkLock)
            {
                SetLastErrorForTest((uint)DropError);
                LastPreserveNetworkLock = preserveNetworkLock;
                DropCount++;
                DropEntered?.Set();
                ContinueDrop?.Wait();
                return DropResult;
            }

            public bool GetTunnelActive(WireSockManager.Mode mode, IntPtr handle)
            {
                SetLastErrorForTest((uint)TunnelActiveError);
                TunnelActiveQueryCount++;
                return TunnelActive;
            }

            public WireguardBoosterExports.WgbStats GetTunnelState(WireSockManager.Mode mode, IntPtr handle)
            {
                SetLastErrorForTest((uint)TunnelStateError);
                TunnelStateQueryCount++;
                return TunnelState;
            }

            public bool SetNetworkLockMode(WireSockManager.Mode mode, IntPtr handle,
                WireguardBoosterExports.WgbNetworkLockMode networkLockMode)
            {
                SetNetworkLockCount++;
                if (SetNetworkLockFailureOnCall == SetNetworkLockCount)
                {
                    SetLastErrorForTest((uint)SetNetworkLockError);
                    return false;
                }

                SetLastErrorForTest(0);
                NetworkLockMode = networkLockMode;
                return true;
            }

            public WireguardBoosterExports.WgbNetworkLockMode GetNetworkLockMode(WireSockManager.Mode mode,
                IntPtr handle)
            {
                SetLastErrorForTest((uint)NetworkLockModeError);
                NetworkLockQueryCount++;
                return NetworkLockMode;
            }
        }

        private sealed class FakeNetworkLockApi : INetworkLockApi
        {
            public bool Active { get; set; }
            public bool QueryResult { get; set; } = true;
            public bool ResetResult { get; set; } = true;
            public int ResetCount { get; private set; }

            public bool TryIsActive(out bool active, out string diagnostic)
            {
                active = Active;
                diagnostic = QueryResult ? null : "simulated query failure";
                return QueryResult;
            }

            public bool TryReset(out string diagnostic)
            {
                ResetCount++;
                diagnostic = ResetResult ? null : "simulated reset failure";
                if (ResetResult)
                    Active = false;
                return ResetResult;
            }
        }

        private sealed class NonPumpingSynchronizationContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object state)
            {
                // Intentionally do not dispatch posted work. The timeout helper must not post here.
            }
        }

        private sealed class QueuedSynchronizeInvoke : ISynchronizeInvoke
        {
            private object[] _arguments;
            private Delegate _callback;

            public bool InvokeRequired => true;

            public IAsyncResult BeginInvoke(Delegate method, object[] args)
            {
                _callback = method;
                _arguments = args;
                return Task.CompletedTask;
            }

            public object EndInvoke(IAsyncResult result)
            {
                return null;
            }

            public object Invoke(Delegate method, object[] args)
            {
                return method.DynamicInvoke(args);
            }

            public void RunCallback()
            {
                var callback = _callback;
                var arguments = _arguments;
                _callback = null;
                _arguments = null;

                if (callback == null)
                    throw new InvalidOperationException("No UI callback is queued.");

                callback.DynamicInvoke(arguments);
            }
        }

        private sealed class ThrowingToStringValue
        {
            public override string ToString()
            {
                throw new InvalidOperationException("Simulated diagnostic formatting failure.");
            }
        }

        private sealed class ChunkedReadStream : Stream
        {
            private readonly int _chunkSize;
            private readonly MemoryStream _stream;

            internal ChunkedReadStream(byte[] contents, int chunkSize)
            {
                _stream = new MemoryStream(contents ?? throw new ArgumentNullException(nameof(contents)), false);
                _chunkSize = chunkSize > 0 ? chunkSize : throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _stream.Read(buffer, offset, Math.Min(count, _chunkSize));
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _stream.Dispose();
                base.Dispose(disposing);
            }
        }

        private static string WriteConfig(string contents)
        {
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"{Guid.NewGuid():N}.conf");
            File.WriteAllText(path, contents);
            return path;
        }

        private static WireguardConfigParser.ConfigParser ParseConfig(string path)
        {
            WireguardConfigParser.ConfigParser parser = null;
            using (var file = SecureFileSystem.OpenFileForBoundedRead(path, Profile.MaxProfileSizeBytes))
                file.UseReadStream(stream => parser = new WireguardConfigParser.ConfigParser(stream));
            return parser;
        }

        private static string ValidConfig()
        {
            return "[Interface]\n" +
                   $"PrivateKey = {PrivateKey}\n" +
                   "Address = 10.0.0.2/32\n" +
                   "\n" +
                   "[Peer]\n" +
                   $"PublicKey = {PublicKey}\n" +
                   "Endpoint = example.com:51820\n" +
                   "AllowedIPs = 0.0.0.0/0\n";
        }

        private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
        {
            if (CreateSymbolicLink(linkPath, targetPath,
                    SymbolicLinkFlagFile | SymbolicLinkFlagAllowUnprivilegedCreate))
                return true;

            return CreateSymbolicLink(linkPath, targetPath, SymbolicLinkFlagFile);
        }

        private static bool TryCreateProfileReparsePoint(string linkPath, string targetPath, out bool isFileLink)
        {
            isFileLink = false;

            if (TryCreateFileSymbolicLink(linkPath, targetPath))
            {
                isFileLink = true;
                return true;
            }

            var targetDirectory = targetPath + ".junction-target";
            try
            {
                Directory.CreateDirectory(targetDirectory);
                return TryCreateDirectoryJunction(linkPath, targetDirectory);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCreateDirectoryJunction(string linkPath, string targetDirectory)
        {
            if (linkPath.IndexOf('"') >= 0 || targetDirectory.IndexOf('"') >= 0)
                return false;

            try
            {
                var startInfo = new ProcessStartInfo("cmd.exe",
                    $"/c mklink /J \"{linkPath}\" \"{targetDirectory}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                        return false;

                    if (!process.WaitForExit(5000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // Best-effort cleanup; the caller will report the unavailable reparse test.
                        }

                        return false;
                    }

                    return process.ExitCode == 0 && Directory.Exists(linkPath);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void SkipOrFail(string message)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(message);

            Console.WriteLine($"SKIP {message}");
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup must not hide the original test failure.
            }
        }

        private static void TryDeleteDirectory(string path, bool recursive)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive);
            }
            catch
            {
                // Best-effort cleanup must not hide the original test failure.
            }
        }

        private static void WithTemporaryConfigFolder(Action action)
        {
            var originalConfigsFolder = Global.ConfigsFolder;
            var originalOverride = Global.AllowUnsecuredConfigFolderOverrideForTests;
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(directory);
                Global.ConfigsFolder = directory;
                Global.AllowUnsecuredConfigFolderOverrideForTests = true;
                action();
            }
            finally
            {
                Global.ConfigsFolder = originalConfigsFolder;
                Global.AllowUnsecuredConfigFolderOverrideForTests = originalOverride;

                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    // Best-effort cleanup must not hide the original test failure.
                }
            }
        }

        private static void WithTemporarySecureMainFolder(Action action)
        {
            var originalSecureMainFolder = Global.SecureMainFolder;
            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(directory);
                Global.SecureMainFolder = directory;
                action();
            }
            finally
            {
                Global.SecureMainFolder = originalSecureMainFolder;

                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    // Best-effort cleanup must not hide the original test failure.
                }
            }
        }

        private static void ProcessPickerPreservesExecutableMatchNames()
        {
            AssertEqual("chrome.exe", TaskManager.GetProcessMatchName(
                new ProcessEntry(1, "chrome.exe", @"C:\Program Files\Google\Chrome\chrome.exe", "user")));
            AssertEqual("wireguard.exe", TaskManager.GetProcessMatchName(
                new ProcessEntry(2, "wireguard", null, "user")));
            AssertTrue(TaskManager.GetProcessMatchName(null) == null,
                "Expected an unavailable process entry not to create an application rule.");
        }

        private static void AutoRunValidatesCompleteTaskDefinition()
        {
            var executablePath = Assembly.GetExecutingAssembly().Location;
            string currentUserId;
            string currentUserName;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                currentUserId = identity.User?.Value ?? throw new InvalidOperationException("Current user SID unavailable.");
                currentUserName = identity.Name;
            }

            AssertTrue(FrmSettings.IsSameTaskUser(currentUserName, currentUserId),
                "Expected account names and SID strings for the same user to compare equal.");
            AssertFalse(FrmSettings.IsSameTaskUser(currentUserId,
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value),
                "Expected different user SIDs not to compare equal.");

            using (var taskService = new Microsoft.Win32.TaskScheduler.TaskService())
            using (var definition = taskService.NewTask())
            {
                definition.Principal.UserId = currentUserId;
                definition.Principal.LogonType = Microsoft.Win32.TaskScheduler.TaskLogonType.InteractiveToken;
                definition.Principal.RunLevel = Microsoft.Win32.TaskScheduler.TaskRunLevel.Highest;
                definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                definition.Triggers.Add(new Microsoft.Win32.TaskScheduler.LogonTrigger { UserId = currentUserId });
                definition.Actions.Add(new Microsoft.Win32.TaskScheduler.ExecAction(executablePath));

                AssertTrue(FrmSettings.IsTaskDefinitionOwnedByExecutable(
                        definition, true, executablePath),
                    "Expected the exact elevated logon task shape to be recognized.");
                AssertFalse(FrmSettings.IsTaskDefinitionOwnedByExecutable(
                        definition, false, executablePath),
                    "Expected a disabled task not to be reported as active autorun.");

                definition.Settings.ExecutionTimeLimit = TimeSpan.FromHours(72);
                AssertFalse(FrmSettings.IsTaskDefinitionOwnedByExecutable(
                        definition, true, executablePath),
                    "Expected a task with the Task Scheduler 72-hour limit not to be accepted.");
                definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;

                ((Microsoft.Win32.TaskScheduler.LogonTrigger)definition.Triggers[0]).UserId = null;
                definition.Settings.ExecutionTimeLimit = TimeSpan.FromHours(72);
                AssertFalse(FrmSettings.IsTaskDefinitionOwnedByExecutable(
                        definition, true, executablePath),
                    "Expected an any-user logon trigger not to be accepted.");
                AssertTrue(FrmSettings.IsTaskDefinitionReplaceableByExecutable(definition, executablePath),
                    "Expected an older WireSock UI task to remain replaceable during migration.");

                definition.Principal.UserId =
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
                AssertFalse(FrmSettings.IsTaskDefinitionReplaceableByExecutable(definition, executablePath),
                    "Expected another user's autorun principal not to be replaceable.");

                definition.Principal.UserId = null;
                ((Microsoft.Win32.TaskScheduler.LogonTrigger)definition.Triggers[0]).UserId =
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
                AssertFalse(FrmSettings.IsTaskDefinitionReplaceableByExecutable(definition, executablePath),
                    "Expected another user's logon trigger not to be replaceable.");

                definition.Principal.UserId = currentUserName;
                ((Microsoft.Win32.TaskScheduler.LogonTrigger)definition.Triggers[0]).UserId = currentUserId;
                definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                AssertTrue(FrmSettings.IsTaskDefinitionReplaceableByExecutable(definition, executablePath),
                    "Expected a current-user account name and SID trigger to remain replaceable.");

                definition.Actions.Add(new Microsoft.Win32.TaskScheduler.ExecAction("cmd.exe"));
                AssertFalse(FrmSettings.IsTaskDefinitionOwnedByExecutable(
                        definition, true, executablePath),
                    "Expected tasks with additional actions not to be treated as owned.");
            }
        }

        private static void ShellLinkHresultValidationUsesSignedFailureSemantics()
        {
            ShellLink.VerifySucceeded(0);
            ShellLink.VerifySucceeded(1);
            ShellLink.VerifySucceeded(2);
            AssertThrows<COMException>(() => ShellLink.VerifySucceeded(0x80004005), "");
        }

        private static string FindRepositoryFile(params string[] relativePath)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = relativePath.Aggregate(directory.FullName, Path.Combine);
                if (File.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }

            throw new FileNotFoundException(
                $"Could not locate repository file '{Path.Combine(relativePath)}' from the test output directory.");
        }

        private static void WithTemporaryLegacyMigrationFolders(Action<string, string> action)
        {
            var originalSecureMainFolder = Global.SecureMainFolder;
            var originalConfigsFolder = Global.ConfigsFolder;
            var originalPendingFolder = Global.PendingLegacyProfilesFolder;
            var originalLegacyFolder = Global.LegacyConfigsFolder;
            var originalOverride = Global.AllowUnsecuredConfigFolderOverrideForTests;
            var originalOwnerWriteFailure = SecureFileSystem.AllowOwnerWriteFailureForTests;
            var root = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            var secureFolder = Path.Combine(root, "secure");
            var legacyFolder = Path.Combine(root, "legacy");
            var pendingFolder = Path.Combine(secureFolder, "PendingLegacyProfiles");

            try
            {
                Directory.CreateDirectory(legacyFolder);
                Global.SecureMainFolder = secureFolder;
                Global.ConfigsFolder = Path.Combine(secureFolder, "Configs");
                Global.PendingLegacyProfilesFolder = pendingFolder;
                Global.LegacyConfigsFolder = legacyFolder;
                Global.AllowUnsecuredConfigFolderOverrideForTests = false;
                SecureFileSystem.AllowOwnerWriteFailureForTests = true;
                Global.EnsureConfigsFolderExists();
                action(legacyFolder, pendingFolder);
            }
            finally
            {
                Global.SecureMainFolder = originalSecureMainFolder;
                Global.ConfigsFolder = originalConfigsFolder;
                Global.PendingLegacyProfilesFolder = originalPendingFolder;
                Global.LegacyConfigsFolder = originalLegacyFolder;
                Global.AllowUnsecuredConfigFolderOverrideForTests = originalOverride;
                SecureFileSystem.AllowOwnerWriteFailureForTests = originalOwnerWriteFailure;

                TryDeleteDirectory(root, true);
            }
        }

        private static byte[] ParseHex(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Length % 2 != 0) throw new FormatException("Hexadecimal input must contain complete bytes.");

            var result = new byte[value.Length / 2];
            for (var index = 0; index < result.Length; index++)
                result[index] = Convert.ToByte(value.Substring(index * 2, 2), 16);
            return result;
        }

        private static string ToHex(IEnumerable<byte> value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return string.Concat(value.Select(item => item.ToString("x2")));
        }

        private static void AssertThrows<T>(Action action, string messagePart) where T : Exception
        {
            try
            {
                action();
            }
            catch (T ex)
            {
                if (messagePart == null || ex.Message.IndexOf(messagePart, StringComparison.OrdinalIgnoreCase) >= 0)
                    return;

                throw new Exception($"Expected exception message to contain '{messagePart}', got '{ex.Message}'.");
            }

            throw new Exception($"Expected {typeof(T).Name}.");
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
                throw new Exception(message);
        }

        private static void AssertFalse(bool condition, string message)
        {
            if (condition)
                throw new Exception(message);
        }

        private static void AssertEqual(string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new Exception($"Expected '{expected}', got '{actual}'.");
        }

        private static void AssertEqual(int expected, int actual)
        {
            if (expected != actual)
                throw new Exception($"Expected '{expected}', got '{actual}'.");
        }
    }
}
