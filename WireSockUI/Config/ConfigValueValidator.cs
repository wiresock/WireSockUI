using System;
using System.Collections.Generic;
using System.Text;

namespace WireSockUI.Config
{
    internal static class ConfigValueValidator
    {
        public const uint MaximumAmneziaPadding = 1279;

        private static readonly ConfigValueRule[] InterfaceExtensionRuleList =
        {
            new ConfigValueRule("Jc", value => IsUIntDecimalInRange(value, 0, 128), "0...128"),
            new ConfigValueRule("Jd", value => IsUIntDecimalInRange(value, 0, 200), "0...200"),
            new ConfigValueRule("Jmin", value => IsUIntDecimalInRange(value, 0, 1280), "0...1280"),
            new ConfigValueRule("Jmax", value => IsUIntDecimalInRange(value, 0, 1280), "0...1280"),
            new ConfigValueRule("S1", value => IsUIntDecimalInRange(value, 0, MaximumAmneziaPadding),
                $"0...{MaximumAmneziaPadding}"),
            new ConfigValueRule("S2", value => IsUIntDecimalInRange(value, 0, MaximumAmneziaPadding),
                $"0...{MaximumAmneziaPadding}"),
            new ConfigValueRule("S3", value => IsUIntDecimalInRange(value, 0, MaximumAmneziaPadding),
                $"0...{MaximumAmneziaPadding}"),
            new ConfigValueRule("S4", value => IsUIntDecimalInRange(value, 0, MaximumAmneziaPadding),
                $"0...{MaximumAmneziaPadding}"),
            new ConfigValueRule("H1", value => IsUIntOrRange(value, 0, uint.MaxValue),
                $"0...{uint.MaxValue} or an ascending range"),
            new ConfigValueRule("H2", value => IsUIntOrRange(value, 0, uint.MaxValue),
                $"0...{uint.MaxValue} or an ascending range"),
            new ConfigValueRule("H3", value => IsUIntOrRange(value, 0, uint.MaxValue),
                $"0...{uint.MaxValue} or an ascending range"),
            new ConfigValueRule("H4", value => IsUIntOrRange(value, 0, uint.MaxValue),
                $"0...{uint.MaxValue} or an ascending range"),
            new ConfigValueRule("Id", IsProtocolImitationId, "1...253 UTF-8 bytes"),
            new ConfigValueRule("Ip",
                value => IsOneOf(value, "quic", "quic_initial", "dns", "dns_request", "sip", "sip_request",
                    "stun", "stun_request"),
                "one of: quic, quic_initial, dns, dns_request, sip, sip_request, stun, stun_request"),
            new ConfigValueRule("Ib",
                value => IsOneOf(value, "chrome", "chromium", "firefox", "ff", "curl", "random"),
                "one of: chrome, chromium, firefox, ff, curl, random")
        };

        private static readonly Dictionary<string, ConfigValueRule> InterfaceExtensionRuleLookup =
            BuildInterfaceExtensionRuleLookup();

        public static IEnumerable<ConfigValueRule> InterfaceExtensionRules => InterfaceExtensionRuleList;

        public static bool TryGetInterfaceExtensionRule(string key, out ConfigValueRule rule)
        {
            return InterfaceExtensionRuleLookup.TryGetValue(key ?? string.Empty, out rule);
        }

        public static bool IsUIntDecimalInRange(string value, uint minValue, uint maxValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return TryParseUIntDecimal(value, out var intValue) &&
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

            if (!TryParseUIntDecimal(parts[0], out var first) || first < minValue || first > maxValue)
                return false;

            if (parts.Length == 1)
                return true;

            return TryParseUIntDecimal(parts[1], out var second) &&
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

        public static bool IsProtocolImitationId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return Encoding.UTF8.GetByteCount(value.Trim()) <= 253;
        }

        public static bool IsSipImitationHost(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var host = value.Trim();
            if (Encoding.UTF8.GetByteCount(host) > 253 || host[0] == '.' || host[0] == '-' ||
                host[host.Length - 1] == '.' || host[host.Length - 1] == '-')
                return false;

            var labelLength = 0;
            for (var index = 0; index < host.Length; index++)
            {
                var character = host[index];
                var valid = character >= 'A' && character <= 'Z' ||
                            character >= 'a' && character <= 'z' ||
                            character >= '0' && character <= '9' ||
                            character == '.' || character == '-';
                if (!valid || labelLength == 0 && character == '-')
                    return false;

                if (character == '.')
                {
                    if (labelLength == 0 || labelLength > 63 || host[index - 1] == '-')
                        return false;

                    labelLength = 0;
                    continue;
                }

                labelLength++;
                if (labelLength > 63)
                    return false;
            }

            return labelLength > 0 && labelLength <= 63;
        }

        private static Dictionary<string, ConfigValueRule> BuildInterfaceExtensionRuleLookup()
        {
            var rules = new Dictionary<string, ConfigValueRule>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in InterfaceExtensionRuleList)
                rules[rule.Key] = rule;

            return rules;
        }

        internal static bool TryParseUIntDecimal(string value, out uint result)
        {
            result = 0;
            if (value == null)
                return false;

            var trimmed = value.Trim();
            if (trimmed.Length == 0)
                return false;

            foreach (var character in trimmed)
                if (character < '0' || character > '9')
                    return false;

            return uint.TryParse(trimmed, out result);
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
