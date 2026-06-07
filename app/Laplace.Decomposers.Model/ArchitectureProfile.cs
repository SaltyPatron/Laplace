namespace Laplace.Decomposers.Model;

public sealed class ArchitectureProfile
{
    public required string ModelType { get; init; }

    public required bool HasGate { get; init; }
    public required bool HasBiases { get; init; }
    public required bool RmsNorm { get; init; }

    public required string  EmbedTokens   { get; init; }
    public required string? LmHead        { get; init; }
    public required string  FinalNorm     { get; init; }
    public required IReadOnlyList<string> PerLayerNorms { get; init; }
    public required string  QProj         { get; init; }
    public required string  KProj         { get; init; }
    public required string  VProj         { get; init; }
    public required string  OProj         { get; init; }
    public required string? GateProj      { get; init; }
    public required string  UpProj        { get; init; }
    public required string  DownProj      { get; init; }

    public static ArchitectureProfile For(string modelType) => modelType.ToLowerInvariant() switch
    {
        "llama" => Llama,
        "phi"   => Phi,
        _ => throw new NotSupportedException(
            $"no ArchitectureProfile for model_type '{modelType}' — add it"),
    };

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

    public static string Layer(string template, int layer) => template.Replace("{L}", layer.ToString());

    public static string BiasOf(string weightName) =>
        weightName.EndsWith(".weight", StringComparison.Ordinal)
            ? weightName[..^".weight".Length] + ".bias"
            : weightName + ".bias";
}
