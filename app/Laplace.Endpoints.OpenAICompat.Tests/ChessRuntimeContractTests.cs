using System.Net;
using Laplace.Chess.Service;
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
        await runtime.DisposeAsync();
    }
}
