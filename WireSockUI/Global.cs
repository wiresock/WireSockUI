using System;
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
            var security = new DirectorySecurity();

            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow));

            return security;
        }

        private static FileSecurity CreateAdministratorsOnlyFileSecurity()
        {
            var security = new FileSecurity();

            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));

            return security;
        }

        private static void SecureExistingChildren(string path)
        {
            foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                Directory.SetAccessControl(directory, CreateAdministratorsOnlyDirectorySecurity());

            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                File.SetAccessControl(file, CreateAdministratorsOnlyFileSecurity());
        }
    }
}
