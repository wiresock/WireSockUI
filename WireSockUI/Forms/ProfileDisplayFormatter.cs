using System;
using System.Text;

namespace WireSockUI.Forms
{
    internal static class ProfileDisplayFormatter
    {
        private const int MaximumApplicationValues = 50;
        private const int MaximumDisplayCharacters = 4096;
        private const int MaximumIpValues = 20;

        internal static string FormatApplications(string input)
        {
            return FormatCommaSeparated(
                input, MaximumApplicationValues, 2, MaximumDisplayCharacters);
        }

        internal static string FormatIpAddresses(string input)
        {
            return FormatCommaSeparated(
                input, MaximumIpValues, 2, MaximumDisplayCharacters);
        }

        internal static string FormatCommaSeparated(
            string input,
            int maximumValues,
            int valuesPerLine,
            int maximumCharacters)
        {
            if (input == null)
                return null;
            if (maximumValues <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumValues));
            if (valuesPerLine <= 0)
                throw new ArgumentOutOfRangeException(nameof(valuesPerLine));
            if (maximumCharacters < 4)
                throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
            if (input.Length == 0)
                return string.Empty;

            var builder = new StringBuilder(Math.Min(input.Length, maximumCharacters));
            var cursor = 0;
            var valueCount = 0;
            var truncated = false;

            while (cursor < input.Length)
            {
                if (valueCount == maximumValues)
                {
                    truncated = cursor < input.Length;
                    break;
                }

                var comma = input.IndexOf(',', cursor);
                var end = comma < 0 ? input.Length : comma;
                var separator = valueCount == 0
                    ? string.Empty
                    : valueCount % valuesPerLine == 0 ? Environment.NewLine : ",";
                var available = maximumCharacters - 3 - builder.Length - separator.Length;
                var valueLength = end - cursor;
                if (available < valueLength)
                {
                    if (separator.Length <= maximumCharacters - 3 - builder.Length)
                        builder.Append(separator);
                    available = maximumCharacters - 3 - builder.Length;
                    if (available > 0)
                    {
                        var appendLength = Math.Min(valueLength, available);
                        if (appendLength > 0 &&
                            char.IsHighSurrogate(input[cursor + appendLength - 1]) &&
                            cursor + appendLength < end &&
                            char.IsLowSurrogate(input[cursor + appendLength]))
                            appendLength--;
                        if (appendLength > 0)
                            builder.Append(input, cursor, appendLength);
                    }
                    truncated = true;
                    break;
                }

                builder.Append(separator);
                builder.Append(input, cursor, valueLength);
                valueCount++;

                if (comma < 0)
                    break;
                cursor = comma + 1;
            }

            if (truncated)
                builder.Append("...");
            return builder.ToString();
        }
    }
}
