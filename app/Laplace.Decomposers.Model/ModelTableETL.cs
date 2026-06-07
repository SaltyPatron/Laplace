using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

public sealed class ModelTableETL
{
    private const int RowsPerChange = 500_000;
    private static readonly double ModelWeight =
        RelationTypeRegistry.Resolve("EMBEDS").Rank * SourceTrust.AiModelProbe;

    private sealed record Role(
        string Name,
        Hash128 TypeId,
        string? PerLayerTemplate,
        string? GlobalName,
        string InSpace,
        string OutSpace,
        bool RowsAreOut);

    private readonly string _modelDir;
    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _source;
    private readonly Hash128 _axisType;
    private readonly ILogger _log;
    private readonly IReadOnlyList<SafetensorsContainerParser.TensorReference> _refs;
    private readonly Dictionary<string, SafetensorsContainerParser.TensorReference> _refMap;

    private long _relationsEmitted, _gamesPlayed, _zeros, _subEpsilon;

    private static readonly double ScoreEpsilon =
        double.TryParse(Environment.GetEnvironmentVariable("LAPLACE_MODEL_SCORE_EPSILON"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var e) && e > 0
        ? e : 0.0;

    public ModelTableETL(
        string modelDir,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId, Hash128 axisType,
        ILogger? log = null)
    {
        _modelDir = modelDir;
        _recipe   = recipe;
        _tokens   = tokens;
        _source   = sourceId;
        _axisType = axisType;
        _log      = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _refs     = SafetensorsContainerParser.ParseModel(modelDir);
        _refMap   = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
            _refs.Count, StringComparer.Ordinal);
        foreach (var r in _refs) _refMap[r.Name] = r;
    }

    private IReadOnlyList<Role> Roles()
    {
        var p = ArchitectureProfile.For(ModelTypeOf());
        return new[]
        {
            new Role("EMBEDS",          ModelDecomposer.EmbedsTypeId,        null, p.EmbedTokens, "TOKEN",   "channel", RowsAreOut: false),
            new Role("OUTPUT_PROJECTS", ModelDecomposer.OutputProjectsTypeId, null, p.LmHead ?? p.EmbedTokens, "channel", "TOKEN", RowsAreOut: true),
            new Role("Q_PROJECTS",      ModelDecomposer.QProjectsTypeId,     p.QProj,    null, "channel", "attn_dim", RowsAreOut: true),
            new Role("K_PROJECTS",      ModelDecomposer.KProjectsTypeId,     p.KProj,    null, "channel", "kv_dim",   RowsAreOut: true),
            new Role("V_PROJECTS",      ModelDecomposer.VProjectsTypeId,     p.VProj,    null, "channel", "kv_dim",   RowsAreOut: true),
            new Role("O_PROJECTS",      ModelDecomposer.OProjectsTypeId,     p.OProj,    null, "attn_dim", "channel", RowsAreOut: true),
            new Role("GATES",           ModelDecomposer.GatesTypeId,         p.GateProj, null, "channel", "neuron",   RowsAreOut: true),
            new Role("UP_PROJECTS",     ModelDecomposer.UpProjectsTypeId,    p.UpProj,   null, "channel", "neuron",   RowsAreOut: true),
            new Role("DOWN_PROJECTS",   ModelDecomposer.DownProjectsTypeId,  p.DownProj, null, "neuron",  "channel",  RowsAreOut: true),
        }.Where(r => r.PerLayerTemplate is not null || r.GlobalName is not null).ToArray();
    }

    private string ModelTypeOf() => _recipe.ModelType;

    public async IAsyncEnumerable<SubstrateChange> EmitAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
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
        _log.LogInformation("phase=etl tokens: {Distinct:N0} distinct content entities over {Vocab:N0} vocab slots",
            distinctTokens, vocab);

        var axisIds = new Dictionary<(string Space, int Index), Hash128>(
            dModel + attnOut + kvDim + interm);
        Hash128 Axis(string space, int i)
        {
            if (!axisIds.TryGetValue((space, i), out var id))
            {
                id = SourceEntityIdConventions.ModelAxisEntity(_source, space, i);
                axisIds[(space, i)] = id;
            }
            return id;
        }

        var roles = Roles();
        {
            var b = new SubstrateChangeBuilder(_source, "model/axes",
                entityCapacity: dModel + attnOut + kvDim + interm,
                physicalityCapacity: 0, attestationCapacity: 0);
            for (int i = 0; i < dModel; i++)  b.AddEntity(new EntityRow(Axis("channel", i),  (byte)MetaTier.Meta, _axisType, _source));
            for (int i = 0; i < attnOut; i++) b.AddEntity(new EntityRow(Axis("attn_dim", i), (byte)MetaTier.Meta, _axisType, _source));
            for (int i = 0; i < kvDim; i++)   b.AddEntity(new EntityRow(Axis("kv_dim", i),   (byte)MetaTier.Meta, _axisType, _source));
            for (int i = 0; i < interm; i++)  b.AddEntity(new EntityRow(Axis("neuron", i),   (byte)MetaTier.Meta, _axisType, _source));
            yield return b.Build();
        }

