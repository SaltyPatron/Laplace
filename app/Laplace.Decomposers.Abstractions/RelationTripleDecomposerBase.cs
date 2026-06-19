using System.Runtime.CompilerServices;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public abstract class RelationTripleDecomposerBase : IDecomposer, IIngestCommitPolicy
{
    public abstract Engine.Core.Hash128 SourceId     { get; }
    public abstract string              SourceName   { get; }
    public abstract int                 LayerOrder   { get; }
    public abstract Engine.Core.Hash128 TrustClassId { get; }

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    public virtual IngestCommitParallelism CommitParallelism => IngestCommitParallelism.Unordered;

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
            }

            await foreach (var change in StreamTriplesAsync(
                               context.EcosystemPath, TriplePass.AttestationsOnly, options, ct)
                               .WithCancellation(ct))
            {
                if (!options.DryRun) yield return change;
            }
        }
        else
        {
            await foreach (var change in StreamTriplesAsync(
                               context.EcosystemPath, TriplePass.Both, options, ct)
                               .WithCancellation(ct))
            {
                if (!options.DryRun) yield return change;
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
