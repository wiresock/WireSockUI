using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using WireSockUI.Config;
using WireSockUI.Native;
using WireSockUI.Properties;

namespace WireSockUI.Forms
{
    public sealed partial class FrmEdit : Form
    {
        private static readonly Regex ProfileMatch =
            new Regex(
                @"^\s*((?<comment>[;#](?!(?-i:@ws:)).*)|(?<section>\[[^\]\r\n]+\])|(?:(?<prefix>(?-i:#@ws:))\s*)?(?<key>[a-zA-Z0-9]+)[ \t]*=[ \t]*(?<value>.*?))\s*$",
                RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MultiValueMatch =
            new Regex(@"[^,]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private volatile bool _highlighting;

        private Font _editorBoldFont;
        private Font _editorItalicFont;
        private Font _editorRegularFont;
        private Timer _highlightTimer;
        private readonly string _originalProfileName;
        private string _targetConfigurationKeyName;

        public FrmEdit() : this(null)
        {
        }

        public FrmEdit(string config) : this(config, null)
        {
        }

        internal FrmEdit(string config, string sourcePath)
        {
            Initialize();

            ShowInTaskbar = false;
            _originalProfileName = sourcePath == null ? config : null;

            if (string.IsNullOrEmpty(config))
            {
                Text = Resources.EditProfileTitleNew;
                txtEditor.Text = Resources.template_conf;
                GenerateMissingPrivateKey();
            }
            else
            {
                Text = string.Format(Resources.EditProfileTitle, config);

                txtProfileName.Text = config;
                var profilePath = sourcePath ?? Profile.GetProfilePath(config);
                if (sourcePath == null)
                {
                    Profile.EnsureRegularProfileFile(profilePath);
                    txtEditor.Text = File.ReadAllText(profilePath);
                }
                else
                {
                    using (SecureFileSystem.OpenFile(profilePath, false))
                        txtEditor.Text = File.ReadAllText(profilePath);
                }
            }
        }

        public string ReturnValue { get; private set; }

        private void GenerateMissingPrivateKey()
        {
            foreach (Match m in ProfileMatch.Matches(txtEditor.Text))
            {
                if (!m.Groups["key"].Success ||
                    !string.Equals(m.Groups["key"].Value, "privatekey", StringComparison.OrdinalIgnoreCase) ||
                    !m.Groups["value"].Success ||
                    !string.IsNullOrEmpty(m.Groups["value"].Value))
                    continue;

                var newPrivateKey = Curve25519.CreateRandomPrivateKey();
                var base64PrivateKey = Convert.ToBase64String(newPrivateKey);
                txtEditor.Text = txtEditor.Text
                    .Remove(m.Groups["value"].Index, m.Groups["value"].Length)
                    .Insert(m.Groups["value"].Index, base64PrivateKey);
                txtPublicKey.Text = Convert.ToBase64String(Curve25519.GetPublicKey(newPrivateKey));
                return;
            }
        }

        private void ApplySyntaxHighlighting()
        {
            if (_highlighting) return;
            _highlighting = true;

            var hasErrors = false;

            // Saving the original settings
            var originalIndex = txtEditor.SelectionStart;
            var originalLength = txtEditor.SelectionLength;
            var originalColor = Color.Black;

            try
            {
                lblName.Focus();

                // removes any previous highlighting
                txtEditor.SelectionStart = 0;
                txtEditor.SelectionLength = txtEditor.Text.Length;
                txtEditor.SelectionColor = originalColor;

                txtEditor.SelectionFont = _editorRegularFont;

                foreach (Match m in ProfileMatch.Matches(txtEditor.Text))
                {
                    if (m.Groups["comment"].Success)
                    {
                        txtEditor.SelectionStart = m.Groups["comment"].Index;
                        txtEditor.SelectionLength = m.Groups["comment"].Length;
                        txtEditor.SelectionFont = _editorItalicFont;

                        switch (m.Groups["comment"].Value[0])
                        {
                            case '#':
                                txtEditor.SelectionColor = Color.LightSlateGray;
                                break;
                            case ';':
                                txtEditor.SelectionColor = Color.SaddleBrown;
                                break;
                        }

                        continue;
                    }

                    if (m.Groups["section"].Success)
                    {
                        txtEditor.SelectionStart = m.Groups["section"].Index;
                        txtEditor.SelectionLength = m.Groups["section"].Length;
                        txtEditor.SelectionColor = Color.DarkBlue;
                        txtEditor.SelectionFont = _editorBoldFont;

                        switch (m.Groups["section"].Value)
                        {
                            case "[Interface]":
                            case "[Peer]":
                                break;
                            // Unrecognized sections
                            default:
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                                break;
                        }

                        continue;
                    }

                    if (m.Groups["key"].Success)
                    {
                        txtEditor.SelectionStart = m.Groups["key"].Index;
                        txtEditor.SelectionLength = m.Groups["key"].Length;
                        txtEditor.SelectionColor = Color.Navy;

                        var key = m.Groups["key"].Value.ToLowerInvariant();
                        var value = string.Empty;

                        if (m.Groups["value"].Success)
                        {
                            txtEditor.SelectionStart = m.Groups["value"].Index;
                            txtEditor.SelectionLength = m.Groups["value"].Length;
                            txtEditor.SelectionColor = Color.DarkGreen;

                            value = m.Groups["value"].Value;
                        }

                        if (ConfigValueValidator.TryGetInterfaceExtensionRule(key, out var interfaceExtensionRule))
                        {
                            if (!interfaceExtensionRule.IsValid(value))
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }

                            continue;
                        }

                        switch (key)
                        {
                            // base64 256-bit keys
                            case "privatekey":
                                {
                                    if (string.IsNullOrEmpty(value))
                                    {
                                        txtPublicKey.Text = string.Empty;
                                        txtEditor.UnderlineSelection();
                                        hasErrors = true;
                                    }
                                    else
                                    {
                                        try
                                        {
                                            var binaryKey = Convert.FromBase64String(value);
                                            if (binaryKey.Length != 32)
                                                throw new FormatException();

                                            txtPublicKey.Text = Convert.ToBase64String(Curve25519.GetPublicKey(binaryKey));
                                        }
                                        catch (FormatException)
                                        {
                                            txtEditor.UnderlineSelection();
                                            hasErrors = true;
                                        }
                                    }
                                }
                                break;
                            case "publickey":
                            case "presharedkey":
                                {
                                    if (!string.IsNullOrEmpty(value))
                                        try
                                        {
                                            var binaryKey = Convert.FromBase64String(value);

                                            if (binaryKey.Length != 32)
                                                throw new FormatException();
                                        }
                                        catch (FormatException)
                                        {
                                            txtEditor.UnderlineSelection();
                                            hasErrors = true;
                                        }
                                }
                                break;
                            // IPv4/IPv6 CIDR notation values
                            case "address":
                            case "allowedips":
                            case "disallowedips":
                                {
                                    foreach (Match e in MultiValueMatch.Matches(value))
                                        if (!string.IsNullOrWhiteSpace(e.Value) &&
                                            !IpHelper.IsValidSubnetOrSingleIpAddress(e.Value.Trim()))
                                        {
                                            txtEditor.SelectionStart = m.Groups["value"].Index + e.Index;
                                            txtEditor.SelectionLength = e.Length;
                                            txtEditor.UnderlineSelection();
                                            hasErrors = true;
                                        }
                                }
                                break;
                            // IPv4/IPv6 values
                            case "dns":
                                {
                                    foreach (Match e in MultiValueMatch.Matches(value))
                                        if (!string.IsNullOrWhiteSpace(e.Value) && !IpHelper.IsValidIpAddress(e.Value.Trim()))
                                        {
                                            txtEditor.SelectionStart = m.Groups["value"].Index + e.Index;
                                            txtEditor.SelectionLength = e.Length;
                                            txtEditor.UnderlineSelection();
                                            hasErrors = true;
                                        }
                                }

                                break;
                            // IPv4, IPv6 or DNS value
                            case "endpoint":
                                if (!IpHelper.IsValidAddress(value.Trim()))
                                {
                                    txtEditor.UnderlineSelection();
                                    hasErrors = true;
                                }

                                break;
                            case "socks5proxy":
                                if (!string.IsNullOrWhiteSpace(value) && !IpHelper.IsValidAddress(value.Trim()))
                                {
                                    txtEditor.UnderlineSelection();
                                    hasErrors = true;
                                }

                                break;
                            // Numerical values
                            case "mtu":
                                if (!ConfigValueValidator.IsUIntDecimalInRange(value, 576, ushort.MaxValue))
                                {
                                    txtEditor.UnderlineSelection();
                                    hasErrors = true;
                                }
                                break;
                            case "listenport":
                                if (!ConfigValueValidator.IsUIntDecimalInRange(value, 0, ushort.MaxValue))
                                {
                                    txtEditor.UnderlineSelection();
                                    hasErrors = true;
                                }
                                break;
                            case "persistentkeepalive":
                            case "scriptexectimeout":
                                if (!ConfigValueValidator.IsUIntDecimalInRange(value, 0, uint.MaxValue))
                                {
                                    txtEditor.UnderlineSelection();
                                    hasErrors = true;
                                }
                                break;
                            // Comma-delimited string values
                            case "allowedapps":
                            case "disallowedapps":
                                {
                                    foreach (Match e in MultiValueMatch.Matches(value))
                                        if (!string.IsNullOrWhiteSpace(e.Value) &&
                                            !Regex.IsMatch(e.Value.Trim(),
                                                @"^(?:[a-zA-Z]:\\)?(?:[^<>:\\\""/\\|?*\n\r]+\\)*[^<>:\\\""/\\|?*\n\r]*$",
                                                RegexOptions.IgnoreCase))
                                        {
                                            txtEditor.SelectionStart = m.Groups["value"].Index + e.Index;
                                            txtEditor.SelectionLength = e.Length;
                                            txtEditor.UnderlineSelection();
                                            hasErrors = true;
                                        }
                                }
                                break;
                            // Boolean values
                            case "bypasslantraffic":
                            case "virtualadaptermode":
                            case "socks5proxyalltraffic":
                                if (!ConfigValueValidator.IsBool(value))
                                {
                                    txtEditor.UnderlineSelection();
                                    hasErrors = true;
                                }
                                break;
                            case "enabledefaultgateway":
                                if (!string.Equals(value.Trim(), "true", StringComparison.Ordinal) &&
                                    !string.Equals(value.Trim(), "false", StringComparison.Ordinal))
                                {
                                    txtEditor.UnderlineSelection();
                                    hasErrors = true;
                                }
                                break;
                            // Known free-form or WireGuard-managed values
                            case "table":
                                break;
                            case "i1":
                            case "i2":
                            case "i3":
                            case "i4":
                            case "i5":
                                break;
                            // String values
                            case "socks5proxyusername":
                            case "socks5proxypassword":
                            case "preup":
                            case "postup":
                            case "predown":
                            case "postdown":
                                break;
                            // Unrecognized keys
                            default:
                                break;
                        }
                    }
                }

                btnSave.Enabled = !hasErrors;
            }
            finally
            {
                try
                {
                    // restoring the original settings
                    txtEditor.SelectionStart = Math.Min(originalIndex, txtEditor.TextLength);
                    txtEditor.SelectionLength = Math.Min(originalLength, txtEditor.TextLength - txtEditor.SelectionStart);
                    txtEditor.SelectionColor = originalColor;
                    txtEditor.Focus();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Failed to restore editor selection after highlighting: {ex.Message}");
                }

                _highlighting = false;
            }
        }

