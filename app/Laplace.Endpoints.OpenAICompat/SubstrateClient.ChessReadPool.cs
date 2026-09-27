using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{
    /// <summary>
    /// The server-enforced read-only serving pool, for chess reads that must not resolve
    /// <see cref="ChessRuntimeService"/> and its writer.
    /// </summary>
    internal NpgsqlDataSource ChessReadOnlyDataSource => _dataSourceReadOnly;
}
