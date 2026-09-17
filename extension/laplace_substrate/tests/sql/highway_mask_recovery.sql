\set ECHO none
\set QUIET 1
\pset format unaligned
\pset tuples_only on
SET client_min_messages=warning;

-- Real database/native execution. There is intentionally no consensus evidence
-- for these entities: recovery must use retained pairs, not rediscover the work.
CREATE TEMP TABLE highway_recovery_pairs(entity_id bytea,type_id bytea);
INSERT INTO highway_recovery_pairs VALUES
(decode('d673b68115514712b366347069127a01','hex'),laplace.relation_type_id('IS_A')),
(decode('d673b68115514712b366347069127a01','hex'),laplace.relation_type_id('HAS_PART')),
(decode('d673b68115514712b366347069127a02','hex'),laplace.relation_type_id('IS_A'));
INSERT INTO laplace.entities(id,tier,type_id)
SELECT DISTINCT entity_id,2,decode('d673b68115514712b366347069127aff','hex')
FROM highway_recovery_pairs;
INSERT INTO laplace.entities(id,tier,type_id) VALUES
(decode('d673b68115514712b366347069127a01','hex'),3,decode('d673b68115514712b366347069127aff','hex'));
CREATE TEMP TABLE highway_recovery_interpretations AS
SELECT DISTINCT entity_id,2::smallint AS tier,
       decode('d673b68115514712b366347069127aff','hex') AS type_id
FROM highway_recovery_pairs
UNION ALL
SELECT decode('d673b68115514712b366347069127a01','hex'),3::smallint,
       decode('d673b68115514712b366347069127aff','hex');
INSERT INTO laplace.highway_mask_pending SELECT * FROM highway_recovery_pairs;

DO $$
BEGIN
    IF (SELECT count(*) FROM laplace.entities WHERE id IN
        (SELECT entity_id FROM highway_recovery_interpretations))<>2
       OR (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id IN
        (SELECT entity_id FROM highway_recovery_interpretations))<>(SELECT count(*) FROM highway_recovery_interpretations)
       OR EXISTS(SELECT FROM highway_recovery_interpretations x
        LEFT JOIN laplace.entity_interpretations i USING(entity_id,tier,type_id)
        WHERE i.entity_id IS NULL) THEN
        RAISE EXCEPTION 'fixture lost canonical entities or exact interpretation pairs';
    END IF;
    IF NOT consensus.highway_ready() THEN RAISE EXCEPTION 'fixture registry is unavailable'; END IF;
    IF (SELECT l.lanname FROM pg_proc p JOIN pg_language l ON l.oid=p.prolang
        WHERE p.oid='consensus.highway_mask_deposit(bytea[],bytea[])'::regprocedure)
       IS DISTINCT FROM 'c' THEN
        RAISE EXCEPTION 'public deposit no longer enters native C directly';
    END IF;
    IF EXISTS(SELECT 1 FROM pg_class WHERE oid IN
        ('laplace.highway_mask_pending'::regclass,'laplace.highway_mask_dirty'::regclass)
        AND relpersistence<>'p') THEN
        RAISE EXCEPTION 'recovery work is not WAL logged';
    END IF;
    BEGIN
        PERFORM consensus.highway_mask_deposit(
            ARRAY[decode('d673b68115514712b366347069127a01','hex')],ARRAY[]::bytea[]);
        RAISE EXCEPTION 'unequal zipped arrays were silently truncated';
    EXCEPTION WHEN array_subscript_error THEN NULL;
    END;
END $$;

-- A failed native update must restore the claimed pending batch atomically.
ALTER TABLE laplace.entities ADD CONSTRAINT highway_recovery_check
CHECK(id<>decode('d673b68115514712b366347069127a01','hex') OR highway_mask IS NULL) NOT VALID;
DO $$
BEGIN
    BEGIN
        CALL laplace.highway_mask_pending_drain(3);
        RAISE EXCEPTION 'native recovery bypassed the table constraint';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    IF (SELECT count(*) FROM laplace.highway_mask_pending p
        JOIN highway_recovery_pairs f USING(entity_id,type_id))<>3 THEN
        RAISE EXCEPTION 'failed replay lost pending pairs';
    END IF;
    IF EXISTS(SELECT 1 FROM laplace.entities WHERE id IN
        (SELECT entity_id FROM highway_recovery_pairs) AND highway_mask IS NOT NULL) THEN
        RAISE EXCEPTION 'failed replay left a partial mask update';
    END IF;
