using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;








public sealed record RelationPathStep(string[] Types, bool Reverse, bool Optional);

public sealed record OperatorSpec(string Op, string? Type, string? Metric)
{
    public double MaxOutsideProbability {get;init;}
    public bool EqualCandidates {get;init;}
    public IReadOnlyList<RelationPathStep>? Steps { get; init; }
    public string PathJson => JsonSerializer.Serialize(Steps?.Select(s => new {types=s.Types,reverse=s.Reverse,optional=s.Optional}));

    public static OperatorSpec FromJson(JsonElement e)
    {
        string op = e.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
        string? type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
        string? metric = e.TryGetProperty("metric", out var m) ? m.GetString() : null;
        if (string.IsNullOrEmpty(op))
            throw new InvalidOperationException($"operator missing 'op': {e}");
        double bound=e.TryGetProperty("max_outside_probability",out var probability)?probability.GetDouble():0;
        string scoring=e.TryGetProperty("scoring",out var scoringElement)?scoringElement.GetString()??"weighted":"weighted";
        if(!double.IsFinite(bound)||bound<0||bound>=1||scoring is not ("weighted" or "set"))throw new InvalidOperationException("invalid output candidate scoring or probability bound");
        if(scoring=="set"&&bound==0)throw new InvalidOperationException("set scoring requires max_outside_probability in (0,1)");
        var result = new OperatorSpec(op, type, metric){MaxOutsideProbability=bound,EqualCandidates=scoring=="set"};
        if (op == "path") {
            if (!e.TryGetProperty("steps",out var steps) || steps.ValueKind!=JsonValueKind.Array || steps.GetArrayLength()==0)
                throw new InvalidOperationException("path operator requires nonempty steps");
            result = result with { Steps = steps.EnumerateArray().Select(step => {
                var types=step.GetProperty("types").EnumerateArray().Select(t=>t.GetString() ?? "").ToArray();
                if(types.Length==0 || types.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("path step requires relation types");
                return new RelationPathStep(types,step.TryGetProperty("reverse",out var reverse) && reverse.GetBoolean(),
                    step.TryGetProperty("optional",out var optional) && optional.GetBoolean());
            }).ToArray() };
        }
        return result;
    }


    public string Key => Op switch
    {
        "path" => $"path:{PathJson}",
        "relation" => $"relation:{Type}",
        "metric" => $"metric:{Metric}",
        "attribute" => $"attribute:{Type}",
        "syntax" => $"syntax:{Type}",
        _ => Op,
    };
}

public sealed record LayerSpec(int KvHeads, IReadOnlyList<OperatorSpec> Heads, OperatorSpec Ffn)
{
    public int IntermediateSize {get;init;}
    public IReadOnlyList<OperatorSpec> Experts {get;init;}=[];
    public int ExpertsPerToken {get;init;}
}

public sealed record VocabSpec(
    string Source, IReadOnlyList<string> Seeds, int Hops, int Fanout, int Size, string? Tokenizer);

public sealed record RecipeDescriptor(
    Hash128 RecipeId,
    string Name,
    string Structure,
    string HiddenSize,
    int IntermediateSize,
    int NumLayers,
    bool Rope,
    bool TieEmbeddings,
    string Norm,
    OperatorSpec Embed,
    OperatorSpec LmHead,
    IReadOnlyList<LayerSpec> Layers,
    VocabSpec Vocab,
    IReadOnlyList<string> Attributes,
    string Compile,
    byte[] CanonicalJson)
{
    public string ModelType {get;init;}="llama";
    public string Format {get;init;}="gguf";
    public int ContextLength {get;init;}=2048;
    public int SequenceLimit {get;init;}=256;
    public double RopeTheta {get;init;}=10000;
    public bool HiddenSizeAuto => HiddenSize == "auto";
    public bool ContinuationCompile =>
        string.Equals(Compile, "continuation", StringComparison.OrdinalIgnoreCase);
    public int HiddenSizeOr(int fallback) => int.TryParse(HiddenSize, out var v) ? v : fallback;

    public static RecipeDescriptor Parse(string recipeJson)
    {
        using var doc = JsonDocument.Parse(recipeJson);
        var root = doc.RootElement;

        string kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
        if (kind != "laplace.recipe")
            throw new InvalidOperationException($"not a laplace.recipe (kind='{kind}')");

        string name = Str(root, "name", "recipe");
        string structure = Str(root, "structure", "dense");
        string hidden = root.TryGetProperty("hidden_size", out var hs)
            ? (hs.ValueKind == JsonValueKind.Number ? hs.GetInt32().ToString() : hs.GetString() ?? "auto")
            : "auto";
        int hiddenInt = int.TryParse(hidden, out var hv) ? hv : 0;
        int intermediate = Int(root, "intermediate_size",
            hiddenInt > 0 ? RoundTo64(hiddenInt * 8 / 3) : 1024);
        bool rope = root.TryGetProperty("rope", out var rp) && rp.ValueKind == JsonValueKind.True;
        bool tie = root.TryGetProperty("tie_embeddings", out var te) && te.ValueKind == JsonValueKind.True;
        string norm = Str(root, "norm", "rmsnorm");

        var embed = root.TryGetProperty("embed", out var em) ? OperatorSpec.FromJson(em)
                                                              : new OperatorSpec("coord", null, null);
        var lmHead = root.TryGetProperty("lm_head", out var lm) ? OperatorSpec.FromJson(lm)
                                                                : new OperatorSpec("trajectory", null, null);

        var layers = new List<LayerSpec>();
        if (!root.TryGetProperty("layers", out var layersEl) || layersEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("recipe has no layers[]");
        int expertCount=Int(root,"num_experts",0),expertsUsed=Int(root,"experts_per_token",Math.Min(2,expertCount));
        foreach (var layer in layersEl.EnumerateArray())
        {
            int kv = layer.TryGetProperty("kv_heads", out var kvh) && kvh.ValueKind == JsonValueKind.Number
                ? kvh.GetInt32() : 1;
            var heads = new List<OperatorSpec>();
            if (layer.TryGetProperty("heads", out var headsEl) && headsEl.ValueKind == JsonValueKind.Array)
                foreach (var h in headsEl.EnumerateArray()) heads.Add(OperatorSpec.FromJson(h));
            if (heads.Count == 0)
                throw new InvalidOperationException($"layer {layers.Count} has no heads[]");
            var ffn = layer.TryGetProperty("ffn", out var ffnEl) ? OperatorSpec.FromJson(ffnEl)
                                                                 : new OperatorSpec("unary", null, null);
            var experts=new List<OperatorSpec>();
            if(layer.TryGetProperty("experts",out var expertArray))
                foreach(var expert in expertArray.EnumerateArray())experts.Add(OperatorSpec.FromJson(expert));
            else if(structure=="moe") {
                int count=expertCount;
                if(layer.TryGetProperty("ffn",out var fspec)&&fspec.TryGetProperty("experts",out var ec)&&ec.ValueKind==JsonValueKind.Number)count=ec.GetInt32();
                if(count<=0)throw new InvalidOperationException("moe requires num_experts or explicit layer experts");
                experts.AddRange(Enumerable.Repeat(ffn,count));
            }
            int selected=Int(layer,"experts_per_token",expertsUsed>0?expertsUsed:Math.Min(2,experts.Count));
            if(experts.Count>0&&(selected<1||selected>experts.Count))throw new InvalidOperationException("experts_per_token must be within the declared experts");
            layers.Add(new LayerSpec(kv,heads,ffn){IntermediateSize=Int(layer,"intermediate_size",intermediate),Experts=experts,ExpertsPerToken=selected});
        }
        if(layers.Count==0)throw new InvalidOperationException("recipe requires at least one layer");
        if(intermediate<=0||layers.Any(l=>l.IntermediateSize<=0||l.KvHeads<=0||l.Heads.Count%l.KvHeads!=0))throw new InvalidOperationException("invalid FFN or grouped-query dimensions");
        if (root.TryGetProperty("num_layers", out var nl) && nl.ValueKind == JsonValueKind.Number
            && nl.GetInt32() != layers.Count)
            throw new InvalidOperationException(
                $"num_layers ({nl.GetInt32()}) != layers.length ({layers.Count})");

        var vocab = ParseVocab(root);
        var attributes = new List<string>();
        if (root.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
            foreach (var a in attrs.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String && a.GetString() is { } s) attributes.Add(s);
                else if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("type", out var at)
                         && at.GetString() is { } ts) attributes.Add(ts);
            }
        string compile = root.TryGetProperty("compile", out var cp) && cp.ValueKind == JsonValueKind.String
            ? cp.GetString() ?? ""
            : "";
        // An omitted compile means full. Continuation compile zeroes the attention and
        // residual scale of every operator outside the continuation set, so it is selected
        // only when the recipe asks for it, never inferred from lm_head; an operator the
        // recipe names always contributes to the emitted tensors.
        if (compile.Length == 0)
            compile = "full";

        byte[] canonical = RecipeExtractor.CanonicalBytes(root);

        return new RecipeDescriptor(
            Hash128.Blake3(canonical), name, structure, hidden, intermediate, layers.Count,
            rope, tie, norm, embed, lmHead, layers, vocab, attributes, compile, canonical) {
                ModelType=Str(root,"model_type",structure=="moe"?"mixtral":"llama"),
                Format=Str(root,"format","gguf"),
                ContextLength=Int(root,"context_length",2048),SequenceLimit=Int(root,"sequence_limit",256),
                RopeTheta=root.TryGetProperty("rope_theta",out var theta)?theta.GetDouble():10000
            };
    }

    private static int RoundTo64(int x) => Math.Max(64, ((x + 63) / 64) * 64);

    private static VocabSpec ParseVocab(JsonElement root)
    {
        if (!root.TryGetProperty("vocab", out var v) || v.ValueKind != JsonValueKind.Object)
            return new VocabSpec("crawl", Array.Empty<string>(), 2, 30, 1500, null);
        string source = Str(v, "source", "crawl");
        var seeds = new List<string>();
        if (v.TryGetProperty("seeds", out var se) && se.ValueKind == JsonValueKind.Array)
            foreach (var s in se.EnumerateArray())
                if (s.GetString() is { } str) seeds.Add(str);
        int hops = Int(v, "hops", 2);
        int fanout = Int(v, "fanout", 30);
        int size = Int(v, "size", 1500);
        string? tok = v.TryGetProperty("tokenizer", out var tk) ? tk.GetString() : null;
        return new VocabSpec(source, seeds, hops, fanout, size, tok);
    }

    private static string Str(JsonElement e, string key, string def)
        => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? def : def;
    private static int Int(JsonElement e, string key, int def)
        => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : def;
}
