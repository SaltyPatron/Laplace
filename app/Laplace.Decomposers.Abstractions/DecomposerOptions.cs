namespace Laplace.Decomposers.Abstractions;

public sealed record DecomposerOptions(
    int                       BatchSize,
    bool                      DryRun,
    IReadOnlySet<string>?     IncludeFilter,
    IReadOnlySet<string>?     ExcludeFilter,
    LanguageFilter?           Languages = null,
    /// <summary>Emit IS_TRANSLATION_OF and other cross-language edges (Wiktionary translations, etc.).</summary>
    bool                      EmitCrossLanguageLinks = true)
{
    public static DecomposerOptions Default { get; } =
        new(BatchSize: 1, DryRun: false, IncludeFilter: null, ExcludeFilter: null);

    public static DecomposerOptions ForWitness(
        string sourceKey,
        int batchSize = 2048,
        LanguageFilter? languageOverride = null,
        bool? emitCrossLanguageLinks = null)
    {
        var langs = languageOverride ?? LanguageFilter.ForSource(sourceKey);
        bool crossLang = emitCrossLanguageLinks ?? (
            langs?.IsActive != true
            || string.Equals(
                Environment.GetEnvironmentVariable("LAPLACE_EMIT_CROSS_LANG"),
                "1", StringComparison.Ordinal)
            || string.Equals(
                Environment.GetEnvironmentVariable("LAPLACE_EMIT_CROSS_LANG"),
                "true", StringComparison.OrdinalIgnoreCase));
        return Default with
        {
            BatchSize = batchSize,
            Languages = langs,
            EmitCrossLanguageLinks = crossLang,
        };
    }
}
