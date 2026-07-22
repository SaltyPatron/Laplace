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
