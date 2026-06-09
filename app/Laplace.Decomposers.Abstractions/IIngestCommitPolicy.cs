namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// How a decomposer's intent stream may be committed when LAPLACE_INGEST_WORKERS &gt; 1.
/// </summary>
public enum IngestCommitParallelism
{
    /// <summary>
    /// Intents may reference entities first introduced in earlier intents; commits must stay
    /// strictly in producer order (pipelined decompose+commit overlap only).
    /// </summary>
    StrictSerial,

    /// <summary>
    /// Parallel commits are allowed within the same commit epoch; all intents in epoch N must
    /// finish before epoch N+1 starts.
    /// </summary>
    EpochBarrier,

    /// <summary>
    /// Intents are mutually independent (synthetic tests). No ordering guarantees required.
    /// </summary>
    Unordered,
}

/// <summary>
/// Optional capability on <see cref="IDecomposer"/>.
/// Default when absent: <see cref="IngestCommitParallelism.EpochBarrier"/>.
/// </summary>
public interface IIngestCommitPolicy
{
    IngestCommitParallelism CommitParallelism { get; }
}
