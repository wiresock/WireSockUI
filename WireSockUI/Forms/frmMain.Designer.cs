using System.Windows.Forms;

namespace WireSockUI.Forms
{
    partial class FrmMain
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.imlProfiles = new System.Windows.Forms.ImageList(this.components);
            this.trayIcon = new System.Windows.Forms.NotifyIcon(this.components);
            this.mnuContext = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.cmiStatus = new System.Windows.Forms.ToolStripMenuItem();
            this.cmiAddresses = new System.Windows.Forms.ToolStripMenuItem();
            this.cmiSepTunnels = new System.Windows.Forms.ToolStripSeparator();
            this.cmiDeactivateTunnel = new System.Windows.Forms.ToolStripMenuItem();
            this.cmiResetKillSwitch = new System.Windows.Forms.ToolStripMenuItem();
            this.cmiManageTunnels = new System.Windows.Forms.ToolStripMenuItem();
            this.cmiOpenTunnel = new System.Windows.Forms.ToolStripMenuItem();
            this.cmiSepBottom = new System.Windows.Forms.ToolStripSeparator();
            this.cmiExit = new System.Windows.Forms.ToolStripMenuItem();
            this.tabControl = new System.Windows.Forms.TabControl();
            this.tabPageTunnels = new System.Windows.Forms.TabPage();
            this.splitTunnels = new System.Windows.Forms.SplitContainer();
            this.pnlLeft = new System.Windows.Forms.Panel();
            this.layoutProfiles = new System.Windows.Forms.TableLayoutPanel();
            this.mnuToolbar = new System.Windows.Forms.ToolStrip();
            this.ddmAddTunnel = new System.Windows.Forms.ToolStripSplitButton();
            this.mniImportTunnel = new System.Windows.Forms.ToolStripMenuItem();
            this.mniNewTunnel = new System.Windows.Forms.ToolStripMenuItem();
            this.tbSep1 = new System.Windows.Forms.ToolStripSeparator();
            this.mniDeleteTunnel = new System.Windows.Forms.ToolStripButton();
            this.tbSep2 = new System.Windows.Forms.ToolStripSeparator();
            this.mniSettings = new System.Windows.Forms.ToolStripButton();
            this.lstProfiles = new System.Windows.Forms.ListView();
            this.colProfile = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.layoutDetails = new System.Windows.Forms.TableLayoutPanel();
            this.pnlRight = new System.Windows.Forms.Panel();
            this.gbxState = new System.Windows.Forms.GroupBox();
            this.layoutState = new System.Windows.Forms.TableLayoutPanel();
            this.gbxPeer = new System.Windows.Forms.GroupBox();
            this.layoutPeer = new System.Windows.Forms.TableLayoutPanel();
            this.gbxInterface = new System.Windows.Forms.GroupBox();
            this.layoutInterface = new System.Windows.Forms.TableLayoutPanel();
            this.pnlProfileActions = new System.Windows.Forms.FlowLayoutPanel();
            this.btnEdit = new System.Windows.Forms.Button();
            this.tabPageLog = new System.Windows.Forms.TabPage();
            this.layoutLog = new System.Windows.Forms.TableLayoutPanel();
            this.pnlLogActions = new System.Windows.Forms.FlowLayoutPanel();
            this.btnClearLog = new System.Windows.Forms.Button();
            this.lstLog = new WireSockUI.Forms.BufferedListView();
            this.colTimestamp = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.colMessage = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.resControls = new WireSockUI.Extensions.ControlTextExtender();
            this.resMenu = new WireSockUI.Extensions.MenuTextExtender();
            this.mnuContext.SuspendLayout();
            this.tabControl.SuspendLayout();
            this.tabPageTunnels.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitTunnels)).BeginInit();
            this.splitTunnels.Panel1.SuspendLayout();
            this.splitTunnels.Panel2.SuspendLayout();
            this.splitTunnels.SuspendLayout();
            this.pnlLeft.SuspendLayout();
            this.layoutProfiles.SuspendLayout();
            this.mnuToolbar.SuspendLayout();
            this.layoutDetails.SuspendLayout();
            this.pnlRight.SuspendLayout();
            this.gbxState.SuspendLayout();
            this.gbxPeer.SuspendLayout();
            this.gbxInterface.SuspendLayout();
            this.pnlProfileActions.SuspendLayout();
            this.tabPageLog.SuspendLayout();
            this.layoutLog.SuspendLayout();
            this.pnlLogActions.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.resControls)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.resMenu)).BeginInit();
            this.SuspendLayout();
            // 
            // imlProfiles
            // 
            this.imlProfiles.ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit;
            this.imlProfiles.ImageSize = new System.Drawing.Size(24, 24);
            this.imlProfiles.TransparentColor = System.Drawing.Color.Transparent;
            // 
            // trayIcon
            // 
            this.trayIcon.ContextMenuStrip = this.mnuContext;
            this.trayIcon.Text = "WireSock: Inactive";
            this.trayIcon.Visible = false;
            this.trayIcon.DoubleClick += new System.EventHandler(this.OnFormShow);
            // 
            // mnuContext
            // 
            this.mnuContext.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.cmiStatus,
            this.cmiAddresses,
            this.cmiSepTunnels,
            this.cmiDeactivateTunnel,
            this.cmiResetKillSwitch,
            this.cmiManageTunnels,
            this.cmiOpenTunnel,
            this.cmiSepBottom,
            this.cmiExit});
            this.mnuContext.Name = "contextMenuStrip1";
            this.resControls.SetResourceKey(this.mnuContext, null);
            this.mnuContext.Size = new System.Drawing.Size(211, 148);
            // 
            // cmiStatus
            // 
            this.cmiStatus.Enabled = false;
            this.cmiStatus.Name = "cmiStatus";
            this.resMenu.SetResourceKey(this.cmiStatus, null);
            this.cmiStatus.Size = new System.Drawing.Size(210, 22);
            this.cmiStatus.Text = "Status: Inactive";
            // 
            // cmiAddresses
            // 
            this.cmiAddresses.Enabled = false;
            this.cmiAddresses.Name = "cmiAddresses";
            this.resMenu.SetResourceKey(this.cmiAddresses, null);
            this.cmiAddresses.Size = new System.Drawing.Size(210, 22);
            this.cmiAddresses.Text = "Addresses:";
            this.cmiAddresses.Visible = false;
            // 
            // cmiSepTunnels
            // 
            this.cmiSepTunnels.Name = "cmiSepTunnels";
            this.resMenu.SetResourceKey(this.cmiSepTunnels, null);
            this.cmiSepTunnels.Size = new System.Drawing.Size(207, 6);
            // 
            // cmiDeactivateTunnel
            // 
            this.cmiDeactivateTunnel.Enabled = false;
            this.cmiDeactivateTunnel.Name = "cmiDeactivateTunnel";
            this.resMenu.SetResourceKey(this.cmiDeactivateTunnel, null);
            this.cmiDeactivateTunnel.Size = new System.Drawing.Size(210, 22);
            this.cmiDeactivateTunnel.Text = "Deactivate tunnel";
            this.cmiDeactivateTunnel.Click += new System.EventHandler(this.OnDisconnectClick);
            //
            // cmiResetKillSwitch
            //
            this.cmiResetKillSwitch.Name = "cmiResetKillSwitch";
            this.resMenu.SetResourceKey(this.cmiResetKillSwitch, "MenuResetKillSwitch");
            this.cmiResetKillSwitch.Size = new System.Drawing.Size(210, 22);
            this.cmiResetKillSwitch.Text = "Reset Kill Switch";
            this.cmiResetKillSwitch.Click += new System.EventHandler(this.OnResetKillSwitchClick);
            // 
            // cmiManageTunnels
            // 
            this.cmiManageTunnels.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.cmiManageTunnels.Name = "cmiManageTunnels";
            this.resMenu.SetResourceKey(this.cmiManageTunnels, null);
            this.cmiManageTunnels.Size = new System.Drawing.Size(210, 22);
            this.cmiManageTunnels.Text = "Manage tunnels...";
            this.cmiManageTunnels.Click += new System.EventHandler(this.OnFormShow);
            // 
            // cmiOpenTunnel
            // 
            this.cmiOpenTunnel.Name = "cmiOpenTunnel";
            this.resMenu.SetResourceKey(this.cmiOpenTunnel, null);
            this.cmiOpenTunnel.Size = new System.Drawing.Size(210, 22);
            this.cmiOpenTunnel.Text = "Open tunnel(s) from file...";
            this.cmiOpenTunnel.Click += new System.EventHandler(this.OnAddProfileClick);
            // 
            // cmiSepBottom
            // 
            this.cmiSepBottom.Name = "cmiSepBottom";
            this.resMenu.SetResourceKey(this.cmiSepBottom, null);
            this.cmiSepBottom.Size = new System.Drawing.Size(207, 6);
            // 
            // cmiExit
            // 
            this.cmiExit.Name = "cmiExit";
            this.resMenu.SetResourceKey(this.cmiExit, null);
            this.cmiExit.Size = new System.Drawing.Size(210, 22);
            this.cmiExit.Text = "Exit";
            this.cmiExit.Click += new System.EventHandler(this.OnExitClick);
            // 
            // tabControl
            // 
            this.tabControl.Controls.Add(this.tabPageTunnels);
            this.tabControl.Controls.Add(this.tabPageLog);
            this.tabControl.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl.Location = new System.Drawing.Point(0, 0);
            this.tabControl.Name = "tabControl";
            this.resControls.SetResourceKey(this.tabControl, null);
            this.tabControl.Padding = new System.Drawing.Point(14, 4);
            this.tabControl.SelectedIndex = 0;
            this.tabControl.Size = new System.Drawing.Size(820, 560);
            this.tabControl.TabIndex = 12;
            this.tabControl.SelectedIndexChanged += new System.EventHandler(this.OnMainTabChanged);
            // 
            // tabPageTunnels
            // 
            this.tabPageTunnels.BackColor = System.Drawing.SystemColors.Control;
            this.tabPageTunnels.Controls.Add(this.splitTunnels);
            this.tabPageTunnels.Location = new System.Drawing.Point(4, 22);
            this.tabPageTunnels.Name = "tabPageTunnels";
            this.tabPageTunnels.Padding = new System.Windows.Forms.Padding(10);
            this.resControls.SetResourceKey(this.tabPageTunnels, "TabPageTunnels");
            this.tabPageTunnels.Size = new System.Drawing.Size(812, 534);
            this.tabPageTunnels.TabIndex = 0;
            this.tabPageTunnels.Text = "Tunnels";
            // 
            // splitTunnels
            // 
            this.splitTunnels.BackColor = System.Drawing.SystemColors.ControlDark;
            this.splitTunnels.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.splitTunnels.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitTunnels.Location = new System.Drawing.Point(10, 10);
            this.splitTunnels.Name = "splitTunnels";
            //
            // splitTunnels.Panel1
            //
            this.splitTunnels.Panel1.BackColor = System.Drawing.SystemColors.Window;
            this.splitTunnels.Panel1.Controls.Add(this.pnlLeft);
            this.splitTunnels.Panel1MinSize = 180;
            //
            // splitTunnels.Panel2
            //
            this.splitTunnels.Panel2.BackColor = System.Drawing.SystemColors.Control;
            this.splitTunnels.Panel2.Controls.Add(this.layoutDetails);
            this.splitTunnels.Panel2.Padding = new System.Windows.Forms.Padding(10, 0, 0, 0);
            this.splitTunnels.Panel2MinSize = 320;
            this.splitTunnels.Size = new System.Drawing.Size(792, 514);
            this.splitTunnels.SplitterDistance = 220;
            this.splitTunnels.SplitterWidth = 6;
            this.splitTunnels.TabIndex = 0;
            // 
            // pnlLeft
            // 
            this.pnlLeft.Controls.Add(this.layoutProfiles);
            this.pnlLeft.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pnlLeft.Location = new System.Drawing.Point(0, 0);
            this.pnlLeft.Margin = new System.Windows.Forms.Padding(0);
            this.pnlLeft.Name = "pnlLeft";
            this.pnlLeft.Padding = new System.Windows.Forms.Padding(0);
            this.resControls.SetResourceKey(this.pnlLeft, null);
            this.pnlLeft.Size = new System.Drawing.Size(218, 512);
            this.pnlLeft.TabIndex = 18;
            // 
            // layoutProfiles
            //
            this.layoutProfiles.BackColor = System.Drawing.SystemColors.Window;
            this.layoutProfiles.ColumnCount = 1;
            this.layoutProfiles.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutProfiles.Controls.Add(this.lstProfiles, 0, 0);
            this.layoutProfiles.Controls.Add(this.mnuToolbar, 0, 1);
            this.layoutProfiles.Dock = System.Windows.Forms.DockStyle.Fill;
            this.layoutProfiles.Location = new System.Drawing.Point(0, 0);
            this.layoutProfiles.Margin = new System.Windows.Forms.Padding(0);
            this.layoutProfiles.Name = "layoutProfiles";
            this.layoutProfiles.Padding = new System.Windows.Forms.Padding(0);
            this.layoutProfiles.RowCount = 2;
            this.layoutProfiles.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutProfiles.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.layoutProfiles.Size = new System.Drawing.Size(218, 512);
            this.layoutProfiles.TabIndex = 0;
            //
            // mnuToolbar
            // 
            this.mnuToolbar.AllowMerge = false;
            this.mnuToolbar.BackColor = System.Drawing.SystemColors.Window;
            this.mnuToolbar.Dock = System.Windows.Forms.DockStyle.Fill;
            this.mnuToolbar.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden;
            this.mnuToolbar.ImageScalingSize = new System.Drawing.Size(32, 32);
            this.mnuToolbar.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.ddmAddTunnel,
            this.tbSep1,
            this.mniDeleteTunnel,
            this.tbSep2,
            this.mniSettings});
            this.mnuToolbar.Location = new System.Drawing.Point(0, 483);
            this.mnuToolbar.Margin = new System.Windows.Forms.Padding(0);
            this.mnuToolbar.Name = "mnuToolbar";
            this.mnuToolbar.Padding = new System.Windows.Forms.Padding(4, 2, 4, 2);
            this.resControls.SetResourceKey(this.mnuToolbar, null);
            this.mnuToolbar.Size = new System.Drawing.Size(218, 29);
            this.mnuToolbar.TabIndex = 20;
            this.mnuToolbar.Text = "toolStrip1";
            // 
            // ddmAddTunnel
            // 
            this.ddmAddTunnel.AutoToolTip = false;
            this.ddmAddTunnel.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.mniImportTunnel,
            this.mniNewTunnel});
            this.ddmAddTunnel.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.ddmAddTunnel.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.ddmAddTunnel.Name = "ddmAddTunnel";
            this.resMenu.SetResourceKey(this.ddmAddTunnel, "MenuAddTunnel");
            this.ddmAddTunnel.Size = new System.Drawing.Size(84, 22);
            this.ddmAddTunnel.Text = "Add Tunnel";
            this.ddmAddTunnel.ButtonClick += new System.EventHandler(this.OnAddProfileClick);
            // 
            // mniImportTunnel
            // 
            this.mniImportTunnel.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.mniImportTunnel.Name = "mniImportTunnel";
            this.resMenu.SetResourceKey(this.mniImportTunnel, "MenuImportTunnel");
            this.mniImportTunnel.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.O)));
            this.mniImportTunnel.Size = new System.Drawing.Size(247, 22);
            this.mniImportTunnel.Text = "Import tunnel from file...";
            this.mniImportTunnel.Click += new System.EventHandler(this.OnAddProfileClick);
            // 
            // mniNewTunnel
            // 
            this.mniNewTunnel.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.mniNewTunnel.Name = "mniNewTunnel";
            this.resMenu.SetResourceKey(this.mniNewTunnel, "MenuEmptyTunnel");
            this.mniNewTunnel.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.N)));
            this.mniNewTunnel.Size = new System.Drawing.Size(247, 22);
            this.mniNewTunnel.Text = "Add empty tunnel...";
            this.mniNewTunnel.Click += new System.EventHandler(this.OnNewProfileClick);
            // 
            // tbSep1
            // 
            this.tbSep1.Name = "tbSep1";
            this.resMenu.SetResourceKey(this.tbSep1, null);
            this.tbSep1.Size = new System.Drawing.Size(6, 25);
            // 
            // mniDeleteTunnel
            // 
            this.mniDeleteTunnel.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.mniDeleteTunnel.Enabled = false;
            this.mniDeleteTunnel.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.mniDeleteTunnel.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.mniDeleteTunnel.Name = "mniDeleteTunnel";
            this.resMenu.SetResourceKey(this.mniDeleteTunnel, "MenuDeleteTunnel");
            this.mniDeleteTunnel.Size = new System.Drawing.Size(23, 22);
            this.mniDeleteTunnel.Text = "Delete Tunnel";
            this.mniDeleteTunnel.Click += new System.EventHandler(this.OnDeleteProfileClick);
            // 
            // tbSep2
            // 
            this.tbSep2.Name = "tbSep2";
            this.resMenu.SetResourceKey(this.tbSep2, null);
            this.tbSep2.Size = new System.Drawing.Size(6, 25);
            // 
            // mniSettings
            // 
            this.mniSettings.Alignment = System.Windows.Forms.ToolStripItemAlignment.Right;
            this.mniSettings.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.mniSettings.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.mniSettings.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.mniSettings.Name = "mniSettings";
            this.resMenu.SetResourceKey(this.mniSettings, "MenuSettings");
            this.mniSettings.Size = new System.Drawing.Size(23, 22);
            this.mniSettings.Text = "Settings";
            this.mniSettings.Click += new System.EventHandler(this.OnSettingsClick);
            // 
            // lstProfiles
            // 
            this.lstProfiles.Activation = System.Windows.Forms.ItemActivation.TwoClick;
            this.lstProfiles.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.lstProfiles.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.colProfile});
            this.lstProfiles.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lstProfiles.FullRowSelect = true;
            this.lstProfiles.HeaderStyle = System.Windows.Forms.ColumnHeaderStyle.None;
            this.lstProfiles.HideSelection = false;
            this.lstProfiles.LabelWrap = false;
            this.lstProfiles.Location = new System.Drawing.Point(4, 4);
            this.lstProfiles.Margin = new System.Windows.Forms.Padding(4);
            this.lstProfiles.MultiSelect = false;
            this.lstProfiles.Name = "lstProfiles";
            this.resControls.SetResourceKey(this.lstProfiles, null);
            this.lstProfiles.ShowGroups = false;
            this.lstProfiles.Size = new System.Drawing.Size(210, 475);
            this.lstProfiles.SmallImageList = this.imlProfiles;
            this.lstProfiles.Sorting = System.Windows.Forms.SortOrder.Ascending;
            this.lstProfiles.TabIndex = 19;
            this.lstProfiles.UseCompatibleStateImageBehavior = false;
            this.lstProfiles.View = System.Windows.Forms.View.Details;
            this.lstProfiles.ItemActivate += new System.EventHandler(this.OnProfileClick);
            this.lstProfiles.ItemSelectionChanged += new System.Windows.Forms.ListViewItemSelectionChangedEventHandler(this.OnProfileChange);
            // 
            // colProfile
            // 
            this.colProfile.Width = 190;
            // 
            // layoutDetails
            //
            this.layoutDetails.BackColor = System.Drawing.SystemColors.Control;
            this.layoutDetails.ColumnCount = 1;
            this.layoutDetails.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutDetails.Controls.Add(this.pnlRight, 0, 0);
            this.layoutDetails.Controls.Add(this.pnlProfileActions, 0, 1);
            this.layoutDetails.Dock = System.Windows.Forms.DockStyle.Fill;
            this.layoutDetails.Location = new System.Drawing.Point(10, 0);
            this.layoutDetails.Margin = new System.Windows.Forms.Padding(0);
            this.layoutDetails.Name = "layoutDetails";
            this.layoutDetails.Padding = new System.Windows.Forms.Padding(0);
            this.layoutDetails.RowCount = 2;
            this.layoutDetails.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutDetails.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.layoutDetails.Size = new System.Drawing.Size(554, 512);
            this.layoutDetails.TabIndex = 0;
            //
            // pnlRight
            // 
            this.pnlRight.AutoScroll = true;
            this.pnlRight.BackColor = System.Drawing.SystemColors.Window;
            this.pnlRight.Controls.Add(this.gbxState);
            this.pnlRight.Controls.Add(this.gbxPeer);
            this.pnlRight.Controls.Add(this.gbxInterface);
            this.pnlRight.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pnlRight.Location = new System.Drawing.Point(0, 0);
            this.pnlRight.Margin = new System.Windows.Forms.Padding(0);
            this.pnlRight.Name = "pnlRight";
            this.pnlRight.Padding = new System.Windows.Forms.Padding(8);
            this.resControls.SetResourceKey(this.pnlRight, null);
            this.pnlRight.Size = new System.Drawing.Size(554, 466);
            this.pnlRight.TabIndex = 17;
            // 
            // gbxState
            // 
            this.gbxState.AutoSize = true;
            this.gbxState.BackColor = System.Drawing.SystemColors.Window;
            this.gbxState.Controls.Add(this.layoutState);
            this.gbxState.Dock = System.Windows.Forms.DockStyle.Top;
            this.gbxState.Location = new System.Drawing.Point(8, 46);
            this.gbxState.Name = "gbxState";
            this.gbxState.Padding = new System.Windows.Forms.Padding(10, 6, 10, 8);
            this.resControls.SetResourceKey(this.gbxState, "PanelState");
            this.gbxState.Size = new System.Drawing.Size(538, 31);
            this.gbxState.TabIndex = 16;
            this.gbxState.TabStop = false;
            this.gbxState.Text = "State";
            this.gbxState.Visible = false;
            // 
            // layoutState
            // 
            this.layoutState.AutoSize = true;
            this.layoutState.BackColor = System.Drawing.SystemColors.Window;
            this.layoutState.ColumnCount = 2;
            this.layoutState.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 120F));
            this.layoutState.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutState.Dock = System.Windows.Forms.DockStyle.Top;
            this.layoutState.Location = new System.Drawing.Point(10, 19);
            this.layoutState.Name = "layoutState";
            this.layoutState.Padding = new System.Windows.Forms.Padding(0, 4, 0, 2);
            this.resControls.SetResourceKey(this.layoutState, null);
            this.layoutState.Size = new System.Drawing.Size(518, 6);
            this.layoutState.TabIndex = 0;
            // 
            // gbxPeer
            // 
            this.gbxPeer.AutoSize = true;
            this.gbxPeer.BackColor = System.Drawing.SystemColors.Window;
            this.gbxPeer.Controls.Add(this.layoutPeer);
            this.gbxPeer.Dock = System.Windows.Forms.DockStyle.Top;
            this.gbxPeer.Location = new System.Drawing.Point(8, 31);
            this.gbxPeer.Name = "gbxPeer";
            this.gbxPeer.Padding = new System.Windows.Forms.Padding(10, 6, 10, 8);
            this.resControls.SetResourceKey(this.gbxPeer, "PanelPeer");
            this.gbxPeer.Size = new System.Drawing.Size(538, 31);
            this.gbxPeer.TabIndex = 15;
            this.gbxPeer.TabStop = false;
            this.gbxPeer.Text = "Peer";
            this.gbxPeer.Visible = false;
            // 
            // layoutPeer
            // 
            this.layoutPeer.AutoSize = true;
            this.layoutPeer.BackColor = System.Drawing.SystemColors.Window;
            this.layoutPeer.ColumnCount = 2;
            this.layoutPeer.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 120F));
            this.layoutPeer.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutPeer.Dock = System.Windows.Forms.DockStyle.Top;
            this.layoutPeer.Location = new System.Drawing.Point(10, 19);
            this.layoutPeer.Name = "layoutPeer";
            this.layoutPeer.Padding = new System.Windows.Forms.Padding(0, 4, 0, 2);
            this.resControls.SetResourceKey(this.layoutPeer, null);
            this.layoutPeer.Size = new System.Drawing.Size(518, 6);
            this.layoutPeer.TabIndex = 0;
            this.layoutPeer.SizeChanged += new System.EventHandler(this.OnLayoutPanelResize);
            // 
            // gbxInterface
            // 
            this.gbxInterface.AutoSize = true;
            this.gbxInterface.BackColor = System.Drawing.SystemColors.Window;
            this.gbxInterface.Controls.Add(this.layoutInterface);
            this.gbxInterface.Dock = System.Windows.Forms.DockStyle.Top;
            this.gbxInterface.Location = new System.Drawing.Point(8, 8);
            this.gbxInterface.Name = "gbxInterface";
            this.gbxInterface.Padding = new System.Windows.Forms.Padding(10, 6, 10, 8);
            this.resControls.SetResourceKey(this.gbxInterface, null);
            this.gbxInterface.Size = new System.Drawing.Size(538, 31);
            this.gbxInterface.TabIndex = 14;
            this.gbxInterface.TabStop = false;
            this.gbxInterface.Text = "Interface: Warp";
            this.gbxInterface.Visible = false;
            // 
            // layoutInterface
            // 
            this.layoutInterface.AutoSize = true;
            this.layoutInterface.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.layoutInterface.BackColor = System.Drawing.SystemColors.Window;
            this.layoutInterface.ColumnCount = 2;
            this.layoutInterface.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 110F));
            this.layoutInterface.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutInterface.Dock = System.Windows.Forms.DockStyle.Top;
            this.layoutInterface.Location = new System.Drawing.Point(10, 19);
            this.layoutInterface.Name = "layoutInterface";
            this.layoutInterface.Padding = new System.Windows.Forms.Padding(0, 4, 0, 2);
            this.resControls.SetResourceKey(this.layoutInterface, null);
            this.layoutInterface.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.layoutInterface.Size = new System.Drawing.Size(518, 6);
            this.layoutInterface.TabIndex = 25;
            this.layoutInterface.SizeChanged += new System.EventHandler(this.OnLayoutPanelResize);
            // 
            // pnlProfileActions
            //
            this.pnlProfileActions.AutoSize = true;
            this.pnlProfileActions.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.pnlProfileActions.BackColor = System.Drawing.SystemColors.Control;
            this.pnlProfileActions.Controls.Add(this.btnEdit);
            this.pnlProfileActions.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pnlProfileActions.FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft;
            this.pnlProfileActions.Location = new System.Drawing.Point(0, 466);
            this.pnlProfileActions.Margin = new System.Windows.Forms.Padding(0);
            this.pnlProfileActions.Name = "pnlProfileActions";
            this.pnlProfileActions.Padding = new System.Windows.Forms.Padding(0, 8, 8, 10);
            this.pnlProfileActions.Size = new System.Drawing.Size(554, 46);
            this.pnlProfileActions.TabIndex = 18;
            this.pnlProfileActions.WrapContents = false;
            //
            // btnEdit
            //
            this.btnEdit.Enabled = false;
            this.btnEdit.Location = new System.Drawing.Point(466, 8);
            this.btnEdit.Margin = new System.Windows.Forms.Padding(0);
            this.btnEdit.Name = "btnEdit";
            this.resControls.SetResourceKey(this.btnEdit, "ButtonEdit");
            this.btnEdit.Size = new System.Drawing.Size(80, 28);
            this.btnEdit.TabIndex = 16;
            this.btnEdit.Text = "Edit";
            this.btnEdit.UseVisualStyleBackColor = true;
            this.btnEdit.Click += new System.EventHandler(this.OnEditProfileClick);
            //
            // tabPageLog
            // 
            this.tabPageLog.BackColor = System.Drawing.SystemColors.Control;
            this.tabPageLog.Controls.Add(this.layoutLog);
            this.tabPageLog.Location = new System.Drawing.Point(4, 22);
            this.tabPageLog.Name = "tabPageLog";
            this.tabPageLog.Padding = new System.Windows.Forms.Padding(10);
            this.resControls.SetResourceKey(this.tabPageLog, "TabPageLog");
            this.tabPageLog.Size = new System.Drawing.Size(812, 534);
            this.tabPageLog.TabIndex = 1;
            this.tabPageLog.Text = "Log";
            this.tabPageLog.UseVisualStyleBackColor = false;
            // 
            // layoutLog
            //
            this.layoutLog.ColumnCount = 1;
            this.layoutLog.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutLog.Controls.Add(this.lstLog, 0, 0);
            this.layoutLog.Controls.Add(this.pnlLogActions, 0, 1);
            this.layoutLog.Dock = System.Windows.Forms.DockStyle.Fill;
            this.layoutLog.Location = new System.Drawing.Point(10, 10);
            this.layoutLog.Margin = new System.Windows.Forms.Padding(0);
            this.layoutLog.Name = "layoutLog";
            this.layoutLog.RowCount = 2;
            this.layoutLog.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutLog.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.layoutLog.Size = new System.Drawing.Size(792, 514);
            this.layoutLog.TabIndex = 0;
            //
            // pnlLogActions
            //
            this.pnlLogActions.AutoSize = true;
            this.pnlLogActions.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.pnlLogActions.BackColor = System.Drawing.SystemColors.Control;
            this.pnlLogActions.Controls.Add(this.btnClearLog);
            this.pnlLogActions.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pnlLogActions.FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft;
            this.pnlLogActions.Location = new System.Drawing.Point(0, 468);
            this.pnlLogActions.Margin = new System.Windows.Forms.Padding(0);
            this.pnlLogActions.Name = "pnlLogActions";
            this.pnlLogActions.Padding = new System.Windows.Forms.Padding(0, 8, 8, 10);
            this.pnlLogActions.Size = new System.Drawing.Size(792, 46);
            this.pnlLogActions.TabIndex = 1;
            this.pnlLogActions.WrapContents = false;
            //
            // btnClearLog
            //
            this.btnClearLog.Enabled = false;
            this.btnClearLog.Location = new System.Drawing.Point(694, 8);
            this.btnClearLog.Margin = new System.Windows.Forms.Padding(0);
            this.btnClearLog.Name = "btnClearLog";
            this.resControls.SetResourceKey(this.btnClearLog, "ButtonClearLog");
            this.btnClearLog.Size = new System.Drawing.Size(90, 28);
            this.btnClearLog.TabIndex = 0;
            this.btnClearLog.Text = "Clear log";
            this.btnClearLog.UseVisualStyleBackColor = true;
            this.btnClearLog.Click += new System.EventHandler(this.OnClearLogClick);
            //
            // lstLog
            // 
            this.lstLog.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.colTimestamp,
            this.colMessage});
            this.lstLog.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lstLog.FullRowSelect = true;
            this.lstLog.HeaderStyle = System.Windows.Forms.ColumnHeaderStyle.Nonclickable;
            this.lstLog.HideSelection = false;
            this.lstLog.Location = new System.Drawing.Point(0, 0);
            this.lstLog.Margin = new System.Windows.Forms.Padding(0);
            this.lstLog.MultiSelect = false;
            this.lstLog.Name = "lstLog";
            this.lstLog.OwnerDraw = true;
            this.resControls.SetResourceKey(this.lstLog, null);
            this.lstLog.ShowGroups = false;
            this.lstLog.Size = new System.Drawing.Size(792, 468);
            this.lstLog.TabIndex = 0;
            this.lstLog.UseCompatibleStateImageBehavior = false;
            this.lstLog.View = System.Windows.Forms.View.Details;
            this.lstLog.DrawColumnHeader += new System.Windows.Forms.DrawListViewColumnHeaderEventHandler(this.OnLogDrawColumnHeader);
            this.lstLog.DrawItem += new System.Windows.Forms.DrawListViewItemEventHandler(this.OnLogDrawHeader);
            this.lstLog.SizeChanged += new System.EventHandler(this.OnLogWindowResize);
            // 
            // colTimestamp
            // 
            this.colTimestamp.Text = "Time";
            this.colTimestamp.Width = 140;
            // 
            // colMessage
            // 
            this.colMessage.Text = "Message";
            // 
            // resControls
            // 
            this.resControls.ResourceClassName = "WireSockUI.Properties.Resources";
            // 
            // resMenu
            // 
            this.resMenu.ResourceClassName = "WireSockUI.Properties.Resources";
            // 
            // frmMain
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.SystemColors.Control;
            this.ClientSize = new System.Drawing.Size(820, 560);
            this.Controls.Add(this.tabControl);
            this.MinimumSize = new System.Drawing.Size(680, 480);
            this.Name = "FrmMain";
            this.resControls.SetResourceKey(this, "FormMain");
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "WireSock UI";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.OnFormClosing);
            this.Resize += new System.EventHandler(this.OnFormMinimize);
            this.mnuContext.ResumeLayout(false);
            this.tabControl.ResumeLayout(false);
            this.tabPageTunnels.ResumeLayout(false);
            this.splitTunnels.Panel1.ResumeLayout(false);
            this.splitTunnels.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitTunnels)).EndInit();
            this.splitTunnels.ResumeLayout(false);
            this.pnlLeft.ResumeLayout(false);
            this.layoutProfiles.ResumeLayout(false);
            this.layoutProfiles.PerformLayout();
            this.mnuToolbar.ResumeLayout(false);
            this.mnuToolbar.PerformLayout();
            this.layoutDetails.ResumeLayout(false);
            this.pnlRight.ResumeLayout(false);
            this.pnlRight.PerformLayout();
            this.gbxState.ResumeLayout(false);
            this.gbxState.PerformLayout();
            this.gbxPeer.ResumeLayout(false);
            this.gbxPeer.PerformLayout();
            this.gbxInterface.ResumeLayout(false);
            this.gbxInterface.PerformLayout();
            this.pnlProfileActions.ResumeLayout(false);
            this.tabPageLog.ResumeLayout(false);
            this.layoutLog.ResumeLayout(false);
            this.pnlLogActions.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.resControls)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.resMenu)).EndInit();
            this.ResumeLayout(false);

        }

        #endregion
        private System.Windows.Forms.ImageList imlProfiles;
        private System.Windows.Forms.NotifyIcon trayIcon;
        private TabControl tabControl;
        private TabPage tabPageTunnels;
        private TabPage tabPageLog;
        private SplitContainer splitTunnels;
        private GroupBox gbxInterface;
        private GroupBox gbxPeer;
        private Button btnEdit;
        private Panel pnlRight;
        private Panel pnlLeft;
        private TableLayoutPanel layoutProfiles;
        private TableLayoutPanel layoutDetails;
        private FlowLayoutPanel pnlProfileActions;
        private ToolStrip mnuToolbar;
        private ListView lstProfiles;
        private ColumnHeader colProfile;
        private ToolStripSplitButton ddmAddTunnel;
        private ToolStripMenuItem mniImportTunnel;
        private ToolStripMenuItem mniNewTunnel;
        private ToolStripButton mniDeleteTunnel;
        private ToolStripSeparator tbSep1;
        private ToolStripButton mniSettings;
        private ToolStripSeparator tbSep2;
        private TableLayoutPanel layoutInterface;
        private TableLayoutPanel layoutPeer;
        private TableLayoutPanel layoutLog;
        private FlowLayoutPanel pnlLogActions;
        private Button btnClearLog;
        private BufferedListView lstLog;
        private ColumnHeader colTimestamp;
        private ColumnHeader colMessage;
        private GroupBox gbxState;
        private TableLayoutPanel layoutState;
        private ContextMenuStrip mnuContext;
        private ToolStripMenuItem cmiExit;
        private ToolStripMenuItem cmiDeactivateTunnel;
        private ToolStripMenuItem cmiResetKillSwitch;
        private ToolStripMenuItem cmiManageTunnels;
        private ToolStripSeparator cmiSepBottom;
        private ToolStripMenuItem cmiOpenTunnel;
        private ToolStripMenuItem cmiStatus;
        private ToolStripSeparator cmiSepTunnels;
        private ToolStripMenuItem cmiAddresses;
        private Extensions.ControlTextExtender resControls;
        private Extensions.MenuTextExtender resMenu;
    }
}

