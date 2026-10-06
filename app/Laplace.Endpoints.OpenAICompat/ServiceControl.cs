using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat;

public enum ManagedService { Mcp, Lichess }
public enum ServiceAction { Status, Start, Stop, Restart }
internal sealed record ServiceControlResult(string Service, string Unit, string Action,
    bool Accepted, string LoadState, string ActiveState, string SubState, string Result, int MainPid,
    string Enabled = "unknown", bool OperatorStopped = false);

internal sealed class ServiceControlUnavailableException(bool busy = false) : Exception("managed service control unavailable")
{
    public bool Busy { get; } = busy;
}

internal interface IServiceControl
{
    Task<ServiceControlResult> ExecuteAsync(ManagedService service, ServiceAction action, CancellationToken ct);
}

internal sealed class ServiceControl(ILogger<ServiceControl> log) : IServiceControl
{
    internal const string Helper = "/usr/local/libexec/laplace-service-control";
    public async Task<ServiceControlResult> ExecuteAsync(ManagedService service, ServiceAction action, CancellationToken ct)
    {
        var name = service switch
        {
            ManagedService.Mcp => "mcp", ManagedService.Lichess => "lichess",
            _ => throw new ArgumentOutOfRangeException(nameof(service)),
        };
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        var verb = action.ToString().ToLowerInvariant();
        // a status read is not an action: the Lichess panel reads it while the service is unreachable
        if (action == ServiceAction.Status) log.LogDebug("managed service status requested: service={Service}", name);
        else log.LogWarning("managed service action requested: service={Service} action={Action}", name, verb);
        var info = new ProcessStartInfo("/usr/bin/sudo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var value in new[] { "-n", "--", Helper, name, verb }) info.ArgumentList.Add(value);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        using var process = Process.Start(info) ?? throw new ServiceControlUnavailableException();
        var output = process.StandardOutput.ReadToEndAsync(budget.Token);
        var error = process.StandardError.ReadToEndAsync(budget.Token);
        try { await process.WaitForExitAsync(budget.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogError("managed service control timed out: service={Service} action={Action}", name, verb);
            // The root helper has its own 10s subprocess deadlines. A disconnected
            // caller does not undo an already accepted systemd job; poll status.
            throw new ServiceControlUnavailableException();
        }
        var stdout = await output;
        var stderr = await error;
        if (stdout.Length > 16_384 || stderr.Length > 4_096)
            throw new InvalidOperationException("service control helper output exceeded its contract");
        if (process.ExitCode != 0)
        {
            log.LogError("managed service action failed: service={Service} action={Action} exit={ExitCode}", name, verb, process.ExitCode);
            throw new ServiceControlUnavailableException(busy: process.ExitCode == 3);
        }
        var payload = JsonNode.Parse(stdout) as JsonObject
            ?? throw new InvalidOperationException("service control returned an invalid response");
        var unit = (string?)payload["unit"] ?? "";
        var expectedUnit = $"laplace-{name}.service";
        if (unit != expectedUnit) throw new InvalidOperationException("service control returned the wrong unit");
        return new(name, unit, verb, action != ServiceAction.Status,
            (string?)payload["load_state"] ?? "unknown", (string?)payload["active_state"] ?? "unknown",
            (string?)payload["sub_state"] ?? "unknown", (string?)payload["result"] ?? "unknown",
            (int?)payload["main_pid"] ?? 0, (string?)payload["enabled"] ?? "unknown",
            (bool?)payload["operator_stopped"] ?? false);
    }
}

/// <summary>
/// The Windows counterpart of deploy/linux/laplace-service-control: the managed services are NSSM
/// services (scripts/win/ensure-managed-services.ps1), whose security descriptor grants the site's pool
/// identity query, start and stop and nothing else (no reconfiguration). Only the two fixed service names
/// are reachable. The operator's stop persists as the same marker the Linux helper writes
/// (<see cref="ManagedServiceState"/>), which the service honors at start.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceControl(ILogger<WindowsServiceControl> log) : IServiceControl
{
    internal static string ServiceName(ManagedService service) => service switch
    {
        ManagedService.Mcp => "LaplaceMcp", ManagedService.Lichess => "LaplaceLichess",
        _ => throw new ArgumentOutOfRangeException(nameof(service)),
    };

    public async Task<ServiceControlResult> ExecuteAsync(ManagedService service, ServiceAction action, CancellationToken ct)
    {
        var name = service switch
        {
            ManagedService.Mcp => "mcp", ManagedService.Lichess => "lichess",
            _ => throw new ArgumentOutOfRangeException(nameof(service)),
        };
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        var unit = ServiceName(service);
        var verb = action.ToString().ToLowerInvariant();
        if (action == ServiceAction.Status) log.LogDebug("managed service status requested: service={Service}", name);
        else log.LogWarning("managed service action requested: service={Service} action={Action}", name, verb);
        using var sc = new ServiceController(unit);
        ServiceControllerStatus status;
        try { status = sc.Status; }
        catch (InvalidOperationException) when (action == ServiceAction.Status)
        {
            // not installed, or the pool identity may not query it: both are "not found" to the caller
            return new(name, unit, verb, false, "not-found", "inactive", "dead", "unknown", 0, "unknown",
                ManagedServiceState.OperatorStopped(name));
        }
        catch (InvalidOperationException ex)
        {
            log.LogError("managed service {Unit} cannot be queried ({Error}); run scripts/win/ensure-managed-services.cmd elevated", unit, ex.InnerException?.Message ?? ex.Message);
            throw new ServiceControlUnavailableException();
        }
        if (action != ServiceAction.Status)
        {
            var marker = ManagedServiceState.StopMarker(name);
            bool previouslyStopped = File.Exists(marker);
            try
            {
                if (action == ServiceAction.Stop) await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("O"), ct);
                else File.Delete(marker);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogError("managed service stop marker {Marker} not writable ({Error}); run scripts/win/ensure-managed-services.cmd elevated", marker, ex.GetType().Name);
                throw new ServiceControlUnavailableException();
            }
            try
            {
                switch (action)
                {
                    case ServiceAction.Start:
                        // NSSM pauses a service while it waits to restart a failed program; continue restarts it now
                        if (status == ServiceControllerStatus.Paused) sc.Continue();
                        else if (status is ServiceControllerStatus.Stopped) sc.Start();
                        break;
                    case ServiceAction.Stop:
                        if (status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)) sc.Stop();
                        break;
                    case ServiceAction.Restart:
                        if (status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)) sc.Stop();
                        // the Lichess drain is bounded at 40 s (LichessServiceHost ShutdownTimeout)
                        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)), ct);
                        sc.Start();
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
            {
                try
                {
                    if (previouslyStopped) await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None);
                    else File.Delete(marker);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException) { }
                log.LogError("managed service action failed: service={Service} action={Action} ({Error})", name, verb, ex.InnerException?.Message ?? ex.Message);
                throw new ServiceControlUnavailableException();
            }
            sc.Refresh();
            status = sc.Status;
        }
        var (active, sub) = status switch
        {
            ServiceControllerStatus.Running => ("active", "running"),
            ServiceControllerStatus.StartPending => ("activating", "start"),
            ServiceControllerStatus.StopPending => ("deactivating", "stop"),
            ServiceControllerStatus.Stopped => ("inactive", "dead"),
            // NSSM's throttle: the program exited and NSSM waits before restarting it
            _ => ("activating", "auto-restart"),
        };
        string enabled = "unknown";
        try
        {
            enabled = sc.StartType switch
            {
                ServiceStartMode.Automatic => "enabled", ServiceStartMode.Manual => "manual",
                ServiceStartMode.Disabled => "disabled", _ => "unknown",
            };
        }
        catch (InvalidOperationException) { }
        return new(name, unit, verb, action != ServiceAction.Status, "loaded", active, sub,
            status == ServiceControllerStatus.Paused ? "exit-code" : "success", 0, enabled,
            ManagedServiceState.OperatorStopped(name));
    }
}

