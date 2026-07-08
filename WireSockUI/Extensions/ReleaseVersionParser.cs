using System;

namespace WireSockUI.Extensions
{
    internal static class ReleaseVersionParser
    {
        private static readonly char[] SuffixDelimiters = { '-', '+' };

        public static bool TryParseReleaseTag(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            var normalized = tag.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(1);

            var suffixIndex = normalized.IndexOfAny(SuffixDelimiters);
            if (suffixIndex >= 0)
                normalized = normalized.Substring(0, suffixIndex);

            return Version.TryParse(normalized, out version);
        }
    }
}
