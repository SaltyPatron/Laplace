using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// IDecomposer for transformer model families (Llama, Qwen, etc.).
/// LayerOrder = 10 — ingests after all linguistic seed layers.
///
/// Decomposition order (FK-dependency safe):
///   1. Bootstrap types + attestation kinds
///   2. Recipe entity + recipe-parameter attestations
///   3. Tokenizer meta-entity, then token vocab entities (batched)
///   4. Weight tables: the cell ETL (<see cref="ModelTableETL"/> — every
///      non-zero cell = one adjudicated match under its tensor-role kind;
///      positions aggregate as witnesses) + S³ placements (separate axis)
/// </summary>
public sealed class ModelDecomposer : IDecomposer
{
    /* Well-known substrate IDs */
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AIModelProbe/v1");

    /// <summary>
    /// Source identity for a model is its CONTENT, not its directory name (truth #5):
    /// chunk-Merkle of config.json + the weight shards (<see cref="SourceEntityIdConventions.ModelContentSourceId"/>).
    /// Two byte-identical copies — renamed, moved, re-downloaded — are ONE witness (no
    /// double-counting; cross-model consensus accumulates correctly); a fine-tune or
    /// re-quantization is a DISTINCT witness. The display NAME is still derived from the
    /// directory (HF "…/models--ORG--NAME/snapshots/SHA" → "ORG/NAME", else the dir name).
    /// Falls back to the name-based id only when no weight files are present (fixtures).
    /// Used by the decomposer AND callers that only have the dir (re-ingest guard, synthesis),
    /// so they agree on the id.
    /// </summary>
    public static (Hash128 Id, string Name) SourceForModel(string modelDir)
    {
        string name = DeriveModelName(modelDir);
        Hash128 id = SourceEntityIdConventions.ModelContentSourceId(modelDir)
                     ?? Hash128.OfCanonical($"substrate/source/{name}/v1");
        return (id, name);
    }

