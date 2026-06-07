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
