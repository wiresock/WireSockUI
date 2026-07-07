using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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
            Disconnected
        }

        private readonly BackgroundWorker _tunnelConnectionWorker;
        private readonly BackgroundWorker _tunnelStateWorker;
        private const int TunnelConnectionTimeoutMilliseconds = 30000;
        private const int TimedOutConnectCleanupRecoveryMilliseconds = 30000;
        private const int MaxVisibleLogMessages = 2000;
        private const int ShutdownDisconnectTimeoutMilliseconds = 5000;

        /**
         * @brief The manager that handles the Wireguard connections.
         */
        private readonly WireSockManager _wiresock;

        private ConnectionState _currentState = ConnectionState.Disconnected;
        private bool _exitRequested;
        private bool _shutdownComplete;
        private int _tunnelOperationInProgress;
        private int _tunnelGeneration;
        private int _tunnelConnectionTimeoutGeneration = -1;
        private int _timedOutConnectCleanupInProgress;
        private int _nativeRecoveryRequired;
        private Icon _ownedTrayIcon;
        private Image _inactiveStatusImage;
        private Image _connectedStatusImage;

        private sealed class TunnelConnectionProgress
        {
            public int Generation { get; set; }
            public bool Connected { get; set; }
            public bool TimedOut { get; set; }
        }

        private sealed class ConnectAttemptResult
        {
            public bool Connected { get; set; }
            public long ConnectionSequence { get; set; }
        }

        private sealed class TunnelStateProgress
        {
            public int Generation { get; set; }
            public WgbStats Stats { get; set; }
        }

        /**
         * @brief Initializes a new instance of the Main class.
         */
        public FrmMain()
        {
            InitializeComponent();

            if (IsApplicationAlreadyRunning())
            {
                MessageBox.Show(Resources.AlreadyRunningMessage, Resources.AlreadyRunningTitle, MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                Environment.Exit(1);
            }

            _tunnelConnectionWorker = InitializeTunnelConnectionWorker();
            _tunnelStateWorker = InitTunnelStateWorker();

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

            // Ensure the profile list rows fill the entire width, but no scrollbar appears
            lstProfiles.Columns[0].Width = lstProfiles.Size.Width - 4;

            OnLogWindowResize(lstLog, EventArgs.Empty);

            // Create a new WireSockManager instance, attached to the logging control
            _wiresock = new WireSockManager(OnWireSockLogMessage);
            _wiresock.LogLevel = _wiresock.LogLevelSetting;

            // Update the list of available configurations.
            LoadProfiles();
        }

        private static bool SleepUntilCancelled(BackgroundWorker worker, int milliseconds)
        {
            const int interval = 100;
            var waited = 0;

            while (waited < milliseconds)
            {
                if (worker.CancellationPending)
                    return true;

                Thread.Sleep(Math.Min(interval, milliseconds - waited));
                waited += interval;
            }

            return worker.CancellationPending;
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
                                      !IsTimedOutConnectCleanupInProgress() &&
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

        private int CurrentTunnelGeneration()
        {
            return Volatile.Read(ref _tunnelGeneration);
        }

        private void AdvanceTunnelGeneration()
        {
            Interlocked.Increment(ref _tunnelGeneration);
            Volatile.Write(ref _tunnelConnectionTimeoutGeneration, -1);
        }

        private bool IsTimedOutConnectCleanupInProgress()
        {
            return Volatile.Read(ref _timedOutConnectCleanupInProgress) != 0;
        }

        private bool IsNativeRecoveryRequired()
        {
            return Volatile.Read(ref _nativeRecoveryRequired) != 0;
        }

        private void BeginTimedOutConnectCleanup()
        {
            Interlocked.Exchange(ref _timedOutConnectCleanupInProgress, 1);
            SetActivateButtonEnabled(false);
            cmiResetKillSwitch.Enabled = false;
        }

        private void EndTimedOutConnectCleanup(string profile)
        {
            Interlocked.Exchange(ref _timedOutConnectCleanupInProgress, 0);

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
            });
        }

        private void MarkNativeRecoveryRequired(string profile, string context)
        {
            Global.WriteNativeRecoveryMarker(
                context,
                "Native WireSock cleanup did not finish safely. New tunnel operations are disabled until WireSock UI is restarted.");

            var wasAlreadyMarked = Interlocked.Exchange(ref _nativeRecoveryRequired, 1) != 0;
            Trace.TraceWarning(
                $"Native WireSock cleanup did not finish safely after {context}. New tunnel operations are disabled until WireSock UI is restarted.");

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

        private void SetNativeRecoveryUi(string profile)
        {
            if (_shutdownComplete || IsDisposed || Disposing)
                return;

            UpdateState(ConnectionState.Disconnected, false, profile);

            var btnActivate = layoutInterface.Controls["btnActivate"] as Button;
            if (btnActivate != null)
                SetActivateButtonEnabled(false);

            var txtStatus = layoutInterface.Controls.Find("txtStatus", true).FirstOrDefault() as TextBox;
            if (txtStatus != null)
                txtStatus.Text = Resources.InterfaceStatusRecoveryRequired;

            cmiDeactivateTunnel.Enabled = false;
            cmiResetKillSwitch.Enabled = true;
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
            _tunnelConnectionWorker.CancelAsync();
            _tunnelStateWorker.CancelAsync();
        }

        private bool TryBeginTunnelOperation(bool showBlockedMessage = true)
        {
            if (IsTimedOutConnectCleanupInProgress())
            {
                if (showBlockedMessage)
                    ShowTunnelOperationBlockedMessage(Resources.TunnelConnectCleanupPending);
                return false;
            }

            if (IsNativeRecoveryRequired())
            {
                if (showBlockedMessage)
                    ShowTunnelOperationBlockedMessage(Resources.TunnelNativeRecoveryRequired);
                return false;
            }

            if (Interlocked.CompareExchange(ref _tunnelOperationInProgress, 1, 0) == 0)
                return true;

            if (showBlockedMessage)
                ShowTunnelOperationBlockedMessage(Resources.TunnelOperationPending);
            return false;
        }

        private void EndTunnelOperation()
        {
            Interlocked.Exchange(ref _tunnelOperationInProgress, 0);
        }

        private void StartTunnelConnectionWorker()
        {
            if (!_tunnelConnectionWorker.IsBusy)
                _tunnelConnectionWorker.RunWorkerAsync(CurrentTunnelGeneration());
        }

        private void StartTunnelStateWorker()
        {
            if (!_tunnelStateWorker.IsBusy)
                _tunnelStateWorker.RunWorkerAsync(CurrentTunnelGeneration());
        }

        private async Task<bool> DisconnectCurrentTunnelAsync(bool notify = true, bool preserveNetworkLock = false)
        {
            var profileName = _wiresock.ProfileName;

            CancelTunnelMonitoring();

            if (await DisconnectNativeTunnelAsync(preserveNetworkLock: preserveNetworkLock))
            {
                UpdateState(ConnectionState.Disconnected, notify, profileName);
                return true;
            }

            if (_wiresock.HasTunnelHandle)
                UpdateState(ConnectionState.Connected, false, profileName);
            else
                UpdateState(ConnectionState.Disconnected, notify, profileName);

            return false;
        }

        private async Task<bool> DisconnectNativeTunnelAsync(long? connectionSequence = null, bool showWarning = true,
            bool preserveNetworkLock = false)
        {
            bool disconnected;

            try
            {
                disconnected = await Task.Run(() => connectionSequence.HasValue
                    ? _wiresock.DisconnectIfConnectionSequence(connectionSequence.Value, preserveNetworkLock)
                    : _wiresock.Disconnect(preserveNetworkLock));
            }
            catch (Exception ex)
            {
                if (showWarning)
                    MessageBox.Show(ex.Message, Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (!disconnected && showWarning)
                MessageBox.Show(Resources.TunnelHandleReleaseWarning, Resources.TunnelErrorTitle, MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

            return disconnected;
        }

        private void Shutdown()
        {
            if (_shutdownComplete)
                return;

            _shutdownComplete = true;
            _currentState = ConnectionState.Disconnected;

            _tunnelConnectionWorker.CancelAsync();
            _tunnelStateWorker.CancelAsync();

            DisposeWireSockWithTimeout();

            trayIcon.Visible = false;
            SetTrayIcon(null, false);
            DisposeStatusImages();

            if (Global.AlreadyRunning != null)
            {
                Global.AlreadyRunning.Dispose();
                Global.AlreadyRunning = null;
            }
        }

        private void DisposeWireSockWithTimeout()
        {
            if (_wiresock == null)
                return;

            try
            {
                var cleanupTask = Task.Run(() =>
                {
                    try
                    {
                        _wiresock.Disconnect();
                    }
                    finally
                    {
                        _wiresock.Dispose();
                    }
                });

                if (!cleanupTask.Wait(ShutdownDisconnectTimeoutMilliseconds))
                {
                    Trace.TraceWarning(
                        $"WireSock manager shutdown exceeded {ShutdownDisconnectTimeoutMilliseconds} ms; continuing application exit.");
                    if (!TryResetNetworkLockAfterNativeCleanupFailure("shutdown timeout", true))
                        Trace.TraceWarning(
                            "WireSock UI could not verify native tunnel cleanup before exit. Reopen WireSock UI as administrator and use Reset Kill Switch if network access remains blocked.");

                    cleanupTask.ContinueWith(task =>
                        {
                            if (task.IsFaulted)
                                Trace.TraceWarning(
                                    $"WireSock manager shutdown completed with an error after exit continued: {task.Exception?.GetBaseException().Message}");

                            TryResetNetworkLockAfterNativeCleanupFailure("late shutdown cleanup", true);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }
            }
            catch (AggregateException ex)
            {
                Trace.TraceWarning(
                    $"Failed to cleanly shut down WireSock manager: {ex.GetBaseException().Message}");
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to cleanly shut down WireSock manager: {ex.Message}");
            }
        }

        private static bool TryResetNetworkLockAfterNativeCleanupFailure(string context,
            bool recordRecoveryMarkerOnFailure = false)
        {
            try
            {
                if (!WireSockManager.TryIsNetworkLockActive(out var networkLockActive, out var queryDiagnostic))
                {
                    var diagnostic = queryDiagnostic ?? "Unable to query WireSock network lock state.";
                    Trace.TraceWarning(
                        $"Unable to query WireSock network lock after {context}: {diagnostic}");
                    if (recordRecoveryMarkerOnFailure)
                        Global.WriteNativeRecoveryMarker(context, diagnostic);
                    return false;
                }

                if (!networkLockActive)
                {
                    if (recordRecoveryMarkerOnFailure)
                        Global.TryDeleteNativeRecoveryMarker();
                    return true;
                }

                if (!WireSockManager.TryResetNetworkLock(out var resetDiagnostic))
                {
                    var diagnostic = resetDiagnostic ?? "Unable to reset WireSock network lock.";
                    Trace.TraceWarning(
                        $"Unable to reset WireSock network lock after {context}: {diagnostic}");
                    if (recordRecoveryMarkerOnFailure)
                        Global.WriteNativeRecoveryMarker(context, diagnostic);
                    return false;
                }

                if (recordRecoveryMarkerOnFailure)
                    Global.TryDeleteNativeRecoveryMarker();
                return true;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to reset WireSock network lock after {context}: {ex.Message}");
                if (recordRecoveryMarkerOnFailure)
                    Global.WriteNativeRecoveryMarker(context, ex.Message);
                return false;
            }
        }

        private void ShowPendingNativeRecoveryWarning()
        {
            var marker = Global.ReadNativeRecoveryMarker();
            if (string.IsNullOrWhiteSpace(marker))
                return;

            var recovered = TryResetNetworkLockAfterNativeCleanupFailure("startup recovery", true);
            if (!recovered)
            {
                Interlocked.Exchange(ref _nativeRecoveryRequired, 1);
                SetNativeRecoveryUi(null);
            }

            var recoveryStatus = recovered
                ? "Startup recovery completed successfully. WireSock UI will continue normally."
                : "Startup recovery could not verify or reset the WireSock Kill Switch. Tunnel operations are disabled until WireSock UI is restarted after network access is restored.";

            var recoveryMessage =
                $"{Resources.TunnelNativeRecoveryRequired}{Environment.NewLine}{Environment.NewLine}{recoveryStatus}{Environment.NewLine}{Environment.NewLine}{marker}";

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
                if (IsHandleCreated)
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            Trace.TraceWarning($"Failed to run WireSock UI callback: {ex.Message}");
                        }
                    }));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
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
                Notifications.Notifications.Notify(title, body);
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
        private static bool IsApplicationAlreadyRunning()
        {
            const string eventName = "Global\\WiresockClientService";

            try
            {
                Global.AlreadyRunning =
                    new EventWaitHandle(false, EventResetMode.AutoReset, eventName, out var createdNew);

                if (createdNew) return false;

                Global.AlreadyRunning.Dispose();
                Global.AlreadyRunning = null;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
            {
                return true;
            }
        }

        /// <summary>
        ///     Initialize a <see cref="T:BackgroundWorker" /> which retrieves tunnel connecting / connecting state and updates it
        ///     in the UI
        /// </summary>
        /// <returns>
        ///     <see cref="T:BackgroundWorker" />
        /// </returns>
        private BackgroundWorker InitializeTunnelConnectionWorker()
        {
            var worker = new BackgroundWorker
            {
                WorkerSupportsCancellation = true,
                WorkerReportsProgress = true
            };

            worker.DoWork += (s, e) =>
            {
                var generation = (int)e.Argument;
                var connected = false;
                var timeout = Stopwatch.StartNew();

                do
                {
                    if (generation != CurrentTunnelGeneration())
                    {
                        e.Cancel = true;
                        return;
                    }

                    if (SleepUntilCancelled(worker, 500))
                    {
                        e.Cancel = true;
                        return;
                    }

                    connected = _wiresock.Connected;
                    if (!connected && timeout.ElapsedMilliseconds >= TunnelConnectionTimeoutMilliseconds)
                    {
                        worker.ReportProgress(0, new TunnelConnectionProgress
                        {
                            Generation = generation,
                            TimedOut = true
                        });
                        e.Cancel = true;
                        return;
                    }

                    worker.ReportProgress(0, new TunnelConnectionProgress
                    {
                        Generation = generation,
                        Connected = connected
                    });
                } while (!worker.CancellationPending && generation == CurrentTunnelGeneration() && !connected);
            };

            worker.ProgressChanged += (s, e) =>
            {
                if (_shutdownComplete || !(e.UserState is TunnelConnectionProgress progress) ||
                    progress.Generation != CurrentTunnelGeneration())
                    return;

                if (progress.TimedOut)
                {
                    _ = HandleTunnelConnectionTimeoutAsync(progress.Generation);
                    return;
                }

                if (_currentState == ConnectionState.Connecting && progress.Connected)
                    UpdateState(ConnectionState.Connected);
            };

            worker.RunWorkerCompleted += (s, e) =>
            {
                if (_shutdownComplete || IsDisposed || Disposing)
                    return;

                if (e.Error != null)
                {
                    Trace.TraceWarning($"Tunnel connection monitor stopped unexpectedly: {e.Error.Message}");
                    return;
                }

                if (_currentState == ConnectionState.Connecting && !_wiresock.Connected && !worker.IsBusy)
                    worker.RunWorkerAsync(CurrentTunnelGeneration());
            };

            return worker;
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

            Volatile.Write(ref _tunnelConnectionTimeoutGeneration, generation);
            return true;
        }

        private bool IsTunnelConnectionTimedOut(int generation)
        {
            return Volatile.Read(ref _tunnelConnectionTimeoutGeneration) == generation;
        }

        private async Task DisconnectTimedOutTunnelAsync(int generation)
        {
            if (_shutdownComplete || generation != CurrentTunnelGeneration() ||
                _currentState != ConnectionState.Connecting || !IsTunnelConnectionTimedOut(generation))
                return;

            await DisconnectCurrentTunnelAsync(false);

            if (!_shutdownComplete)
                MessageBox.Show(Resources.TunnelConnectTimeout, Resources.TunnelErrorTitle, MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
        }

        private async Task<ConnectAttemptResult> ConnectWithTimeoutAsync(string profile, int generation)
        {
            var connectTask = Task.Run(() =>
            {
                var connected = _wiresock.Connect(profile);
                return new ConnectAttemptResult
                {
                    Connected = connected,
                    ConnectionSequence = connected ? _wiresock.ConnectionSequence : 0
                };
            });

            using (var delayCancellation = new CancellationTokenSource())
            {
                var delayTask = Task.Delay(TunnelConnectionTimeoutMilliseconds, delayCancellation.Token);
                var completedTask = await Task.WhenAny(connectTask, delayTask);
                if (completedTask == connectTask)
                {
                    delayCancellation.Cancel();
                    return await connectTask;
                }
            }

            BeginTimedOutConnectCleanup();
            ScheduleTimedOutConnectCleanup(connectTask, generation, profile);

            if (!_shutdownComplete && generation == CurrentTunnelGeneration() &&
                _currentState == ConnectionState.Connecting)
            {
                Volatile.Write(ref _tunnelConnectionTimeoutGeneration, generation);
                UpdateState(ConnectionState.Disconnected, false, profile);
                SetActivateButtonEnabled(false);
                MessageBox.Show(Resources.TunnelConnectTimeout, Resources.TunnelErrorTitle, MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            return null;
        }

        private void ScheduleTimedOutConnectCleanup(Task<ConnectAttemptResult> connectTask, int generation,
            string profile)
        {
            ScheduleTimedOutConnectRecoveryWatchdog(profile);

            connectTask.ContinueWith(task =>
            {
                var cleanupTask = CompleteTimedOutConnectCleanupAsync(task, generation, profile);
                cleanupTask.ContinueWith(faultedTask =>
                        Trace.TraceWarning(
                            $"Unhandled timed-out tunnel cleanup error: {faultedTask.Exception?.GetBaseException().Message}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private void ScheduleTimedOutConnectRecoveryWatchdog(string profile)
        {
            Task.Delay(TimedOutConnectCleanupRecoveryMilliseconds).ContinueWith(_ =>
            {
                if (_shutdownComplete || !IsTimedOutConnectCleanupInProgress())
                    return;

                TryResetNetworkLockAfterNativeCleanupFailure("stalled timed-out connect cleanup");
                MarkNativeRecoveryRequired(profile, "stalled timed-out connect cleanup");
                EndTimedOutConnectCleanup(profile);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private static bool TryGetCompletedConnectResult(Task<ConnectAttemptResult> connectTask,
            out ConnectAttemptResult result)
        {
            result = null;

            try
            {
                result = connectTask.GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Timed-out tunnel connect finished with an error: {ex.Message}");
                return false;
            }
        }

        private async Task CompleteTimedOutConnectCleanupAsync(Task<ConnectAttemptResult> connectTask, int generation,
            string profile)
        {
            ConnectAttemptResult result = null;
            var cleanupFailed = false;

            try
            {
                if (TryGetCompletedConnectResult(connectTask, out result) && result.Connected)
                {
                    var disconnected = await DisconnectNativeTunnelAsync(result.ConnectionSequence, false);
                    if (!disconnected)
                    {
                        Trace.TraceWarning(
                            $"Timed-out tunnel sequence {result.ConnectionSequence} could not be disconnected. The tunnel may still be active.");
                        cleanupFailed = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to clean up timed-out tunnel connect: {ex.Message}");
                cleanupFailed = true;
            }
            finally
            {
                if (cleanupFailed)
                {
                    TryResetNetworkLockAfterNativeCleanupFailure("timed-out connect cleanup");
                    MarkNativeRecoveryRequired(profile, "timed-out connect cleanup failure");
                }

                EndTimedOutConnectCleanup(profile);
            }

            TryRunOnUiThread(() =>
            {
                if (!_shutdownComplete && generation == CurrentTunnelGeneration() &&
                    _currentState == ConnectionState.Connecting)
                    UpdateState(ConnectionState.Disconnected, false, profile);
            });
        }

        /// <summary>
        ///     Initialize a <see cref="T:BackgroundWorker" /> which retrieves the connected tunnel state and updates it in the UI
        /// </summary>
        /// <returns>
        ///     <see cref="T:BackgroundWorker" />
        /// </returns>
        private BackgroundWorker InitTunnelStateWorker()
        {
            var worker = new BackgroundWorker
            {
                WorkerSupportsCancellation = true,
                WorkerReportsProgress = true
            };

            worker.DoWork += (s, e) =>
            {
                var generation = (int)e.Argument;

                while (!worker.CancellationPending)
                {
                    if (generation != CurrentTunnelGeneration())
                    {
                        e.Cancel = true;
                        return;
                    }

                    if (SleepUntilCancelled(worker, 1000))
                    {
                        e.Cancel = true;
                        return;
                    }

                    if (!_wiresock.Connected) continue;

                    var stats = _wiresock.GetState();
                    worker.ReportProgress(0, new TunnelStateProgress
                    {
                        Generation = generation,
                        Stats = stats
                    });
                }
            };

            worker.ProgressChanged += (s, e) =>
            {
                if (_shutdownComplete || !(e.UserState is TunnelStateProgress progress) ||
                    progress.Generation != CurrentTunnelGeneration() || _currentState != ConnectionState.Connected)
                    return;

                var stats = progress.Stats;

                if (layoutState.Controls["txtHandshake"] is TextBox txtHandshake)
                    txtHandshake.Text = stats.time_since_last_handshake.AsTimeAgo();

                if (layoutState.Controls["txtTransfer"] is TextBox txtTransfer)
                    txtTransfer.Text = string.Format(Resources.StateTransferValue,
                        stats.rx_bytes.AsHumanReadable(),
                        stats.tx_bytes.AsHumanReadable());

                if (layoutState.Controls["txtRTT"] is TextBox txtRtt)
                    txtRtt.Text = string.Format(Resources.StateRTTValue, stats.estimated_rtt);

                if (layoutState.Controls["txtLoss"] is TextBox txtLoss)
                    txtLoss.Text = string.Format(Resources.StateLossValue, stats.estimated_loss * 100);
            };

            worker.RunWorkerCompleted += (s, e) =>
            {
                if (_shutdownComplete || IsDisposed || Disposing)
                    return;

                if (e.Error != null)
                {
                    Trace.TraceWarning($"Tunnel state monitor stopped unexpectedly: {e.Error.Message}");
                    return;
                }

                if (_currentState == ConnectionState.Connected && _wiresock.Connected && !worker.IsBusy)
                    worker.RunWorkerAsync(CurrentTunnelGeneration());
            };

            return worker;
        }

        /// <summary>
        ///     Reload profile list and optionally pre-select a profile
        /// </summary>
        /// <param name="selectedProfile">Optional profile to automatically select</param>
        private void LoadProfiles(string selectedProfile = "")
        {
            lstProfiles.Items.Clear();

            var profiles = Profile.GetProfiles().ToList();
            profiles.Sort();

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
            var activeProfileName = profileName ?? _wiresock?.ProfileName;

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

                    StartTunnelConnectionWorker();
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
                            Settings.Default.LastProfile = activeProfileName;
                            Settings.Default.Save();
                        }
                        catch (Exception ex)
                        {
                            Trace.TraceWarning($"Failed to save last active profile setting: {ex.Message}");
                        }
                    }

                    gbxState.Visible = true;

                    StartTunnelStateWorker();

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
                    cmiResetKillSwitch.Enabled = true;

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
            }

        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

#if WIRESOCKUI_ENABLE_UWP
            ScheduleVersionCheck();
#endif

            ShowPendingNativeRecoveryWarning();
            var recoveryBlocksTunnelOperations = IsNativeRecoveryRequired();

            if (Settings.Default.AutoMinimize)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
                Hide();
            }

            if (lstProfiles.Items.ContainsKey(Settings.Default.LastProfile))
                lstProfiles.Items[Settings.Default.LastProfile].Selected = true;

            // Connect to the last used configuration, if required.
            if (!Settings.Default.AutoConnect || recoveryBlocksTunnelOperations) return;

            if (lstProfiles.Items.ContainsKey(Settings.Default.LastProfile))
                OnProfileClick(lstProfiles, EventArgs.Empty);
            else
                MessageBox.Show(Resources.LastProfileNotFound, Resources.DialogAutoConnect, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
        }

        private async void OnDisconnectClick(object sender, EventArgs e)
        {
            if (!TryBeginTunnelOperation())
                return;

            try
            {
                await DisconnectCurrentTunnelAsync();
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
            using (Form form = new FrmEdit())
            {
                if (form.ShowDialog() == DialogResult.OK)
                    LoadProfiles();
            }
        }

        private void OnAddProfileClick(object sender, EventArgs e)
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

        private void OnEditProfileClick(object sender, EventArgs e)
        {
            if (lstProfiles.SelectedItems.Count == 0)
                return;

            var profile = lstProfiles.SelectedItems[0].Text;

            try
            {
                using (var form = new FrmEdit(profile))
                {
                    if (form.ShowDialog() != DialogResult.OK) return;

                    LoadProfiles(form.ReturnValue);

                    if (_wiresock.Connected && _wiresock.ProfileName == profile)
                        OnProfileClick(lstProfiles, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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

                if (_wiresock.Connected && _wiresock.ProfileName == selectedConf)
                    if (!await DisconnectCurrentTunnelAsync())
                        return;

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

        private void OnSettingsClick(object sender, EventArgs e)
        {
            using (var form = new FrmSettings())
            {
                var previousEnableKillSwitch = Settings.Default.EnableKillSwitch;

                // set the owner of the child form to the main form instance
                form.Owner = this;

                if (form.ShowDialog() == DialogResult.OK)
                {
                    _wiresock.LogLevel = _wiresock.LogLevelSetting;
                    ApplyAndSaveKillSwitchSetting(form.RequestedEnableKillSwitch, previousEnableKillSwitch);
                }
            }
        }

        private bool ApplyAndSaveKillSwitchSetting(bool requestedEnableKillSwitch, bool previousEnableKillSwitch)
        {
            var shouldApplyNativeState =
                requestedEnableKillSwitch != previousEnableKillSwitch ||
                _wiresock.HasTunnelHandle;

            if (shouldApplyNativeState && !ApplyKillSwitchSetting(requestedEnableKillSwitch))
            {
                Settings.Default.EnableKillSwitch = previousEnableKillSwitch;
                return false;
            }

            Settings.Default.EnableKillSwitch = requestedEnableKillSwitch;
            try
            {
                Settings.Default.Save();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Resources.SettingsSaveError, ex.Message), Resources.TunnelErrorTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);

                Settings.Default.EnableKillSwitch = previousEnableKillSwitch;
                if (requestedEnableKillSwitch != previousEnableKillSwitch)
                    ApplyKillSwitchSetting(previousEnableKillSwitch);

                return false;
            }
        }

        private bool ApplyKillSwitchSetting(bool enableKillSwitch)
        {
            try
            {
                if (_wiresock.HasTunnelHandle)
                {
                    if (enableKillSwitch)
                    {
                        _wiresock.KillSwitchEnabled = true;
                        return true;
                    }

                    if (!_wiresock.TryGetKillSwitchEnabled(out var killSwitchEnabled, out var diagnostic))
                    {
                        MessageBox.Show(
                            $"{Resources.TunnelKillSwitchStateError}{Environment.NewLine}{Environment.NewLine}{diagnostic}",
                            Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }

                    if (killSwitchEnabled)
                        _wiresock.KillSwitchEnabled = false;

                    return true;
                }

                if (!enableKillSwitch)
                {
                    if (!WireSockManager.TryIsNetworkLockActive(out var networkLockActive, out var queryDiagnostic))
                    {
                        MessageBox.Show(
                            $"{Resources.TunnelKillSwitchQueryError}{Environment.NewLine}{Environment.NewLine}{queryDiagnostic}",
                            Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }

                    if (networkLockActive && !WireSockManager.TryResetNetworkLock(out var resetDiagnostic))
                    {
                        MessageBox.Show(
                            $"{Resources.KillSwitchResetError}{Environment.NewLine}{Environment.NewLine}{resetDiagnostic}",
                            Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void OnResetKillSwitchClick(object sender, EventArgs e)
        {
            if (_currentState != ConnectionState.Disconnected || IsTimedOutConnectCleanupInProgress())
                return;

            if (!WireSockManager.TryResetNetworkLock(out var diagnostic))
            {
                MessageBox.Show(
                    $"{Resources.KillSwitchResetError}{Environment.NewLine}{Environment.NewLine}{diagnostic}",
                    Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Global.TryDeleteNativeRecoveryMarker();

            MessageBox.Show(Resources.KillSwitchResetSuccess, Resources.KillSwitchResetTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        ///     Handles the profile click event for a given sender and event arguments.
        ///     This function is responsible for updating the connection state and tunnel mode,
        ///     connecting or reconnecting to a profile depending on the button's text and the current state.
        /// </summary>
        /// <param name="sender">The source of the event. In this case, a Button control.</param>
        /// <param name="e">The event arguments containing information about the event.</param>
        private async void OnProfileClick(object sender, EventArgs e)
        {
            if (!TryBeginTunnelOperation())
                return;

            try
            {
                // Return if no profile is selected in the list.
                if (lstProfiles.SelectedItems.Count == 0) return;

                // Get the selected profile.
                var profile = lstProfiles.SelectedItems[0].Text;
                var disconnectOnly = false;

                if (e != EventArgs.Empty &&
                    (_currentState == ConnectionState.Connected || _currentState == ConnectionState.Connecting))
                {
                    var reconnect = sender is Button senderButton && senderButton.Text == Resources.ButtonInactive;
                    disconnectOnly = !reconnect;
                }

                Profile profileSettings = null;
                var useAdapter = Settings.Default.UseAdapter;

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

                var preserveNetworkLockForReconnect = Settings.Default.EnableKillSwitch && !disconnectOnly;

                if (_currentState == ConnectionState.Connected || _currentState == ConnectionState.Connecting)
                {
                    if (!await DisconnectCurrentTunnelAsync(e != EventArgs.Empty, preserveNetworkLockForReconnect))
                        return;

                    if (disconnectOnly)
                        return;
                }
                else if (e == EventArgs.Empty && _wiresock.HasTunnelHandle)
                {
                    if (!await DisconnectCurrentTunnelAsync(false, preserveNetworkLockForReconnect))
                        return;
                }

                try
                {
                    if (_wiresock.HasTunnelHandle &&
                        !await DisconnectNativeTunnelAsync(preserveNetworkLock: preserveNetworkLockForReconnect))
                    {
                        return;
                    }

                    _wiresock.TunnelMode = useAdapter
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
                    var connectResult = await ConnectWithTimeoutAsync(profile, connectGeneration);
                    if (connectResult == null)
                        return;

                    var connected = connectResult.Connected;
                    var connectionSequence = connectResult.ConnectionSequence;

                    if (!_shutdownComplete && connectGeneration == CurrentTunnelGeneration() &&
                        IsTunnelConnectionTimedOut(connectGeneration))
                    {
                        if (connected)
                            await DisconnectNativeTunnelAsync(connectionSequence, false);
                        else if (_wiresock.HasTunnelHandle)
                            await DisconnectNativeTunnelAsync(null, false);

                        UpdateState(ConnectionState.Disconnected, false, profile);
                        MessageBox.Show(Resources.TunnelConnectTimeout, Resources.TunnelErrorTitle,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    if (connectGeneration != CurrentTunnelGeneration() || _shutdownComplete)
                    {
                        if (connected)
                            await DisconnectNativeTunnelAsync(connectionSequence, false);

                        return;
                    }

                    if (connected)
                        UpdateState(_wiresock.Connected ? ConnectionState.Connected : ConnectionState.Connecting, true,
                            profile);
                    else
                    {
                        UpdateState(ConnectionState.Disconnected, false, profile);
                        MessageBox.Show(_wiresock.LastError ?? Resources.TunnelErrorManager, Resources.TunnelErrorTitle,
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                catch (Exception ex)
                {
                    UpdateState(ConnectionState.Disconnected, false, profile);
                    MessageBox.Show(ex.Message, Resources.TunnelErrorTitle, MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                finally
                {
                    if (_currentState == ConnectionState.Disconnected)
                        SetActivateButtonEnabled(true);
                }
            }
            finally
            {
                EndTunnelOperation();
            }
        }

        private void OnWireSockLogMessage(WireSockManager.LogMessage logMessage)
        {
            if (_shutdownComplete || IsDisposed || Disposing || !IsHandleCreated)
                return;

            var updating = false;

            try
            {
                lstLog.BeginUpdate();
                updating = true;
                ListViewItem item = new ListViewItem(new[]
                    { logMessage.Timestamp.ToString(Resources.LogTimestampFormat), logMessage.Message });
                lstLog.Items.Add(item);
                while (lstLog.Items.Count > MaxVisibleLogMessages)
                    lstLog.Items.RemoveAt(0);

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

                    btnActivate.Click += OnProfileClick;

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
                    if (_wiresock != null && _wiresock.Connected)
                    {
                        if (_wiresock.ProfileName == selectedConf)
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
