using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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
            Disconnected
        }

        private readonly BackgroundWorker _tunnelConnectionWorker;
        private readonly BackgroundWorker _tunnelStateWorker;

        /**
         * @brief The manager that handles the Wireguard connections.
         */
        private readonly WireSockManager _wiresock;

        private ConnectionState _currentState = ConnectionState.Disconnected;
        private bool _exitRequested;
        private bool _shutdownComplete;
        private int _tunnelGeneration;
        private Icon _ownedTrayIcon;

        private sealed class TunnelConnectionProgress
        {
            public int Generation { get; set; }
            public bool Connected { get; set; }
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

            // Don't try to elevate when running under debugger
            if (!Debugger.IsAttached)
                if (!IsCurrentProcessElevated() && !Settings.Default.DisableAutoAdmin)
                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Environment.CurrentDirectory,
                            FileName = Application.ExecutablePath,
                            Verb = "runas"
                        };
                        Process.Start(startInfo);
                        Environment.Exit(1);
                    }
                    catch
                    {
                        // If the user refused the elevation, or an error occurred
                        // MessageBox.Show("Unable to run as administrator. Continuing as normal user.");
                    }

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
            SetTrayIcon(Resources.ico, false);
            cmiStatus.Image = BitmapExtensions.DrawCircle(16, 15, Brushes.DarkGray);

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

            // Update the list of available configurations.
            LoadProfiles();

            // Create a new WireSockManager instance, attached to the logging control
            _wiresock = new WireSockManager(OnWireSockLogMessage);
            _wiresock.LogLevel = _wiresock.LogLevelSetting;
        }

        private static bool IsCurrentProcessElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
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
                btnActivate.Enabled = enabled;
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

        private async Task<bool> DisconnectCurrentTunnelAsync(bool notify = true)
        {
            UpdateState(ConnectionState.Disconnected, notify);
            return await DisconnectNativeTunnelAsync();
        }

        private async Task<bool> DisconnectNativeTunnelAsync()
        {
            bool disconnected;

            try
            {
                disconnected = await Task.Run(() => _wiresock.Disconnect());
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (!disconnected)
                MessageBox.Show(
                    "WireSock stopped the tunnel, but could not release the native tunnel handle. Retry disconnect or restart WireSock UI before connecting again.",
                    Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);

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

            try
            {
                _wiresock?.Disconnect();
                _wiresock?.Dispose();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to cleanly shut down WireSock manager: {ex.Message}");
            }

            trayIcon.Visible = false;
            SetTrayIcon(null, false);

            if (Global.AlreadyRunning != null)
            {
                Global.AlreadyRunning.Dispose();
                Global.AlreadyRunning = null;
            }
        }

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

                if (_currentState == ConnectionState.Connecting && progress.Connected)
                    UpdateState(ConnectionState.Connected);
            };

            worker.RunWorkerCompleted += (s, e) =>
            {
                if (_shutdownComplete || IsDisposed || Disposing || e.Error != null)
                    return;

                if (_currentState == ConnectionState.Connecting && !_wiresock.Connected && !worker.IsBusy)
                    worker.RunWorkerAsync(CurrentTunnelGeneration());
            };

            return worker;
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
                if (_shutdownComplete || IsDisposed || Disposing || e.Error != null)
                    return;

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
                .Select(p => new ListViewItem(p, "disconnected") { Name = p }).ToArray());

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
        private void UpdateState(ConnectionState state, bool notify = true)
        {
            _currentState = state;

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
                        btnActivate.Enabled = true;
                    }

                    imgStatus?.Focus();

                    cmiDeactivateTunnel.Enabled = true;

                    if (TryGetProfileItem(_wiresock.ProfileName, out var connectingProfile))
                        connectingProfile.ImageKey = ConnectionState.Connecting.ToString();

                    trayIcon.Text = Resources.TrayActivating;

                    StartTunnelConnectionWorker();
                    break;
                case ConnectionState.Connected:
                    if (btnActivate != null)
                    {
                        btnActivate.Text = Resources.ButtonActive;
                        btnActivate.Enabled = true;
                    }

                    if (imgStatus != null)
                    {
                        imgStatus.Image = GetWindowsIconBitmap(WindowsIcons.Icons.Activated, 16);
                        if (txtStatus != null) txtStatus.Text = Resources.InterfaceStatusActive;

                        SetTrayIcon(Resources.ico.SuperImpose(64, WindowsIcons.Icons.Activated, 48, 24), true);
                        trayIcon.Text = Resources.TrayActive;

                        cmiStatus.Image = imgStatus.Image;
                    }

                    cmiStatus.Text = Resources.ContextMenuActive;

                    if (txtAddresses != null) cmiAddresses.Text = txtAddresses.Text;
                    cmiAddresses.Visible = true;

                    cmiDeactivateTunnel.Enabled = true;

                    foreach (ToolStripItem item in mnuContext.Items)
                        if (item is ToolStripMenuItem menuItem && Equals(menuItem.Tag, "tunnel"))
                            menuItem.Checked = menuItem.Text == _wiresock.ProfileName;

                    if (TryGetProfileItem(_wiresock.ProfileName, out var connectedProfile))
                        connectedProfile.ImageKey = ConnectionState.Connected.ToString();

                    Settings.Default.LastProfile = _wiresock.ProfileName;
                    Settings.Default.Save();

                    gbxState.Visible = true;

                    StartTunnelStateWorker();