internal static class ServiceControlEndpoints
{
    private static readonly IReadOnlyDictionary<string, ManagedService> Services =
        new Dictionary<string, ManagedService>(StringComparer.OrdinalIgnoreCase) { ["mcp"] = ManagedService.Mcp, ["lichess"] = ManagedService.Lichess };
    private static readonly IReadOnlyDictionary<string, ServiceAction> Actions =
        new Dictionary<string, ServiceAction>(StringComparer.OrdinalIgnoreCase)
        { ["start"] = ServiceAction.Start, ["stop"] = ServiceAction.Stop, ["restart"] = ServiceAction.Restart };

    public static void MapServiceControlEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/admin/services/{service}", async (string service, HttpRequest request,
            IServiceControl control, IOptions<LaplaceAuthOptions> auth, CancellationToken ct) =>
        {
            if (!OperatorAuth.IsAuthorized(request, auth.Value)) return Results.Unauthorized();
            if (!IsSafeTransport(request)) return Results.BadRequest(new { error = "https_required" });
            if (!Services.TryGetValue(service, out var selected)) return Results.NotFound();
            return await ExecuteAsync(control, selected, ServiceAction.Status, ct);
        }).WithTags("admin").WithDescription("Requires X-Laplace-Operator-Token over HTTPS. Only mcp and lichess are allowed.")
            .Produces<ServiceControlResult>(200).Produces(400).Produces(401).Produces(404).Produces(409).Produces(503);

        app.MapPost("/v1/admin/services/{service}/{action}", async (string service, string action,
            HttpRequest request, IServiceControl control, IOptions<LaplaceAuthOptions> auth, CancellationToken ct) =>
        {
            if (!OperatorAuth.IsAuthorized(request, auth.Value)) return Results.Unauthorized();
            if (!IsSafeTransport(request)) return Results.BadRequest(new { error = "https_required" });
            if (!Services.TryGetValue(service, out var selected) || !Actions.TryGetValue(action, out var verb))
                return Results.NotFound();
            return await ExecuteAsync(control, selected, verb, ct);
        }).WithTags("admin").WithDescription("Requires X-Laplace-Operator-Token over HTTPS. Only mcp/lichess and start/stop/restart are allowed; 202 means accepted, not ready.")
            .Produces<ServiceControlResult>(202).Produces(400).Produces(401).Produces(404).Produces(409).Produces(503);
    }

    internal static async Task<IResult> ExecuteAsync(IServiceControl control, ManagedService service,
        ServiceAction action, CancellationToken ct)
    {
        try
        {
            return Results.Json(await control.ExecuteAsync(service, action, ct),
                statusCode: action == ServiceAction.Status ? 200 : 202);
        }
        catch (ServiceControlUnavailableException ex)
        {
            return Results.Json(new { error = ex.Busy ? "managed_deployment_in_progress" : "service_control_unavailable" },
                statusCode: ex.Busy ? 409 : 503);
        }
    }

    internal static bool IsSafeTransport(HttpRequest request) => request.IsHttps
        || (request.HttpContext.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote));
}
