using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Laplace.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class ChessPerfcacheWireTests
{
    [Fact]
    public async Task ActualHttpSerializerMatchesCollectorFixtureWithFullTickPrecision()
    {
        var observation = new ChessPerfcacheObservation(123,
            new DateTimeOffset(2026, 9, 15, 21, 42, 30, TimeSpan.Zero).AddTicks(1234567),
            "process-lifetime completed managed lookups; counters include earlier mappings", true,
            new(true, 239014, 0, 0), new(true, 7870, 0, 0, 0, 0));
        var response = new ReadinessResponse(true, true, 15, 9, true, ChessPerfcache: observation);
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await Results.Json(response).ExecuteAsync(context);
        body.Position = 0;
        var actual = await JsonNode.ParseAsync(body);
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(FixturePath()));
        Assert.True(JsonNode.DeepEquals(expected, actual));
        Assert.Equal("2026-09-15T21:42:30.1234567+00:00",
            actual!["chess_perfcache"]!["observed_utc"]!.GetValue<string>());
    }

    private static string FixturePath([CallerFilePath] string path = "") =>
        Path.GetFullPath("../../scripts/fixtures/chess-readiness-response.json", Path.GetDirectoryName(path)!);
}
