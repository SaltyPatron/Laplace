-- The remote runtime login survives database recreation; schema ACLs do not.
-- Restore database-scoped access for roles provisioned outside this database.
-- laplace_runtime and laplace_mono_lichess_* record substrate state and keep the
-- writer contract from runtime/database-role.py. laplace_mono_uci_* only reads.
-- Reconcile missing object privileges only: repeating GRANT ON ALL TABLES locks
-- already-correct ingest tables and makes application activation wait on ingest.
DO $$
DECLARE
    candidate text;
    reader boolean;
    namespace record;
    owner_role text;
    canonical_tables oid[];
    required text;
    objects text;
BEGIN
    -- Scope public write access to the Native substrate and its partitions.
    -- Other public extension tables retain their existing read-only grants.
    WITH RECURSIVE substrate(oid) AS (
        SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relname IN ('entity','physicality','witness','consensus')
          AND c.relkind IN ('r','p')
        UNION
        SELECT i.inhrelid FROM pg_inherits i JOIN substrate s ON s.oid=i.inhparent
    )
    SELECT array_agg(oid) INTO canonical_tables FROM substrate;
    FOR candidate IN
        SELECT rolname FROM pg_roles
        WHERE rolname = 'laplace_runtime'
           OR rolname IN ('laplace_staging_db', 'laplace_desktop_db', 'laplace_mcp_runtime', 'laplace_mono_lichess')
           OR rolname LIKE 'laplace_mono_uci\_%' ESCAPE '\'
           OR rolname LIKE 'laplace_mono_lichess\_%' ESCAPE '\'
        ORDER BY rolname
    LOOP
        IF EXISTS (SELECT FROM pg_roles WHERE rolname = candidate
                   AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)) THEN
            RAISE EXCEPTION 'Substrate runtime must be an unprivileged role: %', candidate;
        END IF;
        -- Lichess records live games through SubstrateAccess.Ingest. Only UCI is read-only.
        reader := candidate LIKE 'laplace_mono_uci\_%' ESCAPE '\';
        IF NOT reader THEN
            IF NOT pg_has_role(candidate,'pg_read_all_stats','MEMBER') THEN
                EXECUTE format('GRANT pg_read_all_stats TO %I',candidate);
            END IF;
            IF NOT pg_has_role(candidate,'pg_signal_backend','MEMBER') THEN
                EXECUTE format('GRANT pg_signal_backend TO %I',candidate);
            END IF;
            IF to_regnamespace('stage') IS NOT NULL AND NOT has_schema_privilege(candidate,'stage','CREATE') THEN
                EXECUTE format('GRANT CREATE ON SCHEMA stage TO %I',candidate);
            END IF;
        END IF;
        IF NOT has_database_privilege(candidate,current_database(),'CONNECT') THEN
            EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I',current_database(),candidate);
        END IF;
        FOR namespace IN
            SELECT oid, nspname, pg_get_userbyid(nspowner) AS owner FROM pg_namespace
            WHERE nspname NOT LIKE 'pg_%' AND nspname NOT IN ('information_schema', 'app')
        LOOP
            IF NOT has_schema_privilege(candidate,namespace.oid,'USAGE') THEN
                EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I',namespace.nspname,candidate);
            END IF;
            FOREACH required IN ARRAY ARRAY['SELECT','INSERT','UPDATE','DELETE','MAINTAIN'] LOOP
                SELECT string_agg(format('%I.%I',namespace.nspname,c.relname),', ' ORDER BY c.oid)
                INTO objects FROM pg_class c
                WHERE c.relnamespace=namespace.oid AND c.relkind IN ('r','p','v','m','f')
                  AND (required='SELECT' OR (NOT reader AND
                       (namespace.nspname<>'public' OR c.oid=ANY(canonical_tables))))
                  AND CASE WHEN c.relkind IN ('r','p','v','m','f')
                      THEN NOT has_table_privilege(candidate,c.oid,required) ELSE false END;
                IF objects IS NOT NULL THEN
                    EXECUTE format('GRANT %s ON TABLE %s TO %I',required,objects,candidate);
                END IF;
            END LOOP;
            FOREACH required IN ARRAY ARRAY['USAGE','SELECT'] LOOP
                SELECT string_agg(format('%I.%I',namespace.nspname,c.relname),', ' ORDER BY c.oid)
                INTO objects FROM pg_class c WHERE c.relnamespace=namespace.oid AND c.relkind='S'
                  AND CASE WHEN c.relkind='S'
                      THEN NOT has_sequence_privilege(candidate,c.oid,required) ELSE false END;
                IF objects IS NOT NULL THEN
                    EXECUTE format('GRANT %s ON SEQUENCE %s TO %I',required,objects,candidate);
                END IF;
            END LOOP;
            SELECT string_agg(format('%I.%I(%s)',namespace.nspname,p.proname,
                                    pg_get_function_identity_arguments(p.oid)),', ' ORDER BY p.oid)
            INTO objects FROM pg_proc p WHERE p.pronamespace=namespace.oid AND p.prokind IN ('f','a','w')
              AND NOT has_function_privilege(candidate,p.oid,'EXECUTE');
            IF objects IS NOT NULL THEN
                EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO %I',objects,candidate);
            END IF;
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
