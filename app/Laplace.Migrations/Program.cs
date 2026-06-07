using System.Linq;
using DbUp;
using DbUp.Engine;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Laplace.Migrations;

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
        EnsureDatabase.For.PostgresqlDatabase(connectionString);

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

    private static bool Confirmed(string token)
    {
        if (Environment.GetCommandLineArgs().Contains("--yes")) return true;
        if (Environment.GetEnvironmentVariable("LAPLACE_CONFIRM") == token) return true;
        Console.Write($"Type '{token}' to confirm: ");
        return Console.ReadLine() == token;
    }

    private static int RunReset(string connectionString)
    {
        Console.WriteLine("[migrate reset] DROPs the SchemaVersions table — DbUp will re-apply ALL migrations on next 'up'.");
        Console.WriteLine("This does NOT drop the database, extensions, or substrate data.");
        Console.WriteLine("For a full Layer-1 wipe, use 'nuke' instead.");
        if (!Confirmed("RESET"))
        {
            Console.WriteLine("Aborted.");
            return 1;
        }
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
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
        if (!Confirmed("NUKE"))
        {
            Console.WriteLine("Aborted.");
            return 1;
        }

        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        using var conn = new NpgsqlConnection(maintenance.ConnectionString);
        conn.Open();

        using (var term = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE datname = @db AND pid <> pg_backend_pid()", conn))
        {
            term.Parameters.AddWithValue("db", targetDb);
            term.ExecuteNonQuery();
        }

        var quoted = '"' + targetDb.Replace("\"", "\"\"") + '"';
        using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {quoted}", conn))
        {
            drop.ExecuteNonQuery();
            Console.WriteLine($"[migrate nuke] Dropped {targetDb}.");
        }
        using (var create = new NpgsqlCommand(
            $"CREATE DATABASE {quoted} OWNER laplace_admin", conn))
        {
            create.ExecuteNonQuery();
            Console.WriteLine($"[migrate nuke] Re-created empty {targetDb}.");
        }

        Console.WriteLine("[migrate nuke] Done. Run 'up' to re-apply CREATE EXTENSION + grants.");
        return 0;
    }

    private static UpgradeEngine BuildEngine(string connectionString)
    {
        var migrationsDir = LocateMigrationsDir();
        Console.WriteLine($"Migrations directory: {migrationsDir}");

        return DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsFromFileSystem(migrationsDir)
            .JournalToPostgresqlTable("public", "schemaversions")
            .WithVariablesDisabled()
            .WithExecutionTimeout(TimeSpan.FromHours(4))
            .LogToConsole()
            .Build();
    }

    private static string LocateMigrationsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "db", "migrations");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "db", "migrations");
        if (Directory.Exists(cwdCandidate)) return cwdCandidate;
        throw new DirectoryNotFoundException(
            "Could not locate db/migrations/ — run from repo root or pass --migrations-dir.");
    }

    private static string ResolveConnectionString(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--connection-string") return args[i + 1];
        }

        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        var fromEnv = config["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                ? ParsePostgresUrl(fromEnv)
                : fromEnv;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = config["PGHOST"] ?? "/var/run/postgresql",
            Username = config["PGUSER"] ?? "laplace_admin",
            Database = config["PGDATABASE"] ?? "laplace-dev",
        };
        if (int.TryParse(config["PGPORT"], out var port)) builder.Port = port;
        if (!string.IsNullOrWhiteSpace(config["PGPASSWORD"])) builder.Password = config["PGPASSWORD"];

        return builder.ConnectionString;
    }

    private static string ParsePostgresUrl(string url)
    {
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
            """);
        return 64;
    }
}
