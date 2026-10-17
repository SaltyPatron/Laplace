using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;





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


public sealed record EmbeddingFormView(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("m")] double M,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("constituents")] int Constituents);


public sealed record MeaningNeighborView(
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("eff_mu")] decimal EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses);

public sealed record EmbeddingsUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

public sealed record RerankResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("query_entity_id")] string QueryEntityId,
    [property: JsonPropertyName("results")] IReadOnlyList<RerankResultItem> Results,
    [property: JsonPropertyName("usage")] EmbeddingsUsage Usage);

public sealed record RerankResultItem(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("relevance_score")] double RelevanceScore,
    [property: JsonPropertyName("document"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RerankDocumentView? Document,
    [property: JsonPropertyName("laplace")] RerankProvenance Laplace);

public sealed record RerankDocumentView(
    [property: JsonPropertyName("text")] string Text);

public sealed record RerankProvenance(
    [property: JsonPropertyName("entity_id")] string EntityId,
    [property: JsonPropertyName("frechet_distance")] double FrechetDistance,
    [property: JsonPropertyName("centroid_distance")] double CentroidDistance,
    [property: JsonPropertyName("word_overlap")] double WordOverlap,
    [property: JsonPropertyName("shared_words")] int SharedWords,
    [property: JsonPropertyName("query_words")] int QueryWords,
    [property: JsonPropertyName("document_words")] int DocumentWords);
