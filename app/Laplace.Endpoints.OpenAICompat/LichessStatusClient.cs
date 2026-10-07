using System.Text.Json;
using Laplace.Chess.Service;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>The managed Lichess service's own loopback status, or why it did not answer.</summary>
internal sealed record LichessProbe(LichessConnectivityStatus? Status, string? Failure);

internal interface ILichessStatusClient
{
    Task<LichessProbe> ProbeAsync(CancellationToken ct);
    Task<IReadOnlyList<LichessChatLine>> ChatAsync(string gameId, CancellationToken ct);
}

internal sealed class LichessStatusClient(HttpClient client) : ILichessStatusClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<LichessProbe> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var status = await client.GetFromJsonAsync<LichessConnectivityStatus>("/status", Json, ct);
            return status is null ? new(null, "empty status response") : new(status, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(null, ex is TaskCanceledException ? "status request timed out" : ex.GetType().Name);
        }
    }

    public async Task<IReadOnlyList<LichessChatLine>> ChatAsync(string gameId, CancellationToken ct)
    {
        if (gameId.Length > 32 || !gameId.All(char.IsAsciiLetterOrDigit)) return [];
        try
        {
            return await client.GetFromJsonAsync<LichessChatLine[]>($"/games/{gameId}/chat", Json, ct) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return []; }
    }
}

/// <summary>The service manager's view of the managed Lichess service (systemd unit or NSSM service).</summary>
internal sealed record LichessServiceView(string Unit, string LoadState, string ActiveState, string SubState,
    string Enabled, bool OperatorStopped);

/// <summary>
/// What /chess/lichess/status answers: one <see cref="State"/> that never conflates the cases the panel must
/// tell apart, and the bot's own fields when the service answered.
/// <list type="bullet">
/// <item><c>listening</c>: the bot's event stream is open as the BOT account.</item>
/// <item><c>connecting</c>: the bot runs and is verifying the account or opening the stream.</item>
/// <item><c>token-missing</c>: the service answered and has no LICHESS_TOKEN/LICHESS_API.</item>
/// <item><c>account-not-ready</c>: the token is not a BOT account with bot:play.</item>
/// <item><c>failed</c>: the bot stopped on an error (<see cref="Error"/>); the service manager restarts it.</item>
/// <item><c>restarting</c>: the service manager is restarting the service after a failure.</item>
/// <item><c>starting</c>: the service is starting and not answering yet.</item>
/// <item><c>stopped</c>: the service is stopped (<see cref="LichessServiceView.OperatorStopped"/> when an operator stopped it).</item>
/// <item><c>not-installed</c>: the service manager has no such service.</item>
/// <item><c>unreachable</c>: the service did not answer and its state could not be read.</item>
/// </list>
/// <see cref="Configured"/> is null when the service did not answer: the API does not know, and never guesses.
/// <see cref="Desired"/> is whether the managed service is meant to run (the toggle), which a crash loop does not change.
/// </summary>
internal sealed record LichessStatusView(
    string State,
    bool Reachable,
    bool? Desired,
    bool? Configured,
    bool Connected,
    bool Running,
    string? Username,
    string? Engine,
    int MaxConcurrent,
    long GamesRecorded,
    IReadOnlyList<string> RecentLog,
    string? Error,
    LichessAccountReadiness? Account,
    LichessServiceView? Service,
    DateTimeOffset? LastSeenAt,
    string? TokenPreview = null,
    Laplace.Chess.Service.Uci.EngineIdentity? EngineIdentity = null);

/// <summary>
/// Composes the status: the service's own answer when it gives one; otherwise the service manager's state
/// (read only then, so a healthy poll never invokes the privileged boundary) and the last answer this API saw,
/// so a crash loop shows its last error instead of an empty "unconfigured".
/// </summary>
internal sealed class LichessStatusReader(ILichessStatusClient client, IServiceControl control,
    LichessStatusMemory memory, ILogger<LichessStatusReader> log)
{
    public async Task<LichessStatusView> ReadAsync(CancellationToken ct)
    {
        var probe = await client.ProbeAsync(ct);
        if (probe.Status is { } s)
        {
            memory.Remember(s);
            string state = !s.Configured ? "token-missing"
                : s.Account is { Ready: false } ? "account-not-ready"
                : s.Connected ? "listening"
                : s.Running ? "connecting"
                : "failed";
            return new(state, true, true, s.Configured, s.Connected, s.Running, s.Username, s.Engine, s.MaxConcurrent,
                s.GamesRecorded, s.RecentLog, s.Error, s.Account, null, DateTimeOffset.UtcNow, s.TokenPreview, s.EngineIdentity);
        }

        LichessServiceView? service = null;
        try
        {
            var r = await control.ExecuteAsync(ManagedService.Lichess, ServiceAction.Status, ct);
            service = new(r.Unit, r.LoadState, r.ActiveState, r.SubState, r.Enabled, r.OperatorStopped);
        }
        catch (Exception ex) when (ex is ServiceControlUnavailableException or InvalidOperationException
            or System.ComponentModel.Win32Exception or PlatformNotSupportedException or IOException)
        {
            log.LogDebug("lichess service state unreadable: {Error}", ex.GetType().Name);
        }

        var (last, lastAt) = memory.Last();
        var (st, desired, message) = service switch
        {
            null => ("unreachable", (bool?)null, $"The managed Lichess service did not answer ({probe.Failure}) and its service state could not be read."),
            { LoadState: "not-found" } => ("not-installed", false, $"The managed Lichess service ({service.Unit}) is not installed."),
            { OperatorStopped: true } => ("stopped", false, "Stopped by an operator."),
            { ActiveState: "inactive" } => ("stopped", false, $"The managed Lichess service ({service.Unit}) is stopped."),
            { ActiveState: "failed" } => ("failed", true, $"The managed Lichess service ({service.Unit}) failed."),
            { ActiveState: "deactivating" } => ("stopped", false, "The managed Lichess service is stopping."),
            { SubState: "auto-restart" } => ("restarting", true, $"The managed Lichess service ({service.Unit}) exited and is restarting."),
            _ => ("starting", true, $"The managed Lichess service ({service.Unit}) is starting."),
        };
        // the bot's last own error explains a crash loop; an operator's stop needs no old error
        if (desired == true && last?.Error is { Length: > 0 } lastError) message += " Last error: " + lastError;
        return new(st, false, desired, null, false, false, last?.Username, last?.Engine, last?.MaxConcurrent ?? 0,
            last?.GamesRecorded ?? 0, desired == false ? [] : last?.RecentLog ?? [],
            message, last?.Account, service, lastAt);
    }
}

/// <summary>The last status the managed service gave this API, kept for the moments it does not answer.</summary>
internal sealed class LichessStatusMemory
{
    private readonly object _gate = new();
    private LichessConnectivityStatus? _last;
    private DateTimeOffset? _at;
    public void Remember(LichessConnectivityStatus status) { lock (_gate) { _last = status; _at = DateTimeOffset.UtcNow; } }
    public (LichessConnectivityStatus? Status, DateTimeOffset? At) Last() { lock (_gate) return (_last, _at); }
}
