using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WireSockUI.Extensions;
using WireSockUI.Native;
using static WireSockUI.Native.WireguardConfigParser;

namespace WireSockUI.Config
{
    /// <summary>
    ///     WireGuard Profile, including WireSock extensions
    /// </summary>
    internal class Profile
    {
        private static readonly string[] InterfaceKeys =
        {
            "PrivateKey", "Address", "DNS", "MTU", "ListenPort", "Table", "ScriptExecTimeout", "PreUp",
            "PostUp", "PreDown", "PostDown", "BypassLanTraffic", "VirtualAdapterMode", "EnableDefaultGateway",
            "Jc", "Jmin", "Jmax", "Jd", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4", "Id", "Ip",
            "Ib"
        };

        private static readonly string[] PeerKeys =
        {
            "PublicKey", "PresharedKey", "AllowedIPs", "Endpoint", "PersistentKeepalive", "AllowedApps",
            "DisallowedApps", "DisallowedIPs", "Socks5Proxy", "Socks5ProxyUsername", "Socks5ProxyPassword",
            "Socks5ProxyAllTraffic", "Socks5Username"
        };

        private string _address;

        // WireSock Extensions
        private string _allowedIPs;
        private string _dns;
        private string _endpoint;
        private string _listenport;
        private string _mtu;
        private string _persistentKeepAlive;
        private string _scriptExecTimeout;
        private string _disallowedIPs;
        private string _virtualAdapterMode;

        private string _presharedKey;

        // Interface values
        private string _privateKey;

        // Peer values
        private string _publicKey;
        private string _socks5Proxy;
        private string _socks5ProxyAllTraffic;

        /// <summary>
        ///     Create an empty profile from scratch
        /// </summary>
        public Profile()
        {
        }

        /// <summary>
        ///     Load a profile from specified filepath
        /// </summary>
        /// <param name="profilePath">Full filepath to a profile</param>
        public Profile(string profilePath)
        {
            if (!ProfilePathExists(profilePath))
                throw new FileNotFoundException($"Profile {GetProfileDisplayName(profilePath)} does not exist.");

            EnsureRegularProfileFile(profilePath);

            var parser = new ConfigParser(profilePath);
            var sections = parser.GetSectionNames();

            var configESections = sections as string[] ?? sections.ToArray();
            if (!configESections.Contains("Interface", StringComparer.Ordinal))
                throw new ArgumentException(
                    $"Profile {GetProfileDisplayName(profilePath)} does not contain an \"Interface\" section.");

            var section = parser.GetSection("Interface");
            ValidateKnownKeyCasing(section, InterfaceKeys);
            ValidateUnsupportedInterfaceDirectives(section);

            PrivateKey = GetRequiredValue(profilePath, "Interface", section, "PrivateKey");
            Address = GetRequiredValue(profilePath, "Interface", section, "Address");
            Dns = section.Get("DNS");
            Mtu = section.Get("MTU");
            ListenPort = section.Get("ListenPort");
            ScriptExecTimeout = section.Get("ScriptExecTimeout");
            PreUpScript = section.Get("PreUp");
            PostUpScript = section.Get("PostUp");
            PreDownScript = section.Get("PreDown");
            PostDownScript = section.Get("PostDown");
            VirtualAdapterMode = section.Get("VirtualAdapterMode");
            ValidateInterfaceExtensions(section);

            if (!configESections.Contains("Peer", StringComparer.Ordinal))
                throw new ArgumentException(
                    $"Profile {GetProfileDisplayName(profilePath)} does not contain a \"Peer\" section.");

            section = parser.GetSection("Peer");
            ValidateKnownKeyCasing(section, PeerKeys);

            if (section.ContainsKey("Socks5Username"))
                throw new FormatException(
                    "\"Socks5Username\" in \"Peer\" is not supported by the current SDK. Use \"Socks5ProxyUsername\".");

            PeerKey = GetRequiredValue(profilePath, "Peer", section, "PublicKey");
            PresharedKey = section.Get("PresharedKey");
            AllowedIPs = GetRequiredValue(profilePath, "Peer", section, "AllowedIPs");
            Endpoint = GetRequiredValue(profilePath, "Peer", section, "Endpoint");
            PersistentKeepAlive = section.Get("PersistentKeepalive");

            AllowedApps = section.Get("AllowedApps");
            DisallowedApps = section.Get("DisallowedApps");
            DisallowedIPs = section.Get("DisallowedIPs");
            Socks5Proxy = section.Get("Socks5Proxy");
            Socks5ProxyUsername = section.Get("Socks5ProxyUsername");
            Socks5ProxyPassword = section.Get("Socks5ProxyPassword");
            Socks5ProxyAllTraffic = section.Get("Socks5ProxyAllTraffic");
        }

