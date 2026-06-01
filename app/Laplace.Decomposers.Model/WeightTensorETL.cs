using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;    // QkPair, NativeInterop
using Laplace.SubstrateCRUD;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Universal weight-tensor ETL per ADR 0056. One algorithm; per-architecture-family
/// data drives the per-tensor reduction (math_function + kind_id + subject/object
/// axis mapping). Per ADR 0056:153 the per-family table is *data registered on
/// architecture-template entities*, not code per family. For Stream B-minimum this
/// file ships the Llama-family registry as a static dictionary; Stream B-complete
/// migrates that into substrate `TENSOR_NAME_MEANS_MECHANICAL_ROLE` attestations
/// per ADR 0043:66.
///
/// Phase 1: per-(i, j) matchup via spec.math_function on the tensor.
/// Phase 2: within-model aggregation (sum) across (layer, head, expert) instances
///          → one accumulator entry per (subject, kind, object).
/// Phase 3: lottery-ticket sparsity — per-kind top-k by aggregate magnitude.
/// Phase 5: emit one AttestationRow per (subject, kind, object, source) tuple
///          via AttestationFactory.Create with KindRank.TensorCalculation +
///          SourceTrust.AiModelProbe priors. context = NULL per the ADR 0056
///          amendment + GLOSSARY explicit rule (per-position is recipe content).
///
/// Phase 4 (static-mathematical retention validator) is deferred; the per-kind
/// top-k acts as the noise floor for Stream B-minimum.
///
/// What this ETL is NOT (per ADR 0056 lines 217-225):
///   - container decomposition (ADR 0055 IContainerParser does that)
///   - dtype decoding (ADR 0043 TensorDtypeDecoder does that)
///   - vocab ingest (ADR 0043 ModalityBinder does that, BEFORE this runs)
///   - hashing (ADR 0048 HashComposer does that, BEFORE this runs)
///   - DB writes (ADR 0050 SubstrateCRUD does that, AFTER this yields)
///   - running the model (ADR 0055 — substrate doesn't load + doesn't execute)
/// </summary>
public sealed class WeightTensorETL
{
    private const int    AttBatchSize = 4096;

