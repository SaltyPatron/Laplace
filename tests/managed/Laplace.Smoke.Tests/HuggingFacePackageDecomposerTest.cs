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
/// End-to-end F5 integration test: HuggingFacePackageDecomposer ingests a
/// real HuggingFace model (sentence-transformers/all-MiniLM-L6-v2) and
/// emits substrate state via in-memory recorders. Validates that:
///
///   1. LayoutResolver + Manifest discover the package correctly.
///   2. TokenizerAssetDecomposer emits has_vocab_token edges for every
///      vocab entry (~30,522 for MiniLM BERT WordPiece).
///   3. SafetensorsHeaderDecomposer emits has_tensor edges for every
///      tensor in model.safetensors (~100+ for MiniLM 6-layer BERT).
///   4. All emitted edges share consistent edge_type_hashes and have
///      provenance attributing them to the model source.
///
/// This is the first F5 product-slice integration test that actually
/// exercises the package decomposer pipeline end-to-end against a real
/// model on disk. Env-gated: skipped when MiniLM isn't on disk.
/// </summary>
public class HuggingFacePackageDecomposerTest
{
    private const string MiniLmPath =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2";

    [Fact]
    public async Task DecomposesMiniLmEndToEnd_EmitsTokenizerAndTensorEdges()
    {
        if (!Directory.Exists(MiniLmPath)) { return; }

        var hashing  = new IdentityHashing();
        var pool     = new CodepointPool(hashing);
        var resolver = new ConceptEntityResolver(pool, hashing);

        var entitySink   = new EntityRecorder();
        var childSink    = new EntityChildRecorder();
        var edgeSink     = new EdgeRecorder();
        var provenance   = new ProvenanceRecorder(resolver);
        var f1           = new TextDecomposer(pool, hashing, entitySink, childSink);

        var tokenizerDecomposer = new TokenizerAssetDecomposer(
            f1, hashing, resolver, edgeSink, provenance);
        var safetensorsDecomposer = new SafetensorsHeaderDecomposer(
            new TensorReader(), f1, hashing, resolver, edgeSink, provenance);
        var orchestrator = new HuggingFacePackageDecomposer(
            tokenizerDecomposer, safetensorsDecomposer);

        var modelEntityHash = resolver.Resolve("test_minilm_l6_v2_e2e");
        var result = await orchestrator.DecomposeAsync(
            modelEntityHash,
            "huggingface_model_sentence_transformers_all_minilm_l6_v2",
            MiniLmPath,
            CancellationToken.None);

        // 1. Tokenizer + safetensors both decomposed.
        Assert.Equal(1, result.TokenizerAssetsDecomposed);
        Assert.Equal(1, result.SafetensorShardsDecomposed);

        // 2. Tensor count: MiniLM has ~100 tensors (6 layers × 12 weight
        //    matrices/vectors per layer + embeddings + pooler).
        Assert.True(result.TotalTensorsDecomposed >= 100,
            $"expected ≥ 100 tensors, got {result.TotalTensorsDecomposed}");

        // 3. Vocab + tensor edges emitted. MiniLM vocab is 30,522.
        //    has_vocab_token edges + has_tensor edges share the edge sink.
        Assert.True(edgeSink.EdgeRecords.Count >= 30_000,
            $"expected ≥ 30,000 edges (vocab + tensors), got {edgeSink.EdgeRecords.Count}");

        // 4. Two distinct edge_type_hashes: has_vocab_token + has_tensor.
        var edgeTypes = edgeSink.EdgeRecords
            .Select(r => System.Convert.ToHexString(r.EdgeTypeHash.AsSpan()))
            .ToHashSet();
        Assert.Equal(2, edgeTypes.Count);

        // 5. Provenance: every edge attributes to one source.
        var sources = provenance.EdgeProvenance
            .Select(p => System.Convert.ToHexString(p.SourceHash.AsSpan()))
            .ToHashSet();
        Assert.Single(sources);
    }

    // -----------------------------------------------------------------
    // In-memory recorders (same pattern as Tatoeba + SafetensorsHeader tests).
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
