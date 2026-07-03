using System;
using WireSockUI.Properties;

namespace WireSockUI.Extensions
{
    internal static class NumberExtensions
    {
        internal static string AsHumanReadable(this ulong value)
        {
            var suffixes = new[]
            {
                Resources.SizeBytes,
                Resources.SizeKB,
                Resources.SizeMB,
                Resources.SizeGB,
                Resources.SizeTB,
                Resources.SizePB
            };
            var suffixIndex = value == 0 ? 0 : Math.Min((int)Math.Log10(value) / 3, suffixes.Length - 1);
            var scaledValue = value == 0 ? 0 : value / Math.Pow(1e3, suffixIndex);

            return $"{scaledValue:f2} {suffixes[suffixIndex]}";
        }
    }
}
