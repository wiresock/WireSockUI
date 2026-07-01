using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

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

                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (string.IsNullOrWhiteSpace(line) || IsComment(line))
                        continue;

                    line = StripWireSockPrefix(line);

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line.Substring(1, line.Length - 2);
                        Sections[currentSection] = new Section();
                    }
                    else
                    {
                        var parts = line.Split(new[] { '=' }, 2);
                        if (parts.Length != 2) continue;
                        var key = parts[0].Trim();
                        var value = parts[1].Trim();
                        if (string.IsNullOrEmpty(currentSection)) continue;
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
                       (line.StartsWith("#") && !line.StartsWith("#@ws", StringComparison.OrdinalIgnoreCase));
            }

            private static string StripWireSockPrefix(string line)
            {
                if (!line.StartsWith("#@ws", StringComparison.OrdinalIgnoreCase))
                    return line;

                var value = line.Substring(4).Trim();
                if (value.StartsWith(":"))
                    value = value.Substring(1).Trim();

                return value;
            }
        }
    }
}
