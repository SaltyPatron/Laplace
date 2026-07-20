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
    // ModelCoordinates.ScalarId resolves through the text content law (perfcache-backed
    // decomposition), same as every other content id in the substrate.
    static ModelTokenEdgeETLTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
    }

    private static readonly Hash128 Source = SubstrateCanonicalIds.Of("test", "model-edges", "source");

    // Pair-plane pins construct the ETL under an explicit mode; the default mode is
    // "structure" (APPEARS_IN occurrences). Tests in this class run sequentially, so
    // process-env scoping is safe.
    private static IDisposable PlanesMode(string mode) => new PlanesModeScope(mode);

    private sealed class PlanesModeScope : IDisposable
    {
        private readonly string? _old;
        public PlanesModeScope(string mode)
        {
            _old = Environment.GetEnvironmentVariable("LAPLACE_MODEL_PLANES");
            Environment.SetEnvironmentVariable("LAPLACE_MODEL_PLANES", mode);
        }
        public void Dispose() => Environment.SetEnvironmentVariable("LAPLACE_MODEL_PLANES", _old);
    }


    [Fact]
    public async Task Fold_NormGain_ShiftsAttendSalienceOrdering()
    {
        // The LN gain must fold into the QK projections (LoadGain → ScaleCols):
        // t1=(2,0) is dim0-heavy, t2=(0,2) dim1-heavy, so γ=(2,1) must make t1's
        // QK-subspace salience outrank t2's and γ=(1,2) must reverse it. Pair
        // tiles are deleted (doc 26); the structure recorder's occurrence scores
        // carry the same folded projections.
        var embed = new float[] { 1, 1, 2, 0, 0, 2, 3, 3 };

        var byDim0 = await RunAttend(embed, gamma: new float[] { 2f, 1f });
        var byDim1 = await RunAttend(embed, gamma: new float[] { 1f, 2f });

        Assert.True(byDim0[1] > byDim0[2],
            $"γ=(2,1) should make t1 salience outrank t2; got {byDim0[1]} vs {byDim0[2]}");
        Assert.True(byDim1[2] > byDim1[1],
            $"γ=(1,2) should make t2 salience outrank t1; got {byDim1[2]} vs {byDim1[1]}");
    }



    private static async Task<Dictionary<int, long>> RunAttend(float[] embed, float[] gamma)
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
                    TokenId = i,
                    RawToken = $"t{i}",
                    CanonicalBytes = Encoding.UTF8.GetBytes($"t{i}"),
                    EntityId = ent[i],
                    Tier = 2,
                    IsByteLevel = false,
                    Role = TokenRole.None,
                    ContentX = double.NaN,
                    ContentY = double.NaN,
                    ContentZ = double.NaN,
                    ContentM = double.NaN,
                    HasContentCoord = false,
                };
            }

            var cfg = new ModelConfig
            {
                ModelType = "llama",
                Architecture = "LlamaForCausalLM",
                VocabSize = n,
                HiddenSize = d,
                NumLayers = 1,
                NumHeads = 1,
                NumKvHeads = 1,
                HeadDim = d,
                IntermediateSize = d,
                NumExperts = 0,
                TieWordEmbeddings = false,
                QkNorm = false,
                RopeTheta = 10000,
                NormEps = 1e-5,
                MlaQLoraRank = 0,
                MlaKvLoraRank = 0,
                QkRopeHeadDim = 0,
                QkNopeHeadDim = 0,
                VHeadDim = 0,
                RecipeEntityId = SubstrateCanonicalIds.Of("test", "fold", "recipe"),
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
                Config = cfg,
                Roles = roles,
                Modality = Modality.Text,
                Coverage = Coverage.Full,
                ModelName = "fold-model",
            };

            using var _ = PlanesMode("structure");
            var etl = new ModelTokenEdgeETL(dir, manifest, tokens, Source);
            var appearsIn = RelationTypeRegistry.RelationTypeId("APPEARS_IN");
            var map = new Dictionary<int, long>();
            await foreach (var c in etl.EmitAsync(1, null, DecomposerOptions.Default))
            {
                foreach (var a in c.Attestations)
                {
                    if (a.TypeId != appearsIn) continue;
                    int si = Array.IndexOf(ent, a.SubjectId);
                    if (si >= 0) map[si] = a.SumScoreFp1e9 ?? a.ScoreFp1e9;
                }
            }
            return map;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StructureMode_DepositsAppearsInOccurrences_OnSharedCoordinates()
    {
        // Default mode. A 1-layer/1-head QK model must deposit:
        //   (token, APPEARS_IN, coordinate) occurrences scored by the native tiles,
        //   the coordinate entity composed from plane anchor + layer scalar + head
        //   scalar (Merkle — no model name near any id), CONTAINS/PRECEDES structure
        //   scoped by context = coordinate, and NO token-pair rows.
        var embed = new float[] { 1, 1, 2, 0, 0, 2, 3, 3 };
        const int n = 4, d = 2;
        string dir = Path.Combine(Path.GetTempPath(), "laplace-occ-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteSafetensors(Path.Combine(dir, "model.safetensors"), new[]
            {
                Tensor("model.embed_tokens.weight", new[] { n, d }, embed),
                Tensor("model.layers.0.self_attn.q_proj.weight", new[] { d, d }, new float[] { 1, 0, 0, 1 }),
                Tensor("model.layers.0.self_attn.k_proj.weight", new[] { d, d }, new float[] { 1, 0, 0, 1 }),
            });

            var ent = new Hash128[n];
            var tokens = new LlamaTokenizerParser.TokenRecord[n];
            for (int i = 0; i < n; i++)
            {
                ent[i] = Hash128.OfCanonical($"substrate/test/occ/tok/{i}");
                tokens[i] = new LlamaTokenizerParser.TokenRecord
                {
                    TokenId = i,
                    RawToken = $"t{i}",
                    CanonicalBytes = Encoding.UTF8.GetBytes($"t{i}"),
                    EntityId = ent[i],
                    Tier = 2,
                    IsByteLevel = false,
                    Role = TokenRole.None,
                    ContentX = double.NaN,
                    ContentY = double.NaN,
                    ContentZ = double.NaN,
                    ContentM = double.NaN,
                    HasContentCoord = false,
                };
            }

            var cfg = new ModelConfig
            {
                ModelType = "llama",
                Architecture = "LlamaForCausalLM",
                VocabSize = n,
                HiddenSize = d,
                NumLayers = 1,
                NumHeads = 1,
                NumKvHeads = 1,
                HeadDim = d,
                IntermediateSize = d,
                NumExperts = 0,
                TieWordEmbeddings = false,
                QkNorm = false,
                RopeTheta = 10000,
                NormEps = 1e-5,
                MlaQLoraRank = 0,
                MlaKvLoraRank = 0,
                QkRopeHeadDim = 0,
                QkNopeHeadDim = 0,
                VHeadDim = 0,
                RecipeEntityId = SubstrateCanonicalIds.Of("test", "occ", "recipe"),
                CanonicalJson = Encoding.UTF8.GetBytes("{}"),
            };
            var roles = new[]
            {
                new TensorRole("model.embed_tokens.weight", new[] { n, d }, "F32", TensorRoleKind.Embedding, -1, -1),
                new TensorRole("model.layers.0.self_attn.q_proj.weight", new[] { d, d }, "F32", TensorRoleKind.AttnQ, 0, -1),
                new TensorRole("model.layers.0.self_attn.k_proj.weight", new[] { d, d }, "F32", TensorRoleKind.AttnK, 0, -1),
            };
            var manifest = new ModelManifest
            {
                Config = cfg,
                Roles = roles,
                Modality = Modality.Text,
                Coverage = Coverage.Full,
                ModelName = "occ-model",
            };

            using var _ = PlanesMode("structure");
            var etl = new ModelTokenEdgeETL(dir, manifest, tokens, Source);
            var changes = new List<SubstrateChange>();
            await foreach (var c in etl.EmitAsync(1, null, DecomposerOptions.Default)) changes.Add(c);

            var atts = changes.SelectMany(c => c.Attestations).ToList();
            var entities = changes.SelectMany(c => c.Entities).ToList();

            var appearsIn = RelationTypeRegistry.RelationTypeId("APPEARS_IN");
            var attends = RelationTypeRegistry.RelationTypeId("ATTENDS");
            var contains = RelationTypeRegistry.RelationTypeId("CONTAINS");
            var precedes = RelationTypeRegistry.RelationTypeId("PRECEDES");

            var descriptor = new CircuitDescriptor(0, 0, "attention", "ATTENDS");
            var coord = ModelCoordinates.CoordinateId(descriptor);

            // The coordinate id is Merkle over [plane anchor, layer scalar, head scalar].
            Assert.Equal(
                Hash128.Merkle(EntityTier.Word,
                [
                    ModelCoordinates.PlaneAnchor("attention"),
                    ModelCoordinates.ScalarId(0),
                    ModelCoordinates.ScalarId(0),
                ]),
                coord);

            var occ = atts.Where(a => a.TypeId == appearsIn).ToList();
            Assert.NotEmpty(occ);
            var tokenSet = new HashSet<Hash128>(ent);
            Assert.All(occ, a =>
            {
                Assert.Contains(a.SubjectId, tokenSet);
                Assert.Equal(coord, a.ObjectId);
                Assert.True((a.SumScoreFp1e9 ?? a.ScoreFp1e9) > 0, "occurrence must carry the native tile score");
            });

            // No token-pair rows in structure mode.
            Assert.DoesNotContain(atts, a => a.TypeId == attends);

            // Coordinate entity + constituents, typed correctly.
            Assert.Contains(entities, e => e.Id == coord && e.TypeId == ModelCoordinates.CoordinateTypeId);
            Assert.Contains(entities, e => e.Id == ModelCoordinates.PlaneAnchor("attention")
                                           && e.TypeId == ModelCoordinates.PlaneTypeId);
            Assert.Contains(entities, e => e.Id == ModelCoordinates.ScalarId(0)
                                           && e.TypeId == ModelCoordinates.ScalarTypeId);

            // Structure law: CONTAINS membership + PRECEDES order, context = coordinate.
            // L0.H0's layer scalar and head scalar are the SAME content entity
            // (Blake3("0")), so their CONTAINS rows are one content-addressed row —
            // membership is a set, order lives in PRECEDES.
            var constituents = ModelCoordinates.Constituents(descriptor);
            var containsRows = atts.Where(a => a.TypeId == contains && a.SubjectId == coord).ToList();
            Assert.Equal(constituents.Distinct().Count(), containsRows.Count);
            Assert.All(containsRows, a => Assert.Equal(coord, a.ContextId));
            var precedesRows = atts.Where(a => a.TypeId == precedes && a.ContextId == coord).ToList();
            Assert.Equal(2, precedesRows.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SimilarTo_Symmetric_Directional_Planes_Asymmetric()
    {


        Assert.Equal(RelationTypeRegistry.Symmetry.Symmetric, RelationTypeRegistry.Resolve("SIMILAR_TO").Symmetry);
        Assert.Equal(RelationTypeRegistry.Symmetry.Asymmetric, RelationTypeRegistry.Resolve("ATTENDS").Symmetry);
        Assert.Equal(RelationTypeRegistry.Symmetry.Asymmetric, RelationTypeRegistry.Resolve("OV_RELATES").Symmetry);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LmFactorPlane_DepositedOnlyWhenUntied(bool untied)
    {
        const int n = 6, d = 4;

        var embed = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 1, 1 };
        var lmhead = new float[] { 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1, 0, 1, 0, 0, 1 };

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
                    TokenId = i,
                    RawToken = $"t{i}",
                    CanonicalBytes = Encoding.UTF8.GetBytes($"t{i}"),
                    EntityId = ent[i],
                    Tier = 2,
                    IsByteLevel = false,
                    Role = TokenRole.None,
                    ContentX = double.NaN,
                    ContentY = double.NaN,
                    ContentZ = double.NaN,
                    ContentM = double.NaN,
                    HasContentCoord = false,
                };
            }

            var cfg = new ModelConfig
            {
                ModelType = "llama",
                Architecture = "LlamaForCausalLM",
                VocabSize = n,
                HiddenSize = d,
                NumLayers = 1,
                NumHeads = 1,
                NumKvHeads = 1,
                HeadDim = d,
                IntermediateSize = d,
                NumExperts = 0,
                TieWordEmbeddings = !untied,
                QkNorm = false,
                RopeTheta = 10000,
                NormEps = 1e-5,
                MlaQLoraRank = 0,
                MlaKvLoraRank = 0,
                QkRopeHeadDim = 0,
                QkNopeHeadDim = 0,
                VHeadDim = 0,
                RecipeEntityId = SubstrateCanonicalIds.Of("test", "cont", "recipe"),
                CanonicalJson = Encoding.UTF8.GetBytes("{}"),
            };
            var roles = new List<TensorRole>
            {
                new("model.embed_tokens.weight", new[] { n, d }, "F32", TensorRoleKind.Embedding, -1, -1),
            };
            if (untied) roles.Add(new("lm_head.weight", new[] { n, d }, "F32", TensorRoleKind.LmHead, -1, -1));
            var manifest = new ModelManifest
            {
                Config = cfg,
                Roles = roles,
                Modality = Modality.Text,
                Coverage = Coverage.Full,
                ModelName = "cont-model",
            };

            using var _ = PlanesMode("factors");
            var etl = new ModelTokenEdgeETL(dir, manifest, tokens, Source);
            var appearsIn = RelationTypeRegistry.RelationTypeId("APPEARS_IN");
            var lmAnchor = ModelCoordinates.PlaneAnchor("lm");
            var embAnchor = ModelCoordinates.PlaneAnchor("emb");
            int lmSlices = 0, embSlices = 0;
            await foreach (var c in etl.EmitAsync(1, null, DecomposerOptions.Default))
                foreach (var a in c.Attestations)
                {
                    if (a.TypeId != appearsIn || a.ObjectId is not { } o) continue;
                    if (o == lmAnchor) lmSlices++;
                    if (o == embAnchor) embSlices++;
                }

            Assert.True(embSlices > 0, "factors mode must always deposit the emb-plane factor slice");
            if (untied) Assert.True(lmSlices > 0,
                "untied model must deposit the lm/completion factor plane (unembed rows)");
            else Assert.Equal(0, lmSlices);
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
            ModelType = "llama",
            Architecture = "LlamaForCausalLM",
            VocabSize = vocab,
            HiddenSize = hidden,
            NumLayers = 1,
            NumHeads = 1,
            NumKvHeads = 1,
            HeadDim = hidden,
            IntermediateSize = hidden,
            NumExperts = 0,
            TieWordEmbeddings = false,
            QkNorm = false,
            RopeTheta = 10000,
            NormEps = 1e-5,
            MlaQLoraRank = 0,
            MlaKvLoraRank = 0,
            QkRopeHeadDim = 0,
            QkNopeHeadDim = 0,
            VHeadDim = 0,
            RecipeEntityId = SubstrateCanonicalIds.Of("test", "model-edges", "recipe"),
            CanonicalJson = Encoding.UTF8.GetBytes("{}"),
        };
        var roles = new[]
        {
            new TensorRole("model.embed_tokens.weight", new[] { vocab, hidden }, "F32",
                TensorRoleKind.Embedding, LayerIndex: -1, ExpertIndex: -1),
        };
        return new ModelManifest
        {
            Config = cfg,
            Roles = roles,
            Modality = Modality.Text,
            Coverage = Coverage.Full,
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