END $$;
ALTER TABLE laplace.entities DROP CONSTRAINT highway_recovery_check;

CALL laplace.highway_mask_pending_drain(1);
CALL laplace.highway_mask_pending_drain(1);
DO $$
BEGIN
    IF EXISTS(SELECT 1 FROM laplace.highway_mask_pending p
        JOIN highway_recovery_pairs f USING(entity_id,type_id)) THEN
        RAISE EXCEPTION 'successful replay left exact pairs pending';
    END IF;
    IF EXISTS(SELECT 1 FROM highway_recovery_pairs f JOIN laplace.entities e ON e.id=f.entity_id
        WHERE NOT COALESCE(consensus.highway_mask_bits(e.highway_mask) @>
            ARRAY[consensus.relation_highway_bit(f.type_id)],false)) THEN
        RAISE EXCEPTION 'native replay did not populate every expected canonical mask bit';
    END IF;
    IF (SELECT count(*) FROM laplace.entities WHERE id IN
        (SELECT entity_id FROM highway_recovery_interpretations))<>2
       OR (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id IN
        (SELECT entity_id FROM highway_recovery_interpretations))<>(SELECT count(*) FROM highway_recovery_interpretations)
       OR EXISTS(SELECT FROM highway_recovery_interpretations x
        LEFT JOIN laplace.entity_interpretations i USING(entity_id,tier,type_id)
        WHERE i.entity_id IS NULL) THEN
        RAISE EXCEPTION 'replay changed canonical entities or exact interpretation pairs';
    END IF;
    IF EXISTS(SELECT 1 FROM laplace.consensus WHERE subject_id IN
        (SELECT entity_id FROM highway_recovery_pairs) OR object_id IN
        (SELECT entity_id FROM highway_recovery_pairs)) THEN
        RAISE EXCEPTION 'replay manufactured consensus evidence';
    END IF;
END $$;

-- A later eviction's clear-work wins over an older retained OR deposit.
INSERT INTO laplace.highway_mask_dirty SELECT DISTINCT entity_id FROM highway_recovery_pairs;
CALL laplace.highway_mask_drain(1);
CALL laplace.highway_mask_drain(1);
DO $$
BEGIN
    IF EXISTS(SELECT 1 FROM laplace.entities WHERE id IN
        (SELECT entity_id FROM highway_recovery_pairs) AND highway_mask IS NOT NULL) THEN
        RAISE EXCEPTION 'zero remaining relation bits did not clear the old mask';
    END IF;
    IF EXISTS(SELECT 1 FROM laplace.highway_mask_dirty WHERE id IN
        (SELECT entity_id FROM highway_recovery_pairs)) THEN
        RAISE EXCEPTION 'successful clear left dirty identities pending';
    END IF;
    IF (SELECT count(*) FROM laplace.entities WHERE id IN
        (SELECT entity_id FROM highway_recovery_interpretations))<>2
       OR (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id IN
        (SELECT entity_id FROM highway_recovery_interpretations))<>(SELECT count(*) FROM highway_recovery_interpretations)
       OR EXISTS(SELECT FROM highway_recovery_interpretations x
        LEFT JOIN laplace.entity_interpretations i USING(entity_id,tier,type_id)
        WHERE i.entity_id IS NULL) THEN
        RAISE EXCEPTION 'clear changed canonical entities or exact interpretation pairs';
    END IF;
END $$;
DELETE FROM laplace.entity_interpretations WHERE entity_id IN (SELECT entity_id FROM highway_recovery_pairs);
DELETE FROM laplace.entities WHERE id IN (SELECT entity_id FROM highway_recovery_pairs);
DROP TABLE highway_recovery_interpretations;
DROP TABLE highway_recovery_pairs;

