using global::Npgsql;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// How a consumer intends to use the substrate. The two policies decide whether a
/// slow command surfaces as a bounded error or runs until completion.
/// </summary>
public enum SubstrateAccess
{
    /// <summary>
    /// Request/response surfaces: HTTP, MCP, UCI and live-game hosts.
    /// Bounds the command timeout. Typed hot commands prepare themselves explicitly.
    /// </summary>
    Serving,

    /// <summary>
    /// Ingest/CLI paths: hours-long COPY and fold statements are legitimate, so the
    /// timeout stays unbounded. Hot probes/folds prepare themselves explicitly.
    /// </summary>
    Ingest,
}

/// <summary>
/// The one place a Laplace <see cref="NpgsqlDataSource"/> is built. Every consumer
/// names its <see cref="SubstrateAccess"/> policy, and the policy's pool, timeout
/// and prepare settings are applied over the installed connection string.
/// </summary>
public static class LaplaceDataSource
{
    /// <summary>
    /// Upper bound for a serving command. It is sized so one complete forward pass
    /// (RESOLVE through WITNESS) can finish as a single serving operation rather than
    /// being cut short to fit a timeout; individual commands may choose tighter budgets.
    /// </summary>
    public const int ServingCommandTimeoutSeconds = 120;

    // Ingest fans out briefly, then spends long CPU-only intervals composing the next
    // unit, so surplus pooled sessions are pruned quickly. Idle-pool retention and an
    // in-flight command budget are separate limits. MinPoolSize=0 means the pool holds
    // no permanent sessions.
    public const int PoolIdleLifetimeSeconds = 30;
    public const int PoolPruningIntervalSeconds = 5;

    /// <summary>
    /// Resolve the connection string for <paramref name="access"/>, applying that
    /// policy's tuning on top of the installed base string.
    /// </summary>
    public static string ConnectionStringFor(SubstrateAccess access, string? baseConnectionString = null)
    {
        var basis = baseConnectionString ?? LaplaceInstall.PostgresConnectionString();
        if (access == SubstrateAccess.Ingest)
        {
            // The hot ingest probes and folds prepare their fixed statements explicitly;
            // no auto-prepare LRU sits between them and PostgreSQL to evict a statement.
            // Planning a probe over the partitioned physicality relation opens every
            // leaf's metadata before runtime pruning, so re-planning is the dominant cost.
            var ing = new NpgsqlConnectionStringBuilder(basis)
            {
                MaxPoolSize = PostgresResourcePlan.Current.IngestConnectionOwners,
                MinPoolSize = 0,
                ConnectionIdleLifetime = PoolIdleLifetimeSeconds,
                ConnectionPruningInterval = PoolPruningIntervalSeconds,
                MaxAutoPrepare = 0,
            };
            return ing.ConnectionString;
        }

        var b = new NpgsqlConnectionStringBuilder(basis);
        b.MaxPoolSize = PostgresResourcePlan.Current.ServingConnectionOwners;
        b.MinPoolSize = 0;
        b.ConnectionIdleLifetime = PoolIdleLifetimeSeconds;
        b.ConnectionPruningInterval = PoolPruningIntervalSeconds;

        // The installed connection string may carry `Command Timeout=0` (unbounded) for
        // ingest. A serving path never inherits it: a slow query surfaces as a bounded
        // error instead of holding the caller. Commands may still set a tighter budget.
        if (b.CommandTimeout <= 0 || b.CommandTimeout > ServingCommandTimeoutSeconds)
            b.CommandTimeout = ServingCommandTimeoutSeconds;

        // Typed serving reads prepare their fixed statements explicitly. Any inherited
        // auto-prepare setting is disabled so an environment connection string cannot
        // reintroduce an LRU of prepared statements.
        b.MaxAutoPrepare = 0;

        return b.ConnectionString;
    }

    /// <summary>Build a datasource under the named <paramref name="access"/> policy.</summary>
    public static NpgsqlDataSource Create(SubstrateAccess access, string? baseConnectionString = null)
        => new NpgsqlDataSourceBuilder(ConnectionStringFor(access, baseConnectionString)).Build();

    /// <summary>
    /// Build a datasource under <paramref name="access"/>, letting the caller reach the
    /// builder for extras it alone needs (type mappings, logging, tracing).
    /// </summary>
    public static NpgsqlDataSource Create(
        SubstrateAccess access,
        Action<NpgsqlDataSourceBuilder> configure,
        string? baseConnectionString = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new NpgsqlDataSourceBuilder(ConnectionStringFor(access, baseConnectionString));
        configure(builder);
        return builder.Build();
    }
}
