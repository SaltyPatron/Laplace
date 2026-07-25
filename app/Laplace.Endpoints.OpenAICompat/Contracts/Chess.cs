using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// A player's record over the games the corpus witnessed him in. Wins, draws and
/// losses are counted from the game headers; <c>Unscored</c> is games whose source
/// never asserted a result — abstentions, reported rather than folded into the
/// score. <c>Score</c> is the chess convention (wins + draws/2) over scored games.
/// </summary>
public sealed record ChessRecord(
    [property: JsonPropertyName("games")] long Games,
    [property: JsonPropertyName("wins")] long Wins,
    [property: JsonPropertyName("draws")] long Draws,
    [property: JsonPropertyName("losses")] long Losses,
    [property: JsonPropertyName("unscored")] long Unscored,
    [property: JsonPropertyName("score")] double? Score);

/// <summary>One row of the roster: a player, ranked by how much of him was witnessed.</summary>
public sealed record ChessPlayerRow(
    [property: JsonPropertyName("rank")] long Rank,
    [property: JsonPropertyName("id")] string IdHex,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("record")] ChessRecord Record);

/// <summary>
/// A page of the roster, or the hits for a search. <c>RankedDepth</c> is how far the
/// ranked list goes — the reach of a partial-name search, since substring matching
/// runs over that list while an exactly-spelled name resolves by content address at
/// any depth. Surfacing it lets the UI state the limit instead of implying an empty
/// result means nobody by that name was ever witnessed.
/// </summary>
public sealed record ChessPlayersResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("players")] IReadOnlyList<ChessPlayerRow> Players,
    [property: JsonPropertyName("ranked_depth")] int RankedDepth);

/// <summary>An Elo the source tagged this player with, and how many games carried it.</summary>
public sealed record ChessRatingRow(
    [property: JsonPropertyName("rating")] int Rating,
    [property: JsonPropertyName("games")] long Games);

/// <summary>A head-to-head line: one opponent, and the record against him.</summary>
public sealed record ChessOpponentRow(
    [property: JsonPropertyName("id")] string IdHex,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("record")] ChessRecord Record);

/// <summary>
/// The career page. <c>Overall</c>, <c>AsWhite</c> and <c>AsBlack</c> come from one
/// pass over the same evidence (SQL GROUPING SETS), so the splits can never
/// disagree with the total.
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
    [property: JsonPropertyName("opponents")] IReadOnlyList<ChessOpponentRow> Opponents);

/// <summary>
/// One line of a game log. <c>Outcome</c> is this player's result in the substrate's
/// own enum — 2 win, 1 draw, 0 loss, null when the source never scored the game —
/// bit-identical to PlyOutcome.
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
/// One ply of a replayed game. <c>PositionId</c> is the composed content address of the
/// board AFTER the move — a real Chess_Position entity, not a client-side artefact — so
/// every ply is a door into the rated MOVE web around that board. <c>ClockSeconds</c> is
/// the clock reading the source recorded, present only when it recorded one for every ply.
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
/// A recorded game replayed into the board sequence it describes. <c>Truncated</c> is
/// non-null when a token would not resolve: the walk stops there rather than skipping it,
/// because boards after an unplayable move are fiction.
/// </summary>
public sealed record ChessGamePliesResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("game_id")] string GameId,
    [property: JsonPropertyName("start_fen")] string StartFen,
    [property: JsonPropertyName("has_clocks")] bool HasClocks,
    [property: JsonPropertyName("truncated")] string? Truncated,
    [property: JsonPropertyName("plies")] IReadOnlyList<ChessPlyRow> Plies);

/// <summary>One game exactly as its source recorded it — headers plus verbatim movetext.</summary>
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
