using System;

namespace WireSockUI.Forms
{
    internal enum MainWindowStatusKind
    {
        Inactive,
        Activating,
        Active,
        RecoveryRequired
    }

    internal sealed class SelectedProfilePresentation
    {
        internal SelectedProfilePresentation(
            MainWindowStatusKind status,
            bool canActivate,
            bool showStatistics)
        {
            Status = status;
            CanActivate = canActivate;
            ShowStatistics = showStatistics;
        }

        internal MainWindowStatusKind Status { get; }
        internal bool CanActivate { get; }
        internal bool ShowStatistics { get; }
    }

    internal sealed class MainWindowStatePresentation
    {
        private MainWindowStatePresentation(
            FrmMain.ConnectionState state,
            bool nativeCleanupInProgress,
            bool nativeRecoveryRequired)
        {
            State = state;
            NativeCleanupInProgress = nativeCleanupInProgress;
            NativeRecoveryRequired = nativeRecoveryRequired;
        }

        internal FrmMain.ConnectionState State { get; }
        internal bool NativeCleanupInProgress { get; }
        internal bool NativeRecoveryRequired { get; }
        internal bool IsConnected => State == FrmMain.ConnectionState.Connected;
        internal bool IsConnecting => State == FrmMain.ConnectionState.Connecting;
        internal bool IsRecovery => State == FrmMain.ConnectionState.Indeterminate || NativeRecoveryRequired;
        internal bool ShowAddresses => IsConnected;
        internal bool CanDeactivate => IsConnected;
        internal bool CanResetNetworkLock =>
            !NativeCleanupInProgress &&
            (State == FrmMain.ConnectionState.Disconnected || IsRecovery);

        internal static MainWindowStatePresentation Create(
            FrmMain.ConnectionState state,
            bool nativeCleanupInProgress,
            bool nativeRecoveryRequired)
        {
            if (!Enum.IsDefined(typeof(FrmMain.ConnectionState), state))
                throw new ArgumentOutOfRangeException(nameof(state));
            return new MainWindowStatePresentation(
                state, nativeCleanupInProgress, nativeRecoveryRequired);
        }

        internal bool IsTunnelMenuChecked(bool menuRepresentsActiveProfile)
        {
            return IsConnected && menuRepresentsActiveProfile;
        }

        internal SelectedProfilePresentation ForSelectedProfile(bool selectedProfileIsActiveProfile)
        {
            var status = IsRecovery
                ? MainWindowStatusKind.RecoveryRequired
                : IsConnected && selectedProfileIsActiveProfile
                    ? MainWindowStatusKind.Active
                    : IsConnecting && selectedProfileIsActiveProfile
                        ? MainWindowStatusKind.Activating
                        : MainWindowStatusKind.Inactive;

            var canActivate = !NativeCleanupInProgress &&
                              !NativeRecoveryRequired &&
                              (State == FrmMain.ConnectionState.Disconnected ||
                               IsConnected && selectedProfileIsActiveProfile);
            return new SelectedProfilePresentation(
                status,
                canActivate,
                IsConnected && selectedProfileIsActiveProfile);
        }
    }
}
