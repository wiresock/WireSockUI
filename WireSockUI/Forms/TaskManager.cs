using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WireSockUI.Native;
using WireSockUI.Properties;

namespace WireSockUI.Forms
{
    public partial class TaskManager : Form
    {
        private readonly List<ListViewItem> _cachedProcessListItems = new List<ListViewItem>();
        private readonly ProcessSnapshotCache _processSnapshotCache = new ProcessSnapshotCache();
        private readonly string _currentUserSid;
        private CancellationTokenSource _refreshCancellation;
        private Image _refreshButtonImage;

        private sealed class ProcessDisplayEntry
        {
            public string DisplayName { get; set; }
            public string MatchName { get; set; }
        }

        private sealed class ProcessRefreshResult
        {
            public List<ProcessDisplayEntry> Entries { get; } = new List<ProcessDisplayEntry>();
        }

        public TaskManager()
        {
            InitializeComponent();

            using (var identity = WindowsIdentity.GetCurrent())
                _currentUserSid = identity.User?.Value;

            // Safely set the icon
            if (Resources.ico != null) Icon = Resources.ico;

            // Safely set the refresh button image
            using (var refreshIcon = WindowsIcons.GetWindowsIcon(WindowsIcons.Icons.Refresh, 16))
            {
                if (refreshIcon != null)
                {
                    _refreshButtonImage = refreshIcon.ToBitmap();
                    btnRefresh.Image = _refreshButtonImage;
                }
            }

            // Ensure the process list rows fill the entire width, but no scrollbar appears
            if (lstProcesses != null && lstProcesses.Columns.Count > 0)
                lstProcesses.Columns[0].Width = lstProcesses.Size.Width - 18;

            // Safely set the cue banner text
            if (txtSearch != null && Resources.ProcessesSearchCue != null)
                txtSearch.SetCueBanner(Resources.ProcessesSearchCue);

            Shown += OnTaskManagerShown;
        }

        public string ReturnValue { get; private set; }

        private async void OnTaskManagerShown(object sender, EventArgs e)
        {
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
                    MatchName = matchName
                });
            }

            return result;
        }

        private void ApplyProcessRefreshResult(ProcessRefreshResult result)
        {
            _cachedProcessListItems.Clear();
            lstProcesses.SmallImageList.Images.Clear();

            const string defaultIconKey = "DefaultIcon";
            var defaultIcon = Resources.ico;
            if (defaultIcon != null)
                lstProcesses.SmallImageList.Images.Add(defaultIconKey, defaultIcon);

            foreach (var process in result.Entries)
            {
                var listViewItem = new ListViewItem(process.DisplayName, defaultIconKey)
                { Tag = process.MatchName };
                _cachedProcessListItems.Add(listViewItem);
            }
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
                    lstProcesses.Items.AddRange(_cachedProcessListItems.ToArray());
                }
                else
                {
                    foreach (var item in _cachedProcessListItems)
                    {
                        if (item.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) != -1)
                        {
                            var addedItem = lstProcesses.Items.Add(item);
                            addedItem.Selected = true;
                            addedItem.EnsureVisible();
                        }
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

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refreshCancellation?.Cancel();
            _refreshCancellation = null;
            btnRefresh.Image = null;
            _refreshButtonImage?.Dispose();
            _refreshButtonImage = null;
            base.OnFormClosed(e);
        }
    }
}
