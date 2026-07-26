using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.TaskScheduler;
using WireSockUI.Config;
using WireSockUI.Native;
using WireSockUI.Properties;

namespace WireSockUI.Forms
{
    public partial class FrmSettings : Form
    {
        private const int AutoRunInspectionTimeoutMilliseconds = 5000;
        // TaskScheduler maps BelowNormal to the native Task Scheduler 2.0 priority value 7.
        internal const ProcessPriorityClass AutoRunTaskPriorityClass = ProcessPriorityClass.BelowNormal;
        internal const string AutoRunTaskSecurityDescriptorSddl =
            "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)";
        private static readonly SemaphoreSlim AutoRunOperationGate = new SemaphoreSlim(1, 1);
        private AutoRunStatus _initialAutoRunStatus;
        private bool _initialAutoRunUsesPathScopedTask;
        private bool _hasUnverifiedLegacyShortcut;
        private bool _legacyShortcutMigrationApproved;
        private string _legacyStartupShortcutPath;
        private System.Threading.Tasks.Task<AutoRunInspection> _autoRunInspectionTask;

        internal enum AutoRunStatus
        {
            Unknown,
            Disabled,
            Enabled,
            LegacyEnabled,
            LegacyShortcutMigrationRequired,
            Conflict
        }

        internal enum LegacyStartupShortcutStatus
        {
            Absent,
            Unverified,
            Foreign,
            Unknown
        }

        private sealed class AutoRunInspection
        {
            internal AutoRunInspection(
                AutoRunStatus status,
                bool usesPathScopedTask,
                bool hasUnverifiedLegacyShortcut,
                string legacyStartupShortcutPath,
                string diagnostic = null)
            {
                Status = status;
                UsesPathScopedTask = usesPathScopedTask;
                HasUnverifiedLegacyShortcut = hasUnverifiedLegacyShortcut;
                LegacyStartupShortcutPath = legacyStartupShortcutPath;
                Diagnostic = diagnostic;
            }

            internal AutoRunStatus Status { get; }
            internal bool UsesPathScopedTask { get; }
            internal bool HasUnverifiedLegacyShortcut { get; }
            internal string LegacyStartupShortcutPath { get; }
            internal string Diagnostic { get; }
        }

        private sealed class AutoRunTaskInspection
        {
            internal bool EnabledForCurrentExecutable { get; set; }
            internal bool Canonical { get; set; }
            internal bool Conflict { get; set; }
        }

        public FrmSettings()
        {
            InitializeComponent();

            Icon = Resources.ico;

            _initialAutoRunStatus = AutoRunStatus.Unknown;
            chkAutorun.Checked = Settings.Default.AutoRun;
            chkAutorun.Enabled = false;
            btnSave.Enabled = false;
            chkAutoMinimize.Checked = Settings.Default.AutoMinimize;
            chkAutoConnect.Checked = PrivilegedSettingsStore.AutoConnect;
            chkAutoUpdate.Checked = Settings.Default.AutoUpdate;
            chkUseAdapter.Checked = PrivilegedSettingsStore.UseAdapter;
            chkNotify.Checked = Settings.Default.EnableNotifications;
            chkEnableKillSwitch.Checked = PrivilegedSettingsStore.EnableKillSwitch;
            ddlLogLevel.SelectedItem = Settings.Default.LogLevel;
            if (ddlLogLevel.SelectedItem == null)
                ddlLogLevel.SelectedItem = "Error";

            Shown += OnSettingsShown;
        }

        public bool RequestedEnableKillSwitch => chkEnableKillSwitch.Checked;
        public string RequestedLogLevel => ddlLogLevel.SelectedItem as string ?? "Error";

        internal ApplicationSettingsSnapshot RequestedSettings => new ApplicationSettingsSnapshot(
            GetRequestedAutoRun(),
            chkAutoConnect.Checked,
            chkAutoMinimize.Checked,
            chkAutoUpdate.Checked,
            chkUseAdapter.Checked,
            chkNotify.Checked,
            RequestedEnableKillSwitch,
            RequestedLogLevel);

