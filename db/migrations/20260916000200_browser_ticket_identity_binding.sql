-- The INSERT candidate carries the account declared by the new encrypted
-- ticket. Validate it before ON CONFLICT updates only the ticket columns;
-- an UPDATE-only trigger cannot see the discarded incoming identity fields.
CREATE OR REPLACE FUNCTION app.check_browser_ticket_identity()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, app
AS $function$
DECLARE
    bound_user uuid;
    bound_tenant text;
BEGIN
    SELECT user_id, tenant_id INTO bound_user, bound_tenant
    FROM app.web_sessions WHERE session_id = NEW.session_id FOR UPDATE;
    IF FOUND AND (bound_user IS DISTINCT FROM NEW.user_id
                  OR bound_tenant IS DISTINCT FROM NEW.tenant_id) THEN
        RAISE EXCEPTION 'A browser session cannot be rebound to another identity; sign out before signing in again'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$function$;
DROP TRIGGER IF EXISTS check_browser_ticket_identity ON app.web_sessions;
CREATE TRIGGER check_browser_ticket_identity
    BEFORE INSERT ON app.web_sessions
    FOR EACH ROW EXECUTE FUNCTION app.check_browser_ticket_identity();
