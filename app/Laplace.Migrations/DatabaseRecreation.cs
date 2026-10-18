using Npgsql;

namespace Laplace.Migrations;

/// <summary>Prepare a migrated database before replacing the selected database's name.</summary>
internal static class DatabaseRecreation
{
    private static string Identifier(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static void Run(string connectionString, Func<string, int> migrate)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        string name = target.Database ?? throw new InvalidOperationException("Database name is required.");
        if (name is "postgres" or "template0" or "template1")
            throw new InvalidOperationException("The maintenance and system template databases cannot be recreated.");
        var maintenance = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = "postgres" };
        using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        connection.Open();

        // Catalog identifiers and literals are quoted by PostgreSQL; no host-specific owner or paths.
        string createOptions = Scalar(connection, """
            SELECT format('OWNER %I TEMPLATE template0 ENCODING %L LOCALE_PROVIDER %s LC_COLLATE %L LC_CTYPE %L TABLESPACE %I CONNECTION LIMIT %s',
                pg_get_userbyid(d.datdba), pg_encoding_to_char(d.encoding),
                CASE d.datlocprovider WHEN 'i' THEN 'icu' WHEN 'b' THEN 'builtin' ELSE 'libc' END,
                d.datcollate, d.datctype, t.spcname, d.datconnlimit)
                || CASE WHEN d.datlocprovider='i' THEN format(' ICU_LOCALE %L',d.datlocale)
                        WHEN d.datlocprovider='b' THEN format(' BUILTIN_LOCALE %L',d.datlocale) ELSE '' END
                || CASE WHEN d.daticurules IS NOT NULL THEN format(' ICU_RULES %L',d.daticurules) ELSE '' END
                || CASE WHEN d.datcollversion IS NOT NULL THEN format(' COLLATION_VERSION %L',d.datcollversion) ELSE '' END
            FROM pg_database d JOIN pg_tablespace t ON t.oid=d.dattablespace
            WHERE d.datname=@name AND NOT d.datistemplate AND d.datallowconn AND d.datconnlimit>=-1
            """, name) ?? throw new InvalidOperationException(
                "The selected database must exist and accept connections. Install or repair it before recreation.");

        string replacement = "laplace_recreate_" + Guid.NewGuid().ToString("N");
        string retired = "laplace_retired_" + Guid.NewGuid().ToString("N");
        var settings = Rows(connection, """
            SELECT CASE WHEN s.setrole=0 THEN 'ALTER DATABASE ' ELSE format('ALTER ROLE %I IN DATABASE ',pg_get_userbyid(s.setrole)) END,
                   format(' SET %I TO %L',split_part(v,'=',1),substr(v,strpos(v,'=')+1))
            FROM pg_db_role_setting s JOIN pg_database d ON d.oid=s.setdatabase,
                 LATERAL unnest(s.setconfig) v
            WHERE d.datname=@name ORDER BY s.setrole,v
            """, name);
        var databaseAccess = Rows(connection, """
            SELECT 'GRANT '||a.privilege_type||' ON DATABASE ',
                   ' TO '||CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(a.grantee)) END
                   ||CASE WHEN a.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END
            FROM pg_database d, LATERAL aclexplode(COALESCE(d.datacl,acldefault('d',d.datdba))) a
            WHERE d.datname=@name
            """, name);
        List<(string Prefix, string Suffix)> access;
        using (var original = new NpgsqlConnection(target.ConnectionString))
        {
            original.Open();
            access = Rows(original, AccessSql, null);
        }

        bool created = false, switched = false, connectionsBlocked = false;
        try
        {
            Execute(connection, $"CREATE DATABASE {Identifier(replacement)} {createOptions}");
            created = true;
            foreach (var (prefix, suffix) in settings)
                Execute(connection, prefix + Identifier(replacement) + suffix);
            Execute(connection, $"REVOKE ALL ON DATABASE {Identifier(replacement)} FROM PUBLIC");
            foreach (var (prefix, suffix) in databaseAccess)
                Execute(connection, prefix + Identifier(replacement) + suffix);

            var prepared = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = replacement };
            if (migrate(prepared.ConnectionString) != 0)
                throw new InvalidOperationException("Replacement migrations failed; the original database was preserved.");
            using (var db = new NpgsqlConnection(prepared.ConnectionString))
            {
                db.Open();
                var objects = Rows(db, ObjectSql + " SELECT target,'' FROM objects", null)
                    .Select(static row => row.Prefix).ToHashSet(StringComparer.Ordinal);
                using var tx = db.BeginTransaction();
                // Recreating discards objects outside the installed migrations as well as their data.
                foreach (var statements in access.Where(row => row.Suffix.Length == 0 || objects.Contains(row.Suffix)).Chunk(256))
                {
                    using var batch = new NpgsqlBatch(db, tx) { Timeout = 60 };
                    foreach (var (statement, _) in statements)
                        batch.BatchCommands.Add(new NpgsqlBatchCommand(statement));
                    batch.ExecuteNonQuery();
                }
                tx.Commit();
            }

