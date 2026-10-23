using Npgsql;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using System.Text.RegularExpressions;

namespace Laplace.Migrations;

/// <summary>Prepare a migrated database before replacing the selected database's name.</summary>
internal static class DatabaseRecreation
{
    private static string Identifier(string value) => '"' + value.Replace("\"", "\"\"") + '"';

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
        string createOptions = Scalar(connection, "migration.database_options", name) ?? throw new InvalidOperationException(
                "The selected database must exist and accept connections. Install or repair it before recreation.");

        string replacement = "laplace_recreate_" + Guid.NewGuid().ToString("N");
        string retired = "laplace_retired_" + Guid.NewGuid().ToString("N");
        var settings = Rows(connection, "migration.database_settings", name);
        var databaseAccess = Rows(connection, "migration.database_access", name);
        List<(string Prefix, string Suffix)> access;
        using (var original = new NpgsqlConnection(target.ConnectionString))
        {
            original.Open();
            access = Rows(original, "migration.object_access", null);
        }

        bool created = false, switched = false, connectionsBlocked = false;
        try
        {
            Execute(connection, Statement("migration.database_create", ("database", Identifier(replacement)), ("options", createOptions)));
            created = true;
            foreach (var (prefix, suffix) in settings)
                Execute(connection, prefix + Identifier(replacement) + suffix);
            Execute(connection, Statement("migration.database_revoke_public", ("database", Identifier(replacement))));
            foreach (var (prefix, suffix) in databaseAccess)
                Execute(connection, prefix + Identifier(replacement) + suffix);

            var prepared = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = replacement };
            if (migrate(prepared.ConnectionString) != 0)
                throw new InvalidOperationException("Replacement migrations failed; the original database was preserved.");
            using (var db = new NpgsqlConnection(prepared.ConnectionString))
            {
                db.Open();
                var objects = Rows(db, "migration.object_targets", null)
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
            Execute(connection, Statement("migration.database_block", ("database", Identifier(name))));
            connectionsBlocked = true;
            Execute(connection, Statement("migration.database_block", ("database", Identifier(replacement))));
            using (var terminate = NpgsqlCatalog.Command(connection, null, "migration.cutover_sessions",
                new[] { name, replacement }))
                terminate.ExecuteNonQuery();
            using (var tx = connection.BeginTransaction())
            {
                Execute(connection, Statement("migration.database_rename", ("database", Identifier(name)), ("replacement", Identifier(retired))), tx);
                Execute(connection, Statement("migration.database_rename", ("database", Identifier(replacement)), ("replacement", Identifier(name))), tx);
                Execute(connection, Statement("migration.database_reopen", ("database", Identifier(name))), tx);
                tx.Commit();
                switched = true;
            }
            Execute(connection, Statement("migration.database_drop", ("database", Identifier(retired))));
        }
        catch
        {
            if (connectionsBlocked && !switched)
            {
                try { Execute(connection, Statement("migration.database_reopen", ("database", Identifier(name)))); }
                catch (Exception reopen) { Console.Error.WriteLine($"Reopening original database {name} failed: {reopen.Message}"); }
            }
            if (created && !switched)
            {
                try { Execute(connection, Statement("migration.database_drop", ("database", Identifier(replacement)))); }
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
            schemaOwner = Scalar(db, "migration.schema_owner", "app");
            access = Rows(db, "migration.object_access", null)
                .Where(static row => row.Suffix == "SCHEMA app" || row.Suffix.Contains(" app.", StringComparison.Ordinal))
                .ToList();
            using var tx = db.BeginTransaction();
            Execute(db, SqlCatalog.Get("migration.reset_application_schema").Text, tx);
            if (!string.IsNullOrWhiteSpace(schemaOwner))
                Execute(db, Statement("migration.application_schema_owner", ("owner", Identifier(schemaOwner))), tx);
            else
                Execute(db, SqlCatalog.Get("migration.application_schema").Text, tx);
            tx.Commit();
        }

        if (migrate(target.ConnectionString) != 0)
            throw new InvalidOperationException("Application schema migrations failed.");

        using (var db = new NpgsqlConnection(target.ConnectionString))
        {
            db.Open();
            var objects = Rows(db, "migration.object_targets", null)
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

    private static string Statement(string queryName, params (string Name, string Value)[] substitutions)
    {
        var fields = substitutions.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        string template = SqlCatalog.Get(queryName).Text;
        return Regex.Replace(template, @"\{([a-z_]+)\}", match =>
            fields.TryGetValue(match.Groups[1].Value, out string? value)
                ? value : throw new ArgumentException("A catalog DDL slot was not supplied.", nameof(substitutions)));
    }

    private static void Execute(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
        command.ExecuteNonQuery();
    }

    private static string? Scalar(NpgsqlConnection connection, string queryName, string name)
    {
        using var command = NpgsqlCatalog.Command(connection, null, queryName, name);
        return command.ExecuteScalar() as string;
    }

    private static List<(string Prefix, string Suffix)> Rows(NpgsqlConnection connection, string queryName, string? name)
    {
        using var command = name is null
            ? NpgsqlCatalog.Command(connection, null, queryName)
            : NpgsqlCatalog.Command(connection, null, queryName, name);
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

}
