using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Coalesces concurrent array-in presence probes by their physical routing key. A multi-file
/// ingest deliberately owns several independent working sets; memory-heavy sources may close
/// each set after only a handful of records. Without this boundary, every file worker opens a
/// connection for its own tiny root or tier probe. The unkeyed entity-root path uses one shared
/// lane; tier descent uses one lane per tier. In both cases the batcher preserves each caller's
/// positional bitmap while sending one distinct-id array to the native probe for workers that
/// arrive together.
/// </summary>
internal sealed class PresenceProbeBatcher<TKey> where TKey : notnull
{
    private readonly Func<IReadOnlyList<Hash128>, TKey, CancellationToken, Task<byte[]>> _probe;
    private readonly ConcurrentDictionary<TKey, Lane> _lanes = new();
    private readonly int _maxProbeIds;

    internal PresenceProbeBatcher(
        Func<IReadOnlyList<Hash128>, TKey, CancellationToken, Task<byte[]>> probe,
        int? maxProbeIds = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _maxProbeIds = Math.Max(1, maxProbeIds
            ?? IngestSizing.ResolveApplyIo(IngestTopology.Current.ApplyPartitions).ProbeChunkIds);
    }

    internal Task<byte[]> ProbeAsync(
        IReadOnlyList<Hash128> ids, TKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return Task.FromResult(Array.Empty<byte>());
        if (ct.IsCancellationRequested) return Task.FromCanceled<byte[]>(ct);

        // Snapshot the positional contract. Callers generally pass a List owned by their
        // descent round, but the batch may execute after that caller has resumed elsewhere.
        var snapshot = new Hash128[ids.Count];
        for (int i = 0; i < ids.Count; i++) snapshot[i] = ids[i];

        var request = new Request(snapshot, ct);
        _lanes.GetOrAdd(key, value => new Lane(value, _probe, _maxProbeIds)).Enqueue(request);
        return request.Completion.Task;
    }

    private sealed class Request(Hash128[] ids, CancellationToken cancellationToken)
    {
        internal Hash128[] Ids { get; } = ids;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal TaskCompletionSource<byte[]> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lane(
        TKey key,
        Func<IReadOnlyList<Hash128>, TKey, CancellationToken, Task<byte[]>> probe,
        int maxProbeIds)
    {
        private readonly ConcurrentQueue<Request> _pending = new();
        private int _pumping;

        internal void Enqueue(Request request)
        {
            _pending.Enqueue(request);
            StartPump();
        }

        private void StartPump()
        {
            if (Interlocked.CompareExchange(ref _pumping, 1, 0) == 0)
                _ = PumpAsync();
        }

        private async Task PumpAsync()
        {
            try
            {
                while (true)
                {
                    // Yield one scheduler turn so concurrently composing files can join this
                    // tier without imposing a fixed millisecond delay on every probe.
                    await Task.Yield();

                    var requests = new List<Request>();
                    long ids = 0;
                    while (_pending.TryPeek(out var next)
                        && (requests.Count == 0 || ids + next.Ids.Length <= maxProbeIds)
                        && _pending.TryDequeue(out var request))
                    {
                        requests.Add(request);
                        ids += request.Ids.Length;
                    }
                    if (requests.Count == 0) break;

                    await RunBatchAsync(requests).ConfigureAwait(false);
                }
            }
            finally
            {
                Volatile.Write(ref _pumping, 0);
                // Close the enqueue-vs-stop race: a producer may have queued after the empty
                // read but before _pumping returned to zero.
                if (!_pending.IsEmpty) StartPump();
            }
        }

        private async Task RunBatchAsync(List<Request> requests)
        {
            var live = requests.Where(r => !r.CancellationToken.IsCancellationRequested).ToArray();
            foreach (var cancelled in requests.Where(r => r.CancellationToken.IsCancellationRequested))
                cancelled.Completion.TrySetCanceled(cancelled.CancellationToken);
            if (live.Length == 0) return;

            var distinct = new List<Hash128>();
            var slotOf = new Dictionary<Hash128, int>();
            var requestSlots = new int[live.Length][];
            for (int r = 0; r < live.Length; r++)
            {
                var ids = live[r].Ids;
                var slots = new int[ids.Length];
                for (int i = 0; i < ids.Length; i++)
                {
                    if (!slotOf.TryGetValue(ids[i], out int slot))
                    {
                        slot = distinct.Count;
                        slotOf.Add(ids[i], slot);
                        distinct.Add(ids[i]);
                    }
                    slots[i] = slot;
                }
                requestSlots[r] = slots;
            }

            try
            {
                // No individual caller owns the shared query's cancellation. A cancelled
                // request is cancelled on demultiplex; another live worker must still receive
                // the answer it shares with that request.
                byte[] combined = await probe(distinct, key, CancellationToken.None)
                    .ConfigureAwait(false);
                for (int r = 0; r < live.Length; r++)
                {
                    var request = live[r];
                    if (request.CancellationToken.IsCancellationRequested)
                    {
                        request.Completion.TrySetCanceled(request.CancellationToken);
                        continue;
                    }

                    var bitmap = new byte[BitmapBits.ByteLength(request.Ids.Length)];
                    var slots = requestSlots[r];
                    for (int i = 0; i < slots.Length; i++)
                        if (BitmapBits.IsSet(combined, slots[i])) BitmapBits.Set(bitmap, i);
                    request.Completion.TrySetResult(bitmap);
                }
            }
            catch (Exception ex)
            {
                foreach (var request in live)
                {
                    if (request.CancellationToken.IsCancellationRequested)
                        request.Completion.TrySetCanceled(request.CancellationToken);
                    else
                        request.Completion.TrySetException(ex);
                }
            }
        }
    }
}
