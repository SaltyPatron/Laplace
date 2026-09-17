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
        var record = new CompletedRecord(Id(1), Id(2), Id(3));
        var records = new List<object> { record };
        var reader = new ReceiptReader { CacheEntities = cached };
        reader.Entities.Add(record.TrunkRootId);
        // Another receipt of the same type is not proof for this source unit.
        reader.Receipts.Add((record.CompletionAttestationTypeId, Id(4)));
        var handler = new CountingHandler();
        using var builder = new SubstrateChangeBuilder(Id(90), "completion-gate", null);

        var skipped = await IngestExistenceGate.RemovePresentAsync(
            records, handler, reader, builder, CancellationToken.None);

        Assert.Empty(skipped);
        Assert.Same(record, Assert.Single(records));
        Assert.Equal(0, handler.WitnessCalls);
        Assert.Equal(new[] { record.CompletionAttestationId },
            Assert.Single(reader.ReceiptProbes).Ids);
    }

    [Fact]
    public async Task ExactDurableReceipt_SkipsWithoutReemittingWitness()
    {
        var record = new CompletedRecord(Id(1), Id(2), Id(3));
        var records = new List<object> { record };
        var reader = new ReceiptReader();
        reader.Receipts.Add((record.CompletionAttestationTypeId, record.CompletionAttestationId));
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
    public async Task ReceiptsAreBatchedByType_AndRequireBothTypeAndIdentity()
    {
        var accepted = new CompletedRecord(Id(1), Id(2), Id(3));
        var sameIdOtherType = new CompletedRecord(Id(4), Id(5), Id(3));
        var absent = new CompletedRecord(Id(6), Id(2), Id(7));
        var records = new List<object> { accepted, sameIdOtherType, absent, accepted };
        var reader = new ReceiptReader();
        reader.Receipts.Add((accepted.CompletionAttestationTypeId, accepted.CompletionAttestationId));
        var handler = new CountingHandler();
        using var builder = new SubstrateChangeBuilder(Id(90), "completion-gate", null);

        var skipped = await IngestExistenceGate.RemovePresentAsync(
            records, handler, reader, builder, CancellationToken.None);

        Assert.Equal(new object[] { sameIdOtherType, absent }, records);
        Assert.Equal(2, skipped.Length);
        Assert.All(skipped, x => Assert.Same(accepted, x.Record));
        Assert.Equal(2, reader.ReceiptProbes.Count);
        Assert.Equal(new[] { Id(3), Id(7) },
            reader.ReceiptProbes.Single(x => x.TypeId == Id(2)).Ids);
        Assert.Equal(new[] { Id(3) },
            reader.ReceiptProbes.Single(x => x.TypeId == Id(5)).Ids);
        Assert.Equal(0, reader.EntityProbeCalls);
        Assert.Equal(0, handler.WitnessCalls);
    }

    [Fact]
    public async Task UnspecifiedCompletionIdentity_CannotAuthorizeSkip()
    {
        var records = new List<object>
        {
            new CompletedRecord(Id(1), default, Id(3)),
            new CompletedRecord(Id(2), Id(4), default),
        };
        var reader = new ReceiptReader { CacheEntities = true };
        reader.Entities.UnionWith(new[] { Id(1), Id(2) });
        using var builder = new SubstrateChangeBuilder(Id(90), "completion-gate", null);

        var skipped = await IngestExistenceGate.RemovePresentAsync(
            records, new CountingHandler(), reader, builder, CancellationToken.None);

        Assert.Empty(skipped);
        Assert.Equal(2, records.Count);
        Assert.Empty(reader.ReceiptProbes);
    }

    private static Hash128 Id(byte value)
    {
        var bytes = new byte[16];
        bytes[0] = value;
        return Hash128.FromBytes(bytes);
    }

    private sealed record TrunkRecord(Hash128 TrunkRootId) : ITrunkRootRecord;

    private sealed record CompletedRecord(
        Hash128 TrunkRootId,
        Hash128 CompletionAttestationTypeId,
        Hash128 CompletionAttestationId) : ITrunkRootRecord, IIngestCompletionRecord;

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
        internal readonly HashSet<(Hash128 TypeId, Hash128 Id)> Receipts = [];
        internal readonly List<(Hash128 TypeId, Hash128[] Ids)> ReceiptProbes = [];

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

        public Task<IReadOnlySet<Hash128>> PresentAttestationIdsAsync(
            Hash128 typeId, IReadOnlyList<Hash128> ids, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReceiptProbes.Add((typeId, ids.ToArray()));
            return Task.FromResult<IReadOnlySet<Hash128>>(
                ids.Where(id => Receipts.Contains((typeId, id))).ToHashSet());
        }
    }
}
