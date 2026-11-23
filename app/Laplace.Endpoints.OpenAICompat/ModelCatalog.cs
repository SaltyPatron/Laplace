using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Model ids the OpenAI-compatible interface accepts. Maps product aliases and all
/// 8 Laplace firmware profiles (<c>*.firmware</c>) onto <c>lp_engine_turn</c>.
/// </summary>
internal static class ModelCatalog
{
    public const string Converse = "laplace-converse-001";
    public const string Completions = "laplace-completions-001";
    public const string Code = "laplace-code-001";
    public const string Rerank = "laplace-rerank-001";
    public const string EmbedForm = "laplace-embed-form-001";
    public const string EmbedMeaning = "laplace-embed-meaning-001";

    public static readonly string[] FirmwareProfiles =
    [
        "program",
        "coding",
        "deductive",
        "inductive",
        "abductive",
        "constructive",
        "adversarial",
        "chess",
    ];

    public static readonly ModelInfo[] All =
    [
        new ModelInfo(Converse, "model", 0, "laplace"),
        new ModelInfo(Completions, "model", 0, "laplace"),
        new ModelInfo(Code, "model", 0, "laplace"),
        new ModelInfo(EmbedForm, "model", 0, "laplace"),
        new ModelInfo(EmbedMeaning, "model", 0, "laplace"),
        new ModelInfo("program", "model", 0, "laplace"),
        new ModelInfo("coding", "model", 0, "laplace"),
        new ModelInfo("deductive", "model", 0, "laplace"),
        new ModelInfo("inductive", "model", 0, "laplace"),
        new ModelInfo("abductive", "model", 0, "laplace"),
        new ModelInfo("constructive", "model", 0, "laplace"),
        new ModelInfo("adversarial", "model", 0, "laplace"),
        new ModelInfo("chess", "model", 0, "laplace"),
    ];

    public static bool IsConverse(string model) =>
        string.Equals(model, Converse, StringComparison.Ordinal);

    public static bool IsChatModel(string model) =>
        model is Converse or Completions or Code
        || FirmwareProfiles.Contains(model, StringComparer.Ordinal);

    public static bool IsCode(string model) =>
        model is Code or "coding";

    public static bool IsRerankModel(string model) =>
        model is Rerank or EmbedForm or EmbedMeaning or "laplace" or "laplace-substrate";

    public static bool IsCompletionsModel(string model) =>
        model is Completions or Converse
        || FirmwareProfiles.Contains(model, StringComparer.Ordinal);

    public static string ResolveFirmwareModel(string model) => model switch
    {
        Code or "coding" => "coding",
        "deductive" or "inductive" or "abductive" or "constructive" or "adversarial" or "chess" or "program" => model,
        _ => "program",
    };

    /// <summary>False for an unknown embedding model; <paramref name="includeMeaning"/> is true for
    /// the meaning model, which adds consensus neighbours to the physical form.</summary>
    public static bool TryEmbeddingModel(string model, out bool includeMeaning)
    {
        includeMeaning = string.Equals(model, EmbedMeaning, StringComparison.Ordinal);
        return model is EmbedForm or EmbedMeaning or "laplace" or "laplace-substrate";
    }
}
