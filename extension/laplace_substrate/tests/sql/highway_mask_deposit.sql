-- Native deposit laws: canonical lookup, dynamic IS_A-family fallback,
-- duplicate collapse, OR accumulation, accurate update count, and strict ids.
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS laplace_geom;
CREATE EXTENSION IF NOT EXISTS laplace_substrate;

BEGIN;

CREATE TEMP TABLE deposit_fixtures AS
SELECT * FROM (VALUES
    ('entity_a', decode(repeat('a7', 16), 'hex')),
    ('entity_b', decode(repeat('a8', 16), 'hex')),
    ('missing',  decode(repeat('a9', 16), 'hex')),
    ('dynamic',  decode(repeat('da', 16), 'hex')),
    ('unknown',  decode(repeat('db', 16), 'hex')),
    ('type',     decode(repeat('dc', 16), 'hex'))
) v(name, id);

INSERT INTO laplace.entities (id, tier, type_id)
SELECT id, 2::smallint, (SELECT id FROM deposit_fixtures WHERE name = 'type')
FROM deposit_fixtures WHERE name IN ('entity_a', 'entity_b');

-- The dynamic relation has no highway slot. Its IS_A object does.
INSERT INTO laplace.consensus
    (id, subject_id, type_id, object_id, rating, rd, volatility,
     witness_count, last_observed_at)
VALUES
    (decode(repeat('ed', 16), 'hex'),
     (SELECT id FROM deposit_fixtures WHERE name = 'dynamic'),
     laplace.relation_type_id('IS_A'),
     laplace.relation_type_id('HAS_PART'),
     1500000000000, 350000000000, 60000000, 1, now());

WITH f AS (SELECT name, id FROM deposit_fixtures)
SELECT consensus.highway_mask_deposit(
    ARRAY[(SELECT id FROM f WHERE name = 'entity_a'),
          (SELECT id FROM f WHERE name = 'entity_a'),
          (SELECT id FROM f WHERE name = 'entity_a'),
          (SELECT id FROM f WHERE name = 'entity_b'),
          (SELECT id FROM f WHERE name = 'entity_b'),
          (SELECT id FROM f WHERE name = 'missing')],
    ARRAY[laplace.relation_type_id('IS_A'),
          laplace.relation_type_id('HAS_PART'),
          laplace.relation_type_id('IS_A'),
          (SELECT id FROM f WHERE name = 'dynamic'),
          (SELECT id FROM f WHERE name = 'unknown'),
          laplace.relation_type_id('IS_A')]) AS first_updated;

SELECT f.name, consensus.highway_mask_bits(e.highway_mask) @>
       CASE f.name
         WHEN 'entity_a' THEN ARRAY[consensus.relation_highway_bit(laplace.relation_type_id('IS_A')),
                                    consensus.relation_highway_bit(laplace.relation_type_id('HAS_PART'))]
         ELSE ARRAY[consensus.relation_highway_bit(laplace.relation_type_id('HAS_PART'))]
       END AS expected_bits
FROM laplace.entities e
JOIN deposit_fixtures f ON f.id = e.id
ORDER BY f.name;

WITH f AS (SELECT name, id FROM deposit_fixtures)
SELECT consensus.highway_mask_deposit(
    ARRAY[(SELECT id FROM f WHERE name = 'entity_a'),
          (SELECT id FROM f WHERE name = 'entity_b')],
    ARRAY[laplace.relation_type_id('IS_A'),
          (SELECT id FROM f WHERE name = 'dynamic')]) AS repeat_updated;

WITH f AS (SELECT name, id FROM deposit_fixtures)
SELECT consensus.highway_mask_deposit(
    ARRAY[(SELECT id FROM f WHERE name = 'entity_b')],
    ARRAY[laplace.relation_type_id('IS_A')]) AS or_updated;

SELECT consensus.highway_mask_bits(e.highway_mask) @>
       ARRAY[consensus.relation_highway_bit(laplace.relation_type_id('IS_A')),
             consensus.relation_highway_bit(laplace.relation_type_id('HAS_PART'))]
       AS or_preserved
FROM laplace.entities e
JOIN deposit_fixtures f ON f.id = e.id
WHERE f.name = 'entity_b';

SELECT consensus.highway_mask_deposit(NULL, NULL) AS null_arrays_updated;

