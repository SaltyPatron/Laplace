namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Per-run options for an <see cref="IDecomposer"/>. Per ADR 0051 lines 123–130.
/// </summary>
/// <param name="BatchSize">Soft cap on source-content-units per intent batch.
/// Decomposers MAY ignore (e.g. small sources yielding one big intent).</param>
/// <param name="DryRun">Build intents but don't write them through the
/// substrate. Useful for shape verification + perf measurement without DB
/// side-effects.</param>
/// <param name="ResumeFromCheckpoint">Skip intents already recorded in the
/// checkpoint journal (per <see cref="CheckpointPath"/>). IngestRunner-level
/// concern — decomposers should be agnostic but the option threads through.</param>
/// <param name="CheckpointPath">Override the default journal path
/// (<c>{EcosystemPath}/checkpoint.bin</c>).</param>
/// <param name="IncludeFilter">Per-decomposer source-content-unit filter
/// (e.g. WordNet "only synsets in lexname 'noun.animal'"). Semantics are
/// per-decomposer; null = no filter.</param>
/// <param name="ExcludeFilter">Inverse of <see cref="IncludeFilter"/>.</param>
public sealed record DecomposerOptions(
    int                       BatchSize,
    bool                      DryRun,
    bool                      ResumeFromCheckpoint,
    string?                   CheckpointPath,
    IReadOnlySet<string>?     IncludeFilter,
    IReadOnlySet<string>?     ExcludeFilter)
{
    /// <summary>Reasonable defaults — single-source unit per intent, no
    /// dry-run, resume on, default checkpoint path, no filters.</summary>
    public static DecomposerOptions Default { get; } =
        new(BatchSize: 1, DryRun: false, ResumeFromCheckpoint: true,
            CheckpointPath: null, IncludeFilter: null, ExcludeFilter: null);
}
