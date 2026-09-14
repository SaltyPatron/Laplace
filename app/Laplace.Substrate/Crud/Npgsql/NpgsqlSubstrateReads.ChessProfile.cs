using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    /// <summary>
    /// Pooled transport overload for the canonical connection-owned profile query.
    /// The SQL remains owned by <see cref="ChessPlayerProfileEdgesAsync(NpgsqlConnection,byte[],CancellationToken,NpgsqlRead.ErrorTranslator?)"/>.
    /// </summary>
    public static async Task<IReadOnlyList<ChessProfileEdgeRow>> ChessPlayerProfileEdgesAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ChessPlayerProfileEdgesAsync(connection, id, ct, onError).ConfigureAwait(false);
    }
}
