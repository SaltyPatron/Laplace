using System.Text;
using global::Npgsql;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    // Code is produced by the shared forward program: generation.forward_text runs
    // RESOLVE -> ... -> WITNESS natively over the whole substrate. The final array
    // (prior_frontier in the SQL signature) carries ordered, witnessed feedback roots
    // from earlier attempts, which the program takes as its DISCOURSE operand. No
    // source family is selected here; COUPLE and ROUTE find whatever testimony bears
    // on the prompt.
    internal static string ForwardCodeSql => SqlCatalog.Get("generation.forward_code").Text;

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
        await using var command = NpgsqlCatalog.Command(connection, null, "generation.forward_code",
            prompt, steps, maxStride, spread, topK,
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
