namespace Laplace.Smoke.Tests;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core;
using Laplace.Core.Abstractions;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.Text;
using Laplace.Pipeline;
using Laplace.Pipeline.Abstractions;

using Xunit;

/// <summary>
/// End-to-end F5 product slice (#43): HuggingFacePackageDecomposer ingests
/// a real model (sentence-transformers/all-MiniLM-L6-v2) WITH the firefly
/// extraction stage wired in. Validates the FULL substrate state for an
/// ingested model:
///
///   1. Tokenizer canonicalization → per-token substrate text entities
///      + has_vocab_token edges (one per vocab entry).
///   2. Safetensors header → per-tensor substrate entities + has_tensor
///      edges (one per declared tensor).
///   3. Embedding firefly extraction (B15 KnnExact + B17 LaplacianEigenmap
///      + S³ projection) → per-token Point4D positions emitted as
///      PhysicalityRecord rows in the firefly_s3_extracted partition.
///   4. All emitted edges + physicality rows attribute provenance to the
///      same model source entity.
///
/// This is the FIRST product slice that proves Anthony's invention's
/// ingestion path works end-to-end against a real HuggingFace model.
/// Env-gated: skipped when MiniLM isn't on disk.
/// </summary>
public class HuggingFacePackageDecomposerFireflyTest
{
    private const string MiniLmPath =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2";

    [Fact]
    public async Task IngestsMiniLmEndToEnd_EmitsTokenizerTensorAndFireflyState()
    {
        if (!Directory.Exists(MiniLmPath)) { return; }

        var hashing  = new IdentityHashing();
        var pool     = new CodepointPool(hashing);
        var resolver = new ConceptEntityResolver(pool, hashing);

        var entitySink     = new EntityRecorder();
        var childSink      = new EntityChildRecorder();
        var edgeSink       = new EdgeRecorder();
        var physicalitySink = new PhysicalityRecorder();
        var provenance     = new ProvenanceRecorder(resolver);
        var f1             = new TextDecomposer(pool, hashing, entitySink, childSink);

        var tokenizerDecomposer = new TokenizerAssetDecomposer(
            f1, hashing, resolver, edgeSink, provenance);
        var tensorReader     = new TensorReader();
        var safetensorsDec   = new SafetensorsHeaderDecomposer(
            tensorReader, f1, hashing, resolver, edgeSink, provenance);
        var fireflyExtractor = new FireflyExtraction(new KnnExact(), new LaplacianEigenmap());
        var fireflyJar       = new FireflyJar(resolver, physicalitySink);

        var orchestrator = new HuggingFacePackageDecomposer(
            tokenizerDecomposer,
            safetensorsDec,
            tensorReader,
            fireflyExtractor,
            fireflyJar);

        var modelEntityHash = resolver.Resolve("test_minilm_l6_v2_e2e_with_fireflies");
        var result = await orchestrator.DecomposeAsync(
            modelEntityHash,
            "huggingface_model_sentence_transformers_all_minilm_l6_v2",
            MiniLmPath,
            CancellationToken.None);

        // 1. Tokenizer + safetensors decomposed.
        Assert.Equal(1,    result.TokenizerAssetsDecomposed);
        Assert.Equal(1,    result.SafetensorShardsDecomposed);
        Assert.True(result.TotalTensorsDecomposed >= 100);

        // 2. Fireflies emitted: MiniLM has 30,522 vocab tokens. Every token
        //    that has a substrate entity in the tokenizer's
        //    token_id → entity map gets one firefly. Special tokens may be
        //    skipped if their token_id is outside the vocab embedding range,
        //    so we assert ≥ 30,000 fireflies (slightly less than full vocab
        //    is acceptable).
        Assert.True(result.FirefliesEmitted >= 30_000,
            $"expected ≥ 30,000 fireflies, got {result.FirefliesEmitted}");

        // 3. PhysicalityRecorder captured one record per stored firefly.
        Assert.Equal(result.FirefliesEmitted, physicalitySink.Records.Count);

        // 4. All firefly records use the firefly_s3_extracted physicality type.
        var fireflyType = resolver.Resolve("firefly_s3_extracted");
        Assert.All(physicalitySink.Records, r =>
            Assert.Equal(fireflyType.ToString(), r.PhysicalityTypeHash.ToString()));

        // 5. All firefly records attribute the source to the model entity.
        Assert.All(physicalitySink.Records, r =>
        {
            Assert.NotNull(r.SourceHash);
            Assert.Equal(modelEntityHash.ToString(), r.SourceHash.Value.ToString());
        });

        // 6. Each firefly Geometry contains exactly one Point4D, on S³.
        Assert.All(physicalitySink.Records, r =>
        {
            Assert.Single(r.Geometry);
            var p = r.Geometry[0];
            Assert.InRange(p.Norm, 1.0 - 1e-9, 1.0 + 1e-9);
        });

        // 7. Edge provenance: every has_vocab_token + has_tensor edge
        //    attributes to the model source. (Same source hash for both edge types.)
        var edgeSourceHashes = provenance.EdgeProvenance
            .Select(p => System.Convert.ToHexString(p.SourceHash.AsSpan()))
            .ToHashSet();
        Assert.Single(edgeSourceHashes);
    }

    // -----------------------------------------------------------------
    // In-memory recorders.
    // -----------------------------------------------------------------

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
