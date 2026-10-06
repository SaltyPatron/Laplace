using Laplace.Endpoints.Lichess;
using Laplace.SubstrateCRUD.Npgsql;

// An operator's stop persists (ManagedServiceState): on Windows the service manager (NSSM) starts this process at
// boot and after a deploy, so the process honors the stop itself and exits 0, which NSSM's AppExit 0 maps to Exit
// (scripts/win/ensure-managed-services.ps1). On Linux the unit's ConditionPathExists does this before start.
if (OperatingSystem.IsWindows() && ManagedServiceState.OperatorStopped("lichess"))
{
    Console.WriteLine($"laplace-lichess: stopped by an operator ({ManagedServiceState.StopMarker("lichess")}); not starting.");
    return 0;
}
await using var app = LichessServiceHost.Build(LichessOptions.FromEnvironment());
await app.RunAsync();
return Environment.ExitCode;
