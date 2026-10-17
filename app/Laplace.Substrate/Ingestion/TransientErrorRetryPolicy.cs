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

    // Content-addressed working sets are deduplicated before COPY. Once apply transactions
    // are allowed to overlap, 23505 has a second lawful meaning: two independently-probed
    // working sets raced to land the same canonical id. Re-running the working set performs
    // the presence probe again and subtracts the row that won the race. Deadlock and
    // serialization failures are the same re-probe/retry class.
    internal static bool IsConcurrencySqlState(string sqlState) =>
        sqlState is "23505" or "40P01" or "40001";

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
    /// Runs <paramref name="operation"/> under this policy: a failure classified by
    /// <see cref="IsTransient"/> is retried after <see cref="DelayBeforeAttempt"/> until
    /// <see cref="MaxAttempts"/> is spent, then the last failure surfaces unchanged.
    /// Cancellation is never retried. For a content-addressed working set this is the
    /// re-probe contract: each attempt re-runs the presence probe, so a row another
    /// applier landed meanwhile is subtracted instead of re-inserted.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct,
        Action<Exception, int, TimeSpan>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Random? rng = null;
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       && attempt + 1 < MaxAttempts
                                       && IsTransient(ex))
            {
                var delay = DelayBeforeAttempt(attempt, rng ??= new Random());
                onRetry?.Invoke(ex, attempt + 1, delay);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc cref="ExecuteAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken, Action{Exception, int, TimeSpan}?)"/>
    public Task ExecuteAsync(
        Func<CancellationToken, Task> operation, CancellationToken ct,
        Action<Exception, int, TimeSpan>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<bool>(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, ct, onRetry);
    }

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
                && (IsConcurrencySqlState(pg.SqlState)
                 || pg.SqlState is { Length: >= 2 } s
                    && (s.StartsWith("08", StringComparison.Ordinal)
                     || s.StartsWith("40", StringComparison.Ordinal)
                     || s.StartsWith("53", StringComparison.Ordinal)
                     || s.StartsWith("57", StringComparison.Ordinal)
                     || s.StartsWith("58", StringComparison.Ordinal))))
                return true;
            if (e is global::Npgsql.PostgresException pgx && IsCancelledAsyncRead(pgx))
                return true;
            if (e is global::Npgsql.NpgsqlException && e is not global::Npgsql.PostgresException)
                return true;
        }
        return false;
    }
}
