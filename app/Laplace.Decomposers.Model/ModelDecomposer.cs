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
///   3. Token vocab entities (32K in batches)
///   4. Tokenizer meta-entity
///   5. Weight attestations: EMBEDS, Q_PROJECTS, then per-layer roles
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
    // Witness entities: per (layer, head) model provenance. Set as the attestation
    // context_id so each layer/head's evidence is a distinct row (evidence keeps
    // layer/head; consensus drops it). The model itself is the source; the witness
    // is which circuit instance inside it observed the relation.
    public static readonly Hash128 WitnessTypeId =
        Hash128.OfCanonical("substrate/type/Witness/v1");

    /* Attestation-kind vocabulary.
     *
     * LIVE circuit kinds (emitted by EmitCircuitMemoriesAsync → ModelCircuitEdges):
     *   ATTENDS / OV_RELATES / COMPLETES_TO — signed token×token bilinear-circuit
     *   relations (QK / OV / FFN read through the embedding), each observation
     *   carrying its (layer, head) Witness entity as context_id (evidence keeps
     *   provenance; consensus drops it).
     *
     * LEGACY per-tensor kinds (EMBEDS … OUTPUT_PROJECTS): the vocabulary of the
     * dead per-cell-magnitude ExtractAsync path. Per-token magnitude reduction is
     * banned — it destroys the relation by collapsing the dim axis to one number.
     * They are bootstrapped only so existing rows keep resolving; they go when
     * the dead path is deleted. Nonlinearities (softmax, SiLU/SwiGLU gating) are
     * runtime, never attested. */
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

        /* Model_Feature type removed — feature-dim entities were conventional-AI
         * smuggling; the corrected decomposition emits PROJECTION physicalities for the
         * token-axis tensors (embed_tokens, lm_head) and token×token typed
         * attestations for interior tensors via the bilinear circuits. */

        /* Kind vocabulary bootstrapped at first decomposer run. Live: the circuit
         * kinds (ATTENDS / OV_RELATES / COMPLETES_TO) + TOKEN_MAPS_TO + the recipe
         * kinds. Legacy per-tensor kinds (EMBEDS … OUTPUT_PROJECTS) belong to the
         * dead per-cell-magnitude path and are kept only until it is deleted. */
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

        /* 4. Weights. The model is a witness — interior circuits become signed
         *    token×token attestations; the embedding becomes Projection physicality
         *    placements on the shared Unicode S³ frame. */
        var extractor = new WeightTensorETL(_modelDir, recipe, tokens, Source, WitnessTypeId, log);

        // 4a. The records: ALL interior circuits (QK→ATTENDS, OV→OV_RELATES, FFN→COMPLETES_TO),
        //     each side projected through E / E_U once, contracted tile-by-tile
        //     (ModelCircuitEdges → engine bilinear_edges_tile, exact f64) into signed
        //     token×token observations, each carrying its (layer, head) witness as
        //     context (evidence keeps provenance; consensus drops it).
        log.LogInformation("phase=circuits starting (QK/OV/FFN per layer,head → [n-gram] ⇒ {{tokens}})");
        await foreach (var change in extractor.EmitCircuitMemoriesAsync(ct))
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
        await foreach (var change in extractor.EmitS3MorphAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return change;
        }
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(32000L + 22 * 9 + 4);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
