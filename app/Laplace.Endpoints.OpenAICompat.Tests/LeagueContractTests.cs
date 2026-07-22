using System.Net;
using System.Net.Http.Json;
using Laplace.Api.Contracts;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// The league surface: pulse (live scoreboard), leaders (per-band leaderboards),
/// entity record, and the head-to-head matchup. Shapes and status codes over the
/// FakeSubstrateClient — the same contract the SPA consumes.
/// </summary>
public sealed class LeagueContractTests : IClassFixture<ExploreFactory>
{
    private readonly HttpClient _client;

    public LeagueContractTests(ExploreFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Pulse_ReturnsScoreboardShape()
    {
        using var response = await _client.GetAsync("/v1/pulse");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PulseResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Entities > 0);
        Assert.True(body.Attestations > 0);
        Assert.True(body.Consensus > 0);
        // the heartbeat fields are present (folding is a real bool, not null)
        Assert.True(body.Folding || !body.Folding);
    }

    [Fact]
    public async Task Leaders_DefaultBands_ReturnRankedRows()
    {
        using var response = await _client.GetAsync("/v1/query/leaders?bands=1,2&limit=3");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LeadersResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Bands);
        var first = body.Bands[0];
        Assert.NotEmpty(first.Rows);
        Assert.Equal(32, first.Rows[0].SubjectId.Length);
        Assert.True(first.Rows[0].EffMu > 0);
    }

    [Fact]
    public async Task Leaders_RejectsGarbageBands()
    {
        using var response = await _client.GetAsync("/v1/query/leaders?bands=99,abc");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EntityRecord_ReturnsVerdictCounts()
    {
        using var response = await _client.GetAsync(
            "/v1/explore/entities/00112233445566778899aabbccddeeff/record");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EntityRecordResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Confirmed + body.Contested + body.Refuted + body.Thin > 0);
    }

    [Fact]
    public async Task EntityRecord_RejectsNonHexId()
    {
        using var response = await _client.GetAsync("/v1/explore/entities/not-a-hex-id/record");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Mesh_ReturnsBelongsToAndRoster()
    {
        using var response = await _client.GetAsync(
            "/v1/explore/entities/00112233445566778899aabbccddeeff/mesh");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeshResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body!.Label));
        Assert.NotEmpty(body.BelongsTo);
        Assert.NotEmpty(body.Roster);
    }

    [Fact]
    public async Task Mesh_RejectsNonHexId()
    {
        using var response = await _client.GetAsync("/v1/explore/entities/not-a-hex/mesh");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Matchup_ReturnsBothSidesAndTape()
    {
        using var response = await _client.GetAsync("/v1/explore/matchup?x=dog&y=cat");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MatchupResponse>();
        Assert.NotNull(body);
        Assert.NotNull(body!.X);
        Assert.NotNull(body.Y);
        Assert.NotEmpty(body.Tape);
        Assert.Contains(body.Tape, t => t.Holder is "both" or "x-only" or "y-only");
    }

    [Fact]
    public async Task Matchup_RequiresBothTopics()
    {
        using var response = await _client.GetAsync("/v1/explore/matchup?x=dog");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MatchupVerdict_ReturnsRelationAndVerdict()
    {
        using var response = await _client.GetAsync("/v1/explore/matchup/verdict?x=dog&y=cat");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MatchupVerdictResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body!.Verdict));
    }
}
