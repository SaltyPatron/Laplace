using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The one authority for served model ids. /v1/models advertises exactly this list
/// and the endpoints route on exact ids — substring routing on the model field was
/// an English-dispatch hack (spec 34); an unknown model is a 400, never a silent
/// fallback lane.
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
        new ModelInfo(Code, "model", 0, "laplace"),
        new ModelInfo(EmbedForm, "model", 0, "laplace"),
        new ModelInfo(EmbedMeaning, "model", 0, "laplace"),
    ];

    public static bool IsConverse(string model) =>
        string.Equals(model, Converse, StringComparison.Ordinal);

    public static bool IsChatModel(string model) =>
        model is Converse or Completions or Code;

    public static bool IsCompletionsModel(string model) =>
        model is Completions or Code;

    /// <summary>False = unknown embedding model; includeMeaning distinguishes the two lanes.</summary>
    public static bool TryEmbeddingModel(string model, out bool includeMeaning)
    {
        includeMeaning = string.Equals(model, EmbedMeaning, StringComparison.Ordinal);
        return model is EmbedForm or EmbedMeaning;
    }
}
