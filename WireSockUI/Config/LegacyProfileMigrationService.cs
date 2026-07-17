using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WireSockUI.Native;

namespace WireSockUI.Config
{
    internal static class LegacyProfileMigrationService
    {
        internal const int MaxLegacyCatalogEntries = 1024;

        internal static void StageLegacyProfiles()
        {
            if (!Directory.Exists(Global.LegacyConfigsFolder))
                return;

            using (SecureFileSystem.OpenDirectory(Global.LegacyConfigsFolder, false))
            {
                Global.EnsurePendingLegacyProfilesFolderExists();

                foreach (var legacyProfilePath in EnumerateProfileFiles(
                             Global.LegacyConfigsFolder, "legacy profile"))
                    StageLegacyProfile(legacyProfilePath);
            }
        }

        internal static IReadOnlyList<string> GetPendingProfileNames()
        {
            Global.EnsurePendingLegacyProfilesFolderExists();
            var names = new List<string>();

            using (SecureFileSystem.OpenDirectory(Global.PendingLegacyProfilesFolder, false))
            {
                foreach (var path in EnumerateProfileFiles(
                             Global.PendingLegacyProfilesFolder, "staged legacy profile"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        if (!Profile.IsValidProfileName(name))
                        {
                            Trace.TraceWarning(
                                $"Ignoring staged legacy profile with unsafe name '{Path.GetFileName(path)}'.");
                            continue;
                        }

                        using (SecureFileSystem.OpenFile(path, false))
                            names.Add(name);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning($"Ignoring unsafe staged legacy profile '{path}': {ex.Message}");
                    }
                }
            }

            return names
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        private static IEnumerable<string> EnumerateProfileFiles(string folder, string description)
        {
            var entries = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         folder, "*", SearchOption.TopDirectoryOnly))
            {
                entries++;
                if (entries > MaxLegacyCatalogEntries)
                    throw new InvalidDataException(
                        $"The {description} folder contains more than {MaxLegacyCatalogEntries} entries. Remove unused files or directories before continuing.");

                if (!path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryGetAttributes(path, out var attributes) &&
                    (attributes & FileAttributes.Directory) != 0)
                    continue;

                yield return path;
            }
        }

        internal static string GetPendingProfilePath(string profileName)
        {
            if (!Profile.IsValidProfileName(profileName))
                throw new ArgumentException("The legacy profile name is invalid.", nameof(profileName));

            return Path.Combine(Global.PendingLegacyProfilesFolder, profileName + ".conf");
        }

