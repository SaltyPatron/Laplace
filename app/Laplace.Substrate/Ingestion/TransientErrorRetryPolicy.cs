namespace Laplace.Ingestion;

public sealed record TransientErrorRetryPolicy(
    int MaxAttempts,
    TimeSpan InitialDelay,
    double BackoffMultiplier,
    double JitterFraction,
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









        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is global::Npgsql.PostgresException pg
                && IsConcurrencySqlState(pg.SqlState))
                return true;
        return false;
    }

    // A uniqueness violation is an internal novelty/dedup proof failure under
    // the one-ingest + advisory-lock protocol. Replaying the identical working
    // set cannot repair it; only deadlock/serialization conflicts are transient.
    internal static bool IsConcurrencySqlState(string sqlState) =>
        sqlState is "40P01" or "40001";

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

    /// <summary>
    /// A CANCELLED ASYNC READ, which PostgreSQL reports as XX000 rather than an I/O class.
    ///
    /// <para>MEASURED 2026-08-16: ConceptNet aborted at 4,304,844 of 31,085,888 units after
    /// 33 minutes on
    /// <c>XX000: could not read blocks 146..161 in file "base/3061870/3063304": Operation
    /// canceled</c> — relfilenode 3063304 is <c>laplace.entities_t3_h0</c>. The cluster runs
    /// <c>io_method = io_uring</c> with <c>io_max_concurrency = 64</c> and
    /// <c>effective_io_concurrency = 256</c>; a cancelled in-flight AIO surfaces as
    /// ECANCELED, and Postgres wraps that as XX000 (internal_error), which
    /// <see cref="DefaultIsTransient"/> did not cover. So a retryable cancellation ended a
    /// 31M-unit ingest with no second attempt. The partition read clean afterwards
    /// (3,958,231 rows; the failing block range returns 1,843), which is what a cancelled
    /// read looks like and a damaged one does not.</para>
    ///
    /// <para>NARROW ON PURPOSE. XX000 is Postgres' catch-all internal error and covers real
    /// corruption; retrying all of it would turn a damaged relation into a silent retry
    /// loop. Genuine media failure already has a home — class 58 (system_error, including
    /// <c>58030 io_error</c>) is transient above and stays that way. This adds only the
    /// cancellation phrasing, so "could not read blocks … : Input/output error" is still
    /// fatal on the first attempt.</para>
    /// </summary>
    private static bool IsCancelledAsyncRead(global::Npgsql.PostgresException pg) =>
        pg.SqlState == "XX000"
        && pg.MessageText is { } m
        && m.Contains("could not read block", StringComparison.OrdinalIgnoreCase)
        && m.Contains("Operation canceled", StringComparison.OrdinalIgnoreCase);

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
            if (e is global::Npgsql.PostgresException pgx && IsCancelledAsyncRead(pgx))
                return true;
            if (e is global::Npgsql.NpgsqlException && e is not global::Npgsql.PostgresException)
                return true;
        }
        return false;
    }
}
