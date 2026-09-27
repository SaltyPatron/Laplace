using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Host-lifetime Serving and Ingest datasources for admin maintenance. Npgsql pool limits
/// are per datasource, so every maintenance request shares these two pools.
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
