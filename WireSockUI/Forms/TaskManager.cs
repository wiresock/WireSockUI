using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WireSockUI.Extensions;
using WireSockUI.Native;
using WireSockUI.Properties;

namespace WireSockUI.Forms
{
    public partial class TaskManager : Form
    {
        private const int FilterDebounceMilliseconds = 150;
        private readonly List<ListViewItem> _cachedProcessListItems = new List<ListViewItem>();
        private readonly ProcessSnapshotCache _processSnapshotCache = new ProcessSnapshotCache();
        private readonly string _currentUserSid;
        private ListViewItem[] _cachedProcessListItemArray = Array.Empty<ListViewItem>();
        private System.Windows.Forms.Timer _filterTimer;
        private CancellationTokenSource _refreshCancellation;
        private Image _refreshButtonImage;
        private bool _managedResourcesDisposed;

        private sealed class ProcessDisplayEntry
        {
            public string DisplayName { get; set; }
            public string MatchName { get; set; }
            public Icon ProcessIcon { get; set; }
        }

        private sealed class ProcessRefreshResult : IDisposable
        {
            public List<ProcessDisplayEntry> Entries { get; } = new List<ProcessDisplayEntry>();

            public void Dispose()
            {
                foreach (var entry in Entries)
                {
                    entry.ProcessIcon?.Dispose();
                    entry.ProcessIcon = null;
                }
            }
        }

        public TaskManager()
        {
            InitializeComponent();

            Font = SystemFonts.MessageBoxFont;

            using (var identity = WindowsIdentity.GetCurrent())
                _currentUserSid = identity.User?.Value;

            // Safely set the icon
            if (Resources.ico != null) Icon = Resources.ico;

            // Safely set the refresh button image
            _refreshButtonImage = WindowsIcons.GetWindowsIconBitmap(WindowsIcons.Icons.Refresh, 16);
            if (_refreshButtonImage != null)
            {
                btnRefresh.Image = _refreshButtonImage;
            }

            // Keep the single process column aligned with the resizable viewport.
            UpdateProcessColumnWidth();
            lstProcesses.ClientSizeChanged += OnProcessListClientSizeChanged;

            // Safely set the cue banner text
            if (txtSearch != null && Resources.ProcessesSearchCue != null)
                txtSearch.SetCueBanner(Resources.ProcessesSearchCue);

            _filterTimer = new System.Windows.Forms.Timer
            {
                Interval = FilterDebounceMilliseconds
            };
            _filterTimer.Tick += OnFilterTimerTick;
            Shown += OnTaskManagerShown;
        }

        public string ReturnValue { get; private set; }

        private void OnProcessListClientSizeChanged(object sender, EventArgs e)
        {
            UpdateProcessColumnWidth();
        }

        private void UpdateProcessColumnWidth()
        {
            if (lstProcesses == null || lstProcesses.Columns.Count == 0)
                return;

            lstProcesses.Columns[0].Width = Math.Max(0, lstProcesses.ClientSize.Width - 4);
        }

        private async void OnTaskManagerShown(object sender, EventArgs e)
        {
            Shown -= OnTaskManagerShown;
            await RefreshProcessesAsync(true);
        }

        private async Task RefreshProcessesAsync(bool forceSnapshotRefresh)
        {
            if (IsDisposed || Disposing)
                return;

            _refreshCancellation?.Cancel();
            var refreshCancellation = new CancellationTokenSource();
            var cancellationToken = refreshCancellation.Token;
            _refreshCancellation = refreshCancellation;
            btnRefresh.Enabled = false;
            checkBoxShowUserProcesses.Enabled = false;

            ProcessRefreshResult result = null;
            try
            {
                var hideOtherUsers = checkBoxShowUserProcesses.Checked;

                var processes = await _processSnapshotCache.GetSnapshotAsync(
                    forceSnapshotRefresh,
                    cancellationToken);
                result = await Task.Run(
                    () => BuildProcessRefreshResult(
                        processes,
                        hideOtherUsers,
                        _currentUserSid,
                        cancellationToken),
                    cancellationToken);

                if (refreshCancellation.IsCancellationRequested ||
                    !ReferenceEquals(_refreshCancellation, refreshCancellation) || IsDisposed || Disposing)
                    return;

                ApplyProcessRefreshResult(result);
                _filterTimer?.Stop();
                FilterProcesses(txtSearch.Text);
            }
            catch (OperationCanceledException)
            {
                // A newer refresh or form shutdown superseded this snapshot.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Failed to refresh the process list: {ex.Message}");
            }
            finally
            {
                result?.Dispose();

                if (ReferenceEquals(_refreshCancellation, refreshCancellation))
                {
                    _refreshCancellation = null;

                    if (!IsDisposed && !Disposing)
                    {
                        btnRefresh.Enabled = true;
                        checkBoxShowUserProcesses.Enabled = true;
                    }
                }

                refreshCancellation.Dispose();
            }
        }

