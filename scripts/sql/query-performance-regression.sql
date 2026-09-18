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
    ASSERT laplace.entity_interpretations_publish(
        ARRAY[id], ARRAY[0::smallint], ARRAY[high], ARRAY[high], ARRAY[false]);
    ASSERT NOT EXISTS (SELECT FROM laplace.entity_interpretations e WHERE e.entity_id=id);
    INSERT INTO laplace.entities(id,tier,type_id) VALUES(id,5,high);
    -- Duplicate facets, independent tier/type minima, and null/source ordering.
    ASSERT NOT laplace.entity_interpretations_publish(
        ARRAY[id,id,id,id], ARRAY[5,5,3,4]::smallint[],
        ARRAY[high,high,high,low], ARRAY[high,low,high,low], ARRAY[false,false,true,false]);
    ASSERT (SELECT count(*)=3 FROM laplace.entity_interpretations e WHERE e.entity_id=id);
    ASSERT (SELECT e.tier=3 AND e.type_id=low AND e.first_observed_by=low
            FROM laplace.entities e WHERE e.id=id);
    ASSERT (SELECT e.first_observed_by=low FROM laplace.entity_interpretations e
            WHERE e.entity_id=id AND e.tier=5 AND e.type_id=high);
    SELECT e.ctid INTO before_tid FROM laplace.entities e WHERE e.id=id;
    ASSERT NOT laplace.entity_interpretations_publish(
        ARRAY[id,id], ARRAY[5,5]::smallint[], ARRAY[high,high], ARRAY[high,high], ARRAY[false,true]);
    ASSERT (SELECT e.ctid=before_tid AND e.first_observed_by=low FROM laplace.entities e WHERE e.id=id);
    ASSERT NOT laplace.entity_interpretations_publish(
        ARRAY[]::bytea[], ARRAY[]::smallint[], ARRAY[]::bytea[], ARRAY[]::bytea[], ARRAY[]::boolean[]);
    BEGIN
        PERFORM laplace.entity_interpretations_publish(
            ARRAY[id], ARRAY[]::smallint[], ARRAY[high], ARRAY[high], ARRAY[false]);
        RAISE EXCEPTION 'unequal arrays were accepted';
    EXCEPTION WHEN invalid_parameter_value THEN NULL;
    END;
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
