using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// One row of a per-band leaderboard.  The original compact fields remain the
/// compatibility surface; realization metadata keeps identity, human/content
/// presentation, and technical registry names distinct, while rating/RD/eff_mu
/// remain separate measurements.
/// </summary>
public sealed record LeaderRow(
    [property: JsonPropertyName("subject_id")] string SubjectId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("object_id")] string ObjectId,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("eff_mu")] decimal EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses)
{
    [JsonPropertyName("subject_realization")]
    public string? SubjectRealization { get; init; }

    [JsonPropertyName("subject_technical_name")]
    public string? SubjectTechnicalName { get; init; }

    [JsonPropertyName("subject_type_id")]
    public string? SubjectTypeId { get; init; }

    [JsonPropertyName("subject_type_name")]
    public string? SubjectTypeName { get; init; }

    [JsonPropertyName("relation_id")]
    public string? RelationId { get; init; }

    [JsonPropertyName("relation_realization")]
    public string? RelationRealization { get; init; }

    [JsonPropertyName("relation_technical_name")]
    public string? RelationTechnicalName { get; init; }

    [JsonPropertyName("object_realization")]
    public string? ObjectRealization { get; init; }

    [JsonPropertyName("object_technical_name")]
    public string? ObjectTechnicalName { get; init; }

    [JsonPropertyName("object_type_id")]
    public string? ObjectTypeId { get; init; }

    [JsonPropertyName("object_type_name")]
    public string? ObjectTypeName { get; init; }

    [JsonPropertyName("rating")]
    public decimal? Rating { get; init; }

    [JsonPropertyName("rd")]
    public decimal? Rd { get; init; }
}

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

public sealed record MatchupSide(
    [property: JsonPropertyName("id")] string IdHex,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("record")] EntityRecordResponse Record,
    [property: JsonPropertyName("top_facts")] IReadOnlyList<SalientFactRow> TopFacts);

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
