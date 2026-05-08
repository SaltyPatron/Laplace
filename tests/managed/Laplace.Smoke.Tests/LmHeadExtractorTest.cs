namespace Laplace.Smoke.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core;
using Laplace.Core.Abstractions;
using Laplace.Decomposers.Model.Extractors;
using Laplace.Decomposers.Model.OperatorShapes;
using Laplace.Decomposers.Text;
using Laplace.Pipeline;
using Laplace.Pipeline.Abstractions;

using Xunit;

/// <summary>
/// LmHeadExtractor under the corrected substrate-native design (#38, #115).
/// One source-blind edge kind `lm_predicts`; W_lm_head emits as one
/// LINESTRING4D per model (no per-head/neuron mechanistic decomposition,
/// LM head is wholesale per-model).
/// </summary>
public class LmHeadExtractorTest
{
    [Fact]
    public async Task EmitOperatorShape_EmitsOnePhysicalityRecordPerModel()
    {
        var pipeline = BuildPipeline();

        var modelEntity = pipeline.Resolver.Resolve("test_model_lm_head");

        const int rowCount = 64;
        const int colCount = 32;
        var matrix = new double[rowCount * colCount];
        var rng = new Random(7);
        for (var i = 0; i < matrix.Length; i++) { matrix[i] = rng.NextDouble() - 0.5; }

        var op1 = await pipeline.Extractor.EmitOperatorShapeAsync(
            modelEntity, "src", matrix.AsMemory(), rowCount, colCount, CancellationToken.None);

        Assert.Single(pipeline.PhysicalityRecorder.Records);

        // Re-emission with same model entity yields the SAME operator-shape entity hash
        // (content-addressed dedup) — emits a second physicality record with identical
        // EntityHash but the recorder doesn't dedup at the ON CONFLICT layer.
        var op2 = await pipeline.Extractor.EmitOperatorShapeAsync(
            modelEntity, "src", matrix.AsMemory(), rowCount, colCount, CancellationToken.None);

        Assert.Equal(
            Convert.ToHexString(op1.AsSpan()),
            Convert.ToHexString(op2.AsSpan()));
    }

    [Fact]
    public async Task EmitDiscreteEdge_SameResidualVocabPair_DifferentModels_ProducesTwoEdgesWithDistinctProvenance()
    {
        var pipeline = BuildPipeline();

        var modelA = pipeline.Resolver.Resolve("model_a");
        var modelB = pipeline.Resolver.Resolve("model_b");

        // Source-BLIND: same (residual, vocab) pair, two models attesting → ONE edge.
        var residualPattern = pipeline.Resolver.Resolve("residual_pattern_for_word_cat");
        var vocabToken      = pipeline.Resolver.Resolve("cat");
        var ts              = pipeline.Resolver.Resolve("1741000000");
        var mag             = pipeline.Resolver.Resolve("4.2");

        var edgeA = await pipeline.Extractor.EmitDiscreteEdgeAsync(
            modelA, ts, residualPattern, vocabToken, mag, CancellationToken.None);
        var edgeB = await pipeline.Extractor.EmitDiscreteEdgeAsync(
            modelB, ts, residualPattern, vocabToken, mag, CancellationToken.None);

        Assert.Equal(
            Convert.ToHexString(edgeA.AsSpan()),
            Convert.ToHexString(edgeB.AsSpan()));

        var lmPredicts = pipeline.Resolver.Resolve("lm_predicts");
        var provenanceForEdge = pipeline.ProvenanceRecorder.EdgeProvenance
            .Where(p =>
                Convert.ToHexString(p.EdgeHash.AsSpan()) == Convert.ToHexString(edgeA.AsSpan()) &&
                Convert.ToHexString(p.EdgeTypeHash.AsSpan()) == Convert.ToHexString(lmPredicts.AsSpan()))
            .ToList();
        Assert.Equal(2, provenanceForEdge.Count);

        // Distinct provenance entities (different model in each composition).
        var distinctProvenance = provenanceForEdge
            .Select(p => Convert.ToHexString(p.SourceHash.AsSpan()))
            .Distinct()
            .Count();
        Assert.Equal(2, distinctProvenance);
    }

    private static Pipeline BuildPipeline()
    {
        var hashing  = new IdentityHashing();
        var pool     = new CodepointPool(hashing);
        var resolver = new ConceptEntityResolver(pool, hashing);

        var entitySink   = new EntityRecorder();
        var childSink    = new EntityChildRecorder();
        var edgeSink     = new EdgeRecorder();
        var physicality  = new PhysicalityRecorder();
        var provenance   = new ProvenanceRecorder(resolver);

        var firefly          = new FireflyExtraction(new KnnExact(), new LaplacianEigenmap());
        var matrixProjection = new MatrixToLineString4D(firefly);

        var extractor = new LmHeadExtractor(
            matrixProjection, hashing, resolver, physicality, edgeSink, provenance);

        return new Pipeline(extractor, resolver, edgeSink, physicality, provenance);
    }

    private sealed record Pipeline(
        LmHeadExtractor       Extractor,
        ConceptEntityResolver Resolver,
        EdgeRecorder          EdgeRecorder,
        PhysicalityRecorder   PhysicalityRecorder,
        ProvenanceRecorder    ProvenanceRecorder);

    private sealed class EntityRecorder : IEntityEmission
    {
        public ValueTask EmitAsync(EntityRecord record, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class EntityChildRecorder : IEntityChildEmission
    {
        public ValueTask EmitAsync(EntityChildRecord record, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
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
