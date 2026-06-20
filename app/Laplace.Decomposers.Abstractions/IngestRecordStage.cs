using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// One self-contained ingest unit's native record buffer. Entities + physicalities live in the
/// native <see cref="IntentStage"/> (zero managed-row marshal — the tuple buffer streams straight
/// to COPY). Low-volume PRECEDES / relation attestations ride alongside as managed rows until the
/// walk-journal fold subsumes them (Phase 4).
///
/// Phase 2 extends this with the per-tier candidate id lists (and an optional <c>TierTree</c> for
/// content units) that drive the O(tier) trunk→leaf dedup descent — a present trunk implies its
/// whole subtree is present, so existence is probed top-tier-first and the rest is skipped.
/// </summary>
public sealed class IngestRecordStage : IDisposable
{
    /// <summary>Native entity + physicality rows, COPY-ready via <see cref="IntentStage.TupleBuffer"/>.</summary>
    public IntentStage Stage { get; }

    /// <summary>Content-addressed root id of this unit (the trunk the dedup descent probes first).</summary>
    public Hash128 RootId { get; }

    /// <summary>
    /// PRECEDES / relation attestations carrying Glicko signal the native stage can't hold.
    /// Transitional: the walk-journal fold (Phase 4) will replace these with testimony walks.
    /// </summary>
    public ImmutableArray<AttestationRow> Attestations { get; }

    public IngestRecordStage(
        IntentStage stage, Hash128 rootId, ImmutableArray<AttestationRow> attestations)
    {
        Stage = stage ?? throw new ArgumentNullException(nameof(stage));
        RootId = rootId;
        Attestations = attestations;
    }

    public void Dispose() => Stage.Dispose();
}
