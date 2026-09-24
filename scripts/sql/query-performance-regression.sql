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

CREATE TABLE laplace.query_perf_index_fixture(id integer) PARTITION BY HASH(id);
CREATE TABLE laplace.query_perf_index_fixture_0 PARTITION OF laplace.query_perf_index_fixture
    FOR VALUES WITH (modulus 1,remainder 0);
INSERT INTO laplace.index_cycle_journal(index_name,table_name,index_def)
VALUES ('query_perf_index_fixture_idx','query_perf_index_fixture',
        'CREATE INDEX query_perf_index_fixture_idx ON ONLY laplace.query_perf_index_fixture(id)');
DO $test$ BEGIN
    ASSERT EXISTS (SELECT FROM ops.index_health()
                   WHERE index_name='query_perf_index_fixture_idx' AND NOT valid AND leaf_count=0);
END $test$;
CALL ops.reindex_invalid(true);
DO $test$ BEGIN
    ASSERT to_regclass('laplace.query_perf_index_fixture_idx') IS NULL;
END $test$;
CALL ops.reindex_invalid();
DO $test$ BEGIN
    ASSERT EXISTS (SELECT FROM pg_index WHERE indexrelid='laplace.query_perf_index_fixture_idx'::regclass
                  AND indisvalid AND indisready);
    ASSERT (SELECT count(*)=1 FROM pg_inherits WHERE inhparent='laplace.query_perf_index_fixture_idx'::regclass);
    ASSERT NOT EXISTS (SELECT FROM laplace.index_cycle_journal WHERE index_name='query_perf_index_fixture_idx');
    ASSERT NOT EXISTS (SELECT FROM ops.index_health() WHERE index_name='query_perf_index_fixture_idx');
END $test$;
DROP TABLE laplace.query_perf_index_fixture;
