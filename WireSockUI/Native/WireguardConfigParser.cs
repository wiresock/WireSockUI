using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WireSockUI.Properties;

namespace WireSockUI.Native
{
    internal static class WireguardConfigParser
    {
        public class Section
        {
            public Dictionary<string, List<string>> KeyValues { get; } =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);

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
            private static readonly HashSet<string> MultiValueKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "Interface\0Address", "Interface\0DNS", "Interface\0PreUp", "Interface\0PostUp",
                "Interface\0PreDown", "Interface\0PostDown", "Peer\0AllowedIPs", "Peer\0AllowedApps",
                "Peer\0DisallowedApps", "Peer\0DisallowedIPs"
            };

            public Dictionary<string, Section> Sections { get; } =
                new Dictionary<string, Section>(StringComparer.Ordinal);

            public ConfigParser(string filePath)
            {
                ParseConfig(filePath);
            }

            public void ParseConfig(string filePath)
            {
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    RejectUnsupportedByteOrderMark(fileStream);
                    using (var reader = new StreamReader(fileStream, new UTF8Encoding(false, true), false))
                    {
                        Parse(reader);
                    }
                }
            }

            private static void RejectUnsupportedByteOrderMark(FileStream stream)
            {
                var prefix = new byte[4];
                var bytesRead = stream.Read(prefix, 0, prefix.Length);
                stream.Position = 0;

                var hasUtf8Bom = bytesRead >= 3 && prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf;
                var hasUtf16Bom = bytesRead >= 2 &&
                                  (prefix[0] == 0xff && prefix[1] == 0xfe ||
                                   prefix[0] == 0xfe && prefix[1] == 0xff);
                var hasUtf32BigEndianBom = bytesRead >= 4 && prefix[0] == 0 && prefix[1] == 0 &&
                                           prefix[2] == 0xfe && prefix[3] == 0xff;

                if (hasUtf8Bom || hasUtf16Bom || hasUtf32BigEndianBom)
                    throw new FormatException(
                        "The current wgbooster.dll parser requires a UTF-8 configuration without a byte-order mark (BOM).");
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
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        if (string.IsNullOrEmpty(currentSection))
                            throw new FormatException($"Invalid WireGuard configuration line {lineNumber}: section name is empty.");

                        if (Sections.ContainsKey(currentSection))
                            throw new FormatException(
                                $"Invalid WireGuard configuration line {lineNumber}: {string.Format(Resources.ParserDuplicateSectionError, currentSection)}");

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
                    ? section.KeyValues.ToDictionary(kv => kv.Key,
                        kv => MultiValueKeys.Contains(sectionName + "\0" + kv.Key)
                            ? string.Join(", ", kv.Value)
                            : kv.Value.Last(),
                        StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
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

                return line.Substring(5).Trim();
            }

            private static bool IsWireSockDirective(string line)
            {
                return line.StartsWith("#@ws:", StringComparison.Ordinal);
            }
        }
    }
}
