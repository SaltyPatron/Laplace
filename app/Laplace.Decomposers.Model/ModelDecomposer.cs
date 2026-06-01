using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// IDecomposer for transformer model families (Llama, Qwen, etc.).
/// LayerOrder = 10 — ingests after all linguistic seed layers (ADR 0037).
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
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/TinyLlama-1.1B-Chat-v1.0/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AIModelProbe/v1");

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

    /* Transformer-family tensor-calculation attestation kinds per ADR 0056
     * spec table + ADR 0044 T9 + GLOSSARY:95 + DESIGN.md:731. Fixed
     * vocabulary, all 10 kinds:
     *   EMBEDS               — embed_tokens, shape (text_entity, embed_dim), per-cell magnitude, one instance
     *   Q_PROJECTS           — q_proj × k_proj per (layer, head), q[i,:]·k[j,:]ᵀ, aggregated
     *   K_PROJECTS           — k_proj per (layer, head), per-cell magnitude (companion to Q in some specs), aggregated
     *   V_PROJECTS           — v_proj per (layer, head), (text_entity, hidden_dim), per-cell magnitude, aggregated
     *   O_PROJECTS           — o_proj per (layer, head), (hidden_dim, text_entity), per-cell magnitude, aggregated
     *   GATES                — gate_proj per layer, SiLU/SwiGLU per recipe, aggregated across layers
     *   UP_PROJECTS          — up_proj per layer, (text_entity, intermediate_dim), per-cell magnitude, aggregated
     *   DOWN_PROJECTS        — down_proj per layer, (intermediate_dim, text_entity), per-cell magnitude, aggregated
     *   NORMALIZES           — *_norm.weight per layer, unary (hidden_dim,), per-cell magnitude, aggregated
     *   OUTPUT_PROJECTS      — lm_head, (hidden_dim, text_entity), per-cell magnitude, one instance
     *
     * Per-(layer, head, expert, dim) attribution is RECIPE CONTENT on the model
     * recipe entity per the ADR 0056 amendment + GLOSSARY explicit rule, NOT
     * per-attestation context_id. Restored in Stream A per
     * /home/ahart/.claude/plans/replicated-hatching-stream.md after a prior
     * commit wrongly narrowed the vocabulary citing a fabricated ADR 0056
     * reading. */
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

    private static readonly LlamaWeightExtractor.KindIds ExtractorKinds = new()
    {
        Embeds         = EmbedsKind,
        QProjects      = QProjectsKind,
        KProjects      = KProjectsKind,
        VProjects      = VProjectsKind,
        OProjects      = OProjectsKind,
        Gates          = GatesKind,
        UpProjects     = UpProjectsKind,
        DownProjects   = DownProjectsKind,
        Normalizes     = NormalizesKind,
        OutputProjects = OutputProjectsKind,
        Attends        = AttendsKind,
        OvRelates      = OvRelatesKind,
        CompletesTo    = CompletesToKind,
        NgramType      = NgramTypeId,
        WitnessType    = WitnessTypeId,
    };

    private readonly string _modelDir;

    public ModelDecomposer(string modelDir)
    {
        _modelDir = modelDir ?? throw new ArgumentNullException(nameof(modelDir));
    }

    public Hash128 SourceId     => Source;
    public string  SourceName   => "TinyLlama-1.1B-Chat-v1.0";
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
         * attestations for interior tensors via self-bilinear E·W·Wᵀ·Eᵀ. */

        /* Transformer-family tensor-calculation kind vocabulary per ADR 0056
         * spec table + ADR 0044 T9 + GLOSSARY:95 + DESIGN.md:731. All 10
         * kinds bootstrapped at first decomposer run; per-position attribution
         * (layer/head/expert/dim) is recipe content on the model recipe entity
         * per the ADR 0056 same-day amendment, NOT per-attestation context_id. */
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

        /* 4. Weights: morph embed_tokens onto the shared Unicode S³ frame
         *    (SUBSTRATE-FOUNDATION truth #3). The model is a witness — its embedding
         *    is placed on the canonical frame as Projection physicalities, NOT a
         *    per-model embedding. Interior q/k/v/o/gate/up/down is OPEN (foundation
         *    doc) and intentionally not emitted; the prior per-circuit Score(t,s)
         *    path (extractor.ExtractAsync) is retained for reference but bypassed. */
        var extractor = new WeightTensorETL(_modelDir, recipe, tokens, Source, ExtractorKinds, log);

        // 4a. The records: ALL interior circuits (QK→ATTENDS, OV→OV_RELATES, FFN→COMPLETES_TO)
        //     read per (layer, head) through the embedding address book as [n-gram] ⇒ {tokens}
        //     memories — content-addressed n-gram trajectory entities + typed attestations,
        //     each carrying its (layer, head) witness as context (evidence keeps provenance).
        log.LogInformation("phase=circuits starting (QK/OV/FFN per layer,head → [n-gram] ⇒ {{tokens}})");
        await foreach (var change in extractor.EmitCircuitMemoriesAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return change;
        }

        // 4b. Placement (separate axis): morph embed_tokens onto the shared Unicode S³
        //     frame (eigenmaps → Gram-Schmidt → Procrustes) as Projection physicalities.
        log.LogInformation("phase=S3-morph starting (embed_tokens → Unicode S³ frame)");
        await foreach (var change in extractor.EmitS3MorphAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return change;
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(32000L + 22 * 9 + 4);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
