using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Code;

public sealed class TabularDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/TabularDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 ColumnTypeId = EntityTypeRegistry.TabularColumn;
    private static readonly Hash128 ValueTypeId = EntityTypeRegistry.TabularValue;
    private static readonly Hash128 OutcomeTypeId = EntityTypeRegistry.TabularOutcome;

    private static readonly HashSet<string> IdLike =
        new(StringComparer.OrdinalIgnoreCase) { "id", "customerid", "rownumber" };

    private readonly string _targetColumn;
    private readonly string _positiveValue;
    private readonly int _numBins;
    private readonly HashSet<string> _canonicalNames = new(StringComparer.Ordinal);

    public TabularDecomposer(string targetColumn = "Exited", string positiveValue = "1", int numBins = 10)
    {
        _targetColumn = targetColumn;
        _positiveValue = positiveValue;
        _numBins = numBins;
    }

    public Hash128 SourceId => Source;
    public string SourceName => "TabularDecomposer";
    public int LayerOrder => 2;
    public Hash128 TrustClassId => TrustClass;

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    private Hash128 OutcomeId => Hash128.OfCanonical($"tabular/outcome/{_targetColumn}={_positiveValue}/v1");
    private static Hash128 ColumnId(string col) => Hash128.OfCanonical($"tabular/column/{col}/v1");
    private static Hash128 ValueId(string col, string tok) => Hash128.OfCanonical($"tabular/value/{col}={tok}/v1");

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["TabularColumn", "TabularValue", "TabularOutcome"],
            relationNodeNames: ["PREDICTS", "IS_VALUE_IN", "IS_INSTANCE_OF"],
            ct: ct);
        var seed = new SubstrateChangeBuilder(Source, "bootstrap/tabular-vocab", null,
            entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: 0);
        seed.AddEntity(new EntityRow(OutcomeId, EntityTier.Word, OutcomeTypeId, Source));
        _canonicalNames.Add($"tabular/outcome/{_targetColumn}={_positiveValue}/v1");
        await context.Writer.ApplyAsync(seed.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = EnumerateCsv(context.EcosystemPath).ToList();
        if (files.Count == 0) yield break;

        var rows = new List<Dictionary<string, string>>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            string[]? header = null;
            await foreach (var (fields, _) in GrammarRowReader.ReadFieldsAsync(
                f, EtlManifest.Get("tabular").Modality, ct))
            {
                if (header is null) { header = fields; continue; }
                if (fields.Length != header.Length) continue;
                var rec = new Dictionary<string, string>(header.Length, StringComparer.Ordinal);
                for (int i = 0; i < header.Length; i++) rec[header[i]] = fields[i];
                rows.Add(rec);
            }
        }
        if (rows.Count == 0) yield break;

        var featureCols = rows[0].Keys
            .Where(c => !c.Equals(_targetColumn, StringComparison.Ordinal) && !IdLike.Contains(c))
            .ToList();

        var isNumeric = new Dictionary<string, bool>(StringComparer.Ordinal);
        var edges = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var c in featureCols)
        {
            var vals = rows.Where(r => r.ContainsKey(c)).Select(r => r[c]).ToList();
            bool numeric = vals.Count > 0 && vals.Take(3000).All(v => double.TryParse(v, out _));
            int card = vals.Distinct().Take(_numBins * 2 + 1).Count();
            if (numeric && card > _numBins * 2)
            {
                isNumeric[c] = true;
                edges[c] = Quantiles(vals.Where(v => double.TryParse(v, out _)).Select(double.Parse), _numBins);
            }
            else isNumeric[c] = false;
        }

        var lowCard = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in featureCols)
        {
            if (isNumeric[c]) { lowCard.Add(c); continue; }
            if (rows.Where(r => r.ContainsKey(c)).Select(r => r[c]).Distinct().Take(16).Count() <= 15)
                lowCard.Add(c);
        }

        var counts = new Dictionary<(string Col, string Tok), (long N, long M)>();
        var counts2 = new Dictionary<(string A, string Ta, string B, string Tb), (long N, long M)>();
        var rowtoks = new List<(string Col, string Tok)>(featureCols.Count);
        foreach (var rec in rows)
        {
            bool positive = rec.TryGetValue(_targetColumn, out var tv) && tv.Trim() == _positiveValue;
            rowtoks.Clear();
            foreach (var c in featureCols)
            {
                if (!rec.TryGetValue(c, out var v)) continue;
                string t = Tokenize(c, v, isNumeric, edges);
                var key = (c, t);
                counts.TryGetValue(key, out var nm);
                counts[key] = (nm.N + 1, nm.M + (positive ? 1 : 0));
                if (lowCard.Contains(c)) rowtoks.Add((c, t));
            }
            rowtoks.Sort((x, y) => string.CompareOrdinal(x.Col, y.Col));
            for (int i = 0; i < rowtoks.Count; i++)
                for (int j = i + 1; j < rowtoks.Count; j++)
                {
                    var k2 = (rowtoks[i].Col, rowtoks[i].Tok, rowtoks[j].Col, rowtoks[j].Tok);
                    counts2.TryGetValue(k2, out var nm2);
                    counts2[k2] = (nm2.N + 1, nm2.M + (positive ? 1 : 0));
                }
        }

        int batch = options.BatchSize > 1 ? options.BatchSize : 4096;
        double witnessWeight = RelationTypeRank.Associative * SourceTrust.StructuredCorpus;
        var predicts = RelationTypeRegistry.RelationTypeId("PREDICTS");
        var emitCtx = new TabularEmitContext(
            this, isNumeric, witnessWeight, predicts);

        if (!options.DryRun)
            yield return await BuildSchemaChangeAsync(featureCols, ct);

        await foreach (var change in DecomposerBatch.RunAsync(
                           EnumerateRecords(counts, counts2, ct),
                           emitCtx.Emit,
                           Source, "tabular", batch, context.Reader, options, ct))
            yield return change;
    }

    private async Task<SubstrateChange> BuildSchemaChangeAsync(IReadOnlyList<string> featureCols, CancellationToken ct)
    {
        var b = new SubstrateChangeBuilder(Source, "tabular/schema", null, 256, 0, 512);
        b.AddEntity(new EntityRow(OutcomeId, EntityTier.Word, OutcomeTypeId, Source));
        if (ContentEmitter.Emit(b, _targetColumn, Source) is { } targetNameId)
            b.AddAttestation(NativeAttestation.Categorical(
                OutcomeId, "IS_INSTANCE_OF", targetNameId, Source, SourceTrust.StructuredCorpus));
        if (ContentEmitter.Emit(b, _positiveValue, Source) is { } posValId)
            b.AddAttestation(NativeAttestation.Categorical(
                OutcomeId, "IS_INSTANCE_OF", posValId, Source, SourceTrust.StructuredCorpus));
        foreach (var c in featureCols)
        {
            b.AddEntity(new EntityRow(ColumnId(c), EntityTier.Word, ColumnTypeId, Source));
            _canonicalNames.Add($"tabular/column/{c}/v1");
            if (ContentEmitter.Emit(b, c, Source) is { } colNameId)
                b.AddAttestation(NativeAttestation.Categorical(
                    ColumnId(c), "IS_INSTANCE_OF", colNameId, Source, SourceTrust.StructuredCorpus));
        }
        return await b.SetInputUnitsConsumed(featureCols.Count).BuildAsync(ct);
    }

    private static async IAsyncEnumerable<TabularRecord> EnumerateRecords(
        Dictionary<(string Col, string Tok), (long N, long M)> counts,
        Dictionary<(string A, string Ta, string B, string Tb), (long N, long M)> counts2,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var ((col, tok), nm) in counts)
        {
            ct.ThrowIfCancellationRequested();
            yield return TabularRecord.Value(col, tok, nm.N, nm.M);
        }
        foreach (var ((pa, ta, pb, tb), nm) in counts2)
        {
            ct.ThrowIfCancellationRequested();
            yield return TabularRecord.Pair(pa, ta, pb, tb, nm.N, nm.M);
        }
        await Task.CompletedTask;
    }

    internal void TrackCanonical(string name) => _canonicalNames.Add(name);

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string Tokenize(string col, string v,
                            Dictionary<string, bool> isNumeric, Dictionary<string, double[]> edges)
    {
        if (isNumeric.TryGetValue(col, out var num) && num && double.TryParse(v, out var d) && edges.TryGetValue(col, out var e))
        {
            int bin = 0;
            while (bin < e.Length && d > e[bin]) bin++;
            return "b" + bin;
        }
        return v;
    }

    private static double[] Quantiles(IEnumerable<double> xs, int bins)
    {
        var arr = xs.ToArray();
        Array.Sort(arr);
        if (arr.Length == 0) return Array.Empty<double>();
        var qs = new List<double>(bins + 1);
        for (int i = 0; i <= bins; i++)
        {
            double idx = (double)i / bins * (arr.Length - 1);
            int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
            double q = arr[lo] + (idx - lo) * (arr[hi] - arr[lo]);
            if (qs.Count == 0 || q > qs[^1]) qs.Add(q);
        }
        return qs.ToArray();
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

    private readonly record struct TabularRecord(
        TabularRecordKind Kind,
        string Col,
        string Tok,
        string ColB,
        string TokB,
        long N,
        long M)
    {
        public static TabularRecord Value(string col, string tok, long n, long m) =>
            new(TabularRecordKind.Value, col, tok, "", "", n, m);
        public static TabularRecord Pair(string pa, string ta, string pb, string tb, long n, long m) =>
            new(TabularRecordKind.Pair, pa, ta, pb, tb, n, m);
    }

    private enum TabularRecordKind { Value, Pair }

    private sealed class TabularEmitContext(
        TabularDecomposer owner,
        Dictionary<string, bool> isNumeric,
        double witnessWeight,
        Hash128 predictsType)
    {
        public void Emit(TabularRecord rec, SubstrateChangeBuilder b)
        {
            if (rec.Kind == TabularRecordKind.Value)
                EmitValue(rec, b);
            else
                EmitPair(rec, b);
        }

        private void EmitValue(TabularRecord rec, SubstrateChangeBuilder b)
        {
            var cq = ValueId(rec.Col, rec.Tok);
            b.AddEntity(new EntityRow(cq, EntityTier.Word, ValueTypeId, Source));
            owner.TrackCanonical($"tabular/value/{rec.Col}={rec.Tok}/v1");
            b.AddAttestation(NativeAttestation.Aggregated(
                cq, predictsType, owner.OutcomeId, Source, contextId: ColumnId(rec.Col),
                games: rec.N, sumScoreFp1e9: checked(rec.M * Glicko2.FpScale), witnessWeight: witnessWeight));
            b.AddAttestation(NativeAttestation.Categorical(
                cq, "IS_VALUE_IN", ColumnId(rec.Col), Source, SourceTrust.StructuredCorpus));
            if (!isNumeric[rec.Col])
            {
                var bare = ContentEmitter.Emit(b, rec.Tok, Source);
                if (bare is { } bid)
                    b.AddAttestation(NativeAttestation.Categorical(
                        cq, "IS_INSTANCE_OF", bid, Source, SourceTrust.StructuredCorpus));
            }
        }

        private void EmitPair(TabularRecord rec, SubstrateChangeBuilder b)
        {
            var cq = Hash128.OfCanonical($"tabular/pair/{rec.Col}={rec.Tok}&{rec.ColB}={rec.TokB}/v1");
            b.AddEntity(new EntityRow(cq, EntityTier.Word, ValueTypeId, Source));
            owner.TrackCanonical($"tabular/pair/{rec.Col}={rec.Tok}&{rec.ColB}={rec.TokB}/v1");
            b.AddAttestation(NativeAttestation.Aggregated(
                cq, predictsType, owner.OutcomeId, Source, contextId: null,
                games: rec.N, sumScoreFp1e9: checked(rec.M * Glicko2.FpScale), witnessWeight: witnessWeight));
        }
    }
}
