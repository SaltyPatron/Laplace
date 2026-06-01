using System.Text.RegularExpressions;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Generic, shape-driven model structure detection — ONE path for any transformer
/// (llama / phi / qwen / deepseek / mixtral-MoE / …), NO per-family code.
///
/// Dims come from the generic HF config keys every transformer shares (hidden_size,
/// num_hidden_layers, num_attention_heads, num_key_value_heads, intermediate_size,
/// vocab_size); roles are detected from each tensor's SHAPE, validated against those
/// dims. Shape alone classifies the unambiguous cases (embedding = the [vocab,·] tensor;
/// MLP encode/decode at the intermediate width; attention encode/decode at the head
/// widths). Where shape is genuinely ambiguous — q vs o when n_heads·head_dim == hidden,
/// k vs v, gate vs up — a UNIVERSAL role token (q/k/v/o/dense/wo/out, gate/up/down/fc1/
/// fc2/w1/w2/w3/c_fc/c_proj) breaks the tie. That universal map is shared by every model;
/// it is not a family branch. Each detected circuit is then read the same way through the
/// embedding address book by <see cref="WeightTensorETL"/>.
/// </summary>
public sealed class ModelGeometry
{
    public enum Role { Unknown, Embedding, Unembedding, Q, K, V, O, Up, Gate, Down, Norm, Bias }
    public enum CircuitKind { QK, OV, FFN }

    public sealed record TensorInfo(string Name, int[] Shape, int Layer, int Expert, Role Role);
    /// <summary>A detected interior circuit: Encode·Decodeᵀ read per dimension as
    /// [n-gram]⇒{tokens}. QK: Encode=q, Decode=k. OV: Encode=v, Decode=o. FFN: Encode=up,
    /// Decode=down. Layer + Expert (Expert = -1 for dense models) locate the witness.</summary>
    public sealed record Circuit(int Layer, int Expert, CircuitKind Kind, string Encode, string Decode);

    public int Vocab { get; }
    public int DModel { get; }
    public int NHeads { get; }
    public int NKv { get; }
    public int HeadDim { get; }
    public int Interm { get; }
    public int NLayers { get; }
    public string? EmbeddingName { get; }
    public string? UnembeddingName { get; }
    public IReadOnlyList<TensorInfo> Tensors { get; }
    public IReadOnlyList<Circuit> Circuits { get; }

    private ModelGeometry(int vocab, int dModel, int nHeads, int nKv, int headDim, int interm, int nLayers,
        string? embed, string? unembed, IReadOnlyList<TensorInfo> tensors, IReadOnlyList<Circuit> circuits)
    {
        Vocab = vocab; DModel = dModel; NHeads = nHeads; NKv = nKv; HeadDim = headDim;
        Interm = interm; NLayers = nLayers; EmbeddingName = embed; UnembeddingName = unembed;
        Tensors = tensors; Circuits = circuits;
    }

    private static readonly Regex LayerRx  = new(@"(?:^|\.)(?:layers?|h|blocks?)\.(\d+)\.", RegexOptions.Compiled);
    private static readonly Regex ExpertRx = new(@"(?:^|\.)experts?\.(\d+)\.", RegexOptions.Compiled);

