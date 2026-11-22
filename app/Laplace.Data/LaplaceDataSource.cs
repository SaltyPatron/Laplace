using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.Data;

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
    /// Maintenance and CLI admin paths: timeout stays unbounded unless set on the command.
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
    public const int ServingCommandTimeoutSeconds = 120;
    public const int IngestPoolIdleLifetimeSeconds = 30;
    public const int ServingPoolIdleLifetimeSeconds = 300;
    public const int PoolPruningIntervalSeconds = 5;

    public static string ConnectionStringFor(SubstrateAccess access, string? baseConnectionString = null)
    {
        var basis = baseConnectionString ?? LaplaceInstall.PostgresConnectionString();
        var b = new NpgsqlConnectionStringBuilder(basis);
        if (!b.ShouldSerialize("Maximum Pool Size"))
            b.MaxPoolSize = access == SubstrateAccess.Ingest
                ? PostgresResourcePlan.Current.IngestConnectionOwners
                : PostgresResourcePlan.Current.ServingConnectionOwners;
        if (!b.ShouldSerialize("Minimum Pool Size")) b.MinPoolSize = 0;
        if (!b.ShouldSerialize("Connection Idle Lifetime"))
            b.ConnectionIdleLifetime = access == SubstrateAccess.Ingest
                ? IngestPoolIdleLifetimeSeconds : ServingPoolIdleLifetimeSeconds;
        if (!b.ShouldSerialize("Connection Pruning Interval")) b.ConnectionPruningInterval = PoolPruningIntervalSeconds;

        if (access == SubstrateAccess.Serving &&
            (b.CommandTimeout <= 0 || b.CommandTimeout > ServingCommandTimeoutSeconds))
            b.CommandTimeout = ServingCommandTimeoutSeconds;

        b.MaxAutoPrepare = 0;
        return b.ConnectionString;
    }

    public static NpgsqlDataSource Create(SubstrateAccess access, string? baseConnectionString = null)
        => Create(access, static _ => { }, baseConnectionString);

    public static NpgsqlDataSource Create(
        SubstrateAccess access,
        Action<NpgsqlDataSourceBuilder> configure,
        string? baseConnectionString = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new NpgsqlDataSourceBuilder(ConnectionStringFor(access, baseConnectionString));
#pragma warning disable NPG9001 // Public custom-converter API in pinned Npgsql 10.0.2.
        builder.AddTypeInfoResolverFactory(new Blake3TypeInfoResolverFactory());
#pragma warning restore NPG9001
        configure(builder);
        return builder.Build();
    }
}
