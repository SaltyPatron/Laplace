using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// A node's position in the semantic mesh — the factorization of meaning made
/// navigable: surface → lemma → sense → ILI concept → frame/class/roleset → roles.
/// "belongs_to" walks UP the ladder (the hubs this node plays for), "roster"
/// walks DOWN (its members). Every link re-centers the drill-down.
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
    // The division this node belongs to: WordNet_Synset, FrameNet_Frame,
    // VerbNet_Class, PropBank_Roleset, WordNet_Sense — or null for a bare surface.
    [property: JsonPropertyName("hub_type")] string? HubType,
    [property: JsonPropertyName("belongs_to")] IReadOnlyList<MeshLink> BelongsTo,
    [property: JsonPropertyName("roster")] IReadOnlyList<MeshLink> Roster);

/// <summary>One rung of the taxonomy: an IS_A neighbor with its rating.</summary>
public sealed record TaxonomyNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("eff_mu")] decimal? EffMu);

/// <summary>
/// The IS_A tree around a topic: the chain of parents climbing to the root
/// (the ladder), and the strongest children (the branches). Rooted at the
/// topic's top synset when the topic itself is a bare surface — taxonomy lives
/// on concepts, not spellings.
/// </summary>
public sealed record TaxonomyResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("root_id")] string RootId,
    [property: JsonPropertyName("root_label")] string RootLabel,
    [property: JsonPropertyName("up")] IReadOnlyList<TaxonomyNode> Up,
    [property: JsonPropertyName("children")] IReadOnlyList<TaxonomyNode> Children);
