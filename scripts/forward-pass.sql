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
