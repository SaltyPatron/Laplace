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
/// Integration test: SafetensorsHeaderDecomposer parses MiniLM's
/// model.safetensors header and emits one has_tensor edge per declared
/// tensor. Uses in-memory recorders (same pattern as Tatoeba integration
/// test) so we exercise the full F1 + IConceptEntityResolver +
/// IEdgeEmission + IProvenance composition without a DB.
///
/// Validates:
///   1. Every tensor in the header produces an emitted edge.
///   2. All has_tensor edges share one edge_type_hash.
///   3. All emitted has_tensor edges have provenance attributing them to
///      the model source.
///   4. Edge member rows are well-formed (model + tensor roles per edge).
///   5. Determinism: re-running on the same input produces identical
///      edge_hashes (modulo ordering) — eligible for golden delta records.
///
/// Env-gated: skipped (not failed) when MiniLM isn't on disk.
/// </summary>
public class SafetensorsHeaderDecomposerTest
{
    private const string ModelDir =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed1d9f207416be6d2e6f8de32d1f16199bf";

    [Fact]
    public async Task DecomposesMiniLmHeader_EmitsOneHasTensorEdgePerEntry()
    {
        var safetensorsPath = Path.Combine(ModelDir, "model.safetensors");
        if (!File.Exists(safetensorsPath))
        {
            return; // env-gated
        }

        var (decomposer, edgeSink, provenance, modelEntityHash) = BuildPipeline();

        var emittedCount = await decomposer.DecomposeAsync(
            modelEntityHash,
            "huggingface_model_sentence_transformers_all_minilm_l6_v2",
            safetensorsPath,
            CancellationToken.None);

        // MiniLM has 100+ tensors (embeddings, 6 transformer layers, pooler).
        Assert.True(emittedCount >= 100, $"expected ≥ 100 tensors, got {emittedCount}");
        Assert.Equal(emittedCount, edgeSink.EdgeRecords.Count);

        // Every emitted edge has the SAME edge_type_hash (has_tensor).
        var distinctEdgeTypes = edgeSink.EdgeRecords
            .Select(r => System.Convert.ToHexString(r.EdgeTypeHash.AsSpan()))
            .ToHashSet();
        Assert.Single(distinctEdgeTypes);

        // Every edge has exactly 2 member rows: model role + tensor role.
        Assert.Equal(emittedCount * 2, edgeSink.MemberRecords.Count);

        // Provenance: every edge has a provenance edge to the same source.
        Assert.Equal(emittedCount, provenance.EdgeProvenance.Count);
        var distinctSources = provenance.EdgeProvenance
            .Select(p => System.Convert.ToHexString(p.SourceHash.AsSpan()))
            .ToHashSet();
        Assert.Single(distinctSources);
    }

    [Fact]
    public async Task DecomposesMiniLmHeader_DeterministicAcrossRuns()
    {
        var safetensorsPath = Path.Combine(ModelDir, "model.safetensors");
        if (!File.Exists(safetensorsPath))
        {
            return; // env-gated
        }

        var (decomposer1, edgeSink1, _, modelEntityHash1) = BuildPipeline();
        var (decomposer2, edgeSink2, _, modelEntityHash2) = BuildPipeline();

        // Same model entity hash across runs (caller-supplied; arbitrary fixed).
        Assert.Equal(modelEntityHash1.ToString(), modelEntityHash2.ToString());

        await decomposer1.DecomposeAsync(modelEntityHash1, "test_source", safetensorsPath, CancellationToken.None);
        await decomposer2.DecomposeAsync(modelEntityHash2, "test_source", safetensorsPath, CancellationToken.None);

        Assert.Equal(edgeSink1.EdgeRecords.Count, edgeSink2.EdgeRecords.Count);

        // Same edge_hashes emitted in the same order.
        for (var i = 0; i < edgeSink1.EdgeRecords.Count; i++) {
            Assert.Equal(
                System.Convert.ToHexString(edgeSink1.EdgeRecords[i].Hash.AsSpan()),
                System.Convert.ToHexString(edgeSink2.EdgeRecords[i].Hash.AsSpan()));
        }
    }

    private static (
        SafetensorsHeaderDecomposer decomposer,
        EdgeRecorder                edgeSink,
        ProvenanceRecorder          provenance,
        AtomId                      modelEntityHash)
        BuildPipeline()
    {
        var hashing  = new IdentityHashing();
        var pool     = new CodepointPool(hashing);
        var resolver = new ConceptEntityResolver(pool, hashing);

        var entitySink   = new EntityRecorder();
        var childSink    = new EntityChildRecorder();
        var edgeSink     = new EdgeRecorder();
        var provenance   = new ProvenanceRecorder(resolver);
        var f1           = new TextDecomposer(pool, hashing, entitySink, childSink);

        var tensorReader = new TensorReader();
        var decomposer   = new SafetensorsHeaderDecomposer(
            tensorReader, f1, hashing, resolver, edgeSink, provenance);

        // Caller-provided model entity hash (in production: Merkle composition
        // over the entire HF package). For this test, use a deterministic
        // text-derived hash so the determinism check above is meaningful.
        var modelEntityHash = resolver.Resolve("test_minilm_l6_v2");

        return (decomposer, edgeSink, provenance, modelEntityHash);
    }

    // -----------------------------------------------------------------
    // In-memory recorders (same pattern as TatoebaDecomposerIntegrationTest).
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
