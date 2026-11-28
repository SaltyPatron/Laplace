using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct HeldCodeFileRow(
        string FileIdHex,
        string FilePath,
        string UnitIdHex,
        short UnitTier,
        long MatchedKeys,
        string Snippet);

    public static async Task<IReadOnlyDictionary<Hash128, double>> ComputeSalienceAsync(
        NpgsqlDataSource? dataSource,
        IReadOnlyList<Hash128> wordIds,
        CancellationToken ct = default,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        ct.ThrowIfCancellationRequested();
        var result = new Dictionary<Hash128, double>();
        if (dataSource is null || wordIds.Count == 0)
            return result;

        var unique = new List<byte[]>(wordIds.Count);
        var seen = new HashSet<Hash128>();
        foreach (var id in wordIds)
        {
            if (id != default && seen.Add(id))
                unique.Add(id.ToBytes());
        }
        if (unique.Count == 0)
            return result;

        const string sql = """
            SELECT public.blake3_send(s.id),
                   coalesce(s.info_bits, 0.0::double precision),
                   coalesce(s.salience, 0.0::double precision)
            FROM public.laplace_salience(
                ARRAY(SELECT public.blake3_from_bytea(x) FROM unnest($1::bytea[]) AS x)
            ) s
            WHERE s.id IS NOT NULL
            """;

        var rows = await NpgsqlRead.ReadRowsAsync(
            dataSource,
            sql,
            static r => (
                Id: r.GetFieldValue<byte[]>(0),
                InfoBits: r.GetDouble(1),
                Salience: r.GetDouble(2)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = unique.ToArray() }),
            timeoutSeconds: 30,
            ct: ct,
            label: "compute_salience",
            onError: onError).ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (row.Id is { Length: 16 })
            {
                double weight = row.InfoBits > 0.0 ? row.InfoBits : Math.Max(0.0, row.Salience);
                result[Hash128.FromBytes(row.Id)] = weight;
            }
        }
        return result;
    }

    public static async Task<IReadOnlyList<HeldCodeFileRow>> SearchCodeFilesAsync(
        NpgsqlDataSource dataSource,
        byte[][] termIds,
        string? pathFilter,
        int limit,
        int offset,
        CancellationToken ct = default,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        ct.ThrowIfCancellationRequested();
        if (termIds.Length == 0 || limit <= 0)
            return [];

        string? trimmedFilter = string.IsNullOrWhiteSpace(pathFilter) ? null : pathFilter.Trim();
        int safeOffset = Math.Max(0, offset);
        int candidateCap = trimmedFilter is null
            ? Math.Clamp(limit + safeOffset, 1, 2048)
            : Math.Clamp(Math.Max((limit + safeOffset) * 16, 512), 1, 2048);

        const string sql = """
            WITH held AS MATERIALIZED (
                SELECT hf.file,
                       hf.keys,
                       hf.unit,
                       coalesce(hf.unit_tier, 0::smallint) AS unit_tier,
                       bit_count(hf.keys::bit(64)) AS matched_key_count,
                       coalesce((SELECT sum(c) FROM unnest(hf.counts) AS c), 0::bigint) AS total_hits
                FROM public.laplace_held_files(
                    ARRAY(SELECT public.blake3_from_bytea(x) FROM unnest($1::bytea[]) AS x),
                    NULL,
                    4096::bigint,
                    400000::bigint
                ) hf
                WHERE hf.file IS NOT NULL
            ),
            bounded AS MATERIALIZED (
                SELECT h.file,
                       h.keys,
                       h.unit,
                       h.unit_tier,
                       h.matched_key_count,
                       h.total_hits
                FROM held h
                ORDER BY h.matched_key_count DESC, h.total_hits DESC, h.file ASC
                LIMIT $5
            ),
            filtered AS MATERIALIZED (
                SELECT b.file,
                       encode(public.blake3_send(b.file), 'hex') AS file_id_hex,
                       coalesce(public.laplace_label_text(public.blake3_send(b.file), false), encode(public.blake3_send(b.file), 'hex')) AS file_path,
                       b.unit,
                       coalesce(encode(public.blake3_send(b.unit), 'hex'), encode(public.blake3_send(b.file), 'hex')) AS unit_id_hex,
                       b.unit_tier,
                       b.keys,
                       b.matched_key_count,
                       b.total_hits
                FROM bounded b
            ),
            page AS MATERIALIZED (
                SELECT file,
                       file_id_hex,
                       file_path,
                       unit,
                       unit_id_hex,
                       unit_tier,
                       keys,
                       matched_key_count,
                       total_hits
                FROM filtered
                WHERE $2::text IS NULL OR position(lower($2::text) IN lower(file_path)) > 0
                ORDER BY matched_key_count DESC, total_hits DESC, file_path ASC
                LIMIT $3 OFFSET $4
            )
            SELECT file_id_hex,
                   file_path,
                   unit_id_hex,
                   unit_tier,
                   keys,
                   left(coalesce(
                       CASE WHEN unit IS NOT NULL THEN public.laplace_label_text(public.blake3_send(unit), false) END,
                       file_path,
                       ''
                   ), 2048) AS snippet
            FROM page
            ORDER BY matched_key_count DESC, total_hits DESC, file_path ASC
            """;

        return await NpgsqlRead.ReadRowsAsync(
            dataSource,
            sql,
            static r => new HeldCodeFileRow(
                FileIdHex: r.GetString(0),
                FilePath: r.GetString(1),
                UnitIdHex: r.GetString(2),
                UnitTier: r.GetInt16(3),
                MatchedKeys: r.GetInt64(4),
                Snippet: r.IsDBNull(5) ? string.Empty : r.GetString(5)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = termIds });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)trimmedFilter ?? DBNull.Value });
                p.AddWithValue(limit);
                p.AddWithValue(safeOffset);
                p.AddWithValue(candidateCap);
            },
            timeoutSeconds: 30,
            ct: ct,
            label: "search_code_files",
            onError: onError).ConfigureAwait(false);
    }
}
