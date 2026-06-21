using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.SemLink;

/// <summary>
/// Ingests WordFrameNet FrameNet LU → WordNet synset mapping TSV (Adimen WFN / XWFN packages).
/// </summary>
public sealed class WordFrameNetDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WordFrameNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "WordFrameNetDecomposer";
    public int     LayerOrder   => 3;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("FrameNet_LU");
        boot.AddRelationType("CORRESPONDS_TO");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);

        int batchSize = options.BatchSize > 0 ? options.BatchSize : 4096;
        foreach (string path in WordFrameNetIngest.ResolvePaths(context.EcosystemPath))
        {
            await foreach (var change in WordFrameNetIngest.StreamAsync(path, batchSize, ct))
            {
                if (!options.DryRun)
                    yield return change;
            }
        }
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
