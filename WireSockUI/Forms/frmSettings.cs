using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32.TaskScheduler;
using WireSockUI.Properties;

namespace WireSockUI.Forms
{
    public partial class FrmSettings : Form
    {
        private readonly bool _initialAutoRun;

        public FrmSettings()
        {
            InitializeComponent();

            Icon = Resources.ico;

            _initialAutoRun = IsAutoRunEnabled();
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

        private static void DeleteLegacyStartupShortcutIfPresent()
        {
            try
            {
                var shortcutPath = GetLegacyStartupShortcutPath();
                if (File.Exists(shortcutPath))
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
        ///     This method uses the TaskService to find a task with the same name as the current application.
        ///     If such a task is found, it means that the auto-run feature is enabled.
        /// </remarks>
        private static bool IsAutoRunEnabled()
        {
            try
            {
                using (var ts = new TaskService())
                {
                    return ts.FindTask(GetAppName()) != null;
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
        ///     This method creates a new task in the Task Scheduler with the same name as the current application.
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

                    ts.RootFolder.RegisterTaskDefinition(GetAppName(), td);
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
        ///     This method uses the TaskService to delete a task with the same name as the current application.
        ///     If such a task is found and deleted, it means that the auto-run feature is disabled.
        ///     If an error occurs while disabling auto-run, an error message is displayed.
        /// </remarks>
        private static bool DisableAutoRun()
        {
            try
            {
                using (var ts = new TaskService())
                {
                    if (ts.FindTask(GetAppName()) != null)
                        ts.RootFolder.DeleteTask(GetAppName(), false);
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

        private static void ShowSettingsError(string messageFormat, Exception ex)
        {
            MessageBox.Show(string.Format(messageFormat, ex.Message), Resources.TunnelErrorTitle, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            if (_initialAutoRun != chkAutorun.Checked)
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
            Settings.Default.EnableKillSwitch = chkEnableKillSwitch.Checked;
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
