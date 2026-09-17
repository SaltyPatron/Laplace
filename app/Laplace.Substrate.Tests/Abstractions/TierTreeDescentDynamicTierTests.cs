using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class TierTreeDescentDynamicTierTests
{
    [Fact]
    public async Task DescentVisitsEveryOccupiedTierWithoutFixedCeiling()
    {
        using var tree = TierTree.New(8);
        uint child = tree.AddLeaf(0, 65, 0, 1);
        tree.SetId(child, Id(0));
        for (byte tier = 1; tier <= 7; tier++)
        {
            child = tree.AddNode(tier, child, 1, 0, 1);
            tree.SetId(child, Id(tier));
        }
        tree.FinalizeParents();

        var reader = new TierRecordingReader();
        var result = await TierTreeDescent.ProbeBatchEmitBitmapsAsync([tree], reader);

        Assert.NotNull(Assert.Single(result));
        Assert.Equal(new short[] { 7, 6, 5, 4, 3, 2, 1, 0 }, reader.Tiers);
        Assert.All(reader.CandidateCounts, count => Assert.Equal(1, count));
    }

    private static Hash128 Id(byte tier)
    {
        var bytes = new byte[16];
        bytes[0] = 0xA5;
        bytes[15] = tier;
        return Hash128.FromBytes(bytes);
    }

    private sealed class TierRecordingReader : ISubstrateReader
    {
        internal List<short> Tiers { get; } = [];
        internal List<int> CandidateCounts { get; } = [];

        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> HasSourceCompletedAsync(
            Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task<byte[]> EntitiesExistBitmapAsync(
            IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
            => Task.FromResult(new byte[BitmapBits.ByteLength(candidates.Count)]);

        public Task<byte[]> TierBatchExistenceProbeAsync(
            IReadOnlyList<Hash128> ids, short tier, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Tiers.Add(tier);
            CandidateCounts.Add(ids.Count);
            return Task.FromResult(new byte[BitmapBits.ByteLength(ids.Count)]);
        }
    }
}
