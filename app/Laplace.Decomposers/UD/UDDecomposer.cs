using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.UD;

public sealed class UDDecomposer : DecomposerMultiFile<UdIngestRecord, UDSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = UDSource.SourceId;
    public static readonly Hash128 TrustClass = UDSource.TrustClass;

    public override int LayerOrder => 2;

    protected override double SourceTrust => TC.AcademicCurated;

    private readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);
    private ConcurrentIdSet _seenSourceDeclarations = new();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => new List<string>(_canonicalNames.Keys);

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => _canonicalNames;

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        var files = ListTreebankFiles(ecosystemPath, options);
        return files.Select(p => (Path: p, Label: UdIngestSupport.FileLabel(p))).ToList();
    }

    protected override async IAsyncEnumerable<UdIngestRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string langCode = UdIngestSupport.ExtractLangCode(Path.GetFileName(filePath));
        Hash128 langId = LanguageReference.Resolve(langCode);
        await foreach (var sentence in UdConlluParser.ParseSentencesAsync(filePath, ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return new UdIngestRecord(sentence, langId, langCode);
        }
    }

    protected override IIngestRecordHandler<UdIngestRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new UdIngestHandler(Source, _canonicalNames, fileLabel, _seenSourceDeclarations);

    protected override Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct)
    {
        _seenSourceDeclarations = new ConcurrentIdSet();
        return Task.CompletedTask;
    }

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options) =>
        UdIngestSupport.PipelineConfig(
            Source, fileLabel, options, reader);

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = ListTreebankFiles(context.EcosystemPath, options);
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        return Task.FromResult(IngestInventory.FromConlluFiles(
            "sentences", paths, options.MaxInputUnits, ct, tracksFileCompletion: true));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }

    private static LanguageFilter? EffectiveLanguages(DecomposerOptions options) =>
        options.Languages is { IsActive: true } ? options.Languages
        : LanguageFilter.ForSource("UDDecomposer");

    private static List<string> ListTreebankFiles(string root, DecomposerOptions options)
    {
        var all = IngestInput.ResolveFiles(root, "*.conllu", "ud-treebanks-v2.17");
        if (IngestInput.IsSingleFile(root)) return all;
        var langs = EffectiveLanguages(options);
        if (langs is { IsActive: true })
            return all.Where(p => langs.MatchesUdTreebankFile(Path.GetFileName(p))).ToList();
        if (all.Count > 0)
            Console.Error.WriteLine($"UD: no language filter — ingesting all {all.Count} treebank files (multilingual).");
        return all;
    }
}
