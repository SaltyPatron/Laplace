using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

public sealed class TabularDecomposer
    : ComposeDecomposerMultiFile<TabularDecomposer.RowRecord, TabularSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = TabularSource.SourceId;
    public static readonly Hash128 TrustClass = TabularSource.TrustClass;

    private static readonly Hash128 ColumnTypeId = EntityTypeRegistry.TabularColumn;
    private static readonly Hash128 ValueTypeId = EntityTypeRegistry.TabularValue;
    private static readonly Hash128 OutcomeTypeId = EntityTypeRegistry.TabularOutcome;

    private static readonly HashSet<string> IdLike =
        new(StringComparer.OrdinalIgnoreCase) { "id", "customerid", "rownumber" };

    private readonly string _targetColumn;
    private readonly string _positiveValue;
    private readonly ConcurrentStringSet _canonicalNames = new(StringComparer.Ordinal);

    public TabularDecomposer(string targetColumn = "Exited", string positiveValue = "1", int numBins = 10)
    {
        _targetColumn = targetColumn;
        _positiveValue = positiveValue;
        _ = numBins;
        OutcomeId = Hash128.OfCanonical($"tabular/outcome/{_targetColumn}={_positiveValue}/v1");
    }

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "tabular";

    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    private Hash128 OutcomeId { get; }
    private static Hash128 ColumnId(string col) => Hash128.OfCanonical($"tabular/column/{col}/v1");
    private static readonly Hash128 PredictsTypeId = RelationTypeRegistry.RelationTypeId("PREDICTS");

    protected override async Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
        var seed = new SubstrateChangeBuilder(Source, "bootstrap/tabular-vocab", null,
            entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: 2);
        seed.AddEntity(new EntityRow(OutcomeId, EntityTier.Word, OutcomeTypeId, Source));
        _canonicalNames.Add($"tabular/outcome/{_targetColumn}={_positiveValue}/v1");
        if (ContentEmitter.Emit(seed, _targetColumn, Source) is { } targetNameId)
            seed.AddAttestation(NativeAttestation.Categorical(
                OutcomeId, "IS_INSTANCE_OF", targetNameId, Source, TC.StructuredCorpus));
        if (ContentEmitter.Emit(seed, _positiveValue, Source) is { } posValId)
            seed.AddAttestation(NativeAttestation.Categorical(
                OutcomeId, "IS_INSTANCE_OF", posValId, Source, TC.StructuredCorpus));
        await context.Writer.ApplyAsync(seed.Build(), ct);
    }

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options) =>
        EnumerateCsv(ecosystemPath)
            .Select((file, i) => (file, $"tabular/{i}/{Path.GetFileName(file)}"))
            .ToList();

    protected override async IAsyncEnumerable<RowRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string[]? header = await ReadHeaderAsync([filePath], ct);
        if (header is null || header.Length == 0) yield break;

        var featureCols = header
            .Where(c => !c.Equals(_targetColumn, StringComparison.Ordinal) && !IdLike.Contains(c))
            .ToList();
        if (featureCols.Count == 0) yield break;

        int targetIdx = Array.IndexOf(header, _targetColumn);
        if (targetIdx < 0) yield break;

        await foreach (var row in StreamRowsAsync([filePath], header, featureCols, targetIdx, ct))
            yield return row;
    }

    protected override void Compose(RowRecord rec, SubstrateChangeBuilder b)
    {
        double witnessWeight = RelationTypeRank.Associative * TC.StructuredCorpus;
        long score = rec.Positive ? checked(2 * Glicko2.FpScale) : 0;

        b.AddEntity(new EntityRow(OutcomeId, EntityTier.Word, OutcomeTypeId, Source));

        foreach (var (col, raw) in rec.Cells)
        {
            string tok = raw.Trim();
            if (tok.Length == 0) continue;

            var columnId = ColumnId(col);
            EnsureColumn(b, col, columnId);

            string valueCanonical = $"tabular/value/{col}={tok}/v1";
            var valueId = Hash128.OfCanonical(valueCanonical);
            b.AddEntity(new EntityRow(valueId, EntityTier.Word, ValueTypeId, Source));
            _canonicalNames.Add(valueCanonical);
            b.AddAttestation(NativeAttestation.Aggregated(
                valueId, PredictsTypeId, OutcomeId, Source, contextId: columnId,
                games: 1, sumScoreFp1e9: score, witnessWeight: witnessWeight));
            b.AddAttestation(NativeAttestation.Categorical(
                valueId, "IS_VALUE_IN", columnId, Source, TC.StructuredCorpus));
            if (ContentEmitter.Emit(b, tok, Source) is { } bareId)
                b.AddAttestation(NativeAttestation.Categorical(
                    valueId, "IS_INSTANCE_OF", bareId, Source, TC.StructuredCorpus));
        }
    }

    private void EnsureColumn(SubstrateChangeBuilder b, string col, Hash128 columnId)
    {
        b.AddEntity(new EntityRow(columnId, EntityTier.Word, ColumnTypeId, Source));
        _canonicalNames.Add($"tabular/column/{col}/v1");
        if (ContentEmitter.Emit(b, col, Source) is { } colNameId)
            b.AddAttestation(NativeAttestation.Categorical(
                columnId, "IS_INSTANCE_OF", colNameId, Source, TC.StructuredCorpus));
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateCsv(context.EcosystemPath).ToList();
        return Task.FromResult(IngestInventory.FromFiles(
            "rows", paths, options.MaxInputUnits, ct, tracksFileCompletion: true));
    }

    public override async Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default) =>
        (await DescribeInputAsync(context, DecomposerOptions.Default, ct))?.TotalInputUnits;

    private static async Task<string[]?> ReadHeaderAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            string[]? header = null;
            await foreach (var (fields, _) in GrammarRowReader.ReadFieldsAsync(
                f, EtlManifest.Get("tabular").Modality, ct))
            {
                header = fields;
                break;
            }
            if (header is { Length: > 0 }) return header;
        }
        return null;
    }

    private async IAsyncEnumerable<RowRecord> StreamRowsAsync(
        IReadOnlyList<string> files,
        string[] header,
        IReadOnlyList<string> featureCols,
        int targetIdx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            bool skippedHeader = false;
            await foreach (var (fields, _) in GrammarRowReader.ReadFieldsAsync(
                f, EtlManifest.Get("tabular").Modality, ct))
            {
                if (!skippedHeader) { skippedHeader = true; continue; }
                if (fields.Length != header.Length) continue;

                var rec = new Dictionary<string, string>(featureCols.Count, StringComparer.Ordinal);
                for (int i = 0; i < header.Length; i++)
                {
                    string col = header[i];
                    if (col.Equals(_targetColumn, StringComparison.Ordinal) || IdLike.Contains(col)) continue;
                    rec[col] = fields[i];
                }

                bool positive = fields[targetIdx].Trim() == _positiveValue;
                yield return new RowRecord(rec, positive);
            }
        }
    }

    private static IEnumerable<string> EnumerateCsv(string root)
    {
        // The shared valet already reads "<path> is one file OR a corpus root"; a single
        // file still has to BE a csv, which the recursive arm gets from the glob.
        if (IngestInput.IsSingleFile(root))
            return root.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? [root] : [];
        return IngestInput.ResolveFiles(root, "*.csv").OrderBy(p => p, StringComparer.Ordinal);
    }

    public readonly record struct RowRecord(IReadOnlyDictionary<string, string> Cells, bool Positive);
}