        /// <summary>
        ///     Local interface private key
        /// </summary>
        public string PrivateKey
        {
            get => _privateKey;
            set
            {
                ValidateKey("Interface", "PrivateKey", value);
                _privateKey = value;
            }
        }

        /// <summary>
        ///     Local interface public key, derived from private key
        /// </summary>
        public string PublicKey
        {
            get
            {
                if (!string.IsNullOrEmpty(_privateKey))
                    // Determine public key from private key data
                    return
                        Convert.ToBase64String(
                            Curve25519.GetPublicKey(
                                Convert.FromBase64String(PrivateKey)));

                return null;
            }
        }

        /// <summary>
        ///     List of interface IP addresses
        /// </summary>
        public string Address
        {
            get => _address;
            set
            {
                ValidateAddresses("Interface", "Address", value, IpHelper.IsValidSubnetOrSingleIpAddress);
                _address = value;
            }
        }

        /// <summary>
        ///     List of interface DNS servers
        /// </summary>
        public string Dns
        {
            get => _dns;
            set
            {
                ValidateAddresses("Interface", "DNS", value, IpHelper.IsValidIpAddress);
                _dns = value;
            }
        }

        /// <summary>
        ///     Interface Maximum Transmissible Unit size
        /// </summary>
        public string Mtu
        {
            get => _mtu;
            set
            {
                ValidateUInt("Interface", "MTU", value, 576, ushort.MaxValue);
                _mtu = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        /// <summary>
        ///     Interface ListenPort
        /// </summary>
        public string ListenPort
        {
            get => _listenport;
            set
            {
                ValidateUInt("Interface", "ListenPort", value, 0, ushort.MaxValue);
                _listenport = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        /// <summary>
        ///     Timeout for Pre/Post up/down scripts.
        /// </summary>
        public string ScriptExecTimeout
        {
            get => _scriptExecTimeout;
            set
            {
                ValidateUInt("Interface", "ScriptExecTimeout", value, 0, uint.MaxValue);
                _scriptExecTimeout = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        public string PreUpScript { get; set; }

        public string PostUpScript { get; set; }

        public string PreDownScript { get; set; }

        public string PostDownScript { get; set; }

        public IReadOnlyList<KeyValuePair<string, string>> GetConfiguredScriptHooks()
        {
            var hooks = new List<KeyValuePair<string, string>>();

            AddScriptHook(hooks, "PreUp", PreUpScript);
            AddScriptHook(hooks, "PostUp", PostUpScript);
            AddScriptHook(hooks, "PreDown", PreDownScript);
            AddScriptHook(hooks, "PostDown", PostDownScript);

            return hooks;
        }

        /// <summary>
        ///     Profile-level virtual adapter preference when supported by the SDK.
        /// </summary>
        public string VirtualAdapterMode
        {
            get => _virtualAdapterMode;
            set
            {
                ValidateBool("Interface", "VirtualAdapterMode", value);
                _virtualAdapterMode = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        /// <summary>
        ///     Peer public key
        /// </summary>
        public string PeerKey
        {
            get => _publicKey;
            set
            {
                ValidateKey("Peer", "PublicKey", value);
                _publicKey = value;
            }
        }

        /// <summary>
        ///     Peer preshared key (optional)
        /// </summary>
        public string PresharedKey
        {
            get => _presharedKey;
            set
            {
                ValidateKey("Peer", "PresharedKey", value);
                _presharedKey = value;
            }
        }

        /// <summary>
        ///     Peer allowed IP list
        /// </summary>
        public string AllowedIPs
        {
            get => _allowedIPs;
            set
            {
                ValidateAddresses("Peer", "AllowedIPs", value, IpHelper.IsValidSubnetOrSingleIpAddress);
                _allowedIPs = value;
            }
        }

        /// <summary>
        ///     Peer endpoint address (DNS or IP)
        /// </summary>
        public string Endpoint
        {
            get => _endpoint;
            set
            {
                if (!IpHelper.IsValidAddress(value))
                    throw new FormatException("\"Endpoint\" in \"Peer\", is not a valid IPv4, IPv6 or domain address.");

                _endpoint = value;
            }
        }


        /// <summary>
        ///     Persistent keep alive interval
        /// </summary>
        public string PersistentKeepAlive
        {
            get => _persistentKeepAlive;
            set
            {
                ValidateUInt("Peer", "PersistentKeepalive", value, 0, uint.MaxValue);
                _persistentKeepAlive = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        /// <summary>
        ///     Peer allowed applications list
        /// </summary>
        /// <remarks>WireSock specific extension</remarks>
        public string AllowedApps { get; set; }

        /// <summary>
        ///     Peer disallowed applications list
        /// </summary>
        /// <remarks>WireSock specific extension</remarks>
        public string DisallowedApps { get; set; }

        /// <summary>
        ///     Peer disallowed IP addresses
        /// </summary>
        /// <remarks>WireSock specific extension</remarks>
        public string DisallowedIPs
        {
            get => _disallowedIPs;
            set
            {
                ValidateAddresses("Peer", "DisallowedIPs", value, IpHelper.IsValidSubnetOrSingleIpAddress);
                _disallowedIPs = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        /// <summary>
        ///     Peer SOCKS5 proxy
        /// </summary>
        /// <remarks>WireSock specific extension</remarks>
        public string Socks5Proxy
        {
            get => _socks5Proxy;
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    if (!IpHelper.IsValidAddress(value))
                        throw new FormatException(
                            "\"Socks5Proxy\" in \"Peer\", is not a valid IPv4, IPv6 or domain address.");

                    _socks5Proxy = value;
                }
                else
                {
                    _socks5Proxy = null;
                }
            }
        }

        /// <summary>
        ///     Peer SOCKS5 proxy username
        /// </summary>
        /// <remarks>WireSock specific extension</remarks>
        public string Socks5ProxyUsername { get; set; }

        /// <summary>
        ///     Peer SOCKS5 proxy password
        /// </summary>
        /// <remarks>WireSock specific extension</remarks>
        public string Socks5ProxyPassword { get; set; }

        /// <summary>
        ///     Route all WireGuard traffic through the configured SOCKS5 proxy.
        /// </summary>
        /// <remarks>WireSock specific extension</remarks>
        public string Socks5ProxyAllTraffic
        {
            get => _socks5ProxyAllTraffic;
            set
            {
                ValidateBool("Peer", "Socks5ProxyAllTraffic", value);
                _socks5ProxyAllTraffic = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        internal static void ValidateKey(string section, string key, string keyValue)
        {
            byte[] keyBinary;

            if (string.IsNullOrWhiteSpace(keyValue)) return;

            try
            {
                keyBinary = Convert.FromBase64String(keyValue);
            }
            catch (FormatException)
            {
                throw new FormatException($"\"{key}\" in \"{section}\", invalid base64 encoded value.");
            }

            // 256-bit keys only
            if (keyBinary.Length != 32)
                throw new FormatException(
                    $"\"{key}\" in \"{section}\", invalid key length, only 256-bit keys are supported.");
        }

        internal static void ValidateAddresses(string section, string key, string keyValue,
            Func<string, bool> validator)
        {
            if (string.IsNullOrWhiteSpace(keyValue)) return;

            foreach (var value in keyValue.Split(','))
            {
                var trimmedValue = value.Trim();
                if (string.IsNullOrWhiteSpace(trimmedValue) || !validator(trimmedValue))
                    throw new FormatException($"\"{key}\" in \"{section}\", invalid address \"{value}\".");
            }
        }

        internal static void ValidateBool(string section, string key, string keyValue)
        {
            if (string.IsNullOrWhiteSpace(keyValue)) return;

            var trimmedValue = keyValue.Trim();
            if (!string.Equals(trimmedValue, "true", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(trimmedValue, "false", StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"\"{key}\" in \"{section}\", must be true or false.");
        }

        private static void ValidateInterfaceExtensions(Dictionary<string, string> section)
        {
            foreach (var rule in ConfigValueValidator.InterfaceExtensionRules)
            {
                var value = section.Get(rule.Key);
                if (section.ContainsKey(rule.Key) && string.IsNullOrWhiteSpace(value) &&
                    !IsBlankAmneziaHeaderAllowed(rule.Key))
                    throw new FormatException($"\"{rule.Key}\" in \"Interface\" cannot be empty.");

                if (!rule.IsValid(value))
                    throw new FormatException(
                        $"\"{rule.Key}\" in \"Interface\", invalid value. Expected {rule.ExpectedValueDescription}.");
            }

            ValidateRequiredPair(section, "Jmin", "Jmax");
            ValidateUIntOrdering(section, "Jmin", "Jmax");
            ValidatePreHandshakeActivation(section);
            ValidateAmneziaGroup(section);
            ValidateAmneziaHeaderRanges(section);
            ValidateProtocolImitation(section);

            if (section.ContainsKey("EnableDefaultGateway") &&
                !string.Equals(section.Get("EnableDefaultGateway"), "true", StringComparison.Ordinal) &&
                !string.Equals(section.Get("EnableDefaultGateway"), "false", StringComparison.Ordinal))
                throw new FormatException(
                    "\"EnableDefaultGateway\" in \"Interface\" must be exactly \"true\" or \"false\".");
        }

        private static bool IsBlankAmneziaHeaderAllowed(string key)
        {
            return string.Equals(key, "H1", StringComparison.Ordinal) ||
                   string.Equals(key, "H2", StringComparison.Ordinal) ||
                   string.Equals(key, "H3", StringComparison.Ordinal) ||
                   string.Equals(key, "H4", StringComparison.Ordinal);
        }

        internal static void ValidateUInt(string section, string key, string keyValue, uint minValue, uint maxValue)
        {
            if (string.IsNullOrWhiteSpace(keyValue)) return;

            if (!ConfigValueValidator.IsUIntDecimalInRange(keyValue, minValue, maxValue))
                throw new FormatException(
                    $"\"{key}\" in \"{section}\", invalid value. Expected {minValue}...{maxValue}.");
        }

        private static void ValidateKnownKeyCasing(Dictionary<string, string> section, IEnumerable<string> knownKeys)
        {
            var canonicalKeys = knownKeys.ToDictionary(key => key, key => key, StringComparer.OrdinalIgnoreCase);
            foreach (var key in section.Keys)
            {
                if (canonicalKeys.TryGetValue(key, out var canonicalKey) &&
                    !string.Equals(key, canonicalKey, StringComparison.Ordinal))
                    throw new FormatException(
                        $"Configuration key \"{key}\" has invalid casing. The current SDK expects \"{canonicalKey}\".");
            }
        }

        private static void ValidateUnsupportedInterfaceDirectives(Dictionary<string, string> section)
        {
            if (section.ContainsKey("BypassLanTraffic"))
                throw new FormatException(
                    "\"BypassLanTraffic\" is not supported by direct wgbooster.dll integration. Specify LAN prefixes with \"DisallowedIPs\" in \"Peer\".");

            if (section.ContainsKey("Table"))
                throw new FormatException(
                    "\"Table\" is not supported by the current wgbooster.dll configuration parser.");

            for (var index = 1; index <= 5; index++)
            {
                var key = $"I{index}";
                if (section.Keys.Any(existingKey =>
                        string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase)))
                    throw new FormatException(
                        $"\"{key}\" is not supported by the current wgbooster.dll configuration parser.");
            }
        }

        private static void ValidateRequiredPair(Dictionary<string, string> section, string firstKey, string secondKey)
        {
            if (section.ContainsKey(firstKey) == section.ContainsKey(secondKey))
                return;

            throw new FormatException(
                $"\"{firstKey}\" and \"{secondKey}\" in \"Interface\" must be specified together.");
        }

        private static void ValidateAmneziaGroup(Dictionary<string, string> section)
        {
            var allKeys = new[] { "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4" };
            if (!allKeys.Any(section.ContainsKey))
                return;

            var requiredKeys = new[] { "S1", "S2", "H1", "H2", "H3", "H4" };
            var missingKeys = requiredKeys.Where(key => !section.ContainsKey(key)).ToArray();
            if (missingKeys.Length > 0)
                throw new FormatException(
                    $"Amnezia configuration in \"Interface\" is incomplete. Missing: {string.Join(", ", missingKeys)}.");
        }

        private static void ValidatePreHandshakeActivation(Dictionary<string, string> section)
        {
            if (!(section.ContainsKey("Jmin") || section.ContainsKey("Jmax") || section.ContainsKey("Jd")))
                return;

            if (!section.ContainsKey("Jc") && !section.ContainsKey("Id"))
                throw new FormatException(
                    "\"Jmin\", \"Jmax\", and \"Jd\" in \"Interface\" require \"Jc\" or \"Id\" to enable pre-handshake configuration.");
        }

        private static void ValidateProtocolImitation(Dictionary<string, string> section)
        {
            if ((section.ContainsKey("Ip") || section.ContainsKey("Ib")) && !section.ContainsKey("Id"))
                throw new FormatException("\"Ip\" and \"Ib\" in \"Interface\" require \"Id\".");

            var protocol = section.Get("Ip")?.Trim();
            if ((string.Equals(protocol, "sip", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(protocol, "sip_request", StringComparison.OrdinalIgnoreCase)) &&
                !ConfigValueValidator.IsSipImitationHost(section.Get("Id")))
                throw new FormatException(
                    "\"Id\" in \"Interface\" is not a valid SIP imitation host. Use ASCII labels of at most 63 characters.");
        }

        private static void ValidateAmneziaHeaderRanges(Dictionary<string, string> section)
        {
            var keys = new[] { "H1", "H2", "H3", "H4" };
            if (!keys.All(section.ContainsKey))
                return;

            var ranges = keys.Select((key, index) => ParseResolvedHeaderRange(section.Get(key), (uint)index + 1))
                .ToArray();
            for (var first = 0; first < ranges.Length; first++)
            {
                for (var second = first + 1; second < ranges.Length; second++)
                {
                    if (ranges[first].Start <= ranges[second].End && ranges[second].Start <= ranges[first].End)
                        throw new FormatException(
                            $"\"{keys[first]}\" and \"{keys[second]}\" in \"Interface\" resolve to overlapping header ranges.");
                }
            }
        }

        private static HeaderRange ParseResolvedHeaderRange(string value, uint defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new HeaderRange(defaultValue, defaultValue);

            var parts = value.Split('-');
            ConfigValueValidator.TryParseUIntDecimal(parts[0], out var start);
            var end = start;
            if (parts.Length == 2)
                ConfigValueValidator.TryParseUIntDecimal(parts[1], out end);

            return start == 0 && end == 0
                ? new HeaderRange(defaultValue, defaultValue)
                : new HeaderRange(start, end);
        }

        private struct HeaderRange
        {
            public HeaderRange(uint start, uint end)
            {
                Start = start;
                End = end;
            }

            public uint Start { get; }
            public uint End { get; }
        }

        private static void ValidateUIntOrdering(Dictionary<string, string> section, string minKey, string maxKey)
        {
            var minValue = section.Get(minKey);
            var maxValue = section.Get(maxKey);
            if (string.IsNullOrWhiteSpace(minValue) || string.IsNullOrWhiteSpace(maxValue))
                return;

            if (!ConfigValueValidator.TryParseUIntDecimal(minValue, out var min) ||
                !ConfigValueValidator.TryParseUIntDecimal(maxValue, out var max))
                return;

            if (min >= max)
                throw new FormatException(
                    $"\"{minKey}\" in \"Interface\" must be less than \"{maxKey}\".");
        }

        public static IEnumerable<string> GetProfiles()
        {
            string[] files;

            try
            {
                Global.EnsureConfigsFolderExists();
                files = Directory.GetFiles(Global.ConfigsFolder);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unable to enumerate WireSock UI profiles: {ex.Message}");
                yield break;
            }

            foreach (var file in files)
            {
                if (!file.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) continue;

                if (!IsRegularProfileFile(file, out var diagnostic))
                {
                    Trace.TraceWarning(diagnostic);
                    continue;
                }

                var profileName = Path.GetFileNameWithoutExtension(file);
                if (!IsValidProfileName(profileName))
                {
                    Trace.TraceWarning($"Skipping invalid WireSock UI profile file name '{Path.GetFileName(file)}'.");
                    continue;
                }

                yield return profileName;
            }
        }

        /// <summary>
        ///     Retrieve the full path to a given <paramref name="profileName" />
        /// </summary>
        /// <param name="profileName">Profile name</param>
        /// <returns>Full path to the profile</returns>
        /// <remarks>The profile might not exist, this merely returns the path it should be at.</remarks>
        public static string GetProfilePath(string profileName)
        {
            if (!IsValidProfileName(profileName))
                throw new ArgumentException("Profile name is empty or contains characters that are not valid in a Windows file name.",
                    nameof(profileName));

            var configRoot = Path.GetFullPath(Global.ConfigsFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var profilePath = Path.GetFullPath(Path.Combine(configRoot, profileName + ".conf"));

            if (!profilePath.StartsWith(configRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Profile path must stay inside the WireSock UI configuration folder.",
                    nameof(profileName));

            return profilePath;
        }

        public static bool IsValidProfileName(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return false;

            var trimmedName = profileName.Trim();
            if (!string.Equals(profileName, trimmedName, StringComparison.Ordinal) ||
                trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;

            var reservedName = trimmedName.TrimEnd(' ', '.');
            if (reservedName.Length == 0 || !string.Equals(reservedName, trimmedName, StringComparison.Ordinal))
                return false;

            var reservedDeviceNames = new[]
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };

            var baseName = reservedName.Split('.')[0];

            return !reservedDeviceNames.Contains(baseName, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     Load an existing named profile
        /// </summary>
        /// <param name="profileName">Profile name (i.e. filename without extension)</param>
        public static Profile LoadProfile(string profileName)
        {
            var filename = GetProfilePath(profileName);
            return new Profile(filename);
        }

        internal static void EnsureRegularProfileFile(string profilePath)
        {
            if (!IsRegularProfileFile(profilePath, out var diagnostic))
                throw new IOException(diagnostic);
        }

        internal static bool ProfilePathExists(string profilePath)
        {
            if (string.IsNullOrWhiteSpace(profilePath))
                return false;

            try
            {
                File.GetAttributes(profilePath);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"Unable to inspect profile path '{GetProfileDisplayName(profilePath)}': {ex.Message}");
                return true;
            }
        }

        internal static bool IsRegularProfileFile(string profilePath, out string diagnostic)
        {
            diagnostic = null;

            try
            {
                if (string.IsNullOrWhiteSpace(profilePath))
                {
                    diagnostic = "Profile path is empty.";
                    return false;
                }

                var attributes = File.GetAttributes(profilePath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostic =
                        $"Profile file '{GetProfileDisplayName(profilePath)}' is a reparse point and will not be loaded by elevated WireSock UI.";
                    return false;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    diagnostic =
                        $"Profile path '{GetProfileDisplayName(profilePath)}' is a directory and will not be loaded by elevated WireSock UI.";
                    return false;
                }

                if (!Global.AllowUnsecuredConfigFolderOverrideForTests && IsPathInConfigFolder(profilePath) &&
                    !Program.TryValidateTrustedFilePath(profilePath, "Profile file", out diagnostic))
                    return false;

                return true;
            }
            catch (FileNotFoundException)
            {
                diagnostic = $"Profile file '{GetProfileDisplayName(profilePath)}' does not exist.";
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                diagnostic = $"Profile directory for '{GetProfileDisplayName(profilePath)}' does not exist.";
                return false;
            }
            catch (Exception ex)
            {
                diagnostic =
                    $"Unable to inspect profile file '{GetProfileDisplayName(profilePath)}': {ex.Message}";
                return false;
            }
        }

        private static bool IsPathInConfigFolder(string profilePath)
        {
            var configRoot = Path.GetFullPath(Global.ConfigsFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullProfilePath = Path.GetFullPath(profilePath);
            return fullProfilePath.StartsWith(configRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRequiredValue(string profilePath, string sectionName, Dictionary<string, string> section,
            string key)
        {
            if (!section.ContainsKey(key))
                throw new ArgumentException(
                    $"Profile {GetProfileDisplayName(profilePath)}, section \"{sectionName}\" does not have a \"{key}\" defined.");

            var value = section.Get(key);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException(
                    $"Profile {GetProfileDisplayName(profilePath)}, section \"{sectionName}\" has an empty \"{key}\" value.");

            return value;
        }

        private static string GetProfileDisplayName(string profilePath)
        {
            if (string.IsNullOrWhiteSpace(profilePath))
                return string.Empty;

            var trimmedPath = profilePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            try
            {
                var fileName = Path.GetFileName(trimmedPath);
                if (!string.IsNullOrWhiteSpace(fileName))
                    return EscapeProfileDisplayName(fileName);
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (PathTooLongException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }

            return EscapeProfileDisplayName(string.IsNullOrWhiteSpace(trimmedPath) ? profilePath : trimmedPath);
        }

        private static string EscapeProfileDisplayName(string value)
        {
            return (value ?? string.Empty).Replace("\0", "\\0");
        }

        private static void AddScriptHook(List<KeyValuePair<string, string>> hooks, string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                hooks.Add(new KeyValuePair<string, string>(name, value));
        }
    }
}