        internal static void CompleteApprovedMigration(string originalProfileName)
        {
            var pendingPath = GetPendingProfilePath(originalProfileName);
            if (!TryGetAttributes(pendingPath, out var pendingAttributes))
                return;

            if ((pendingAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException(
                    $"The staged legacy profile '{pendingPath}' is not a regular file and was not deleted.");

            var matchingSourceFound = false;
            var sourceCandidateFound = false;
            var legacyPaths = Directory.Exists(Global.LegacyConfigsFolder)
                ? EnumerateProfileFiles(Global.LegacyConfigsFolder, "legacy profile").ToArray()
                : Array.Empty<string>();
            foreach (var legacyPath in legacyPaths)
            {
                if (!string.Equals(Path.GetFileNameWithoutExtension(legacyPath), originalProfileName,
                        StringComparison.Ordinal))
                    continue;

                sourceCandidateFound = true;
                if (!DeleteLegacySourceIfMatchesStagedCopy(legacyPath, pendingPath))
                {
                    Trace.TraceWarning(
                        $"Preserving legacy profile '{legacyPath}' because it differs from the approved staged copy.");
                    continue;
                }

                matchingSourceFound = true;
            }

            if (sourceCandidateFound && !matchingSourceFound)
                throw new IOException(
                    "The legacy source no longer matches its staged copy. The staged copy was preserved so the migration can be reviewed again.");

            DeleteRegularFileIfPresent(pendingPath, "staged legacy profile");
        }

        private static void StageLegacyProfile(string legacyProfilePath)
        {
            var profileName = Path.GetFileNameWithoutExtension(legacyProfilePath);
            if (!Profile.IsValidProfileName(profileName))
            {
                Trace.TraceWarning($"Skipping legacy profile with unsafe name '{Path.GetFileName(legacyProfilePath)}'.");
                return;
            }

            var securedProfilePath = Profile.GetProfilePath(profileName);
            if (Profile.ProfilePathExists(securedProfilePath))
            {
                if (!Profile.IsRegularProfileFile(securedProfilePath, out var diagnostic))
                {
                    Trace.TraceWarning(
                        $"Skipping legacy profile '{profileName}' because the secured profile path is unsafe: {diagnostic}");
                    return;
                }

                if (!FilesHaveSameContent(legacyProfilePath, securedProfilePath))
                    Trace.TraceWarning(
                        $"Skipping legacy profile '{profileName}' because an approved profile with different content already exists.");
                else
                    Trace.TraceInformation(
                        $"Leaving legacy profile '{profileName}' untouched because an identical approved profile already exists.");
                return;
            }

            var pendingPath = GetPendingProfilePath(profileName);
            if (File.Exists(pendingPath))
            {
                if (!FilesHaveSameContent(legacyProfilePath, pendingPath))
                    Trace.TraceWarning(
                        $"Skipping legacy profile '{profileName}' because a different staged copy already awaits review.");
                return;
            }

            var temporaryPath = Path.Combine(Global.PendingLegacyProfilesFolder, $"{Guid.NewGuid():N}.tmp");
            try
            {
                RegularFileSource.CopyToTemporaryFile(
                    legacyProfilePath,
                    temporaryPath,
                    Profile.MaxProfileSizeBytes,
                    "legacy profile",
                    "The legacy profile file is too large to be staged.");

                _ = new Profile(temporaryPath);
                File.Move(temporaryPath, pendingPath);
                temporaryPath = null;
                Trace.TraceInformation(
                    $"Staged legacy profile '{profileName}' for explicit review; it was not activated or promoted.");
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to stage legacy profile '{profileName}': {ex.Message}");
            }
            finally
            {
                if (temporaryPath != null)
                    TryDeleteRegularFile(temporaryPath, "temporary staged profile");
            }
        }

        private static bool FilesHaveSameContent(string firstPath, string secondPath)
        {
            try
            {
                using (var first = RegularFileSource.OpenForRead(firstPath, "legacy profile"))
                using (var second = RegularFileSource.OpenForRead(secondPath, "staged profile"))
                    return StreamsHaveSameContent(first, second);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to compare legacy profile files: {ex.Message}");
                return false;
            }
        }

        private static bool DeleteLegacySourceIfMatchesStagedCopy(string sourcePath, string pendingPath)
        {
            using (var source = SecureFileSystem.OpenFileForBoundedReadAndDelete(
                       sourcePath, Profile.MaxProfileSizeBytes))
            using (var pending = SecureFileSystem.OpenFileForBoundedRead(
                       pendingPath, Profile.MaxProfileSizeBytes))
            {
                var contentsMatch = false;
                source.UseReadStream(sourceStream =>
                    pending.UseReadStream(pendingStream =>
                        contentsMatch = StreamsHaveSameContent(sourceStream, pendingStream)));

                if (!contentsMatch)
                    return false;

                source.Delete();
                return true;
            }
        }

        private static bool StreamsHaveSameContent(Stream first, Stream second)
        {
            if (first.Length != second.Length)
                return false;

            var firstBuffer = new byte[81920];
            var secondBuffer = new byte[81920];
            while (true)
            {
                var firstBytesRead = ReadBlock(first, firstBuffer);
                var secondBytesRead = ReadBlock(second, secondBuffer);
                if (firstBytesRead != secondBytesRead)
                    return false;
                if (firstBytesRead == 0)
                    return true;

                for (var i = 0; i < firstBytesRead; i++)
                    if (firstBuffer[i] != secondBuffer[i])
                        return false;
            }
        }

        private static int ReadBlock(Stream stream, byte[] buffer)
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var bytesRead = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (bytesRead == 0)
                    break;
                totalRead += bytesRead;
            }

            return totalRead;
        }

        private static void TryDeleteRegularFile(string path, string label)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                using (var file = SecureFileSystem.OpenFileForDelete(path))
                    file.Delete();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to delete {label} '{path}': {ex.Message}");
            }
        }

        private static void DeleteRegularFileIfPresent(string path, string label)
        {
            if (!TryGetAttributes(path, out var attributes))
                return;

            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException($"The {label} '{path}' is not a regular file and was not deleted.");

            using (var file = SecureFileSystem.OpenFileForDelete(path))
                file.Delete();
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = default(FileAttributes);
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = default(FileAttributes);
                return false;
            }
        }
    }
}
