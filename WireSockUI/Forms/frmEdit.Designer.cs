namespace WireSockUI.Forms
{
    sealed partial class FrmEdit
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
            if (disposing)
            {
                DisposeManagedResources();
                components?.Dispose();
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
            this.pnlTop = new System.Windows.Forms.Panel();
            this.tableLayoutPanel1 = new System.Windows.Forms.TableLayoutPanel();
            this.lblPublicKey = new System.Windows.Forms.Label();
            this.txtPublicKey = new System.Windows.Forms.TextBox();
            this.txtProfileName = new System.Windows.Forms.TextBox();
            this.lblName = new System.Windows.Forms.Label();
            this.pnlBottom = new System.Windows.Forms.Panel();
            this.btnAddDisallowedApp = new System.Windows.Forms.Button();
            this.btnAddAllowedApp = new System.Windows.Forms.Button();
            this.btnSave = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.txtEditor = new System.Windows.Forms.RichTextBox();
            this.resControls = new WireSockUI.Extensions.ControlTextExtender();
            this.contextMenuStripAllow = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.toolStripMenuItemByProcName = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripMenuItemByDirPath = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripMenuItemByFilePath = new System.Windows.Forms.ToolStripMenuItem();
            this.pnlTop.SuspendLayout();
            this.tableLayoutPanel1.SuspendLayout();
            this.pnlBottom.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.resControls)).BeginInit();
            this.contextMenuStripAllow.SuspendLayout();
            this.SuspendLayout();
            // 
            // pnlTop
            // 
            this.pnlTop.Controls.Add(this.tableLayoutPanel1);
            this.pnlTop.Dock = System.Windows.Forms.DockStyle.Top;
            this.pnlTop.Location = new System.Drawing.Point(0, 0);
            this.pnlTop.Name = "pnlTop";
            this.resControls.SetResourceKey(this.pnlTop, null);
            this.pnlTop.Padding = new System.Windows.Forms.Padding(12, 8, 12, 4);
            this.pnlTop.Size = new System.Drawing.Size(760, 68);
            this.pnlTop.TabIndex = 43;
            // 
            // tableLayoutPanel1
            // 
            this.tableLayoutPanel1.ColumnCount = 2;
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 112F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tableLayoutPanel1.Controls.Add(this.lblPublicKey, 0, 1);
            this.tableLayoutPanel1.Controls.Add(this.txtPublicKey, 1, 1);
            this.tableLayoutPanel1.Controls.Add(this.txtProfileName, 1, 0);
            this.tableLayoutPanel1.Controls.Add(this.lblName, 0, 0);
            this.tableLayoutPanel1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tableLayoutPanel1.Location = new System.Drawing.Point(12, 8);
            this.tableLayoutPanel1.Name = "tableLayoutPanel1";
            this.resControls.SetResourceKey(this.tableLayoutPanel1, null);
            this.tableLayoutPanel1.RowCount = 2;
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 50F));
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 50F));
            this.tableLayoutPanel1.Size = new System.Drawing.Size(736, 56);
            this.tableLayoutPanel1.TabIndex = 0;
            // 
            // lblPublicKey
            // 
            this.lblPublicKey.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblPublicKey.AutoSize = true;
            this.lblPublicKey.Location = new System.Drawing.Point(3, 28);
            this.lblPublicKey.Name = "lblPublicKey";
            this.lblPublicKey.Padding = new System.Windows.Forms.Padding(0, 0, 8, 0);
            this.resControls.SetResourceKey(this.lblPublicKey, "EditPublicKey");
            this.lblPublicKey.Size = new System.Drawing.Size(106, 28);
            this.lblPublicKey.TabIndex = 27;
            this.lblPublicKey.Text = "Public key:";
            this.lblPublicKey.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // txtPublicKey
            // 
            this.txtPublicKey.BackColor = System.Drawing.SystemColors.Control;
            this.txtPublicKey.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.txtPublicKey.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtPublicKey.Location = new System.Drawing.Point(115, 35);
            this.txtPublicKey.Margin = new System.Windows.Forms.Padding(3, 7, 0, 4);
            this.txtPublicKey.Multiline = true;
            this.txtPublicKey.Name = "txtPublicKey";
            this.txtPublicKey.ReadOnly = true;
            this.resControls.SetResourceKey(this.txtPublicKey, null);
            this.txtPublicKey.Size = new System.Drawing.Size(621, 17);
            this.txtPublicKey.TabIndex = 26;
            // 
            // txtProfileName
            // 
            this.txtProfileName.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.txtProfileName.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtProfileName.Location = new System.Drawing.Point(115, 7);
            this.txtProfileName.Margin = new System.Windows.Forms.Padding(3, 7, 0, 4);
            this.txtProfileName.Name = "txtProfileName";
            this.resControls.SetResourceKey(this.txtProfileName, null);
            this.txtProfileName.Size = new System.Drawing.Size(621, 17);
            this.txtProfileName.TabIndex = 25;
            // 
            // lblName
            // 
            this.lblName.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblName.AutoSize = true;
            this.lblName.Location = new System.Drawing.Point(3, 0);
            this.lblName.Name = "lblName";
            this.lblName.Padding = new System.Windows.Forms.Padding(0, 0, 8, 0);
            this.resControls.SetResourceKey(this.lblName, "EditName");
            this.lblName.Size = new System.Drawing.Size(106, 28);
            this.lblName.TabIndex = 19;
            this.lblName.Text = "Name:";
            this.lblName.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // pnlBottom
            // 
            this.pnlBottom.Controls.Add(this.btnAddDisallowedApp);
            this.pnlBottom.Controls.Add(this.btnAddAllowedApp);
            this.pnlBottom.Controls.Add(this.btnSave);
            this.pnlBottom.Controls.Add(this.btnCancel);
            this.pnlBottom.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlBottom.Location = new System.Drawing.Point(0, 472);
            this.pnlBottom.Name = "pnlBottom";
            this.resControls.SetResourceKey(this.pnlBottom, null);
            this.pnlBottom.Size = new System.Drawing.Size(760, 48);
            this.pnlBottom.TabIndex = 45;
            // 
            // btnAddDisallowedApp
            // 
            this.btnAddDisallowedApp.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.btnAddDisallowedApp.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnAddDisallowedApp.Location = new System.Drawing.Point(128, 9);
            this.btnAddDisallowedApp.Name = "btnAddDisallowedApp";
            this.resControls.SetResourceKey(this.btnAddDisallowedApp, "EditDisallowApp");
            this.btnAddDisallowedApp.Size = new System.Drawing.Size(116, 30);
            this.btnAddDisallowedApp.TabIndex = 3;
            this.btnAddDisallowedApp.Text = "Disallow App...";
            this.btnAddDisallowedApp.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            this.btnAddDisallowedApp.UseVisualStyleBackColor = true;
            this.btnAddDisallowedApp.Click += new System.EventHandler(this.OnAddDisallowedAppClick);
            // 
            // btnAddAllowedApp
            // 
            this.btnAddAllowedApp.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.btnAddAllowedApp.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnAddAllowedApp.Location = new System.Drawing.Point(12, 9);
            this.btnAddAllowedApp.Name = "btnAddAllowedApp";
            this.resControls.SetResourceKey(this.btnAddAllowedApp, "EditAllowApp");
            this.btnAddAllowedApp.Size = new System.Drawing.Size(108, 30);
            this.btnAddAllowedApp.TabIndex = 2;
            this.btnAddAllowedApp.Text = "Allow App...";
            this.btnAddAllowedApp.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            this.btnAddAllowedApp.UseVisualStyleBackColor = true;
            this.btnAddAllowedApp.Click += new System.EventHandler(this.OnAddAllowedAppClick);
            // 
            // btnSave
            // 
            this.btnSave.DialogResult = System.Windows.Forms.DialogResult.OK;
            this.btnSave.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnSave.Location = new System.Drawing.Point(568, 9);
            this.btnSave.Name = "btnSave";
            this.resControls.SetResourceKey(this.btnSave, "EditSave");
            this.btnSave.Size = new System.Drawing.Size(84, 30);
            this.btnSave.TabIndex = 1;
            this.btnSave.Text = "Save";
            this.btnSave.UseVisualStyleBackColor = true;
            this.btnSave.Click += new System.EventHandler(this.OnSaveClick);
            // 
            // btnCancel
            // 
            this.btnCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.Location = new System.Drawing.Point(664, 9);
            this.btnCancel.Name = "btnCancel";
            this.resControls.SetResourceKey(this.btnCancel, "EditCancel");
            this.btnCancel.Size = new System.Drawing.Size(84, 30);
            this.btnCancel.TabIndex = 0;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            // 
            // txtEditor
            // 
            this.txtEditor.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.txtEditor.DetectUrls = false;
            this.txtEditor.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtEditor.Location = new System.Drawing.Point(0, 68);
            this.txtEditor.Name = "txtEditor";
            this.resControls.SetResourceKey(this.txtEditor, null);
            this.txtEditor.Size = new System.Drawing.Size(760, 404);
            this.txtEditor.TabIndex = 46;
            this.txtEditor.Text = "";
            this.txtEditor.TextChanged += new System.EventHandler(this.OnProfileChanged);
            // 
            // resControls
            // 
            this.resControls.ResourceClassName = "WireSockUI.Properties.Resources";
            // 
            // contextMenuStripAllow
            // 
            this.contextMenuStripAllow.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStripMenuItemByProcName,
            this.toolStripMenuItemByDirPath,
            this.toolStripMenuItemByFilePath});
            this.contextMenuStripAllow.Name = "contextMenuStripAllow";
            this.resControls.SetResourceKey(this.contextMenuStripAllow, null);
            this.contextMenuStripAllow.Size = new System.Drawing.Size(174, 70);
            // 
            // toolStripMenuItemByProcName
            // 
            this.toolStripMenuItemByProcName.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripMenuItemByProcName.Name = "toolStripMenuItemByProcName";
            this.toolStripMenuItemByProcName.Size = new System.Drawing.Size(173, 22);
            this.toolStripMenuItemByProcName.Text = "By process name...";
            this.toolStripMenuItemByProcName.Click += new System.EventHandler(this.OnAllowAppByProcessNameClick);
            // 
            // toolStripMenuItemByDirPath
            // 
            this.toolStripMenuItemByDirPath.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripMenuItemByDirPath.Name = "toolStripMenuItemByDirPath";
            this.toolStripMenuItemByDirPath.Size = new System.Drawing.Size(173, 22);
            this.toolStripMenuItemByDirPath.Text = "By directory path...";
            this.toolStripMenuItemByDirPath.Click += new System.EventHandler(this.OnAllowAppByDirPathClick);
            // 
            // toolStripMenuItemByFilePath
            // 
            this.toolStripMenuItemByFilePath.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripMenuItemByFilePath.Name = "toolStripMenuItemByFilePath";
            this.toolStripMenuItemByFilePath.Size = new System.Drawing.Size(173, 22);
            this.toolStripMenuItemByFilePath.Text = "By file path...";
            this.toolStripMenuItemByFilePath.Click += new System.EventHandler(this.OnAllowAppByFileNameClick);
            // 
            // FrmEdit
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(760, 520);
            this.Controls.Add(this.txtEditor);
            this.Controls.Add(this.pnlTop);
            this.Controls.Add(this.pnlBottom);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.MinimizeBox = false;
            this.MinimumSize = new System.Drawing.Size(720, 520);
            this.Name = "FrmEdit";
            this.resControls.SetResourceKey(this, null);
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Edit";
            this.pnlTop.ResumeLayout(false);
            this.tableLayoutPanel1.ResumeLayout(false);
            this.tableLayoutPanel1.PerformLayout();
            this.pnlBottom.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.resControls)).EndInit();
            this.contextMenuStripAllow.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion
        private System.Windows.Forms.Panel pnlTop;
        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel1;
        private System.Windows.Forms.Label lblName;
        private System.Windows.Forms.TextBox txtProfileName;
        private System.Windows.Forms.TextBox txtPublicKey;
        private System.Windows.Forms.Label lblPublicKey;
        private System.Windows.Forms.Panel pnlBottom;
        private System.Windows.Forms.Button btnSave;
        private System.Windows.Forms.Button btnCancel;
        private System.Windows.Forms.RichTextBox txtEditor;
        private System.Windows.Forms.Button btnAddAllowedApp;
        private Extensions.ControlTextExtender resControls;
        private System.Windows.Forms.Button btnAddDisallowedApp;
        private System.Windows.Forms.ContextMenuStrip contextMenuStripAllow;
        private System.Windows.Forms.ToolStripMenuItem toolStripMenuItemByProcName;
        private System.Windows.Forms.ToolStripMenuItem toolStripMenuItemByDirPath;
        private System.Windows.Forms.ToolStripMenuItem toolStripMenuItemByFilePath;
    }
}
