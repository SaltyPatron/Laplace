using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

public static partial class NpgsqlSubstrateReads
{
    public sealed record ResolvedRefRow(byte[] Id, string Label);

    private static (byte[] Id, string? Surface) CanonicalReference(string reference)
    {
        if (reference.Length == 32)
        {
            try { return (Convert.FromHexString(reference), null); }
            catch (FormatException) { }
        }
        return (Hash128.Text(reference).ToBytes(), reference);
    }

    private static void BindCanonicalReference(NpgsqlParameterCollection parameters, string reference)
    {
        var value = CanonicalReference(reference);
        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = value.Id });
        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value.Surface ?? DBNull.Value });
    }

    public static async Task<IReadOnlyList<ResolvedRefRow?>> ResolveRefsWithLabelsAsync(
        NpgsqlDataSource dataSource, IReadOnlyList<string> references,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        if (references.Count == 0) return [];
        var prepared = references.Select(CanonicalReference).ToArray();
        var rows = await NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("conversation.resolve_refs_with_labels"),
            static r => (Ordinal: r.GetInt64(0), Id: r.GetFieldValue<byte[]>(1), Label: r.GetString(2), Present: r.GetBoolean(3)),
            p =>
            {
                p.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = prepared.Select(x => x.Id).ToArray();
                p.Add("surfaces", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = prepared.Select(x => x.Surface).ToArray();
            }, ct: ct, onError: onError).ConfigureAwait(false);
        if (rows.Count != references.Count)
            throw new InvalidOperationException("Native reference resolution lost input positions.");
        var result = new ResolvedRefRow?[references.Count];
        foreach (var row in rows)
        {
            if (row.Ordinal < 1 || row.Ordinal > result.Length)
                throw new InvalidOperationException("Native reference resolution returned an invalid input position.");
            result[(int)row.Ordinal - 1] = row.Present ? new(row.Id, row.Label) : null;
        }
        return result;
    }

    public static async Task<ResolvedRefRow?> ResolveRefWithLabelAsync(
        NpgsqlDataSource dataSource, string reference, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
        => (await ResolveRefsWithLabelsAsync(dataSource, [reference], ct, onError).ConfigureAwait(false))[0];
}
