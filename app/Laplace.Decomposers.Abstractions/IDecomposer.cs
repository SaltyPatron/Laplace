using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Canonical C# plugin contract every per-source decomposer implements per
/// . Examples: UnicodeDecomposer (Layer 0, #183), ISODecomposer,
/// WordNetDecomposer, OMWDecomposer, UDDecomposer,
/// WiktionaryDecomposer, TatoebaDecomposer, ConceptNetDecomposer
///, Atomic2020Decomposer, TreeSitterDecomposer,
/// the composite ModelDecomposer.
///
/// <para>
/// Lifecycle: caller (typically <see cref="IngestRunner"/>)
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
/// converges them transparently. No coordination logic
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

    /// <summary> layer order (0/1 = Unicode atoms; ~10 = Model).
    /// Used by orchestration to enforce layer ordering for fresh-substrate
    /// bootstrap: Layer N's decomposer requires Layer 0..N-1 to have completed.
    /// </summary>
    int LayerOrder { get; }

    /// <summary>Trust class assigned to this source. Recorded
    /// as a HAS_TRUST_CLASS meta-attestation on the source entity on first run.
    /// </summary>
    Hash128 TrustClassId { get; }

    /// <summary>Initialize — verify ecosystem path exists, register source
    /// entity if not in substrate, bootstrap the decomposer's own type
    /// vocabulary + attestation kind vocabulary + arena-semantics
    /// meta-attestations. Idempotent: re-init on an already-
    /// bootstrapped substrate is a no-op (via SubstrateCRUD ON CONFLICT
    ///).</summary>
    Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default);

    /// <summary>Decompose the source's full domain ecosystem into a STREAM
    /// of <see cref="SubstrateChange"/> intents. Each yielded
    /// intent is one source-content-unit (one WordNet synset; one Wiktionary
    /// entry; one model tokenizer vocab batch; etc.). The caller hands each
    /// intent to <see cref="ISubstrateWriter.ApplyAsync"/>.
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

    /// <summary>The dynamic classifier / value canonical names this source
    /// mints — the names that are data-derived (not in the extension's static
    /// seed vocabulary), so <c>laplace.render()</c> cannot answer them in names
    /// until they are registered. Read AFTER <see cref="DecomposeAsync"/> by the
    /// ingest driver and registered post-ingest (via
    /// <c>laplace.register_canonicals</c>) so render() answers in names instead
    /// of hex. Static vocabulary ships in the extension seed and is NOT included
    /// here. Default: none.</summary>
    IReadOnlyCollection<string> CanonicalNamesForReadback => Array.Empty<string>();
}
