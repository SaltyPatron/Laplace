using System.Linq;
using DbUp;
using DbUp.Engine;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Laplace.Migrations;

/// <summary>
/// DbUp-based migration runner for Laplace's Layer 1 — extension lifecycle
/// orchestration. Per ADR 0021 (DbUp + Npgsql) + ADR 0023 (extension owns
/// schema; DbUp orchestrates).
///
/// Scope: ensure the 'laplace' database exists, CREATE EXTENSION postgis +
/// laplace (or ALTER EXTENSION laplace UPDATE), apply role grants. The
/// substrate schema itself (entities/physicalities/attestations) is owned
/// by the laplace extension's .sql files, NOT by DbUp migrations.
///
/// Usage:
///   dotnet run --project app/Laplace.Migrations -- [up|status|reset|nuke]
///
/// Commands:
///   up      EnsureDatabase + apply pending migrations (default)
///   status  Show applied + pending migrations
///   reset   Drop the DbUp 'SchemaVersions' table — re-applies on next 'up'.
///           Does NOT drop the database or extensions.
///   nuke    DROP DATABASE laplace + re-create empty. Full Layer-1 reset.
///           Requires typing 'NUKE' to confirm.
///
/// Connection is read from (priority order):
///   1. --connection-string &lt;value&gt; CLI arg
///   2. DATABASE_URL env var (Npgsql connection-string format or postgres:// URL)
///   3. PG_* env vars (PGHOST, PGUSER, PGDATABASE, PGPORT)
///   4. Default: peer auth → laplace_admin role on local socket, db=laplace
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "up";
        var connectionString = ResolveConnectionString(args);

        Console.WriteLine($"Laplace.Migrations: command={command}");
        Console.WriteLine($"Target database connection: {Mask(connectionString)}");

        try
        {
            return command switch
            {
                "up" => RunUp(connectionString),
                "status" => RunStatus(connectionString),
                "reset" => RunReset(connectionString),
                "nuke" => RunNuke(connectionString),
                _ => Usage()
            };
        }
        catch (NpgsqlException ex)
        {
            Console.Error.WriteLine($"[NpgsqlException] {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int RunUp(string connectionString)
    {
        // EnsureDatabase from a custom TEMPLATE database. DbUp's default
        // EnsureDatabase.For.PostgresqlDatabase() creates an EMPTY database;
        // that's wrong for us because the laplace DB needs postgis +
        // laplace_priv before any migration can run, and Layer 0 sets those
        // up in `template_laplace` so cloned laplace DBs inherit them
        // without needing superuser. See bootstrap_pg_database_and_postgis.
        EnsureDatabaseFromTemplate(connectionString, templateName: "template_laplace");

        var engine = BuildEngine(connectionString);
        var result = engine.PerformUpgrade();

        if (!result.Successful)
        {
            Console.Error.WriteLine($"[migrate up FAILED] {result.Error?.Message}");
            return 1;
        }

        var applied = result.Scripts.ToList();
        if (applied.Count == 0)
        {
            Console.WriteLine("[migrate up] No pending migrations. Database is current.");
        }
        else
        {
            Console.WriteLine($"[migrate up] Applied {applied.Count} migration(s):");
            foreach (var script in applied)
            {
                Console.WriteLine($"  ✓ {script.Name}");
            }
        }
        return 0;
    }

    private static int RunStatus(string connectionString)
    {
        var engine = BuildEngine(connectionString);
        var pending = engine.GetScriptsToExecute().ToList();
        var applied = engine.GetExecutedScripts().ToList();

        Console.WriteLine($"[migrate status] applied: {applied.Count}, pending: {pending.Count}");
        Console.WriteLine();
        if (applied.Count > 0)
        {
            Console.WriteLine("Applied:");
            foreach (var name in applied) Console.WriteLine($"  ✓ {name}");
            Console.WriteLine();
        }
        if (pending.Count > 0)
        {
            Console.WriteLine("Pending:");
            foreach (var script in pending) Console.WriteLine($"  · {script.Name}");
        }
        return 0;
    }

    private static int RunReset(string connectionString)
    {
        Console.WriteLine("[migrate reset] DROPs the SchemaVersions table — DbUp will re-apply ALL migrations on next 'up'.");
        Console.WriteLine("This does NOT drop the database, extensions, or substrate data.");
        Console.WriteLine("For a full Layer-1 wipe, use 'nuke' instead.");
        Console.Write("Type 'RESET' to confirm: ");
        if (Console.ReadLine() != "RESET")
        {
            Console.WriteLine("Aborted.");
            return 1;
        }
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        // DbUp's default CREATE TABLE for the journal uses the unquoted
        // identifier `SchemaVersions`, which PostgreSQL case-folds to
        // `schemaversions`. A quoted DROP "SchemaVersions" matches
        // nothing (silently no-ops via IF EXISTS), leaving the journal
        // intact and the next `up` reporting "No new scripts." Match the
        // case-folded name explicitly. BuildEngine() pins this in code
        // via JournalToPostgresqlTable("public", "schemaversions") so
        // this contract isn't relying on PG's case-folding default.
        using var cmd = new NpgsqlCommand(
            "DROP TABLE IF EXISTS public.schemaversions", conn);
        cmd.ExecuteNonQuery();
        Console.WriteLine("[migrate reset] schemaversions dropped. Run 'up' to re-apply.");
        return 0;
    }

    private static int RunNuke(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var targetDb = b.Database
            ?? throw new InvalidOperationException("Target database name missing from connection string.");

        Console.WriteLine($"[migrate nuke] DROP DATABASE \"{targetDb}\" + re-create empty.");
        Console.WriteLine("Loses ALL substrate data: entities, physicalities, attestations.");
        Console.WriteLine("Loses the extensions (CREATE EXTENSION must run again on next 'up').");
        Console.Write("Type 'NUKE' to confirm: ");
        if (Console.ReadLine() != "NUKE")
        {
            Console.WriteLine("Aborted.");
            return 1;
        }

        // Connect to maintenance DB to drop + recreate the target.
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        using var conn = new NpgsqlConnection(maintenance.ConnectionString);
        conn.Open();

        // Terminate other sessions on the target DB so DROP isn't blocked.
        using (var term = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE datname = @db AND pid <> pg_backend_pid()", conn))
        {
            term.Parameters.AddWithValue("db", targetDb);
            term.ExecuteNonQuery();
        }

        // Quote the database name to defend against any future weird naming.
        var quoted = '"' + targetDb.Replace("\"", "\"\"") + '"';
        using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {quoted}", conn))
        {
            drop.ExecuteNonQuery();
            Console.WriteLine($"[migrate nuke] Dropped {targetDb}.");
        }
        // Clone from template_laplace (set up by Layer 0 bootstrap_pg_database_and_postgis).
        // The template already has postgis + laplace_priv installed, so the
        // recreated DB inherits both without needing superuser — laplace_admin
        // is DB owner of template_laplace and so allowed to use it as TEMPLATE.
        // Without this, the next `db-up` would fail with "schema laplace_priv
        // does not exist" because Layer-0 per-DB state died with the DROP.
        using (var create = new NpgsqlCommand(
            $"CREATE DATABASE {quoted} TEMPLATE template_laplace OWNER laplace_admin", conn))
        {
            create.ExecuteNonQuery();
            Console.WriteLine($"[migrate nuke] Re-created {targetDb} from template_laplace (postgis + laplace_priv inherited).");
        }

        Console.WriteLine("[migrate nuke] Done. Run 'up' to re-apply CREATE EXTENSION + grants.");
        return 0;
    }

    /// <summary>
    /// EnsureDatabase that, if the target DB doesn't exist, creates it via
    /// CREATE DATABASE ... TEMPLATE &lt;templateName&gt; OWNER laplace_admin.
    /// Allows the target to inherit prebuilt per-DB state (postgis +
    /// laplace_priv) from a Layer-0-managed template, so per-DB recovery
    /// (drop laplace → recreate) doesn't require a Layer-0 sudo re-run.
    /// Idempotent: no-op when target DB already exists.
    /// </summary>
    private static void EnsureDatabaseFromTemplate(string connectionString,
                                                    string templateName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var targetDb = builder.Database
            ?? throw new InvalidOperationException("Target database name missing from connection string.");

        // Connect to the maintenance DB ('postgres') as our role for the check + create.
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        using var conn = new NpgsqlConnection(maintenance.ConnectionString);
        conn.Open();

        using (var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db", conn))
        {
            check.Parameters.AddWithValue("db", targetDb);
            if (check.ExecuteScalar() != null)
            {
                // Already exists — leave alone, db-up is non-destructive.
                return;
            }
        }

        // Defensive quoting against unusual names (the connection string is
        // user-controlled in principle; keep this safe).
        string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";
        using var create = new NpgsqlCommand(
            $"CREATE DATABASE {Quote(targetDb)} TEMPLATE {Quote(templateName)} OWNER laplace_admin",
            conn);
        create.ExecuteNonQuery();
        Console.WriteLine($"[ensure-database] Created {targetDb} from TEMPLATE {templateName}.");
    }

    private static UpgradeEngine BuildEngine(string connectionString)
    {
        var migrationsDir = LocateMigrationsDir();
        Console.WriteLine($"Migrations directory: {migrationsDir}");

        return DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsFromFileSystem(migrationsDir)
            // Pin the journal table name to lowercase explicitly so the
            // identity is documented in code instead of relying on
            // PostgreSQL's unquoted-identifier case-folding. Matches what
            // DbUp produces by default; RunReset() drops by this same name.
            .JournalToPostgresqlTable("public", "schemaversions")
            .LogToConsole()
            .Build();
    }

    private static string LocateMigrationsDir()
    {
        // Walk up from the assembly location looking for the repo's db/migrations dir.
        // CI sets working dir to repo root, but local invocations may run from elsewhere.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "db", "migrations");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        // Fallback to CWD/db/migrations.
        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "db", "migrations");
        if (Directory.Exists(cwdCandidate)) return cwdCandidate;
        throw new DirectoryNotFoundException(
            "Could not locate db/migrations/ — run from repo root or pass --migrations-dir.");
    }

    private static string ResolveConnectionString(string[] args)
    {
        // CLI override
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--connection-string") return args[i + 1];
        }

        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        var fromEnv = config["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            // DATABASE_URL may be in URL format (postgres://...) or Npgsql key-value format.
            return fromEnv.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                ? ParsePostgresUrl(fromEnv)
                : fromEnv;
        }

        // PG_* env vars (libpq convention)
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = config["PGHOST"] ?? "/var/run/postgresql",
            Username = config["PGUSER"] ?? "laplace_admin",
            Database = config["PGDATABASE"] ?? "laplace",
        };
        if (int.TryParse(config["PGPORT"], out var port)) builder.Port = port;
        if (!string.IsNullOrWhiteSpace(config["PGPASSWORD"])) builder.Password = config["PGPASSWORD"];

        return builder.ConnectionString;
    }

    private static string ParsePostgresUrl(string url)
    {
        // postgres://user:password@host:port/dbname?param=value
        var uri = new Uri(url);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
        };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            b.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1) b.Password = Uri.UnescapeDataString(parts[1]);
        }
        return b.ConnectionString;
    }

    private static string Mask(string connectionString)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(b.Password)) b.Password = "***";
            return b.ConnectionString;
        }
        catch
        {
            return "<unparseable connection string>";
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            Laplace.Migrations — DbUp runner (Layer 1 — extension lifecycle)

            Usage:
              dotnet run --project app/Laplace.Migrations -- [command]

            Commands:
              up        EnsureDatabase + apply all pending migrations (default)
              status    Show applied vs pending migrations
              reset     Drop SchemaVersions (re-applies migrations; preserves DB)
              nuke      DROP DATABASE + re-create empty (full Layer-1 wipe)

            Connection (priority order):
              --connection-string <value>
              DATABASE_URL env var
              PG_* env vars (PGHOST, PGUSER, PGDATABASE, PGPORT, PGPASSWORD)
              Default: peer auth → laplace_admin on /var/run/postgresql, db=laplace

            See ADR 0021 (DbUp + Npgsql) + ADR 0023 (extension owns schema).
            """);
        return 64;
    }
}
