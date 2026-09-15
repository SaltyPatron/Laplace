using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>One row of a per-band leaderboard: a consensus edge at full label.</summary>
public sealed record LeaderRow(
    [property: JsonPropertyName("subject_id")] string SubjectId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("object_id")] string ObjectId,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("eff_mu")] decimal EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses);

public sealed record BandLeaders(
    [property: JsonPropertyName("band")] int Band,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rows")] IReadOnlyList<LeaderRow> Rows);

public sealed record LeadersResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("bands")] IReadOnlyList<BandLeaders> Bands);

/// <summary>An entity's verdict record — its edges scored by epistemic status.</summary>
public sealed record EntityRecordResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("id")] string IdHex,
    [property: JsonPropertyName("confirmed")] long Confirmed,
    [property: JsonPropertyName("contested")] long Contested,
    [property: JsonPropertyName("refuted")] long Refuted,
    [property: JsonPropertyName("thin")] long Thin);

/// <summary>One tale-of-the-tape row: a fact held by x, y, or both.</summary>
public sealed record TapeRow(
    [property: JsonPropertyName("holder")] string Holder,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("fact")] string Fact,
    [property: JsonPropertyName("eff_mu")] decimal? EffMu);

/// <summary>
/// Source-observed chess career facts. These are deliberately separate from relation Glicko
/// standing: an Elo tag like 2690 is source testimony, while consensus eff_mu rates a substrate
/// edge and must never be presented as the player's chess Elo.
/// </summary>
public sealed record ChessMatchupSide(
    [property: JsonPropertyName("peak_source_elo")] int? PeakSourceElo,
    [property: JsonPropertyName("games")] long Games,
    [property: JsonPropertyName("wins")] long Wins,
    [property: JsonPropertyName("draws")] long Draws,
    [property: JsonPropertyName("losses")] long Losses,
    [property: JsonPropertyName("unscored")] long Unscored,
    [property: JsonPropertyName("score")] double? Score);

public sealed record MatchupSide(
    [property: JsonPropertyName("id")] string IdHex,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("record")] EntityRecordResponse Record,
    [property: JsonPropertyName("top_facts")] IReadOnlyList<SalientFactRow> TopFacts,
    // These coordinates are deliberately separate. Generic consensus standing is NOT a
    // source chess Elo, and calling one the other produced the Spassky/Karpov mismatch.
    [property: JsonPropertyName("entity_type")] string? EntityType = null,
    [property: JsonPropertyName("source_rating_peak")] int? SourceRatingPeak = null,
    [property: JsonPropertyName("source_rating_observations")] long SourceRatingObservations = 0,
    [property: JsonPropertyName("chess")] ChessMatchupSide? Chess = null);

/// <summary>The fast half of a matchup: both sides' cards plus the tape.</summary>
public sealed record MatchupResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("x")] MatchupSide X,
    [property: JsonPropertyName("y")] MatchupSide Y,
    [property: JsonPropertyName("tape")] IReadOnlyList<TapeRow> Tape);

/// <summary>The slow half: the witnessed path and the substrate's verdict.</summary>
public sealed record MatchupVerdictResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("relation")] string? Relation,
    [property: JsonPropertyName("plane")] string? Plane,
    [property: JsonPropertyName("eff_mu")] decimal? EffMu,
    [property: JsonPropertyName("usage")] long? Usage,
    [property: JsonPropertyName("geodesic")] double? Geodesic,
    [property: JsonPropertyName("verdict")] string? Verdict);