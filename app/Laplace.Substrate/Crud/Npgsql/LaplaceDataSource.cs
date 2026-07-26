using global::Npgsql;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// How a consumer intends to use the substrate. This is NOT cosmetic — the two
/// policies differ in ways that decide whether a slow query surfaces as an error
/// or hangs a caller forever.
/// </summary>
public enum SubstrateAccess
{
    /// <summary>
    /// Request/response paths: HTTP endpoints, MCP tools, UCI/live-game hosts.
    /// Bounds the command timeout and enables server-side plan reuse.
    /// </summary>
    Serving,

    /// <summary>
    /// Ingest/CLI paths: hours-long COPY and fold statements are legitimate, so the
    /// timeout stays unbounded and auto-prepare stays OFF (the ingest path issues
    /// staging DDL, which invalidates cached plans).
    /// </summary>
    Ingest,
}

/// <summary>
/// The one place a Laplace <see cref="NpgsqlDataSource"/> is built.
///
/// Before this existed there were four wrappers around
/// <c>LaplaceInstall.PostgresConnectionString()</c> — SubstrateClient's (bounded +
/// auto-prepared, with a documented rationale), CliRuntime's (bare), and
/// ChessEngineService's (bare) — which meant the serving policy was applied in
/// exactly one of the three places that needed it. Chess's live-game and UCI hosts
/// are serving paths and were silently inheriting the ingest CLI's
/// <c>Command Timeout=0</c>, the precise failure SubstrateClient's comment warns of.
///
/// The fix is not one connection string. It is one place where the CHOICE is
/// named, so a consumer must say which it is.
/// </summary>
public static class LaplaceDataSource
{
    /// <summary>
    /// Upper bound for a serving command. A substrate read slower than this should
    /// surface as an error envelope, not as a hung client.
    /// </summary>
    public const int ServingCommandTimeoutSeconds = 30;

    /// <summary>Npgsql prepares a statement after this many uses on a physical connection.</summary>
    private const int AutoPrepareMinUsages = 2;

    /// <summary>LRU cap on cached server-side plans per connection.</summary>
    private const int MaxAutoPrepare = 50;

    /// <summary>
    /// Resolve the connection string for <paramref name="access"/>, applying that
    /// policy's tuning on top of the installed base string.
    /// </summary>
    public static string ConnectionStringFor(SubstrateAccess access, string? baseConnectionString = null)
    {
        var basis = baseConnectionString ?? LaplaceInstall.PostgresConnectionString();
        if (access == SubstrateAccess.Ingest) return basis;

        var b = new NpgsqlConnectionStringBuilder(basis);

        // LAPLACE_DB carries `Command Timeout=0` (unbounded) for the ingest CLI. A
        // request/response path must never inherit it: a slow substrate query would
        // hang the caller forever instead of surfacing a 503 envelope. Individual
        // commands may still set a tighter per-command budget.
        if (b.CommandTimeout <= 0 || b.CommandTimeout > ServingCommandTimeoutSeconds)
            b.CommandTimeout = ServingCommandTimeoutSeconds;

        // Server-side plan reuse. Serving queries are pure reads replayed across
        // requests on pooled connections; without auto-prepare Postgres re-parses and
        // re-plans each one on every execution. Safe here because serving issues no
        // dynamic DDL — the staging/DDL churn that would invalidate cached plans lives
        // on the Ingest policy, which deliberately leaves auto-prepare off.
        b.MaxAutoPrepare = MaxAutoPrepare;
        b.AutoPrepareMinUsages = AutoPrepareMinUsages;

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
