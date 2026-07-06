using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// Gödel-engine feedback (doc 15 G3): confirm/refute either a token chain
/// (PRECEDES pairs — the generation walk's own edges) or one explicit
/// (subject, relation, object) consensus triple.
/// </summary>
public sealed record FeedbackRequest(
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("tokens")] IReadOnlyList<string>? Tokens = null,
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("relation")] string? Relation = null,
    [property: JsonPropertyName("object")] string? Object = null);

public sealed record FeedbackTokenStatus(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("status")] string Status);

public sealed record FeedbackConsensusState(
    [property: JsonPropertyName("rating")] long Rating,
    [property: JsonPropertyName("rd")] long Rd,
    [property: JsonPropertyName("witness_count")] long WitnessCount);

public sealed record FeedbackResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("attestations_inserted")] long AttestationsInserted,
    [property: JsonPropertyName("consensus_updated")] long ConsensusUpdated,
    [property: JsonPropertyName("tokens")] IReadOnlyList<FeedbackTokenStatus>? Tokens = null,
    [property: JsonPropertyName("relation")] string? Relation = null,
    [property: JsonPropertyName("consensus_before")] FeedbackConsensusState? ConsensusBefore = null,
    [property: JsonPropertyName("consensus_after")] FeedbackConsensusState? ConsensusAfter = null);
