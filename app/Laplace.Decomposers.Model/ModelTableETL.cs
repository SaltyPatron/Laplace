using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// THE model ingest — "ETL on conventional AI, for AI" (the inventor's design;
/// model-ingest-is-etl memory; ARCHITECTURE §8; discussion #192 §6-§7).
///
/// EXTRACT — a weight tensor is a flattened 2D lookup table whose every cell
/// training already computed. Stream the cells at rest: O(params), exact,
/// no forward pass, no probes, no GEMM, no pre-joining.
///
/// TRANSFORM — key resolution only. Token axes are literal (embed/lm_head rows
/// are tokens). Hidden axes are the source's SURROGATE KEYS — residual
/// channels, attention dims, kv dims, FFN neurons — first-class join-node
/// entities (<see cref="SourceEntityIdConventions.ModelAxisEntity"/>), aligned
/// cross-model by placements, never by index.
///
/// LOAD — adjudication. Each non-zero cell is ONE signed Glicko match under
/// its TENSOR-ROLE kind (the fixed ten: EMBEDS, Q_PROJECTS, K_PROJECTS,
/// V_PROJECTS, O_PROJECTS, GATES, UP_PROJECTS, DOWN_PROJECTS, NORM_SCALES,
/// OUTPUT_PROJECTS), oriented along the dataflow (in-axis → out-axis).
/// score = ½(1+tanh(w/M)), M = the role's pooled RMS (measured, never a knob).
/// Zero = the only non-event; tiny = a draw by math; negative = a loss.
///
/// POSITIONS AGGREGATE — layers (and norm slots) are positions of the same
/// logical table. Relation identity EXCLUDES position, and so does EVIDENCE
/// identity: one evidence row per (subject, kind, object, source) with
/// context_id NULL ("Q_PROJECTS context_id=NULL is correct"); every position's
/// match lands ON THAT ROW — observation_count = the games played, the exact
/// score sum rides in-flight into the consensus accumulation. NO synthetic
/// per-position entities (#192 §7); per-position attribution is RECIPE
/// content. RECORDS ARE BOUNDED BY THE SCHEMA SHAPE (vocab×d_model,
/// d_model×attnOut, d_model×interm, …) — never by depth, never by parameter
/// count. A flat per-(cell, position) dump is the named failure: "a
/// billion-edge write overflow = the emit ignoring the DAG."
///
/// The token×token bilinear (QK/OV/FFN) is the QUERY-TIME read — μ-ranked
/// joins EMBEDS → interior kinds → OUTPUT_PROJECTS — never materialized here.
/// </summary>
public sealed class ModelTableETL
{
    private const int RowsPerChange = 500_000;
    // Witness weight = kind_rank × source_trust (× tenant_trust = 1 until S5).
    // Witness weight for the tensor-role arenas: rank from THE registry (the
    // tensor-role family is first-class Canon — all ten share TensorCalculation)
    // × the model source trust. Never a call-site literal.
    private static readonly double ModelWeight =
        RelationTypeRegistry.Resolve("EMBEDS").Rank * SourceTrust.AiModelProbe;

    /// <summary>One logical table role of the transformer-family schema.</summary>
    private sealed record Role(
        string Name,            // canonical kind name (arena)
        Hash128 KindId,
        string? PerLayerTemplate, // tensor name template with {L}, or null for global tensors
        string? GlobalName,       // global tensor name (embed/lm_head), or null
        string InSpace,           // axis space of the INPUT side (subject)
        string OutSpace,          // axis space of the OUTPUT side (object); "TOKEN" = token entities
        bool RowsAreOut);         // HF [rows × cols]: true ⇒ rows = out-axis, cols = in-axis

    private readonly string _modelDir;
    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _source;
    private readonly Hash128 _axisType;
    private readonly ILogger _log;
    private readonly IReadOnlyList<SafetensorsContainerParser.TensorReference> _refs;
    private readonly Dictionary<string, SafetensorsContainerParser.TensorReference> _refMap;

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
        // Dataflow orientation per HF out×in convention: [rows × cols] = [out × in]
        // for projections; embed is [token × channel] (token IN), lm_head is
        // [token × channel] with token OUT.
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

