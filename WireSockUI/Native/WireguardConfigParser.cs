using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WireSockUI.Properties;

namespace WireSockUI.Native
{
    internal static class WireguardConfigParser
    {
        public class Section
        {
            public Dictionary<string, List<string>> KeyValues { get; } =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            public bool Contains(string key)
            {
                return KeyValues.ContainsKey(key);
            }

            public List<string> this[string key]
            {
                get => KeyValues.ContainsKey(key) ? KeyValues[key] : new List<string>();
                set => KeyValues[key] = value;
            }
        }

        public class ConfigParser
        {
            public Dictionary<string, Section> Sections { get; } =
                new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase);

            public ConfigParser(string filePath)
            {
                ParseConfig(filePath);
            }

            public void ParseConfig(string filePath)
            {
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (var reader = new StreamReader(fileStream))
                {
                    Parse(reader);
                }
            }

            private void Parse(TextReader reader)
            {
                string line;
                string currentSection = null;
                var lineNumber = 0;

                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    line = line.Trim();
                    if (string.IsNullOrWhiteSpace(line) || IsComment(line))
                        continue;

                    line = StripWireSockPrefix(line);
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line.Substring(1, line.Length - 2);
                        if (string.IsNullOrWhiteSpace(currentSection))
                            throw new FormatException($"Invalid WireGuard configuration line {lineNumber}: section name is empty.");

                        if (Sections.ContainsKey(currentSection))
                            throw new FormatException(
                                string.Format(Resources.ParserDuplicateSectionError, currentSection));

                        Sections[currentSection] = new Section();
                    }
                    else
                    {
                        var parts = line.Split(new[] { '=' }, 2);
                        if (parts.Length != 2)
                            throw new FormatException(
                                $"Invalid WireGuard configuration line {lineNumber}: expected \"key = value\".");

                        var key = parts[0].Trim();
                        var value = parts[1].Trim();
                        if (string.IsNullOrWhiteSpace(key))
                            throw new FormatException(
                                $"Invalid WireGuard configuration line {lineNumber}: key name is empty.");

                        if (string.IsNullOrEmpty(currentSection))
                            throw new FormatException(
                                $"Invalid WireGuard configuration line {lineNumber}: key \"{key}\" appears before any section.");

                        if (!Sections.ContainsKey(currentSection))
                            Sections[currentSection] = new Section();
                        if (!Sections[currentSection].KeyValues.ContainsKey(key))
                            Sections[currentSection].KeyValues[key] = new List<string>();
                        Sections[currentSection].KeyValues[key].Add(value);
                    }
                }
            }

            public IEnumerable<string> GetSectionNames()
            {
                return Sections.Keys;
            }

            public Dictionary<string, string> GetSection(string sectionName)
            {
                return Sections.TryGetValue(sectionName, out var section)
                    ? section.KeyValues.ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value),
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            private static bool IsComment(string line)
            {
                return line.StartsWith(";") ||
                       (line.StartsWith("#") && !IsWireSockDirective(line));
            }

            private static string StripWireSockPrefix(string line)
            {
                if (!IsWireSockDirective(line))
                    return line;

                var value = line.Substring(4).Trim();
                if (value.StartsWith(":"))
                    value = value.Substring(1).Trim();

                return value;
            }

            private static bool IsWireSockDirective(string line)
            {
                if (!line.StartsWith("#@ws", StringComparison.OrdinalIgnoreCase))
                    return false;

                return line.Length == 4 || line[4] == ':' || char.IsWhiteSpace(line[4]);
            }
        }
    }
}
