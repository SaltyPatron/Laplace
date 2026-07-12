using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// ETL sources route through <see cref="DecomposerMultiPhase"/> +
/// <see cref="StructuredGrammarIngest"/> (runtime <see cref="EtlRuntimeManifest"/>).
/// </summary>
public sealed class EtlDecomposer : DecomposerMultiPhase, IIngestInventoryProvider
{
    private readonly EtlSource _src;
    private ISubstrateReader? _containmentReader;

    internal static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> LanguageNamesBySource =
        new(StringComparer.Ordinal);

    public EtlDecomposer(EtlSource src) => _src = src;

    public override Hash128 SourceId => _src.SourceId;
    public override string SourceName => _src.Name;
    public override int LayerOrder => _src.Layer;
    public override Hash128 TrustClassId => _src.TrustClassId;

    public IReadOnlyCollection<string> CanonicalNamesForReadback
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

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _containmentReader = context.Reader;

        if (_src.Anchor == AnchorResolver.IliSynset)
        {
            if (_src.RequireIliMap)
                SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, _src.Name);
            else
                SourceEntityIdConventions.WarnIfCiliMapMissing(context.Logger, _src.Name);
        }

        var files = EnumerateFiles(context.EcosystemPath).ToList();
        if (files.Count == 0) yield break;

        int batch = options.BatchSize > 1
            ? options.BatchSize
            : IngestSizing.ResolveForSource(IngestSourceProfile.Wiktionary).RecordBatchSize;
        long cap = options.MaxInputUnits;
        long consumed = 0;
        int fileBn = 0;

        Func<ReadOnlySpan<byte>, bool>? acceptRow = _src.AcceptCommentRows
            ? null
            : static line => line.Length > 0 && line[0] != (byte)'#';

        ISubstrateReader? composeReader = _containmentReader;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (cap > 0 && consumed >= cap) yield break;
            long fileCap = cap > 0 ? cap - consumed : 0;

            Hash128? fileContext = _src.ContextIdFromFile?.Invoke(file);

            var witness = new EtlWitness(new EtlWitnessContext(_src, file, options));
            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                file,
                _src,
                witness: witness,
                batchSize: batch,
                witnessWeight: 1.0,
                batchLabelPrefix: $"{_src.Name}/{fileBn++}",
                reportUnits: null,
                contextId: fileContext,
                commitEpoch: 0,
                acceptRow: acceptRow,
                maxInputUnits: fileCap,
                containmentReader: composeReader,
                ct: ct))
            {
                if (!options.DryRun)
                {
                    consumed += change.Metadata.InputUnitsConsumed;
                    yield return change;
                }
            }
        }
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
            "records", paths, options.MaxInputUnits, ct));
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }
}
