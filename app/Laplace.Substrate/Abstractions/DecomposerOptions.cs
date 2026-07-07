namespace Laplace.Decomposers.Abstractions;

public sealed record DecomposerOptions(
    int BatchSize,
    bool DryRun,
    IReadOnlySet<string>? IncludeFilter,
    IReadOnlySet<string>? ExcludeFilter,
    LanguageFilter? Languages = null,

    bool EmitCrossLanguageLinks = true,
    long MaxInputUnits = 0)
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
        bool crossLang = emitCrossLanguageLinks ?? (langs?.IsActive != true);
        return Default with
        {
            BatchSize = batchSize,
            Languages = langs,
            EmitCrossLanguageLinks = crossLang,
        };
    }
}
