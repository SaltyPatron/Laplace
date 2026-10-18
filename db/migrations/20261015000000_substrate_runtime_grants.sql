-- The remote runtime login survives database recreation; schema ACLs do not.
-- Restore database-scoped access for the writer role and for the UCI and Lichess
-- reader roles provisioned outside this database. Readers get SELECT only.
-- Writers keep the runtime contract from runtime/database-role.py.
DO $$
DECLARE
    candidate text;
    reader boolean;
    namespace record;
    owner_role text;
BEGIN
    FOR candidate IN
        SELECT rolname FROM pg_roles
        WHERE rolname = 'laplace_runtime'
           OR rolname LIKE 'laplace_mono_uci\_%' ESCAPE '\'
           OR rolname LIKE 'laplace_mono_lichess\_%' ESCAPE '\'
        ORDER BY rolname
    LOOP
        IF EXISTS (SELECT FROM pg_roles WHERE rolname = candidate
                   AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)) THEN
            RAISE EXCEPTION 'Substrate runtime must be an unprivileged role: %', candidate;
        END IF;
        reader := candidate LIKE 'laplace_mono_uci\_%' ESCAPE '\'
               OR candidate LIKE 'laplace_mono_lichess\_%' ESCAPE '\';
        IF NOT reader THEN
            EXECUTE format('GRANT pg_read_all_stats, pg_signal_backend TO %I', candidate);
        END IF;
        EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), candidate);
        FOR namespace IN
            SELECT oid, nspname, pg_get_userbyid(nspowner) AS owner FROM pg_namespace
            WHERE nspname NOT LIKE 'pg_%' AND nspname NOT IN ('information_schema', 'app')
        LOOP
            EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I', namespace.nspname, candidate);
            IF reader THEN
                EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO %I', namespace.nspname, candidate);
            ELSE
                EXECUTE format('GRANT %s ON ALL TABLES IN SCHEMA %I TO %I',
                    CASE WHEN namespace.nspname = 'public' THEN 'SELECT' ELSE 'SELECT,INSERT,UPDATE,DELETE,MAINTAIN' END,
                    namespace.nspname, candidate);
            END IF;
            EXECUTE format('GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA %I TO %I', namespace.nspname, candidate);
            EXECUTE format('GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA %I TO %I', namespace.nspname, candidate);
            IF reader OR namespace.nspname = 'public' THEN
                CONTINUE;
            END IF;
            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT SELECT,INSERT,UPDATE,DELETE,MAINTAIN ON TABLES TO %I',
                namespace.nspname, candidate);
            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA %I GRANT USAGE,SELECT ON SEQUENCES TO %I',
                namespace.nspname, candidate);
            FOR owner_role IN
                SELECT namespace.owner
                UNION
                SELECT DISTINCT pg_get_userbyid(c.relowner) FROM pg_class c
                WHERE c.relnamespace = namespace.oid AND c.relkind IN ('r', 'p')
            LOOP
                IF owner_role <> current_user AND owner_role <> candidate
                   AND pg_has_role(owner_role, 'MEMBER') THEN
                    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I GRANT MAINTAIN ON TABLES TO %I',
                        owner_role, namespace.nspname, candidate);
                ELSIF owner_role <> current_user AND owner_role <> candidate THEN
                    RAISE EXCEPTION 'Cannot grant default MAINTAIN for owner % in schema %: % is not a member',
                        owner_role, namespace.nspname, current_user;
                END IF;
            END LOOP;
        END LOOP;
    END LOOP;
END $$;
