using System.Runtime.InteropServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Engine.Synthesis;    // QkPair
using Laplace.SubstrateCRUD;
using DynamicsInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Per ADR 0043 the model-decomposer composite is `ModelDecomposer&lt;ContainerFormat&gt;` with
/// sub-decomposers along orthogonal axes (ContainerFormat × TensorDtypeDecoder ×
/// IArchitectureTemplate × ModalityBinder). The Llama-specific extractor here is the
/// wrong shape: it hardcodes one architecture family and one container format into a
/// single class.
///
/// Per ADR 0056 the universal model-ingest algorithm is `WeightTensorETL`, a single
/// algorithm whose per-family math is registered as data on architecture-template
/// entities. The prior committed body of this class implemented:
///
///   - embed_tokens / lm_head as PROJECTION / PROJECTION_OUTPUT physicalities
///     (wrong: per ADR 0056:163-164 these emit EMBEDS / OUTPUT_PROJECTS attestations
///      of shape (text_entity, embed_dim) / (hidden_dim, text_entity), per-cell magnitude)
///   - Q_PROJECTS via static joint bilinear `E·Wq·Wkᵀ·Eᵀ` (wrong reduction: per ADR
///     0056:157 the math is `q_proj[i,:] · k_proj[j,:]ᵀ` per (layer, head) BEFORE the
///     embedding step, then aggregated; the bilinear projection through E collapses
///     the per-instance structure recipe layout needs)
///   - V/O/GATES/UP/DOWN via self-bilinear `E·W·Wᵀ·Eᵀ` (wrong shape: per ADR 0056:158-162
///     these are per-cell magnitudes with object axis = hidden_dim or intermediate_dim
///     reduced per the spec.math_function, not token×token via self-bilinear collapse)
///
/// Stream A stubs <see cref="ExtractAsync"/> to a no-op pending Stream B's
/// implementation of `WeightTensorETL.extract` per ADR 0056 phases 1-5 (per-tensor
/// matchup via spec.math_function → within-model aggregation across (layer, head,
/// expert) instances → lottery-ticket sparsity on aggregates → static-mathematical
/// retention validation → emission with `initial_rating = scale_aggregated_strength_into_rating(
/// aggregate.strength_summary, rating_prior)`).
///
/// The class name (`LlamaWeightExtractor`) is itself the wrong shape per ADR 0043 +
/// ADR 0056 (one algorithm + per-family registered data). Renamed at Stream B.
/// </summary>
public sealed class LlamaWeightExtractor
{
    /// <summary>
    /// Per-architecture mechanical-role kind id registry. Stream B replaces this
    /// hardcoded class with the per-architecture-template-entity registry per ADR 0056:153
    /// + ADR 0043's `IArchitectureTemplate` plugin. For now (Stream A stub) this carries
    /// the full ADR 0056 spec table T9 vocabulary even though
    /// <see cref="ExtractAsync"/> emits nothing.
    /// </summary>
    public sealed class KindIds
    {
        public required Hash128 Embeds         { get; init; }
        public required Hash128 QProjects      { get; init; }
        public required Hash128 KProjects      { get; init; }
        public required Hash128 VProjects      { get; init; }
        public required Hash128 OProjects      { get; init; }
        public required Hash128 Gates          { get; init; }
        public required Hash128 UpProjects     { get; init; }
        public required Hash128 DownProjects   { get; init; }
        public required Hash128 Normalizes     { get; init; }
        public required Hash128 OutputProjects { get; init; }
        /* Content x content relatedness from trajectory geometry (corrected ingest).
         * Non-required so existing KindIds initializers compile; ModelDecomposer sets it. */
        public Hash128 SimilarTo { get; init; }
        /* Per-circuit relations read per (head/neuron) through the embedding address book,
         * + the n-gram trajectory entity type + the per-(layer,head) Witness provenance
         * type. Non-required; ModelDecomposer sets them. */
        public Hash128 Attends     { get; init; }   // QK  : [query n-gram] attends [key tokens]
        public Hash128 OvRelates   { get; init; }   // OV  : [value n-gram] relates [output tokens]
        public Hash128 CompletesTo { get; init; }   // FFN : [context n-gram] ⇒ {completion tokens}
        public Hash128 NgramType   { get; init; }
        public Hash128 WitnessType { get; init; }
    }

    private readonly string _safetensorsPath;
    private readonly LlamaRecipeExtractor.RecipeInfo _recipe;
    private readonly IReadOnlyList<SafetensorsContainerParser.TensorReference> _refs;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly KindIds _kinds;
    private readonly Hash128 _sourceId;

    public LlamaWeightExtractor(
        string modelDir,
        LlamaRecipeExtractor.RecipeInfo recipe,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId,
        KindIds kinds)
    {
        _recipe          = recipe;
        _tokens          = tokens;
        _sourceId        = sourceId;
        _kinds           = kinds;
        _safetensorsPath = Path.Combine(modelDir, "model.safetensors");
        _refs            = SafetensorsContainerParser.ParseHeader(_safetensorsPath);
    }

    /// <summary>
    /// Stream A stub. Yields no model-derived attestations. Stream B replaces this with
    /// `WeightTensorETL.extract` per ADR 0056 (5-phase universal algorithm; per-family
    /// math registered as data on architecture-template entities; not a Llama-specific
    /// extractor). See /home/ahart/.claude/plans/replicated-hatching-stream.md.
    /// </summary>
#pragma warning disable CS1998 // async without await — intentional empty enumerator
    public async IAsyncEnumerable<SubstrateChange> ExtractAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }
#pragma warning restore CS1998
}
