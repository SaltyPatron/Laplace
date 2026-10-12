using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace Laplace.Endpoints.Lichess.Tests;

public sealed class LichessControlTests
{
    [Fact]
    public async Task IndependentRoutingPersistsAllowedTupleAndRejectsUninstalledGrant()
    {
        var root = Directory.CreateTempSubdirectory("laplace-routing-").FullName;
        try
        {
            var source = new Laplace.Chess.Service.ChessKnowledgeIdentity("123", "admitted-world");
            var destination = new Laplace.Chess.Service.ChessKnowledgeIdentity("456", "live-observations");
            var routing = new LichessRouting(
                new() { ["desktop"] = new("Desktop", "development", "laplace-mono",
                    new() { ["curated"] = new("desktop-curated", ["observations"]) }) },
                new() { ["curated"] = new("Curated world", "laplace-mono", source) },
                new() { ["observations"] = new("Live observations", "development", "laplace-mono",
                    Path.Combine(root, "writer.connection"), destination),
                    ["restricted"] = new("Restricted destination", "production", "laplace-mono",
                    Path.Combine(root, "restricted.connection"), destination) });
            routing.Validate();
            var config = new LichessControlConfiguration(2, 5189, Path.Combine(root, "selection.json"),
                Path.Combine(root, "token"), Path.Combine(root, "connector"), Path.Combine(root, "connector.json"), new(), routing);
            var activation = config.Resolve("desktop", "curated", "observations");
            Assert.Equal(source, activation.ReadIdentity);
            Assert.Equal(destination, activation.WriteIdentity);
            Assert.Equal("desktop-curated", activation.ConnectorProfile);
            using (var controller = new LichessController(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<LichessController>.Instance))
            {
                await controller.SelectAsync(new("desktop", 0, "authenticated-user", "curated", "observations"), default);
                await Assert.ThrowsAsync<ArgumentException>(() => controller.SelectAsync(new("desktop", 1,
                    "authenticated-user", "curated", "restricted"), default));
                await Assert.ThrowsAsync<ArgumentException>(() => controller.SelectAsync(new("desktop", 1,
                    "authenticated-user", "missing", "observations"), default));
                await Assert.ThrowsAsync<ArgumentException>(() => controller.SelectAsync(new("desktop", 1,
                    "authenticated-user"), default));
            }
            using var restored = new LichessController(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<LichessController>.Instance);
            var status = JsonSerializer.SerializeToElement(restored.Status(), LichessControlConfiguration.Json);
            Assert.Equal("curated", status.GetProperty("selection").GetProperty("readSource").GetString());
            Assert.Equal("observations", status.GetProperty("selection").GetProperty("recordingTarget").GetString());
            Assert.Equal(1, status.GetProperty("selection").GetProperty("revision").GetInt64());
            Assert.DoesNotContain("connectionFile", status.GetRawText());
            Assert.DoesNotContain(root.Replace("\\", "\\\\"), status.GetRawText());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ControllerAuthenticatesAndPersistsRevisionWithoutStartingABot()
    {
        var root = Directory.CreateTempSubdirectory("laplace-control-").FullName;
        try
        {
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start(); var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
            var token = new string('a', 64);
            var configPath = Path.Combine(root, "control.json");
            var tokenPath = Path.Combine(root, "token");
            File.WriteAllText(tokenPath, token);
            var config = new LichessControlConfiguration(1, number, Path.Combine(root, "selection.json"), tokenPath,
                Path.Combine(root, "connector"), Path.Combine(root, "connector.json"),
                new() { ["staging"] = new("Staging", "staging", "laplace-mono", "knowledge", Path.Combine(root, "connection"), "staging") });
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, LichessControlConfiguration.Json));
            await using (var app = ControlledLichessHost.Build(configPath))
            {
                await app.StartAsync();
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{number}") };
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/status")).StatusCode);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/selection", new
                    { profile = "unknown", expectedRevision = 0, actor = "test-user" })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/selection", new
                    { profile = (string?)null, expectedRevision = 0, actor = "test-user" })).StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/selection", new
                    { profile = (string?)null, expectedRevision = 0, actor = "test-user" })).StatusCode);
                await app.StopAsync();
            }
            await using (var app = ControlledLichessHost.Build(configPath))
            {
                await app.StartAsync();
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{number}") };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var status = await client.GetFromJsonAsync<JsonElement>("/status");
                Assert.Equal(1, status.GetProperty("selection").GetProperty("revision").GetInt64());
                Assert.Equal(JsonValueKind.Null, status.GetProperty("selection").GetProperty("profile").ValueKind);
                Assert.Equal(JsonValueKind.Null, status.GetProperty("bot").ValueKind);
                await app.StopAsync();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
