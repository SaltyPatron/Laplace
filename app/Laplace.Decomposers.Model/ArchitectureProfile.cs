namespace Laplace.Decomposers.Model;

/// <summary>
/// Architecture profile — the ingestion-side <c>IArchitectureTemplate</c> axis per ADR 0043
/// (<c>ContainerFormat × TensorDtypeDecoder × IArchitectureTemplate × ModalityBinder</c>).
/// Maps per-family tensor <b>roles</b> to safetensors name templates + structural flags, so
/// the universal <see cref="WeightTensorETL"/> ingests by role rather than hardcoded Llama
/// names (its prior shape, called out as wrong in <see cref="LlamaWeightExtractor"/>'s header).
///
/// <para>
/// INTERIM: families are hardcoded here for <c>llama</c> + <c>phi</c>. ADR 0056's end state
/// migrates this to per-family math registered as <b>data on architecture-template substrate
/// entities</b> (looked up at ingest); the seam (role-driven ETL) is unchanged by that move —
/// only where the role/name/math data lives.
/// </para>
///
/// <para>
/// Role→kind is fixed across families (q/k→Q_PROJECTS joint, v→V, o→O, gate→GATES, up→UP,
/// down→DOWN, norm→NORMALIZES, embed→EMBEDS, lm_head→OUTPUT_PROJECTS); families differ in
/// (a) which roles exist (Phi has no gate), (b) tensor names (Phi o=<c>dense</c>,
/// up=<c>fc1</c>, down=<c>fc2</c>), (c) biases present, (d) norm type, (e) per-layer norm count.
/// <c>{L}</c> in a template is the layer index.
/// </para>
/// </summary>
public sealed class ArchitectureProfile
{
    public required string ModelType { get; init; }

    // ── structural flags ──────────────────────────────────────────────
    /// <summary>SwiGLU gate present (Llama) vs plain MLP (Phi: fc1→act→fc2, no gate).</summary>
    public required bool HasGate { get; init; }
    /// <summary>Projection/norm bias tensors present (Phi) vs absent (Llama).</summary>
    public required bool HasBiases { get; init; }
    /// <summary>true = RMSNorm (Llama, no bias); false = LayerNorm (Phi, with bias).</summary>
    public required bool RmsNorm { get; init; }

    // ── tensor name templates ({L} = layer index); null = role absent ──
    public required string  EmbedTokens   { get; init; }   // model.embed_tokens.weight
    public required string? LmHead        { get; init; }   // lm_head.weight (null ⇒ tied)
    public required string  FinalNorm     { get; init; }   // model.norm / model.final_layernorm
    /// <summary>Per-layer norm name templates: 2 for Llama (input + post-attn), 1 for Phi
    /// (input only; parallel block).</summary>
    public required IReadOnlyList<string> PerLayerNorms { get; init; }
    public required string  QProj         { get; init; }
    public required string  KProj         { get; init; }
    public required string  VProj         { get; init; }
    /// <summary>Attention output projection: Llama <c>o_proj</c>, Phi <c>dense</c>.</summary>
    public required string  OProj         { get; init; }
    /// <summary>SwiGLU gate; null when <see cref="HasGate"/> is false (Phi).</summary>
    public required string? GateProj      { get; init; }
    /// <summary>MLP up/in projection: Llama <c>up_proj</c>, Phi <c>fc1</c>.</summary>
    public required string  UpProj        { get; init; }
    /// <summary>MLP down/out projection: Llama <c>down_proj</c>, Phi <c>fc2</c>.</summary>
    public required string  DownProj      { get; init; }

    /// <summary>Resolve a profile from the recipe's <c>model_type</c>. Throws for an
    /// unmapped family rather than silently mis-ingesting (absence-is-signal discipline).</summary>
    public static ArchitectureProfile For(string modelType) => modelType.ToLowerInvariant() switch
    {
        "llama" => Llama,
        "phi"   => Phi,
        _ => throw new NotSupportedException(
            $"no ArchitectureProfile for model_type '{modelType}' — add it (ADR 0043/0056)"),
    };

    /// <summary>Llama family (TinyLlama, Llama 2/3, Qwen-Llama): SwiGLU gated MLP, RMSNorm,
    /// no biases, two per-layer norms.</summary>
    public static readonly ArchitectureProfile Llama = new()
    {
        ModelType = "llama",
        HasGate = true, HasBiases = false, RmsNorm = true,
        EmbedTokens = "model.embed_tokens.weight",
        LmHead      = "lm_head.weight",
        FinalNorm   = "model.norm.weight",
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
    };

    /// <summary>Phi family (Phi-2, <c>PhiForCausalLM</c>): plain GELU MLP (fc1/fc2, no gate),
    /// LayerNorm with bias, biases on all projections, single per-layer norm (parallel block),
    /// attention output named <c>dense</c>, partial rotary. Verified against
    /// /vault/models/.../phi-2/config.json (hidden 2560, 32 layers, vocab 51200).</summary>
    public static readonly ArchitectureProfile Phi = new()
    {
        ModelType = "phi",
        HasGate = false, HasBiases = true, RmsNorm = false,
        EmbedTokens = "model.embed_tokens.weight",
        LmHead      = "lm_head.weight",
        FinalNorm   = "model.final_layernorm.weight",
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
    };

    /// <summary>Resolve a per-layer template to a concrete tensor name.</summary>
    public static string Layer(string template, int layer) => template.Replace("{L}", layer.ToString());

    /// <summary>The bias sibling of a <c>.weight</c> tensor name (only meaningful when
    /// <see cref="HasBiases"/>).</summary>
    public static string BiasOf(string weightName) =>
        weightName.EndsWith(".weight", StringComparison.Ordinal)
            ? weightName[..^".weight".Length] + ".bias"
            : weightName + ".bias";
}
