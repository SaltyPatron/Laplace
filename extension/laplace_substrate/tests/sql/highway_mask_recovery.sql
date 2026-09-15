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
INSERT INTO laplace.highway_mask_pending SELECT * FROM highway_recovery_pairs;

DO $$
BEGIN
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
        RAISE EXCEPTION 'native replay did not populate every expected bit and stored tier';
    END IF;
    IF (SELECT count(*) FROM laplace.entities WHERE id IN
        (SELECT entity_id FROM highway_recovery_pairs))<>3 THEN
        RAISE EXCEPTION 'replay removed a stored entity tier';
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
END $$;
DELETE FROM laplace.entities WHERE id IN (SELECT entity_id FROM highway_recovery_pairs);
DROP TABLE highway_recovery_pairs;
SELECT 'HIGHWAY_MASK_RECOVERY_OK';