        private async void OnSettingsShown(object sender, EventArgs e)
        {
            Shown -= OnSettingsShown;
            _autoRunInspectionTask = RunSerializedAutoRunOperationAsync(InspectAutoRun);

            try
            {
                var completedTask = await System.Threading.Tasks.Task.WhenAny(
                    _autoRunInspectionTask,
                    System.Threading.Tasks.Task.Delay(AutoRunInspectionTimeoutMilliseconds));
                if (!ReferenceEquals(completedTask, _autoRunInspectionTask))
                {
                    ObserveLateAutoRunInspectionFailure(_autoRunInspectionTask);
                    throw new TimeoutException(
                        $"Autorun inspection did not complete within {AutoRunInspectionTimeoutMilliseconds} ms.");
                }

                var inspection = await _autoRunInspectionTask;
                if (IsDisposed || Disposing)
                    return;

                _initialAutoRunStatus = inspection.Status;
                _initialAutoRunUsesPathScopedTask = inspection.UsesPathScopedTask;
                _hasUnverifiedLegacyShortcut = inspection.HasUnverifiedLegacyShortcut;
                _legacyStartupShortcutPath = inspection.LegacyStartupShortcutPath;
                chkAutorun.Checked = ResolveRequestedAutoRun(
                    _initialAutoRunStatus,
                    IsEnabledAutoRunStatus(_initialAutoRunStatus),
                    Settings.Default.AutoRun);
                chkAutorun.Enabled = IsKnownAutoRunStatus(_initialAutoRunStatus);

                if (_initialAutoRunStatus == AutoRunStatus.Conflict)
                    ShowSettingsError(
                        Resources.SettingsAutoRunCheckAdminError,
                        new InvalidOperationException(inspection.Diagnostic ??
                                                      "Conflicting autorun entries were found."));
                else if (_initialAutoRunStatus == AutoRunStatus.Unknown)
                    ShowSettingsError(
                        Resources.SettingsAutoRunCheckAdminError,
                        new IOException(inspection.Diagnostic ??
                                        "Autorun status could not be determined. Autorun was left unchanged."));
                else if (_hasUnverifiedLegacyShortcut)
                    MessageBox.Show(
                        inspection.Diagnostic ??
                        "An unauthenticated legacy Startup shortcut was found. It is not treated as consent to enable elevated autorun.",
                        Resources.TunnelErrorTitle,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    ShowSettingsError(Resources.SettingsAutoRunCheckAdminError, ex);
                else
                    Trace.TraceWarning($"Failed to inspect autorun settings after the settings window closed: {ex}");
            }
            finally
            {
                if (!IsDisposed && !Disposing)
                    btnSave.Enabled = true;
            }
        }

        private static void ObserveLateAutoRunInspectionFailure(
            System.Threading.Tasks.Task<AutoRunInspection> inspectionTask)
        {
            inspectionTask.ContinueWith(
                task => Trace.TraceWarning(
                    $"The timed-out autorun inspection later failed: {task.Exception?.GetBaseException()}"),
                CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted |
                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                System.Threading.Tasks.TaskScheduler.Default);
        }

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
            var absoluteStartupFolderPath = Global.RequireAbsoluteSpecialFolderRoot(
                startupFolderPath,
                "the current user's Startup folder");
            return Path.Combine(absoluteStartupFolderPath, $"{GetAppName()}.lnk");
        }

        private static string GetAutoRunTaskName()
        {
            return BuildAutoRunTaskName(Application.ExecutablePath);
        }

        private static string GetLegacyPathScopedAutoRunTaskName()
        {
            return BuildLegacyPathScopedAutoRunTaskName(Application.ExecutablePath);
        }

        private static string GetLegacyAutoRunTaskName()
        {
            return GetAppName();
        }

        private static string BuildAutoRunTaskName(string executablePath)
        {
            return BuildAutoRunTaskNameForUser(executablePath, GetCurrentUserId());
        }

        internal static string BuildAutoRunTaskNameForUser(string executablePath, string userSid)
        {
            if (string.IsNullOrWhiteSpace(userSid))
                throw new ArgumentException("A Windows user SID is required.", nameof(userSid));

            var normalizedSid = new SecurityIdentifier(userSid).Value.ToUpperInvariant();
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.Unicode.GetBytes(normalizedSid));
                var userSeed = new StringBuilder(16);
                for (var index = 0; index < 8; index++)
                    userSeed.Append(hash[index].ToString("x2"));

