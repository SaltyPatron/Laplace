CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

CREATE TEMP TABLE test_fixtures AS
SELECT
    decode(lpad(to_hex(b), 2, '0') || repeat(lpad(to_hex(b), 2, '0'), 15), 'hex') AS id
FROM generate_series(0, 7) b;

INSERT INTO laplace.entities (id, tier, type_id, first_observed_by)
SELECT id, 0::smallint, (SELECT id FROM test_fixtures LIMIT 1), NULL
FROM test_fixtures
WHERE id IN (
    SELECT id FROM test_fixtures LIMIT 4
)
ON CONFLICT (id, tier) DO NOTHING;

SELECT encode(laplace.entities_exist_bitmap(ARRAY(
    SELECT id FROM test_fixtures WHERE id IN (
        SELECT id FROM test_fixtures OFFSET 4 LIMIT 4
    )
)::bytea[]), 'hex') AS all_absent;

SELECT encode(laplace.entities_exist_bitmap(ARRAY(
    SELECT id FROM test_fixtures WHERE id IN (
        SELECT id FROM test_fixtures LIMIT 4
    )
)::bytea[]), 'hex') AS all_present;

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
SELECT encode(laplace.entities_exist_bitmap(
    ARRAY(SELECT id FROM ordered_input ORDER BY ord)::bytea[]
), 'hex') AS mixed;

SELECT octet_length(laplace.entities_exist_bitmap(ARRAY[]::bytea[])) AS empty_len;

SELECT
    encode(laplace.entities_exist_bitmap(ARRAY(
        SELECT id FROM test_fixtures LIMIT 4
    )::bytea[]), 'hex') =
    encode(laplace.entities_exist_bitmap(ARRAY(
        SELECT id FROM test_fixtures LIMIT 4
    )::bytea[]), 'hex') AS idempotent;

-- The keyed native path groups mixed tiers and executes one session-cached,
-- literal-tier plan per run.  It must preserve the caller's original bitmap
-- positions without the historical per-call temp table/index/ANALYZE path.
INSERT INTO laplace.entities (id, tier, type_id, first_observed_by)
SELECT id, 2::smallint, (SELECT id FROM test_fixtures LIMIT 1), NULL
FROM test_fixtures
ORDER BY test_fixtures.id
LIMIT 2;

WITH keyed AS (
    SELECT array_agg(id ORDER BY ord)::bytea[] AS ids,
           array_agg(tier ORDER BY ord)::smallint[] AS tiers
    FROM (VALUES
        ((SELECT id FROM test_fixtures ORDER BY id OFFSET 0 LIMIT 1), 2::smallint, 0),
        ((SELECT id FROM test_fixtures ORDER BY id OFFSET 4 LIMIT 1), 2::smallint, 1),
        ((SELECT id FROM test_fixtures ORDER BY id OFFSET 1 LIMIT 1), 0::smallint, 2),
        ((SELECT id FROM test_fixtures ORDER BY id OFFSET 5 LIMIT 1), 0::smallint, 3)
    ) v(id, tier, ord)
)
SELECT encode(laplace.entities_stored_bitmap(ids, tiers), 'hex') AS keyed_mixed
FROM keyed;

DELETE FROM laplace.entities WHERE id IN (SELECT id FROM test_fixtures);
DROP TABLE test_fixtures;
