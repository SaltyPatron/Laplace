using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Model.Tests;

public class ModelTokenEdgeETLTests
{
    private static readonly Hash128 Source = Hash128.OfCanonical("substrate/test/model-edges/source");

    [Fact]
    public async Task EmitAsync_StagesTokenEdges_AndStoresNoCoordsOrDims()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-edges-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 8 tokens, d=4. Two tight clusters: {0,1,2,3} near +x, {4,5,6,7} near +y.
            // The eigenmap reduction + partner scan must connect within-cluster tokens.
            int n = 8, d = 4;
            var rows = new (double x, double y)[]
            {
                (1.0, 0.0), (0.96, 0.05), (0.93, 0.10), (0.90, 0.02),
                (0.0, 1.0), (0.05, 0.96), (0.10, 0.93), (0.02, 0.90),
            };
            var embed = new float[n * d];
            for (int i = 0; i < n; i++)
            {
                embed[i * d + 0] = (float)rows[i].x;
                embed[i * d + 1] = (float)rows[i].y;
                embed[i * d + 2] = (float)(0.01 * i);
                embed[i * d + 3] = (float)(-0.01 * i);
            }

            WriteSafetensors(Path.Combine(dir, "model.safetensors"), new[]
            {
                Tensor("model.embed_tokens.weight", new[] { n, d }, embed),
            });

            var ent = new Hash128[n];
            var tokens = new LlamaTokenizerParser.TokenRecord[n];
            for (int i = 0; i < n; i++)
            {
                ent[i] = Hash128.OfCanonical($"substrate/test/model-edges/tok/{i}");
                tokens[i] = new LlamaTokenizerParser.TokenRecord
                {
                    TokenId = i, RawToken = $"t{i}", CanonicalBytes = Encoding.UTF8.GetBytes($"t{i}"),
                    EntityId = ent[i], Tier = 2, IsByteLevel = false, Role = TokenRole.None,
                    ContentX = double.NaN, ContentY = double.NaN, ContentZ = double.NaN, ContentM = double.NaN,
                    HasContentCoord = false,
                };
            }

            var manifest = ToyManifest(dir, vocab: n, hidden: d);
            var etl = new ModelTokenEdgeETL(dir, manifest, tokens, Source);
            var changes = new List<SubstrateChange>();
            await foreach (var c in etl.EmitAsync(commitEpoch: 1)) changes.Add(c);

            var atts = changes.SelectMany(c => c.Attestations).ToList();
            // Reconciliation: the embedding self-similarity plane is SIMILAR_TO (what ModelDecomposer
            // bootstraps and ArchitectureProfile's SelfSimilarityPath declares), not RELATED_TO.
            var similarTo = RelationTypeRegistry.RelationTypeId("SIMILAR_TO");

            // (1) it staged token<->token SIMILAR_TO edges
            Assert.NotEmpty(atts);
            Assert.All(atts, a => Assert.Equal(similarTo, a.TypeId));

            // (2) every endpoint is a content token entity — nothing else
            var tokenSet = new HashSet<Hash128>(ent);
            Assert.All(atts, a =>
            {
                Assert.Contains(a.SubjectId, tokenSet);
                Assert.True(a.ObjectId is { } o && tokenSet.Contains(o));
            });

            // (3) it stores NO geometry and NO new entities (only codepoints surface; tokens
            //     already exist with content geometry; the model adds edges, nothing else)
            Assert.Empty(changes.SelectMany(c => c.Physicalities));
            Assert.Empty(changes.SelectMany(c => c.Entities));

            // (4) the learned similarity is real: token0's within-cluster edges {1,2,3}
            //     must outscore its cross-cluster edges {4..7} — clean separation.
            //     RELATED_TO is symmetric (endpoints canonicalized), so match either side.
            var t0 = atts.Where(a => a.SubjectId == ent[0] || a.ObjectId == ent[0])
                         .Select(a =>
                         {
                             var other = a.SubjectId == ent[0] ? a.ObjectId!.Value : a.SubjectId;
                             return (idx: Array.IndexOf(ent, other), score: a.SumScoreFp1e9 ?? a.ScoreFp1e9);
                         }).ToList();
            var same  = t0.Where(p => p.idx is >= 1 and <= 3).Select(p => p.score).ToList();
            var cross = t0.Where(p => p.idx is >= 4 and <= 7).Select(p => p.score).ToList();
            Assert.NotEmpty(same);
            Assert.NotEmpty(cross);
            Assert.True(same.Min() > cross.Max(),
                $"within-cluster edges must outscore cross-cluster; same.min={same.Min()} cross.max={cross.Max()}");