    /* QK noise floor — SEPARATE from the unary NoiseFloor (1e-9). Pre-softmax q·k scores
     * are dense-but-tiny (TinyLlama L0: |q·k| median 2e-3, p99 1.7e-2, max 0.14); at 1e-9
     * ~99% of pairs survive (~1B unique relations — infeasible to store), with no natural
     * gap. Attention is sparse in EFFECT (softmax concentrates on few keys), so low scores
     * are genuine non-relationships. 0.05 keeps the meaningful attention tail. This floor
     * (B) makes QK storable; the spatial-indexed kernel (A) finds these survivors
     * sub-quadratically instead of scanning all pairs. 0.05 is the default; raising it
     * (LAPLACE_QK_FLOOR) keeps only the strongest attention edges — a noise-floor
     * threshold (NOT top-k) that cuts pair volume so ingest finishes fast and the
     * substrate stays tractable. Tunable without a rebuild, like LAPLACE_OVFFN_FLOOR. */
    private static readonly double QkNoiseFloor =
        double.TryParse(Environment.GetEnvironmentVariable("LAPLACE_QK_FLOOR"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var _qkf) && _qkf > 0.0
            ? _qkf : 0.05;

    /* Noise floor: attestations with aggregate magnitude at or below this
     * value are not real observations — they are gradient jitter or training
     * artifacts that did not fire. True zeros are faster in every downstream
     * computation than near-zero floats, and the substrate should not record
     * non-relationships. The lottery ticket is what survives multi-model
     * Glicko-2 consensus, not a pre-selected top-k percentage from one model. */
    private const double NoiseFloor   = 1e-9;

    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly LlamaWeightExtractor.KindIds _kinds;
    private readonly Hash128 _sourceId;
    private readonly IReadOnlyList<SafetensorsContainerParser.TensorReference> _refs;
    private readonly ILogger _log;

    public WeightTensorETL(
        string modelDir,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId,
        LlamaWeightExtractor.KindIds kinds,
        ILogger? log = null)
    {
        _recipe          = recipe;
        _tokens          = tokens;
        _sourceId        = sourceId;
        _kinds           = kinds;
        _log             = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        // Sharded-aware: union of every *.safetensors shard; each tensor carries its FilePath.
        _refs            = SafetensorsContainerParser.ParseModel(modelDir);
    }

    /// <summary>
    /// Aligned ingest (SUBSTRATE-FOUNDATION truth #3): morph <c>embed_tokens</c> onto the
    /// shared S³ Unicode frame and emit one Projection physicality per token. Streams the
    /// embedding table (truth #1), never a recompute. Replaces the per-circuit Score(t,s)
    /// in <see cref="ExtractAsync"/>; the interior q/k/v/o/gate/up/down resolution is OPEN
    /// (foundation doc) and intentionally NOT emitted — flagged, not fabricated.
    /// </summary>
    public async IAsyncEnumerable<SubstrateChange> EmitS3MorphAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
            _refs.Count, StringComparer.Ordinal);
        foreach (var r in _refs) refMap[r.Name] = r;
        int vocabSize = _recipe.VocabSize, dModel = _recipe.HiddenSize;
        float[] embed = LoadRawBF16AsF32(refMap, "model.embed_tokens.weight",
            (long)vocabSize * dModel);
        var morph = new TokenS3Morph(embed, vocabSize, dModel, _tokens, _sourceId, _log);
        foreach (var change in morph.Emit())
        {
            ct.ThrowIfCancellationRequested();
            yield return change;
            await Task.Yield();
        }
    }

    /// <summary>
    /// ALL interior circuits as relations through the embedding ADDRESS BOOK — O(params),
    /// streamed, GENERIC across architectures (circuits detected by <see cref="ModelGeometry"/>
    /// from tensor shape, no per-family code).
    ///
    /// Address book (read the embedding ONCE): <c>addr[m]</c> = the token dimension m points to
    /// (argmax_t |E[t,m]|); <c>addrOut</c> likewise from the unembedding. Then each weight CELL
    /// W[h,m] resolves its d_model axis m → token addr[m] — a LOOKUP, never a vocab projection.
    /// Per hidden unit h of a detected circuit (encode·decode pair):
    ///   keys = { addr[m]    : |encode[h,m]| &gt; floor }   (the encode ROW h)     — the context n-gram,
    ///   vals = { addrOut[m] : |decode[m,h]| &gt; floor }   (the decode COLUMN h)   — the related tokens.
    /// keys → one content-addressed n-gram trajectory entity; attest the circuit's kind to each
    /// val (signed Glicko), context = the (layer,head) witness. Cost = O(H·d_model) per circuit
    /// = O(params); NO E·W projection (that was O(vocab·params) — the slow path), NO vocab² pairs.
    /// </summary>
    public async IAsyncEnumerable<SubstrateChange> EmitCircuitMemoriesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
            _refs.Count, StringComparer.Ordinal);
        foreach (var r in _refs) refMap[r.Name] = r;

        int vocab = _recipe.VocabSize, dModel = _recipe.HiddenSize, interm = _recipe.IntermediateSize;
        int nHeads = _recipe.NumHeads, nKv = _recipe.NumKvHeads, headDim = dModel / nHeads;
        int qPerKv = nHeads / Math.Max(1, nKv);

        var tokById = new LlamaTokenizerParser.TokenRecord?[vocab];
        foreach (var rec in _tokens)
            if (rec.TokenId >= 0 && rec.TokenId < vocab) tokById[rec.TokenId] = rec;

        // Address book(s) — ONE pass over the embedding / unembedding. addrIn restricted to
        // content-anchored tokens (keys become n-gram constituents); addrOut unrestricted.
        var geo = ModelGeometry.Detect(vocab, dModel, nHeads, nKv, headDim, interm, _recipe.NumLayers,
            _refs.Select(r => (r.Name, r.Shape)));
        string embedName  = geo.EmbeddingName  ?? "model.embed_tokens.weight";
        string unembName  = geo.UnembeddingName ?? embedName;
        float[] E   = LoadRawBF16AsF32(refMap, embedName, (long)vocab * dModel);
        float[] E_U = unembName != embedName ? LoadRawBF16AsF32(refMap, unembName, (long)vocab * dModel) : E;
        int[] addrIn  = BuildAddressBook(E,   vocab, dModel, tokById, requireContentCoord: true);
        int[] addrOut = ReferenceEquals(E_U, E) ? addrIn
                      : BuildAddressBook(E_U, vocab, dModel, tokById, requireContentCoord: false);

        // Floor is detected PER TENSOR by the model inspector (engine detect_energy_floor):
        // the energy-retention floor from each tensor's OWN magnitude distribution — keep the
        // high-energy (lottery-ticket) subnetwork, drop the noise tail; model-agnostic, no
        // hardcoded value. LAPLACE_CIRCUIT_ENERGY (default 0.99) = retain target;
        // LAPLACE_CIRCUIT_FLOOR, if set, is an absolute override (0 = keep all). Above the
        // floor everything is ranked by μ, never pre-judged.
        double energy = 0.99;
        if (double.TryParse(Environment.GetEnvironmentVariable("LAPLACE_CIRCUIT_ENERGY"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var en) && en > 0 && en <= 1)
            energy = en;
        bool floorOverride = double.TryParse(Environment.GetEnvironmentVariable("LAPLACE_CIRCUIT_FLOOR"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ff) && ff >= 0;

        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var counters = new long[2];   // [0]=memories, [1]=relations

        // Witness entities — emitted ONCE so every attestation's context_id FK resolves.
        // DRIVEN BY the detected circuits so the witness tuple (source, layer, head, expert)
        // matches what the attestation loop references exactly: attention (QK/OV) → one witness
        // per (layer, head) with expert = -1 (attention is shared, not per-expert); FFN → one
        // witness per (layer, expert) with head = -1 (MoE: a distinct witness per expert; dense:
        // expert = -1). De-duped because QK and OV of the same head share a witness.
        {
            var wb = new SubstrateChangeBuilder(_sourceId, "model/witnesses",
                entityCapacity: geo.Circuits.Count * (nHeads + 1), physicalityCapacity: 0, attestationCapacity: 0);
            var seenWit = new HashSet<Hash128>();
            foreach (var circ in geo.Circuits)
            {
                if (circ.Kind == ModelGeometry.CircuitKind.FFN)
                {
                    var id = WitnessId(_sourceId, circ.Layer, -1, circ.Expert);
                    if (seenWit.Add(id)) wb.AddEntity(new EntityRow(id, 0, _kinds.WitnessType, _sourceId));
                }
                else
                {
                    for (int h = 0; h < nHeads; h++)
                    {
                        var id = WitnessId(_sourceId, circ.Layer, h, circ.Expert);
                        if (seenWit.Add(id)) wb.AddEntity(new EntityRow(id, 0, _kinds.WitnessType, _sourceId));
                    }
                }
            }
            yield return wb.Build();
        }
        _log.LogInformation("phase=circuits: {N} circuits detected (generic, shape-based); address book built ({Ms} ms)",
            geo.Circuits.Count, sw.ElapsedMilliseconds);

        var keyMap = new Dictionary<Hash128, (LlamaTokenizerParser.TokenRecord rec, double mag)>(128);
        var valMap = new Dictionary<Hash128, (LlamaTokenizerParser.TokenRecord rec, double mag)>(128);
        var keys   = new List<(NgramTrajectory.Constituent c, double mag, byte tier)>(128);
        // Per-WITNESS client-side fold: distinct n-gram entities + relations folded by identity,
        // counting occurrences as Glicko games (observation_count). One folded change per witness
        // → no duplicate rows written, the DB never parses a dup flood. Cross-witness fold is the
        // consensus layer's job.
        var entMap = new Dictionary<Hash128, (EntityRow e, PhysicalityRow p)>(512);
        var relMap = new Dictionary<Hash128, (AttestationRow row, long count)>(2048);

        foreach (var circ in geo.Circuits)
        {
            ct.ThrowIfCancellationRequested();
            var encRef = refMap[circ.Encode]; var decRef = refMap[circ.Decode];
            float[] enc = LoadRawBF16AsF32(refMap, circ.Encode, (long)encRef.Shape[0] * encRef.Shape[1]);
            float[] dec = LoadRawBF16AsF32(refMap, circ.Decode, (long)decRef.Shape[0] * decRef.Shape[1]);
            double keyFloor = floorOverride ? ff : DetectFloor(enc, energy);
            double valFloor = floorOverride ? ff : DetectFloor(dec, energy);
            double mScale   = MeasureRms(dec);   // measured arena scale M for ½(1+tanh(signed_m/M))
            Hash128 kind = circ.Kind switch {
                ModelGeometry.CircuitKind.QK => _kinds.Attends,
                ModelGeometry.CircuitKind.OV => _kinds.OvRelates,
                _                            => _kinds.CompletesTo };

            if (circ.Kind == ModelGeometry.CircuitKind.FFN)
            {
                Hash128 wit = WitnessId(_sourceId, circ.Layer, -1, circ.Expert);
                entMap.Clear(); relMap.Clear();
                for (int i = 0; i < interm; i++)
                    AccumulateUnit(enc, encRef.Shape, i, dec, decRef.Shape, i, addrIn, addrOut,
                        keyFloor, valFloor, mScale, kind, wit, dModel, tokById, keyMap, valMap, keys, entMap, relMap, nowUs, counters);
                var chg = BuildWitnessChange(entMap, relMap);
                if (chg is not null) { yield return chg; await Task.Yield(); }
            }
            else  // attention: one witness per (layer, head); fold its headDim units
            {
                for (int h = 0; h < nHeads; h++)
                {
                    Hash128 wit = WitnessId(_sourceId, circ.Layer, h, circ.Expert);
                    int kvHead = h / qPerKv;
                    entMap.Clear(); relMap.Clear();
                    for (int d = 0; d < headDim; d++)
                    {
                        int encUnit, decUnit;
                        if (circ.Kind == ModelGeometry.CircuitKind.QK) { encUnit = h * headDim + d; decUnit = kvHead * headDim + d; }
                        else                                           { encUnit = kvHead * headDim + d; decUnit = h * headDim + d; }
                        AccumulateUnit(enc, encRef.Shape, encUnit, dec, decRef.Shape, decUnit, addrIn, addrOut,
                            keyFloor, valFloor, mScale, kind, wit, dModel, tokById, keyMap, valMap, keys, entMap, relMap, nowUs, counters);
                    }
                    var chg = BuildWitnessChange(entMap, relMap);
                    if (chg is not null) { yield return chg; await Task.Yield(); }
                }
            }
            _log.LogInformation("phase=circuits L{Layer} {Kind} (keyFloor={Kf:E2} valFloor={Vf:E2}): "
                + "{Mem:N0} memories, {Rel:N0} relations, wall {Ms} ms",
                circ.Layer, circ.Kind, keyFloor, valFloor, counters[0], counters[1], sw.ElapsedMilliseconds);
        }
        _log.LogInformation("phase=circuits done: {Mem:N0} memories, {Rel:N0} relations, {Ms} ms",
            counters[0], counters[1], sw.ElapsedMilliseconds);
    }

    /* Per-tensor model-inspector floor: the engine's energy-retention detector over this tensor's
     * own magnitude distribution. Model-agnostic — no hardcoded value. */
    private static unsafe double DetectFloor(float[] w, double energy)
    {
        fixed (float* p = w) return DynInterop.DetectEnergyFloor(p, (nuint)w.LongLength, energy);
    }

    /* Witness weight for a model circuit = kind_rank (tensor-calculation ≈ 0.27)
     * × source_trust (AI-model probe 0.50). A model is a low-trust, high-volume
     * witness; consensus accumulates many of them. A NUMBER folded into Glicko
     * opponent φ — never a tier, never a μ multiplier (ARCHITECTURE.md §10). */
    private const double ModelWitnessWeight = 0.27 * 0.50;

    /* Measured per-arena magnitude scale M for the signed Glicko outcome
     * ½(1+tanh(m/M)): the RMS (≈ σ for zero-mean weights) of the tensor's own
     * cells. DERIVED from the arena's distribution at ingest — never a knob. */
    private static double MeasureRms(float[] w)
    {
        long n = w.LongLength;
        if (n == 0) return 1.0;
        double s = 0.0;
        for (long i = 0; i < n; i++) { double v = w[i]; s += v * v; }
        double rms = Math.Sqrt(s / n);
        return rms > 0.0 ? rms : 1.0;
    }

    /* Build one folded SubstrateChange for a witness: distinct n-gram entities + relations
     * folded by identity with their occurrence/game count as observation_count. */
    private SubstrateChange? BuildWitnessChange(
        Dictionary<Hash128, (EntityRow e, PhysicalityRow p)> entMap,
        Dictionary<Hash128, (AttestationRow row, long count)> relMap)
    {
        if (relMap.Count == 0) return null;
        var bb = new SubstrateChangeBuilder(_sourceId, "circuit-mem",
            entityCapacity: entMap.Count, physicalityCapacity: entMap.Count, attestationCapacity: relMap.Count);
        foreach (var kv in entMap) { bb.AddEntity(kv.Value.e); bb.AddPhysicality(kv.Value.p); }
        foreach (var kv in relMap)
            bb.AddAttestation(kv.Value.count == 1 ? kv.Value.row
                                                  : kv.Value.row with { ObservationCount = kv.Value.count });
        return bb.Build();
    }

    /* One hidden unit of a circuit → its [n-gram]⇒{tokens} memory, FOLDED into the witness maps
     * (not emitted standalone). Reads the encode ROW (or COLUMN, by shape) at encUnit → keys via
     * the address book; the decode at decUnit → vals. The n-gram entity dedups by content id;
     * each completion folds by attestation id, incrementing the occurrence/game count. No-op when
     * the unit has no keys or vals. keyMap/valMap/keys are caller-owned scratch (cleared here). */
    private void AccumulateUnit(
        float[] enc, int[] encShape, int encUnit,
        float[] dec, int[] decShape, int decUnit,
        int[] addrIn, int[] addrOut, double keyFloor, double valFloor, double mScale,
        Hash128 kind, Hash128 witness, int dModel,
        LlamaTokenizerParser.TokenRecord?[] tokById,
        Dictionary<Hash128, (LlamaTokenizerParser.TokenRecord rec, double mag)> keyMap,
        Dictionary<Hash128, (LlamaTokenizerParser.TokenRecord rec, double mag)> valMap,
        List<(NgramTrajectory.Constituent c, double mag, byte tier)> keys,
        Dictionary<Hash128, (EntityRow e, PhysicalityRow p)> entMap,
        Dictionary<Hash128, (AttestationRow row, long count)> relMap,
        long nowUs, long[] counters)
    {
        ReadHalf(enc, encShape, encUnit, dModel, addrIn, addrOut, tokById, keyFloor, keyMap);
        if (keyMap.Count == 0) return;
        keys.Clear();
        byte maxTier = 0;
        foreach (var kv in keyMap)
        {
            var rec = kv.Value.rec;
            if (!rec.HasContentCoord) continue;   // keys must be placeable n-gram constituents
            keys.Add((new NgramTrajectory.Constituent(
                rec.EntityId, rec.ContentX, rec.ContentY, rec.ContentZ, rec.ContentM), kv.Value.mag, rec.Tier));
            if (rec.Tier > maxTier) maxTier = rec.Tier;
        }
        if (keys.Count == 0) return;

        ReadHalf(dec, decShape, decUnit, dModel, addrIn, addrOut, tokById, valFloor, valMap);
        if (valMap.Count == 0) return;

        // CONTENT order — identity is the token set, NEVER the source's weight magnitudes (that
        // poisoned the id and destroyed dedup). Magnitude survives only as the completion's μ.
        keys.Sort((a, b) => a.c.Id.CompareToBytewise(b.c.Id));
        var cons = new NgramTrajectory.Constituent[keys.Count];
        for (int k = 0; k < keys.Count; k++) cons[k] = keys[k].c;
        byte ngramTier = (byte)Math.Min(255, maxTier + 1);

        var (ngramId, entity, phys) = NgramTrajectory.Compose(cons, ngramTier, _kinds.NgramType, _sourceId, nowUs);
        if (entMap.TryAdd(ngramId, (entity, phys))) counters[0]++;   // new distinct n-gram memory

        foreach (var v in valMap)
        {
            // One Glicko OBSERVATION (ARCHITECTURE.md §10): score = ½(1+tanh(signed_m/M))
            // from the SIGNED coupling (a negative decode weight is a refute → loss → score<½,
            // never abs'd away), M = the measured arena scale; witness weight (kind_rank ×
            // source_trust) → opponent φ at consensus. NO tier, NO μ seeded from magnitude.
            var row = AttestationFactory.CreateObservation(
                ngramId, kind, v.Key, _sourceId, contextId: witness,
                signedMagnitude: v.Value.mag, arenaScale: mScale, witnessWeight: ModelWitnessWeight);
            if (relMap.TryGetValue(row.Id, out var acc)) relMap[row.Id] = (acc.row, acc.count + 1);
            else { relMap[row.Id] = (row, 1); counters[1]++; }   // distinct relation; count = games
        }
    }

    /* Read one hidden unit's d_model-axis weights and resolve each to a token via the address
     * book — the O(d_model) core of the streaming read. shape [H, d_model] ⇒ ROW `unit` (addrIn);
     * shape [d_model, H] ⇒ COLUMN `unit` stride H (addrOut). Dedup tokens (a dim's argmax token
     * can repeat) keeping the max-|magnitude| occurrence. Fills `outMap`. */
    private static void ReadHalf(
        float[] w, int[] shape, int unit, int dModel, int[] addrIn, int[] addrOut,
        LlamaTokenizerParser.TokenRecord?[] tokById, double floor,
        Dictionary<Hash128, (LlamaTokenizerParser.TokenRecord rec, double mag)> outMap)
    {
        outMap.Clear();
        bool encode = shape.Length == 2 && shape[1] == dModel;   // [H, d_model] row; else [d_model, H] col
        int H = encode ? 0 : shape[1];
        int[] addr = encode ? addrIn : addrOut;
        for (int m = 0; m < dModel; m++)
        {
            double v = encode ? w[(long)unit * dModel + m] : w[(long)m * H + unit];
            if (Math.Abs(v) <= floor) continue;
            int t = addr[m];
            if (t < 0) continue;
            var rec = tokById[t];
            if (rec is null) continue;
            if (outMap.TryGetValue(rec.EntityId, out var ex))
            {
                if (Math.Abs(v) > Math.Abs(ex.mag)) outMap[rec.EntityId] = (rec, v);
            }
            else outMap[rec.EntityId] = (rec, v);
        }
    }

    /* Address book: per d_model dimension m, the token it points to (argmax_t |E[t,m]|), read in
     * ONE row-major pass over the embedding (O(vocab·d_model)). requireContentCoord restricts the
     * key side to placeable tokens. -1 = no token resolves that dimension. */
    private static int[] BuildAddressBook(
        float[] E, int vocab, int dModel,
        LlamaTokenizerParser.TokenRecord?[] tokById, bool requireContentCoord)
    {
        var addr = new int[dModel];
        var best = new double[dModel];
        for (int m = 0; m < dModel; m++) { addr[m] = -1; best[m] = -1.0; }
        for (int t = 0; t < vocab; t++)
        {
            var rec = tokById[t];
            if (rec is null) continue;
            if (requireContentCoord && !rec.HasContentCoord) continue;
            long off = (long)t * dModel;
            for (int m = 0; m < dModel; m++)
            {
                double a = Math.Abs(E[off + m]);
                if (a > best[m]) { best[m] = a; addr[m] = t; }
            }
        }
        return addr;
    }

    /* Noise floor for a weight tensor: the (1-surviveFrac) quantile of |cell| over a sampled
     * stride. A noise-floor threshold (keeps the surviveFrac strongest cells), NOT top-k. */
    private static double CalibrateWeightFloor(float[] w, double surviveFrac)
    {
        long n = w.LongLength;
        if (n == 0) return 0.0;
        int target = (int)Math.Min(1_000_000L, n);
        long stride = Math.Max(1L, n / target);
        var mags = new List<double>(target + 16);
        for (long i = 0; i < n; i += stride) mags.Add(Math.Abs(w[i]));
        mags.Sort();
        int idx = (int)((1.0 - surviveFrac) * (mags.Count - 1));
        return mags[Math.Clamp(idx, 0, mags.Count - 1)];
    }

    /* Per-(layer, head, expert) witness id — the model-circuit provenance carried as the
     * attestation context_id. head = -1 is the layer's FFN witness; expert = -1 is dense /
     * shared attention, expert ≥ 0 is an MoE expert (so two experts in one layer do NOT
     * collapse to one witness). Deterministic per (source, layer, head, expert). */
    private static Hash128 WitnessId(Hash128 source, int layer, int head, int expert)
    {
        Span<byte> b = stackalloc byte[16 + 4 + 4 + 4];
        source.WriteBytes(b.Slice(0, 16));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.Slice(16, 4), layer);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.Slice(20, 4), head);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.Slice(24, 4), expert);
        return Hash128.Blake3(b);
    }

    /* Noise floor for a projection cache [vocab × width]: the (1-surviveFrac) quantile of
     * |cell| over a sampled token stride. A noise-floor threshold (keeps the surviveFrac
     * strongest token↔neuron couplings), NOT a per-neuron top-k. */
    private static double CalibrateProjectionFloor(double[] cache, int vocab, int width, double surviveFrac)
    {
        int sampleT = Math.Min(512, vocab);
        int stride  = Math.Max(1, vocab / sampleT);
        var mags = new List<double>(sampleT * width);
        for (int t = 0; t < vocab; t += stride)
        {
            long off = (long)t * width;
            for (int i = 0; i < width; i++) mags.Add(Math.Abs(cache[off + i]));
        }
        if (mags.Count == 0) return 0.0;
        mags.Sort();
        long idx = (long)((1.0 - surviveFrac) * (mags.Count - 1));
        return mags[(int)Math.Clamp(idx, 0, mags.Count - 1)];
    }

    public async IAsyncEnumerable<SubstrateChange> ExtractAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(
            _refs.Count, StringComparer.Ordinal);
        foreach (var r in _refs) refMap[r.Name] = r;

        int vocabSize = _recipe.VocabSize;
        int dModel    = _recipe.HiddenSize;
        int nHeads    = _recipe.NumHeads;
        int nKvHeads  = _recipe.NumKvHeads;
        int headDim   = dModel / nHeads;
        int interm    = _recipe.IntermediateSize;
        int kvDim     = nKvHeads * headDim;
        int attnOut   = nHeads * headDim;

        // Validate config dims against the ACTUAL embedding tensor shape — per
        // dimension, not just the product LoadRawBF16 checks. Catches an under-
        // specified/mis-parsed config (e.g. vocab & hidden both wrong but product
        // right) before any reduction runs. Fail-loud, faithfulness mandate.
        if (refMap.TryGetValue("model.embed_tokens.weight", out var embedRef) &&
            (embedRef.Shape.Length != 2 || embedRef.Shape[0] != vocabSize || embedRef.Shape[1] != dModel))
        {
            throw new InvalidOperationException(
                $"Recipe dims (vocab={vocabSize}, hidden={dModel}) disagree with " +
                $"model.embed_tokens.weight shape [{string.Join(",", embedRef.Shape)}]. " +
                "Refusing to ingest mismatched geometry.");
        }

        var phase = System.Diagnostics.Stopwatch.StartNew();
        ushort[] E_bf16 = LoadRawBF16(refMap, "model.embed_tokens.weight",
                                       (long)vocabSize * dModel);

        // ─── EMBEDS (unary per token) ───────────────────────────────────
        // ADR 0056:163 — (text_entity, embed_dim), per-cell magnitude, one
        // instance. Reduce embed row to a per-token scalar via L2 norm of
        // the dModel-wide row. Emit one attestation per token, object NULL.
        var embedAccum = ReducePerCellMagnitude(E_bf16, vocabSize, dModel);
        foreach (var c in EmitUnaryBatches(embedAccum, _kinds.Embeds, "embeds"))
        { yield return c; await Task.Yield(); }
        _log.LogInformation("phase=EMBEDS done: {N} tokens ({Ms} ms)", vocabSize, phase.ElapsedMilliseconds);

        // ─── OUTPUT_PROJECTS (unary per token) ──────────────────────────
        // ADR 0056:164 — (hidden_dim, text_entity), per-cell magnitude.
        // For lm_head with shape [vocab, hidden_dim] (Llama convention),
        // reduce per-row to a per-token scalar. Object NULL.
        if (refMap.ContainsKey("lm_head.weight"))
        {
            ushort[] lm = LoadRawBF16(refMap, "lm_head.weight",
                                       (long)vocabSize * dModel);
            var lmAccum = ReducePerCellMagnitude(lm, vocabSize, dModel);
            foreach (var c in EmitUnaryBatches(lmAccum, _kinds.OutputProjects, "output_projects"))
            { yield return c; await Task.Yield(); }
            _log.LogInformation("phase=OUTPUT_PROJECTS done ({Ms} ms)", phase.ElapsedMilliseconds);
        }
        phase.Restart();

        // ─── Q_PROJECTS (binary text×text) — exact, threshold (NOT top-k), streamed ───
        // Temporary isolation gate: LAPLACE_SKIP_QK=1 ingests the (bounded, fast) unary
        // kinds only, to verify the kernel rewrite end-to-end and isolate the O(vocab²)
        // QK cost. Removed once QK scaling is decided.
        if (Environment.GetEnvironmentVariable("LAPLACE_SKIP_QK") != "1")
        {
        // ADR 0056:157 — q_proj[i,:]·k_proj[j,:]ᵀ per (layer, head). The engine kernel
        // emits every |q·k| > NoiseFloor pair in bounded query-row windows; we stream
        // them straight to attestation batches. No cross-layer Dictionary, no vocab×k
        // pinned buffer (the prior 80 GB OOM). Cross-instance consensus is the DB's job
        // (Glicko consensus-upsert chunk); the interim relies on content-addressed
        // dedup at the writer. NOTE: with top-k removed, QK volume is governed solely by
        // NoiseFloor — calibrated empirically against ingest survivor counts.
        float[] E_f32 = LoadRawBF16AsF32(refMap, "model.embed_tokens.weight",
                                          (long)vocabSize * dModel);
        int queriesPerKv = nHeads / Math.Max(1, nKvHeads);
        var qkBuf = new QkPairF64[1 << 20];   // 16 MB bounded scratch, reused
        // Project Q+K through the embedding ONCE per layer for ALL heads (streams E a single
        // time/layer), then score each head from the caches — instead of re-streaming the
        // ~250 MB E once per (head) inside the pruned kernel (~64×/layer of memory traffic).
        // q_cache [vocab][nHeads][headDim], k_cache [vocab][nKv][headDim], f64; allocated
        // ONCE and reused across layers (TinyLlama: q_cache ~512 MB, k_cache ~64 MB). The
        // cache holds the IDENTICAL compensated projections the pruned kernel computes, so
        // per-head scoring (ScoreQkHeadCached) is bit-identical to the per-head pruned kernel.
        var qCache = new double[(long)vocabSize * nHeads   * headDim];
        var kCache = new double[(long)vocabSize * nKvHeads * headDim];
        // Diagnostic: LAPLACE_QK_BENCH=1 runs the kernel for every head and accumulates
        // native compute time + pair counts but SKIPS emit/marshal/DB — isolates the
        // native projection+scoring cost from the managed pipeline.
        bool qkBench = Environment.GetEnvironmentVariable("LAPLACE_QK_BENCH") == "1";
        var kernelSw = new System.Diagnostics.Stopwatch();
        long kernelPairs = 0; long kernelCalls = 0;
        for (int layer = 0; layer < _recipe.NumLayers; layer++)
        {
            ct.ThrowIfCancellationRequested();
            string qName = $"model.layers.{layer}.self_attn.q_proj.weight";
            string kName = $"model.layers.{layer}.self_attn.k_proj.weight";
            if (!refMap.ContainsKey(qName) || !refMap.ContainsKey(kName)) continue;
            float[] qW = LoadRawBF16AsF32(refMap, qName, (long)nHeads   * headDim * dModel);
            float[] kW = LoadRawBF16AsF32(refMap, kName, (long)nKvHeads * headDim * dModel);
            // Stream E once: project all heads' Q + all kv heads' K into the caches.
            kernelSw.Start();
            ProjectLayerQk(E_f32, vocabSize, dModel, qW, nHeads, kW, nKvHeads, headDim,
                           qCache, kCache);
            kernelSw.Stop();
            for (int h = 0; h < nHeads; h++)
            {
                int kvHead = h / queriesPerKv;
                int q0 = 0;
                while (q0 < vocabSize)
                {
                    // One call per head normally (whole vocab) so the cached scorer builds
                    // its key-norm table ONCE; the overflow path shrinks the window only if
                    // a head's survivors ever exceed the buffer (rare at this floor).
                    int win = vocabSize - q0;
                    long n;
                    int overflow;
                    while (true)
                    {
                        kernelSw.Start();
                        n = QkWindowCached(qCache, nHeads, kCache, nKvHeads, vocabSize, headDim,
                                           h, kvHead, QkNoiseFloor, q0, q0 + win, qkBuf, out overflow);
                        kernelSw.Stop();
                        kernelCalls++;
                        if (overflow == 0) break;
                        win = Math.Max(1, win / 2);   // shrink window until the batch fits
                    }
                    kernelPairs += n;
                    if (!qkBench)
                        foreach (var c in EmitQkBatches(qkBuf, (int)n)) yield return c;
                    await Task.Yield();
                    q0 += win;
                }
            }
            _log.LogInformation(
                "phase=QK layer {Layer}/{Total}: kernel {KMs} ms cumulative ({Calls} calls), "
                + "{Pairs:N0} pairs above floor, wall {WallMs} ms{Bench}",
                layer + 1, _recipe.NumLayers, kernelSw.ElapsedMilliseconds, kernelCalls,
                kernelPairs, phase.ElapsedMilliseconds, qkBench ? " [BENCH: emit skipped]" : "");
        }
        _log.LogInformation("phase=QK done: {KMs} ms kernel, {Pairs:N0} pairs above floor, {WallMs} ms wall",
            kernelSw.ElapsedMilliseconds, kernelPairs, phase.ElapsedMilliseconds);

        } // end QK isolation gate (LAPLACE_SKIP_QK)
        phase.Restart();

        // ─── OV + FFN circuits — exact token×token BILINEAR through the embedding ───
        // Every interior weight tensor is a token×token bilinear via the embedding; QK
        // is done that way above, OV and FFN are done the same way here, REUSING the QK
        // projection + cached-scoring kernels (no new native kernel). The former per-token
        // MAGNITUDE reduction of v/o/gate/up/down is GONE — it collapsed the dim axis to a
        // scalar (destroying the relation structure). Now:
        //
        //   OV (per layer L, per attention head h):
        //     A_h[t,:] = E[t,:]·Wv_h        (E    through v_proj head h → [vocab × headDim])
        //     C_h[s,:] = E_U[s,:]·Wo_h      (E_U  through o_proj head h → [vocab × headDim])
        //     Score(t,s) = Σ_d A_h[t,d]·C_h[s,d]  over headDim;  emit pairs > floor.
        //   GQA: head h's V side uses kv head g = h/(nHeads/nKvHeads); the O side is per
        //   query head h. So A is projected once per kv head (nKvHeads slices) and scored
        //   as the QUERY side at slice g; C is projected per query head (nHeads slices) and
        //   scored as the KEY side at slice h. Both caches are reused across all layers.
        //
        //   FFN (per layer L):
        //     A[t,:] = E[t,:]·Wup           (E    through up_proj   → [vocab × interm])
        //     C[s,:] = E_U[s,:]·Wdown       (E_U  through down_proj → [vocab × interm])
        //     Score over interm; emit pairs > floor. interm (~5632) >> headDim (64) ⇒ caches
        //   are large and scoring is heavier, so the same bounded q0..q1 windowing the QK
        //   path uses (overflow → shrink) is applied. The SiLU/GELU gate is data-dependent
        //   (runtime), so it is NOT attested — only the static up→down skeleton is.
        //   gate_proj is therefore not projected here.
        //
        // KIND VOCABULARY: one kind per math circuit. OV pairs emit under O_PROJECTS (the
        // OV output); FFN pairs emit under DOWN_PROJECTS (the FFN output). V_PROJECTS,
        // GATES and UP_PROJECTS remain bootstrapped (ModelDecomposer) but are NO LONGER
        // EMITTED — they are sub-parts of the OV / FFN circuits, not standalone relations.
        //
        // E_U = lm_head.weight as f32; if absent (tied embeddings) E_U = E (the embedding).
        //
        // FLOOR — MEASURED, ESCALATED: the OV/FFN bilinear has a DIFFERENT magnitude scale
        // than QK. On TinyLlama L0, OV head-0 |score| max ≈ 8.2e-4 / mean ≈ 9.1e-5 (64×64
        // probe), FFN similar — vs the QkNoiseFloor 0.05 calibrated for pre-softmax q·k
        // (median 2e-3, max 0.14). At 0.05 the OV/FFN circuits emit ~zero pairs (the floor
        // sits ~60× above their max). The circuits are CORRECT (projections + bilinear are
        // real, nonzero, structured); only the floor is mis-scaled for them. Per-circuit
        // floor calibration is a substrate-policy decision for the user, NOT for this code
        // to guess — so the default stays QkNoiseFloor and LAPLACE_OVFFN_FLOOR overrides it
        // (e.g. 1e-4) for empirical calibration without a rebuild. Set it before declaring
        // OV/FFN ingest "done".
        if (Environment.GetEnvironmentVariable("LAPLACE_SKIP_OVFFN") != "1")
        {
        // Per-circuit noise floor. QK |score| peaks ~0.14, OV/FFN ~8e-4 — a single
        // global floor either floods or emits ~zero (see note above). Default: calibrate
        // the floor to each circuit's OWN magnitude spectrum (sampled once per circuit
        // type) so significance adapts per tensor. An explicit LAPLACE_OVFFN_FLOOR still
        // overrides (skips calibration). LAPLACE_SURVIVE_FRAC is the one policy knob:
        // fraction of sampled pairs kept (default 5e-4).
        bool floorOverridden = double.TryParse(
                Environment.GetEnvironmentVariable("LAPLACE_OVFFN_FLOOR"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f) && f >= 0.0;
        double ovffnFloor = floorOverridden ? f : QkNoiseFloor;
        double surviveFrac = 5e-4;
        if (double.TryParse(Environment.GetEnvironmentVariable("LAPLACE_SURVIVE_FRAC"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var sf) && sf > 0.0 && sf < 1.0)
            surviveFrac = sf;
        double ovFloorCal = double.NaN, ffnFloorCal = double.NaN;

        float[] E_f32_circuit = LoadRawBF16AsF32(refMap, "model.embed_tokens.weight",
                                                  (long)vocabSize * dModel);
        float[] E_U = refMap.ContainsKey("lm_head.weight")
            ? LoadRawBF16AsF32(refMap, "lm_head.weight", (long)vocabSize * dModel)
            : E_f32_circuit;   // tied embeddings: unembedding IS the embedding

        int queriesPerKvOv = nHeads / Math.Max(1, nKvHeads);
        var circBuf = new QkPairF64[1 << 20];   // 16 MB bounded scratch, reused

        bool ovffnBench = Environment.GetEnvironmentVariable("LAPLACE_QK_BENCH") == "1";
        var circSw = new System.Diagnostics.Stopwatch();
        long circPairsOv = 0, circPairsFfn = 0;

        // ── OV caches (reused across all layers) ──
        // A side: E   through v_proj's nKvHeads slices → [vocab][nKvHeads][headDim].
        // C side: E_U through o_proj's nHeads   slices → [vocab][nHeads][headDim].
        var ovA = new double[(long)vocabSize * nKvHeads * headDim];   // ~64 MB
        var ovC = new double[(long)vocabSize * nHeads   * headDim];   // ~512 MB
        // ── FFN caches (reused across all layers), single "head" of width interm ──
        var ffnA = new double[(long)vocabSize * interm];             // ~1.4 GB
        var ffnC = new double[(long)vocabSize * interm];             // ~1.4 GB

        for (int layer = 0; layer < _recipe.NumLayers; layer++)
        {
            ct.ThrowIfCancellationRequested();
            string p = $"model.layers.{layer}.";

            // ───────────── OV circuit ─────────────
            string vName = p + "self_attn.v_proj.weight";
            string oName = p + "self_attn.o_proj.weight";
            if (refMap.ContainsKey(vName) && refMap.ContainsKey(oName))
            {
                // Wv: [nKvHeads*headDim × dModel] (HF output×input) — head g's slice is
                // rows [g*headDim:(g+1)*headDim], already [headDim × dModel]: project_token
                // wants proj[d]=Σ_m E[m]·W[d,m] over m∈dModel → NO transpose.
                float[] vW = LoadRawBF16AsF32(refMap, vName, (long)kvDim * dModel);
                // Wo: [dModel × nHeads*headDim] (HF output×input). The OV circuit needs
                // C_h[s,d] = Σ_{m'} E_U[s,m']·Wo[m', h*headDim+d] (m'∈dModel). project_token
                // wants Wslice[d, m'] = Wo[m', h*headDim+d] → TRANSPOSE the per-head column
                // block of Wo into [headDim × dModel] per head, stacked over nHeads.
                float[] oW   = LoadRawBF16AsF32(refMap, oName, (long)dModel * attnOut);
                float[] oW_T = TransposeOProjToHeads(oW, dModel, nHeads, headDim);

                circSw.Start();
                ProjectThroughHeads(E_f32_circuit, vocabSize, dModel, vW,   nKvHeads, headDim, ovA);
                ProjectThroughHeads(E_U,           vocabSize, dModel, oW_T, nHeads,   headDim, ovC);
                circSw.Stop();

                if (!floorOverridden)
                    ovffnFloor = double.IsNaN(ovFloorCal)
                        ? (ovFloorCal = CalibrateFloor(ovA, nKvHeads, 0, ovC, nHeads, 0, vocabSize, headDim, surviveFrac))
                        : ovFloorCal;

                for (int h = 0; h < nHeads; h++)
                {
                    int kvHead = h / queriesPerKvOv;   // GQA: V side uses the kv head
                    int q0 = 0;
                    while (q0 < vocabSize)
                    {
                        int win = vocabSize - q0;
                        long n; int overflow;
                        while (true)
                        {
                            circSw.Start();
                            // QUERY side = A (nKvHeads, slice kvHead); KEY side = C (nHeads, slice h).
                            n = QkWindowCached(ovA, nKvHeads, ovC, nHeads, vocabSize, headDim,
                                               kvHead, h, ovffnFloor, q0, q0 + win, circBuf, out overflow);
                            circSw.Stop();
                            if (overflow == 0) break;
                            win = Math.Max(1, win / 2);
                        }
                        circPairsOv += n;
                        if (!ovffnBench)
                            foreach (var c in EmitPairBatches(circBuf, (int)n, _kinds.OProjects, "o_projects", ovffnFloor))
                                yield return c;
                        await Task.Yield();
                        q0 += win;
                    }
                }
            }

            // ───────────── FFN circuit ─────────────
            string upName   = p + "mlp.up_proj.weight";
            string downName = p + "mlp.down_proj.weight";
            if (refMap.ContainsKey(upName) && refMap.ContainsKey(downName))
            {
                // Wup: [interm × dModel] (HF output×input) — already [interm × dModel]:
                // A[t,i]=Σ_m E[t,m]·Wup[i,m] → NO transpose. One "head" of width interm.
                float[] upW = LoadRawBF16AsF32(refMap, upName, (long)interm * dModel);
                // Wdown: [dModel × interm] (HF output×input). C[s,i]=Σ_{m'} E_U[s,m']·Wdown[m',i]
                // → Wslice[i,m']=Wdown[m',i]: TRANSPOSE [dModel × interm] → [interm × dModel].
                float[] downW   = LoadRawBF16AsF32(refMap, downName, (long)dModel * interm);
                float[] downW_T = Transpose2D(downW, dModel, interm);

                circSw.Start();
                ProjectThroughHeads(E_f32_circuit, vocabSize, dModel, upW,     1, interm, ffnA);
                ProjectThroughHeads(E_U,           vocabSize, dModel, downW_T, 1, interm, ffnC);
                circSw.Stop();

                if (!floorOverridden)
                    ovffnFloor = double.IsNaN(ffnFloorCal)
                        ? (ffnFloorCal = CalibrateFloor(ffnA, 1, 0, ffnC, 1, 0, vocabSize, interm, surviveFrac))
                        : ffnFloorCal;

                int q0 = 0;
                while (q0 < vocabSize)
                {
                    int win = vocabSize - q0;
                    long n; int overflow;
                    while (true)
                    {
                        circSw.Start();
                        n = QkWindowCached(ffnA, 1, ffnC, 1, vocabSize, interm,
                                           0, 0, ovffnFloor, q0, q0 + win, circBuf, out overflow);
                        circSw.Stop();
                        if (overflow == 0) break;
                        win = Math.Max(1, win / 2);
                    }
                    circPairsFfn += n;
                    if (!ovffnBench)
                        foreach (var c in EmitPairBatches(circBuf, (int)n, _kinds.DownProjects, "down_projects", ovffnFloor))
                            yield return c;
                    await Task.Yield();
                    q0 += win;
                }
            }

            _log.LogInformation(
                "phase=OV+FFN layer {Layer}/{Total} (floor={Floor}): kernel {KMs} ms cumulative, "
                + "OV {Ov:N0} / FFN {Ffn:N0} pairs above floor, wall {WallMs} ms{Bench}",
                layer + 1, _recipe.NumLayers, ovffnFloor, circSw.ElapsedMilliseconds,
                circPairsOv, circPairsFfn, phase.ElapsedMilliseconds,
                ovffnBench ? " [BENCH: emit skipped]" : "");
        }
        _log.LogInformation(
            "phase=OV+FFN done (floor={Floor}): {KMs} ms kernel, OV {Ov:N0} / FFN {Ffn:N0} pairs above floor, {WallMs} ms wall",
            ovffnFloor, circSw.ElapsedMilliseconds, circPairsOv, circPairsFfn, phase.ElapsedMilliseconds);
        } // end OV+FFN gate (LAPLACE_SKIP_OVFFN)

        // ─── NORMALIZES — unary on model recipe entity ──────────────────
        // ADR 0056:165 — unary (hidden_dim,), per-cell magnitude across layers.
        // The per-(layer, role, dim) is recipe content; substrate emission is
        // ONE unary attestation on the model recipe entity carrying the
        // aggregate. Subject = recipe entity, object = NULL.
        double normAggregate = 0.0;
        int normCount = 0;
        for (int layer = 0; layer < _recipe.NumLayers; layer++)
        {
            foreach (var role in new[] {
                $"model.layers.{layer}.input_layernorm.weight",
                $"model.layers.{layer}.post_attention_layernorm.weight"
            })
            {
                if (!refMap.ContainsKey(role)) continue;
                float[] w = LoadRawBF16AsF32(refMap, role, dModel);
                for (int d = 0; d < dModel; d++) normAggregate += Math.Abs(w[d]);
                normCount += dModel;
            }
        }
        if (refMap.ContainsKey("model.norm.weight"))
        {
            float[] w = LoadRawBF16AsF32(refMap, "model.norm.weight", dModel);
            for (int d = 0; d < dModel; d++) normAggregate += Math.Abs(w[d]);
            normCount += dModel;
        }
        if (normCount > 0)
        {
            var b = new SubstrateChangeBuilder(_sourceId, "normalizes",
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 1);
            b.AddAttestation(AttestationFactory.Create(
                subject:   _recipe.RecipeEntityId,
                kindId:    _kinds.Normalizes,
                obj:       null,
                sourceId:  _sourceId,
                contextId: null,
                kindRank:    KindRank.TensorCalculation,
                sourceTrust: SourceTrust.AiModelProbe,
                observationCount: normCount));
            yield return b.Build();
        }

        // K_PROJECTS — Per ADR 0056:157 spec table, transformer attention collapses Q+K
        // joint via Q_PROJECTS; K_PROJECTS kind is bootstrapped for future architectures
        // (encoder-decoder cross-attn etc.) but transformer-family doesn't emit it
        // separately. No emission here for Llama-family.
    }

    /* ----------------------------------------------------------- *
     * Per-cell-magnitude reduction over a [vocab × dim] tensor:
     * returns one scalar per token (L2 norm of the row).
     * Used for EMBEDS + OUTPUT_PROJECTS (one-instance) and as the
     * base per-instance contribution for V/O/G/U/D aggregation.
     * ----------------------------------------------------------- */
    private static unsafe double[] ReducePerCellMagnitude(ushort[] tensorBf16, int rows, int cols)
    {
        // Exact, deterministic, TBB+SIMD engine kernel (Neumaier-compensated f64,
        // fixed column order) — bit-parity verified against this former scalar path.
        var result = new double[rows];
        fixed (ushort* tp = tensorBf16)
        fixed (double* op = result)
        {
            int rc = SynthInterop.ComputePerTokenL2Magnitude(tp, (nuint)rows, (nuint)cols, op);
            if (rc != 0)
                throw new InvalidOperationException($"compute_per_token_l2_magnitude returned {rc}");
        }
        return result;
    }

    /* Emit unary attestations (one per token, object NULL).
     * Records every token whose aggregate magnitude clears the noise floor.
     * Near-zero = no real observation = not recorded (true zero is correct). */
    private IEnumerable<SubstrateChange> EmitUnaryBatches(
        double[] perTokenAccum, Hash128 kindId, string unitName)
    {
        SubstrateChangeBuilder? b = null;
        int inBatch = 0;
        int batchIdx = 0;
        var seen = new HashSet<Hash128>();

        for (int tokenIdx = 0; tokenIdx < perTokenAccum.Length; tokenIdx++)
        {
            if (perTokenAccum[tokenIdx] <= NoiseFloor) continue;
            if (tokenIdx >= _tokens.Count) continue;
            var subj = _tokens[tokenIdx].EntityId;
            var row = AttestationFactory.CreateWeighted(
                subj, kindId, obj: null, _sourceId, contextId: null,
                kindRank: KindRank.TensorCalculation, sourceTrust: SourceTrust.AiModelProbe,
                magnitude: perTokenAccum[tokenIdx], floor: NoiseFloor);
            if (!seen.Add(row.Id)) continue;

            b ??= new SubstrateChangeBuilder(_sourceId,
                $"{unitName}/batch-{batchIdx}",
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: AttBatchSize);
            b.AddAttestation(row);
            if (++inBatch >= AttBatchSize) { yield return b.Build(); b = null; inBatch = 0; batchIdx++; }
        }
        if (b != null) yield return b.Build();
    }

    /* Emit binary attestations (subject=token_i, object=token_j) for Q_PROJECTS.
     * Records every pair whose aggregate magnitude clears the noise floor —
     * positive (attends-to) and negative (repels) alike. Near-zero = no real
     * observation = not recorded. The lottery ticket is what survives
     * multi-model Glicko-2 consensus when additional models are ingested. */
    /* Stream above-threshold QK pairs (from a kernel window) as Q_PROJECTS attestation
     * batches. No C# dedup set — that would be unbounded; content-addressed dedup /
     * consensus is the writer's job. */
    private IEnumerable<SubstrateChange> EmitQkBatches(QkPairF64[] buf, int n)
    {
        SubstrateChangeBuilder? bb = null;
        int inBatch = 0, batchIdx = 0;
        int tokCount = _tokens.Count;
        for (int i = 0; i < n; i++)
        {
            uint qi = buf[i].QueryIdx, kj = buf[i].KeyIdx;
            if (qi >= (uint)tokCount || kj >= (uint)tokCount) continue;
            var row = AttestationFactory.CreateWeighted(
                _tokens[(int)qi].EntityId, _kinds.QProjects, _tokens[(int)kj].EntityId,
                _sourceId, contextId: null,
                kindRank: KindRank.TensorCalculation, sourceTrust: SourceTrust.AiModelProbe,
                magnitude: buf[i].Score, floor: QkNoiseFloor);

            bb ??= new SubstrateChangeBuilder(_sourceId, $"q_projects/batch-{batchIdx}",
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: AttBatchSize);
            bb.AddAttestation(row);
            if (++inBatch >= AttBatchSize) { yield return bb.Build(); bb = null; inBatch = 0; batchIdx++; }
        }
        if (bb != null) yield return bb.Build();
    }

    /* Stream above-threshold token×token pairs (from a cached-scorer window) as
     * attestation batches under an arbitrary binary kind — the OV (O_PROJECTS) and FFN
     * (DOWN_PROJECTS) circuit emitters. Identical contract to EmitQkBatches (no C# dedup;
     * content-addressed dedup / Glicko consensus is the writer's job), parameterized by
     * kind + label + floor so one math circuit ⇒ one kind. */
    private IEnumerable<SubstrateChange> EmitPairBatches(
        QkPairF64[] buf, int n, Hash128 kindId, string label, double floor)
    {
        SubstrateChangeBuilder? bb = null;
        int inBatch = 0, batchIdx = 0;
        int tokCount = _tokens.Count;
        for (int i = 0; i < n; i++)
        {
            uint qi = buf[i].QueryIdx, kj = buf[i].KeyIdx;
            if (qi >= (uint)tokCount || kj >= (uint)tokCount) continue;
            var row = AttestationFactory.CreateWeighted(
                _tokens[(int)qi].EntityId, kindId, _tokens[(int)kj].EntityId,
                _sourceId, contextId: null,
                kindRank: KindRank.TensorCalculation, sourceTrust: SourceTrust.AiModelProbe,
                magnitude: buf[i].Score, floor: floor);

            bb ??= new SubstrateChangeBuilder(_sourceId, $"{label}/batch-{batchIdx}",
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: AttBatchSize);
            bb.AddAttestation(row);
            if (++inBatch >= AttBatchSize) { yield return bb.Build(); bb = null; inBatch = 0; batchIdx++; }
        }
        if (bb != null) yield return bb.Build();
    }

    /* Project a layer's Q + K through the embedding ONCE for ALL heads, streaming E a
     * single time, into the reusable f64 caches. qCache [vocab][nHeads][headDim],
     * kCache [vocab][nKvHeads][headDim], row-major. The per-element compensated projection
     * (fixed order) is identical to the pruned kernel's, so scoring from these caches is
     * bit-identical. Replaces the prior per-head E re-streaming. */
    private static unsafe void ProjectLayerQk(
        float[] eF32, int vocab, int dModel, float[] qW, int nHeads, float[] kW, int nKv,
        int headDim, double[] qCache, double[] kCache)
    {
        int rc;
        fixed (float* ep = eF32)
        fixed (float* qp = qW)
        fixed (float* kp = kW)
        fixed (double* qc = qCache)
        fixed (double* kc = kCache)
        {
            rc = SynthInterop.ProjectQkLayer(
                ep, (nuint)vocab, (nuint)dModel, qp, (nuint)nHeads, kp, (nuint)nKv,
                (nuint)headDim, qc, kc);
        }
        if (rc != 0) throw new InvalidOperationException($"project_qk_layer returned {rc}");
    }

    /* Project a SINGLE source matrix `src` [vocab × dModel] through `nProj` stacked head
     * weights `wStacked` ([nProj*headWidth × dModel], project_token row-major: out[d]=Σ_m
     * src[m]·W[d,m]) into `cacheOut` [vocab][nProj][headWidth], REUSING the existing
     * project_qk_layer kernel. That kernel projects two weight sets through ONE source;
     * here only one side is wanted (OV / FFN use DIFFERENT sources for the A and C sides),
     * so the k-slot is fed a 1-head dummy (the same weight's first headWidth rows, valid
     * memory) into a tiny throwaway cache. The compensated f64 projection in the q-slot is
     * therefore IDENTICAL to the QK path (no new kernel). */
    private static unsafe void ProjectThroughHeads(
        float[] src, int vocab, int dModel, float[] wStacked, int nProj, int headWidth,
        double[] cacheOut)
    {
        if ((long)nProj * headWidth * dModel != wStacked.LongLength)
            throw new InvalidOperationException(
                $"ProjectThroughHeads: weight length {wStacked.LongLength} != nProj({nProj})*headWidth({headWidth})*dModel({dModel})");
        if ((long)vocab * nProj * headWidth != cacheOut.LongLength)
            throw new InvalidOperationException(
                $"ProjectThroughHeads: cache length {cacheOut.LongLength} != vocab({vocab})*nProj({nProj})*headWidth({headWidth})");
        // 1-head throwaway k-slot — project_qk_layer rejects null Wk / zero n_kv (qk_project_cached.cpp:85-86).
        var kDummy = new double[(long)vocab * headWidth];
        int rc;
        fixed (float* ep = src)
        fixed (float* wp = wStacked)
        fixed (double* qc = cacheOut)
        fixed (double* kc = kDummy)
        {
            rc = SynthInterop.ProjectQkLayer(
                ep, (nuint)vocab, (nuint)dModel, wp, (nuint)nProj, wp, 1u,
                (nuint)headWidth, qc, kc);
        }
        if (rc != 0) throw new InvalidOperationException($"project_qk_layer (single-source) returned {rc}");
    }

    /* Transpose a row-major [rows × cols] f32 matrix to [cols × rows]. Used to turn HF
     * down_proj [dModel × interm] into the [interm × dModel] layout project_token wants
     * (Wslice[i,m']=Wdown[m',i]) for the FFN C side. */
    private static float[] Transpose2D(float[] a, int rows, int cols)
    {
        if ((long)rows * cols != a.LongLength)
            throw new InvalidOperationException(
                $"Transpose2D: length {a.LongLength} != rows({rows})*cols({cols})");
        var t = new float[(long)cols * rows];
        for (int r = 0; r < rows; r++)
        {
            long ro = (long)r * cols;
            for (int c = 0; c < cols; c++)
                t[(long)c * rows + r] = a[ro + c];
        }
        return t;
    }

    /* Per-circuit noise-floor calibration. The bilinear magnitude scale differs per
     * circuit (QK |q·k| peaks ~0.14, OV/FFN ~8e-4), so a single global floor cannot fit
     * all three. Sample a stride of query rows, score each against ALL keys over the
     * SAME cached dot the production kernel uses, collect |score|, and return the
     * magnitude at the (1-surviveFrac) quantile — the floor that keeps ~surviveFrac of
     * pairs for THIS circuit's spectrum. Calibration samples; production scoring stays
     * exact. aSlot/cSlot pick the head slice within an interleaved [vocab][slots][width]
     * cache. */
    private static double CalibrateFloor(
        double[] aCache, int aSlots, int aSlot,
        double[] cCache, int cSlots, int cSlot,
        int vocab, int width, double surviveFrac)
    {
        int sampleQ = Math.Min(256, vocab);
        int stride  = Math.Max(1, vocab / sampleQ);
        int rows = 0;
        for (int t = 0; t < vocab; t += stride) rows++;
        var mags = new double[(long)rows * vocab];
        long w = 0;
        for (int t = 0; t < vocab; t += stride)
        {
            long aOff = ((long)t * aSlots + aSlot) * width;
            for (int s = 0; s < vocab; s++)
            {
                long cOff = ((long)s * cSlots + cSlot) * width;
                double dot = 0.0;
                for (int d = 0; d < width; d++) dot += aCache[aOff + d] * cCache[cOff + d];
                mags[w++] = Math.Abs(dot);
            }
        }
        if (mags.Length == 0) return 0.0;
        Array.Sort(mags);
        long idx = (long)((1.0 - surviveFrac) * (mags.LongLength - 1));
        return mags[Math.Clamp(idx, 0L, mags.LongLength - 1)];
    }

    /* Build the OV C-side weight stack from HF o_proj. o_proj is [dModel × nHeads*headDim]
     * (output×input). The OV circuit needs, per head h, Wslice_h[d, m'] = Wo[m', h*headDim+d]
     * (m'∈dModel, d∈headDim) so project_token gives C_h[s,d]=Σ_{m'} E_U[s,m']·Wo[m',h*headDim+d].
     * Output: [nHeads*headDim × dModel], head h's [headDim × dModel] block at h*headDim*dModel,
     * row d = column (h*headDim+d) of Wo over all dModel output rows. */
    private static float[] TransposeOProjToHeads(float[] oW, int dModel, int nHeads, int headDim)
    {
        int attnOut = nHeads * headDim;
        if ((long)dModel * attnOut != oW.LongLength)
            throw new InvalidOperationException(
                $"TransposeOProjToHeads: length {oW.LongLength} != dModel({dModel})*nHeads*headDim({attnOut})");
        var t = new float[(long)attnOut * dModel];   // [nHeads*headDim × dModel]
        for (int mp = 0; mp < dModel; mp++)            // Wo output row m'
        {
            long woRow = (long)mp * attnOut;
            for (int col = 0; col < attnOut; col++)    // col = h*headDim + d
                t[(long)col * dModel + mp] = oW[woRow + col];
        }
        return t;
    }

    /* One (layer, head) query-row window scored from the pre-projected caches (no E touch).
     * Fills buf with every above-floor pair for query rows [q0, q1); sets overflow=1
     * (returning the whole-row prefix that fit) when buf is too small — caller shrinks
     * the window and retries. Bit-identical to the per-head pruned kernel. */
    private static unsafe long QkWindowCached(
        double[] qCache, int nHeads, double[] kCache, int nKv, int vocab, int headDim,
        int head, int kvHead, double floor, int q0, int q1, QkPairF64[] buf, out int overflow)
    {
        int of;
        long n;
        fixed (double* qc = qCache)
        fixed (double* kc = kCache)
        fixed (QkPairF64* bp = buf)
        {
            // Sub-quadratic exact (Cauchy-Schwarz norm-pruned) over the cached projections;
            // bit-identical to compute_qk_pairs_above_threshold_pruned for this head.
            n = SynthInterop.ScoreQkHeadCached(
                qc, (nuint)nHeads, kc, (nuint)nKv, (nuint)vocab, (nuint)headDim,
                (nuint)head, (nuint)kvHead, floor, (nuint)q0, (nuint)q1, bp, (nuint)buf.Length, &of);
        }
        overflow = of;
        if (n < 0) throw new InvalidOperationException("score_qk_head_cached returned -1");
        return n;
    }

    /* Load helpers (lifted from the pre-Stream-A LlamaWeightExtractor). */
    private byte[] LoadRawBytes(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string name)
    {
        var tref = refMap[name];
        byte[] rawBytes = new byte[tref.DataLength];
        // Open the SHARD this tensor lives in (sharded-aware); FilePath is set by ParseModel.
        using var fs = new FileStream(tref.FilePath, FileMode.Open, FileAccess.Read,
                                      FileShare.Read, 1 << 16, useAsync: false);
        fs.Seek(tref.AbsoluteDataStart, SeekOrigin.Begin);
        int total = 0;
        while (total < rawBytes.Length)
        {
            int n = fs.Read(rawBytes, total, rawBytes.Length - total);
            if (n == 0) throw new IOException($"safetensors: truncated data for {name}");
            total += n;
        }
        return rawBytes;
    }

    private ushort[] LoadRawBF16(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string name, long expectedElements)
    {
        byte[] raw = LoadRawBytes(refMap, name);
        if (raw.Length != expectedElements * 2)
            throw new InvalidOperationException(
                $"BF16 size mismatch for {name}: got {raw.Length} bytes, expected {expectedElements * 2}");
        return MemoryMarshal.Cast<byte, ushort>(raw).ToArray();
    }

    private float[] LoadRawBF16AsF32(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string name, long expectedElements)
    {
        var tref = refMap[name];
        byte[] raw = LoadRawBytes(refMap, name);
        float[] result = new float[expectedElements];
        // Generic dtype → f32 dispatch. The dtype is self-describing (safetensors header),
        // so handling a new one is just a decoder, never detection. Covers every safetensors
        // numeric/bool dtype incl. the float8 quant formats; fails loud (never zeros) on
        // anything else (e.g. GGUF block-quant, which is a different container entirely).
        long n = expectedElements;
        unsafe
        {
            fixed (byte* rp = raw)
            fixed (float* op = result)
            {
                switch (tref.Dtype)
                {
                    case "F32":  { float*  s = (float*)rp;  for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "F64":  { double* s = (double*)rp; for (long i = 0; i < n; i++) op[i] = (float)s[i]; break; }
                    case "F16":  { ushort* s = (ushort*)rp; for (long i = 0; i < n; i++) op[i] = (float)BitConverter.UInt16BitsToHalf(s[i]); break; }
                    case "BF16": { ushort* s = (ushort*)rp; for (long i = 0; i < n; i++) { uint b = (uint)s[i] << 16; float f; Buffer.MemoryCopy(&b, &f, 4, 4); op[i] = f; } break; }
                    case "F8_E5M2": { byte* s = rp; for (long i = 0; i < n; i++) op[i] = DecodeE5M2(s[i]); break; }
                    case "F8_E4M3": { byte* s = rp; for (long i = 0; i < n; i++) op[i] = DecodeE4M3(s[i]); break; }
                    case "I64":  { long*  s = (long*)rp;  for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I32":  { int*   s = (int*)rp;   for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I16":  { short* s = (short*)rp; for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I8":   { sbyte* s = (sbyte*)rp; for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "U8":   for (long i = 0; i < n; i++) op[i] = rp[i]; break;
                    case "BOOL": for (long i = 0; i < n; i++) op[i] = rp[i] != 0 ? 1f : 0f; break;
                    default:
                        throw new NotSupportedException(
                            $"tensor '{name}' dtype '{tref.Dtype}' has no decoder. safetensors numeric/bool " +
                            "are covered; GGUF block-quant (Q4_K/Q6_K/…) is a separate container needing its " +
                            "own dequantizer. Refusing to ingest zeros.");
                }
            }
        }
        return result;
    }

    /* float8 E5M2 (1-5-2, bias 15) → f32. Same 5-bit exponent/bias as IEEE half, so widen
     * the mantissa into a half and let the half decoder handle normals/subnormals/inf/nan. */
    private static float DecodeE5M2(byte b)
    {
        int sign = (b >> 7) & 1, exp = (b >> 2) & 0x1F, mant = b & 0x3;
        ushort half = (ushort)((sign << 15) | (exp << 10) | (mant << 8));
        return (float)BitConverter.UInt16BitsToHalf(half);
    }

    /* float8 E4M3FN (1-4-3, bias 7, no inf; S.1111.111 = NaN; max 448) → f32. */
    private static float DecodeE4M3(byte b)
    {
        int sign = (b >> 7) & 1, exp = (b >> 3) & 0xF, mant = b & 0x7;
        float v;
        if (exp == 0)                 v = mant * MathF.ScaleB(1f, -9);          // subnormal: mant/8·2⁻⁶
        else if (exp == 15 && mant == 7) v = float.NaN;
        else                          v = (1f + mant * 0.125f) * MathF.ScaleB(1f, exp - 7);
        return sign != 0 ? -v : v;
    }
}
