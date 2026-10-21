namespace Laplace.Ops;

/// <summary>Process-lifetime persistent-sink failures. A failed write remains
/// visible until the process is restarted after repair; stderr is a second path.</summary>
public sealed class PersistentLogHealth
{
    private int _initialized;
    private long _failures;
    private long _lastFailureTicks;

    internal void Initialized() => Volatile.Write(ref _initialized, 1);

    internal void Failed()
    {
        Interlocked.Exchange(ref _lastFailureTicks, DateTimeOffset.UtcNow.UtcTicks);
        Interlocked.Increment(ref _failures);
    }

    public PersistentLogSnapshot Snapshot()
    {
        var failures = Interlocked.Read(ref _failures);
        var ticks = Interlocked.Read(ref _lastFailureTicks);
        var initialized = Volatile.Read(ref _initialized) != 0;
        return new(initialized && failures == 0, initialized, failures,
            ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
    }
}

public sealed record PersistentLogSnapshot(bool Ready, bool Initialized,
    long FailureCount, DateTimeOffset? LastFailureUtc);
