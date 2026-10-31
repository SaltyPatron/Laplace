DO $accept$
DECLARE n bigint; previous_mcp text[];
BEGIN
    BEGIN
        PERFORM * FROM ops.app_log();
        RAISE EXCEPTION 'All unavailable log sources incorrectly appeared empty';
    EXCEPTION WHEN object_not_in_prerequisite_state THEN
        RAISE NOTICE 'PASS unconfigured logging cannot masquerade as no events';
    END;
    SELECT ftoptions INTO previous_mcp FROM pg_foreign_table WHERE ftrelid='ops.app_log_mcp'::regclass;
    PERFORM ops.repoint_app_log('api','E:/Repositories/Laplace/.build/.tmp/observability-20261021/sql-reader-logs');
    IF (SELECT ftoptions FROM pg_foreign_table WHERE ftrelid='ops.app_log_mcp'::regclass) IS DISTINCT FROM previous_mcp THEN
        RAISE EXCEPTION 'API log binding changed another process source';
    END IF;
    SELECT count(*) INTO n FROM ops.app_log();
    IF n<>1 THEN RAISE EXCEPTION 'API source did not expose actual emitted CSV event: %',n; END IF;
    IF NOT EXISTS(SELECT FROM ops.app_log() WHERE detail::jsonb->'Properties'->>'CorrelationId'='actual-sink-check') THEN
        RAISE EXCEPTION 'Real API-bound event lost correlation metadata';
    END IF;
    PERFORM ops.repoint_app_log('api',E'E:\\Repositories\\Laplace\\.build\\.tmp\\observability-20261021\\sql-reader-logs');
    SELECT count(*) INTO n FROM ops.app_log();
    IF n<>1 THEN RAISE EXCEPTION 'Windows path normalization failed'; END IF;
    PERFORM ops.repoint_app_log('E:/Repositories/Laplace/.build/.tmp/observability-20261021/sql-reader-logs');
    SELECT count(*) INTO n FROM ops.app_log();
    IF n<>1 THEN RAISE EXCEPTION 'Shared-directory binding compatibility failed'; END IF;
    RAISE NOTICE 'PASS per-role/shared log bindings expose real CSV, preserve other roles and normalize Windows paths';
END $accept$;
