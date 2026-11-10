using System.Linq;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging;
using Npgsql;
using Spectre.Console.Cli;

namespace Laplace.Migrations;

internal static class Program
{
    // Spectre.Console.Cli entrypoint routes verbs to the declared-schema helpers. The connection string and --yes are resolved from the raw
    // process args, independent of Spectre parsing. The CommandApp has no registrar because no
    // command takes constructor dependencies.
    public static int Main(string[] args)
    {
        ILoggerFactory? logs = null;
        int exitCode = 1;
        try
        {
            logs = Laplace.Ops.LaplaceLogging.FileOnly("migrations");
            NpgsqlLoggingConfiguration.InitializeLogging(logs, parameterLoggingEnabled: false);
            exitCode = Run(args, logs);
            if (exitCode != 0) logs.CreateLogger("migrations")
                .LogWarning("Migration command {Command} ended with exit code {ExitCode}.", args.FirstOrDefault() ?? "up", exitCode);
        }
        catch (Exception ex)
        {
            Laplace.Ops.LaplaceLogging.ReportProcessFailure("Laplace.Migrations", ex,
                failure => logs?.CreateLogger("migrations").LogCritical(failure, "Migration process terminated unexpectedly."));
            exitCode = 1;
        }
        return Laplace.Ops.LaplaceLogging.CompleteProcess(logs, "Laplace.Migrations", exitCode);
    }

    private static int Run(string[] args, ILoggerFactory logs)
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
                logs.CreateLogger("migrations").LogError(ex, "Migration command failed.");
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

