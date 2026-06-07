using Laplace.Decomposers.Abstractions;

namespace Laplace.Ingestion;

public sealed record IngestRunOptions(
    DecomposerOptions          DecomposerOptions,
    int                        ParallelWorkers,
    TransientErrorRetryPolicy  RetryPolicy,
    IProgress<IngestProgress>? Progress,
    bool                       AbortOnTransientExhaustion = false,
    bool                       SkipLayerOrderingCheck = false,
    string?                    EcosystemPath = null,
    int                        BatchSize = 1,
    int                        CommitRows = 0)
{
    public static IngestRunOptions Default { get; } = new(
        DecomposerOptions:        DecomposerOptions.Default,
        ParallelWorkers:          1,
        RetryPolicy:              TransientErrorRetryPolicy.Default,
        Progress:                 null);
}
