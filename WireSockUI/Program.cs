using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;
using WireSockUI.Config;
using WireSockUI.Diagnostics;
using WireSockUI.Extensions;
using WireSockUI.Forms;
using WireSockUI.Native;
using WireSockUI.Properties;

namespace WireSockUI
{
    internal static class Program
    {
        private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        private const uint LoadLibrarySearchSystem32 = 0x00000800;
        private const uint LoadLibrarySearchUserDirs = 0x00000400;

        private static IntPtr _wireSockLibraryHandle = IntPtr.Zero;
        private static IntPtr _wireSockLibraryDirectoryCookie = IntPtr.Zero;
        private static bool _restrictedDllSearchPathConfigured;
        private static int _handlingUnhandledUiException;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr AddDllDirectory(string newDirectory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool RemoveDllDirectory(IntPtr cookie);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [STAThread]
        private static void Main()
        {
            if (!TryValidateApplicationPayload(Assembly.GetExecutingAssembly().Location, out var payloadDiagnostic))
            {
                MessageBox.Show(
                    $"WireSock UI cannot run safely from its current location.{Environment.NewLine}{Environment.NewLine}{payloadDiagnostic}{Environment.NewLine}{Environment.NewLine}Install WireSock UI in an administrator-owned directory and retry.",
                    "WireSock UI startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                UpgradeUserSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unable to migrate WireSock UI settings from the previous release.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    Resources.AppNoWireSockTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            }

            try
            {
                Global.EnsureApplicationFolders();
                SecureRollingTraceListener.Initialize();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unable to initialize WireSock UI secure data folders and diagnostics.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    Resources.AppNoWireSockTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            }

            RegisterUnhandledExceptionHandlers();

            try
            {
                LegacyProfileMigrationService.StageLegacyProfiles();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unable to stage legacy WireSock UI profiles: {ex}");
                MessageBox.Show(
                    $"WireSock UI could not inspect profiles from an earlier installation. " +
                    $"Those profiles will remain untouched, and startup will continue.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    Resources.AppNoWireSockTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (!IsWireSockInstalled(out var wireSockDiagnostic, out var installationCandidateFound))
            {
                var message = string.IsNullOrWhiteSpace(wireSockDiagnostic)
                    ? Resources.AppNoWireSockMessage
                    : $"{Resources.AppNoWireSockMessage}{Environment.NewLine}{Environment.NewLine}{wireSockDiagnostic}";
                MessageBox.Show(message, Resources.AppNoWireSockTitle, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                if (!installationCandidateFound)
                    OpenBrowser(Resources.AppWireSockURL);

                Environment.Exit(1);
            }

            Application.Run(new FrmMain());
        }

        internal static bool TryValidateApplicationPayload(string executablePath, out string diagnostic)
        {
            diagnostic = null;
            var normalizedExecutable = NormalizePathFileCore(executablePath, false);
            if (normalizedExecutable == null)
            {
                diagnostic = "The WireSock UI executable path is invalid.";
                return false;
            }

            if (!TryValidateTrustedFilePathCore(normalizedExecutable, "WireSock UI executable", out diagnostic,
                    false))
                return false;

            var configurationPath = normalizedExecutable + ".config";
            if (!TryValidateTrustedFilePathCore(configurationPath, "WireSock UI configuration", out diagnostic,
                    false))
                return false;

            var applicationDirectory = Path.GetDirectoryName(normalizedExecutable);
            if (!TryEnumerateApplicationPayloadEntries(applicationDirectory, out var companionFiles,
                    out var payloadDirectories, out diagnostic))
                return false;

            foreach (var payloadDirectory in payloadDirectories)
            {
                if (!TryValidateApplicationPayloadDirectory(payloadDirectory, out diagnostic))
                    return false;
            }

            foreach (var companionFile in companionFiles)
            {
                var extension = Path.GetExtension(companionFile);
                if (!string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(companionFile, normalizedExecutable, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryValidateTrustedFilePathCore(companionFile,
                        $"WireSock UI companion '{Path.GetFileName(companionFile)}'", out diagnostic, false))
                    return false;
            }

            return true;
        }

        internal static bool TryEnumerateApplicationPayloadEntries(string applicationDirectory,
            out string[] files, out string[] directories, out string diagnostic)
        {
            files = new string[0];
            directories = new string[0];
            diagnostic = null;

            var normalizedDirectory = NormalizePathDirectoryCore(applicationDirectory, false);
            if (normalizedDirectory == null)
            {
                diagnostic = "The WireSock UI application directory path is invalid.";
                return false;
            }

            var pendingDirectories = new Stack<string>();
            var discoveredDirectories = new List<string>();
            var discoveredFiles = new List<string>();
            pendingDirectories.Push(normalizedDirectory);

            try
            {
                while (pendingDirectories.Count > 0)
                {
                    var currentDirectory = pendingDirectories.Pop();
                    if (!TryGetExistingAttributes(currentDirectory, out var attributes) ||
                        (attributes & FileAttributes.Directory) == 0)
                    {
                        diagnostic = $"Application payload path '{currentDirectory}' is not an accessible directory.";
                        return false;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostic = $"Application payload directory '{currentDirectory}' is a reparse point.";
                        return false;
                    }

                    discoveredDirectories.Add(currentDirectory);
                    discoveredFiles.AddRange(Directory.GetFiles(currentDirectory, "*", SearchOption.TopDirectoryOnly));

                    foreach (var childDirectory in Directory.GetDirectories(
                                 currentDirectory, "*", SearchOption.TopDirectoryOnly))
                        pendingDirectories.Push(childDirectory);
                }
            }
            catch (Exception ex)
            {
                diagnostic =
                    $"Unable to enumerate WireSock UI application payload in '{normalizedDirectory}': {ex.Message}";
                return false;
            }

            files = discoveredFiles.ToArray();
            directories = discoveredDirectories.ToArray();
            return true;
        }

        private static bool TryValidateApplicationPayloadDirectory(string directory, out string diagnostic)
        {
            diagnostic = null;
            if (!TryGetExistingAttributes(directory, out var attributes) ||
                (attributes & FileAttributes.Directory) == 0)
            {
                diagnostic = $"Application payload path '{directory}' is not an accessible directory.";
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                diagnostic = $"Application payload directory '{directory}' is a reparse point.";
                return false;
            }

            if (!IsPotentiallyUserWritableDirectoryCore(directory, false))
                return true;

            diagnostic =
                $"Application payload directory '{directory}' is writable by or owned by non-administrative users.";
            return false;
        }

        private static void RegisterUnhandledExceptionHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, args) =>
            {
                if (System.Threading.Interlocked.Exchange(ref _handlingUnhandledUiException, 1) != 0)
                    return;

                Trace.TraceError($"Unhandled UI exception: {args.Exception}");
                Trace.Flush();
                try
                {
                    MessageBox.Show(
                        $"WireSock UI encountered an unexpected error and must close.{Environment.NewLine}{Environment.NewLine}{args.Exception.Message}",
                        Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    Application.Exit();
                }
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                Trace.TraceError($"Unhandled process exception. Terminating={args.IsTerminating}. {args.ExceptionObject}");
                Trace.Flush();
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Trace.TraceError($"Unobserved task exception: {args.Exception}");
                Trace.Flush();
                args.SetObserved();
            };
        }

        private static void UpgradeUserSettings()
        {
            RunSettingsUpgrade(
                Settings.Default.UpgradeRequired,
                Settings.Default.Upgrade,
                () => Settings.Default.UpgradeRequired = false,
                Settings.Default.Save);
        }

        internal static void RunSettingsUpgrade(bool upgradeRequired, Action upgrade, Action markComplete, Action save)
        {
            if (!upgradeRequired)
                return;

            if (upgrade == null) throw new ArgumentNullException(nameof(upgrade));
            if (markComplete == null) throw new ArgumentNullException(nameof(markComplete));
            if (save == null) throw new ArgumentNullException(nameof(save));

            upgrade();
            markComplete();
            save();
        }

        internal static void OpenBrowser(string url)
        {
            try
            {
                Process.Start(url);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to open browser for '{url}': {ex.Message}");
            }
        }

        /// <summary>
        ///     Determine if this WireSockUI was generated by an automated build from a GitHub repository
        /// </summary>
        /// <returns>Assembly repository if set during build</returns>
        private static string GetRepository()
        {
            var assembly = Assembly.GetExecutingAssembly();

            foreach (var metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
                if (string.Equals(metadata.Key, "Repository"))
                    return metadata.Value;

            return null;
        }

#if WIRESOCKUI_ENABLE_UWP
        /// <summary>
        ///     Compare the local product version against the latest GitHub repository release tag
        /// </summary>
        /// <remarks>If auto update is enabled, repository is known and there is a new version, return the releases URL.</remarks>
        internal static bool TryGetAvailableUpdate(out string releasesUrl)
        {
            releasesUrl = null;
            if (!Settings.Default.AutoUpdate) return false;
        
            try
            {
                var repository = GetRepository();
        
                if (!string.IsNullOrWhiteSpace(repository))
                {
                    var currentVersion = new Version(Application.ProductVersion);
                    var latestVersion = GitHubExtensions.GetLatestRelease(repository);
        
                    if (currentVersion != null && latestVersion != null && latestVersion > currentVersion)
                    {
                        releasesUrl = $"https://github.com/{repository}/releases";
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unable to check for WireSock UI updates: {ex.Message}");
            }

            return false;
        }
#endif

        /// <summary>
        ///     Determine if the WireSock library components are installed.
        /// </summary>
        /// <returns><c>true</c> if installed, otherwise <c>false</c></returns>
        private static bool IsWireSockInstalled(out string diagnostic, out bool installationCandidateFound)
        {
            var diagnostics = new List<string>();
            foreach (var candidate in EnumerateWireSockLibraryDirectoryCandidates())
            {
                if (!TryValidateWireSockLibraryDirectory(candidate, out var libraryDirectory,
                        out var candidateDiagnostic))
                {
                    if (!string.IsNullOrWhiteSpace(candidateDiagnostic))
                        diagnostics.Add(candidateDiagnostic);
                    continue;
                }

                if (ConfigureWireSockLibraryDirectory(libraryDirectory, out candidateDiagnostic))
                {
                    diagnostic = null;
                    installationCandidateFound = true;
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(candidateDiagnostic))
                    diagnostics.Add(candidateDiagnostic);
            }

            installationCandidateFound = diagnostics.Count != 0;
            diagnostic = !installationCandidateFound
                ? "No trusted wgbooster.dll installation candidate was found."
                : string.Join(Environment.NewLine, diagnostics);
            return false;
        }

        private static IEnumerable<string> EnumerateWireSockLibraryDirectoryCandidates()
        {
            yield return AppDomain.CurrentDomain.BaseDirectory;

            foreach (var installLocation in GetInstallLocations())
            {
                foreach (var candidate in GetLibraryDirectories(installLocation))
                    yield return candidate;
            }
        }

        internal static bool TryValidateWireSockLibraryDirectory(string directory, out string libraryDirectory)
        {
            return TryValidateWireSockLibraryDirectory(directory, out libraryDirectory, out _);
        }

        internal static bool TryValidateWireSockLibraryDirectory(string directory, out string libraryDirectory,
            out string diagnostic)
        {
            libraryDirectory = null;
            diagnostic = null;

            var fullDirectory = NormalizePathDirectory(directory);
            if (fullDirectory == null)
                return false;

            if (!TryGetExistingAttributes(fullDirectory, out var directoryAttributes, out diagnostic))
            {
                if (!string.IsNullOrWhiteSpace(diagnostic))
                    Trace.TraceWarning(diagnostic);
                return false;
            }

            if ((directoryAttributes & FileAttributes.Directory) == 0)
                return false;

            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                diagnostic =
                    $"Skipping WireSock library directory '{fullDirectory}' because it is a reparse point.";
                Trace.TraceWarning(diagnostic);
                return false;
            }

            var libraryPath = Path.Combine(fullDirectory, "wgbooster.dll");
            if (!TryGetExistingAttributes(libraryPath, out var libraryAttributes, out diagnostic))
            {
                if (!string.IsNullOrWhiteSpace(diagnostic))
                    Trace.TraceWarning(diagnostic);
                return false;
            }

            if ((libraryAttributes & FileAttributes.Directory) != 0)
            {
                diagnostic = $"Skipping WireSock library '{libraryPath}' because it is a directory.";
                Trace.TraceWarning(diagnostic);
                return false;
            }

            if ((libraryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                diagnostic = $"Skipping WireSock library '{libraryPath}' because it is a reparse point.";
                Trace.TraceWarning(diagnostic);
                return false;
            }

            if (!TryValidateTrustedFilePath(libraryPath, "WireSock library", out diagnostic))
            {
                Trace.TraceWarning(diagnostic);
                return false;
            }

            if (!TryValidateTrustedWireSockCompanionFiles(fullDirectory, libraryPath, out diagnostic))
            {
                Trace.TraceWarning(diagnostic);
                return false;
            }

            if (!WindowsBinaryArchitectureInfo.TryGetCurrentProcessArchitecture(
                    out var processArchitecture, out diagnostic))
            {
                diagnostic = $"Unable to determine the WireSock UI process architecture: {diagnostic}";
                Trace.TraceWarning(diagnostic);
                return false;
            }

            if (!WindowsBinaryArchitectureInfo.TryReadPortableExecutableArchitecture(
                    libraryPath, out var libraryArchitecture, out diagnostic))
            {
                Trace.TraceWarning(diagnostic);
                return false;
            }

            if (!WindowsBinaryArchitectureInfo.AreCompatible(processArchitecture, libraryArchitecture))
            {
                diagnostic =
                    $"WireSock library '{libraryPath}' targets {WindowsBinaryArchitectureInfo.Format(libraryArchitecture)}, but WireSock UI is running as {WindowsBinaryArchitectureInfo.Format(processArchitecture)}.";
                Trace.TraceWarning(diagnostic);
                return false;
            }

            libraryDirectory = fullDirectory;
            return true;
        }

        private static bool TryValidateTrustedWireSockCompanionFiles(string directory, string libraryPath,
            out string diagnostic)
        {
            diagnostic = null;
            string[] files;

            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex)
            {
                diagnostic = $"Unable to enumerate WireSock SDK companion files in '{directory}': {ex.Message}";
                return false;
            }

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (!string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(file, libraryPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryValidateTrustedFilePath(file,
                        $"WireSock SDK companion '{Path.GetFileName(file)}'", out diagnostic))
                    return false;
            }

            return true;
        }

        private static string[] GetLibraryDirectories(string installLocation)
        {
            if (string.IsNullOrWhiteSpace(installLocation))
                return new string[0];

            var directories = new List<string>();
            AddLibraryDirectory(directories, installLocation, "sdk");
            AddLibraryDirectory(directories, installLocation, "bin");
            directories.Add(installLocation);

            return directories.ToArray();
        }

        private static void AddLibraryDirectory(List<string> directories, string path1, string path2)
        {
            try
            {
                directories.Add(Path.Combine(path1, path2));
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"Skipping invalid WireSock library candidate '{path1}{Path.DirectorySeparatorChar}{path2}': {ex.Message}");
            }
        }

        private static string[] GetInstallLocations()
        {
            var locations = new List<string>();
            var registryPaths = new[]
            {
                "SOFTWARE\\WireSock Foundation\\WireSock Secure Connect",
                "SOFTWARE\\WireSock Foundation\\WireSock Secure Connect Pro",
                "SOFTWARE\\NTKernelResources\\WinpkFilterForVPNClient"
            };

            var registryViews = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (var view in registryViews)
            {
                RegistryKey baseKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                }
                catch
                {
                    continue;
                }

                using (baseKey)
                {
                    foreach (var registryPath in registryPaths)
                    {
                        try
                        {
                            using (var key = baseKey.OpenSubKey(registryPath))
                            {
                                var value = key?.GetValue("InstallLocation") as string;
                                if (string.IsNullOrWhiteSpace(value))
                                    continue;

                                if (!locations.Exists(path =>
                                        string.Equals(path, value, StringComparison.OrdinalIgnoreCase)))
                                    locations.Add(value);
                            }
                        }
                        catch (Exception ex)
                        {
                            Trace.TraceWarning(
                                $"Unable to inspect WireSock install registry key '{registryPath}' in the {view} view: {ex.Message}");
                        }
                    }
                }
            }

            return locations.ToArray();
        }

        private static bool ConfigureWireSockLibraryDirectory(string directory, out string diagnostic)
        {
            diagnostic = null;
            if (string.IsNullOrWhiteSpace(directory))
            {
                diagnostic = "The WireSock library directory path is empty.";
                return false;
            }

            var fullDirectory = NormalizePathDirectory(directory);
            if (fullDirectory == null)
            {
                diagnostic = $"The WireSock library directory '{directory}' is invalid.";
                return false;
            }

            var libraryPath = Path.Combine(fullDirectory, "wgbooster.dll");

            try
            {
                if (TryConfigureRestrictedDllSearchPath(fullDirectory, libraryPath, out diagnostic))
                    return true;

                diagnostic = $"Failed to configure WireSock library '{libraryPath}': {diagnostic}";
                Trace.TraceWarning(diagnostic);
            }
            catch (Exception ex)
            {
                diagnostic = $"Failed to load WireSock library '{libraryPath}': {ex.Message}";
                Trace.TraceWarning(diagnostic);
            }

            return false;
        }

        internal static bool TryConfigureRestrictedDllSearchPath(
            string fullDirectory,
            string libraryPath,
            out string diagnostic)
        {
            diagnostic = null;

            if (_wireSockLibraryHandle != IntPtr.Zero && _wireSockLibraryDirectoryCookie != IntPtr.Zero)
                return true;

            if (!TryEnsureRestrictedDllSearchPath(out diagnostic))
                return false;

            IntPtr cookie;
            try
            {
                cookie = AddDllDirectory(fullDirectory);
            }
            catch (EntryPointNotFoundException ex)
            {
                diagnostic =
                    $"This Windows installation does not support AddDllDirectory. Install KB2533623 or use a supported Windows version. {ex.Message}";
                return false;
            }

            if (cookie == IntPtr.Zero)
            {
                diagnostic = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            if (TryLoadWireSockLibrary(libraryPath, out diagnostic))
            {
                _wireSockLibraryDirectoryCookie = cookie;
                return true;
            }

            TryRemoveDllDirectory(cookie);
            return false;
        }

        private static bool TryEnsureRestrictedDllSearchPath(out string diagnostic)
        {
            diagnostic = null;

            if (_restrictedDllSearchPathConfigured)
                return true;

            try
            {
                if (!SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchUserDirs))
                {
                    diagnostic = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }
            }
            catch (EntryPointNotFoundException ex)
            {
                diagnostic =
                    $"This Windows installation does not support SetDefaultDllDirectories. Install KB2533623 or use a supported Windows version. {ex.Message}";
                return false;
            }

            _restrictedDllSearchPathConfigured = true;
            return true;
        }

        private static bool TryLoadWireSockLibrary(string libraryPath, out string diagnostic)
        {
            diagnostic = null;

            if (_wireSockLibraryHandle != IntPtr.Zero)
                return true;

            var handle = LoadLibraryEx(
                libraryPath,
                IntPtr.Zero,
                LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);

            if (handle == IntPtr.Zero)
            {
                diagnostic = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            _wireSockLibraryHandle = handle;
            return true;
        }

        private static void TryRemoveDllDirectory(IntPtr cookie)
        {
            try
            {
                if (cookie != IntPtr.Zero && !RemoveDllDirectory(cookie))
                    Trace.TraceWarning(
                        $"Failed to remove unsuccessful WireSock library directory from the process search path: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"Failed to remove unsuccessful WireSock library directory from the process search path: {ex.Message}");
            }
        }

        private static bool TryGetExistingAttributes(string path, out FileAttributes attributes,
            bool warnOnFailure = false)
        {
            return TryGetExistingAttributesCore(path, out attributes, out _, warnOnFailure);
        }

        internal static bool TryGetExistingAttributes(string path, out FileAttributes attributes,
            out string diagnostic)
        {
            return TryGetExistingAttributesCore(path, out attributes, out diagnostic, false);
        }

        private static bool TryGetExistingAttributesCore(string path, out FileAttributes attributes,
            out string diagnostic, bool warnOnFailure)
        {
            attributes = 0;
            diagnostic = null;

            try
            {
                attributes = File.GetAttributes(path);
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
            catch (Exception ex)
            {
                diagnostic =
                    $"Unable to inspect file system attributes for '{EscapeDiagnosticText(path)}': {EscapeDiagnosticText(ex.Message)}";
                if (warnOnFailure)
                    Trace.TraceWarning(diagnostic);

                return false;
            }
        }

        private static string EscapeDiagnosticText(string value)
        {
            return (value ?? string.Empty).Replace("\0", "\\0");
        }

        internal static bool IsPotentiallyUserWritableDirectory(string directory)
        {
            return IsPotentiallyUserWritableDirectoryCore(directory, true);
        }

        private static bool IsPotentiallyUserWritableDirectoryCore(string directory, bool traceFailures)
        {
            var normalizedDirectory = NormalizePathDirectoryCore(directory, traceFailures);
            if (normalizedDirectory == null)
                return true;

            try
            {
                var security = Directory.GetAccessControl(normalizedDirectory);
                return IsPotentiallyUserWritableSecurityCore(security, traceFailures);
            }
            catch (Exception ex)
            {
                if (traceFailures)
                    Trace.TraceWarning($"Unable to inspect ACL for '{normalizedDirectory}': {ex.Message}");
                return true;
            }
        }

        internal static bool IsPotentiallyUserWritableFile(string file)
        {
            return IsPotentiallyUserWritableFileCore(file, true);
        }

        private static bool IsPotentiallyUserWritableFileCore(string file, bool traceFailures)
        {
            var normalizedFile = NormalizePathFileCore(file, traceFailures);
            if (normalizedFile == null)
                return true;

            try
            {
                var security = File.GetAccessControl(normalizedFile);
                return IsPotentiallyUserWritableSecurityCore(security, traceFailures);
            }
            catch (Exception ex)
            {
                if (traceFailures)
                    Trace.TraceWarning($"Unable to inspect ACL for '{normalizedFile}': {ex.Message}");
                return true;
            }
        }

        internal static bool TryValidateTrustedFilePath(string file, string label, out string diagnostic)
        {
            return TryValidateTrustedFilePathCore(file, label, out diagnostic, true);
        }

        private static bool TryValidateTrustedFilePathCore(string file, string label, out string diagnostic,
            bool traceFailures)
        {
            diagnostic = null;
            var normalizedFile = NormalizePathFileCore(file, traceFailures);
            if (normalizedFile == null)
            {
                diagnostic = $"{label} path is invalid.";
                return false;
            }

            if (!TryGetExistingAttributes(normalizedFile, out var fileAttributes) ||
                (fileAttributes & FileAttributes.Directory) != 0)
            {
                diagnostic = $"{label} '{normalizedFile}' is not a regular file.";
                return false;
            }

            if ((fileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                diagnostic = $"{label} '{normalizedFile}' is a reparse point.";
                return false;
            }

            if (IsPotentiallyUserWritableFileCore(normalizedFile, traceFailures))
            {
                diagnostic =
                    $"{label} '{normalizedFile}' is writable by or owned by non-administrative users.";
                return false;
            }

            var directory = Path.GetDirectoryName(normalizedFile);
            var isContainingDirectory = true;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                if (!TryGetExistingAttributes(directory, out var directoryAttributes) ||
                    (directoryAttributes & FileAttributes.Directory) == 0)
                {
                    diagnostic = $"{label} ancestor '{directory}' is not an accessible directory.";
                    return false;
                }

                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostic = $"{label} ancestor '{directory}' is a reparse point.";
                    return false;
                }

                var unsafeDirectory = isContainingDirectory
                    ? IsPotentiallyUserWritableDirectoryCore(directory, traceFailures)
                    : IsPotentiallyUserReplaceableAncestorCore(directory, traceFailures);
                if (unsafeDirectory)
                {
                    diagnostic =
                        $"{label} ancestor '{directory}' can be replaced by or is owned by non-administrative users.";
                    return false;
                }

                var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Path.GetDirectoryName(trimmed);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                    break;

                directory = parent;
                isContainingDirectory = false;
            }

            return true;
        }

        private static bool IsPotentiallyUserReplaceableAncestor(string directory)
        {
            return IsPotentiallyUserReplaceableAncestorCore(directory, true);
        }

        private static bool IsPotentiallyUserReplaceableAncestorCore(string directory, bool traceFailures)
        {
            try
            {
                var security = Directory.GetAccessControl(directory);
                const FileSystemRights replacementRights =
                    FileSystemRights.Delete |
                    FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions |
                    FileSystemRights.TakeOwnership;

                return !HasTrustedOwnerCore(security, traceFailures) ||
                       ContainsPotentiallyUserWritableRule(
                           security.GetAccessRules(true, true, typeof(SecurityIdentifier)), replacementRights);
            }
            catch (Exception ex)
            {
                if (traceFailures)
                    Trace.TraceWarning($"Unable to inspect ancestor ACL for '{directory}': {ex.Message}");
                return true;
            }
        }

        private static bool IsPotentiallyUserWritableSecurity(FileSystemSecurity security)
        {
            return IsPotentiallyUserWritableSecurityCore(security, true);
        }

        private static bool IsPotentiallyUserWritableSecurityCore(FileSystemSecurity security, bool traceFailures)
        {
            return !HasTrustedOwnerCore(security, traceFailures) ||
                   ContainsPotentiallyUserWritableRule(
                       security.GetAccessRules(true, true, typeof(SecurityIdentifier)));
        }

        private static bool HasTrustedOwner(FileSystemSecurity security)
        {
            return HasTrustedOwnerCore(security, true);
        }

        private static bool HasTrustedOwnerCore(FileSystemSecurity security, bool traceFailures)
        {
            try
            {
                return security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner &&
                       IsTrustedOwnerSid(owner);
            }
            catch (Exception ex)
            {
                if (traceFailures)
                    Trace.TraceWarning($"Unable to inspect owner for a privileged application path: {ex.Message}");
                return false;
            }
        }

        private static bool ContainsPotentiallyUserWritableRule(AuthorizationRuleCollection rules)
        {
            const FileSystemRights writeRights =
                FileSystemRights.WriteData |
                FileSystemRights.CreateFiles |
                FileSystemRights.AppendData |
                FileSystemRights.CreateDirectories |
                FileSystemRights.WriteAttributes |
                FileSystemRights.WriteExtendedAttributes |
                FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership |
                FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles;

            return ContainsPotentiallyUserWritableRule(rules, writeRights);
        }

        private static bool ContainsPotentiallyUserWritableRule(AuthorizationRuleCollection rules,
            FileSystemRights writeRights)
        {
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow ||
                    !(rule.IdentityReference is SecurityIdentifier sid) ||
                    (rule.FileSystemRights & writeRights) == 0)
                    continue;

                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                    continue;

                if (sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid))
                    return true;

                if (!IsTrustedAdministrativeSid(sid))
                    return true;
            }

            return false;
        }

        internal static bool IsTrustedAdministrativeSid(SecurityIdentifier sid)
        {
            return sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                   sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                   string.Equals(
                       sid.Value,
                       "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
                       StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsTrustedOwnerSid(SecurityIdentifier sid)
        {
            if (IsTrustedAdministrativeSid(sid))
                return true;

            var accountDomainSid = sid.AccountDomainSid;
            if (accountDomainSid == null)
                return false;

            return sid.Equals(new SecurityIdentifier(
                       WellKnownSidType.AccountAdministratorSid,
                       accountDomainSid)) ||
                   sid.Equals(new SecurityIdentifier(
                       WellKnownSidType.AccountDomainAdminsSid,
                       accountDomainSid));
        }

        private static string NormalizePathDirectory(string directory)
        {
            return NormalizePathDirectoryCore(directory, true);
        }

        private static string NormalizePathDirectoryCore(string directory, bool traceFailures)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            try
            {
                return NormalizePathRoot(Path.GetFullPath(directory.Trim().Trim('"')));
            }
            catch (Exception ex)
            {
                if (traceFailures)
                    Trace.TraceWarning($"Skipping invalid PATH directory '{directory}': {ex.Message}");
                return null;
            }
        }

        private static string NormalizePathFile(string file)
        {
            return NormalizePathFileCore(file, true);
        }

        private static string NormalizePathFileCore(string file, bool traceFailures)
        {
            if (string.IsNullOrWhiteSpace(file))
                return null;

            try
            {
                return NormalizePathRoot(Path.GetFullPath(file.Trim().Trim('"')));
            }
            catch (Exception ex)
            {
                if (traceFailures)
                    Trace.TraceWarning($"Skipping invalid file path '{file}': {ex.Message}");
                return null;
            }
        }

        private static string NormalizePathRoot(string fullPath)
        {
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                return fullPath;

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

    }
}
