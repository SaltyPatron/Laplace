-- Story D.3 / #250 / Framework Epic #232 — entities_exist_bitmap SRF.
--
-- Verifies the SubstrateCRUD batch existence SRF returns a packed bitmap
-- matching the convention consumed by Laplace.Engine.Core.MerkleDedup
-- (LSB-first within each byte).

CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

SET search_path TO laplace, public;

-- =====================================================================
-- Setup — synthetic test entities with known IDs (16 raw bytes each).
-- =====================================================================

-- A 16-byte hash with byte i = fill_byte for all positions.
CREATE TEMP TABLE test_fixtures AS
SELECT
    decode(lpad(to_hex(b), 2, '0') || repeat(lpad(to_hex(b), 2, '0'), 15), 'hex') AS id
FROM generate_series(0, 7) b;

-- Seed the entities table with the first 4 fixtures (bytes 0..3).
-- type_id reuses fixture 0 to satisfy the FK constraint (fixture 0 references itself).
INSERT INTO entities (id, tier, type_id, first_observed_by)
SELECT id, 0::smallint, (SELECT id FROM test_fixtures LIMIT 1), NULL
FROM test_fixtures
WHERE id IN (
    SELECT id FROM test_fixtures LIMIT 4
)
ON CONFLICT (id) DO NOTHING;

-- =====================================================================
-- Test 1: All-absent → all bits clear.
-- Candidate: fixtures 4,5,6,7 (none in entities).
-- Expected bitmap: 1 byte = 0x00.
-- =====================================================================

SELECT encode(entities_exist_bitmap(ARRAY(
    SELECT id FROM test_fixtures WHERE id IN (
        SELECT id FROM test_fixtures OFFSET 4 LIMIT 4
    )
)::bytea[]), 'hex') AS all_absent;

-- =====================================================================
-- Test 2: All-present → all bits set.
-- Candidate: fixtures 0,1,2,3 (all in entities).
-- Expected bitmap: 1 byte = 0x0f (low 4 bits set).
-- =====================================================================

SELECT encode(entities_exist_bitmap(ARRAY(
    SELECT id FROM test_fixtures WHERE id IN (
        SELECT id FROM test_fixtures LIMIT 4
    )
)::bytea[]), 'hex') AS all_present;

-- =====================================================================
-- Test 3: Mixed — fixtures [0, 4, 1, 5, 2, 6, 3, 7].
-- Present: positions 0, 2, 4, 6 (the even positions in the candidate list).
-- Bitmap: bits 0, 2, 4, 6 set → byte 0x55.
-- =====================================================================

WITH ordered_input AS (
    SELECT id, ord FROM (VALUES
        ((SELECT id FROM test_fixtures OFFSET 0 LIMIT 1), 0),
        ((SELECT id FROM test_fixtures OFFSET 4 LIMIT 1), 1),
        ((SELECT id FROM test_fixtures OFFSET 1 LIMIT 1), 2),
        ((SELECT id FROM test_fixtures OFFSET 5 LIMIT 1), 3),
        ((SELECT id FROM test_fixtures OFFSET 2 LIMIT 1), 4),
        ((SELECT id FROM test_fixtures OFFSET 6 LIMIT 1), 5),
        ((SELECT id FROM test_fixtures OFFSET 3 LIMIT 1), 6),
        ((SELECT id FROM test_fixtures OFFSET 7 LIMIT 1), 7)
    ) v(id, ord)
)
SELECT encode(entities_exist_bitmap(
    ARRAY(SELECT id FROM ordered_input ORDER BY ord)::bytea[]
), 'hex') AS mixed;

-- =====================================================================
-- Test 4: Empty input → empty bytea.
-- =====================================================================

SELECT octet_length(entities_exist_bitmap(ARRAY[]::bytea[])) AS empty_len;

-- =====================================================================
-- Test 5: Idempotent — calling twice returns the same bitmap.
-- =====================================================================

SELECT
    encode(entities_exist_bitmap(ARRAY(
        SELECT id FROM test_fixtures LIMIT 4
    )::bytea[]), 'hex') =
    encode(entities_exist_bitmap(ARRAY(
        SELECT id FROM test_fixtures LIMIT 4
    )::bytea[]), 'hex') AS idempotent;

-- =====================================================================
-- Cleanup
-- =====================================================================
DELETE FROM entities WHERE id IN (SELECT id FROM test_fixtures);
DROP TABLE test_fixtures;
