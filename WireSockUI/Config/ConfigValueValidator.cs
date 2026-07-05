using System;
using System.Globalization;

namespace WireSockUI.Config
{
    internal static class ConfigValueValidator
    {
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
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsOneOf(string value, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            foreach (var item in values)
                if (string.Equals(value.Trim(), item, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        private static bool TryParseUInt(string value, out uint result)
        {
            var trimmed = value.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(trimmed.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out result);

            return uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }
    }
}
