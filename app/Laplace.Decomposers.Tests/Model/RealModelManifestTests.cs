using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model.Tests;






public class RealModelManifestTests
{
    private static string HubRoot =>
        Environment.GetEnvironmentVariable("LAPLACE_MODEL_ROOT") is { Length: > 0 } root
            ? root
            : TestInstall.ResolveModelHubOrFallback();

    public sealed record Expect(
        string HubDir, string ModelType, int Vocab, int Hidden, int Layers, int Heads, int KvHeads,
        int Interm, bool Tied, bool Moe, bool Mla);

    public static IEnumerable<object[]> Models() => new[]
    {
        new object[] { new Expect("models--TinyLlama--TinyLlama-1.1B-Chat-v1.0", "llama", 32000, 2048, 22, 32, 4, 5632, false, false, false) },
        new object[] { new Expect("models--Qwen--Qwen2.5-Coder-3B-Instruct", "qwen2", 151936, 2048, 36, 16, 2, 11008, true, false, false) },
        new object[] { new Expect("models--Qwen--Qwen3-Embedding-0.6B", "qwen3", 151669, 1024, 28, 16, 8, 3072, true, false, false) },
        new object[] { new Expect("models--deepseek-ai--DeepSeek-Coder-V2-Lite-Instruct", "deepseek_v2", 102400, 2048, 27, 16, 16, 10944, false, true, true) },
        new object[] { new Expect("models--microsoft--phi-2", "phi", 51200, 2560, 32, 32, 32, 10240, false, false, false) },
    };

    private static bool HasModel(string dir) =>
        File.Exists(Path.Combine(dir, "config.json")) && Directory.GetFiles(dir, "*.safetensors").Length > 0;

    private static string? ResolveSnapshot(string hubDir)
    {
        string root = Path.Combine(HubRoot, hubDir);
        if (!Directory.Exists(root)) return null;
        if (HasModel(root)) return root;
        string snaps = Path.Combine(root, "snapshots");
        if (Directory.Exists(snaps))
            foreach (var d in Directory.GetDirectories(snaps))
                if (HasModel(d)) return d;
        return null;
    }

    private static ModelManifest Parse(string dir, string name)
    {
        var cfg = ModelConfigReader.Read(Path.Combine(dir, "config.json"));
        var headers = SafetensorsContainerParser.ParseModel(dir);
        string tokenizer = Path.Combine(dir, "tokenizer.json");
        int? ids = File.Exists(tokenizer) ? LlamaTokenizerParser.IdSpace(File.ReadAllBytes(tokenizer)) : null;
        return ModelManifest.Recognize(headers, cfg, ids, name);
    }

    [SkippableFact]
    public void TinyLlamaHeader_IsRecognizedByShapeWithNothingLeftOver()
    {
        const string hubDir = "models--TinyLlama--TinyLlama-1.1B-Chat-v1.0";
        string? dir = ResolveSnapshot(hubDir);
        if (dir is null) throw new SkipException($"model snapshot not present: {hubDir}");
        ModelAnatomy a = Parse(dir, hubDir).Anatomy;

        Assert.Equal(201, a.TensorCount);
        Assert.Empty(a.Unrecognized);
        Assert.Empty(a.Conflicts);
        Assert.Equal(2048, a.Symbol("d"));
        Assert.Equal(32000, a.Symbol("V"));
        Assert.Equal(22, a.Symbol("L"));
        Assert.Equal(64, a.Symbol("d_h"));
        Assert.Equal("frequency", a.Symbols["d"].Basis);
        Assert.Equal("tokenizer", a.Symbols["V"].Basis);
        Assert.Equal(22, a.Operators.Count(o => o.Operator == "self-attention"));
        Assert.Equal(22, a.Operators.Count(o => o.Operator == "gated-mlp"));
        Assert.Equal(22, a.Operators.Count(o => o.Operator == "block-norm"));
        Assert.DoesNotContain(a.Operators, o => o.Slots.Any(s => s.IsAmbiguous));
        OperatorInstance attention = a.Operators.First(o => o.Operator == "self-attention" && o.Block == 3);
        Assert.Equal("model.layers.3.self_attn.q_proj.weight", attention.Slot("q")!.Tensor);
        Assert.Equal("model.layers.3.self_attn.o_proj.weight", attention.Slot("o")!.Tensor);
        Assert.Equal("shape+hint", attention.Slot("q")!.Basis);
        OperatorInstance mlp = a.Operators.First(o => o.Operator == "gated-mlp" && o.Block == 3);
        Assert.Equal(5632, mlp.InstanceSymbols["w"]);
        Assert.Equal("shape", mlp.Slot("down")!.Basis);
    }

