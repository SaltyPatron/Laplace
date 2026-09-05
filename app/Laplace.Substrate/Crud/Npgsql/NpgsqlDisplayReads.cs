using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Human-facing entity labels for bounded visualization/read surfaces.
///
/// Identity and presentation are deliberately separate: the 128-bit content id is always
/// returned alongside the label, but it is never substituted for missing presentation text.
/// Named entities use the normal realization name ladder; low-tier text renders directly;
/// unnamed high-tier compositions follow only their first constituent spine until a bounded
/// text chunk is reached. That gives a document/chapter/other text composition a useful Unicode
/// preview without recursively rendering the whole object just to draw a graph node.
/// </summary>
public static class NpgsqlDisplayReads
{
    public readonly record struct DisplayLabelRow(
        string IdHex, string Label, short? Tier, string? TypeLabel);

    /// <summary>
    /// Resolve a display label for each existing id in one bounded query.
    ///
    /// Order of presentation evidence:
    /// 1. witnessed/canonical name from <c>resolve_name_batch</c>;
    /// 2. the entity's own Unicode content for tier &lt;= 3;
    /// 3. for a higher-tier composition, the first descendant that reaches tier &lt;= 3,
    ///    rendered as a preview (one spine, never the full descendant closure);
    /// 4. its canonical entity-type label;
    /// 5. the explicit abstention marker <c>unrealized entity</c>.
    ///
    /// The id remains <see cref="DisplayLabelRow.IdHex"/> for navigation/click identity and
    /// is never a presentation fallback. Truncation happens in PostgreSQL text semantics, so
    /// the 48-character UI label cannot split a UTF-8 code point / UTF-16 surrogate pair.
    /// </summary>
    public static Task<IReadOnlyList<DisplayLabelRow>> DisplayLabelsAsync(
        NpgsqlConnection conn,
        byte[][] ids,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        if (ids.Length == 0)
            return Task.FromResult<IReadOnlyList<DisplayLabelRow>>([]);

        return NpgsqlRead.ReadRowsAsync(conn, """
            WITH RECURSIVE inp AS MATERIALIZED (
                SELECT x.id,
                       x.ord,
                       row_number() OVER (ORDER BY x.ord)::integer AS ix,
                       e.tier,
                       e.type_id
                FROM unnest(@ids::bytea[]) WITH ORDINALITY AS x(id, ord)
                JOIN laplace.entities e ON e.id = x.id
            ), names AS MATERIALIZED (
                SELECT realize.resolve_name_batch(
                           COALESCE(array_agg(i.id ORDER BY i.ix), '{}'::bytea[])) AS labels
                FROM inp i
            ), types AS MATERIALIZED (
                SELECT lexical.type_label_batch(
                           COALESCE(array_agg(i.type_id ORDER BY i.ix), '{}'::bytea[])) AS labels
                FROM inp i
            ), shallow_ids AS MATERIALIZED (
                SELECT COALESCE(
                           array_agg(i.id ORDER BY i.ix) FILTER (WHERE i.tier <= 3),
                           '{}'::bytea[]) AS ids
                FROM inp i
            ), shallow_labels AS MATERIALIZED (
                SELECT s.ids, realize.render_text_batch(s.ids, 3) AS labels
                FROM shallow_ids s
            ), shallow AS MATERIALIZED (
                SELECT s.ids[n] AS id, s.labels[n] AS label
                FROM shallow_labels s,
                     generate_subscripts(s.ids, 1) AS n
            ), spine(root_id, root_ord, root_ix, node_id, node_tier, depth) AS (
                SELECT i.id, i.ord, i.ix, i.id, i.tier, 0
                FROM inp i
                WHERE i.tier > 3
              UNION ALL
                SELECT s.root_id,
                       s.root_ord,
                       s.root_ix,
                       c.child_id,
                       realize.vertex_tier(c.flags),
                       s.depth + 1
                FROM spine s
                CROSS JOIN LATERAL (
                    -- A display preview follows ONE ordered trunk-to-leaf spine.  Do not
                    -- expand siblings: doing so would turn a 48-character label into a
                    -- document render and recreate the serving-path boil-the-ocean bug.
                    SELECT k.child_id, k.flags
                    FROM realize.constituents(s.node_id) k
                    ORDER BY k.ordinal
                    LIMIT 1
                ) c
                WHERE s.node_tier > 3
                  AND s.depth < 32
            ), target AS MATERIALIZED (
                SELECT DISTINCT ON (s.root_id)
                       s.root_id, s.root_ord, s.root_ix, s.node_id, s.depth
                FROM spine s
                WHERE s.node_tier <= 3
                ORDER BY s.root_id, s.depth
            ), target_ordered AS MATERIALIZED (
                SELECT t.*,
                       row_number() OVER (ORDER BY t.root_ord)::integer AS preview_ix
                FROM target t
            ), preview_ids AS MATERIALIZED (
                SELECT COALESCE(
                           array_agg(t.node_id ORDER BY t.preview_ix),
                           '{}'::bytea[]) AS ids
                FROM target_ordered t
            ), preview_labels AS MATERIALIZED (
                SELECT p.ids, realize.render_text_batch(p.ids, 3) AS labels
                FROM preview_ids p
            ), preview AS MATERIALIZED (
                SELECT t.root_id,
                       p.labels[t.preview_ix] AS label
                FROM target_ordered t
                CROSS JOIN preview_labels p
            ), raw AS MATERIALIZED (
                SELECT i.id,
                       i.ord,
                       i.tier,
                       NULLIF(types.labels[i.ix], '') AS type_label,
                       COALESCE(
                           NULLIF(names.labels[i.ix], ''),
                           NULLIF(sh.label, ''),
                           CASE
                               WHEN NULLIF(pr.label, '') IS NOT NULL THEN
                                   regexp_replace(pr.label, '[[:space:]]+', ' ', 'g') || '…'
                               ELSE NULL
                           END,
                           NULLIF(types.labels[i.ix], ''),
                           'unrealized entity') AS label
                FROM inp i
                CROSS JOIN names
                CROSS JOIN types
                LEFT JOIN shallow sh ON sh.id = i.id
                LEFT JOIN preview pr ON pr.root_id = i.id
            )
            SELECT encode(r.id, 'hex'),
                   CASE
                       WHEN char_length(r.label) > 48 THEN left(r.label, 47) || '…'
                       ELSE r.label
                   END,
                   r.tier,
                   r.type_label
            FROM raw r
            ORDER BY r.ord
            """,
            static r => new DisplayLabelRow(
                r.GetString(0),
                r.IsDBNull(1) ? "unrealized entity" : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt16(2),
                r.IsDBNull(3) ? null : r.GetString(3)),
            p =>
            {
                var parameter = p.AddWithValue("ids", ids);
                parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            },
            timeoutSeconds: 60,
            ct: ct,
            label: "display_labels",
            onError: onError);
    }
}
