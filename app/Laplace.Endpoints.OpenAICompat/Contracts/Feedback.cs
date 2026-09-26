using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// Confirm/refute testimony on either a token chain (consecutive PRECEDES pairs) or one
/// explicit (subject, relation, object) triple.
/// </summary>
public sealed record FeedbackRequest(
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("tokens")] IReadOnlyList<string>? Tokens = null,
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("relation")] string? Relation = null,
    [property: JsonPropertyName("object")] string? Object = null,
    [property: JsonPropertyName("request_id")] string? RequestId = null);

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