    // Parse() defaults model_type to "llama"; ArchitectureProfile.For throws for unmapped.
    private string ModelTypeOf() => _recipe.ModelType;

    /// <summary>Stream the whole model as substrate changes: axis join-node
    /// entities first, then every logical table folded into ONE evidence row
    /// per relation (positions aggregated, exact score sums into consensus).</summary>
    public async IAsyncEnumerable<SubstrateChange> EmitAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        int dModel = _recipe.HiddenSize, interm = _recipe.IntermediateSize;
        int attnOut = _recipe.NumHeads * (dModel / _recipe.NumHeads);
        int kvDim   = _recipe.NumKvHeads * (dModel / _recipe.NumHeads);
        int vocab   = _recipe.VocabSize;

        // Token axis → DISTINCT CONTENT ENTITIES, not vocab slots. Content is
        // identity: a byte-fallback token and a text token with the same content
        // are ONE entity, so their slots' matches FOLD onto the same relation
        // here (otherwise the second slot's games die on the writer's
        // ON CONFLICT and evidence undercounts what consensus accumulated).
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

        // ── Axis join-node entities (the source's surrogate keys) ──
        var axisIds = new Dictionary<(string Space, int Index), Hash128>();
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

        // ── PASS 1 — per-role arena scale M: pooled RMS over the role's cells ──
        // (measured from the role's own testimony; a SCALE, never a cut).
        var arenaM = new double[roles.Count];
        for (int r = 0; r < roles.Count; r++)
        {
            double sumsq = 0; long n = 0;
            foreach (var (name, _) in Instances(roles[r]))
            {
                if (!_refMap.TryGetValue(name, out var tref)) continue;
                var w = WeightTensorETL.LoadTensorF32(_refMap, name,
                    (long)tref.Shape[0] * (tref.Shape.Length > 1 ? tref.Shape[1] : 1));
                for (long i = 0; i < w.LongLength; i++) { double v = w[i]; sumsq += v * v; }
                n += w.LongLength;
            }
            arenaM[r] = n > 0 && sumsq > 0 ? Math.Sqrt(sumsq / n) : 1.0;
            _log.LogInformation("phase=etl-calibrate {Role}: arena M={M:E3} over {N:N0} cells",
                roles[r].Name, arenaM[r], n);
        }

        // ── PASS 2 — fold every position's matches onto the relation, then emit
        //    ONE evidence row per relation: (games, exact Σscore), context NULL ──
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long relationsEmitted = 0, gamesPlayed = 0, zeros = 0;
        for (int r = 0; r < roles.Count; r++)
        {
            var role = roles[r];
            double M = arenaM[r];

            // The role's relation space = its SCHEMA SHAPE (one logical table,
            // however many layer instances). Fail loud on a shape mismatch.
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
            if (instances.Count == 0) continue;

            // Relation space = the role's SCHEMA SHAPE with token axes folded to
            // DISTINCT ENTITIES (key resolution happens AT FOLD TIME: vocab slot →
            // content entity ordinal; a padded slot past the vocab has no key).
            bool inTok  = role.InSpace  == "TOKEN";
            bool outTok = role.OutSpace == "TOKEN";
            int tRows = role.RowsAreOut ? cols : rows;   // tensor extent of the in-axis
            int tCols = role.RowsAreOut ? rows : cols;   // tensor extent of the out-axis
            int inSize  = inTok  ? distinctTokens : tRows;
            int outSize = outTok ? distinctTokens : tCols;
            long space = (long)inSize * outSize;
            var sumFp = new long[space];
            var games = new int[space];
            long unresolved = 0;

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
                        if (v == 0f) { zeros++; continue; }       // zero = the only non-event
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
                await Task.Yield();
            }

