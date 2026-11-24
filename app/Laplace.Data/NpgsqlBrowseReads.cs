using System.Diagnostics;
using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

/// <summary>
/// Indexed entity, case-variant, holder, and consensus leaderboard queries over
/// <c>public.consensus</c>, <c>public.laplace_holders_with_standings</c>,
/// <c>public.laplace_entities</c>, <c>public.laplace_constituents</c>, and <c>public.laplace_labels</c>.
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

    public readonly record struct LeaderboardEntryRow(
        string IdHex,
        string Label,
        short Tier,
        double Confidence,
        double Rating,
        double Deviation,
        long Matches,
        string PartsJson);

    public readonly record struct LeaderboardPage(
        IReadOnlyList<LeaderboardEntryRow> Rows,
        int Offset,
        int Limit,
        string Sort,
        short? Tier,
        long ElapsedUs);

    public static Task<IReadOnlyList<NamedEntityRow>> NamedEntitiesAsync(
        NpgsqlConnection conn, byte[][] memberIds, byte[] exactRootId,
        int offset, int limit, int candidateCapacity, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NamedEntitiesAsync(conn, memberIds, exactRootId, [], discover: true, offset, limit, candidateCapacity, ct, onError);

    public static async Task<IReadOnlyList<NamedEntityRow>> NamedEntitiesAsync(
        NpgsqlConnection conn, byte[][] memberIds, byte[] exactRootId, byte[][] variantRootIds,
        bool discover, int offset, int limit, int candidateCapacity, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH exact_id AS (
                SELECT public.blake3_from_bytea($2) AS id
            ),
            variant_ids AS MATERIALIZED (
                SELECT DISTINCT v.raw_id, public.blake3_from_bytea(v.raw_id) AS id
                FROM unnest($3::bytea[]) AS v(raw_id), exact_id ei
                WHERE $7::boolean
                  AND v.raw_id IS NOT NULL
                  AND public.blake3_from_bytea(v.raw_id) <> ei.id
            ),
            direct AS (
                SELECT e.id, e.tier, $2::bytea AS matched_raw, 'exact'::text AS match_kind,
                       c.rating::numeric, c.deviation::numeric,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches AS witnesses,
                       1e6::double precision AS sal_score
                FROM public.laplace_entities(ARRAY[(SELECT id FROM exact_id)]) e
                LEFT JOIN public.consensus c ON c.claim = e.id
                WHERE e.tier IS NOT NULL
            ),
            variants AS (
                SELECT e.id, e.tier, vi.raw_id AS matched_raw, 'case_variant'::text AS match_kind,
                       c.rating::numeric, c.deviation::numeric,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches AS witnesses,
                       5e5::double precision AS sal_score
                FROM variant_ids vi
                JOIN LATERAL public.laplace_entities(ARRAY[vi.id]) e ON e.id = vi.id
                LEFT JOIN public.consensus c ON c.claim = e.id
                WHERE $7::boolean AND e.tier IS NOT NULL
            ),
            all_lookup AS (
                SELECT ARRAY(
                    SELECT public.blake3_from_bytea(m) FROM unnest($1::bytea[]) AS m
                    UNION
                    SELECT id FROM variant_ids
                ) AS ids
            ),
            sal AS MATERIALIZED (
                SELECT s.id, s.salience
                FROM all_lookup al,
                     public.laplace_salience(al.ids) s
                WHERE cardinality(al.ids) > 0
            ),
            sal_max AS MATERIALIZED (
                SELECT coalesce(max(salience), 0::double precision) AS max_sal FROM sal
            ),
            constituents AS MATERIALIZED (
                SELECT e.id, e.tier, public.blake3_send(e.id) AS matched_raw,
                       'constituent'::text AS match_kind,
                       c.rating::numeric, c.deviation::numeric,
                       public.laplace_confidence(c.rating, c.deviation, 2::double precision)::numeric AS eff_mu,
                       c.matches AS witnesses,
                       coalesce(sc.salience, 0::double precision) AS sal_score
                FROM all_lookup al
                CROSS JOIN unnest(al.ids) AS u(id)
                JOIN public.laplace_entities(ARRAY[u.id]) e ON e.id = u.id
                LEFT JOIN sal sc ON sc.id = u.id
                LEFT JOIN public.consensus c ON c.claim = e.id
                WHERE cardinality(al.ids) > 1
                  AND e.tier IS NOT NULL
                  AND e.id <> (SELECT id FROM exact_id)
                  AND NOT EXISTS (SELECT 1 FROM variant_ids vi WHERE vi.id = e.id)
            ),
            salient_lookup AS MATERIALIZED (
                SELECT ARRAY(
                    SELECT u.id
                    FROM all_lookup al, unnest(al.ids) AS u(id)
                    LEFT JOIN sal sc ON sc.id = u.id
                    CROSS JOIN sal_max sm
                    WHERE cardinality(al.ids) <= 1
                       OR sm.max_sal <= 0
                       OR coalesce(sc.salience, 0::double precision) >= 0.40 * sm.max_sal
                ) AS ids
            ),
            holders AS (
                SELECT DISTINCT ON (h.entity)
                       h.entity AS id, h.tier,
                       coalesce(public.blake3_send(sl.ids[1]), $1[1], $2::bytea) AS matched_raw,
                       'member'::text AS match_kind,
                       h.rating::numeric, h.deviation::numeric,
                       public.laplace_confidence(h.rating, h.deviation, 2::double precision)::numeric AS eff_mu,
                       h.matches AS witnesses,
                       coalesce((SELECT sm.max_sal FROM sal_max sm), 0::double precision) AS sal_score
                FROM salient_lookup sl,
                     public.laplace_holders_with_standings(
                         sl.ids,
                         CASE WHEN $7::boolean THEN '{}'::public.blake3[] ELSE sl.ids END,
                         $7::boolean,
                         (-1)::smallint, greatest($6::bigint, 256), 3::smallint) h
                WHERE cardinality(sl.ids) > 0
                  AND h.entity <> (SELECT id FROM exact_id)
                  AND NOT EXISTS (SELECT 1 FROM variant_ids vi WHERE vi.id = h.entity)
                  AND NOT EXISTS (SELECT 1 FROM constituents ct WHERE ct.id = h.entity)
                ORDER BY h.entity, h.matches DESC NULLS LAST
            ),
            combined AS (
                SELECT * FROM direct
                UNION ALL
                SELECT * FROM variants
                UNION ALL
                SELECT * FROM constituents
                UNION ALL
                SELECT * FROM holders
            ),
            total AS (SELECT count(*)::bigint AS total_cnt FROM combined)
            SELECT public.blake3_send(c.id), c.tier, c.matched_raw, c.match_kind,
                   c.rating, c.deviation, c.eff_mu, coalesce(c.witnesses, 0)::bigint,
                   (SELECT total_cnt FROM total),
                   ((SELECT total_cnt FROM total) >= $6)::boolean,
                   (SELECT total_cnt FROM total)
            FROM combined c
            ORDER BY (c.match_kind = 'exact') DESC,
                     (c.match_kind = 'case_variant') DESC,
                     (c.match_kind = 'constituent') DESC,
                     c.sal_score DESC,
                     coalesce(c.eff_mu, -1e9) DESC NULLS LAST,
                     coalesce(c.witnesses, 0) DESC,
                     c.tier,
                     c.id
            OFFSET $4 LIMIT $5
            """;

        var rows = await NpgsqlRead.ReadRowsAsync(conn, sql,
            static r =>
            {
                short tier = r.GetInt16(1);
                string kind = tier switch { 0 => "unicode", 1 => "word", _ => "composition" };
                return (
                    Id: r.GetFieldValue<byte[]>(0),
                    Row: new NamedEntityRow(
                        Convert.ToHexStringLower(r.GetFieldValue<byte[]>(0)),
                        "",
                        tier,
                        kind,
                        Convert.ToHexStringLower(r.GetFieldValue<byte[]>(2)),
                        r.GetString(3),
                        r.IsDBNull(4) ? null : r.GetDecimal(4),
                        r.IsDBNull(5) ? null : r.GetDecimal(5),
                        r.IsDBNull(6) ? null : r.GetDecimal(6),
                        r.GetInt64(7),
                        r.GetInt64(8),
                        r.GetBoolean(9),
                        r.GetInt64(10)));
            },
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = memberIds });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = exactRootId });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = variantRootIds });
                p.AddWithValue(Math.Max(0, offset));
                p.AddWithValue(Math.Max(0, limit));
                p.AddWithValue(Math.Max(1, candidateCapacity));
                p.AddWithValue(discover);
            }, ct: ct, label: "named_entities_search", onError: onError).ConfigureAwait(false);
        if (rows.Count == 0) return [];

        var labels = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("display.labels"),
            static r => (IdHex: r.GetString(0), Label: r.GetString(1)),
            p => p.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = rows.Select(r => r.Id).ToArray(),
            timeoutSeconds: 30, ct: ct, label: "named_entity_labels", onError: onError).ConfigureAwait(false);

        var labelMap = new Dictionary<string, string>(labels.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var l in labels) labelMap.TryAdd(l.IdHex, l.Label);

        return rows.Select(r => r.Row with
        {
            Label = labelMap.TryGetValue(r.Row.IdHex, out var text) && !string.IsNullOrEmpty(text) ? text : r.Row.IdHex,
        }).ToArray();
    }

    public static async Task<LeaderboardPage> LeaderboardAsync(
        NpgsqlConnection conn, int offset, int limit, string sort, short? tier,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);
        string normalizedSort = sort?.Trim().ToLowerInvariant() switch
        {
            "witnesses" or "matches" => "witnesses",
            _ => "confidence"
        };

        var clock = Stopwatch.StartNew();
        IReadOnlyList<LeaderboardEntryRow> rows;
        if (normalizedSort == "confidence" && tier is null)
        {
            const string fastSql = """
                WITH tops AS MATERIALIZED (
                    SELECT c.claim,
                           public.laplace_confidence(c.rating, c.deviation, 2::double precision) AS confidence,
                           c.rating,
                           c.deviation,
                           c.matches
                    FROM public.consensus c
                    WHERE c.matches > 0
                    ORDER BY public.laplace_confidence(c.rating, c.deviation, 2::double precision) DESC, c.matches DESC, c.claim
                    OFFSET $1 LIMIT $2
                ),
                ents AS MATERIALIZED (
                    SELECT id, tier
                    FROM public.laplace_entities(ARRAY(SELECT claim FROM tops))
                )
                SELECT encode(public.blake3_send(t.claim), 'hex'),
                       coalesce(public.laplace_label_text(public.blake3_send(t.claim), true), encode(public.blake3_send(t.claim), 'hex')),
                       coalesce(e.tier, 0)::smallint,
                       t.confidence,
                       t.rating,
                       t.deviation,
                       t.matches,
                       coalesce((
                           SELECT jsonb_agg(jsonb_build_object(
                               'id', encode(public.blake3_send(tc.child), 'hex'),
                               'label', coalesce(public.laplace_label_text(public.blake3_send(tc.child), true), encode(public.blake3_send(tc.child), 'hex')),
                               'run', tc.run_length
                           ) ORDER BY tc.ordinal)::text
                           FROM public.laplace_constituents(ARRAY[t.claim]) tc
                       ), '[]')
                FROM tops t
                LEFT JOIN ents e ON e.id = t.claim
                ORDER BY t.confidence DESC, t.matches DESC, t.claim
                """;
            rows = await NpgsqlRead.ReadRowsAsync(conn, fastSql, MapLeaderboardRow, p =>
            {
                p.AddWithValue(offset);
                p.AddWithValue(limit);
            }, ct: ct, label: "leaderboard_standing", onError: onError).ConfigureAwait(false);
        }
        else
        {
            string sql = normalizedSort == "witnesses"
                ? """
                  WITH pool AS MATERIALIZED (
                      SELECT c.claim,
                             public.laplace_confidence(c.rating, c.deviation, 2::double precision) AS confidence,
                             c.rating,
                             c.deviation,
                             c.matches
                      FROM public.consensus c
                      WHERE c.matches > 0
                      ORDER BY public.laplace_confidence(c.rating, c.deviation, 2::double precision) DESC, c.matches DESC, c.claim
                      LIMIT greatest(1000, ($1 + $2) * 16)
                  ),
                  ents AS MATERIALIZED (
                      SELECT id, tier
                      FROM public.laplace_entities(ARRAY(SELECT claim FROM pool))
                  ),
                  tops AS MATERIALIZED (
                      SELECT p.claim, p.confidence, p.rating, p.deviation, p.matches, coalesce(e.tier, 0)::smallint AS tier
                      FROM pool p
                      LEFT JOIN ents e ON e.id = p.claim
                      WHERE ($3::smallint IS NULL OR e.tier = $3::smallint)
                      ORDER BY p.matches DESC, p.confidence DESC, p.claim
                      OFFSET $1 LIMIT $2
                  )
                  SELECT encode(public.blake3_send(t.claim), 'hex'),
                         coalesce(public.laplace_label_text(public.blake3_send(t.claim), true), encode(public.blake3_send(t.claim), 'hex')),
                         t.tier,
                         t.confidence,
                         t.rating,
                         t.deviation,
                         t.matches,
                         coalesce((
                             SELECT jsonb_agg(jsonb_build_object(
                                 'id', encode(public.blake3_send(tc.child), 'hex'),
                                 'label', coalesce(public.laplace_label_text(public.blake3_send(tc.child), true), encode(public.blake3_send(tc.child), 'hex')),
                                 'run', tc.run_length
                             ) ORDER BY tc.ordinal)::text
                             FROM public.laplace_constituents(ARRAY[t.claim]) tc
                         ), '[]')
                  FROM tops t
                  ORDER BY t.matches DESC, t.confidence DESC, t.claim
                  """
                : """
                  WITH pool AS MATERIALIZED (
                      SELECT c.claim,
                             public.laplace_confidence(c.rating, c.deviation, 2::double precision) AS confidence,
                             c.rating,
                             c.deviation,
                             c.matches
                      FROM public.consensus c
                      WHERE c.matches > 0
                      ORDER BY public.laplace_confidence(c.rating, c.deviation, 2::double precision) DESC, c.matches DESC, c.claim
                      LIMIT greatest(1000, ($1 + $2) * 16)
                  ),
                  ents AS MATERIALIZED (
                      SELECT id, tier
                      FROM public.laplace_entities(ARRAY(SELECT claim FROM pool))
                  ),
                  tops AS MATERIALIZED (
                      SELECT p.claim, p.confidence, p.rating, p.deviation, p.matches, coalesce(e.tier, 0)::smallint AS tier
                      FROM pool p
                      LEFT JOIN ents e ON e.id = p.claim
                      WHERE ($3::smallint IS NULL OR e.tier = $3::smallint)
                      ORDER BY p.confidence DESC, p.matches DESC, p.claim
                      OFFSET $1 LIMIT $2
                  )
                  SELECT encode(public.blake3_send(t.claim), 'hex'),
                         coalesce(public.laplace_label_text(public.blake3_send(t.claim), true), encode(public.blake3_send(t.claim), 'hex')),
                         t.tier,
                         t.confidence,
                         t.rating,
                         t.deviation,
                         t.matches,
                         coalesce((
                             SELECT jsonb_agg(jsonb_build_object(
                                 'id', encode(public.blake3_send(tc.child), 'hex'),
                                 'label', coalesce(public.laplace_label_text(public.blake3_send(tc.child), true), encode(public.blake3_send(tc.child), 'hex')),
                                 'run', tc.run_length
                             ) ORDER BY tc.ordinal)::text
                             FROM public.laplace_constituents(ARRAY[t.claim]) tc
                         ), '[]')
                  FROM tops t
                  ORDER BY t.confidence DESC, t.matches DESC, t.claim
                  """;
            rows = await NpgsqlRead.ReadRowsAsync(conn, sql, MapLeaderboardRow, p =>
            {
                p.AddWithValue(offset);
                p.AddWithValue(limit);
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (object?)tier ?? DBNull.Value });
            }, ct: ct, label: "leaderboard_filtered", onError: onError).ConfigureAwait(false);
        }

        clock.Stop();
        return new LeaderboardPage(rows, offset, limit, normalizedSort, tier,
            Math.Max(0, clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency));
    }

    private static LeaderboardEntryRow MapLeaderboardRow(NpgsqlDataReader r) =>
        new(
            IdHex: r.GetString(0),
            Label: r.GetString(1),
            Tier: r.GetInt16(2),
            Confidence: r.GetDouble(3),
            Rating: r.GetDouble(4),
            Deviation: r.GetDouble(5),
            Matches: r.GetInt64(6),
            PartsJson: r.GetString(7));
}
