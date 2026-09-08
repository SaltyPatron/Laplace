using System.Net;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class PublicLeadersBudgetTests : IClassFixture<GoldenFactory>
{
    private readonly HttpClient _client;
    public PublicLeadersBudgetTests(GoldenFactory factory) => _client = factory.CreateClient();

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(int.MaxValue)]
    public async Task InvalidWorkBudgetIsRejectedBeforeDatabaseRead(int limit)
    {
        using var response = await _client.GetAsync($"/v1/query/leaders?bands=1,2,4,5&limit={limit}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task BoundedPreviewRemainsPublic(int limit)
    {
        using var response = await _client.GetAsync($"/v1/query/leaders?bands=1,2,4,5&limit={limit}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
