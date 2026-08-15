namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Bounds concurrent file admission by INPUT BYTES rather than by file count.
///
/// The file pool was bounded by count alone — N workers, a source channel of N*2 handles —
/// and a worker composes its file end to end. That is correct only when files are of
/// comparable size. MEASURED 2026-08-15 on UD: 686 treebanks spanning 4,577 B to
/// 360,217,466 B, a 78,000x spread, against file_workers=10 and a declared
/// working_set_budget_bytes of 4 GiB. Ten workers each claiming a large file put an
/// unbounded multiple of that budget in flight at once; RSS reached 83,136,288 kB and the
/// kernel OOM-killer took the run at file 30 of 686 (rc=137), with rows_new still 0 —
/// nothing had committed, so the memory was composed records waiting on a commit side that
/// had not started.
///
/// The budget is not a new dial. It is the working-set budget the sizing plan already
/// declares (IngestSizing.ResolveWorkingSetBudgetBytes) — admitting more raw input than the
/// declared envelope is over budget by that plan's own definition, whatever the file count.
///
/// FIFO, so a large file cannot starve behind a stream of small ones, and an input larger
/// than the entire budget still runs — alone — rather than deadlocking the pool.
/// </summary>
public sealed class ByteAdmissionGate
{
    private readonly long _budget;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Queue<(long Bytes, TaskCompletionSource Tcs)> _waiters = new();
    private long _inFlight;

    public ByteAdmissionGate(long budgetBytes) => _budget = Math.Max(1, budgetBytes);

    public long InFlightBytes => Interlocked.Read(ref _inFlight);

    public async Task AcquireAsync(long bytes, CancellationToken ct = default)
    {
        if (bytes < 0) bytes = 0;
        TaskCompletionSource tcs;

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Admit when it fits, or when the pool is empty — the second clause is what keeps
            // an over-budget input from deadlocking every worker against a budget it can
            // never satisfy. Queue non-empty means wait regardless, or a stream of small
            // files admits ahead of a large one forever.
            if (_waiters.Count == 0 && (_inFlight + bytes <= _budget || _inFlight == 0))
            {
                _inFlight += bytes;
                return;
            }

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue((bytes, tcs));
        }
        finally
        {
            _mutex.Release();
        }

        using var reg = ct.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs);
        await tcs.Task.ConfigureAwait(false);
    }

    public async Task ReleaseAsync(long bytes)
    {
        if (bytes < 0) bytes = 0;

        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _inFlight = Math.Max(0, _inFlight - bytes);

            // Drain in order. A cancelled waiter releases nothing and must not hold the head.
            while (_waiters.Count > 0)
            {
                var (b, tcs) = _waiters.Peek();
                if (tcs.Task.IsCompleted) { _waiters.Dequeue(); continue; }
                if (_inFlight + b > _budget && _inFlight > 0) break;
                _waiters.Dequeue();
                _inFlight += b;
                tcs.TrySetResult();
            }
        }
        finally
        {
            _mutex.Release();
        }
    }
}
