namespace Laplace.Decomposers.Model;

public sealed record ArchitectureProfile
{
    public required string ModelType { get; init; }

    public required bool HasGate { get; init; }
    public required bool HasBiases { get; init; }
    public required bool RmsNorm { get; init; }


    public required string EmbedTokens { get; init; }
    public required string? LmHead { get; init; }
    public required string FinalNorm { get; init; }
    public required IReadOnlyList<string> PerLayerNorms { get; init; }


    public required string QProj { get; init; }
    public required string KProj { get; init; }
    public required string VProj { get; init; }
    public required string OProj { get; init; }
    public required string? GateProj { get; init; }
    public required string UpProj { get; init; }
    public required string DownProj { get; init; }


    public required IReadOnlyList<PathSpec> Paths { get; init; }

    // Probe-input roles (campaign doc 26, BERT defects b/c): the layer-0 input
    // for a single-token probe is LN(E[t] + P[0] + S[0]; gamma, beta) for
    // BERT-family — additive embedding terms plus a TRUE LayerNorm, computed
    // per token at scrape time, never folded into weight columns. Null on
    // families whose probe input is the raw embedding row.
    public string? PositionEmbeddings { get; init; }
    public string? TokenTypeEmbeddings { get; init; }
    public string? EmbeddingNormWeight { get; init; }
    public string? EmbeddingNormBias { get; init; }

    // Family-default ε / activation — BindWitnessed overlays config.json values.
    public double NormEps { get; init; } = 1e-6;
    public string HiddenAct { get; init; } = "silu";

    public static ArchitectureProfile For(string modelType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelType);
        string normalized = modelType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "llama" => Llama,
            "phi" => Phi,
            "qwen2" => Qwen2,
            "bert" => Bert,
            _ => throw new NotSupportedException(
                $"Unsupported model_type '{modelType}'. Refusing to decompose it with a different architecture profile."),
        };
    }

    /// <summary>
    /// Family skeleton + config-witnessed scalars. NormEps and HiddenAct are
    /// source-asserted (#540/#541); static profiles only carry family defaults.
    /// </summary>
    public static ArchitectureProfile For(ModelConfig cfg)
    {
        var skeleton = For(cfg.ModelType);
        string act = string.IsNullOrWhiteSpace(cfg.HiddenAct) ? skeleton.HiddenAct : cfg.HiddenAct;
        return skeleton with { NormEps = cfg.NormEps, HiddenAct = act };
    }

    /// <summary>
    /// Native <c>ffn_write_vectors_d</c> act code from the witnessed identity:
    /// 0 = SiLU-gated (requires a gate tensor), 1 = erf-GELU ungated.
    /// Unknown strings keep the prior gate-presence heuristic so ingest does not refuse.
    /// </summary>
    public int ResolveFfnActCode(bool gatePresent)
    {
        string a = HiddenAct.Trim().ToLowerInvariant();
        if (a is "gelu" or "gelu_new" or "gelu_fast" or "gelu_pytorch_tanh" or "quick_gelu")
            return 1;
        if (a is "silu" or "swish")
            return gatePresent ? 0 : 1;
        return gatePresent ? 0 : 1;
    }

    public static readonly ArchitectureProfile Llama = new()
    {
        ModelType = "llama",
        HasGate = true,
        HasBiases = false,
        RmsNorm = true,
        HiddenAct = "silu",
        NormEps = 1e-5,
        EmbedTokens = "model.embed_tokens.weight",
        LmHead = "lm_head.weight",
        FinalNorm = "model.norm.weight",
        PerLayerNorms = new[]
        {
            "model.layers.{L}.input_layernorm.weight",
            "model.layers.{L}.post_attention_layernorm.weight",
        },
        QProj = "model.layers.{L}.self_attn.q_proj.weight",
        KProj = "model.layers.{L}.self_attn.k_proj.weight",
        VProj = "model.layers.{L}.self_attn.v_proj.weight",
        OProj = "model.layers.{L}.self_attn.o_proj.weight",
        GateProj = "model.layers.{L}.mlp.gate_proj.weight",
        UpProj = "model.layers.{L}.mlp.up_proj.weight",
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
        HasGate = false,
        HasBiases = true,
        RmsNorm = false,
        HiddenAct = "gelu_new",
        NormEps = 1e-5,
        EmbedTokens = "model.embed_tokens.weight",
        LmHead = "lm_head.weight",
        FinalNorm = "model.final_layernorm.weight",
        PerLayerNorms = new[]
        {
            "model.layers.{L}.input_layernorm.weight",
        },
        QProj = "model.layers.{L}.self_attn.q_proj.weight",
        KProj = "model.layers.{L}.self_attn.k_proj.weight",
        VProj = "model.layers.{L}.self_attn.v_proj.weight",
        OProj = "model.layers.{L}.self_attn.dense.weight",
        GateProj = null,
        UpProj = "model.layers.{L}.mlp.fc1.weight",
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
        HasGate = true,
        HasBiases = true,
        RmsNorm = true,
        HiddenAct = "silu",
        NormEps = 1e-6,
        EmbedTokens = "model.embed_tokens.weight",
        LmHead = "lm_head.weight",
        FinalNorm = "model.norm.weight",
        PerLayerNorms = new[]
        {
            "model.layers.{L}.input_layernorm.weight",
            "model.layers.{L}.post_attention_layernorm.weight",
        },
        QProj = "model.layers.{L}.self_attn.q_proj.weight",
        KProj = "model.layers.{L}.self_attn.k_proj.weight",
        VProj = "model.layers.{L}.self_attn.v_proj.weight",
        OProj = "model.layers.{L}.self_attn.o_proj.weight",
        GateProj = "model.layers.{L}.mlp.gate_proj.weight",
        UpProj = "model.layers.{L}.mlp.up_proj.weight",
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
        HasGate = false,
        HasBiases = true,
        RmsNorm = false,
        HiddenAct = "gelu",
        PositionEmbeddings = "embeddings.position_embeddings.weight",
        TokenTypeEmbeddings = "embeddings.token_type_embeddings.weight",
        EmbeddingNormWeight = "embeddings.LayerNorm.weight",
        EmbeddingNormBias = "embeddings.LayerNorm.bias",
        NormEps = 1e-12,
        EmbedTokens = "embeddings.word_embeddings.weight",
        LmHead = null,
        FinalNorm = "embeddings.LayerNorm.weight",
        PerLayerNorms = new[]
        {
            "encoder.layer.{L}.attention.output.LayerNorm.weight",
            "encoder.layer.{L}.output.LayerNorm.weight",
        },
        QProj = "encoder.layer.{L}.attention.self.query.weight",
        KProj = "encoder.layer.{L}.attention.self.key.weight",
        VProj = "encoder.layer.{L}.attention.self.value.weight",
        OProj = "encoder.layer.{L}.attention.output.dense.weight",
        GateProj = null,
        UpProj = "encoder.layer.{L}.intermediate.dense.weight",
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
