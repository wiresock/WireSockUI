using WireSockUI.Properties;

namespace WireSockUI.Config
{
    internal sealed class ApplicationSettingsSnapshot
    {
        internal ApplicationSettingsSnapshot(bool autoRun, bool autoConnect, bool autoMinimize, bool autoUpdate,
            bool useAdapter, bool enableNotifications, bool enableKillSwitch, string logLevel)
        {
            AutoRun = autoRun;
            AutoConnect = autoConnect;
            AutoMinimize = autoMinimize;
            AutoUpdate = autoUpdate;
            UseAdapter = useAdapter;
            EnableNotifications = enableNotifications;
            EnableKillSwitch = enableKillSwitch;
            LogLevel = logLevel;
        }

        internal bool AutoRun { get; }
        internal bool AutoConnect { get; }
        internal bool AutoMinimize { get; }
        internal bool AutoUpdate { get; }
        internal bool UseAdapter { get; }
        internal bool EnableNotifications { get; }
        internal bool EnableKillSwitch { get; }
        internal string LogLevel { get; }

        internal static ApplicationSettingsSnapshot Capture()
        {
            return new ApplicationSettingsSnapshot(
                Settings.Default.AutoRun,
                Settings.Default.AutoConnect,
                Settings.Default.AutoMinimize,
                Settings.Default.AutoUpdate,
                Settings.Default.UseAdapter,
                Settings.Default.EnableNotifications,
                Settings.Default.EnableKillSwitch,
                Settings.Default.LogLevel);
        }

        internal void Apply()
        {
            Settings.Default.AutoRun = AutoRun;
            Settings.Default.AutoConnect = AutoConnect;
            Settings.Default.AutoMinimize = AutoMinimize;
            Settings.Default.AutoUpdate = AutoUpdate;
            Settings.Default.UseAdapter = UseAdapter;
            Settings.Default.EnableNotifications = EnableNotifications;
            Settings.Default.EnableKillSwitch = EnableKillSwitch;
            Settings.Default.LogLevel = LogLevel;
        }
    }
}
