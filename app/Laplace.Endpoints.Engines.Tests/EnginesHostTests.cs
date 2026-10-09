using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Laplace.Chess.Uci.Commands;
using Laplace.Chess.Uci.Engines;
using Xunit;

namespace Laplace.Endpoints.Engines.Tests;

public sealed class EnginesHostTests
{
    private const string Token = "test-token-0123456789abcdef";

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(EngineHub? hub = null)
    {
        var app = EnginesHost.Build(new EnginesOptions("127.0.0.1", 0, Token), hub,
            () => [new EngineListing("laplace", false, "analysis", 2, [], Error: "not installed here")]);
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        return (app, client);
    }

    [Fact]
    public void RefusesToRunWithoutAToken()
        => Assert.Throws<InvalidOperationException>(() => EnginesHost.Build(new EnginesOptions("127.0.0.1", 0, null)));

    [Fact]
    public async Task HealthIsOpen_EverythingElseNeedsTheBearerToken()
    {
        var (app, client) = await StartAsync();
        await using (app)
        using (client)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/engines")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/engines")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            Assert.Contains("\"laplace\"", await client.GetStringAsync("/engines"));
        }
    }

    [Fact]
    public async Task TypedErrors_NeverAnotherEngine()
    {
        var unavailable = new ChessEngineSpec("lc0", "analysis", null, "Lc0 is not installed", [], 1);
        var (app, client) = await StartAsync(new EngineHub("analysis", (_, _) => unavailable));
        await using (app)
        using (client)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            var unknown = await client.PostAsJsonAsync("/analyse", new { engine = "komodo", limits = new { nodes = 10 } });
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Contains("\"rejected\"", await unknown.Content.ReadAsStringAsync());

            var missing = await client.PostAsJsonAsync("/analyse", new { engine = "lc0", limits = new { nodes = 10 } });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
            Assert.Contains("\"unavailable\"", await missing.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task AnUnboundedSearchIsRejected()
    {
        var spec = new ChessEngineSpec("stockfish", "analysis", new("stockfish-not-started"), null, [], 1);
        var (app, client) = await StartAsync(new EngineHub("analysis", (_, _) => spec));
        await using (app)
        using (client)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            var response = await client.PostAsJsonAsync("/analyse", new { engine = "stockfish" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("limits", await response.Content.ReadAsStringAsync());
        }
    }
}
