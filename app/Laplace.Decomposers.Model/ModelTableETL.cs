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
/// V_PROJECTS, O_PROJECTS, GATES, UP_PROJECTS, DOWN_PROJECTS, NORMALIZES,
/// OUTPUT_PROJECTS), oriented along the dataflow (in-axis → out-axis).
/// score = ½(1+tanh(w/M)), M = the role's pooled RMS (measured, never a knob).
/// Zero = the only non-event; tiny = a draw by math; negative = a loss.
///
/// POSITIONS AGGREGATE — layers are positions of the same logical table:
/// relation identity EXCLUDES the layer; each (layer, role) instance is a
/// WITNESS entity carried in context_id (evidence = provenance of which
/// positions testified; consensus folds them). Records are bounded by the
/// SCHEMA SHAPE (vocab×d_model, d_model×attnOut, d_model×interm, …), never by
/// depth and never by parameter count.
///
/// The token×token bilinear (QK/OV/FFN) is the QUERY-TIME read — μ-ranked
/// joins EMBEDS → interior kinds → OUTPUT_PROJECTS — never materialized here.
/// </summary>
public sealed class ModelTableETL
{
    private const int RowsPerChange = 500_000;
    private const double ModelTrust = 0.50;   // SourceTrust.AiModelProbe; kind_rank via registry rank of the role kinds

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
    private readonly Hash128 _witnessType;
    private readonly Hash128 _axisType;
    private readonly ILogger _log;
    private readonly IReadOnlyList<SafetensorsContainerParser.TensorReference> _refs;
    private readonly Dictionary<string, SafetensorsContainerParser.TensorReference> _refMap;

