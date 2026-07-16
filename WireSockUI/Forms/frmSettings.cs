using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32.TaskScheduler;
using WireSockUI.Config;
using WireSockUI.Native;
using WireSockUI.Properties;

namespace WireSockUI.Forms
{
    public partial class FrmSettings : Form
    {
        private const long MaxLegacyStartupShortcutSizeBytes = 1024 * 1024;
        private readonly bool _initialAutoRun;
        private readonly bool _initialAutoRunUsesPathScopedTask;

        public FrmSettings()
        {
            InitializeComponent();

            Icon = Resources.ico;

            _initialAutoRun = IsAutoRunEnabled(out _initialAutoRunUsesPathScopedTask);
            chkAutorun.Checked = _initialAutoRun;
            chkAutoMinimize.Checked = Settings.Default.AutoMinimize;
            chkAutoConnect.Checked = Settings.Default.AutoConnect;
            chkAutoUpdate.Checked = Settings.Default.AutoUpdate;
            chkUseAdapter.Checked = Settings.Default.UseAdapter;
            chkNotify.Checked = Settings.Default.EnableNotifications;
            chkEnableKillSwitch.Checked = Settings.Default.EnableKillSwitch;
            ddlLogLevel.SelectedItem = Settings.Default.LogLevel;
            if (ddlLogLevel.SelectedItem == null)
                ddlLogLevel.SelectedItem = "Error";
        }

        public bool RequestedEnableKillSwitch => chkEnableKillSwitch.Checked;
        public string RequestedLogLevel => ddlLogLevel.SelectedItem as string ?? "Error";

        internal ApplicationSettingsSnapshot RequestedSettings => new ApplicationSettingsSnapshot(
            chkAutorun.Checked,
            chkAutoConnect.Checked,
            chkAutoMinimize.Checked,
            chkAutoUpdate.Checked,
            chkUseAdapter.Checked,
            chkNotify.Checked,
            RequestedEnableKillSwitch,
            RequestedLogLevel);

        private void OnProfilesFolderClick(object sender, EventArgs e)
        {
            try
            {
                var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrWhiteSpace(windowsDirectory))
                    throw new DirectoryNotFoundException("The Windows installation directory is unavailable.");

                var explorerPath = Path.Combine(windowsDirectory, "explorer.exe");
                if (!File.Exists(explorerPath))
                    throw new FileNotFoundException("Windows Explorer was not found.", explorerPath);

                Process.Start(explorerPath, $"\"{Global.ConfigsFolder}\"");
            }
            catch (Exception ex)
            {
                ShowSettingsError(Resources.SettingsProfilesFolderError, ex);
            }
        }

        private static string GetAppName()
        {
            return Assembly.GetExecutingAssembly().GetName().Name;
        }

        private static string GetLegacyStartupShortcutPath()
        {
            var startupFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return Path.Combine(startupFolderPath, $"{GetAppName()}.lnk");
        }

        private static string GetAutoRunTaskName()
        {
            return BuildAutoRunTaskName(Application.ExecutablePath);
        }

        private static string GetLegacyAutoRunTaskName()
        {
            return GetAppName();
        }

        private static string BuildAutoRunTaskName(string executablePath)
        {
            return $"{GetAppName()}-{WindowsApplicationContext.BuildPathSeed(executablePath)}";
        }

