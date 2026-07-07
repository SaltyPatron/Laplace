using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OpenSubtitles;

public sealed class OpenSubtitlesDecomposer : RelationTripleDecomposerBase, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/OpenSubtitlesDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

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

    public override Hash128 SourceId => Source;
    public override string SourceName => "OpenSubtitlesDecomposer";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => TrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            relationNodeNames: ["IS_TRANSLATION_OF", "HAS_LANGUAGE"],
            readbackNames: LanguageNames, ct: ct);

    protected override async IAsyncEnumerable<RelationTripleRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(ecosystemPath)) yield break;
        foreach (var (zipPath, _) in SelectZips(ecosystemPath, options))
        {
            string pairStem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(zipPath));
            await foreach (var pair in OpenSubtitlesZipIngest.ReadZipPairsAsync(zipPath, pairStem, ct))
            {
                yield return new RelationTripleRecord(
                    pair.LineA, "IS_TRANSLATION_OF", pair.LineB,
                    SubjectLangId: pair.LangA, ObjectLangId: pair.LangB);
            }
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        if (!Directory.Exists(context.EcosystemPath)) return Task.FromResult<IngestInventory?>(null);
        var zips = SelectZips(context.EcosystemPath, options);
        if (zips.Count == 0) return Task.FromResult<IngestInventory?>(null);
        if (options.MaxInputUnits > 0)
        {
            var paths = zips.Select(z => z.Path).ToList();
            return Task.FromResult(IngestInventory.FromFiles("pairs", paths, options.MaxInputUnits, ct));
        }
        var files = zips
            .Select(z =>
            {
                long pairs = PairCounts.FirstOrDefault(x => x.Pair == z.Stem).Pairs;
                return new IngestFileSpec(z.Stem, z.Path, pairs);
            })
            .ToList();
        return Task.FromResult<IngestInventory?>(new IngestInventory("pairs", files.Sum(f => f.InputUnits), files));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.ForWitness(SourceName), ct);
        return inv?.TotalInputUnits;
    }

    internal static HashSet<string>? ResolvePairAllowlist() => null;

    private static List<(string Path, string Stem)> SelectZips(string dir, DecomposerOptions options)
    {
        var pairAllow = ResolvePairAllowlist();
        var list = new List<(string, string)>();
        foreach (string zipPath in Directory.EnumerateFiles(dir, "*.txt.zip").OrderBy(p => p, StringComparer.Ordinal))
        {
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(zipPath));
            if (pairAllow is not null && !pairAllow.Contains(stem)) continue;
            if (options.Languages?.MatchesLanguagePair(stem) == false) continue;
            list.Add((zipPath, stem));
        }
        return list;
    }
}
