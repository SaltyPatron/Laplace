using System.Net;
using System.Net.Http.Json;
using Laplace.Api.Contracts;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class ExploreContractTests : IClassFixture<ExploreFactory>
{
    private readonly HttpClient _client;

    public ExploreContractTests(ExploreFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task ExploreCatalog_ReturnsShape()
    {
        using var response = await _client.GetAsync("/v1/explore/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExploreCatalogResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Counts);
        Assert.NotEmpty(body.FeaturedRefs);
    }

    [Fact]
    public async Task ExploreResolve_Whale()
    {
        using var response = await _client.GetAsync("/v1/explore/resolve?reference=whale");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExploreResolveResponse>();
        Assert.NotNull(body);
        Assert.Equal("whale", body!.Label);
        Assert.Equal(32, body.IdHex.Length);
    }

    [Fact]
    public async Task ExplorePreview_ReturnsTeaser()
    {
        using var resolve = await _client.GetAsync("/v1/explore/resolve?reference=whale");
        var hit = await resolve.Content.ReadFromJsonAsync<ExploreResolveResponse>();
        using var response = await _client.GetAsync($"/v1/explore/entities/{hit!.IdHex}/preview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<ExploreEntityPreviewResponse>();
        Assert.NotNull(preview);
        Assert.NotEmpty(preview!.PreviewFacts);
    }

    [Fact]
    public async Task ExploreEntity_GatedWithBypass()
    {
        using var resolve = await _client.GetAsync("/v1/explore/resolve?reference=whale");
        var hit = await resolve.Content.ReadFromJsonAsync<ExploreResolveResponse>();
        using var response = await _client.GetAsync($"/v1/explore/entities/{hit!.IdHex}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<ExploreEntityDetailResponse>();
        Assert.NotNull(detail?.Entity);
        Assert.NotEmpty(detail!.Entity.ConsensusOut);
    }

    [Fact]
    public async Task ExploreDecompose_ReturnsNodes()
    {
        using var response = await _client.PostAsJsonAsync("/v1/explore/decompose", new DecomposeRequest("hello world"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DecomposeResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Nodes.Count > 0);
    }
}
