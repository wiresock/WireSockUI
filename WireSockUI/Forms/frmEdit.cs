using System;
using System.Drawing;
using System.Globalization;
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
                @"^\s*((?<comment>[;#](?!@ws\b).*)|(?<section>\[[^\]\r\n]+\])|(?:(?<prefix>#@ws:?)\s*)?(?<key>[a-zA-Z0-9]+)[ \t]*=[ \t]*(?<value>.*?))\s*$",
                RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex MultiValueMatch =
            new Regex(@"[^,]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private volatile bool _highlighting;

        private Font _editorBoldFont;
        private Font _editorItalicFont;
        private Font _editorRegularFont;
        private string _targetConfigurationKeyName;

        public FrmEdit() : this(null)
        {
        }

        public FrmEdit(string config)
        {
            Initialize();

            ShowInTaskbar = false;

            if (string.IsNullOrEmpty(config))
            {
                Text = Resources.EditProfileTitleNew;
                txtEditor.Text = Resources.template_conf;
            }
            else
            {
                Text = string.Format(Resources.EditProfileTitle, config);

                txtProfileName.Text = config;
                txtEditor.Text = File.ReadAllText(Path.Combine(Global.ConfigsFolder, config + ".conf"));
            }

            var textChanged = ApplySyntaxHighlighting();
            if (textChanged)
                // Call it again to reapply highlighting
                ApplySyntaxHighlighting();
        }

        public string ReturnValue { get; private set; }

        private bool ApplySyntaxHighlighting()
        {
            if (_highlighting) return false;
            _highlighting = true;

            var hasErrors = false;
            var textChanged = false;

            // Saving the original settings
            var originalIndex = txtEditor.SelectionStart;
            var originalLength = txtEditor.SelectionLength;
            var originalColor = Color.Black;

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

                    switch (m.Groups["section"].Value.ToLowerInvariant())
                    {
                        case "[interface]":
                        case "[peer]":
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

                    switch (key)
                    {
                        // base64 256-bit keys
                        case "privatekey":
                        {
                            if (string.IsNullOrEmpty(value))
                            {
                                // Generate a new private key
                                var newPrivateKey = Curve25519.CreateRandomPrivateKey();
                                var base64PrivateKey = Convert.ToBase64String(newPrivateKey);

                                // Insert the new private key into the text editor
                                txtEditor.SelectionStart = m.Groups["value"].Index;
                                txtEditor.SelectionLength = m.Groups["value"].Length;
                                txtEditor.SelectedText = base64PrivateKey;

                                // Update the public key display
                                txtPublicKey.Text = Convert.ToBase64String(Curve25519.GetPublicKey(newPrivateKey));
                                textChanged = true; // Set flag to true as text is changed
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
                            if (!IsIntInRange(value, 576, 65535))
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }
                            break;
                        case "listenport":
                            if (!IsIntInRange(value, 1, 65535))
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }
                            break;
                        case "persistentkeepalive":
                        case "scriptexectimeout":
                            if (!IsIntInRange(value, 0, 65535))
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }
                            break;
                        case "jc":
                            if (!IsUIntInRange(value, 0, 128))
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }
                            break;
                        case "jd":
                            if (!IsUIntInRange(value, 0, 200))
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }
                            break;
                        case "jmin":
                        case "jmax":
                        case "s1":
                        case "s2":
                            if (!IsUIntInRange(value, 0, 1280))
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }
                            break;
                        case "s3":
                        case "s4":
                            if (!IsUIntInRange(value, 0, uint.MaxValue))
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }
                            break;
                        case "h1":
                        case "h2":
                        case "h3":
                        case "h4":
                            if (!IsUIntOrRange(value, 0, uint.MaxValue))
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
                            if (!IsBool(value))
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }
                            break;
                        // Known free-form or WireGuard-managed values
                        case "table":
                            break;
                        case "id":
                            if (!string.IsNullOrWhiteSpace(value) &&
                                Uri.CheckHostName(value.Trim()) == UriHostNameType.Unknown)
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }
                            break;
                        case "ip":
                            if (!IsOneOf(value, "quic", "dns", "sip", "stun"))
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }
                            break;
                        case "ib":
                            if (!IsOneOf(value, "chrome", "firefox", "curl", "random"))
                            {
                                txtEditor.UnderlineSelection();
                                hasErrors = true;
                            }
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
                            txtEditor.SelectionStart = m.Groups["key"].Index;
                            txtEditor.SelectionLength = m.Groups["key"].Length;
                            txtEditor.UnderlineSelection();
                            hasErrors = true;
                            break;
                    }
                }
            }

            // restoring the original settings
            txtEditor.SelectionStart = originalIndex;
            txtEditor.SelectionLength = originalLength;
            txtEditor.SelectionColor = originalColor;

            txtEditor.Focus();

            btnSave.Enabled = !hasErrors;

            _highlighting = false;
            return textChanged;
        }

        private static bool IsIntInRange(string value, int minValue, int maxValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue) &&
                   intValue >= minValue &&
                   intValue <= maxValue;
        }

        private static bool IsUIntInRange(string value, uint minValue, uint maxValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return TryParseUInt(value, out var intValue) &&
                   intValue >= minValue &&
                   intValue <= maxValue;
        }

        private static bool IsUIntOrRange(string value, uint minValue, uint maxValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            var parts = value.Split('-');
            if (parts.Length == 0 || parts.Length > 2)
                return false;

            if (!TryParseUInt(parts[0], out var first) || first < minValue || first > maxValue)
                return false;

            if (parts.Length == 1)
                return true;

            return TryParseUInt(parts[1], out var second) &&
                   second >= minValue &&
                   second <= maxValue &&
                   first <= second;
        }

        private static bool TryParseUInt(string value, out uint result)
        {
            var trimmed = value.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(trimmed.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out result);

            return uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static bool IsBool(string value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOneOf(string value, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            foreach (var item in values)
                if (string.Equals(value.Trim(), item, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        private void InsertOrAppendConfigurationValue(string key, string value)
        {
            // Insertion must be robust: it needs to handle incomplete or malformed configurations since
            // our user is in the middle of editing the file. Parsing isn't really an option.
            var possibleKeyValueMatch = new Regex(
                $@"^\s*(?:(?<comment>[;#](?!@ws\b).*)|(?:(?<prefix>#@ws:?)\s*)?{Regex.Escape(key)}((?<afterkey>[ \t]*$)|[ \t]*(?<equals>=)?(?<afterkey>.*?)$))",
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

            Icon = Resources.ico;
            txtProfileName.SetCueBanner(Resources.EditProfileCue);
            toolStripMenuItemByProcName.Image = GetWindowsIconBitmap(WindowsIcons.Icons.ProcessList, 16);
            toolStripMenuItemByDirPath.Image = GetWindowsIconBitmap(WindowsIcons.Icons.OpenTunnel, 16);
            toolStripMenuItemByFilePath.Image = GetWindowsIconBitmap(WindowsIcons.Icons.NewTunnel, 16);
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            var tmpProfile = Path.Combine(Global.ConfigsFolder, $"{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllText(tmpProfile, txtEditor.Text);
                var profile = new Profile(tmpProfile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.EditProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                TryDeleteTemporaryProfile(tmpProfile);

                DialogResult = DialogResult.None;
                return;
            }

            if (!Profile.IsValidProfileName(txtProfileName.Text))
            {
                MessageBox.Show(Resources.EditProfileNameError, Resources.EditProfileError, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                TryDeleteTemporaryProfile(tmpProfile);

                DialogResult = DialogResult.None;
                return;
            }

            var profilePath = Path.Combine(Global.ConfigsFolder, txtProfileName.Text + ".conf");

            try
            {
                if (File.Exists(profilePath))
                    File.Replace(tmpProfile, profilePath, null);
                else
                    File.Move(tmpProfile, profilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.EditProfileError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                TryDeleteTemporaryProfile(tmpProfile);

                DialogResult = DialogResult.None;
                return;
            }

            ReturnValue = txtProfileName.Text;

            Close();
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
            var textChanged = ApplySyntaxHighlighting();
            if (textChanged)
                // Call it again to reapply highlighting
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
