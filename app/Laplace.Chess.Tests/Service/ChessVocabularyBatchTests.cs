using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Tests.Service;

[Trait("Tier", "fast")]
public sealed class ChessVocabularyBatchTests
{
    private static readonly ChessVocabulary.BootstrapSource[] Sources =
    [
        new(Hash128.OfCanonical("test/chess/source/a"), "ChessA",
            Hash128.OfCanonical("test/chess/trust/a")),
        new(Hash128.OfCanonical("test/chess/source/b"), "ChessB",
            Hash128.OfCanonical("test/chess/trust/b")),
        new(Hash128.OfCanonical("test/chess/source/c"), "ChessC",
            Hash128.OfCanonical("test/chess/trust/c")),
    ];

    [Fact]
    public async Task BootstrapMany_UsesOneProbeAndOneApplyForAllAbsentSources()
    {
        var reader = new Reader();
        var writer = new Writer();

        var names = await ChessVocabulary.BootstrapManyAsync(writer, Sources, reader: reader);

        Assert.Equal(1, reader.ProbeCalls);
        Assert.Equal(Sources.Select(static x => x.SourceId), Assert.Single(reader.Probes));
        Assert.Equal(1, writer.ApplyManyCalls);
        Assert.Equal(3, Assert.Single(writer.Batches).Count);
        Assert.Equal(0, writer.ApplyOneCalls);
        Assert.NotEmpty(names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Sources, source => Assert.Contains(source.SourceId, reader.Proven));
    }

    [Fact]
    public async Task BootstrapMany_ProbesOnlyUncachedSourcesAndAppliesOnlyAbsentSources()
    {
        var reader = new Reader(
            cached: [Sources[0].SourceId],
            stored: [Sources[1].SourceId]);
        var writer = new Writer();

        await ChessVocabulary.BootstrapManyAsync(
            writer, [Sources[0], Sources[1], Sources[2], Sources[2]], reader: reader);

        Assert.Equal(1, reader.ProbeCalls);
        Assert.Equal(
            [Sources[1].SourceId, Sources[2].SourceId],
            Assert.Single(reader.Probes));
        Assert.Equal(1, writer.ApplyManyCalls);
        var change = Assert.Single(Assert.Single(writer.Batches));
        Assert.Equal(Sources[2].SourceId, change.Metadata.SourceId);
        Assert.Equal(0, writer.ApplyOneCalls);
        Assert.Contains(Sources[1].SourceId, reader.Proven);
        Assert.Contains(Sources[2].SourceId, reader.Proven);
    }

    private sealed class Writer : ISubstrateWriter
    {
        public int ApplyOneCalls { get; private set; }
        public int ApplyManyCalls { get; private set; }
        public List<IReadOnlyList<SubstrateChange>> Batches { get; } = [];

        public Task<ApplyResult> ApplyAsync(
            SubstrateChange change, CancellationToken ct = default)
        {
            ApplyOneCalls++;
            throw new InvalidOperationException("bulk bootstrap must not use scalar apply");
        }

        public Task<ApplyResult> ApplyManyAsync(
            IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        {
            ApplyManyCalls++;
            Batches.Add(changes);
            return Task.FromResult(new ApplyResult(
                changes.Sum(static x => x.Entities.Length),
                changes.Sum(static x => x.Entities.Length),
                changes.Sum(static x => x.Physicalities.Length),
                changes.Sum(static x => x.Physicalities.Length),
                changes.Sum(static x => x.Attestations.Length),
                changes.Sum(static x => x.Attestations.Length),
                1, TimeSpan.Zero, false));
        }
    }

    private sealed class Reader : ISubstrateReader
    {
        private readonly HashSet<Hash128> _cached;
        private readonly HashSet<Hash128> _stored;

        public Reader(IEnumerable<Hash128>? cached = null, IEnumerable<Hash128>? stored = null)
        {
            _cached = cached?.ToHashSet() ?? [];
            _stored = stored?.ToHashSet() ?? [];
        }

        public int ProbeCalls { get; private set; }
        public List<IReadOnlyList<Hash128>> Probes { get; } = [];
        public HashSet<Hash128> Proven { get; } = [];

        public bool IsProvenPresent(Hash128 id) => _cached.Contains(id);

        public void MarkProven(IReadOnlyList<Hash128> ids)
        {
            foreach (var id in ids)
            {
                _cached.Add(id);
                Proven.Add(id);
            }
        }

        public Task<byte[]> EntitiesExistBitmapAsync(
            IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            ProbeCalls++;
            Probes.Add(candidates.ToArray());
            var bitmap = new byte[(candidates.Count + 7) / 8];
            for (int i = 0; i < candidates.Count; i++)
                if (_stored.Contains(candidates[i])) bitmap[i >> 3] |= (byte)(1 << (i & 7));
            return Task.FromResult(bitmap);
        }

        public Task<bool> HasSourceEverCompletedAsync(
            int layerOrder, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> HasSourceCompletedAsync(
            Hash128 sourceId, int layerOrder, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<long> CountEntitiesByTypeAsync(
            Hash128 typeId, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
