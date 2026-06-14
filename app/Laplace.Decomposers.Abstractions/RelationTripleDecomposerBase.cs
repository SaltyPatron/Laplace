using System.Runtime.CompilerServices;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public abstract class RelationTripleDecomposerBase : IDecomposer, IIngestCommitPolicy
{
    public abstract Engine.Core.Hash128 SourceId     { get; }
    public abstract string              SourceName   { get; }
    public abstract int                 LayerOrder   { get; }
    public abstract Engine.Core.Hash128 TrustClassId { get; }

    /// <summary>
    /// Single-pass relation triples are SELF-CONTAINED — each intent creates the concept
    /// entities it references (ContentEmitter.Emit in the same change), so no intent depends
    /// on an entity introduced by another. That makes them parallel-commit safe:
    ///   • the writers are concurrent — ProvenIdCache is a ConcurrentDictionary, connections
    ///     are per-call, and ConsensusAccumulatingWriter mutates each Acc under lock(acc);
    ///   • the staging merge inserts "... FROM stage ORDER BY id ON CONFLICT DO NOTHING" and
    ///     locks attestation rows "ORDER BY a.id FOR UPDATE", so concurrent committers acquire
    ///     row locks in the SAME order — no 40P01 cycle can form (and class-40 retries backstop
    ///     any that still slip through under extreme contention).
    /// The hot-concept-row deadlock that once forced serial (the conceptnet/omw env pins,
    /// retired 2026-06-12) is resolved by that ordered merge — so the parallel path the
    /// IngestRunner already implements (RunUnorderedParallelAsync) is the correct default here.
    ///
    /// Two-pass variants emit ALL entities then ALL attestations across separate intents, so an
    /// attestation could commit before its entity under parallel workers; they stay StrictSerial
    /// until they stamp CommitEpoch (entities epoch N, attestations epoch N+1) for EpochBarrier.
    /// </summary>
    // NOTE: parallel (Unordered) commit is correct in principle here — these intents are
    // self-contained — BUT ConceptNet's hot concept rows make ~most concurrent batches
    // deadlock (40P01), and the runner's retry-the-batch path is broken for prebuilt
    // IntentStages (ApplyManyAsync disposes them on attempt 1 -> ObjectDisposedException on
    // retry). Until the commit is sharded by content-id (disjoint rows -> no deadlock, no
    // retry) OR the stage lifecycle survives retries, StrictSerial is the working path.
    public virtual IngestCommitParallelism CommitParallelism => IngestCommitParallelism.StrictSerial;

    protected abstract bool RequiresTwoPass { get; }

    public abstract Task InitializeAsync(
        IDecomposerContext context, CancellationToken ct = default);

    public abstract Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected abstract IAsyncEnumerable<SubstrateChange> StreamTriplesAsync(
        string ecosystemPath, TriplePass pass, DecomposerOptions options, CancellationToken ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (RequiresTwoPass)
        {
            await foreach (var change in StreamTriplesAsync(
                               context.EcosystemPath, TriplePass.EntitiesOnly, options, ct)
                               .WithCancellation(ct))
            {
                if (!options.DryRun) yield return change;
                await Task.Yield();
            }

            await foreach (var change in StreamTriplesAsync(
                               context.EcosystemPath, TriplePass.AttestationsOnly, options, ct)
                               .WithCancellation(ct))
            {
                if (!options.DryRun) yield return change;
                await Task.Yield();
            }
        }
        else
        {
            await foreach (var change in StreamTriplesAsync(
                               context.EcosystemPath, TriplePass.Both, options, ct)
                               .WithCancellation(ct))
            {
                if (!options.DryRun) yield return change;
                await Task.Yield();
            }
        }
    }
}

public enum TriplePass
{
    Both              = 0,
    EntitiesOnly      = 1,
    AttestationsOnly  = 2,
}
