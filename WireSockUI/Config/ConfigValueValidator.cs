using System;
using System.Collections.Generic;
using System.Globalization;

namespace WireSockUI.Config
{
    internal static class ConfigValueValidator
    {
        private static readonly ConfigValueRule[] InterfaceExtensionRuleList =
        {
            new ConfigValueRule("Jc", value => IsUIntInRange(value, 0, 128), "0...128"),
            new ConfigValueRule("Jd", value => IsUIntInRange(value, 0, 200), "0...200"),
            new ConfigValueRule("Jmin", value => IsUIntInRange(value, 0, 1280), "0...1280"),
            new ConfigValueRule("Jmax", value => IsUIntInRange(value, 0, 1280), "0...1280"),
            new ConfigValueRule("S1", value => IsUIntInRange(value, 0, 1280), "0...1280"),
            new ConfigValueRule("S2", value => IsUIntInRange(value, 0, 1280), "0...1280"),
            new ConfigValueRule("S3", value => IsUIntInRange(value, 0, uint.MaxValue), $"0...{uint.MaxValue}"),
            new ConfigValueRule("S4", value => IsUIntInRange(value, 0, uint.MaxValue), $"0...{uint.MaxValue}"),
            new ConfigValueRule("H1", value => IsUIntOrRange(value, 0, uint.MaxValue),
                $"0...{uint.MaxValue} or an ascending range"),
            new ConfigValueRule("H2", value => IsUIntOrRange(value, 0, uint.MaxValue),
                $"0...{uint.MaxValue} or an ascending range"),
            new ConfigValueRule("H3", value => IsUIntOrRange(value, 0, uint.MaxValue),
                $"0...{uint.MaxValue} or an ascending range"),
            new ConfigValueRule("H4", value => IsUIntOrRange(value, 0, uint.MaxValue),
                $"0...{uint.MaxValue} or an ascending range"),
            new ConfigValueRule("Id", IsHostName, "a valid host name"),
            new ConfigValueRule("Ip", value => IsOneOf(value, "quic", "dns", "sip", "stun"),
                "one of: quic, dns, sip, stun"),
            new ConfigValueRule("Ib", value => IsOneOf(value, "chrome", "firefox", "curl", "random"),
                "one of: chrome, firefox, curl, random")
        };

        private static readonly Dictionary<string, ConfigValueRule> InterfaceExtensionRuleLookup =
            BuildInterfaceExtensionRuleLookup();

        public static IEnumerable<ConfigValueRule> InterfaceExtensionRules => InterfaceExtensionRuleList;

        public static bool TryGetInterfaceExtensionRule(string key, out ConfigValueRule rule)
        {
            return InterfaceExtensionRuleLookup.TryGetValue(key ?? string.Empty, out rule);
        }

        public static bool IsIntInRange(string value, int minValue, int maxValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue) &&
                   intValue >= minValue &&
                   intValue <= maxValue;
        }

        public static bool IsUIntInRange(string value, uint minValue, uint maxValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return TryParseUInt(value, out var intValue) &&
                   intValue >= minValue &&
                   intValue <= maxValue;
        }

        public static bool IsUIntOrRange(string value, uint minValue, uint maxValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            var parts = value.Split('-');
            if (parts.Length == 0 || parts.Length > 2)
                return false;

            if (!TryParseUInt(parts[0], out var first) || first < minValue || first > maxValue)
                return false;

            if (parts.Length == 1)
                return true;

            return TryParseUInt(parts[1], out var second) &&
                   second >= minValue &&
                   second <= maxValue &&
                   first <= second;
        }

        public static bool IsBool(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            var trimmed = value.Trim();
            return string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsOneOf(string value, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            var trimmed = value.Trim();

            foreach (var item in values)
                if (string.Equals(trimmed, item, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        public static bool IsHostName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return Uri.CheckHostName(value.Trim()) != UriHostNameType.Unknown;
        }

        private static Dictionary<string, ConfigValueRule> BuildInterfaceExtensionRuleLookup()
        {
            var rules = new Dictionary<string, ConfigValueRule>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in InterfaceExtensionRuleList)
                rules[rule.Key] = rule;

            return rules;
        }

        private static bool TryParseUInt(string value, out uint result)
        {
            var trimmed = value.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(trimmed.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out result);

            return uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        internal sealed class ConfigValueRule
        {
            public ConfigValueRule(string key, Func<string, bool> isValid, string expectedValueDescription)
            {
                Key = key;
                IsValid = isValid;
                ExpectedValueDescription = expectedValueDescription;
            }

            public string Key { get; }

            public Func<string, bool> IsValid { get; }

            public string ExpectedValueDescription { get; }
        }
    }
}
