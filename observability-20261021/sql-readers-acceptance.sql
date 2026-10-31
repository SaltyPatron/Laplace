SELECT ops.repoint_app_log('E:/Repositories/Laplace/.build/.tmp/observability-20261021/sql-reader-logs');
SELECT ops.repoint_chess_drops('E:/Repositories/Laplace/.build/.tmp/observability-20261021/sql-reader-logs');
DO $accept$
DECLARE count_events bigint; event jsonb; category_name text;
BEGIN
    SELECT count(*) INTO count_events FROM ops.app_log();
    IF count_events <> 1 THEN RAISE EXCEPTION 'Actual CSV event count was %, expected 1',count_events; END IF;
    SELECT detail::jsonb,category INTO event,category_name FROM ops.app_log();
    IF event->'Properties'->>'CorrelationId' <> 'actual-sink-check'
       OR category_name <> 'Laplace.Acceptance.Nested.Category'
       OR event->>'Exception' NOT LIKE '%inner storage failure%' THEN
        RAISE EXCEPTION 'CSV reader lost structured scope, category or inner exception';
    END IF;
    ALTER FOREIGN TABLE ops.app_log_api OPTIONS (SET filename 'E:/Repositories/Laplace/.build/.tmp/observability-20261021/sql-reader-logs/malformed.csv');
    BEGIN
        PERFORM * FROM ops.app_log();
        RAISE EXCEPTION 'Malformed CSV incorrectly appeared successful';
    EXCEPTION WHEN bad_copy_file_format THEN RAISE NOTICE 'PASS malformed application log propagates 22P04'; END;
    ALTER FOREIGN TABLE ops.app_log_api OPTIONS (SET filename 'E:/Repositories/Laplace/.build/.tmp/observability-20261021/sql-reader-logs/missing.csv');
    SELECT count(*) INTO count_events FROM ops.app_log();
    IF count_events <> 0 THEN RAISE EXCEPTION 'Unexpected events from absent file'; END IF;
    BEGIN
        PERFORM * FROM ops.chess_drops();
        RAISE EXCEPTION 'Malformed chess drop ledger incorrectly appeared successful';
    EXCEPTION WHEN invalid_text_representation THEN RAISE NOTICE 'PASS malformed chess drop ledger propagates 22P02'; END;
    RAISE NOTICE 'PASS real six-column CSV preserves exception chain and correlation; missing file emits WARNING';
END $accept$;
SELECT p.proname,e.extname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
JOIN pg_depend d ON d.classid='pg_proc'::regclass AND d.objid=p.oid AND d.deptype='e'
JOIN pg_extension e ON e.oid=d.refobjid
WHERE n.nspname='ops' AND p.proname IN ('app_log','chess_drops','repoint_app_log');
