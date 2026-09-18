using System.Text;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    // The code adapter does not own a reduced cognition path. The canonical native
    // forward program owns RESOLVE -> ... -> WITNESS over the shared substrate.
    // prior_frontier contains only witnessed state from earlier failed candidates;
    // model checkpoints, code corpora, or any other source family are optional
    // testimony discovered by COUPLE/ROUTE, never prerequisites selected here.
    internal const string ForwardCodeSql = """
        SELECT g.step, g.entity, g.stride_used
        FROM generation.forward_text(
            $1, $2, $3, $4, $5, NULL::bigint, 4, 16, $6::bytea[]) g
        ORDER BY g.step
        """;

    public static async Task<string?> ForwardCodeAsync(
        NpgsqlDataSource dataSource,
        string prompt,
        int steps,
        int maxStride,
        double spread,
        int topK,
        IReadOnlyList<byte[]> feedbackRoots,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(ForwardCodeSql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, prompt);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, steps);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, maxStride);
        command.Parameters.AddWithValue(NpgsqlDbType.Double, spread);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, topK);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            feedbackRoots.Count == 0 ? Array.Empty<byte[]>() : feedbackRoots.ToArray());

        var text = new StringBuilder();
        bool hasRows = false;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            hasRows = true;
            if (!reader.IsDBNull(1)) text.Append(reader.GetString(1));
        }
        return hasRows ? text.ToString() : null;
    }
}
