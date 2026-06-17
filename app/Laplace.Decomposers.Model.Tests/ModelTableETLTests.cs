using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Model.Tests;

public class ModelTableETLTests
{
    private static readonly Hash128 Source = Hash128.OfCanonical("substrate/test/model-table/source");

    [Fact]
    public async Task EmitAsync_StreamsTokenEntityAndPathMatchups()
    {
        Environment.SetEnvironmentVariable("LAPLACE_MODEL_NOISE_SIGMA", "0.01");

        string dir = Path.Combine(Path.GetTempPath(), "laplace-etl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteSafetensors(Path.Combine(dir, "model.safetensors"), new[]
            {
                Tensor("model.embed_tokens.weight",                    new[] { 2, 2 }, new[] { 1f, 0f, 0f, 1f }),
                Tensor("lm_head.weight",                               new[] { 2, 2 }, new[] { 1f, 0f, 0f, 1f }),
                Tensor("model.layers.0.self_attn.q_proj.weight",       new[] { 2, 2 }, new[] { 1f, 0f, 0f, 1f }),
                Tensor("model.layers.0.self_attn.k_proj.weight",       new[] { 2, 2 }, new[] { 0f, 1f, 1f, 0f }),
                Tensor("model.layers.0.self_attn.v_proj.weight",       new[] { 2, 2 }, new[] { 1f, 0f, 0f, 1f }),
                Tensor("model.layers.0.self_attn.o_proj.weight",       new[] { 2, 2 }, new[] { 0f, 1f, 1f, 0f }),
                Tensor("model.layers.0.mlp.gate_proj.weight",          new[] { 2, 2 }, new[] { 1f, 0f, 0f, 1f }),
                Tensor("model.layers.0.mlp.up_proj.weight",            new[] { 2, 2 }, new[] { 1f, 0f, 0f, 1f }),
                Tensor("model.layers.0.mlp.down_proj.weight",          new[] { 2, 2 }, new[] { 1f, 0f, 0f, 1f }),
            });

            var e0 = Hash128.OfCanonical("substrate/test/model-table/token/zero");
            var e1 = Hash128.OfCanonical("substrate/test/model-table/token/one");
            var tokens = new[]
            {
                Token(0, "zero", e0, tier: 1),
                Token(1, "one",  e1, tier: 2),
            };

            var etl = new ModelTableETL(dir, Recipe(), tokens, Source,
                ModelDecomposer.ModelLayerTypeId);
            var changes = new List<SubstrateChange>();
            await foreach (var c in etl.EmitAsync()) changes.Add(c);

            var entities     = changes.SelectMany(c => c.Entities).ToList();
            var attestations = changes.SelectMany(c => c.Attestations).ToList();

            
            Assert.Contains(entities, e => e.Id == e0 && e.Tier == 1);
            Assert.Contains(entities, e => e.Id == e1 && e.Tier == 2);

            
            var neuronType = Hash128.OfCanonical("substrate/type/Neuron/v1");
            Assert.DoesNotContain(entities, e => e.TypeId == neuronType);

            
            
            Assert.Contains(attestations, HasEdge("ATTENDS", e0, e1));
            Assert.Contains(attestations, HasEdge("ATTENDS", e1, e0));

            
            Assert.Contains(attestations, HasEdge("OV_RELATES", e0, e1));
            Assert.Contains(attestations, HasEdge("OV_RELATES", e1, e0));

            
            Assert.Contains(attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("COMPLETES_TO"));

            
            Assert.DoesNotContain(attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("EMBEDS"));
            Assert.DoesNotContain(attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("Q_PROJECTS"));
            Assert.DoesNotContain(attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("DETECTS"));
            Assert.DoesNotContain(attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("WRITES"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Predicate<AttestationRow> HasEdge(string relation, Hash128 subject, Hash128 obj)
    {
        var type = RelationTypeRegistry.RelationTypeId(relation);
        return a => a.TypeId == type && a.SubjectId == subject && a.ObjectId == obj;
    }

    private static LlamaTokenizerParser.TokenRecord Token(int id, string raw, Hash128 entityId, byte tier) => new()
    {
        TokenId        = id,
        RawToken       = raw,
        CanonicalBytes = Encoding.UTF8.GetBytes(raw),
        EntityId       = entityId,
        Tier           = tier,
        IsByteLevel    = false,
        Role           = TokenRole.None,
        ContentX       = double.NaN, ContentY = double.NaN,
        ContentZ       = double.NaN, ContentM = double.NaN,
        HasContentCoord = false,
    };

    private static LlamaRecipeExtractor.RecipeInfo Recipe() => new()
    {
        RecipeEntityId   = Hash128.OfCanonical("substrate/test/model-table/recipe"),
        Architecture     = "LlamaForCausalLM",
        HiddenSize       = 2,
        NumLayers        = 1,
        NumHeads         = 1,
        NumKvHeads       = 1,
        IntermediateSize = 2,
        VocabSize        = 2,
        TorchDtype       = "float32",
        HiddenAct        = "silu",
        RopeTheta        = 10000,
        RmsNormEps       = 1e-5,
        ModelType        = "llama",
        CanonicalJson    = Encoding.UTF8.GetBytes("{}"),
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
                w.WriteStartArray(); foreach (int d in t.Shape) w.WriteNumberValue(d); w.WriteEndArray();
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
            foreach (float v in t.Values)
            { BinaryPrimitives.WriteSingleLittleEndian(fb, v); file.Write(fb); }
    }

    private sealed record TensorSpec(string Name, int[] Shape, float[] Values);
}
