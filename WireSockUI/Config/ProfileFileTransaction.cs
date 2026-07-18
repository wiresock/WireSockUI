using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WireSockUI.Native;

namespace WireSockUI.Config
{
    internal static class ProfileFileTransaction
    {
        private const int JournalVersion = 1;
        private const int MaximumTransactionEntries = 256;
        private const long MaximumJournalSizeBytes = 16 * 1024;
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;
        private const string ProfileTemporarySuffix = ".profile.tmp";
        private const string JournalPrefix = "rename-";
        private const string JournalSuffix = ".xml";
        private const string JournalTemporaryPrefix = ".journal-";
        private const string JournalTemporarySuffix = ".tmp";
        private const string LegacyProfileTemporarySuffix = ".tmp";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly object SyncRoot = new object();

        private sealed class RenameJournal
        {
            internal RenameJournal(string originalFileName, string destinationFileName,
                string temporaryFileName)
            {
                OriginalFileName = originalFileName;
                DestinationFileName = destinationFileName;
                TemporaryFileName = temporaryFileName;
            }

            internal string OriginalFileName { get; }
            internal string DestinationFileName { get; }
            internal string TemporaryFileName { get; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        internal static string CreateTemporaryProfilePath()
        {
            Global.EnsureProfileTransactionsFolderExists();
            return Path.Combine(Global.ProfileTransactionsFolder,
                Guid.NewGuid().ToString("N") + ProfileTemporarySuffix);
        }

        internal static string WriteTemporaryProfile(string contents)
        {
            contents = contents ?? string.Empty;
            if (StrictUtf8.GetByteCount(contents) > Profile.MaxProfileSizeBytes)
                throw new InvalidDataException(
                    $"The profile exceeds the maximum supported size of {Profile.MaxProfileSizeBytes} bytes.");

            var temporaryPath = CreateTemporaryProfilePath();
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, StrictUtf8))
                    writer.Write(contents);
                return temporaryPath;
            }
            catch
            {
                TryDeleteTemporaryProfile(temporaryPath);
                throw;
            }
        }

