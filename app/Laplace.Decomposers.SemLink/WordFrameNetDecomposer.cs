using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.SemLink;

public sealed class WordFrameNetDecomposer : IDecomposer, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WordFrameNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "WordFrameNetDecomposer";
    public int     LayerOrder   => 3;
    public Hash128 TrustClassId => TrustClass;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["FrameNet_LU"],
            relationNodeNames: ["CORRESPONDS_TO"],
            ct: ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);

        int batchSize = options.BatchSize > 0 ? options.BatchSize : 4096;
        long cap = options.MaxInputUnits;
        long consumed = 0;
        foreach (string path in WordFrameNetIngest.ResolvePaths(context.EcosystemPath))
        {
            if (cap > 0 && consumed >= cap) yield break;
            long fileCap = cap > 0 ? cap - consumed : 0;
            await foreach (var change in WordFrameNetIngest.StreamAsync(path, batchSize, fileCap, ct))
            {
                if (!options.DryRun)
                {
                    consumed += change.Metadata.InputUnitsConsumed;
                    yield return change;
                }
                if (cap > 0 && consumed >= cap) yield break;
            }
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = WordFrameNetIngest.ResolvePaths(context.EcosystemPath).ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        return Task.FromResult(IngestInventory.FromFiles("records", paths, options.MaxInputUnits, ct));
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long total = 0;
        foreach (string path in WordFrameNetIngest.ResolvePaths(context.EcosystemPath))
        {
            long? lines = await WordFrameNetIngest.EstimateLineCountAsync(path, ct);
            if (lines is not null) total += lines.Value;
        }
        return total > 0 ? total : null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
