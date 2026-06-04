-- Migration 20260603230000_signed_effective_mu_reads
--
-- Read primitives over the CONSENSUS layer. Effective μ is SIGNED — never
-- clamped (the sign IS the truth-state; refuted ranks at the bottom).
--
-- EVIDENCE IS PROVENANCE: evidence rows carry no values (outcome class only),
-- so there is NO evidence-replay accumulation. A source/context SCOPE is a
-- PROVENANCE FILTER — "objects this scope witnessed" — while relation STRENGTH
-- is always the global consensus μ. (The earlier scoped re-accumulation from
-- stored evidence scores is gone with the value columns.)

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
LANGUAGE sql STABLE
SET search_path = laplace, public
AS $$
    SELECT c.object_id,
           ((c.rating - 2.0 * c.rd) / 1e9)::double precision,
           c.witness_count::integer,
           c.rating,
           c.rd
    FROM laplace.consensus c
    WHERE c.subject_id = p_subject_id
      AND c.kind_id    = p_kind_id
      AND c.object_id IS NOT NULL
      AND (
            (p_source_scope IS NULL AND p_context_id IS NULL)
         OR EXISTS (             -- provenance filter: the scope witnessed it
                SELECT 1 FROM laplace.attestations a
                WHERE a.subject_id = c.subject_id
                  AND a.kind_id    = c.kind_id
                  AND a.object_id IS NOT DISTINCT FROM c.object_id
                  AND (p_source_scope IS NULL OR a.source_id = ANY(p_source_scope))
                  AND (p_context_id   IS NULL OR a.context_id IS NOT DISTINCT FROM p_context_id)
            )
          )
    ORDER BY (c.rating - 2.0 * c.rd) DESC
    LIMIT GREATEST(1, p_top_k)
$$;

COMMENT ON FUNCTION laplace.attestation_response IS
    'One-hop typed-edge expansion: top-K objects by SIGNED effective μ (rating − 2·RD, never '
    'clamped — refuted ranks at the bottom) for (subject, kind). source_scope/context are '
    'PROVENANCE FILTERS (evidence = who witnessed); strength is always the global consensus. '
    'Pre-cascade read primitive.';

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
LANGUAGE sql STABLE
SET search_path = laplace, public
AS $$
    SELECT ((c.rating - 2.0 * c.rd) / 1e9)::double precision,
           c.witness_count::integer,
           c.rating,
           c.rd
    FROM laplace.consensus c
    WHERE c.subject_id = p_subject_id
      AND c.kind_id    = p_kind_id
      AND c.object_id IS NULL
      AND (
            (p_source_scope IS NULL AND p_context_id IS NULL)
         OR EXISTS (
                SELECT 1 FROM laplace.attestations a
                WHERE a.subject_id = c.subject_id
                  AND a.kind_id    = c.kind_id
                  AND a.object_id IS NULL
                  AND (p_source_scope IS NULL OR a.source_id = ANY(p_source_scope))
                  AND (p_context_id   IS NULL OR a.context_id IS NOT DISTINCT FROM p_context_id)
            )
          )
$$;

COMMENT ON FUNCTION laplace.attestation_unary_response IS
    'Unary-kind subject lookup (object_id IS NULL). SIGNED effective μ (never clamped) from '
    'consensus; source_scope/context are provenance filters over the evidence.';

GRANT EXECUTE ON FUNCTION laplace.attestation_response       TO PUBLIC;
GRANT EXECUTE ON FUNCTION laplace.attestation_unary_response TO PUBLIC;
