using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Host-lifetime PostgreSQL pools for the admin maintenance surface.
///
/// Maintenance requests used to construct a Serving datasource to resolve a table
/// and an Ingest datasource to execute VACUUM on every request. Npgsql pool limits
/// are per datasource, so that turned each request into two independent connection
/// budgets and bypassed the process ownership contract in GH #933.
/// </summary>
internal sealed class AdminPostgresDataSources : IAsyncDisposable
{
    public NpgsqlDataSource Serving { get; } = LaplaceDataSource.Create(SubstrateAccess.Serving);
    public NpgsqlDataSource Ingest { get; } = LaplaceDataSource.Create(SubstrateAccess.Ingest);

    public async ValueTask DisposeAsync()
    {
        await Serving.DisposeAsync().ConfigureAwait(false);
        await Ingest.DisposeAsync().ConfigureAwait(false);
    }
}
