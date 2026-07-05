using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using WireSockUI.Config;
using WireSockUI.Properties;
using static WireSockUI.Native.WireguardBoosterExports;

namespace WireSockUI
{
    /// <summary>
    ///     Manages the Wireguard tunnel using the Wireguard Booster library.
    /// </summary>
    internal class WireSockManager : IDisposable
    {
        /// <summary>
        ///     LogMessage function delegate
        /// </summary>
        /// <param name="message">
        ///     <see cref="T:LogMessage" />
        /// </param>
        public delegate void LogMessageCallback(LogMessage message);

        /// <summary>
        ///     <see cref="Mode" /> operating mode
        /// </summary>
        public enum Mode
        {
            Undefined,

            /// <summary>
            ///     "Transparent" mode (default)
            /// </summary>
            Transparent,

            /// <summary>
            ///     Virtual network adapter mode
            /// </summary>
            VirtualAdapter
        }

        private readonly LogPrinter _logPrinter;

        private const int MaxQueuedLogMessages = 1000;
        private readonly BlockingCollection<LogMessage> _logQueue;
        private readonly object _logQueueSyncRoot = new object();
        private readonly BackgroundWorker _logWorker;
        private readonly object _syncRoot = new object();

        private volatile IntPtr _handle = IntPtr.Zero;
        private WgbLogLevel _logLevel;
        private GCHandle _logPrinterHandle;
        private long _connectionSequence;
        private volatile bool _disposed;

        /// <summary>
        ///     Initializes a new instance of the <see cref="WireSockManager" />.
        /// </summary>
        /// <param name="logMessageCallback">
        ///     <see cref="T:LogMessageCallback" />
        /// </param>
        public WireSockManager(LogMessageCallback logMessageCallback = null)
        {
            _logQueue = new BlockingCollection<LogMessage>(
                new ConcurrentQueue<LogMessage>(),
                MaxQueuedLogMessages);
            _logWorker = InitializeLogWorker(logMessageCallback);
            _logWorker.RunWorkerAsync();

            TunnelMode = Mode.Transparent;

            // Create a new instance of the LogPrinter delegate
            _logPrinter = PrintLog;

            // Create a GCHandle to keep the delegate alive
            _logPrinterHandle = GCHandle.Alloc(_logPrinter);
        }

        /// <summary>
        ///     WireSock tunnel mode <see cref="Mode.Transparent" /> or <see cref="Mode.VirtualAdapter" />
        /// </summary>
        public Mode TunnelMode
        {
            get
            {
                lock (_syncRoot)
                {
                    return _adapterMode;
                }
            }
            set
            {
                lock (_syncRoot)
                {
                    ThrowIfDisposed();

                    if (value == _adapterMode)
                        return;

                    if (_handle != IntPtr.Zero)
                        throw new InvalidOperationException(
                            "Adapter mode cannot be changed while a tunnel handle is still allocated. Disconnect and retry.");

                    switch (value)
                    {
                        case Mode.VirtualAdapter:
                            _getHandle = wgbp_get_handle;
                            _setLogLevel = wgbp_set_log_level;
                            _createTunnelFromFile = wgbp_create_tunnel_from_file_w;
                            _startTunnel = wgbp_start_tunnel;
                            _stopTunnel = wgbp_stop_tunnel;
                            _dropTunnel = wgbp_drop_tunnel;
                            _tunnelActive = wgbp_get_tunnel_active;
                            _tunnelState = wgbp_get_tunnel_state;
                            _setNetworkLockMode = wgbp_set_network_lock_mode;
                            _getNetworkLockMode = wgbp_get_network_lock_mode;
                            break;
                        default:
                            _getHandle = wgb_get_handle;
                            _setLogLevel = wgb_set_log_level;
                            _createTunnelFromFile = wgb_create_tunnel_from_file_w;
                            _startTunnel = wgb_start_tunnel;
                            _stopTunnel = wgb_stop_tunnel;
                            _dropTunnel = wgb_drop_tunnel;
                            _tunnelActive = wgb_get_tunnel_active;
                            _tunnelState = wgb_get_tunnel_state;
                            _setNetworkLockMode = wgb_set_network_lock_mode;
                            _getNetworkLockMode = wgb_get_network_lock_mode;
                            break;
                    }

                    _adapterMode = value;
                }
            }
        }

