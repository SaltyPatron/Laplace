\set ON_ERROR_STOP on
-- Run in an isolated installed-extension database. All identity mutations roll
-- back; the repair case owns and removes its own small partitioned table.
BEGIN;
SET LOCAL search_path = laplace, public;
DO $test$
#variable_conflict use_variable
DECLARE
    id bytea := laplace.word_id('A');
    low bytea := decode(repeat('11',16),'hex');
    high bytea := decode(repeat('ee',16),'hex');
    before_tid tid;
BEGIN
    IF EXISTS (SELECT FROM laplace.entities e WHERE e.id = id) THEN
        RAISE EXCEPTION 'test requires an unseeded fixture database';
    END IF;
    -- A resolvable cached atom is not proof of a persisted canonical entity.
    ASSERT bit_count(laplace.entities_exist_bitmap(ARRAY[id])) = 1;
    ASSERT bit_count(laplace.entities_stored_bitmap(ARRAY[id])) = 0;
    INSERT INTO laplace.entities(id,tier,type_id) VALUES(id,5,high);
    -- Re-inserting the same content id is an ordinary key conflict, not a
    -- second identity or mutation of the row's projection.
    BEGIN
        INSERT INTO laplace.entities(id,tier,type_id) VALUES(id,3,low);
        RAISE EXCEPTION 'duplicate content id was accepted as a second identity';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    SELECT e.ctid INTO before_tid FROM laplace.entities e WHERE e.id=id;
    ASSERT (SELECT e.tier=5 AND e.type_id=high FROM laplace.entities e WHERE e.id=id);
    ASSERT (SELECT e.ctid=before_tid FROM laplace.entities e WHERE e.id=id);
END $test$;
ROLLBACK;

