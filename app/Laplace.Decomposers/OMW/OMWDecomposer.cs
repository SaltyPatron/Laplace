using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OMW;

public readonly record struct OmwIngestRecord(
    OmwRow Row,
    byte[] ValueUtf8,
    OmwLmfRecord? Lmf = null);

public sealed class OMWDecomposer : DecomposerMultiFile<OmwIngestRecord, OMWSource, FullScope>,
    IIngestInventoryProvider, IIngestArtifactGraphProvider
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

    private const string ChangesLabelPrefix = "omw-changes/";
    private const string FreqLabelPrefix = "omw-freq/";
    internal const string LmfLabelPrefix = "omw-lmf/";

    private static int IndexOfNthTab(ReadOnlySpan<byte> line, int n)
    {
        int seen = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != (byte)'\t') continue;
            if (++seen == n) return i;
        }
        return -1;
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

        // The corpus's retractions, ingested through the same per-file spine so they
        // resume and journal like any other file. Labelled distinctly because their rows
        // carry two extra leading fields and must not be read as data rows.
        var changesFiles = OMWTabFiles.EnumerateChangesFiles(wnsDir)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        for (int i = 0; i < changesFiles.Count; i++)
            labeled.Add((changesFiles[i], $"{ChangesLabelPrefix}{i}"));

        // The corpus's only per-row magnitude. Its rows are synset \t lemma \t count,
        // not the data tabs' synset \t lang:type \t value, so it needs its own label.
        var freqFiles = OMWTabFiles.EnumerateFreqFiles(wnsDir)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        for (int i = 0; i < freqFiles.Count; i++)
            labeled.Add((freqFiles[i], $"{FreqLabelPrefix}{OMWTabFiles.FileLang(freqFiles[i])}"));

        return labeled;
    }

    protected override async IAsyncEnumerable<OmwIngestRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (fileLabel.StartsWith(LmfLabelPrefix, StringComparison.Ordinal))
        {
            await foreach (var record in OMWLmfParser.ReadAsync(filePath, fileLabel, ct))
                yield return new OmwIngestRecord(default, [], record);
            yield break;
        }

        bool isChanges = fileLabel.StartsWith(ChangesLabelPrefix, StringComparison.Ordinal);
        bool isFreq = fileLabel.StartsWith(FreqLabelPrefix, StringComparison.Ordinal);
        string fileLang = isChanges ? "und"
            : isFreq ? fileLabel[FreqLabelPrefix.Length..]
            : OmwIngestSupport.LangFromLabel(fileLabel);
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
            if (isChanges)
            {
                // date \t action \t <data row>. Only REMOVED is acted on; see OMWEmitter.
                if (!TsvSpan.TryField(line.Span, 1, out var action)) continue;
                if (!action.SequenceEqual("REMOVED"u8)) continue;
                int cut = IndexOfNthTab(line.Span, 2);
                if (cut < 0) continue;
                var dataRow = line.Span[(cut + 1)..];
                if (!OMWRowParser.TryParseRow(dataRow, fileLang, out var cr, out var cv))
                    continue;
                yield return new OmwIngestRecord(cr with { Removed = true }, cv.ToArray());
                continue;
            }
            if (isFreq)
            {
                if (!OMWRowParser.TryParseFreqRow(line.Span, fileLang, out var fr, out var fv))
                    continue;
                yield return new OmwIngestRecord(fr, fv.ToArray());
                continue;
            }
            if (!OMWRowParser.TryParseRow(line.Span, fileLang, out var row, out var valueUtf8))
                continue;
            yield return new OmwIngestRecord(row, valueUtf8.ToArray());
        }
    }

    protected override IIngestRecordHandler<OmwIngestRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new DirectComposeHandler<OmwIngestRecord>(
            static (record, builder) =>
            {
                if (record.Lmf is { } lmf)
                    OMWLmfEmitter.Emit(builder, lmf);
                else
                    OMWEmitter.Emit(builder, record.Row, record.ValueUtf8);
            });

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
        if (context.HasArtifactGraph)
        {
            var selected = context.SelectedArtifacts;
            if (selected.Count == 0) return Task.FromResult<IngestInventory?>(null);
            long total = options.MaxInputUnits > 0 ? options.MaxInputUnits : 0;
            var specs = selected.Select(static artifact =>
                    new IngestFileSpec(artifact.FileLabel, artifact.Path, 0))
                .ToArray();
            return Task.FromResult<IngestInventory?>(
                new IngestInventory("WN-LMF records", total, specs, TracksFileCompletion: true));
        }

        var paths = ListFiles(context.EcosystemPath, options).Select(f => f.Path).ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        return Task.FromResult(IngestInventory.FromFiles(
            "records", paths, options.MaxInputUnits, ct, tracksFileCompletion: true));
    }

    public Task<IngestArtifactGraph?> DescribeArtifactsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(OMWLmfArtifacts.Build(ecosystemPath, options));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }
}