        private void InsertOrAppendConfigurationValue(string key, string value)
        {
            // Insertion must be robust: it needs to handle incomplete or malformed configurations since
            // our user is in the middle of editing the file. Parsing isn't really an option.
            var possibleKeyValueMatch = new Regex(
                $@"^\s*(?:(?<comment>[;#](?!(?-i:@ws:)).*)|(?:(?<prefix>(?-i:#@ws:))\s*)?{Regex.Escape(key)}((?<afterkey>[ \t]*$)|[ \t]*(?<equals>=)?(?<afterkey>.*?)$))",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            int textReplacementIndex = txtEditor.Text.Length;
            int textReplacementLength = 0;

            // We'll first try matching the key alone while skipping commented lines. Then determine whether
            // a value is already present or not. Equals signs are optional. Examples:
            // "DisallowedApps = app1,app2, "
            // "DisallowedApps = app 1,app2"
            // "DisallowedApps     "
            var newValue = $"\n#@ws:{key} = {value}";

            foreach (Match m in possibleKeyValueMatch.Matches(txtEditor.Text))
            {
                if (m.Groups["comment"].Success) continue;

                newValue = !m.Groups["equals"].Success ? " =" : string.Empty;
                var afterKeyPart = m.Groups["afterkey"].Value.Trim();

                if (afterKeyPart.EndsWith(","))
                    newValue += $" {afterKeyPart}{value}";
                else if (!string.IsNullOrWhiteSpace(afterKeyPart))
                    newValue += $" {afterKeyPart},{value}";
                else
                    newValue += $" {value}";

                textReplacementIndex = m.Groups["afterkey"].Index;
                textReplacementLength = m.Groups["afterkey"].Length;
                break;
            }

            txtEditor.Text = txtEditor.Text
                .Remove(textReplacementIndex, textReplacementLength)
                .Insert(textReplacementIndex, newValue);
            txtEditor.SelectionStart = textReplacementIndex + newValue.Length;
            txtEditor.SelectionLength = 0;
        }

        private void Initialize()
        {
            InitializeComponent();
            _editorRegularFont = new Font(txtEditor.Font, FontStyle.Regular);
            _editorItalicFont = new Font(txtEditor.Font, FontStyle.Italic);
            _editorBoldFont = new Font(txtEditor.Font, FontStyle.Bold);
            _highlightTimer = new Timer { Interval = 150 };
            _highlightTimer.Tick += OnHighlightTimerTick;

            Icon = Resources.ico;
            txtProfileName.SetCueBanner(Resources.EditProfileCue);
            toolStripMenuItemByProcName.Image = GetWindowsIconBitmap(WindowsIcons.Icons.ProcessList, 16);
            toolStripMenuItemByDirPath.Image = GetWindowsIconBitmap(WindowsIcons.Icons.OpenTunnel, 16);
            toolStripMenuItemByFilePath.Image = GetWindowsIconBitmap(WindowsIcons.Icons.NewTunnel, 16);
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            if (!ValidateEditorBeforeSave())
            {
                DialogResult = DialogResult.None;
                return;
            }

            string tmpProfile = null;
            Profile profile;

            try
            {
                Global.EnsureConfigsFolderExists();
                tmpProfile = Path.Combine(Global.ConfigsFolder, $"{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tmpProfile, txtEditor.Text);
                profile = new Profile(tmpProfile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.EditProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                TryDeleteTemporaryProfile(tmpProfile);

                DialogResult = DialogResult.None;
                return;
            }

            var requestedProfileName = txtProfileName.Text;
            if (!Profile.IsValidProfileName(requestedProfileName))
            {
                MessageBox.Show(Resources.EditProfileNameError, Resources.EditProfileError, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                TryDeleteTemporaryProfile(tmpProfile);

                DialogResult = DialogResult.None;
                return;
            }

            var profilePath = Profile.GetProfilePath(requestedProfileName);
            var isExistingProfile = !string.IsNullOrEmpty(_originalProfileName);
            var isRename = isExistingProfile &&
                           !string.Equals(_originalProfileName, requestedProfileName,
                               StringComparison.OrdinalIgnoreCase);

            if (Profile.ProfilePathExists(profilePath) && (!isExistingProfile || isRename))
            {
                var message = Profile.IsRegularProfileFile(profilePath, out var diagnostic)
                    ? string.Format(Resources.AddProfileExistsMsg, requestedProfileName)
                    : diagnostic;

                MessageBox.Show(message,
                    Resources.EditProfileError,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                TryDeleteTemporaryProfile(tmpProfile);

                DialogResult = DialogResult.None;
                return;
            }

            if (!ProfileScriptWarning.ConfirmIfProfileHasScriptHooks(this, profile))
            {
                TryDeleteTemporaryProfile(tmpProfile);

                DialogResult = DialogResult.None;
                return;
            }

            try
            {
                if (Profile.ProfilePathExists(profilePath))
                {
                    Profile.EnsureRegularProfileFile(profilePath);
                    File.Replace(tmpProfile, profilePath, null);
                }
                else
                {
                    File.Move(tmpProfile, profilePath);
                }

                if (isRename)
                    TryDeleteOriginalProfile(_originalProfileName, profilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.EditProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                TryDeleteTemporaryProfile(tmpProfile);

                DialogResult = DialogResult.None;
                return;
            }

            ReturnValue = requestedProfileName;

            Close();
        }

        private bool ValidateEditorBeforeSave()
        {
            if (_highlightTimer != null)
                _highlightTimer.Stop();

            ApplySyntaxHighlighting();
            return btnSave.Enabled;
        }

        private static void TryDeleteOriginalProfile(string originalProfileName, string newProfilePath)
        {
            try
            {
                var originalProfilePath = Profile.GetProfilePath(originalProfileName);
                if (!string.Equals(Path.GetFullPath(originalProfilePath), Path.GetFullPath(newProfilePath),
                        StringComparison.OrdinalIgnoreCase) &&
                    Profile.ProfilePathExists(originalProfilePath))
                {
                    Profile.EnsureRegularProfileFile(originalProfilePath);
                    File.Delete(originalProfilePath);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to delete renamed profile '{originalProfileName}': {ex.Message}");
            }
        }

        private static void TryDeleteTemporaryProfile(string tmpProfile)
        {
            try
            {
                if (File.Exists(tmpProfile))
                    File.Delete(tmpProfile);
            }
            catch
            {
                // Best-effort cleanup must not hide the original save or validation failure.
            }
        }

        private void OnProfileChanged(object sender, EventArgs e)
        {
            ScheduleSyntaxHighlighting();
        }

        private void ScheduleSyntaxHighlighting()
        {
            if (_highlighting || _highlightTimer == null)
                return;

            _highlightTimer.Stop();
            _highlightTimer.Start();
        }

        private void OnHighlightTimerTick(object sender, EventArgs e)
        {
            _highlightTimer.Stop();
            ApplySyntaxHighlighting();
        }

        private void OnAddAllowedAppClick(object sender, EventArgs e)
        {
            _targetConfigurationKeyName = "AllowedApps";
            contextMenuStripAllow.Show(btnAddAllowedApp, new Point(0, btnAddAllowedApp.Height));
        }

        private void OnAddDisallowedAppClick(object sender, EventArgs e)
        {
            _targetConfigurationKeyName = "DisallowedApps";
            contextMenuStripAllow.Show(btnAddDisallowedApp, new Point(0, btnAddDisallowedApp.Height));
        }

        private void OnAllowAppByProcessNameClick(object sender, EventArgs e)
        {
            using (var taskManager = new TaskManager())
            {
                if (taskManager.ShowDialog() != DialogResult.OK) return;
                InsertOrAppendConfigurationValue(_targetConfigurationKeyName, taskManager.ReturnValue);
            }
        }

        private void OnAllowAppByDirPathClick(object sender, EventArgs e)
        {
            using (var openFolderDialog = new FolderBrowserDialog())
            {
                if (openFolderDialog.ShowDialog() != DialogResult.OK) return;
                openFolderDialog.SelectedPath += Path.DirectorySeparatorChar;

                InsertOrAppendConfigurationValue(_targetConfigurationKeyName, openFolderDialog.SelectedPath);
            }
        }

        private void OnAllowAppByFileNameClick(object sender, EventArgs e)
        {
            using (var openFileDialog = new OpenFileDialog())
            {
                if (openFileDialog.ShowDialog() != DialogResult.OK) return;
                InsertOrAppendConfigurationValue(_targetConfigurationKeyName, openFileDialog.FileName);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_highlightTimer != null)
            {
                _highlightTimer.Stop();
                _highlightTimer.Dispose();
                _highlightTimer = null;
            }

            _editorRegularFont?.Dispose();
            _editorItalicFont?.Dispose();
            _editorBoldFont?.Dispose();

            base.OnFormClosed(e);
        }

        private static Bitmap GetWindowsIconBitmap(WindowsIcons.Icons icon, int size)
        {
            using (var windowsIcon = WindowsIcons.GetWindowsIcon(icon, size))
            {
                return windowsIcon?.ToBitmap();
            }
        }
    }
}
