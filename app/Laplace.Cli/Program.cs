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
            if (args.Length >= 2 && args[0] == "ingest" && args[1] is "catalog" or "--catalog" or "--list-sources")
                return IngestCatalogCommands.Run(args[1..]);
            if (args.Length >= 2 && args[0] == "decompose")
                return RunDecompose(string.Join(' ', args[1..]));
            logging = CliRuntime.LoggerFactory;
            using var scope = logging.CreateLogger("cli").BeginScope(
                new Dictionary<string, object>
                {
                    ["OperationId"] = Guid.NewGuid().ToString("N"),
                    ["Command"] = args.FirstOrDefault() ?? "help",
                });
            Npgsql.NpgsqlLoggingConfiguration.InitializeLogging(logging, parameterLoggingEnabled: false);
            exitCode = await RunAsync(args);
            if (exitCode != 0)
                CliRuntime.LoggerFactory.CreateLogger("cli").LogWarning("CLI command ended with exit code {ExitCode}.", exitCode);
        }
        catch (Exception ex)
        {
            LaplaceLogging.ReportProcessFailure("laplace-cli", ex,
                failure => logging?.CreateLogger("cli").LogCritical(failure, "CLI terminated unexpectedly."));
            exitCode = 1;
        }
        return LaplaceLogging.CompleteProcess(logging, "laplace-cli", exitCode);
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "--api-client")
            return await ApiClientCommands.RunAsync(args[1..], (error, message) =>
                CliRuntime.LoggerFactory.CreateLogger("api-client").LogError(error, "{Failure}", message));

        if (args.Length == 0 || args[0] != "cpu-topology")
            NativeRuntimeEnv.ApplyFromTopology();

        CliRuntime.InitializeServices();

        if (args.Length == 0 || args[0] is "-h" or "--help")
            CliBanner.Render();

        return await BuildApp().RunAsync(ForExecution(args));
    }

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
        services.AddSingleton<ILoggerFactory>(_ => CliRuntime.LoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        var app = new CommandApp(new DiTypeRegistrar(services));
        app.Configure(config =>
        {
            config.SetApplicationName("laplace");
            config.SetExceptionHandler((ex, _) =>
            {
                CliRuntime.LoggerFactory.CreateLogger("cli").LogError(ex, "CLI command failed.");
                Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
                for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                    Console.Error.WriteLine($"  inner: {inner.GetType().Name}: {inner.Message}");
                if (Environment.GetEnvironmentVariable("LAPLACE_STACK") is "1" or "true")
                    Console.Error.WriteLine(ex.ToString());
                return 1;
            });

            config.AddCommand<IngestCommand>("ingest");
            config.AddCommand<InspectCommand>("inspect");
            config.AddCommand<ConverseCommand>("converse");
            config.AddCommand<RecallCommand>("recall");
            config.AddCommand<NeighborsCommand>("neighbors");
            config.AddCommand<WalkCommand>("walk");
            config.AddCommand<ChatCommand>("chat");
            config.AddCommand<AttestCommand>("attest");
            config.AddCommand<ChessCommand>("chess");
            config.AddCommand<StatsCommand>("stats");
            config.AddCommand<StatusCommand>("status");
            config.AddCommand<CpuTopologyCommand>("cpu-topology");
        });
        return app;
    }

    private static int RunDecompose(string input)
    {
        CodepointPerfcache.LoadDefault();
        using var tree = TextDecomposer.Run(input);
        unsafe { HashComposer.Run(tree, &PerfcacheResolver); }
        var root = tree.GetNode(tree.NaturalUnitIndex());
        Console.WriteLine($"{Convert.ToHexStringLower(root.Id.ToBytes())}\ttier={root.Tier}\tnodes={tree.NodeCount}");
        return 0;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe int PerfcacheResolver(
        uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        if (!CodepointPerfcache.TryLookupCodepoint(atom, out var r)) return -1;
        *outId = r.Hash;
        outCoord[0] = r.CoordX;
        outCoord[1] = r.CoordY;
        outCoord[2] = r.CoordZ;
        outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
    }
}
