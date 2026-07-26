using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WireSockUI.Config;
using WireSockUI.Native;
using WireSockUI.Properties;
using static WireSockUI.Native.WireguardBoosterExports;

namespace WireSockUI
{
    internal interface IVirtualAdapterRenamer
    {
        void Rename(string adapterFriendlyName, string newName, Func<bool> shouldContinue);
    }

    internal sealed class WmiVirtualAdapterRenamer : IVirtualAdapterRenamer
    {
        internal enum CandidateReadiness
        {
            Missing,
            NotReady,
            Ready,
            Ambiguous
        }

        private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

        public void Rename(string adapterFriendlyName, string newName, Func<bool> shouldContinue)
        {
            if (shouldContinue == null) throw new ArgumentNullException(nameof(shouldContinue));

            var stopwatch = Stopwatch.StartNew();
            do
            {
                if (!shouldContinue())
                    return;
                if (TryRenameOnce(adapterFriendlyName, newName, shouldContinue))
                    return;

                var remaining = ReadinessTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    break;
                Thread.Sleep(remaining < RetryDelay ? remaining : RetryDelay);
            } while (stopwatch.Elapsed < ReadinessTimeout);

            throw new TimeoutException(
                $"The virtual adapter '{adapterFriendlyName}' was not ready for renaming within {ReadinessTimeout.TotalSeconds:0} seconds.");
        }

        private static bool TryRenameOnce(
            string adapterFriendlyName,
            string newName,
            Func<bool> shouldContinue)
        {
            var query = new SelectQuery("Win32_NetworkAdapter",
                $"Name = '{EscapeWqlString(adapterFriendlyName)}'");
            using (var searcher = new ManagementObjectSearcher(query))
            {
                searcher.Options.ReturnImmediately = false;
                searcher.Options.Timeout = OperationTimeout;
                using (var results = searcher.Get())
                {
                    ManagementObject candidate = null;
                    var candidateCount = 0;
                    try
                    {
                        foreach (ManagementObject adapter in results)
                        {
                            var retainCandidate = false;
                            try
                            {
                                candidateCount++;
                                if (candidateCount == 1)
                                {
                                    candidate = adapter;
                                    retainCandidate = true;
                                }
                            }
                            finally
                            {
                                if (!retainCandidate)
                                    adapter.Dispose();
                            }
                        }

                        var connectionId = candidate?["NetConnectionID"]?.ToString();
                        switch (ClassifyCandidates(candidateCount, connectionId))
                        {
                            case CandidateReadiness.Missing:
                            case CandidateReadiness.NotReady:
                                return false;
                            case CandidateReadiness.Ambiguous:
                                throw new InvalidOperationException(
                                    $"Found {candidateCount} adapters named '{adapterFriendlyName}'. " +
                                    "The SDK does not expose the created adapter identifier, so WireSock UI did not rename an ambiguous adapter.");
                        }

                        if (!shouldContinue())
                            return true;

                        candidate["NetConnectionID"] = newName;
                        candidate.Put(new PutOptions { Timeout = OperationTimeout });
                        if (!shouldContinue())
                        {
                            // WMI Put is not cancelable once dispatched. Restore the value
                            // observed by this generation; any queued newer generation then
                            // applies its own profile name.
                            candidate["NetConnectionID"] = connectionId;
                            candidate.Put(new PutOptions { Timeout = OperationTimeout });
                            throw new OperationCanceledException(
                                "The tunnel connection changed while the virtual-adapter rename was committing.");
                        }
                        return true;
                    }
                    finally
                    {
                        candidate?.Dispose();
                    }
                }
            }
        }

        internal static CandidateReadiness ClassifyCandidates(int candidateCount, string connectionId)
        {
            if (candidateCount < 0)
                throw new ArgumentOutOfRangeException(nameof(candidateCount));
            if (candidateCount == 0)
                return CandidateReadiness.Missing;
            if (candidateCount > 1)
                return CandidateReadiness.Ambiguous;
            return string.IsNullOrWhiteSpace(connectionId)
                ? CandidateReadiness.NotReady
                : CandidateReadiness.Ready;
        }

