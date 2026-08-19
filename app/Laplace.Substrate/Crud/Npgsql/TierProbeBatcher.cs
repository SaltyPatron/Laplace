using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Coalesces concurrent compose-side tier probes. A multi-file ingest deliberately owns
/// several independent working sets; memory-heavy sources may close each set after only a
/// handful of records. Without this boundary, every file worker issues its own identical
/// tier round trip. The batcher preserves each caller's positional bitmap while sending one
/// distinct-id probe per tier for the workers that arrive together.
/// </summary>
internal sealed class TierProbeBatcher
{
    private const int CoalesceDelayMilliseconds = 1;
    private const int MaxRequestsPerBatch = 256;

    private readonly Func<IReadOnlyList<Hash128>, short, CancellationToken, Task<byte[]>> _probe;
    private readonly ConcurrentDictionary<short, Lane> _lanes = new();

    internal TierProbeBatcher(
        Func<IReadOnlyList<Hash128>, short, CancellationToken, Task<byte[]>> probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    internal Task<byte[]> ProbeAsync(
        IReadOnlyList<Hash128> ids, short tier, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return Task.FromResult(Array.Empty<byte>());
        if (ct.IsCancellationRequested) return Task.FromCanceled<byte[]>(ct);

        // Snapshot the positional contract. Callers generally pass a List owned by their
        // descent round, but the batch may execute after that caller has resumed elsewhere.
        var snapshot = new Hash128[ids.Count];
        for (int i = 0; i < ids.Count; i++) snapshot[i] = ids[i];

        var request = new Request(snapshot, ct);
        _lanes.GetOrAdd(tier, t => new Lane(t, _probe)).Enqueue(request);
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
        short tier,
        Func<IReadOnlyList<Hash128>, short, CancellationToken, Task<byte[]>> probe)
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
                    // One scheduler tick is often enough for the other file workers to reach
                    // the same tier. The 1 ms ceiling is negligible beside a database probe
                    // and avoids turning an ingest optimization into visible serial latency.
                    await Task.Delay(CoalesceDelayMilliseconds, CancellationToken.None)
                        .ConfigureAwait(false);

                    var requests = new List<Request>(MaxRequestsPerBatch);
                    while (requests.Count < MaxRequestsPerBatch && _pending.TryDequeue(out var request))
                        requests.Add(request);
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
                byte[] combined = await probe(distinct, tier, CancellationToken.None)
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
