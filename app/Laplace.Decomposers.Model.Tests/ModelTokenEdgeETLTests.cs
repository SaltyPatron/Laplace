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

            var recipe = Recipe(vocab: n, hidden: d);
            var etl = new ModelTokenEdgeETL(dir, recipe, tokens, Source);
            var changes = new List<SubstrateChange>();
            await foreach (var c in etl.EmitAsync(commitEpoch: 1)) changes.Add(c);

            var atts = changes.SelectMany(c => c.Attestations).ToList();
            var relatedTo = RelationTypeRegistry.RelationTypeId("RELATED_TO");

            // (1) it staged token<->token RELATED_TO edges
            Assert.NotEmpty(atts);
            Assert.All(atts, a => Assert.Equal(relatedTo, a.TypeId));

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
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static LlamaRecipeExtractor.RecipeInfo Recipe(int vocab, int hidden) => new()
    {
        RecipeEntityId = Hash128.OfCanonical("substrate/test/model-edges/recipe"),
        Architecture = "LlamaForCausalLM",
        HiddenSize = hidden, NumLayers = 1, NumHeads = 1, NumKvHeads = 1,
        IntermediateSize = hidden, VocabSize = vocab,
        TorchDtype = "float32", HiddenAct = "silu", RopeTheta = 10000, RmsNormEps = 1e-5,
        ModelType = "llama", CanonicalJson = Encoding.UTF8.GetBytes("{}"),
    };

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
