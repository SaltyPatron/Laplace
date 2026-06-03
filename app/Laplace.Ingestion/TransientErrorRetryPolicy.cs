namespace Laplace.Ingestion;

/// <summary>
/// Transient-error retry policy used by <see cref="IngestRunner"/> when
/// <see cref="ISubstrateWriter.ApplyAsync"/> throws a recoverable error
/// (transient Npgsql failure, connection drop, etc.).
///
/// Exponential backoff with optional jitter — caller can supply a custom
/// classifier for "is this exception transient" (default treats any
/// derived-from <see cref="Exception"/> matching a small known set as
/// transient).
/// </summary>
public sealed record TransientErrorRetryPolicy(
    int      MaxAttempts,
    TimeSpan InitialDelay,
    double   BackoffMultiplier,
    double   JitterFraction,
    Func<Exception, bool> IsTransient)
{
    /// <summary>Default policy: 3 retries (100ms, 1s, 10s with 10% jitter).
    /// Treats <see cref="TimeoutException"/> and any
    /// <see cref="global::Npgsql.PostgresException"/> with a transient
    /// SQLSTATE class (08, 53, 57, 58) as transient.</summary>
    public static TransientErrorRetryPolicy Default { get; } =
        new(MaxAttempts: 3,
            InitialDelay: TimeSpan.FromMilliseconds(100),
            BackoffMultiplier: 10.0,
            JitterFraction: 0.1,
            IsTransient: DefaultIsTransient);

    /// <summary>Policy that disables retry — surfaces every error immediately.
    /// Useful for tests + dry-runs.</summary>
    public static TransientErrorRetryPolicy NoRetry { get; } =
        new(MaxAttempts: 1,
            InitialDelay: TimeSpan.Zero,
            BackoffMultiplier: 1.0,
            JitterFraction: 0.0,
            IsTransient: static _ => false);

    /// <summary>Compute the wait time before attempt <paramref name="attemptIndex"/>
    /// (0 = before the first retry, i.e. after the initial failure).</summary>
    public TimeSpan DelayBeforeAttempt(int attemptIndex, Random rng)
    {
        if (attemptIndex < 0) return TimeSpan.Zero;
        double baseMs = InitialDelay.TotalMilliseconds *
                        Math.Pow(BackoffMultiplier, attemptIndex);
        if (JitterFraction > 0)
        {
            double jitter = 1.0 + (rng.NextDouble() * 2 - 1) * JitterFraction;
            baseMs *= Math.Max(0.0, jitter);
        }
        return TimeSpan.FromMilliseconds(baseMs);
    }

    private static bool DefaultIsTransient(Exception ex)
    {
        // Walk the inner-exception chain. A transient DB fault can arrive WRAPPED:
        // e.g. the cluster is restarted mid-transaction, the backend connection is
        // killed (57P01), and the best-effort rollback on the now-dead connection
        // throws ObjectDisposedException carrying the real PostgresException as
        // InnerException. Classifying only the outermost exception (the old behaviour)
        // missed it → the purpose-built class-57 retry never fired and the run aborted.
        // Treat the error as transient if ANY link in the chain is transient.
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is TimeoutException) return true;
            // PostgresException with a transient SQLSTATE class (first 2 chars):
            //   08 connection_exception · 53 insufficient_resources
            //   57 operator_intervention (e.g. admin_shutdown / cluster restart)
            //   58 system_error · 40 transaction_rollback (40001 serialization,
            //   40P01 deadlock — expected under concurrent ingestion of shared content).
            if (e is global::Npgsql.PostgresException pg
                && pg.SqlState is { Length: >= 2 } s
                && (s.StartsWith("08", StringComparison.Ordinal)
                 || s.StartsWith("40", StringComparison.Ordinal)
                 || s.StartsWith("53", StringComparison.Ordinal)
                 || s.StartsWith("57", StringComparison.Ordinal)
                 || s.StartsWith("58", StringComparison.Ordinal)))
                return true;
            // Connection-level Npgsql faults (not Postgres SQLSTATE errors) are transient.
            if (e is global::Npgsql.NpgsqlException && e is not global::Npgsql.PostgresException)
                return true;
        }
        return false;
    }
}
