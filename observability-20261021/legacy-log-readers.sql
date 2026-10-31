CREATE SCHEMA IF NOT EXISTS ops;
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
            NULL;
        END;
    END LOOP;
END;
$$;
CREATE OR REPLACE FUNCTION ops.chess_drops()
    RETURNS TABLE(
        log_time     timestamptz,
        source_name  text,
        reason       text,
        dropped      bigint,
        kept         bigint,
        seen         bigint,
        drop_pct     double precision)
    LANGUAGE plpgsql
    STABLE
AS $$
BEGIN
    BEGIN
        RETURN QUERY
            SELECT f.log_time, f.source_name, f.reason, f.dropped, f.kept, f.seen, f.drop_pct
            FROM ops.chess_drops_ft f;
    EXCEPTION WHEN OTHERS THEN
        NULL;
    END;
END;
$$;

