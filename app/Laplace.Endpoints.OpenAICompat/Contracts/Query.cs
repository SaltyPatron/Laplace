using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// A structural read. The caller names the shape of the read, the lens (which
/// salience bands to traverse) and the dials; nothing here is inferred from the
/// phrasing of a question, so the same query works from any language.
/// </summary>
public sealed record QueryRequest(
    [property: JsonPropertyName("topic")] string? Topic,
    [property: JsonPropertyName("topic2")] string? Topic2 = null,
    [property: JsonPropertyName("shape")] string? Shape = null,
    [property: JsonPropertyName("bands")] int[]? Bands = null,
    [property: JsonPropertyName("relation_type")] string? RelationType = null,
    [property: JsonPropertyName("lang")] string? Lang = null,
    [property: JsonPropertyName("context")] string[]? Context = null,
    // Walk / beam dials
    [property: JsonPropertyName("depth")] int? Depth = null,
    [property: JsonPropertyName("breadth")] int? Breadth = null,
    // Generation dials
    [property: JsonPropertyName("steps")] int? Steps = null,
    [property: JsonPropertyName("spread")] double? Spread = null,
    [property: JsonPropertyName("max_stride")] int? MaxStride = null,
    [property: JsonPropertyName("seed")] long? Seed = null,
    // Path dials
    [property: JsonPropertyName("directed")] bool? Directed = null,
    [property: JsonPropertyName("use_geometry")] bool? UseGeometry = null,
    // Shared
    [property: JsonPropertyName("limit")] int? Limit = null);

public sealed record QueryRow(
    [property: JsonPropertyName("reply")] string Reply,
    [property: JsonPropertyName("eff_mu")] decimal? EffMu,
    [property: JsonPropertyName("witnesses")] long? Witnesses);

public sealed record QueryResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("topic_id")] string? TopicId,
    [property: JsonPropertyName("topic_label")] string? TopicLabel,
    [property: JsonPropertyName("topic2_id")] string? Topic2Id,
    [property: JsonPropertyName("topic2_label")] string? Topic2Label,
    [property: JsonPropertyName("bands")] int[]? Bands,
    [property: JsonPropertyName("rows")] IReadOnlyList<QueryRow> Rows);

/// <summary>One published read shape and what it requires.</summary>
public sealed record QueryShape(
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("needs_topic2")] bool NeedsTopic2,
    [property: JsonPropertyName("needs_type")] bool NeedsType,
    [property: JsonPropertyName("accepts_lang")] bool AcceptsLang);

public sealed record QueryShapesResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("shapes")] IReadOnlyList<QueryShape> Shapes);

/// <summary>
/// A salience band: a set of relation types sharing a read-time rank, addressed
/// by one precomputed highway mask. Counts are live, not manifest-declared.
/// </summary>
public sealed record RelationBand(
    [property: JsonPropertyName("band")] int Band,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rank")] double Rank,
    [property: JsonPropertyName("relation_types")] long RelationTypes,
    [property: JsonPropertyName("consensus_rows")] long ConsensusRows);

public sealed record RelationBandsResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("bands")] IReadOnlyList<RelationBand> Bands);
