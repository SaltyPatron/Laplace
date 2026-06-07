using System.Runtime.CompilerServices;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public abstract class LexicalDecomposerBase<TEntry> : IDecomposer
{
    public abstract Engine.Core.Hash128 SourceId       { get; }
    public abstract string              SourceName     { get; }
    public abstract int                 LayerOrder     { get; }
    public abstract Engine.Core.Hash128 TrustClassId   { get; }

    public abstract Task InitializeAsync(
        IDecomposerContext context, CancellationToken ct = default);

    public abstract Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected abstract IAsyncEnumerable<TEntry> ParseEntriesAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct);

    protected abstract SubstrateChange? EntryToChange(TEntry entry);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var entry in ParseEntriesAsync(context.EcosystemPath, options, ct)
                           .WithCancellation(ct))
        {
            var change = EntryToChange(entry);
            if (change is null) continue;
            if (!options.DryRun)
                yield return change;
            await Task.Yield();
        }
    }
}
