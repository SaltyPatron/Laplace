namespace Laplace.Decomposers.Model;

/// <summary>
/// Source-scoped role of a recognized checkpoint tensor. These are coordinates of
/// the conventional source architecture (INVENTIONS #58), resolved from the
/// governed operator templates; they are not the native ontology of cognition.
/// </summary>
public enum TensorRoleKind
{
    Unknown = 0,

    Norm,
    Embedding,
    LmHead,
    PositionEmbedding,
    SegmentEmbedding,

    AttnQ,
    AttnK,
    AttnV,
    AttnO,
    AttnQkv,

    MlpGate,
    MlpUp,
    MlpDown,
    MlpGateUp,

    MoeRouter,
    MoeExpertGate,
    MoeExpertUp,
    MoeExpertDown,

    LowRankDown,
    LowRankUp,
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

/// <summary>
/// One recognized slot binding: the tensor, its source-scoped role, the operator
/// and slot that recognized it, the layout it matched in ("out,in" row-major
/// projection, "in,out" transposed, "square" undecided, "-" not a matrix) and its
/// bias sibling when the checkpoint has one.
/// </summary>
public sealed record TensorRole(
    string Name,
    int[] Shape,
    string Dtype,
    TensorRoleKind Kind,
    int LayerIndex,
    int ExpertIndex,
    string Operator = "",
    string Slot = "",
    string Orientation = "-",
    string? Bias = null)
{
    public bool IsLayerScoped => LayerIndex >= 0;

    /// <summary>A row-major [out, in] projection that numeric circuits may consume as stored.</summary>
    public bool IsRowMajorProjection => Orientation is "out,in" or "-";
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
    /// <summary>Config activation identity (<c>hidden_act</c> / aliases). Empty if absent.</summary>
    public required string HiddenAct { get; init; }

    public required int MlaQLoraRank { get; init; }
    public required int MlaKvLoraRank { get; init; }
    public required int QkRopeHeadDim { get; init; }
    public required int QkNopeHeadDim { get; init; }
    public required int VHeadDim { get; init; }

    public bool IsMoe => NumExperts > 0;
    public bool IsMla => MlaKvLoraRank > 0 || MlaQLoraRank > 0;

    public int AttnDim => NumHeads * HeadDim;
    public int KvDim => NumKvHeads * HeadDim;
}

/// <summary>
/// The recognized checkpoint: config dimensions re-bound to the symbols the shapes
/// proved, the full operator anatomy, and the role of every recognized tensor.
/// Built from headers, config and tokenizer size only, before any write, so an
/// unfamiliar architecture is reported rather than failing mid-run.
/// </summary>
public sealed class ModelManifest
{
    public required ModelConfig Config { get; init; }
    public required ModelAnatomy Anatomy { get; init; }
    public required IReadOnlyList<TensorRole> Roles { get; init; }
    public required Modality Modality { get; init; }
    public required Coverage Coverage { get; init; }
    public required string ModelName { get; init; }

    /// <summary>
    /// Native FFN activation code for the config's declared function, or null when
    /// the checkpoint declares one the native FFN operator does not implement; the
    /// FFN circuits are then reported and skipped rather than substituted.
    /// </summary>
    public int? FfnActivation(bool gated)
    {
        string a = Config.HiddenAct.Trim().ToLowerInvariant();
        if (a.Length == 0) a = gated ? "silu" : "";
        return a switch
        {
            "gelu" => 1,
            "gelu_new" or "gelu_fast" or "gelu_pytorch_tanh" => 2,
            "quick_gelu" => 3,
            "relu" => 4,
            "silu" or "swish" => gated ? 0 : 5,
            _ => null,
        };
    }

    public bool TextPlanesRunnable => Coverage == Coverage.Full && Modality == Modality.Text;

    public TensorRole? Embedding => Roles.FirstOrDefault(r => r.Kind == TensorRoleKind.Embedding);

    public TensorRole? LmHead =>
        Roles.FirstOrDefault(r => r.Kind == TensorRoleKind.LmHead) ?? Embedding;

    public int LayerCount => (int)(Anatomy.Symbol("L") ?? 0);

    public IEnumerable<TensorRole> ForLayer(int layer) => Roles.Where(r => r.LayerIndex == layer);

    public TensorRole? Single(int layer, TensorRoleKind kind) =>
        Roles.FirstOrDefault(r => r.LayerIndex == layer && r.ExpertIndex < 0 && r.Kind == kind);

    public TensorRole? Norm(int layer, string slot) =>
        Roles.FirstOrDefault(r => r.LayerIndex == layer && r.Kind == TensorRoleKind.Norm && r.Slot == slot);

