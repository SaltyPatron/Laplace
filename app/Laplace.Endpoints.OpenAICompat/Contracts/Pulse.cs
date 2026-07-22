using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// The live scoreboard. The running totals of the witnessed graph plus the
/// ingest heartbeat — when a source is folding, these numbers climb and the
/// flush clock ticks. It is deliberately cheap (estimate counts + a 1.5ms
/// recency query) so a client can poll it every few seconds without cost.
/// </summary>
public sealed record PulseResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("at")] long At,
    [property: JsonPropertyName("entities")] long Entities,
    [property: JsonPropertyName("attestations")] long Attestations,
    [property: JsonPropertyName("consensus")] long Consensus,
    [property: JsonPropertyName("physicalities")] long Physicalities,
    // The heartbeat: flushes are working-set applies. Recent flushes mean a
    // source is at bat right now.
    [property: JsonPropertyName("last_flush_at")] long? LastFlushAt,
    [property: JsonPropertyName("flushes_last_min")] long FlushesLastMin,
    [property: JsonPropertyName("folding")] bool Folding);
