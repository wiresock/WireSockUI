using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using WireSockUI.Native;

namespace WireSockUI
{
    internal static class Global
    {
        private const string ApplicationFolderName = "WireSockUI";
        internal const int MaxSecuredTreeEntries = 4096;
        private static readonly string ApplicationDataRoot =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string CommonApplicationDataRoot =
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        public static string MainFolder =
            Path.Combine(ApplicationDataRoot, ApplicationFolderName);

        public static string SecureMainFolder =
            Path.Combine(CommonApplicationDataRoot, ApplicationFolderName);

        public static string ConfigsFolder = Path.Combine(SecureMainFolder, "Configs");

        public static string ProfileTransactionsFolder => Path.Combine(ConfigsFolder, ".transactions");

        public static string PendingLegacyProfilesFolder =
            Path.Combine(SecureMainFolder, "PendingLegacyProfiles");

        public static string DiagnosticsFolder = Path.Combine(SecureMainFolder, "Logs");

        public static string DiagnosticLogPath => Path.Combine(DiagnosticsFolder, "WireSockUI.log");

        public static string NotificationAssetsFolder =
            Path.Combine(CommonApplicationDataRoot, ApplicationFolderName + "-Notifications");

        public static string LegacyConfigsFolder = Path.Combine(MainFolder, "Configs");

        public static string NativeRecoveryMarkerPath =>
            Path.Combine(SecureMainFolder, "NativeRecoveryRequired.txt");

        internal static NativeRecoveryMarkerStore NativeRecoveryMarkers { get; } =
            new NativeRecoveryMarkerStore(
                () => NativeRecoveryMarkerPath,
                EnsureSecureMainFolderExists,
                CreateAdministratorsOnlyFileSecurity);

        internal static bool AllowUnsecuredConfigFolderOverrideForTests { get; set; }

        public static EventWaitHandle AlreadyRunning;

        internal static void ValidateSpecialFolderRoots()
        {
            RequireAbsoluteSpecialFolderRoot(
                ApplicationDataRoot,
                "the signed-in user's ApplicationData folder");
            RequireAbsoluteSpecialFolderRoot(
                CommonApplicationDataRoot,
                "the system CommonApplicationData folder");
        }

        internal static string RequireAbsoluteSpecialFolderRoot(string path, string description)
        {
            if (string.IsNullOrWhiteSpace(description))
                throw new ArgumentException("A special-folder description is required.", nameof(description));
            if (string.IsNullOrWhiteSpace(path) || !IsFullyQualifiedWindowsPath(path))
                throw new DirectoryNotFoundException(
                    $"Windows did not provide an absolute path for {description}. WireSock UI will not use a relative data directory.");

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException ||
                                       ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                throw new DirectoryNotFoundException(
                    $"Windows provided an invalid path for {description}.", ex);
            }
        }

