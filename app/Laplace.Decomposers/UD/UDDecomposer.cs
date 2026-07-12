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
                "HAS_POS", "HAS_XPOS", "HAS_LANGUAGE", "IS_A"],
            readbackNames: _canonicalNames, ct: ct);

    protected override IMultiFileRecordStream<UdIngestRecord> CreateMultiFileStream(
        string ecosystemPath, DecomposerOptions options)
    {
        var files = ListTreebankFiles(ecosystemPath, options);
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
        var paths = ListTreebankFiles(context.EcosystemPath, options);
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

    // Root may be a single .conllu file, a directory of treebanks, or the ecosystem
    // root containing ud-treebanks-v2.17 — resolved by the shared IngestInput valet.
    private static List<string> ListTreebankFiles(string root, DecomposerOptions options)
    {
        var all = IngestInput.ResolveFiles(root, "*.conllu", "ud-treebanks-v2.17");
        // Explicit single file: the operator named it — honour it, skip the language filter.
        if (IngestInput.IsSingleFile(root)) return all;
        var langs = EffectiveLanguages(options);
        if (langs is { IsActive: true })
            return all.Where(p => langs.MatchesUdTreebankFile(Path.GetFileName(p))).ToList();
        if (all.Count > 0)
            Console.Error.WriteLine($"UD: no language filter — ingesting all {all.Count} treebank files (multilingual).");
        return all;
    }
}
