namespace Laplace.Decomposers.Abstractions;

public sealed record DecomposerOptions(
    int                       BatchSize,
    bool                      DryRun,
    IReadOnlySet<string>?     IncludeFilter,
    IReadOnlySet<string>?     ExcludeFilter)
{
    public static DecomposerOptions Default { get; } =
        new(BatchSize: 1, DryRun: false, IncludeFilter: null, ExcludeFilter: null);
}
