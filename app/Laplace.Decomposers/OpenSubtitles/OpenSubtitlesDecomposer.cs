using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OpenSubtitles;

/// <summary>
/// Multi-file aligned-corpus source. Each language-pair zip is decomposed by one
/// per-file worker into bounded ordered alignment blocks; files run concurrently.
/// </summary>
public sealed class OpenSubtitlesDecomposer
    : DecomposerMultiFile<AlignedSubtitleBlock, OpenSubtitlesSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = OpenSubtitlesSource.SourceId;
    public static readonly Hash128 TrustClass = OpenSubtitlesSource.TrustClass;

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

    public override int LayerOrder => 2;
    public override bool PerFileCompletion => true;
    protected override double SourceTrust => TC.StructuredCorpus;

    private static IngestSourceProfile BlockProfile => OpenSubtitlesSource.Profile;

    public override int EstimatedBytesPerRecord => BlockProfile.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => BlockProfile.EstComposeUnitsPerRecord;

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => LanguageNames;

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        if (!Directory.Exists(ecosystemPath)) return [];
        return SelectZips(ecosystemPath, options)
            .Select(z => (z.Path, $"opensubtitles/{z.Stem}"))
            .ToList();
    }

    protected override async IAsyncEnumerable<AlignedSubtitleBlock> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string pairStem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(filePath));
        // The generic cap is record-boundary based. Use one pair per record only for
        // bounded diagnostic runs so a cross-file cap cannot overshoot by a partial block;
        // full ingestion retains the 512-pair amortization.
        int blockPairs = options.MaxInputUnits > 0 ? 1 : OpenSubtitlesZipIngest.BlockPairs;
        await foreach (var record in OpenSubtitlesZipIngest.ReadZipBlocksAsync(
                           filePath, pairStem, blockPairs, ct))
            yield return record;
    }

    protected override IIngestRecordHandler<AlignedSubtitleBlock> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new OpenSubtitlesAlignedHandler(Source, TC.StructuredCorpus);

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options) =>
        new()
        {
            SourceId = Source,
            BatchLabelPrefix = fileLabel,
            BatchSize = 1,
            ProbeChunkSize = 1,
            WorkingSet = true,
            WorkingSetProbeInterval = 1,
            WorkingSetRecordCap = 1,
            WorkingSetProfile = BlockProfile,
            ContainmentReader = reader,
            MaxInputUnits = options.MaxInputUnits,
            EntityCapacity = OpenSubtitlesZipIngest.BlockPairs * 8,
            PhysicalityCapacity = OpenSubtitlesZipIngest.BlockPairs * 8,
            AttestationCapacity = 2,
        };

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        if (!Directory.Exists(context.EcosystemPath)) return Task.FromResult<IngestInventory?>(null);
        var zips = SelectZips(context.EcosystemPath, options);
        if (zips.Count == 0) return Task.FromResult<IngestInventory?>(null);
        if (options.MaxInputUnits > 0)
        {
            var paths = zips.Select(z => z.Path).ToList();
            return Task.FromResult(IngestInventory.FromFiles(
                "pairs", paths, options.MaxInputUnits, ct, tracksFileCompletion: true));
        }
        var files = zips
            .Select(z =>
            {
                long pairs = PairCounts.FirstOrDefault(x => x.Pair == z.Stem).Pairs;
                return new IngestFileSpec(z.Stem, z.Path, pairs);
            })
            .ToList();
        return Task.FromResult<IngestInventory?>(
            new IngestInventory("pairs", files.Sum(f => f.InputUnits), files, TracksFileCompletion: true));
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
