using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;

// ── Lane A: the frozen contract every other lane consumes ─────────────────────────────────────
// A ModelManifest is the shape-inferred, modality-agnostic description of a model on disk: the
// config scalars (ModelConfig) plus the list of weight tensors, each tagged with the STRUCTURAL
// role inferred from its shape ("the magic number"). Meaning is never assumed from a tensor's
// name — name/block-order is only a tiebreak. Lanes B (circuit orchestration), C (decoder ring),
// and D (recipe synthesis) all read this and nothing else about the raw weights.

// What a tensor structurally is. Family comes from shape; the exact member (Q vs O when H*hd==d)
// is a name/block-order tiebreak resolved by the classifier.
public enum TensorRoleKind
{
    Unknown = 0,

    Norm,            // [d] 1-D scale (rmsnorm / layernorm weight)
    Bias,            // [d] or [out] 1-D additive bias
    Embedding,       // [V, d] input token table
    LmHead,          // [V, d] output unembedding (untied, or name lm_head|output)

    AttnQ,           // [H*hd, d]
    AttnK,           // [Hkv*hd, d]
    AttnV,           // [Hkv*hd, d]
    AttnO,           // [d, H*hd]

    MlpGate,         // [I, d]
    MlpUp,           // [I, d]
    MlpDown,         // [d, I]

    // MoE: an expert stack has first dim == E (num_experts).
    MoeRouter,       // [E, d] gate/router logits
    MoeExpertGate,   // [E, I, d]
    MoeExpertUp,     // [E, I, d]
    MoeExpertDown,   // [E, d, I]
    MoeExpert,       // [E, *] expert stack, member undetermined

    // MLA (DeepSeek-V2/V3): low-rank latent attention projections.
    MlaQDown,        // q_a_proj          [q_lora_rank, d]
    MlaQUp,          // q_b_proj          [H*qk_head_dim, q_lora_rank]
    MlaKvDown,       // kv_a_proj_with_mqa [kv_lora_rank + qk_rope_head_dim, d]
    MlaKvUp,         // kv_b_proj         [H*(qk_nope_head_dim+v_head_dim), kv_lora_rank]

    Conv,            // >=3-D [out,in,kh,kw] — vision/audio, out of token scope
}

// The product surface for a model. Determines which circuit planes can run.
public enum Modality
{
    Text = 0,        // has a token vocab + embedding table — full decrypt
    Vision,          // patch/conv front-end, no token vocab
    Audio,           // waveform/mel front-end, no token vocab
    Diffusion,       // denoiser, no token vocab
    Unknown,
}

// How much of the model the pipeline can faithfully decrypt.
public enum Coverage
{
    Full = 0,        // text model, all anchors present — every plane runs
    Partial,         // recognized but incomplete (e.g. vision tower) — embedding-plane only, never throw
    Unsupported,     // could not classify — deposit recipe scalars only, never throw
}

// One classified tensor.
public sealed record TensorRole(
    string Name,
    int[] Shape,
    string Dtype,
    TensorRoleKind Kind,
    int LayerIndex,      // -1 when not layer-scoped
    int ExpertIndex)     // -1 when not an MoE expert slice
{
    public bool IsLayerScoped => LayerIndex >= 0;
    public bool IsAttention   => Kind is TensorRoleKind.AttnQ or TensorRoleKind.AttnK
                                       or TensorRoleKind.AttnV or TensorRoleKind.AttnO;
    public bool IsMlp         => Kind is TensorRoleKind.MlpGate or TensorRoleKind.MlpUp
                                       or TensorRoleKind.MlpDown;
}

// Config scalars, generalized across model_type. Fields that a given architecture does not declare
// are left at their neutral default (0 / false); consumers must tolerate that. This is never the
// place a missing field throws — see ModelConfigReader and the Coverage verdict.
public sealed record ModelConfig
{
    public required string ModelType    { get; init; }
    public required string Architecture { get; init; }

    public required int VocabSize        { get; init; }
    public required int HiddenSize       { get; init; }   // d
    public required int NumLayers        { get; init; }
    public required int NumHeads         { get; init; }   // H
    public required int NumKvHeads       { get; init; }   // Hkv
    public required int HeadDim          { get; init; }   // hd
    public required int IntermediateSize { get; init; }   // I
    public required int NumExperts       { get; init; }   // E (0 = dense)

    public required bool   TieWordEmbeddings { get; init; }
    public required bool   QkNorm            { get; init; }
    public required double RopeTheta         { get; init; }
    public required double NormEps           { get; init; }

    // MLA latent ranks (0 when the model is not MLA).
    public required int MlaQLoraRank   { get; init; }
    public required int MlaKvLoraRank  { get; init; }
    public required int QkRopeHeadDim  { get; init; }
    public required int QkNopeHeadDim  { get; init; }
    public required int VHeadDim       { get; init; }

    public required Hash128 RecipeEntityId { get; init; }
    public required byte[]  CanonicalJson  { get; init; }

    public bool IsMoe => NumExperts > 0;
    public bool IsMla => MlaKvLoraRank > 0 || MlaQLoraRank > 0;

    // Derived anchor dims used by the magic-number rules.
    public int AttnDim   => NumHeads   * HeadDim;   // H*hd
    public int KvDim     => NumKvHeads * HeadDim;   // Hkv*hd
}

public sealed class ModelManifest
{
    public required ModelConfig Config { get; init; }
    public required IReadOnlyList<TensorRole> Roles { get; init; }
    public required Modality Modality { get; init; }
    public required Coverage Coverage { get; init; }
    public required string ModelName { get; init; }

    public bool TextPlanesRunnable => Coverage == Coverage.Full && Modality == Modality.Text;

    // The input embedding table, if one was classified.
    public TensorRole? Embedding =>
        Roles.FirstOrDefault(r => r.Kind == TensorRoleKind.Embedding);

    // The output unembedding: explicit LmHead, else (tied) the embedding table.
    public TensorRole? LmHead =>
        Roles.FirstOrDefault(r => r.Kind == TensorRoleKind.LmHead) ?? Embedding;

    public int LayerCount
    {
        get
        {
            int max = -1;
            foreach (var r in Roles) if (r.LayerIndex > max) max = r.LayerIndex;
            return max + 1;
        }
    }

    public IEnumerable<TensorRole> ForLayer(int layer) =>
        Roles.Where(r => r.LayerIndex == layer);

    public TensorRole? Single(int layer, TensorRoleKind kind) =>
        Roles.FirstOrDefault(r => r.LayerIndex == layer && r.Kind == kind);
}
