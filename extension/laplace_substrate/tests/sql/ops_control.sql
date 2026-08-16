-- The control surface: seeing running work, stopping it, and repairing what the
-- index catalog reports as damaged. These pin the REFUSALS and the row shapes,
-- because the failure mode of a control operation is not an error message — it is
-- acting on the wrong target and reporting success.
-- No SET search_path — every name is purpose- or storage-qualified.
BEGIN;

-- ops.api distinguishes CALL from SELECT. A caller that cannot tell them apart
-- issues the wrong statement, which is a parse error rather than a run.
SELECT kind = 'procedure' AS evict_is_procedure FROM ops.api('evict_source') LIMIT 1;
SELECT kind = 'procedure' AS reindex_is_procedure FROM ops.api('reindex_invalid') LIMIT 1;
SELECT kind = 'function' AS activity_is_function FROM ops.api('activity') LIMIT 1;
SELECT kind = 'function' AS cancel_is_function FROM ops.api('cancel_backend') LIMIT 1;

-- index_health carries the schema its index lives in: an index name alone is
-- ambiguous across the four substrate schemas, and reindex_invalid cannot issue
-- REINDEX without it.
SELECT count(*) = 6 AS index_health_arity
FROM information_schema.routines r
JOIN information_schema.parameters p ON p.specific_name = r.specific_name
WHERE r.routine_schema = 'ops' AND r.routine_name = 'index_health' AND p.parameter_mode = 'OUT';

-- A scratch database has no invalid index. The EMPTY SET is the healthy answer.
SELECT count(*) = 0 AS scratch_has_no_invalid_index FROM ops.index_health();

-- ops.activity always sees at least this backend, and marks it as this backend.
SELECT count(*) >= 1 AS activity_sees_backends FROM ops.activity();
SELECT count(*) = 1 AS activity_marks_self FROM ops.activity() WHERE is_self;

-- restricted is not a synonym for idle: this session reads its own row in full,
-- so nothing here is masked.
SELECT bool_and(NOT restricted) AS own_row_is_not_masked FROM ops.activity() WHERE is_self;

-- The age filter is a floor on the running clock, not a row limit.
SELECT count(*) = 0 AS ancient_filter_excludes_everything
FROM ops.activity(999999, true, false);

-- Idle exclusion never drops an active backend — this session is active.
SELECT count(*) >= 1 AS busy_filter_keeps_self FROM ops.activity(0, false, false);

-- THE REFUSALS. Each is a wrong target that would otherwise be signalled. Every
-- one raises, so each sits in its own savepoint — without that the first refusal
-- aborts the transaction and every assertion after it is skipped rather than run,
-- which is a passing test file that tested nothing.

-- A dead pid. The literal -1 keeps this message byte-stable across runs.
SAVEPOINT refusal;
SELECT ops.cancel_backend(-1);
ROLLBACK TO SAVEPOINT refusal;

SELECT ops.terminate_backend(-1);
ROLLBACK TO SAVEPOINT refusal;
RELEASE SAVEPOINT refusal;

-- This session. The message names the pid, which differs every run, so the
-- assertion is on the stable clause rather than the rendered error — a test that
-- compares a pid byte-for-byte fails on its second execution.
DO $$
DECLARE refused boolean;
BEGIN
    BEGIN
        PERFORM ops.cancel_backend(pg_backend_pid());
        refused := false;
    EXCEPTION WHEN OTHERS THEN
        refused := SQLERRM LIKE '%is this session%';
    END;
    RAISE NOTICE 'cancel_refuses_this_session: %', refused;

    BEGIN
        PERFORM ops.terminate_backend(pg_backend_pid());
        refused := false;
    EXCEPTION WHEN OTHERS THEN
        refused := SQLERRM LIKE '%is this session%';
    END;
    RAISE NOTICE 'terminate_refuses_this_session: %', refused;
END $$;

-- A dry run touches nothing and is legal inside this transaction precisely
-- because it never reaches the COMMIT in the repair branch.
CALL ops.reindex_invalid(true);

SELECT count(*) = 0 AS dry_run_left_the_catalog_alone FROM ops.index_health();

ROLLBACK;
