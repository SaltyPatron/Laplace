using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Model ids the OpenAI-compatible interface accepts. Each id selects an operation of the
/// substrate, not a separate model. <see cref="All"/> is what /v1/models lists;
/// <see cref="Code"/> is accepted by exact dispatch but not listed.
/// </summary>
internal static class ModelCatalog
{
    public const string Converse = "laplace-converse-001";
    public const string Completions = "laplace-completions-001";
    public const string Code = "laplace-code-001";
    public const string EmbedForm = "laplace-embed-form-001";
    public const string EmbedMeaning = "laplace-embed-meaning-001";

    public static readonly ModelInfo[] All =
    [
        new ModelInfo(Converse, "model", 0, "laplace"),
        new ModelInfo(Completions, "model", 0, "laplace"),
        new ModelInfo(EmbedForm, "model", 0, "laplace"),
        new ModelInfo(EmbedMeaning, "model", 0, "laplace"),
    ];

    public static bool IsConverse(string model) =>
        string.Equals(model, Converse, StringComparison.Ordinal);

    public static bool IsChatModel(string model) =>
        model is Converse or Completions;

    public static bool IsCode(string model) =>
        string.Equals(model, Code, StringComparison.Ordinal);

    public static bool IsCompletionsModel(string model) =>
        model is Completions;

    /// <summary>False for an unknown embedding model; <paramref name="includeMeaning"/> is true for
    /// the meaning model, which adds consensus neighbours to the physical form.</summary>
    public static bool TryEmbeddingModel(string model, out bool includeMeaning)
    {
        includeMeaning = string.Equals(model, EmbedMeaning, StringComparison.Ordinal);
        return model is EmbedForm or EmbedMeaning;
    }
}
