using Laplace.Api.Contracts;
using Laplace.Chess.Service;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The chess READ surface — the database half of the chess pillar, as distinct from
/// the playing half in <see cref="ChessEndpoints"/>. Those endpoints drive a board:
/// legal moves, engine evals, a live game. These serve what the substrate already
/// witnessed: the roster, a career, a game.
///
/// Every route is a drill: roster -> player -> game -> the two players in it. Ids are
/// content hashes throughout, so every one of them is also a substrate entity id —
/// the same id /v1/explore/entities/{id} will explain down to its witnesses. The
/// chess view and the substrate view are two readings of one row, never two copies.
/// </summary>
internal static class ChessReadEndpoints
{
    public static void MapChessReadEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/chess/laplace/games", async (
            int? limit, int? offset, ISubstrateClient substrate, CancellationToken ct) =>
        {
            var laplaceId = Convert.ToHexStringLower(ChessVocabulary.LaplacePlayerId.ToBytes());
            var games = await substrate.ChessPlayerGamesAsync(
                laplaceId, limit ?? 100, offset ?? 0, ct);
            return Results.Json(games ?? new ChessGamesResponse(
                "chess.games", laplaceId, Math.Max(0, offset ?? 0), []));
        })
        .WithTags("chess")
        .Produces<ChessGamesResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/chess/players", async (
            int? limit, int? offset, string? search, string? initial,
            string? sort, string? direction,
            ISubstrateClient substrate, CancellationToken ct) =>
        {
            return Results.Json(await substrate.ChessPlayersAsync(
                limit ?? 50, offset ?? 0, search, initial, sort, direction, ct));
        })
        .WithTags("chess")
        .Produces<ChessPlayersResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/chess/players/{idHex}", async (
            string idHex, int? opponents, ISubstrateClient substrate, CancellationToken ct) =>
        {
            var player = await substrate.ChessPlayerAsync(idHex, opponents ?? 25, ct);
            return player is null
                ? EndpointJson.NotFound("player_not_found",
                    $"'{idHex}' is not a player the substrate has witnessed at a board.")
                : Results.Json(player);
        })
        .WithTags("chess")
        .Produces<ChessPlayerResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/chess/players/{idHex}/games", async (
            string idHex, int? limit, int? offset, ISubstrateClient substrate, CancellationToken ct) =>
        {
            var games = await substrate.ChessPlayerGamesAsync(idHex, limit ?? 25, offset ?? 0, ct);
            return games is null
                ? EndpointJson.NotFound("player_not_found", $"'{idHex}' is not a 32-hex entity id.")
                : Results.Json(games);
        })
        .WithTags("chess")
        .Produces<ChessGamesResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/chess/games/{idHex}/plies", async (
            string idHex, ISubstrateClient substrate, CancellationToken ct) =>
        {
            var plies = await substrate.ChessGamePliesAsync(idHex, ct);
            return plies is null
                ? EndpointJson.NotFound("game_not_found",
                    $"'{idHex}' is not a game the substrate has witnessed.")
                : Results.Json(plies);
        })
        .WithTags("chess")
        .Produces<ChessGamePliesResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/chess/games/{idHex}", async (
            string idHex, ISubstrateClient substrate, CancellationToken ct) =>
        {
            var game = await substrate.ChessGameAsync(idHex, ct);
            return game is null
                ? EndpointJson.NotFound("game_not_found",
                    $"'{idHex}' is not a game the substrate has witnessed.")
                : Results.Json(game);
        })
        .WithTags("chess")
        .Produces<ChessGameResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }
}
