using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;










public enum TensorRoleKind
{
    Unknown = 0,

    Norm,
    Bias,
    Embedding,
    LmHead,

    AttnQ,
    AttnK,
    AttnV,
    AttnO,

    MlpGate,
    MlpUp,
    MlpDown,


    MoeRouter,
    MoeExpertGate,
    MoeExpertUp,
    MoeExpertDown,
    MoeExpert,


    MlaQDown,
    MlaQUp,
    MlaKvDown,
    MlaKvUp,

    Conv,
}


public enum Modality
{
    Text = 0,
    Vision,
    Audio,
    Diffusion,
    Unknown,
}


public enum Coverage
{
    Full = 0,
    Partial,
    Unsupported,
}


public sealed record TensorRole(
    string Name,
    int[] Shape,
    string Dtype,
    TensorRoleKind Kind,
    int LayerIndex,
    int ExpertIndex)
{
    public bool IsLayerScoped => LayerIndex >= 0;
    public bool IsAttention => Kind is TensorRoleKind.AttnQ or TensorRoleKind.AttnK
                                       or TensorRoleKind.AttnV or TensorRoleKind.AttnO;
    public bool IsMlp => Kind is TensorRoleKind.MlpGate or TensorRoleKind.MlpUp
                                       or TensorRoleKind.MlpDown;
}




public sealed record ModelConfig
{
    public required string ModelType { get; init; }
    public required string Architecture { get; init; }

    public required int VocabSize { get; init; }
    public required int HiddenSize { get; init; }
    public required int NumLayers { get; init; }
    public required int NumHeads { get; init; }
    public required int NumKvHeads { get; init; }
    public required int HeadDim { get; init; }
    public required int IntermediateSize { get; init; }
    public required int NumExperts { get; init; }

    public required bool TieWordEmbeddings { get; init; }
    public required bool QkNorm { get; init; }
    public required double RopeTheta { get; init; }
    public required double NormEps { get; init; }


    public required int MlaQLoraRank { get; init; }
    public required int MlaKvLoraRank { get; init; }
    public required int QkRopeHeadDim { get; init; }
    public required int QkNopeHeadDim { get; init; }
    public required int VHeadDim { get; init; }

    public required Hash128 RecipeEntityId { get; init; }
    public required byte[] CanonicalJson { get; init; }

    public bool IsMoe => NumExperts > 0;
    public bool IsMla => MlaKvLoraRank > 0 || MlaQLoraRank > 0;


    public int AttnDim => NumHeads * HeadDim;
    public int KvDim => NumKvHeads * HeadDim;
}

public sealed class ModelManifest
{
    public required ModelConfig Config { get; init; }
    public required IReadOnlyList<TensorRole> Roles { get; init; }
    public required Modality Modality { get; init; }
    public required Coverage Coverage { get; init; }
    public required string ModelName { get; init; }

    public bool TextPlanesRunnable => Coverage == Coverage.Full && Modality == Modality.Text;


    public TensorRole? Embedding =>
        Roles.FirstOrDefault(r => r.Kind == TensorRoleKind.Embedding);


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









    private static bool NameHas(TensorRole r, string token) =>
        r.Name.Contains(token, StringComparison.OrdinalIgnoreCase);
    private static bool IsQkNorm(TensorRole r) => NameHas(r, "q_norm") || NameHas(r, "k_norm");
    private static bool IsLatentNorm(TensorRole r) => NameHas(r, "q_a_layernorm") || NameHas(r, "kv_a_layernorm");
    private static bool IsPostNorm(TensorRole r) => NameHas(r, "post_attention") || NameHas(r, "ffn_norm")
                                                   || NameHas(r, "ln_2") || NameHas(r, "post_ln");

    private IEnumerable<TensorRole> LayerNorms(int layer) =>
        Roles.Where(r => r.LayerIndex == layer && r.Kind == TensorRoleKind.Norm);




    public TensorRole? InputNorm(int layer)
    {
        var norms = LayerNorms(layer).ToList();
        var named = norms.FirstOrDefault(r => NameHas(r, "input_layernorm") || NameHas(r, "attention_norm")
                                           || NameHas(r, "ln_1") || NameHas(r, "pre_ln"));
        if (named is not null) return named;

        var plain = norms.Where(r => !IsQkNorm(r) && !IsPostNorm(r) && !IsLatentNorm(r)).ToList();
        return plain.Count == 1 ? plain[0] : null;
    }


    public TensorRole? PostAttnNorm(int layer) =>
        LayerNorms(layer).FirstOrDefault(IsPostNorm) ?? InputNorm(layer);


    public TensorRole? QNorm(int layer) => LayerNorms(layer).FirstOrDefault(r => NameHas(r, "q_norm"));
    public TensorRole? KNorm(int layer) => LayerNorms(layer).FirstOrDefault(r => NameHas(r, "k_norm"));


    public TensorRole? QaLatentNorm(int layer) => LayerNorms(layer).FirstOrDefault(r => NameHas(r, "q_a_layernorm"));
    public TensorRole? KvaLatentNorm(int layer) => LayerNorms(layer).FirstOrDefault(r => NameHas(r, "kv_a_layernorm"));
}
