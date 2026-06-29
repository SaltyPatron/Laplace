\set ON_ERROR_STOP on
SET search_path = laplace, public;

CREATE OR REPLACE FUNCTION laplace.define(p_word bytea, p_limit int DEFAULT 5)
    RETURNS TABLE(definition text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = laplace, public AS $$
    SELECT d.definition, d.eff_mu, d.witnesses
    FROM (
        SELECT render_text(g.object_id, 24) AS definition,
               eff_mu_display(g.rating, g.rd) AS eff_mu,
               g.witness_count AS witnesses,
               sn.eff_mu AS sense_rank
        FROM laplace.senses(p_word) sn
        JOIN laplace.consensus g ON g.subject_id = sn.synset_id
                        AND g.type_id = laplace.relation_type_id('HAS_DEFINITION')
        UNION ALL
        SELECT render_text(g.object_id, 24),
               eff_mu_display(g.rating, g.rd),
               g.witness_count,
               NULL::numeric
        FROM laplace.consensus g
        WHERE g.subject_id = p_word
          AND g.type_id = laplace.relation_type_id('HAS_DEFINITION')
    ) d
    ORDER BY COALESCE(d.sense_rank, 0) + d.eff_mu DESC
    LIMIT p_limit
$$;

CREATE OR REPLACE FUNCTION laplace.define(p_word bytea, p_context bytea[], p_limit int DEFAULT 5)
    RETURNS TABLE(definition text, eff_mu numeric, witnesses bigint)
    LANGUAGE sql STABLE PARALLEL SAFE
    SET search_path = laplace, public AS $$
    SELECT d.definition, d.eff_mu, d.witnesses
    FROM (
        SELECT render_text(g.object_id, 24) AS definition,
               eff_mu_display(g.rating, g.rd) AS eff_mu,
               g.witness_count AS witnesses,
               sn.score AS sense_rank
        FROM laplace.senses(p_word, p_context) sn
        JOIN laplace.consensus g ON g.subject_id = sn.synset_id
                        AND g.type_id = laplace.relation_type_id('HAS_DEFINITION')
        UNION ALL
        SELECT render_text(g.object_id, 24),
               eff_mu_display(g.rating, g.rd),
               g.witness_count,
               NULL::numeric
        FROM laplace.consensus g
        WHERE g.subject_id = p_word
          AND g.type_id = laplace.relation_type_id('HAS_DEFINITION')
    ) d
    ORDER BY COALESCE(d.sense_rank, 0) + d.eff_mu DESC
    LIMIT p_limit
$$;
