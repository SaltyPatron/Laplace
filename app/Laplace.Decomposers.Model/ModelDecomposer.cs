using System.Runtime.CompilerServices;
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

    /* Transformer-family attestation kinds */
    public static readonly Hash128 EmbedsKind        = Hash128.OfCanonical("substrate/kind/EMBEDS/v1");
    public static readonly Hash128 QProjectsKind     = Hash128.OfCanonical("substrate/kind/Q_PROJECTS/v1");
    public static readonly Hash128 KProjectsKind     = Hash128.OfCanonical("substrate/kind/K_PROJECTS/v1");
    public static readonly Hash128 VProjectsKind     = Hash128.OfCanonical("substrate/kind/V_PROJECTS/v1");
    public static readonly Hash128 OProjectsKind     = Hash128.OfCanonical("substrate/kind/O_PROJECTS/v1");
    public static readonly Hash128 GatesKind         = Hash128.OfCanonical("substrate/kind/GATES/v1");
    public static readonly Hash128 UpProjectsKind    = Hash128.OfCanonical("substrate/kind/UP_PROJECTS/v1");
    public static readonly Hash128 DownProjectsKind  = Hash128.OfCanonical("substrate/kind/DOWN_PROJECTS/v1");
    public static readonly Hash128 NormalizesKind    = Hash128.OfCanonical("substrate/kind/NORMALIZES/v1");
    public static readonly Hash128 OutputProjectsKind= Hash128.OfCanonical("substrate/kind/OUTPUT_PROJECTS/v1");
    public static readonly Hash128 TokenMapsToKind   = Hash128.OfCanonical("substrate/kind/TOKEN_MAPS_TO/v1");

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
        Embeds        = EmbedsKind,
        QProjects     = QProjectsKind,
        KProjects     = KProjectsKind,
        VProjects     = VProjectsKind,
        OProjects     = OProjectsKind,
        Gates         = GatesKind,
        UpProjects    = UpProjectsKind,
        DownProjects  = DownProjectsKind,
        Normalizes    = NormalizesKind,
        OutputProjects= OutputProjectsKind,
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

        boot.AddType("Text");
        boot.AddType("Model_Recipe");
        boot.AddType("Model_Tokenizer");
        boot.AddType("Scalar");
        boot.AddType("Architecture");
        /* Model_Feature type removed — feature-dim entities were conventional-AI
         * smuggling; the corrected codec uses PROJECTION physicalities for the
         * token-axis tensors (embed_tokens, lm_head) and token×token typed
         * attestations for interior tensors via self-bilinear E·W·Wᵀ·Eᵀ. */

        /* EMBEDS kind removed — embed_tokens is now a per-token PROJECTION
         * physicality, not a per-cell token×feature attestation. */
        boot.AddKind("Q_PROJECTS");
        boot.AddKind("K_PROJECTS");
        boot.AddKind("V_PROJECTS");
        boot.AddKind("O_PROJECTS");
        boot.AddKind("GATES");
        boot.AddKind("UP_PROJECTS");
        boot.AddKind("DOWN_PROJECTS");
        boot.AddKind("NORMALIZES");
        boot.AddKind("OUTPUT_PROJECTS");
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
        string configPath    = Path.Combine(_modelDir, "config.json");
        string tokenizerPath = Path.Combine(_modelDir, "tokenizer.json");

        /* 1. Recipe */
        var recipe = LlamaRecipeExtractor.Parse(configPath);
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
        var tokens = LlamaTokenizerParser.Parse(tokenizerPath);
        /* Model vocab is always large (32K+); the DecomposerOptions default BatchSize=1
         * would emit one micro-intent per token (32K transactions, round-trip-bound).
         * Floor the vocab/weight batch at 8192 regardless of the tiny per-unit default. */
        int batchSz = Math.Max(options.BatchSize, 8192);
        foreach (var batch in LlamaTokenizerParser.BuildBatches(
            tokens, Source, TextTypeId, tokEntityId, TokenMapsToKind, batchSz))
        {
            ct.ThrowIfCancellationRequested();
            yield return batch;
            await Task.Yield();
        }

        /* 4. Weight attestations */
        var extractor = new LlamaWeightExtractor(_modelDir, recipe, tokens, Source, ExtractorKinds);
        await foreach (var change in extractor.ExtractAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return change;
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(32000L + 22 * 9 + 4);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
