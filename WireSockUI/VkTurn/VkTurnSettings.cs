using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WireSockUI.VkTurn
{
    /// <summary>
    ///     VK TURN settings stored in a profile as <c>#@vk:Key = Value</c> lines.
    ///     WireSock and the WireSock UI parser treat these lines as comments, so a
    ///     profile stays a valid WireGuard/WireSock configuration.
    /// </summary>
    /// <example>
    ///     #@vk:Server   = 45.196.97.204:57001
    ///     #@vk:Link     = https://vk.com/call/join/AbCd..., https://vk.com/call/join/EfGh...
    ///     #@vk:WrapKey  = 64 hex characters
    ///     #@vk:Streams  = 8
    ///     #@vk:Udp      = true
    ///     #@vk:Captcha  = manual
    ///     #@vk:Listen   = 127.0.0.1:9000
    /// </example>
    internal sealed class VkTurnSettings
    {
        internal const string DirectivePrefix = "#@vk:";
        internal const string CoreProcessName = "vkturn-core";
        internal const int DefaultStreams = 8;
        internal const int MaxStreams = 64;
        internal const string DefaultListen = "127.0.0.1:9000";

        /// <summary>
        ///     The WireGuard peer that vkturn-core impersonates behind its own Wintun adapter
        ///     (adapter address 10.255.255.1/30). WireSock sends WireGuard through its NDIS
        ///     filter onto the adapter that routes to the Endpoint, so it cannot reach a
        ///     loopback listener or proxy; an address behind an adapter the core owns works.
        /// </summary>
        internal const string WintunPeer = "10.255.255.2:51820";

        private static readonly Regex WrapKeyPattern = new Regex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex HostPortPattern =
            new Regex(@"^[A-Za-z0-9.\-]+:\d{1,5}$|^\[[0-9A-Fa-f:.]+\]:\d{1,5}$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> KnownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Server", "Link", "Links", "WrapKey", "Streams", "Udp", "Captcha", "Listen"
        };

        public string Server { get; private set; }
        public IReadOnlyList<string> Links { get; private set; } = Array.Empty<string>();
        public string WrapKey { get; private set; }
        public int Streams { get; private set; } = DefaultStreams;
        public bool Udp { get; private set; } = true;
        public bool ManualCaptchaOnly { get; private set; } = true;
        public string Listen { get; private set; } = DefaultListen;

        /// <summary>Returns true when the profile text contains any #@vk: directive.</summary>
        public static bool HasDirectives(string profileText)
        {
            return EnumerateDirectives(profileText).Any();
        }

        /// <summary>
        ///     Parses #@vk: directives. Returns null when the profile has none.
        ///     Throws <see cref="FormatException" /> with a user-readable message on invalid values.
        /// </summary>
        public static VkTurnSettings Parse(string profileText)
        {
            var directives = EnumerateDirectives(profileText).ToList();
            if (directives.Count == 0)
                return null;

            var settings = new VkTurnSettings();
            var links = new List<string>();

            foreach (var d in directives)
            {
                var key = d.Key;
                var value = d.Value;
                if (!KnownKeys.Contains(key))
                    throw new FormatException($"Unknown VK TURN setting \"{DirectivePrefix}{key}\" (line {d.Line}).");

                switch (key.ToLowerInvariant())
                {
                    case "server":
                        if (!HostPortPattern.IsMatch(value))
                            throw new FormatException(
                                $"{DirectivePrefix}Server must be host:port, for example 45.196.97.204:57001 (line {d.Line}).");
                        settings.Server = value;
                        break;
                    case "link":
                    case "links":
                        foreach (var raw in value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                            links.Add(NormalizeLink(raw, d.Line));
                        break;
                    case "wrapkey":
                        if (value.Length > 0 && !WrapKeyPattern.IsMatch(value))
                            throw new FormatException(
                                $"{DirectivePrefix}WrapKey must be 64 hexadecimal characters (line {d.Line}).");
                        settings.WrapKey = value.Length > 0 ? value.ToLowerInvariant() : null;
                        break;
                    case "streams":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var streams) ||
                            streams < 1 || streams > MaxStreams)
                            throw new FormatException(
                                $"{DirectivePrefix}Streams must be a number from 1 to {MaxStreams} (line {d.Line}).");
                        settings.Streams = streams;
                        break;
                    case "udp":
                        settings.Udp = ParseBool(key, value, d.Line);
                        break;
                    case "captcha":
                        if (value.Equals("manual", StringComparison.OrdinalIgnoreCase))
                            settings.ManualCaptchaOnly = true;
                        else if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
                            settings.ManualCaptchaOnly = false;
                        else
                            throw new FormatException(
                                $"{DirectivePrefix}Captcha must be \"manual\" or \"auto\" (line {d.Line}).");
                        break;
                    case "listen":
                        if (!HostPortPattern.IsMatch(value) || !IsLoopbackHostPort(value))
                            throw new FormatException(
                                $"{DirectivePrefix}Listen must be a loopback address such as 127.0.0.1:9000 (line {d.Line}).");
                        settings.Listen = value;
                        break;
                }
            }

            if (string.IsNullOrEmpty(settings.Server))
                throw new FormatException($"VK TURN profile is missing {DirectivePrefix}Server.");
            if (links.Count == 0)
                throw new FormatException($"VK TURN profile is missing {DirectivePrefix}Link.");

            settings.Links = links.Distinct(StringComparer.Ordinal).ToList();
            return settings;
        }

        /// <summary>
        ///     Command line for vkturn-core.exe. Secrets (wrap key, link) are passed as
        ///     arguments of a child process that only the elevated UI can inspect.
        /// </summary>
        public string BuildCoreArguments(int linkIndex, bool externalCaptcha, string credsCachePath)
        {
            var args = new List<string>
            {
                "-peer", Server,
                "-vk-link", Links[linkIndex % Links.Count],
                "-wintun", WintunPeer,
                "-n", Streams.ToString(CultureInfo.InvariantCulture),
                "-events-json"
            };
            if (Udp) args.Add("-udp");
            if (!string.IsNullOrEmpty(WrapKey))
            {
                args.Add("-wrap");
                args.Add("-wrap-key");
                args.Add(WrapKey);
            }

            if (ManualCaptchaOnly) args.Add("-manual-captcha");
            if (externalCaptcha) args.Add("-external-captcha");
            if (!string.IsNullOrEmpty(credsCachePath))
            {
                args.Add("-creds-cache");
                args.Add(credsCachePath);
            }

            return string.Join(" ", args.Select(QuoteArgument));
        }

        /// <summary>
        ///     Produces the configuration handed to wgbooster.dll: the peer Endpoint is the fake
        ///     WireGuard server behind vkturn-core's Wintun adapter (<see cref="WintunPeer" />).
        ///     SOCKS5 settings are dropped (WireSock relays them through NDIS as well, so a local
        ///     proxy never works). Unless the profile uses an AllowedApps allow-list, vkturn-core
        ///     is excluded from the tunnel so its own traffic to VK never loops back.
        /// </summary>
        public string BuildEffectiveConfig(string profileText)
        {
            var lines = SplitLines(profileText);
            var output = new List<string>(lines.Count + 2);
            string section = null;
            var endpointWritten = false;
            var peerSeen = false;
            var hasAllowedApps = lines.Any(l => IsKey(l, "AllowedApps", out _));
            var disallowedAppsHandled = hasAllowedApps;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    if (section == "Peer")
                        FlushPeer(output, ref endpointWritten, ref disallowedAppsHandled);
                    section = trimmed.Substring(1, trimmed.Length - 2).Trim();
                    if (section == "Peer") peerSeen = true;
                    output.Add(line);
                    continue;
                }

                if (trimmed.StartsWith(DirectivePrefix, StringComparison.Ordinal))
                    continue; // keep secrets out of the runtime copy

                if (section == "Peer" && IsKey(line, "Endpoint", out _))
                {
                    if (!endpointWritten)
                        output.Add("Endpoint = " + WintunPeer);
                    endpointWritten = true;
                    continue;
                }

                if (section == "Peer" && (IsKey(line, "Socks5Proxy", out _) ||
                                          IsKey(line, "Socks5ProxyUsername", out _) ||
                                          IsKey(line, "Socks5ProxyPassword", out _) ||
                                          IsKey(line, "Socks5ProxyAllTraffic", out _)))
                    continue; // a local proxy cannot work with WireSock (see above)

                if (section == "Peer" && !disallowedAppsHandled && IsKey(line, "DisallowedApps", out var existing))
                {
                    var prefix = trimmed.StartsWith("#@ws:", StringComparison.Ordinal) ? "#@ws:" : string.Empty;
                    output.Add($"{prefix}DisallowedApps = {existing.TrimEnd(',', ' ')}, {CoreProcessName}");
                    disallowedAppsHandled = true;
                    continue;
                }

                output.Add(line);
            }

            if (section == "Peer")
                FlushPeer(output, ref endpointWritten, ref disallowedAppsHandled);
            if (!peerSeen)
                throw new FormatException("VK TURN profile has no [Peer] section.");

            return string.Join("\r\n", output) + "\r\n";
        }

        private void FlushPeer(List<string> output, ref bool endpointWritten, ref bool disallowedAppsHandled)
        {
            if (!endpointWritten)
            {
                output.Add("Endpoint = " + WintunPeer);
                endpointWritten = true;
            }

            if (!disallowedAppsHandled)
            {
                output.Add("#@ws:DisallowedApps = " + CoreProcessName);
                disallowedAppsHandled = true;
            }
        }

        private static bool IsKey(string line, string key, out string value)
        {
            value = null;
            var t = line.Trim();
            if (t.StartsWith("#@ws:", StringComparison.Ordinal))
                t = t.Substring(5).TrimStart();
            else if (t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith(";", StringComparison.Ordinal))
                return false;

            var eq = t.IndexOf('=');
            if (eq <= 0 || !string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                return false;
            value = t.Substring(eq + 1).Trim();
            return true;
        }

        private struct Directive
        {
            public string Key;
            public string Value;
            public int Line;
        }

        private static IEnumerable<Directive> EnumerateDirectives(string profileText)
        {
            var lineNumber = 0;
            foreach (var line in SplitLines(profileText ?? string.Empty))
            {
                lineNumber++;
                var t = line.Trim();
                if (!t.StartsWith(DirectivePrefix, StringComparison.Ordinal))
                    continue;
                var body = t.Substring(DirectivePrefix.Length);
                var eq = body.IndexOf('=');
                if (eq <= 0)
                    throw new FormatException($"Invalid VK TURN line {lineNumber}: expected \"{DirectivePrefix}Key = Value\".");
                yield return new Directive
                {
                    Key = body.Substring(0, eq).Trim(),
                    Value = body.Substring(eq + 1).Trim(),
                    Line = lineNumber
                };
            }
        }

        private static List<string> SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        }

        private static string NormalizeLink(string raw, int line)
        {
            var link = raw.Trim();
            if (link.StartsWith("https://vk.com/call/join/", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("https://vk.ru/call/join/", StringComparison.OrdinalIgnoreCase))
                return link;
            if (Regex.IsMatch(link, "^[A-Za-z0-9_\\-]{8,}$"))
                return "https://vk.com/call/join/" + link;
            throw new FormatException(
                $"{DirectivePrefix}Link must be a VK call link like https://vk.com/call/join/... (line {line}).");
        }

        private static bool ParseBool(string key, string value, int line)
        {
            if (bool.TryParse(value, out var b)) return b;
            if (value == "1") return true;
            if (value == "0") return false;
            throw new FormatException($"{DirectivePrefix}{key} must be true or false (line {line}).");
        }

        private static bool IsLoopbackHostPort(string hostPort)
        {
            var host = hostPort.Substring(0, hostPort.LastIndexOf(':')).Trim('[', ']');
            return host == "127.0.0.1" || host == "::1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }

        internal static string QuoteArgument(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return arg;
            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                    sb.Append('\\', backslashes * 2 + 1);
                else
                    sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }

            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }
    }
}
