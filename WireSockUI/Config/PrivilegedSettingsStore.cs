using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WireSockUI.Native;

namespace WireSockUI.Config
{
    internal sealed class PrivilegedSettingsSnapshot
    {
        internal PrivilegedSettingsSnapshot(bool autoConnect, string lastProfile, bool useAdapter,
            bool enableKillSwitch)
        {
            if (!string.IsNullOrEmpty(lastProfile) && !Profile.IsValidProfileName(lastProfile))
                throw new ArgumentException("The last profile name is invalid.", nameof(lastProfile));

            AutoConnect = autoConnect;
            LastProfile = lastProfile ?? string.Empty;
            UseAdapter = useAdapter;
            EnableKillSwitch = enableKillSwitch;
        }

        internal bool AutoConnect { get; }
        internal string LastProfile { get; }
        internal bool UseAdapter { get; }
        internal bool EnableKillSwitch { get; }

        internal bool IsDefault => !AutoConnect && string.IsNullOrEmpty(LastProfile) && !UseAdapter &&
                                   !EnableKillSwitch;
    }

    internal static class PrivilegedSettingsStore
    {
        private const int CurrentVersion = 1;
        private const long MaximumSettingsFileSizeBytes = 64 * 1024;
        private const string SettingsFileName = "PrivilegedSettings.xml";
        private const string BackupFileName = "PrivilegedSettings.xml.backup";
        private static readonly object SyncRoot = new object();
        private static PrivilegedSettingsSnapshot _current = CreateDefaults();

        internal static string SettingsFilePath => Path.Combine(Global.SecureMainFolder, SettingsFileName);
        private static string BackupFilePath => Path.Combine(Global.SecureMainFolder, BackupFileName);

        internal static bool AutoConnect
        {
            get { lock (SyncRoot) return _current.AutoConnect; }
        }

        internal static string LastProfile
        {
            get { lock (SyncRoot) return _current.LastProfile; }
        }

        internal static bool UseAdapter
        {
            get { lock (SyncRoot) return _current.UseAdapter; }
        }

        internal static bool EnableKillSwitch
        {
            get { lock (SyncRoot) return _current.EnableKillSwitch; }
        }

        internal static PrivilegedSettingsSnapshot Capture()
        {
            lock (SyncRoot)
                return Copy(_current);
        }

        internal static void Initialize(PrivilegedSettingsSnapshot legacySettings,
            Func<PrivilegedSettingsSnapshot, bool> confirmLegacyImport)
        {
            if (legacySettings == null) throw new ArgumentNullException(nameof(legacySettings));
            if (confirmLegacyImport == null) throw new ArgumentNullException(nameof(confirmLegacyImport));

            Global.EnsureSecureMainFolderExists();
            RecoverInterruptedSave();
            PrivilegedSettingsSnapshot loaded;
            if (PathExists(SettingsFilePath))
            {
                loaded = Load(SettingsFilePath);
            }
            else
            {
                loaded = !legacySettings.IsDefault && confirmLegacyImport(legacySettings)
                    ? legacySettings
                    : CreateDefaults();
                SaveSnapshot(loaded);
            }

            lock (SyncRoot)
                _current = Copy(loaded);
        }

        internal static void Apply(PrivilegedSettingsSnapshot settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            lock (SyncRoot)
                _current = Copy(settings);
        }

        internal static void Save()
        {
            lock (SyncRoot)
                SaveSnapshot(_current);
        }

        internal static void SetForTests(PrivilegedSettingsSnapshot settings)
        {
            Apply(settings);
        }

        internal static PrivilegedSettingsSnapshot Parse(string contents)
        {
            if (contents == null) throw new ArgumentNullException(nameof(contents));

            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumSettingsFileSizeBytes
            };

            XDocument document;
            using (var stringReader = new StringReader(contents))
            using (var reader = XmlReader.Create(stringReader, readerSettings))
                document = XDocument.Load(reader, LoadOptions.None);

            var root = document.Root;
            if (root == null || root.Name != "PrivilegedSettings")
                throw new FormatException("The protected settings document has an invalid root element.");

            var versionAttribute = root.Attribute("Version");
            if (versionAttribute == null ||
                !int.TryParse(versionAttribute.Value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var version) || version != CurrentVersion)
                throw new FormatException("The protected settings document has an unsupported version.");

            if (root.Attributes().Count() != 1)
                throw new FormatException("The protected settings document contains unsupported attributes.");

            var expectedNames = new[] { "AutoConnect", "LastProfile", "UseAdapter", "EnableKillSwitch" };
            var elements = root.Elements().ToArray();
            if (elements.Length != expectedNames.Length ||
                expectedNames.Where((name, index) => elements[index].Name != name).Any())
                throw new FormatException("The protected settings document has an invalid structure.");

            if (elements.Any(element => element.HasAttributes || element.Elements().Any()))
                throw new FormatException("The protected settings document contains unsupported nested data.");

            try
            {
                return new PrivilegedSettingsSnapshot(
                    ParseBoolean(elements[0], "AutoConnect"),
                    elements[1].Value,
                    ParseBoolean(elements[2], "UseAdapter"),
                    ParseBoolean(elements[3], "EnableKillSwitch"));
            }
            catch (ArgumentException ex)
            {
                throw new FormatException("The protected settings document contains an invalid value.", ex);
            }
        }

        internal static string Serialize(PrivilegedSettingsSnapshot settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var document = new XDocument(
                new XElement("PrivilegedSettings",
                    new XAttribute("Version", CurrentVersion),
                    new XElement("AutoConnect", XmlConvert.ToString(settings.AutoConnect)),
                    new XElement("LastProfile", settings.LastProfile),
                    new XElement("UseAdapter", XmlConvert.ToString(settings.UseAdapter)),
                    new XElement("EnableKillSwitch", XmlConvert.ToString(settings.EnableKillSwitch))));
            return document.ToString(SaveOptions.DisableFormatting);
        }

