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
    string?                    EcosystemPath = null,
    /// <summary>Number of intents coalesced into one
    /// <see cref="SubstrateCRUD.ISubstrateWriter.ApplyManyAsync"/> call. 1 =
    /// legacy per-intent apply. &gt;1 = batched COPY: one existence pass and one
    /// COPY per table per batch, the throughput path for mechanical bulk
    /// sources. Combines with <see cref="ParallelWorkers"/> (each worker fills
    /// and flushes its own batch).</summary>
    int                        BatchSize = 1,
    /// <summary>Rows-per-commit dial. When &gt;0, a batch is flushed once its
    /// accumulated row count (entities + physicalities + attestations across the
    /// buffered intents) reaches this target, instead of after a fixed
    /// <see cref="BatchSize"/> intent count. This is the memory-safe, scale-invariant
    /// knob: one QK intent can carry thousands of attestations, so counting intents
    /// makes the real commit size swing wildly with relation density; counting rows
    /// pins the COPY payload (≈ rows × ~200 B) regardless of intent fan-out. 0 =
    /// fall back to intent-count batching. Combines with <see cref="BatchSize"/>
    /// (which still caps buffered intents) and <see cref="ParallelWorkers"/>.</summary>
    int                        CommitRows = 0)
{
    public static IngestRunOptions Default { get; } = new(
        DecomposerOptions:        DecomposerOptions.Default,
        ParallelWorkers:          1,
        CheckpointFlushInterval:  TimeSpan.FromSeconds(30),
        RetryPolicy:              TransientErrorRetryPolicy.Default,
        Progress:                 null);
}
