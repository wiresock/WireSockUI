using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32.TaskScheduler;
using WireSockUI.Native;
using WireSockUI.Properties;

namespace WireSockUI.Forms
{
    public partial class FrmSettings : Form
    {
        public FrmSettings()
        {
            InitializeComponent();

            Icon = Resources.ico;

            chkAutorun.Checked = Settings.Default.AutoRun;
            chkAutoMinimize.Checked = Settings.Default.AutoMinimize;
            chkAutoConnect.Checked = Settings.Default.AutoConnect;
            chkAutoUpdate.Checked = Settings.Default.AutoUpdate;
            chkUseAdapter.Checked = Settings.Default.UseAdapter;
            chkNotify.Checked = Settings.Default.EnableNotifications;
            chkDisableAutoAdmin.Checked = Settings.Default.DisableAutoAdmin;
            chkEnableKillSwitch.Checked = Settings.Default.EnableKillSwitch;
            ddlLogLevel.SelectedItem = Settings.Default.LogLevel;
            if (ddlLogLevel.SelectedItem == null)
                ddlLogLevel.SelectedItem = "Error";

            if (!IsCurrentProcessElevated())
            {
                chkUseAdapter.Enabled = false;
                chkUseAdapter.Checked = false;
                chkEnableKillSwitch.Enabled = false;
                chkEnableKillSwitch.Checked = false;

                // If autorun is enabled for admin users while we are not an admin, disable the checkbox
                if (chkAutorun.Checked && !IsAutoRunForNonAdminEnabled())
                {
                    chkAutorun.Enabled = false;
                    return;
                }
            }

            chkAutorun.Checked =
                IsCurrentProcessElevated() ? IsAutoRunForAdminEnabled() : IsAutoRunForNonAdminEnabled();
            Settings.Default.AutoRun = chkAutorun.Checked;
        }

        private static bool IsCurrentProcessElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private void OnProfilesFolderClick(object sender, EventArgs e)
        {
            Process.Start("explorer.exe", Global.MainFolder);
        }

        private static string GetAppName()
        {
            return Assembly.GetExecutingAssembly().GetName().Name;
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
        private static bool IsAutoRunForAdminEnabled()
        {
            using (var ts = new TaskService())
            {
                return ts.FindTask(GetAppName()) != null;
            }
        }

        /// <summary>
        ///     Checks if the auto-run feature is enabled for the current application for non-admin users.
        /// </summary>
        /// <returns>
        ///     Returns true if the auto-run feature is enabled, otherwise false.
        /// </returns>
        /// <remarks>
        ///     This method checks if a shortcut to the application exists in the Startup folder.
        ///     If such a shortcut is found, it means that the auto-run feature is enabled for non-admin users.
        /// </remarks>
        private static bool IsAutoRunForNonAdminEnabled()
        {
            try
            {
                var startupFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var shortcutPath = Path.Combine(startupFolderPath, $"{GetAppName()}.lnk");

                return File.Exists(shortcutPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking auto-run status: {ex.Message}", "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
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
        private static void EnableAutoRunForAdmin()
        {
            try
            {
                using (var ts = new TaskService())
                {
                    var td = ts.NewTask();
                    td.RegistrationInfo.Description = "Auto start for " + GetAppName();

                    td.Principal.RunLevel = TaskRunLevel.Highest; // Run with the highest privileges

                    td.Triggers.Add(new LogonTrigger()); // Trigger on logon

                    var appPath = Assembly.GetExecutingAssembly().Location;
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
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error enabling autorun: " + ex.Message, "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
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
        private static void DisableAutoRunForAdmin()
        {
            try
            {
                using (var ts = new TaskService())
                {
                    ts.RootFolder.DeleteTask(GetAppName(), false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error disabling autorun: {ex.Message}", "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        ///     Enables the auto-run feature for the current application for non-admin users.
        /// </summary>
        /// <remarks>
        ///     This method creates a shortcut to the application in the Startup folder.
        ///     The shortcut is created using the ShellLink class and is saved to the Startup folder.
        ///     If the shortcut is created successfully, a success message is displayed.
        ///     If an error occurs while enabling auto-run, an error message is displayed.
        /// </remarks>
        private static void EnableAutoRunForNonAdmin()
        {
            try
            {
                var appPath = Application.ExecutablePath;
                var startupFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var shortcutPath = Path.Combine(startupFolderPath, GetAppName() + ".lnk");

                using (var link = new ShellLink())
                {
                    link.TargetPath = appPath;
                    link.WorkingDirectory = AppContext.BaseDirectory;

                    link.Save(shortcutPath);
                }

                //MessageBox.Show("Auto-run enabled successfully.", "Success", MessageBoxButtons.OK,
                //    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error enabling auto-run: {ex.Message}", "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        ///     Disables the auto-run feature for the current application for non-admin users.
        /// </summary>
        /// <remarks>
        ///     This method deletes the shortcut to the application in the Startup folder.
        ///     If the shortcut is found and deleted, it means that the auto-run feature is disabled for non-admin users.
        ///     If the shortcut is not found, an info message is displayed.
        ///     If an error occurs while disabling auto-run, an error message is displayed.
        /// </remarks>
        private static void DisableAutoRunForNonAdmin()
        {
            try
            {
                var startupFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var shortcutPath = Path.Combine(startupFolderPath, GetAppName() + ".lnk");

                if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                //MessageBox.Show("Auto-run disabled successfully.", "Success", MessageBoxButtons.OK,
                //    MessageBoxIcon.Information);
                //else
                //{
                //    MessageBox.Show("Auto-run shortcut not found.", "Info", MessageBoxButtons.OK,
                //        MessageBoxIcon.Information);
                //}
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error disabling auto-run: {ex.Message}", "Error", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            if (Settings.Default.AutoRun != chkAutorun.Checked)
            {
                if (!chkAutorun.Checked)
                {
                    if (IsCurrentProcessElevated())
                    {
                        DisableAutoRunForAdmin();

                        // Under Administrator ensure that non-admin AutoRun is also disabled
                        if (IsAutoRunForNonAdminEnabled())
                            DisableAutoRunForNonAdmin();
                    }
                    else
                    {
                        DisableAutoRunForNonAdmin();
                    }
                }
                else
                {
                    if (IsCurrentProcessElevated())
                    {
                        EnableAutoRunForAdmin();

                        // Under Administrator ensure that non-admin AutoRun is disabled
                        if (IsAutoRunForNonAdminEnabled())
                            DisableAutoRunForNonAdmin();
                    }
                    else
                    {
                        EnableAutoRunForNonAdmin();
                    }
                }

                Settings.Default.AutoRun = chkAutorun.Checked;
            }

            Settings.Default.AutoConnect = chkAutoConnect.Checked;
            Settings.Default.AutoMinimize = chkAutoMinimize.Checked;
            Settings.Default.AutoUpdate = chkAutoUpdate.Checked;
            Settings.Default.UseAdapter = chkUseAdapter.Checked;
            Settings.Default.EnableNotifications = chkNotify.Checked;
            Settings.Default.DisableAutoAdmin = chkDisableAutoAdmin.Checked;
            Settings.Default.EnableKillSwitch = chkEnableKillSwitch.Checked;
            Settings.Default.LogLevel = ddlLogLevel.SelectedItem as string;

            Settings.Default.Save();

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