DO $$
BEGIN
    BEGIN
        PERFORM consensus.highway_mask_deposit(
            ARRAY[decode('00', 'hex')],
            ARRAY[laplace.relation_type_id('IS_A')]);
        RAISE EXCEPTION 'malformed entity id was accepted';
    EXCEPTION WHEN string_data_length_mismatch THEN
        NULL;
    END;
    BEGIN
        PERFORM consensus.highway_mask_deposit(
            ARRAY[(SELECT id FROM deposit_fixtures WHERE name = 'entity_a')],
            ARRAY[decode(repeat('00', 17), 'hex')]);
        RAISE EXCEPTION 'malformed type id was accepted';
    EXCEPTION WHEN string_data_length_mismatch THEN
        NULL;
    END;
END $$;

-- Physical tuple routing must retain every stored tier for an identity.
INSERT INTO laplace.entities(id,tier,type_id)
SELECT id,3,decode(repeat('dc',16),'hex') FROM deposit_fixtures WHERE name='entity_a';
DO $$
DECLARE n bigint;
BEGIN
    SELECT consensus.highway_mask_deposit(
        ARRAY[decode(repeat('a7',16),'hex')],
        ARRAY[laplace.relation_type_id('IS_ANTONYM_OF')]) INTO n;
    IF n<>2 THEN RAISE EXCEPTION 'multi-tier identity lost an update: %',n; END IF;
    SELECT consensus.highway_mask_deposit(
        ARRAY[decode(repeat('a7',16),'hex')],
        ARRAY[laplace.relation_type_id('IS_ANTONYM_OF')]) INTO n;
    IF n<>0 THEN RAISE EXCEPTION 'replay updated % masks',n; END IF;
END $$;

-- Native writes still run the table's constraints and roll back on failure.
ALTER TABLE laplace.entities ADD CONSTRAINT mask_deposit_check
    CHECK(id<>decode(repeat('a8',16),'hex') OR highway_mask IS NULL) NOT VALID;
DO $$
BEGIN
    BEGIN
        PERFORM consensus.highway_mask_deposit(
            ARRAY[decode(repeat('a8',16),'hex')],
            ARRAY[laplace.relation_type_id('IS_ANTONYM_OF')]);
        RAISE EXCEPTION 'mask write bypassed a CHECK constraint';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    IF EXISTS(SELECT 1 FROM laplace.entities WHERE id=decode(repeat('a8',16),'hex')
        AND consensus.highway_mask_bits(highway_mask) @>
            ARRAY[consensus.relation_highway_bit(laplace.relation_type_id('IS_ANTONYM_OF'))]) THEN
        RAISE EXCEPTION 'failed mask update survived rollback';
    END IF;
END $$;
ALTER TABLE laplace.entities DROP CONSTRAINT mask_deposit_check;

CREATE ROLE mask_deposit_reader;
GRANT USAGE ON SCHEMA laplace,consensus TO mask_deposit_reader;
GRANT SELECT ON laplace.entities TO mask_deposit_reader;
SET LOCAL ROLE mask_deposit_reader;
DO $$
BEGIN
    BEGIN
        PERFORM consensus.highway_mask_deposit(
            ARRAY[decode(repeat('a7',16),'hex')],ARRAY[laplace.relation_type_id('IS_A')]);
        RAISE EXCEPTION 'SELECT privilege authorized a native UPDATE';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
END $$;
RESET ROLE;
GRANT UPDATE ON laplace.entities TO mask_deposit_reader;
ALTER TABLE laplace.entities ENABLE ROW LEVEL SECURITY;
SET LOCAL ROLE mask_deposit_reader;
DO $$
BEGIN
    BEGIN
        PERFORM consensus.highway_mask_deposit(
            ARRAY[decode(repeat('a7',16),'hex')],ARRAY[laplace.relation_type_id('IS_A')]);
        RAISE EXCEPTION 'native mask writer bypassed row security';
    EXCEPTION WHEN internal_error THEN
        IF SQLERRM NOT LIKE '%cannot bypass row security%' THEN RAISE; END IF;
    END;
END $$;
RESET ROLE;
ALTER TABLE laplace.entities DISABLE ROW LEVEL SECURITY;

ROLLBACK;

BEGIN READ ONLY;
DO $$
BEGIN
    BEGIN
        PERFORM consensus.highway_mask_deposit(
            ARRAY[decode(repeat('a7',16),'hex')],ARRAY[laplace.relation_type_id('IS_A')]);
        RAISE EXCEPTION 'native mask writer accepted a read-only transaction';
    EXCEPTION WHEN read_only_sql_transaction THEN NULL;
    END;
END $$;
ROLLBACK;
