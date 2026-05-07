-- 0001_entity.sql — the entity table (Phase 2 / Track C1).
--
-- ONE substrate table. tier metadata + range partitioning, NOT separate
-- Atom / Composition / Relation tables (rejected). Tier 0 = Unicode codepoint
-- atoms (full 1,114,112 across all 17 planes — Phase 3 populates these).
-- Tier 1+ = compositions of lower-tier entities.
--
-- entity_hash is BLAKE3-256 of the entity's canonical content:
--   tier 0  : the codepoint's UTF-8 byte sequence
--   tier ≥1 : composition Merkle hash (children with RLE counts)
--
-- Knowledge IS edges + intersections: a single entity row is referenced
-- AS FEW TIMES AS PHYSICALLY POSSIBLE — RLE in entity_child, dedup at
-- every adjacency.

BEGIN;

CREATE TABLE IF NOT EXISTS entity (
    entity_hash      bytea       NOT NULL,
    tier             smallint    NOT NULL,
    -- For tier 0 atoms: the Unicode codepoint integer (0..1114111).
    -- NULL for tier >= 1 (composition entities have no scalar codepoint).
    codepoint        integer,
    -- Raw content bytes. For tier 0 this is the codepoint's UTF-8.
    -- For tier >= 1 this is empty (children are in entity_child).
    content          bytea       NOT NULL DEFAULT '',
    -- BLAKE3 of the canonical decomposition (Merkle for tier >= 1; identical
    -- to entity_hash by construction — denormalized here for index-only
    -- verification scans).
    canonical_hash   bytea       NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (entity_hash, tier)
)
PARTITION BY RANGE (tier);

CREATE TABLE IF NOT EXISTS entity_tier0 PARTITION OF entity
    FOR VALUES FROM (0) TO (1);

CREATE TABLE IF NOT EXISTS entity_tier1 PARTITION OF entity
    FOR VALUES FROM (1) TO (2);

CREATE TABLE IF NOT EXISTS entity_tier2 PARTITION OF entity
    FOR VALUES FROM (2) TO (3);

CREATE TABLE IF NOT EXISTS entity_tier3 PARTITION OF entity
    FOR VALUES FROM (3) TO (4);

CREATE TABLE IF NOT EXISTS entity_tier4 PARTITION OF entity
    FOR VALUES FROM (4) TO (5);

CREATE TABLE IF NOT EXISTS entity_tier_higher PARTITION OF entity
    FOR VALUES FROM (5) TO (32767);

-- Composition children with run-length encoding.
-- One row per (parent, position) where rle_count is the number of identical
-- consecutive children at that position. Same content adjacent ⇒ ONE row,
-- never multiple. Position is the start position of the run, not the index.
CREATE TABLE IF NOT EXISTS entity_child (
    parent_hash      bytea       NOT NULL,
    parent_tier      smallint    NOT NULL,
    position         integer     NOT NULL,
    child_hash       bytea       NOT NULL,
    child_tier       smallint    NOT NULL,
    rle_count        integer     NOT NULL CHECK (rle_count > 0),
    PRIMARY KEY (parent_hash, parent_tier, position),
    FOREIGN KEY (parent_hash, parent_tier) REFERENCES entity (entity_hash, tier),
    FOREIGN KEY (child_hash,  child_tier)  REFERENCES entity (entity_hash, tier)
);

CREATE INDEX IF NOT EXISTS entity_child_by_child
    ON entity_child (child_hash, child_tier);

-- Tier 0 fast lookup by codepoint (the universal codepoint atom pool).
CREATE UNIQUE INDEX IF NOT EXISTS entity_tier0_by_codepoint
    ON entity_tier0 (codepoint)
    WHERE codepoint IS NOT NULL;

COMMIT;
