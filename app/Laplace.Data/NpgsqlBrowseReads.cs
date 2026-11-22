using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

/// <summary>
/// Browse page read over <c>public.laplace_holders_with_standings</c>,
/// <c>public.laplace_entities</c>, and <c>public.laplace_labels</c>.
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
        const string sql = """
            WITH direct AS (
                SELECT e.id, e.tier, $2::bytea AS matched_raw, 'exact'::text AS match_kind,
                       c.rating::numeric, c.deviation::numeric,
                       public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS eff_mu,
                       c.matches AS witnesses
                FROM public.laplace_entities(ARRAY[public.blake3_from_bytea($2)]) e
                LEFT JOIN public.consensus c ON c.claim = e.id
                WHERE e.tier IS NOT NULL
            ),
            holders AS (
                SELECT h.entity AS id, h.tier, $1[1] AS matched_raw, 'member'::text AS match_kind,
                       h.rating::numeric, h.deviation::numeric,
                       public.laplace_confidence(h.rating, h.deviation, 2)::numeric AS eff_mu,
                       h.matches AS witnesses
                FROM public.laplace_holders_with_standings(
                    ARRAY(SELECT public.blake3_from_bytea(m) FROM unnest($1::bytea[]) AS m),
                    ARRAY(SELECT public.blake3_from_bytea(m) FROM unnest($1::bytea[]) AS m),
                    false, (-1)::smallint, greatest($5::bigint, 256), 3::smallint) h
                WHERE cardinality($1::bytea[]) > 0
                  AND NOT h.hub
                  AND h.entity <> public.blake3_from_bytea($2)
            ),
            combined AS (
                SELECT * FROM direct
                UNION ALL
                SELECT * FROM holders
            ),
            total AS (SELECT count(*)::bigint AS total_cnt FROM combined)
            SELECT public.blake3_send(c.id), c.tier, c.matched_raw, c.match_kind,
                   c.rating, c.deviation, c.eff_mu, coalesce(c.witnesses, 0)::bigint,
                   (SELECT total_cnt FROM total),
                   ((SELECT total_cnt FROM total) >= $5)::boolean,
                   (SELECT total_cnt FROM total)
            FROM combined c
            ORDER BY (c.match_kind = 'exact') DESC, coalesce(c.eff_mu, -1e9) DESC, c.tier, c.id
            OFFSET $3 LIMIT $4
            """;

        var rows = await NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => (
                Id: r.GetFieldValue<byte[]>(0),
                Row: new NamedEntityRow(
                    Convert.ToHexStringLower(r.GetFieldValue<byte[]>(0)),
                    "",
                    r.GetInt16(1),
                    "",
                    Convert.ToHexStringLower(r.GetFieldValue<byte[]>(2)),
                    r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetDecimal(4),
                    r.IsDBNull(5) ? null : r.GetDecimal(5),
                    r.IsDBNull(6) ? null : r.GetDecimal(6),
                    r.GetInt64(7),
                    r.GetInt64(8),
                    r.GetBoolean(9),
                    r.GetInt64(10))),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = memberIds });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = exactRootId });
                p.AddWithValue(Math.Max(0, offset));
                p.AddWithValue(Math.Max(0, limit));
                p.AddWithValue(Math.Max(1, candidateCapacity));
            }, ct: ct, label: "browse_named_entities", onError: onError).ConfigureAwait(false);
        if (rows.Count == 0) return [];

        var labels = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("display.labels"),
            static r => (IdHex: r.GetString(0), Label: r.GetString(1)),
            p => p.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = rows.Select(r => r.Id).ToArray(),
            timeoutSeconds: 30, ct: ct, label: "browse_labels", onError: onError).ConfigureAwait(false);

        var labelMap = new Dictionary<string, string>(labels.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var l in labels) labelMap.TryAdd(l.IdHex, l.Label);

        return rows.Select(r => r.Row with
        {
            Label = labelMap.TryGetValue(r.Row.IdHex, out var text) && !string.IsNullOrEmpty(text) ? text : r.Row.IdHex,
        }).ToArray();
    }
}
