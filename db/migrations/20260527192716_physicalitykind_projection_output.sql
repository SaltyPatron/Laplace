-- Migration 20260527192716_physicalitykind_projection_output
--
-- Add PhysicalityKind.ProjectionOutput = 4 to the substrate-canonical kind
-- enumeration. lm_head's output-direction representation needs a distinct
-- physicality kind so it can coexist with embed's input-direction kind=3
-- PROJECTION row on the same (entity, source) tuple under
-- UNIQUE(entity_id, source_id, kind).
--
-- No DDL change — physicalities.kind is smallint and accepts 4 already.
-- This migration is a comment refresh + an explicit log marker so DbUp
-- records the schema-level convention shift.

DO $$
BEGIN
    -- Refresh the table + column comments to enumerate kind=4.
    -- The 03_physicalities.sql.in source-of-truth already carries the new
    -- comments; this migration ensures live databases match.
    COMMENT ON TABLE laplace.physicalities IS
        'Per-source per-kind 4D representations. One-to-many entity -> physicality. Same entity may have many physicalities (one per source x per kind). kind axis: 1=CONTENT (decomposition view; trajectory populated), 2=BUILDING_BLOCK (used-as-constituent view), 3=PROJECTION (source-embedding-space input-direction view via Procrustes alignment), 4=PROJECTION_OUTPUT (source-embedding-space output-direction view, e.g. lm_head; allows untied input/output embeddings to coexist on the same entity under UNIQUE(entity_id, source_id, kind)).';

    COMMENT ON COLUMN laplace.physicalities.kind IS
        'Physicality kind: 1=CONTENT, 2=BUILDING_BLOCK, 3=PROJECTION (input-direction), 4=PROJECTION_OUTPUT (output-direction; lm_head). Extensible. Values mirror substrate-canonical PhysicalityKind entities (bootstrapped per ADR 0042 Stage 2).';
END$$;
