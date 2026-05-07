-- 0006_sequence.sql — ordered sequences inside compositions (Phase 2 / Track C1).
--
-- entity_child handles RLE'd composition order. sequence handles the cases
-- where a composition needs an ordered sequence of children that is not
-- amenable to RLE (e.g., a 1024-token text passage whose surface form is a
-- composition of word entities in reading order with NO adjacent repeats
-- compressible — RLE would still produce N rows, but sequence labels each
-- position explicitly without the rle_count overhead).
--
-- Use sequence when:
--   - the position-to-child mapping must be queried directly by position
--   - the composition is a logical sequence (e.g., a sentence) where
--     position-1 referencing matters more than RLE compression
--
-- Use entity_child for everything else (the default).

BEGIN;

CREATE TABLE IF NOT EXISTS sequence (
    parent_hash    bytea     NOT NULL,
    parent_tier    smallint  NOT NULL,
    position       integer   NOT NULL,
    child_hash     bytea     NOT NULL,
    child_tier     smallint  NOT NULL,
    PRIMARY KEY (parent_hash, parent_tier, position),
    FOREIGN KEY (parent_hash, parent_tier) REFERENCES entity (entity_hash, tier),
    FOREIGN KEY (child_hash,  child_tier)  REFERENCES entity (entity_hash, tier)
);

CREATE INDEX IF NOT EXISTS sequence_by_child
    ON sequence (child_hash, child_tier);

COMMIT;
