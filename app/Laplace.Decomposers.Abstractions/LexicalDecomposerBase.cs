using System.Runtime.CompilerServices;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Abstract base for streaming lexical decomposers (WordNet, OMW, UD,
/// Wiktionary, …). Handles the boilerplate streaming loop:
/// <see cref="ParseEntriesAsync"/> yields raw parsed entries; each entry
/// goes to <see cref="EntryToChange"/> for conversion to a
/// <see cref="SubstrateChange"/>; <see cref="DecomposeAsync"/> streams the
/// results with backpressure.
///
/// <para>This base class does NOT inject <see cref="TextEntityBuilder"/> —
/// entry-to-change methods construct and drive it directly because each
/// decomposer has its own text-resolution flow (resolver delegate,
/// tier-type overrides, multi-text-per-entry logic). The base handles only
/// the per-entry loop, cancellation, and optional dry-run bypass.</para>
/// </summary>
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

    /// <summary>Stream raw parsed entries from the ecosystem on disk.
    /// Implementations should be lazy (no bulk-load into RAM).</summary>
    protected abstract IAsyncEnumerable<TEntry> ParseEntriesAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct);

    /// <summary>Convert one parsed entry to a <see cref="SubstrateChange"/>
    /// intent. May return null to silently skip malformed entries.</summary>
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