    public static ModelManifest Recognize(
        IReadOnlyList<SafetensorsContainerParser.TensorReference> headers,
        ModelConfigReader.Result config,
        int? tokenizerSize,
        string modelName)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return Recognize(
            headers.Select(h => new HeaderTensor(h.Name, h.Dtype, h.Shape)).ToArray(),
            config, tokenizerSize, modelName);
    }

    public static ModelManifest Recognize(
        IReadOnlyList<HeaderTensor> tensors,
        ModelConfigReader.Result config,
        int? tokenizerSize,
        string modelName)
    {
        ArgumentNullException.ThrowIfNull(tensors);
        ArgumentNullException.ThrowIfNull(config);
        ModelAnatomy anatomy = ModelOperatorRecognizer.Recognize(tensors, config.IntegerFields, tokenizerSize);
        var byName = tensors.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var roles = new List<TensorRole>();
        foreach (OperatorInstance op in anatomy.Operators)
        {
            foreach (SlotBinding slot in op.Slots)
            {
                if (slot.Tensor is not { } name) continue;
                HeaderTensor t = byName[name];
                roles.Add(new TensorRole(
                    name, t.Shape, t.Dtype, KindOf(op, slot.Role), op.Block, op.Expert,
                    op.Operator, slot.Role, slot.Orientation, slot.Bias));
            }
        }

        ModelConfig cfg = config.Config;
        int Sym(string name, int fallback) =>
            anatomy.Symbol(name) is { } v && v <= int.MaxValue ? (int)v : fallback;
        cfg = cfg with
        {
            HiddenSize = Sym("d", cfg.HiddenSize),
            VocabSize = Sym("V", cfg.VocabSize),
            NumLayers = Sym("L", cfg.NumLayers),
            NumHeads = Sym("h", cfg.NumHeads),
            NumKvHeads = Sym("h_kv", cfg.NumKvHeads),
            HeadDim = Sym("d_h", cfg.HeadDim),
            IntermediateSize = Sym("f", cfg.IntermediateSize),
            NumExperts = Sym("E", cfg.NumExperts),
        };

        Coverage coverage = config.Coverage;
        if (tensors.Count == 0) coverage = coverage == Coverage.Full ? Coverage.Partial : coverage;
        else if (roles.All(r => r.Kind != TensorRoleKind.Embedding)) coverage = Coverage.Partial;
        else if (config.Modality == Modality.Text && coverage == Coverage.Unsupported) coverage = Coverage.Partial;

        return new ModelManifest
        {
            Config = cfg,
            Anatomy = anatomy,
            Roles = roles,
            Modality = config.Modality,
            Coverage = coverage,
            ModelName = modelName,
        };
    }

    private static TensorRoleKind KindOf(OperatorInstance op, string role)
    {
        bool expert = op.Scope == "expert" || op.Family == "experts";
        return (op.Family, role) switch
        {
            ("vocabulary", "embedding") => TensorRoleKind.Embedding,
            ("vocabulary", "unembedding") => TensorRoleKind.LmHead,
            ("position", _) => TensorRoleKind.PositionEmbedding,
            ("segment", _) => TensorRoleKind.SegmentEmbedding,
            ("model-norm", _) or ("block-norm", _) => TensorRoleKind.Norm,
            ("attention", "q") => TensorRoleKind.AttnQ,
            ("attention", "k") => TensorRoleKind.AttnK,
            ("attention", "v") => TensorRoleKind.AttnV,
            ("attention", "o") => TensorRoleKind.AttnO,
            ("attention", "qkv") => TensorRoleKind.AttnQkv,
            ("attention", "q-norm") or ("attention", "k-norm") or ("attention", "kv-norm") => TensorRoleKind.Norm,
            ("mlp", "gate") or ("experts", "gate") => expert ? TensorRoleKind.MoeExpertGate : TensorRoleKind.MlpGate,
            ("mlp", "up") or ("experts", "up") => expert ? TensorRoleKind.MoeExpertUp : TensorRoleKind.MlpUp,
            ("mlp", "down") or ("experts", "down") => expert ? TensorRoleKind.MoeExpertDown : TensorRoleKind.MlpDown,
            ("mlp", "gate-up") => TensorRoleKind.MlpGateUp,
            ("router", _) => TensorRoleKind.MoeRouter,
            ("low-rank", "down") => TensorRoleKind.LowRankDown,
            ("low-rank", "up") => TensorRoleKind.LowRankUp,
            _ => TensorRoleKind.Unknown,
        };
    }
}
