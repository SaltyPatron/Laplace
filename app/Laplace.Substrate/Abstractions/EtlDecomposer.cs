using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Runtime-manifest ETL source on the same multi-file record pipeline as compiled vendors.
/// </summary>
public sealed class EtlDecomposer : DecomposerMultiFile<GrammarIngestRecord>, IIngestInventoryProvider
{
    private readonly EtlSource _src;
    private readonly ConcurrentDictionary<string, string> _filePathsByLabel =
        new(StringComparer.Ordinal);

    internal static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> LanguageNamesBySource =
        new(StringComparer.Ordinal);

    public EtlDecomposer(EtlSource src) => _src = src;

    public override Hash128 SourceId => _src.SourceId;
    public override string SourceName => _src.Name;
    public override int LayerOrder => _src.Layer;
    public override Hash128 TrustClassId => _src.TrustClassId;
    protected override double SourceTrust => _src.Trust;
    public override int EstimatedBytesPerRecord =>
        (_src.Profile ?? IngestSourceProfile.Wiktionary).EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord =>
        (_src.Profile ?? IngestSourceProfile.Wiktionary).EstComposeUnitsPerRecord;

    public override IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (LanguageNamesBySource.TryGetValue(_src.Name, out var d))
                foreach (var n in d.Keys) names.Add(n);
            foreach (var n in EtlWitnessFactory.Readback(_src.Name)) names.Add(n);
            return names;
        }
    }

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var langs = LanguageNamesBySource.GetOrAdd(_src.Name, _ => new(StringComparer.Ordinal));
        await SourceVocabularyBootstrap.RegisterManifestAsync(
            context, new EtlRuntimeManifest(_src), readbackNames: langs, ct: ct);
    }

    protected override Task OnBeforeDecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        CancellationToken ct)
    {
        if (_src.Anchor == AnchorResolver.IliSynset)
        {
            if (_src.RequireIliMap)
                SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, _src.Name);
            else
                SourceEntityIdConventions.WarnIfCiliMapMissing(context.Logger, _src.Name);
        }
        return Task.CompletedTask;
    }

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        var files = EnumerateFiles(ecosystemPath).ToList();
        _filePathsByLabel.Clear();
        var result = new List<(string Path, string Label)>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            string label = $"{_src.Name}/{i}/{Path.GetFileName(path)}";
            _filePathsByLabel[label] = path;
            result.Add((path, label));
        }
        return result;
    }

    protected override async IAsyncEnumerable<GrammarIngestRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Func<ReadOnlySpan<byte>, bool>? acceptRow = _src.AcceptCommentRows
            ? null
            : static line => line.Length > 0 && line[0] != (byte)'#';
        var stream = GrammarFileRecordStream.ForSource(filePath, _src, acceptRow);
        await foreach (var record in stream.RecordsAsync(ct))
            yield return record;
    }

    protected override IIngestRecordHandler<GrammarIngestRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options)
    {
        string path = _filePathsByLabel.TryGetValue(fileLabel, out var resolved)
            ? resolved
            : fileLabel;
        return new GrammarIngestHandler(
            SourceId,
            _src.Modality.GrammarId,
            new EtlWitness(new EtlWitnessContext(_src, path, options)),
            _src.ContextIdFromFile?.Invoke(path));
    }

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options)
    {
        var profile = _src.Profile ?? IngestSourceProfile.Wiktionary;
        return IngestPipelineDefaults.StructuredGrammar(
            SourceId,
            fileLabel,
            IngestSizing.ResolveForSource(profile).RecordBatchSize,
            options,
            reader,
            witnessWeight: 1.0,
            profile: profile);
    }

    private IEnumerable<string> EnumerateFiles(string ecosystemPath)
    {
        string glob = _src.Glob ?? _src.Modality.Glob ?? "*";
        if (File.Exists(ecosystemPath))
            return new[] { ecosystemPath };
        if (!Directory.Exists(ecosystemPath))
            return Array.Empty<string>();
        return Directory
            .EnumerateFiles(ecosystemPath, glob, SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateFiles(context.EcosystemPath).ToList();
        return Task.FromResult(IngestInventory.FromFiles(
            "records", paths, options.MaxInputUnits, ct, tracksFileCompletion: true));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }
}