#if WIRESOCKUI_ENABLE_UWP
                    if (notify && Settings.Default.EnableNotifications)
                        Notifications.Notifications.Notify(Resources.ToastActiveTitle,
                            string.Format(Resources.ToastActiveMessage, _wiresock.ProfileName));
#endif
                    break;
                case ConnectionState.Disconnected:
                    AdvanceTunnelGeneration();

                    if (btnActivate != null)
                    {
                        btnActivate.Text = Resources.ButtonInactive;
                        btnActivate.Enabled = true;
                    }

                    if (imgStatus != null)
                    {
                        imgStatus.Image = BitmapExtensions.DrawCircle(16, 15, Brushes.DarkGray);
                        if (txtStatus != null) txtStatus.Text = Resources.InterfaceStatusInactive;

                        SetTrayIcon(Resources.ico, false);
                        trayIcon.Text = Resources.TrayInactive;

                        cmiStatus.Image = imgStatus.Image;
                    }

                    cmiStatus.Text = Resources.ContextMenuInactive;

                    cmiAddresses.Text = string.Empty;
                    cmiAddresses.Visible = false;

                    cmiDeactivateTunnel.Enabled = false;

                    foreach (ToolStripItem item in mnuContext.Items)
                        if (item is ToolStripMenuItem menuItem && Equals(menuItem.Tag, "tunnel"))
                            menuItem.Checked = false;

                    if (TryGetProfileItem(_wiresock.ProfileName, out var disconnectedProfile))
                        disconnectedProfile.ImageKey = ConnectionState.Disconnected.ToString();

                    gbxState.Visible = false;
                    _tunnelConnectionWorker.CancelAsync();
                    _tunnelStateWorker.CancelAsync();

#if WIRESOCKUI_ENABLE_UWP
                    if (notify && Settings.Default.EnableNotifications)
                        Notifications.Notifications.Notify(Resources.ToastInactiveTitle,
                            string.Format(Resources.ToastInactiveMessage, _wiresock.ProfileName));
