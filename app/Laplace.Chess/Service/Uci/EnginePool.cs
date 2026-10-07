namespace Laplace.Chess.Service.Uci;

/// <summary>
/// Rent/return pool of engine processes (or anything that wraps one). Capacity bounds how many exist at once, so a
/// GPU engine runs with capacity 1. A broken engine is discarded on rent or return and replaced lazily; startup runs
/// outside the bookkeeping lock; every engine is disposed with the pool or on process exit.
/// </summary>
public sealed class EnginePool<T> : IDisposable where T : class
{
    private readonly Func<T> _factory;
    private readonly Func<T, bool> _broken;
    private readonly object _gate = new();
    private readonly Stack<T> _idle = new();
    private readonly HashSet<T> _all = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<T> _leased = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public int Capacity { get; }

    public EnginePool(Func<T> factory, Func<T, bool> isBroken, int capacity = 1, T? initial = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _factory = factory;
        _broken = isBroken;
        Capacity = capacity;
        _slots = new SemaphoreSlim(capacity, capacity);
        if (initial is not null)
        {
            _idle.Push(initial);
            _all.Add(initial);
        }
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public T Rent(CancellationToken cancellationToken = default)
        => TryRent(Timeout.InfiniteTimeSpan, cancellationToken)
           ?? throw new InvalidOperationException("unreachable: an infinite wait returned no engine");

    /// <summary>Null when no slot frees within <paramref name="wait"/> (the caller's "busy").</summary>
    public T? TryRent(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (!_slots.Wait(wait, waiting.Token)) return null;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                while (_idle.TryPop(out var engine))
                {
                    if (_broken(engine))
                    {
                        _all.Remove(engine);
                        (engine as IDisposable)?.Dispose();
                        continue;
                    }
                    _leased.Add(engine);
                    return engine;
                }
            }
            // The acquired slot owns its capacity, including a failed or cancelled startup.
            var fresh = _factory();
            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    (fresh as IDisposable)?.Dispose();
                    waiting.Token.ThrowIfCancellationRequested();
                    throw new ObjectDisposedException(GetType().Name);
                }
                _all.Add(fresh);
                _leased.Add(fresh);
            }
            return fresh;
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    public void Return(T engine)
    {
        bool discard;
        lock (_gate)
        {
            if (!_leased.Remove(engine))
                throw new InvalidOperationException("Evaluator was not leased by this pool, or was already returned.");
            discard = _disposed || _broken(engine);
            if (discard) _all.Remove(engine);
            else _idle.Push(engine);
        }
        try { if (discard) (engine as IDisposable)?.Dispose(); }
        finally { _slots.Release(); }
    }

    private void OnProcessExit(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        T[] engines;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            engines = _all.ToArray();
            _all.Clear();
            _idle.Clear();
        }
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _lifetime.Cancel();
        foreach (var engine in engines) (engine as IDisposable)?.Dispose();
        // Outstanding leases may still return after disposal; the semaphore and CTS stay usable for them.
    }
}