        internal static void TryDeleteTemporaryProfile(string temporaryPath)
        {
            if (string.IsNullOrWhiteSpace(temporaryPath))
                return;

            try
            {
                var fullTemporaryPath = Path.GetFullPath(temporaryPath);
                var transactionDirectory = Path.GetFullPath(Global.ProfileTransactionsFolder);
                if (!string.Equals(Path.GetDirectoryName(fullTemporaryPath), transactionDirectory,
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsManagedProfileTemporaryName(Path.GetFileName(fullTemporaryPath)))
                {
                    Trace.TraceWarning(
                        $"Refusing to delete unmanaged temporary profile path '{temporaryPath}'.");
                    return;
                }

                DeleteRegularFileIfPresent(fullTemporaryPath);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to delete temporary profile '{temporaryPath}': {ex.Message}");
            }
        }

        public static void Commit(string temporaryPath, string destinationPath, string originalPath = null)
        {
            if (string.IsNullOrWhiteSpace(temporaryPath))
                throw new ArgumentException("A temporary profile path is required.", nameof(temporaryPath));
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("A destination profile path is required.", nameof(destinationPath));

            lock (SyncRoot)
            {
                Profile.EnsureRegularProfileFile(temporaryPath);

                if (string.IsNullOrWhiteSpace(originalPath))
                {
                    CommitNewProfile(temporaryPath, destinationPath);
                    return;
                }

                var fullOriginalPath = Path.GetFullPath(originalPath);
                var fullDestinationPath = Path.GetFullPath(destinationPath);
                if (string.Equals(fullOriginalPath, fullDestinationPath, StringComparison.Ordinal))
                {
                    CommitWithoutRename(temporaryPath, destinationPath);
                    return;
                }

                CommitRename(temporaryPath, fullDestinationPath, fullOriginalPath);
            }
        }

        internal static void RecoverInterruptedTransactions()
        {
            lock (SyncRoot)
            {
                Global.EnsureProfileTransactionsFolderExists();
                RecoverInterruptedTransactionsCore(Global.ConfigsFolder, Global.ProfileTransactionsFolder);
            }
        }

        internal static string CreateRenameJournalForTests(string originalPath, string destinationPath,
            string temporaryPath)
        {
            lock (SyncRoot)
            {
                var profileDirectory = ValidateRenamePaths(originalPath, destinationPath,
                    out var originalFileName, out var destinationFileName);
                var transactionDirectory = EnsureTransactionDirectory(profileDirectory);
                var fullTemporaryPath = Path.GetFullPath(temporaryPath);
                if (!string.Equals(Path.GetDirectoryName(fullTemporaryPath), transactionDirectory,
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsManagedProfileTemporaryName(Path.GetFileName(fullTemporaryPath)))
                    throw new InvalidOperationException(
                        "A recovery journal can only reference a managed profile transaction file.");

                Profile.EnsureRegularProfileFile(fullTemporaryPath);
                return CreateRenameJournalCore(transactionDirectory,
                    new RenameJournal(originalFileName, destinationFileName,
                        Path.GetFileName(fullTemporaryPath)));
            }
        }

        private static void CommitRename(string temporaryPath, string destinationPath, string originalPath)
        {
            var profileDirectory = ValidateRenamePaths(originalPath, destinationPath,
                out var originalFileName, out var destinationFileName);
            var caseOnlyRename = string.Equals(originalPath, destinationPath, StringComparison.OrdinalIgnoreCase);

            Profile.EnsureRegularProfileFile(originalPath);
            if (caseOnlyRename)
                ValidateCaseOnlyRenameDestination(originalPath, destinationPath);
            else if (Profile.ProfilePathExists(destinationPath))
                throw new IOException($"The destination profile '{destinationPath}' already exists.");

            var transactionDirectory = EnsureTransactionDirectory(profileDirectory);
            var stagedTemporaryPath = StageTemporaryProfile(temporaryPath, transactionDirectory);
            var journalPath = string.Empty;
            var originalMoved = false;

            try
            {
                // Publish recovery intent before changing the visible profile name.
                journalPath = CreateRenameJournalCore(transactionDirectory,
                    new RenameJournal(originalFileName, destinationFileName,
                        Path.GetFileName(stagedTemporaryPath)));

                MoveFileWithoutReplacingExisting(originalPath, destinationPath,
                    $"Unable to rename profile '{originalPath}' to '{destinationPath}'.");
                originalMoved = true;

                CommitWithoutRename(stagedTemporaryPath, destinationPath);
                TryDeleteTransactionFile(journalPath, "completed profile rename journal");
            }
            catch (Exception commitException)
            {
                var rollbackExceptions = new List<Exception>();
                var visibleRenameRolledBack = !originalMoved;
                if (originalMoved)
                {
                    try
                    {
                        MoveFileWithoutReplacingExisting(destinationPath, originalPath,
                            $"Unable to restore profile '{originalPath}'.");
                        visibleRenameRolledBack = true;
                    }
                    catch (Exception ex)
                    {
                        rollbackExceptions.Add(ex);
                    }
                }

                // Keep the staged payload beside its journal when the visible rename is still pending.
                // Startup recovery can then finish the replacement instead of mistaking the old contents
                // at the destination for a completed save.
                if (visibleRenameRolledBack &&
                    !string.Equals(stagedTemporaryPath, Path.GetFullPath(temporaryPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (EntryExists(stagedTemporaryPath))
                            MoveFileWithoutReplacingExisting(stagedTemporaryPath, temporaryPath,
                                $"Unable to restore temporary profile '{temporaryPath}'.");
                    }
                    catch (Exception ex)
                    {
                        rollbackExceptions.Add(ex);
                    }
                }

                if (rollbackExceptions.Count == 0 && !string.IsNullOrEmpty(journalPath))
                    TryDeleteTransactionFile(journalPath, "rolled-back profile rename journal");

                if (rollbackExceptions.Count == 0)
                    throw;

                throw new AggregateException(
                    "The profile rename failed and one or more rollback operations also failed.",
                    new[] { commitException }.Concat(rollbackExceptions));
            }
        }

        private static string StageTemporaryProfile(string temporaryPath, string transactionDirectory)
        {
            var fullTemporaryPath = Path.GetFullPath(temporaryPath);
            if (string.Equals(Path.GetDirectoryName(fullTemporaryPath), transactionDirectory,
                    StringComparison.OrdinalIgnoreCase) &&
                IsManagedProfileTemporaryName(Path.GetFileName(fullTemporaryPath)))
                return fullTemporaryPath;

            var stagedPath = Path.Combine(transactionDirectory,
                Guid.NewGuid().ToString("N") + ProfileTemporarySuffix);
            MoveFileWithoutReplacingExisting(fullTemporaryPath, stagedPath,
                $"Unable to stage temporary profile '{temporaryPath}'.");
            return stagedPath;
        }

        private static string ValidateRenamePaths(string originalPath, string destinationPath,
            out string originalFileName, out string destinationFileName)
        {
            var fullOriginalPath = Path.GetFullPath(originalPath);
            var fullDestinationPath = Path.GetFullPath(destinationPath);
            var originalDirectory = Path.GetDirectoryName(fullOriginalPath);
            var destinationDirectory = Path.GetDirectoryName(fullDestinationPath);
            if (string.IsNullOrWhiteSpace(originalDirectory) ||
                !string.Equals(originalDirectory, destinationDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Profile renames must remain inside one profile directory.");

            ValidateProfilePath(fullOriginalPath);
            ValidateProfilePath(fullDestinationPath);
            originalFileName = ResolveExistingFileName(fullOriginalPath);
            destinationFileName = Path.GetFileName(fullDestinationPath);
            return destinationDirectory;
        }

        private static string ResolveExistingFileName(string path)
        {
            var directory = Path.GetDirectoryName(path);
            var expectedFileName = Path.GetFileName(path);
            string match = null;
            foreach (var candidate in EnumerateProfileDirectoryFiles(
                         directory,
                         Profile.MaxProfileCatalogEntries,
                         $"The profile folder contains more than {Profile.MaxProfileCatalogEntries} entries."))
            {
                if (!string.Equals(Path.GetFileName(candidate), expectedFileName,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (match != null)
                    throw new InvalidDataException(
                        $"Multiple profile files match '{expectedFileName}' when compared case-insensitively.");

                match = Path.GetFileName(candidate);
            }

            return match ?? throw new FileNotFoundException(
                $"Profile file '{expectedFileName}' does not exist.", path);
        }

        private static void ValidateProfilePath(string profilePath)
        {
            var fileName = Path.GetFileName(profilePath);
            if (!string.Equals(Path.GetExtension(fileName), ".conf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Profile transaction path '{profilePath}' must end in .conf.");

            var profileName = Path.GetFileNameWithoutExtension(fileName);
            if (!Profile.IsValidProfileName(profileName) ||
                !string.Equals(fileName, profileName + ".conf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Profile transaction path '{profilePath}' has an invalid name.");
        }

        private static string EnsureTransactionDirectory(string profileDirectory)
        {
            var fullProfileDirectory = Path.GetFullPath(profileDirectory);
            if (string.Equals(fullProfileDirectory, Path.GetFullPath(Global.ConfigsFolder),
                    StringComparison.OrdinalIgnoreCase))
            {
                Global.EnsureProfileTransactionsFolderExists();
                return Path.GetFullPath(Global.ProfileTransactionsFolder);
            }

            var transactionDirectory = Path.Combine(fullProfileDirectory, ".transactions");
            Directory.CreateDirectory(transactionDirectory);
            using (SecureFileSystem.OpenDirectory(transactionDirectory, false))
            {
            }

            return Path.GetFullPath(transactionDirectory);
        }

        private static string CreateRenameJournalCore(string transactionDirectory, RenameJournal journal)
        {
            ValidateJournal(journal);
            var identifier = Guid.NewGuid().ToString("N");
            var temporaryJournalPath = Path.Combine(transactionDirectory,
                JournalTemporaryPrefix + identifier + JournalTemporarySuffix);
            var journalPath = Path.Combine(transactionDirectory, JournalPrefix + identifier + JournalSuffix);

            try
            {
                using (var stream = new FileStream(temporaryJournalPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, StrictUtf8))
                    writer.Write(SerializeJournal(journal));

                if (!Global.AllowUnsecuredConfigFolderOverrideForTests &&
                    string.Equals(Path.GetFullPath(transactionDirectory),
                        Path.GetFullPath(Global.ProfileTransactionsFolder), StringComparison.OrdinalIgnoreCase))
                {
                    using (var file = SecureFileSystem.OpenFile(temporaryJournalPath, true))
                        file.SetSecurity(Global.CreateAdministratorsOnlyFileSecurity());
                }

                MoveFileWithoutReplacingExisting(temporaryJournalPath, journalPath,
                    "Unable to publish the profile rename recovery journal.");
                return journalPath;
            }
            catch
            {
                TryDeleteTransactionFile(temporaryJournalPath, "incomplete profile rename journal");
                throw;
            }
        }

        private static void RecoverInterruptedTransactionsCore(string profileDirectory, string transactionDirectory)
        {
            RecoverLegacyTemporaryProfiles(profileDirectory);
            var entries = EnumerateTransactionEntries(transactionDirectory);
            var journals = entries
                .Where(path => IsManagedJournalName(Path.GetFileName(path)))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new KeyValuePair<string, RenameJournal>(path, LoadJournal(path)))
                .ToArray();

            ValidateIndependentJournals(journals.Select(item => item.Value));
            var exactProfileNames = EnumerateExactProfileFileNames(profileDirectory);
            foreach (var item in journals)
                RecoverJournal(profileDirectory, transactionDirectory, exactProfileNames, item.Key, item.Value);

            foreach (var path in EnumerateTransactionEntries(transactionDirectory))
            {
                var fileName = Path.GetFileName(path);
                if (IsManagedProfileTemporaryName(fileName) || IsManagedJournalTemporaryName(fileName))
                    DeleteRegularFileIfPresent(path);
            }
        }

        private static void RecoverLegacyTemporaryProfiles(string profileDirectory)
        {
            foreach (var path in EnumerateProfileDirectoryFiles(
                         profileDirectory,
                         Global.MaxSecuredTreeEntries,
                         $"The profile folder contains more than {Global.MaxSecuredTreeEntries} entries during transaction recovery."))
            {
                if (HasGuidFileName(Path.GetFileName(path), string.Empty, LegacyProfileTemporarySuffix))
                    DeleteRegularFileIfPresent(path);
            }
        }

        private static void RecoverJournal(string profileDirectory, string transactionDirectory,
            ISet<string> exactProfileNames, string journalPath, RenameJournal journal)
        {
            var originalFileName = journal.OriginalFileName;
            var destinationFileName = journal.DestinationFileName;
            var originalExists = exactProfileNames.Contains(originalFileName);
            var destinationExists = exactProfileNames.Contains(destinationFileName);
            if (originalExists && destinationExists)
                throw new InvalidDataException(
                    $"Profile rename recovery found both '{originalFileName}' and '{destinationFileName}'.");

            var originalPath = Path.Combine(profileDirectory, originalFileName);
            var destinationPath = Path.Combine(profileDirectory, destinationFileName);
            var temporaryPath = Path.Combine(transactionDirectory, journal.TemporaryFileName);
            var temporaryExists = EntryExists(temporaryPath);

            // Exact names distinguish the two sides of a case-only rename on Windows.
            if (originalExists)
            {
                Profile.EnsureRegularProfileFile(originalPath);
                if (temporaryExists)
                {
                    Profile.EnsureRegularProfileFile(temporaryPath);
                    DeleteRegularFileIfPresent(temporaryPath);
                }

                DeleteRegularFileIfPresent(journalPath);
                return;
            }

            if (destinationExists)
            {
                Profile.EnsureRegularProfileFile(destinationPath);
                if (temporaryExists)
                {
                    Profile.EnsureRegularProfileFile(temporaryPath);
                    CommitWithoutRename(temporaryPath, destinationPath);
                }

                DeleteRegularFileIfPresent(journalPath);
                return;
            }

            throw new InvalidDataException(
                $"Profile rename recovery could not find either '{originalFileName}' or '{destinationFileName}'.");
        }

        private static IReadOnlyList<string> EnumerateTransactionEntries(string transactionDirectory)
        {
            var entries = new List<string>();
            var enumeratedEntries = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(transactionDirectory, "*",
                         SearchOption.TopDirectoryOnly))
            {
                enumeratedEntries++;
                if (enumeratedEntries > MaximumTransactionEntries)
                    throw new InvalidDataException(
                        $"The profile transaction folder contains more than {MaximumTransactionEntries} entries.");

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    // A vanished entry is harmless, but loss of the secured parent must remain fatal.
                    using (SecureFileSystem.OpenDirectory(transactionDirectory, false))
                    {
                    }

                    continue;
                }

                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException(
                        $"The profile transaction entry '{Path.GetFileName(entry)}' is not a regular file.");

                entries.Add(entry);
            }

            return entries;
        }

        private static ISet<string> EnumerateExactProfileFileNames(string profileDirectory)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in EnumerateProfileDirectoryFiles(
                         profileDirectory,
                         Profile.MaxProfileCatalogEntries,
                         $"The profile folder contains more than {Profile.MaxProfileCatalogEntries} entries."))
            {
                if (path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                    names.Add(Path.GetFileName(path));
            }

            return names;
        }

        private static IEnumerable<string> EnumerateProfileDirectoryFiles(
            string directory,
            int maximumEntries,
            string overflowMessage)
        {
            var entries = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                entries++;
                if (entries > maximumEntries)
                    throw new InvalidDataException(overflowMessage);

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(path);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) == 0)
                    yield return path;
            }
        }

        private static RenameJournal LoadJournal(string path)
        {
            string contents;
            using (var file = SecureFileSystem.OpenFileForBoundedRead(path, MaximumJournalSizeBytes))
                contents = file.ReadAllText(StrictUtf8);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumJournalSizeBytes
            };

            XDocument document;
            using (var reader = XmlReader.Create(new StringReader(contents), settings))
                document = XDocument.Load(reader, LoadOptions.None);

            var root = document.Root;
            if (root == null || root.Name != "ProfileRename" || root.Attributes().Count() != 1 ||
                (string)root.Attribute("Version") != JournalVersion.ToString())
                throw new FormatException("The profile rename journal has an invalid root element.");

            var elements = root.Elements().ToArray();
            var expectedNames = new[] { "Original", "Destination", "Temporary" };
            if (elements.Length != expectedNames.Length ||
                expectedNames.Where((name, index) => elements[index].Name != name).Any() ||
                elements.Any(element => element.HasAttributes || element.Elements().Any()))
                throw new FormatException("The profile rename journal has an invalid structure.");

            var journal = new RenameJournal(elements[0].Value, elements[1].Value, elements[2].Value);
            ValidateJournal(journal);
            return journal;
        }

        private static string SerializeJournal(RenameJournal journal)
        {
            return new XDocument(
                    new XElement("ProfileRename",
                        new XAttribute("Version", JournalVersion),
                        new XElement("Original", journal.OriginalFileName),
                        new XElement("Destination", journal.DestinationFileName),
                        new XElement("Temporary", journal.TemporaryFileName)))
                .ToString(SaveOptions.DisableFormatting);
        }

        private static void ValidateJournal(RenameJournal journal)
        {
            if (journal == null) throw new ArgumentNullException(nameof(journal));
            if (!IsValidProfileFileName(journal.OriginalFileName) ||
                !IsValidProfileFileName(journal.DestinationFileName) ||
                string.Equals(journal.OriginalFileName, journal.DestinationFileName, StringComparison.Ordinal))
                throw new FormatException("The profile rename journal contains invalid profile file names.");
            if (!IsManagedProfileTemporaryName(journal.TemporaryFileName))
                throw new FormatException("The profile rename journal contains an invalid temporary file name.");
        }

        private static void ValidateIndependentJournals(IEnumerable<RenameJournal> journals)
        {
            var claimedProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var journal in journals)
            {
                var names = new[] { journal.OriginalFileName, journal.DestinationFileName }
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                    if (!claimedProfiles.Add(name))
                        throw new InvalidDataException(
                            $"Multiple interrupted profile transactions reference '{name}'.");
            }
        }

