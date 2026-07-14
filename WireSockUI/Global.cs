using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace WireSockUI
{
    internal static class Global
    {
        private const string ApplicationFolderName = "WireSockUI";

        public static string MainFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ApplicationFolderName);

        public static string SecureMainFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ApplicationFolderName);

        public static string ConfigsFolder = Path.Combine(SecureMainFolder, "Configs");

        public static string NotificationAssetsFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ApplicationFolderName + "-Notifications");

        public static string LegacyConfigsFolder = Path.Combine(MainFolder, "Configs");

        public static string NativeRecoveryMarkerPath =>
            Path.Combine(SecureMainFolder, "NativeRecoveryRequired.txt");

        internal static bool AllowUnsecuredConfigFolderOverrideForTests { get; set; }

        public static EventWaitHandle AlreadyRunning;

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
                Directory.CreateDirectory(path, security);
                if (IsReparsePoint(path))
                    throw new IOException($"Refusing to secure WireSock UI directory reparse point '{path}'.");

                Directory.SetAccessControl(path, security);
                if (secureExistingChildren)
                    SecureExistingChildren(path, excludedChildDirectory);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to secure WireSock UI directory '{path}': {ex.Message}");
                throw;
            }
        }

        public static void WriteNativeRecoveryMarker(string context, string diagnostic)
        {
            try
            {
                EnsureSecureMainFolderExists();
                DeleteNativeRecoveryMarkerPathIfUnsafeForWrite();

                var message =
                    $"UTC: {DateTime.UtcNow:o}{Environment.NewLine}" +
                    $"Context: {context}{Environment.NewLine}" +
                    $"Diagnostic: {diagnostic ?? "No diagnostic available."}{Environment.NewLine}";

                File.WriteAllText(NativeRecoveryMarkerPath, message);
                File.SetAccessControl(NativeRecoveryMarkerPath, CreateAdministratorsOnlyFileSecurity());
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to write WireSock UI native recovery marker: {ex.Message}");
            }
        }

        public static string ReadNativeRecoveryMarker()
        {
            try
            {
                if (!TryGetAttributes(NativeRecoveryMarkerPath, out var attributes))
                    return null;

                if ((attributes & FileAttributes.Directory) != 0)
                    return "The native recovery marker is a directory and was not read.";

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return "The native recovery marker is a reparse point and was not read.";

                return File.ReadAllText(NativeRecoveryMarkerPath);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex)
            {
                var diagnostic = $"The native recovery marker could not be read: {ex.Message}";
                Trace.TraceWarning($"Failed to read WireSock UI native recovery marker: {ex.Message}");
                return diagnostic;
            }
        }

        public static void TryDeleteNativeRecoveryMarker()
        {
            try
            {
                DeleteNativeRecoveryMarkerPath();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to delete WireSock UI native recovery marker: {ex.Message}");
            }
        }

        private static void DeleteNativeRecoveryMarkerPathIfUnsafeForWrite()
        {
            if (!TryGetAttributes(NativeRecoveryMarkerPath, out var attributes))
                return;

            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                return;

            DeleteNativeRecoveryMarkerPath(attributes);
        }

        private static void DeleteNativeRecoveryMarkerPath()
        {
            if (TryGetAttributes(NativeRecoveryMarkerPath, out var attributes))
                DeleteNativeRecoveryMarkerPath(attributes);
        }

        private static void DeleteNativeRecoveryMarkerPath(FileAttributes attributes)
        {
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(
                    NativeRecoveryMarkerPath,
                    (attributes & FileAttributes.ReparsePoint) == 0);
                return;
            }

            File.Delete(NativeRecoveryMarkerPath);
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
                attributes = 0;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = 0;
                return false;
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
                Directory.CreateDirectory(path, security);
                if (IsReparsePoint(path))
                    throw new IOException($"Refusing to secure WireSock UI read-only directory reparse point '{path}'.");

                Directory.SetAccessControl(path, security);
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

        private static FileSecurity CreateAdministratorsOnlyFileSecurity()
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

        private static void SecureExistingChildren(string path, string excludedDirectory = null)
        {
            var directories = new Stack<string>();
            directories.Push(path);

            while (directories.Count > 0)
            {
                var directory = directories.Pop();
                string[] files;

                try
                {
                    files = Directory.GetFiles(directory);
                }
                catch (Exception ex)
                {
                    throw new IOException(
                        $"Unable to enumerate WireSock UI configuration files in '{directory}'.", ex);
                }

                foreach (var file in files)
                {
                    if (IsReparsePoint(file))
                    {
                        DeleteConfigurationFileReparsePoint(file);
                        continue;
                    }

                    try
                    {
                        File.SetAccessControl(file, CreateAdministratorsOnlyFileSecurity());
                    }
                    catch (Exception ex)
                    {
                        throw new UnauthorizedAccessException(
                            $"Failed to secure WireSock UI configuration file '{file}'.", ex);
                    }
                }

                string[] childDirectories;
                try
                {
                    childDirectories = Directory.GetDirectories(directory);
                }
                catch (Exception ex)
                {
                    throw new IOException(
                        $"Unable to enumerate WireSock UI configuration directories in '{directory}'.", ex);
                }

                foreach (var childDirectory in childDirectories)
                {
                    if (!string.IsNullOrWhiteSpace(excludedDirectory) &&
                        IsSameOrChildPath(childDirectory, excludedDirectory))
                        continue;

                    if (IsReparsePoint(childDirectory))
                        throw new IOException(
                            $"Refusing to secure WireSock UI configuration directory reparse point '{childDirectory}'.");

                    try
                    {
                        Directory.SetAccessControl(childDirectory, CreateAdministratorsOnlyDirectorySecurity());
                        directories.Push(childDirectory);
                    }
                    catch (Exception ex)
                    {
                        throw new UnauthorizedAccessException(
                            $"Failed to secure WireSock UI configuration directory '{childDirectory}'.", ex);
                    }
                }
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
            FileAttributes attributes;

            try
            {
                attributes = File.GetAttributes(file);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Unable to inspect WireSock UI configuration file reparse point '{file}'.", ex);
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
                throw new IOException(
                    $"WireSock UI configuration file '{file}' changed while its reparse-point status was being validated.");

            try
            {
                File.Delete(file);
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
