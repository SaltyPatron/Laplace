using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OpenSubtitles;

public sealed class OpenSubtitlesDecomposer : IDecomposer, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/OpenSubtitlesDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    private static readonly (string Pair, long Pairs)[] PairCounts =
    {
        ("ar-en",     87_893_588L),
        ("de-en",     65_673_701L),
        ("en-es",    105_482_431L),
        ("en-fr",     83_896_581L),
        ("en-it",     72_430_053L),
        ("en-ja",      2_068_294L),
        ("en-ko",     31_052_957L),
        ("en-pt",     68_557_861L),
        ("en-ru",     61_544_952L),
        ("en-zh_CN",  22_394_812L),
    };

    public Hash128 SourceId     => Source;
    public string  SourceName   => "OpenSubtitlesDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("IS_TRANSLATION_OF");
        boot.AddRelationType("HAS_LANGUAGE");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(context.EcosystemPath)) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 8192;

        var zips = Directory.EnumerateFiles(context.EcosystemPath, "*.txt.zip")
                            .OrderBy(p => p, StringComparer.Ordinal)
                            .ToList();

        foreach (string zipPath in zips)
        {
            ct.ThrowIfCancellationRequested();
            string pairStem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(zipPath));
            if (options.Languages?.MatchesLanguagePair(pairStem) == false) continue;

            await foreach (var change in OpenSubtitlesFastIngest.IngestZipAsync(zipPath, pairStem, batch, ct))
            {
                if (!options.DryRun) yield return change;
            }
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        if (!Directory.Exists(context.EcosystemPath)) return Task.FromResult<IngestInventory?>(null);
        var files = Directory.EnumerateFiles(context.EcosystemPath, "*.txt.zip")
            .Select(p => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(p)))
            .Where(pair => options.Languages?.MatchesLanguagePair(pair) != false)
            .Select(pair =>
            {
                long pairs = PairCounts.FirstOrDefault(x => x.Pair == pair).Pairs;
                return new IngestFileSpec(pair, Path.Combine(context.EcosystemPath, pair + ".txt.zip"), pairs);
            })
            .ToList();
        long total = 0;
        foreach (var f in files) total += f.InputUnits;
        return Task.FromResult<IngestInventory?>(new IngestInventory("pairs", total, files));
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static bool IsTextEntry(string entryName)
    {
        string leaf = entryName;
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];
        return leaf.StartsWith("OpenSubtitles.", StringComparison.Ordinal);
    }

    private static string LangSuffix(string entryName)
    {
        string leaf = entryName;
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];
        int dot = leaf.LastIndexOf('.');
        return dot >= 0 && dot + 1 < leaf.Length ? leaf[(dot + 1)..] : "und";
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(Source, unit, null,
            entityCapacity:      batch * 8,
            physicalityCapacity: batch * 8,
            attestationCapacity: batch * 4);
}
