using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Human-facing labels for bounded visualization/result sets.
///
/// Identity and display are deliberately separate: the caller keeps the canonical hash id,
/// while this read chooses a Unicode surface that a person can inspect. It never uses a
/// content hash as a label. The expensive case (tier-4+ content) is previewed from one
/// constituent, not recursively rendered in full, so a graph containing a book cannot turn
/// a 1,024-node label pass into a 1,024-document reconstruction pass.
/// </summary>
public static class NpgsqlDisplayLabels
{
    public readonly record struct DisplayLabelRow(string IdHex, string Label, short? Tier);

    /// <summary>
    /// Set-wise display-label policy, in order:
    /// canonical name; semantic name/lemma; exact shallow content; file name metadata;
    /// bounded document/definition preview; relation name; type/source description;
    /// explicit "Unrealized entity". The hash remains available separately as IdHex.
    /// </summary>
    public static Task<IReadOnlyList<DisplayLabelRow>> ReadAsync(
        NpgsqlConnection conn, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, """
            WITH inp AS MATERIALIZED (
                SELECT x.id, x.ord, e.tier, e.type_id, e.first_observed_by
                FROM unnest(@ids::bytea[]) WITH ORDINALITY AS x(id, ord)
                LEFT JOIN laplace.entities e ON e.id = x.id
            ),
            names AS MATERIALIZED (
                SELECT realize.resolve_name_batch(@ids::bytea[]) AS labels
            ),
            shallow_indexed AS MATERIALIZED (
                SELECT i.id, i.ord, i.id AS render_id,
                       row_number() OVER (ORDER BY i.ord)::integer AS rn
                FROM inp i
                WHERE i.tier <= 3
            ),
            shallow_batch AS MATERIALIZED (
                SELECT COALESCE(array_agg(s.render_id ORDER BY s.rn), '{}'::bytea[]) AS ids
                FROM shallow_indexed s
            ),
            shallow_rendered AS MATERIALIZED (
                SELECT b.ids, realize.render_text_batch(b.ids, 3) AS labels
                FROM shallow_batch b
            ),
            shallow AS MATERIALIZED (
                SELECT s.id, NULLIF(r.labels[s.rn], '') AS label
                FROM shallow_indexed s
                CROSS JOIN shallow_rendered r
            ),

            -- A file is an occurrence/provenance envelope around global content. When that
            -- envelope is present, its recorded file name is a better UI label than making
            -- the user inspect the content id. File metadata is a tiny known document, so
            -- depth 4 is exact without ever expanding the user's file body.
            file_meta_choice AS MATERIALIZED (
                SELECT DISTINCT ON (i.id)
                       i.id AS owner_id, i.ord, a.object_id AS metadata_id
                FROM inp i
                JOIN laplace.attestations a
                  ON a.subject_id = i.id
                 AND a.type_id = realize.canonical_id('substrate/type/HasFileMetadata/v1')
                WHERE a.object_id IS NOT NULL
                ORDER BY i.id, a.last_observed_at DESC, a.id
            ),
            file_meta_indexed AS MATERIALIZED (
                SELECT m.*,
                       row_number() OVER (ORDER BY m.ord)::integer AS rn
                FROM file_meta_choice m
            ),
            file_meta_batch AS MATERIALIZED (
                SELECT COALESCE(array_agg(m.metadata_id ORDER BY m.rn), '{}'::bytea[]) AS ids
                FROM file_meta_indexed m
            ),
            file_meta_rendered AS MATERIALIZED (
                SELECT b.ids, realize.render_text_batch(b.ids, 4) AS labels
                FROM file_meta_batch b
            ),
            file_meta_text AS MATERIALIZED (
                SELECT m.owner_id, r.labels[m.rn] AS txt
                FROM file_meta_indexed m
                CROSS JOIN file_meta_rendered r
            ),
            file_label AS MATERIALIZED (
                SELECT f.owner_id,
                       NULLIF(substr(lines.line, length('name=') + 1), '') AS label
                FROM file_meta_text f
                CROSS JOIN LATERAL unnest(
                    string_to_array(COALESCE(f.txt, ''), E'\n')) AS lines(line)
                WHERE lines.line LIKE 'name=%'
            ),

            -- Provider/catalog identities are not literal text and therefore correctly
            -- abstain in realize.resolve_name_batch. For a display-only fallback, however,
            -- an attested definition is useful. Choose one deterministic definition root;
            -- the preview arm below still renders only one bounded text constituent.
            definitions AS MATERIALIZED (
                SELECT DISTINCT ON (i.id)
                       i.id AS owner_id, a.object_id AS target_id
                FROM inp i
                JOIN laplace.attestations a
                  ON a.subject_id = i.id
                 AND a.type_id = laplace.relation_type_id('HAS_DEFINITION')
                WHERE a.object_id IS NOT NULL
                ORDER BY i.id, a.last_observed_at DESC, a.id
            ),
            preview_targets AS MATERIALIZED (
                SELECT i.id AS owner_id, i.ord,
                       CASE
                           WHEN i.tier > 3
                            AND i.type_id = laplace.entity_type_id('Document')
                               THEN i.id
                           ELSE d.target_id
                       END AS target_id
                FROM inp i
                LEFT JOIN definitions d ON d.owner_id = i.id
                WHERE (i.tier > 3 AND i.type_id = laplace.entity_type_id('Document'))
                   OR d.target_id IS NOT NULL
            ),
            preview_render_ids AS MATERIALIZED (
                SELECT p.owner_id, p.ord,
                       CASE
                           WHEN e.tier <= 3 THEN p.target_id
                           WHEN e.type_id = laplace.entity_type_id('Document') THEN head.child_id
                           ELSE NULL
                       END AS render_id
                FROM preview_targets p
                LEFT JOIN laplace.entities e ON e.id = p.target_id
                LEFT JOIN LATERAL (
                    SELECT u.entity_id AS child_id
                    FROM laplace.v_word_points w
                    CROSS JOIN LATERAL public.laplace_mantissa_unpack(
                        public.ST_PointN(w.trajectory, 1)) u
                    WHERE w.id = p.target_id
                      AND w.trajectory IS NOT NULL
                    ORDER BY w.physicality_id
                    LIMIT 1
                ) head ON e.tier > 3
                      AND e.type_id = laplace.entity_type_id('Document')
            ),
            preview_indexed AS MATERIALIZED (
                SELECT p.owner_id, p.ord, p.render_id,
                       row_number() OVER (ORDER BY p.ord)::integer AS rn
                FROM preview_render_ids p
                WHERE p.render_id IS NOT NULL
            ),
            preview_batch AS MATERIALIZED (
                SELECT COALESCE(array_agg(p.render_id ORDER BY p.rn), '{}'::bytea[]) AS ids
                FROM preview_indexed p
            ),
            preview_rendered AS MATERIALIZED (
                SELECT b.ids, realize.render_text_batch(b.ids, 3) AS labels
                FROM preview_batch b
            ),
            preview AS MATERIALIZED (
                SELECT p.owner_id,
                       NULLIF(regexp_replace(r.labels[p.rn], '[[:space:]]+', ' ', 'g'), '') AS label
                FROM preview_indexed p
                CROSS JOIN preview_rendered r
            ),

            -- Types/sources are tiny governed identities. Batch them once so a truly
            -- non-text modality still says what it IS and where it came from instead of
            -- presenting an arbitrary 128-bit handle as though that were a human name.
            type_indexed AS MATERIALIZED (
                SELECT d.type_id,
                       row_number() OVER (ORDER BY d.type_id)::integer AS rn
                FROM (SELECT DISTINCT i.type_id FROM inp i WHERE i.type_id IS NOT NULL) d
            ),
            type_batch AS MATERIALIZED (
                SELECT COALESCE(array_agg(t.type_id ORDER BY t.rn), '{}'::bytea[]) AS ids
                FROM type_indexed t
            ),
            type_rendered AS MATERIALIZED (
                SELECT b.ids, realize.label_batch(b.ids) AS labels
                FROM type_batch b
            ),
            type_label AS MATERIALIZED (
                SELECT t.type_id,
                       NULLIF(replace(r.labels[t.rn], '_', ' '), '') AS label
                FROM type_indexed t
                CROSS JOIN type_rendered r
            ),
            source_indexed AS MATERIALIZED (
                SELECT d.source_id,
                       row_number() OVER (ORDER BY d.source_id)::integer AS rn
                FROM (
                    SELECT DISTINCT i.first_observed_by AS source_id
                    FROM inp i
                    WHERE i.first_observed_by IS NOT NULL
                ) d
            ),
            source_batch AS MATERIALIZED (
                SELECT COALESCE(array_agg(s.source_id ORDER BY s.rn), '{}'::bytea[]) AS ids
                FROM source_indexed s
            ),
            source_rendered AS MATERIALIZED (
                SELECT b.ids, realize.label_batch(b.ids) AS labels
                FROM source_batch b
            ),
            source_label AS MATERIALIZED (
                SELECT s.source_id, NULLIF(r.labels[s.rn], '') AS label
                FROM source_indexed s
                CROSS JOIN source_rendered r
            )
            SELECT encode(i.id, 'hex'),
                   COALESCE(
                       NULLIF(canonical.name, ''),
                       NULLIF(names.labels[i.ord], ''),
                       shallow.label,
                       file_label.label,
                       preview.label,
                       NULLIF(consensus.relation_canonical(i.id), ''),
                       NULLIF(concat_ws(' · ', type_label.label, source_label.label), ''),
                       'Unrealized entity'),
                   i.tier
            FROM inp i
            CROSS JOIN names
            LEFT JOIN shallow ON shallow.id = i.id
            LEFT JOIN file_label ON file_label.owner_id = i.id
            LEFT JOIN preview ON preview.owner_id = i.id
            LEFT JOIN type_label ON type_label.type_id = i.type_id
            LEFT JOIN source_label ON source_label.source_id = i.first_observed_by
            LEFT JOIN laplace.canonical_names canonical ON canonical.id = i.id
            ORDER BY i.ord
            """,
            static r => new DisplayLabelRow(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt16(2)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            },
            timeoutSeconds: 30, ct: ct, label: "display_labels", onError: onError);
}
