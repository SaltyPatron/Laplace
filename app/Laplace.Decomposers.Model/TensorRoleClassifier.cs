using System.Text.RegularExpressions;

namespace Laplace.Decomposers.Model;

// ── Lane A + Lane E: shape-based tensor-role inference (the "magic number") ────────────────────
// Stops keying off hardcoded tensor names per model_type. Each tensor's structural role is the
// shape matched against config anchors (V, d, H*hd, Hkv*hd, I, E); the name is only a tiebreak
// when two roles share a shape (Q vs O when H*hd==d). This is what scales to every architecture
// in the hub and to MoE / MLA without an enumerated profile per family.
//
//   [d]                 -> Norm (or Bias if name says so)
//   [V, d]              -> Embedding (input) | LmHead (untied, name lm_head|output)
//   [H*hd, d]           -> Q  (HF weight is [out,in])
//   [d, H*hd]           -> O
//   [Hkv*hd, d]         -> K / V  (GQA: smaller first dim than Q)
//   [I, d]              -> Gate / Up   ;   [d, I] -> Down
//   [E, d]              -> MoE router   ;   first dim == E (3-D) -> MoE expert stack
//   q_a/q_b/kv_a/kv_b   -> MLA latent down/up projections (by name + lora ranks)
//   >= 3-D conv         -> Vision/conv (out of token scope)
//   otherwise           -> Unknown
public static partial class TensorRoleClassifier
{
    [GeneratedRegex(@"\.(?:layers?|h|blocks?|encoder\.layer)\.(\d+)\.", RegexOptions.IgnoreCase)]
    private static partial Regex LayerIndexRegex();

    [GeneratedRegex(@"\.experts?\.(\d+)\.", RegexOptions.IgnoreCase)]
    private static partial Regex ExpertIndexRegex();

    public static ModelManifest Build(
        IReadOnlyList<SafetensorsContainerParser.TensorReference> tensors,
        ModelConfigReader.Result config,
        string modelName)
    {
        var cfg = config.Config;
        var roles = new List<TensorRole>(tensors.Count);
        foreach (var t in tensors)
        {
            int layer = ExtractIndex(LayerIndexRegex(), t.Name);
            int expert = ExtractIndex(ExpertIndexRegex(), t.Name);
            var kind = Classify(t.Shape, cfg, t.Name, expert >= 0);
            roles.Add(new TensorRole(t.Name, t.Shape, t.Dtype, kind, layer, expert));
        }

        // Finalize coverage: a "full" config that nevertheless has no classifiable embedding table
        // can only run the embedding-plane (none), so it degrades to partial — never throws.
        Coverage coverage = config.Coverage;
        bool hasEmbedding = roles.Any(r => r.Kind == TensorRoleKind.Embedding);
        if (coverage == Coverage.Full && !hasEmbedding) coverage = Coverage.Partial;

        return new ModelManifest
        {
            Config = cfg,
            Roles = roles,
            Modality = config.Modality,
            Coverage = coverage,
            ModelName = modelName,
        };
    }

    private static int ExtractIndex(Regex rx, string name)
    {
        var m = rx.Match(name);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : -1;
    }

