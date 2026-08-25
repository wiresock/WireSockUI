using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
        private const int AutoRunMutationTimeoutMilliseconds = 15000;
        // TaskScheduler maps BelowNormal to the native Task Scheduler 2.0 priority value 7.
        internal const ProcessPriorityClass AutoRunTaskPriorityClass = ProcessPriorityClass.BelowNormal;
        internal const string AutoRunTaskSecurityDescriptorSddl =
            "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)";
        private static readonly AutoRunOperationService AutoRunOperations =
            new AutoRunOperationService(() => Program.ApplicationLauncherPath);
        private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();
        private AutoRunStatus _initialAutoRunStatus;
        private bool _initialAutoRunUsesPathScopedTask;
        private bool _hasUnverifiedLegacyShortcut;
        private bool _legacyShortcutMigrationApproved;
        private bool _hasLegacyProductTaskToMigrate;
        private bool _legacyProductTaskMigrationApproved;
        private string _legacyStartupShortcutPath;
        private System.Threading.Tasks.Task<AutoRunInspection> _autoRunInspectionTask;
        private bool _managedResourcesDisposed;

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
                bool hasLegacyProductTaskToMigrate,
                string legacyStartupShortcutPath,
                string diagnostic = null)
            {
                Status = status;
                UsesPathScopedTask = usesPathScopedTask;
                HasUnverifiedLegacyShortcut = hasUnverifiedLegacyShortcut;
                HasLegacyProductTaskToMigrate = hasLegacyProductTaskToMigrate;
                LegacyStartupShortcutPath = legacyStartupShortcutPath;
                Diagnostic = diagnostic;
            }

            internal AutoRunStatus Status { get; }
            internal bool UsesPathScopedTask { get; }
            internal bool HasUnverifiedLegacyShortcut { get; }
            internal bool HasLegacyProductTaskToMigrate { get; }
            internal string LegacyStartupShortcutPath { get; }
            internal string Diagnostic { get; }
        }

        private sealed class AutoRunTaskInspection
        {
            internal bool EnabledForCurrentExecutable { get; set; }
            internal bool Canonical { get; set; }
            internal bool Conflict { get; set; }
            internal bool RequiresLegacyProductTaskMigration { get; set; }
        }

        public FrmSettings()
        {
            InitializeComponent();

            UiFonts.TryApplyMessageBoxFont(messageBoxFont => Font = messageBoxFont);

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

#if !WIRESOCKUI_ENABLE_UWP
            // These settings have no runtime implementation in the classic build. Preserve
            // their stored values, but do not present controls that promise unsupported behavior.
            chkAutoUpdate.Enabled = false;
            chkAutoUpdate.Visible = false;
            chkNotify.Enabled = false;
            chkNotify.Visible = false;
            CompactClassicSettingsLayout();
#endif

            Shown += OnSettingsShown;
        }

#if !WIRESOCKUI_ENABLE_UWP
        private void CompactClassicSettingsLayout()
        {
            // Measure the already-scaled designer rows so the classic layout remains compact
            // with legacy Windows fonts and non-default DPI settings.
            var autoUpdateOffset = Math.Max(0, chkUseAdapter.Top - chkAutoUpdate.Top);
            var notificationOffset = Math.Max(0, chkEnableKillSwitch.Top - chkNotify.Top);
            var contentOffset = autoUpdateOffset + notificationOffset;
            if (contentOffset == 0)
                return;

            SuspendLayout();
            try
            {
                chkUseAdapter.Top -= autoUpdateOffset;
                chkEnableKillSwitch.Top -= contentOffset;
                lblLogLevel.Top -= contentOffset;
                ddlLogLevel.Top -= contentOffset;
                ClientSize = new Size(ClientSize.Width, Math.Max(1, ClientSize.Height - contentOffset));
            }
            finally
            {
                // The action buttons are bottom-anchored, so this single layout pass moves them
                // with the resized client area while preserving their existing bottom margin.
                ResumeLayout(true);
            }
        }
