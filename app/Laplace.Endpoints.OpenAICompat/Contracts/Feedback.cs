using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

/// <summary>
/// The caller confirms or refutes one claim, [subject, relation, object] or [subject, relation] when the object is
/// omitted: each part the text it is, the relation the caller's own word for it.
/// </summary>
public sealed record FeedbackRequest(
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("relation")] string? Relation = null,
    [property: JsonPropertyName("object")] string? Object = null);

/// <summary>A claim's Native standing: rating, deviation, games folded, and its conservative rating at k = 2.</summary>
public sealed record FeedbackStanding(
    [property: JsonPropertyName("rating")] double Rating,
    [property: JsonPropertyName("deviation")] double Deviation,
    [property: JsonPropertyName("matches")] long Matches,
    [property: JsonPropertyName("confidence")] double Confidence);

public sealed record FeedbackResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("participant")] string Participant,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("claim_object")] string ClaimObject,
    [property: JsonPropertyName("witness_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WitnessId,
    [property: JsonPropertyName("claim_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClaimId,
    [property: JsonPropertyName("standing_before")] FeedbackStanding? StandingBefore,
    [property: JsonPropertyName("standing_after")] FeedbackStanding? StandingAfter);
