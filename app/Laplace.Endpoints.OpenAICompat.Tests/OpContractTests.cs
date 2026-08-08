using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class OpContractTests : IClassFixture<GoldenFactory>
{
    private readonly HttpClient _client;

    public OpContractTests(GoldenFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Op_PropagatesBoundedLongRunningTimeout()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/op")
        {
            Content = JsonContent.Create(new
            {
                name = "source_status",
                max_rows = 10,
                timeout_seconds = 300,
            }),
        };
        request.Headers.Add("X-Laplace-Tenant", "contract-test");

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = json.RootElement.GetProperty("rows")[0];
        Assert.Equal(300, row.GetProperty("timeout_seconds").GetInt32());
    }
}
