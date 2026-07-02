using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows.Forms;
using WireSockUI.Native;
using WireSockUI.Properties;

namespace WireSockUI.Forms
{
    public partial class TaskManager : Form
    {
        private readonly List<ListViewItem> _cachedProcessListItems = new List<ListViewItem>();

        public TaskManager()
        {
            InitializeComponent();

            // Safely set the icon
            if (Resources.ico != null) Icon = Resources.ico;

            // Safely set the refresh button image
            using (var refreshIcon = WindowsIcons.GetWindowsIcon(WindowsIcons.Icons.Refresh, 16))
            {
                if (refreshIcon != null) btnRefresh.Image = refreshIcon.ToBitmap();
            }

            // Ensure the process list rows fill the entire width, but no scrollbar appears
            if (lstProcesses != null && lstProcesses.Columns.Count > 0)
                lstProcesses.Columns[0].Width = lstProcesses.Size.Width - 18;

            // Safely set the cue banner text
            if (txtSearch != null && Resources.ProcessesSearchCue != null)
                txtSearch.SetCueBanner(Resources.ProcessesSearchCue);

            UpdateProcesses();
            FilterProcesses(null);
        }

        public string ReturnValue { get; private set; }

        private void UpdateProcesses()
        {
            _cachedProcessListItems.Clear();
            lstProcesses.SmallImageList.Images.Clear();

            var currentUser = WindowsIdentity.GetCurrent().Name;

            // Get unique processes for the current user
            var processes = ProcessList.GetProcessList()
                .Where(p => !checkBoxShowUserProcesses.Checked || p.User == currentUser)
                .Distinct(ProcessEntry.Comparer);

            // Add a default icon to the list view's image list
            const string defaultIconKey = "DefaultIcon";
            var defaultIcon = Resources.ico; // Replace with the appropriate resource for the default icon
            if (defaultIcon != null) lstProcesses.SmallImageList.Images.Add(defaultIconKey, (Icon)defaultIcon.Clone());

            // Add process items to the list view
            foreach (var process in processes)
            {
                var displayName = !string.IsNullOrWhiteSpace(process.ImageName)
                    ? Path.GetFileNameWithoutExtension(process.ImageName)
                    : Path.GetFileNameWithoutExtension(process.Name);
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = process.Name;
                var iconKey = process.ProcessId.ToString();

                // If the process's image file exists, extract its associated icon and add it to the list view's image list
                if (File.Exists(process.ImageName))
                {
                    if (process.ImageName != null)
                        using (var icon = Icon.ExtractAssociatedIcon(process.ImageName))
                        {
                            if (icon != null)
                                lstProcesses.SmallImageList.Images.Add(iconKey, (Icon)icon.Clone());
                            else
                                iconKey = defaultIconKey;
                        }
                    else
                        iconKey = defaultIconKey;
                }
                else
                {
                    iconKey = defaultIconKey;
                }

                // Create a new list view item for the process and add it to the list view
                var listViewItem = new ListViewItem(displayName, iconKey);
                _cachedProcessListItems.Add(listViewItem);
            }
        }

        private void FilterProcesses(string filter)
        {
            lstProcesses.BeginUpdate();
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

            lstProcesses.EndUpdate();
        }

        private void OnRefreshClick(object sender, EventArgs e)
        {
            UpdateProcesses();
            FilterProcesses(txtSearch.Text);
        }

        private void OnFindProcessChanged(object sender, EventArgs e)
        {
            FilterProcesses(txtSearch.Text);
        }

        private void OnProcessSelected(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            ReturnValue = lstProcesses.SelectedItems[0].Text;
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

        private void OnChangeUserProcessVisibilityCheckBox(object sender, EventArgs e)
        {
            UpdateProcesses();
            FilterProcesses(txtSearch.Text);
        }
    }
}
