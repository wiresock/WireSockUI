using System;
using System.Collections.Generic;

namespace WireSockUI.Forms
{
    internal sealed class UiLogMessageBuffer : IDisposable
    {
        private readonly int _batchSize;
        private readonly int _capacity;
        private readonly Action<IReadOnlyList<WireSockManager.LogMessage>> _consumeBatch;
        private readonly Queue<WireSockManager.LogMessage> _messages =
            new Queue<WireSockManager.LogMessage>();
        private readonly Func<Action, bool> _schedule;
        private readonly object _syncRoot = new object();

        private long _droppedMessages;
        private bool _dispatchPending;
        private bool _disposed;

        internal UiLogMessageBuffer(
            int capacity,
            int batchSize,
            Func<Action, bool> schedule,
            Action<IReadOnlyList<WireSockManager.LogMessage>> consumeBatch)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (batchSize <= 0 || batchSize > capacity) throw new ArgumentOutOfRangeException(nameof(batchSize));
            _capacity = capacity;
            _batchSize = batchSize;
            _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
            _consumeBatch = consumeBatch ?? throw new ArgumentNullException(nameof(consumeBatch));
        }

        internal void Enqueue(WireSockManager.LogMessage message)
        {
            var shouldSchedule = false;
            lock (_syncRoot)
            {
                if (_disposed)
                    return;

                if (_messages.Count == _capacity)
                {
                    _messages.Dequeue();
                    _droppedMessages++;
                }

                _messages.Enqueue(message);
                if (!_dispatchPending)
                {
                    _dispatchPending = true;
                    shouldSchedule = true;
                }
            }

            if (shouldSchedule && !_schedule(DrainBatch))
                CancelPendingDispatch();
        }

        private void DrainBatch()
        {
            List<WireSockManager.LogMessage> batch;
            var scheduleNext = false;
            lock (_syncRoot)
            {
                if (_disposed)
                    return;

                batch = new List<WireSockManager.LogMessage>(_batchSize + 1);
                if (_droppedMessages > 0)
                {
                    batch.Add(CreateDroppedMessage(_droppedMessages));
                    _droppedMessages = 0;
                }

                while (batch.Count < _batchSize && _messages.Count > 0)
                    batch.Add(_messages.Dequeue());

                scheduleNext = _messages.Count > 0;
                _dispatchPending = scheduleNext;
            }

            try
            {
                if (batch.Count > 0)
                    _consumeBatch(batch);
            }
            finally
            {
                if (scheduleNext && !_schedule(DrainBatch))
                    CancelPendingDispatch();
            }
        }

        private void CancelPendingDispatch()
        {
            lock (_syncRoot)
            {
                _dispatchPending = false;
                if (_disposed)
                    _messages.Clear();
            }
        }

        private static WireSockManager.LogMessage CreateDroppedMessage(long count)
        {
            return new WireSockManager.LogMessage
            {
                Message = $"WireSock UI dropped {count} queued log message{(count == 1 ? string.Empty : "s")} while the interface was busy."
            };
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _dispatchPending = false;
                _droppedMessages = 0;
                _messages.Clear();
            }
        }
    }
}
