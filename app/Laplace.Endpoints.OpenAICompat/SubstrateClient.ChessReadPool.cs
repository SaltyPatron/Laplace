using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{
    /// <summary>
    /// Server-enforced read-only datasource for chess inference. Pure board/eval/explore
    /// routes borrow this existing serving pool instead of resolving ChessRuntimeService,
    /// which owns the ingest-capable live writer.
    /// </summary>
    internal NpgsqlDataSource ChessReadOnlyDataSource => _dataSourceReadOnly;
}