            // Emit the folded table — one row per relation that played ≥1 game.
            long roleStart = relationsEmitted;
            var b = NewChunk(role.Name);
            int inChunk = 0;
            for (long flat = 0; flat < space; flat++)
            {
                if (games[flat] == 0) continue;
                int inIdx  = (int)(flat / outSize);
                int outIdx = (int)(flat % outSize);
                Hash128 subj = inTok  ? ordToEntity[inIdx]  : Axis(role.InSpace, inIdx);
                Hash128 obj  = outTok ? ordToEntity[outIdx] : Axis(role.OutSpace, outIdx);

                b.AddAttestation(AttestationFactory.CreateAggregated(
                    subj, role.TypeId, obj, _source, contextId: null,
                    games: games[flat], sumScoreFp1e9: sumFp[flat], witnessWeight: ModelWeight));
                relationsEmitted++;
                gamesPlayed += games[flat];
                if (++inChunk >= RowsPerChange)
                {
                    yield return b.Build();
                    b = NewChunk(role.Name);
                    inChunk = 0;
                    await Task.Yield();
                }
            }
            if (inChunk > 0) yield return b.Build();
            _log.LogInformation(
                "phase=etl {Role}: {Rel:N0} relations from {Pos} positions (cum relations {Cum:N0}, games {Games:N0}, zeros {Z:N0}, unresolved {U:N0}), {S:F0}s",
                role.Name, relationsEmitted - roleStart, instances.Count, relationsEmitted, gamesPlayed, zeros, unresolved, sw.Elapsed.TotalSeconds);
        }

        // ── NORM_SCALES — per-channel scale rows (unary; one logical table whose
        //    positions are every norm instance: per-layer slots + final) ──
        {
            var prof = ArchitectureProfile.For(ModelTypeOf());
            var normNames = NormInstances(prof).Where(t => _refMap.ContainsKey(t.Name)).ToList();
            if (normNames.Count > 0)
            {
                double sumsq = 0; long n = 0;
                foreach (var (name, _) in normNames)
                {
                    var w = WeightTensorETL.LoadTensorF32(_refMap, name, dModel);
                    for (int i = 0; i < dModel; i++) { double v = w[i]; sumsq += v * v; }
                    n += dModel;
                }
                double M = n > 0 && sumsq > 0 ? Math.Sqrt(sumsq / n) : 1.0;

                var sumFp = new long[dModel];
                var games = new int[dModel];
                foreach (var (name, _) in normNames)
                {
                    var w = WeightTensorETL.LoadTensorF32(_refMap, name, dModel);
                    for (int i = 0; i < dModel; i++)
                    {
                        if (w[i] == 0f) { zeros++; continue; }
                        sumFp[i] += (long)(AttestationFactory.Score(w[i], M) * Glicko2.FpScale);
                        games[i]++;
                    }
                }

                var b = NewChunk("NORM_SCALES");
                for (int i = 0; i < dModel; i++)
                {
                    if (games[i] == 0) continue;
                    b.AddAttestation(AttestationFactory.CreateAggregated(
                        Axis("channel", i), ModelDecomposer.NormScalesTypeId, obj: null, _source,
                        contextId: null, games: games[i], sumScoreFp1e9: sumFp[i],
                        witnessWeight: ModelWeight));
                    relationsEmitted++;
                    gamesPlayed += games[i];
                }
                yield return b.Build();
            }
        }

        _log.LogInformation(
            "phase=etl done: {Rel:N0} relations (schema-shape bounded) carrying {Games:N0} matches ({Z:N0} zero non-events) in {S:F0}s",
            relationsEmitted, gamesPlayed, zeros, sw.Elapsed.TotalSeconds);
    }

    /// <summary>The role's tensor instances: (name, layer). Layer = -1 for the
    /// global tables (embed/lm_head — one position).</summary>
    private IEnumerable<(string Name, int Layer)> Instances(Role role)
    {
        if (role.GlobalName is not null) { yield return (role.GlobalName, -1); yield break; }
        for (int l = 0; l < _recipe.NumLayers; l++)
            yield return (ArchitectureProfile.Layer(role.PerLayerTemplate!, l), l);
    }

    /// <summary>Norm positions: every per-layer slot of every layer, plus the
    /// final norm. All fold onto the same per-channel NORM_SCALES relations.</summary>
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