#endif

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
            _autoRunInspectionTask = InspectAutoRunIsolatedAsync(_lifetimeCancellation.Token);

            try
            {
                var inspection = await _autoRunInspectionTask;
                if (IsDisposed || Disposing)
                    return;

                _initialAutoRunStatus = inspection.Status;
                _initialAutoRunUsesPathScopedTask = inspection.UsesPathScopedTask;
                _hasUnverifiedLegacyShortcut = inspection.HasUnverifiedLegacyShortcut;
                _hasLegacyProductTaskToMigrate = inspection.HasLegacyProductTaskToMigrate;
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
            catch (OperationCanceledException) when (IsDisposed || Disposing)
            {
                // Form disposal terminates the isolated helper and suppresses stale UI updates.
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

        private void OnCopyProfilesFolderPathClick(object sender, EventArgs e)
        {
            try
            {
                var profilesFolder = GetProfilesFolderPathForClipboard(Global.ConfigsFolder);
                Clipboard.SetText(profilesFolder, TextDataFormat.UnicodeText);
                MessageBox.Show(
                    string.Format(Resources.SettingsProfilesFolderCopied, profilesFolder),
                    Resources.SettingsProfiles,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowSettingsError(
                    Resources.SettingsProfilesFolderError,
                    ex);
            }
        }

        internal static string GetProfilesFolderPathForClipboard(string profilesFolder)
        {
            return Global.RequireAbsoluteSpecialFolderRoot(
                profilesFolder,
                "the profiles folder");
        }

        private static string GetAppName()
        {
            var launcherName = Path.GetFileNameWithoutExtension(Program.ApplicationLauncherPath);
            if (string.IsNullOrWhiteSpace(launcherName))
                throw new InvalidOperationException("The trusted WireSock UI launcher name is unavailable.");
            return launcherName;
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
            return BuildAutoRunTaskName(Program.ApplicationLauncherPath);
        }

        private static string GetLegacyPathScopedAutoRunTaskName()
        {
            return BuildLegacyPathScopedAutoRunTaskName(Program.ApplicationLauncherPath);
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
                    legacyInspection = InspectAutoRunTask(
                        legacyTask,
                        false,
                        true,
                        allowLegacyProductTask: true);

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
                    legacyInspection.RequiresLegacyProductTaskMigration,
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
            bool ignoreTaskScopedToAnotherUser,
            bool allowLegacyProductTask = false)
        {
            var inspection = new AutoRunTaskInspection();
            if (task == null)
                return inspection;

            var replaceableByCurrentExecutable = IsTaskDefinitionReplaceableByExecutable(
                task.Definition,
                Program.ApplicationLauncherPath);
            var migratableLegacyProductTask =
                !replaceableByCurrentExecutable &&
                allowLegacyProductTask &&
                IsLegacyAutoRunTaskDefinitionMigratable(
                    task.Definition,
                    Program.NativeLauncherFileName,
                    "Auto start for " + GetAppName());
            var replaceable = replaceableByCurrentExecutable || migratableLegacyProductTask;
            inspection.Conflict = !replaceable &&
                                  !(ignoreTaskScopedToAnotherUser &&
                                    IsTaskScopedToAnotherUser(task.Definition));
            inspection.EnabledForCurrentExecutable = replaceable && task.Enabled;
            inspection.RequiresLegacyProductTaskMigration = migratableLegacyProductTask;
            inspection.Canonical = pathScopedCandidate &&
                                   IsTaskDefinitionOwnedByExecutable(
                                       task.Definition, task.Enabled, Program.ApplicationLauncherPath) &&
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

        private static async System.Threading.Tasks.Task<AutoRunInspection> InspectAutoRunIsolatedAsync(
            CancellationToken cancellationToken)
        {
            var result = await AutoRunOperations.ExecuteAsync(
                    AutoRunHelperOperation.Inspect,
                    TimeSpan.FromMilliseconds(AutoRunInspectionTimeoutMilliseconds),
                    false,
                    cancellationToken)
                .ConfigureAwait(false);

            switch (result.Outcome)
            {
                case AutoRunOperationOutcome.Succeeded:
                    return DeserializeAutoRunInspection(result.Payload);
                case AutoRunOperationOutcome.TimedOut:
                    throw new TimeoutException(result.Diagnostic);
                case AutoRunOperationOutcome.StateUncertain:
                    throw new InvalidOperationException(result.Diagnostic);
                default:
                    throw new IOException(
                        result.Diagnostic ?? "The autorun helper could not inspect Task Scheduler state.");
            }
        }

        private static async System.Threading.Tasks.Task<bool> ExecuteAutoRunMutationAsync(
            AutoRunHelperOperation operation,
            CancellationToken cancellationToken)
        {
            var result = await AutoRunOperations.ExecuteAsync(
                    operation,
                    TimeSpan.FromMilliseconds(AutoRunMutationTimeoutMilliseconds),
                    true,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.Outcome == AutoRunOperationOutcome.Succeeded)
                return true;
            if (result.Outcome == AutoRunOperationOutcome.Failed)
                throw new InvalidOperationException(
                    result.Diagnostic ??
                    $"The autorun {GetAutoRunOperationDisplayName(operation)} operation failed.");

            if (result.Outcome == AutoRunOperationOutcome.StateUncertain)
            {
                var verification = await AutoRunOperations.ExecuteAsync(
                        AutoRunHelperOperation.Inspect,
                        TimeSpan.FromMilliseconds(AutoRunInspectionTimeoutMilliseconds),
                        false,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (verification.Outcome == AutoRunOperationOutcome.Succeeded)
                {
                    var inspection = DeserializeAutoRunInspection(verification.Payload);
                    if (TryResolveMutationOutcome(operation, inspection, out var succeeded))
                    {
                        AutoRunOperations.AcknowledgeVerifiedMutationState();
                        if (!result.OperationStarted)
                            return await ExecuteAutoRunMutationAsync(operation, cancellationToken)
                                .ConfigureAwait(false);
                        if (succeeded)
                            return true;

                        throw new InvalidOperationException(
                            BuildVerifiedIncompleteAutoRunDiagnostic(operation, result.Diagnostic));
                    }
                }
            }

            throw new AutoRunOperationUncertainException(
                result.Diagnostic ??
                $"The autorun {GetAutoRunOperationDisplayName(operation)} operation did not complete within its safety limit, and its final state could not be verified. No compensating autorun mutation will be started.");
        }

        internal static string BuildVerifiedIncompleteAutoRunDiagnostic(
            AutoRunHelperOperation operation,
            string helperDiagnostic)
        {
            var verifiedDiagnostic =
                $"The autorun {GetAutoRunOperationDisplayName(operation)} operation was verified not to have completed.";
            return string.IsNullOrWhiteSpace(helperDiagnostic)
                ? verifiedDiagnostic
                : $"{verifiedDiagnostic} {helperDiagnostic.Trim()}";
        }

        private static string GetAutoRunOperationDisplayName(AutoRunHelperOperation operation)
        {
            switch (operation)
            {
                case AutoRunHelperOperation.Enable:
                case AutoRunHelperOperation.EnableMigratingLegacyTask:
                    return "Enable";
                case AutoRunHelperOperation.Disable:
                case AutoRunHelperOperation.DisableMigratingLegacyTask:
                    return "Disable";
                case AutoRunHelperOperation.DeleteLegacyShortcut:
                    return "legacy shortcut cleanup";
                default:
                    return operation.ToString();
            }
        }

        private static bool TryResolveMutationOutcome(
            AutoRunHelperOperation operation,
            AutoRunInspection inspection,
            out bool succeeded)
        {
            if (inspection == null)
            {
                succeeded = false;
                return false;
            }

            return TryResolveMutationOutcome(
                operation,
                inspection.Status,
                inspection.UsesPathScopedTask,
                inspection.HasUnverifiedLegacyShortcut,
                inspection.HasLegacyProductTaskToMigrate,
                out succeeded);
        }

        internal static bool TryResolveMutationOutcome(
            AutoRunHelperOperation operation,
            AutoRunStatus status,
            bool usesPathScopedTask,
            bool hasUnverifiedLegacyShortcut,
            bool hasLegacyProductTaskToMigrate,
            out bool succeeded)
        {
            succeeded = false;
            if (status == AutoRunStatus.Unknown || status == AutoRunStatus.Conflict)
                return false;

            if ((operation == AutoRunHelperOperation.EnableMigratingLegacyTask ||
                 operation == AutoRunHelperOperation.DisableMigratingLegacyTask) &&
                hasLegacyProductTaskToMigrate)
            {
                // The requested migration explicitly includes removal of the historical
                // product task. A canonical current state alone is only partial success.
                return true;
            }

            switch (operation)
            {
                case AutoRunHelperOperation.Enable:
                case AutoRunHelperOperation.EnableMigratingLegacyTask:
                    if (status == AutoRunStatus.Enabled && usesPathScopedTask)
                    {
                        succeeded = true;
                        return true;
                    }

                    return status == AutoRunStatus.Disabled ||
                           status == AutoRunStatus.LegacyShortcutMigrationRequired;

                case AutoRunHelperOperation.Disable:
                case AutoRunHelperOperation.DisableMigratingLegacyTask:
                    if (status == AutoRunStatus.Disabled ||
                        status == AutoRunStatus.LegacyShortcutMigrationRequired)
                    {
                        succeeded = true;
                        return true;
                    }

                    return status == AutoRunStatus.Enabled ||
                           status == AutoRunStatus.LegacyEnabled;

                case AutoRunHelperOperation.DeleteLegacyShortcut:
                    succeeded = !hasUnverifiedLegacyShortcut;
                    return true;

                default:
                    return false;
            }
        }

        private static string SerializeAutoRunInspection(AutoRunInspection inspection)
        {
            if (inspection == null)
                throw new ArgumentNullException(nameof(inspection));

            return string.Join(
                "|",
                "2",
                ((int)inspection.Status).ToString(System.Globalization.CultureInfo.InvariantCulture),
                inspection.UsesPathScopedTask ? "1" : "0",
                inspection.HasUnverifiedLegacyShortcut ? "1" : "0",
                inspection.HasLegacyProductTaskToMigrate ? "1" : "0",
                EncodeHelperField(inspection.LegacyStartupShortcutPath),
                EncodeHelperField(inspection.Diagnostic));
        }

        private static AutoRunInspection DeserializeAutoRunInspection(string payload)
        {
            var fields = (payload ?? string.Empty).Split('|');
            if (fields.Length != 7 || fields[0] != "2" ||
                !int.TryParse(
                    fields[1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var statusValue) ||
                !Enum.IsDefined(typeof(AutoRunStatus), statusValue) ||
                !TryParseHelperBoolean(fields[2], out var usesPathScopedTask) ||
                !TryParseHelperBoolean(fields[3], out var hasUnverifiedLegacyShortcut) ||
                !TryParseHelperBoolean(fields[4], out var hasLegacyProductTaskToMigrate))
                throw new InvalidDataException("The autorun helper returned an invalid inspection result.");

            try
            {
                return new AutoRunInspection(
                    (AutoRunStatus)statusValue,
                    usesPathScopedTask,
                    hasUnverifiedLegacyShortcut,
                    hasLegacyProductTaskToMigrate,
                    DecodeHelperField(fields[5]),
                    DecodeHelperField(fields[6]));
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    "The autorun helper returned an invalid encoded inspection result.",
                    ex);
            }
        }

        private static string EncodeHelperField(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string DecodeHelperField(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty));
        }

        private static bool TryParseHelperBoolean(string value, out bool result)
        {
            if (value == "1")
            {
                result = true;
                return true;
            }

            result = false;
            return value == "0";
        }

        internal static bool TryRunAutoRunHelperCommandLine(string[] arguments, out int exitCode)
        {
            return AutoRunOperationService.TryRunHelperCommandLine(
                arguments,
                ExecuteAutoRunHelperOperation,
                out exitCode);
        }

        private static AutoRunHelperExecution ExecuteAutoRunHelperOperation(
            AutoRunHelperOperation operation)
        {
            switch (operation)
            {
                case AutoRunHelperOperation.Inspect:
                    return AutoRunHelperExecution.Success(
                        SerializeAutoRunInspection(InspectAutoRun()));
                case AutoRunHelperOperation.Enable:
                    EnableAutoRun(false);
                    return AutoRunHelperExecution.Success();
                case AutoRunHelperOperation.EnableMigratingLegacyTask:
                    EnableAutoRun(true);
                    return AutoRunHelperExecution.Success();
                case AutoRunHelperOperation.Disable:
                    DisableAutoRun(false);
                    return AutoRunHelperExecution.Success();
                case AutoRunHelperOperation.DisableMigratingLegacyTask:
                    DisableAutoRun(true);
                    return AutoRunHelperExecution.Success();
                case AutoRunHelperOperation.DeleteLegacyShortcut:
                    DeleteLegacyStartupShortcutIfPresent(GetLegacyStartupShortcutPath());
                    return AutoRunHelperExecution.Success();
                default:
                    return AutoRunHelperExecution.Failure(
                        "The requested autorun helper operation is not supported.");
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
        private static void EnableAutoRun(bool allowLegacyProductTask)
        {
            var registrationCompleted = false;
            var pathScopedTaskExisted = false;
            var legacyCleanupStarted = false;
            try
            {
                using (var ts = new TaskService())
                using (var td = ts.NewTask())
                {
                    var currentUserId = GetCurrentUserId();
                    var appPath = Program.ApplicationLauncherPath;
                    if (!IsExecutablePathTrustedForAutoRun(appPath, out var trustDiagnostic))
                        throw new InvalidOperationException(trustDiagnostic);

                    ConfigureAutoRunTaskDefinition(
                        td,
                        GetAppName(),
                        currentUserId,
                        appPath);

                    if (!IsExecutablePathTrustedForAutoRun(appPath, out trustDiagnostic))
                        throw new InvalidOperationException(trustDiagnostic);

                    var autoRunTaskName = GetAutoRunTaskName();
                    pathScopedTaskExisted = EnsureAutoRunTaskCanBeReplaced(ts, autoRunTaskName);
                    EnsureAutoRunTaskCanBeRemoved(
                        ts,
                        GetLegacyPathScopedAutoRunTaskName(),
                        ignoreTaskScopedToAnotherUser: true);
                    EnsureAutoRunTaskCanBeRemoved(
                        ts,
                        GetLegacyAutoRunTaskName(),
                        ignoreTaskScopedToAnotherUser: true,
                        allowLegacyProductTask: allowLegacyProductTask);
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
                    DeleteAutoRunTaskIfReplaceable(
                        ts,
                        GetLegacyAutoRunTaskName(),
                        ignoreTaskScopedToAnotherUser: true,
                        allowLegacyProductTask: allowLegacyProductTask);
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

        internal static void ConfigureAutoRunTaskDefinition(
            TaskDefinition definition,
            string appName,
            string currentUserId,
            string appPath)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrWhiteSpace(appName))
                throw new ArgumentException("An application name is required.", nameof(appName));
            if (string.IsNullOrWhiteSpace(currentUserId))
                throw new ArgumentException("A current-user identifier is required.", nameof(currentUserId));
            if (string.IsNullOrWhiteSpace(appPath))
                throw new ArgumentException("An application path is required.", nameof(appPath));

            definition.RegistrationInfo.Description = "Auto start for " + appName;

            // Windows 7 exposes Task Scheduler 2.1 (task XML schema 1.3). Explicitly
            // stay on that schema so a definition authored on newer Windows remains
            // portable to every supported OS.
            definition.Settings.Compatibility = TaskCompatibility.V2_1;

            definition.Principal.UserId = currentUserId;
            definition.Principal.LogonType = TaskLogonType.InteractiveToken;
            definition.Principal.RunLevel = TaskRunLevel.Highest;
            definition.Principal.ProcessTokenSidType = TaskProcessTokenSidType.Default;

            definition.Triggers.Add(new LogonTrigger
            {
                UserId = currentUserId,
                Delay = TimeSpan.Zero,
                Enabled = true,
                StartBoundary = DateTime.MinValue,
                EndBoundary = DateTime.MaxValue,
                ExecutionTimeLimit = TimeSpan.Zero
            });
            definition.Actions.Add(new ExecAction(appPath));

            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.WakeToRun = true;
            definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
            definition.Settings.IdleSettings.StopOnIdleEnd = false;
            definition.Settings.RunOnlyIfIdle = false;
            definition.Settings.RunOnlyIfNetworkAvailable = false;
            definition.Settings.RestartCount = 0;
            definition.Settings.RestartInterval = TimeSpan.Zero;
            definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.Enabled = true;
            definition.Settings.Hidden = false;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.DeleteExpiredTaskAfter = TimeSpan.Zero;
            definition.Settings.Priority = AutoRunTaskPriorityClass;
            definition.Settings.DisallowStartOnRemoteAppSession = false;

            // Do not assign TaskSettings.Volatile, even to false. The TaskScheduler
            // wrapper promotes the definition to Task Scheduler 2.2/schema 1.4 on
            // assignment, which Windows 7 cannot register. Its pre-2.2 value is
            // inherently false.
        }

        /// <summary>
        ///     Disables the auto-run feature for the current application with administrative privileges.
        /// </summary>
        /// <remarks>
        ///     This method deletes only tasks that point to the current executable.
        ///     If an error occurs while disabling auto-run, a contextual exception is propagated to the settings transaction.
        /// </remarks>
        private static void DisableAutoRun(bool allowLegacyProductTask)
        {
            try
            {
                using (var ts = new TaskService())
                {
                    EnsureAutoRunTaskCanBeRemoved(ts, GetAutoRunTaskName());
                    EnsureAutoRunTaskCanBeRemoved(
                        ts,
                        GetLegacyPathScopedAutoRunTaskName(),
                        ignoreTaskScopedToAnotherUser: true);
                    EnsureAutoRunTaskCanBeRemoved(
                        ts,
                        GetLegacyAutoRunTaskName(),
                        ignoreTaskScopedToAnotherUser: true,
                        allowLegacyProductTask: allowLegacyProductTask);
                    DeleteAutoRunTaskIfReplaceable(ts, GetAutoRunTaskName());
                    DeleteAutoRunTaskIfReplaceable(ts, GetLegacyPathScopedAutoRunTaskName(), true);
                    DeleteAutoRunTaskIfReplaceable(
                        ts,
                        GetLegacyAutoRunTaskName(),
                        ignoreTaskScopedToAnotherUser: true,
                        allowLegacyProductTask: allowLegacyProductTask);
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
            bool ignoreTaskScopedToAnotherUser = false,
            bool allowLegacyProductTask = false)
        {
            using (var task = FindRootAutoRunTask(ts, taskName))
            {
                if (task == null)
                    return;

                if (!IsTaskReplaceableByCurrentExecutable(task, allowLegacyProductTask))
                {
                    if (ignoreTaskScopedToAnotherUser && IsTaskScopedToAnotherUser(task.Definition))
                        return;

                    throw new InvalidOperationException(
                        $"Autorun task '{taskName}' changed or belongs to another executable and cannot be removed safely.");
                }
            }

            ts.RootFolder.DeleteTask(taskName, false);
        }

        private static void EnsureAutoRunTaskCanBeRemoved(
            TaskService taskService,
            string taskName,
            bool ignoreTaskScopedToAnotherUser = false,
            bool allowLegacyProductTask = false)
        {
            using (var task = FindRootAutoRunTask(taskService, taskName))
            {
                if (task == null ||
                    IsTaskReplaceableByCurrentExecutable(task, allowLegacyProductTask) ||
                    ignoreTaskScopedToAnotherUser && IsTaskScopedToAnotherUser(task.Definition))
                    return;

                throw new InvalidOperationException(
                    $"Autorun task '{taskName}' changed or belongs to another executable and cannot be modified safely.");
            }
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

        private static bool IsTaskReplaceableByCurrentExecutable(
            Microsoft.Win32.TaskScheduler.Task task,
            bool allowLegacyProductTask = false)
        {
            return task != null &&
                   (IsTaskDefinitionReplaceableByExecutable(
                        task.Definition,
                        Program.ApplicationLauncherPath) ||
                    allowLegacyProductTask &&
                    IsLegacyAutoRunTaskDefinitionMigratable(
                        task.Definition,
                        Program.NativeLauncherFileName,
                        "Auto start for " + GetAppName()));
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
                IsAutoRunTaskVolatile(settings) ||
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

        internal static bool IsAutoRunTaskVolatile(TaskSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            // ITaskSettings3.Volatile is unavailable before Task Scheduler 2.2.
            // Older schemas cannot express a volatile task, so avoid touching that
            // interface on Windows 7 and treat the effective value as false.
            return settings.Compatibility >= TaskCompatibility.V2_2 && settings.Volatile;
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

        internal static bool IsLegacyAutoRunTaskDefinitionMigratable(
            TaskDefinition definition,
            string expectedExecutableFileName,
            string expectedDescription)
        {
            if (definition?.Actions == null || definition.Actions.Count != 1 ||
                definition.Triggers == null || definition.Triggers.Count != 1 ||
                definition.Principal == null ||
                definition.Principal.RunLevel != TaskRunLevel.Highest ||
                definition.Principal.LogonType != TaskLogonType.InteractiveToken ||
                definition.RegistrationInfo == null ||
                !string.Equals(
                    definition.RegistrationInfo.Description,
                    expectedDescription,
                    StringComparison.Ordinal))
                return false;

            var execAction = definition.Actions[0] as ExecAction;
            if (execAction == null ||
                !string.IsNullOrWhiteSpace(execAction.Arguments) ||
                !string.IsNullOrWhiteSpace(execAction.WorkingDirectory) ||
                string.IsNullOrWhiteSpace(execAction.Path))
                return false;

            try
            {
                var legacyPath = execAction.Path.Trim().Trim('"');
                if (!Path.IsPathRooted(legacyPath) ||
                    !string.Equals(
                        Path.GetFileName(legacyPath),
                        expectedExecutableFileName,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch (Exception ex) when (ex is ArgumentException ||
                                       ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                return false;
            }

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
            return ApplyAutoRunChangeAsync().GetAwaiter().GetResult();
        }

        internal async System.Threading.Tasks.Task<bool> ApplyAutoRunChangeAsync()
        {
            if (!TryCaptureAutoRunChange(out _, out var requestedAutoRun))
                return true;

            return await ExecuteAutoRunMutationAsync(
                    GetAutoRunMutationOperation(
                        requestedAutoRun,
                        _hasLegacyProductTaskToMigrate &&
                        _legacyProductTaskMigrationApproved),
                    _lifetimeCancellation.Token)
                .ConfigureAwait(false);
        }

        internal bool RollbackAutoRunChange()
        {
            return RollbackAutoRunChangeAsync().GetAwaiter().GetResult();
        }

        internal async System.Threading.Tasks.Task<bool> RollbackAutoRunChangeAsync()
        {
            if (!TryCaptureAutoRunChange(out var initialAutoRun, out _))
                return true;

            return await ExecuteAutoRunMutationAsync(
                    GetAutoRunMutationOperation(
                        initialAutoRun,
                        _hasLegacyProductTaskToMigrate &&
                        _legacyProductTaskMigrationApproved),
                    _lifetimeCancellation.Token)
                .ConfigureAwait(false);
        }

        internal static AutoRunHelperOperation GetAutoRunMutationOperation(
            bool enable,
            bool migrateLegacyProductTask)
        {
            if (enable)
                return migrateLegacyProductTask
                    ? AutoRunHelperOperation.EnableMigratingLegacyTask
                    : AutoRunHelperOperation.Enable;

            return migrateLegacyProductTask
                ? AutoRunHelperOperation.DisableMigratingLegacyTask
                : AutoRunHelperOperation.Disable;
        }

        internal async System.Threading.Tasks.Task<bool> CommitAutoRunChangeAsync()
        {
            var shortcutPath = GetLegacyStartupShortcutPathForCommit(
                _hasUnverifiedLegacyShortcut,
                _legacyShortcutMigrationApproved,
                _legacyStartupShortcutPath);
            if (shortcutPath == null)
                return true;

            if (!string.Equals(
                    shortcutPath,
                    GetLegacyStartupShortcutPath(),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The approved legacy Startup shortcut path changed before cleanup.");

            return await ExecuteAutoRunMutationAsync(
                    AutoRunHelperOperation.DeleteLegacyShortcut,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(false);
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
                _legacyShortcutMigrationApproved,
                _hasLegacyProductTaskToMigrate,
                _legacyProductTaskMigrationApproved);
        }

        internal static bool ShouldApplyAutoRunChange(
            AutoRunStatus initialStatus,
            bool requestedAutoRun,
            bool initialUsesPathScopedTask,
            bool hasUnverifiedLegacyShortcut,
            bool migrationApproved,
            bool hasLegacyProductTaskToMigrate = false,
            bool legacyProductTaskMigrationApproved = false)
        {
            if (!IsKnownAutoRunStatus(initialStatus) ||
                hasUnverifiedLegacyShortcut && !migrationApproved ||
                hasLegacyProductTaskToMigrate && !legacyProductTaskMigrationApproved)
                return false;

            var initialAutoRun = IsEnabledAutoRunStatus(initialStatus);
            return initialAutoRun != requestedAutoRun ||
                   requestedAutoRun && !initialUsesPathScopedTask ||
                   hasLegacyProductTaskToMigrate && legacyProductTaskMigrationApproved;
        }

        internal static bool ShouldOfferLegacyProductTaskMigration(
            bool hasLegacyProductTaskToMigrate,
            bool legacyProductTaskMigrationApproved,
            AutoRunStatus initialStatus,
            bool hasUnverifiedLegacyShortcut,
            bool legacyShortcutMigrationApproved)
        {
            return hasLegacyProductTaskToMigrate &&
                   !legacyProductTaskMigrationApproved &&
                   IsKnownAutoRunStatus(initialStatus) &&
                   (!hasUnverifiedLegacyShortcut || legacyShortcutMigrationApproved);
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
                else if (_hasLegacyProductTaskToMigrate)
                    requestedAction =
                        "approve removal of the opaque legacy file after all settings commit; any required elevated-task migration will be confirmed separately";
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

            if (ShouldOfferLegacyProductTaskMigration(
                    _hasLegacyProductTaskToMigrate,
                    _legacyProductTaskMigrationApproved,
                    _initialAutoRunStatus,
                    _hasUnverifiedLegacyShortcut,
                    _legacyShortcutMigrationApproved))
            {
                var initialAutoRun = IsEnabledAutoRunStatus(_initialAutoRunStatus);
                var result = MessageBox.Show(
                    "WireSock UI found an older elevated autorun task for this user. It has the historical WireSock UI definition but launches WireSockUI.exe from another installation path." +
                    $"{Environment.NewLine}{Environment.NewLine}Select Yes to apply the selected autorun setting to this installation and remove the older task when settings are saved." +
                    $"{Environment.NewLine}Select No to leave the older task unchanged and save the other settings." +
                    $"{Environment.NewLine}Select Cancel to return to Settings.",
                    Resources.TunnelErrorTitle,
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button3);
                if (result == DialogResult.Cancel)
                    return;

                if (result == DialogResult.Yes)
                {
                    _legacyProductTaskMigrationApproved = true;
                }
                else
                {
                    // Do not persist a checkbox change whose corresponding legacy
                    // task mutation was declined.
                    chkAutorun.Checked = initialAutoRun;
                }
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        internal void CancelPendingOperations()
        {
            try
            {
                _lifetimeCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void DisposeManagedResources()
        {
            if (_managedResourcesDisposed)
                return;

            _managedResourcesDisposed = true;
            Shown -= OnSettingsShown;
            CancelPendingOperations();
            _lifetimeCancellation.Dispose();
        }
    }
}