            // Symmetric SIMILAR_TO must be canonicalized — no pair emitted in both orders (a→b AND b→a).
            var seen = new HashSet<(Hash128, Hash128)>();
            foreach (var a in atts) seen.Add((a.SubjectId, a.ObjectId!.Value));
            foreach (var a in atts)
                Assert.False(seen.Contains((a.ObjectId!.Value, a.SubjectId)),
                    "symmetric SIMILAR_TO emitted a redundant mirror pair");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Fold_NormGain_ShiftsAttendOrdering()
    {
        // Track A2 known-answer. With identity Q/K, ATTENDS(i→j) = Σ_c γ_c² · e_i,c · e_j,c.
        // t0=(1,1); t1=(2,0) lives on dim0, t2=(0,2) on dim1. A γ favoring dim0 must make t0→t1
        // outrank t0→t2, and a γ favoring dim1 must flip it. If the gain were NOT folded the two
        // would tie regardless of γ — so this proves the fold is applied and on the right axis.
        var embed = new float[] { 1, 1,  2, 0,  0, 2,  3, 3 };   // 4 tokens (n≥4 required), d=2

        var byDim0 = await RunAttend(embed, gamma: new float[] { 2f, 1f });
        var byDim1 = await RunAttend(embed, gamma: new float[] { 1f, 2f });

        Assert.True(byDim0[(0, 1)] > byDim0[(0, 2)],
            $"γ=(2,1) should make t0→t1 outrank t0→t2; got {byDim0[(0, 1)]} vs {byDim0[(0, 2)]}");
        Assert.True(byDim1[(0, 2)] > byDim1[(0, 1)],
            $"γ=(1,2) should make t0→t2 outrank t0→t1; got {byDim1[(0, 2)]} vs {byDim1[(0, 1)]}");
    }

