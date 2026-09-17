using System.Text;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public static async Task<string?> ForwardCodeAsync(
        NpgsqlDataSource dataSource,
        string prompt,
        int steps,
        int maxStride,
        double spread,
        int topK,
        int frontierLimit,
        IReadOnlyList<byte[]> feedbackRoots,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            WITH prompt_ids AS MATERIALIZED (
                SELECT DISTINCT p.id FROM converse.prompt_words($1) p WHERE p.id IS NOT NULL
            ), code_relations AS MATERIALIZED (
                SELECT ARRAY[
                    laplace.relation_type_id('DEFINES'),
                    laplace.relation_type_id('CALLS'),
                    laplace.relation_type_id('REFERENCES')
                ]::bytea[] AS ids
            ), matched AS MATERIALIZED (
                SELECT a.context_id AS root_id,
                       count(DISTINCT a.type_id)::integer AS relation_kinds,
                       sum(a.observation_count)::bigint AS witnesses,
                       sum(a.sum_score_fp1e9)::numeric AS evidence_mass
                FROM laplace.attestations a
                CROSS JOIN code_relations r
                WHERE a.type_id = ANY(r.ids)
                  AND a.object_id = ANY(ARRAY(SELECT p.id FROM prompt_ids p))
                  AND a.context_id IS NOT NULL
                GROUP BY a.context_id
            ), verification AS MATERIALIZED (
                SELECT c.subject_id AS root_id,
                       max(consensus.eff_mu(c.rating, c.rd)) AS eff_mu,
                       sum(c.witness_count)::bigint AS witnesses
                FROM laplace.consensus c
                WHERE c.type_id = laplace.relation_type_id('HAS_RESULT')
                  AND c.subject_id = ANY(ARRAY(SELECT m.root_id FROM matched m))
                GROUP BY c.subject_id
            ), ast_ranked AS MATERIALIZED (
                SELECT m.root_id,
                       row_number() OVER (
                         ORDER BY COALESCE(v.eff_mu, consensus.glicko2_neutral_mu()) DESC,
                                  m.relation_kinds DESC, m.evidence_mass DESC,
                                  m.witnesses DESC, m.root_id) AS ord
                FROM matched m
                LEFT JOIN verification v USING (root_id)
                ORDER BY COALESCE(v.eff_mu, consensus.glicko2_neutral_mu()) DESC,
                         m.relation_kinds DESC, m.evidence_mass DESC,
                         m.witnesses DESC, m.root_id
                LIMIT $6
            ), model_candidates AS MATERIALIZED (
                -- COMPLETES_TO is the model-ingestion continuation testimony. This is
                -- the pooled consensus read: incompatible checkpoint witnesses have
                -- already folded into one standing before proposal selection. No
                -- runtime ensemble, answer vote, or tensor arithmetic is introduced.
                SELECT ar.object AS id,
                       max(ar.eff_mu) AS eff_mu,
                       min(ar.rd) AS rd,
                       sum(ar.witness_count)::bigint AS witnesses
                FROM prompt_ids p
                CROSS JOIN LATERAL generation.adjudicated_row(
                    p.id, GREATEST(8, $5 * 2), 'COMPLETES_TO') ar
                WHERE ar.object IS NOT NULL
                GROUP BY ar.object
            ), model_ranked AS MATERIALIZED (
                SELECT m.id,
                       row_number() OVER (
                         ORDER BY m.eff_mu DESC, m.rd, m.witnesses DESC, m.id) AS ord
                FROM model_candidates m
                ORDER BY m.eff_mu DESC, m.rd, m.witnesses DESC, m.id
                LIMIT $6
            ), feedback AS MATERIALIZED (
                SELECT f.id, f.ord
                FROM unnest($7::bytea[]) WITH ORDINALITY AS f(id, ord)
                WHERE f.id IS NOT NULL
            ), frontier_rows AS MATERIALIZED (
                -- WITNESS feedback has highest priority, then pooled model continuation
                -- evidence, then exact Tree-sitter code roots. The canonical forward
                -- program receives one combined frontier and owns the actual election.
                SELECT f.id, 0 AS branch, f.ord::bigint AS ord FROM feedback f
                UNION ALL
                SELECT m.id, 1, m.ord FROM model_ranked m
                UNION ALL
                SELECT r.root_id, 2, r.ord FROM ast_ranked r
            ), frontier AS MATERIALIZED (
                SELECT array_agg(x.id ORDER BY x.branch, x.ord, x.id) AS ids
                FROM (
                    SELECT DISTINCT ON (fr.id) fr.id, fr.branch, fr.ord
                    FROM frontier_rows fr
                    ORDER BY fr.id, fr.branch, fr.ord
                ) x
            )
            SELECT g.step, g.entity, g.stride_used
            FROM frontier f
            CROSS JOIN LATERAL generation.forward_text(
                $1, $2, $3, $4, $5, NULL::bigint, 4, 16, f.ids) g
            WHERE COALESCE(cardinality(f.ids), 0) > 0
            ORDER BY g.step
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, prompt);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, steps);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, maxStride);
        command.Parameters.AddWithValue(NpgsqlDbType.Double, spread);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, topK);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, Math.Max(1, frontierLimit));
        command.Parameters.AddWithValue(
            NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            feedbackRoots.Count == 0 ? Array.Empty<byte[]>() : feedbackRoots.ToArray());

        var text = new StringBuilder();
        bool hasRows = false;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            hasRows = true;
            if (!reader.IsDBNull(1)) text.Append(reader.GetString(1));
        }
        return hasRows ? text.ToString() : null;
    }
}
