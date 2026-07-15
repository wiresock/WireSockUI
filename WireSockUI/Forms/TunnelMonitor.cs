using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using static WireSockUI.Native.WireguardBoosterExports;

namespace WireSockUI.Forms
{
    internal enum TunnelMonitorUpdateKind
    {
        Connected,
        ConnectionTimedOut,
        TunnelInactive,
        Statistics,
        QueryFailed
    }

    internal sealed class TunnelMonitorUpdate
    {
        private TunnelMonitorUpdate(int generation, TunnelMonitorUpdateKind kind)
        {
            Generation = generation;
            Kind = kind;
        }

        public int Generation { get; }
        public TunnelMonitorUpdateKind Kind { get; }
        public NativeOperationResult<bool> ConnectionQuery { get; private set; }
        public NativeOperationResult<WgbStats> StatisticsQuery { get; private set; }
        public WgbStats Statistics { get; private set; }

        public static TunnelMonitorUpdate Connected(int generation)
        {
            return new TunnelMonitorUpdate(generation, TunnelMonitorUpdateKind.Connected);
        }

        public static TunnelMonitorUpdate ConnectionTimedOut(int generation)
        {
            return new TunnelMonitorUpdate(generation, TunnelMonitorUpdateKind.ConnectionTimedOut);
        }

        public static TunnelMonitorUpdate TunnelInactive(int generation)
        {
            return new TunnelMonitorUpdate(generation, TunnelMonitorUpdateKind.TunnelInactive);
        }

        public static TunnelMonitorUpdate StatisticsChanged(int generation, WgbStats statistics)
        {
            return new TunnelMonitorUpdate(generation, TunnelMonitorUpdateKind.Statistics)
            {
                Statistics = statistics
            };
        }

        public static TunnelMonitorUpdate ConnectionQueryFailed(int generation,
            NativeOperationResult<bool> result)
        {
            return new TunnelMonitorUpdate(generation, TunnelMonitorUpdateKind.QueryFailed)
            {
                ConnectionQuery = result
            };
        }

        public static TunnelMonitorUpdate StatisticsQueryFailed(int generation,
            NativeOperationResult<WgbStats> result)
        {
            return new TunnelMonitorUpdate(generation, TunnelMonitorUpdateKind.QueryFailed)
            {
                StatisticsQuery = result
            };
        }
    }

    internal sealed class TunnelMonitor : IDisposable
    {
        private readonly int _connectedPollIntervalMilliseconds;
        private readonly int _connectionPollIntervalMilliseconds;
        private readonly int _connectionTimeoutMilliseconds;
        private readonly Func<int> _currentGeneration;
        private readonly Func<int, Task<NativeOperationResult<bool>>> _getConnectedAsync;
        private readonly Func<int, Task<NativeOperationResult<WgbStats>>> _getStateAsync;
        private readonly object _syncRoot = new object();
        private readonly int _queryTimeoutMilliseconds;
        private readonly Func<TunnelMonitorUpdate, Task> _updateHandler;
        private CancellationTokenSource _cancellation;
        private bool _disposed;
        private Task _monitorTask = Task.CompletedTask;

        public TunnelMonitor(
            Func<int, Task<NativeOperationResult<bool>>> getConnectedAsync,
            Func<int, Task<NativeOperationResult<WgbStats>>> getStateAsync,
            Func<int> currentGeneration,
            Func<TunnelMonitorUpdate, Task> updateHandler,
            int queryTimeoutMilliseconds,
            int connectionTimeoutMilliseconds,
            int connectionPollIntervalMilliseconds = 500,
            int connectedPollIntervalMilliseconds = 1000)
        {
            _getConnectedAsync = getConnectedAsync ?? throw new ArgumentNullException(nameof(getConnectedAsync));
            _getStateAsync = getStateAsync ?? throw new ArgumentNullException(nameof(getStateAsync));
            _currentGeneration = currentGeneration ?? throw new ArgumentNullException(nameof(currentGeneration));
            _updateHandler = updateHandler ?? throw new ArgumentNullException(nameof(updateHandler));

            if (queryTimeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(queryTimeoutMilliseconds));
            if (connectionTimeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(connectionTimeoutMilliseconds));
            if (connectionPollIntervalMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(connectionPollIntervalMilliseconds));
            if (connectedPollIntervalMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(connectedPollIntervalMilliseconds));

            _queryTimeoutMilliseconds = queryTimeoutMilliseconds;
            _connectionTimeoutMilliseconds = connectionTimeoutMilliseconds;
            _connectionPollIntervalMilliseconds = connectionPollIntervalMilliseconds;
            _connectedPollIntervalMilliseconds = connectedPollIntervalMilliseconds;
        }