        private static bool IsValidProfileFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(fileName), ".conf", StringComparison.OrdinalIgnoreCase))
                return false;

            var profileName = Path.GetFileNameWithoutExtension(fileName);
            return Profile.IsValidProfileName(profileName) &&
                   string.Equals(fileName, profileName + ".conf", StringComparison.OrdinalIgnoreCase);
        }

        internal static void ValidateCaseOnlyRenameDestination(string originalPath, string destinationPath)
        {
            if (!Profile.ProfilePathExists(destinationPath))
                return;

            Profile.EnsureRegularProfileFile(destinationPath);
            if (!SecureFileSystem.ReferToSameFile(originalPath, destinationPath))
                throw new IOException($"The destination profile '{destinationPath}' already exists as a different file.");
        }

        private static void CommitWithoutRename(string temporaryPath, string destinationPath)
        {
            if (Profile.ProfilePathExists(destinationPath))
            {
                Profile.EnsureRegularProfileFile(destinationPath);
                MoveFileReplacingExisting(temporaryPath, destinationPath,
                    $"Unable to replace profile '{destinationPath}'.");
                return;
            }

            MoveFileWithoutReplacingExisting(temporaryPath, destinationPath,
                $"Unable to create profile '{destinationPath}'.");
        }

        private static void CommitNewProfile(string temporaryPath, string destinationPath)
        {
            if (Profile.ProfilePathExists(destinationPath))
                throw new IOException($"The destination profile '{destinationPath}' already exists.");

            MoveFileWithoutReplacingExisting(temporaryPath, destinationPath,
                $"Unable to create profile '{destinationPath}'.");
        }

        private static bool IsManagedProfileTemporaryName(string fileName)
        {
            return HasGuidFileName(fileName, string.Empty, ProfileTemporarySuffix);
        }

        private static bool IsManagedJournalName(string fileName)
        {
            return HasGuidFileName(fileName, JournalPrefix, JournalSuffix);
        }

        private static bool IsManagedJournalTemporaryName(string fileName)
        {
            return HasGuidFileName(fileName, JournalTemporaryPrefix, JournalTemporarySuffix);
        }

        private static bool HasGuidFileName(string fileName, string prefix, string suffix)
        {
            if (string.IsNullOrEmpty(fileName) || !fileName.StartsWith(prefix, StringComparison.Ordinal) ||
                !fileName.EndsWith(suffix, StringComparison.Ordinal))
                return false;

            var identifierLength = fileName.Length - prefix.Length - suffix.Length;
            return identifierLength == 32 &&
                   Guid.TryParseExact(fileName.Substring(prefix.Length, identifierLength), "N", out _);
        }

        private static bool EntryExists(string path)
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

        private static void DeleteRegularFileIfPresent(string path)
        {
            if (!EntryExists(path))
                return;

            using (var file = SecureFileSystem.OpenFileForDelete(path))
                file.Delete();
        }

        private static void TryDeleteTransactionFile(string path, string description)
        {
            try
            {
                DeleteRegularFileIfPresent(path);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to delete {description} '{path}': {ex.Message}");
            }
        }

        private static void MoveFileReplacingExisting(string existingPath, string destinationPath,
            string failureMessage)
        {
            MoveFile(existingPath, destinationPath, MoveFileReplaceExisting | MoveFileWriteThrough, failureMessage);
        }

        private static void MoveFileWithoutReplacingExisting(string existingPath, string destinationPath,
            string failureMessage)
        {
            MoveFile(existingPath, destinationPath, MoveFileWriteThrough, failureMessage);
        }

        private static void MoveFile(string existingPath, string destinationPath, int flags, string failureMessage)
        {
            if (MoveFileEx(existingPath, destinationPath, flags))
                return;

            var error = Marshal.GetLastWin32Error();
            throw new IOException(failureMessage, new Win32Exception(error));
        }
    }
}
