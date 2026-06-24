namespace Laplace.Decomposers.Model;

public sealed class ArchitectureProfile
{
    public required string ModelType { get; init; }

    public required bool HasGate   { get; init; }
    public required bool HasBiases { get; init; }
    public required bool RmsNorm   { get; init; }

    
    public required string  EmbedTokens { get; init; }
    public required string? LmHead      { get; init; }
    public required string  FinalNorm   { get; init; }
    public required IReadOnlyList<string> PerLayerNorms { get; init; }

    
    public required string  QProj    { get; init; }
    public required string  KProj    { get; init; }
    public required string  VProj    { get; init; }
    public required string  OProj    { get; init; }
    public required string? GateProj { get; init; }
    public required string  UpProj   { get; init; }
    public required string  DownProj { get; init; }

    
    public required IReadOnlyList<PathSpec> Paths { get; init; }

    // The generic, shape-inferred pipeline (ModelManifest / TensorRoleClassifier) replaces this
    // name-keyed profile, so unknown model_types must NOT throw — partial/unsupported is decided by
    // the manifest's Coverage verdict, never by an exception here. Unknown types fall back to the
    // standard HF decoder naming (model.layers.{L}.self_attn.* / mlp.*), which the manifest path
    // will simply ignore where it does not apply.
    public static ArchitectureProfile For(string modelType) => modelType.ToLowerInvariant() switch
    {
        "llama"  => Llama,
        "phi"    => Phi,
        "qwen2"  => Qwen2,
        "bert"   => Bert,
        _        => Llama,
    };

    public static readonly ArchitectureProfile Llama = new()
    {
        ModelType = "llama",
        HasGate = true, HasBiases = false, RmsNorm = true,
        EmbedTokens   = "model.embed_tokens.weight",
        LmHead        = "lm_head.weight",
        FinalNorm     = "model.norm.weight",
        PerLayerNorms = new[]
        {
            "model.layers.{L}.input_layernorm.weight",
            "model.layers.{L}.post_attention_layernorm.weight",
        },
        QProj    = "model.layers.{L}.self_attn.q_proj.weight",
        KProj    = "model.layers.{L}.self_attn.k_proj.weight",
        VProj    = "model.layers.{L}.self_attn.v_proj.weight",
        OProj    = "model.layers.{L}.self_attn.o_proj.weight",
        GateProj = "model.layers.{L}.mlp.gate_proj.weight",
        UpProj   = "model.layers.{L}.mlp.up_proj.weight",
        DownProj = "model.layers.{L}.mlp.down_proj.weight",
        Paths = new PathSpec[]
        {
            new SelfSimilarityPath("SIMILAR_TO",
                EmbedPattern: "model.embed_tokens.weight"),
            new BilinearPath("ATTENDS",
                LeftPattern:  "model.layers.{L}.self_attn.q_proj.weight",
                RightPattern: "model.layers.{L}.self_attn.k_proj.weight",
                RightIsKv:    true),
            new ProjectionPath("OV_RELATES",
                VPattern: "model.layers.{L}.self_attn.v_proj.weight",
                OPattern: "model.layers.{L}.self_attn.o_proj.weight"),
            new ContractionPath("COMPLETES_TO",
                GatePattern: "model.layers.{L}.mlp.gate_proj.weight",
                UpPattern:   "model.layers.{L}.mlp.up_proj.weight",
                DownPattern: "model.layers.{L}.mlp.down_proj.weight"),
        },
    };

    public static readonly ArchitectureProfile Phi = new()
    {
        ModelType = "phi",
        HasGate = false, HasBiases = true, RmsNorm = false,
        EmbedTokens   = "model.embed_tokens.weight",
        LmHead        = "lm_head.weight",
        FinalNorm     = "model.final_layernorm.weight",
        PerLayerNorms = new[]
        {
            "model.layers.{L}.input_layernorm.weight",
        },
        QProj    = "model.layers.{L}.self_attn.q_proj.weight",
        KProj    = "model.layers.{L}.self_attn.k_proj.weight",
        VProj    = "model.layers.{L}.self_attn.v_proj.weight",
        OProj    = "model.layers.{L}.self_attn.dense.weight",
        GateProj = null,
        UpProj   = "model.layers.{L}.mlp.fc1.weight",
        DownProj = "model.layers.{L}.mlp.fc2.weight",
        Paths = new PathSpec[]
        {
            new SelfSimilarityPath("SIMILAR_TO",
                EmbedPattern: "model.embed_tokens.weight"),
            new BilinearPath("ATTENDS",
                LeftPattern:  "model.layers.{L}.self_attn.q_proj.weight",
                RightPattern: "model.layers.{L}.self_attn.k_proj.weight",
                RightIsKv:    false),
            new ProjectionPath("OV_RELATES",
                VPattern: "model.layers.{L}.self_attn.v_proj.weight",
                OPattern: "model.layers.{L}.self_attn.dense.weight"),
            new ContractionPath("COMPLETES_TO",
                GatePattern: null,
                UpPattern:   "model.layers.{L}.mlp.fc1.weight",
                DownPattern: "model.layers.{L}.mlp.fc2.weight"),
        },
    };

