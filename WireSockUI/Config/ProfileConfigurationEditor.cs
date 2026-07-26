using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WireSockUI.Config
{
    internal static class ProfileConfigurationEditor
    {
        private static readonly Regex SectionHeader =
            new Regex(@"^\s*\[\s*(?<name>[^\]\r\n]+?)\s*\]\s*$", RegexOptions.Compiled);

        private sealed class ConfigurationLine
        {
            internal int Start { get; set; }
            internal int Length { get; set; }
            internal string Text { get; set; }
        }

        internal static bool TryInsertOrAppendPeerValue(
            string configuration,
            string key,
            string value,
            out string updatedConfiguration,
            out int selectionIndex,
            out string diagnostic)
        {
            updatedConfiguration = configuration ?? string.Empty;
            selectionIndex = updatedConfiguration.Length;
            diagnostic = null;

            if (string.IsNullOrWhiteSpace(key) || !Regex.IsMatch(key, @"^[a-zA-Z0-9]+$"))
            {
                diagnostic = "The configuration key is invalid.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                diagnostic = "The application rule cannot be empty.";
                return false;
            }

            if (value.IndexOfAny(new[] { ',', '\r', '\n' }) >= 0)
            {
                diagnostic =
                    "The selected application path contains a comma or line break, which cannot be represented safely in the comma-delimited application rule.";
                return false;
            }

            var lines = SplitLines(updatedConfiguration);
            var peerHeaderIndex = FindLastPeerHeader(lines);
            if (peerHeaderIndex < 0)
            {
                diagnostic = "Add a valid [Peer] section before adding application rules.";
                return false;
            }

            var peerEndLineIndex = FindSectionEnd(lines, peerHeaderIndex + 1);
            for (var lineIndex = peerHeaderIndex + 1; lineIndex < peerEndLineIndex; lineIndex++)
            {
                var line = lines[lineIndex];
                if (!TryBuildAppendedAssignment(line.Text, key, value, out var replacement))
                    continue;

                updatedConfiguration = updatedConfiguration
                    .Remove(line.Start, line.Length)
                    .Insert(line.Start, replacement);
                selectionIndex = line.Start + replacement.Length;
                return true;
            }

            var insertionIndex = peerEndLineIndex < lines.Count
                ? lines[peerEndLineIndex].Start
                : updatedConfiguration.Length;
            var newline = DetectNewline(updatedConfiguration);
            var prefix = insertionIndex > 0 && !IsNewline(updatedConfiguration[insertionIndex - 1])
                ? newline
                : string.Empty;
            var suffix = insertionIndex < updatedConfiguration.Length ? newline : string.Empty;
            var directive = $"#@ws:{key} = {value}";
            var insertion = prefix + directive + suffix;

            updatedConfiguration = updatedConfiguration.Insert(insertionIndex, insertion);
            selectionIndex = insertionIndex + prefix.Length + directive.Length;
            return true;
        }

        private static int FindLastPeerHeader(IReadOnlyList<ConfigurationLine> lines)
        {
            var result = -1;
            for (var index = 0; index < lines.Count; index++)
            {
                if (TryGetSectionName(lines[index].Text, out var sectionName) &&
                    string.Equals(sectionName, "Peer", StringComparison.Ordinal))
                    result = index;
            }

            return result;
        }

        private static int FindSectionEnd(IReadOnlyList<ConfigurationLine> lines, int startIndex)
        {
            for (var index = startIndex; index < lines.Count; index++)
                if (TryGetSectionName(lines[index].Text, out _))
                    return index;

            return lines.Count;
        }

        private static bool TryGetSectionName(string line, out string sectionName)
        {
            sectionName = null;
            var match = SectionHeader.Match(line ?? string.Empty);
            if (!match.Success)
                return false;

            sectionName = match.Groups["name"].Value.Trim();
            return sectionName.Length > 0;
        }

        private static bool TryBuildAppendedAssignment(string line, string key, string value,
            out string replacement)
        {
            replacement = null;
            if (line == null)
                return false;

            var cursor = 0;
            while (cursor < line.Length && IsHorizontalWhitespace(line[cursor]))
                cursor++;

            if (cursor < line.Length && (line[cursor] == ';' || line[cursor] == '#'))
            {
                if (!line.Substring(cursor).StartsWith("#@ws:", StringComparison.Ordinal))
                    return false;

                cursor += "#@ws:".Length;
                while (cursor < line.Length && IsHorizontalWhitespace(line[cursor]))
                    cursor++;
            }

            var keyStart = cursor;
            while (cursor < line.Length && IsKeyCharacter(line[cursor]))
                cursor++;

            if (cursor == keyStart ||
                !string.Equals(line.Substring(keyStart, cursor - keyStart), key,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            if (cursor < line.Length && !IsHorizontalWhitespace(line[cursor]) && line[cursor] != '=')
                return false;

            while (cursor < line.Length && IsHorizontalWhitespace(line[cursor]))
                cursor++;

            string existingValue;
            if (cursor < line.Length && line[cursor] == '=')
            {
                cursor++;
                existingValue = line.Substring(cursor).Trim();
            }
            else if (cursor == line.Length)
            {
                existingValue = string.Empty;
            }
            else
            {
                // Repair an incomplete line such as "AllowedApps app.exe" while the user is editing it.
                existingValue = line.Substring(cursor).Trim();
            }

            string combinedValue;
            if (existingValue.Length == 0)
                combinedValue = value;
            else if (existingValue.EndsWith(",", StringComparison.Ordinal))
                combinedValue = existingValue + " " + value;
            else
                combinedValue = existingValue + ", " + value;

            replacement = line.Substring(0, keyStart) + key + " = " + combinedValue;
            return true;
        }

        private static List<ConfigurationLine> SplitLines(string configuration)
        {
            var lines = new List<ConfigurationLine>();
            var start = 0;
            while (start < configuration.Length)
            {
                var end = start;
                while (end < configuration.Length && !IsNewline(configuration[end]))
                    end++;

                lines.Add(new ConfigurationLine
                {
                    Start = start,
                    Length = end - start,
                    Text = configuration.Substring(start, end - start)
                });

                if (end < configuration.Length && configuration[end] == '\r')
                    end++;
                if (end < configuration.Length && configuration[end] == '\n')
                    end++;
                start = end;
            }

            if (configuration.Length == 0)
                lines.Add(new ConfigurationLine { Start = 0, Length = 0, Text = string.Empty });

            return lines;
        }

        private static string DetectNewline(string configuration)
        {
            var newlineIndex = configuration.IndexOfAny(new[] { '\r', '\n' });
            if (newlineIndex < 0)
                return Environment.NewLine;
            if (configuration[newlineIndex] == '\r' &&
                newlineIndex + 1 < configuration.Length &&
                configuration[newlineIndex + 1] == '\n')
                return "\r\n";
            return configuration[newlineIndex].ToString();
        }

        private static bool IsHorizontalWhitespace(char value)
        {
            return value == ' ' || value == '\t';
        }

        private static bool IsKeyCharacter(char value)
        {
            return value >= 'a' && value <= 'z' ||
                   value >= 'A' && value <= 'Z' ||
                   value >= '0' && value <= '9';
        }

        private static bool IsNewline(char value)
        {
            return value == '\r' || value == '\n';
        }
    }
}