        private static string EscapeWqlString(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "''");
        }
    }

    /// <summary>
    ///     Manages the Wireguard tunnel using the Wireguard Booster library.
    /// </summary>
    internal class WireSockManager : IDisposable
    {
        private sealed class AdapterRenameRequest
        {
            internal AdapterRenameRequest(string profile, long connectionSequence)
            {
                Profile = profile;
                ConnectionSequence = connectionSequence;
            }

            internal string Profile { get; }
            internal long ConnectionSequence { get; }
        }

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
        private readonly IVirtualAdapterRenamer _virtualAdapterRenamer;

        private const int MaxQueuedLogMessages = 1000;
        private const int DefaultAdapterRenameTimeoutMilliseconds = 6000;
        internal const int MaximumRetainedLogMessageCharacters = 4096;
        private const string LogMessageTruncationSuffix = " ... [truncated]";
        private const string DroppedHandleDiagnostic =
            "The native tunnel was already dropped, but its handle could not be released. Retry disconnect or restart WireSock UI.";
        private static readonly object NativeOperationSyncRoot = new object();
        private readonly BlockingCollection<LogMessage> _logQueue;
        private readonly object _logQueueSyncRoot = new object();
        private readonly BackgroundWorker _logWorker;
        private readonly object _adapterRenameQueueSyncRoot = new object();
        private readonly object _connectSyncRoot = new object();
        private readonly object _syncRoot = new object();
        private readonly int _adapterRenameTimeoutMilliseconds;

        private AdapterRenameRequest _pendingAdapterRename;
        private bool _adapterRenameOperationHung;
        private bool _adapterRenameWorkerRunning;
        private volatile IntPtr _handle = IntPtr.Zero;
        private WgbLogLevel _logLevel;
        private GCHandle _logPrinterHandle;
        private long _connectionSequence;
        private bool _handleTunnelDropped;
        private volatile bool _disposed;
        private volatile string _lastError;
        private volatile string _profileName;
        private long _droppedLogMessages;

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
            : this(nativeApi, new WmiVirtualAdapterRenamer(), logMessageCallback)
        {
        }

        internal WireSockManager(IWireSockNativeApi nativeApi, IVirtualAdapterRenamer virtualAdapterRenamer,
            LogMessageCallback logMessageCallback,
            int adapterRenameTimeoutMilliseconds = DefaultAdapterRenameTimeoutMilliseconds)
        {
            _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
            _virtualAdapterRenamer =
                virtualAdapterRenamer ?? throw new ArgumentNullException(nameof(virtualAdapterRenamer));
            if (adapterRenameTimeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(adapterRenameTimeoutMilliseconds));
            _adapterRenameTimeoutMilliseconds = adapterRenameTimeoutMilliseconds;
            _logQueue = new BlockingCollection<LogMessage>(
                new ConcurrentQueue<LogMessage>(),
                MaxQueuedLogMessages);
            _logWorker = InitializeLogWorker(logMessageCallback);

            TunnelMode = Mode.Transparent;

            // Create a new instance of the LogPrinter delegate
            _logPrinter = PrintNativeLog;

            try
            {
                // Create a GCHandle to keep the delegate alive
                _logPrinterHandle = GCHandle.Alloc(_logPrinter);
                _logWorker.RunWorkerAsync();
            }
            catch
            {
                if (_logPrinterHandle.IsAllocated)
                    _logPrinterHandle.Free();
                _logWorker.Dispose();
                _logQueue.Dispose();
                throw;
            }
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

                    if (value != Mode.Transparent && value != Mode.VirtualAdapter)
                        throw new ArgumentOutOfRangeException(nameof(value), value,
                            "WireSock tunnel mode must be Transparent or VirtualAdapter.");

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
            get => ParseLogLevelSetting(Settings.Default.LogLevel);
        }

        internal static WgbLogLevel ParseLogLevelSetting(string logLevel)
        {
            switch (logLevel)
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

        public WgbLogLevel LogLevel
        {
            get => _logLevel;
            set
            {
                lock (_syncRoot)
                {
                    ThrowIfDisposed();

                    if (_handleTunnelDropped)
                        throw new InvalidOperationException(DroppedHandleDiagnostic);

                    // Update loglevel directly if instantiated
                    if (_handle != IntPtr.Zero)
                    {
                        lock (NativeOperationSyncRoot)
                        {
                            _nativeApi.SetLogLevel(_adapterMode, _handle, value);
                        }
                    }

                    _logLevel = value;
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

                if (_disposed)
                {
                    diagnostic = "The WireSock manager has been disposed.";
                    return false;
                }

                if (_handle == IntPtr.Zero)
                    return true;

                if (_handleTunnelDropped)
                {
                    diagnostic = DroppedHandleDiagnostic;
                    return false;
                }

                try
                {
                    lock (NativeOperationSyncRoot)
                    {
                        return NativeCall.TryQuery(() => _nativeApi.GetTunnelActive(_adapterMode, _handle),
                            value => !value, out connected, out diagnostic);
                    }
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
            get => _handle != IntPtr.Zero;
        }

        /// <summary>
        ///     Current active profile, if any
        /// </summary>
        public string ProfileName
        {
            get => _profileName;
            private set => _profileName = value;
        }

        public string LastError
        {
            get => _lastError;
            private set => _lastError = value;
        }

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

                    if (_handleTunnelDropped)
                        throw new InvalidOperationException(DroppedHandleDiagnostic);

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

                if (_disposed)
                {
                    diagnostic = "The WireSock manager has been disposed.";
                    return false;
                }

                if (_handle == IntPtr.Zero)
                    return true;

                if (_handleTunnelDropped)
                {
                    diagnostic = DroppedHandleDiagnostic;
                    return false;
                }

                try
                {
                    WgbNetworkLockMode mode;
                    lock (NativeOperationSyncRoot)
                    {
                        if (!NativeCall.TryQuery(() => _nativeApi.GetNetworkLockMode(_adapterMode, _handle),
                                value => value == WgbNetworkLockMode.Disabled, out mode, out diagnostic))
                        {
                            PrintLog($"Failed to query kill switch network lock mode: {diagnostic}");
                            return false;
                        }
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
                lock (_adapterRenameQueueSyncRoot)
                    _pendingAdapterRename = null;

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
        ///     Decodes a native UTF-8 log message without relying on callback parameter marshaling.
        /// </summary>
        private void PrintNativeLog(IntPtr message)
        {
            try
            {
                PrintLog(WireguardBoosterExports.DecodeLogMessage(message));
            }
            catch (Exception ex)
            {
                try
                {
                    PrintLog($"Failed to decode a native wgbooster log message: {ex.Message}");
                }
                catch (Exception)
                {
                    // No managed exception may cross the native callback boundary.
                }
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
                var logMessage = new LogMessage { Message = BoundLogMessage(message) };

                lock (_logQueueSyncRoot)
                {
                    // CompleteAdding shares this lock, so a failed TryAdd below means the bounded queue is full.
                    if (_disposed || _logQueue.IsAddingCompleted)
                        return;

                    if (_logQueue.TryAdd(logMessage))
                        return;

                    // The worker may have freed a slot after the first bounded add failed.
                    if (_logQueue.TryAdd(logMessage))
                        return;

                    if (_logQueue.TryTake(out _))
                        Interlocked.Increment(ref _droppedLogMessages);

                    if (!_logQueue.TryAdd(logMessage))
                        Interlocked.Increment(ref _droppedLogMessages);
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

        internal static string BoundLogMessage(string message)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= MaximumRetainedLogMessageCharacters)
                return message ?? string.Empty;

            var retainedLength = MaximumRetainedLogMessageCharacters - LogMessageTruncationSuffix.Length;
            if (retainedLength > 0 && char.IsHighSurrogate(message[retainedLength - 1]))
                retainedLength--;

            return message.Substring(0, retainedLength) + LogMessageTruncationSuffix;
        }

        /// <summary>
        ///     Initialize a <see cref="T:BackgroundWorker" /> which retrieves log messages from the logging queue.
        ///     The callback runs on this worker; UI consumers must provide their own bounded marshaling.
        /// </summary>
        /// <param name="logMessageCallback"><see cref="T:LogMessageCallback" /> to call for each log message</param>
        /// <returns>
        ///     <see cref="T:BackgroundWorker" />
        /// </returns>
        private BackgroundWorker InitializeLogWorker(LogMessageCallback logMessageCallback)
        {
            var logQueue = _logQueue;
            var worker = new BackgroundWorker();

            worker.DoWork += (s, e) =>
            {
                while (!_disposed)
                {
                    try
                    {
                        if (logQueue.TryTake(out var message, 500))
                        {
                            ReportDroppedLogMessages(logMessageCallback);
                            DispatchLogMessage(logMessageCallback, message);
                        }
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

            worker.RunWorkerCompleted += (s, e) =>
            {
                if (_disposed)
                    worker.Dispose();
            };

            return worker;
        }

        private void ReportDroppedLogMessages(LogMessageCallback logMessageCallback)
        {
            var dropped = Interlocked.Exchange(ref _droppedLogMessages, 0);
            if (dropped <= 0)
                return;

            DispatchLogMessage(logMessageCallback, new LogMessage
            {
                Message = $"wgbooster produced logs faster than WireSock UI could process them; {dropped} message{(dropped == 1 ? string.Empty : "s")} dropped."
            });
        }

        private void DispatchLogMessage(LogMessageCallback logMessageCallback, LogMessage message)
        {
            if (_disposed || logMessageCallback == null)
                return;

            try
            {
                logMessageCallback(message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"WireSock log consumer rejected a message: {ex.Message}");
            }
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
        ///     Create a Wireguard tunnel using the specified configuration file.
        /// </summary>
        /// <param name="profile">Profile identifier</param>
        public bool Connect(string profile)
        {
            var profilePath = Profile.GetProfilePath(profile);
            bool connected;
            Mode connectedMode;
            long connectedSequence;
            lock (_connectSyncRoot)
            {
                lock (_syncRoot)
                {
                    lock (NativeOperationSyncRoot)
                    {
                        connected = ConnectLocked(profile, profilePath);
                        connectedMode = _adapterMode;
                        connectedSequence = connected ? _connectionSequence : 0;
                    }
                }
            }

            if (connected && connectedMode == Mode.VirtualAdapter)
                QueueVirtualAdapterRename(profile, connectedSequence);

            return connected;
        }

        private bool ConnectLocked(string profile, string profilePath)
        {
            ThrowIfDisposed();
            LastError = null;

            try
            {
                if (!Profile.IsLoadableProfileFile(profilePath, out var profileDiagnostic))
                    return ShowTunnelError($"Failed to load profile '{profile}'.", profileDiagnostic);

                if (_handle != IntPtr.Zero &&
                    !DropCurrentHandle(true, preserveNetworkLock: PrivilegedSettingsStore.EnableKillSwitch))
                    return ShowTunnelError(
                        "A previous WireSock tunnel handle could not be released. Retry disconnect or restart WireSock UI before connecting again.");

                if (_handle == IntPtr.Zero)
                {
                    NativeCall.ClearLastError();
                    _handle = _nativeApi.CreateHandle(_adapterMode, _logPrinter, _logLevel, false, false);
                    _handleTunnelDropped = false;
                }

                if (_handle == IntPtr.Zero)
                    return ShowTunnelError(Resources.TunnelErrorManager);

                if (PrivilegedSettingsStore.EnableKillSwitch && !SetNetworkLockMode(true))
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

            ProfileName = profile;
            _connectionSequence++;

            return true;
        }

        private void QueueVirtualAdapterRename(string profile, long connectionSequence)
        {
            var startWorker = false;
            lock (_adapterRenameQueueSyncRoot)
            {
                if (_disposed)
                    return;

                // Connect releases lifecycle locks before enqueueing, so concurrent callers can arrive
                // here out of sequence. Capacity one must retain the highest observed generation.
                if (_pendingAdapterRename != null &&
                    _pendingAdapterRename.ConnectionSequence >= connectionSequence)
                    return;

                _pendingAdapterRename = new AdapterRenameRequest(profile, connectionSequence);
                if (!_adapterRenameWorkerRunning && !_adapterRenameOperationHung)
                {
                    _adapterRenameWorkerRunning = true;
                    startWorker = true;
                }
            }

            if (!startWorker || TryQueueAdapterRenameWorker())
                return;

            PrintLog("Tunnel is active, but WireSock UI could not queue the virtual-adapter rename.");
        }

        private bool TryQueueAdapterRenameWorker()
        {
            if (ThreadPool.QueueUserWorkItem(_ => ProcessAdapterRenameQueue()))
                return true;

            lock (_adapterRenameQueueSyncRoot)
            {
                _adapterRenameWorkerRunning = false;
                _pendingAdapterRename = null;
            }

            return false;
        }

        private void ProcessAdapterRenameQueue()
        {
            while (true)
            {
                AdapterRenameRequest request;
                lock (_adapterRenameQueueSyncRoot)
                {
                    if (_disposed)
                    {
                        _pendingAdapterRename = null;
                        _adapterRenameWorkerRunning = false;
                        return;
                    }

                    request = _pendingAdapterRename;
                    _pendingAdapterRename = null;
                    if (request == null)
                    {
                        _adapterRenameWorkerRunning = false;
                        return;
                    }
                }

                if (!IsCurrentVirtualAdapterConnection(request.Profile, request.ConnectionSequence))
                    continue;

                try
                {
                    if (TryRunVirtualAdapterRenameWithTimeout(request, out var timedOutOperation))
                        continue;

                    SuspendAdapterRenameQueueUntilCompletion(timedOutOperation);
                    PrintLog(
                        $"Tunnel is active, but the virtual-adapter rename exceeded {_adapterRenameTimeoutMilliseconds} ms. " +
                        "Further rename attempts are coalesced until the provider returns.");
                    return;
                }
                catch (OperationCanceledException)
                {
                    // A disconnect or newer connection invalidated this request.
                }
                catch (Exception ex)
                {
                    PrintLog($"Tunnel is active, but WireSock UI could not rename the virtual adapter: {ex.Message}");
                }
            }
        }

        private bool TryRunVirtualAdapterRenameWithTimeout(
            AdapterRenameRequest request,
            out Task timedOutOperation)
        {
            var canceled = 0;
            var operation = Task.Run(() => _virtualAdapterRenamer.Rename(
                "Wiresock Virtual Adapter",
                request.Profile,
                () => Volatile.Read(ref canceled) == 0 &&
                      IsCurrentVirtualAdapterConnection(request.Profile, request.ConnectionSequence)));

            try
            {
                if (operation.Wait(_adapterRenameTimeoutMilliseconds))
                {
                    timedOutOperation = null;
                    return true;
                }
            }
            catch (AggregateException ex)
            {
                throw ex.GetBaseException();
            }

            Interlocked.Exchange(ref canceled, 1);
            timedOutOperation = operation;
            return false;
        }

        private void SuspendAdapterRenameQueueUntilCompletion(Task timedOutOperation)
        {
            lock (_adapterRenameQueueSyncRoot)
            {
                _adapterRenameOperationHung = true;
                _adapterRenameWorkerRunning = false;
            }

            timedOutOperation.ContinueWith(
                CompleteTimedOutAdapterRename,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void CompleteTimedOutAdapterRename(Task operation)
        {
            var lateException = operation.Exception?.GetBaseException();
            var restartWorker = false;
            lock (_adapterRenameQueueSyncRoot)
            {
                _adapterRenameOperationHung = false;
                if (!_disposed && _pendingAdapterRename != null && !_adapterRenameWorkerRunning)
                {
                    _adapterRenameWorkerRunning = true;
                    restartWorker = true;
                }
            }

            if (lateException != null && !(lateException is OperationCanceledException))
                PrintLog($"The timed-out virtual-adapter rename later failed: {lateException.Message}");

            if (restartWorker && !TryQueueAdapterRenameWorker())
                PrintLog("WireSock UI could not resume the queued virtual-adapter rename.");
        }

        internal bool AdapterRenameOperationHungForTests
        {
            get
            {
                lock (_adapterRenameQueueSyncRoot)
                    return _adapterRenameOperationHung;
            }
        }

        private bool IsCurrentVirtualAdapterConnection(string profile, long connectionSequence)
        {
            lock (_syncRoot)
            {
                return !_disposed &&
                       _handle != IntPtr.Zero &&
                       !_handleTunnelDropped &&
                       _adapterMode == Mode.VirtualAdapter &&
                       _connectionSequence == connectionSequence &&
                       string.Equals(_profileName, profile, StringComparison.OrdinalIgnoreCase);
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
                lock (NativeOperationSyncRoot)
                {
                    LastError = null;

                    if (_handle == IntPtr.Zero)
                        return true;

                    if (!_handleTunnelDropped)
                    {
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
                    }

                    return DropCurrentHandle(true, preserveNetworkLock: preserveNetworkLock);
                }
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

                if (_disposed)
                {
                    diagnostic = "The WireSock manager has been disposed.";
                    return false;
                }

                if (_handle == IntPtr.Zero)
                    return true;

                if (_handleTunnelDropped)
                {
                    diagnostic = DroppedHandleDiagnostic;
                    return false;
                }

                try
                {
                    lock (NativeOperationSyncRoot)
                    {
                        return NativeCall.TryQuery(() => _nativeApi.GetTunnelState(_adapterMode, _handle),
                            IsEmptyStats, out state, out diagnostic);
                    }
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
                System.Diagnostics.Trace.TraceWarning($"{message} {diagnostic}");
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
            lock (NativeOperationSyncRoot)
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
        }

        public static bool ResetNetworkLock()
        {
            return TryResetNetworkLock(out _);
        }

        public static bool TryResetNetworkLock(out string diagnostic)
        {
            lock (NativeOperationSyncRoot)
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
        }

        public static bool IsNetworkLockActive()
        {
            return TryIsNetworkLockActive(out var active, out _) && active;
        }

        public static bool TryIsNetworkLockActive(out bool active, out string diagnostic)
        {
            lock (NativeOperationSyncRoot)
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
            lock (NativeOperationSyncRoot)
            {
                if (_handle == IntPtr.Zero)
                    return true;

                var handle = _handle;
                try
                {
                    if (!_handleTunnelDropped)
                    {
                        NativeCall.ClearLastError();
                        if (!_nativeApi.DropTunnel(_adapterMode, handle, preserveNetworkLock))
                        {
                            if (logFailure)
                                RecordHandleReleaseFailure(
                                    GetLastNativeErrorOrDefault("native drop_tunnel returned false."));
                            return false;
                        }

                        _handleTunnelDropped = true;
                    }

                    _nativeApi.ReleaseHandle(_adapterMode, handle);
                    _handle = IntPtr.Zero;
                    _handleTunnelDropped = false;
                    ProfileName = null;
                    return true;
                }
                catch (EntryPointNotFoundException ex)
                {
                    if (logFailure)
                        RecordHandleReleaseFailure(
                            $"{(_handleTunnelDropped ? "release_handle" : "drop_tunnel")} export is unavailable. {ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    if (logFailure)
                        RecordHandleReleaseFailure(ex.Message);
                    return false;
                }
            }
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
