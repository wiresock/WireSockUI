namespace WireSockUI.Forms
{
    internal enum TunnelCommand
    {
        ActivateSelectedProfile,
        ToggleSelectedProfile
    }

    internal static class TunnelCommandPolicy
    {
        internal static bool IsDisconnectOnly(FrmMain.ConnectionState state, TunnelCommand command)
        {
            return command == TunnelCommand.ToggleSelectedProfile &&
                   (state == FrmMain.ConnectionState.Connected || state == FrmMain.ConnectionState.Connecting);
        }
    }
}
