using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace WireSockUI.Native
{
    internal class WindowsApplicationContext : ApplicationContext
    {
        private WindowsApplicationContext(string name, string appUserModelId)
        {
            Name = name;
            AppUserModelId = appUserModelId;
        }

        /// <summary>
        /// </summary>
        public string Name { get; }

        public string AppUserModelId { get; }

        [DllImport("shell32.dll")]
        private static extern int SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string appId);

        public static WindowsApplicationContext FromCurrentProcess(
            string customName = null,
            string appUserModelId = null)
        {
            var mainModule = Process.GetCurrentProcess().MainModule;

            if (mainModule?.FileName == null) throw new InvalidOperationException("No valid process module found.");

            var appName = customName ?? Path.GetFileNameWithoutExtension(mainModule.FileName);
            var aumid = appUserModelId ?? BuildDefaultAppUserModelId(appName, mainModule.FileName);

            var result = SetCurrentProcessExplicitAppUserModelID(aumid);
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);

            EnsureNotificationShortcut(appName, aumid, mainModule.FileName);

            return new WindowsApplicationContext(appName, aumid);
        }

        private static void EnsureNotificationShortcut(string appName, string appUserModelId, string executablePath)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var startMenuPath = Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs");
            EnsureRegularDirectoryPath(startMenuPath);

            var shortcutFile = Path.Combine(startMenuPath, BuildShortcutFileName(appName, executablePath));
            if (TryGetAttributes(shortcutFile, out var shortcutAttributes))
            {
                if ((shortcutAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException($"Notification shortcut '{shortcutFile}' is not a regular file.");

                EnsureShortcutMatches(shortcutFile, executablePath, appUserModelId);
                return;
            }

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

                try
                {
                    File.Copy(stagingFile, shortcutFile, false);
                }
                catch (IOException) when (File.Exists(shortcutFile))
                {
                    // Another process won the create race. Reuse it only if it is exactly ours.
                }

                EnsureShortcutMatches(shortcutFile, executablePath, appUserModelId);
            }
            finally
            {
                try
                {
                    File.Delete(stagingFile);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Unable to delete staged notification shortcut '{stagingFile}': {ex.Message}");
                }
            }
        }

        private static void EnsureRegularDirectoryPath(string directory)
        {
            var current = Path.GetFullPath(directory);
            while (!string.IsNullOrWhiteSpace(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new IOException($"Notification shortcut ancestor '{current}' is not a directory.");
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Notification shortcut ancestor '{current}' is a reparse point.");

                var trimmed = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Path.GetDirectoryName(trimmed);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;

                current = parent;
            }
        }

        private static void EnsureShortcutMatches(string shortcutFile, string executablePath, string appUserModelId)
        {
            var attributes = File.GetAttributes(shortcutFile);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException($"Notification shortcut '{shortcutFile}' is not a regular file.");

            using (var shortcut = new ShellLink(shortcutFile))
            {
                if (!PathsEqual(shortcut.TargetPath, executablePath) ||
                    !string.IsNullOrEmpty(shortcut.Arguments) ||
                    !string.Equals(shortcut.AppUserModelId, appUserModelId, StringComparison.Ordinal))
                    throw new IOException(
                        $"Existing notification shortcut '{shortcutFile}' does not belong to this WireSock UI installation.");
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

        private static bool PathsEqual(string first, string second)
        {
            try
            {
                return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
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
