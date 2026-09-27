-- ops.app_log exposes each application role's RFC 4180 CSV log
-- (LaplaceInstall.OpsLogDirectory/laplace-{role}.csv) through the ops_log_files file_fdw
-- server. No log row is written into a substrate table.
--
-- Each role writes its own file, so a role that has never run on this host has none and its
-- foreign table errors on read. ops.app_log() is a function that reads each role under its
-- own exception handler and skips the missing ones. Role filenames are stable (size-rolled
-- archives take timestamped names), so one repoint is enough.

CREATE SCHEMA IF NOT EXISTS ops;
CREATE EXTENSION IF NOT EXISTS file_fdw;
CREATE SERVER IF NOT EXISTS ops_log_files FOREIGN DATA WRAPPER file_fdw;

-- One foreign table per known role; ops.repoint_app_log sets the filenames. The 6-column
-- shape is OpsLogCsvFormatter.Columns.
DO $$
DECLARE r text;
BEGIN
    FOREACH r IN ARRAY ARRAY['cli','mcp','uci','migrations','api'] LOOP
        IF to_regclass('ops.app_log_' || r) IS NULL THEN
            EXECUTE format($ddl$
                CREATE FOREIGN TABLE ops.%I (
                    log_time         timestamptz,
                    application_name text,
                    error_severity   text,
                    category         text,
                    message          text,
                    detail           text
                ) SERVER ops_log_files
                  OPTIONS (filename %L, format 'csv')
            $ddl$, 'app_log_' || r, 'ops_app_log_' || r || '_awaiting_repoint.csv');
        END IF;
    END LOOP;
END $$;

-- Points every per-role table at <p_dir>/laplace-{role}.csv, where p_dir is
-- LaplaceInstall.OpsLogDirectory (LAPLACE_OPS_LOG_DIR). PostgreSQL cannot discover this path,
-- so a deploy step or the API calls it after publish:
--   SELECT ops.repoint_app_log('/opt/laplace/app/logs');
CREATE OR REPLACE FUNCTION ops.repoint_app_log(p_dir text)
    RETURNS text
    LANGUAGE plpgsql AS $$
DECLARE
    r text;
    n int := 0;
BEGIN
    IF p_dir IS NULL OR btrim(p_dir) = '' THEN
        RAISE EXCEPTION 'ops.repoint_app_log: a log directory is required';
    END IF;
    FOREACH r IN ARRAY ARRAY['cli','mcp','uci','migrations','api'] LOOP
        EXECUTE format('ALTER FOREIGN TABLE ops.%I OPTIONS (SET filename %L)',
                       'app_log_' || r, rtrim(p_dir, '/') || '/laplace-' || r || '.csv');
        n := n + 1;
    END LOOP;
    RETURN format('repointed %s role tables at %s', n, p_dir);
END;
$$;

-- Unified read across roles; a role whose file is missing or unreadable is skipped.
-- application_name distinguishes the roles.
CREATE OR REPLACE FUNCTION ops.app_log()
    RETURNS TABLE(
        log_time         timestamptz,
        application_name text,
        error_severity   text,
        category         text,
        message          text,
        detail           text)
    LANGUAGE plpgsql STABLE AS $$
DECLARE r text;
BEGIN
    FOREACH r IN ARRAY ARRAY['cli','mcp','uci','migrations','api'] LOOP
        BEGIN
            RETURN QUERY EXECUTE
                'SELECT log_time, application_name, error_severity, category, message, detail '
                || 'FROM ops.' || quote_ident('app_log_' || r);
        EXCEPTION WHEN OTHERS THEN
            -- file absent (role never ran here) or transiently unreadable — skip this role.
            NULL;
        END;
    END LOOP;
END;
$$;
