using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

// The per-slot cell fold: every (role, layer) arena from ModelArenaPlan deposits one
// aggregated relation per schema cell (subject/object = per-source axis entities, or token
// entities for the TOKEN-faced slots), with games/sum-score carrying the magnitudes through
// the rational Score law. THIS is the testimony audit-export and synthesize-substrate read.
//
// History: this fold shipped in checkpoint c213b47 ("per-layer arena rewire") and was
// destroyed by the next commit (the sabotage commit), which replaced the file wholesale with
// the behavioral-planes ETL. Resurrected 2026-06-10 and ported to the NativeAttestation API.
public sealed class ModelCellETL
{
    private const int RowsPerChange = 500_000;
    private static readonly double ModelWeight =
        RelationTypeRegistry.Resolve("EMBEDS").Rank * SourceTrust.AiModelProbe;

    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _source;
    private readonly Hash128 _axisType;
    private readonly ILogger _log;
    private readonly Dictionary<string, SafetensorsContainerParser.TensorReference> _refMap;
    private int _commitEpoch;

    private long _relationsEmitted, _gamesPlayed, _zeros, _subEpsilon;

    private static readonly double ScoreEpsilon =
        double.TryParse(Environment.GetEnvironmentVariable("LAPLACE_MODEL_SCORE_EPSILON"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var e) && e > 0
        ? e : 0.0;

    public ModelCellETL(
        string modelDir,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId, Hash128 axisType,
        ILogger? log = null)
    {
        _recipe   = recipe;
        _tokens   = tokens;
        _source   = sourceId;
        _axisType = axisType;
        _log      = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var refs  = SafetensorsContainerParser.ParseModel(modelDir);
        _refMap   = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
            refs.Count, StringComparer.Ordinal);
        foreach (var r in refs) _refMap[r.Name] = r;
    }

    public async IAsyncEnumerable<SubstrateChange> EmitAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var prof = ArchitectureProfile.For(_recipe.ModelType);
        int dModel = _recipe.HiddenSize, interm = _recipe.IntermediateSize;
        int attnOut = _recipe.NumHeads * (dModel / _recipe.NumHeads);
        int kvDim   = _recipe.NumKvHeads * (dModel / _recipe.NumHeads);
        int vocab   = _recipe.VocabSize;

        var idxToOrd = new int[vocab];
        Array.Fill(idxToOrd, -1);
        var ordToEntity = new List<Hash128>();
        {
            var seen = new Dictionary<Hash128, int>();
            foreach (var rec in _tokens)
            {
                if (rec.TokenId < 0 || rec.TokenId >= vocab) continue;
                if (!seen.TryGetValue(rec.EntityId, out int ord))
                {
                    ord = ordToEntity.Count;
                    seen[rec.EntityId] = ord;
                    ordToEntity.Add(rec.EntityId);
                }
                idxToOrd[rec.TokenId] = ord;
            }
        }
        int distinctTokens = ordToEntity.Count;
        _log.LogInformation(
            "phase=cells tokens: {Distinct:N0} distinct content entities over {Vocab:N0} vocab slots",
            distinctTokens, vocab);

        var axisIds = new Dictionary<(string Space, int Index), Hash128>(
            dModel + attnOut + kvDim + interm);
        var axisLock = new object();
        Hash128 Axis(string space, int i)
        {
            lock (axisLock)
            {
                if (!axisIds.TryGetValue((space, i), out var id))
                {
                    id = SourceEntityIdConventions.ModelAxisEntity(_source, space, i);
                    axisIds[(space, i)] = id;
                }
                return id;
            }
        }

        // Epoch 0: axis entities — the fence. Cell attestations reference them, so they
        // must be committed before any attestation chunk applies.
        _commitEpoch = 0;
        {
            var b = NewChunk("axes", entityCapacity: dModel + attnOut + kvDim + interm);
            for (int i = 0; i < dModel; i++)  b.AddEntity(new EntityRow(Axis("channel", i),  EntityTier.Vocabulary, _axisType, _source));
            for (int i = 0; i < attnOut; i++) b.AddEntity(new EntityRow(Axis("attn_dim", i), EntityTier.Vocabulary, _axisType, _source));
            for (int i = 0; i < kvDim; i++)   b.AddEntity(new EntityRow(Axis("kv_dim", i),   EntityTier.Vocabulary, _axisType, _source));
            for (int i = 0; i < interm; i++)  b.AddEntity(new EntityRow(Axis("neuron", i),   EntityTier.Vocabulary, _axisType, _source));
            yield return b.Build();
        }
        _commitEpoch = 1;

        var slots = ModelArenaPlan.Slots(_recipe, prof)
            .Where(s => _refMap.ContainsKey(s.TensorName))
            .ToList();

