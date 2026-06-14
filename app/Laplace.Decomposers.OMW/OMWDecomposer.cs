using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.OMW;

public sealed class OMWDecomposer : IDecomposer, IIngestInventoryProvider, IIngestCommitPolicy
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/OMWDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    /// <summary>
    /// OMW re-attests the SAME synset/lemma rows across languages: parallel batch
    /// commits collide on those row locks (40P01 deadlocks, 2026-06-12 — the documented
    /// wordnet/omw serial law). Pipelined serial commit keeps decompose parallelism.
    /// </summary>
    public IngestCommitParallelism CommitParallelism => IngestCommitParallelism.StrictSerial;

    public Hash128 SourceId     => Source;
    public string  SourceName   => "OMWDecomposer";
    public int     LayerOrder   => 3;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_EXAMPLE");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string wnsDir = Path.Combine(context.EcosystemPath, "wns");
        if (!Directory.Exists(wnsDir)) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 8192;

        await foreach (var change in OMWFastIngest.IngestAsync(wnsDir, options.Languages, batch, ct))
        {
            if (!options.DryRun) yield return change;
        }
    }

    public async Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string wnsDir = Path.Combine(context.EcosystemPath, "wns");
        if (!Directory.Exists(wnsDir)) return null;
        var files = new List<IngestFileSpec>();
        foreach (string tab in Directory.EnumerateFiles(wnsDir, "wn-data-*.tab", SearchOption.AllDirectories))
        {
            string lang = FileLang(tab);
            if (options.Languages?.MatchesRaw(lang) == false) continue;
            long n = await EtlInventory.CountDataLinesAsync(tab, ct: ct);
            files.Add(new(lang, tab, n));
        }
        long total = 0;
        foreach (var f in files) total += f.InputUnits;
        return new IngestInventory("records", total, files);
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // The ingest/emit path lives entirely in OMWFastIngest (the span-based reader the live
    // DecomposeAsync delegates to). A second managed copy here previously folded satellite 's' to
    // 'a' — the same bug, a second time — and was never called; it is gone. Only FileLang remains,
    // which DescribeInputAsync uses.
    private static string FileLang(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int dash = name.LastIndexOf('-');
        return dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : "und";
    }
}
