using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// A player's record over witnessed games. Wins, draws, and losses are counted from game
/// headers; <c>Unscored</c> counts games whose source asserted no result, which are kept
/// out of <c>Score</c> (wins + draws/2 over scored games). Absence is not a loss.
/// </summary>
public sealed record ChessRecord(
    [property: JsonPropertyName("games")] long Games,
    [property: JsonPropertyName("wins")] long Wins,
    [property: JsonPropertyName("draws")] long Draws,
    [property: JsonPropertyName("losses")] long Losses,
    [property: JsonPropertyName("unscored")] long Unscored,
    [property: JsonPropertyName("score")] double? Score);

/// <summary>
/// One roster row: the player's folded standing cell. <c>Games</c> is its witness count,
/// <c>Rating</c>/<c>Rd</c> the Glicko-2 pair, <c>EffMu</c> the conservative estimate that
/// ranks it. The W/D/L split is on <see cref="ChessPlayerResponse"/>.
/// </summary>
public sealed record ChessPlayerRow(
    [property: JsonPropertyName("rank")] long Rank,
    [property: JsonPropertyName("id")] string IdHex,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("games")] long Games,
    [property: JsonPropertyName("rating")] double Rating,
    [property: JsonPropertyName("rd")] double Rd,
    [property: JsonPropertyName("eff_mu")] double EffMu);

/// <summary>
/// A page of the sortable roster, or the relevance-ranked hits for a search, read directly
/// from folded standing cells.
/// </summary>
public sealed record ChessPlayersResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("players")] IReadOnlyList<ChessPlayerRow> Players);

/// <summary>An Elo the source tagged this player with, and how many games carried it.</summary>
public sealed record ChessRatingRow(
    [property: JsonPropertyName("rating")] int Rating,
    [property: JsonPropertyName("games")] long Games);

/// <summary>
/// A head-to-head line read from the folded pairing cell: every meeting between two players
/// folds into one cell, so <c>Games</c> is its witness count and <c>EffMu</c> its
/// conservative standing, which orders the list.
/// </summary>
public sealed record ChessOpponentRow(
    [property: JsonPropertyName("id")] string IdHex,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("games")] long Games,
    [property: JsonPropertyName("rating")] double Rating,
    [property: JsonPropertyName("rd")] double Rd,
    [property: JsonPropertyName("eff_mu")] double EffMu);

/// <summary>One provider identity and the profile evidence acquired for it.</summary>
public sealed record ChessIdentityProfile(
    [property: JsonPropertyName("id")] string IdHex,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("provider_id")] string ProviderId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("federation")] string? Federation,
    [property: JsonPropertyName("biography")] string? Biography,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("links")] IReadOnlyList<string> Links,
    [property: JsonPropertyName("ratings")] IReadOnlyDictionary<string, int> Ratings,
    [property: JsonPropertyName("facts")] IReadOnlyDictionary<string, string> Facts);

/// <summary>
/// A player's career. <c>Overall</c>, <c>AsWhite</c>, and <c>AsBlack</c> are rows of one
/// record read over the same evidence, so the splits agree with the total.
/// </summary>
public sealed record ChessPlayerResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("id")] string IdHex,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("overall")] ChessRecord Overall,
    [property: JsonPropertyName("as_white")] ChessRecord AsWhite,
    [property: JsonPropertyName("as_black")] ChessRecord AsBlack,
    [property: JsonPropertyName("peak_rating")] int? PeakRating,
    [property: JsonPropertyName("ratings")] IReadOnlyList<ChessRatingRow> Ratings,
    [property: JsonPropertyName("opponents")] IReadOnlyList<ChessOpponentRow> Opponents,
    [property: JsonPropertyName("profiles")] IReadOnlyList<ChessIdentityProfile> Profiles);

/// <summary>
/// One line of a game log. <c>Outcome</c> is this player's result in the substrate's
/// outcome encoding (2 win, 1 draw, 0 loss, same values as PlyOutcome); null when the
/// source did not score the game.
/// </summary>
public sealed record ChessGameRow(
    [property: JsonPropertyName("id")] string IdHex,
    [property: JsonPropertyName("played_on")] string? PlayedOn,
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("eco")] string? Eco,
    [property: JsonPropertyName("as_white")] bool AsWhite,
    [property: JsonPropertyName("opponent_id")] string? OpponentId,
    [property: JsonPropertyName("opponent")] string Opponent,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("outcome")] short? Outcome);

public sealed record ChessGamesResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("games")] IReadOnlyList<ChessGameRow> Games);

/// <summary>
/// One ply of a replayed game. <c>PositionId</c> is the content id of the board after the
/// move, the same entity that carries that position's relations and standing.
/// <c>ClockSeconds</c> is the source's clock reading, present only when every ply has one.
/// </summary>
public sealed record ChessPlyRow(
    [property: JsonPropertyName("ply")] int Ply,
    [property: JsonPropertyName("san")] string San,
    [property: JsonPropertyName("uci")] string Uci,
    [property: JsonPropertyName("fen")] string Fen,
    [property: JsonPropertyName("white_moved")] bool WhiteMoved,
    [property: JsonPropertyName("clock_seconds")] double? ClockSeconds,
    [property: JsonPropertyName("position_id")] string PositionId);

/// <summary>
/// A recorded game replayed into its board sequence. <c>Truncated</c> is non-null when a
/// move does not resolve or the stored structure is inconsistent; the replay stops there.
/// </summary>
public sealed record ChessGamePliesResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("game_id")] string GameId,
    [property: JsonPropertyName("start_fen")] string StartFen,
    [property: JsonPropertyName("has_clocks")] bool HasClocks,
    [property: JsonPropertyName("truncated")] string? Truncated,
    [property: JsonPropertyName("plies")] IReadOnlyList<ChessPlyRow> Plies);

/// <summary>One game with source headers and canonical PGN generated from its typed trajectory.</summary>
public sealed record ChessGameResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("id")] string IdHex,
    [property: JsonPropertyName("white_id")] string? WhiteId,
    [property: JsonPropertyName("white")] string White,
    [property: JsonPropertyName("black_id")] string? BlackId,
    [property: JsonPropertyName("black")] string Black,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("played_on")] string? PlayedOn,
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("eco")] string? Eco,
    [property: JsonPropertyName("termination")] string? Termination,
    [property: JsonPropertyName("time_control")] string? TimeControl,
    [property: JsonPropertyName("tc_class")] string? TcClass,
    [property: JsonPropertyName("movetext")] string? Movetext);