            // Commit the connection block before terminating sessions so application pools cannot
            // reconnect between termination and RENAME. Failed cutover restores the original name
            // transactionally and reopens it below; preparation never disconnects serving clients.
            Execute(connection, $"ALTER DATABASE {Identifier(name)} ALLOW_CONNECTIONS false");
            connectionsBlocked = true;
            Execute(connection, $"ALTER DATABASE {Identifier(replacement)} ALLOW_CONNECTIONS false");
            Execute(connection, "SELECT pg_terminate_backend(pid,5000) FROM pg_stat_activity WHERE datname IN ("
                + Literal(name) + "," + Literal(replacement) + ") AND pid<>pg_backend_pid()");
            using (var tx = connection.BeginTransaction())
            {
                Execute(connection, $"ALTER DATABASE {Identifier(name)} RENAME TO {Identifier(retired)}", tx);
                Execute(connection, $"ALTER DATABASE {Identifier(replacement)} RENAME TO {Identifier(name)}", tx);
                Execute(connection, $"ALTER DATABASE {Identifier(name)} ALLOW_CONNECTIONS true", tx);
                tx.Commit();
                switched = true;
            }
            Execute(connection, $"DROP DATABASE {Identifier(retired)} WITH (FORCE)");
        }
        catch
        {
            if (connectionsBlocked && !switched)
            {
                try { Execute(connection, $"ALTER DATABASE {Identifier(name)} ALLOW_CONNECTIONS true"); }
                catch (Exception reopen) { Console.Error.WriteLine($"Reopening original database {name} failed: {reopen.Message}"); }
            }
            if (created && !switched)
            {
                try { Execute(connection, $"DROP DATABASE {Identifier(replacement)} WITH (FORCE)"); }
                catch (Exception cleanup) { Console.Error.WriteLine($"Replacement cleanup failed for {replacement}: {cleanup.Message}"); }
            }
            if (switched)
                Console.Error.WriteLine($"Replacement is serving as {name}; retired database cleanup must complete: {retired}.");
            throw;
        }
    }

    public static void RunApplicationSchema(string connectionString, Func<string, int> migrate)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        _ = target.Database ?? throw new InvalidOperationException("Database name is required.");
        List<(string Prefix, string Suffix)> access;
        string? schemaOwner;
        using (var db = new NpgsqlConnection(target.ConnectionString))
        {
            db.Open();
            schemaOwner = Scalar(db, "SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname=@name", "app");
            access = Rows(db, AccessSql, null)
                .Where(static row => row.Suffix == "SCHEMA app" || row.Suffix.Contains(" app.", StringComparison.Ordinal))
                .ToList();
            using var tx = db.BeginTransaction();
            Execute(db, """
                DROP SCHEMA IF EXISTS app CASCADE;
                DO $$
                BEGIN
                    IF to_regclass('public.schemaversions') IS NOT NULL THEN
                        DELETE FROM public.schemaversions
                        WHERE scriptname ~ '^[0-9]+_(app_|browser_)';
                    END IF;
                END $$;
                """, tx);
            if (!string.IsNullOrWhiteSpace(schemaOwner))
                Execute(db, $"CREATE SCHEMA app AUTHORIZATION {Identifier(schemaOwner)}", tx);
            else
                Execute(db, "CREATE SCHEMA app", tx);
            tx.Commit();
        }

        if (migrate(target.ConnectionString) != 0)
            throw new InvalidOperationException("Application schema migrations failed.");

        using (var db = new NpgsqlConnection(target.ConnectionString))
        {
            db.Open();
            var objects = Rows(db, ObjectSql + " SELECT target,'' FROM objects", null)
                .Select(static row => row.Prefix).ToHashSet(StringComparer.Ordinal);
            using var tx = db.BeginTransaction();
            foreach (var statements in access.Where(row => row.Suffix.Length == 0 || objects.Contains(row.Suffix)).Chunk(256))
            {
                using var batch = new NpgsqlBatch(db, tx) { Timeout = 60 };
                foreach (var (statement, _) in statements)
                    batch.BatchCommands.Add(new NpgsqlBatchCommand(statement));
                batch.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static void Execute(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
        command.ExecuteNonQuery();
    }

    private static string? Scalar(NpgsqlConnection connection, string sql, string name)
    {
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("name", name);
        return command.ExecuteScalar() as string;
    }

    private static List<(string Prefix, string Suffix)> Rows(NpgsqlConnection connection, string sql, string? name)
    {
        using var command = new NpgsqlCommand(sql, connection);
        if (name is not null) command.Parameters.AddWithValue("name", name);
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    // Access is catalog state, independent of credentials. Restore explicit object ACLs and
    // default privileges after migrations, before the replacement receives the serving name.
    private const string ObjectSql = """
        WITH objects AS (
            SELECT 'SCHEMA '||quote_ident(nspname) AS target,nspacl AS acl,nspowner AS owner
            FROM pg_namespace WHERE nspname !~ '^pg_' AND nspname<>'information_schema'
            UNION ALL
            SELECT CASE WHEN c.relkind='S' THEN 'SEQUENCE ' ELSE 'TABLE ' END||format('%I.%I',n.nspname,c.relname),c.relacl,c.relowner
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema' AND c.relkind IN ('r','p','v','m','f','S')
            UNION ALL
            SELECT CASE WHEN p.prokind='p' THEN 'PROCEDURE ' ELSE 'FUNCTION ' END||format('%I.%I(%s)',n.nspname,p.proname,pg_get_function_identity_arguments(p.oid)),p.proacl,p.proowner
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema'
            UNION ALL
            SELECT 'TYPE '||format('%I.%I',n.nspname,t.typname),t.typacl,t.typowner
            FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace
            WHERE n.nspname !~ '^pg_' AND n.nspname<>'information_schema'
        )
        """;

    private const string AccessSql = ObjectSql + """
        , defaults AS (
            SELECT 'ALTER DEFAULT PRIVILEGES FOR ROLE '||quote_ident(pg_get_userbyid(d.defaclrole))
                ||CASE WHEN n.oid IS NULL THEN '' ELSE ' IN SCHEMA '||quote_ident(n.nspname) END AS prefix,
                CASE d.defaclobjtype WHEN 'r' THEN 'TABLES' WHEN 'S' THEN 'SEQUENCES' WHEN 'f' THEN 'FUNCTIONS'
                    WHEN 'T' THEN 'TYPES' WHEN 'n' THEN 'SCHEMAS' END AS kind,d.defaclacl AS acl,
                CASE WHEN n.oid IS NULL THEN '' ELSE 'SCHEMA '||quote_ident(n.nspname) END AS target
            FROM pg_default_acl d LEFT JOIN pg_namespace n ON n.oid=d.defaclnamespace
        ), statements AS (
            SELECT 0 AS ordinal,'REVOKE ALL ON '||target||' FROM PUBLIC' AS sql,target FROM objects WHERE acl IS NOT NULL
            UNION ALL
            SELECT 1,'GRANT '||a.privilege_type||' ON '||o.target||' TO '
                ||CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(a.grantee)) END
                ||CASE WHEN a.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END,o.target
            FROM objects o,LATERAL aclexplode(o.acl) a
            UNION ALL
            SELECT 2,prefix||' REVOKE ALL ON '||kind||' FROM PUBLIC',target FROM defaults
            UNION ALL
            SELECT 3,d.prefix||' GRANT '||a.privilege_type||' ON '||d.kind||' TO '
                ||CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(a.grantee)) END
                ||CASE WHEN a.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END,d.target
            FROM defaults d,LATERAL aclexplode(d.acl) a
        ) SELECT sql,target FROM statements ORDER BY ordinal,sql
        """;
}
