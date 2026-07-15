namespace WireSockUI.Forms
{
    internal enum TunnelOperationBlockReason
    {
        None,
        CleanupPending,
        RecoveryRequired,
        OperationPending
    }

    internal sealed class TunnelSessionCoordinator
    {
        private int _cleanupPending;
        private int _connectionTimeoutGeneration = -1;
        private int _generation;
        private int _operationInProgress;
        private int _recoveryRequired;
        private readonly object _syncRoot = new object();

        public int CurrentGeneration
        {
            get
            {
                lock (_syncRoot)
                    return _generation;
            }
        }

        public bool CleanupPending
        {
            get
            {
                lock (_syncRoot)
                    return _cleanupPending != 0;
            }
        }

        public bool RecoveryRequired
        {
            get
            {
                lock (_syncRoot)
                    return _recoveryRequired != 0;
            }
        }

        public int AdvanceGeneration()
        {
            lock (_syncRoot)
            {
                _connectionTimeoutGeneration = -1;
                return ++_generation;
            }
        }

        public bool TryBeginOperation(out TunnelOperationBlockReason blockReason)
        {
            lock (_syncRoot)
            {
                if (_cleanupPending != 0)
                {
                    blockReason = TunnelOperationBlockReason.CleanupPending;
                    return false;
                }

                if (_recoveryRequired != 0)
                {
                    blockReason = TunnelOperationBlockReason.RecoveryRequired;
                    return false;
                }

                if (_operationInProgress != 0)
                {
                    blockReason = TunnelOperationBlockReason.OperationPending;
                    return false;
                }

                _operationInProgress = 1;
                blockReason = TunnelOperationBlockReason.None;
                return true;
            }
        }

        public bool TryBeginRecoveryOperation(out TunnelOperationBlockReason blockReason)
        {
            lock (_syncRoot)
            {
                if (_cleanupPending != 0)
                {
                    blockReason = TunnelOperationBlockReason.CleanupPending;
                    return false;
                }

                if (_operationInProgress != 0)
                {
                    blockReason = TunnelOperationBlockReason.OperationPending;
                    return false;
                }

                _operationInProgress = 1;
                blockReason = TunnelOperationBlockReason.None;
                return true;
            }
        }

        public void EndOperation()
        {
            lock (_syncRoot)
                _operationInProgress = 0;
        }

        public void BeginCleanup()
        {
            lock (_syncRoot)
                _cleanupPending++;
        }

        public bool EndCleanup()
        {
            lock (_syncRoot)
            {
                if (_cleanupPending == 0)
                    return false;

                _cleanupPending--;
                return _cleanupPending == 0;
            }
        }

        public bool RequireRecovery()
        {
            lock (_syncRoot)
            {
                var firstTransition = _recoveryRequired == 0;
                _recoveryRequired = 1;
                return firstTransition;
            }
        }

        public void ClearRecovery()
        {
            lock (_syncRoot)
                _recoveryRequired = 0;
        }

        public bool TryMarkConnectionTimedOut(int generation)
        {
            lock (_syncRoot)
            {
                if (generation != _generation || _connectionTimeoutGeneration == generation)
                    return false;

                _connectionTimeoutGeneration = generation;
                return true;
            }
        }

        public bool IsConnectionTimedOut(int generation)
        {
            lock (_syncRoot)
                return _connectionTimeoutGeneration == generation;
        }
    }
}