    // Run EmitAsync on a 4-token, d=2 toy with identity Q/K and the given input_layernorm gain;
    // return the ATTENDS score for each (subjectIdx, objectIdx) pair.
    private static async Task<Dictionary<(int, int), long>> RunAttend(float[] embed, float[] gamma)
    {
        const int n = 4, d = 2;
        string dir = Path.Combine(Path.GetTempPath(), "laplace-fold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteSafetensors(Path.Combine(dir, "model.safetensors"), new[]
            {
                Tensor("model.embed_tokens.weight", new[] { n, d }, embed),
                Tensor("model.layers.0.self_attn.q_proj.weight", new[] { d, d }, new float[] { 1, 0, 0, 1 }),
                Tensor("model.layers.0.self_attn.k_proj.weight", new[] { d, d }, new float[] { 1, 0, 0, 1 }),
                Tensor("model.layers.0.input_layernorm.weight", new[] { d }, gamma),
            });

            var ent = new Hash128[n];
            var tokens = new LlamaTokenizerParser.TokenRecord[n];
            for (int i = 0; i < n; i++)
            {
                ent[i] = Hash128.OfCanonical($"substrate/test/fold/tok/{i}");
                tokens[i] = new LlamaTokenizerParser.TokenRecord
                {
                    TokenId = i, RawToken = $"t{i}", CanonicalBytes = Encoding.UTF8.GetBytes($"t{i}"),
                    EntityId = ent[i], Tier = 2, IsByteLevel = false, Role = TokenRole.None,
                    ContentX = double.NaN, ContentY = double.NaN, ContentZ = double.NaN, ContentM = double.NaN,
                    HasContentCoord = false,
                };
            }

            var cfg = new ModelConfig
            {
                ModelType = "llama", Architecture = "LlamaForCausalLM",
                VocabSize = n, HiddenSize = d, NumLayers = 1, NumHeads = 1, NumKvHeads = 1,
                HeadDim = d, IntermediateSize = d, NumExperts = 0,
                TieWordEmbeddings = false, QkNorm = false, RopeTheta = 10000, NormEps = 1e-5,
                MlaQLoraRank = 0, MlaKvLoraRank = 0, QkRopeHeadDim = 0, QkNopeHeadDim = 0, VHeadDim = 0,
                RecipeEntityId = Hash128.OfCanonical("substrate/test/fold/recipe"),
                CanonicalJson = Encoding.UTF8.GetBytes("{}"),
            };
            var roles = new[]
            {
                new TensorRole("model.embed_tokens.weight", new[] { n, d }, "F32", TensorRoleKind.Embedding, -1, -1),
                new TensorRole("model.layers.0.self_attn.q_proj.weight", new[] { d, d }, "F32", TensorRoleKind.AttnQ, 0, -1),
                new TensorRole("model.layers.0.self_attn.k_proj.weight", new[] { d, d }, "F32", TensorRoleKind.AttnK, 0, -1),
                new TensorRole("model.layers.0.input_layernorm.weight", new[] { d }, "F32", TensorRoleKind.Norm, 0, -1),
            };
            var manifest = new ModelManifest
            {
                Config = cfg, Roles = roles, Modality = Modality.Text, Coverage = Coverage.Full, ModelName = "fold-model",
            };

            var etl = new ModelTokenEdgeETL(dir, manifest, tokens, Source);
            var attends = RelationTypeRegistry.RelationTypeId("ATTENDS");
            var map = new Dictionary<(int, int), long>();
            await foreach (var c in etl.EmitAsync(commitEpoch: 1))
                foreach (var a in c.Attestations)
                {
                    if (a.TypeId != attends || a.ObjectId is not { } o) continue;
                    int si = Array.IndexOf(ent, a.SubjectId), oi = Array.IndexOf(ent, o);
                    if (si >= 0 && oi >= 0) map[(si, oi)] = a.SumScoreFp1e9 ?? a.ScoreFp1e9;
                }
            return map;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SimilarTo_Symmetric_Directional_Planes_Asymmetric()
    {
        // The dedup gate relies on this contract: SIMILAR_TO is symmetric (canonicalize), the
        // tensor-calculation planes are directional (keep both orderings).
        Assert.Equal(RelationTypeRegistry.Symmetry.Symmetric, RelationTypeRegistry.Resolve("SIMILAR_TO").Symmetry);
        Assert.Equal(RelationTypeRegistry.Symmetry.Asymmetric, RelationTypeRegistry.Resolve("ATTENDS").Symmetry);
        Assert.Equal(RelationTypeRegistry.Symmetry.Asymmetric, RelationTypeRegistry.Resolve("OV_RELATES").Symmetry);
    }

    [Theory]
    [InlineData(true)]    // untied (distinct lm_head) → CONTINUES_TO emitted
    [InlineData(false)]   // tied (no lm_head) → skipped, since E·E ≡ SIMILAR_TO
    public async Task ContinuesTo_EmittedOnlyWhenUntied(bool untied)
    {
        const int n = 6, d = 4;
        // Distinct embedding vs unembedding (shifted) so the direct path E·W_U is genuinely directional.
        var embed  = new float[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1, 1,1,0,0, 0,0,1,1 };
        var lmhead = new float[] { 0,1,0,0, 0,0,1,0, 0,0,0,1, 1,0,0,0, 0,1,1,0, 1,0,0,1 };

        string dir = Path.Combine(Path.GetTempPath(), "laplace-cont-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tensors = new List<TensorSpec> { Tensor("model.embed_tokens.weight", new[] { n, d }, embed) };
            if (untied) tensors.Add(Tensor("lm_head.weight", new[] { n, d }, lmhead));
            WriteSafetensors(Path.Combine(dir, "model.safetensors"), tensors);

            var ent = new Hash128[n];
            var tokens = new LlamaTokenizerParser.TokenRecord[n];
            for (int i = 0; i < n; i++)
            {
                ent[i] = Hash128.OfCanonical($"substrate/test/cont/tok/{i}");
                tokens[i] = new LlamaTokenizerParser.TokenRecord
                {
                    TokenId = i, RawToken = $"t{i}", CanonicalBytes = Encoding.UTF8.GetBytes($"t{i}"),
                    EntityId = ent[i], Tier = 2, IsByteLevel = false, Role = TokenRole.None,
                    ContentX = double.NaN, ContentY = double.NaN, ContentZ = double.NaN, ContentM = double.NaN,
                    HasContentCoord = false,
                };
            }

            var cfg = new ModelConfig
            {
                ModelType = "llama", Architecture = "LlamaForCausalLM",
                VocabSize = n, HiddenSize = d, NumLayers = 1, NumHeads = 1, NumKvHeads = 1,
                HeadDim = d, IntermediateSize = d, NumExperts = 0,
                TieWordEmbeddings = !untied, QkNorm = false, RopeTheta = 10000, NormEps = 1e-5,
                MlaQLoraRank = 0, MlaKvLoraRank = 0, QkRopeHeadDim = 0, QkNopeHeadDim = 0, VHeadDim = 0,
                RecipeEntityId = Hash128.OfCanonical("substrate/test/cont/recipe"),
                CanonicalJson = Encoding.UTF8.GetBytes("{}"),
            };
            var roles = new List<TensorRole>
            {
                new("model.embed_tokens.weight", new[] { n, d }, "F32", TensorRoleKind.Embedding, -1, -1),
            };
            if (untied) roles.Add(new("lm_head.weight", new[] { n, d }, "F32", TensorRoleKind.LmHead, -1, -1));
            var manifest = new ModelManifest
            {
                Config = cfg, Roles = roles, Modality = Modality.Text, Coverage = Coverage.Full, ModelName = "cont-model",
            };

            var etl = new ModelTokenEdgeETL(dir, manifest, tokens, Source);
            var continuesTo = RelationTypeRegistry.RelationTypeId("CONTINUES_TO");
            int count = 0;
            await foreach (var c in etl.EmitAsync(commitEpoch: 1))
                foreach (var a in c.Attestations)
                    if (a.TypeId == continuesTo) count++;

            if (untied) Assert.True(count > 0, "untied model must emit CONTINUES_TO (LM-head direct path)");
            else        Assert.Equal(0, count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static ModelManifest ToyManifest(string dir, int vocab, int hidden)
    {
        var cfg = new ModelConfig
        {
            ModelType = "llama", Architecture = "LlamaForCausalLM",
            VocabSize = vocab, HiddenSize = hidden, NumLayers = 1, NumHeads = 1, NumKvHeads = 1,
            HeadDim = hidden, IntermediateSize = hidden, NumExperts = 0,
            TieWordEmbeddings = false, QkNorm = false, RopeTheta = 10000, NormEps = 1e-5,
            MlaQLoraRank = 0, MlaKvLoraRank = 0, QkRopeHeadDim = 0, QkNopeHeadDim = 0, VHeadDim = 0,
            RecipeEntityId = Hash128.OfCanonical("substrate/test/model-edges/recipe"),
            CanonicalJson = Encoding.UTF8.GetBytes("{}"),
        };
        var roles = new[]
        {
            new TensorRole("model.embed_tokens.weight", new[] { vocab, hidden }, "F32",
                TensorRoleKind.Embedding, LayerIndex: -1, ExpertIndex: -1),
        };
        return new ModelManifest
        {
            Config = cfg, Roles = roles, Modality = Modality.Text, Coverage = Coverage.Full,
            ModelName = "toy-model",
        };
    }

    private static TensorSpec Tensor(string name, int[] shape, float[] values) => new(name, shape, values);

    private static void WriteSafetensors(string path, IReadOnlyList<TensorSpec> tensors)
    {
        long offset = 0;
        var ranges = new List<(long Start, long End)>(tensors.Count);
        foreach (var t in tensors)
        {
            long bytes = checked(t.Values.Length * sizeof(float));
            ranges.Add((offset, offset + bytes));
            offset += bytes;
        }
        using var hdr = new MemoryStream();
        using (var w = new Utf8JsonWriter(hdr))
        {
            w.WriteStartObject();
            for (int i = 0; i < tensors.Count; i++)
            {
                var t = tensors[i];
                w.WritePropertyName(t.Name);
                w.WriteStartObject();
                w.WriteString("dtype", "F32");
                w.WritePropertyName("shape");
                w.WriteStartArray(); foreach (int dd in t.Shape) w.WriteNumberValue(dd); w.WriteEndArray();
                w.WritePropertyName("data_offsets");
                w.WriteStartArray(); w.WriteNumberValue(ranges[i].Start); w.WriteNumberValue(ranges[i].End); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        byte[] header = hdr.ToArray();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Span<byte> len = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(len, header.Length);
        file.Write(len);
        file.Write(header);
        Span<byte> fb = stackalloc byte[4];
        foreach (var t in tensors)
            foreach (float v in t.Values) { BinaryPrimitives.WriteSingleLittleEndian(fb, v); file.Write(fb); }
    }

    private sealed record TensorSpec(string Name, int[] Shape, float[] Values);
}
