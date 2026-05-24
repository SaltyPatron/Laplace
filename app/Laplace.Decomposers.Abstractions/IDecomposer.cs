using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Canonical C# plugin contract every per-source decomposer implements per
/// ADR 0051. Examples: UnicodeDecomposer (Layer 0, #183), ISODecomposer (#193),
/// WordNetDecomposer (#184), OMWDecomposer (#185), UDDecomposer (#186),
/// WiktionaryDecomposer (#187), TatoebaDecomposer (#188), ConceptNetDecomposer
/// (#190), Atomic2020Decomposer (#189), TreeSitterDecomposer (#194),
/// the composite ModelDecomposer (#191) per ADR 0043.
///
/// <para>
/// Lifecycle: caller (typically <see cref="IngestRunner"/> per ADR 0052)
/// calls <see cref="InitializeAsync"/> exactly once, then iterates
/// <see cref="DecomposeAsync"/>'s yielded intents, then disposes.
/// <see cref="DisposeAsync"/> releases any file handles / readers /
/// network connections the decomposer held open.
/// </para>
///
/// <para>
/// Streaming is mandatory: frontier-scale sources (37 GB Unicode, 34 GB
/// Wiktionary, 125 GB DeepSeek-Coder) cannot buffer everything in RAM.
/// Per-unit yield with backpressure via <see cref="IAsyncEnumerable{T}"/>.
/// Smaller sources may yield one large intent + return; the contract supports
/// both shapes.
/// </para>
///
/// <para>
/// Cross-decomposer entity sharing is automatic via content-addressing —
/// when two decomposers emit the same canonical content, they produce the
/// same BLAKE3-128 entity id and SubstrateCRUD's <c>ON CONFLICT DO NOTHING</c>
/// (per RULES R5) converges them transparently. No coordination logic
/// required in either decomposer.
/// </para>
/// </summary>
public interface IDecomposer : IAsyncDisposable
{
    /// <summary>Identity of this decomposer — the source entity id it emits
    /// as <c>source_id</c> on every entity / physicality / attestation it
    /// produces. Content-addressed:
    /// <c>BLAKE3-128(canonical_name)</c> where the canonical name follows
    /// the convention <c>substrate/source/&lt;DecomposerName&gt;/v1</c>.
    /// </summary>
    Hash128 SourceId { get; }

    /// <summary>Human-readable name for logs / observability / CLI.</summary>
    string SourceName { get; }

    /// <summary>Per ADR 0037 layer order (0/1 = Unicode atoms; ~10 = Model).
    /// Used by orchestration to enforce layer ordering for fresh-substrate
    /// bootstrap: Layer N's decomposer requires Layer 0..N-1 to have completed.
    /// </summary>
    int LayerOrder { get; }

    /// <summary>Trust class assigned to this source per ADR 0044. Recorded
    /// as a HAS_TRUST_CLASS meta-attestation on the source entity on first run.
    /// </summary>
    Hash128 TrustClassId { get; }

    /// <summary>Initialize — verify ecosystem path exists, register source
    /// entity if not in substrate, bootstrap the decomposer's own type
    /// vocabulary + attestation kind vocabulary + arena-semantics
    /// meta-attestations per ADR 0042. Idempotent: re-init on an already-
    /// bootstrapped substrate is a no-op (via SubstrateCRUD ON CONFLICT
    /// per RULES R5).</summary>
    Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default);

    /// <summary>Decompose the source's full domain ecosystem into a STREAM
    /// of <see cref="SubstrateChange"/> intents per ADR 0049. Each yielded
    /// intent is one source-content-unit (one WordNet synset; one Wiktionary
    /// entry; one model tokenizer vocab batch; etc.). The caller hands each
    /// intent to <see cref="ISubstrateWriter.ApplyAsync"/> per ADR 0050.
    /// </summary>
    IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        CancellationToken ct = default);

    /// <summary>Estimate the total number of source-content-units this
    /// decomposer will yield, for progress reporting. Cheap to compute
    /// (typically a file count or index lookup). Return null if estimation
    /// is expensive or impossible.</summary>
    Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default);
}
