namespace Laplace.Smoke.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core;
using Laplace.Core.Abstractions;
using Laplace.Decomposers.Model.Extractors;
using Laplace.Decomposers.Model.Mechanistic;
using Laplace.Decomposers.Model.OperatorShapes;
using Laplace.Decomposers.Text;
using Laplace.Pipeline;
using Laplace.Pipeline.Abstractions;

using Xunit;

/// <summary>
/// AttentionEdgeExtractor under the corrected source-blind / mechanistic-
/// head / LINESTRING4D-operator-shape design (#38, #113).
///
/// Operator-shape half (geometric):
///   - One PhysicalityRecord per per-head matrix
///   - PhysicalityType = `model_weights_4d` concept entity
///   - Entity = composition over (matrix_role_atom, mechanistic_head_entity)
///   - Geometry = LINESTRING4D (Point4D[]) projected via Laplacian eigenmap
///
/// Discrete-edge half (source-blind dedup):
///   - Same (queryToken, keyToken) attested by TWO different (model, head)
///     attestors produces ONE shared edge_hash with TWO provenance rows
///   - has_magnitude meta-edge emitted per attestation
///   - Edge composition = [attends_kind, queryToken, keyToken] only — NEVER
///     model/layer/head in the composition (substrate growth sublinear with
///     model count)
/// </summary>
public class AttentionEdgeExtractorTest
{
    private const string MiniLmDir =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed1d9f207416be6d2e6f8de32d1f16199bf";