    /// <summary>Detect geometry + circuits from generic config dims + the tensor headers
    /// (name, shape) — sharded or single-file, the caller supplies the union.</summary>
    public static ModelGeometry Detect(
        int vocab, int dModel, int nHeads, int nKv, int headDim, int interm, int nLayers,
        IEnumerable<(string name, int[] shape)> tensors)
    {
        int qW = nHeads * headDim, kvW = nKv * headDim;
        var infos = new List<TensorInfo>();
        string? embed = null, unembed = null;

        foreach (var (name, shape) in tensors)
        {
            int layer  = Match(LayerRx, name);
            int expert = Match(ExpertRx, name);
            Role role  = Classify(name, shape, vocab, dModel, qW, kvW, interm);
            if (role == Role.Embedding   && embed   is null) embed   = name;
            if (role == Role.Unembedding && unembed is null) unembed = name;
            infos.Add(new TensorInfo(name, shape, layer, expert, role));
        }
        // Tied embeddings: no separate unembedding tensor → the embedding is the address book
        // for both sides. Untied: lm_head/output is the unembedding.
        if (unembed is null) unembed = embed;

        // Assemble circuits per (layer, expert) by mechanical role — generic across dense
        // and MoE. A circuit needs both halves present; missing halves (e.g. a model with no
        // gate, or tied/absent o) just yield fewer circuits, never a crash.
        var byGroup = infos.Where(t => t.Layer >= 0)
                           .GroupBy(t => (t.Layer, t.Expert));
        var circuits = new List<Circuit>();
        foreach (var g in byGroup)
        {
            string? q = First(g, Role.Q), k = First(g, Role.K), v = First(g, Role.V), o = First(g, Role.O);
            string? up = First(g, Role.Up), down = First(g, Role.Down);
            if (q != null && k != null) circuits.Add(new Circuit(g.Key.Layer, g.Key.Expert, CircuitKind.QK,  q,  k));
            if (v != null && o != null) circuits.Add(new Circuit(g.Key.Layer, g.Key.Expert, CircuitKind.OV,  v,  o));
            if (up != null && down != null) circuits.Add(new Circuit(g.Key.Layer, g.Key.Expert, CircuitKind.FFN, up, down));
        }
        circuits.Sort((a, b) => a.Layer != b.Layer ? a.Layer.CompareTo(b.Layer)
                              : a.Expert != b.Expert ? a.Expert.CompareTo(b.Expert)
                              : a.Kind.CompareTo(b.Kind));

        return new ModelGeometry(vocab, dModel, nHeads, nKv, headDim, interm, nLayers,
            embed, unembed, infos, circuits);
    }

    /* Shape-first classification; universal role token only disambiguates within a shape
     * class. Returns Unknown for anything that doesn't match a known mechanical shape. */
    private static Role Classify(string name, int[] shape, int vocab, int dModel, int qW, int kvW, int interm)
    {
        string lname = name.ToLowerInvariant();
        if (shape.Length == 1)
            return lname.Contains("bias") ? Role.Bias : Role.Norm;
        if (shape.Length != 2) return Role.Unknown;
        int a = shape[0], b = shape[1];

        // Token-anchored: an axis equals the vocab. Embedding vs unembedding by universal token.
        if (a == vocab || b == vocab)
            return (lname.Contains("lm_head") || lname.Contains("output") || lname.Contains("unembed"))
                ? Role.Unembedding : Role.Embedding;

        // MLP: encode [interm, D], decode [D, interm]. interm ≠ D, ≠ vocab ⇒ class is unambiguous;
        // gate vs up needs the universal token (both are [interm, D]).
        if (a == interm && b == dModel) return RoleToken(lname) == Role.Gate ? Role.Gate : Role.Up;
        if (a == dModel && b == interm) return Role.Down;

        // Attention: encode [qW|kvW, D], decode [D, qW]. Disambiguate q/k/v/o by universal token,
        // validated by which width the shape carries.
        if (b == dModel && (a == qW || a == kvW))
        {
            Role rt = RoleToken(lname);
            if (rt is Role.Q or Role.K or Role.V) return rt;
            return a == kvW ? Role.V : Role.Q;   // fallback by width when no token matched
        }
        if (a == dModel && b == qW)
            return Role.O;   // attention output / dense

        return Role.Unknown;
    }

    /* Universal mechanical-role token map — shared by every transformer family, NOT a
     * per-family table. Matched on path segments so substrings don't cross-trip. */
    private static Role RoleToken(string lname)
    {
        var seg = lname.Split('.', '/');
        foreach (var s in seg)
        {
            switch (s)
            {
                case "q_proj": case "wq": case "query": case "q": return Role.Q;
                case "k_proj": case "wk": case "key":   case "k": return Role.K;
                case "v_proj": case "wv": case "value": case "v": return Role.V;
                case "o_proj": case "wo": case "dense": case "out_proj": case "o": return Role.O;
                case "gate_proj": case "w1": case "gate": return Role.Gate;
                case "up_proj":   case "w3": case "fc1": case "c_fc": case "up": return Role.Up;
                case "down_proj": case "w2": case "fc2": case "down": return Role.Down;
            }
        }
        return Role.Unknown;
    }

    private static string? First(IEnumerable<TensorInfo> g, Role r)
        => g.Where(t => t.Role == r).Select(t => t.Name).FirstOrDefault();

    private static int Match(Regex rx, string name)
    {
        var m = rx.Match(name);
        return m.Success && int.TryParse(m.Groups[1].Value, out int i) ? i : -1;
    }
}
