using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct MeshPositionRow(
        string Dir, string IdHex, string Label, string Relation, string? HubType,
        decimal? EffMu, long Witnesses);

    public static Task<IReadOnlyList<MeshPositionRow>> MeshPositionAsync(
        NpgsqlDataSource dataSource, byte[] id, int relationLimit, int memberLimit,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH self_row AS (
                SELECT 'self'::text AS dir,
                       encode($1, 'hex') AS id_hex,
                       coalesce(public.laplace_label_text($1, false), encode($1, 'hex')) AS label,
                       ''::text AS relation,
                       NULL::text AS hub_type,
                       NULL::numeric AS eff_mu,
                       0::bigint AS witnesses
            ),
            out_claims AS (
                SELECT 'up'::text AS dir,
                       public.laplace_parts(c.path) AS pts,
                       public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS eff_mu,
                       c.matches AS witnesses
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], greatest(1, $2)::bigint, NULL, '{}'::public.blake3[], 1::smallint) c
            ),
            in_claims AS (
                SELECT 'down'::text AS dir,
                       public.laplace_parts(c.path) AS pts,
                       public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS eff_mu,
                       c.matches AS witnesses
                FROM public.laplace_claims_each(ARRAY[public.blake3_from_bytea($1)], greatest(1, $3)::bigint, NULL, '{}'::public.blake3[], 2::smallint) c
            ),
            all_links AS (
                SELECT dir, pts[cardinality(pts)] AS peer_id, pts[2] AS rel_id, eff_mu, witnesses
                FROM out_claims WHERE cardinality(pts) >= 2
                UNION ALL
                SELECT dir, pts[1] AS peer_id, pts[2] AS rel_id, eff_mu, witnesses
                FROM in_claims WHERE cardinality(pts) >= 2
            )
            SELECT dir, id_hex, label, relation, hub_type, eff_mu, witnesses FROM self_row
            UNION ALL
            SELECT dir,
                   encode(public.blake3_send(peer_id), 'hex'),
                   coalesce(public.laplace_label_text(public.blake3_send(peer_id), false), encode(public.blake3_send(peer_id), 'hex')),
                   coalesce(public.laplace_label_text(public.blake3_send(rel_id), false), encode(public.blake3_send(rel_id), 'hex')),
                   NULL::text,
                   eff_mu,
                   witnesses
            FROM all_links
            """;

        return NpgsqlRead.ReadRowsAsync(dataSource, sql,
            static r => new MeshPositionRow(
                r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetDecimal(5),
                r.IsDBNull(6) ? 0L : r.GetInt64(6)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(RequestedLimit(relationLimit));
                p.AddWithValue(RequestedLimit(memberLimit));
            }, ct: ct, label: "mesh_position", onError: onError);
    }

    public readonly record struct ExploreResolveRow(byte[] Id, string Label, string RefKind, bool Exists);

    public static async Task<ExploreResolveRow?> ExploreResolveAsync(
        NpgsqlConnection conn, string reference, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH target AS (
                SELECT coalesce(public.laplace_resolve_ref($2), $1) AS id_raw
            ),
            ent AS (
                SELECT t.id_raw,
                       e.id,
                       coalesce(public.laplace_label_text(t.id_raw, false), coalesce($2, encode(t.id_raw, 'hex'))) AS label,
                       CASE WHEN $2 IS NULL THEN 'id' ELSE 'surface' END AS ref_kind,
                       (e.tier IS NOT NULL OR public.laplace_codepoint(public.blake3_from_bytea(t.id_raw)) >= 0) AS exists_flag
                FROM target t
                LEFT JOIN public.laplace_entities(ARRAY[public.blake3_from_bytea(t.id_raw)]) e ON true
            )
            SELECT id_raw, label, ref_kind, exists_flag FROM ent
            """;
        var rows = await NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new ExploreResolveRow(
                r.GetFieldValue<byte[]>(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)),
            p => BindCanonicalReference(p, reference.Trim()),
            ct: ct, label: "explore_resolve", onError: onError).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public readonly record struct AnchorNeighborRow(
        string Axis, string IdHex, string? Label, short? Tier, double? Geodesic, double? Frechet);

    public static Task<IReadOnlyList<AnchorNeighborRow>> ExploreAnchorNeighborsAsync(
        NpgsqlConnection conn, double cx, double cy, double cz, double cm, string? trajectoryWkt,
        int geodesicK, int frechetK, double frechetMax, int timeoutSeconds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH anchor AS (
                SELECT ST_MakePoint($1, $2, $3, $4) AS pt,
                       CASE WHEN $5 IS NULL THEN NULL ELSE ST_GeomFromText($5) END AS traj
            ),
            knn AS MATERIALIZED (
                SELECT k.ordinal, k.id, k.tier, k.distance
                FROM anchor a, public.laplace_knn4d(a.pt, greatest(1, $6), false, 0::smallint, NULL, NULL) k
            ),
            curves AS MATERIALIZED (
                SELECT c.entity, ST_GeomFromEWKB(c.curve_ewkb) AS curve
                FROM public.laplace_curves(ARRAY(SELECT id FROM knn LIMIT greatest(1, $7))) c
                WHERE c.curve_ewkb IS NOT NULL
            )
            SELECT CASE WHEN a.traj IS NOT NULL AND cv.curve IS NOT NULL THEN 'shape' ELSE 'geodesic' END AS axis,
                   encode(public.blake3_send(k.id), 'hex') AS id_hex,
                   public.laplace_label_text(public.blake3_send(k.id), false) AS label,
                   k.tier,
                   k.distance AS geodesic,
                   CASE WHEN a.traj IS NOT NULL AND cv.curve IS NOT NULL
                        THEN public.laplace_frechet4d(a.traj, cv.curve)
                        ELSE NULL END AS frechet
            FROM knn k
            CROSS JOIN anchor a
            LEFT JOIN curves cv ON cv.entity = k.id
            WHERE a.traj IS NULL OR cv.curve IS NULL OR public.laplace_frechet4d(a.traj, cv.curve) <= $8
            ORDER BY k.distance ASC
            """;

        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new AnchorNeighborRow(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt16(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5)),
            p =>
            {
                p.AddWithValue(cx);
                p.AddWithValue(cy);
                p.AddWithValue(cz);
                p.AddWithValue(cm);
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)trajectoryWkt ?? DBNull.Value });
                p.AddWithValue(geodesicK);
                p.AddWithValue(frechetK);
                p.AddWithValue(frechetMax);
            }, timeoutSeconds: timeoutSeconds, ct: ct, label: "explore_anchor_neighbors", onError: onError);
    }

    public readonly record struct WitnessedWordRow(string Surface, string IdHex, long Witnesses);

    public static Task<IReadOnlyList<WitnessedWordRow>> WitnessedWordsAsync(
        NpgsqlConnection conn, string[] surfaces, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH inputs AS MATERIALIZED (
                SELECT s, public.laplace_text_id(s) AS id
                FROM unnest($1::text[]) AS u(s)
            ),
            present AS MATERIALIZED (
                SELECT e.id, e.ordinal
                FROM public.laplace_entities(ARRAY(SELECT id FROM inputs)) e
                WHERE e.tier IS NOT NULL
            )
            SELECT inp.s,
                   encode(public.blake3_send(inp.id), 'hex'),
                   coalesce(c.matches, 1)::bigint
            FROM inputs inp
            JOIN present pr ON pr.id = inp.id
            LEFT JOIN public.consensus c ON c.claim = inp.id
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new WitnessedWordRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? 0L : r.GetInt64(2)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = surfaces }),
            ct: ct, label: "witnessed_words", onError: onError);
    }

    public readonly record struct StructuralNeighborRow(
        string? IdHex, string? Label, double Geodesic, double? Frechet,
        double? X, double? Y, double? Z, double? M, double? Radius);

    private const string StructuralNeighborsSql = """
        WITH target AS MATERIALIZED (
            SELECT public.blake3_from_bytea($1) AS tid
        ),
        src_ent AS (
            SELECT e.coord
            FROM target t, public.laplace_entities(ARRAY[t.tid]) e
            LIMIT 1
        ),
        src_curve AS (
            SELECT ST_GeomFromEWKB(cv.curve_ewkb) AS curve
            FROM target t, public.laplace_curves(ARRAY[t.tid]) cv
            WHERE cv.curve_ewkb IS NOT NULL
            LIMIT 1
        ),
        knn AS MATERIALIZED (
            SELECT k.id, k.coord, k.distance
            FROM target t, src_ent s,
                 public.laplace_knn4d(s.coord, greatest(1, $2), false, 0::smallint, NULL, t.tid) k
        ),
        curves AS MATERIALIZED (
            SELECT cv.entity, ST_GeomFromEWKB(cv.curve_ewkb) AS curve
            FROM public.laplace_curves(ARRAY(SELECT id FROM knn)) cv
            WHERE cv.curve_ewkb IS NOT NULL
        )
        SELECT encode(public.blake3_send(k.id), 'hex'),
               public.laplace_label_text(public.blake3_send(k.id), false),
               k.distance,
               CASE WHEN sc.curve IS NOT NULL AND c.curve IS NOT NULL
                    THEN public.laplace_frechet4d(sc.curve, c.curve)
                    ELSE NULL END,
               ST_X(k.coord), ST_Y(k.coord), ST_Z(k.coord), ST_M(k.coord),
               sqrt(ST_X(k.coord)*ST_X(k.coord) + ST_Y(k.coord)*ST_Y(k.coord) + ST_Z(k.coord)*ST_Z(k.coord) + ST_M(k.coord)*ST_M(k.coord))
        FROM knn k
        LEFT JOIN src_curve sc ON true
        LEFT JOIN curves c ON c.entity = k.id
        ORDER BY k.distance ASC
        """;

    public static Task<IReadOnlyList<StructuralNeighborRow>> StructuralNeighborsAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, StructuralNeighborsSql,
            static r => new StructuralNeighborRow(
                r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetDouble(2), r.IsDBNull(3) ? null : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7), r.IsDBNull(8) ? null : r.GetDouble(8)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(limit);
            }, ct: ct, label: "structural_neighbors", onError: onError);

    public static Task<IReadOnlyList<StructuralNeighborRow>> StructuralNeighborsAsync(
        NpgsqlDataSource dataSource, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, StructuralNeighborsSql,
            static r => new StructuralNeighborRow(
                r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.GetDouble(2), r.IsDBNull(3) ? null : r.GetDouble(3), r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5), r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7), r.IsDBNull(8) ? null : r.GetDouble(8)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(limit);
            }, ct: ct, label: "structural_neighbors", onError: onError);

    public readonly record struct ContainerRow(string IdHex, short Tier, string? Type, int Hops, string Label);

    public static async Task<IReadOnlyList<ContainerRow>> ContainersAsync(
        NpgsqlConnection conn, byte[] id, int hops, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT encode(public.blake3_send(a.entity), 'hex'), a.tier, NULL::text, a.hops,
                   coalesce(public.laplace_label_text(public.blake3_send(a.entity), false), encode(public.blake3_send(a.entity), 'hex'))
            FROM public.laplace_above(ARRAY[public.blake3_from_bytea($1)], greatest(1, $2), greatest(1, $3)::bigint) a
            """;
        return await NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new ContainerRow(
                r.GetString(0), r.GetInt16(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetInt32(3), r.GetString(4)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = id });
                p.AddWithValue(hops);
                p.AddWithValue(limit);
            }, ct: ct, label: "containers", onError: onError).ConfigureAwait(false);
    }

    public readonly record struct EmbeddingLookupRow(
        int Kind, long Ord, byte[]? EntityId,
        double? X, double? Y, double? Z, double? M, double? Radius, int? Constituents,
        string? Relation, string? ObjectLabel, decimal? EffMu, long? Witnesses);

    public static Task<IReadOnlyList<EmbeddingLookupRow>> EmbeddingLookupAsync(
        NpgsqlConnection conn, string target, int meaningLimit, bool includeMeaning, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null, string[]? constituentTerms = null)
    {
        const string sql = """
            WITH anchor_id AS MATERIALIZED (
                SELECT coalesce(public.laplace_resolve_ref($1), public.blake3_send(public.laplace_text_id($1))) AS id_raw
            ),
            anchor AS (
                SELECT a.id_raw, e.id, e.x, e.y, e.z, e.m,
                       sqrt(e.x*e.x + e.y*e.y + e.z*e.z + e.m*e.m) AS radius,
                       coalesce(ST_NumPoints(p.path), 1) AS path_len
                FROM anchor_id a
                JOIN public.laplace_entity_coords(ARRAY[public.blake3_from_bytea(a.id_raw)]) e ON true
                LEFT JOIN public.laplace_physicalities(ARRAY[public.blake3_from_bytea(a.id_raw)]) p ON p.entity = e.id
            ),
            terms_ids AS (
                SELECT public.laplace_text_id(t) AS id
                FROM unnest(coalesce($4::text[], ARRAY[]::text[])) AS u(t)
                WHERE $3 AND $4 IS NOT NULL
            ),
            seed_ids AS MATERIALIZED (
                SELECT public.blake3_from_bytea(id_raw) AS id FROM anchor_id
                UNION ALL
                SELECT id FROM terms_ids WHERE id IS NOT NULL
            ),
            coupled AS MATERIALIZED (
                SELECT row_number() OVER (
                    ORDER BY public.laplace_confidence(c.rating, c.deviation, 2) DESC
                ) AS ord,
                c.rel, c.entity, public.laplace_confidence(c.rating, c.deviation, 2)::numeric AS eff_mu,
                s.matches AS witnesses
                FROM public.laplace_couple(
                    ARRAY(SELECT DISTINCT id FROM seed_ids),
                    greatest(8, $2)::bigint,
                    '{}'::public.blake3[], 0::smallint, 0.0, greatest(1, $2)) c
                LEFT JOIN public.laplace_standings(ARRAY[c.via]) s ON s.claim = c.via
                WHERE $3 AND $2 > 0
            )
            SELECT 0, 0::bigint, id_raw, x, y, z, m, radius, path_len,
                   NULL::text, NULL::text, NULL::numeric, NULL::bigint
            FROM anchor
            UNION ALL
            SELECT 1, c.ord, public.blake3_send(c.entity), NULL::float8, NULL::float8, NULL::float8, NULL::float8,
                   NULL::float8, NULL::int4,
                   coalesce(public.laplace_label_text(public.blake3_send(c.rel), false), 'related'),
                   coalesce(public.laplace_label_text(public.blake3_send(c.entity), false), encode(public.blake3_send(c.entity), 'hex')),
                   c.eff_mu,
                   coalesce(c.witnesses, 1)::bigint
            FROM coupled c
            ORDER BY 1, 2
            """;

        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new EmbeddingLookupRow(
                r.GetInt32(0), r.GetInt64(1), r.IsDBNull(2) ? null : (byte[])r[2],
                r.IsDBNull(3) ? null : r.GetDouble(3),
                r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5),
                r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7),
                r.IsDBNull(8) ? null : r.GetInt32(8),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetDecimal(11),
                r.IsDBNull(12) ? null : r.GetInt64(12)),
            p =>
            {
                p.AddWithValue(target);
                p.AddWithValue(meaningLimit);
                p.AddWithValue(includeMeaning);
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = (object?)constituentTerms ?? DBNull.Value });
            }, ct: ct, label: "embedding_lookup", onError: onError);
    }

    public readonly record struct ExploreWebEdgeRow(
        string SourceIdHex, string TypeIdHex, string ObjectIdHex, short Hop, decimal EffMu,
        long WitnessCount, double CompleteWeight, bool Refuted);

    public static Task<IReadOnlyList<ExploreWebEdgeRow>> ExploreWebAsync(
        NpgsqlConnection conn, byte[] seed, int hops, int fanout, int maxNodes, int timeoutSeconds,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT encode(public.blake3_send(p.via), 'hex'),
                   coalesce(encode(public.blake3_send(p.relation), 'hex'), repeat('0', 32)),
                   encode(public.blake3_send(p.id), 'hex'),
                   p.hops::smallint,
                   public.laplace_confidence(p.rating, p.deviation, 2)::numeric,
                   coalesce(s.matches, 1)::bigint,
                   public.laplace_confidence(p.rating, p.deviation, 2)::float8,
                   (public.laplace_confidence(p.rating, p.deviation, 2) < 1450)
            FROM public.laplace_pull(
                ARRAY[public.blake3_from_bytea($1)],
                NULL,
                greatest(1, $2),
                greatest(1, $3)::bigint,
                greatest(1, $4)::bigint) p
            LEFT JOIN public.laplace_standings(ARRAY[p.claim]) s ON s.claim = p.claim
            WHERE p.via IS NOT NULL
            """;

        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new ExploreWebEdgeRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt16(3), r.GetDecimal(4),
                r.GetInt64(5), r.GetDouble(6), r.GetBoolean(7)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = seed });
                p.AddWithValue(hops);
                p.AddWithValue(fanout);
                p.AddWithValue(maxNodes);
            }, timeoutSeconds: timeoutSeconds, ct: ct, label: "explore_web", onError: onError);
    }

    public readonly record struct ExploreInducedEdgeRow(
        string SourceIdHex, string TypeIdHex, string ObjectIdHex,
        decimal Rating, decimal Rd, decimal Volatility, decimal EffMu,
        long WitnessCount, double CompleteWeight, bool Refuted);

    public static Task<IReadOnlyList<ExploreInducedEdgeRow>> ExploreInducedEdgesAsync(
        NpgsqlConnection conn, byte[][] nodes, int timeoutSeconds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            WITH node_ids AS MATERIALIZED (
                SELECT public.blake3_from_bytea(raw_id) AS id
                FROM unnest($1::bytea[]) AS u(raw_id)
            ),
            claims AS MATERIALIZED (
                SELECT DISTINCT c.entity AS claim, c.path, c.rating, c.deviation, c.volatility, c.matches
                FROM public.laplace_claims_each(
                    ARRAY(SELECT id FROM node_ids),
                    32::bigint, NULL, '{}'::public.blake3[], 1::smallint,
                    NULL, 0::bigint, ARRAY(SELECT id FROM node_ids)) c
            ),
            parsed AS (
                SELECT public.laplace_parts(path) AS pts, rating, deviation, volatility, matches
                FROM claims
            )
            SELECT encode(public.blake3_send(pts[1]), 'hex'),
                   encode(public.blake3_send(pts[2]), 'hex'),
                   encode(public.blake3_send(pts[cardinality(pts)]), 'hex'),
                   rating::numeric, deviation::numeric, volatility::numeric,
                   public.laplace_confidence(rating, deviation, 2)::numeric,
                   matches,
                   public.laplace_confidence(rating, deviation, 2)::float8,
                   (public.laplace_confidence(rating, deviation, 2) < 1450)
            FROM parsed
            WHERE cardinality(pts) >= 2
            """;

        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new ExploreInducedEdgeRow(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6),
                r.GetInt64(7), r.GetDouble(8), r.GetBoolean(9)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = nodes }),
            timeoutSeconds: timeoutSeconds, ct: ct, label: "explore_induced_edges", onError: onError);
    }

    public readonly record struct FastLabelRow(string IdHex, string? Label, short? Tier);

    public static Task<IReadOnlyList<FastLabelRow>> LabelsFastAsync(
        NpgsqlConnection conn, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        const string sql = """
            SELECT encode(public.blake3_send(l.entity), 'hex'),
                   l.content,
                   coalesce(e.tier, CASE WHEN public.laplace_codepoint(l.entity) >= 0 THEN 0::smallint END)
            FROM public.laplace_labels(ARRAY(SELECT public.blake3_from_bytea(u.id) FROM unnest($1::bytea[]) AS u(id) WHERE u.id IS NOT NULL)) l
            LEFT JOIN public.laplace_entities(ARRAY(SELECT public.blake3_from_bytea(u.id) FROM unnest($1::bytea[]) AS u(id) WHERE u.id IS NOT NULL)) e
              ON e.ordinal = l.i
            """;
        return NpgsqlRead.ReadRowsAsync(conn, sql,
            static r => new FastLabelRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt16(2)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = ids }),
            ct: ct, label: "labels_fast", onError: onError);
    }
}
