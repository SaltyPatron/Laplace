using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// Estimated totals of entities, attestations, consensus, and physicalities, plus recent
/// working-set flush activity. Estimates and one recency read keep it cheap to poll.
/// </summary>
public sealed record PulseResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("at")] long At,
    [property: JsonPropertyName("entities")] long Entities,
    [property: JsonPropertyName("attestations")] long Attestations,
    [property: JsonPropertyName("consensus")] long Consensus,
    [property: JsonPropertyName("physicalities")] long Physicalities,
    // Flushes are working-set applies; recent ones mean a source is being folded.
    [property: JsonPropertyName("last_flush_at")] long? LastFlushAt,
    [property: JsonPropertyName("flushes_last_min")] long FlushesLastMin,
    [property: JsonPropertyName("folding")] bool Folding);
