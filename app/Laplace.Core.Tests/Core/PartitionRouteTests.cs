using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class PartitionRouteTests
{
    // Entity ids and the remainder of the entities leaf PostgreSQL stored each in
    // (modulus 64), with hashbyteaextended(id, HASH_PARTITION_SEED) for each.
    public static TheoryData<string, long, int> Placements => new()
    {
        { "003cc3a405636c9f2c245cce67f7b354", 2535364988515514516, 55 },
        { "00dd4d7fb510f99a6719b0aeca1a9bc7", 3593260662944503793, 20 },
        { "018c36f0e1bb04f467409d985d9dcf41", 4697266198515808151, 58 },
        { "01c1537a634ba4ae4237b48a878305a4", 7063613722366470668, 47 },
        { "022275857bf047fcb4cf5906ecff9715", -1485905939525376673, 2 },
        { "022c20c974867831d6624ec0121550fa", 6892059734729797528, 59 },
    };

    private const ulong PartitionSeed = 0x7A5B22367996DCFD;

    [Theory]
    [MemberData(nameof(Placements))]
    public void Remainder_IsThePostgresLeafOfTheKey(string hex, long hash, int remainder)
    {
        byte[] key = Convert.FromHexString(hex);
        Assert.Equal(hash, unchecked((long)PartitionRoute.HashBytesExtended(key, PartitionSeed)));
        Span<int> remainders = stackalloc int[1];
        PartitionRoute.Remainders([Hash128.FromBytes(key)], 64, remainders);
        Assert.Equal(remainder, remainders[0]);
    }

    [Fact]
    public void HashBytesExtended_MatchesPostgresForEveryTailLength()
    {
        var seventeen = Enumerable.Range(0, 17).Select(static b => (byte)b).ToArray();
        Assert.Equal(-5700645584453517373, unchecked((long)PartitionRoute.HashBytesExtended([], PartitionSeed)));
        Assert.Equal(3591986179850072241, unchecked((long)PartitionRoute.HashBytesExtended([0x61], 0)));
        Assert.Equal(5421338085723872396, unchecked((long)PartitionRoute.HashBytesExtended(seventeen, PartitionSeed)));
    }
}
