using System;
using System.Collections.Generic;
using System.Globalization;
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
        private string _bypassLanTraffic;
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
            if (!File.Exists(profilePath))
                throw new FileNotFoundException($"Profile {Path.GetFileName(profilePath)} does not exist.");

            var parser = new ConfigParser(profilePath);
            var sections = parser.GetSectionNames();

            var configESections = sections as string[] ?? sections.ToArray();
            if (!configESections.Contains("Interface", StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Profile {Path.GetFileName(profilePath)} does not contain an \"Interface\" section.");

            var section = parser.GetSection("Interface");

            PrivateKey = GetRequiredValue(profilePath, "Interface", section, "PrivateKey");
            Address = GetRequiredValue(profilePath, "Interface", section, "Address");
            Dns = section.Get("DNS");
            Mtu = section.Get("MTU");
            ListenPort = section.Get("ListenPort");
            Table = section.Get("Table");
            ScriptExecTimeout = section.Get("ScriptExecTimeout");
            PreUpScript = section.Get("PreUp");
            PostUpScript = section.Get("PostUp");
            PreDownScript = section.Get("PreDown");
            PostDownScript = section.Get("PostDown");
            BypassLanTraffic = section.Get("BypassLanTraffic");
            VirtualAdapterMode = section.Get("VirtualAdapterMode");

            if (!configESections.Contains("Peer", StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Profile {Path.GetFileName(profilePath)} does not contain a \"Peer\" section.");

            section = parser.GetSection("Peer");

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
            if (string.IsNullOrEmpty(Socks5ProxyUsername))
                Socks5ProxyUsername = section.Get("Socks5Username");
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
                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (!int.TryParse(value, out var mtu))
                        throw new FormatException("\"MTU\" in \"Interface\", is not a numerical value.");

                    if (mtu < 576 || mtu > 65535)
                        throw new FormatException("\"MTU\" in \"Interface\", invalid value. Expected 576...65535.");

                    _mtu = value;
                }
                else
                {
                    _mtu = null;
                }
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
                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (!int.TryParse(value, out var listenPort))
                        throw new FormatException("\"ListenPort\" in \"Interface\", is not a numerical value.");

                    if (listenPort < 1 || listenPort > 65535)
                        throw new FormatException(
                            "\"ListenPort\" in \"Interface\", invalid value. Expected 1...65535.");

                    _listenport = value;
                }
                else
                {
                    _listenport = null;
                }
            }
        }

        /// <summary>
        ///     Interface routing table setting.
        /// </summary>
        public string Table { get; set; }

        /// <summary>
        ///     Timeout for Pre/Post up/down scripts.
        /// </summary>
        public string ScriptExecTimeout
        {
            get => _scriptExecTimeout;
            set
            {
                ValidateInt("Interface", "ScriptExecTimeout", value, 0, 65535);
                _scriptExecTimeout = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        public string PreUpScript { get; set; }

        public string PostUpScript { get; set; }

        public string PreDownScript { get; set; }

        public string PostDownScript { get; set; }

        /// <summary>
        ///     Exclude local LAN traffic from the tunnel when supported by the SDK.
        /// </summary>
        public string BypassLanTraffic
        {
            get => _bypassLanTraffic;
            set
            {
                ValidateBool("Interface", "BypassLanTraffic", value);
                _bypassLanTraffic = string.IsNullOrWhiteSpace(value) ? null : value;
            }
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
                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (!int.TryParse(value, out var mtu))
                        throw new FormatException("\"PersistentKeepalive\" in \"Peer\", is not a numerical value.");

                    if (mtu < 0 || mtu > 65535)
                        throw new FormatException(
                            "\"PersistentKeepalive\" in \"Peer\", invalid value. Expected 0...65535.");

                    _persistentKeepAlive = value;
                }
                else
                {
                    _persistentKeepAlive = null;
                }
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

        internal static void ValidateInt(string section, string key, string keyValue, int minValue, int maxValue)
        {
            if (string.IsNullOrWhiteSpace(keyValue)) return;

            var trimmedValue = keyValue.Trim();
            if (!int.TryParse(trimmedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                throw new FormatException($"\"{key}\" in \"{section}\", is not a numerical value.");

            if (intValue < minValue || intValue > maxValue)
                throw new FormatException(
                    $"\"{key}\" in \"{section}\", invalid value. Expected {minValue}...{maxValue}.");
        }

        internal static void ValidateBool(string section, string key, string keyValue)
        {
            if (string.IsNullOrWhiteSpace(keyValue)) return;

            var trimmedValue = keyValue.Trim();
            if (!string.Equals(trimmedValue, "true", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(trimmedValue, "false", StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"\"{key}\" in \"{section}\", must be true or false.");
        }

        public static IEnumerable<string> GetProfiles()
        {
            var files = Directory.GetFiles(Global.ConfigsFolder);

            foreach (var file in files)
            {
                if (!file.EndsWith(".conf")) continue;

                yield return Path.GetFileNameWithoutExtension(file);
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
            return Path.Combine(Global.ConfigsFolder, profileName + ".conf");
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

            return !reservedDeviceNames.Contains(reservedName, StringComparer.OrdinalIgnoreCase);
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

        private static string GetRequiredValue(string profilePath, string sectionName, Dictionary<string, string> section,
            string key)
        {
            if (!section.ContainsKey(key))
                throw new ArgumentException(
                    $"Profile {Path.GetFileName(profilePath)}, section \"{sectionName}\" does not have a \"{key}\" defined.");

            var value = section.Get(key);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException(
                    $"Profile {Path.GetFileName(profilePath)}, section \"{sectionName}\" has an empty \"{key}\" value.");

            return value;
        }
    }
}
