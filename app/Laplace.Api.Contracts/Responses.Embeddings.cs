using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

// OpenAI-shaped embeddings, but two-level: the dense `embedding` carries the S³ FORM coordinate
// (the only true vector Laplace has), while the Glicko-2 MEANING level — salient consensus neighbours,
// scaffolding filtered out by relation rank — rides in the per-item `laplace` extension. Form and
// meaning stay separated and individually inspectable: the crystal ball, not the black box.
public sealed record EmbeddingsResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<EmbeddingData> Data,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("usage")] EmbeddingsUsage Usage);

public sealed record EmbeddingData(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("embedding")] IReadOnlyList<double> Embedding,
    [property: JsonPropertyName("laplace")] EmbeddingProvenance Laplace);

public sealed record EmbeddingProvenance(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("resolved")] bool Resolved,
    [property: JsonPropertyName("entity_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntityId,
    [property: JsonPropertyName("form"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EmbeddingFormView? Form,
    [property: JsonPropertyName("meaning"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MeaningNeighborView>? Meaning);

// FORM: S³ geometry — coordinate on the Hopf surface (x,y,z,m), radius = compositional depth.
public sealed record EmbeddingFormView(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("m")] double M,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("constituents")] int Constituents);

// MEANING: one Glicko-2 nearest neighbour in the consensus field (eff_mu = rating − 2·rd).
public sealed record MeaningNeighborView(
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("eff_mu")] decimal EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses);

public sealed record EmbeddingsUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);