-- Refresh must use the same native relation/tuple machinery as admission,
-- including replacement (not merely OR), all interpretations and more than one SPI page.
BEGIN;
CREATE TEMP TABLE highway_refresh_fixture(name text PRIMARY KEY,id bytea);
INSERT INTO highway_refresh_fixture
SELECT name,public.laplace_hash128_blake3(convert_to('test/native-highway-refresh/'||name,'UTF8'))
FROM unnest(ARRAY['a','b','isolated','untouched','dynamic','unknown','type']) name;
INSERT INTO highway_refresh_fixture VALUES ('zero',decode(repeat('00',16),'hex'));
CREATE TEMP TABLE highway_refresh_interpretations AS
SELECT f.id AS entity_id,t.tier::smallint AS tier,k.id AS type_id FROM highway_refresh_fixture f
CROSS JOIN (VALUES(2),(3)) t(tier)
CROSS JOIN highway_refresh_fixture k
WHERE k.name='type' AND f.name IN ('a','b','isolated','untouched','zero');
INSERT INTO laplace.entities(id,tier,type_id)
SELECT entity_id,tier,type_id FROM highway_refresh_interpretations;
INSERT INTO laplace.consensus
    (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
SELECT laplace.consensus_id(s,r,o),s,r,o,
       1500000000000,350000000000,60000000,1,now()
FROM (
    SELECT a.id s,laplace.relation_type_id('IS_A') r,b.id o
    FROM highway_refresh_fixture a,highway_refresh_fixture b WHERE a.name='a' AND b.name='b'
    UNION ALL
    SELECT a.id,laplace.relation_type_id('HAS_PART'),a.id
    FROM highway_refresh_fixture a WHERE a.name='a'
    UNION ALL
    SELECT a.id,d.id,b.id FROM highway_refresh_fixture a,highway_refresh_fixture b,
        highway_refresh_fixture d WHERE a.name='b' AND b.name='zero' AND d.name='dynamic'
    UNION ALL
    SELECT d.id,laplace.relation_type_id('IS_A'),laplace.relation_type_id(family)
    FROM highway_refresh_fixture d CROSS JOIN unnest(ARRAY['HAS_PART','CAUSES']) family
    WHERE d.name='dynamic'
    UNION ALL
    SELECT a.id,d.id,b.id FROM highway_refresh_fixture a,highway_refresh_fixture b,
        highway_refresh_fixture d WHERE a.name='isolated' AND b.name='b' AND d.name='unknown'
) cells;
-- Empty masks must become SQL NULL. Unrequested rows must remain untouched.
UPDATE laplace.entities SET highway_mask=decode(repeat('00',32),'hex')
WHERE id IN (SELECT id FROM highway_refresh_fixture WHERE name IN ('isolated','untouched'));
CREATE TEMP TABLE highway_refresh_expected AS
WITH requested AS (
    SELECT id FROM highway_refresh_fixture WHERE name IN ('a','b','zero','isolated')
), incident AS (
    SELECT q.id,c.type_id FROM requested q JOIN laplace.consensus c ON c.subject_id=q.id
    UNION SELECT q.id,c.type_id FROM requested q JOIN laplace.consensus c ON c.object_id=q.id
), bits AS (
    SELECT i.id,COALESCE(consensus.relation_highway_bit(i.type_id),
                        consensus.relation_highway_bit(f.object_id)) AS bit
    FROM incident i LEFT JOIN laplace.consensus f ON f.subject_id=i.type_id
        AND f.type_id=laplace.relation_type_id('IS_A')
)
SELECT q.id,consensus.highway_mask_from_bits(array_agg(DISTINCT b.bit)) AS mask
FROM requested q LEFT JOIN bits b ON b.id=q.id GROUP BY q.id;
DO $$
DECLARE ids bytea[]; changed bigint;
BEGIN
    IF (SELECT count(*) FROM laplace.entities WHERE id IN
        (SELECT entity_id FROM highway_refresh_interpretations))<>5
       OR (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id IN
        (SELECT entity_id FROM highway_refresh_interpretations))<>(SELECT count(*) FROM highway_refresh_interpretations)
       OR EXISTS(SELECT FROM highway_refresh_interpretations x
        LEFT JOIN laplace.entity_interpretations i USING(entity_id,tier,type_id)
        WHERE i.entity_id IS NULL) THEN
        RAISE EXCEPTION 'refresh fixture lost canonical entities or exact interpretation pairs';
    END IF;
    IF (SELECT l.lanname FROM pg_proc p JOIN pg_language l ON l.oid=p.prolang
        WHERE p.oid='consensus.highway_mask_refresh(bytea[])'::regprocedure)
       IS DISTINCT FROM 'c' THEN RAISE EXCEPTION 'refresh did not enter native C'; END IF;
    IF consensus.highway_mask_refresh(NULL)<>0 OR
       consensus.highway_mask_refresh(ARRAY[]::bytea[])<>0 OR
       consensus.highway_mask_refresh(ARRAY[NULL]::bytea[])<>0 THEN
        RAISE EXCEPTION 'empty refresh changed rows';
    END IF;
    SELECT array_agg(id ORDER BY id) INTO ids FROM highway_refresh_expected;
    changed:=consensus.highway_mask_refresh(ids||ids||ARRAY[NULL]::bytea[]);
    IF changed<>4 THEN RAISE EXCEPTION 'refresh changed wrong canonical entity count: %',changed; END IF;
    IF EXISTS(SELECT FROM highway_refresh_expected x JOIN laplace.entities e USING(id)
        WHERE e.highway_mask IS DISTINCT FROM x.mask) THEN
        RAISE EXCEPTION 'refresh differs from independent full incident/family oracle';
    END IF;
    IF consensus.highway_mask_refresh(ids)<>0 THEN RAISE EXCEPTION 'unchanged refresh rewrote masks'; END IF;
    IF EXISTS(SELECT FROM laplace.entities e JOIN highway_refresh_fixture f USING(id)
        WHERE f.name='untouched' AND e.highway_mask IS DISTINCT FROM decode(repeat('00',32),'hex')) THEN
        RAISE EXCEPTION 'refresh changed an unrequested entity';
    END IF;
    BEGIN
        PERFORM consensus.highway_mask_refresh(ids||ARRAY[decode('00','hex')]);
        RAISE EXCEPTION 'refresh accepted an invalid identity';
    EXCEPTION WHEN string_data_length_mismatch THEN NULL;
    END;
END $$;
-- Removing one dynamic-family assertion clears only its bit, leaving all
-- canonical and remaining-family bits intact on both incident endpoints.
DELETE FROM laplace.consensus WHERE subject_id=(SELECT id FROM highway_refresh_fixture WHERE name='dynamic')
AND object_id=laplace.relation_type_id('CAUSES');
DO $$
DECLARE ids bytea[]; changed bigint;
BEGIN
    SELECT array_agg(id) INTO ids FROM highway_refresh_fixture WHERE name IN ('a','b','zero','isolated');
    changed:=consensus.highway_mask_refresh(ids);
    IF changed<>2 THEN RAISE EXCEPTION 'family withdrawal changed wrong canonical entity count: %',changed; END IF;
    IF EXISTS(SELECT FROM laplace.entities e JOIN highway_refresh_fixture f USING(id)
        WHERE f.name IN ('b','zero') AND
            (consensus.highway_mask_bits(e.highway_mask) @> ARRAY[consensus.relation_highway_bit(laplace.relation_type_id('CAUSES'))]
             OR NOT consensus.highway_mask_bits(e.highway_mask) @> ARRAY[consensus.relation_highway_bit(laplace.relation_type_id('HAS_PART'))])) THEN
        RAISE EXCEPTION 'family withdrawal left stale bits or erased surviving bits';
    END IF;
END $$;
DELETE FROM laplace.consensus WHERE subject_id IN (SELECT id FROM highway_refresh_fixture)
   OR object_id IN (SELECT id FROM highway_refresh_fixture);
-- Reject the later nonzero endpoint so the earlier zero-id mask replacement
-- must roll back too. The mask belongs to the canonical entity, not a tier.
ALTER TABLE laplace.entities ADD CONSTRAINT highway_refresh_preserve
CHECK(id<>GREATEST(
    public.laplace_hash128_blake3(convert_to('test/native-highway-refresh/a','UTF8')),
    public.laplace_hash128_blake3(convert_to('test/native-highway-refresh/b','UTF8')))
    OR highway_mask IS NOT NULL) NOT VALID;
CREATE TEMP TABLE highway_before_clear AS
SELECT e.id,e.tier,e.highway_mask FROM laplace.entities e JOIN highway_refresh_fixture f USING(id);
DO $$
DECLARE ids bytea[];
BEGIN
    SELECT array_agg(id) INTO ids FROM highway_refresh_fixture WHERE name IN ('a','b','zero','isolated');
    BEGIN
        PERFORM consensus.highway_mask_refresh(ids);
        RAISE EXCEPTION 'native refresh bypassed CHECK';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    IF EXISTS(SELECT FROM highway_before_clear b LEFT JOIN laplace.entities e USING(id)
        WHERE e.id IS NULL OR (e.tier,e.highway_mask) IS DISTINCT FROM (b.tier,b.highway_mask)) THEN
        RAISE EXCEPTION 'failed refresh left a partial replacement';
    END IF;
    IF (SELECT count(*) FROM laplace.entities WHERE id IN
        (SELECT entity_id FROM highway_refresh_interpretations))<>5
       OR (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id IN
        (SELECT entity_id FROM highway_refresh_interpretations))<>(SELECT count(*) FROM highway_refresh_interpretations)
       OR EXISTS(SELECT FROM highway_refresh_interpretations x
        LEFT JOIN laplace.entity_interpretations i USING(entity_id,tier,type_id)
        WHERE i.entity_id IS NULL) THEN
        RAISE EXCEPTION 'failed refresh changed canonical entities or exact interpretation pairs';
    END IF;
END $$;
ALTER TABLE laplace.entities DROP CONSTRAINT highway_refresh_preserve;
DO $$
DECLARE ids bytea[]; changed bigint;
BEGIN
    SELECT array_agg(id) INTO ids FROM highway_refresh_fixture WHERE name IN ('a','b','zero','isolated');
    changed:=consensus.highway_mask_refresh(ids);
    IF changed<>3 THEN
        RAISE EXCEPTION 'final incident withdrawal changed wrong canonical entity count: %',changed;
    END IF;
    IF consensus.highway_mask_refresh(ids)<>0 THEN
        RAISE EXCEPTION 'final incident withdrawal did not clear precisely three canonical masks once';
    END IF;
    IF EXISTS(SELECT FROM laplace.entities WHERE id=ANY(ids) AND highway_mask IS NOT NULL) THEN
        RAISE EXCEPTION 'final incident withdrawal retained a non-NULL mask';
    END IF;
    IF (SELECT count(*) FROM laplace.entities WHERE id IN
        (SELECT entity_id FROM highway_refresh_interpretations))<>5
       OR (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id IN
        (SELECT entity_id FROM highway_refresh_interpretations))<>(SELECT count(*) FROM highway_refresh_interpretations)
       OR EXISTS(SELECT FROM highway_refresh_interpretations x
        LEFT JOIN laplace.entity_interpretations i USING(entity_id,tier,type_id)
        WHERE i.entity_id IS NULL) THEN
        RAISE EXCEPTION 'final refresh changed canonical entities or exact interpretation pairs';
    END IF;
END $$;

CREATE TEMP TABLE highway_refresh_pages AS
SELECT public.laplace_hash128_blake3(convert_to('test/native-highway-page/'||n,'UTF8')) AS id,
       CASE WHEN n%2=0 THEN laplace.relation_type_id('IS_A')
            ELSE (SELECT id FROM highway_refresh_fixture WHERE name='dynamic') END AS type_id
FROM generate_series(1,8305) n;
INSERT INTO laplace.entities(id,tier,type_id)
SELECT p.id,2,f.id FROM highway_refresh_pages p,highway_refresh_fixture f WHERE f.name='type';
INSERT INTO laplace.consensus
    (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
SELECT laplace.consensus_id(p.id,p.type_id,f.id),p.id,p.type_id,f.id,
    1500000000000,350000000000,60000000,1,now()
FROM highway_refresh_pages p,highway_refresh_fixture f WHERE f.name='untouched';
INSERT INTO laplace.consensus
    (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
SELECT laplace.consensus_id(f.id,laplace.relation_type_id('IS_A'),laplace.relation_type_id('HAS_PART')),
    f.id,laplace.relation_type_id('IS_A'),laplace.relation_type_id('HAS_PART'),
    1500000000000,350000000000,60000000,1,now()
FROM highway_refresh_fixture f WHERE f.name='dynamic';
DO $$
DECLARE ids bytea[]; changed bigint;
BEGIN
    SELECT array_agg(id ORDER BY id) INTO ids FROM highway_refresh_pages;
    changed:=consensus.highway_mask_refresh(ids);
    IF changed<>8305 THEN RAISE EXCEPTION 'SPI page truncated refresh: %',changed; END IF;
    IF EXISTS(SELECT FROM highway_refresh_pages p JOIN laplace.entities e USING(id)
        WHERE consensus.highway_mask_bits(e.highway_mask) IS DISTINCT FROM
        ARRAY[consensus.relation_highway_bit(CASE WHEN p.type_id=laplace.relation_type_id('IS_A')
          THEN p.type_id ELSE laplace.relation_type_id('HAS_PART') END)]) THEN
        RAISE EXCEPTION 'paged refresh changed relation-family semantics';
    END IF;
    IF consensus.highway_mask_refresh(ids)<>0 THEN RAISE EXCEPTION 'paged replay was not idempotent'; END IF;
END $$;
ROLLBACK;

SELECT 'HIGHWAY_MASK_RECOVERY_OK';
