using System;
using System.Threading;
using System.Threading.Tasks;
using WireSockUI.Native;
using WireSockUI.Properties;
using static WireSockUI.Native.WireguardBoosterExports;

namespace WireSockUI.Forms
{
    internal interface INetworkLockApi
    {
        bool TryIsActive(out bool active, out string diagnostic);
        bool TryReset(out string diagnostic);
    }

    internal sealed class NetworkLockApi : INetworkLockApi
    {
        public bool TryIsActive(out bool active, out string diagnostic)
        {
            return WireSockManager.TryIsNetworkLockActive(out active, out diagnostic);
        }

        public bool TryReset(out string diagnostic)
        {
            return WireSockManager.TryResetNetworkLock(out diagnostic);
        }
    }

    internal sealed class NativeOperationResult<T>
    {
        private NativeOperationResult(bool succeeded, bool timedOut, T value, string diagnostic,
            Task<NativeOperationResult<T>> pendingCompletion)
        {
            Succeeded = succeeded;
            TimedOut = timedOut;
            Value = value;
            Diagnostic = diagnostic;
            PendingCompletion = pendingCompletion;
        }

        public bool Succeeded { get; }
        public bool TimedOut { get; }
        public T Value { get; }
        public string Diagnostic { get; }
        public Task<NativeOperationResult<T>> PendingCompletion { get; }

        public static NativeOperationResult<T> Success(T value)
        {
            return new NativeOperationResult<T>(true, false, value, null, null);
        }

        public static NativeOperationResult<T> Failure(string diagnostic, T value = default)
        {
            return new NativeOperationResult<T>(false, false, value, diagnostic, null);
        }

        public static NativeOperationResult<T> Timeout(string diagnostic,
            Task<NativeOperationResult<T>> pendingCompletion)
        {
            return new NativeOperationResult<T>(false, true, default, diagnostic, pendingCompletion);
        }
    }

    internal sealed class TunnelConnectionResult
    {
        public bool Connected { get; set; }
        public long ConnectionSequence { get; set; }
        public bool RecoveryRequired { get; set; }
        public string Diagnostic { get; set; }
    }

    internal sealed class TunnelLifecycleController
    {
        private readonly WireSockManager _manager;
        private readonly INetworkLockApi _networkLockApi;

        internal TunnelLifecycleController(WireSockManager.LogMessageCallback logMessageCallback = null)
            : this(new WireSockManager(logMessageCallback), new NetworkLockApi())
        {
        }

        internal TunnelLifecycleController(WireSockManager manager, INetworkLockApi networkLockApi)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _networkLockApi = networkLockApi ?? throw new ArgumentNullException(nameof(networkLockApi));
            _manager.LogLevel = _manager.LogLevelSetting;
        }

        public bool HasTunnelHandle => _manager.HasTunnelHandle;
        public string ProfileName => _manager.ProfileName;
        public string LastError => _manager.LastError;
        public WgbLogLevel ConfiguredLogLevel => _manager.LogLevelSetting;

        public WireSockManager.Mode TunnelMode
        {
            get => _manager.TunnelMode;
            set => _manager.TunnelMode = value;
        }

        public Task<NativeOperationResult<TunnelConnectionResult>> ConnectAsync(string profile,
            bool releasePreservedNetworkLockOnFailure, int timeoutMilliseconds)
        {
            return RunWithTimeoutAsync(() =>
            {
                var connected = _manager.Connect(profile);
                var connectionResult = new TunnelConnectionResult
                {
                    Connected = connected,
                    ConnectionSequence = connected ? _manager.ConnectionSequence : 0
                };

                if (connected)
                    return NativeOperationResult<TunnelConnectionResult>.Success(connectionResult);

                var diagnostic = _manager.LastError;
                if (releasePreservedNetworkLockOnFailure && !_manager.HasTunnelHandle &&
                    !TryReleasePreservedNetworkLock(out var resetDiagnostic))
                {
                    connectionResult.RecoveryRequired = true;
                    diagnostic = AppendDiagnostic(diagnostic, resetDiagnostic);
                }

                connectionResult.Diagnostic = diagnostic;

                return NativeOperationResult<TunnelConnectionResult>.Failure(diagnostic, connectionResult);
            }, timeoutMilliseconds, "The native tunnel connect operation timed out.");
        }

        public Task<NativeOperationResult<bool>> DisconnectAsync(long? connectionSequence, bool preserveNetworkLock,
            int timeoutMilliseconds)
        {
            return RunWithTimeoutAsync(() =>
            {
                var disconnected = connectionSequence.HasValue
                    ? _manager.DisconnectIfConnectionSequence(connectionSequence.Value, preserveNetworkLock)
                    : _manager.Disconnect(preserveNetworkLock);
                return disconnected
                    ? NativeOperationResult<bool>.Success(true)
                    : NativeOperationResult<bool>.Failure(_manager.LastError, false);
            }, timeoutMilliseconds, "The native tunnel disconnect operation timed out.");
        }

        public Task<NativeOperationResult<bool>> GetConnectedAsync(int timeoutMilliseconds)
        {
            return RunWithTimeoutAsync(() =>
            {
                return _manager.TryGetConnected(out var connected, out var diagnostic)
                    ? NativeOperationResult<bool>.Success(connected)
                    : NativeOperationResult<bool>.Failure(diagnostic, false);
            }, timeoutMilliseconds, "The native tunnel-state query timed out.");
        }