                return $"{BuildLegacyPathScopedAutoRunTaskName(executablePath)}-{userSeed}";
            }
        }

        internal static string BuildLegacyPathScopedAutoRunTaskName(string executablePath)
        {
            return $"{GetAppName()}-{WindowsApplicationContext.BuildPathSeed(executablePath)}";
        }

        private static void DeleteLegacyStartupShortcutIfPresent(string shortcutPath)
        {
            if (string.IsNullOrWhiteSpace(shortcutPath))
                throw new InvalidOperationException(
                    "The inspected legacy Startup shortcut path is unavailable.");

            var shortcutStatus = InspectLegacyStartupShortcutPath(shortcutPath, true);
            EnsureLegacyStartupShortcutCleanupCompleted(
                shortcutStatus,
                shortcutPath);
        }

        internal static void EnsureLegacyStartupShortcutCleanupCompleted(
            LegacyStartupShortcutStatus shortcutStatus,
            string shortcutPath)
        {
            if (shortcutStatus == LegacyStartupShortcutStatus.Foreign)
                throw new InvalidOperationException(
                    $"The reserved legacy Startup shortcut path '{shortcutPath}' is a directory or reparse point and cannot be removed safely.");
            if (shortcutStatus == LegacyStartupShortcutStatus.Unknown)
                throw new IOException(
                    $"The reserved legacy Startup shortcut path '{shortcutPath}' could not be inspected and cannot be removed safely.");
        }

        internal static LegacyStartupShortcutStatus InspectLegacyStartupShortcutPath(
            string shortcutPath,
            bool deleteIfOwned)
        {
            return InspectLegacyStartupShortcutPath(
                shortcutPath,
                deleteIfOwned,
                File.GetAttributes);
        }

        internal static LegacyStartupShortcutStatus InspectLegacyStartupShortcutPath(
            string shortcutPath,
            bool deleteIfOwned,
            Func<string, FileAttributes> getAttributes)
        {
            if (string.IsNullOrWhiteSpace(shortcutPath))
                throw new ArgumentException("A legacy Startup shortcut path is required.", nameof(shortcutPath));
            if (getAttributes == null)
                throw new ArgumentNullException(nameof(getAttributes));

            FileAttributes attributes;
            try
            {
                attributes = getAttributes(shortcutPath);
            }
            catch (Exception ex) when (IsMissingShortcutException(ex))
            {
                return LegacyStartupShortcutStatus.Absent;
            }
            catch (Exception ex) when (IsShortcutMetadataInspectionException(ex))
            {
                return LegacyStartupShortcutStatus.Unknown;
            }

            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return LegacyStartupShortcutStatus.Foreign;

            if (deleteIfOwned)
            {
                try
                {
                    // This exact filename is the product's reserved pre-task-scheduler
                    // autorun artifact. Delete it by validated handle without feeding
                    // user-writable bytes to an elevated Shell parser.
                    using (var shortcut = SecureFileSystem.OpenFileForDelete(shortcutPath))
                        shortcut.Delete();
                }
                catch (Exception ex) when (IsMissingShortcutException(ex))
                {
                    return LegacyStartupShortcutStatus.Absent;
                }
            }

            return LegacyStartupShortcutStatus.Unverified;
        }

        private static bool IsMissingShortcutException(Exception exception)
        {
            if (exception is FileNotFoundException || exception is DirectoryNotFoundException)
                return true;
            return exception is Win32Exception win32Exception &&
                   (win32Exception.NativeErrorCode == 2 || win32Exception.NativeErrorCode == 3);
        }

        private static bool IsShortcutMetadataInspectionException(Exception exception)
        {
            return exception is UnauthorizedAccessException ||
                   exception is IOException ||
                   exception is SecurityException ||
                   exception is Win32Exception;
        }

        private static AutoRunInspection InspectAutoRun()
        {
            var legacyStartupShortcutPath = GetLegacyStartupShortcutPath();
            using (var taskService = new TaskService())
            {
                AutoRunTaskInspection pathScopedInspection;
                using (var pathScopedTask = FindRootAutoRunTask(taskService, GetAutoRunTaskName()))
                    pathScopedInspection = InspectAutoRunTask(pathScopedTask, true, false);

                AutoRunTaskInspection legacyPathScopedInspection;
                using (var legacyPathScopedTask =
                       FindRootAutoRunTask(taskService, GetLegacyPathScopedAutoRunTaskName()))
                    legacyPathScopedInspection = InspectAutoRunTask(legacyPathScopedTask, false, true);

                AutoRunTaskInspection legacyInspection;
                using (var legacyTask = FindRootAutoRunTask(taskService, GetLegacyAutoRunTaskName()))
                    legacyInspection = InspectAutoRunTask(legacyTask, false, true);

                var shortcutStatus = InspectLegacyStartupShortcutPath(
                    legacyStartupShortcutPath,
                    false);
                var status = ClassifyAutoRunStatus(
                    pathScopedInspection.EnabledForCurrentExecutable,
                    pathScopedInspection.Canonical,
                    pathScopedInspection.Conflict,
                    legacyPathScopedInspection.EnabledForCurrentExecutable ||
                    legacyInspection.EnabledForCurrentExecutable,
                    legacyPathScopedInspection.Conflict || legacyInspection.Conflict,
                    shortcutStatus,
                    out var usesPathScopedTask);

                string diagnostic = null;
                if (status == AutoRunStatus.Conflict)
                    diagnostic =
                        "An autorun task or Startup shortcut with WireSock UI's name belongs to a different executable or has an unsafe definition. It was left unchanged.";
                else
                    diagnostic = GetLegacyStartupShortcutDiagnostic(
                        shortcutStatus,
                        legacyStartupShortcutPath);
                return new AutoRunInspection(
                    status,
                    usesPathScopedTask,
                    RequiresLegacyStartupShortcutMigrationConsent(shortcutStatus),
                    legacyStartupShortcutPath,
                    diagnostic);
            }
        }

        internal static string GetLegacyStartupShortcutDiagnostic(
            LegacyStartupShortcutStatus shortcutStatus,
            string shortcutPath)
        {
            if (shortcutStatus == LegacyStartupShortcutStatus.Unverified)
                return
                    $"An unauthenticated regular file exists at the reserved legacy Startup path '{shortcutPath}'. " +
                    "WireSock UI will not infer elevated-autorun consent from or delete that user-writable file without explicit cleanup or migration confirmation.";
            if (shortcutStatus == LegacyStartupShortcutStatus.Unknown)
                return
                    $"WireSock UI could not inspect metadata for the reserved legacy Startup path '{shortcutPath}'. " +
                    "Autorun was left unchanged. Check that the path is accessible and not blocked by filesystem permissions, then reopen Settings.";
            return null;
        }

        internal static bool RequiresLegacyStartupShortcutMigrationConsent(
            LegacyStartupShortcutStatus shortcutStatus)
        {
            return shortcutStatus == LegacyStartupShortcutStatus.Unverified;
        }

        private static AutoRunTaskInspection InspectAutoRunTask(
            Microsoft.Win32.TaskScheduler.Task task,
            bool pathScopedCandidate,
            bool ignoreTaskScopedToAnotherUser)
        {
            var inspection = new AutoRunTaskInspection();
            if (task == null)
                return inspection;

            var replaceable = IsTaskDefinitionReplaceableByExecutable(
                task.Definition, Application.ExecutablePath);
            inspection.Conflict = !replaceable &&
                                  !(ignoreTaskScopedToAnotherUser &&
                                    IsTaskScopedToAnotherUser(task.Definition));
            inspection.EnabledForCurrentExecutable = replaceable && task.Enabled;
            inspection.Canonical = pathScopedCandidate &&
                                   IsTaskDefinitionOwnedByExecutable(
                                       task.Definition, task.Enabled, Application.ExecutablePath) &&
                                   IsAutoRunTaskSecurityCanonical(task);
            return inspection;
        }

        internal static AutoRunStatus ClassifyAutoRunStatus(
            bool pathScopedTaskEnabled,
            bool pathScopedTaskCanonical,
            bool pathScopedTaskConflict,
            bool legacyTaskEnabled,
            bool legacyTaskConflict,
            LegacyStartupShortcutStatus shortcutStatus,
            out bool usesPathScopedTask)
        {
            usesPathScopedTask = false;
            if (shortcutStatus == LegacyStartupShortcutStatus.Unknown)
                return AutoRunStatus.Unknown;
            if (pathScopedTaskConflict || legacyTaskConflict ||
                shortcutStatus == LegacyStartupShortcutStatus.Foreign)
                return AutoRunStatus.Conflict;

            if (pathScopedTaskEnabled || legacyTaskEnabled)
            {
                usesPathScopedTask = pathScopedTaskEnabled &&
                                     pathScopedTaskCanonical &&
                                     !legacyTaskEnabled;
                return usesPathScopedTask ? AutoRunStatus.Enabled : AutoRunStatus.LegacyEnabled;
            }

            if (shortcutStatus == LegacyStartupShortcutStatus.Unverified)
                return AutoRunStatus.LegacyShortcutMigrationRequired;

            return AutoRunStatus.Disabled;
        }

        internal static bool ResolveRequestedAutoRun(AutoRunStatus status, bool checkedValue, bool persistedValue)
        {
            if (status == AutoRunStatus.LegacyShortcutMigrationRequired)
                return checkedValue;
            return IsKnownAutoRunStatus(status) ? checkedValue : persistedValue;
        }

        private static bool IsKnownAutoRunStatus(AutoRunStatus status)
        {
            return status != AutoRunStatus.Unknown && status != AutoRunStatus.Conflict;
        }

        private static bool IsEnabledAutoRunStatus(AutoRunStatus status)
        {
            return status == AutoRunStatus.Enabled || status == AutoRunStatus.LegacyEnabled;
        }

        private bool GetRequestedAutoRun()
        {
            if (_initialAutoRunStatus == AutoRunStatus.LegacyShortcutMigrationRequired &&
                !_legacyShortcutMigrationApproved)
                return false;

            return ResolveRequestedAutoRun(
                _initialAutoRunStatus,
                chkAutorun.Checked,
                Settings.Default.AutoRun);
        }

        private static async System.Threading.Tasks.Task<T> RunSerializedAutoRunOperationAsync<T>(Func<T> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            await AutoRunOperationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await System.Threading.Tasks.Task.Run(operation).ConfigureAwait(false);
            }
            finally
            {
                AutoRunOperationGate.Release();
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
            var registrationCompleted = false;
            var pathScopedTaskExisted = false;
            var legacyCleanupStarted = false;
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
                    td.Principal.ProcessTokenSidType = TaskProcessTokenSidType.Default;

                    var logonTrigger = new LogonTrigger
                    {
                        UserId = currentUserId,
                        Delay = TimeSpan.Zero,
                        Enabled = true,
                        StartBoundary = DateTime.MinValue,
                        EndBoundary = DateTime.MaxValue,
                        ExecutionTimeLimit = TimeSpan.Zero
                    };
                    td.Triggers.Add(logonTrigger); // Trigger for this user only

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
                    td.Settings.RunOnlyIfIdle = false;
                    td.Settings.RunOnlyIfNetworkAvailable = false;
                    td.Settings.RestartCount = 0;
                    td.Settings.RestartInterval = TimeSpan.Zero;
                    td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
                    td.Settings.StartWhenAvailable = true;
                    td.Settings.Enabled = true;
                    td.Settings.Hidden = false;
                    td.Settings.AllowDemandStart = true;
                    td.Settings.DeleteExpiredTaskAfter = TimeSpan.Zero;
                    td.Settings.Priority = AutoRunTaskPriorityClass;
                    td.Settings.Volatile = false;
                    td.Settings.DisallowStartOnRemoteAppSession = false;

                    if (!IsExecutablePathTrustedForAutoRun(appPath, out trustDiagnostic))
                        throw new InvalidOperationException(trustDiagnostic);

                    var autoRunTaskName = GetAutoRunTaskName();
                    pathScopedTaskExisted = EnsureAutoRunTaskCanBeReplaced(ts, autoRunTaskName);
                    using (var registeredTask = ts.RootFolder.RegisterTaskDefinition(
                               autoRunTaskName,
                               td,
                               TaskCreation.CreateOrUpdate | TaskCreation.DontAddPrincipalAce,
                               currentUserId,
                               null,
                               TaskLogonType.InteractiveToken,
                               AutoRunTaskSecurityDescriptorSddl))
                    {
                        registrationCompleted = true;
                        if (!IsRootAutoRunTaskPath(registeredTask.Path, autoRunTaskName) ||
                            !IsTaskDefinitionOwnedByExecutable(
                                registeredTask.Definition, registeredTask.Enabled, appPath) ||
                            !IsAutoRunTaskSecurityCanonical(registeredTask))
                            throw new InvalidOperationException(
                                "Task Scheduler did not preserve the protected WireSock UI autorun definition.");
                    }
                    legacyCleanupStarted = true;
                    DeleteAutoRunTaskIfReplaceable(ts, GetLegacyPathScopedAutoRunTaskName(), true);
                    DeleteAutoRunTaskIfReplaceable(ts, GetLegacyAutoRunTaskName(), true);
                }
            }
            catch (Exception ex)
            {
                var rollbackDiagnostic = ShouldDeleteAutoRunTaskAfterEnableFailure(
                    registrationCompleted, pathScopedTaskExisted, legacyCleanupStarted)
                    ? TryDeleteNewAutoRunTaskAfterMigrationFailure()
                    : registrationCompleted
                        ? "The protected autorun task was retained; legacy autorun cleanup remains incomplete."
                        : null;
                var diagnostic = string.IsNullOrWhiteSpace(rollbackDiagnostic)
                    ? ex.Message
                    : $"{ex.Message} {rollbackDiagnostic}";
                throw new InvalidOperationException(
                    string.Format(Resources.SettingsAutoRunEnableAdminError, diagnostic), ex);
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
                    DeleteAutoRunTaskIfReplaceable(ts, GetLegacyPathScopedAutoRunTaskName(), true);
                    DeleteAutoRunTaskIfReplaceable(ts, GetLegacyAutoRunTaskName(), true);
                }
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

        private static void DeleteAutoRunTaskIfReplaceable(
            TaskService ts,
            string taskName,
            bool ignoreTaskScopedToAnotherUser = false)
        {
            using (var task = FindRootAutoRunTask(ts, taskName))
            {
                if (task == null)
                    return;

                if (!IsTaskReplaceableByCurrentExecutable(task))
                {
                    if (ignoreTaskScopedToAnotherUser && IsTaskScopedToAnotherUser(task.Definition))
                        return;

                    throw new InvalidOperationException(
                        $"Autorun task '{taskName}' changed or belongs to another executable and cannot be removed safely.");
                }
            }

            ts.RootFolder.DeleteTask(taskName, false);
        }

        private static string TryDeleteNewAutoRunTaskAfterMigrationFailure()
        {
            try
            {
                using (var taskService = new TaskService())
                    DeleteAutoRunTaskIfReplaceable(taskService, GetAutoRunTaskName());
                return null;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"Failed to roll back the newly registered autorun task '{GetAutoRunTaskName()}': {ex}");
                return "The newly registered autorun task could not be rolled back safely; inspect Task Scheduler before retrying.";
            }
        }

        internal static bool ShouldDeleteAutoRunTaskAfterEnableFailure(
            bool registrationCompleted,
            bool taskExistedBeforeRegistration,
            bool legacyCleanupStarted)
        {
            return registrationCompleted && !taskExistedBeforeRegistration && !legacyCleanupStarted;
        }

        private static bool EnsureAutoRunTaskCanBeReplaced(TaskService taskService, string taskName)
        {
            using (var existingTask = FindRootAutoRunTask(taskService, taskName))
            {
                if (existingTask != null && !IsTaskReplaceableByCurrentExecutable(existingTask))
                    throw new InvalidOperationException(
                        $"Autorun task '{taskName}' already exists with a definition that this WireSock UI installation cannot safely replace.");

                return existingTask != null;
            }
        }

        private static Microsoft.Win32.TaskScheduler.Task FindRootAutoRunTask(
            TaskService taskService,
            string taskName)
        {
            if (taskService == null) throw new ArgumentNullException(nameof(taskService));

            var task = taskService.GetTask($@"\{taskName}");
            if (task == null)
                return null;

            if (IsRootAutoRunTaskPath(task.Path, taskName))
                return task;

            var returnedPath = task.Path;
            task.Dispose();
            throw new InvalidOperationException(
                $"Task Scheduler returned unexpected path '{returnedPath}' while looking up root task '{taskName}'.");
        }

        internal static bool IsRootAutoRunTaskPath(string taskPath, string taskName)
        {
            return !string.IsNullOrWhiteSpace(taskName) &&
                   string.Equals(taskPath, $@"\{taskName}", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTaskReplaceableByCurrentExecutable(Microsoft.Win32.TaskScheduler.Task task)
        {
            return task != null &&
                   IsTaskDefinitionReplaceableByExecutable(task.Definition, Application.ExecutablePath);
        }

        internal static bool IsTaskScopedToAnotherUser(TaskDefinition definition)
        {
            if (definition?.Principal == null ||
                definition.Triggers == null ||
                definition.Triggers.Count != 1 ||
                !(definition.Triggers[0] is LogonTrigger logonTrigger) ||
                string.IsNullOrWhiteSpace(definition.Principal.UserId) ||
                string.IsNullOrWhiteSpace(logonTrigger.UserId))
                return false;

            var currentUserId = GetCurrentUserId();
            return IsSameTaskUser(definition.Principal.UserId, logonTrigger.UserId) &&
                   !IsSameTaskUser(definition.Principal.UserId, currentUserId);
        }

        internal static bool IsTaskDefinitionOwnedByExecutable(TaskDefinition definition, bool taskEnabled,
            string executablePath)
        {
            return taskEnabled && IsTaskDefinitionOwnedByExecutable(definition, executablePath);
        }

        private static bool IsTaskDefinitionOwnedByExecutable(TaskDefinition definition, string executablePath)
        {
            var settings = definition?.Settings;
            if (!IsTaskDefinitionReplaceableByExecutable(definition, executablePath) ||
                settings == null ||
                settings.ExecutionTimeLimit != TimeSpan.Zero ||
                settings.DisallowStartIfOnBatteries ||
                settings.StopIfGoingOnBatteries ||
                !settings.WakeToRun ||
                !settings.Enabled ||
                settings.RunOnlyIfIdle ||
                settings.RunOnlyIfNetworkAvailable ||
                settings.RestartCount != 0 ||
                settings.RestartInterval != TimeSpan.Zero ||
                settings.MultipleInstances != TaskInstancesPolicy.IgnoreNew ||
                !settings.StartWhenAvailable ||
                settings.Hidden ||
                !settings.AllowDemandStart ||
                settings.DeleteExpiredTaskAfter != TimeSpan.Zero ||
                settings.Priority != AutoRunTaskPriorityClass ||
                settings.Volatile ||
                settings.DisallowStartOnRemoteAppSession ||
                !settings.RunOnlyIfLoggedOn ||
                settings.IdleSettings == null ||
                settings.IdleSettings.StopOnIdleEnd ||
                definition.Principal.LogonType != TaskLogonType.InteractiveToken ||
                definition.Principal.ProcessTokenSidType != TaskProcessTokenSidType.Default ||
                !string.IsNullOrWhiteSpace(definition.Principal.GroupId) ||
                definition.Principal.RequiredPrivileges == null ||
                definition.Principal.RequiredPrivileges.Count != 0)
                return false;

            var currentUserId = GetCurrentUserId();
            if (!IsSameTaskUser(definition.Principal.UserId, currentUserId))
                return false;

            var logonTrigger = (LogonTrigger)definition.Triggers[0];
            return IsSameTaskUser(logonTrigger.UserId, currentUserId) &&
                   logonTrigger.Delay == TimeSpan.Zero &&
                   logonTrigger.StartBoundary == DateTime.MinValue &&
                   logonTrigger.EndBoundary == DateTime.MaxValue &&
                   logonTrigger.ExecutionTimeLimit == TimeSpan.Zero &&
                   logonTrigger.Repetition != null &&
                   logonTrigger.Repetition.Interval == TimeSpan.Zero &&
                   logonTrigger.Repetition.Duration == TimeSpan.Zero &&
                   !logonTrigger.Repetition.StopAtDurationEnd;
        }

        private static bool IsAutoRunTaskSecurityCanonical(
            Microsoft.Win32.TaskScheduler.Task task)
        {
            if (task == null)
                return false;

            var sddl = task.GetSecurityDescriptorSddlForm(
                SecurityInfos.Owner | SecurityInfos.DiscretionaryAcl);
            return IsAutoRunTaskSecurityCanonical(new RawSecurityDescriptor(sddl));
        }

        internal static bool IsAutoRunTaskSecurityCanonical(RawSecurityDescriptor security)
        {
            if (security == null ||
                !(security.Owner is SecurityIdentifier owner) ||
                !Program.IsTrustedAdministrativeSid(owner) ||
                (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                security.DiscretionaryAcl == null)
                return false;

            var administratorsSid =
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administratorsHaveFullControl = false;
            var systemHasFullControl = false;
            var requiredMask = (int)TaskRights.FullControl;

            foreach (GenericAce ace in security.DiscretionaryAcl)
            {
                if (!(ace is QualifiedAce qualifiedAce) ||
                    qualifiedAce.AceQualifier != AceQualifier.AccessAllowed ||
                    qualifiedAce.AceFlags != AceFlags.None ||
                    qualifiedAce.AccessMask != requiredMask)
                    return false;

                var sid = qualifiedAce.SecurityIdentifier;
                if (sid.Equals(administratorsSid))
                {
                    if (administratorsHaveFullControl)
                        return false;
                    administratorsHaveFullControl = true;
                }
                else if (sid.Equals(systemSid))
                {
                    if (systemHasFullControl)
                        return false;
                    systemHasFullControl = true;
                }
                else
                    return false;
            }

            return administratorsHaveFullControl && systemHasFullControl;
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
            if (!TryCaptureAutoRunChange(out _, out var requestedAutoRun))
                return true;

            return SetAutoRun(requestedAutoRun);
        }

        internal System.Threading.Tasks.Task<bool> ApplyAutoRunChangeAsync()
        {
            if (!TryCaptureAutoRunChange(out _, out var requestedAutoRun))
                return System.Threading.Tasks.Task.FromResult(true);

            return RunSerializedAutoRunOperationAsync(
                () => SetAutoRun(requestedAutoRun));
        }

        internal bool RollbackAutoRunChange()
        {
            if (!TryCaptureAutoRunChange(out var initialAutoRun, out _))
                return true;

            return SetAutoRun(initialAutoRun);
        }

        internal System.Threading.Tasks.Task<bool> RollbackAutoRunChangeAsync()
        {
            if (!TryCaptureAutoRunChange(out var initialAutoRun, out _))
                return System.Threading.Tasks.Task.FromResult(true);

            return RunSerializedAutoRunOperationAsync(
                () => SetAutoRun(initialAutoRun));
        }

        internal System.Threading.Tasks.Task<bool> CommitAutoRunChangeAsync()
        {
            var shortcutPath = GetLegacyStartupShortcutPathForCommit(
                _hasUnverifiedLegacyShortcut,
                _legacyShortcutMigrationApproved,
                _legacyStartupShortcutPath);
            if (shortcutPath == null)
                return System.Threading.Tasks.Task.FromResult(true);

            return RunSerializedAutoRunOperationAsync(() =>
            {
                DeleteLegacyStartupShortcutIfPresent(shortcutPath);
                return true;
            });
        }

        internal static bool ShouldPreserveLegacyShortcutUntilCommit(
            bool hasUnverifiedLegacyShortcut,
            bool migrationApproved)
        {
            return hasUnverifiedLegacyShortcut && migrationApproved;
        }

        internal static string GetLegacyStartupShortcutPathForCommit(
            bool hasUnverifiedLegacyShortcut,
            bool migrationApproved,
            string inspectedShortcutPath)
        {
            if (!ShouldPreserveLegacyShortcutUntilCommit(
                    hasUnverifiedLegacyShortcut,
                    migrationApproved))
                return null;
            if (string.IsNullOrWhiteSpace(inspectedShortcutPath))
                throw new InvalidOperationException(
                    "The approved legacy Startup shortcut cleanup has no inspected path.");

            string absolutePath;
            try
            {
                absolutePath = Path.GetFullPath(inspectedShortcutPath);
            }
            catch (Exception ex) when (ex is ArgumentException ||
                                       ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                throw new InvalidOperationException(
                    "The approved legacy Startup shortcut cleanup path is invalid.",
                    ex);
            }

            if (!string.Equals(
                    inspectedShortcutPath,
                    absolutePath,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The approved legacy Startup shortcut cleanup path is not absolute.");

            return inspectedShortcutPath;
        }

        private bool TryCaptureAutoRunChange(out bool initialAutoRun, out bool requestedAutoRun)
        {
            initialAutoRun = IsEnabledAutoRunStatus(_initialAutoRunStatus);
            requestedAutoRun = GetRequestedAutoRun();
            return ShouldApplyAutoRunChange(
                _initialAutoRunStatus,
                requestedAutoRun,
                _initialAutoRunUsesPathScopedTask,
                _hasUnverifiedLegacyShortcut,
                _legacyShortcutMigrationApproved);
        }

        internal static bool ShouldApplyAutoRunChange(
            AutoRunStatus initialStatus,
            bool requestedAutoRun,
            bool initialUsesPathScopedTask,
            bool hasUnverifiedLegacyShortcut,
            bool migrationApproved)
        {
            if (!IsKnownAutoRunStatus(initialStatus) ||
                hasUnverifiedLegacyShortcut && !migrationApproved)
                return false;

            var initialAutoRun = IsEnabledAutoRunStatus(initialStatus);
            return initialAutoRun != requestedAutoRun ||
                   requestedAutoRun && !initialUsesPathScopedTask;
        }

        private static bool SetAutoRun(bool enabled)
        {
            if (enabled)
                EnableAutoRun();
            else
                DisableAutoRun();

            return true;
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            if (_hasUnverifiedLegacyShortcut &&
                !_legacyShortcutMigrationApproved)
            {
                var initialAutoRun = IsEnabledAutoRunStatus(_initialAutoRunStatus);
                string requestedAction;
                if (!IsKnownAutoRunStatus(_initialAutoRunStatus))
                    requestedAction =
                        "remove only the opaque legacy file after all settings commit; conflicting or unreadable task entries will remain unchanged";
                else if (chkAutorun.Checked != initialAutoRun)
                    requestedAction = chkAutorun.Checked
                        ? "enable WireSock UI autorun with a protected highest-privilege task and remove the opaque legacy file after all settings commit"
                        : "disable WireSock UI autorun and remove the opaque legacy file after all settings commit";
                else
                    requestedAction = chkAutorun.Checked
                        ? "keep autorun enabled, migrate any validated older task if needed, and remove the opaque legacy file after all settings commit"
                        : "keep autorun disabled and remove the opaque legacy file after all settings commit";

                var result = MessageBox.Show(
                    "A regular file exists at WireSock UI's old Startup shortcut name. Its contents cannot be authenticated and will never be parsed by this elevated process." +
                    $"{Environment.NewLine}{Environment.NewLine}Select Yes to {requestedAction}." +
                    $"{Environment.NewLine}Select No to leave the file untouched and save the other settings." +
                    $"{Environment.NewLine}Select Cancel to return to Settings.",
                    Resources.TunnelErrorTitle,
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button3);
                if (result == DialogResult.Cancel)
                    return;

                if (result == DialogResult.Yes)
                {
                    _legacyShortcutMigrationApproved = true;
                }
                else
                {
                    // Saving unrelated settings must not persist a checkbox state that
                    // was not applied because opaque-artifact cleanup was declined.
                    chkAutorun.Checked = initialAutoRun;
                }
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