    public static readonly ArchitectureProfile Qwen2 = new()
    {
        ModelType = "qwen2",
        HasGate = true, HasBiases = true, RmsNorm = true,
        EmbedTokens   = "model.embed_tokens.weight",
        LmHead        = "lm_head.weight",
        FinalNorm     = "model.norm.weight",
        PerLayerNorms = new[]
        {
            "model.layers.{L}.input_layernorm.weight",
            "model.layers.{L}.post_attention_layernorm.weight",
        },
        QProj    = "model.layers.{L}.self_attn.q_proj.weight",
        KProj    = "model.layers.{L}.self_attn.k_proj.weight",
        VProj    = "model.layers.{L}.self_attn.v_proj.weight",
        OProj    = "model.layers.{L}.self_attn.o_proj.weight",
        GateProj = "model.layers.{L}.mlp.gate_proj.weight",
        UpProj   = "model.layers.{L}.mlp.up_proj.weight",
        DownProj = "model.layers.{L}.mlp.down_proj.weight",
        Paths = new PathSpec[]
        {
            new SelfSimilarityPath("SIMILAR_TO",
                EmbedPattern: "model.embed_tokens.weight"),
            new BilinearPath("ATTENDS",
                LeftPattern:  "model.layers.{L}.self_attn.q_proj.weight",
                RightPattern: "model.layers.{L}.self_attn.k_proj.weight",
                RightIsKv:    true),
            new ProjectionPath("OV_RELATES",
                VPattern: "model.layers.{L}.self_attn.v_proj.weight",
                OPattern: "model.layers.{L}.self_attn.o_proj.weight"),
            new ContractionPath("COMPLETES_TO",
                GatePattern: "model.layers.{L}.mlp.gate_proj.weight",
                UpPattern:   "model.layers.{L}.mlp.up_proj.weight",
                DownPattern: "model.layers.{L}.mlp.down_proj.weight"),
        },
    };

    
    
    
    
    
    
    public static readonly ArchitectureProfile Bert = new()
    {
        ModelType = "bert",
        HasGate = false, HasBiases = true, RmsNorm = false,
        EmbedTokens   = "embeddings.word_embeddings.weight",
        LmHead        = null,
        FinalNorm     = "embeddings.LayerNorm.weight",
        PerLayerNorms = new[]
        {
            "encoder.layer.{L}.attention.output.LayerNorm.weight",
            "encoder.layer.{L}.output.LayerNorm.weight",
        },
        QProj    = "encoder.layer.{L}.attention.self.query.weight",
        KProj    = "encoder.layer.{L}.attention.self.key.weight",
        VProj    = "encoder.layer.{L}.attention.self.value.weight",
        OProj    = "encoder.layer.{L}.attention.output.dense.weight",
        GateProj = null,
        UpProj   = "encoder.layer.{L}.intermediate.dense.weight",
        DownProj = "encoder.layer.{L}.output.dense.weight",
        Paths = new PathSpec[]
        {
            new SelfSimilarityPath("SIMILAR_TO",
                EmbedPattern: "embeddings.word_embeddings.weight"),
            new BilinearPath("ATTENDS",
                LeftPattern:  "encoder.layer.{L}.attention.self.query.weight",
                RightPattern: "encoder.layer.{L}.attention.self.key.weight",
                RightIsKv:    false),
            new ProjectionPath("OV_RELATES",
                VPattern: "encoder.layer.{L}.attention.self.value.weight",
                OPattern: "encoder.layer.{L}.attention.output.dense.weight"),
            new ContractionPath("COMPLETES_TO",
                GatePattern: null,
                UpPattern:   "encoder.layer.{L}.intermediate.dense.weight",
                DownPattern: "encoder.layer.{L}.output.dense.weight"),
        },
    };

    public static string Layer(string template, int layer) =>
        template.Replace("{L}", layer.ToString());

    public static string BiasOf(string weightName) =>
        weightName.EndsWith(".weight", StringComparison.Ordinal)
            ? weightName[..^".weight".Length] + ".bias"
            : weightName + ".bias";
}