        int foldWorkers = int.TryParse(
            Environment.GetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS"), out var dw) && dw > 0
            ? Math.Min(dw, roles.Count + 1)
            : Math.Clamp(Environment.ProcessorCount - 4, 2, roles.Count + 1);

        var chan = Channel.CreateBounded<SubstrateChange>(new BoundedChannelOptions(foldWorkers * 2)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var gate = new SemaphoreSlim(foldWorkers);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("phase=etl folding {Roles} roles + norms across {Workers} parallel workers",
            roles.Count, foldWorkers);

        var producers = new List<Task>(roles.Count + 1);
        foreach (var role in roles)
        {
            var r = role;
            producers.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try { await FoldRoleAsync(r, vocab, distinctTokens, idxToOrd, ordToEntity, Axis, chan.Writer, sw, ct); }
                finally { gate.Release(); }
            }, ct));
        }
        producers.Add(Task.Run(async () =>
        {
            await gate.WaitAsync(ct);
            try { await FoldNormsAsync(dModel, Axis, chan.Writer, ct); }
            finally { gate.Release(); }
        }, ct));

        var completion = Task.WhenAll(producers).ContinueWith(
            t => chan.Writer.TryComplete(t.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        await foreach (var change in chan.Reader.ReadAllAsync(ct))
            yield return change;
        await completion;

        _log.LogInformation(
            "phase=etl done: {Rel:N0} relations (schema-shape bounded) carrying {Games:N0} matches ({Z:N0} zero + {Eps:N0} sub-ε non-events; ε={Epsilon}) in {S:F0}s",
            Interlocked.Read(ref _relationsEmitted), Interlocked.Read(ref _gamesPlayed),
            Interlocked.Read(ref _zeros), Interlocked.Read(ref _subEpsilon), ScoreEpsilon, sw.Elapsed.TotalSeconds);
    }

    private async Task FoldRoleAsync(
        Role role, int vocab, int distinctTokens, int[] idxToOrd, List<Hash128> ordToEntity,
        Func<string, int, Hash128> axis, ChannelWriter<SubstrateChange> output,
        System.Diagnostics.Stopwatch sw, CancellationToken ct)
    {
        int rows = -1, cols = -1;
        var instances = new List<(string Name, int Layer)>();
        foreach (var inst in Instances(role))
        {
            if (!_refMap.TryGetValue(inst.Name, out var tref)) continue;
            int tr = tref.Shape[0], tc = tref.Shape.Length > 1 ? tref.Shape[1] : 1;
            if (rows < 0) { rows = tr; cols = tc; }
            else if (tr != rows || tc != cols)
                throw new InvalidOperationException(
                    $"{role.Name}: instance {inst.Name} shape [{tr}×{tc}] differs from the role shape [{rows}×{cols}] — one logical table per role");
            instances.Add(inst);
        }
        if (instances.Count == 0) return;

        double sumsq = 0; long n = 0;
        foreach (var (name, _) in instances)
        {
            ct.ThrowIfCancellationRequested();
            var w = WeightTensorETL.LoadTensorF32(_refMap, name, (long)rows * cols);
            for (long i = 0; i < w.LongLength; i++) { double v = w[i]; sumsq += v * v; }
            n += w.LongLength;
        }
        double M = n > 0 && sumsq > 0 ? Math.Sqrt(sumsq / n) : 1.0;
        float epsM = (float)(ScoreEpsilon * M);
        _log.LogInformation("phase=etl-calibrate {Role}: arena M={M:E3} over {N:N0} cells, non-event threshold |w|<{Eps:E3} ({S:F0}s)",
            role.Name, M, n, epsM, sw.Elapsed.TotalSeconds);

        bool inTok  = role.InSpace  == "TOKEN";
        bool outTok = role.OutSpace == "TOKEN";
        int tRows = role.RowsAreOut ? cols : rows;
        int tCols = role.RowsAreOut ? rows : cols;
        int inSize  = inTok  ? distinctTokens : tRows;
        int outSize = outTok ? distinctTokens : tCols;
        long space = (long)inSize * outSize;
        var sumFp = new long[space];
        var games = new int[space];
        long unresolved = 0, zeros = 0, subEps = 0;

        foreach (var (name, layer) in instances)
        {
            ct.ThrowIfCancellationRequested();
            var w = WeightTensorETL.LoadTensorF32(_refMap, name, (long)rows * cols);
            for (int row = 0; row < rows; row++)
            {
                long off = (long)row * cols;
                for (int col = 0; col < cols; col++)
                {
                    float v = w[off + col];
                    if (v == 0f) { zeros++; continue; }
                    if (epsM > 0f && Math.Abs(v) < epsM) { subEps++; continue; }
                    int inIdx  = role.RowsAreOut ? col : row;
                    int outIdx = role.RowsAreOut ? row : col;
                    if (inTok)  { inIdx  = inIdx  < vocab ? idxToOrd[inIdx]  : -1; }
                    if (outTok) { outIdx = outIdx < vocab ? idxToOrd[outIdx] : -1; }
                    if (inIdx < 0 || outIdx < 0) { unresolved++; continue; }
                    long flat = (long)inIdx * outSize + outIdx;
                    sumFp[flat] += (long)(AttestationFactory.Score(v, M) * Glicko2.FpScale);
                    games[flat]++;
                }
            }
            _log.LogInformation("phase=etl {Role} L{Layer}: folded ({S:F0}s)",
                role.Name, layer, sw.Elapsed.TotalSeconds);
        }
        Interlocked.Add(ref _zeros, zeros);
        Interlocked.Add(ref _subEpsilon, subEps);

        long emitted = 0, gamesOut = 0;
        var b = NewChunk(role.Name);
        int inChunk = 0;
        for (long flat = 0; flat < space; flat++)
        {
            if (games[flat] == 0) continue;
            int inIdx  = (int)(flat / outSize);
            int outIdx = (int)(flat % outSize);
            Hash128 subj = inTok  ? ordToEntity[inIdx]  : axis(role.InSpace, inIdx);
            Hash128 obj  = outTok ? ordToEntity[outIdx] : axis(role.OutSpace, outIdx);

            b.AddAttestation(AttestationFactory.CreateAggregated(
                subj, role.TypeId, obj, _source, contextId: null,
                games: games[flat], sumScoreFp1e9: sumFp[flat], witnessWeight: ModelWeight));
            emitted++;
            gamesOut += games[flat];
            if (++inChunk >= RowsPerChange)
            {
                await output.WriteAsync(b.Build(), ct);
                b = NewChunk(role.Name);
                inChunk = 0;
            }
        }
        if (inChunk > 0) await output.WriteAsync(b.Build(), ct);

        long cum = Interlocked.Add(ref _relationsEmitted, emitted);
        long cumGames = Interlocked.Add(ref _gamesPlayed, gamesOut);
        _log.LogInformation(
            "phase=etl {Role}: {Rel:N0} relations from {Pos} positions (cum relations {Cum:N0}, games {Games:N0}, unresolved {U:N0}, sub-ε non-events {Eps:N0}), {S:F0}s",
            role.Name, emitted, instances.Count, cum, cumGames, unresolved, subEps, sw.Elapsed.TotalSeconds);
    }

    private async Task FoldNormsAsync(
        int dModel, Func<string, int, Hash128> axis,
        ChannelWriter<SubstrateChange> output, CancellationToken ct)
    {
        var prof = ArchitectureProfile.For(ModelTypeOf());
        var normNames = NormInstances(prof).Where(t => _refMap.ContainsKey(t.Name)).ToList();
        if (normNames.Count == 0) return;

        double sumsq = 0; long n = 0;
        foreach (var (name, _) in normNames)
        {
            ct.ThrowIfCancellationRequested();
            var w = WeightTensorETL.LoadTensorF32(_refMap, name, dModel);
            for (int i = 0; i < dModel; i++) { double v = w[i]; sumsq += v * v; }
            n += dModel;
        }
        double M = n > 0 && sumsq > 0 ? Math.Sqrt(sumsq / n) : 1.0;

        var sumFp = new long[dModel];
        var games = new int[dModel];
        long zeros = 0;
        foreach (var (name, _) in normNames)
        {
            ct.ThrowIfCancellationRequested();
            var w = WeightTensorETL.LoadTensorF32(_refMap, name, dModel);
            for (int i = 0; i < dModel; i++)
            {
                if (w[i] == 0f) { zeros++; continue; }
                sumFp[i] += (long)(AttestationFactory.Score(w[i], M) * Glicko2.FpScale);
                games[i]++;
            }
        }
        Interlocked.Add(ref _zeros, zeros);

        long emitted = 0, gamesOut = 0;
        var b = NewChunk("NORM_SCALES");
        for (int i = 0; i < dModel; i++)
        {
            if (games[i] == 0) continue;
            b.AddAttestation(AttestationFactory.CreateAggregated(
                axis("channel", i), ModelDecomposer.NormScalesTypeId, obj: null, _source,
                contextId: null, games: games[i], sumScoreFp1e9: sumFp[i],
                witnessWeight: ModelWeight));
            emitted++;
            gamesOut += games[i];
        }
        await output.WriteAsync(b.Build(), ct);
        Interlocked.Add(ref _relationsEmitted, emitted);
        Interlocked.Add(ref _gamesPlayed, gamesOut);
    }

    private IEnumerable<(string Name, int Layer)> Instances(Role role)
    {
        if (role.GlobalName is not null) { yield return (role.GlobalName, -1); yield break; }
        for (int l = 0; l < _recipe.NumLayers; l++)
            yield return (ArchitectureProfile.Layer(role.PerLayerTemplate!, l), l);
    }

    private IEnumerable<(string Name, int Layer)> NormInstances(ArchitectureProfile p)
    {
        for (int l = 0; l < _recipe.NumLayers; l++)
            foreach (var t in p.PerLayerNorms)
                yield return (ArchitectureProfile.Layer(t, l), l);
        yield return (p.FinalNorm, -1);
    }

    private SubstrateChangeBuilder NewChunk(string roleName) =>
        new(_source, $"etl/{roleName}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: RowsPerChange);
}
