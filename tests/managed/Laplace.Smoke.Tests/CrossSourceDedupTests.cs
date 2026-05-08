namespace Laplace.Smoke.Tests;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core;
using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline;
using Laplace.Pipeline.Abstractions;

using Xunit;

/// <summary>
/// G4 verification gate — cross-source dedup. The substrate's defining
/// property: ingesting the same content from N different sources lands on
/// ONE entity row + N provenance edges. Per CLAUDE.md invariants 1+4:
/// "knowledge IS edges and intersections" + "identity = content".
///
/// This test demonstrates the property at the F1+IProvenance level without
/// needing the full WordNet/OMW/Wiktionary/Tatoeba parsing pipelines —
/// each "source" is represented by its canonical name, resolved to a
/// substrate source entity via composition through the codepoint pool.
/// </summary>
public class CrossSourceDedupTests
{
    [Fact]
    public async Task Cat_From_Four_Sources_Yields_One_EntityHash_And_Four_ProvenanceRecords()
    {
        var hashing  = new IdentityHashing();
        var pool     = new CodepointPool(hashing);
        var sink     = new RecordingSink();
        var prov     = new InMemoryProvenance(pool, hashing);
        var f1       = new TextDecomposer(pool, hashing, sink, sink);

        var sources = new[] { "WordNet", "OMW", "Wiktionary", "Tatoeba" };

        var entityHashes = new HashSet<string>();
        foreach (var src in sources)
        {
            var sourceHash = await prov.ResolveSourceAsync(src, CancellationToken.None);
            var entityHash = await f1.DecomposeAsync("cat", CancellationToken.None);
            await prov.EmitEntityProvenanceAsync(
                new EntityProvenanceRecord(entityHash, sourceHash),
                CancellationToken.None);
            entityHashes.Add(System.Convert.ToHexString(entityHash.AsSpan()));
        }

        Assert.Single(entityHashes);                            // ONE substrate entity
        Assert.Equal(4, prov.RecordedEntityProvenance.Count);  // FOUR provenance records
        Assert.Equal(4, prov.UniqueSourceHashes.Count);        // FOUR distinct source entities

        // Each provenance record references the same entity_hash and a
        // different source_hash — exactly the G4 verification predicate.
        foreach (var record in prov.RecordedEntityProvenance)
        {
            Assert.Equal(entityHashes.First(), System.Convert.ToHexString(record.EntityHash.AsSpan()));
        }
    }

    private sealed class RecordingSink : IEntityEmission, IEntityChildEmission
    {
        public ValueTask EmitAsync(EntityRecord r, CancellationToken c) => ValueTask.CompletedTask;
        public ValueTask EmitAsync(EntityChildRecord r, CancellationToken c) => ValueTask.CompletedTask;
    }

    private sealed class InMemoryProvenance : IProvenance
    {
        private readonly CodepointPool   _pool;
        private readonly IIdentityHashing _hashing;

        public InMemoryProvenance(CodepointPool pool, IIdentityHashing hashing)
        {
            _pool    = pool;
            _hashing = hashing;
        }

        public List<EntityProvenanceRecord> RecordedEntityProvenance { get; } = new();
        public List<EdgeProvenanceRecord>   RecordedEdgeProvenance   { get; } = new();
        public HashSet<string> UniqueSourceHashes { get; } = new();

        public Task<AtomId> ResolveSourceAsync(string canonicalName, CancellationToken cancellationToken)
        {
            var children = new List<AtomId>(canonicalName.Length);
            var counts   = new List<int>(canonicalName.Length);
            foreach (var rune in canonicalName.EnumerateRunes())
            {
                children.Add(_pool.AtomIdFor(rune.Value));
                counts.Add(1);
            }
            // Note: this implementation does not RLE-collapse adjacent runs.
            // For "WordNet" / "OMW" / etc. there are no adjacent duplicate
            // runes, so the result matches the canonical (RLE-collapsed)
            // hash anyway. A production IProvenance should always RLE.
            var hash = _hashing.CompositionId(children, counts);
            UniqueSourceHashes.Add(System.Convert.ToHexString(hash.AsSpan()));
            return Task.FromResult(hash);
        }

        public ValueTask EmitEntityProvenanceAsync(EntityProvenanceRecord record, CancellationToken cancellationToken)
        {
            RecordedEntityProvenance.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask EmitEdgeProvenanceAsync(EdgeProvenanceRecord record, CancellationToken cancellationToken)
        {
            RecordedEdgeProvenance.Add(record);
            return ValueTask.CompletedTask;
        }
    }
}

internal static class HashSetExtensions
{
    public static string First(this System.Collections.Generic.HashSet<string> set)
    {
        foreach (var s in set) { return s; }
        throw new System.InvalidOperationException("set is empty");
    }
}
