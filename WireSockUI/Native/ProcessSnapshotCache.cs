using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WireSockUI.Native
{
    internal sealed class ProcessSnapshotCache
    {
        private static readonly IReadOnlyList<ProcessEntry> EmptySnapshot =
            Array.AsReadOnly(Array.Empty<ProcessEntry>());
        private readonly SemaphoreSlim _enumerationGate = new SemaphoreSlim(1, 1);
        private readonly Func<CancellationToken, IReadOnlyList<ProcessEntry>> _snapshotFactory;
        private IReadOnlyList<ProcessEntry> _cachedSnapshot;

        internal ProcessSnapshotCache()
            : this(cancellationToken => ProcessList.GetProcessList(cancellationToken).ToArray())
        {
        }

        internal ProcessSnapshotCache(Func<CancellationToken, IReadOnlyList<ProcessEntry>> snapshotFactory)
        {
            _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        }

        internal async Task<IReadOnlyList<ProcessEntry>> GetSnapshotAsync(
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            await _enumerationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!forceRefresh && _cachedSnapshot != null)
                    return _cachedSnapshot;

                var snapshot = await Task.Run(
                        () => _snapshotFactory(cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var entries = snapshot?.ToArray() ?? Array.Empty<ProcessEntry>();
                _cachedSnapshot = entries.Length == 0
                    ? EmptySnapshot
                    : Array.AsReadOnly(entries);
                return _cachedSnapshot;
            }
            finally
            {
                _enumerationGate.Release();
            }
        }
    }
}
