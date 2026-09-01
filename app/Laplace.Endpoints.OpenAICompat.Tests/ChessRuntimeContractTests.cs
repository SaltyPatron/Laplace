using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Laplace.Chess.Service;
using Laplace.Modality.Chess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Pure and status chess routes must not initialize the write-capable chess runtime as
/// a side effect of DI resolution. Runtime initialization belongs to operations that
/// actually need substrate-backed play or recording.
/// </summary>
public sealed class ChessRuntimeContractTests : IClassFixture<ExploreFactory>
{
    private readonly ExploreFactory _factory;

    public ChessRuntimeContractTests(ExploreFactory factory) => _factory = factory;

    [Theory]
    [InlineData("elo=2300", "option.UCI_LimitStrength=true", "option.UCI_Elo=2300")]
    [InlineData("elo=1500", "option.UCI_LimitStrength=true", "option.UCI_Elo=1500")]
    [InlineData("elo=2300&limitStrength=false", "option.UCI_LimitStrength=false", null)]
    public async Task GauntletPreviewPreservesRequestedStrength(string query, string limiter, string? elo)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/chess/lab/cutechess/preview?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var args = json.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains(limiter, args);
        if (elo is not null) Assert.Contains(elo, args);
        else Assert.DoesNotContain(args, x => x?.StartsWith("option.UCI_Elo=", StringComparison.Ordinal) == true);
        Assert.False(_factory.Services.GetRequiredService<ChessRuntimeService>().InitializationStarted);
    }

    [Fact]
    public async Task PureAndStatusRoutes_DoNotInitializeWriteRuntime()
    {
        using var client = _factory.CreateClient();
        var runtime = _factory.Services.GetRequiredService<ChessRuntimeService>();
        Assert.False(runtime.InitializationStarted);

        foreach (var route in new[]
                 {
                     "/chess/new",
                     "/chess/train/status",
                     "/chess/lichess/status",
                     "/chess/lab/catalog",
                     "/chess/lab/jobs",
                 })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.False(runtime.InitializationStarted);
    }

    [Fact]
    public async Task EvalWithoutSubstrate_DoesNotResolveWriteRuntime()
    {
        using var client = _factory.CreateClient();
        var runtime = _factory.Services.GetRequiredService<ChessRuntimeService>();
        Assert.False(runtime.InitializationStarted);

        using var response = await client.PostAsJsonAsync("/chess/eval", new
        {
            fen = ChessModality.StartFen,
            depth = 1,
            substrate = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(runtime.InitializationStarted);
    }

    [Fact]
    public async Task FailedInitialization_IsRetriedByNextCaller()
    {
        var attempts = 0;
        var runtime = new ChessRuntimeService(
            NullLogger<ChessRuntimeService>.Instance,
            _ =>
            {
                attempts++;
                return Task.FromException<ChessLiveGameHost>(
                    new InvalidOperationException("test substrate unavailable"));
            });

        await runtime.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.GetAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.GetAsync(CancellationToken.None));

        Assert.Equal(2, attempts);
        await runtime.StopAsync(CancellationToken.None);
        Assert.False(runtime.InitializationStarted);

        await runtime.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.GetAsync(CancellationToken.None));
        Assert.Equal(3, attempts);
        await runtime.StopAsync(CancellationToken.None);
        await runtime.DisposeAsync();
    }
}
