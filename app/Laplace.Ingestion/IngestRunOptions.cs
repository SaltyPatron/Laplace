using Laplace.Decomposers.Abstractions;

namespace Laplace.Ingestion;

/// <summary>
/// Per-<see cref="IngestRunner.RunAsync"/> configuration. Per ADR 0052
/// lines 67–72 plus the fatal-error abort policy.
/// </summary>
public sealed record IngestRunOptions(
    DecomposerOptions          DecomposerOptions,
    int                        ParallelWorkers,
    TimeSpan                   CheckpointFlushInterval,
    TransientErrorRetryPolicy  RetryPolicy,
    IProgress<IngestProgress>? Progress,
    string?                    CheckpointPathOverride = null,
    bool                       AbortOnTransientExhaustion = false,
    bool                       SkipLayerOrderingCheck = false,
    string?                    EcosystemPath = null)
{
    public static IngestRunOptions Default { get; } = new(
        DecomposerOptions:        DecomposerOptions.Default,
        ParallelWorkers:          1,
        CheckpointFlushInterval:  TimeSpan.FromSeconds(30),
        RetryPolicy:              TransientErrorRetryPolicy.Default,
        Progress:                 null);
}
