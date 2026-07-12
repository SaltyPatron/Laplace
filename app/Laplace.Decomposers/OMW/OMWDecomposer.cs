using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OMW;

public sealed class OMWDecomposer : DecomposerMultiFile<GrammarIngestRecord, OMWSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = OMWSource.SourceId;
    public static readonly Hash128 TrustClass = OMWSource.TrustClass;

    public override int LayerOrder => 3;
    protected override double SourceTrust => TC.AcademicCurated;

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => LanguageNames;

    internal static void TrackLanguage(string? langInput) =>
        VocabularyNames.TrackLanguage(LanguageNames, langInput);

    protected override Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct)
    {
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);
        return Task.CompletedTask;
    }

    protected override IMultiFileRecordStream<GrammarIngestRecord> CreateMultiFileStream(
        string ecosystemPath, DecomposerOptions options)
    {
        string wnsDir = Path.Combine(ecosystemPath, "wns");
        if (!Directory.Exists(wnsDir))
            return new OmwMultiFileStream([]);

        var tabFiles = OMWTabFiles.EnumerateTabFiles(wnsDir, options.Languages)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var labeled = new List<(string Path, string Label, string Lang)>(tabFiles.Count);
        for (int i = 0; i < tabFiles.Count; i++)
        {
            string path = tabFiles[i];
            string lang = OMWTabFiles.FileLang(path);
            labeled.Add((path, $"omw/{i}/{lang}", lang));
        }
        return new OmwMultiFileStream(labeled);
    }

    protected override IIngestRecordHandler<GrammarIngestRecord> CreateHandlerForFile(string fileLabel) =>
        new GrammarIngestHandler(
            Source, "tsv",
            new OMWGrammarWitness(OmwIngestSupport.LangFromLabel(fileLabel)),
            contextId: null);

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options)
    {
        int batch = options.BatchSize > 1 ? options.BatchSize : 2048;
        int slash = fileLabel.LastIndexOf('/');
        string prefix = slash > 0 ? fileLabel[..slash] : fileLabel;
        return IngestPipelineDefaults.ApplyMaxInputUnits(
            IngestPipelineDefaults.StructuredGrammar(
                Source, prefix, batch, options, reader, witnessWeight: 1.0),
            options);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string wnsDir = Path.Combine(context.EcosystemPath, "wns");
        if (!Directory.Exists(wnsDir)) return Task.FromResult<IngestInventory?>(null);
        var paths = OMWTabFiles.EnumerateTabFiles(wnsDir, options.Languages)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(IngestInventory.FromFiles("records", paths, options.MaxInputUnits, ct));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }
}
