using Laplace.Endpoints.Engines;
using Laplace.SubstrateCRUD.Npgsql;

// The engine endpoint: laplace, stockfish and lc0 over HTTP for other hosts (hart-server), a thin layer over
// laplace-uci's library. An operator's stop persists (ManagedServiceState), as for the other managed services
// (scripts/win/ensure-managed-services.ps1).
if (OperatingSystem.IsWindows() && ManagedServiceState.OperatorStopped("engines"))
{
    Console.WriteLine($"laplace-engines: stopped by an operator ({ManagedServiceState.StopMarker("engines")}); not starting.");
    return 0;
}
await using var app = EnginesHost.Build(EnginesOptions.FromEnvironment());
await app.RunAsync();
return 0;
