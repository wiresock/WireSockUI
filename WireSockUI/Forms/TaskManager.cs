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
        private CancellationTokenSource _refreshCancellation;
        private Image _refreshButtonImage;

        private sealed class ProcessDisplayEntry
        {
            public string DisplayName { get; set; }
            public string MatchName { get; set; }
            public string IconKey { get; set; }
        }

        private sealed class ProcessRefreshResult : IDisposable
        {
            public List<ProcessDisplayEntry> Entries { get; } = new List<ProcessDisplayEntry>();
            public Dictionary<string, Icon> Icons { get; } =
                new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);

            public void Dispose()
            {
                foreach (var icon in Icons.Values)
                    icon.Dispose();
                Icons.Clear();
            }
        }

        public TaskManager()
        {
            InitializeComponent();

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
            await RefreshProcessesAsync();
        }

        private async Task RefreshProcessesAsync()
        {
            if (IsDisposed || Disposing)
                return;

            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            var refreshCancellation = new CancellationTokenSource();
            _refreshCancellation = refreshCancellation;
            btnRefresh.Enabled = false;
            checkBoxShowUserProcesses.Enabled = false;

            ProcessRefreshResult result = null;
            try
            {
                string currentUser;
                using (var identity = WindowsIdentity.GetCurrent())
                    currentUser = identity.Name;
                var hideOtherUsers = checkBoxShowUserProcesses.Checked;

                result = await Task.Run(
                    () => BuildProcessRefreshResult(hideOtherUsers, currentUser, refreshCancellation.Token),
                    refreshCancellation.Token);

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
                result?.Dispose();
                if (ReferenceEquals(_refreshCancellation, refreshCancellation))
                {
                    _refreshCancellation = null;
                    refreshCancellation.Dispose();

                    if (!IsDisposed && !Disposing)
                    {
                        btnRefresh.Enabled = true;
                        checkBoxShowUserProcesses.Enabled = true;
                    }
                }
            }
        }

        private static ProcessRefreshResult BuildProcessRefreshResult(bool hideOtherUsers, string currentUser,
            CancellationToken cancellationToken)
        {
            const string defaultIconKey = "DefaultIcon";
            var result = new ProcessRefreshResult();
            try
            {
                var processes = ProcessList.GetProcessList()
                    .Where(p => !hideOtherUsers ||
                                string.Equals(p.User, currentUser, StringComparison.OrdinalIgnoreCase))
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

                    var iconKey = defaultIconKey;
                    if (!string.IsNullOrWhiteSpace(process.ImageName) && File.Exists(process.ImageName))
                    {
                        iconKey = process.ImageName;
                        if (!result.Icons.ContainsKey(iconKey))
                        {
                            try
                            {
                                using (var icon = Icon.ExtractAssociatedIcon(process.ImageName))
                                {
                                    if (icon != null)
                                        result.Icons.Add(iconKey, (Icon)icon.Clone());
                                    else
                                        iconKey = defaultIconKey;
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Trace.TraceWarning(
                                    $"Failed to extract process icon for '{process.ImageName}': {ex.Message}");
                                iconKey = defaultIconKey;
                            }
                        }
                    }

                    result.Entries.Add(new ProcessDisplayEntry
                    {
                        DisplayName = displayName,
                        MatchName = matchName,
                        IconKey = iconKey
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

        private void ApplyProcessRefreshResult(ProcessRefreshResult result)
        {
            _cachedProcessListItems.Clear();
            lstProcesses.SmallImageList.Images.Clear();

            const string defaultIconKey = "DefaultIcon";
            var defaultIcon = Resources.ico;
            if (defaultIcon != null)
                lstProcesses.SmallImageList.Images.Add(defaultIconKey, (Icon)defaultIcon.Clone());

            foreach (var icon in result.Icons)
                lstProcesses.SmallImageList.Images.Add(icon.Key, (Icon)icon.Value.Clone());

            foreach (var process in result.Entries)
            {
                var iconKey = result.Icons.ContainsKey(process.IconKey) ? process.IconKey : defaultIconKey;
                var listViewItem = new ListViewItem(process.DisplayName, iconKey) { Tag = process.MatchName };
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
            await RefreshProcessesAsync();
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
            await RefreshProcessesAsync();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = null;
            btnRefresh.Image = null;
            _refreshButtonImage?.Dispose();
            _refreshButtonImage = null;
            base.OnFormClosed(e);
        }
    }
}