        private static void DeleteLegacyStartupShortcutIfPresent()
        {
            try
            {
                var shortcutPath = GetLegacyStartupShortcutPath();
                if (!File.Exists(shortcutPath))
                    return;

                var snapshotPath = Path.Combine(Global.SecureMainFolder,
                    $"legacy-startup-shortcut-{Guid.NewGuid():N}.lnk");
                try
                {
                    using (var shortcutFile = SecureFileSystem.OpenFileForReadAndDelete(shortcutPath))
                    {
                        shortcutFile.CopyToNewFile(snapshotPath, MaxLegacyStartupShortcutSizeBytes);
                        using (var shortcut = new ShellLink(snapshotPath))
                        {
                            if (!IsSameExecutablePath(shortcut.TargetPath, Application.ExecutablePath))
                            {
                                Trace.TraceWarning(
                                    $"Skipping legacy Startup shortcut '{shortcutPath}' because it points to a different executable.");
                                return;
                            }
                        }

                        shortcutFile.Delete();
                    }
                }
                finally
                {
                    TryDeleteShortcutSnapshot(snapshotPath);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to delete legacy Startup shortcut: {ex.Message}");
            }
        }

        private static void TryDeleteShortcutSnapshot(string snapshotPath)
        {
            try
            {
                if (!File.Exists(snapshotPath))
                    return;

                using (var snapshot = SecureFileSystem.OpenFileForDelete(snapshotPath))
                    snapshot.Delete();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to delete temporary legacy Startup shortcut snapshot: {ex.Message}");
            }
        }

        /// <summary>
        ///     Checks if the auto-run feature is enabled for the current application.
        /// </summary>
        /// <returns>
        ///     Returns true if the auto-run feature is enabled, otherwise false.
        /// </returns>
        /// <remarks>
        ///     This method uses a path-seeded task name and verifies that the task points to the current executable.
        /// </remarks>
        private static bool IsAutoRunEnabled(out bool usesPathScopedTask)
        {
            usesPathScopedTask = false;

            try
            {
                using (var ts = new TaskService())
                {
                    var pathScopedTaskEnabled = false;
                    using (var pathScopedTask = ts.FindTask(GetAutoRunTaskName()))
                    {
                        if (pathScopedTask != null && pathScopedTask.Enabled &&
                            IsTaskReplaceableByCurrentExecutable(pathScopedTask))
                        {
                            pathScopedTaskEnabled = true;
                            usesPathScopedTask = IsTaskDefinitionOwnedByExecutable(
                                pathScopedTask.Definition, true, Application.ExecutablePath);
                        }
                    }

                    var legacyTaskEnabled = false;
                    using (var legacyTask = ts.FindTask(GetLegacyAutoRunTaskName()))
                        legacyTaskEnabled = legacyTask != null && legacyTask.Enabled &&
                                            IsTaskReplaceableByCurrentExecutable(legacyTask);

                    usesPathScopedTask = usesPathScopedTask && !legacyTaskEnabled;
                    return pathScopedTaskEnabled || legacyTaskEnabled;
                }
            }
            catch (Exception ex)
            {
                ShowSettingsError(Resources.SettingsAutoRunCheckAdminError, ex);
                return false;
            }
        }

        /// <summary>
        ///     Enables the auto-run feature for the current application with administrative privileges.
        /// </summary>
        /// <remarks>
        ///     This method creates a new path-scoped task in the Task Scheduler.
        ///     The task is configured to run with the highest privileges and to trigger on logon.
        ///     The task action is set to the path of the current executable.
        ///     The task is also configured to run even if the computer is running on batteries, to not stop if the computer
        ///     switches to battery power, to wake the computer if needed, and to not stop when the computer ceases to be idle.
        ///     If an error occurs while enabling auto-run, a contextual exception is propagated to the settings transaction.
        /// </remarks>
        private static void EnableAutoRun()
        {
            try
            {
                using (var ts = new TaskService())
                using (var td = ts.NewTask())
                {
                    td.RegistrationInfo.Description = "Auto start for " + GetAppName();

                    var currentUserId = GetCurrentUserId();
                    td.Principal.UserId = currentUserId;
                    td.Principal.LogonType = TaskLogonType.InteractiveToken;
                    td.Principal.RunLevel = TaskRunLevel.Highest; // Run with the highest privileges

                    td.Triggers.Add(new LogonTrigger { UserId = currentUserId }); // Trigger for this user only

                    var appPath = Application.ExecutablePath;
                    if (!IsExecutablePathTrustedForAutoRun(appPath, out var trustDiagnostic))
                        throw new InvalidOperationException(trustDiagnostic);

                    td.Actions.Add(new ExecAction(appPath)); // Path to the executable

                    // Set power and idle options
                    td.Settings.DisallowStartIfOnBatteries =
                        false; // Allow the task to start if the computer is running on batteries
                    td.Settings.StopIfGoingOnBatteries =
                        false; // Do not stop the task if the computer switches to battery power
                    td.Settings.WakeToRun = true; // Allow the task to wake the computer if needed
                    td.Settings.ExecutionTimeLimit = TimeSpan.Zero; // The VPN must not be terminated after 72 hours
                    td.Settings.IdleSettings.StopOnIdleEnd =
                        false; // Do not stop the task when the computer ceases to be idle

                    if (!IsExecutablePathTrustedForAutoRun(appPath, out trustDiagnostic))
                        throw new InvalidOperationException(trustDiagnostic);

                    EnsureAutoRunTaskCanBeReplaced(ts, GetAutoRunTaskName());
                    ts.RootFolder.RegisterTaskDefinition(GetAutoRunTaskName(), td);
                    TryDeleteAutoRunTaskIfReplaceable(ts, GetLegacyAutoRunTaskName());
                }

                DeleteLegacyStartupShortcutIfPresent();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    string.Format(Resources.SettingsAutoRunEnableAdminError, ex.Message), ex);
            }
        }