    public ModelTableETL(
        string modelDir,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId, Hash128 witnessType, Hash128 axisType,
        ILogger? log = null)
    {
        _modelDir    = modelDir;
        _recipe      = recipe;
        _tokens      = tokens;
        _source      = sourceId;
        _witnessType = witnessType;
        _axisType    = axisType;
        _log         = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _refs        = SafetensorsContainerParser.ParseModel(modelDir);
        _refMap      = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
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
            new Role("EMBEDS",          ModelDecomposer.EmbedsKind,        null, p.EmbedTokens, "TOKEN",   "channel", RowsAreOut: false),
            new Role("OUTPUT_PROJECTS", ModelDecomposer.OutputProjectsKind, null, p.LmHead ?? p.EmbedTokens, "channel", "TOKEN", RowsAreOut: true),
            new Role("Q_PROJECTS",      ModelDecomposer.QProjectsKind,     p.QProj,    null, "channel", "attn_dim", RowsAreOut: true),
            new Role("K_PROJECTS",      ModelDecomposer.KProjectsKind,     p.KProj,    null, "channel", "kv_dim",   RowsAreOut: true),
            new Role("V_PROJECTS",      ModelDecomposer.VProjectsKind,     p.VProj,    null, "channel", "kv_dim",   RowsAreOut: true),
            new Role("O_PROJECTS",      ModelDecomposer.OProjectsKind,     p.OProj,    null, "attn_dim", "channel", RowsAreOut: true),
            new Role("GATES",           ModelDecomposer.GatesKind,         p.GateProj, null, "channel", "neuron",   RowsAreOut: true),
            new Role("UP_PROJECTS",     ModelDecomposer.UpProjectsKind,    p.UpProj,   null, "channel", "neuron",   RowsAreOut: true),
            new Role("DOWN_PROJECTS",   ModelDecomposer.DownProjectsKind,  p.DownProj, null, "neuron",  "channel",  RowsAreOut: true),
        }.Where(r => r.PerLayerTemplate is not null || r.GlobalName is not null).ToArray();
    }

    // Parse() defaults model_type to "llama"; ArchitectureProfile.For throws for unmapped.
    private string ModelTypeOf() => _recipe.ModelType;

    /// <summary>Stream the whole model as substrate changes: axis entities +
    /// witnesses first, then every table's cells as adjudicated matches.</summary>
    public async IAsyncEnumerable<SubstrateChange> EmitAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        int dModel = _recipe.HiddenSize, interm = _recipe.IntermediateSize;
        int attnOut = _recipe.NumHeads * (dModel / _recipe.NumHeads);
        int kvDim   = _recipe.NumKvHeads * (dModel / _recipe.NumHeads);
        int vocab   = _recipe.VocabSize;

        var tokById = new Hash128?[vocab];
        foreach (var rec in _tokens)
            if (rec.TokenId >= 0 && rec.TokenId < vocab) tokById[rec.TokenId] = rec.EntityId;

        // ── Axis join-node entities (the source's surrogate keys) + witnesses ──
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
            var b = new SubstrateChangeBuilder(_source, "model/axes+witnesses",
                entityCapacity: dModel + attnOut + kvDim + interm + roles.Count * (_recipe.NumLayers + 1),
                physicalityCapacity: 0, attestationCapacity: 0);
            for (int i = 0; i < dModel; i++)  b.AddEntity(new EntityRow(Axis("channel", i),  0, _axisType, _source));
            for (int i = 0; i < attnOut; i++) b.AddEntity(new EntityRow(Axis("attn_dim", i), 0, _axisType, _source));
            for (int i = 0; i < kvDim; i++)   b.AddEntity(new EntityRow(Axis("kv_dim", i),   0, _axisType, _source));
            for (int i = 0; i < interm; i++)  b.AddEntity(new EntityRow(Axis("neuron", i),   0, _axisType, _source));
            for (int r = 0; r < roles.Count; r++)
            {
                if (roles[r].GlobalName is not null)
                    b.AddEntity(new EntityRow(Witness(r, -1), 0, _witnessType, _source));
                else
                    for (int l = 0; l < _recipe.NumLayers; l++)
                        b.AddEntity(new EntityRow(Witness(r, l), 0, _witnessType, _source));
            }
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

        // ── PASS 2 — stream every cell as one adjudicated match ──
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long emitted = 0, zeros = 0;
        for (int r = 0; r < roles.Count; r++)
        {
            var role = roles[r];
            double M = arenaM[r];
            foreach (var (name, layer) in Instances(role))
            {
                ct.ThrowIfCancellationRequested();
                if (!_refMap.TryGetValue(name, out var tref)) continue;
                int rows = tref.Shape[0], cols = tref.Shape.Length > 1 ? tref.Shape[1] : 1;
                var w = WeightTensorETL.LoadTensorF32(_refMap, name, (long)rows * cols);
                Hash128 wit = Witness(r, layer);

                var b = NewChunk(role.Name, layer, wit);
                int inChunk = 0;
                for (int row = 0; row < rows; row++)
                {
                    long off = (long)row * cols;
                    for (int col = 0; col < cols; col++)
                    {
                        float v = w[off + col];
                        if (v == 0f) { zeros++; continue; }       // zero = the only non-event

                        int outIdx = role.RowsAreOut ? row : col;
                        int inIdx  = role.RowsAreOut ? col : row;
                        // Token axes resolve through the source's own key-mapping table;
                        // a padded row past the vocab has no key — nothing to resolve.
                        Hash128? subj = role.InSpace  == "TOKEN"
                            ? (inIdx  < vocab ? tokById[inIdx]  : null) : Axis(role.InSpace, inIdx);
                        Hash128? obj  = role.OutSpace == "TOKEN"
                            ? (outIdx < vocab ? tokById[outIdx] : null) : Axis(role.OutSpace, outIdx);
                        if (subj is null || obj is null) continue;  // unresolvable token id

                        b.AddAttestation(AttestationFactory.CreateWeighted(
                            subj.Value, role.KindId, obj.Value, _source, contextId: wit,
                            kindRank: KindRank.TensorCalculation, sourceTrust: ModelTrust,
                            magnitude: v, arenaScale: M));
                        emitted++;
                        if (++inChunk >= RowsPerChange)
                        {
                            yield return b.Build();
                            b = NewChunk(role.Name, layer, wit);
                            inChunk = 0;
                        }
                    }
                }
                if (inChunk > 0) yield return b.Build();
                _log.LogInformation("phase=etl {Role} L{Layer}: {Cells:N0} cells loaded (cum {Cum:N0}, zeros {Z:N0}), {S:F0}s",
                    role.Name, layer, (long)rows * cols, emitted, zeros, sw.Elapsed.TotalSeconds);
                await Task.Yield();
            }
        }

        // ── NORMALIZES — per-channel scale rows (unary; one logical table).
        //    Llama has TWO positions per layer (input + post-attn norm) plus the
        //    final norm — each is a DISTINCT witness slot (roles.Count + slot),
        //    never collapsed: same channel, same kind, different position. ──
        {
            var prof = ArchitectureProfile.For(ModelTypeOf());
            double sumsq = 0; long n = 0;
            var normNames = NormInstances(prof).ToList();
            foreach (var (name, _, _) in normNames)
                if (_refMap.TryGetValue(name, out _))
                {
                    var w = WeightTensorETL.LoadTensorF32(_refMap, name, dModel);
                    for (int i = 0; i < dModel; i++) { double v = w[i]; sumsq += v * v; }
                    n += dModel;
                }
            double M = n > 0 && sumsq > 0 ? Math.Sqrt(sumsq / n) : 1.0;

            var wb = new SubstrateChangeBuilder(_source, "model/norm-witnesses",
                entityCapacity: normNames.Count, physicalityCapacity: 0, attestationCapacity: 0);
            foreach (var (_, layer, slot) in normNames)
                wb.AddEntity(new EntityRow(Witness(roles.Count + slot, layer), 0, _witnessType, _source));
            yield return wb.Build();

            foreach (var (name, layer, slot) in normNames)
            {
                if (!_refMap.TryGetValue(name, out _)) continue;
                var w = WeightTensorETL.LoadTensorF32(_refMap, name, dModel);
                Hash128 wit = Witness(roles.Count + slot, layer);
                var b = NewChunk("NORMALIZES", layer, wit);
                for (int i = 0; i < dModel; i++)
                {
                    if (w[i] == 0f) continue;
                    b.AddAttestation(AttestationFactory.CreateWeighted(
                        Axis("channel", i), ModelDecomposer.NormalizesKind, obj: null, _source,
                        contextId: wit, kindRank: KindRank.TensorCalculation,
                        sourceTrust: ModelTrust, magnitude: w[i], arenaScale: M));
                    emitted++;
                }
                yield return b.Build();
            }
        }

        _log.LogInformation("phase=etl done: {N:N0} matches loaded ({Z:N0} zero non-events) in {S:F0}s",
            emitted, zeros, sw.Elapsed.TotalSeconds);
    }

    /// <summary>The role's tensor instances: (name, layer). Layer = -1 for the
    /// global tables (embed/lm_head — one position).</summary>
    private IEnumerable<(string Name, int Layer)> Instances(Role role)
    {
        if (role.GlobalName is not null) { yield return (role.GlobalName, -1); yield break; }
        for (int l = 0; l < _recipe.NumLayers; l++)
            yield return (ArchitectureProfile.Layer(role.PerLayerTemplate!, l), l);
    }

    /// <summary>Norm positions: (name, layer, slot). Slot = which norm template
    /// within the layer (Llama: 0 = input, 1 = post-attn; final norm = next slot)
    /// — distinct positions get distinct witnesses (slot offsets past roles.Count).</summary>
    private IEnumerable<(string Name, int Layer, int Slot)> NormInstances(ArchitectureProfile p)
    {
        for (int l = 0; l < _recipe.NumLayers; l++)
            for (int t = 0; t < p.PerLayerNorms.Count; t++)
                yield return (ArchitectureProfile.Layer(p.PerLayerNorms[t], l), l, t);
        yield return (p.FinalNorm, -1, p.PerLayerNorms.Count);
    }

    /// <summary>Witness id per (role-ordinal, layer position) — provenance of
    /// WHICH position testified; never part of relation identity.</summary>
    private Hash128 Witness(int roleOrdinal, int layer)
    {
        Span<byte> b = stackalloc byte[16 + 4 + 4];
        _source.WriteBytes(b.Slice(0, 16));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.Slice(16, 4), roleOrdinal);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.Slice(20, 4), layer);
        return Hash128.Blake3(b);
    }

    private SubstrateChangeBuilder NewChunk(string roleName, int layer, Hash128 witness)
    {
        var b = new SubstrateChangeBuilder(_source, $"etl/{roleName}/L{layer}", null,
            entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: RowsPerChange);
        b.AddEntity(new EntityRow(witness, 0, _witnessType, _source));
        return b;
    }
}