        private static ProcessRefreshResult BuildProcessRefreshResult(
            IEnumerable<ProcessEntry> processSnapshot,
            bool hideOtherUsers,
            string currentUserSid,
            CancellationToken cancellationToken)
        {
            var result = new ProcessRefreshResult();
            try
            {
                var processes = (processSnapshot ?? Enumerable.Empty<ProcessEntry>())
                    .Where(p => ShouldIncludeProcessForUser(p, hideOtherUsers, currentUserSid))
                    .Distinct(ProcessEntry.Comparer);

                foreach (var process in processes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var displayName = !string.IsNullOrWhiteSpace(process.ImageName)
                        ? Path.GetFileNameWithoutExtension(process.ImageName)
                        : Path.GetFileNameWithoutExtension(process.Name);
                    if (string.IsNullOrWhiteSpace(displayName))
                        displayName = process.Name;
                    var matchName = GetProcessMatchName(process);
                    if (string.IsNullOrWhiteSpace(matchName))
                        continue;

                    result.Entries.Add(new ProcessDisplayEntry
                    {
                        DisplayName = displayName,
                        MatchName = matchName,
                        ProcessIcon = TryExtractProcessIcon(process.ImageName)
                    });
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        internal static Icon TryExtractProcessIcon(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !Path.IsPathRooted(imagePath) || !File.Exists(imagePath))
                return null;

            try
            {
                return Icon.ExtractAssociatedIcon(imagePath);
            }
            catch (Exception ex) when (ex is ArgumentException ||
                                       ex is IOException ||
                                       ex is UnauthorizedAccessException ||
                                       ex is NotSupportedException ||
                                       ex is ExternalException ||
                                       ex is SecurityException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"Unable to load the icon for process image '{Path.GetFileName(imagePath)}': {ex.Message}");
                return null;
            }
        }

        private void ApplyProcessRefreshResult(ProcessRefreshResult result)
        {
            _cachedProcessListItems.Clear();
            lstProcesses.SmallImageList.Images.Clear();

            const string defaultIconKey = "DefaultIcon";
            var defaultIcon = Resources.ico;
            if (defaultIcon != null)
                lstProcesses.SmallImageList.Images.AddClonedIcon(defaultIconKey, defaultIcon);

            var processIconIndex = 0;
            foreach (var process in result.Entries)
            {
                var imageKey = defaultIcon != null ? defaultIconKey : null;
                if (process.ProcessIcon != null)
                {
                    imageKey = $"ProcessIcon{processIconIndex++}";
                    lstProcesses.SmallImageList.Images.AddClonedIcon(imageKey, process.ProcessIcon);
                }

                var listViewItem = new ListViewItem(process.DisplayName) { Tag = process.MatchName };
                if (!string.IsNullOrEmpty(imageKey))
                    listViewItem.ImageKey = imageKey;
                _cachedProcessListItems.Add(listViewItem);
            }

            _cachedProcessListItemArray = _cachedProcessListItems.ToArray();
        }

        internal static string GetProcessMatchName(ProcessEntry process)
        {
            if (process == null)
                return null;

            var matchName = !string.IsNullOrWhiteSpace(process.ImageName)
                ? Path.GetFileName(process.ImageName)
                : Path.GetFileName(process.Name);
            if (string.IsNullOrWhiteSpace(matchName))
                return null;

            return string.IsNullOrEmpty(Path.GetExtension(matchName)) ? matchName + ".exe" : matchName;
        }

        internal static bool ShouldIncludeProcessForUser(
            ProcessEntry process,
            bool hideOtherUsers,
            string currentUserSid)
        {
            if (process == null)
                return false;
            if (!hideOtherUsers)
                return true;
            return !string.IsNullOrWhiteSpace(currentUserSid) &&
                   string.Equals(process.User, currentUserSid, StringComparison.OrdinalIgnoreCase);
        }

        private void FilterProcesses(string filter)
        {
            lstProcesses.BeginUpdate();
            try
            {
                lstProcesses.Items.Clear();

                if (string.IsNullOrEmpty(filter))
                {
                    lstProcesses.Items.AddRange(_cachedProcessListItemArray);
                }
                else
                {
                    ListViewItem firstMatch = null;
                    foreach (var item in _cachedProcessListItems)
                    {
                        if (item.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) != -1)
                        {
                            var addedItem = lstProcesses.Items.Add(item);
                            if (firstMatch == null)
                                firstMatch = addedItem;
                        }
                    }

                    if (firstMatch != null)
                    {
                        firstMatch.Selected = true;
                        firstMatch.EnsureVisible();
                    }
                }
            }
            finally
            {
                lstProcesses.EndUpdate();
            }
        }

        private async void OnRefreshClick(object sender, EventArgs e)
        {
            await RefreshProcessesAsync(true);
        }

        private void OnFindProcessChanged(object sender, EventArgs e)
        {
            if (_filterTimer == null)
                return;

            _filterTimer.Stop();
            _filterTimer.Start();
        }

        private void OnFilterTimerTick(object sender, EventArgs e)
        {
            _filterTimer.Stop();
            FilterProcesses(txtSearch.Text);
        }

        private void OnProcessSelected(object sender, EventArgs e)
        {
            if (lstProcesses.SelectedItems.Count == 0)
                return;

            ReturnValue = lstProcesses.SelectedItems[0].Tag as string;
            if (string.IsNullOrWhiteSpace(ReturnValue))
                return;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void OnProcessKeyPress(object sender, KeyPressEventArgs e)
        {
            if (char.IsControl(e.KeyChar)) return;
            txtSearch.Focus();
            txtSearch.Text += e.KeyChar;
            txtSearch.SelectionStart = txtSearch.Text.Length;
            e.Handled = true;
        }

        private async void OnChangeUserProcessVisibilityCheckBox(object sender, EventArgs e)
        {
            await RefreshProcessesAsync(false);
        }

        private void DisposeManagedResources()
        {
            if (_managedResourcesDisposed)
                return;

            _managedResourcesDisposed = true;
            Shown -= OnTaskManagerShown;
            lstProcesses.ClientSizeChanged -= OnProcessListClientSizeChanged;
            if (_filterTimer != null)
            {
                _filterTimer.Stop();
                _filterTimer.Tick -= OnFilterTimerTick;
                _filterTimer.Dispose();
                _filterTimer = null;
            }

            try
            {
                _refreshCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            _refreshCancellation = null;
            btnRefresh.Image = null;
            _refreshButtonImage?.Dispose();
            _refreshButtonImage = null;
        }
    }
}
