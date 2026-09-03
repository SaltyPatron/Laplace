using System.Linq;
using DbUp;
using DbUp.Engine;
using Laplace.Engine.Core;
using Microsoft.Extensions.Logging;
using Npgsql;
using Spectre.Console.Cli;

namespace Laplace.Migrations;

internal static class Program
{
    // Spectre.Console.Cli entrypoint (GH #603), same shape as Laplace.Cli but proportionally
    // smaller: no banner, four verbs. The DbUp helpers (RunUp/RunStatus/RunReset/RunNuke,
    // ResolveConnectionString, Confirmed) are unchanged — the commands just route to them, and
    // the connection string is still resolved from the raw process args (so --database /
    // --connection-string / --yes behave exactly as before, independent of Spectre parsing).
    // A registrar-less CommandApp is deliberate: no command constructor-injects anything (ops
    // logging is written directly via LaplaceLogging), so a DI bridge here would be unused.
    public static int Main(string[] args)
    {
        var app = new CommandApp<UpCommand>();
        app.Configure(config =>
        {
            config.SetApplicationName("laplace-migrate");
            config.AddCommand<UpCommand>("up");
            config.AddCommand<StatusCommand>("status");
            config.AddCommand<ResetCommand>("reset");
            config.AddCommand<NukeCommand>("nuke");
            config.SetExceptionHandler((ex, _) =>
            {
                if (ex is NpgsqlException nex)
                {
                    Console.Error.WriteLine($"[NpgsqlException] {nex.Message}");
                    return 2;
                }
                Console.Error.WriteLine($"[error] {ex.GetType().Name}: {ex.Message}");
                return 1;
            });
        });
        return app.Run(Rewrite(args));
    }

    // Route on the verb, hand the raw arguments to the existing resolver untouched. A leading
    // flag (no verb) means the default 'up' — Spectre needs the explicit token for that. `--`
    // keeps Spectre from binding --database/--connection-string it does not model.
    private static readonly string[] Verbs = { "up", "status", "reset", "nuke" };
    private static string[] Rewrite(string[] args)
    {
        if (args.Length == 0) return new[] { "up" };
        if (args[0] is "-h" or "--help" or "--version") return args;
        string verb = Verbs.Contains(args[0].ToLowerInvariant()) ? args[0].ToLowerInvariant() : "up";
        int skip = verb == args[0].ToLowerInvariant() ? 1 : 0;
        var rest = args.Skip(skip);
        return new[] { verb, "--" }.Concat(rest).ToArray();
    }

    // The connection string is resolved from the ORIGINAL process args (exe stripped), exactly
    // as the pre-Spectre Main did — Spectre routing does not touch it.
    private static int Dispatch(string command, Func<string, int> run)
    {
        var raw = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var connectionString = ResolveConnectionString(raw);
        Console.WriteLine($"Laplace.Migrations: command={command}");
        Console.WriteLine($"Target database connection: {LaplaceInstall.RedactConnectionString(connectionString)}");
        return run(connectionString);
    }

    internal sealed class MigrateSettings : CommandSettings
    {
        [CommandOption("--database <NAME>")]
        [System.ComponentModel.Description("Target database name (default: the canonical laplace database).")]
        public string? Database { get; init; }

        [CommandOption("--connection-string <CONN>")]
        [System.ComponentModel.Description("Full Npgsql connection string (overrides --database and DATABASE_URL).")]
        public string? ConnectionString { get; init; }

        [CommandOption("--yes")]
        [System.ComponentModel.Description("Skip the confirmation prompt for destructive commands (reset/nuke).")]
        public bool Yes { get; init; }
    }

    [System.ComponentModel.Description("EnsureDatabase + apply all pending migrations (default).")]
    internal sealed class UpCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct) => Dispatch("up", RunUp);
    }

    [System.ComponentModel.Description("Show applied vs pending migrations.")]
    internal sealed class StatusCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct) => Dispatch("status", RunStatus);
    }

    [System.ComponentModel.Description("Drop SchemaVersions (re-applies migrations; preserves DB data).")]
    internal sealed class ResetCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct) => Dispatch("reset", RunReset);
    }

    [System.ComponentModel.Description("DROP DATABASE + re-create empty (full Layer-1 wipe).")]
    internal sealed class NukeCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct) => Dispatch("nuke", RunNuke);
    }

    private static int RunUp(string connectionString)
    {
        // FileOnly (not ConsoleAndFile): the human-facing report already goes to stdout
        // below; this is the queryable audit trail (which migration applied, when) in the
        // shared ops sink — ops.app_log, GH #602. Console output stays as-is.
        using var loggerFactory = Laplace.Ops.LaplaceLogging.FileOnly("migrations");
        var log = loggerFactory.CreateLogger("up");

        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        var engine = BuildEngine(connectionString);
        var result = engine.PerformUpgrade();

        if (!result.Successful)
        {
            Console.Error.WriteLine($"[migrate up FAILED] {result.Error?.Message}");
            log.LogError(result.Error, "migration upgrade failed");
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
                Spectre.Console.AnsiConsole.MarkupLine($"  [green]✓[/] {Spectre.Console.Markup.Escape(script.Name)}");
                log.LogInformation("applied migration {Migration}", script.Name);
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
            foreach (var name in applied)
                Spectre.Console.AnsiConsole.MarkupLine($"  [green]✓[/] {Spectre.Console.Markup.Escape(name)}");
            Console.WriteLine();
        }
        if (pending.Count > 0)
        {
            Console.WriteLine("Pending:");
            foreach (var script in pending)
                Spectre.Console.AnsiConsole.MarkupLine($"  [grey]·[/] {Spectre.Console.Markup.Escape(script.Name)}");
        }
        return 0;
    }

    private static bool Confirmed(string token)
    {
        if (Environment.GetCommandLineArgs().Contains("--yes")) return true;
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

        // DbUp parses Search Path= from the connection string as the executor schema.
        // Multi-schema paths (laplace,public) become invalid SQL in VerifySchema:
        //   CREATE SCHEMA IF NOT EXISTS laplace,public  → syntax error at ","
        // Journal lives in public; pass it explicitly and keep Search Path for scripts.
        return DeployChanges.To
            .PostgresqlDatabase(connectionString, "public")
            .WithScriptsFromFileSystem(migrationsDir)
            .JournalToPostgresqlTable("public", "schemaversions")
            .WithVariablesDisabled()
            .WithExecutionTimeout(TimeSpan.FromHours(4))
            .LogTo(new LiteralSafeUpgradeLog())
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
            if (args[i] == "--database") return LaplaceInstall.PostgresConnectionString(args[i + 1]);
        }

        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(databaseUrl))
            return ParsePostgresUrl(databaseUrl);

        return LaplaceInstall.PostgresConnectionString();
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
}
