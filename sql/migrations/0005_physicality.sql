-- 0005_physicality.sql — physical-position partition (Phase 2 / Track C1).
--
-- Open-vocabulary physicality types. Each physicality_type_hash is itself
-- a substrate entity (composition of its name's codepoint LINESTRING).
-- Examples (all defined as substrate entities, NOT enum values):
--   "substrate codepoint S3 position" — tier-0 atom positions (deterministic
--                                       super-Fibonacci on S³)
--   "AI model firefly S3 position"    — per-(token × model) firefly clouds
--   "composition centroid S3 position"— derived 4-ball centroids
--   per-modality positions (audio waveform / spectral / image patch /
--                           protein backbone / etc.)
--
-- Hash partitioning by physicality_type_hash means substrate atoms live in
-- a different physical partition from AI model fireflies — substrate-only
-- queries never touch firefly partitions; consensus queries scan only the
-- firefly partition. No conflation between substrate primitives and model
-- opinions.
--
-- position is stored as raw 32 bytes = 4 little-endian doubles (x, y, z, w)
-- for now. Once the GEOMETRY4D PostgreSQL type registers (Track B5/B6
-- finalization), position is migrated to GEOMETRY4D POINT4D via ALTER.

BEGIN;

CREATE TABLE IF NOT EXISTS physicality (
    physicality_type_hash bytea     NOT NULL,
    entity_hash           bytea     NOT NULL,
    entity_tier           smallint  NOT NULL,
    -- 4 doubles, little-endian (x, y, z, w). Migrated to POINT4D later.
    position              bytea     NOT NULL CHECK (octet_length(position) = 32),
    -- Cached 64-bit Hilbert index for B-tree-based locality probes (computed
    -- by a managed insert path that calls laplace_hilbert_point4d_to_index).
    hilbert_index         bigint    NOT NULL DEFAULT 0,
    created_at            timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (physicality_type_hash, entity_hash, entity_tier)
)
PARTITION BY HASH (physicality_type_hash);

DO $$
DECLARE i int;
BEGIN
    FOR i IN 0..7 LOOP
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS physicality_p%s PARTITION OF physicality '
            'FOR VALUES WITH (modulus 8, remainder %s)', i, i);
    END LOOP;
END$$;

CREATE INDEX IF NOT EXISTS physicality_by_entity
    ON physicality (entity_hash, entity_tier);

CREATE INDEX IF NOT EXISTS physicality_by_hilbert
    ON physicality (physicality_type_hash, hilbert_index);

COMMIT;
