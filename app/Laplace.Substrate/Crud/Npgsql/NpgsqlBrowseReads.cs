using global::Npgsql;
using Laplace.Engine.Core;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Product-browse reads. SQL here only orchestrates installed substrate operators and
/// batch realization; candidate generation/ranking remains in the extension so web,
/// API, CLI and direct SQL do not grow separate search laws.
/// </summary>
public static class NpgsqlBrowseReads
{
    public readonly record struct NamedEntityRow(
        string IdHex,
        string Label,
        short Tier,
        string Type,
        string MatchedNameIdHex,
        string MatchKind,
        decimal? Rating,
        decimal? Rd,
        decimal? EffMu,
        long Witnesses,
        long CandidateNames,
        bool CandidateTruncated,
        long MatchedEntities);

    public static async Task<IReadOnlyList<NamedEntityRow>> NamedEntitiesAsync(
        NpgsqlConnection conn, byte[][] memberIds, byte[] exactRootId,
        int offset, int limit, int candidateCapacity, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("browse.page"),
            static r => (
                Id: r.GetFieldValue<byte[]>(0), Type: r.GetFieldValue<byte[]>(2),
                Row: new NamedEntityRow(
                    Convert.ToHexString(r.GetFieldValue<byte[]>(0)).ToLowerInvariant(), "",
                    r.GetInt16(1), "",
                    Convert.ToHexString(r.GetFieldValue<byte[]>(3)).ToLowerInvariant(), r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetDecimal(5),
                    r.IsDBNull(6) ? null : r.GetDecimal(6),
                    r.IsDBNull(7) ? null : r.GetDecimal(7),
                    r.GetInt64(8), r.GetInt64(9), r.GetBoolean(10), r.GetInt64(11))),
            p =>
            {
                p.Add("members", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = memberIds;
                p.Add("exact", NpgsqlDbType.Bytea).Value = exactRootId;
                p.AddWithValue("offset", Math.Max(0, offset));
                p.AddWithValue("limit", Math.Max(0, limit));
                p.AddWithValue("capacity", Math.Max(0, candidateCapacity));
            }, ct: ct, label: "browse_named_entities", onError: onError);
        if (rows.Count == 0) return [];

        // Transport the already-selected page to the canonical batch realizers.
        // Selection and ordering remain native; no per-row database calls.
        var labels = await NpgsqlRead.ReadBatchRowsAsync(conn,
            SqlCatalog.Get("display.labels"),
            p => p.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = rows.Select(r => r.Id).ToArray(),
            static r => r.GetString(1),
            SqlCatalog.Get("types.labels"),
            p => p.Add("types", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = rows.Select(r => r.Type).ToArray(),
            static r => r.GetFieldValue<string[]>(0), ct: ct, onError: onError);
        var types = labels.Second.Single();
        if (labels.First.Count != rows.Count || types.Length != rows.Count)
            throw new InvalidOperationException("Browse label batch lost page positions.");
        return rows.Select((r, i) => r.Row with
        {
            Label = labels.First[i],
            Type = string.IsNullOrEmpty(types[i]) ? Convert.ToHexString(r.Type).ToLowerInvariant() : types[i],
        }).ToArray();
    }
}
