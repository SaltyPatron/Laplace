namespace Laplace.Decomposers.Abstractions;

using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Base contract every modality / source decomposer implements. Decomposers
/// are THIN orchestrators over shared services from <c>Laplace.Core</c> and
/// <c>Laplace.Pipeline</c> — they consume <c>IIdentityHashing</c>,
/// <c>ITextDecomposition</c>, <c>INumberDecomposition</c>, emission services,
/// etc. They contain no compute primitives.
///
/// Conventionally 100-300 lines: parse the source format, route content
/// through services, emit substrate edges with provenance.
/// </summary>
public interface IDecomposer
{
    /// <summary>Stable canonical name (e.g. "wordnet", "tatoeba", "huggingface_model").</summary>
    string Name { get; }

    /// <summary>Substrate provenance source entity for this decomposer (resolved via IConceptEntityResolver at startup).</summary>
    AtomId ProvenanceSource { get; }

    /// <summary>
    /// Run the decomposition. Implementations are bounded by the input
    /// (per-file, per-directory, per-corpus); they emit substrate state and
    /// return when done. Idempotent: re-running on the same input is a no-op
    /// (content addressing + provenance edge dedup).
    /// </summary>
    Task RunAsync(
        DecomposerInput input,
        CancellationToken cancellationToken);
}

/// <summary>
/// Input descriptor for a decomposition run. Format-specific decomposers
/// extend this via their own derived input records.
/// </summary>
public abstract record DecomposerInput(string SourcePath);