    [Fact]
    public async Task EmitOperatorShape_SyntheticMatrix_EmitsOnePhysicalityRecordWithModelWeights4dType()
    {
        var pipeline = BuildPipeline();

        var modelEntity = pipeline.Resolver.Resolve("test_model_attention_extractor");
        var headEntity  = await pipeline.Heads.ResolveHeadAsync(
            modelEntity, layerIndex: 3, headIndex: 7, CancellationToken.None);

        // 64 rows × 16 cols — small enough for fast Laplacian eigenmap.
        const int rowCount = 64;
        const int colCount = 16;
        var matrix = new double[rowCount * colCount];
        var rng    = new Random(42);
        for (var i = 0; i < matrix.Length; i++) { matrix[i] = rng.NextDouble() - 0.5; }

        await pipeline.Extractor.EmitOperatorShapeAsync(
            modelEntity,
            "test_model_source",
            AttentionMatrixKind.Query,
            headEntity,
            matrix.AsMemory(),
            rowCount,
            colCount,
            CancellationToken.None);

        Assert.Single(pipeline.PhysicalityRecorder.Records);
        var record = pipeline.PhysicalityRecorder.Records[0];

        var modelWeights4dType = pipeline.Resolver.Resolve("model_weights_4d");
        Assert.Equal(
            Convert.ToHexString(modelWeights4dType.AsSpan()),
            Convert.ToHexString(record.PhysicalityTypeHash.AsSpan()));

        Assert.NotNull(record.SourceHash);
        Assert.True(record.Geometry.Length > 1, "expected non-degenerate LINESTRING4D");

        // Each Point4D should be on S³ (norm ≈ 1) — LaplacianEigenmap.EmbedToSphere
        // post-condition.
        foreach (var p in record.Geometry)
        {
            var norm = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z + p.W * p.W);
            Assert.InRange(norm, 1.0 - 1e-6, 1.0 + 1e-6);
        }
    }

    [Fact]
    public async Task EmitOperatorShape_TwoMatrixKinds_ProduceTwoDistinctOperatorShapeEntities()
    {
        var pipeline = BuildPipeline();

        var modelEntity = pipeline.Resolver.Resolve("test_model_two_kinds");
        var headEntity  = await pipeline.Heads.ResolveHeadAsync(
            modelEntity, layerIndex: 0, headIndex: 0, CancellationToken.None);

        const int rowCount = 32;
        const int colCount = 8;
        var matrix = new double[rowCount * colCount];
        for (var i = 0; i < matrix.Length; i++) { matrix[i] = (i % 3) - 1; }

        var wqEntity = await pipeline.Extractor.EmitOperatorShapeAsync(
            modelEntity, "src", AttentionMatrixKind.Query,  headEntity,
            matrix.AsMemory(), rowCount, colCount, CancellationToken.None);

        var wkEntity = await pipeline.Extractor.EmitOperatorShapeAsync(
            modelEntity, "src", AttentionMatrixKind.Key, headEntity,
            matrix.AsMemory(), rowCount, colCount, CancellationToken.None);

        Assert.NotEqual(
            Convert.ToHexString(wqEntity.AsSpan()),
            Convert.ToHexString(wkEntity.AsSpan()));
        Assert.Equal(2, pipeline.PhysicalityRecorder.Records.Count);
    }

    [Fact]
    public async Task EmitDiscreteEdge_SameTokenPair_DifferentAttestors_DedupesToOneEdgeWithTwoProvenance()
    {
        var pipeline = BuildPipeline();

        // Two different (model, head) attestors observing the same (queryToken, keyToken)
        var modelA = pipeline.Resolver.Resolve("model_a_llama_4_maverick");
        var modelB = pipeline.Resolver.Resolve("model_b_qwen3_32b");

        var headA = await pipeline.Heads.ResolveHeadAsync(modelA, 3, 7, CancellationToken.None);
        var headB = await pipeline.Heads.ResolveHeadAsync(modelB, 5, 2, CancellationToken.None);

        var queryToken = pipeline.Resolver.Resolve("cat");
        var keyToken   = pipeline.Resolver.Resolve("mammal");

        var ts = await pipeline.Heads.IntegerAtomAsync(1741000000, CancellationToken.None);

        var magA = pipeline.Resolver.Resolve("0.873");
        var magB = pipeline.Resolver.Resolve("0.795");

        var edgeFromA = await pipeline.Extractor.EmitDiscreteEdgeAsync(
            modelA, headA, ts, queryToken, keyToken, magA, CancellationToken.None);

        var edgeFromB = await pipeline.Extractor.EmitDiscreteEdgeAsync(
            modelB, headB, ts, queryToken, keyToken, magB, CancellationToken.None);

        // Same edge_hash: source-blind dedup
        Assert.Equal(
            Convert.ToHexString(edgeFromA.AsSpan()),
            Convert.ToHexString(edgeFromB.AsSpan()));

        // Two distinct provenance rows on the shared edge
        var attendsType = pipeline.Resolver.Resolve("attends");
        var provenanceForSharedEdge = pipeline.ProvenanceRecorder.EdgeProvenance
            .Where(p =>
                Convert.ToHexString(p.EdgeHash.AsSpan()) == Convert.ToHexString(edgeFromA.AsSpan()) &&
                Convert.ToHexString(p.EdgeTypeHash.AsSpan()) == Convert.ToHexString(attendsType.AsSpan()))
            .ToList();
        Assert.Equal(2, provenanceForSharedEdge.Count);

        // Distinct provenance entities (composition over distinct (model, head, ts))
        var distinctProvenanceEntities = provenanceForSharedEdge
            .Select(p => Convert.ToHexString(p.SourceHash.AsSpan()))
            .Distinct()
            .Count();
        Assert.Equal(2, distinctProvenanceEntities);
    }

    [Fact]
    public async Task EmitDiscreteEdge_EmitsHasMagnitudeMetaEdgeAndProvenanceMetaEdges()
    {
        var pipeline = BuildPipeline();

        var modelEntity = pipeline.Resolver.Resolve("test_model_meta_edges");
        var headEntity  = await pipeline.Heads.ResolveHeadAsync(modelEntity, 0, 0, CancellationToken.None);
        var queryToken  = pipeline.Resolver.Resolve("a");
        var keyToken    = pipeline.Resolver.Resolve("b");
        var magnitude   = pipeline.Resolver.Resolve("0.5");
        var ts          = await pipeline.Heads.IntegerAtomAsync(1741000000, CancellationToken.None);

        var attendsType = pipeline.Resolver.Resolve("attends");
        var hasMag      = pipeline.Resolver.Resolve("has_magnitude");
        var fromModel   = pipeline.Resolver.Resolve("from_model");
        var fromHead    = pipeline.Resolver.Resolve("from_head");

        var beforeCount = pipeline.EdgeRecorder.EdgeRecords.Count;

        await pipeline.Extractor.EmitDiscreteEdgeAsync(
            modelEntity, headEntity, ts, queryToken, keyToken, magnitude, CancellationToken.None);

        var emittedAfter = pipeline.EdgeRecorder.EdgeRecords.Skip(beforeCount).ToList();

        var attendsTypeHex   = Convert.ToHexString(attendsType.AsSpan());
        var hasMagTypeHex    = Convert.ToHexString(hasMag.AsSpan());
        var fromModelTypeHex = Convert.ToHexString(fromModel.AsSpan());
        var fromHeadTypeHex  = Convert.ToHexString(fromHead.AsSpan());

        Assert.Contains(emittedAfter,
            r => Convert.ToHexString(r.EdgeTypeHash.AsSpan()) == attendsTypeHex);
        Assert.Contains(emittedAfter,
            r => Convert.ToHexString(r.EdgeTypeHash.AsSpan()) == hasMagTypeHex);
        Assert.Contains(emittedAfter,
            r => Convert.ToHexString(r.EdgeTypeHash.AsSpan()) == fromModelTypeHex);
        Assert.Contains(emittedAfter,
            r => Convert.ToHexString(r.EdgeTypeHash.AsSpan()) == fromHeadTypeHex);
    }

    [Fact]
    public async Task EmitOperatorShape_AgainstRealMiniLmAttentionTensor_ProducesOnS3LineString()
    {
        var safetensorsPath = Path.Combine(MiniLmDir, "model.safetensors");
        if (!File.Exists(safetensorsPath))
        {
            return; // env-gated
        }

        var pipeline = BuildPipeline();

        // Find a real W_Q-style attention tensor in MiniLM (BERT-style names).
        var reader = new TensorReader();
        using var handle = reader.Open(safetensorsPath);

        SafetensorEntry? wq = null;
        foreach (var e in handle.Entries)
        {
            if (e.Name.EndsWith("attention.self.query.weight", StringComparison.Ordinal))
            {
                wq = e;
                break;
            }
        }
        Assert.NotNull(wq);
        Assert.Equal(2, wq.Shape.Length);

        var rowCount = (int)wq.Shape[0];
        var colCount = (int)wq.Shape[1];
        var matrix   = new double[(long)rowCount * colCount];
        handle.ReadFloat64(wq, matrix);

        var modelEntity = pipeline.Resolver.Resolve("test_minilm_l6_v2");
        var headEntity  = await pipeline.Heads.ResolveHeadAsync(modelEntity, 0, 0, CancellationToken.None);

        await pipeline.Extractor.EmitOperatorShapeAsync(
            modelEntity, "huggingface_minilm", AttentionMatrixKind.Query,
            headEntity, matrix.AsMemory(), rowCount, colCount, CancellationToken.None);

        Assert.Single(pipeline.PhysicalityRecorder.Records);
        var record = pipeline.PhysicalityRecorder.Records[0];
        Assert.Equal(rowCount, record.Geometry.Length);

        foreach (var p in record.Geometry)
        {
            var norm = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z + p.W * p.W);
            Assert.InRange(norm, 1.0 - 1e-6, 1.0 + 1e-6);
        }
    }

    private static Pipeline BuildPipeline()
    {
        var hashing  = new IdentityHashing();
        var pool     = new CodepointPool(hashing);
        var resolver = new ConceptEntityResolver(pool, hashing);

        var entitySink = new EntityRecorder();
        var childSink  = new EntityChildRecorder();
        var edgeSink   = new EdgeRecorder();
        var physicality = new PhysicalityRecorder();
        var provenance = new ProvenanceRecorder(resolver);

        var f1 = new TextDecomposer(pool, hashing, entitySink, childSink);

        var heads            = new MechanisticHeadEntityResolver(hashing, resolver, f1);
        var firefly          = new FireflyExtraction(new KnnExact(), new LaplacianEigenmap());
        var matrixProjection = new MatrixToLineString4D(firefly);

        var extractor = new AttentionEdgeExtractor(
            heads, matrixProjection, hashing, resolver, physicality, edgeSink, provenance);

        return new Pipeline(extractor, heads, resolver, edgeSink, physicality, provenance);
    }

    private sealed record Pipeline(
        AttentionEdgeExtractor        Extractor,
        MechanisticHeadEntityResolver Heads,
        ConceptEntityResolver         Resolver,
        EdgeRecorder                  EdgeRecorder,
        PhysicalityRecorder           PhysicalityRecorder,
        ProvenanceRecorder            ProvenanceRecorder);

    private sealed class EntityRecorder : IEntityEmission
    {
        public List<EntityRecord> Records { get; } = new();
        public ValueTask EmitAsync(EntityRecord record, CancellationToken cancellationToken)
        { Records.Add(record); return ValueTask.CompletedTask; }
    }

    private sealed class EntityChildRecorder : IEntityChildEmission
    {
        public List<EntityChildRecord> Records { get; } = new();
        public ValueTask EmitAsync(EntityChildRecord record, CancellationToken cancellationToken)
        { Records.Add(record); return ValueTask.CompletedTask; }
    }

    private sealed class EdgeRecorder : IEdgeEmission
    {
        public List<EdgeRecord>       EdgeRecords   { get; } = new();
        public List<EdgeMemberRecord> MemberRecords { get; } = new();
        public ValueTask EmitEdgeAsync(EdgeRecord record, CancellationToken cancellationToken)
        { EdgeRecords.Add(record); return ValueTask.CompletedTask; }
        public ValueTask EmitMemberAsync(EdgeMemberRecord record, CancellationToken cancellationToken)
        { MemberRecords.Add(record); return ValueTask.CompletedTask; }
    }

    private sealed class PhysicalityRecorder : IPhysicalityEmission
    {
        public List<PhysicalityRecord> Records { get; } = new();
        public ValueTask EmitAsync(PhysicalityRecord record, CancellationToken cancellationToken)
        { Records.Add(record); return ValueTask.CompletedTask; }
    }

    private sealed class ProvenanceRecorder : IProvenance
    {
        private readonly ConceptEntityResolver _resolver;
        public ProvenanceRecorder(ConceptEntityResolver resolver) { _resolver = resolver; }
        public List<EntityProvenanceRecord> EntityProvenance { get; } = new();
        public List<EdgeProvenanceRecord>   EdgeProvenance   { get; } = new();
        public Task<AtomId> ResolveSourceAsync(string canonicalName, CancellationToken cancellationToken)
            => Task.FromResult(_resolver.Resolve(canonicalName));
        public ValueTask EmitEntityProvenanceAsync(EntityProvenanceRecord record, CancellationToken cancellationToken)
        { EntityProvenance.Add(record); return ValueTask.CompletedTask; }
        public ValueTask EmitEdgeProvenanceAsync(EdgeProvenanceRecord record, CancellationToken cancellationToken)
        { EdgeProvenance.Add(record); return ValueTask.CompletedTask; }
    }
}
