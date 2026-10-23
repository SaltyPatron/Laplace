using Laplace.Cli.Spectre;
using Laplace.Engine.Core;
using Laplace.Ops;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Laplace.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ILoggerFactory? logging = null;
        int exitCode = 1;
        try
        {
            logging = CliRuntime.LoggerFactory;
            using var scope = logging.CreateLogger("cli").BeginScope(
                new Dictionary<string, object> { ["OperationId"] = Guid.NewGuid().ToString("N"),
                    ["Command"] = args.FirstOrDefault() ?? "help" });
            Npgsql.NpgsqlLoggingConfiguration.InitializeLogging(logging, parameterLoggingEnabled: false);
            exitCode = await RunAsync(args);
            if (exitCode != 0) CliRuntime.LoggerFactory.CreateLogger("cli")
                .LogWarning("CLI command ended with exit code {ExitCode}.", exitCode);
        }
        catch (Exception ex)
        {
            LaplaceLogging.ReportProcessFailure("laplace-mono", ex,
                failure => logging?.CreateLogger("cli").LogCritical(failure, "CLI terminated unexpectedly."));
            exitCode = 1;
        }
        return LaplaceLogging.CompleteProcess(logging, "laplace-mono", exitCode);
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "--api-client")
            return await ApiClientCommands.RunAsync(args[1..]);

        // Native runtime init before any command touches the engine, keyed off the original
        // args[0] so cpu-topology (which can run before MKL is available) is exempt.
        if (args.Length == 0 || args[0] != "cpu-topology")
        {
            NativeRuntimeEnv.ApplyFromTopology();
            Laplace.Engine.Dynamics.MklAvailability.EnsureOrThrow();
            Laplace.Engine.Synthesis.MklAvailability.EnsureOrThrow();
        }

        CliRuntime.InitializeServices();

        if (args.Length == 0 || args[0] is "-h" or "--help")
            CliBanner.Render();

        var app = BuildApp();

        // Spectre routes on the command token; the arguments reach the command parsers
        // verbatim via ctx.Remaining.Raw (see ForExecution). Help requests pass through so
        // Spectre renders its own help.
        return await app.RunAsync(ForExecution(args));
    }

    // Insert `--` after the command token so every real argument reaches ctx.Remaining.Raw
    // untouched (Spectre otherwise binds/drops interleaved flags). Left alone: an empty line,
    // a top-level help/version request, and per-command help (`laplace <cmd> --help`), so
    // Spectre's own help still renders.
    private static string[] ForExecution(string[] args)
    {
        if (args.Length == 0) return args;
        if (args[0] is "-h" or "--help" or "--version") return args;
        if (args.Length >= 2 && args[1] is "-h" or "--help") return args;

        var injected = new string[args.Length + 1];
        injected[0] = args[0];
        injected[1] = "--";
        Array.Copy(args, 1, injected, 2, args.Length - 1);
        return injected;
    }

    private static CommandApp BuildApp()
    {
        var services = new ServiceCollection();
        // The shared ops logging factory is available to any command that takes a
        // constructor-injected ILogger; CliRuntime.Services stays the decomposer resolver.
        services.AddSingleton<ILoggerFactory>(_ => CliRuntime.LoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        var app = new CommandApp(new DiTypeRegistrar(services));
        app.Configure(config =>
        {
            config.SetApplicationName("laplace-mono");

            // A failure prints "error: <Type>: <message>" plus the inner chain to stderr and
            // exits 1, for both parse errors and runtime command exceptions.
            config.SetExceptionHandler((ex, _) =>
            {
                CliRuntime.LoggerFactory.CreateLogger("cli").LogError(ex, "CLI command failed.");
                Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
                for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                    Console.Error.WriteLine($"  inner: {inner.GetType().Name}: {inner.Message}");
                // LAPLACE_STACK=1|true adds the full stack trace.
                if (Environment.GetEnvironmentVariable("LAPLACE_STACK") is "1" or "true")
                    Console.Error.WriteLine(ex.ToString());
                return 1;
            });

            config.AddCommand<IngestCommand>("ingest");
            config.AddCommand<DocumentCommand>("document");
            config.AddCommand<CookbookCommand>("cookbook");
            config.AddCommand<SynthesizeCommand>("synthesize");
            config.AddCommand<DecomposeCommand>("decompose");
            config.AddCommand<InspectCommand>("inspect");
            config.AddCommand<ConverseCommand>("converse");
            config.AddCommand<RecallCommand>("recall");
            config.AddCommand<NeighborsCommand>("neighbors");
            config.AddCommand<WalkCommand>("walk");
            config.AddCommand<ChatCommand>("chat");
            config.AddCommand<AttestCommand>("attest");
            config.AddCommand<ChessCommand>("chess");
            config.AddCommand<RoundtripCommand>("roundtrip");
            config.AddCommand<DbRoundtripCommand>("db-roundtrip");
            config.AddCommand<EvalCommand>("eval");
            config.AddCommand<StatsCommand>("stats");
            config.AddCommand<StatusCommand>("status");
            config.AddCommand<EvictCommand>("evict");
            config.AddCommand<RebuildPhysIndexesCommand>("rebuild-phys-indexes");
            config.AddCommand<CloseRunCommand>("close-run");
            config.AddCommand<SourceBootstrapCommand>("source-bootstrap");
            config.AddCommand<CpuTopologyCommand>("cpu-topology");
            config.AddCommand<MeasureLaneCommand>("measure-lane");
            config.AddCommand<SvdExactBenchCommand>("svd-exact-bench");
            config.AddCommand<ModelBenchCommand>("model-bench");
        });
        return app;
    }
}
