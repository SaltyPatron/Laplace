-- psql -v execution_library=/absolute/path/laplace_execution_<digest>.so
-- Uses populated indexed state; every local binding and fixture rolls back.
\set ON_ERROR_STOP on
BEGIN;
SET LOCAL statement_timeout = '60s';
SET LOCAL lock_timeout = '2s';
CREATE FUNCTION pg_temp.native_stored(bytea[]) RETURNS bytea
AS :'execution_library', 'pg_laplace_entities_stored_bitmap' LANGUAGE C STABLE;
CREATE FUNCTION pg_temp.native_ordinals(bytea[]) RETURNS TABLE(idx integer)
AS :'execution_library', 'pg_laplace_entities_present_ordinals'
LANGUAGE C STABLE PARALLEL RESTRICTED;
CREATE TEMP TABLE presence_input AS
SELECT array_agg(id ORDER BY id) AS ids
FROM (SELECT id FROM laplace.entities LIMIT 8192) sampled;
DO $$
DECLARE
    ids bytea[];
    old_bitmap bytea;
    new_bitmap bytea;
    actual int[];
    expected int[];
BEGIN
    SELECT p.ids INTO ids FROM presence_input p;
    IF cardinality(ids) < 2 THEN RAISE EXCEPTION 'populated entity sample required'; END IF;
    ids := ids || ids[1:32] || ARRAY[decode(repeat('ff',16),'hex'), '\x01'::bytea];
    old_bitmap := laplace.entities_stored_bitmap(ids);
    new_bitmap := pg_temp.native_stored(ids);
    IF old_bitmap IS DISTINCT FROM new_bitmap THEN
        RAISE EXCEPTION 'stored bitmap mismatch';
    END IF;
    ids := ids || ARRAY[NULL::bytea];
    SELECT array_agg(idx ORDER BY idx) INTO actual FROM pg_temp.native_ordinals(ids);
    SELECT array_agg((u.ord-1)::int ORDER BY u.ord) INTO expected
    FROM unnest(ids) WITH ORDINALITY u(id,ord)
    JOIN laplace.entities e ON e.id=u.id;
    IF actual IS DISTINCT FROM expected THEN
        RAISE EXCEPTION 'ordinal/duplicate/null/malformed parity failed';
    END IF;
    IF EXISTS (SELECT FROM pg_temp.native_ordinals(NULL))
       OR EXISTS (SELECT FROM pg_temp.native_ordinals(ARRAY[]::bytea[])) THEN
        RAISE EXCEPTION 'empty/null ordinal input must be empty';
    END IF;
END $$;
EXPLAIN (ANALYZE, BUFFERS, SETTINGS)
SELECT laplace.entities_stored_bitmap(ids) FROM presence_input;
EXPLAIN (ANALYZE, BUFFERS, SETTINGS)
SELECT pg_temp.native_stored(ids) FROM presence_input;
EXPLAIN (ANALYZE, BUFFERS, SETTINGS)
SELECT pg_temp.native_stored(ids) FROM presence_input;
EXPLAIN (ANALYZE, BUFFERS, SETTINGS)
SELECT laplace.entities_stored_bitmap(ids) FROM presence_input;
ROLLBACK;
