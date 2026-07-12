using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Code;

public sealed class TabularDecomposer : ComposeDecomposer<TabularDecomposer.RowRecord, TabularSource, FullScope>
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
    private readonly HashSet<string> _canonicalNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stagedColumns = new(StringComparer.Ordinal);

    public TabularDecomposer(string targetColumn = "Exited", string positiveValue = "1", int numBins = 10)
    {
        _targetColumn = targetColumn;
        _positiveValue = positiveValue;
        _ = numBins;
    }

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "tabular";
    protected override int DefaultBatchSize => BatchConfigDefaults.HighVolume;

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    private Hash128 OutcomeId => Hash128.OfCanonical($"tabular/outcome/{_targetColumn}={_positiveValue}/v1");
    private static Hash128 ColumnId(string col) => Hash128.OfCanonical($"tabular/column/{col}/v1");
    private static Hash128 ValueId(string col, string tok) => Hash128.OfCanonical($"tabular/value/{col}={tok}/v1");

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

    protected override async IAsyncEnumerable<RowRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var files = EnumerateCsv(ecosystemPath).ToList();
        if (files.Count == 0) yield break;

        string[]? header = await ReadHeaderAsync(files, ct);
        if (header is null || header.Length == 0) yield break;

        var featureCols = header
            .Where(c => !c.Equals(_targetColumn, StringComparison.Ordinal) && !IdLike.Contains(c))
            .ToList();
        if (featureCols.Count == 0) yield break;

        int targetIdx = Array.IndexOf(header, _targetColumn);
        if (targetIdx < 0) yield break;

        await foreach (var row in StreamRowsAsync(files, header, featureCols, targetIdx, ct))
            yield return row;
    }

    protected override void Compose(RowRecord rec, SubstrateChangeBuilder b)
    {
        double witnessWeight = RelationTypeRank.Associative * TC.StructuredCorpus;
        var predicts = RelationTypeRegistry.RelationTypeId("PREDICTS");
        long score = rec.Positive ? checked(2 * Glicko2.FpScale) : 0;

        b.AddEntity(new EntityRow(OutcomeId, EntityTier.Word, OutcomeTypeId, Source));

        foreach (var (col, raw) in rec.Cells)
        {
            string tok = raw.Trim();
            if (tok.Length == 0) continue;

            EnsureColumn(b, col);

            var valueId = ValueId(col, tok);
            b.AddEntity(new EntityRow(valueId, EntityTier.Word, ValueTypeId, Source));
            _canonicalNames.Add($"tabular/value/{col}={tok}/v1");
            b.AddAttestation(NativeAttestation.Aggregated(
                valueId, predicts, OutcomeId, Source, contextId: ColumnId(col),
                games: 1, sumScoreFp1e9: score, witnessWeight: witnessWeight));
            b.AddAttestation(NativeAttestation.Categorical(
                valueId, "IS_VALUE_IN", ColumnId(col), Source, TC.StructuredCorpus));
            if (ContentEmitter.Emit(b, tok, Source) is { } bareId)
                b.AddAttestation(NativeAttestation.Categorical(
                    valueId, "IS_INSTANCE_OF", bareId, Source, TC.StructuredCorpus));
        }
    }

    private void EnsureColumn(SubstrateChangeBuilder b, string col)
    {
        if (!_stagedColumns.Add(col)) return;
        b.AddEntity(new EntityRow(ColumnId(col), EntityTier.Word, ColumnTypeId, Source));
        _canonicalNames.Add($"tabular/column/{col}/v1");
        if (ContentEmitter.Emit(b, col, Source) is { } colNameId)
            b.AddAttestation(NativeAttestation.Categorical(
                ColumnId(col), "IS_INSTANCE_OF", colNameId, Source, TC.StructuredCorpus));
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

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
        if (File.Exists(root))
        {
            if (root.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) yield return root;
            yield break;
        }
        if (!Directory.Exists(root)) yield break;
        foreach (var f in Directory.EnumerateFiles(root, "*.csv", SearchOption.AllDirectories)
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }

    public readonly record struct RowRecord(IReadOnlyDictionary<string, string> Cells, bool Positive);
}
