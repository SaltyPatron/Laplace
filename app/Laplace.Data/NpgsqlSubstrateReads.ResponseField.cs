using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

public static partial class NpgsqlSubstrateReads
{
    public sealed record LexiconRelationRow(byte[] Id, string? Canonical, bool Inverse, bool Descriptive, bool Textual);

    public static Task<IReadOnlyList<LexiconRelationRow>> LexiconRelationsAsync(
        NpgsqlConnection connection, byte[][] relations, string[] criteria, CancellationToken ct = default)
    {
        if (relations.Length == 0)
            return Task.FromResult<IReadOnlyList<LexiconRelationRow>>(Array.Empty<LexiconRelationRow>());

        const string sql = """
            SELECT l.entity,
                   l.content,
                   false,
                   l.content IS NOT NULL AND lower(l.content) = ANY(coalesce($2::text[], ARRAY[]::text[])),
                   true
            FROM public.laplace_labels(ARRAY(SELECT public.blake3_from_bytea(r) FROM unnest($1::bytea[]) AS r WHERE r IS NOT NULL)) l
            """;
        return NpgsqlRead.ReadRowsAsync(connection, sql,
            static r => new LexiconRelationRow(
                r.GetFieldValue<byte[]>(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                !r.IsDBNull(2) && r.GetBoolean(2),
                r.GetBoolean(3),
                r.GetBoolean(4)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = relations });
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = criteria });
            }, ct: ct, label: "lexicon_relations");
    }

    public sealed record LexiconDescriptionRow(byte[] Id, string? Label, bool Identifier);

    public static Task<IReadOnlyList<LexiconDescriptionRow>> LexiconDescriptionsAsync(
        NpgsqlConnection connection, byte[][] ids, CancellationToken ct = default)
    {
        if (ids.Length == 0)
            return Task.FromResult<IReadOnlyList<LexiconDescriptionRow>>(Array.Empty<LexiconDescriptionRow>());

        const string sql = """
            SELECT public.blake3_send(l.entity),
                   l.content,
                   false
            FROM public.laplace_labels(ARRAY(SELECT public.blake3_from_bytea(u) FROM unnest($1::bytea[]) AS u WHERE u IS NOT NULL)) l
            """;
        return NpgsqlRead.ReadRowsAsync(connection, sql,
            static r => new LexiconDescriptionRow(r.GetFieldValue<byte[]>(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetBoolean(2)),
            p => p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = ids }),
            ct: ct, label: "lexicon_descriptions");
    }

    public sealed record ResponseFieldRow(
        int Round, int Occurrence, byte[]? Anchor, string? AnchorLabel,
        byte[] Entity, string Label, short Route, byte[]? Via, byte[]? Relation, string? RelationLabel,
        double? Rating, double? Deviation, double? Volatility, long? Matches, long? Witnesses,
        short Tier, double? Distance, bool? Outbound, int[] Origins, int[] Targets,
        byte[][] BindingClaims, int[] Anchors, bool Textual, int[] ReadingOrigins);

    public static Task<IReadOnlyList<ResponseFieldRow>> ResponseFieldAsync(
        NpgsqlConnection connection, IReadOnlyList<byte[]> occurrences, int fanout, int nearest,
        CancellationToken ct = default) => ReadFieldAsync(connection, occurrences, fanout, nearest, false, ct);

    public static Task<IReadOnlyList<ResponseFieldRow>> LexiconFieldAsync(
        NpgsqlConnection connection, IReadOnlyList<byte[]> occurrences,
        CancellationToken ct = default) => ReadFieldAsync(connection, occurrences, 0, 0, true, ct);

    private static Task<IReadOnlyList<ResponseFieldRow>> ReadFieldAsync(
        NpgsqlConnection connection, IReadOnlyList<byte[]> occurrences, int fanout, int nearest,
        bool lexical, CancellationToken ct)
    {
        if (occurrences.Count == 0)
            return Task.FromResult<IReadOnlyList<ResponseFieldRow>>(Array.Empty<ResponseFieldRow>());

        const string sql = """
            WITH occs AS MATERIALIZED (
                SELECT (ord - 1)::int AS occ_idx, public.blake3_from_bytea(raw_id) AS id, raw_id
                FROM unnest($1::bytea[]) WITH ORDINALITY AS u(raw_id, ord)
            ),
            coupled AS MATERIALIZED (
                SELECT 1 AS rnd,
                       c.occ,
                       (SELECT id FROM occs WHERE occ_idx = c.occ LIMIT 1) AS anchor_id,
                       c.entity, c.route, c.rating, c.deviation, c.volatility,
                       c.via, c.rel, c.tier, c.distance, c.vertices
                FROM public.laplace_couple(
                    ARRAY(SELECT id FROM occs ORDER BY occ_idx),
                    greatest(8, $2)::bigint,
                    '{}'::public.blake3[],
                    CASE WHEN $3 > 0 THEN 2::smallint ELSE 0::smallint END,
                    0.0,
                    greatest(8, $2 + $3)) c
            ),
            st AS MATERIALIZED (
                SELECT s.claim, s.matches
                FROM public.laplace_standings(ARRAY(SELECT DISTINCT via FROM coupled WHERE via IS NOT NULL)) s
            )
            SELECT c.rnd,
                   c.occ,
                   CASE WHEN c.anchor_id IS NULL THEN NULL ELSE public.blake3_send(c.anchor_id) END,
                   CASE WHEN c.anchor_id IS NULL THEN NULL ELSE public.laplace_label_text(public.blake3_send(c.anchor_id), false) END,
                   public.blake3_send(c.entity),
                   coalesce(public.laplace_label_text(public.blake3_send(c.entity), false), encode(public.blake3_send(c.entity), 'hex')),
                   c.route,
                   CASE WHEN c.via IS NULL THEN NULL ELSE public.blake3_send(c.via) END,
                   CASE WHEN c.rel IS NULL THEN NULL ELSE public.blake3_send(c.rel) END,
                   CASE WHEN c.rel IS NULL THEN NULL ELSE coalesce(public.laplace_label_text(public.blake3_send(c.rel), false), encode(public.blake3_send(c.rel), 'hex')) END,
                   c.rating, c.deviation, c.volatility,
                   st.matches,
                   st.matches,
                   c.tier,
                   c.distance,
                   CASE WHEN c.vertices IS NOT NULL AND cardinality(c.vertices) >= 1 THEN (c.vertices[1] = c.anchor_id) ELSE NULL END,
                   ARRAY[c.occ],
                   ARRAY[]::int[],
                   CASE WHEN c.via IS NULL THEN ARRAY[]::bytea[] ELSE ARRAY[public.blake3_send(c.via)] END,
                   ARRAY[c.occ],
                   true,
                   ARRAY[c.occ]
            FROM coupled c
            LEFT JOIN st ON st.claim = c.via
            """;

        return NpgsqlRead.ReadRowsAsync(connection, sql,
            static r => new ResponseFieldRow(
                r.GetInt32(0), r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetFieldValue<byte[]>(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetFieldValue<byte[]>(4), r.GetString(5), r.GetInt16(6),
                r.IsDBNull(7) ? null : r.GetFieldValue<byte[]>(7),
                r.IsDBNull(8) ? null : r.GetFieldValue<byte[]>(8),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetDouble(10),
                r.IsDBNull(11) ? null : r.GetDouble(11),
                r.IsDBNull(12) ? null : r.GetDouble(12),
                r.IsDBNull(13) ? null : r.GetInt64(13),
                r.IsDBNull(14) ? null : r.GetInt64(14),
                r.GetInt16(15),
                r.IsDBNull(16) ? null : r.GetDouble(16),
                r.IsDBNull(17) ? null : r.GetBoolean(17),
                r.GetFieldValue<int[]>(18),
                r.GetFieldValue<int[]>(19),
                r.GetFieldValue<byte[][]>(20),
                r.GetFieldValue<int[]>(21),
                r.GetBoolean(22),
                r.GetFieldValue<int[]>(23)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = occurrences.ToArray() });
                p.AddWithValue(lexical ? 24 : Math.Max(1, fanout));
                p.AddWithValue(lexical ? 0 : Math.Max(0, nearest));
            }, ct: ct, label: lexical ? "lexicon_field" : "response_field");
    }
}