#endif
                    break;
            }

        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            if (Settings.Default.AutoMinimize)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
                Hide();
            }

            if (lstProfiles.Items.ContainsKey(Settings.Default.LastProfile))
                lstProfiles.Items[Settings.Default.LastProfile].Selected = true;

            // Connect to the last used configuration, if required.
            if (!Settings.Default.AutoConnect) return;

            if (lstProfiles.Items.ContainsKey(Settings.Default.LastProfile))
                OnProfileClick(lstProfiles, EventArgs.Empty);
            else
                MessageBox.Show(Resources.LastProfileNotFound, Resources.DialogAutoConnect, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
        }

        private async void OnDisconnectClick(object sender, EventArgs e)
        {
            await DisconnectCurrentTunnelAsync();
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

                if (Profile.GetProfiles().Contains(profileName, StringComparer.OrdinalIgnoreCase))
                {
                    MessageBox.Show(string.Format(Resources.AddProfileExistsMsg, profileName),
                        Resources.AddProfileExistsTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!Profile.IsValidProfileName(profileName))
                {
                    MessageBox.Show(Resources.EditProfileNameError, Resources.ProfileError, MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                try
                {
                    new Profile(filePath);
                    File.Copy(filePath, Profile.GetProfilePath(profileName));
                    LoadProfiles(profileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnEditProfileClick(object sender, EventArgs e)
        {
            var profile = lstProfiles.SelectedItems[0].Text;

            using (var form = new FrmEdit(profile))
            {
                if (form.ShowDialog() != DialogResult.OK) return;

                LoadProfiles(form.ReturnValue);

                if (_wiresock.Connected && _wiresock.ProfileName == profile)
                    OnProfileClick(lstProfiles, EventArgs.Empty);
            }
        }

        private async void OnDeleteProfileClick(object sender, EventArgs e)
        {
            var selectedConf = lstProfiles.SelectedItems[0].Text;

            if (MessageBox.Show(string.Format(Resources.DeleteProfileConfirmMsg, selectedConf),
                    Resources.DeleteProfileConfirmTitle,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) != DialogResult.Yes)
                return;

            if (_wiresock.Connected && _wiresock.ProfileName == selectedConf)
                if (!await DisconnectCurrentTunnelAsync())
                    return;

            try
            {
                File.Delete(Profile.GetProfilePath(selectedConf));
                LoadProfiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnSettingsClick(object sender, EventArgs e)
        {
            using (var form = new FrmSettings())
            {
                // set the owner of the child form to the main form instance
                form.Owner = this;

                if (form.ShowDialog() == DialogResult.OK)
                {
                    _wiresock.LogLevel = _wiresock.LogLevelSetting;
                    ApplyKillSwitchSetting();
                }
            }
        }

        private void ApplyKillSwitchSetting()
        {
            try
            {
                if (_wiresock.HasTunnelHandle)
                {
                    if (Settings.Default.EnableKillSwitch)
                    {
                        _wiresock.KillSwitchEnabled = Settings.Default.EnableKillSwitch;
                        return;
                    }

                    if (!_wiresock.TryGetKillSwitchEnabled(out var killSwitchEnabled, out var diagnostic))
                    {
                        MessageBox.Show(
                            $"Unable to confirm the current Kill Switch state.{Environment.NewLine}{Environment.NewLine}{diagnostic}",
                            Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    if (killSwitchEnabled)
                        _wiresock.KillSwitchEnabled = false;

                    return;
                }

                if (!Settings.Default.EnableKillSwitch)
                {
                    if (!WireSockManager.TryIsNetworkLockActive(out var networkLockActive, out var queryDiagnostic))
                    {
                        MessageBox.Show(
                            $"Unable to query the current Kill Switch network lock state.{Environment.NewLine}{Environment.NewLine}{queryDiagnostic}",
                            Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    if (networkLockActive && !WireSockManager.TryResetNetworkLock(out var resetDiagnostic))
                        MessageBox.Show(
                            $"{Resources.KillSwitchResetError}{Environment.NewLine}{Environment.NewLine}{resetDiagnostic}",
                            Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.TunnelErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
            // Return if no profile is selected in the list.
            if (lstProfiles.SelectedItems.Count == 0) return;

            // Check if the event arguments are not empty.
            if (e != EventArgs.Empty)
            {
                // Check if the current state is connected or connecting.
                if (_currentState == ConnectionState.Connected || _currentState == ConnectionState.Connecting)
                {
                    var reconnect = false;

                    // Check if the sender is a Button, and if its text is equal to ButtonInactive.
                    if (sender is Button senderButton)
                        if (senderButton.Text == Resources.ButtonInactive)
                            reconnect = true;

                    // Update the state to disconnected.
                    if (!await DisconnectCurrentTunnelAsync())
                        return;

                    // Proceed with reconnecting if the reconnect flag is set.
                    if (!reconnect) return;
                }
            }
            else
            {
                // Update the state to disconnected.
                if (!await DisconnectCurrentTunnelAsync(false))
                    return;
            }

            // Get the selected profile.
            var profile = lstProfiles.SelectedItems[0].Text;
            Profile profileSettings;

            try
            {
                profileSettings = Profile.LoadProfile(profile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.ProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var useAdapter = Settings.Default.UseAdapter;
            if (bool.TryParse(profileSettings.VirtualAdapterMode, out var profileUseAdapter))
                useAdapter = profileUseAdapter;

            try
            {
                if (_wiresock.HasTunnelHandle && !await DisconnectNativeTunnelAsync())
                {
                    return;
                }

                if (IsCurrentProcessElevated())
                    // Set the tunnel mode based on the application settings and profile override.
                    _wiresock.TunnelMode = useAdapter
                        ? WireSockManager.Mode.VirtualAdapter
                        : WireSockManager.Mode.Transparent;
                else
                    _wiresock.TunnelMode = WireSockManager.Mode.Transparent;
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
                var connected = await Task.Run(() => _wiresock.Connect(profile));

                if (connectGeneration != CurrentTunnelGeneration() || _shutdownComplete)
                    return;

                if (connected)
                    UpdateState(ConnectionState.Connecting);
                else
                    MessageBox.Show(_wiresock.LastError ?? Resources.TunnelErrorManager, Resources.TunnelErrorTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (_currentState == ConnectionState.Disconnected)
                    SetActivateButtonEnabled(true);
            }
        }

        private void OnWireSockLogMessage(WireSockManager.LogMessage logMessage)
        {
            lstLog.BeginUpdate();

            ListViewItem item = new ListViewItem(new[]
                { logMessage.Timestamp.ToString(Resources.LogTimestampFormat), logMessage.Message });
            lstLog.Items.Add(item);
            lstLog.Items[lstLog.Items.Count - 1].EnsureVisible();

            lstLog.EndUpdate();
        }

        #region Layout

        private void OnProfileChange(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            gbxInterface.Visible = false;
            gbxInterface.Text = string.Empty;
            layoutInterface.Controls.Clear();
            layoutInterface.RowStyles.Clear();

            gbxPeer.Visible = false;
            layoutPeer.Controls.Clear();
            layoutPeer.RowStyles.Clear();

            gbxState.Visible = false;
            layoutState.Controls.Clear();
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
                        false, BitmapExtensions.DrawCircle(16, 15, Brushes.DarkGray));
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
                    if (_wiresock.Connected)
                    {
                        if (_wiresock.ProfileName == selectedConf)
                            UpdateState(ConnectionState.Connected, false);
                        else
                            btnActivate.Enabled = false;
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
                Bitmap icon = null)
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
            lstLog.Columns[1].Width = lstLog.Columns[0].Width + lstLog.Size.Width - 4;
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