    private static TensorRoleKind Classify(int[] s, ModelConfig c, string name, bool isExpertSlice)
    {
        string nm = name.ToLowerInvariant();
        int rank = s.Length;

        // 1-D: norm scale or additive bias.
        if (rank == 1)
            return nm.Contains(".bias") || nm.EndsWith("_bias") ? TensorRoleKind.Bias : TensorRoleKind.Norm;

        // MLA latent projections are recognized by name + the presence of lora ranks; their shapes
        // ([q_lora,d] / [H*qk,q_lora] / …) collide with ordinary projections, so name leads here.
        if (c.IsMla)
        {
            if (Has(nm, "q_a_proj")) return TensorRoleKind.MlaQDown;
            if (Has(nm, "q_b_proj")) return TensorRoleKind.MlaQUp;
            if (Has(nm, "kv_a_proj")) return TensorRoleKind.MlaKvDown;
            if (Has(nm, "kv_b_proj")) return TensorRoleKind.MlaKvUp;
        }

        // 3-D+: either a fused MoE expert stack ([E, *, *]) or a conv kernel ([out,in,kh,kw]).
        if (rank >= 3)
        {
            if (c.NumExperts > 0 && s[0] == c.NumExperts) return MoeExpertMember(nm);
            return TensorRoleKind.Conv;
        }

        // rank == 2 from here. An expert slice (…experts.{N}.w1) is an MLP member tagged Moe*.
        if (isExpertSlice)
            return ExpertSliceMember(nm, s, c);

        // MoE router: [E, d]. The first dim equals the (small) expert count, the second the hidden.
        if (c.NumExperts > 0 && s[0] == c.NumExperts && Match(s[1], c.HiddenSize)
            && (Has(nm, "gate") || Has(nm, "router")) && !Has(nm, "gate_proj"))
            return TensorRoleKind.MoeRouter;

        int d = c.HiddenSize, V = c.VocabSize, attn = c.AttnDim, kv = c.KvDim, I = c.IntermediateSize;

        // Embedding / LmHead: [V, d].
        if (Match(s[0], V) && Match(s[1], d))
            return (Has(nm, "lm_head") || Has(nm, "output") || Has(nm, "unembed"))
                ? TensorRoleKind.LmHead
                : TensorRoleKind.Embedding;

        // Attention output O: [d, H*hd]. Distinct from Q's [H*hd, d] except when attn==d (MHA),
        // where the name token decides.
        if (Match(s[0], d) && Match(s[1], attn) && IsOName(nm))
            return TensorRoleKind.AttnO;

        // Attention Q: [H*hd, d].
        if (Match(s[0], attn) && Match(s[1], d) && (IsQName(nm) || (!IsKName(nm) && !IsVName(nm) && attn != kv)))
            return TensorRoleKind.AttnQ;

        // Attention K / V: [Hkv*hd, d] (smaller first dim under GQA).
        if (Match(s[0], kv) && Match(s[1], d))
        {
            if (IsKName(nm)) return TensorRoleKind.AttnK;
            if (IsVName(nm)) return TensorRoleKind.AttnV;
        }

        // MLP down: [d, I].
        if (Match(s[0], d) && Match(s[1], I) && IsDownName(nm))
            return TensorRoleKind.MlpDown;

        // MLP gate / up: [I, d].
        if (Match(s[0], I) && Match(s[1], d))
        {
            if (IsGateName(nm)) return TensorRoleKind.MlpGate;
            if (IsUpName(nm))   return TensorRoleKind.MlpUp;
        }

        // Shape known to be a 2-D projection but the name was ambiguous: fall back on the strongest
        // shape signal so the circuit still runs (it is still "a projection in this layer").
        if (Match(s[0], d) && Match(s[1], attn)) return TensorRoleKind.AttnO;
        if (Match(s[0], attn) && Match(s[1], d)) return TensorRoleKind.AttnQ;
        if (Match(s[0], kv) && Match(s[1], d))   return TensorRoleKind.AttnK;
        if (Match(s[0], I) && Match(s[1], d))    return TensorRoleKind.MlpUp;
        if (Match(s[0], d) && Match(s[1], I))    return TensorRoleKind.MlpDown;

        return TensorRoleKind.Unknown;
    }

    private static TensorRoleKind ExpertSliceMember(string nm, int[] s, ModelConfig c)
    {
        // Per-expert 2-D slice: gate/up are [I,d], down is [d,I]; member by name with shape fallback.
        if (IsDownName(nm) || Has(nm, "w2")) return TensorRoleKind.MoeExpertDown;
        if (IsGateName(nm) || Has(nm, "w1")) return TensorRoleKind.MoeExpertGate;
        if (IsUpName(nm) || Has(nm, "w3"))   return TensorRoleKind.MoeExpertUp;
        if (s.Length == 2 && Match(s[0], c.HiddenSize) && Match(s[1], c.IntermediateSize))
            return TensorRoleKind.MoeExpertDown;
        return TensorRoleKind.MoeExpert;
    }

    private static TensorRoleKind MoeExpertMember(string nm)
    {
        if (IsDownName(nm) || Has(nm, "w2")) return TensorRoleKind.MoeExpertDown;
        if (IsGateName(nm) || Has(nm, "w1")) return TensorRoleKind.MoeExpertGate;
        if (IsUpName(nm) || Has(nm, "w3"))   return TensorRoleKind.MoeExpertUp;
        return TensorRoleKind.MoeExpert;
    }

    // Tolerant equality: anchor 0 means "config did not declare this dim", so never matches.
    private static bool Match(int dim, int anchor) => anchor > 0 && dim == anchor;
    private static bool Has(string nm, string token) => nm.Contains(token, StringComparison.Ordinal);

    private static bool IsQName(string nm) => Has(nm, "q_proj") || Has(nm, ".query") || Has(nm, ".wq") || Has(nm, "q_b_proj");
    private static bool IsKName(string nm) => Has(nm, "k_proj") || Has(nm, ".key")   || Has(nm, ".wk");
    private static bool IsVName(string nm) => Has(nm, "v_proj") || Has(nm, ".value") || Has(nm, ".wv");
    private static bool IsOName(string nm) => Has(nm, "o_proj") || Has(nm, "out_proj") || Has(nm, ".wo")
                                            || Has(nm, "attention.output.dense") || Has(nm, "self_attn.dense");
    private static bool IsGateName(string nm) => Has(nm, "gate_proj") || Has(nm, ".w1");
    private static bool IsUpName(string nm)   => Has(nm, "up_proj") || Has(nm, "fc1") || Has(nm, ".w3")
                                              || Has(nm, "intermediate.dense");
    private static bool IsDownName(string nm) => Has(nm, "down_proj") || Has(nm, "fc2") || Has(nm, ".w2")
                                              || (Has(nm, "mlp") && Has(nm, "output.dense"))
                                              || Has(nm, "output.dense") && !Has(nm, "attention");
}
