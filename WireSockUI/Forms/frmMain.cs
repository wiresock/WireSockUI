using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WireSockUI.Config;
using WireSockUI.Extensions;
using WireSockUI.Native;
using WireSockUI.Properties;
using static WireSockUI.Native.WireguardBoosterExports;

namespace WireSockUI.Forms
{
    /**
     * @brief The main form of the application.
     */
    public partial class FrmMain : Form
    {
        public enum ConnectionState
        {
            Connecting,
            Connected,
            Disconnected,
            Indeterminate
        }

        private const int TunnelConnectionTimeoutMilliseconds = 30000;
        private const int TunnelDisconnectTimeoutMilliseconds = 10000;
        private const int NativeQueryTimeoutMilliseconds = 5000;
        private const int MaxVisibleLogMessages = 2000;
        private const int LogUiBatchSize = 256;
        private const int ShutdownDisconnectTimeoutMilliseconds = 5000;

        /**
         * @brief The manager that handles the Wireguard connections.
         */
        private readonly TunnelLifecycleController _tunnelLifecycle;
        private readonly TunnelMonitor _tunnelMonitor;
        private readonly TunnelSessionCoordinator _tunnelSession = new TunnelSessionCoordinator();
        private readonly ProfileCatalog _profileCatalog = new ProfileCatalog();
        private readonly UiLogMessageBuffer _uiLogBuffer;

        private ConnectionState _currentState = ConnectionState.Disconnected;
        private bool _exitRequested;
        private volatile bool _shutdownComplete;
        private Icon _ownedTrayIcon;
        private Image _inactiveStatusImage;
        private Image _connectedStatusImage;

        /**
         * @brief Initializes a new instance of the Main class.
         */
        public FrmMain()
        {
            InitializeComponent();

            _uiLogBuffer = new UiLogMessageBuffer(
                MaxVisibleLogMessages,
                LogUiBatchSize,
                TryScheduleLogDrain,
                AppendWireSockLogMessages);
            _tunnelLifecycle = new TunnelLifecycleController(OnWireSockLogMessage);
            _tunnelMonitor = new TunnelMonitor(
                _tunnelLifecycle.GetConnectedAsync,
                _tunnelLifecycle.GetStateAsync,
                CurrentTunnelGeneration,
                HandleTunnelMonitorUpdateAsync,
                NativeQueryTimeoutMilliseconds,
                TunnelConnectionTimeoutMilliseconds);

            // Configure icons
            Icon = Resources.ico;
            _inactiveStatusImage = BitmapExtensions.DrawCircle(16, 15, Brushes.DarkGray);
            _connectedStatusImage = GetWindowsIconBitmap(WindowsIcons.Icons.Activated, 16);
            SetTrayIcon(Resources.ico, false);
            SetStatusImage(null, _inactiveStatusImage);

            // Populate menu items with Windows supplied icons
            ddmAddTunnel.Image = GetWindowsIconBitmap(WindowsIcons.Icons.Addtunnel, 16);
            mniImportTunnel.Image = GetWindowsIconBitmap(WindowsIcons.Icons.OpenTunnel, 16);
            mniNewTunnel.Image = GetWindowsIconBitmap(WindowsIcons.Icons.NewTunnel, 16);
            mniDeleteTunnel.Image = GetWindowsIconBitmap(WindowsIcons.Icons.DeleteTunnel, 16);
            mniSettings.Image = GetWindowsIconBitmap(WindowsIcons.Icons.Settings, 16);

            // Populate profile image list with Windows supplied icons
            imlProfiles.Images.Clear();
            AddProfileIcon(ConnectionState.Disconnected.ToString(), WindowsIcons.Icons.DisconnectedTunnel, 24);
            AddProfileIcon(ConnectionState.Connected.ToString(), WindowsIcons.Icons.ConnectedTunnel, 24);
            AddProfileIcon(ConnectionState.Connecting.ToString(), WindowsIcons.Icons.ConnectingTunnel, 24);
            AddProfileIcon(ConnectionState.Indeterminate.ToString(), WindowsIcons.Icons.ConnectingTunnel, 24);

            // Ensure the profile list rows fill the entire width, but no scrollbar appears
            lstProfiles.Columns[0].Width = lstProfiles.Size.Width - 4;

            OnLogWindowResize(lstLog, EventArgs.Empty);

            // Update the list of available configurations.
            LoadProfiles();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _uiLogBuffer?.RetryPendingDispatch();
        }

        private static Bitmap GetWindowsIconBitmap(WindowsIcons.Icons icon, int size)
        {
            using (var windowsIcon = WindowsIcons.GetWindowsIcon(icon, size))
            {
                return windowsIcon?.ToBitmap();
            }
        }

        private void AddProfileIcon(string key, WindowsIcons.Icons icon, int size)
        {
            using (var windowsIcon = WindowsIcons.GetWindowsIcon(icon, size))
            {
                if (windowsIcon != null)
                    imlProfiles.Images.Add(key, windowsIcon);
            }
        }

        private void SetTrayIcon(Icon icon, bool ownsIcon)
        {
            var previousOwnedIcon = _ownedTrayIcon;
            _ownedTrayIcon = ownsIcon ? icon : null;
            trayIcon.Icon = icon;

            if (previousOwnedIcon != null && !ReferenceEquals(previousOwnedIcon, icon))
                previousOwnedIcon.Dispose();
        }

        private void SetActivateButtonEnabled(bool enabled)
        {
            if (layoutInterface.Controls["btnActivate"] is Button btnActivate)
                btnActivate.Enabled = enabled &&
                                      !IsNativeCleanupInProgress() &&
                                      !IsNativeRecoveryRequired();
        }

        private void SetStatusImage(PictureBox imgStatus, Image image)
        {
            if (imgStatus != null)
                imgStatus.Image = image;

            cmiStatus.Image = image;
        }

        private void DisposeStatusImages()
        {
            foreach (var control in layoutInterface.Controls.Find("imgStatus", true))
                if (control is PictureBox pictureBox)
                    pictureBox.Image = null;

            cmiStatus.Image = null;
            _inactiveStatusImage?.Dispose();
            _inactiveStatusImage = null;
            _connectedStatusImage?.Dispose();
            _connectedStatusImage = null;
        }

        private void ClearDynamicLayout(TableLayoutPanel panel)
        {
            while (panel.Controls.Count > 0)
            {
                var control = panel.Controls[0];
                DetachSharedStatusImages(control);
                panel.Controls.RemoveAt(0);
                control.Dispose();
            }
        }

        private void DetachSharedStatusImages(Control control)
        {
            if (control is PictureBox pictureBox &&
                (ReferenceEquals(pictureBox.Image, _inactiveStatusImage) ||
                 ReferenceEquals(pictureBox.Image, _connectedStatusImage)))
                pictureBox.Image = null;

            foreach (Control child in control.Controls)
                DetachSharedStatusImages(child);
        }

        private bool TryGetProfileItem(string profileName, out ListViewItem profileItem)
        {
            profileItem = null;

            if (string.IsNullOrWhiteSpace(profileName) || !lstProfiles.Items.ContainsKey(profileName))
                return false;

            profileItem = lstProfiles.Items[profileName];
            return profileItem != null;
        }

        private bool IsCurrentTunnelProfile(string profileName)
        {
            return (_currentState == ConnectionState.Connected ||
                    _currentState == ConnectionState.Connecting) &&
                   string.Equals(_tunnelLifecycle.ProfileName, profileName, StringComparison.OrdinalIgnoreCase);
        }

        private int CurrentTunnelGeneration()
        {
            return _tunnelSession.CurrentGeneration;
        }

        private void AdvanceTunnelGeneration()
        {
            _tunnelSession.AdvanceGeneration();
        }

        private bool IsNativeCleanupInProgress()
        {
            return _tunnelSession.CleanupPending;
        }

        private bool IsNativeRecoveryRequired()
        {
            return _tunnelSession.RecoveryRequired;
        }

        private void BeginNativeCleanup()
        {
            _tunnelSession.BeginCleanup();
            TryRunOnUiThread(() =>
            {
                SetActivateButtonEnabled(false);
                cmiResetKillSwitch.Enabled = false;
            });
        }

        private void EndNativeCleanup(string profile)
        {
            if (!_tunnelSession.EndCleanup())
                return;

            if (_shutdownComplete || IsDisposed || Disposing)
                return;

            TryRunOnUiThread(() =>
            {
                if (_currentState == ConnectionState.Disconnected)
                {
                    SetActivateButtonEnabled(true);
                    cmiResetKillSwitch.Enabled = true;
                    if (TryGetProfileItem(profile, out var profileItem))
                        profileItem.ImageKey = ConnectionState.Disconnected.ToString();
                }
                else if (_currentState == ConnectionState.Indeterminate)
                {
                    SetActivateButtonEnabled(false);
                    cmiResetKillSwitch.Enabled = true;
                }
            });
        }

