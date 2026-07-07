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

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern void SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string appId);

        public static WindowsApplicationContext FromCurrentProcess(
            string customName = null,
            string appUserModelId = null)
        {
            var mainModule = Process.GetCurrentProcess().MainModule;

            if (mainModule?.FileName == null) throw new InvalidOperationException("No valid process module found.");

            var appName = customName ?? Path.GetFileNameWithoutExtension(mainModule.FileName);
            var aumid = appUserModelId ?? BuildDefaultAppUserModelId(appName, mainModule.FileName);

            SetCurrentProcessExplicitAppUserModelID(aumid);

            using (var shortcut = new ShellLink
                   {
                       TargetPath = mainModule.FileName,
                       Arguments = string.Empty,
                       AppUserModelId = aumid
                   })
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var startMenuPath = Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs");
                var shortcutFile = Path.Combine(startMenuPath, $"{appName}.lnk");

                shortcut.Save(shortcutFile);
            }

            return new WindowsApplicationContext(appName, aumid);
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
