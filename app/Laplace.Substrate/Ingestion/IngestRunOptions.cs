using Laplace.Decomposers.Abstractions;

namespace Laplace.Ingestion;

public sealed record IngestRunOptions(
    DecomposerOptions DecomposerOptions,
    TransientErrorRetryPolicy RetryPolicy,
    IProgress<IngestProgress>? Progress,
    bool AbortOnTransientExhaustion = false,
    bool SkipLayerOrderingCheck = false,
    bool SkipSourceCompletion = false,
    string? EcosystemPath = null,
    int BatchSize = 1,
    int CommitRows = 0,
    bool BypassSourceCompletionGuard = false,
    bool RequireArtifactManifest = false)
{
    public static IngestRunOptions Default { get; } = new(
        DecomposerOptions: DecomposerOptions.Default,
        RetryPolicy: TransientErrorRetryPolicy.Default,
        Progress: null);
}
