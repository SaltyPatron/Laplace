namespace Laplace.Ingestion;

public sealed record TransientErrorRetryPolicy(
    int      MaxAttempts,
    TimeSpan InitialDelay,
    double   BackoffMultiplier,
    double   JitterFraction,
    Func<Exception, bool> IsTransient)
{
    public static TransientErrorRetryPolicy Default { get; } =
        new(MaxAttempts: 3,
            InitialDelay: TimeSpan.FromMilliseconds(100),
            BackoffMultiplier: 10.0,
            JitterFraction: 0.1,
            IsTransient: DefaultIsTransient);

    public static TransientErrorRetryPolicy NoRetry { get; } =
        new(MaxAttempts: 1,
            InitialDelay: TimeSpan.Zero,
            BackoffMultiplier: 1.0,
            JitterFraction: 0.0,
            IsTransient: static _ => false);

    
    
    
    
    
    
    
    
    public static TransientErrorRetryPolicy ConcurrencyRetry { get; } =
        new(MaxAttempts: 10,
            InitialDelay: TimeSpan.FromMilliseconds(15),
            BackoffMultiplier: 1.8,
            JitterFraction: 0.5,
            IsTransient: IsConcurrencyConflict);

    private static bool IsConcurrencyConflict(Exception ex)
    {
        // 40P01 deadlock_detected, 40001 serialization_failure: classic concurrent-commit conflicts.
        // 23505 unique_violation: the cross-worker insert race left after removing the promote's
        // `ON CONFLICT DO NOTHING` (NpgsqlSubstrateWriter.StageAndInsertManyAsync). Two parallel
        // workers can stage the same novel content-addressed id in overlapping transactions; the
        // loser's anti-join didn't see the winner's uncommitted row, so its INSERT raises 23505.
        // Retrying the whole batch is correct and idempotent: on retry the winner's row is committed
        // and visible, so the anti-join skips the entity/physicality and the attestation preflight
        // routes the re-observation to the locked observation_count UPDATE (no dup rows, no lost
        // counts). The set-based anti-join — not this net — is the dedup mechanism.
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is global::Npgsql.PostgresException pg
                && pg.SqlState is "40P01" or "40001" or "23505")
                return true;
        return false;
    }

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
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is TimeoutException) return true;
            if (e is global::Npgsql.PostgresException pg
                && pg.SqlState is { Length: >= 2 } s
                && (s.StartsWith("08", StringComparison.Ordinal)
                 || s.StartsWith("40", StringComparison.Ordinal)
                 || s.StartsWith("53", StringComparison.Ordinal)
                 || s.StartsWith("57", StringComparison.Ordinal)
                 || s.StartsWith("58", StringComparison.Ordinal)))
                return true;
            if (e is global::Npgsql.NpgsqlException && e is not global::Npgsql.PostgresException)
                return true;
        }
        return false;
    }
}
