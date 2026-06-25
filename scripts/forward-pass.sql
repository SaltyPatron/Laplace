-- Grounded forward-pass over the live consensus field — the trajectory_pairs (raw bigram) replacement.
-- Navigates the typed, Glicko-folded field by plane/band weighting, not a precomputed co-occurrence table.
-- Hot-swappable: a plain function over consensus — no extension rebuild, no reseed.
--
--   mode 'recall'   : MEANING-constrained. Only traverse rank>=0.5 edges (definitional/taxonomic/
--                     equivalence/partitive/causal), so the walk climbs the concept/sense ladder and
--                     stays there instead of descending into word-sequence scaffolding ("and the the").
--   mode 'generate' : sequential/syntactic planes allowed (no band floor), eff_mu picks the edge.
--
-- Tie-break is deterministic (object_id) so the walk is reproducible.
-- Renders each step via label() (follows HAS_NAME_ALIAS) → render_text() fallback. Intermediate ILI
-- concepts that lack a definition alias still render as their raw id (i90107) — that is the WS3
-- compositional-synset gap (synsets are string-addressed, not legible concept nodes), surfaced exactly.

CREATE OR REPLACE FUNCTION laplace.forward_pass(
    p_prompt text, p_steps int DEFAULT 12, p_mode text DEFAULT 'recall')
RETURNS text
LANGUAGE sql STABLE
SET search_path = laplace, public AS $$
WITH RECURSIVE seed AS (
    SELECT a.ids, a.ids[array_upper(a.ids, 1)] AS cur
    FROM (SELECT array_agg(id ORDER BY ord) AS ids
          FROM laplace.prompt_state(p_prompt) WHERE id IS NOT NULL) a
),
walk AS (
    SELECT cur, 0 AS step, ids AS path FROM seed
    UNION ALL
    SELECT n.obj, w.step + 1, w.path || n.obj
    FROM walk w
    CROSS JOIN LATERAL (
        SELECT c.object_id AS obj
        FROM laplace.consensus c
        WHERE c.subject_id = w.cur
          AND c.object_id IS NOT NULL
          AND NOT (c.object_id = ANY(w.path))
          AND NOT laplace.refuted(c.rating, c.rd)
          AND COALESCE(laplace.relation_rank_resolved(c.type_id), 0)
              >= CASE WHEN p_mode = 'generate' THEN 0.0 ELSE 0.5 END
        ORDER BY laplace.relation_rank_resolved(c.type_id) * (laplace.eff_mu(c.rating, c.rd) / 1e9) DESC,
                 c.object_id
        LIMIT 1
    ) n
    WHERE w.step < p_steps AND w.cur IS NOT NULL
)
SELECT string_agg(COALESCE(laplace.label(cur), laplace.render_text(cur, 16)), '  >  ' ORDER BY step)
FROM walk
$$;
