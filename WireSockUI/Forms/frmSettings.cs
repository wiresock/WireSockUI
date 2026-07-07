using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32.TaskScheduler;
using WireSockUI.Native;
using WireSockUI.Properties;

namespace WireSockUI.Forms
{
    public partial class FrmSettings : Form
    {
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

        private void OnProfilesFolderClick(object sender, EventArgs e)
        {
            try
            {
                Process.Start("explorer.exe", Global.ConfigsFolder);
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

                if (!IsShortcutOwnedByCurrentExecutable(shortcutPath))
                {
                    Trace.TraceWarning(
                        $"Skipping legacy Startup shortcut '{shortcutPath}' because it points to a different executable.");
                    return;
                }

                File.Delete(shortcutPath);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to delete legacy Startup shortcut: {ex.Message}");
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
                    if (IsTaskOwnedByCurrentExecutable(ts.FindTask(GetAutoRunTaskName())))
                    {
                        usesPathScopedTask = true;
                        return true;
                    }

                    return IsTaskOwnedByCurrentExecutable(ts.FindTask(GetLegacyAutoRunTaskName()));
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
        ///     If an error occurs while enabling auto-run, an error message is displayed.
        /// </remarks>
        private static bool EnableAutoRun()
        {
            try
            {
                using (var ts = new TaskService())
                {
                    var td = ts.NewTask();
                    td.RegistrationInfo.Description = "Auto start for " + GetAppName();

                    td.Principal.RunLevel = TaskRunLevel.Highest; // Run with the highest privileges

                    td.Triggers.Add(new LogonTrigger()); // Trigger on logon

                    var appPath = Application.ExecutablePath;
                    td.Actions.Add(new ExecAction(appPath)); // Path to the executable

                    // Set power and idle options
                    td.Settings.DisallowStartIfOnBatteries =
                        false; // Allow the task to start if the computer is running on batteries
                    td.Settings.StopIfGoingOnBatteries =
                        false; // Do not stop the task if the computer switches to battery power
                    td.Settings.WakeToRun = true; // Allow the task to wake the computer if needed
                    td.Settings.IdleSettings.StopOnIdleEnd =
                        false; // Do not stop the task when the computer ceases to be idle

                    ts.RootFolder.RegisterTaskDefinition(GetAutoRunTaskName(), td);
                    TryDeleteAutoRunTaskIfOwned(ts, GetLegacyAutoRunTaskName());
                }

                DeleteLegacyStartupShortcutIfPresent();
                return true;
            }
            catch (Exception ex)
            {
                ShowSettingsError(Resources.SettingsAutoRunEnableAdminError, ex);
                return false;
            }
        }

        /// <summary>
        ///     Disables the auto-run feature for the current application with administrative privileges.
        /// </summary>
        /// <remarks>
        ///     This method deletes only tasks that point to the current executable.
        ///     If an error occurs while disabling auto-run, an error message is displayed.
        /// </remarks>
        private static bool DisableAutoRun()
        {
            try
            {
                using (var ts = new TaskService())
                {
                    DeleteAutoRunTaskIfOwned(ts, GetAutoRunTaskName());
                    DeleteAutoRunTaskIfOwned(ts, GetLegacyAutoRunTaskName());
                }

                DeleteLegacyStartupShortcutIfPresent();
                return true;
            }
            catch (Exception ex)
            {
                ShowSettingsError(Resources.SettingsAutoRunDisableAdminError, ex);
                return false;
            }
        }

        private static void DeleteAutoRunTaskIfOwned(TaskService ts, string taskName)
        {
            var task = ts.FindTask(taskName);
            if (task == null)
                return;

            if (!IsTaskOwnedByCurrentExecutable(task))
            {
                Trace.TraceWarning(
                    $"Skipping autorun task '{taskName}' because it points to a different executable.");
                return;
            }

            ts.RootFolder.DeleteTask(taskName, false);
        }

        private static void TryDeleteAutoRunTaskIfOwned(TaskService ts, string taskName)
        {
            try
            {
                DeleteAutoRunTaskIfOwned(ts, taskName);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to delete legacy autorun task '{taskName}': {ex}");
            }
        }

        private static bool IsTaskOwnedByCurrentExecutable(Microsoft.Win32.TaskScheduler.Task task)
        {
            if (task?.Definition?.Actions == null)
                return false;

            foreach (var action in task.Definition.Actions)
            {
                var execAction = action as ExecAction;
                if (execAction != null && IsSameExecutablePath(execAction.Path, Application.ExecutablePath))
                    return true;
            }

            return false;
        }

        private static bool IsShortcutOwnedByCurrentExecutable(string shortcutPath)
        {
            using (var shortcut = new ShellLink(shortcutPath))
            {
                return IsSameExecutablePath(shortcut.TargetPath, Application.ExecutablePath);
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

        private void OnSaveClick(object sender, EventArgs e)
        {
            if (_initialAutoRun != chkAutorun.Checked ||
                (chkAutorun.Checked && !_initialAutoRunUsesPathScopedTask))
            {
                var autoRunUpdated = chkAutorun.Checked ? EnableAutoRun() : DisableAutoRun();
                if (!autoRunUpdated)
                {
                    DialogResult = DialogResult.None;
                    return;
                }
            }

            Settings.Default.AutoRun = chkAutorun.Checked;
            Settings.Default.AutoConnect = chkAutoConnect.Checked;
            Settings.Default.AutoMinimize = chkAutoMinimize.Checked;
            Settings.Default.AutoUpdate = chkAutoUpdate.Checked;
            Settings.Default.UseAdapter = chkUseAdapter.Checked;
            Settings.Default.EnableNotifications = chkNotify.Checked;
            Settings.Default.LogLevel = ddlLogLevel.SelectedItem as string;

            try
            {
                Settings.Default.Save();
            }
            catch (Exception ex)
            {
                ShowSettingsError(Resources.SettingsSaveError, ex);
                DialogResult = DialogResult.None;
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
