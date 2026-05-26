using System.Runtime.CompilerServices;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Abstract base for relation-triple decomposers (Atomic2020, ConceptNet).
///
/// <para>Single-pass decomposers (<see cref="RequiresTwoPass"/> = false)
/// emit entities AND attestations in one streaming pass over the file.</para>
///
/// <para>Two-pass decomposers (<see cref="RequiresTwoPass"/> = true, required
/// for cyclic graphs like ConceptNet) run two full passes over the file:
/// pass 1 emits entity + physicality rows only; pass 2 emits attestation rows
/// only. This ensures every relation's subject/object entity exists before the
/// attestation that references it is written.</para>
/// </summary>
public abstract class RelationTripleDecomposerBase : IDecomposer
{
    public abstract Engine.Core.Hash128 SourceId     { get; }
    public abstract string              SourceName   { get; }
    public abstract int                 LayerOrder   { get; }
    public abstract Engine.Core.Hash128 TrustClassId { get; }

    /// <summary>Return true iff the source has cyclic concept references
    /// that require entities to be committed before attestations reference
    /// them. ConceptNet = true; Atomic2020 = false.</summary>
    protected abstract bool RequiresTwoPass { get; }

    public abstract Task InitializeAsync(
        IDecomposerContext context, CancellationToken ct = default);

    public abstract Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Stream raw triple rows from the data file. The same method
    /// is called twice for two-pass sources; implementations MUST be
    /// independently restartable (re-open the file each time).</summary>
    protected abstract IAsyncEnumerable<SubstrateChange> StreamTriplesAsync(
        string ecosystemPath, TriplePass pass, DecomposerOptions options, CancellationToken ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (RequiresTwoPass)
        {
            // Pass 1: entities + physicalities only
            await foreach (var change in StreamTriplesAsync(
                               context.EcosystemPath, TriplePass.EntitiesOnly, options, ct)
                               .WithCancellation(ct))
            {
                if (!options.DryRun) yield return change;
                await Task.Yield();
            }

            // Pass 2: attestations only
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

/// <summary>Which rows to emit during a streaming pass.</summary>
public enum TriplePass
{
    Both              = 0,  // single-pass: emit entities + attestations together
    EntitiesOnly      = 1,  // two-pass pass 1: emit entity/physicality rows only
    AttestationsOnly  = 2,  // two-pass pass 2: emit attestation rows only
}
