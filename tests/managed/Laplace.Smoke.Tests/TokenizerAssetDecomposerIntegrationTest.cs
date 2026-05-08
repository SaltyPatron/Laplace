namespace Laplace.Smoke.Tests;

using System.Collections.Generic;
using System.IO;
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
/// F5 TokenizerAssetDecomposer integration test against a real HuggingFace
/// model (sentence-transformers/all-MiniLM-L6-v2) tokenizer.json. Each of
/// the model's ~30,522 vocabulary surface strings routes through F1
/// TextDecomposer; the resulting token-entity hashes are content-addressed
/// — so the same surface string from a DIFFERENT model's tokenizer would
/// land on the SAME substrate entity (cross-model dedup, no mapping table).
///
/// Test asserts:
///   1. Tokenizer parses (~30K vocab + special tokens).
///   2. Each "has_vocab_token" edge is emitted with model + token + position
///      members; provenance edge attaches each to the model source entity.
///   3. The substrate entity for token surface "the" matches what F1
///      TextDecomposer produces for the same string directly — cross-model
///      dedup will work because identity = content.
/// </summary>
public class TokenizerAssetDecomposerIntegrationTest
{
    private const string MiniLmTokenizerPath =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed1d9f207416be6d2e6f8de32d1f16199bf\tokenizer.json";

    [Fact]
    public async Task IngestsMiniLmVocab_EmitsHasVocabTokenEdgesPerToken_WithProvenance()
    {
        if (!File.Exists(MiniLmTokenizerPath))
        {
            return; // env-gated: model artifacts not present on this machine
        }

        var hashing  = new IdentityHashing();
        var pool     = new CodepointPool(hashing);
        var resolver = new ConceptEntityResolver(pool, hashing);

        var entitySink = new EntityRecorder();
        var childSink  = new EntityChildRecorder();
        var edgeSink   = new EdgeRecorder();
        var provenance = new ProvenanceRecorder(resolver);
        var f1         = new TextDecomposer(pool, hashing, entitySink, childSink);
        var decomposer = new TokenizerAssetDecomposer(f1, hashing, resolver, edgeSink, provenance);

        // Synthetic model entity hash — production code would derive this
        // from the model's safetensors + config Merkle composition.
        var modelHash = AtomId.FromSpan(new byte[AtomId.SizeBytes]);

        await decomposer.DecomposeAsync(
            modelHash,
            "huggingface_sentence_transformers_all_MiniLM_L6_v2",
            MiniLmTokenizerPath,
            CancellationToken.None);

        // MiniLM has 30,522 vocab tokens (BERT-WordPiece). Plus a handful
        // of added/special tokens. We expect at least 30K edges.
        Assert.True(edgeSink.EdgeRecords.Count >= 30_000,
            $"expected ≥ 30,000 has_vocab_token edges, got {edgeSink.EdgeRecords.Count}");

        // Each edge has 3 members (model, token, position).
        Assert.Equal(edgeSink.EdgeRecords.Count * 3, edgeSink.MemberRecords.Count);

        // Every edge has a provenance record attaching it to one source.
        Assert.Equal(edgeSink.EdgeRecords.Count, provenance.EdgeProvenance.Count);
        var provSourceHashes = new HashSet<string>();
        foreach (var p in provenance.EdgeProvenance)
        {
            provSourceHashes.Add(System.Convert.ToHexString(p.SourceHash.AsSpan()));
        }
        Assert.Single(provSourceHashes);

        // Cross-model dedup proof: F1.DecomposeAsync("the") should match
        // exactly the substrate entity hash that the tokenizer produced for
        // surface "the". Find the matching member in MemberRecords by
        // recomputing the expected token hash directly via F1.
        var expectedTheHash = await f1.DecomposeAsync("the", CancellationToken.None);
        var expectedHex     = System.Convert.ToHexString(expectedTheHash.AsSpan());

        var foundTheToken = false;
        foreach (var m in edgeSink.MemberRecords)
        {
            if (System.Convert.ToHexString(m.ParticipantHash.AsSpan()) == expectedHex)
            {
                foundTheToken = true;
                break;
            }
        }
        Assert.True(foundTheToken,
            "F1 'the' hash not found among emitted token participants — content addressing didn't dedupe");
    }

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
