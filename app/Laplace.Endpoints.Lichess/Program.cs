using Laplace.Endpoints.Lichess;
using Laplace.SubstrateCRUD.Npgsql;
using Laplace.Chess.Service;

if (args.Length is 2 or 4 && args[0] == "--verify-routing")
{
    var declaration = args[1];
    var configuration = LichessControlConfiguration.Read(declaration);
    var routing = configuration.Routing ?? throw new InvalidDataException("Independent routing is not declared.");
    if (args.Length == 4)
    {
        if (args[2] != "--selection") throw new ArgumentException("Expected --selection <installed state file>.");
        var selection = System.Text.Json.JsonSerializer.Deserialize<LichessSelection>(File.ReadAllText(args[3]), LichessControlConfiguration.Json)
            ?? throw new InvalidDataException("Empty selection.");
        selection = routing.Restore(selection);
        if (selection.Profile is not null) _ = configuration.Resolve(selection.Profile, selection.ReadSource, selection.RecordingTarget);
    }
    foreach (var (executorId, executor) in routing.Executors)
        foreach (var (sourceId, route) in executor.Sources)
        {
            var activation = routing.Resolve(executorId, sourceId, route.RecordingTargets[0]);
            using (configuration.Engine(activation, activation.ReadIdentity!).Open()) { }
            Console.WriteLine($"Engine verified: {executorId}, read source {sourceId}");
        }
    foreach (var (id, target) in routing.RecordingTargets)
    {
        var connection = ManagedServiceDatabase.Resolve(File.ReadAllText(target.ConnectionFile).Trim());
        using var source = LaplaceDataSource.Create(SubstrateAccess.Serving, connection);
        if (ChessKnowledgeIdentity.Read(source) != target.Identity)
            throw new InvalidOperationException($"Recording destination {id} does not match its declared identity.");
        Console.WriteLine($"Recording destination verified: {id}");
    }
    return 0;
}
if (args.Length != 0) throw new ArgumentException("Usage: --verify-routing <configuration> [--selection <state>], or no arguments to run the installed service.");

// An operator's stop persists (ManagedServiceState): on Windows the service manager (NSSM) starts this process at
// boot and after a deploy, so the process honors the stop itself and exits 0, which NSSM's AppExit 0 maps to Exit
// (scripts/win/ensure-managed-services.ps1). On Linux the unit's ConditionPathExists does this before start.
if (OperatingSystem.IsWindows() && ManagedServiceState.OperatorStopped("lichess"))
{
    Console.WriteLine($"laplace-lichess: stopped by an operator ({ManagedServiceState.StopMarker("lichess")}); not starting.");
    return 0;
}
await using var app = Environment.GetEnvironmentVariable("LAPLACE_LICHESS_CONTROL_CONFIG") is { Length: > 0 } control
    ? ControlledLichessHost.Build(control)
    : LichessServiceHost.Build(LichessOptions.FromEnvironment());
await app.RunAsync();
return Environment.ExitCode;