        private static bool IsFullyQualifiedWindowsPath(string path)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                return path.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries).Length >= 2;

            return path.Length >= 3 &&
                   char.IsLetter(path[0]) &&
                   path[1] == Path.VolumeSeparatorChar &&
                   (path[2] == Path.DirectorySeparatorChar ||
                    path[2] == Path.AltDirectorySeparatorChar);
        }

        public static void EnsureApplicationFolders()
        {
            Directory.CreateDirectory(MainFolder);
            EnsureConfigsFolder();
        }

        public static void EnsureConfigsFolder()
        {
            EnsureConfigsFolder(secureExistingChildren: true);
        }

        public static void EnsureConfigsFolderExists()
        {
            EnsureConfigsFolder(secureExistingChildren: false);
        }

        public static void EnsureProfileTransactionsFolderExists()
        {
            if (!IsSameOrChildPath(ProfileTransactionsFolder, ConfigsFolder))
                throw new InvalidOperationException(
                    $"The profile transaction folder '{ProfileTransactionsFolder}' must be inside '{ConfigsFolder}'.");

            EnsureConfigsFolderExists();
            if (IsSameOrChildPath(ConfigsFolder, SecureMainFolder))
            {
                EnsureAdministratorsOnlyDirectory(ProfileTransactionsFolder, false);
                return;
            }

            if (!AllowUnsecuredConfigFolderOverrideForTests)
                throw new InvalidOperationException(
                    $"WireSock UI profile transactions cannot use the unsecured folder '{ProfileTransactionsFolder}'.");

            Directory.CreateDirectory(ProfileTransactionsFolder);
            using (SecureFileSystem.OpenDirectory(ProfileTransactionsFolder, false))
            {
            }
        }

        public static void EnsureSecureMainFolder()
        {
            EnsureAdministratorsOnlyDirectory(SecureMainFolder, true);
        }

        public static void EnsureSecureMainFolderExists()
        {
            EnsureAdministratorsOnlyDirectory(SecureMainFolder, false);
        }

        public static void EnsureNotificationAssetsFolderExists()
        {
            EnsureUsersReadOnlyDirectory(NotificationAssetsFolder);
        }

        public static void EnsurePendingLegacyProfilesFolderExists()
        {
            if (!IsSameOrChildPath(PendingLegacyProfilesFolder, SecureMainFolder))
                throw new InvalidOperationException(
                    $"Pending legacy profiles folder '{PendingLegacyProfilesFolder}' must be inside '{SecureMainFolder}'.");

            EnsureAdministratorsOnlyDirectory(SecureMainFolder, false);
            EnsureAdministratorsOnlyDirectory(PendingLegacyProfilesFolder, false);
        }

        public static void EnsureDiagnosticsFolderExists()
        {
            if (!IsSameOrChildPath(DiagnosticsFolder, SecureMainFolder))
                throw new InvalidOperationException(
                    $"Diagnostics folder '{DiagnosticsFolder}' must be inside '{SecureMainFolder}'.");

            EnsureAdministratorsOnlyDirectory(SecureMainFolder, false);
            EnsureAdministratorsOnlyDirectory(DiagnosticsFolder, false);
        }

        private static void EnsureConfigsFolder(bool secureExistingChildren)
        {
            if (IsSameOrChildPath(ConfigsFolder, SecureMainFolder))
            {
                EnsureAdministratorsOnlyDirectory(SecureMainFolder, secureExistingChildren, ConfigsFolder);
                EnsureAdministratorsOnlyDirectory(ConfigsFolder, secureExistingChildren);
                return;
            }

            if (!AllowUnsecuredConfigFolderOverrideForTests)
                throw new InvalidOperationException(
                    $"WireSock UI configuration folder '{ConfigsFolder}' must be inside the secured folder '{SecureMainFolder}'.");

            Directory.CreateDirectory(ConfigsFolder);
        }

        private static void EnsureAdministratorsOnlyDirectory(string path, bool secureExistingChildren)
        {
            EnsureAdministratorsOnlyDirectory(path, secureExistingChildren, null);
        }

        private static void EnsureAdministratorsOnlyDirectory(
            string path,
            bool secureExistingChildren,
            string excludedChildDirectory)
        {
            try
            {
                var security = CreateAdministratorsOnlyDirectorySecurity();
                if (!PathEntryExists(path))
                {
                    if (SecureFileSystem.AllowOwnerWriteFailureForTests)
                        Directory.CreateDirectory(path);
                    else
                        Directory.CreateDirectory(path, security);
                }

                using (var directory = SecureFileSystem.OpenDirectory(path, false))
                {
                    EnsureTrustedBeforeSecurityMutation(path, false);
                    if (secureExistingChildren)
                    {
                        SecureExistingChildren(path, excludedChildDirectory);
                    }
                    else
                    {
                        using (var writableDirectory = SecureFileSystem.OpenDirectory(path, true))
                            writableDirectory.SetSecurity(security);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to secure WireSock UI directory '{path}': {ex.Message}");
                throw;
            }
        }

        private static DirectorySecurity CreateAdministratorsOnlyDirectorySecurity()
        {
            var inheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var security = new DirectorySecurity();

            security.SetAccessRuleProtection(true, false);
            security.SetOwner(administratorsSid);
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                administratorsSid,
                FileSystemRights.FullControl,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow));

            return security;
        }

        private static void EnsureUsersReadOnlyDirectory(string path)
        {
            try
            {
                var security = CreateUsersReadOnlyDirectorySecurity();
                if (!PathEntryExists(path))
                {
                    if (SecureFileSystem.AllowOwnerWriteFailureForTests)
                        Directory.CreateDirectory(path);
                    else
                        Directory.CreateDirectory(path, security);
                }

                using (var directory = SecureFileSystem.OpenDirectory(path, false))
                {
                    EnsureTrustedBeforeSecurityMutation(path, false);
                    using (var writableDirectory = SecureFileSystem.OpenDirectory(path, true))
                        writableDirectory.SetSecurity(security);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to secure WireSock UI read-only directory '{path}': {ex.Message}");
                throw;
            }
        }

        private static DirectorySecurity CreateUsersReadOnlyDirectorySecurity()
        {
            var inheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var security = new DirectorySecurity();

            security.SetAccessRuleProtection(true, false);
            security.SetOwner(administratorsSid);
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                administratorsSid,
                FileSystemRights.FullControl,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                usersSid,
                FileSystemRights.ReadAndExecute,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow));

            return security;
        }

        internal static FileSecurity CreateAdministratorsOnlyFileSecurity()
        {
            var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var security = new FileSecurity();

            security.SetAccessRuleProtection(true, false);
            security.SetOwner(administratorsSid);
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                administratorsSid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));

            return security;
        }

        internal static void SecureExistingChildren(string path, string excludedDirectory = null, int depth = 0)
        {
            var entries = 0;
            SecureExistingChildrenCore(path, excludedDirectory, depth, ref entries);
        }

        private static void SecureExistingChildrenCore(
            string path,
            string excludedDirectory,
            int depth,
            ref int entries)
        {
            const int maximumDepth = 64;
            if (depth > maximumDepth)
                throw new IOException(
                    $"WireSock UI configuration directory nesting exceeds {maximumDepth} levels at '{path}'.");

            using (var directoryHandle = SecureFileSystem.OpenDirectory(path, false))
            {
                EnsureTrustedBeforeSecurityMutation(path, false);
                using (var writableDirectory = SecureFileSystem.OpenDirectory(path, true))
                    writableDirectory.SetSecurity(CreateAdministratorsOnlyDirectorySecurity());
                EnumerateBoundedChildren(path, ref entries, out var files, out var childDirectories);

                foreach (var file in files)
                {
                    if (IsReparsePoint(file))
                    {
                        DeleteConfigurationFileReparsePoint(file);
                        continue;
                    }

                    try
                    {
                        using (var fileHandle = SecureFileSystem.OpenFile(file, false))
                        {
                            EnsureTrustedBeforeSecurityMutation(file, true);
                            using (var writableFile = SecureFileSystem.OpenFile(file, true))
                                writableFile.SetSecurity(CreateAdministratorsOnlyFileSecurity());
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new UnauthorizedAccessException(
                            $"Failed to secure WireSock UI configuration file '{file}'.", ex);
                    }
                }

                foreach (var childDirectory in childDirectories)
                {
                    if (!string.IsNullOrWhiteSpace(excludedDirectory) &&
                        IsSameOrChildPath(childDirectory, excludedDirectory))
                        continue;

                    try
                    {
                        SecureExistingChildrenCore(childDirectory, null, depth + 1, ref entries);
                    }
                    catch (IOException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new UnauthorizedAccessException(
                            $"Failed to secure WireSock UI configuration directory '{childDirectory}'.", ex);
                    }
                }
            }
        }

        private static void EnumerateBoundedChildren(
            string path,
            ref int entries,
            out string[] files,
            out string[] directories)
        {
            var discoveredFiles = new List<string>();
            var discoveredDirectories = new List<string>();

            try
            {
                var directory = new DirectoryInfo(path);
                foreach (var entry in directory.EnumerateFileSystemInfos(
                             "*", SearchOption.TopDirectoryOnly))
                {
                    entries++;
                    if (entries > MaxSecuredTreeEntries)
                        throw new InvalidDataException(
                            $"The WireSock UI secured data tree contains more than {MaxSecuredTreeEntries} entries. Remove unexpected files or directories before continuing.");

                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                        discoveredDirectories.Add(entry.FullName);
                    else
                        discoveredFiles.Add(entry.FullName);
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Unable to enumerate WireSock UI configuration entries in '{path}'.", ex);
            }

            files = discoveredFiles.ToArray();
            directories = discoveredDirectories.ToArray();
        }

        private static void EnsureTrustedBeforeSecurityMutation(string path, bool file)
        {
            if (SecureFileSystem.AllowOwnerWriteFailureForTests)
                return;

            var potentiallyUserWritable = file
                ? Program.IsPotentiallyUserWritableFile(path)
                : Program.IsPotentiallyUserWritableDirectory(path);
            if (!potentiallyUserWritable)
                return;

            throw new UnauthorizedAccessException(
                $"Refusing to change security on pre-existing WireSock UI {(file ? "file" : "directory")} '{path}' because it is writable by or owned by non-administrative users.");
        }

        private static bool PathEntryExists(string path)
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

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unable to inspect reparse point attributes for '{path}': {ex.Message}");
                return true;
            }
        }

        private static bool IsSameOrChildPath(string path, string parentPath)
        {
            var normalizedPath = NormalizeDirectoryPath(path);
            var normalizedParent = NormalizeDirectoryPath(parentPath);
            if (normalizedPath == null || normalizedParent == null)
                return false;

            if (string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase))
                return true;

            var parentPrefix = normalizedParent.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? normalizedParent
                : normalizedParent + Path.DirectorySeparatorChar;

            return normalizedPath.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                var fullPath = Path.GetFullPath(path);
                var root = Path.GetPathRoot(fullPath);
                if (!string.IsNullOrEmpty(root) &&
                    string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                    return fullPath;

                return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unable to normalize WireSock UI directory path '{path}': {ex.Message}");
                return null;
            }
        }

        private static void DeleteConfigurationFileReparsePoint(string file)
        {
            try
            {
                using (var reparsePoint = SecureFileSystem.OpenReparsePointForDelete(file, false))
                    reparsePoint.Delete();
                Trace.TraceWarning($"Deleted WireSock UI configuration file reparse point '{file}'.");
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"WireSock UI configuration file reparse point '{file}' could not be deleted.", ex);
            }
        }
    }
}
