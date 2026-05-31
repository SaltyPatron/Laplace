namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Per-run options for an <see cref="IDecomposer"/>. Per ADR 0051 lines 123–130.
/// </summary>
/// <param name="BatchSize">Soft cap on source-content-units per intent batch.
/// Decomposers MAY ignore (e.g. small sources yielding one big intent).</param>
/// <param name="DryRun">Build intents but don't write them through the
/// substrate. Useful for shape verification + perf measurement without DB
/// side-effects.</param>
/// <param name="IncludeFilter">Per-decomposer source-content-unit filter
/// (e.g. WordNet "only synsets in lexname 'noun.animal'"). Semantics are
/// per-decomposer; null = no filter.</param>
/// <param name="ExcludeFilter">Inverse of <see cref="IncludeFilter"/>.</param>
public sealed record DecomposerOptions(
    int                       BatchSize,
    bool                      DryRun,
    IReadOnlySet<string>?     IncludeFilter,
    IReadOnlySet<string>?     ExcludeFilter)
{
    /// <summary>Reasonable defaults — single-source unit per intent, no
    /// dry-run, no filters.</summary>
    public static DecomposerOptions Default { get; } =
        new(BatchSize: 1, DryRun: false, IncludeFilter: null, ExcludeFilter: null);
}