        public Task<NativeOperationResult<WgbStats>> GetStateAsync(int timeoutMilliseconds)
        {
            return RunWithTimeoutAsync(() =>
            {
                return _manager.TryGetState(out var state, out var diagnostic)
                    ? NativeOperationResult<WgbStats>.Success(state)
                    : NativeOperationResult<WgbStats>.Failure(diagnostic);
            }, timeoutMilliseconds, "The native tunnel-statistics query timed out.");
        }

        public Task<NativeOperationResult<bool>> ApplyKillSwitchAsync(bool enableKillSwitch,
            int timeoutMilliseconds)
        {
            return RunWithTimeoutAsync(() =>
            {
                if (_manager.HasTunnelHandle)
                {
                    if (enableKillSwitch)
                    {
                        _manager.KillSwitchEnabled = true;
                        return NativeOperationResult<bool>.Success(true);
                    }

                    if (!_manager.TryGetKillSwitchEnabled(out var killSwitchEnabled, out var diagnostic))
                        return NativeOperationResult<bool>.Failure(diagnostic, false);

                    if (killSwitchEnabled)
                        _manager.KillSwitchEnabled = false;

                    return NativeOperationResult<bool>.Success(true);
                }

                if (!enableKillSwitch && !TryReleasePreservedNetworkLock(out var resetDiagnostic))
                    return NativeOperationResult<bool>.Failure(resetDiagnostic, false);

                return NativeOperationResult<bool>.Success(true);
            }, timeoutMilliseconds, "The native Kill Switch update timed out.");
        }

        public Task<NativeOperationResult<bool>> SetLogLevelAsync(WgbLogLevel logLevel, int timeoutMilliseconds)
        {
            return RunWithTimeoutAsync(() =>
            {
                _manager.LogLevel = logLevel;
                return NativeOperationResult<bool>.Success(true);
            }, timeoutMilliseconds, "The native log-level update timed out.");
        }

        public Task<NativeOperationResult<bool>> QueryNetworkLockAsync(int timeoutMilliseconds)
        {
            return RunWithTimeoutAsync(() =>
            {
                return _networkLockApi.TryIsActive(out var active, out var diagnostic)
                    ? NativeOperationResult<bool>.Success(active)
                    : NativeOperationResult<bool>.Failure(diagnostic, false);
            }, timeoutMilliseconds, "The native network-lock query timed out.");
        }

        public Task<NativeOperationResult<bool>> ResetNetworkLockAsync(int timeoutMilliseconds)
        {
            return RunWithTimeoutAsync(() =>
            {
                return _networkLockApi.TryReset(out var diagnostic)
                    ? NativeOperationResult<bool>.Success(true)
                    : NativeOperationResult<bool>.Failure(diagnostic, false);
            }, timeoutMilliseconds, "The native network-lock reset timed out.");
        }

        public Task<NativeOperationResult<bool>> ShutdownAsync(int timeoutMilliseconds)
        {
            return RunWithTimeoutAsync(() =>
            {
                try
                {
                    _manager.Disconnect();
                }
                finally
                {
                    _manager.Dispose();
                }

                return _manager.HasTunnelHandle
                    ? NativeOperationResult<bool>.Failure(
                        "The native tunnel handle remained allocated after shutdown cleanup returned.", false)
                    : NativeOperationResult<bool>.Success(true);
            }, timeoutMilliseconds, "The native shutdown cleanup timed out.");
        }

        internal bool TryReleasePreservedNetworkLock(out string diagnostic)
        {
            diagnostic = null;
            if (!_networkLockApi.TryIsActive(out var networkLockActive, out var queryDiagnostic))
            {
                diagnostic = queryDiagnostic ?? "Unable to query WireSock network lock state.";
                return false;
            }

            if (!networkLockActive)
                return true;

            if (_networkLockApi.TryReset(out var resetDiagnostic))
                return true;

            diagnostic = resetDiagnostic ?? "Unable to reset WireSock network lock.";
            return false;
        }

        private static async Task<NativeOperationResult<T>> RunWithTimeoutAsync<T>(
            Func<NativeOperationResult<T>> operation, int timeoutMilliseconds, string timeoutDiagnostic)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

            var operationTask = Task.Run(() =>
            {
                try
                {
                    return operation();
                }
                catch (Exception ex)
                {
                    return NativeOperationResult<T>.Failure(ex.Message);
                }
            });

            using (var timeoutCancellation = new CancellationTokenSource())
            {
                var timeoutTask = Task.Delay(timeoutMilliseconds, timeoutCancellation.Token);
                if (await Task.WhenAny(operationTask, timeoutTask).ConfigureAwait(false) == operationTask)
                {
                    timeoutCancellation.Cancel();
                    return await operationTask.ConfigureAwait(false);
                }
            }

            return NativeOperationResult<T>.Timeout(timeoutDiagnostic, operationTask);
        }

        private static string AppendDiagnostic(string diagnostic, string additionalDiagnostic)
        {
            if (string.IsNullOrWhiteSpace(diagnostic))
                return additionalDiagnostic;
            if (string.IsNullOrWhiteSpace(additionalDiagnostic))
                return diagnostic;
            return $"{diagnostic}{Environment.NewLine}{Environment.NewLine}{additionalDiagnostic}";
        }
    }
}