        public void StartConnecting(int generation)
        {
            Start(cancellationToken => MonitorConnectingAsync(generation, cancellationToken));
        }

        public void StartConnected(int generation)
        {
            Start(cancellationToken => MonitorConnectedAsync(generation, cancellationToken));
        }

        public void Cancel()
        {
            CancellationTokenSource cancellation;
            Task task;

            lock (_syncRoot)
            {
                cancellation = _cancellation;
                task = _monitorTask;
                _cancellation = null;
                _monitorTask = Task.CompletedTask;
            }

            CancelAndDisposeWhenComplete(cancellation, task);
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            Cancel();
        }

        private void Start(Func<CancellationToken, Task> monitor)
        {
            CancellationTokenSource previousCancellation;
            Task previousTask;

            lock (_syncRoot)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(TunnelMonitor));

                previousCancellation = _cancellation;
                previousTask = _monitorTask;
                _cancellation = new CancellationTokenSource();
                _monitorTask = monitor(_cancellation.Token);
            }

            CancelAndDisposeWhenComplete(previousCancellation, previousTask);
        }

        private async Task MonitorConnectingAsync(int generation, CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();

            try
            {
                while (generation == _currentGeneration())
                {
                    await Task.Delay(_connectionPollIntervalMilliseconds, cancellationToken);
                    if (generation != _currentGeneration())
                        return;

                    var result = await _getConnectedAsync(_queryTimeoutMilliseconds);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != _currentGeneration())
                        return;

                    if (!result.Succeeded)
                    {
                        await _updateHandler(TunnelMonitorUpdate.ConnectionQueryFailed(generation, result));
                        return;
                    }

                    if (result.Value)
                    {
                        await _updateHandler(TunnelMonitorUpdate.Connected(generation));
                        return;
                    }

                    if (elapsed.ElapsedMilliseconds >= _connectionTimeoutMilliseconds)
                    {
                        await _updateHandler(TunnelMonitorUpdate.ConnectionTimedOut(generation));
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await PublishUnexpectedFailureAsync(generation, "Tunnel connection monitor", ex, cancellationToken);
            }
        }

        private async Task MonitorConnectedAsync(int generation, CancellationToken cancellationToken)
        {
            try
            {
                while (generation == _currentGeneration())
                {
                    await Task.Delay(_connectedPollIntervalMilliseconds, cancellationToken);
                    if (generation != _currentGeneration())
                        return;

                    var connectedResult = await _getConnectedAsync(_queryTimeoutMilliseconds);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != _currentGeneration())
                        return;

                    if (!connectedResult.Succeeded)
                    {
                        await _updateHandler(
                            TunnelMonitorUpdate.ConnectionQueryFailed(generation, connectedResult));
                        return;
                    }

                    if (!connectedResult.Value)
                    {
                        await _updateHandler(TunnelMonitorUpdate.TunnelInactive(generation));
                        return;
                    }

                    var stateResult = await _getStateAsync(_queryTimeoutMilliseconds);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != _currentGeneration())
                        return;

                    if (!stateResult.Succeeded)
                    {
                        await _updateHandler(TunnelMonitorUpdate.StatisticsQueryFailed(generation, stateResult));
                        return;
                    }

                    await _updateHandler(TunnelMonitorUpdate.StatisticsChanged(generation, stateResult.Value));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await PublishUnexpectedFailureAsync(generation, "Tunnel state monitor", ex, cancellationToken);
            }
        }

        private async Task PublishUnexpectedFailureAsync(int generation, string context, Exception exception,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || generation != _currentGeneration())
                return;

            await _updateHandler(TunnelMonitorUpdate.ConnectionQueryFailed(
                generation,
                NativeOperationResult<bool>.Failure($"{context} stopped unexpectedly: {exception.Message}")));
        }

        private static void CancelAndDisposeWhenComplete(CancellationTokenSource cancellation, Task task)
        {
            if (cancellation == null)
                return;

            cancellation.Cancel();
            (task ?? Task.CompletedTask).ContinueWith(
                completedTask =>
                {
                    var ignored = completedTask.Exception;
                    cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
