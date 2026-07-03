using System;
using System.Linq;
using System.Windows.Forms;
using WireSockUI.Config;
using WireSockUI.Properties;

namespace WireSockUI.Forms
{
    internal static class ProfileScriptWarning
    {
        public static bool ConfirmIfProfileHasScriptHooks(IWin32Window owner, Profile profile)
        {
            var hooks = profile.GetConfiguredScriptHooks().ToList();
            if (hooks.Count == 0)
                return true;

            var hookSummary = string.Join(Environment.NewLine,
                hooks.Select(hook => $"{hook.Key}: {Truncate(hook.Value)}"));

            var message =
                Resources.ProfileScriptWarningMessage +
                Environment.NewLine + Environment.NewLine +
                hookSummary +
                Environment.NewLine + Environment.NewLine +
                Resources.ProfileScriptWarningContinue;

            return MessageBox.Show(owner, message, Resources.ProfileError, MessageBoxButtons.YesNo,
                       MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        private static string Truncate(string value)
        {
            const int maxLength = 160;

            value = (value ?? string.Empty).Trim();
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }
    }
}
