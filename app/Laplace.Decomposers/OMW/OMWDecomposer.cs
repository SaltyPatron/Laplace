using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OMW;

public readonly record struct OmwIngestRecord(OmwRow Row, byte[] ValueUtf8);

public sealed class OMWDecomposer : DecomposerMultiFile<OmwIngestRecord, OMWSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = OMWSource.SourceId;
    public static readonly Hash128 TrustClass = OMWSource.TrustClass;

    public override int LayerOrder => 3;

    protected override double SourceTrust => TC.AcademicCurated;

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => LanguageNames;

    internal static void TrackLanguage(string? langInput) =>
        VocabularyNames.TrackLanguage(LanguageNames, langInput);

    protected override Task OnBeforeRegisterAsync(IDecomposerContext context, CancellationToken ct)
    {
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);
        return Task.CompletedTask;
    }

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        string wnsDir = Path.Combine(ecosystemPath, "wns");
        if (!Directory.Exists(wnsDir)) return [];

        var tabFiles = OMWTabFiles.EnumerateTabFiles(wnsDir, options.Languages)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var labeled = new List<(string Path, string Label)>(tabFiles.Count);
        for (int i = 0; i < tabFiles.Count; i++)
        {
            string path = tabFiles[i];
            string lang = OMWTabFiles.FileLang(path);
            labeled.Add((path, $"omw/{i}/{lang}"));
        }
        return labeled;
    }

    protected override async IAsyncEnumerable<OmwIngestRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string fileLang = OmwIngestSupport.LangFromLabel(fileLabel);
        await using var e = StreamingUtf8LineReader.ReadLinesAsync(filePath, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            ReadOnlyMemory<byte> line;
            try
            {
                if (!await e.MoveNextAsync()) break;
                line = e.Current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"OMW ingest failed in \"{filePath}\": {ex.Message}", ex);
            }
            if (!OMWRowParser.TryParseRow(line.Span, fileLang, out var row, out var valueUtf8))
                continue;
            yield return new OmwIngestRecord(row, valueUtf8.ToArray());
        }
    }

    protected override IIngestRecordHandler<OmwIngestRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new DirectComposeHandler<OmwIngestRecord>(
            static (record, builder) => OMWEmitter.Emit(builder, record.Row, record.ValueUtf8));

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options)
    {
        int slash = fileLabel.LastIndexOf('/');
        string prefix = slash > 0 ? fileLabel[..slash] : fileLabel;
        return IngestPipelineDefaults.Compose(
            Source, prefix, options, reader, IngestSourceProfile.Omw);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = ListFiles(context.EcosystemPath, options).Select(f => f.Path).ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        return Task.FromResult(IngestInventory.FromFiles(
            "records", paths, options.MaxInputUnits, ct, tracksFileCompletion: true));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }
}