        /// <summary>
        ///     Return log level configured in settings as <see cref="WgbLogLevel" />
        /// </summary>
        public WgbLogLevel LogLevelSetting
        {
            get
            {
                switch (Settings.Default.LogLevel)
                {
                    case "Info":
                        return WgbLogLevel.Info;
                    case "Warning":
                        return WgbLogLevel.Warning;
                    case "Debug":
                        return WgbLogLevel.Debug;
                    case "All":
                        return WgbLogLevel.All;
                    default:
                        return WgbLogLevel.Error;
                }
            }
        }

        public WgbLogLevel LogLevel
        {
            get => _logLevel;
            set
            {
                lock (_syncRoot)
                {
                    ThrowIfDisposed();

                    _logLevel = value;

                    // Update loglevel directly if instantiated
                    if (_handle != IntPtr.Zero && _setLogLevel != null)
                        _setLogLevel(_handle, value);
                }
            }
        }

        /// <summary>
        ///     <c>true</c> if a tunnel is currently active, otherwise <c>false</c>
        /// </summary>
        public bool Connected
        {
            get
            {
                lock (_syncRoot)
                {
                    if (_handle != IntPtr.Zero)
                    {
                        try
                        {
                            return _tunnelActive(_handle);
                        }
                        catch (Exception ex)
                        {
                            PrintLog($"Failed to query tunnel state: {ex.Message}");
                        }
                    }

                    return false;
                }
            }
        }

        public bool HasTunnelHandle
        {
            get
            {
                lock (_syncRoot)
                {
                    return _handle != IntPtr.Zero;
                }
            }
        }

        /// <summary>
        ///     Current active profile, if any
        /// </summary>
        public string ProfileName { get; private set; }

        public string LastError { get; private set; }

        public bool KillSwitchEnabled
        {
            get
            {
                return TryGetKillSwitchEnabled(out var enabled, out _) && enabled;
            }
            set
            {
                lock (_syncRoot)
                {
                    ThrowIfDisposed();

                    if (_handle == IntPtr.Zero)
                        throw new InvalidOperationException("Kill Switch mode cannot be changed before a tunnel handle is allocated.");

                    if (!SetNetworkLockMode(value))
                        throw new InvalidOperationException(
                            LastError ?? "Failed to update Kill Switch network lock mode.");
                }
            }
        }

        public long ConnectionSequence
        {
            get
            {
                lock (_syncRoot)
                {
                    return _connectionSequence;
                }
            }
        }

        public bool TryGetKillSwitchEnabled(out bool enabled, out string diagnostic)
        {
            lock (_syncRoot)
            {
                enabled = false;
                diagnostic = null;

                if (_handle == IntPtr.Zero)
                    return true;

                if (_getNetworkLockMode == null)
                {
                    enabled = false;
                    return true;
                }

                try
                {
                    enabled = _getNetworkLockMode(_handle) == WgbNetworkLockMode.Enabled;
                    return true;
                }
                catch (EntryPointNotFoundException)
                {
                    enabled = false;
                    return true;
                }
                catch (Exception ex)
                {
                    diagnostic = ex.Message;
                }

                PrintLog($"Failed to query kill switch network lock mode: {diagnostic}");
                return false;
            }
        }

        /// <summary>
        ///     Disposes the GCHandle for the log printer delegate.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                    return;

                if (_handle != IntPtr.Zero)
                {
                    if (disposing)
                    {
                        if (!Disconnect() && _handle != IntPtr.Zero)
                        {
                            PrintLog("Forcing tunnel handle cleanup during disposal after native drop_tunnel failed.");
                            DropCurrentHandle(true, true);
                        }
                    }
                    else
                    {
                        DropCurrentHandle(false, true);
                    }
                }

                _disposed = true;

                if (disposing)
                {
                    CompleteLogQueue();
                    if (!_logWorker.IsBusy)
                        _logWorker.Dispose();
                }