    [System.ComponentModel.Description("Create the database if missing and apply the declared schema, roles and grants (default).")]
    internal sealed class UpCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct) => Dispatch("up", RunUp);
    }

    [System.ComponentModel.Description("Apply the declared application schema to LAPLACE_APP_DB only.")]
    internal sealed class AppUpCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("app-up", _ => RunAppUp());
    }

    [System.ComponentModel.Description("Report drift between the declared application schema and the live catalog.")]
    internal sealed class AppStatusCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("app-status", connection => RunStatusTargets(new[] { (connection, (Func<string, bool>)IsAppScript, "application database") }));
    }

    [System.ComponentModel.Description("Verify the application database matches its declaration without changing data.")]
    internal sealed class AppCheckCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("app-check", RunAppCheck);
    }

    private static bool HasAllAppTables(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = NpgsqlCatalog.Command(connection, null, "migration.app_tables_present");
        return command.ExecuteScalar() is true;
    }

    private static int RunAppCheck(string connectionString)
    {
        var owners = PlatformOwnerPolicy.Load();
        var drift = DeclaredSchema.Drift(connectionString, DeclaredSchema.Files(IsAppScript));
        if (drift.Count != 0)
        {
            DeclaredSchema.Report("application database", drift);
            throw new InvalidOperationException("The application database drifts from the declared schema; run app-up.");
        }
        if (!HasAllAppTables(connectionString))
            throw new InvalidOperationException("The application database is missing required identity or billing tables.");
        using var database = NpgsqlDataSource.Create(connectionString);
        new PlatformOwnerReconciler(database, owners).VerifyAsync().GetAwaiter().GetResult();
        Console.WriteLine("Declared application schema and identity/billing tables verified.");
        ReportOwners(owners);
        return 0;
    }

    [System.ComponentModel.Description("Re-apply the declared application schema (there is no journal to clear); preserves application data.")]
    internal sealed class AppResetCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("app-reset", connection => RunReset(connection, applicationOnly: true));
    }

    [System.ComponentModel.Description("Recreate the selected application database; deletes its accounts, sessions, keys and billing data.")]
    internal sealed class AppNukeCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("app-nuke", connection => RunNuke(connection, applicationOnly: true));
    }

    [System.ComponentModel.Description("Report drift between the declared schema and the live catalog.")]
    internal sealed class StatusCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct) => Dispatch("status", RunStatus);
    }

    [System.ComponentModel.Description("Re-apply the declared schema (there is no journal to clear); preserves DB data.")]
    internal sealed class ResetCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct) => Dispatch("reset", RunReset);
    }

    [System.ComponentModel.Description("Recreate from the declared schema, preserving configuration and access; deletes data.")]
    internal sealed class NukeCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct) => Dispatch("nuke", RunNuke);
    }

    private static int RunUp(string connectionString)
        => RunUpTargets(Targets(connectionString));

    [System.ComponentModel.Description("Create and apply the declared schema to only the selected substrate database.")]
    internal sealed class SubstrateUpCommand : Command<MigrateSettings>
    {
        protected override int Execute(CommandContext ctx, MigrateSettings s, CancellationToken ct)
            => Dispatch("substrate-up", connection => RunUpTargets(new[] { (connection, (Func<string, bool>)(path => !IsAppScript(path)), "substrate database") }));
    }

    [System.ComponentModel.Description("Report drift between the declared substrate schema and the live catalog.")]
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
        var selected = targets.ToArray();
        var owners = selected.Any(item => IncludesApplication(item.filter)) ? PlatformOwnerPolicy.Load() : null;
        // File sink only: the human-facing report goes to stdout below; this is the
        // queryable record of what was applied when, read through ops.app_log.
        var log = Laplace.Ops.LaplaceLogging.CreateLogger("migrations.up");

        // The app schema (accounts, sessions, keys, billing) has a database of its own when LAPLACE_APP_DB is set:
        // its declaration goes there and nowhere else; the substrate database takes the rest. One database takes all.
        foreach (var (target, filter, label) in selected)
        {
            DeclaredSchema.EnsureDatabase(target);
            IReadOnlyList<string> applied;
            try
            {
                applied = DeclaredSchema.Apply(target, DeclaredSchema.Files(filter));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[apply FAILED] {label}: {ex.Message}");
                log.LogError(ex, "declared schema apply failed ({Target})", label);
                return 1;
            }
            Console.WriteLine($"[apply] {label}: declared schema applied ({applied.Count} file(s)); every statement is idempotent, so objects and grants are reconciled.");
            foreach (var name in applied)
            {
                Spectre.Console.AnsiConsole.MarkupLine($"  [green]✓[/] {Spectre.Console.Markup.Escape(name)}");
                log.LogInformation("applied declared schema file {File}", name);
            }
            if (IncludesApplication(filter))
            {
                using var database = NpgsqlDataSource.Create(target);
                var reconciler = new PlatformOwnerReconciler(database, owners!);
                reconciler.ReconcileAsync().GetAwaiter().GetResult();
                reconciler.VerifyAsync().GetAwaiter().GetResult();
                ReportOwners(owners!);
            }
        }
        return 0;
    }

    private static bool IncludesApplication(Func<string, bool> filter)
        => filter("30_app.sql");

    private static void ReportOwners(PlatformOwnerPolicy owners)
        => Console.WriteLine(owners.Owners.Count == 0
            ? "Installation owner policy is not configured."
            : $"Installation owner administrator access verified: {owners.Owners.Count} provider binding(s).");

    private static int RunStatus(string connectionString)
        => RunStatusTargets(Targets(connectionString));

    private static int RunStatusTargets(IEnumerable<(string target, Func<string, bool> filter, string label)> targets)
    {
        bool inSync = true;
        foreach (var (target, filter, label) in targets)
        {
            if (!DeclaredSchema.DatabaseExists(target))
            {
                Console.WriteLine($"[status] {label}: DRIFT, the database does not exist; the up command creates it from the declaration.");
                inSync = false;
                continue;
            }
            inSync &= DeclaredSchema.Report(label, DeclaredSchema.Drift(target, DeclaredSchema.Files(filter)));
        }
        return inSync ? 0 : 1;
    }

    private static bool Confirmed(string token)
    {
        if (Environment.GetCommandLineArgs().Contains("--yes")) return true;
        Console.Write($"Type '{token}' to confirm: ");
        return Console.ReadLine() == token;
    }

    // There is no journal to clear: the declaration is applied in full every time. The reset verbs
    // remain so operator tooling keeps working; they apply the declaration and keep the data.
    private static int RunReset(string connectionString)
        => RunReset(connectionString, applicationOnly: false);

    private static int RunReset(string connectionString, bool applicationOnly)
    {
        Console.WriteLine("There is no migration journal: re-applying the declared schema, roles and grants (data is preserved).");
        return RunUpTargets(applicationOnly
            ? new[] { (connectionString, (Func<string, bool>)IsAppScript, "application database") }
            : Targets(connectionString));
    }

    private static int RunNuke(string connectionString)
        => RunNuke(connectionString, applicationOnly: false);

    private static bool IsCoLocatedApplication(string connectionString)
    {
        var app = new NpgsqlConnectionStringBuilder(connectionString);
        var substrate = new NpgsqlConnectionStringBuilder(LaplaceInstall.PostgresConnectionString());
        return app.Database == substrate.Database && app.Port == substrate.Port &&
            string.Equals(app.Host, substrate.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static int RunNuke(string connectionString, bool applicationOnly)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var targetDb = b.Database
            ?? throw new InvalidOperationException("Target database name missing from connection string.");

        Console.WriteLine($"[migrate nuke] Re-create \"{targetDb}\" using its installed configuration and the declared schema.");
        Console.WriteLine(applicationOnly
            ? "Deletes application accounts, sessions, API keys and billing data."
            : "Loses ALL substrate data: entities, physicalities, attestations.");
        Console.WriteLine("Rebuilds extensions and applies the declared schema; preserves database configuration and access.");
        if (!Confirmed("NUKE"))
        {
            Console.WriteLine("Aborted.");
            return 1;
        }

        var app = LaplaceInstall.HasSeparateAppDatabase
            ? new NpgsqlConnectionStringBuilder(LaplaceInstall.AppConnectionString()) : b;
        bool includesApp = app.Database == b.Database && app.Host == b.Host && app.Port == b.Port;
        // Validate durable authority before creating a replacement or disconnecting serving clients.
        if (applicationOnly || includesApp) PlatformOwnerPolicy.Load();
        if (applicationOnly && IsCoLocatedApplication(connectionString))
        {
            DatabaseRecreation.RunApplicationSchema(connectionString,
                target => RunUpTargets(new[] { (target, (Func<string, bool>)IsAppScript, "application schema") }));
        }
        else
        {
            DatabaseRecreation.Run(connectionString, replacement => RunUpTargets(new[]
            {
                (replacement, (Func<string, bool>)(path => applicationOnly ? IsAppScript(path) : includesApp || !IsAppScript(path)), "replacement database")
            }));
        }
        Console.WriteLine("[migrate nuke] Re-created from the declared schema, database settings and existing access grants.");
        return 0;
    }

    // A script of the app schema: accounts, sessions, keys, and billing.
    private static bool IsAppScript(string path)
    {
        var name = Path.GetFileName(path);
        var separator = name.IndexOf('_');
        if (separator < 0) return false;
        var scope = name[(separator + 1)..];
        return scope.StartsWith("app.", StringComparison.Ordinal) || scope.StartsWith("app_", StringComparison.Ordinal);
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
