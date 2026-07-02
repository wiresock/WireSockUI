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

        private readonly BlockingCollection<LogMessage> _logQueue;
        private readonly object _syncRoot = new object();

        private volatile IntPtr _handle = IntPtr.Zero;
        private WgbLogLevel _logLevel;
        private GCHandle _logPrinterHandle;
        private bool _disposed;

        /// <summary>
        ///     Initializes a new instance of the <see cref="WireSockManager" />.
        /// </summary>
        /// <param name="logMessageCallback">
        ///     <see cref="T:LogMessageCallback" />
        /// </param>
        public WireSockManager(LogMessageCallback logMessageCallback = null)
        {
            _logQueue = new BlockingCollection<LogMessage>(new ConcurrentQueue<LogMessage>());
            InitializeLogWorker(logMessageCallback).RunWorkerAsync();

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

            CompleteLogQueue();

            if (_logPrinterHandle.IsAllocated)
                _logPrinterHandle.Free();

            _disposed = true;
        }

        /// <summary>
        ///     Appends the specified message to the log queue to process control on the UI thread.
        /// </summary>
        /// <param name="message">The message to append to the log queue.</param>
        private void PrintLog(string message)
        {
            if (_disposed || _logQueue.IsAddingCompleted)
                return;

            try
            {
                _logQueue.Add(new LogMessage { Message = message });
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
                foreach (var message in logQueue.GetConsumingEnumerable())
                    worker.ReportProgress(0, message);
            };

            worker.ProgressChanged += (s, e) =>
            {
                if (e.UserState is LogMessage message)
                    logMessageCallback?.Invoke(message);
            };

            return worker;
        }

        private void CompleteLogQueue()
        {
            try
            {
                if (!_logQueue.IsAddingCompleted)
                    _logQueue.CompleteAdding();
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
            var query = new SelectQuery("Win32_NetworkAdapter", $"Name = '{adapterFriendlyName}'");
            using (var searcher = new ManagementObjectSearcher(query))
            {
                foreach (var o in searcher.Get())
                {
                    var obj = (ManagementObject)o;
                    // Check if NetConnectionID is not null or empty
                    if (obj["NetConnectionID"] != null && !string.IsNullOrEmpty(obj["NetConnectionID"].ToString()))
                    {
                        obj["NetConnectionID"] = newName;
                        obj.Put(); // Save changes
                    }
                }
            }
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
                LastError = null;

                try
                {
                    if (_handle != IntPtr.Zero && !DropCurrentHandle(true))
                        return ShowTunnelError(
                            "A previous WireSock tunnel handle could not be released. Retry disconnect or restart WireSock UI before connecting again.");

                    if (_handle == IntPtr.Zero)
                        _handle = _getHandle(_logPrinter, _logLevel, false);

                    if (_handle == IntPtr.Zero)
                        return ShowTunnelError(Resources.TunnelErrorManager);

                    if (!_createTunnelFromFile(_handle, profilePath))
                    {
                        ShowTunnelError(Resources.TunnelErrorCreate);

                        DropCurrentHandle(true);
                        return false;
                    }

                    if (_adapterMode == Mode.VirtualAdapter)
                        ChangeNetConnectionIdByAdapterName("Wiresock Virtual Adapter", profile);

                    if (!_startTunnel(_handle))
                    {
                        ShowTunnelError(Resources.TunnelErrorStart);

                        DropCurrentHandle(true);
                        return false;
                    }
                }
                catch (DllNotFoundException ex)
                {
                    DropCurrentHandle(true);
                    return ShowTunnelError(Resources.TunnelErrorManager, ex.Message);
                }
                catch (EntryPointNotFoundException ex)
                {
                    DropCurrentHandle(true);
                    return ShowTunnelError(Resources.TunnelErrorManager, ex.Message);
                }
                catch (BadImageFormatException ex)
                {
                    DropCurrentHandle(true);
                    return ShowTunnelError(Resources.AppUnsupportedArchMessage, ex.Message);
                }
                catch (Exception ex)
                {
                    DropCurrentHandle(true);
                    return ShowTunnelError(Resources.TunnelErrorManager, ex.Message);
                }

                // Update connected profile
                ProfileName = profile;

                return true;
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

        private GetHandle _getHandle;
        private SetLogLevel _setLogLevel;
        private CreateTunnelFromFile _createTunnelFromFile;
        private TunnelAction _startTunnel;
        private TunnelAction _stopTunnel;
        private DropTunnelAction _dropTunnel;
        private TunnelAction _tunnelActive;
        private TunnelState _tunnelState;

        private Mode _adapterMode;

        #endregion
    }
}