    private static string DeriveModelName(string modelDir)
    {
        string norm = (modelDir ?? "").Replace('\\', '/').TrimEnd('/');
        var segs = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segs)
            if (seg.StartsWith("models--", StringComparison.Ordinal))
                return string.Join("/", seg.Substring("models--".Length).Split("--"));
        for (int i = segs.Length - 1; i >= 0; i--)
        {
            string s = segs[i];
            if (s == "snapshots") continue;
            if (s.Length >= 32 && s.All(Uri.IsHexDigit)) continue;   // skip a snapshot SHA
            return s;
        }
        return "model";
    }

    /* Type IDs */
    public static readonly Hash128 TextTypeId =
        Hash128.OfCanonical("substrate/type/Text/v1");
    public static readonly Hash128 ModelRecipeTypeId =
        Hash128.OfCanonical("substrate/type/Model_Recipe/v1");
    public static readonly Hash128 ModelTokenizerTypeId =
        Hash128.OfCanonical("substrate/type/Model_Tokenizer/v1");
    public static readonly Hash128 ArchitectureTypeId =
        Hash128.OfCanonical("substrate/type/Architecture/v1");
    public static readonly Hash128 ScalarTypeId =
        Hash128.OfCanonical("substrate/type/Scalar/v1");
    // N-gram trajectory entities (a path of tokens — [the, capital, of] — as one
    // content-addressed entity, a tier above its constituents).
    public static readonly Hash128 NgramTypeId =
        Hash128.OfCanonical("substrate/type/Ngram/v1");
    // Witness entities: per-position model provenance (role instance × layer).
    // Set as the attestation context_id so each position's evidence is a distinct
    // row (evidence keeps the position; consensus folds it). The model itself is
    // the source; the witness is WHICH position inside it testified.
    public static readonly Hash128 WitnessTypeId =
        Hash128.OfCanonical("substrate/type/Witness/v1");
    // Model-axis entities: the source's SURROGATE KEYS (residual channels,
    // attention dims, kv dims, FFN neurons) — first-class join nodes of the
    // cell ETL (SourceEntityIdConventions.ModelAxisEntity). Source-scoped;
    // aligned cross-model by placements, never by index identity.
    public static readonly Hash128 ModelAxisTypeId =
        Hash128.OfCanonical("substrate/type/Model_Axis/v1");

    /* Attestation-kind vocabulary.
     *
     * THE TENSOR-ROLE KINDS (EMBEDS … OUTPUT_PROJECTS) are the LIVE arenas of
     * the model ETL — the inventor's original fixed vocabulary (discussion #192
     * §6, reconfirmed 2026-06-04: "ETL on conventional AI for AI"). One kind per
     * logical table role; each cell loads as one signed Glicko match between its
     * own endpoints; LAYERS/HEADS ARE POSITIONS of the same table and aggregate
     * as witnesses (per-position attribution = recipe content). The token×token
     * bilinear (QK/OV/FFN) is the QUERY-TIME read — μ-ranked joins across these
     * arenas — never an ingest materialization. A prior session's smear of these
     * kinds as "the disease" was the corruption vector; never re-bury them.
     *
     * ATTENDS / OV_RELATES / COMPLETES_TO are READ vocabulary — names for the
     * query-time bilinear compositions — never ingest-written; the per-(i,j)
     * pre-join emitter that wrote them (ModelCircuitEdges) is deleted.
     *
     * Per-token magnitude reduction (row → one scalar) stays the cardinal sin.
     * Nonlinearities (softmax, SiLU/SwiGLU gating) are the source's runtime —
     * never attested, never run at ingest. */
    public static readonly Hash128 EmbedsKind        = Hash128.OfCanonical("substrate/kind/EMBEDS/v1");
    public static readonly Hash128 QProjectsKind     = Hash128.OfCanonical("substrate/kind/Q_PROJECTS/v1");
    public static readonly Hash128 KProjectsKind     = Hash128.OfCanonical("substrate/kind/K_PROJECTS/v1");
    public static readonly Hash128 VProjectsKind     = Hash128.OfCanonical("substrate/kind/V_PROJECTS/v1");
    public static readonly Hash128 OProjectsKind     = Hash128.OfCanonical("substrate/kind/O_PROJECTS/v1");
    public static readonly Hash128 GatesKind         = Hash128.OfCanonical("substrate/kind/GATES/v1");
    public static readonly Hash128 UpProjectsKind    = Hash128.OfCanonical("substrate/kind/UP_PROJECTS/v1");
    public static readonly Hash128 DownProjectsKind  = Hash128.OfCanonical("substrate/kind/DOWN_PROJECTS/v1");
    public static readonly Hash128 NormalizesKind    = Hash128.OfCanonical("substrate/kind/NORMALIZES/v1");
    public static readonly Hash128 OutputProjectsKind = Hash128.OfCanonical("substrate/kind/OUTPUT_PROJECTS/v1");
    public static readonly Hash128 TokenMapsToKind   = Hash128.OfCanonical("substrate/kind/TOKEN_MAPS_TO/v1");
    /* Content x content relatedness witnessed from model trajectory geometry — the
     * corrected ingest axis (same kind the lexical decomposers emit, so model + dataset
     * witnesses dedup onto one consensus edge). */
    public static readonly Hash128 SimilarToKind     = Hash128.OfCanonical("substrate/kind/SIMILAR_TO/v1");
    // Per-circuit relations, each read per (head/neuron) through the embedding address book:
    //   ATTENDS      — QK: [query n-gram] attends [key tokens]
    //   OV_RELATES   — OV: [value n-gram] relates [output tokens]
    //   COMPLETES_TO — FFN: [context n-gram] ⇒ {completion tokens}
    public static readonly Hash128 AttendsKind       = Hash128.OfCanonical("substrate/kind/ATTENDS/v1");
    public static readonly Hash128 OvRelatesKind     = Hash128.OfCanonical("substrate/kind/OV_RELATES/v1");
    public static readonly Hash128 CompletesToKind   = Hash128.OfCanonical("substrate/kind/COMPLETES_TO/v1");

    /* Recipe attestation kinds */
    public static readonly Hash128 HasHiddenSizeKind  = Hash128.OfCanonical("substrate/kind/HAS_HIDDEN_SIZE/v1");
    public static readonly Hash128 HasNumLayersKind   = Hash128.OfCanonical("substrate/kind/HAS_NUM_LAYERS/v1");
    public static readonly Hash128 HasNumHeadsKind    = Hash128.OfCanonical("substrate/kind/HAS_NUM_HEADS/v1");
    public static readonly Hash128 HasNumKvHeadsKind  = Hash128.OfCanonical("substrate/kind/HAS_NUM_KV_HEADS/v1");
    public static readonly Hash128 HasIntermSizeKind  = Hash128.OfCanonical("substrate/kind/HAS_INTERMEDIATE_SIZE/v1");
    public static readonly Hash128 HasVocabSizeKind   = Hash128.OfCanonical("substrate/kind/HAS_VOCAB_SIZE/v1");
    public static readonly Hash128 IsAKind            = Hash128.OfCanonical("substrate/kind/IS_A/v1");

    /* Well-known entity */
    public static readonly Hash128 LlamaArchitectureId =
        Hash128.OfCanonical("substrate/entity/Architecture_Llama/v1");

    private readonly string _modelDir;
    private readonly Hash128 _source;
    private readonly string  _sourceName;

    public ModelDecomposer(string modelDir)
    {
        _modelDir = modelDir ?? throw new ArgumentNullException(nameof(modelDir));
        (_source, _sourceName) = SourceForModel(modelDir);   // per-model identity, not hardcoded
    }

    /// <summary>This model's source identity, derived from its directory.</summary>
    public Hash128 Source       => _source;
    public Hash128 SourceId     => _source;
    public string  SourceName   => _sourceName;
    public int     LayerOrder   => 10;
    public Hash128 TrustClassId => TrustClass;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);

        boot.AddType("Model_Recipe");
        boot.AddType("Model_Tokenizer");
        boot.AddType("Scalar");
        boot.AddType("Architecture");
        boot.AddType("Ngram");
        boot.AddType("Witness");
        boot.AddType("Model_Axis");

        /* Codepoint / Grapheme / Word / Sentence / Document type entities are
         * substrate-canonical text-tier types used by TextEntityBuilder for any
         * text decomposition (TextDecomposer + HashComposer). They're seeded
         * by 10_bootstrap.sql.in at install — every text-using decomposer
         * (UnicodeDecomposer, this one, future WordNet/UD/Wiktionary/etc.)
         * references the same type IDs.
         *
         * Text type alias kept for the canonical name `substrate/type/Text/v1`
         * used by tokenizer entity rows (separate from the tier-typed entities
         * TextEntityBuilder emits; matches LlamaTokenizerParser's existing
         * `textTypeId` parameter). */
        boot.AddType("Text");

        /* Kind vocabulary bootstrapped at first decomposer run. LIVE arenas: the
         * ten tensor-role kinds (EMBEDS … OUTPUT_PROJECTS — the cell ETL's load
         * targets) + TOKEN_MAPS_TO + the recipe kinds. ATTENDS / OV_RELATES /
         * COMPLETES_TO are read-side vocabulary for the query-time bilinear
         * compositions — registered, never ingest-written. */
        boot.AddKind("EMBEDS");
        boot.AddKind("Q_PROJECTS");
        boot.AddKind("K_PROJECTS");
        boot.AddKind("V_PROJECTS");
        boot.AddKind("O_PROJECTS");
        boot.AddKind("GATES");
        boot.AddKind("UP_PROJECTS");
        boot.AddKind("DOWN_PROJECTS");
        boot.AddKind("NORMALIZES");
        boot.AddKind("OUTPUT_PROJECTS");
        boot.AddKind("ATTENDS");
        boot.AddKind("OV_RELATES");
        boot.AddKind("COMPLETES_TO");
        boot.AddKind("TOKEN_MAPS_TO");
        boot.AddKind("HAS_HIDDEN_SIZE");
        boot.AddKind("HAS_NUM_LAYERS");
        boot.AddKind("HAS_NUM_HEADS");
        boot.AddKind("HAS_NUM_KV_HEADS");
        boot.AddKind("HAS_INTERMEDIATE_SIZE");
        boot.AddKind("HAS_VOCAB_SIZE");
        boot.AddKind("IS_A");

        return context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var log = context.Logger;
        var phaseSw = System.Diagnostics.Stopwatch.StartNew();
        string configPath    = Path.Combine(_modelDir, "config.json");
        string tokenizerPath = Path.Combine(_modelDir, "tokenizer.json");

        /* 1. Recipe */
        var recipe = LlamaRecipeExtractor.Parse(configPath);
        log.LogInformation("phase=recipe parsed: {Layers} layers, {Heads} heads/{Kv} kv, "
            + "d_model={DModel}, vocab={Vocab} ({Ms} ms)",
            recipe.NumLayers, recipe.NumHeads, recipe.NumKvHeads, recipe.HiddenSize,
            recipe.VocabSize, phaseSw.ElapsedMilliseconds);
        yield return LlamaRecipeExtractor.BuildChange(
            recipe, Source, ModelRecipeTypeId,
            HasHiddenSizeKind, HasNumLayersKind, HasNumHeadsKind, HasNumKvHeadsKind,
            HasIntermSizeKind, HasVocabSizeKind,
            IsAKind, LlamaArchitectureId);

        /* 2. Tokenizer meta-entity — MUST come before vocab batches.
         * BuildBatches emits TOKEN_MAPS_TO attestations that reference tokEntityId
         * as subject_id; the entity must be in DB before those COPYs run. */
        byte[] tokBytes = File.ReadAllBytes(tokenizerPath);
        var tokEntityId = Hash128.Blake3(tokBytes);
        var tokChange = new SubstrateChangeBuilder(Source, "tokenizer/entity",
            entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: 0);
        tokChange.AddEntity(tokEntityId, tier: 0, ModelTokenizerTypeId, firstObservedBy: Source);
        yield return tokChange.Build();

        /* 3. Token vocab entities + TOKEN_MAPS_TO attestations.
         * Records are sorted by token_id in Parse() — _tokens[(int)vocabIndex].EntityId
         * is correct for the QK scorer's vocab-index output. */
        phaseSw.Restart();
        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        log.LogInformation("phase=vocab parsed: {Count} tokens ({Ms} ms)",
            tokens.Count, phaseSw.ElapsedMilliseconds);
        /* Model vocab is always large (32K+); the DecomposerOptions default BatchSize=1
         * would emit one micro-intent per token (32K transactions, round-trip-bound).
         * Floor the vocab/weight batch at 8192 regardless of the tiny per-unit default. */
        int batchSz = Math.Max(options.BatchSize, 8192);
        phaseSw.Restart();
        int vocabBatches = 0;
        foreach (var batch in LlamaTokenizerParser.BuildBatches(
            tokens, Source, TextTypeId, tokEntityId, TokenMapsToKind, batchSz))
        {
            ct.ThrowIfCancellationRequested();
            yield return batch;
            vocabBatches++;
            await Task.Yield();
        }
        log.LogInformation("phase=vocab emitted: {Batches} batches ({Ms} ms)",
            vocabBatches, phaseSw.ElapsedMilliseconds);

        /* 4. Weights — "ETL on conventional AI, for AI". The model is a witness;
         *    its weight tables load as adjudicated matches under the ten
         *    tensor-role kinds; the embedding additionally becomes Projection
         *    physicality placements on the shared Unicode S³ frame. */

        // 4a. The records: stream every table's cells AT REST (O(params), exact —
        //     no forward pass, no probe, no GEMM pre-join). Token axes resolve
        //     through the model's own embed/lm_head key-mapping tables; hidden
        //     axes are its surrogate keys (Model_Axis join nodes); positions
        //     (layer instances) aggregate as witnesses in context_id. The
        //     token×token bilinear (QK/OV/FFN) is the QUERY-TIME read across
        //     these arenas — never materialized at ingest.
        log.LogInformation("phase=etl starting (weight tables → adjudicated matches under tensor-role kinds)");
        var etl = new ModelTableETL(_modelDir, recipe, tokens, Source,
                                    WitnessTypeId, ModelAxisTypeId, log);
        await foreach (var change in etl.EmitAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return change;
        }

        // 4b. Placement (separate axis): morph embed_tokens onto the shared Unicode S³
        //     frame (eigenmaps → Gram-Schmidt → Procrustes) as Projection physicalities.
        //     NOTE: the dense Laplacian-eigenmaps affinity is O(n²·d_model) — it does NOT
        //     share the O(params) budget of the circuit read; its affinity should come from
        //     the streamed relation graph (sparse), the same redesign the circuits got.
        //     LAPLACE_SKIP_MORPH=1 omits this placement so the O(params) circuit ingest can
        //     be measured/run on its own until the morph affinity is made sparse.
        if (Environment.GetEnvironmentVariable("LAPLACE_SKIP_MORPH") == "1")
        {
            log.LogInformation("phase=S3-morph SKIPPED (LAPLACE_SKIP_MORPH=1)");
        }
        else
        {
        log.LogInformation("phase=S3-morph starting (embed_tokens → Unicode S³ frame)");
        var morph = new WeightTensorETL(_modelDir, recipe, tokens, Source, log);
        await foreach (var change in morph.EmitS3MorphAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return change;
        }
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        // Best-effort row estimate from the recipe's schema shapes — an upper
        // bound (zero cells are non-events and emit nothing). Never hardcoded.
        string configPath = Path.Combine(_modelDir, "config.json");
        if (!File.Exists(configPath)) return Task.FromResult<long?>(null);
        var r = LlamaRecipeExtractor.Parse(configPath);
        var p = ArchitectureProfile.For(r.ModelType);
        long d = r.HiddenSize, vocab = r.VocabSize, interm = r.IntermediateSize, L = r.NumLayers;
        long headDim = d / r.NumHeads;
        long attnOut = r.NumHeads * headDim, kvDim = (long)r.NumKvHeads * headDim;
        long cells =
              vocab * d                                   // EMBEDS
            + vocab * d                                   // OUTPUT_PROJECTS (own table or tied)
            + L * (2 * d * attnOut                        // Q + O
                   + 2 * d * kvDim                        // K + V
                   + (p.HasGate ? d * interm : 0)         // GATES
                   + 2 * d * interm)                      // UP + DOWN
            + (p.PerLayerNorms.Count * L + 1) * d;        // NORMALIZES
        return Task.FromResult<long?>(vocab + cells);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
