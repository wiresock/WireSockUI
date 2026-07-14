using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using WireSockUI.Config;
using WireSockUI.Native;
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
        private readonly IWireSockNativeApi _nativeApi;

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
            : this(new WireSockNativeApi(), logMessageCallback)
        {
        }

        internal WireSockManager(IWireSockNativeApi nativeApi, LogMessageCallback logMessageCallback = null)
        {
            _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
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
                    if (_handle != IntPtr.Zero)
                        _nativeApi.SetLogLevel(_adapterMode, _handle, value);
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
                if (TryGetConnected(out var connected, out var diagnostic))
                    return connected;

                PrintLog($"Failed to query tunnel state: {diagnostic}");
                return false;
            }
        }

        public bool TryGetConnected(out bool connected, out string diagnostic)
        {
            lock (_syncRoot)
            {
                connected = false;
                diagnostic = null;

                if (_handle == IntPtr.Zero)
                    return true;

                try
                {
                    return NativeCall.TryQuery(() => _nativeApi.GetTunnelActive(_adapterMode, _handle), value => !value, out connected,
                        out diagnostic);
                }
                catch (Exception ex)
                {
                    diagnostic = ex.Message;
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

                try
                {
                    if (!NativeCall.TryQuery(() => _nativeApi.GetNetworkLockMode(_adapterMode, _handle),
                            value => value == WgbNetworkLockMode.Disabled, out var mode, out diagnostic))
                    {
                        PrintLog($"Failed to query kill switch network lock mode: {diagnostic}");
                        return false;
                    }

                    if (mode != WgbNetworkLockMode.Disabled && mode != WgbNetworkLockMode.Enabled)
                    {
                        diagnostic = $"The native SDK returned an unsupported network lock mode value: {(int)mode}.";
                        PrintLog($"Failed to query kill switch network lock mode: {diagnostic}");
                        return false;
                    }

                    enabled = mode == WgbNetworkLockMode.Enabled;
                    return true;
                }
                catch (EntryPointNotFoundException ex)
                {
                    diagnostic = $"The loaded wgbooster.dll does not expose network lock state support. {ex.Message}";
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

                if (_handle != IntPtr.Zero && disposing)
                {
                    if (!Disconnect() && _handle != IntPtr.Zero)
                    {
                        PrintLog("Retrying tunnel handle cleanup during disposal after native drop_tunnel failed.");
                        DropCurrentHandle(true);
                    }
                }

                if (disposing && _handle != IntPtr.Zero)
                    PrintLog(
                        "The native tunnel handle could not be released. Its logging callback will remain rooted until process exit.");

                _disposed = true;

                if (disposing)
                {
                    CompleteAndDisposeLogQueue();
                    if (!_logWorker.IsBusy)
                        _logWorker.Dispose();
                }

                if (disposing && _handle == IntPtr.Zero && _logPrinterHandle.IsAllocated)
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
            catch (ObjectDisposedException)
            {
                // The queue can be disposed while native callbacks are still unwinding during shutdown.
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

        private void CompleteAndDisposeLogQueue()
        {
            try
            {
                lock (_logQueueSyncRoot)
                {
                    if (!_logQueue.IsAddingCompleted)
                        _logQueue.CompleteAdding();

                    _logQueue.Dispose();
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

                    if (_handle != IntPtr.Zero && !DropCurrentHandle(true, preserveNetworkLock: Settings.Default.EnableKillSwitch))
                        return ShowTunnelError(
                            "A previous WireSock tunnel handle could not be released. Retry disconnect or restart WireSock UI before connecting again.");

                    if (_handle == IntPtr.Zero)
                    {
                        NativeCall.ClearLastError();
                        _handle = _nativeApi.GetHandle(_adapterMode, _logPrinter, _logLevel, false);
                    }

                    if (_handle == IntPtr.Zero)
                        return ShowTunnelError(Resources.TunnelErrorManager);

                    if (Settings.Default.EnableKillSwitch && !SetNetworkLockMode(true))
                    {
                        DropFailedConnectHandle();
                        return false;
                    }

                    NativeCall.ClearLastError();
                    if (!_nativeApi.CreateTunnelFromFile(_adapterMode, _handle, profilePath))
                    {
                        ShowTunnelError(Resources.TunnelErrorCreate);

                        DropFailedConnectHandle();
                        return false;
                    }

                    NativeCall.ClearLastError();
                    if (!_nativeApi.StartTunnel(_adapterMode, _handle))
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

        public bool DisconnectIfConnectionSequence(long connectionSequence, bool preserveNetworkLock = false)
        {
            lock (_syncRoot)
            {
                if (_connectionSequence != connectionSequence)
                    return true;

                return Disconnect(preserveNetworkLock);
            }
        }

        /// <summary>
        ///     Stops and disconnects from the Wireguard tunnel asynchronously.
        /// </summary>
        public bool Disconnect(bool preserveNetworkLock = false)
        {
            lock (_syncRoot)
            {
                LastError = null;

                if (_handle == IntPtr.Zero)
                    return true;

                try
                {
                    NativeCall.ClearLastError();
                    if (!_nativeApi.StopTunnel(_adapterMode, _handle))
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

                return DropCurrentHandle(true, preserveNetworkLock: preserveNetworkLock);
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
            if (TryGetState(out var state, out var diagnostic))
                return state;

            PrintLog($"Failed to read tunnel statistics: {diagnostic}");
            return new WgbStats();
        }

        public bool TryGetState(out WgbStats state, out string diagnostic)
        {
            lock (_syncRoot)
            {
                state = new WgbStats();
                diagnostic = null;

                if (_handle == IntPtr.Zero)
                    return true;

                try
                {
                    return NativeCall.TryQuery(() => _nativeApi.GetTunnelState(_adapterMode, _handle), IsEmptyStats, out state,
                        out diagnostic);
                }
                catch (Exception ex)
                {
                    diagnostic = ex.Message;
                    return false;
                }
            }
        }

        private static bool IsEmptyStats(WgbStats stats)
        {
            return stats.time_since_last_handshake == 0 &&
                   stats.tx_bytes == 0 &&
                   stats.rx_bytes == 0 &&
                   Math.Abs(stats.estimated_loss) < float.Epsilon &&
                   stats.estimated_rtt == 0;
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
            return NativeCall.GetLastErrorDiagnostic();
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
                var mode = enabled ? WgbNetworkLockMode.Enabled : WgbNetworkLockMode.Disabled;
                NativeCall.ClearLastError();
                if (_nativeApi.SetNetworkLockMode(_adapterMode, _handle, mode))
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
                NativeCall.ClearLastError();
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
                return NativeCall.TryQuery(wg_is_network_lock_active, value => !value, out active,
                    out diagnostic);
            }
            catch (EntryPointNotFoundException ex)
            {
                diagnostic = $"The loaded wgbooster.dll does not expose network lock state support. {ex.Message}";
                return false;
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

            if (!DropCurrentHandle(true))
            {
                const string cleanupError =
                    "The failed tunnel handle could not be released. New connections are blocked until cleanup succeeds or WireSock UI is restarted.";
                PrintLog(cleanupError);
                LastError = string.IsNullOrWhiteSpace(LastError)
                    ? cleanupError
                    : $"{LastError}{Environment.NewLine}{Environment.NewLine}{cleanupError}";
            }
        }

        private bool DropCurrentHandle(bool logFailure, bool preserveNetworkLock = false)
        {
            if (_handle == IntPtr.Zero)
                return true;

            var dropped = false;

            try
            {
                NativeCall.ClearLastError();
                dropped = _nativeApi.DropTunnel(_adapterMode, _handle, preserveNetworkLock);
                if (!dropped && logFailure)
                    RecordHandleReleaseFailure(
                        GetLastNativeErrorOrDefault("native drop_tunnel returned false."));
            }
            catch (EntryPointNotFoundException ex)
            {
                if (logFailure)
                    RecordHandleReleaseFailure($"drop_tunnel export is unavailable. {ex.Message}");
            }
            catch (Exception ex)
            {
                if (logFailure)
                    RecordHandleReleaseFailure(ex.Message);
            }
            finally
            {
                if (dropped)
                {
                    _handle = IntPtr.Zero;
                    ProfileName = null;
                }
            }

            return dropped;
        }

        private void RecordHandleReleaseFailure(string diagnostic)
        {
            var message = $"Failed to release tunnel handle: {diagnostic}";
            PrintLog(message);
            LastError = string.IsNullOrWhiteSpace(LastError)
                ? message
                : $"{LastError}{Environment.NewLine}{Environment.NewLine}{message}";
        }

        private Mode _adapterMode;
    }
}
