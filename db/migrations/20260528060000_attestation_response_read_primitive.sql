-- Migration 20260528060000_attestation_response_read_primitive
--
-- One-hop typed-edge read primitive: "what does subject X attest about kind Y
-- (optionally scoped to sources/context), top K". Cascade A* SRF (ADR 0035) calls
-- this as its inner per-hop step.
--
-- EVIDENCE vs CONSENSUS (ARCHITECTURE.md §10): source AND context (model
-- layer/head) live in the EVIDENCE layer (attestations); the CONSENSUS layer
-- drops both from identity (one row per subject/kind/object). So this primitive
-- is two-mode:
--   • all-source, all-context  → read the materialized `consensus` directly
--     (a sorted index scan over consensus_rating_btree — μs, no re-aggregation).
--   • source-/context-scoped   → accumulate Glicko-2 over the EVIDENCE rows in
--     that scope, with the SAME neutral prior + neutral opponent + per-witness
--     opponent_rd(trust→φ) + stored tanh score that rebuild_consensus uses,
--     replaying observation_count games (order-invariant within a period).
-- Effective μ = max(0, (rating − 2·rd)/1e9) — the 95% lower bound (ADR 0036).

CREATE OR REPLACE FUNCTION laplace.attestation_response(
    p_subject_id   bytea,
    p_kind_id      bytea,
    p_source_scope bytea[]    DEFAULT NULL,
    p_context_id   bytea      DEFAULT NULL,
    p_top_k        integer    DEFAULT 32
)
RETURNS TABLE(
    object_id        bytea,
    combined_eff_mu  double precision,
    source_count     integer,
    rating_fp1e9     bigint,
    rd_fp1e9         bigint
)
LANGUAGE plpgsql STABLE
SET search_path = laplace, public
AS $$
BEGIN
    IF p_source_scope IS NULL AND p_context_id IS NULL THEN
        -- Cross-source / cross-context: the materialized consensus IS the answer.
        RETURN QUERY
            SELECT c.object_id,
                   GREATEST(0.0, (c.rating - 2.0 * c.rd) / 1e9)::double precision,
                   c.witness_count::integer,
                   c.rating,
                   c.rd
            FROM laplace.consensus c
            WHERE c.subject_id = p_subject_id
              AND c.kind_id    = p_kind_id
              AND c.object_id IS NOT NULL
            ORDER BY (c.rating - 2.0 * c.rd) DESC
            LIMIT GREATEST(1, p_top_k);
    ELSE
        -- Scoped: per-(source-set, context) consensus accumulated on the fly from
        -- evidence (source + context live here). Same kernel as rebuild_consensus.
        RETURN QUERY
            SELECT g.object_id,
                   GREATEST(0.0, ((g.acc).rating - 2.0 * (g.acc).rd) / 1e9)::double precision,
                   g.cnt::integer,
                   (g.acc).rating,
                   (g.acc).rd
            FROM (
                SELECT a.object_id,
                       SUM(a.observation_count) AS cnt,
                       laplace_glicko2_accumulate(
                           1500000000000::bigint, 350000000000::bigint, 60000000::bigint,  -- neutral prior
                           1500000000000::bigint,                                           -- neutral opponent μ
                           a.opponent_rd,                                                   -- witness φ (trust)
                           a.score,                                                         -- stored tanh outcome
                           500000000::bigint                                                -- tau = 0.5
                       ) AS acc
                FROM laplace.attestations a
                CROSS JOIN LATERAL generate_series(1, a.observation_count) AS occ
                WHERE a.subject_id = p_subject_id
                  AND a.kind_id    = p_kind_id
                  AND a.object_id IS NOT NULL
                  AND (p_source_scope IS NULL OR a.source_id = ANY(p_source_scope))
                  AND (p_context_id   IS NULL OR a.context_id IS NOT DISTINCT FROM p_context_id)
                GROUP BY a.object_id
            ) g
            ORDER BY ((g.acc).rating - 2.0 * (g.acc).rd) DESC
            LIMIT GREATEST(1, p_top_k);
    END IF;
END;
$$;

COMMENT ON FUNCTION laplace.attestation_response IS
    'One-hop typed-edge expansion: top-K objects by effective μ (max(0, rating − 2·RD)) '
    'for (subject, kind). NULL source_scope AND NULL context → reads the materialized '
    'consensus (cross-source/context). Either set → accumulates Glicko-2 over the evidence '
    'in that scope (source + context live in evidence). Pre-cascade read primitive (ADR 0035).';

-- Companion: unary-attestation lookup (object_id IS NULL — for kinds like
-- EMBEDS / V_PROJECTS / O_PROJECTS that emit per-subject scalars per ADR 0056).
CREATE OR REPLACE FUNCTION laplace.attestation_unary_response(
    p_subject_id   bytea,
    p_kind_id      bytea,
    p_source_scope bytea[]    DEFAULT NULL,
    p_context_id   bytea      DEFAULT NULL
)
RETURNS TABLE(
    combined_eff_mu  double precision,
    source_count     integer,
    rating_fp1e9     bigint,
    rd_fp1e9         bigint
)
LANGUAGE plpgsql STABLE
SET search_path = laplace, public
AS $$
BEGIN
    IF p_source_scope IS NULL AND p_context_id IS NULL THEN
        RETURN QUERY
            SELECT GREATEST(0.0, (c.rating - 2.0 * c.rd) / 1e9)::double precision,
                   c.witness_count::integer,
                   c.rating,
                   c.rd
            FROM laplace.consensus c
            WHERE c.subject_id = p_subject_id
              AND c.kind_id    = p_kind_id
              AND c.object_id IS NULL;
    ELSE
        RETURN QUERY
            SELECT GREATEST(0.0, ((g.acc).rating - 2.0 * (g.acc).rd) / 1e9)::double precision,
                   g.cnt::integer,
                   (g.acc).rating,
                   (g.acc).rd
            FROM (
                SELECT SUM(a.observation_count) AS cnt,
                       laplace_glicko2_accumulate(
                           1500000000000::bigint, 350000000000::bigint, 60000000::bigint,
                           1500000000000::bigint, a.opponent_rd, a.score, 500000000::bigint
                       ) AS acc
                FROM laplace.attestations a
                CROSS JOIN LATERAL generate_series(1, a.observation_count) AS occ
                WHERE a.subject_id = p_subject_id
                  AND a.kind_id    = p_kind_id
                  AND a.object_id IS NULL
                  AND (p_source_scope IS NULL OR a.source_id = ANY(p_source_scope))
                  AND (p_context_id   IS NULL OR a.context_id IS NOT DISTINCT FROM p_context_id)
            ) g;
    END IF;
END;
$$;

COMMENT ON FUNCTION laplace.attestation_unary_response IS
    'Unary-kind subject lookup (object_id IS NULL — EMBEDS / *_PROJECTS / GATES / NORMALIZES '
    'per ADR 0056). NULL scope+context → consensus; scoped → Glicko-2 over the evidence in '
    'that scope. Effective μ + witness count for (subject, kind).';

GRANT EXECUTE ON FUNCTION laplace.attestation_response       TO PUBLIC;
GRANT EXECUTE ON FUNCTION laplace.attestation_unary_response TO PUBLIC;
