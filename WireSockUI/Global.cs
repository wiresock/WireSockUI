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

        public static string LegacyConfigsFolder = Path.Combine(MainFolder, "Configs");

        public static EventWaitHandle AlreadyRunning;

        public static void EnsureApplicationFolders()
        {
            Directory.CreateDirectory(MainFolder);
            EnsureAdministratorsOnlyDirectory(SecureMainFolder);
            EnsureAdministratorsOnlyDirectory(ConfigsFolder);
        }

        private static void EnsureAdministratorsOnlyDirectory(string path)
        {
            try
            {
                var security = CreateAdministratorsOnlyDirectorySecurity();
                Directory.CreateDirectory(path, security);
                if (IsReparsePoint(path))
                    throw new IOException($"Refusing to secure WireSock UI directory reparse point '{path}'.");

                Directory.SetAccessControl(path, security);
                SecureExistingChildren(path);
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

        private static void SecureExistingChildren(string path)
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
                    Trace.TraceWarning($"Unable to enumerate WireSock UI configuration files in '{directory}': {ex.Message}");
                    files = Array.Empty<string>();
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
                        Trace.TraceWarning($"Failed to secure WireSock UI configuration file '{file}': {ex.Message}");
                    }
                }

                string[] childDirectories;
                try
                {
                    childDirectories = Directory.GetDirectories(directory);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning(
                        $"Unable to enumerate WireSock UI configuration directories in '{directory}': {ex.Message}");
                    continue;
                }

                foreach (var childDirectory in childDirectories)
                {
                    if (IsReparsePoint(childDirectory))
                    {
                        Trace.TraceWarning($"Skipping WireSock UI configuration directory reparse point '{childDirectory}'.");
                        continue;
                    }

                    try
                    {
                        Directory.SetAccessControl(childDirectory, CreateAdministratorsOnlyDirectorySecurity());
                        directories.Push(childDirectory);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning(
                            $"Failed to secure WireSock UI configuration directory '{childDirectory}': {ex.Message}");
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

        private static void DeleteConfigurationFileReparsePoint(string file)
        {
            FileAttributes attributes;

            try
            {
                attributes = File.GetAttributes(file);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"Skipping WireSock UI configuration file reparse point '{file}' because its attributes could not be inspected: {ex.Message}");
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                Trace.TraceWarning(
                    $"Skipping WireSock UI configuration file '{file}' because it is no longer a reparse point.");
                return;
            }

            try
            {
                File.Delete(file);
                Trace.TraceWarning($"Deleted WireSock UI configuration file reparse point '{file}'.");
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"Skipping WireSock UI configuration file reparse point '{file}' because it could not be deleted: {ex.Message}");
            }
        }
    }
}