        private void MarkNativeRecoveryRequired(string profile, string context,
            NativeRecoveryMarkerLease markerLease = null)
        {
            const string diagnostic =
                "Native WireSock cleanup did not finish safely. New tunnel operations are disabled until recovery succeeds or WireSock UI is restarted.";
            WriteOrUpdateNativeRecoveryMarker(markerLease, context, diagnostic);

            var wasAlreadyMarked = !_tunnelSession.RequireRecovery();
            Trace.TraceWarning(
                $"Native WireSock cleanup did not finish safely after {context}. New tunnel operations are disabled until recovery succeeds or WireSock UI is restarted.");

            TryRunOnUiThread(() =>
            {
                if (!IsNativeRecoveryRequired())
                    return;

                SetNativeRecoveryUi(profile);

                if (!wasAlreadyMarked)
                    MessageBox.Show(Resources.TunnelNativeRecoveryRequired, Resources.TunnelErrorTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
        }

        private static NativeRecoveryMarkerLease WriteOrUpdateNativeRecoveryMarker(
            NativeRecoveryMarkerLease markerLease,
            string context,
            string diagnostic)
        {
            if (markerLease == null)
                return Global.NativeRecoveryMarkers.Write(context, diagnostic);

            Global.NativeRecoveryMarkers.TryUpdate(markerLease, context, diagnostic);
            return markerLease;
        }

        private async Task HandleNativeCleanupFailureAsync(string profile, string context, string diagnostic = null,
            NativeRecoveryMarkerLease markerLease = null)
        {
            if (!string.IsNullOrWhiteSpace(diagnostic))
                Trace.TraceWarning($"Native cleanup failure after {context}: {diagnostic}");

            await TryResetNetworkLockAfterNativeCleanupFailureAsync(context);
            MarkNativeRecoveryRequired(profile, string.IsNullOrWhiteSpace(diagnostic)
                    ? context
                    : $"{context}: {diagnostic}",
                markerLease);
        }

        private void SetNativeRecoveryUi(string profile)
        {
            if (_shutdownComplete || IsDisposed || Disposing)
                return;

            UpdateState(ConnectionState.Indeterminate, false, profile);
        }

        private void ShowTunnelOperationBlockedMessage(string message)
        {
            if (_shutdownComplete || IsDisposed || Disposing)
                return;

            MessageBox.Show(message, Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void CancelTunnelMonitoring()
        {
            AdvanceTunnelGeneration();
            _tunnelMonitor.Cancel();
        }

        private bool TryBeginTunnelOperation(bool showBlockedMessage = true)
        {
            if (_tunnelSession.TryBeginOperation(out var blockReason))
                return true;

            if (showBlockedMessage)
            {
                var message = blockReason == TunnelOperationBlockReason.CleanupPending
                    ? Resources.TunnelConnectCleanupPending
                    : blockReason == TunnelOperationBlockReason.RecoveryRequired
                        ? Resources.TunnelNativeRecoveryRequired
                        : Resources.TunnelOperationPending;
                ShowTunnelOperationBlockedMessage(message);
            }

            return false;
        }

        private void EndTunnelOperation()
        {
            _tunnelSession.EndOperation();
        }

        private void StartTunnelConnectionMonitor()
        {
            _tunnelMonitor.StartConnecting(CurrentTunnelGeneration());
        }

        private void StartTunnelStateMonitor()
        {
            _tunnelMonitor.StartConnected(CurrentTunnelGeneration());
        }

        private async Task<bool> DisconnectCurrentTunnelAsync(bool notify = true, bool preserveNetworkLock = false)
        {
            var profileName = _tunnelLifecycle.ProfileName;

            CancelTunnelMonitoring();

            if (await DisconnectNativeTunnelAsync(preserveNetworkLock: preserveNetworkLock))
            {
                UpdateState(ConnectionState.Disconnected, notify, profileName);
                return true;
            }

            if (IsNativeCleanupInProgress())
                return false;

            if (_tunnelLifecycle.HasTunnelHandle)
                await HandleNativeCleanupFailureAsync(profileName, "tunnel disconnect cleanup",
                    _tunnelLifecycle.LastError);
            else
                UpdateState(ConnectionState.Disconnected, notify, profileName);

            return false;
        }

        private async Task<bool> DisconnectNativeTunnelAsync(long? connectionSequence = null, bool showWarning = true,
            bool preserveNetworkLock = false)
        {
            var profile = _tunnelLifecycle.ProfileName;
            var result = await _tunnelLifecycle.DisconnectAsync(connectionSequence, preserveNetworkLock,
                TunnelDisconnectTimeoutMilliseconds);
            if (result.TimedOut)
            {
                BeginNativeCleanup();
                var markerLease = Global.NativeRecoveryMarkers.Write("tunnel disconnect timeout",
                    $"The native disconnect call for profile '{profile}' did not return within {TunnelDisconnectTimeoutMilliseconds} ms.");
                SetNativeRecoveryUi(profile);
                ScheduleTimedOutDisconnectCleanup(result.PendingCompletion, profile, preserveNetworkLock, markerLease);

                if (showWarning)
                    MessageBox.Show(Resources.TunnelDisconnectTimeout, Resources.TunnelErrorTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (!result.Succeeded && showWarning)
                MessageBox.Show(result.Diagnostic ?? Resources.TunnelHandleReleaseWarning, Resources.TunnelErrorTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return result.Succeeded;
        }

        private void ScheduleTimedOutDisconnectCleanup(Task<NativeOperationResult<bool>> pendingCompletion,
            string profile, bool preservedNetworkLock, NativeRecoveryMarkerLease markerLease)
        {
            var cleanupTask = CompleteTimedOutDisconnectCleanupAsync(pendingCompletion, profile,
                preservedNetworkLock, markerLease);
            cleanupTask.ContinueWith(task =>
                    Trace.TraceWarning(
                        $"Unhandled timed-out disconnect cleanup error: {task.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private async Task CompleteTimedOutDisconnectCleanupAsync(
            Task<NativeOperationResult<bool>> pendingCompletion, string profile, bool preservedNetworkLock,
            NativeRecoveryMarkerLease markerLease)
        {
            var cleanupFailed = false;
            string diagnostic = null;

            try
            {
                try
                {
                    var result = NativeOperationRecoveryPolicy.NormalizeCompletion(
                        await pendingCompletion.ConfigureAwait(false), "native disconnect cleanup");
                    cleanupFailed = !result.Succeeded || _tunnelLifecycle.HasTunnelHandle;
                    diagnostic = result.Diagnostic;

                    if (!cleanupFailed && preservedNetworkLock)
                    {
                        var resetResult = await _tunnelLifecycle.ResetNetworkLockAsync(NativeQueryTimeoutMilliseconds)
                            .ConfigureAwait(false);
                        cleanupFailed = !resetResult.Succeeded;
                        diagnostic = resetResult.Diagnostic;
                    }
                }
                catch (Exception ex)
                {
                    cleanupFailed = true;
                    diagnostic = ex.Message;
                }

                if (cleanupFailed)
                {
                    await HandleNativeCleanupFailureAsync(profile, "timed-out disconnect cleanup", diagnostic,
                            markerLease)
                        .ConfigureAwait(false);
                }
                else
                {
                    Global.NativeRecoveryMarkers.TryDelete(markerLease);
                    TryRunOnUiThread(() => UpdateState(ConnectionState.Disconnected, false, profile));
                }
            }
            finally
            {
                EndNativeCleanup(profile);
            }
        }

        private void Shutdown()
        {
            if (_shutdownComplete)
                return;

            _shutdownComplete = true;
            _currentState = ConnectionState.Disconnected;

            _uiLogBuffer.Dispose();
            _tunnelMonitor.Dispose();

            DisposeTunnelLifecycleWithTimeout();

            trayIcon.Visible = false;
            SetTrayIcon(null, false);
            DisposeStatusImages();

            // Keep the global ownership event alive until process termination. This prevents another
            // direct SDK client from starting while a timed-out native cleanup may still be unwinding.
        }

        private void DisposeTunnelLifecycleWithTimeout()
        {
            if (_tunnelLifecycle == null)
                return;

            try
            {
                var cleanupTask = _tunnelLifecycle.ShutdownAsync(ShutdownDisconnectTimeoutMilliseconds);
                var cleanupResult = cleanupTask.GetAwaiter().GetResult();
                if (cleanupResult.TimedOut)
                {
                    Trace.TraceWarning(
                        $"WireSock manager shutdown exceeded {ShutdownDisconnectTimeoutMilliseconds} ms; continuing application exit.");
                    var markerLease = Global.NativeRecoveryMarkers.Write("shutdown timeout",
                        "The native cleanup call was still running when WireSock UI exited. No concurrent global reset was attempted.");

                    cleanupResult.PendingCompletion.ContinueWith(task =>
                        {
                            if (task.IsFaulted)
                            {
                                Trace.TraceWarning(
                                    $"WireSock manager shutdown completed with an error after exit continued: {task.Exception?.GetBaseException().Message}");
                                WriteOrUpdateNativeRecoveryMarker(markerLease, "shutdown cleanup failure",
                                    task.Exception?.GetBaseException().Message);
                                return;
                            }

                            if (task.IsCanceled)
                            {
                                WriteOrUpdateNativeRecoveryMarker(markerLease, "shutdown cleanup failure",
                                    "The native shutdown cleanup was canceled.");
                                return;
                            }

                            var completedResult = NativeOperationRecoveryPolicy.NormalizeCompletion(
                                task.Result, "native shutdown cleanup");
                            if (!completedResult.Succeeded)
                            {
                                WriteOrUpdateNativeRecoveryMarker(markerLease, "shutdown cleanup failure",
                                    completedResult.Diagnostic);
                                return;
                            }

                            Global.NativeRecoveryMarkers.TryDelete(markerLease);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }
                else if (!cleanupResult.Succeeded)
                {
                    Global.NativeRecoveryMarkers.Write("shutdown cleanup failure",
                        cleanupResult.Diagnostic);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to cleanly shut down WireSock manager: {ex.Message}");
                Global.NativeRecoveryMarkers.Write("shutdown cleanup failure", ex.Message);
            }
        }

        private async Task<bool> TryResetNetworkLockAfterNativeCleanupFailureAsync(string context,
            NativeRecoveryMarkerSnapshot markerToClear = null, bool recordRecoveryMarkerOnFailure = false)
        {
            try
            {
                var queryResult = await _tunnelLifecycle.QueryNetworkLockAsync(NativeQueryTimeoutMilliseconds);
                if (!queryResult.Succeeded)
                {
                    TrackTimedOutNativeOperation(queryResult, context);
                    var diagnostic = queryResult.Diagnostic ?? "Unable to query WireSock network lock state.";
                    Trace.TraceWarning(
                        $"Unable to query WireSock network lock after {context}: {diagnostic}");
                    if (recordRecoveryMarkerOnFailure)
                        Global.NativeRecoveryMarkers.Write(context, diagnostic);
                    return false;
                }

                if (!queryResult.Value)
                {
                    if (recordRecoveryMarkerOnFailure)
                        Global.NativeRecoveryMarkers.TryDelete(markerToClear);
                    return true;
                }

                var resetResult = await _tunnelLifecycle.ResetNetworkLockAsync(NativeQueryTimeoutMilliseconds);
                if (!resetResult.Succeeded)
                {
                    TrackTimedOutNativeOperation(resetResult, context);
                    var diagnostic = resetResult.Diagnostic ?? "Unable to reset WireSock network lock.";
                    Trace.TraceWarning(
                        $"Unable to reset WireSock network lock after {context}: {diagnostic}");
                    if (recordRecoveryMarkerOnFailure)
                        Global.NativeRecoveryMarkers.Write(context, diagnostic);
                    return false;
                }

                if (recordRecoveryMarkerOnFailure)
                    Global.NativeRecoveryMarkers.TryDelete(markerToClear);
                return true;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to reset WireSock network lock after {context}: {ex.Message}");
                if (recordRecoveryMarkerOnFailure)
                    Global.NativeRecoveryMarkers.Write(context, ex.Message);
                return false;
            }
        }

        private void TrackTimedOutNativeOperation<T>(NativeOperationResult<T> result, string context)
        {
            if (!result.TimedOut || result.PendingCompletion == null)
                return;

            var profile = _tunnelLifecycle.ProfileName;
            BeginNativeCleanup();
            result.PendingCompletion.ContinueWith(task =>
                {
                    try
                    {
                        if (task.IsFaulted)
                            Trace.TraceWarning(
                                $"Timed-out native operation after {context} faulted: {task.Exception?.GetBaseException().Message}");
                        else if (task.IsCanceled)
                            Trace.TraceWarning($"Timed-out native operation after {context} was canceled.");
                        else
                        {
                            var completedResult = NativeOperationRecoveryPolicy.NormalizeCompletion(
                                task.Result, context);
                            if (!completedResult.Succeeded)
                                Trace.TraceWarning(
                                    $"Timed-out native operation after {context} failed: {completedResult.Diagnostic}");
                        }
                    }
                    finally
                    {
                        EndNativeCleanup(profile);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        private async Task ShowPendingNativeRecoveryWarningAsync()
        {
            var markerSnapshot = Global.NativeRecoveryMarkers.Capture();
            var marker = markerSnapshot?.Contents;

            var queryResult = await _tunnelLifecycle.QueryNetworkLockAsync(NativeQueryTimeoutMilliseconds);
            if (!queryResult.Succeeded)
            {
                TrackTimedOutNativeOperation(queryResult, "startup network-lock query");
                var diagnostic = queryResult.Diagnostic ?? "Unable to query WireSock network lock state.";
                Global.NativeRecoveryMarkers.Write("startup network-lock query", diagnostic);
                _tunnelSession.RequireRecovery();
                SetNativeRecoveryUi(null);
                MessageBox.Show(
                    $"WireSock UI could not verify the driver network-lock state.{Environment.NewLine}{Environment.NewLine}{diagnostic}",
                    Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!queryResult.Value)
            {
                Global.NativeRecoveryMarkers.TryDelete(markerSnapshot);
                return;
            }

            var recovered = await TryResetNetworkLockAfterNativeCleanupFailureAsync(
                "startup recovery", markerSnapshot, true);
            if (!recovered)
            {
                _tunnelSession.RequireRecovery();
                SetNativeRecoveryUi(null);
            }

            var recoveryStatus = recovered
                ? "Startup recovery completed successfully. WireSock UI will continue normally."
                : "Startup recovery could not verify or reset the WireSock Kill Switch. Tunnel operations are disabled until WireSock UI is restarted after network access is restored.";

            var recoveryContext = string.IsNullOrWhiteSpace(marker)
                ? "The WireSock driver reported an orphaned network lock."
                : marker;
            var recoveryMessage =
                $"{Resources.TunnelNativeRecoveryRequired}{Environment.NewLine}{Environment.NewLine}{recoveryStatus}{Environment.NewLine}{Environment.NewLine}{recoveryContext}";

            TryRunOnUiThread(() => MessageBox.Show(
                recoveryMessage,
                Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }

        private void TryRunOnUiThread(Action action)
        {
            if (_shutdownComplete || IsDisposed || Disposing)
                return;

            try
            {
                if (!IsHandleCreated)
                    return;

                if (InvokeRequired)
                    BeginInvoke(new Action(() => RunUiActionSafely(action)));
                else
                    RunUiActionSafely(action);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static void RunUiActionSafely(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to run WireSock UI callback: {ex.Message}");
            }
        }

#if WIRESOCKUI_ENABLE_UWP
        private void ScheduleVersionCheck()
        {
            Task.Run(() =>
                {
                    return Program.TryGetAvailableUpdate(out var releasesUrl)
                        ? releasesUrl
                        : null;
                })
                .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Trace.TraceWarning(
                                $"Unable to check for WireSock UI updates: {task.Exception?.GetBaseException().Message}");
                            return;
                        }

                        if (string.IsNullOrWhiteSpace(task.Result))
                            return;

                        TryRunOnUiThread(() =>
                        {
                            if (MessageBox.Show(this, Resources.AppUpdateMessage, Resources.AppUpdateTitle,
                                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
                                Program.OpenBrowser(task.Result);
                        });
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
        }

        private void TryShowNotification(string title, string body)
        {
            try
            {
                Notifications.Notifications.Notify(title, body, this);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unable to show WireSock UI notification: {ex.Message}");
            }
        }
#endif

        /// <summary>
        ///     Determines if another instance of the current application is already running.
        /// </summary>
        /// <returns>
        ///     A boolean value that is true if another instance of the application is already running,
        ///     and false if the current instance is the only one running.
        /// </returns>
        /// <remarks>
        ///     This function uses the same named event as the direct WireSock C++ CLI/service so only one
        ///     direct SDK tunnel owner is active at a time.
        /// </remarks>
        internal static bool IsApplicationAlreadyRunning()
        {
            const string eventName = "Global\\WiresockClientService";

            try
            {
                Global.AlreadyRunning =
                    new EventWaitHandle(
                        false,
                        EventResetMode.AutoReset,
                        eventName,
                        out var createdNew,
                        CreateSingleInstanceEventSecurity());

                if (createdNew) return false;

                if (!TryValidateSingleInstanceEventSecurity(Global.AlreadyRunning, out var diagnostic))
                {
                    Global.AlreadyRunning.Dispose();
                    Global.AlreadyRunning = null;
                    throw new InvalidOperationException(diagnostic);
                }

                Global.AlreadyRunning.Dispose();
                Global.AlreadyRunning = null;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "The global WireSock ownership event exists but its security descriptor denies elevated access. Close the process that pre-created it and retry.");
            }
            catch (IOException)
            {
                throw new InvalidOperationException(
                    "The global WireSock ownership event could not be opened safely. Close other WireSock clients and retry.");
            }
        }

        private static EventWaitHandleSecurity CreateSingleInstanceEventSecurity()
        {
            var security = new EventWaitHandleSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                EventWaitHandleRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                EventWaitHandleRights.FullControl,
                AccessControlType.Allow));
            return security;
        }

        private static bool TryValidateSingleInstanceEventSecurity(EventWaitHandle waitHandle,
            out string diagnostic)
        {
            diagnostic = null;
            try
            {
                return IsSingleInstanceEventSecurityTrusted(
                    waitHandle.GetAccessControl(),
                    WindowsIdentity.GetCurrent().User,
                    out diagnostic);
            }
            catch (Exception ex)
            {
                diagnostic = $"Unable to validate the global WireSock ownership event: {ex.Message}";
                return false;
            }
        }

        internal static bool IsSingleInstanceEventSecurityTrusted(EventWaitHandleSecurity security,
            SecurityIdentifier currentUser, out string diagnostic)
        {
            diagnostic = null;
            if (security == null || currentUser == null)
            {
                diagnostic = "The global WireSock ownership event has no verifiable security descriptor.";
                return false;
            }

            if (!(security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner) ||
                (!owner.Equals(currentUser) && !Program.IsTrustedOwnerSid(owner)))
            {
                diagnostic = "The global WireSock ownership event is not owned by the current administrator, " +
                             "the Administrators group, or LocalSystem.";
                return false;
            }

            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (EventWaitHandleAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow ||
                    !(rule.IdentityReference is SecurityIdentifier sid))
                    continue;

                if (sid.Equals(currentUser) || Program.IsTrustedAdministrativeSid(sid))
                    continue;

                diagnostic =
                    $"The global WireSock ownership event grants access to an untrusted identity ({sid.Value}).";
                return false;
            }

            return true;
        }

        private Task HandleTunnelMonitorUpdateAsync(TunnelMonitorUpdate update)
        {
            if (_shutdownComplete || IsDisposed || Disposing || !IsHandleCreated)
                return Task.CompletedTask;

            return InvokeOnUiThreadAsync(this, () => HandleTunnelMonitorUpdateOnUiThreadAsync(update));
        }

        internal static Task InvokeOnUiThreadAsync(ISynchronizeInvoke synchronizer, Func<Task> action)
        {
            if (synchronizer == null)
                throw new ArgumentNullException(nameof(synchronizer));
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            bool invokeRequired;
            try
            {
                invokeRequired = synchronizer.InvokeRequired;
            }
            catch (ObjectDisposedException)
            {
                return Task.CompletedTask;
            }
            catch (InvalidOperationException)
            {
                return Task.CompletedTask;
            }

            if (!invokeRequired)
                return action();

            var completion = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                synchronizer.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await action();
                        completion.TrySetResult(null);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }), Array.Empty<object>());
                return completion.Task;
            }
            catch (ObjectDisposedException)
            {
                return Task.CompletedTask;
            }
            catch (InvalidOperationException)
            {
                return Task.CompletedTask;
            }
        }

        private async Task HandleTunnelMonitorUpdateOnUiThreadAsync(TunnelMonitorUpdate update)
        {
            try
            {
                if (update == null || _shutdownComplete || IsDisposed || Disposing ||
                    update.Generation != CurrentTunnelGeneration())
                    return;

                switch (update.Kind)
                {
                    case TunnelMonitorUpdateKind.Connected:
                        if (_currentState == ConnectionState.Connecting)
                            UpdateState(ConnectionState.Connected);
                        return;
                    case TunnelMonitorUpdateKind.ConnectionTimedOut:
                        if (_currentState == ConnectionState.Connecting)
                            await HandleTunnelConnectionTimeoutAsync(update.Generation);
                        return;
                    case TunnelMonitorUpdateKind.TunnelInactive:
                        if (_currentState == ConnectionState.Connected)
                            await HandleTunnelInactiveAsync(update.Generation);
                        return;
                    case TunnelMonitorUpdateKind.Statistics:
                        if (_currentState == ConnectionState.Connected)
                            ApplyTunnelStatistics(update.Statistics);
                        return;
                    case TunnelMonitorUpdateKind.QueryFailed:
                        if (update.ConnectionQuery != null)
                        {
                            await HandleTunnelMonitorQueryFailureAsync(
                                update.Generation,
                                update.ConnectionQuery,
                                "tunnel active-state monitor");
                        }
                        else if (update.StatisticsQuery != null)
                        {
                            await HandleTunnelMonitorQueryFailureAsync(
                                update.Generation,
                                update.StatisticsQuery,
                                "tunnel statistics monitor");
                        }

                        return;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Tunnel monitor update handling failed unexpectedly: {ex.Message}");
            }
        }

        private async Task HandleTunnelMonitorQueryFailureAsync<T>(int generation,
            NativeOperationResult<T> result, string context)
        {
            var diagnostic = result.Diagnostic ?? $"The native {context} query failed.";
            if (result.TimedOut)
            {
                TrackTimedOutNativeOperation(result, context);
                MarkNativeRecoveryRequired(_tunnelLifecycle.ProfileName, $"{context}: {diagnostic}");
                return;
            }

            await HandleTunnelMonitorFailureAsync(generation, diagnostic);
        }

        private void ApplyTunnelStatistics(WgbStats stats)
        {
            if (layoutState.Controls["txtHandshake"] is TextBox txtHandshake)
                txtHandshake.Text = stats.time_since_last_handshake.AsHandshakeAge();

            if (layoutState.Controls["txtTransfer"] is TextBox txtTransfer)
                txtTransfer.Text = string.Format(Resources.StateTransferValue,
                    stats.rx_bytes.AsHumanReadable(),
                    stats.tx_bytes.AsHumanReadable());

            if (layoutState.Controls["txtRTT"] is TextBox txtRtt)
                txtRtt.Text = string.Format(Resources.StateRTTValue, stats.estimated_rtt);

            if (layoutState.Controls["txtLoss"] is TextBox txtLoss)
                txtLoss.Text = string.Format(Resources.StateLossValue, stats.estimated_loss * 100);
        }

        private async Task HandleTunnelMonitorFailureAsync(int generation, string diagnostic)
        {
            try
            {
                Trace.TraceWarning(diagnostic);
                if (_shutdownComplete || IsDisposed || Disposing)
                    return;

                await HandleTunnelQueryFailureAsync(generation, diagnostic);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Unable to recover from a tunnel monitor failure: {ex.Message}");
            }
        }

        private async Task HandleTunnelQueryFailureAsync(int generation, string diagnostic)
        {
            while (!TryBeginTunnelOperation(false))
            {
                if (ShouldStopTunnelFailureHandling(generation))
                    return;

                await Task.Delay(100);
            }

            try
            {
                if (ShouldStopTunnelFailureHandling(generation))
                    return;

                Trace.TraceWarning($"Native tunnel state query failed: {diagnostic}");
                if (!await DisconnectCurrentTunnelAsync(false))
                    return;

                if (!_shutdownComplete)
                    MessageBox.Show(
                        $"WireSock UI could not query the native tunnel state.{Environment.NewLine}{Environment.NewLine}{diagnostic}",
                        Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                EndTunnelOperation();
            }
        }

        private bool ShouldStopTunnelFailureHandling(int generation)
        {
            return _shutdownComplete || IsDisposed || Disposing || IsNativeRecoveryRequired() ||
                   generation != CurrentTunnelGeneration() ||
                   _currentState == ConnectionState.Disconnected ||
                   _currentState == ConnectionState.Indeterminate;
        }

        private async Task HandleTunnelConnectionTimeoutAsync(int generation)
        {
            if (!RequestTunnelConnectionTimeout(generation) || !TryBeginTunnelOperation(false))
                return;

            try
            {
                try
                {
                    await DisconnectTimedOutTunnelAsync(generation);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Failed to handle tunnel connection timeout: {ex.Message}");
                }
            }
            finally
            {
                EndTunnelOperation();
            }
        }

        private bool RequestTunnelConnectionTimeout(int generation)
        {
            if (_shutdownComplete || generation != CurrentTunnelGeneration() ||
                _currentState != ConnectionState.Connecting ||
                IsTunnelConnectionTimedOut(generation))
                return false;

            return _tunnelSession.TryMarkConnectionTimedOut(generation);
        }

        private bool IsTunnelConnectionTimedOut(int generation)
        {
            return _tunnelSession.IsConnectionTimedOut(generation);
        }

        private async Task DisconnectTimedOutTunnelAsync(int generation)
        {
            if (_shutdownComplete || generation != CurrentTunnelGeneration() ||
                _currentState != ConnectionState.Connecting || !IsTunnelConnectionTimedOut(generation))
                return;

            if (!await DisconnectCurrentTunnelAsync(false))
                return;

            if (!_shutdownComplete)
                MessageBox.Show(Resources.TunnelConnectTimeout, Resources.TunnelErrorTitle, MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
        }

        private async Task<TunnelConnectionResult> ConnectWithTimeoutAsync(string profile, int generation,
            bool preservedNetworkLock)
        {
            var connectResult = await _tunnelLifecycle.ConnectAsync(profile, preservedNetworkLock,
                TunnelConnectionTimeoutMilliseconds);
            if (!connectResult.TimedOut)
            {
                return connectResult.Value ?? new TunnelConnectionResult
                {
                    Diagnostic = connectResult.Diagnostic
                };
            }

            BeginNativeCleanup();
            var markerLease = Global.NativeRecoveryMarkers.Write("tunnel connect timeout",
                $"The native connect call for profile '{profile}' did not return within {TunnelConnectionTimeoutMilliseconds} ms.");
            var cleanupUiGeneration = generation;

            if (!_shutdownComplete && generation == CurrentTunnelGeneration() &&
                _currentState == ConnectionState.Connecting)
            {
                _tunnelSession.TryMarkConnectionTimedOut(generation);
                UpdateState(ConnectionState.Indeterminate, false, profile);
                cleanupUiGeneration = CurrentTunnelGeneration();
                SetActivateButtonEnabled(false);
                MessageBox.Show(Resources.TunnelConnectTimeout, Resources.TunnelErrorTitle, MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            ScheduleTimedOutConnectCleanup(connectResult.PendingCompletion, cleanupUiGeneration, profile,
                markerLease);

            return null;
        }

        private void ScheduleTimedOutConnectCleanup(
            Task<NativeOperationResult<TunnelConnectionResult>> pendingCompletion, int generation, string profile,
            NativeRecoveryMarkerLease markerLease)
        {
            var cleanupTask = CompleteTimedOutConnectCleanupAsync(pendingCompletion, generation, profile, markerLease);
            cleanupTask.ContinueWith(faultedTask =>
                    Trace.TraceWarning(
                        $"Unhandled timed-out tunnel cleanup error: {faultedTask.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private async Task CompleteTimedOutConnectCleanupAsync(
            Task<NativeOperationResult<TunnelConnectionResult>> pendingCompletion, int generation, string profile,
            NativeRecoveryMarkerLease markerLease)
        {
            var cleanupFailed = false;
            var cleanupDelegated = false;
            string diagnostic = null;

            try
            {
                var connectResult = NativeOperationRecoveryPolicy.NormalizeCompletion(
                    await pendingCompletion.ConfigureAwait(false), "native tunnel connect");
                var result = connectResult.Value;
                diagnostic = connectResult.Diagnostic ?? result?.Diagnostic;

                if (connectResult.Succeeded && result?.Connected == true)
                {
                    var disconnectResult = await _tunnelLifecycle.DisconnectAsync(result.ConnectionSequence, false,
                        TunnelDisconnectTimeoutMilliseconds).ConfigureAwait(false);
                    if (disconnectResult.TimedOut)
                    {
                        cleanupDelegated = true;
                        markerLease = WriteOrUpdateNativeRecoveryMarker(markerLease, "tunnel disconnect timeout",
                            $"The native disconnect cleanup for profile '{profile}' did not return within {TunnelDisconnectTimeoutMilliseconds} ms.");
                        ScheduleTimedOutDisconnectCleanup(disconnectResult.PendingCompletion, profile, false,
                            markerLease);
                        return;
                    }

                    cleanupFailed = !disconnectResult.Succeeded;
                    diagnostic = disconnectResult.Diagnostic;
                }
                else if (_tunnelLifecycle.HasTunnelHandle)
                {
                    var disconnectResult = await _tunnelLifecycle.DisconnectAsync(null, false,
                        TunnelDisconnectTimeoutMilliseconds).ConfigureAwait(false);
                    if (disconnectResult.TimedOut)
                    {
                        cleanupDelegated = true;
                        markerLease = WriteOrUpdateNativeRecoveryMarker(markerLease, "tunnel disconnect timeout",
                            $"The native disconnect cleanup for profile '{profile}' did not return within {TunnelDisconnectTimeoutMilliseconds} ms.");
                        ScheduleTimedOutDisconnectCleanup(disconnectResult.PendingCompletion, profile, false,
                            markerLease);
                        return;
                    }

                    cleanupFailed = !disconnectResult.Succeeded || _tunnelLifecycle.HasTunnelHandle;
                    diagnostic = disconnectResult.Diagnostic;
                }
                else if (result?.RecoveryRequired == true)
                {
                    cleanupFailed = true;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to clean up timed-out tunnel connect: {ex.Message}");
                cleanupFailed = true;
            }
            finally
            {
                if (!cleanupDelegated && cleanupFailed)
                {
                    await HandleNativeCleanupFailureAsync(profile, "timed-out connect cleanup", diagnostic,
                            markerLease)
                        .ConfigureAwait(false);
                }
                else if (!cleanupDelegated)
                {
                    Global.NativeRecoveryMarkers.TryDelete(markerLease);
                }

                if (!cleanupDelegated)
                    EndNativeCleanup(profile);
            }

            TryRunOnUiThread(() =>
            {
                if (!cleanupDelegated && !cleanupFailed && !IsNativeRecoveryRequired() && !_shutdownComplete &&
                    generation == CurrentTunnelGeneration() &&
                    (_currentState == ConnectionState.Connecting ||
                     _currentState == ConnectionState.Indeterminate))
                    UpdateState(ConnectionState.Disconnected, false, profile);
            });
        }

        private async Task HandleTunnelInactiveAsync(int generation)
        {
            while (!TryBeginTunnelOperation(false))
            {
                if (ShouldStopTunnelFailureHandling(generation))
                    return;

                await Task.Delay(100);
            }

            try
            {
                if (ShouldStopTunnelFailureHandling(generation))
                    return;

                Trace.TraceWarning("The native tunnel became inactive unexpectedly; releasing its SDK handle.");
                if (!await DisconnectCurrentTunnelAsync())
                    return;

                if (!_shutdownComplete)
                    MessageBox.Show(
                        Resources.ResourceManager.GetString("TunnelStoppedUnexpectedly") ??
                        "The tunnel stopped unexpectedly. WireSock UI released its native resources.",
                        Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                EndTunnelOperation();
            }
        }

        /// <summary>
        ///     Reload profile list and optionally pre-select a profile
        /// </summary>
        /// <param name="selectedProfile">Optional profile to automatically select</param>
        private void LoadProfiles(string selectedProfile = "")
        {
            var catalogResult = _profileCatalog.Load();
            if (!catalogResult.Succeeded)
            {
                MessageBox.Show(string.Format(Resources.ProfileEnumerationError, catalogResult.Exception.Message),
                    Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var profiles = catalogResult.Profiles;
            lstProfiles.Items.Clear();

            lstProfiles.Items.AddRange(profiles
                .Select(p => new ListViewItem(p, ConnectionState.Disconnected.ToString()) { Name = p }).ToArray());

            // Clear any previously loaded tunnels
            for (var i = mnuContext.Items.Count - 1; i >= 0; i--)
            {
                var item = mnuContext.Items[i];

                if (Equals(item.Tag, "tunnel"))
                    mnuContext.Items.Remove(item);
            }

            if (profiles.Any())
            {
                var insertIndex = mnuContext.Items.IndexOf(cmiSepTunnels);

                mnuContext.Items.Insert(insertIndex + 1, new ToolStripSeparator { Tag = "tunnel" });

                foreach (var profile in profiles.Reverse<string>())
                {
                    var item = new ToolStripMenuItem(profile) { Tag = "tunnel", Text = profile };
                    item.Click += (s, e) =>
                    {
                        lstProfiles.Items[item.Text].Selected = true;
                        OnProfileClick(lstProfiles, EventArgs.Empty);
                    };

                    mnuContext.Items.Insert(insertIndex + 1, item);
                }
            }

            if (lstProfiles.Items.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(selectedProfile))
                {
                    var profile = lstProfiles.Items[selectedProfile];

                    if (profile != null)
                    {
                        profile.Selected = true;
                        return;
                    }
                }

                lstProfiles.Items[0].Selected = true;
            }
        }

        /// <summary>
        ///     Update the connection state of the WireSock tunnel
        /// </summary>
        /// <param name="state">
        ///     <see cref="T:ConnectionState" />
        /// </param>
        /// <param name="notify">
        ///     <c>true</c> if a toast notification should be triggered, otherwise <c>false</c>
        /// </param>
        /// <remarks>This updates both the actual tunnel state and all related UI elements.</remarks>
        private void UpdateState(ConnectionState state, bool notify = true, string profileName = null)
        {
            _currentState = state;
            var activeProfileName = profileName ?? _tunnelLifecycle?.ProfileName;

            var btnActivate = layoutInterface.Controls["btnActivate"] as Button;
            var imgStatus = layoutInterface.Controls.Find("imgStatus", true).FirstOrDefault() as PictureBox;
            var txtStatus = layoutInterface.Controls.Find("txtStatus", true).FirstOrDefault() as TextBox;
            var txtAddresses = layoutInterface.Controls["txtAddresses"] as TextBox;

            switch (state)
            {
                case ConnectionState.Connecting:
                    if (btnActivate != null)
                    {
                        btnActivate.Text = Resources.ButtonActivating;
                        SetActivateButtonEnabled(false);
                    }

                    imgStatus?.Focus();

                    cmiDeactivateTunnel.Enabled = false;

                    if (TryGetProfileItem(activeProfileName, out var connectingProfile))
                        connectingProfile.ImageKey = ConnectionState.Connecting.ToString();

                    trayIcon.Text = Resources.TrayActivating;

                    cmiResetKillSwitch.Enabled = false;

                    StartTunnelConnectionMonitor();
                    break;
                case ConnectionState.Connected:
                    if (btnActivate != null)
                    {
                        btnActivate.Text = Resources.ButtonActive;
                        SetActivateButtonEnabled(true);
                    }

                    if (imgStatus != null)
                    {
                        SetStatusImage(imgStatus, _connectedStatusImage);
                        if (txtStatus != null) txtStatus.Text = Resources.InterfaceStatusActive;

                        SetTrayIcon(Resources.ico.SuperImpose(64, WindowsIcons.Icons.Activated, 48, 24), true);
                        trayIcon.Text = Resources.TrayActive;
                    }

                    cmiStatus.Text = Resources.ContextMenuActive;

                    if (txtAddresses != null) cmiAddresses.Text = txtAddresses.Text;
                    cmiAddresses.Visible = true;

                    cmiDeactivateTunnel.Enabled = true;
                    cmiResetKillSwitch.Enabled = false;

                    foreach (ToolStripItem item in mnuContext.Items)
                        if (item is ToolStripMenuItem menuItem && Equals(menuItem.Tag, "tunnel"))
                            menuItem.Checked = menuItem.Text == activeProfileName;

                    if (TryGetProfileItem(activeProfileName, out var connectedProfile))
                        connectedProfile.ImageKey = ConnectionState.Connected.ToString();

                    if (!string.IsNullOrWhiteSpace(activeProfileName))
                    {
                        try
                        {
                            PersistLastActiveProfile(activeProfileName);
                        }
                        catch (Exception ex)
                        {
                            Trace.TraceWarning($"Failed to save last active profile setting: {ex.Message}");
                        }
                    }

                    gbxState.Visible = true;

                    StartTunnelStateMonitor();

#if WIRESOCKUI_ENABLE_UWP
                    if (notify && Settings.Default.EnableNotifications)
                        TryShowNotification(Resources.ToastActiveTitle,
                            string.Format(Resources.ToastActiveMessage, activeProfileName));
#endif
                    break;
                case ConnectionState.Disconnected:
                    CancelTunnelMonitoring();

                    if (btnActivate != null)
                    {
                        btnActivate.Text = Resources.ButtonInactive;
                        SetActivateButtonEnabled(true);
                    }

                    if (imgStatus != null)
                    {
                        SetStatusImage(imgStatus, _inactiveStatusImage);
                        if (txtStatus != null) txtStatus.Text = Resources.InterfaceStatusInactive;

                        SetTrayIcon(Resources.ico, false);
                        trayIcon.Text = Resources.TrayInactive;
                    }

                    cmiStatus.Text = Resources.ContextMenuInactive;

                    cmiAddresses.Text = string.Empty;
                    cmiAddresses.Visible = false;

                    cmiDeactivateTunnel.Enabled = false;
                    cmiResetKillSwitch.Enabled = !IsNativeCleanupInProgress();

                    foreach (ToolStripItem item in mnuContext.Items)
                        if (item is ToolStripMenuItem menuItem && Equals(menuItem.Tag, "tunnel"))
                            menuItem.Checked = false;

                    if (TryGetProfileItem(activeProfileName, out var disconnectedProfile))
                        disconnectedProfile.ImageKey = ConnectionState.Disconnected.ToString();

                    gbxState.Visible = false;

#if WIRESOCKUI_ENABLE_UWP
                    if (notify && Settings.Default.EnableNotifications)
                        TryShowNotification(Resources.ToastInactiveTitle,
                            string.Format(Resources.ToastInactiveMessage, activeProfileName));
#endif
                    break;
                case ConnectionState.Indeterminate:
                    CancelTunnelMonitoring();

                    if (btnActivate != null)
                    {
                        btnActivate.Text = Resources.ButtonInactive;
                        SetActivateButtonEnabled(false);
                    }

                    if (imgStatus != null)
                    {
                        SetStatusImage(imgStatus, _inactiveStatusImage);
                        if (txtStatus != null) txtStatus.Text = Resources.InterfaceStatusRecoveryRequired;
                    }

                    trayIcon.Text = Resources.InterfaceStatusRecoveryRequired;
                    cmiStatus.Text = Resources.InterfaceStatusRecoveryRequired;
                    cmiAddresses.Text = string.Empty;
                    cmiAddresses.Visible = false;
                    cmiDeactivateTunnel.Enabled = false;
                    cmiResetKillSwitch.Enabled = !IsNativeCleanupInProgress();

                    if (TryGetProfileItem(activeProfileName, out var indeterminateProfile))
                        indeterminateProfile.ImageKey = ConnectionState.Indeterminate.ToString();

                    gbxState.Visible = false;
                    break;
            }

        }

        private static void PersistLastActiveProfile(string profileName)
        {
            var previousSettings = PrivilegedSettingsStore.Capture();
            if (string.Equals(previousSettings.LastProfile, profileName, StringComparison.Ordinal))
                return;

            PrivilegedSettingsStore.Apply(new PrivilegedSettingsSnapshot(
                previousSettings.AutoConnect,
                profileName,
                previousSettings.UseAdapter,
                previousSettings.EnableKillSwitch));
            try
            {
                PrivilegedSettingsStore.Save();
            }
            catch
            {
                PrivilegedSettingsStore.Apply(previousSettings);
                throw;
            }
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

#if WIRESOCKUI_ENABLE_UWP
            ScheduleVersionCheck();
#endif

            BeginNativeCleanup();
            try
            {
                await ShowPendingNativeRecoveryWarningAsync();
            }
            finally
            {
                EndNativeCleanup(null);
            }

            if (_shutdownComplete || IsDisposed || Disposing)
                return;
            var recoveryBlocksTunnelOperations = IsNativeRecoveryRequired();
            var approvedLegacyProfileThisLaunch = ReviewPendingLegacyProfiles();

            if (Settings.Default.AutoMinimize)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
                Hide();
            }

            if (lstProfiles.Items.ContainsKey(PrivilegedSettingsStore.LastProfile))
                lstProfiles.Items[PrivilegedSettingsStore.LastProfile].Selected = true;

            // Connect to the last used configuration, if required.
            if (!PrivilegedSettingsStore.AutoConnect || recoveryBlocksTunnelOperations || approvedLegacyProfileThisLaunch)
                return;

            if (lstProfiles.Items.ContainsKey(PrivilegedSettingsStore.LastProfile))
                await ExecuteSelectedProfileCommandAsync(TunnelCommand.ActivateSelectedProfile, false);
            else
                MessageBox.Show(Resources.LastProfileNotFound, Resources.DialogAutoConnect, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
        }

        private bool ReviewPendingLegacyProfiles()
        {
            var approvedAny = false;
            IReadOnlyList<string> pendingProfiles;
            try
            {
                pendingProfiles = LegacyProfileMigrationService.GetPendingProfileNames();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unable to enumerate staged legacy profiles: {ex.Message}");
                MessageBox.Show(
                    $"WireSock UI could not inspect staged legacy profiles.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            foreach (var profileName in pendingProfiles)
            {
                var profileSaved = false;
                var review = MessageBox.Show(
                    $"Legacy profile '{profileName}' is quarantined and cannot be activated yet.{Environment.NewLine}{Environment.NewLine}" +
                    "Review its endpoint, DNS, routes, application filters, and scripts before approving it. Review now?",
                    Resources.EditProfileTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (review != DialogResult.Yes)
                    continue;

                try
                {
                    using (var form = new FrmEdit(
                               profileName,
                               LegacyProfileMigrationService.GetPendingProfilePath(profileName)))
                    {
                        form.Owner = this;
                        if (form.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(form.ReturnValue))
                            continue;
                    }

                    approvedAny = true;
                    profileSaved = true;
                    LegacyProfileMigrationService.CompleteApprovedMigration(profileName);
                }
                catch (Exception ex)
                {
                    var action = profileSaved
                        ? "remove the reviewed legacy source files after saving"
                        : "approve";
                    Trace.TraceWarning($"Unable to {action} legacy profile '{profileName}': {ex.Message}");
                    MessageBox.Show(
                        profileSaved
                            ? $"The profile was saved, but WireSock UI could not remove its quarantined legacy files.{Environment.NewLine}{Environment.NewLine}{ex.Message}"
                            : ex.Message,
                        Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            if (approvedAny)
                LoadProfiles();

            return approvedAny;
        }

        private async void OnDisconnectClick(object sender, EventArgs e)
        {
            if (!TryBeginTunnelOperation())
                return;

            try
            {
                if (!await DisconnectCurrentTunnelAsync())
                    return;
            }
            finally
            {
                EndTunnelOperation();
            }
        }

        private void OnExitClick(object sender, EventArgs e)
        {
            _exitRequested = true;
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing || _exitRequested)
            {
                Shutdown();
                return;
            }

            e.Cancel = true;
            Hide();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Shutdown();
            base.OnFormClosed(e);
        }

        /// <summary>
        ///     Handles the form show event.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">An EventArgs that contains the event data.</param>
        private void OnFormShow(object sender, EventArgs e)
        {
            TopMost = true;
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
            TopMost = false;
        }

        private void OnFormMinimize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
                Hide();
        }

        private void OnNewProfileClick(object sender, EventArgs e)
        {
            if (!TryBeginTunnelOperation())
                return;

            try
            {
                using (Form form = new FrmEdit())
                {
                    if (form.ShowDialog() == DialogResult.OK)
                        LoadProfiles();
                }
            }
            finally
            {
                EndTunnelOperation();
            }
        }

        private void OnAddProfileClick(object sender, EventArgs e)
        {
            if (!TryBeginTunnelOperation())
                return;

            try
            {
                using (var openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Title = Resources.DialogOpenFileTitle;
                    openFileDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                    openFileDialog.Filter = Resources.DialogOpenFileFilter;
                    openFileDialog.RestoreDirectory = true;

                    if (openFileDialog.ShowDialog() != DialogResult.OK)
                        return;

                    var filePath = openFileDialog.FileName;

                    var profileName = Path.GetFileNameWithoutExtension(filePath);

                    if (!Profile.IsValidProfileName(profileName))
                    {
                        MessageBox.Show(Resources.EditProfileNameError, Resources.ProfileError, MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return;
                    }

                    var destinationPath = Profile.GetProfilePath(profileName);
                    if (Profile.ProfilePathExists(destinationPath))
                    {
                        var message = Profile.IsRegularProfileFile(destinationPath, out var diagnostic)
                            ? string.Format(Resources.AddProfileExistsMsg, profileName)
                            : diagnostic;

                        MessageBox.Show(message,
                            Resources.AddProfileExistsTitle,
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    try
                    {
                        if (ImportProfileFromFile(filePath, destinationPath))
                            LoadProfiles(profileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            finally
            {
                EndTunnelOperation();
            }
        }

        private bool ImportProfileFromFile(string filePath, string destinationPath)
        {
            var tmpProfile = ProfileImportService.CopyToTemporaryProfileFile(filePath);

            try
            {
                var profile = new Profile(tmpProfile);
                if (!ProfileScriptWarning.ConfirmIfProfileHasScriptHooks(this, profile))
                    return false;

                File.Move(tmpProfile, destinationPath);
                tmpProfile = null;
                return true;
            }
            finally
            {
                if (tmpProfile != null)
                    ProfileImportService.TryDeleteTemporaryProfile(tmpProfile);
            }
        }

        private async void OnEditProfileClick(object sender, EventArgs e)
        {
            if (!TryBeginTunnelOperation())
                return;

            var reconnect = false;

            try
            {
                if (lstProfiles.SelectedItems.Count == 0)
                    return;

                var profile = lstProfiles.SelectedItems[0].Text;

                using (var form = new FrmEdit(profile))
                {
                    if (form.ShowDialog() != DialogResult.OK) return;

                    LoadProfiles(form.ReturnValue);

                    reconnect = IsCurrentTunnelProfile(profile);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                EndTunnelOperation();
            }

            if (reconnect)
                await ExecuteSelectedProfileCommandAsync(TunnelCommand.ActivateSelectedProfile, false);
        }

        private async void OnDeleteProfileClick(object sender, EventArgs e)
        {
            if (!TryBeginTunnelOperation())
                return;

            try
            {
                if (lstProfiles.SelectedItems.Count == 0)
                    return;

                var selectedConf = lstProfiles.SelectedItems[0].Text;

                if (MessageBox.Show(string.Format(Resources.DeleteProfileConfirmMsg, selectedConf),
                        Resources.DeleteProfileConfirmTitle,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) != DialogResult.Yes)
                    return;

                if (IsCurrentTunnelProfile(selectedConf))
                {
                    if (!await DisconnectCurrentTunnelAsync())
                        return;
                }

                var profilePath = Profile.GetProfilePath(selectedConf);
                Profile.EnsureRegularProfileFile(profilePath);
                File.Delete(profilePath);
                LoadProfiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                EndTunnelOperation();
            }
        }

        private async void OnSettingsClick(object sender, EventArgs e)
        {
            if (!TryBeginTunnelOperation())
                return;

            try
            {
                using (var form = new FrmSettings())
                {
                    var previousSettings = ApplicationSettingsSnapshot.Capture();

                    // set the owner of the child form to the main form instance
                    form.Owner = this;

                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        var requestedSettings = form.RequestedSettings;
                        var logLevelChanged = !string.Equals(requestedSettings.LogLevel,
                            previousSettings.LogLevel, StringComparison.Ordinal);
                        var killSwitchRequiresNativeUpdate =
                            requestedSettings.EnableKillSwitch != previousSettings.EnableKillSwitch ||
                            _tunnelLifecycle.HasTunnelHandle;
                        var result = await ApplySettingsUpdatesAsync(new[]
                        {
                            new CompensatingTransactionStep(
                                "autorun task",
                                () => Task.FromResult(form.ApplyAutoRunChange()),
                                () => Task.FromResult(form.RollbackAutoRunChange())),
                            new CompensatingTransactionStep(
                                "native log level",
                                () => logLevelChanged
                                    ? ApplyLogLevelSettingAsync(requestedSettings.LogLevel)
                                    : Task.FromResult(true),
                                () => logLevelChanged
                                    ? ApplyLogLevelSettingAsync(previousSettings.LogLevel)
                                    : Task.FromResult(true)),
                            new CompensatingTransactionStep(
                                "settings persistence",
                                () => PersistSettingsAsync(requestedSettings),
                                () => PersistSettingsAsync(previousSettings)),
                            new CompensatingTransactionStep(
                                "native Kill Switch",
                                () => ApplyKillSwitchSettingAsync(requestedSettings.EnableKillSwitch,
                                    killSwitchRequiresNativeUpdate),
                                () => ApplyKillSwitchSettingAsync(previousSettings.EnableKillSwitch,
                                    killSwitchRequiresNativeUpdate))
                        });

                        if (!result.Succeeded)
                        {
                            ShowSettingsTransactionFailure(result);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Unable to apply WireSock UI settings: {ex.Message}");
                MessageBox.Show(ex.Message, Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                EndTunnelOperation();
            }
        }

        internal static Task<CompensatingTransactionResult> ApplySettingsUpdatesAsync(
            IReadOnlyList<CompensatingTransactionStep> steps)
        {
            return CompensatingTransaction.ApplyAsync(steps);
        }

        private async Task<bool> ApplyLogLevelSettingAsync(string logLevel)
        {
            var stateBeforeUpdate = _currentState;
            var profile = _tunnelLifecycle.ProfileName;
            var updateResult = await _tunnelLifecycle.SetLogLevelAsync(
                WireSockManager.ParseLogLevelSetting(logLevel), NativeQueryTimeoutMilliseconds);
            updateResult = await AwaitTimedOutNativeOperationAsync(updateResult,
                "native log-level update", profile, stateBeforeUpdate);
            if (!updateResult.Succeeded)
                throw new InvalidOperationException(updateResult.Diagnostic ??
                                                    "Unable to update the native log level.");

            return true;
        }

        private async Task<bool> ApplyKillSwitchSettingAsync(bool enableKillSwitch, bool shouldApplyNativeState)
        {
            if (!shouldApplyNativeState)
                return true;

            var stateBeforeUpdate = _currentState;
            var profile = _tunnelLifecycle.ProfileName;
            var result = await _tunnelLifecycle.ApplyKillSwitchAsync(enableKillSwitch,
                NativeQueryTimeoutMilliseconds);
            result = await AwaitTimedOutNativeOperationAsync(result, "native Kill Switch update", profile,
                stateBeforeUpdate);
            if (!result.Succeeded)
                throw new InvalidOperationException(
                    $"{Resources.TunnelKillSwitchStateError} {result.Diagnostic}".Trim());

            return true;
        }

        private static Task<bool> PersistSettingsAsync(ApplicationSettingsSnapshot settings)
        {
            settings.Persist();
            return Task.FromResult(true);
        }

        private void ShowSettingsTransactionFailure(CompensatingTransactionResult result)
        {
            var diagnostic = result.Exception?.Message ??
                             $"The {result.FailedStep} step did not complete.";
            var message = string.Format(Resources.SettingsApplyError, result.FailedStep, diagnostic);
            if (result.RollbackFailures.Count > 0)
                message += Environment.NewLine + Environment.NewLine +
                           string.Format(Resources.SettingsRollbackError,
                               string.Join(", ", result.RollbackFailures));

            Trace.TraceError(message);
            if (SettingsFailureRequiresNativeRecovery(result))
            {
                Global.NativeRecoveryMarkers.Write("settings rollback failure", message);
                _tunnelSession.RequireRecovery();
                SetNativeRecoveryUi(_tunnelLifecycle.ProfileName);
            }

            MessageBox.Show(message, Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        internal static bool SettingsFailureRequiresNativeRecovery(CompensatingTransactionResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            return result.RollbackFailed("native Kill Switch");
        }

        private async Task<NativeOperationResult<T>> AwaitTimedOutNativeOperationAsync<T>(
            NativeOperationResult<T> result, string context, string profile, ConnectionState previousState)
        {
            if (!result.TimedOut)
                return result;

            BeginNativeCleanup();
            var markerLease = Global.NativeRecoveryMarkers.Write(context, result.Diagnostic);
            SetNativeRecoveryUi(profile);
            MessageBox.Show(result.Diagnostic, Resources.TunnelErrorTitle, MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            NativeOperationResult<T> completedResult;
            try
            {
                completedResult = await result.PendingCompletion;
            }
            catch (Exception ex)
            {
                completedResult = NativeOperationResult<T>.Failure(ex.Message);
            }

            completedResult = NativeOperationRecoveryPolicy.NormalizeCompletion(completedResult, context);

            try
            {
                if (NativeOperationRecoveryPolicy.CanRestorePreviousState(completedResult))
                {
                    Global.NativeRecoveryMarkers.TryDelete(markerLease);
                    if (!_shutdownComplete && !IsDisposed && !Disposing)
                        UpdateState(previousState, false, profile);
                }
                else
                {
                    var diagnostic = completedResult.Diagnostic ??
                                     "The timed-out native operation completed without a verified result.";
                    WriteOrUpdateNativeRecoveryMarker(markerLease, context, diagnostic);
                    _tunnelSession.RequireRecovery();
                    SetNativeRecoveryUi(profile);
                }
            }
            finally
            {
                EndNativeCleanup(profile);
            }

            return completedResult;
        }

        private async void OnResetKillSwitchClick(object sender, EventArgs e)
        {
            if ((_currentState != ConnectionState.Disconnected &&
                 _currentState != ConnectionState.Indeterminate) || IsNativeCleanupInProgress())
                return;

            if (!_tunnelSession.TryBeginRecoveryOperation(out var blockReason))
            {
                var message = blockReason == TunnelOperationBlockReason.CleanupPending
                    ? Resources.TunnelConnectCleanupPending
                    : Resources.TunnelOperationPending;
                ShowTunnelOperationBlockedMessage(message);
                return;
            }

            cmiResetKillSwitch.Enabled = false;
            var profile = _tunnelLifecycle.ProfileName;
            var markerSnapshot = Global.NativeRecoveryMarkers.Capture();

            try
            {
                var stateBeforeRecovery = _currentState;
                var handleReleased = !_tunnelLifecycle.HasTunnelHandle ||
                                     await DisconnectNativeTunnelAsync(showWarning: false);
                if (!handleReleased && IsNativeCleanupInProgress())
                    return;

                var resetResult = await _tunnelLifecycle.ResetNetworkLockAsync(NativeQueryTimeoutMilliseconds);
                resetResult = await AwaitTimedOutNativeOperationAsync(resetResult, "manual Kill Switch recovery",
                    profile, stateBeforeRecovery);
                var networkLockReset = resetResult.Succeeded;

                if (!networkLockReset || !handleReleased)
                {
                    var failureDiagnostic = !networkLockReset
                        ? resetResult.Diagnostic
                        : _tunnelLifecycle.LastError ?? Resources.TunnelHandleReleaseWarning;
                    Global.NativeRecoveryMarkers.Write("manual Kill Switch recovery", failureDiagnostic);
                    _tunnelSession.RequireRecovery();
                    SetNativeRecoveryUi(profile);

                    var message = !networkLockReset
                        ? $"{Resources.KillSwitchResetError}{Environment.NewLine}{Environment.NewLine}{resetResult.Diagnostic}"
                        : Resources.TunnelHandleReleaseWarning;
                    MessageBox.Show(message, Resources.TunnelErrorTitle, MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                Global.NativeRecoveryMarkers.TryDelete(markerSnapshot);
                _tunnelSession.ClearRecovery();
                UpdateState(ConnectionState.Disconnected, false, profile);

                MessageBox.Show(Resources.KillSwitchResetSuccess, Resources.KillSwitchResetTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Manual Kill Switch recovery failed unexpectedly: {ex.Message}");
                Global.NativeRecoveryMarkers.Write("manual Kill Switch recovery", ex.Message);
                _tunnelSession.RequireRecovery();
                SetNativeRecoveryUi(profile);
                MessageBox.Show(
                    $"{Resources.KillSwitchResetError}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _tunnelSession.EndOperation();
                if (!_shutdownComplete && !IsDisposed && !Disposing)
                    cmiResetKillSwitch.Enabled = !IsNativeCleanupInProgress() &&
                                                 (_currentState == ConnectionState.Disconnected ||
                                                  _currentState == ConnectionState.Indeterminate);
            }
        }

        private async Task ReleaseUntransferredNetworkLockAsync(string profile)
        {
            var resetResult = await _tunnelLifecycle.ResetNetworkLockAsync(NativeQueryTimeoutMilliseconds);
            resetResult = await AwaitTimedOutNativeOperationAsync(resetResult,
                "preserved network-lock rollback", profile, ConnectionState.Disconnected);
            if (resetResult.Succeeded)
                return;

            var markerSnapshot = Global.NativeRecoveryMarkers.Capture();
            var recovered = await TryResetNetworkLockAfterNativeCleanupFailureAsync(
                "preserved network-lock rollback", markerSnapshot, true);
            if (!recovered)
                MarkNativeRecoveryRequired(profile,
                    resetResult.Diagnostic ?? "preserved network-lock rollback failure");
        }

        private async void OnProfileClick(object sender, EventArgs e)
        {
            await ExecuteSelectedProfileCommandAsync(TunnelCommand.ActivateSelectedProfile, true);
        }

        private async void OnActivateButtonClick(object sender, EventArgs e)
        {
            await ExecuteSelectedProfileCommandAsync(TunnelCommand.ToggleSelectedProfile, true);
        }

        private async Task ExecuteSelectedProfileCommandAsync(TunnelCommand command, bool userInitiated)
        {
            if (!TryBeginTunnelOperation())
                return;

            var preservedNetworkLockPending = false;
            string operationProfile = null;
            try
            {
                // Return if no profile is selected in the list.
                if (lstProfiles.SelectedItems.Count == 0) return;

                // Get the selected profile.
                var profile = lstProfiles.SelectedItems[0].Text;
                operationProfile = profile;
                var disconnectOnly = TunnelCommandPolicy.IsDisconnectOnly(_currentState, command);

                Profile profileSettings = null;
                var useAdapter = PrivilegedSettingsStore.UseAdapter;

                if (!disconnectOnly)
                {
                    try
                    {
                        profileSettings = Profile.LoadProfile(profile);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    if (!ProfileScriptWarning.ConfirmIfProfileHasScriptHooks(this, profileSettings))
                        return;

                    if (bool.TryParse(profileSettings.VirtualAdapterMode, out var profileUseAdapter))
                        useAdapter = profileUseAdapter;
                }

                var preserveNetworkLockForReconnect = PrivilegedSettingsStore.EnableKillSwitch && !disconnectOnly;

                if (_currentState == ConnectionState.Connected || _currentState == ConnectionState.Connecting)
                {
                    if (!await DisconnectCurrentTunnelAsync(
                            userInitiated,
                            preserveNetworkLockForReconnect))
                        return;

                    preservedNetworkLockPending = preserveNetworkLockForReconnect;

                    if (disconnectOnly)
                        return;
                }
                else if (_tunnelLifecycle.HasTunnelHandle)
                {
                    if (!await DisconnectCurrentTunnelAsync(false, preserveNetworkLockForReconnect))
                        return;

                    preservedNetworkLockPending = preserveNetworkLockForReconnect;
                }

                try
                {
                    if (_tunnelLifecycle.HasTunnelHandle)
                    {
                        if (!await DisconnectNativeTunnelAsync(
                                preserveNetworkLock: preserveNetworkLockForReconnect))
                            return;

                        preservedNetworkLockPending = preserveNetworkLockForReconnect;
                    }

                    _tunnelLifecycle.TunnelMode = useAdapter
                        ? WireSockManager.Mode.VirtualAdapter
                        : WireSockManager.Mode.Transparent;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Connect to the selected profile and update the state to connecting if successful.
                SetActivateButtonEnabled(false);
                try
                {
                    var connectGeneration = CurrentTunnelGeneration();
                    UpdateState(ConnectionState.Connecting, true, profile);
                    var connectTask = ConnectWithTimeoutAsync(profile, connectGeneration,
                        preservedNetworkLockPending);
                    preservedNetworkLockPending = false;
                    var connectResult = await connectTask;
                    if (connectResult == null)
                        return;

                    var connected = connectResult.Connected;
                    var connectionSequence = connectResult.ConnectionSequence;

                    if (!_shutdownComplete && connectGeneration == CurrentTunnelGeneration() &&
                        IsTunnelConnectionTimedOut(connectGeneration))
                    {
                        var cleanupSucceeded = !connected && !_tunnelLifecycle.HasTunnelHandle ||
                                               await DisconnectNativeTunnelAsync(
                                                   connected ? connectionSequence : (long?)null, false);

                        if (!cleanupSucceeded)
                        {
                            if (!IsNativeCleanupInProgress())
                                await HandleNativeCleanupFailureAsync(profile, "late connection-timeout cleanup",
                                    _tunnelLifecycle.LastError);
                            return;
                        }

                        UpdateState(ConnectionState.Disconnected, false, profile);
                        MessageBox.Show(Resources.TunnelConnectTimeout, Resources.TunnelErrorTitle,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    if (connectGeneration != CurrentTunnelGeneration() || _shutdownComplete)
                    {
                        if ((connected || _tunnelLifecycle.HasTunnelHandle) &&
                            !await DisconnectNativeTunnelAsync(connected ? connectionSequence : (long?)null, false))
                        {
                            if (!IsNativeCleanupInProgress())
                                await HandleNativeCleanupFailureAsync(profile, "stale connection cleanup",
                                    _tunnelLifecycle.LastError);
                        }

                        return;
                    }

                    if (connected)
                    {
                        var queryResult = await _tunnelLifecycle.GetConnectedAsync(NativeQueryTimeoutMilliseconds);
                        if (queryResult.Succeeded)
                        {
                            UpdateState(queryResult.Value ? ConnectionState.Connected : ConnectionState.Connecting,
                                true,
                                profile);
                        }
                        else
                        {
                            var queryDiagnostic = queryResult.Diagnostic ??
                                                  "Unable to query the native tunnel state.";
                            Trace.TraceWarning($"Native tunnel state query failed after connect: {queryDiagnostic}");
                            if (NativeOperationRecoveryPolicy.MustDeferCleanup(queryResult))
                            {
                                TrackTimedOutNativeOperation(queryResult, "post-connect tunnel-state query");
                                MarkNativeRecoveryRequired(profile,
                                    $"post-connect tunnel-state query: {queryDiagnostic}");
                                MessageBox.Show(
                                    $"WireSock UI could not verify the native tunnel state.{Environment.NewLine}{Environment.NewLine}{queryDiagnostic}",
                                    Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            }
                            else if (!await DisconnectNativeTunnelAsync(connectionSequence, false))
                            {
                                if (!IsNativeCleanupInProgress())
                                    await HandleNativeCleanupFailureAsync(profile,
                                        "post-connect verification cleanup",
                                        _tunnelLifecycle.LastError ?? queryDiagnostic);
                            }
                            else
                            {
                                UpdateState(ConnectionState.Disconnected, false, profile);
                                MessageBox.Show(
                                    $"WireSock UI could not verify the native tunnel state.{Environment.NewLine}{Environment.NewLine}{queryDiagnostic}",
                                    Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            }
                        }
                    }
                    else
                    {
                        UpdateState(ConnectionState.Disconnected, false, profile);
                        if (connectResult.RecoveryRequired)
                            MarkNativeRecoveryRequired(profile,
                                connectResult.Diagnostic ?? "preserved network-lock rollback failure");
                        else if (_tunnelLifecycle.HasTunnelHandle)
                            await HandleNativeCleanupFailureAsync(profile, "failed connection cleanup",
                                _tunnelLifecycle.LastError);
                        else
                            MessageBox.Show(connectResult.Diagnostic ?? _tunnelLifecycle.LastError ??
                                            Resources.TunnelErrorManager,
                                Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                catch (Exception ex)
                {
                    if (_tunnelLifecycle.HasTunnelHandle)
                        await HandleNativeCleanupFailureAsync(profile, "unexpected connection workflow failure",
                            ex.Message);
                    else
                    {
                        UpdateState(ConnectionState.Disconnected, false, profile);
                        MessageBox.Show(ex.Message, Resources.TunnelErrorTitle, MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
                finally
                {
                    if (_currentState == ConnectionState.Disconnected)
                        SetActivateButtonEnabled(true);
                }
            }
            finally
            {
                try
                {
                    if (preservedNetworkLockPending)
                        await ReleaseUntransferredNetworkLockAsync(operationProfile);
                }
                finally
                {
                    EndTunnelOperation();
                }
            }
        }

        private void OnWireSockLogMessage(WireSockManager.LogMessage logMessage)
        {
            _uiLogBuffer.Enqueue(logMessage);
        }

        private bool TryScheduleLogDrain(Action drain)
        {
            if (_shutdownComplete || IsDisposed || Disposing || !IsHandleCreated)
                return false;

            try
            {
                BeginInvoke(drain);
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void AppendWireSockLogMessages(IReadOnlyList<WireSockManager.LogMessage> logMessages)
        {
            if (_shutdownComplete || IsDisposed || Disposing || logMessages == null || logMessages.Count == 0)
                return;

            var updating = false;
            try
            {
                lstLog.BeginUpdate();
                updating = true;
                var items = logMessages.Select(logMessage => new ListViewItem(new[]
                {
                    logMessage.Timestamp.ToString(Resources.LogTimestampFormat), logMessage.Message
                })).ToArray();
                lstLog.Items.AddRange(items);

                var overflow = lstLog.Items.Count - MaxVisibleLogMessages;
                while (overflow-- > 0)
                    lstLog.Items.RemoveAt(0);

                if (lstLog.Items.Count > 0)
                    lstLog.Items[lstLog.Items.Count - 1].EnsureVisible();
            }
            catch (ObjectDisposedException)
            {
                // Late native log callbacks can arrive while the form is shutting down.
            }
            catch (InvalidOperationException)
            {
                // The logging ListView can be in handle destruction during shutdown.
            }
            finally
            {
                if (updating && !_shutdownComplete && !IsDisposed && !Disposing)
                    try
                    {
                        lstLog.EndUpdate();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
            }
        }

        #region Layout

        private void OnProfileChange(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            gbxInterface.Visible = false;
            gbxInterface.Text = string.Empty;
            ClearDynamicLayout(layoutInterface);
            layoutInterface.RowStyles.Clear();

            gbxPeer.Visible = false;
            ClearDynamicLayout(layoutPeer);
            layoutPeer.RowStyles.Clear();

            gbxState.Visible = false;
            ClearDynamicLayout(layoutState);
            layoutState.RowStyles.Clear();

            if (e.IsSelected)
            {
                var selectedConf = lstProfiles.SelectedItems[0].Text;

                try
                {
                    var profile = Profile.LoadProfile(selectedConf);

                    // Interface Panel
                    gbxInterface.Text = string.Format(Resources.InterfaceTitle, selectedConf);

                    AddRow(layoutInterface, "Status", Resources.InterfaceStatus, Resources.InterfaceStatusInactive,
                        false, _inactiveStatusImage);
                    AddRow(layoutInterface, "PrivateKey", Resources.InterfacePublicKey, profile.PublicKey);
                    AddRow(layoutInterface, "MTU", Resources.InterfaceMTU, profile.Mtu, true);
                    AddRow(layoutInterface, "ListenPort", Resources.InterfaceListenPort, profile.ListenPort, true);
                    AddRow(layoutInterface, "Addresses", Resources.InterfaceAddresses, profile.Address);

                    layoutInterface.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
                    layoutInterface.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
                    layoutInterface.RowCount = layoutInterface.RowStyles.Count;

                    var btnActivate = new Button
                    {
                        AutoSize = true,
                        AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        Dock = DockStyle.Left,
                        Name = "btnActivate",
                        Text = Resources.ButtonInactive
                    };

                    btnActivate.Click += OnActivateButtonClick;

                    layoutInterface.Controls.Add(btnActivate, 1, layoutInterface.RowCount - 1);
                    SetActivateButtonEnabled(true);

                    layoutInterface.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    gbxInterface.Visible = true;

                    OnLayoutPanelResize(layoutInterface, EventArgs.Empty);

                    // Peer Panel
                    AddRow(layoutPeer, "PublicKey", Resources.PeerPublicKey, profile.PeerKey);
                    AddRow(layoutPeer, "PresharedKey", Resources.PeerPresharedKey,
                        !string.IsNullOrWhiteSpace(profile.PresharedKey)
                            ? Resources.PeerPresharedKeyValue
                            : string.Empty, true);
                    AddRow(layoutPeer, "AllowedIPs", Resources.PeerAllowedIPs, TruncateLongString(profile.AllowedIPs));
                    AddRow(layoutPeer, "Endpoint", Resources.PeerEndpoint, profile.Endpoint);
                    AddRow(layoutPeer, "PersistentKeepAlive", Resources.PeerPersistentKeepAlive,
                        profile.PersistentKeepAlive, true);

                    layoutPeer.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

                    AddRow(layoutPeer, "AllowedApps", Resources.PeerAllowedApps, profile.AllowedApps, true);
                    AddRow(layoutPeer, "DisallowedApps", Resources.PeerDisallowedApps, profile.DisallowedApps, true);
                    AddRow(layoutPeer, "DisallowedIPs", Resources.PeerDisallowedIPs,
                        TruncateLongString(profile.DisallowedIPs), true);
                    AddRow(layoutPeer, "Socks5Proxy", Resources.PeerSocks5Proxy, profile.Socks5Proxy, true);
                    AddRow(layoutPeer, "Socks5Username", Resources.PeerSocks5Username, profile.Socks5ProxyUsername,
                        true);
                    AddRow(layoutPeer, "Socks5Password", Resources.PeerSocks5Password,
                        !string.IsNullOrWhiteSpace(profile.Socks5ProxyPassword)
                            ? Resources.PeerSocks5PasswordValue
                            : string.Empty, true);

                    if (!string.IsNullOrWhiteSpace(profile.AllowedApps) ||
                        !string.IsNullOrWhiteSpace(profile.DisallowedApps) ||
                        !string.IsNullOrWhiteSpace(profile.DisallowedIPs) ||
                        !string.IsNullOrWhiteSpace(profile.Socks5Proxy))
                        layoutPeer.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

                    layoutPeer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    gbxPeer.Visible = true;

                    OnLayoutPanelResize(layoutPeer, EventArgs.Empty);

                    // Layout state                    
                    AddRow(layoutState, "Handshake", Resources.StateHandshake, "");
                    AddRow(layoutState, "Transfer", Resources.StateTransfer, "");
                    AddRow(layoutState, "RTT", Resources.StateRTT, "");
                    AddRow(layoutState, "Loss", Resources.StateLoss, "");

                    layoutState.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                    // Only 1 profile can be active at a time, either show the active state or do not allow to activate
                    if (_tunnelLifecycle != null && _currentState == ConnectionState.Connected)
                    {
                        if (_tunnelLifecycle.ProfileName == selectedConf)
                            UpdateState(ConnectionState.Connected, false);
                        else
                            SetActivateButtonEnabled(false);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            mniDeleteTunnel.Enabled = e.IsSelected;
            btnEdit.Enabled = e.IsSelected;
            return;

            void AddRow(TableLayoutPanel container, string name, string key, string value, bool isOptional = false,
                Image icon = null)
            {
                if (isOptional && string.IsNullOrWhiteSpace(value)) return;

                container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                container.RowCount = container.RowStyles.Count;

                var label = new Label
                {
                    Dock = DockStyle.Fill,
                    Name = $"lbl{name}",
                    Height = 18,
                    Margin = new Padding(0, 0, 0, 0),
                    Padding = new Padding(0),
                    TextAlign = ContentAlignment.TopRight,
                    Text = $@"{key}:"
                };

                container.Controls.Add(label, 0, container.RowCount - 1);

                if (icon != null)
                {
                    var panel = new TableLayoutPanel
                    {
                        AutoSize = true,
                        AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        Dock = DockStyle.Fill,
                        Margin = new Padding(0),
                        Padding = new Padding(0)
                    };

                    panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
                    panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
                    panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

                    panel.RowCount = panel.RowStyles.Count;
                    panel.ColumnCount = panel.ColumnStyles.Count;

                    panel.Controls.Add(new PictureBox
                    {
                        Dock = DockStyle.Fill,
                        Height = 16,
                        Image = icon,
                        Margin = new Padding(0),
                        Name = $"img{name}",
                        Padding = new Padding(0),
                        Width = 16
                    }, 0, 0);

                    panel.Controls.Add(new TextBox
                    {
                        BorderStyle = BorderStyle.None,
                        BackColor = Color.FromKnownColor(KnownColor.Control),
                        Dock = DockStyle.Fill,
                        Margin = new Padding(0),
                        Multiline = true,
                        Name = $"txt{name}",
                        Padding = new Padding(0),
                        ReadOnly = true,
                        Text = value
                    });

                    container.Controls.Add(panel, 1, container.RowCount - 1);
                }
                else
                {
                    var textBox = new TextBox
                    {
                        BorderStyle = BorderStyle.None,
                        BackColor = Color.FromKnownColor(KnownColor.Control),
                        Dock = DockStyle.Fill,
                        Margin = new Padding(0),
                        Multiline = true,
                        Name = $"txt{name}",
                        Padding = new Padding(0),
                        ReadOnly = true,
                        Text = value
                    };

                    container.Controls.Add(textBox, 1, container.RowCount - 1);
                }
            }

            // Helper function to truncate long strings
            string TruncateLongString(string input)
            {
                if (input == null)
                    return null;

                var values = input.Split(',');
                var groupedValues = new List<string>();

                for (var i = 0; i < values.Length && i < 20; i += 2)
                {
                    var group = values.Skip(i).Take(2);
                    groupedValues.Add(string.Join(",", group));
                }

                var result = string.Join("\n", groupedValues);

                if (values.Length > 20) result += "...";

                return result;
            }
        }

        private void OnLayoutPanelResize(object sender, EventArgs e)
        {
            var panel = sender as TableLayoutPanel;
            if (panel != null && panel.Width > 0)
                panel.ColumnStyles[1].Width = panel.Width - panel.ColumnStyles[0].Width;

            if (panel == null) return;
            foreach (Control control in panel.Controls)
                if (control is TextBox textBox)
                {
                    var textHeight = TextRenderer.MeasureText(
                        textBox.Text,
                        textBox.Font,
                        new Size(
                            textBox.ClientSize.Width,
                            textBox.ClientSize.Height),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;

                    textBox.Height = textHeight + 0 + textHeight / textBox.Font.Height;
                }
        }

        #endregion

        #region LogWindow

        private void OnLogWindowResize(object sender, EventArgs e)
        {
            // Ensure the log list rows fill the entire width, but no scrollbar appears
            lstLog.Columns[1].Width = Math.Max(0, lstLog.ClientSize.Width - lstLog.Columns[0].Width - 4);
        }

        private void OnLogDrawHeader(object sender, DrawListViewItemEventArgs e)
        {
            e.DrawDefault = true;

            if (e.ItemIndex % 2 == 1)
            {
                var color = Color.FromKnownColor(KnownColor.Window);

                e.Item.BackColor = Color.FromArgb(
                    (int)(color.R * 0.95),
                    (int)(color.G * 0.95),
                    (int)(color.B * 0.95));

                e.Item.UseItemStyleForSubItems = true;
            }
        }

        private void OnLogDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = true;
        }

        #endregion
    }
}
