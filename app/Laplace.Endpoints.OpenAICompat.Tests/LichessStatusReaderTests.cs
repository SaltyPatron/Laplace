using Laplace.Chess.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// /chess/lichess/status keeps "the service did not answer" apart from "the token is missing", and a crash loop
/// apart from an operator's stop, so the panel never flips between them under polling.
/// </summary>
public sealed class LichessStatusReaderTests
{
    private sealed class Client : ILichessStatusClient
    {
        public LichessConnectivityStatus? Status;
        public Task<LichessProbe> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(Status is null ? new LichessProbe(null, "HttpRequestException") : new LichessProbe(Status, null));
        public Task<IReadOnlyList<LichessChatLine>> ChatAsync(string gameId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LichessChatLine>>([]);
    }

    private sealed class Control : IServiceControl
    {
        public string Load = "loaded", Active = "active", Sub = "running";
        public bool OperatorStopped, Unavailable;
        public List<ServiceAction> Calls { get; } = [];
        public Task<ServiceControlResult> ExecuteAsync(ManagedService service, ServiceAction action, CancellationToken ct)
        {
            Calls.Add(action);
            if (Unavailable) throw new ServiceControlUnavailableException();
            return Task.FromResult(new ServiceControlResult("lichess", "LaplaceLichess", "status", false,
                Load, Active, Sub, "success", 0, "enabled", OperatorStopped));
        }
    }

    private static LichessConnectivityStatus Bot(bool configured = true, bool connected = false, bool running = true,
        string? error = null, LichessAccountReadiness? account = null) =>
        new(configured, null, connected, "SaltyPatron", "laplace", 2, 3, ["line"], error, running, account);

    private static (LichessStatusReader Reader, Client Client, Control Control) Make()
    {
        var client = new Client();
        var control = new Control();
        return (new LichessStatusReader(client, control, new LichessStatusMemory(), NullLogger<LichessStatusReader>.Instance), client, control);
    }

    [Fact]
    public async Task AnsweringServiceIsNeverAskedOfTheServiceManager()
    {
        var (reader, client, control) = Make();
        client.Status = Bot(connected: true);
        var view = await reader.ReadAsync(default);
        Assert.Equal("listening", view.State);
        Assert.True(view.Reachable);
        Assert.True(view.Desired);
        Assert.Empty(control.Calls);
    }

    [Theory]
    [InlineData(false, false, true, "token-missing")]
    [InlineData(true, false, true, "connecting")]
    [InlineData(true, false, false, "failed")]
    public async Task AnsweringStates(bool configured, bool connected, bool running, string expected)
    {
        var (reader, client, _) = Make();
        client.Status = Bot(configured, connected, running);
        Assert.Equal(expected, (await reader.ReadAsync(default)).State);
    }

    [Fact]
    public async Task AccountThatIsNotABotIsItsOwnState()
    {
        var (reader, client, _) = Make();
        client.Status = Bot(account: new(TokenValid: true, BotAccount: false, BotPlayScope: true, Username: "x", Error: "not a BOT account"));
        Assert.Equal("account-not-ready", (await reader.ReadAsync(default)).State);
    }

    [Fact]
    public async Task UnreachableServiceNeverClaimsTheTokenIsMissing()
    {
        var (reader, _, control) = Make();
        control.Unavailable = true;
        var view = await reader.ReadAsync(default);
        Assert.Equal("unreachable", view.State);
        Assert.Null(view.Configured);
        Assert.Null(view.Desired);
        Assert.False(view.Reachable);
    }

    [Fact]
    public async Task CrashLoopStaysDesiredAndCarriesTheLastError()
    {
        var (reader, client, control) = Make();
        client.Status = Bot(running: false, error: "No password has been provided");
        Assert.Equal("failed", (await reader.ReadAsync(default)).State);
        client.Status = null;
        control.Active = "activating"; control.Sub = "auto-restart";
        var view = await reader.ReadAsync(default);
        Assert.Equal("restarting", view.State);
        Assert.True(view.Desired);
        Assert.Null(view.Configured);
        Assert.Contains("No password has been provided", view.Error);
        Assert.NotNull(view.LastSeenAt);
    }

    [Theory]
    [InlineData("loaded", "inactive", "dead", true, "stopped", false)]
    [InlineData("loaded", "inactive", "dead", false, "stopped", false)]
    [InlineData("not-found", "inactive", "dead", false, "not-installed", false)]
    [InlineData("loaded", "activating", "start", false, "starting", true)]
    [InlineData("loaded", "failed", "failed", false, "failed", true)]
    public async Task ServiceManagerStates(string load, string active, string sub, bool operatorStopped, string expected, bool desired)
    {
        var (reader, _, control) = Make();
        control.Load = load; control.Active = active; control.Sub = sub; control.OperatorStopped = operatorStopped;
        var view = await reader.ReadAsync(default);
        Assert.Equal(expected, view.State);
        Assert.Equal(desired, view.Desired);
        Assert.Equal(ServiceAction.Status, Assert.Single(control.Calls));
    }
}