        int foldWorkers = int.TryParse(
            Environment.GetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS"), out var dw) && dw > 0
            ? Math.Min(dw, slots.Count)
            : Math.Clamp(Environment.ProcessorCount - 4, 2, Math.Max(2, slots.Count));

        var chan = Channel.CreateBounded<SubstrateChange>(new BoundedChannelOptions(foldWorkers * 2)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var gate = new SemaphoreSlim(foldWorkers);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("phase=cells folding {Slots} per-layer arena slots across {Workers} parallel workers",
            slots.Count, foldWorkers);

        var producers = new List<Task>(slots.Count);
        foreach (var slot in slots)
        {
            var s = slot;
            producers.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try { await FoldSlotAsync(s, vocab, distinctTokens, idxToOrd, ordToEntity, dModel, Axis, chan.Writer, sw, ct); }
                finally { gate.Release(); }
            }, ct));
        }

        var completion = Task.WhenAll(producers).ContinueWith(
            t => chan.Writer.TryComplete(t.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        await foreach (var change in chan.Reader.ReadAllAsync(ct))
            yield return change;
        await completion;

        _log.LogInformation(
            "phase=cells done: {Rel:N0} relations (schema-shape bounded) carrying {Games:N0} matches ({Z:N0} zero + {Eps:N0} sub-ε non-events; ε={Epsilon}) in {S:F0}s",
            Interlocked.Read(ref _relationsEmitted), Interlocked.Read(ref _gamesPlayed),
            Interlocked.Read(ref _zeros), Interlocked.Read(ref _subEpsilon), ScoreEpsilon, sw.Elapsed.TotalSeconds);
    }

    private async Task FoldSlotAsync(
        ArenaSlot slot, int vocab, int distinctTokens, int[] idxToOrd, List<Hash128> ordToEntity,
        int dModel, Func<string, int, Hash128> axis, ChannelWriter<SubstrateChange> output,
        System.Diagnostics.Stopwatch sw, CancellationToken ct)
    {
        if (slot.IsNorm)
        {
            await FoldNormSlotAsync(slot, dModel, axis, output, sw, ct);
            return;
        }

        var tref = _refMap[slot.TensorName];
        int rows = tref.Shape[0], cols = tref.Shape.Length > 1 ? tref.Shape[1] : 1;

        ct.ThrowIfCancellationRequested();
        var w = WeightTensorETL.LoadTensorF32(_refMap, slot.TensorName, (long)rows * cols);
        double sumsq = 0;
        for (long i = 0; i < w.LongLength; i++) { double v = w[i]; sumsq += v * v; }
        double M = w.LongLength > 0 && sumsq > 0 ? Math.Sqrt(sumsq / w.LongLength) : 1.0;
        float epsM = (float)(ScoreEpsilon * M);
        _log.LogInformation(
            "phase=cells-calibrate {Role} L{Layer}: arena M={M:E3} over {N:N0} cells, non-event threshold |w|<{Eps:E3} ({S:F0}s)",
            slot.Role, slot.Layer, M, w.LongLength, epsM, sw.Elapsed.TotalSeconds);

        bool inTok  = slot.InSpace  == "TOKEN";
        bool outTok = slot.OutSpace == "TOKEN";
        int tRows = slot.RowsAreOut ? cols : rows;
        int tCols = slot.RowsAreOut ? rows : cols;
        int inSize  = inTok  ? distinctTokens : tRows;
        int outSize = outTok ? distinctTokens : tCols;
        long space = (long)inSize * outSize;
        var sumFp = new long[space];
        var games = new int[space];
        long unresolved = 0, zeros = 0, subEps = 0;

        // Score the tensor row-at-a-time through the native batch kernel (one P/Invoke
        // per row instead of one per cell), then accumulate above-threshold cells.
        var fpRow = new long[cols];
        for (int row = 0; row < rows; row++)
        {
            int off = checked(row * cols);
            NativeAttestation.ScoreBatchFp(new ReadOnlySpan<float>(w, off, cols), M, fpRow);
            for (int col = 0; col < cols; col++)
            {
                float v = w[off + col];
                if (v == 0f) { zeros++; continue; }
                if (epsM > 0f && Math.Abs(v) < epsM) { subEps++; continue; }
                int inIdx  = slot.RowsAreOut ? col : row;
                int outIdx = slot.RowsAreOut ? row : col;
                if (inTok)  { inIdx  = inIdx  < vocab ? idxToOrd[inIdx]  : -1; }
                if (outTok) { outIdx = outIdx < vocab ? idxToOrd[outIdx] : -1; }
                if (inIdx < 0 || outIdx < 0) { unresolved++; continue; }
                long flat = (long)inIdx * outSize + outIdx;
                sumFp[flat] += fpRow[col];
                games[flat]++;
            }
        }
        Interlocked.Add(ref _zeros, zeros);
        Interlocked.Add(ref _subEpsilon, subEps);

        // Emit through the native aggregated-batch builder: orient/outcome/id for a whole
        // sub-batch in one P/Invoke (the per-cell path cost ~3.8us/relation of fmgr ceremony).
        const int SubBatch = 131072;
        var cellBuf   = new AttestationAggregatedCellNative[SubBatch];
        var stagedBuf = new AttestationStagedNative[SubBatch];
        int nCells = 0;
        long emitted = 0, gamesOut = 0;
        var b = NewChunk(slot.Role);
        int inChunk = 0;

        async Task FlushSubBatchAsync()
        {
            if (nCells == 0) return;
            NativeAttestation.AggregatedBatch(
                cellBuf, nCells, slot.RelationTypeId, _source, null, ModelWeight, stagedBuf);
            for (int i = 0; i < nCells; i++)
            {
                b.AddAttestation(NativeAttestation.Row(in stagedBuf[i]));
                if (++inChunk >= RowsPerChange)
                {
                    await output.WriteAsync(b.Build(), ct);
                    b = NewChunk(slot.Role);
                    inChunk = 0;
                }
            }
            nCells = 0;
        }

        for (long flat = 0; flat < space; flat++)
        {
            if (games[flat] == 0) continue;
            int inIdx  = (int)(flat / outSize);
            int outIdx = (int)(flat % outSize);
            cellBuf[nCells++] = new AttestationAggregatedCellNative
            {
                Subject = inTok ? ordToEntity[inIdx] : axis(slot.InSpace, inIdx),
                Object  = outTok ? ordToEntity[outIdx] : axis(slot.OutSpace, outIdx),
                ObjectIsNull = 0,
                Games = games[flat],
                SumScoreFp1e9 = sumFp[flat],
            };
            emitted++;
            gamesOut += games[flat];
            if (nCells == SubBatch) await FlushSubBatchAsync();
        }
        await FlushSubBatchAsync();
        if (inChunk > 0) await output.WriteAsync(b.Build(), ct);

        long cum = Interlocked.Add(ref _relationsEmitted, emitted);
        long cumGames = Interlocked.Add(ref _gamesPlayed, gamesOut);
        _log.LogInformation(
            "phase=cells {Role} L{Layer}: {Rel:N0} relations (cum relations {Cum:N0}, games {Games:N0}, unresolved {U:N0}, sub-ε non-events {Eps:N0}), {S:F0}s",
            slot.Role, slot.Layer, emitted, cum, cumGames, unresolved, subEps, sw.Elapsed.TotalSeconds);
    }

    private async Task FoldNormSlotAsync(
        ArenaSlot slot, int dModel, Func<string, int, Hash128> axis,
        ChannelWriter<SubstrateChange> output, System.Diagnostics.Stopwatch sw, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var w = WeightTensorETL.LoadTensorF32(_refMap, slot.TensorName, dModel);
        double sumsq = 0;
        for (int i = 0; i < dModel; i++) { double v = w[i]; sumsq += v * v; }
        double M = sumsq > 0 ? Math.Sqrt(sumsq / dModel) : 1.0;

        var sumFp = new long[dModel];
        var games = new int[dModel];
        long zeros = 0;
        for (int i = 0; i < dModel; i++)
        {
            if (w[i] == 0f) { zeros++; continue; }
            sumFp[i] += (long)(NativeAttestation.Score(w[i], M) * Glicko2.FpScale);
            games[i]++;
        }
        Interlocked.Add(ref _zeros, zeros);

        long emitted = 0, gamesOut = 0;
        var b = NewChunk(slot.Role);
        for (int i = 0; i < dModel; i++)
        {
            if (games[i] == 0) continue;
            b.AddAttestation(NativeAttestation.Aggregated(
                axis("channel", i), slot.RelationTypeId, obj: null, _source,
                contextId: null, games: games[i], sumScoreFp1e9: sumFp[i],
                witnessWeight: ModelWeight));
            emitted++;
            gamesOut += games[i];
        }
        await output.WriteAsync(b.Build(), ct);
        long cum = Interlocked.Add(ref _relationsEmitted, emitted);
        Interlocked.Add(ref _gamesPlayed, gamesOut);
        _log.LogInformation("phase=cells {Role} L{Layer}: {Rel:N0} relations (cum relations {Cum:N0}), {S:F0}s",
            slot.Role, slot.Layer, emitted, cum, sw.Elapsed.TotalSeconds);
    }

    private SubstrateChangeBuilder NewChunk(string roleName, int entityCapacity = 0) =>
        new SubstrateChangeBuilder(_source, $"cells/{roleName}", null,
            entityCapacity: entityCapacity == 0 ? RowsPerChange : entityCapacity,
            physicalityCapacity: 0, attestationCapacity: RowsPerChange)
            .SetCommitEpoch(_commitEpoch);
}
