using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The one generic source driver. Given an <see cref="EtlSource"/> manifest row it bootstraps the
/// source's relation/type vocabulary, enumerates the source's files under the ecosystem path by
/// glob, and streams each through <see cref="StructuredGrammarIngest.IngestFileAsync"/> with the
/// source modality and an <see cref="EtlWitness"/>. Reading, batching, tier-containment dedup, and
/// provenance all live in <see cref="StructuredGrammarIngest"/> + the partitioned sink — this
/// driver owns none of it. Adding a source is a manifest row, not a new class.
/// </summary>
public sealed class EtlDecomposer : IDecomposer, IIngestInventoryProvider
{
    private readonly EtlSource _src;
    private ISubstrateReader? _containmentReader;

    // Languages discovered during the walk (the bespoke witnesses track these for canonical readback).
    internal static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> LanguageNamesBySource =
        new(StringComparer.Ordinal);

    public EtlDecomposer(EtlSource src) => _src = src;

    public Hash128 SourceId     => _src.SourceId;
    public string  SourceName   => _src.Name;
    public int     LayerOrder   => _src.Layer;
    public Hash128 TrustClassId => _src.TrustClassId;

    public IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (LanguageNamesBySource.TryGetValue(_src.Name, out var d))
                foreach (var n in d.Keys) names.Add(n);
            // A factory-routed witness tracks discovered names in its own source's static dict.
            foreach (var n in EtlWitnessFactory.Readback(_src.Name)) names.Add(n);
            return names;
        }
    }

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(_src.SourceId, _src.Name, _src.TrustClassId);
        foreach (var rel in BootstrapRelationNames())
            boot.AddRelationType(rel);
        await context.Writer.ApplyAsync(boot.Build(), ct);

        var langs = LanguageNamesBySource.GetOrAdd(_src.Name, _ => new(StringComparer.Ordinal));
        foreach (var n in boot.CanonicalNames)
            langs.TryAdd(n, 0);
    }

    private IEnumerable<string> BootstrapRelationNames()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (_src.BootstrapRelations is not null)
            foreach (var r in _src.BootstrapRelations)
                if (seen.Add(r)) yield return r;
        foreach (var rule in _src.NodeEdgeMap)
            if (seen.Add(rule.RelationType)) yield return rule.RelationType;
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _containmentReader = context.Reader;

        // ILI-anchored sources gate on the CILI map exactly as the bespoke decomposers do: a hard
        // error where resolved synset anchors are mandatory (OMW), a warning where best-effort.
        if (_src.Anchor == AnchorResolver.IliSynset)
        {
            if (_src.RequireIliMap)
                SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, _src.Name);
            else
                SourceEntityIdConventions.WarnIfCiliMapMissing(context.Logger, _src.Name);
        }

        var files = EnumerateFiles(context.EcosystemPath).ToList();
        if (files.Count == 0) yield break;

        int batch = options.BatchSize > 1 ? options.BatchSize : 2048;
        long cap = options.MaxInputUnits;
        long consumed = 0;
        int fileBn = 0;

        Func<ReadOnlySpan<byte>, bool>? acceptRow = _src.AcceptCommentRows
            ? null
            : static line => line.Length > 0 && line[0] != (byte)'#';

        // Lean triple sources skip compose-time exist probes — they block the hot path on a warm DB.
        ISubstrateReader? composeReader = NativeGrammarIngest.ShouldExistProbe(_src)
            ? _containmentReader : null;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (cap > 0 && consumed >= cap) yield break;
            long fileCap = cap > 0 ? cap - consumed : 0;

            Hash128? fileContext = _src.ContextIdFromFile?.Invoke(file);

            if (NativeGrammarIngest.CanUseNative(_src, options))
            {
                await foreach (var change in NativeGrammarIngest.IngestFileAsync(
                    file,
                    _src,
                    batchSize: batch,
                    batchLabelPrefix: $"{_src.Name}/{fileBn++}",
                    reportUnits: null,
                    contextId: fileContext,
                    commitEpoch: 0,
                    maxInputUnits: fileCap,
                    containmentReader: composeReader,
                    options: options,
                    ct: ct))
                {
                    if (!options.DryRun)
                    {
                        consumed += change.Metadata.InputUnitsConsumed;
                        yield return change;
                    }
                }
                continue;
            }

            var witness = new EtlWitness(new EtlWitnessContext(_src, file, options));
            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                file,
                modalityId: _src.Modality.GrammarId,
                sourceId: _src.SourceId,
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

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
