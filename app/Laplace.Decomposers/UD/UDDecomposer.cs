using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.UD;

public sealed class UDDecomposer : DecomposerMultiFile<UdIngestRecord>, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/UDDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public override Hash128 SourceId => Source;
    public override string SourceName => "UDDecomposer";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => TrustClass;
    protected override double SourceTrust => TC.AcademicCurated;

    public override int EstimatedBytesPerRecord => IngestSourceProfile.UdSentence.EstBytesPerRecord;

    private readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => new List<string>(_canonicalNames.Keys);

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["UD_Feature"],
            relationNodeNames: ["HAS_DEFINITION", "TRANSCRIBES_AS", "ENHANCED_DEPENDS_ON",
                "HAS_XPOS", "HAS_LANGUAGE", "IS_A"],
            readbackNames: _canonicalNames, ct: ct);

    protected override IMultiFileRecordStream<UdIngestRecord> CreateMultiFileStream(
        string ecosystemPath, DecomposerOptions options)
    {
        string treebanksDir = Path.Combine(ecosystemPath, "ud-treebanks-v2.17");
        var files = ListTreebankFiles(treebanksDir, options);
        var labeled = files.Select(p =>
        {
            string stem = Path.GetFileNameWithoutExtension(p);
            return (Path: p, Label: $"ud/{stem}");
        }).ToList();
        return new UdConlluMultiFileStream(labeled);
    }

    protected override IIngestRecordHandler<UdIngestRecord> CreateHandlerForFile(string fileLabel) =>
        new UdIngestHandler(Source, _canonicalNames);

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options) =>
        UdIngestSupport.PipelineConfig(
            Source, fileLabel, UdIngestSupport.ResolveBatchSentences(options), reader);

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string treebanksDir = Path.Combine(context.EcosystemPath, "ud-treebanks-v2.17");
        if (!Directory.Exists(treebanksDir))
            return Task.FromResult<IngestInventory?>(null);
        var paths = ListTreebankFiles(treebanksDir, options);
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        if (options.MaxInputUnits > 0)
            return Task.FromResult(IngestInventory.FromFiles("sentences", paths, options.MaxInputUnits, ct));
        var files = paths.Select(p =>
        {
            string id = Path.GetFileNameWithoutExtension(p);
            return new IngestFileSpec(id, p, EtlInventory.CountConlluSentences(p));
        }).ToList();
        long total = files.Sum(f => f.InputUnits);
        return Task.FromResult<IngestInventory?>(new IngestInventory("sentences", total, files));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }

    private static LanguageFilter? EffectiveLanguages(DecomposerOptions options) =>
        options.Languages is { IsActive: true } ? options.Languages
        : LanguageFilter.ForSource("UDDecomposer");

    private static List<string> ListTreebankFiles(string treebanksDir, DecomposerOptions options)
    {
        var all = Directory.EnumerateFiles(treebanksDir, "*.conllu", SearchOption.AllDirectories).ToList();
        var langs = EffectiveLanguages(options);
        if (langs is { IsActive: true })
            return all.Where(p => langs.MatchesUdTreebankFile(Path.GetFileName(p))).ToList();
        Console.Error.WriteLine($"UD: no language filter — ingesting all {all.Count} treebank files (multilingual).");
        return all;
    }
}
