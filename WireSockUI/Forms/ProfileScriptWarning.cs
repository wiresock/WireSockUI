using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using WireSockUI.Config;
using WireSockUI.Properties;

namespace WireSockUI.Forms
{
    internal static class ProfileScriptWarning
    {
        public static bool ConfirmIfProfileHasScriptHooks(IWin32Window owner, Profile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            var hooks = profile.GetConfiguredScriptHooks().ToList();
            if (hooks.Count == 0)
                return true;

            using (var dialog = CreateDialog(FormatHookSummary(hooks)))
                return dialog.ShowDialog(owner) == DialogResult.Yes;
        }

        internal static string FormatHookSummary(IEnumerable<KeyValuePair<string, string>> hooks)
        {
            if (hooks == null) throw new ArgumentNullException(nameof(hooks));

            return string.Join(Environment.NewLine + Environment.NewLine,
                hooks.Select(hook => $"{hook.Key}:{Environment.NewLine}{EscapeForDisplay(hook.Value)}"));
        }

        internal static string EscapeForDisplay(string value)
        {
            var builder = new StringBuilder();

            foreach (var character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append(@"\\");
                        break;
                    case '\r':
                        builder.Append(@"\r");
                        break;
                    case '\n':
                        builder.Append(@"\n");
                        break;
                    case '\t':
                        builder.Append(@"\t");
                        break;
                    default:
                        var category = CharUnicodeInfo.GetUnicodeCategory(character);
                        if (char.IsControl(character) || category == UnicodeCategory.Format ||
                            category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator)
                            builder.Append($@"\u{(int)character:X4}");
                        else
                            builder.Append(character);
                        break;
                }
            }

            return builder.ToString();
        }

        private static Form CreateDialog(string hookSummary)
        {
            var dialog = new Form
            {
                Text = Resources.ProfileError,
                AutoScaleMode = AutoScaleMode.Dpi,
                ClientSize = new Size(720, 460),
                MinimumSize = new Size(520, 340),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                MaximizeBox = true,
                MinimizeBox = false,
                ShowInTaskbar = false
            };

            var warning = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                MaximumSize = new Size(690, 0),
                Text = Resources.ProfileScriptWarningMessage
            };

            var commandFont = new Font(FontFamily.GenericMonospace, 9F);
            var commands = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = commandFont,
                Text = hookSummary
            };

            var prompt = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Text = Resources.ProfileScriptWarningContinue
            };

            var yesButton = new Button
            {
                AutoSize = true,
                DialogResult = DialogResult.Yes,
                Text = Resources.Yes
            };
            var noButton = new Button
            {
                AutoSize = true,
                DialogResult = DialogResult.No,
                Text = Resources.No
            };

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            buttons.Controls.Add(noButton);
            buttons.Controls.Add(yesButton);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 4
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(warning, 0, 0);
            layout.Controls.Add(commands, 0, 1);
            layout.Controls.Add(prompt, 0, 2);
            layout.Controls.Add(buttons, 0, 3);

            dialog.Controls.Add(layout);
            dialog.AcceptButton = noButton;
            dialog.CancelButton = noButton;
            dialog.ActiveControl = noButton;
            dialog.ClientSizeChanged += (sender, args) =>
                warning.MaximumSize = new Size(Math.Max(100, dialog.ClientSize.Width - 36), 0);
            dialog.Disposed += (sender, args) => commandFont.Dispose();
            return dialog;
        }
    }
}
