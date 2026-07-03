using System.Text;
using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

public class Hash128Tests
{
    [Fact]
    public void Zero_IsAllZeroBytes()
    {
        var z = Hash128.Zero;
        Assert.Equal(0ul, z.Hi);
        Assert.Equal(0ul, z.Lo);
        var bytes = z.ToBytes();
        foreach (var b in bytes) Assert.Equal(0, b);
    }

    [Fact]
    public void Blake3_OfEmptyMatchesKnownVector()
    {
        var h = Hash128.Blake3(ReadOnlySpan<byte>.Empty);
        byte[] expected =
        {
            0xaf, 0x13, 0x49, 0xb9, 0xf5, 0xf9, 0xa1, 0xa6,
            0xa0, 0x40, 0x4d, 0xea, 0x36, 0xdc, 0xc9, 0x49,
        };
        Assert.Equal(expected, h.ToBytes());
    }

    [Fact]
    public void Blake3_SameInputProducesSameHash()
    {
        var input = "the quick brown fox jumps over the lazy dog"u8;
        var a = Hash128.Blake3(input);
        var b = Hash128.Blake3(input);
        Assert.Equal(a, b);
        Assert.True(a.EqualsBytewise(b));
        Assert.Equal(0, a.CompareToBytewise(b));
    }

    [Fact]
    public void Blake3_DifferentInputsDiffer()
    {
        var a = Hash128.Blake3("alpha"u8);
        var b = Hash128.Blake3("beta"u8);
        Assert.NotEqual(a, b);
        Assert.False(a.EqualsBytewise(b));
        Assert.NotEqual(0, a.CompareToBytewise(b));
    }

    [Fact]
    public void OfCanonical_MatchesBlake3OfUtf8Bytes()
    {
        const string canonical = "substrate/source/UnicodeDecomposer/v1";
        var fromString = Hash128.OfCanonical(canonical);
        var fromBytes = Hash128.Blake3(Encoding.UTF8.GetBytes(canonical));
        Assert.Equal(fromBytes, fromString);
    }

    [Fact]
    public void Merkle_OrderMatters()
    {
        var a = Hash128.Blake3("alpha"u8);
        var b = Hash128.Blake3("beta"u8);
        var ab = Hash128.Merkle(1, new[] { a, b });
        var ba = Hash128.Merkle(1, new[] { b, a });
        Assert.NotEqual(ab, ba);
    }

    [Fact]
    public void Merkle_TierIsNotPartOfIdentity()
    {
        // CONTENT-ADDRESSING LAW: same content = same hash. The id is a
        // function of the child-id sequence only. word_id('a') == grapheme
        // 'a' == codepoint 'a' is by design, not a collision: rows for the
        // same content at different tiers are distinguished by the
        // (id, tier) compound key at the schema level, never by the id.
        var a = Hash128.Blake3("x"u8);
        var t1 = Hash128.Merkle(1, new[] { a });
        var t2 = Hash128.Merkle(2, new[] { a });
        Assert.Equal(t1, t2);
    }

    [Fact]
    public void ToBytes_RoundTripsThroughWriteBytes()
    {
        var h = Hash128.Blake3("round-trip"u8);
        var arr = h.ToBytes();
        Span<byte> span = stackalloc byte[16];
        h.WriteBytes(span);
        for (int i = 0; i < 16; i++) Assert.Equal(arr[i], span[i]);
    }

    [Fact]
    public void StructLayoutIsExactly16Bytes()
    {
        unsafe { Assert.Equal(16, sizeof(Hash128)); }
    }
}
