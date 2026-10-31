BEGIN;
CREATE ROLE laplace_log_permission_acceptance;
GRANT USAGE ON SCHEMA ops TO laplace_log_permission_acceptance;
DO $accept$ BEGIN
    BEGIN
        SET LOCAL ROLE laplace_log_permission_acceptance;
        PERFORM * FROM ops.app_log();
        RAISE EXCEPTION 'Permission error was hidden as an empty result';
    EXCEPTION WHEN insufficient_privilege THEN
        RESET ROLE;
        RAISE NOTICE 'PASS application log reader propagates permission denial';
    END;
END $accept$;
ROLLBACK;
