using System.Net;
using Laplace.Api.Contracts;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class PublicLeadersBudgetTests : IClassFixture<GoldenFactory>
{
    private readonly HttpClient _client;
    private readonly FakeSubstrateClient _substrate;
    public PublicLeadersBudgetTests(GoldenFactory factory)
    {
        _client = factory.CreateClient();
        _substrate = (FakeSubstrateClient)factory.Services.GetRequiredService<ISubstrateClient>();
    }

    [Fact]
    public async Task ConcurrentHomeReadsHaveNoQueueOrHeaderRotationBypass()
    {
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        _substrate.LeadersHandler = async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref entered) == 16) allEntered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Array.Empty<BandLeaders>();
        };
        var pending = Enumerable.Range(0, 16).Select(_ => _client.GetAsync("/v1/query/leaders/home")).ToArray();
        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/query/leaders/home?limit=2147483647");
            request.Headers.Add("X-Tenant-Id", "rotated-tenant");
            request.Headers.Add("X-API-Key", "rotated-key");
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(16, Volatile.Read(ref entered));
        }
        finally
        {
            release.TrySetResult();
            foreach (var response in await Task.WhenAll(pending)) response.Dispose();
            _substrate.LeadersHandler = null;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("?limit=2147483647&bands=0,1,2,3,4,5,6,7,8,9,10,11,12")]
    [InlineData("?limit=-1")]
    public async Task HomePreviewHasFixedServerBudget(string query)
    {
        using var response = await _client.GetAsync("/v1/query/leaders/home" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, _substrate.LastLeadersPerBand);
        Assert.Equal(new[] { 1, 2, 4, 5 }, _substrate.LastLeadersBands);
    }

    [Fact]
    public async Task RemovingLimitDoesNotRequestAnUnboundedRead()
    {
        using var response = await _client.GetAsync("/v1/query/leaders?bands=1,2,4,5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, _substrate.LastLeadersPerBand);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(int.MaxValue)]
    public async Task InvalidWorkBudgetIsRejectedBeforeDatabaseRead(int limit)
    {
        var before = _substrate.LeadersCalls;
        using var response = await _client.GetAsync($"/v1/query/leaders?bands=1,2,4,5&limit={limit}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, _substrate.LeadersCalls);
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
