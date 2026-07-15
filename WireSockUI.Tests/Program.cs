using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
                { "Profile enumeration accepts uppercase conf extension", ProfileEnumerationAcceptsUppercaseConfExtension },
                { "Profile enumeration creates missing overridden config folder", ProfileEnumerationCreatesMissingOverriddenConfigFolder },
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
                { "Global removes configuration file reparse points by handle", GlobalRemovesConfigurationFileReparsePointsByHandle },
                { "Profile rejects user-writable secured files", ProfileRejectsUserWritableSecuredFiles },
                { "Release version parser handles SemVer tags", ReleaseVersionParserHandlesSemVerTags },
                { "Program path normalization preserves drive roots", ProgramPathNormalizationPreservesDriveRoots },
                { "Program rejects untrusted application payloads", ProgramRejectsUntrustedApplicationPayloads },
                { "Program enumerates nested application payloads", ProgramEnumeratesNestedApplicationPayloads },
                { "Program distinguishes x64 and ARM64 PE images", ProgramDistinguishesBinaryArchitectures },
                { "Program rejects user-writable WireSock library directories", ProgramRejectsUserWritableWireSockLibraryDirectories },
                { "Program detects user-writable WireSock library files", ProgramDetectsUserWritableWireSockLibraryFiles },
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
                { "Legacy migration preserves approved duplicates", LegacyMigrationPreservesApprovedDuplicates },
                { "Legacy migration rejects oversized files", LegacyMigrationRejectsOversizedFiles },
                { "Legacy migration rejects reparse point sources", LegacyMigrationRejectsReparsePointSources },
                { "Legacy migration accepts scripts only into quarantine", LegacyMigrationAcceptsScriptsOnlyIntoQuarantine },
                { "Legacy migration completion removes staged sources", LegacyMigrationCompletionRemovesStagedSources },
                { "Native recovery marker cleanup removes directory markers", NativeRecoveryMarkerCleanupRemovesDirectoryMarkers },
                { "Native recovery marker replacement does not follow hard links", NativeRecoveryMarkerReplacementDoesNotFollowHardLinks },
                { "Secure filesystem delete handles block concurrent writes", SecureFileSystemDeleteHandlesBlockConcurrentWrites },
                { "Secure filesystem snapshots permit shell-link inspection", SecureFileSystemSnapshotsPermitShellLinkInspection },
                { "Secure filesystem reads text through validated handles", SecureFileSystemReadsTextThroughValidatedHandles },
                { "Secure filesystem rejects writable hard links", SecureFileSystemRejectsWritableHardLinks },
                { "Tunnel session coordinator enforces recovery invariants", TunnelSessionCoordinatorEnforcesRecoveryInvariants },
                { "Tunnel monitor stops after a bounded query timeout", TunnelMonitorStopsAfterBoundedQueryTimeout },
                { "Tunnel monitor preserves statistics query timeouts", TunnelMonitorPreservesStatisticsQueryTimeouts },
                { "Tunnel monitor suppresses canceled query updates", TunnelMonitorSuppressesCanceledQueryUpdates },
                { "Tunnel monitor classifies unexpected statistics failures", TunnelMonitorClassifiesUnexpectedStatisticsFailures },
                { "Diagnostic logging redacts credentials", DiagnosticLoggingRedactsCredentials },
                { "Diagnostic logging bounds oversized records", DiagnosticLoggingBoundsOversizedRecords },
                { "Native query distinguishes error sentinels", NativeQueryDistinguishesErrorSentinels },
                { "Settings upgrade runs exactly once", SettingsUpgradeRunsExactlyOnce },
                { "Persisted setting transactions compensate failures", PersistedSettingTransactionsCompensateFailures },
                { "Settings updates stop after the first failure", SettingsUpdatesStopAfterFirstFailure },
                { "Editor validates Amnezia options", EditorValidatesAmneziaOptions },
                { "AppUserModelID is path seeded", AppUserModelIdIsPathSeeded },
                { "Notification shortcut name is path seeded", NotificationShortcutNameIsPathSeeded },
                { "Shell link HRESULT validation uses signed failure semantics", ShellLinkHresultValidationUsesSignedFailureSemantics },
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
                { "Profile rename commits and rolls back transactionally", ProfileRenameCommitsAndRollsBackTransactionally },
                { "Single-instance event rejects broad access", SingleInstanceEventRejectsBroadAccess },
                { "Network lock enum matches wgbooster ABI", NetworkLockEnumMatchesWgboosterAbi },
                { "WireSock exports use restricted DLL search", WireSockExportsUseRestrictedDllSearch },
                { "WireSock handle booleans match the C++ ABI", WireSockHandleBooleansMatchCppAbi },
                { "WireSock log callback decodes UTF-8 explicitly", WireSockLogCallbackDecodesUtf8Explicitly },
                { "Stats struct matches wgbooster ABI", StatsStructMatchesWgboosterAbi }
            };

            var failures = 0;
            foreach (var test in tests)
            {
                try
                {
                    test.Value();
                    Console.WriteLine($"PASS {test.Key}");
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine($"FAIL {test.Key}: {ex.Message}");
                }
            }

            return failures == 0 ? 0 : 1;
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
                var handle = api.CreateHandle(WireSockManager.Mode.Transparent, logPrinter,
                    WireguardBoosterExports.WgbLogLevel.Error, false, false);
                if (handle == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "wgbooster failed to create a handle.");

                var tunnelCreated = false;
                var tunnelStarted = false;
                try
                {
                    var profilePath = Environment.GetEnvironmentVariable("WIRESOCKUI_TEST_PROFILE");
                    if (!string.IsNullOrWhiteSpace(profilePath))
                    {
                        profilePath = Path.GetFullPath(profilePath);
                        if (!File.Exists(profilePath))
                            throw new FileNotFoundException("The SDK integration profile was not found.", profilePath);

                        if (!api.CreateTunnelFromFile(WireSockManager.Mode.Transparent, handle, profilePath))
                            throw new Win32Exception(Marshal.GetLastWin32Error(),
                                "wgbooster failed to create the integration tunnel.");
                        tunnelCreated = true;

                        if (!api.StartTunnel(WireSockManager.Mode.Transparent, handle))
                            throw new Win32Exception(Marshal.GetLastWin32Error(),
                                "wgbooster failed to start the integration tunnel.");
                        tunnelStarted = true;

                        if (!api.GetTunnelActive(WireSockManager.Mode.Transparent, handle))
                            throw new Win32Exception(Marshal.GetLastWin32Error(),
                                "wgbooster did not report the integration tunnel active.");
                    }
                }
                finally
                {
                    Exception cleanupException = null;
                    var tunnelDropped = !tunnelCreated;
                    try
                    {
                        if (tunnelStarted && !api.StopTunnel(WireSockManager.Mode.Transparent, handle))
                            cleanupException = new Win32Exception(Marshal.GetLastWin32Error(),
                                "wgbooster failed to stop the integration tunnel.");
                    }
                    catch (Exception ex)
                    {
                        cleanupException = ex;
                    }

                    try
                    {
                        if (tunnelCreated)
                            tunnelDropped = api.DropTunnel(WireSockManager.Mode.Transparent, handle, false);
                        if (!tunnelDropped && cleanupException == null)
                            cleanupException = new Win32Exception(Marshal.GetLastWin32Error(),
                                "wgbooster failed to drop the integration tunnel.");
                    }
                    catch (Exception ex)
                    {
                        if (cleanupException == null)
                            cleanupException = ex;
                    }

                    try
                    {
                        if (tunnelDropped)
                            api.ReleaseHandle(WireSockManager.Mode.Transparent, handle);
                    }
                    catch (Exception ex)
                    {
                        if (cleanupException == null)
                            cleanupException = ex;
                    }
                    finally
                    {
                        GC.KeepAlive(logPrinter);
                    }

                    if (cleanupException != null)
                        throw cleanupException;
                }

                Console.WriteLine("PASS real wgbooster.dll load and handle lifecycle smoke test.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL real wgbooster.dll smoke test: {ex.GetBaseException().Message}");
                return 1;
            }
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

        private static void ProfileEnumerationAcceptsUppercaseConfExtension()
        {
            WithTemporaryConfigFolder(() =>
            {
                File.WriteAllText(Path.Combine(Global.ConfigsFolder, "Office.CONF"), string.Empty);

                var profiles = Profile.GetProfiles().ToList();
                AssertTrue(profiles.Contains("Office"), "Expected .CONF profiles to be listed on Windows.");
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

            var section = new WireguardConfigParser.ConfigParser(path).GetSection("Interface");

            AssertTrue(section.ContainsKey("BypassLanTraffic"), "Expected #@ws: directive to become a normal key.");
            AssertFalse(section.ContainsKey("VirtualAdapterMode"),
                "Expected non-SDK WireSock directive prefixes to remain comments.");
            AssertEqual("true", section["BypassLanTraffic"]);
        }

        private static void ParserMatchesSdkCasing()
        {
            var path = WriteConfig("[interface]\nprivatekey = value\n");
            var parser = new WireguardConfigParser.ConfigParser(path);

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
                var peer = new WireguardConfigParser.ConfigParser(path).GetSection("Peer");
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
                AssertThrows<FormatException>(() => new WireguardConfigParser.ConfigParser(path), "line 2");
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
                AssertThrows<FormatException>(() => new WireguardConfigParser.ConfigParser(path), "before any section");
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
                AssertThrows<FormatException>(() => new WireguardConfigParser.ConfigParser(path), "section name is empty");
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
                var parser = new WireguardConfigParser.ConfigParser(path);
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

            var parser = new WireguardConfigParser.ConfigParser(path);
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
                AssertThrows<FormatException>(() => new WireguardConfigParser.ConfigParser(path), "BOM");
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
                var parser = new WireguardConfigParser.ConfigParser(path);
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
                    () => new WireguardConfigParser.ConfigParser(path), null);
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

                var secureChildren = typeof(Global).GetMethod("SecureExistingChildren",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (secureChildren == null)
                    throw new InvalidOperationException("SecureExistingChildren helper was not found.");

                AssertInvocationThrows<IOException>(
                    () => secureChildren.Invoke(null, new object[] { root, null, 0 }), "reparse point");
            }
            finally
            {
                SecureFileSystem.AllowOwnerWriteFailureForTests = false;
                TryDeleteDirectory(link, false);
                TryDeleteDirectory(root, true);
                TryDeleteDirectory(target, true);
            }
        }

        private static void ProgramRejectsUntrustedWireSockCrashHandler()
        {
            var validate = typeof(WireSockUI.Program).GetMethod("TryValidateTrustedWireSockCompanionFiles",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (validate == null)
                throw new InvalidOperationException("TryValidateTrustedWireSockCompanionFiles helper was not found.");

            var directory = Path.Combine(Path.GetTempPath(), "WireSockUI.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                File.WriteAllText(Path.Combine(directory, "crashpad_handler.exe"), string.Empty);
                var args = new object[] { directory, Path.Combine(directory, "wgbooster.dll"), null };

                AssertFalse((bool)validate.Invoke(null, args),
                    "Expected an explicitly user-writable crash handler to be rejected.");
                AssertTrue(args[2] is string diagnostic &&
                           diagnostic.IndexOf("non-administrative", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected an actionable crash-handler trust diagnostic, got '{args[2]}'.");
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

                var diagnostic = Global.ReadNativeRecoveryMarker();
                AssertTrue(diagnostic.IndexOf("directory", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Expected directory marker diagnostic, got '{diagnostic}'.");

                Global.TryDeleteNativeRecoveryMarker();
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

                    Global.WriteNativeRecoveryMarker("test marker", "test diagnostic");

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

                AssertEqual("[Interface]\r\nPrivateKey = test", SecureFileSystem.ReadAllText(path));
                if (!CreateHardLink(hardLinkPath, path, IntPtr.Zero))
                {
                    SkipOrFail("hard-link creation unavailable; validated content-read rejection not exercised.");
                    return;
                }

                AssertThrows<IOException>(() => SecureFileSystem.ReadAllText(hardLinkPath), "hard-linked");
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

        private static void PersistedSettingTransactionsCompensateFailures()
        {
            var value = false;
            var saveCount = 0;
            var result = PersistedSettingTransaction.Apply(
                true,
                false,
                requested => value = requested,
                () =>
                {
                    saveCount++;
                    throw new IOException("initial save failed");
                },
                () => throw new InvalidOperationException("runtime must not run"));

            AssertEqual((int)PersistedSettingUpdateStatus.InitialSaveFailed, (int)result.Status);
            AssertFalse(value, "Expected an initial save failure to restore the previous in-memory value.");
            AssertEqual(1, saveCount);

            value = false;
            saveCount = 0;
            result = PersistedSettingTransaction.Apply(
                true,
                false,
                requested => value = requested,
                () => saveCount++,
                () => false);
            AssertEqual((int)PersistedSettingUpdateStatus.RuntimeApplyFailed, (int)result.Status);
            AssertFalse(value, "Expected a runtime failure to persist and restore the previous value.");
            AssertEqual(2, saveCount);

            value = false;
            saveCount = 0;
            result = PersistedSettingTransaction.Apply(
                true,
                false,
                requested => value = requested,
                () =>
                {
                    saveCount++;
                    if (saveCount == 2)
                        throw new IOException("rollback failed");
                },
                () => false);
            AssertEqual((int)PersistedSettingUpdateStatus.RollbackSaveFailed, (int)result.Status);
            AssertTrue(value,
                "Expected a rollback save failure to retain the last value known to have been saved successfully.");

            value = false;
            saveCount = 0;
            result = PersistedSettingTransaction.ApplyAsync(
                    true,
                    false,
                    requested => value = requested,
                    () => saveCount++,
                    () => System.Threading.Tasks.Task.FromResult(false))
                .GetAwaiter().GetResult();
            AssertEqual((int)PersistedSettingUpdateStatus.RuntimeApplyFailed, (int)result.Status);
            AssertFalse(value, "Expected asynchronous runtime failure to restore the previous value.");
            AssertEqual(2, saveCount);
        }

        private static void SettingsUpdatesStopAfterFirstFailure()
        {
            var calls = new List<string>();
            var result = FrmMain.ApplySettingsUpdatesAsync(
                    () =>
                    {
                        calls.Add("log-level");
                        return Task.FromResult(false);
                    },
                    () =>
                    {
                        calls.Add("kill-switch");
                        return Task.FromResult(true);
                    })
                .GetAwaiter().GetResult();

            AssertFalse(result, "Expected a failed log-level update to fail the settings workflow.");
            AssertEqual("log-level", string.Join(",", calls));

            calls.Clear();
            result = FrmMain.ApplySettingsUpdatesAsync(
                    () =>
                    {
                        calls.Add("log-level");
                        return Task.FromResult(true);
                    },
                    () =>
                    {
                        calls.Add("kill-switch");
                        return Task.FromResult(false);
                    })
                .GetAwaiter().GetResult();

            AssertFalse(result, "Expected a failed Kill Switch update to fail the settings workflow.");
            AssertEqual("log-level,kill-switch", string.Join(",", calls));
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
                var originalKillSwitch = WireSockUI.Properties.Settings.Default.EnableKillSwitch;
                var nativeApi = new FakeWireSockNativeApi();
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = false;
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
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void LifecycleResetsPreservedLockAfterHandleCreationFails()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = WireSockUI.Properties.Settings.Default.EnableKillSwitch;
                var nativeApi = new FakeWireSockNativeApi { CreateHandleResult = IntPtr.Zero };
                var networkLockApi = new FakeNetworkLockApi { Active = true };
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = true;
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
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void LifecycleTracksLateDisconnectCompletionAfterTimeout()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = WireSockUI.Properties.Settings.Default.EnableKillSwitch;
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
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = false;
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
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void LifecycleShutdownAvoidsSynchronizationContextDeadlocks()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = WireSockUI.Properties.Settings.Default.EnableKillSwitch;
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
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = false;
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
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void WireSockManagerSurfacesNativeQueryFailures()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = WireSockUI.Properties.Settings.Default.EnableKillSwitch;
                var nativeApi = new FakeWireSockNativeApi();
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = false;
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
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void WireSockManagerCleansUpFailedStarts()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = WireSockUI.Properties.Settings.Default.EnableKillSwitch;
                var nativeApi = new FakeWireSockNativeApi
                {
                    StartResult = false,
                    StartError = 31
                };

                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = false;
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
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void WireSockManagerRetainsHandlesWhenCleanupFails()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = WireSockUI.Properties.Settings.Default.EnableKillSwitch;
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
                    WireSockUI.Properties.Settings.Default.EnableKillSwitch = false;
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
                    WireSockUI.Properties.Settings.Default.EnableKillSwitch = originalKillSwitch;
                }
            });
        }

        private static void WireSockManagerRetriesReleaseWithoutDroppingTwice()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = WireSockUI.Properties.Settings.Default.EnableKillSwitch;
                var nativeApi = new FakeWireSockNativeApi { ReleaseFailuresRemaining = 1 };
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = false;
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
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = originalKillSwitch;
                    }
                }
            });
        }

        private static void WireSockManagerQuarantinesDroppedHandles()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = WireSockUI.Properties.Settings.Default.EnableKillSwitch;
                var nativeApi = new FakeWireSockNativeApi { ReleaseFailuresRemaining = 1 };
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = false;
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
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = originalKillSwitch;
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
                AssertTrue(update.StatisticsQuery.Diagnostic.Contains("simulated statistics failure"),
                    "Expected the original statistics failure diagnostic to be preserved.");
            }
        }

        private static void WireSockManagerRollsBackFailedLogLevelChanges()
        {
            WithTemporaryConfigFolder(() =>
            {
                var originalKillSwitch = WireSockUI.Properties.Settings.Default.EnableKillSwitch;
                var nativeApi = new FakeWireSockNativeApi();
                using (var manager = new WireSockManager(nativeApi))
                {
                    try
                    {
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = false;
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
                        WireSockUI.Properties.Settings.Default.EnableKillSwitch = originalKillSwitch;
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
            }
            finally
            {
                TryDeleteDirectory(directory, true);
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

        private sealed class FakeWireSockNativeApi : IWireSockNativeApi
        {
            public IntPtr CreateHandleResult { get; set; } = new IntPtr(1234);
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
                return true;
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
                SetLastErrorForTest(0);
                SetNetworkLockCount++;
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

        private sealed class ThrowingToStringValue
        {
            public override string ToString()
            {
                throw new InvalidOperationException("Simulated diagnostic formatting failure.");
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
            using (var taskService = new Microsoft.Win32.TaskScheduler.TaskService())
            using (var definition = taskService.NewTask())
            {
                definition.Principal.RunLevel = Microsoft.Win32.TaskScheduler.TaskRunLevel.Highest;
                definition.Triggers.Add(new Microsoft.Win32.TaskScheduler.LogonTrigger());
                definition.Actions.Add(new Microsoft.Win32.TaskScheduler.ExecAction(executablePath));

                AssertTrue(FrmSettings.IsTaskDefinitionOwnedByExecutable(
                        definition, true, executablePath),
                    "Expected the exact elevated logon task shape to be recognized.");
                AssertFalse(FrmSettings.IsTaskDefinitionOwnedByExecutable(
                        definition, false, executablePath),
                    "Expected a disabled task not to be reported as active autorun.");

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

        private static void AssertInvocationThrows<T>(Action action, string messagePart) where T : Exception
        {
            try
            {
                action();
            }
            catch (TargetInvocationException ex) when (ex.InnerException is T inner)
            {
                if (messagePart == null || inner.Message.IndexOf(messagePart, StringComparison.OrdinalIgnoreCase) >= 0)
                    return;

                throw new Exception($"Expected inner exception message to contain '{messagePart}', got '{inner.Message}'.");
            }

            throw new Exception($"Expected invocation to throw {typeof(T).Name}.");
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
