using System.Text.RegularExpressions;

namespace Laplace.Decomposers.Model;

















public static partial class TensorRoleClassifier
{



    [GeneratedRegex(@"(?:^|\.)(?:layers?|h|blocks?|encoder\.layer)\.(\d+)\.", RegexOptions.IgnoreCase)]
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


        if (rank == 1)
            return nm.Contains(".bias") || nm.EndsWith("_bias") ? TensorRoleKind.Bias : TensorRoleKind.Norm;



        if (c.IsMla)
        {
            if (Has(nm, "q_a_proj")) return TensorRoleKind.MlaQDown;
            if (Has(nm, "q_b_proj")) return TensorRoleKind.MlaQUp;
            if (Has(nm, "kv_a_proj")) return TensorRoleKind.MlaKvDown;
            if (Has(nm, "kv_b_proj")) return TensorRoleKind.MlaKvUp;
        }


        if (rank >= 3)
        {
            if (c.NumExperts > 0 && s[0] == c.NumExperts) return MoeExpertMember(nm);
            return TensorRoleKind.Conv;
        }




        if (rank != 2) return TensorRoleKind.Unknown;


        if (isExpertSlice)
            return ExpertSliceMember(nm, s, c);


        if (c.NumExperts > 0 && s[0] == c.NumExperts && Match(s[1], c.HiddenSize)
            && (Has(nm, "gate") || Has(nm, "router")) && !Has(nm, "gate_proj"))
            return TensorRoleKind.MoeRouter;

        int d = c.HiddenSize, V = c.VocabSize, attn = c.AttnDim, kv = c.KvDim, I = c.IntermediateSize;


        if (Match(s[0], V) && Match(s[1], d))
            return (Has(nm, "lm_head") || Has(nm, "output") || Has(nm, "unembed"))
                ? TensorRoleKind.LmHead
                : TensorRoleKind.Embedding;



        if (Match(s[0], d) && Match(s[1], attn) && IsOName(nm))
            return TensorRoleKind.AttnO;


        if (Match(s[0], attn) && Match(s[1], d) && (IsQName(nm) || (!IsKName(nm) && !IsVName(nm) && attn != kv)))
            return TensorRoleKind.AttnQ;


        if (Match(s[0], kv) && Match(s[1], d))
        {
            if (IsKName(nm)) return TensorRoleKind.AttnK;
            if (IsVName(nm)) return TensorRoleKind.AttnV;
        }


        if (Match(s[0], d) && Match(s[1], I) && IsDownName(nm))
            return TensorRoleKind.MlpDown;


        if (Match(s[0], I) && Match(s[1], d))
        {
            if (IsGateName(nm)) return TensorRoleKind.MlpGate;
            if (IsUpName(nm)) return TensorRoleKind.MlpUp;
        }



        if (Match(s[0], d) && Match(s[1], attn)) return TensorRoleKind.AttnO;
        if (Match(s[0], attn) && Match(s[1], d)) return TensorRoleKind.AttnQ;
        if (Match(s[0], kv) && Match(s[1], d)) return TensorRoleKind.AttnK;
        if (Match(s[0], I) && Match(s[1], d)) return TensorRoleKind.MlpUp;
        if (Match(s[0], d) && Match(s[1], I)) return TensorRoleKind.MlpDown;

        return TensorRoleKind.Unknown;
    }

    private static TensorRoleKind ExpertSliceMember(string nm, int[] s, ModelConfig c)
    {

        if (IsDownName(nm) || Has(nm, "w2")) return TensorRoleKind.MoeExpertDown;
        if (IsGateName(nm) || Has(nm, "w1")) return TensorRoleKind.MoeExpertGate;
        if (IsUpName(nm) || Has(nm, "w3")) return TensorRoleKind.MoeExpertUp;
        if (s.Length == 2 && Match(s[0], c.HiddenSize) && Match(s[1], c.IntermediateSize))
            return TensorRoleKind.MoeExpertDown;
        return TensorRoleKind.MoeExpert;
    }

    private static TensorRoleKind MoeExpertMember(string nm)
    {
        if (IsDownName(nm) || Has(nm, "w2")) return TensorRoleKind.MoeExpertDown;
        if (IsGateName(nm) || Has(nm, "w1")) return TensorRoleKind.MoeExpertGate;
        if (IsUpName(nm) || Has(nm, "w3")) return TensorRoleKind.MoeExpertUp;
        return TensorRoleKind.MoeExpert;
    }


    private static bool Match(int dim, int anchor) => anchor > 0 && dim == anchor;
    private static bool Has(string nm, string token) => nm.Contains(token, StringComparison.Ordinal);

    private static bool IsQName(string nm) => Has(nm, "q_proj") || Has(nm, ".query") || Has(nm, ".wq") || Has(nm, "q_b_proj");
    private static bool IsKName(string nm) => Has(nm, "k_proj") || Has(nm, ".key") || Has(nm, ".wk");
    private static bool IsVName(string nm) => Has(nm, "v_proj") || Has(nm, ".value") || Has(nm, ".wv");
    private static bool IsOName(string nm) => Has(nm, "o_proj") || Has(nm, "out_proj") || Has(nm, ".wo")
                                            || Has(nm, "attention.output.dense") || Has(nm, "self_attn.dense");
    private static bool IsGateName(string nm) => Has(nm, "gate_proj") || Has(nm, ".w1");
    private static bool IsUpName(string nm) => Has(nm, "up_proj") || Has(nm, "fc1") || Has(nm, ".w3")
                                              || Has(nm, "intermediate.dense");
    private static bool IsDownName(string nm) => Has(nm, "down_proj") || Has(nm, "fc2") || Has(nm, ".w2")
                                              || (Has(nm, "mlp") && Has(nm, "output.dense"))
                                              || Has(nm, "output.dense") && !Has(nm, "attention");
}
