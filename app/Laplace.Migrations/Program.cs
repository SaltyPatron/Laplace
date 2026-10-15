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
    // Spectre.Console.Cli entrypoint routes verbs to the DbUp helpers. The connection string and --yes are resolved from the raw
    // process args, independent of Spectre parsing. The CommandApp has no registrar because no
    // command takes constructor dependencies.
    public static int Main(string[] args)
    {
        if (args.Length > 0 && !args[0].StartsWith('-') && !Verbs.Contains(args[0].ToLowerInvariant()))
        {
            Console.Error.WriteLine($"Unknown migration command: {args[0]}");
            return 2;
        }
        var app = new CommandApp<UpCommand>();
        app.Configure(config =>
        {
            config.SetApplicationName("laplace-migrate");
            config.AddCommand<UpCommand>("up");
            config.AddCommand<AppUpCommand>("app-up");
            config.AddCommand<AppStatusCommand>("app-status");
            config.AddCommand<AppCheckCommand>("app-check");
            config.AddCommand<AppResetCommand>("app-reset");
            config.AddCommand<AppNukeCommand>("app-nuke");
            config.AddCommand<SubstrateUpCommand>("substrate-up");
            config.AddCommand<SubstrateStatusCommand>("substrate-status");
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

    // Route on the verb and hand the raw arguments to the resolver untouched. A leading flag
    // (no verb) means the default 'up', which Spectre needs as an explicit token. `--` keeps
    // Spectre from binding --database/--connection-string, which it does not model.
    private static readonly string[] Verbs = { "up", "app-up", "app-status", "app-check", "app-reset", "app-nuke", "substrate-up", "substrate-status", "status", "reset", "nuke" };
    private static string[] Rewrite(string[] args)
    {
        if (args.Length == 0) return new[] { "up" };
        if (args[0] is "-h" or "--help" or "--version") return args;
        string verb = Verbs.Contains(args[0].ToLowerInvariant()) ? args[0].ToLowerInvariant() : "up";
        int skip = verb == args[0].ToLowerInvariant() ? 1 : 0;
        var rest = args.Skip(skip);
        return new[] { verb, "--" }.Concat(rest).ToArray();
    }

    // The connection string is resolved from the original process args (exe stripped);
    // Spectre routing does not touch it.
    private static int Dispatch(string command, Func<string, int> run)
    {
        var raw = Environment.GetCommandLineArgs().Skip(1).ToArray();
        bool application = command.StartsWith("app-", StringComparison.Ordinal);
        if (application && !LaplaceInstall.HasSeparateAppDatabase)
            throw new InvalidOperationException($"{command} requires LAPLACE_APP_DB; no database was changed.");
        if (application && raw.Any(arg => arg is "--database" or "--connection-string"))
            throw new InvalidOperationException("Application commands select LAPLACE_APP_DB; remove the conflicting target override.");
        var connectionString = application ? LaplaceInstall.AppConnectionString() : ResolveConnectionString(raw);
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

    [System.ComponentModel.Description("Apply application migrations to LAPLACE_APP_DB only.")]
    internal sealed class AppUpCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("app-up", _ => RunAppUp());
    }

    [System.ComponentModel.Description("Show pending and applied application migrations only.")]
    internal sealed class AppStatusCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("app-status", connection => RunStatusTargets(new[] { (connection, (Func<string, bool>)IsAppScript, "application database") }));
    }

    [System.ComponentModel.Description("Verify application tables and pending migrations without changing data.")]
    internal sealed class AppCheckCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("app-check", RunAppCheck);
    }

    private static int RunAppCheck(string connectionString)
    {
        if (BuildEngine(connectionString, IsAppScript).GetScriptsToExecute().Count != 0)
            throw new InvalidOperationException("Application migrations are pending; run app-up.");
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""
            SELECT bool_and(to_regclass(name) IS NOT NULL)
            FROM unnest(ARRAY['app.users','app.external_identities','app.platform_roles',
                'app.api_keys','app.billing_quotes']) AS required(name)
            """, connection);
        if (command.ExecuteScalar() is not true)
            throw new InvalidOperationException("The application database is missing required identity or billing tables.");
        Console.WriteLine("Application migrations and identity/billing tables verified.");
        return 0;
    }

    [System.ComponentModel.Description("Clear the application migration journal; preserves application data.")]
    internal sealed class AppResetCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("app-reset", connection => { RequireSeparateApplication(connection); return RunReset(connection); });
    }

    [System.ComponentModel.Description("Recreate the selected application database; deletes its accounts, sessions, keys and billing data.")]
    internal sealed class AppNukeCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("app-nuke", connection => RunNuke(connection, applicationOnly: true));
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

    [System.ComponentModel.Description("Recreate from installed migrations, preserving configuration and access; deletes data.")]
    internal sealed class NukeCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct) => Dispatch("nuke", RunNuke);
    }

    private static int RunUp(string connectionString)
        => RunUpTargets(Targets(connectionString));

    [System.ComponentModel.Description("Create and migrate only the selected substrate database.")]
    internal sealed class SubstrateUpCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("substrate-up", connection => RunUpTargets(new[] { (connection, (Func<string, bool>)(path => !IsAppScript(path)), "substrate database") }));
    }

    [System.ComponentModel.Description("Show pending and applied substrate migrations only.")]
    internal sealed class SubstrateStatusCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("substrate-status", connection => RunStatusTargets(new[] { (connection, (Func<string, bool>)(path => !IsAppScript(path)), "substrate database") }));
    }

    private static int RunAppUp()
    {
        if (!LaplaceInstall.HasSeparateAppDatabase)
            throw new InvalidOperationException("app-up requires LAPLACE_APP_DB; no database was changed.");
        return RunUpTargets(new[] { (LaplaceInstall.AppConnectionString(), (Func<string, bool>)IsAppScript, "application database") });
    }

    private static int RunUpTargets(IEnumerable<(string target, Func<string, bool> filter, string label)> targets)
    {
        // File sink only: the human-facing report goes to stdout below; this is the
        // queryable record of which migration applied when, read through ops.app_log.
        using var loggerFactory = Laplace.Ops.LaplaceLogging.FileOnly("migrations");
        var log = loggerFactory.CreateLogger("up");

        var applied = new List<DbUp.Engine.SqlScript>();

        // The app schema (accounts, sessions, keys, billing) has a database of its own when LAPLACE_APP_DB is set:
        // its scripts go there and nowhere else; the substrate database takes the rest. One database takes all.
        foreach (var (target, filter, label) in targets)
        {
            EnsureDatabase.For.PostgresqlDatabase(target);
            var engine = BuildEngine(target, filter);
            var result = engine.PerformUpgrade();
            if (!result.Successful)
            {
                Console.Error.WriteLine($"[migrate up FAILED] {label}: {result.Error?.Message}");
                log.LogError(result.Error, "migration upgrade failed ({Target})", label);
                return 1;
            }
            applied.AddRange(result.Scripts);
        }

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
        => RunStatusTargets(Targets(connectionString));

    private static int RunStatusTargets(IEnumerable<(string target, Func<string, bool> filter, string label)> targets)
    {
        var pending = new List<DbUp.Engine.SqlScript>();
        var applied = new List<string>();
        foreach (var (target, filter, _) in targets)
        {
            var engine = BuildEngine(target, filter);
            pending.AddRange(engine.GetScriptsToExecute());
            applied.AddRange(engine.GetExecutedScripts());
        }

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
        Console.WriteLine("Clears the selected database's migration journal while preserving its data and extensions.");
        Console.WriteLine("Run the matching up command to reapply migrations.");
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
        => RunNuke(connectionString, applicationOnly: false);

    private static void RequireSeparateApplication(string connectionString)
    {
        var app = new NpgsqlConnectionStringBuilder(connectionString);
        var substrate = new NpgsqlConnectionStringBuilder(LaplaceInstall.PostgresConnectionString());
        if (app.Database == substrate.Database && app.Port == substrate.Port &&
            string.Equals(app.Host, substrate.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Application-only reset requires distinct application and substrate databases.");
    }

    private static int RunNuke(string connectionString, bool applicationOnly)
    {
        if (applicationOnly) RequireSeparateApplication(connectionString);
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var targetDb = b.Database
            ?? throw new InvalidOperationException("Target database name missing from connection string.");

        Console.WriteLine($"[migrate nuke] Re-create \"{targetDb}\" using its installed configuration and migrations.");
        Console.WriteLine(applicationOnly
            ? "Deletes application accounts, sessions, API keys and billing data."
            : "Loses ALL substrate data: entities, physicalities, attestations.");
        Console.WriteLine("Rebuilds extensions from installed migrations; preserves database configuration and access.");
        if (!Confirmed("NUKE"))
        {
            Console.WriteLine("Aborted.");
            return 1;
        }

        var app = LaplaceInstall.HasSeparateAppDatabase
            ? new NpgsqlConnectionStringBuilder(LaplaceInstall.AppConnectionString()) : b;
        bool includesApp = app.Database == b.Database && app.Host == b.Host && app.Port == b.Port;
        DatabaseRecreation.Run(connectionString, replacement => RunUpTargets(new[]
        {
            (replacement, (Func<string, bool>)(path => applicationOnly ? IsAppScript(path) : includesApp || !IsAppScript(path)), "replacement database")
        }));
        Console.WriteLine("[migrate nuke] Re-created with migrations, database settings and existing access grants.");
        return 0;
    }

    // A script of the app schema: accounts, sessions, keys, billing, and the browser-ticket binding on them.
    private static bool IsAppScript(string path)
    {
        var name = Path.GetFileName(path);
        var separator = name.IndexOf('_');
        if (separator < 0) return false;
        var scope = name[(separator + 1)..];
        return scope.StartsWith("app_", StringComparison.Ordinal) || scope.StartsWith("browser_", StringComparison.Ordinal);
    }

    // Where the scripts go: the substrate database and, when LAPLACE_APP_DB names one, the application database.
    private static IEnumerable<(string connectionString, Func<string, bool> filter, string label)> Targets(string connectionString)
    {
        if (!LaplaceInstall.HasSeparateAppDatabase)
        {
            yield return (connectionString, _ => true, "database");
            yield break;
        }
        var app = LaplaceInstall.AppConnectionString();
        Console.WriteLine($"Application database connection: {LaplaceInstall.RedactConnectionString(app)}");
        yield return (connectionString, path => !IsAppScript(path), "substrate database");
        yield return (app, IsAppScript, "application database");
    }

    private static UpgradeEngine BuildEngine(string connectionString) => BuildEngine(connectionString, _ => true);

    private static UpgradeEngine BuildEngine(string connectionString, Func<string, bool> filter)
    {
        var migrationsDir = LocateMigrationsDir();
        Console.WriteLine($"Migrations directory: {migrationsDir}");

        // DbUp parses Search Path= from the connection string as the executor schema.
        // Multi-schema paths (laplace,public) become invalid SQL in VerifySchema:
        //   CREATE SCHEMA IF NOT EXISTS laplace,public  → syntax error at ","
        // Journal lives in public; pass it explicitly and keep Search Path for scripts.
        return DeployChanges.To
            .PostgresqlDatabase(connectionString, "public")
            .WithScriptsFromFileSystem(migrationsDir, filter)
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
