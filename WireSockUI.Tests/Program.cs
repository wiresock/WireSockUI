using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using WireSockUI;
using WireSockUI.Config;
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

        private static int Main()
        {
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
                { "Parser rejects duplicate sections", ParserRejectsDuplicateSections },
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
                { "Time formatting uses plural hours", TimeFormattingUsesPluralHours },
                { "Time formatting uses singular hour for partial second hour", TimeFormattingUsesSingularHourForPartialSecondHour },
                { "Time formatting handles future values", TimeFormattingHandlesFutureValues },
                { "Global config folder containment handles drive roots", GlobalConfigFolderContainmentHandlesDriveRoots },
                { "Global rejects unsecured config folder overrides by default", GlobalRejectsUnsecuredConfigFolderOverridesByDefault },
                { "Global fails closed on configuration directory reparse points", GlobalFailsClosedOnConfigurationDirectoryReparsePoints },
                { "Profile rejects user-writable secured files", ProfileRejectsUserWritableSecuredFiles },
                { "Release version parser handles SemVer tags", ReleaseVersionParserHandlesSemVerTags },
                { "Program path normalization preserves drive roots", ProgramPathNormalizationPreservesDriveRoots },
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
                { "Legacy migration rejects oversized files", LegacyMigrationRejectsOversizedFiles },
                { "Legacy migration rejects reparse point sources", LegacyMigrationRejectsReparsePointSources },
                { "Legacy migration rejects script hooks", LegacyMigrationRejectsScriptHooks },
                { "Legacy migration script-hook check is narrow", LegacyMigrationScriptHookCheckIsNarrow },
                { "Native recovery marker cleanup removes directory markers", NativeRecoveryMarkerCleanupRemovesDirectoryMarkers },
                { "Native query distinguishes error sentinels", NativeQueryDistinguishesErrorSentinels },
                { "Settings upgrade runs exactly once", SettingsUpgradeRunsExactlyOnce },
                { "Editor validates Amnezia options", EditorValidatesAmneziaOptions },
                { "AppUserModelID is path seeded", AppUserModelIdIsPathSeeded },
                { "Notification shortcut name is path seeded", NotificationShortcutNameIsPathSeeded },
                { "Autorun task name is path seeded", AutoRunTaskNameIsPathSeeded },
                { "WireSock disconnect forwards network-lock preservation", WireSockDisconnectForwardsNetworkLockPreservation },
                { "WireSock manager surfaces native query failures", WireSockManagerSurfacesNativeQueryFailures },
                { "WireSock manager cleans up failed starts", WireSockManagerCleansUpFailedStarts },
                { "WireSock manager retains handles when cleanup fails", WireSockManagerRetainsHandlesWhenCleanupFails },
                { "Network lock enum matches wgbooster ABI", NetworkLockEnumMatchesWgboosterAbi },
                { "WireSock exports use restricted DLL search", WireSockExportsUseRestrictedDllSearch },
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

        private static void ParserRejectsDuplicateSections()
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

            AssertThrows<FormatException>(() => new WireguardConfigParser.ConfigParser(path), "line 10");
            AssertThrows<FormatException>(() => new WireguardConfigParser.ConfigParser(path), "Duplicate [Peer]");
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

        private static void ProgramRejectsUserWritableWireSockLibraryDirectories()
        {
            var validate = typeof(WireSockUI.Program).GetMethod("TryValidateWireSockLibraryDirectory",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (validate == null)
                throw new InvalidOperationException("TryValidateWireSockLibraryDirectory helper was not found.");

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

                var args = new object[] { directory, null };
                var accepted = (bool)validate.Invoke(null, args);

                AssertFalse(accepted, "Expected user-writable WireSock library directories to be rejected.");
                AssertTrue(args[1] == null, "Expected rejected WireSock library directories not to return a path.");
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
                    () => secureChildren.Invoke(null, new object[] { root, null }), "reparse point");
            }
            finally
            {
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
            var copyLegacyProfileToTemporaryFile = typeof(WireSockUI.Program).GetMethod(
                "CopyLegacyProfileToTemporaryFile", BindingFlags.NonPublic | BindingFlags.Static);
            if (copyLegacyProfileToTemporaryFile == null)
                throw new InvalidOperationException("CopyLegacyProfileToTemporaryFile helper was not found.");

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

                AssertInvocationThrows<InvalidOperationException>(
                    () => copyLegacyProfileToTemporaryFile.Invoke(null, new object[] { source, destination }),
                    "too large");
                AssertFalse(File.Exists(destination),
                    "Expected oversized legacy profile migrations not to create a temp copy.");
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

        private static void LegacyMigrationRejectsReparsePointSources()
        {
            var copyLegacyProfileToTemporaryFile = typeof(WireSockUI.Program).GetMethod(
                "CopyLegacyProfileToTemporaryFile", BindingFlags.NonPublic | BindingFlags.Static);
            if (copyLegacyProfileToTemporaryFile == null)
                throw new InvalidOperationException("CopyLegacyProfileToTemporaryFile helper was not found.");

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
                    SkipOrFail("profile reparse point creation unavailable; legacy migration reparse check not exercised.");
                    return;
                }

                AssertInvocationThrows<IOException>(
                    () => copyLegacyProfileToTemporaryFile.Invoke(null, new object[] { link, destination }),
                    "reparse point");
                AssertFalse(File.Exists(destination),
                    "Expected reparse point legacy profile migrations not to create a temp copy.");
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

        private static void LegacyMigrationRejectsScriptHooks()
        {
            var path = WriteConfig("[Interface]\n" +
                                   $"PrivateKey = {PrivateKey}\n" +
                                   "Address = 10.0.0.2/32\n" +
                                   "postup = powershell.exe -NoProfile -Command Write-Host test\n" +
                                   "\n" +
                                   "[Peer]\n" +
                                   $"PublicKey = {PublicKey}\n" +
                                   "Endpoint = example.com:51820\n" +
                                   "AllowedIPs = 0.0.0.0/0\n");

            try
            {
                AssertInvocationThrows<InvalidOperationException>(
                    () => EnsureMigratedProfileCanBePromoted(path),
                    "script hooks");
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static void LegacyMigrationScriptHookCheckIsNarrow()
        {
            var path = WriteConfig("[Interface]\nAddress = 10.0.0.2/32\n");

            try
            {
                EnsureMigratedProfileCanBePromoted(path);
            }
            finally
            {
                TryDeleteFile(path);
            }
        }

        private static void EnsureMigratedProfileCanBePromoted(string path)
        {
            var ensureMigratedProfileCanBePromoted = typeof(WireSockUI.Program).GetMethod(
                "EnsureMigratedProfileCanBePromoted", BindingFlags.NonPublic | BindingFlags.Static);
            if (ensureMigratedProfileCanBePromoted == null)
                throw new InvalidOperationException("EnsureMigratedProfileCanBePromoted helper was not found.");

            ensureMigratedProfileCanBePromoted.Invoke(null, new object[] { path });
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
                        AssertTrue(nativeApi.LastPreserveNetworkLock == true,
                            "Expected preserved reconnect cleanup to pass preserveNetworkLock=true to wgbooster.");

                        AssertTrue(manager.Connect("office"), "Expected the fake tunnel to reconnect.");
                        AssertTrue(manager.Disconnect(), "Expected fake default disconnect to succeed.");
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

            public IntPtr GetHandle(WireSockManager.Mode mode, WireguardBoosterExports.LogPrinter logPrinter,
                WireguardBoosterExports.WgbLogLevel logLevel, bool enableTrafficCapture)
            {
                SetLastErrorForTest(0);
                GetHandleCount++;
                return new IntPtr(1234);
            }

            public void SetLogLevel(WireSockManager.Mode mode, IntPtr handle,
                WireguardBoosterExports.WgbLogLevel logLevel)
            {
                SetLastErrorForTest(0);
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
                return DropResult;
            }

            public bool GetTunnelActive(WireSockManager.Mode mode, IntPtr handle)
            {
                SetLastErrorForTest((uint)TunnelActiveError);
                return TunnelActive;
            }

            public WireguardBoosterExports.WgbStats GetTunnelState(WireSockManager.Mode mode, IntPtr handle)
            {
                SetLastErrorForTest((uint)TunnelStateError);
                return TunnelState;
            }

            public bool SetNetworkLockMode(WireSockManager.Mode mode, IntPtr handle,
                WireguardBoosterExports.WgbNetworkLockMode networkLockMode)
            {
                SetLastErrorForTest(0);
                NetworkLockMode = networkLockMode;
                return true;
            }

            public WireguardBoosterExports.WgbNetworkLockMode GetNetworkLockMode(WireSockManager.Mode mode,
                IntPtr handle)
            {
                SetLastErrorForTest((uint)NetworkLockModeError);
                return NetworkLockMode;
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