                if (_logPrinterHandle.IsAllocated)
                    _logPrinterHandle.Free();
            }
        }

        /// <summary>
        ///     Appends the specified message to the log queue to process control on the UI thread.
        /// </summary>
        /// <param name="message">The message to append to the log queue.</param>
        private void PrintLog(string message)
        {
            if (_disposed)
                return;

            try
            {
                var logMessage = new LogMessage { Message = message };

                lock (_logQueueSyncRoot)
                {
                    // CompleteAdding shares this lock, so a failed TryAdd below means the bounded queue is full.
                    if (_disposed || _logQueue.IsAddingCompleted)
                        return;

                    if (_logQueue.TryAdd(logMessage))
                        return;

                    _logQueue.TryTake(out _);
                    _logQueue.TryAdd(logMessage);
                }
            }
            catch (InvalidOperationException)
            {
                // The native logger can race with shutdown; dropping late messages is safer than crashing.
            }
        }

        /// <summary>
        ///     Initialize a <see cref="T:BackgroundWorker" /> which retrieves log messages from the logging queue
        /// </summary>
        /// <param name="logMessageCallback"><see cref="T:LogMessageCallback" /> to call for each log message</param>
        /// <returns>
        ///     <see cref="T:BackgroundWorker" />
        /// </returns>
        private BackgroundWorker InitializeLogWorker(LogMessageCallback logMessageCallback)
        {
            var logQueue = _logQueue;
            var worker = new BackgroundWorker
            {
                WorkerReportsProgress = true
            };

            worker.DoWork += (s, e) =>
            {
                while (!_disposed)
                {
                    try
                    {
                        if (logQueue.TryTake(out var message, 500))
                            worker.ReportProgress(0, message);
                        else if (logQueue.IsCompleted)
                            break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }
                }
            };

            worker.ProgressChanged += (s, e) =>
            {
                if (!_disposed && e.UserState is LogMessage message)
                    logMessageCallback?.Invoke(message);
            };

            worker.RunWorkerCompleted += (s, e) =>
            {
                if (_disposed)
                    worker.Dispose();
            };

            return worker;
        }

        private void CompleteLogQueue()
        {
            try
            {
                lock (_logQueueSyncRoot)
                {
                    if (!_logQueue.IsAddingCompleted)
                        _logQueue.CompleteAdding();
                }
            }
            catch (ObjectDisposedException)
            {
                // Queue shutdown races are harmless; the goal is only to unblock the worker.
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding can race with another shutdown path after IsAddingCompleted is checked.
            }
        }

        ~WireSockManager()
        {
            Dispose(false);
        }

        /// <summary>
        ///     Changes the NetConnectionID of a network adapter identified by its friendly name.
        /// </summary>
        /// <remarks>
        ///     This function uses Windows Management Instrumentation (WMI) to locate a network adapter based on its friendly name.
        ///     Once found, it changes the adapter's NetConnectionID to the specified new name. This is particularly useful for
        ///     managing and identifying network connections programmatically. The function iterates through all matching network
        ///     adapters and updates their NetConnectionID, if it is not null or empty.
        /// </remarks>
        /// <param name="adapterFriendlyName">The friendly name of the network adapter whose NetConnectionID is to be changed.</param>
        /// <param name="newName">The new NetConnectionID to be assigned to the network adapter.</param>
        /// <example>
        ///     <code>
        /// ChangeNetConnectionIdByAdapterName("Ethernet", "NewEthernetConnection");
        /// </code>
        /// </example>
        private static void ChangeNetConnectionIdByAdapterName(string adapterFriendlyName, string newName)
        {
            var query = new SelectQuery("Win32_NetworkAdapter", $"Name = '{EscapeWqlString(adapterFriendlyName)}'");
            using (var searcher = new ManagementObjectSearcher(query))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    {
                        // Check if NetConnectionID is not null or empty
                        if (obj["NetConnectionID"] != null && !string.IsNullOrEmpty(obj["NetConnectionID"].ToString()))
                        {
                            obj["NetConnectionID"] = newName;
                            obj.Put(); // Save changes
                        }
                    }
                }
            }
        }

        private static string EscapeWqlString(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "''");
        }

        /// <summary>
        ///     Create a Wireguard tunnel using the specified configuration file.
        /// </summary>
        /// <param name="profile">Profile identifier</param>
        public bool Connect(string profile)
        {
            var profilePath = Profile.GetProfilePath(profile);

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                LastError = null;

                try
                {
                    if (!Profile.IsRegularProfileFile(profilePath, out var profileDiagnostic))
                        return ShowTunnelError($"Failed to load profile '{profile}'.", profileDiagnostic);

                    if (_handle != IntPtr.Zero && !DropCurrentHandle(true))
                        return ShowTunnelError(
                            "A previous WireSock tunnel handle could not be released. Retry disconnect or restart WireSock UI before connecting again.");

                    if (_handle == IntPtr.Zero)
                        _handle = _getHandle(_logPrinter, _logLevel, false);

                    if (_handle == IntPtr.Zero)
                        return ShowTunnelError(Resources.TunnelErrorManager);

                    if (Settings.Default.EnableKillSwitch && !SetNetworkLockMode(true))
                    {
                        DropCurrentHandle(true, true);
                        return false;
                    }

                    if (!_createTunnelFromFile(_handle, profilePath))
                    {
                        ShowTunnelError(Resources.TunnelErrorCreate);

                        DropFailedConnectHandle();
                        return false;
                    }

                    if (!_startTunnel(_handle))
                    {
                        ShowTunnelError(Resources.TunnelErrorStart);

                        DropFailedConnectHandle();
                        return false;
                    }

                    if (_adapterMode == Mode.VirtualAdapter)
                    {
                        try
                        {
                            ChangeNetConnectionIdByAdapterName("Wiresock Virtual Adapter", profile);
                        }
                        catch (Exception ex)
                        {
                            PrintLog($"Tunnel is active, but WireSock UI could not rename the virtual adapter: {ex.Message}");
                        }
                    }
                }
                catch (DllNotFoundException ex)
                {
                    DropFailedConnectHandle();
                    return ShowTunnelError(Resources.TunnelErrorManager, ex.Message);
                }
                catch (EntryPointNotFoundException ex)
                {
                    DropFailedConnectHandle();
                    return ShowTunnelError(Resources.TunnelErrorManager, ex.Message);
                }
                catch (BadImageFormatException ex)
                {
                    DropFailedConnectHandle();
                    return ShowTunnelError(Resources.AppUnsupportedArchMessage, ex.Message);
                }
                catch (Exception ex)
                {
                    DropFailedConnectHandle();
                    return ShowTunnelError(Resources.TunnelErrorManager, ex.Message);
                }

                // Update connected profile
                ProfileName = profile;
                _connectionSequence++;

                return true;
            }
        }

        public bool DisconnectIfConnectionSequence(long connectionSequence)
        {
            lock (_syncRoot)
            {
                if (_connectionSequence != connectionSequence)
                    return true;

                return Disconnect();
            }
        }

        /// <summary>
        ///     Stops and disconnects from the Wireguard tunnel asynchronously.
        /// </summary>
        public bool Disconnect()
        {
            lock (_syncRoot)
            {
                if (_handle == IntPtr.Zero)
                    return true;

                try
                {
                    if (!_stopTunnel(_handle))
                        PrintLog(
                            $"Failed to stop tunnel cleanly: {GetLastNativeErrorOrDefault("native stop_tunnel returned false.")}");
                }
                catch (EntryPointNotFoundException ex)
                {
                    PrintLog($"Failed to stop tunnel cleanly: stop_tunnel export is unavailable. {ex.Message}");
                }
                catch (Exception ex)
                {
                    PrintLog($"Failed to stop tunnel cleanly: {ex.Message}");
                }

                return DropCurrentHandle(true);
            }
        }

        /// <summary>
        ///     Get current tunnel state, or empty if no connection
        /// </summary>
        /// <returns>
        ///     <see cref="WgbStats" />
        /// </returns>
        public WgbStats GetState()
        {
            lock (_syncRoot)
            {
                if (_handle != IntPtr.Zero)
                {
                    try
                    {
                        return _tunnelState(_handle);
                    }
                    catch (Exception ex)
                    {
                        PrintLog($"Failed to read tunnel statistics: {ex.Message}");
                    }
                }

                return new WgbStats();
            }
        }

        /// <summary>
        ///     WireSock Log message with associated timestamp
        /// </summary>
        public struct LogMessage
        {
            private string _message;

            public DateTime Timestamp;

            public string Message
            {
                get => _message;
                set
                {
                    Timestamp = DateTime.Now;
                    _message = value;
                }
            }
        }

        private bool ShowTunnelError(string message, string details = null)
        {
            var diagnostic = string.IsNullOrWhiteSpace(details) ? GetLastNativeError() : details;

            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                PrintLog($"{message} {diagnostic}");
                message = $"{message}{Environment.NewLine}{Environment.NewLine}{diagnostic}";
            }

            LastError = message;
            return false;
        }

        private static string GetLastNativeError()
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 0)
                return null;

            return $"Native error {error}: {new Win32Exception(error).Message}";
        }

        private static string GetLastNativeErrorOrDefault(string fallback)
        {
            var diagnostic = GetLastNativeError();
            return string.IsNullOrWhiteSpace(diagnostic) ? fallback : diagnostic;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WireSockManager));
        }

        private bool SetNetworkLockMode(bool enabled)
        {
            try
            {
                if (_setNetworkLockMode == null)
                    return ShowTunnelError("Failed to update Kill Switch network lock mode.",
                        "The loaded wgbooster.dll does not expose network lock support.");

                var mode = enabled ? WgbNetworkLockMode.Enabled : WgbNetworkLockMode.Disabled;
                if (_setNetworkLockMode(_handle, mode))
                    return true;

                return ShowTunnelError("Failed to update Kill Switch network lock mode.",
                    GetLastNativeErrorOrDefault("native set_network_lock_mode returned false."));
            }
            catch (EntryPointNotFoundException ex)
            {
                return ShowTunnelError("Failed to update Kill Switch network lock mode.",
                    $"The loaded wgbooster.dll does not expose network lock support. {ex.Message}");
            }
            catch (Exception ex)
            {
                return ShowTunnelError("Failed to update Kill Switch network lock mode.", ex.Message);
            }
        }

        public static bool ResetNetworkLock()
        {
            return TryResetNetworkLock(out _);
        }

        public static bool TryResetNetworkLock(out string diagnostic)
        {
            diagnostic = null;

            try
            {
                if (wg_reset_network_lock())
                    return true;

                diagnostic = GetLastNativeErrorOrDefault("native reset_network_lock returned false.");
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                diagnostic = $"The loaded wgbooster.dll does not expose network lock reset support. {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                diagnostic = ex.Message;
                return false;
            }
        }

        public static bool IsNetworkLockActive()
        {
            return TryIsNetworkLockActive(out var active, out _) && active;
        }

        public static bool TryIsNetworkLockActive(out bool active, out string diagnostic)
        {
            active = false;
            diagnostic = null;

            try
            {
                active = wg_is_network_lock_active();
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                active = false;
                return true;
            }
            catch (Exception ex)
            {
                diagnostic = ex.Message;
                return false;
            }
        }

        private void DropFailedConnectHandle()
        {
            if (_handle == IntPtr.Zero)
                return;

            if (!DropCurrentHandle(true, true))
                PrintLog("Discarding failed tunnel handle after cleanup failure. Restart WireSock UI if the next connection attempt fails.");
        }

        private bool DropCurrentHandle(bool logFailure, bool clearOnFailure = false)
        {
            if (_handle == IntPtr.Zero)
                return true;

            var dropped = false;

            try
            {
                dropped = _dropTunnel(_handle, false);
                if (!dropped && logFailure)
                    PrintLog(
                        $"Failed to release tunnel handle: {GetLastNativeErrorOrDefault("native drop_tunnel returned false.")}");
            }
            catch (EntryPointNotFoundException ex)
            {
                if (logFailure)
                    PrintLog($"Failed to release tunnel handle: drop_tunnel export is unavailable. {ex.Message}");
            }
            catch (Exception ex)
            {
                if (logFailure)
                    PrintLog($"Failed to release tunnel handle: {ex.Message}");
            }
            finally
            {
                if (dropped || clearOnFailure)
                {
                    _handle = IntPtr.Zero;
                    ProfileName = null;
                }
            }

            return dropped;
        }

        #region Wireguard Boost Library

        private delegate IntPtr GetHandle(LogPrinter logPrinter, WgbLogLevel logLevel, bool enableTrafficCapture);

        private delegate void SetLogLevel(IntPtr handle, WgbLogLevel logLevel);

        private delegate bool CreateTunnelFromFile(IntPtr handle, string fileName);

        private delegate bool TunnelAction(IntPtr handle);

        private delegate bool DropTunnelAction(IntPtr handle, bool preserveNetworkLock);

        private delegate WgbStats TunnelState(IntPtr handle);

        private delegate bool SetNetworkLockModeAction(IntPtr handle, WgbNetworkLockMode mode);

        private delegate WgbNetworkLockMode GetNetworkLockModeAction(IntPtr handle);

        private GetHandle _getHandle;
        private SetLogLevel _setLogLevel;
        private CreateTunnelFromFile _createTunnelFromFile;
        private TunnelAction _startTunnel;
        private TunnelAction _stopTunnel;
        private DropTunnelAction _dropTunnel;
        private TunnelAction _tunnelActive;
        private TunnelState _tunnelState;
        private SetNetworkLockModeAction _setNetworkLockMode;
        private GetNetworkLockModeAction _getNetworkLockMode;

        private Mode _adapterMode;

        #endregion
    }
}
