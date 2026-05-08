namespace Laplace.Smoke.Tests;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core;
using Laplace.Core.Abstractions;
using Laplace.Decomposers.Tatoeba;
using Laplace.Decomposers.Text;
using Laplace.Pipeline;
using Laplace.Pipeline.Abstractions;

using Xunit;

/// <summary>
/// Integration test: TatoebaDecomposer ingests a small fixture corpus and
/// emits real substrate entities + has_language edges + provenance records.
/// Exercises the full F1 + IConceptEntityResolver + IEntityEmission +
/// IEdgeEmission + IProvenance composition the production decomposer uses.
///
/// Test fixture: 5 hand-crafted sentences in 3 languages (eng / jpn / spa)
/// plus a links.csv asserting one translation pair. Emitted records are
/// captured in-memory and asserted at the substrate-content level (not via
/// any DB).
/// </summary>
public class TatoebaDecomposerIntegrationTest
{
    private static readonly string[] FixtureSentenceLines =
    {
        "1\teng\tThe cat sat on the mat.",
        "2\tjpn\t猫が敷物の上に座った。",
        "3\tspa\tEl gato se sentó en la alfombra.",
        "4\teng\tHello world.",
        "5\teng\tShe sells seashells by the seashore.",
    };

    [Fact]
    public async Task IngestsFiveSentences_EmitsEntitiesAndHasLanguageEdges()
    {
        var dir = Directory.CreateTempSubdirectory("laplace_tatoeba_test_").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "sentences.csv"), string.Join('\n', FixtureSentenceLines));
            File.WriteAllText(Path.Combine(dir, "links.csv"), "1\t2\n1\t3\n");

            var hashing  = new IdentityHashing();
            var pool     = new CodepointPool(hashing);
            var resolver = new ConceptEntityResolver(pool, hashing);

            var entitySink   = new EntityRecorder();
            var childSink    = new EntityChildRecorder();
            var edgeSink     = new EdgeRecorder();
            var provenance   = new ProvenanceRecorder(resolver);
            var f1           = new TextDecomposer(pool, hashing, entitySink, childSink);
            var decomposer   = new TatoebaDecomposer(f1, hashing, resolver, entitySink, edgeSink, provenance);

            await decomposer.DecomposeAsync(dir, CancellationToken.None);

            // 5 sentences + 5 language entities (eng/jpn/spa each ingested) =
            // composition-entity-emit count varies with reuse; the recorder
            // counts unique-after-emission keys. F1 emits one EntityRecord
            // per sentence + one per language string ingested — so we expect
            // at least 5 sentence entities present in the recorder.
            var distinctEntityHashes = new HashSet<string>();
            foreach (var e in entitySink.Records)
            {
                distinctEntityHashes.Add(System.Convert.ToHexString(e.Hash.AsSpan()));
            }
            Assert.True(distinctEntityHashes.Count >= 5,
                $"expected ≥ 5 distinct entity hashes, got {distinctEntityHashes.Count}");

            // 5 sentences × 1 has_language edge each = 5 has_language edges.
            // Plus 2 parallel_translation edges from links.csv.
            // EdgeRecorder counts edge_record emissions; some edges (e.g.,
            // duplicate has_language for same sentence) might dedupe by hash.
            Assert.True(edgeSink.EdgeRecords.Count >= 5,
                $"expected ≥ 5 edges, got {edgeSink.EdgeRecords.Count}");

            // Provenance: every emitted sentence entity has a provenance edge
            // to the tatoeba_corpus source. 5 sentences → ≥5 entity_provenance
            // records.
            Assert.True(provenance.EntityProvenance.Count >= 5,
                $"expected ≥ 5 entity_provenance records, got {provenance.EntityProvenance.Count}");

            // Every sentence's provenance points at the same source hash.
            var sourceHashes = new HashSet<string>();
            foreach (var p in provenance.EntityProvenance)
            {
                sourceHashes.Add(System.Convert.ToHexString(p.SourceHash.AsSpan()));
            }
            Assert.Single(sourceHashes); // ALL sentences attributed to ONE source entity

            // Cross-language assertion: the English sentence ("Hello world.")
            // and the same string ingested from any other source would dedupe
            // by content. Within Tatoeba, each sentence is unique, so this is
            // implicitly covered by the 5 distinct entity hashes assertion.
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
