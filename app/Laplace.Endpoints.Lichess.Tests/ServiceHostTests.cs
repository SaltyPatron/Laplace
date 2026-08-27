using System.Net;
using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Endpoints.Lichess.Tests;

public sealed class ServiceHostTests
{
    private sealed class Connection : ILichessConnection
    {
        public bool Ready;
        public bool StartAllowed = true;
        public bool Stopped;
        public bool Disposed;
        public (int Depth, int Maximum, bool Substrate)? Started;
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LichessConnectivityStatus Status() => new(true, null, Ready, "test", 8, 2, true, 0, [], null);
        public IReadOnlyList<LichessChatLine> ChatForGame(string gameId) => [];
        public bool Start(int depth = 8, int maxConcurrent = 2, bool substrate = true, IReadOnlySet<string>? acceptSpeeds = null)
        { Started = (depth, maxConcurrent, substrate); return StartAllowed; }
        public Task WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);
        public Task StopAsync(CancellationToken ct) { Stopped = true; _exit.TrySetResult(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task ServiceStartsByDefault_HealthTracksConnection_AndShutdownIsAwaited()
    {
        var bot = new Connection();
        bool failed = false;
        var app = LichessServiceHost.Build(new(Depth: 6, MaxConcurrent: 3, Port: 0), bot, () => failed = true);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal((6, 3, true), bot.Started);
        bot.Ready = true;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/stop", null)).StatusCode);
        Assert.DoesNotContain("tokenPreview\":\"", await client.GetStringAsync("/status"));
        await app.StopAsync();
        await app.DisposeAsync();
        Assert.True(bot.Stopped);
        Assert.True(bot.Disposed);
        Assert.False(failed);
    }

    [Fact]
    public async Task MissingConfigurationStopsHostWithFailureInsteadOfClaimingReadiness()
    {
        var bot = new Connection { StartAllowed = false };
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = LichessServiceHost.Build(new(Port: 0), bot, () => failed.TrySetResult());
        // A fail-closed worker can stop the host before Kestrel finishes binding;
        // either order is valid, but cancellation without the failure signal is not.
        try { await app.StartAsync(); }
        catch (OperationCanceledException) when (failed.Task.IsCompletedSuccessfully
            && app.Lifetime.ApplicationStopping.IsCancellationRequested) { }
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await app.StopAsync();
        Assert.False(bot.Ready);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(8, 0)]
    [InlineData(100, 2)]
    public void InvalidLimitsCannotStartABot(int depth, int maximum) =>
        Assert.Throws<InvalidOperationException>(() => LichessServiceHost.Build(new(Depth: depth, MaxConcurrent: maximum), new Connection()));
}
