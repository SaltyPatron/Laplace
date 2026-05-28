-- Migration 20260528060000_attestation_response_read_primitive
--
-- The substrate had no read primitive for "what does subject X attest about
-- kind Y under source scope S, top K". Without this, no query surface =
-- substrate has data but isn't queryable. Cascade A* SRF (the compiled C
-- version per ADR 0035) needs this as its inner one-hop step regardless.
--
-- This migration adds laplace.attestation_response — pure SQL set-returning
-- function. Not a recursive CTE / RBAR / cursor (per RULES R19). One indexed
-- lookup + aggregate + sort. Cross-source consensus via SUM of per-row
-- effective-mu (= max(0, (rating − 2·rd) / 1e9), the 95% lower bound per
-- ADR 0036). Higher-fidelity proper-Glicko-2-period-update aggregation per
-- ADR 0036 + ADR 0044 lands when the cascade C SRF replaces this.

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
    max_rating_fp1e9 bigint,
    min_rd_fp1e9     bigint
)
LANGUAGE sql STABLE STRICT PARALLEL SAFE
AS $$
    SELECT
        a.object_id,
        SUM(GREATEST(0.0, (a.rating - 2.0 * a.rd) / 1e9))::double precision  AS combined_eff_mu,
        COUNT(*)::integer                                                     AS source_count,
        MAX(a.rating)                                                         AS max_rating_fp1e9,
        MIN(a.rd)                                                             AS min_rd_fp1e9
    FROM laplace.attestations a
    WHERE a.subject_id = p_subject_id
      AND a.kind_id    = p_kind_id
      AND a.object_id IS NOT NULL
      AND (p_source_scope IS NULL OR a.source_id = ANY(p_source_scope))
      AND (a.context_id IS NOT DISTINCT FROM p_context_id)
    GROUP BY a.object_id
    ORDER BY combined_eff_mu DESC
    LIMIT GREATEST(1, p_top_k);
$$;

COMMENT ON FUNCTION laplace.attestation_response IS
    'One-hop typed-edge expansion. Returns the top-K objects by combined effective-mu '
    '(sum of max(0, rating − 2·RD) per row, across sources matching p_source_scope) for '
    '(subject, kind) under p_context_id. Pre-cascade read primitive — the cascade A* SRF '
    'per ADR 0035 will call this as its inner per-hop step once landed. NULL p_source_scope '
    'means all sources. NULL p_context_id matches rows whose context_id IS NULL (uses '
    'IS NOT DISTINCT FROM for SQL-NULL-aware equality).';

-- Companion: unary-attestation lookup (object_id IS NULL — for kinds like
-- EMBEDS / V_PROJECTS / O_PROJECTS that emit per-subject scalars per ADR 0056).
CREATE OR REPLACE FUNCTION laplace.attestation_unary_response(
    p_subject_id   bytea,
    p_kind_id      bytea,
    p_source_scope bytea[]    DEFAULT NULL
)
RETURNS TABLE(
    combined_eff_mu  double precision,
    source_count     integer,
    max_rating_fp1e9 bigint,
    min_rd_fp1e9     bigint
)
LANGUAGE sql STABLE STRICT PARALLEL SAFE
AS $$
    SELECT
        SUM(GREATEST(0.0, (a.rating - 2.0 * a.rd) / 1e9))::double precision  AS combined_eff_mu,
        COUNT(*)::integer                                                     AS source_count,
        COALESCE(MAX(a.rating), 0)                                            AS max_rating_fp1e9,
        COALESCE(MIN(a.rd), 0)                                                AS min_rd_fp1e9
    FROM laplace.attestations a
    WHERE a.subject_id = p_subject_id
      AND a.kind_id    = p_kind_id
      AND a.object_id IS NULL
      AND (p_source_scope IS NULL OR a.source_id = ANY(p_source_scope));
$$;

COMMENT ON FUNCTION laplace.attestation_unary_response IS
    'Unary-kind subject lookup. For kinds whose attestations have object_id IS NULL '
    '(EMBEDS / V_PROJECTS / O_PROJECTS / GATES / UP_PROJECTS / DOWN_PROJECTS / '
    'OUTPUT_PROJECTS / NORMALIZES per ADR 0056). Returns combined effective-mu + source '
    'count for (subject, kind) under p_source_scope. NULL scope = all sources.';

GRANT EXECUTE ON FUNCTION laplace.attestation_response   TO PUBLIC;
GRANT EXECUTE ON FUNCTION laplace.attestation_unary_response TO PUBLIC;