    [SkippableFact]
    public void TinyLlamaTokenizer_EveryContentIdentityHasPhysicality()
    {
        const string hubDir = "models--TinyLlama--TinyLlama-1.1B-Chat-v1.0";
        string? dir = ResolveSnapshot(hubDir);
        if (dir is null) throw new SkipException($"model snapshot not present: {hubDir}");
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());

        var records = LlamaTokenizerParser.Parse(Path.Combine(dir, "tokenizer.json"));
        Assert.Equal(32_000, records.Count);

        Hash128 source = Hash128.OfCanonical("test/model/tinyllama-tokenizer-admission");
        var builder = new SubstrateChangeBuilder(
            source, "tokenizer/vocab/tinyllama", entityCapacity: 200_000,
            physicalityCapacity: 200_000);
        foreach (var record in records)
            LlamaTokenizerParser.StageVocabToken(builder, record, source);

        var change = builder.Build();
        var placed = change.Physicalities.Select(p => p.EntityId).ToHashSet();
        var pending = change.Entities
            .Where(e => !placed.Contains(e.Id))
            .Select(e => e.Id)
            .Distinct()
            .ToArray();

        Assert.True(
            pending.Length == 0,
            $"TinyLlama tokenizer emitted {pending.Length} content entities without physicality; " +
            $"first={string.Join(',', pending.Take(10))}");
    }

    [SkippableFact]
    public void TinyLlamaVocabulary_OrdinalIsTheModelLocalTokenId()
    {
        const string hubDir = "models--TinyLlama--TinyLlama-1.1B-Chat-v1.0";
        string? dir = ResolveSnapshot(hubDir);
        if (dir is null) throw new SkipException($"model snapshot not present: {hubDir}");
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());

        var records = LlamaTokenizerParser.Parse(Path.Combine(dir, "tokenizer.json"));
        Hash128 witness = Hash128.OfCanonical("test/model/tinyllama-vocabulary");
        var builder = new SubstrateChangeBuilder(witness, "tokenizer/vocabulary/tinyllama");
        Hash128 vocabulary = LlamaTokenizerParser.StageVocabulary(builder, records, witness)
            ?? throw new InvalidOperationException("TinyLlama ids are dense");
        var change = builder.Build();

        PhysicalityRow trajectory = Assert.Single(change.Physicalities, p => p.EntityId == vocabulary);
        Assert.Equal(witness, trajectory.SourceId);
        Assert.Equal(32_000, trajectory.NConstituents);
        Hash128[] ordinals = Trajectory.Constituents(trajectory.TrajectoryXyzm!);
        Assert.Equal(records.Select(r => r.EntityId).ToArray(), ordinals);
        // Control pieces resolve to their literal surface, not to a minted id.
        Assert.True(records[1].Role.HasFlag(TokenRole.Special));
        Assert.True(records[1].HasContentCoord);
        Assert.NotEqual(Hash128.OfCanonical("substrate/token/special/<s>/v1"), records[1].EntityId);
        Assert.Equal(records[1].EntityId, LlamaTokenizerParser.Parse(System.Text.Encoding.UTF8.GetBytes(
            """{"model":{"vocab":{"<s>":0}}}""")).Single().EntityId);
    }

    [SkippableTheory]
    [MemberData(nameof(Models))]
    public void Manifest_MatchesRealModel(Expect e)
    {
        string? dir = ResolveSnapshot(e.HubDir);
        if (dir is null) throw new SkipException($"model snapshot not present: {e.HubDir}");

        var m = Parse(dir, e.HubDir);
        var c = m.Config;


        Assert.Equal(e.ModelType, c.ModelType);
        Assert.Equal(e.Vocab, c.VocabSize);
        Assert.Equal(e.Hidden, c.HiddenSize);
        Assert.Equal(e.Layers, c.NumLayers);
        Assert.Equal(e.Heads, c.NumHeads);
        Assert.Equal(e.KvHeads, c.NumKvHeads);
        Assert.Equal(e.Interm, c.IntermediateSize);
        Assert.Equal(e.Tied, c.TieWordEmbeddings);
        Assert.Equal(e.Moe, c.IsMoe);
        Assert.Equal(e.Mla, c.IsMla);


        Assert.Equal(Modality.Text, m.Modality);
        Assert.Equal(Coverage.Full, m.Coverage);
        Assert.NotNull(m.Embedding);
        Assert.NotNull(m.LmHead);
        if (e.Tied)
            Assert.Equal(m.Embedding, m.LmHead);
        else
            Assert.NotEqual(m.Embedding, m.LmHead);


        Assert.NotNull(m.Norm(0, "attention"));
        if (e.ModelType != "phi")
            Assert.NotNull(m.Norm(0, "mlp"));

        bool qkNorm = m.Roles.Any(r => r.LayerIndex == 0 && r.Slot is "q-norm" or "k-norm");
        Assert.Equal(e.ModelType == "qwen3", qkNorm);

        if (e.Mla)
            Assert.NotNull(m.Norm(0, "kv-norm"));
        Assert.Empty(m.Anatomy.Unrecognized);


        if (!e.Mla)
        {
            Assert.NotNull(m.Single(0, TensorRoleKind.AttnQ));
            Assert.NotNull(m.Single(0, TensorRoleKind.AttnK));
            Assert.NotNull(m.Single(0, TensorRoleKind.AttnV));
            Assert.NotNull(m.Single(0, TensorRoleKind.AttnO));
        }

        Assert.NotNull(m.Single(0, TensorRoleKind.MlpDown));
    }



    [SkippableTheory]
    [InlineData("DETR-ResNet-101")]
    [InlineData("RT-DETR-v1-R101")]
    [InlineData("Conditional-DETR-R50")]
    [InlineData("Grounding-DINO-Base")]
    public void VisionDetection_NeverRunsTextPlanes(string hubDir)
    {
        string? dir = ResolveSnapshot(hubDir);
        if (dir is null) throw new SkipException($"model snapshot not present: {hubDir}");
        var m = Parse(dir, hubDir);
        Assert.False(m.TextPlanesRunnable,
            $"{hubDir} (type={m.Config.ModelType}, modality={m.Modality}, coverage={m.Coverage}) must not run text planes");
    }



    [SkippableTheory]
    [InlineData("models--sentence-transformers--all-MiniLM-L6-v2")]
    [InlineData("Florence-2-base")]
    [InlineData("models--Qwen--Qwen3-VL-Embedding-2B")]
    [InlineData("models--Qwen--Qwen3-Reranker-0.6B")]
    [InlineData("models--jinaai--jina-code-embeddings-1.5b")]
    [InlineData("models--nvidia--canary-qwen-2.5b")]
    public void AnyFormat_ParsesWithoutCrashing(string hubDir)
    {
        string? dir = ResolveSnapshot(hubDir);
        if (dir is null) throw new SkipException($"model snapshot not present: {hubDir}");
        var ex = Record.Exception(() =>
        {
            var m = Parse(dir, hubDir);
            Assert.True(Enum.IsDefined(m.Coverage));
        });
        Assert.True(ex is null, $"{hubDir} parse threw: {ex?.GetType().Name}: {ex?.Message}");
    }
}
