using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace WireSockUI.Native
{
    internal class WindowsApplicationContext : ApplicationContext
    {
        internal const long MaxNotificationShortcutSizeBytes = 1024 * 1024;

        private sealed class NotificationShortcutCreateCollisionException : IOException
        {
            internal NotificationShortcutCreateCollisionException(Exception innerException)
                : base("The notification shortcut destination already exists.", innerException)
            {
            }
        }

        private readonly string _executablePath;
        private readonly object _notificationShortcutSyncRoot = new object();
        private bool _notificationShortcutReady;

        private WindowsApplicationContext(
            string name,
            string appUserModelId,
            string executablePath,
            bool notificationShortcutReady)
        {
            Name = name;
            AppUserModelId = appUserModelId;
            _executablePath = executablePath;
            _notificationShortcutReady = notificationShortcutReady;
        }

        /// <summary>
        /// </summary>
        public string Name { get; }

        public string AppUserModelId { get; }

        internal bool NotificationShortcutReady
        {
            get
            {
                lock (_notificationShortcutSyncRoot)
                    return _notificationShortcutReady;
            }
        }

        internal bool TryEnsureNotificationShortcutReady()
        {
            lock (_notificationShortcutSyncRoot)
            {
                if (_notificationShortcutReady)
                    return true;

                _notificationShortcutReady = EnsureNotificationShortcut(Name, AppUserModelId, _executablePath);
                return _notificationShortcutReady;
            }
        }

        [DllImport("shell32.dll")]
        private static extern int SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string appId);

        public static WindowsApplicationContext FromCurrentProcess(
            string customName = null,
            string appUserModelId = null)
        {
            string executablePath;
            using (var process = Process.GetCurrentProcess())
                executablePath = process.MainModule?.FileName;

            if (executablePath == null) throw new InvalidOperationException("No valid process module found.");

            var appName = customName ?? Path.GetFileNameWithoutExtension(executablePath);
            var aumid = appUserModelId ?? BuildDefaultAppUserModelId(appName, executablePath);

            var result = SetCurrentProcessExplicitAppUserModelID(aumid);
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);

            var notificationShortcutReady = EnsureNotificationShortcut(appName, aumid, executablePath);

            return new WindowsApplicationContext(appName, aumid, executablePath, notificationShortcutReady);
        }

        private static bool EnsureNotificationShortcut(string appName, string appUserModelId, string executablePath)
        {
            try
            {
                Global.EnsureSecureMainFolderExists();
                var startMenuPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                if (string.IsNullOrWhiteSpace(startMenuPath))
                    throw new DirectoryNotFoundException(
                        "The current user's Start Menu Programs folder is unavailable.");

                using (SecureFileSystem.OpenDirectoryChainForStableChildCreation(startMenuPath))
                {
                    var shortcutFile = Path.Combine(startMenuPath, BuildShortcutFileName(appName, executablePath));
                    var stagingFile = Path.Combine(Global.SecureMainFolder,
                        $"notification-shortcut-{Guid.NewGuid():N}.lnk");
                    try
                    {
                        using (var shortcut = new ShellLink
                        {
                            TargetPath = executablePath,
                            Arguments = string.Empty,
                            AppUserModelId = appUserModelId
                        })
                        {
                            shortcut.Save(stagingFile);
                        }

                        using (var stagedShortcut = SecureFileSystem.OpenFile(stagingFile, true))
                            stagedShortcut.SetSecurity(Global.CreateAdministratorsOnlyFileSecurity());

                        InstallTrustedNotificationShortcut(stagingFile, shortcutFile);
                    }
                    finally
                    {
                        TryDeleteSecureShortcutFile(stagingFile, "staged notification shortcut");
                    }

                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException ||
                                       ex is UnauthorizedAccessException ||
                                       ex is Win32Exception ||
                                       ex is COMException ||
                                       ex is CryptographicException ||
                                       ex is InvalidOperationException)
            {
                Trace.TraceWarning($"Unable to ensure the WireSock UI notification shortcut: {ex.Message}");
                return false;
            }
        }

        internal static void InstallTrustedNotificationShortcut(
            string stagedShortcutFile,
            string shortcutFile,
            Action beforeCreate = null,
            Action<FileStream> afterCreateBeforeCopy = null,
            FileSecurity destinationSecurity = null)
        {
            if (string.IsNullOrWhiteSpace(stagedShortcutFile))
                throw new ArgumentException("A staged shortcut path is required.", nameof(stagedShortcutFile));
            if (string.IsNullOrWhiteSpace(shortcutFile))
                throw new ArgumentException("A destination shortcut path is required.", nameof(shortcutFile));

            if (TryGetAttributes(shortcutFile, out var existingAttributes))
            {
                Trace.TraceInformation(
                    $"Replacing notification shortcut '{shortcutFile}' without parsing user-controlled shortcut data.");
                DeleteExistingShortcut(shortcutFile, existingAttributes);
            }

            beforeCreate?.Invoke();

            try
            {
                CopyTrustedShortcutToNewFile(
                    stagedShortcutFile,
                    shortcutFile,
                    afterCreateBeforeCopy,
                    destinationSecurity ?? CreateNotificationShortcutFileSecurity());
            }
            catch (NotificationShortcutCreateCollisionException ex)
            {
                if (!TryGetAttributes(shortcutFile, out var raceWinnerAttributes))
                    throw;

                string cleanupDiagnostic = null;
                try
                {
                    DeleteExistingShortcut(shortcutFile, raceWinnerAttributes);
                }
                catch (Exception cleanupException)
                {
                    cleanupDiagnostic =
                        $" The competing path could not be removed safely: {cleanupException.Message}";
                }

                throw new IOException(
                    $"A competing process created notification shortcut path '{shortcutFile}'. " +
                    $"The competing object was rejected and was never parsed.{cleanupDiagnostic}",
                    ex);
            }
        }

        private static void CopyTrustedShortcutToNewFile(
            string stagedShortcutFile,
            string shortcutFile,
            Action<FileStream> afterCreateBeforeCopy,
            FileSecurity destinationSecurity)
        {
            var destinationCreated = false;
            try
            {
                using (var source =
                       SecureFileSystem.OpenFileForBoundedRead(
                           stagedShortcutFile, MaxNotificationShortcutSizeBytes))
                {
                    source.UseReadStream(input =>
                    {
                        FileStream output;
                        try
                        {
                            output = new FileStream(
                                shortcutFile,
                                FileMode.CreateNew,
                                FileSystemRights.Write | FileSystemRights.Delete,
                                FileShare.None,
                                81920,
                                FileOptions.WriteThrough,
                                destinationSecurity);
                            destinationCreated = true;
                        }
                        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) &&
                                                   TryGetAttributes(shortcutFile, out _))
                        {
                            throw new NotificationShortcutCreateCollisionException(ex);
                        }

                        using (output)
                        {
                            try
                            {
                                SecureFileSystem.ValidateCreatedRegularFile(
                                    output.SafeFileHandle, shortcutFile);
                                afterCreateBeforeCopy?.Invoke(output);
                                input.CopyTo(output);
                                output.Flush(true);
                            }
                            catch (Exception operationException)
                            {
                                try
                                {
                                    SecureFileSystem.DeleteOpenFile(
                                        output.SafeFileHandle, shortcutFile);
                                    destinationCreated = false;
                                }
                                catch (Exception cleanupException)
                                {
                                    throw new IOException(
                                        $"Writing notification shortcut '{shortcutFile}' failed and the opened partial file could not be removed.",
                                        new AggregateException(operationException, cleanupException));
                                }

                                throw;
                            }
                        }
                    });
                }
            }
            catch
            {
                if (destinationCreated)
                    TryDeleteDestinationShortcut(shortcutFile, "partially written notification shortcut");
                throw;
            }
        }

        private static FileSecurity CreateNotificationShortcutFileSecurity()
        {
            var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
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
            security.AddAccessRule(new FileSystemAccessRule(
                usersSid,
                FileSystemRights.ReadAndExecute,
                AccessControlType.Allow));
            return security;
        }

        private static void DeleteExistingShortcut(string shortcutFile, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    using (var shortcut = SecureFileSystem.OpenReparsePointForDelete(shortcutFile, true))
                        shortcut.Delete();
                    return;
                }

                throw new InvalidOperationException(
                    $"The notification shortcut path '{shortcutFile}' points to a directory.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                using (var shortcut = SecureFileSystem.OpenReparsePointForDelete(shortcutFile, false))
                    shortcut.Delete();
                return;
            }

            using (var shortcut = SecureFileSystem.OpenFileForDelete(shortcutFile))
                shortcut.Delete();
        }

        private static void TryDeleteSecureShortcutFile(string path, string description)
        {
            try
            {
                using (var shortcut = SecureFileSystem.OpenFileForDelete(path))
                    shortcut.Delete();
            }
            catch (FileNotFoundException)
            {
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unable to delete {description} '{path}': {ex.Message}");
            }
        }

        private static void TryDeleteDestinationShortcut(string path, string description)
        {
            try
            {
                if (TryGetAttributes(path, out var attributes))
                    DeleteExistingShortcut(path, attributes);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unable to delete {description} '{path}': {ex.Message}");
            }
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

        private static string BuildDefaultAppUserModelId(string appName, string executablePath)
        {
            const int maxAppUserModelIdLength = 128;
            const string prefix = "WireSock.Foundation";

            var seed = BuildPathSeed(executablePath);
            var segment = SanitizeAppUserModelIdSegment(appName);
            var maxSegmentLength = maxAppUserModelIdLength - prefix.Length - seed.Length - 2;
            if (segment.Length > maxSegmentLength)
                segment = segment.Substring(0, maxSegmentLength).Trim('.');

            return $"{prefix}.{segment}.{seed}";
        }

        private static string SanitizeAppUserModelIdSegment(string value)
        {
            var builder = new StringBuilder();
            foreach (var character in value ?? string.Empty)
                builder.Append(char.IsLetterOrDigit(character) ? character : '.');

            var segment = builder.ToString().Trim('.');
            return string.IsNullOrWhiteSpace(segment) ? "WireSockUI" : segment;
        }

        internal static string BuildShortcutFileName(string appName, string executablePath)
        {
            return $"{SanitizeShortcutFileNameSegment(appName)}-{BuildPathSeed(executablePath)}.lnk";
        }

        private static string SanitizeShortcutFileNameSegment(string value)
        {
            const int maxSegmentLength = 80;
            var builder = new StringBuilder();

            foreach (var character in value ?? string.Empty)
                builder.Append(char.IsLetterOrDigit(character) || character == ' ' || character == '-' ||
                               character == '_'
                    ? character
                    : '_');

            var segment = builder.ToString().Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(segment))
                segment = "WireSockUI";
            if (segment.Length > maxSegmentLength)
                segment = segment.Substring(0, maxSegmentLength).TrimEnd('.');

            return segment;
        }

        internal static string BuildPathSeed(string path)
        {
            using (var sha256 = SHA256.Create())
            {
                var normalizedPath = Path.GetFullPath(path ?? string.Empty).ToUpperInvariant();
                var hash = sha256.ComputeHash(Encoding.Unicode.GetBytes(normalizedPath));
                var builder = new StringBuilder(16);
                for (var index = 0; index < 8; index++)
                    builder.Append(hash[index].ToString("x2"));

                return builder.ToString();
            }
        }
    }
}
