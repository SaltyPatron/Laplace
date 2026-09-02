using global::Npgsql;
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

    public static Task<IReadOnlyList<NamedEntityRow>> NamedEntitiesAsync(
        NpgsqlConnection conn,
        byte[][] memberIds,
        byte[] exactRootId,
        int offset,
        int limit,
        int candidateCapacity,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            WITH page AS MATERIALIZED (
                SELECT row_number() OVER ()::int AS ord, b.*
                FROM structural.browse_named_entities(
                    @members, @exact, @offset, @limit, @capacity) b
            ), ids AS MATERIALIZED (
                SELECT COALESCE(array_agg(p.entity_id ORDER BY p.ord), '{}'::bytea[]) AS entities,
                       COALESCE(array_agg(p.type_id ORDER BY p.ord), '{}'::bytea[]) AS types
                FROM page p
            ), labels AS MATERIALIZED (
                SELECT realize.label_batch(i.entities) AS entity_labels,
                       lexical.type_label_batch(i.types) AS type_labels
                FROM ids i
            )
            SELECT encode(p.entity_id, 'hex'),
                   COALESCE(NULLIF(l.entity_labels[p.ord], ''), 'Unrealized entity'),
                   p.tier,
                   COALESCE(NULLIF(l.type_labels[p.ord], ''), 'unrealized type'),
                   encode(p.matched_name_id, 'hex'),
                   p.match_kind,
                   p.rating,
                   p.rd,
                   p.eff_mu,
                   p.witness_count,
                   p.candidate_name_count,
                   p.candidate_truncated,
                   p.matched_entity_count
            FROM page p
            CROSS JOIN labels l
            ORDER BY p.ord
            """,
            static r => new NamedEntityRow(
                r.GetString(0),
                r.GetString(1),
                r.GetInt16(2),
                r.GetString(3),
                r.GetString(4),
                r.GetString(5),
                r.IsDBNull(6) ? null : r.GetDecimal(6),
                r.IsDBNull(7) ? null : r.GetDecimal(7),
                r.IsDBNull(8) ? null : r.GetDecimal(8),
                r.GetInt64(9),
                r.GetInt64(10),
                r.GetBoolean(11),
                r.GetInt64(12)),
            p =>
            {
                var members = p.AddWithValue("members", memberIds);
                members.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.Add("exact", NpgsqlDbType.Bytea).Value = exactRootId;
                p.AddWithValue("offset", Math.Max(0, offset));
                p.AddWithValue("limit", Math.Max(0, limit));
                p.AddWithValue("capacity", Math.Max(0, candidateCapacity));
            },
            ct: ct,
            label: "browse_named_entities",
            onError: onError);
}
