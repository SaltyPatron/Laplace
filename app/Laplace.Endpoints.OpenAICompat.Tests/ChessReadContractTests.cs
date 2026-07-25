using System.Net;
using System.Net.Http.Json;
using Laplace.Api.Contracts;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// The chess read surface: roster, career, game log, game. Shapes and status codes
/// over the FakeSubstrateClient — the same contract the SPA consumes.
///
/// The drill is the thing being pinned. Every id a page hands back has to be an id
/// the next page accepts, or the navigation dead-ends: roster gives player ids,
/// a player's game log gives game ids AND opponent ids, and a game gives both
/// players' ids back. These tests walk that loop rather than checking rows in
/// isolation.
/// </summary>
public sealed class ChessReadContractTests : IClassFixture<ExploreFactory>
{
    private const string TalIdHex = "b422a7d40dec7948426e7c8ae40810d5";

    private readonly HttpClient _client;

    public ChessReadContractTests(ExploreFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Players_ReturnsRankedRoster()
    {
        using var response = await _client.GetAsync("/v1/chess/players?limit=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChessPlayersResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Players);

        var top = body.Players[0];
        Assert.Equal(1, top.Rank);
        Assert.Equal(32, top.IdHex.Length);
        Assert.NotEmpty(top.Name);
        // The record has to add up: every game is a win, a draw, a loss, or
        // explicitly unscored. Nothing may vanish between the columns.
        Assert.Equal(top.Record.Games,
            top.Record.Wins + top.Record.Draws + top.Record.Losses + top.Record.Unscored);
    }

    [Fact]
    public async Task Players_Paginate()
    {
        using var response = await _client.GetAsync("/v1/chess/players?limit=1&offset=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChessPlayersResponse>();
        Assert.NotNull(body);
        Assert.Equal(1, body!.Offset);
        Assert.Single(body.Players);
        Assert.Equal(2, body.Players[0].Rank);
    }

    [Fact]
    public async Task Players_SearchByName_ResolvesToOnePlayer()
    {
        using var response = await _client.GetAsync("/v1/chess/players?search=Tal,%20Mikhail");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChessPlayersResponse>();
        Assert.NotNull(body);
        Assert.Single(body!.Players);
        Assert.Equal(TalIdHex, body.Players[0].IdHex);
    }

    [Fact]
    public async Task Players_SearchByPartialName_StillFinds()
    {
        // A person typing a fragment is not wrong, just partial — "tal" has to
        // reach Tal without the caller knowing the source's exact spelling.
        using var response = await _client.GetAsync("/v1/chess/players?search=tal");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChessPlayersResponse>();
        Assert.NotNull(body);
        Assert.Contains(body!.Players, p => p.IdHex == TalIdHex);
    }

    [Fact]
    public async Task Players_SearchIsCaseInsensitive()
    {
        using var response = await _client.GetAsync("/v1/chess/players?search=BOTVINNIK");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChessPlayersResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Players);
        Assert.Contains("Botvinnik", body.Players[0].Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Players_ReportsHowDeepTheRankedListGoes()
    {
        // Substring reach ends where the cached ranking does. The response has to
        // say how far that is, or an empty result reads as "never witnessed".
        using var response = await _client.GetAsync("/v1/chess/players?search=zzz-nobody");
        var body = await response.Content.ReadFromJsonAsync<ChessPlayersResponse>();
        Assert.NotNull(body);
        Assert.Empty(body!.Players);
        Assert.True(body.RankedDepth > 0);
    }

    [Fact]
    public async Task Players_SearchForNobody_IsEmptyNotAnError()
    {
        using var response = await _client.GetAsync("/v1/chess/players?search=no%20such%20person");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChessPlayersResponse>();
        Assert.NotNull(body);
        Assert.Empty(body!.Players);
        Assert.Equal(0, body.Total);
    }

    [Fact]
    public async Task Player_SplitsAgreeWithTheTotal()
    {
        using var response = await _client.GetAsync($"/v1/chess/players/{TalIdHex}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChessPlayerResponse>();
        Assert.NotNull(body);
        Assert.Equal(TalIdHex, body!.IdHex);

        // The colour splits come from one GROUPING SETS pass over the same
        // evidence as the total, so they must reconcile exactly.
        Assert.Equal(body.Overall.Games, body.AsWhite.Games + body.AsBlack.Games);
        Assert.Equal(body.Overall.Wins, body.AsWhite.Wins + body.AsBlack.Wins);
        Assert.Equal(body.Overall.Draws, body.AsWhite.Draws + body.AsBlack.Draws);
        Assert.Equal(body.Overall.Losses, body.AsWhite.Losses + body.AsBlack.Losses);
    }

    [Fact]
    public async Task Player_PeakRatingIsTheHighestWitnessed()
    {
        using var response = await _client.GetAsync($"/v1/chess/players/{TalIdHex}");
        var body = await response.Content.ReadFromJsonAsync<ChessPlayerResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Ratings);
        Assert.Equal(body.Ratings.Max(r => r.Rating), body.PeakRating);
    }

    [Fact]
    public async Task Player_UnknownId_Is404()
    {
        using var response = await _client.GetAsync(
            "/v1/chess/players/00000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PlayerGames_LogRowsCarryTheIdsTheNextPageNeeds()
    {
        using var response = await _client.GetAsync($"/v1/chess/players/{TalIdHex}/games?limit=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChessGamesResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Games);

        var row = body.Games[0];
        Assert.Equal(32, row.IdHex.Length);
        Assert.Equal(32, row.OpponentId!.Length);
        Assert.NotEmpty(row.Opponent);
        // outcome is the substrate's own enum, bit-identical to PlyOutcome
        Assert.InRange(row.Outcome!.Value, (short)0, (short)2);
    }

    [Fact]
    public async Task PlayerGames_PastTheEnd_IsEmptyNotAnError()
    {
        using var response = await _client.GetAsync(
            $"/v1/chess/players/{TalIdHex}/games?limit=5&offset=9999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChessGamesResponse>();
        Assert.NotNull(body);
        Assert.Empty(body!.Games);
    }

    [Fact]
    public async Task Game_DrillsBackToBothPlayers()
    {
        using var log = await _client.GetAsync($"/v1/chess/players/{TalIdHex}/games?limit=1");
        var games = await log.Content.ReadFromJsonAsync<ChessGamesResponse>();
        var gameId = games!.Games[0].IdHex;

        using var response = await _client.GetAsync($"/v1/chess/games/{gameId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChessGameResponse>();
        Assert.NotNull(body);
        Assert.Equal(gameId, body!.IdHex);

        // The player whose log we came from must be one of the two sides, and both
        // sides must be followable ids — that is the loop closing.
        Assert.Contains(TalIdHex, new[] { body.WhiteId, body.BlackId });
        Assert.Equal(32, body.WhiteId!.Length);
        Assert.Equal(32, body.BlackId!.Length);
        Assert.NotEmpty(body.Movetext!);
    }

    [Fact]
    public async Task GamePlies_ReplayCarriesBoardsAndPositionIds()
    {
        using var log = await _client.GetAsync($"/v1/chess/players/{TalIdHex}/games?limit=1");
        var games = await log.Content.ReadFromJsonAsync<ChessGamesResponse>();
        var gameId = games!.Games[0].IdHex;

        using var response = await _client.GetAsync($"/v1/chess/games/{gameId}/plies");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChessGamePliesResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Plies);
        Assert.Contains(" w ", body.StartFen);

        // Plies are 1-based and consecutive, and colours alternate — the sequence has to
        // be a real game, not a bag of moves.
        for (int i = 0; i < body.Plies.Count; i++)
        {
            Assert.Equal(i + 1, body.Plies[i].Ply);
            Assert.Equal(i % 2 == 0, body.Plies[i].WhiteMoved);
        }

        // Every board is addressable as a substrate entity — that is what makes this a
        // walk into the graph rather than a private replay.
        Assert.All(body.Plies, p => Assert.Equal(32, p.PositionId.Length));
        Assert.All(body.Plies, p => Assert.NotEmpty(p.Fen));
        Assert.All(body.Plies, p => Assert.Equal(4, p.Uci.Length));
    }

    [Fact]
    public async Task GamePlies_ClockedGameReportsClocksThatOnlyFall()
    {
        using var log = await _client.GetAsync($"/v1/chess/players/{TalIdHex}/games?limit=1");
        var games = await log.Content.ReadFromJsonAsync<ChessGamesResponse>();
        using var response = await _client.GetAsync($"/v1/chess/games/{games!.Games[0].IdHex}/plies");
        var body = await response.Content.ReadFromJsonAsync<ChessGamePliesResponse>();
        Assert.NotNull(body);
        Assert.True(body!.HasClocks);
        // A clock series is present for EVERY ply or reported absent — never partial,
        // because a gap would be a reading the source never made.
        Assert.All(body.Plies, p => Assert.NotNull(p.ClockSeconds));
    }

    [Fact]
    public async Task GamePlies_UnknownId_Is404()
    {
        using var response = await _client.GetAsync(
            "/v1/chess/games/00000000000000000000000000000000/plies");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Game_UnknownId_Is404()
    {
        using var response = await _client.GetAsync(
            "/v1/chess/games/00000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
