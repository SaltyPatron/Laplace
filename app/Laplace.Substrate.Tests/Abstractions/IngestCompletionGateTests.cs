using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class IngestCompletionGateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresentEntityWithoutReceipt_DoesNotSkipRecord(bool cached)
    {
        var record = new TrunkRecord(Id(1));
        var records = new List<object> { record };
        var reader = new ReceiptReader { CacheEntities = cached };
        reader.Entities.Add(record.TrunkRootId);
        var handler = new CountingHandler();
        using var builder = new SubstrateChangeBuilder(Id(90), "completion-gate", null);

        var skipped = await IngestExistenceGate.RemovePresentAsync(
            records, handler, reader, builder, CancellationToken.None);

        Assert.Empty(skipped);
        Assert.Same(record, Assert.Single(records));
        Assert.Equal(0, handler.WitnessCalls);
        Assert.Empty(reader.ReceiptProbes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionRecordWithOnlyEntityPresent_RemainsForComposition(bool cached)
    {
        var record = new CompletedRecord(Id(1), Key(Id(2), Id(3)));
        var records = new List<object> { record };
        var reader = new ReceiptReader { CacheEntities = cached };
        reader.Entities.Add(record.TrunkRootId);
        // Another unit completed by the same witness is not proof for this source unit.
        reader.Receipts.Add(Key(Id(2), Id(4)));
        var handler = new CountingHandler();
        using var builder = new SubstrateChangeBuilder(Id(90), "completion-gate", null);

        var skipped = await IngestExistenceGate.RemovePresentAsync(
            records, handler, reader, builder, CancellationToken.None);

        Assert.Empty(skipped);
        Assert.Same(record, Assert.Single(records));
        Assert.Equal(0, handler.WitnessCalls);
        Assert.Equal(new[] { record.Completion!.Value }, Assert.Single(reader.ReceiptProbes));
    }

    [Fact]
    public async Task ExactDurableReceipt_SkipsWithoutReemittingWitness()
    {
        var record = new CompletedRecord(Id(1), Key(Id(2), Id(3)));
        var records = new List<object> { record };
        var reader = new ReceiptReader();
        reader.Receipts.Add(record.Completion!.Value);
        var handler = new CountingHandler();
        using var builder = new SubstrateChangeBuilder(Id(90), "completion-gate", null);

        var skipped = await IngestExistenceGate.RemovePresentAsync(
            records, handler, reader, builder, CancellationToken.None);

        Assert.Empty(records);
        var completed = Assert.Single(skipped);
        Assert.Same(record, completed.Record);
        Assert.Equal(3, completed.Units);
        Assert.Equal(0, handler.WitnessCalls);
        Assert.Equal(0, reader.EntityProbeCalls);
    }

    [Fact]
    public async Task CompletionsAreBatched_AndRequireWitnessUnitLayerAndDigest()
    {
        var accepted = new CompletedRecord(Id(1), Key(Id(2), Id(3)));
        var sameUnitOtherWitness = new CompletedRecord(Id(4), Key(Id(5), Id(3)));
        var sameUnitOtherLayer = new CompletedRecord(Id(8), Key(Id(2), Id(3), layer: 2));
        var sameUnitOtherDigest = new CompletedRecord(Id(9), Key(Id(2), Id(3), digest: Id(10)));
        var absent = new CompletedRecord(Id(6), Key(Id(2), Id(7)));
        var records = new List<object>
            { accepted, sameUnitOtherWitness, sameUnitOtherLayer, sameUnitOtherDigest, absent, accepted };
        var reader = new ReceiptReader();
        reader.Receipts.Add(accepted.Completion!.Value);
        var handler = new CountingHandler();
        using var builder = new SubstrateChangeBuilder(Id(90), "completion-gate", null);

        var skipped = await IngestExistenceGate.RemovePresentAsync(
            records, handler, reader, builder, CancellationToken.None);

        Assert.Equal(new object[]
            { sameUnitOtherWitness, sameUnitOtherLayer, sameUnitOtherDigest, absent }, records);
        Assert.Equal(2, skipped.Length);
        Assert.All(skipped, x => Assert.Same(accepted, x.Record));
        // One batched probe over the distinct keys.
        Assert.Equal(5, Assert.Single(reader.ReceiptProbes).Length);
        Assert.Equal(0, reader.EntityProbeCalls);
        Assert.Equal(0, handler.WitnessCalls);
    }

    [Fact]
    public async Task UnspecifiedCompletionIdentity_CannotAuthorizeSkip()
    {
        var records = new List<object>
        {
            new CompletedRecord(Id(1), new IngestUnitCompletionKey(default, Id(3), 1)),
            new CompletedRecord(Id(2), new IngestUnitCompletionKey(Id(4), default, 1)),
            new CompletedRecord(Id(5), null),
        };
        var reader = new ReceiptReader { CacheEntities = true };
        reader.Entities.UnionWith(new[] { Id(1), Id(2) });
        using var builder = new SubstrateChangeBuilder(Id(90), "completion-gate", null);

        var skipped = await IngestExistenceGate.RemovePresentAsync(
            records, new CountingHandler(), reader, builder, CancellationToken.None);

        Assert.Empty(skipped);
        Assert.Equal(3, records.Count);
        Assert.Empty(reader.ReceiptProbes);
    }

    private static IngestUnitCompletionKey Key(
        Hash128 witness, Hash128 unit, int layer = 1, Hash128? digest = null) =>
        new(witness, unit, layer, digest);

    private static Hash128 Id(byte value)
    {
        var bytes = new byte[16];
        bytes[0] = value;
        return Hash128.FromBytes(bytes);
    }

    private sealed record TrunkRecord(Hash128 TrunkRootId) : ITrunkRootRecord;

    private sealed record CompletedRecord(
        Hash128 TrunkRootId,
        IngestUnitCompletionKey? Completion) : ITrunkRootRecord, IIngestCompletionRecord;

    private sealed class CountingHandler : IIngestRecordHandler<object>
    {
        internal int WitnessCalls;
        public IIngestDeferredUnit CreateDeferredUnit(object record) =>
            throw new InvalidOperationException("The gate must leave composition to its caller.");
        public void WalkWitness(
            object record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit) =>
            WitnessCalls++;
        public long UnitsPerRecord(object record) => 3;
    }

    private sealed class ReceiptReader : ISubstrateReader
    {
        internal bool CacheEntities;
        internal int EntityProbeCalls;
        internal readonly HashSet<Hash128> Entities = [];
        internal readonly HashSet<IngestUnitCompletionKey> Receipts = [];
        internal readonly List<IngestUnitCompletionKey[]> ReceiptProbes = [];

        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(
            Hash128 sourceId, int layerOrder, CancellationToken ct = default) => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default) =>
            Task.FromResult(0L);
        public bool IsProvenPresent(Hash128 id) => CacheEntities && Entities.Contains(id);

        public Task<byte[]> EntitiesExistBitmapAsync(
            IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            EntityProbeCalls++;
            var bitmap = new byte[(candidates.Count + 7) / 8];
            for (int i = 0; i < candidates.Count; i++)
                if (Entities.Contains(candidates[i]))
                    bitmap[i >> 3] |= (byte)(1 << (i & 7));
            return Task.FromResult(bitmap);
        }

        public Task<IReadOnlySet<IngestUnitCompletionKey>> CompletedUnitsAsync(
            IReadOnlyList<IngestUnitCompletionKey> keys, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReceiptProbes.Add(keys.ToArray());
            return Task.FromResult<IReadOnlySet<IngestUnitCompletionKey>>(
                keys.Where(Receipts.Contains).ToHashSet());
        }
    }
}
