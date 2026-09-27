using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// One link of an entity's mesh position. <c>belongs_to</c> links go up to the entities it
/// is a sense, instance, kind, part, or role of; <c>roster</c> links go down to its members.
/// Each link is a consensus cell with its standing.
/// </summary>
public sealed record MeshLink(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("hub_type")] string? HubType,
    [property: JsonPropertyName("eff_mu")] decimal? EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses);

public sealed record MeshResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    // The label of this entity's stored type; null when it has none.
    [property: JsonPropertyName("hub_type")] string? HubType,
    [property: JsonPropertyName("belongs_to")] IReadOnlyList<MeshLink> BelongsTo,
    [property: JsonPropertyName("roster")] IReadOnlyList<MeshLink> Roster);

/// <summary>One rung of the taxonomy: an IS_A neighbor with its rating.</summary>
public sealed record TaxonomyNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("eff_mu")] decimal? EffMu);

/// <summary>
/// The IS_A tree around a topic: the strongest-parent chain upward and the strongest
/// children. A topic that realizes a concept is rooted at that concept
/// (taxonomy.top_synset); otherwise at the topic itself.
/// </summary>
public sealed record TaxonomyResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("root_id")] string RootId,
    [property: JsonPropertyName("root_label")] string RootLabel,
    [property: JsonPropertyName("up")] IReadOnlyList<TaxonomyNode> Up,
    [property: JsonPropertyName("children")] IReadOnlyList<TaxonomyNode> Children);