        private static PrivilegedSettingsSnapshot Load(string path)
        {
            using (var file = SecureFileSystem.OpenFileForBoundedRead(path, MaximumSettingsFileSizeBytes))
                return Parse(file.ReadAllText(new UTF8Encoding(false, true)));
        }

        private static void SaveSnapshot(PrivilegedSettingsSnapshot settings)
        {
            Global.EnsureSecureMainFolderExists();
            RecoverInterruptedSave();
            var temporaryPath = Path.Combine(Global.SecureMainFolder,
                $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");
            var backupCreated = false;
            var committed = false;

            try
            {
                WriteNewSettingsFile(temporaryPath, settings);
                using (var temporaryFile = SecureFileSystem.OpenFile(temporaryPath, true))
                    temporaryFile.SetSecurity(Global.CreateAdministratorsOnlyFileSecurity());

                if (PathExists(SettingsFilePath))
                {
                    using (SecureFileSystem.OpenFile(SettingsFilePath, false))
                    {
                    }

                    File.Move(SettingsFilePath, BackupFilePath);
                    backupCreated = true;
                }

                File.Move(temporaryPath, SettingsFilePath);

                using (var settingsFile = SecureFileSystem.OpenFile(SettingsFilePath, true))
                    settingsFile.SetSecurity(Global.CreateAdministratorsOnlyFileSecurity());
                committed = true;
            }
            catch (Exception saveException)
            {
                if (backupCreated)
                {
                    try
                    {
                        RestoreBackupAfterFailedSave(temporaryPath);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException(
                            "The protected settings update failed and the previous settings could not be restored.",
                            saveException,
                            rollbackException);
                    }
                }

                throw;
            }
            finally
            {
                TryDeleteTransactionFile(temporaryPath);
                if (committed)
                    TryDeleteTransactionFile(BackupFilePath);
            }
        }

        private static void WriteNewSettingsFile(string path, PrivilegedSettingsSnapshot settings)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false, true)))
                writer.Write(Serialize(settings));
        }

        private static void RecoverInterruptedSave()
        {
            if (!PathExists(BackupFilePath))
                return;

            using (SecureFileSystem.OpenFile(BackupFilePath, false))
            {
            }

            if (PathExists(SettingsFilePath))
            {
                try
                {
                    Load(SettingsFilePath);
                    DeleteTransactionFile(BackupFilePath);
                    return;
                }
                catch (Exception settingsException) when (IsInvalidSettingsDataException(settingsException))
                {
                    try
                    {
                        Load(BackupFilePath);
                    }
                    catch (Exception backupException)
                    {
                        throw new AggregateException(
                            "Both the protected settings file and its recovery backup are invalid.",
                            settingsException,
                            backupException);
                    }

                    RestoreBackupOverInvalidSettings(settingsException);
                    return;
                }
            }

            Load(BackupFilePath);
            File.Move(BackupFilePath, SettingsFilePath);
        }

        private static void RestoreBackupOverInvalidSettings(Exception settingsException)
        {
            var invalidPath = Path.Combine(Global.SecureMainFolder,
                $".{SettingsFileName}.{Guid.NewGuid():N}.invalid");
            File.Move(SettingsFilePath, invalidPath);
            try
            {
                File.Move(BackupFilePath, SettingsFilePath);
            }
            catch (Exception restoreException)
            {
                try
                {
                    File.Move(invalidPath, SettingsFilePath);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "The protected settings backup could not be restored and the invalid settings file could not be returned to its original path.",
                        settingsException,
                        restoreException,
                        rollbackException);
                }

                throw new AggregateException(
                    "The protected settings file is invalid and its recovery backup could not be restored.",
                    settingsException,
                    restoreException);
            }

            Trace.TraceWarning(
                $"Restored the protected settings backup because the committed file was invalid: {settingsException.Message}");
            TryDeleteTransactionFile(invalidPath);
        }

        private static void RestoreBackupAfterFailedSave(string temporaryPath)
        {
            if (PathExists(SettingsFilePath))
                File.Move(SettingsFilePath, temporaryPath);
            File.Move(BackupFilePath, SettingsFilePath);
        }

        private static void DeleteTransactionFile(string path)
        {
            using (var file = SecureFileSystem.OpenFileForDelete(path))
                file.Delete();
        }

        private static void TryDeleteTransactionFile(string path)
        {
            if (!PathExists(path))
                return;

            try
            {
                DeleteTransactionFile(path);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to remove protected settings transaction file '{path}': {ex.Message}");
            }
        }

        private static bool ParseBoolean(XElement element, string name)
        {
            try
            {
                return XmlConvert.ToBoolean(element.Value);
            }
            catch (FormatException ex)
            {
                throw new FormatException($"The protected '{name}' setting is not a valid Boolean value.", ex);
            }
        }

        private static bool IsInvalidSettingsDataException(Exception exception)
        {
            return exception is DecoderFallbackException ||
                   exception is XmlException ||
                   exception is FormatException ||
                   exception is InvalidDataException;
        }

        private static bool PathExists(string path)
        {
            try
            {
                File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static PrivilegedSettingsSnapshot CreateDefaults()
        {
            return new PrivilegedSettingsSnapshot(false, string.Empty, false, false);
        }

        private static PrivilegedSettingsSnapshot Copy(PrivilegedSettingsSnapshot settings)
        {
            return new PrivilegedSettingsSnapshot(settings.AutoConnect, settings.LastProfile, settings.UseAdapter,
                settings.EnableKillSwitch);
        }
    }
}