        /// <summary>
        ///     Disables the auto-run feature for the current application with administrative privileges.
        /// </summary>
        /// <remarks>
        ///     This method deletes only tasks that point to the current executable.
        ///     If an error occurs while disabling auto-run, a contextual exception is propagated to the settings transaction.
        /// </remarks>
        private static void DisableAutoRun()
        {
            try
            {
                using (var ts = new TaskService())
                {
                    DeleteAutoRunTaskIfReplaceable(ts, GetAutoRunTaskName());
                    DeleteAutoRunTaskIfReplaceable(ts, GetLegacyAutoRunTaskName());
                }

                DeleteLegacyStartupShortcutIfPresent();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    string.Format(Resources.SettingsAutoRunDisableAdminError, ex.Message), ex);
            }
        }

        private static bool IsExecutablePathTrustedForAutoRun(string executablePath, out string diagnostic)
        {
            diagnostic = null;

            try
            {
                var fullPath = Path.GetFullPath((executablePath ?? string.Empty).Trim().Trim('"'));
                if (!File.Exists(fullPath))
                {
                    diagnostic = $"Autorun executable '{fullPath}' does not exist.";
                    return false;
                }

                if (!IsPathFreeOfReparsePoints(fullPath, out diagnostic))
                    return false;

                if (!Program.TryValidateTrustedFilePath(fullPath, "Autorun executable", out diagnostic))
                {
                    diagnostic += " Install WireSock UI into an administrator-owned folder before enabling elevated autorun.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                diagnostic = $"Autorun executable path could not be validated: {ex.Message}";
                return false;
            }
        }

        private static bool IsPathFreeOfReparsePoints(string fullPath, out string diagnostic)
        {
            if (IsReparsePointOrUnreadable(fullPath, "Autorun executable", out diagnostic))
                return false;

            var directory = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrWhiteSpace(directory))
            {
                if (IsReparsePointOrUnreadable(directory, "Autorun executable folder", out diagnostic))
                    return false;

                var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Path.GetDirectoryName(trimmed);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                    break;

                directory = parent;
            }

            diagnostic = null;
            return true;
        }

        private static bool IsReparsePointOrUnreadable(string path, string label, out string diagnostic)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostic =
                        $"{label} '{path}' is a reparse point. Install WireSock UI into a real administrator-owned folder before enabling elevated autorun.";
                    return true;
                }
            }
            catch (Exception ex)
            {
                diagnostic = $"{label} '{path}' could not be inspected for reparse points: {ex.Message}";
                return true;
            }

            diagnostic = null;
            return false;
        }

        private static void DeleteAutoRunTaskIfReplaceable(TaskService ts, string taskName)
        {
            using (var task = ts.FindTask(taskName))
            {
                if (task == null)
                    return;

                if (!IsTaskReplaceableByCurrentExecutable(task))
                {
                    Trace.TraceWarning(
                        $"Skipping autorun task '{taskName}' because its complete definition is not replaceable by this WireSock UI installation.");
                    return;
                }
            }

            ts.RootFolder.DeleteTask(taskName, false);
        }

        private static void TryDeleteAutoRunTaskIfReplaceable(TaskService ts, string taskName)
        {
            try
            {
                DeleteAutoRunTaskIfReplaceable(ts, taskName);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to delete legacy autorun task '{taskName}': {ex}");
            }
        }

        private static void EnsureAutoRunTaskCanBeReplaced(TaskService taskService, string taskName)
        {
            using (var existingTask = taskService.FindTask(taskName))
            {
                if (existingTask != null && !IsTaskReplaceableByCurrentExecutable(existingTask))
                    throw new InvalidOperationException(
                        $"Autorun task '{taskName}' already exists with a definition that this WireSock UI installation cannot safely replace.");
            }
        }

        private static bool IsTaskReplaceableByCurrentExecutable(Microsoft.Win32.TaskScheduler.Task task)
        {
            return task != null &&
                   IsTaskDefinitionReplaceableByExecutable(task.Definition, Application.ExecutablePath);
        }

        internal static bool IsTaskDefinitionOwnedByExecutable(TaskDefinition definition, bool taskEnabled,
            string executablePath)
        {
            return taskEnabled && IsTaskDefinitionOwnedByExecutable(definition, executablePath);
        }

        private static bool IsTaskDefinitionOwnedByExecutable(TaskDefinition definition, string executablePath)
        {
            if (!IsTaskDefinitionReplaceableByExecutable(definition, executablePath) ||
                definition.Settings == null || definition.Settings.ExecutionTimeLimit != TimeSpan.Zero ||
                definition.Principal.LogonType != TaskLogonType.InteractiveToken)
                return false;

            var currentUserId = GetCurrentUserId();
            if (!IsSameTaskUser(definition.Principal.UserId, currentUserId))
                return false;

            var logonTrigger = (LogonTrigger)definition.Triggers[0];
            return IsSameTaskUser(logonTrigger.UserId, currentUserId);
        }

        internal static bool IsTaskDefinitionReplaceableByExecutable(TaskDefinition definition,
            string executablePath)
        {
            if (definition?.Actions == null || definition.Actions.Count != 1 ||
                definition.Triggers == null || definition.Triggers.Count != 1 ||
                definition.Principal == null || definition.Principal.RunLevel != TaskRunLevel.Highest)
                return false;

            var execAction = definition.Actions[0] as ExecAction;
            if (execAction == null || !string.IsNullOrWhiteSpace(execAction.Arguments) ||
                !string.IsNullOrWhiteSpace(execAction.WorkingDirectory) ||
                !IsSameExecutablePath(execAction.Path, executablePath))
                return false;

            if (!(definition.Triggers[0] is LogonTrigger logonTrigger) || !logonTrigger.Enabled)
                return false;

            var currentUserId = GetCurrentUserId();
            return IsTaskUserReplaceable(definition.Principal.UserId, currentUserId) &&
                   IsTaskUserReplaceable(logonTrigger.UserId, currentUserId);
        }

        private static string GetCurrentUserId()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User == null)
                    throw new InvalidOperationException("The current Windows user SID is unavailable.");

                return identity.User.Value;
            }
        }

        private static bool IsTaskUserReplaceable(string taskUserId, string currentUserId)
        {
            return string.IsNullOrWhiteSpace(taskUserId) || IsSameTaskUser(taskUserId, currentUserId);
        }

        internal static bool IsSameTaskUser(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                return false;

            var firstResolved = TryGetSecurityIdentifier(first, out var firstSid);
            var secondResolved = TryGetSecurityIdentifier(second, out var secondSid);
            if (firstResolved && secondResolved)
                return firstSid.Equals(secondSid);

            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetSecurityIdentifier(string identity, out SecurityIdentifier sid)
        {
            sid = null;
            try
            {
                sid = GetSecurityIdentifier(identity);
                return sid != null;
            }
            catch (IdentityNotMappedException)
            {
                return false;
            }
            catch (SystemException)
            {
                return false;
            }
        }

        private static SecurityIdentifier GetSecurityIdentifier(string identity)
        {
            try
            {
                return new SecurityIdentifier(identity);
            }
            catch (ArgumentException)
            {
                return new NTAccount(identity).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier;
            }
        }

        private static bool IsSameExecutablePath(string first, string second)
        {
            try
            {
                return string.Equals(NormalizeExecutablePath(first), NormalizeExecutablePath(second),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to compare autorun executable paths: {ex.Message}");
                return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string NormalizeExecutablePath(string path)
        {
            var trimmedPath = (path ?? string.Empty).Trim().Trim('"');
            return string.IsNullOrEmpty(trimmedPath) ? string.Empty : Path.GetFullPath(trimmedPath);
        }

        private static void ShowSettingsError(string messageFormat, Exception ex)
        {
            MessageBox.Show(string.Format(messageFormat, ex.Message), Resources.TunnelErrorTitle, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        internal bool ApplyAutoRunChange()
        {
            var autoRunChanged = _initialAutoRun != chkAutorun.Checked ||
                                 chkAutorun.Checked && !_initialAutoRunUsesPathScopedTask;
            if (!autoRunChanged)
                return true;

            if (chkAutorun.Checked)
                EnableAutoRun();
            else
                DisableAutoRun();

            return true;
        }

        internal bool RollbackAutoRunChange()
        {
            var autoRunChanged = _initialAutoRun != chkAutorun.Checked ||
                                 chkAutorun.Checked && !_initialAutoRunUsesPathScopedTask;
            if (!autoRunChanged)
                return true;

            if (_initialAutoRun)
                EnableAutoRun();
            else
                DisableAutoRun();

            return true;
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
