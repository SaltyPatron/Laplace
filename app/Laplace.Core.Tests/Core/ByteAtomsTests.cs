using Xunit;

namespace Laplace.Engine.Core.Tests;

public class ByteAtomsTests
{
    [Fact]
    public void AsciiByte_IsItsCodepointEntity()
    {
        Assert.Equal(Hash128.Blake3(new byte[] { 0x41 }), ByteAtoms.Id(0x41));
        Assert.Equal(Hash128.Blake3(System.Text.Encoding.UTF8.GetBytes("A")), ByteAtoms.Id(0x41));
    }

    [Fact]
    public void HighBytes_DeterministicUnitGlomePlacements()
    {
        var seen = new HashSet<(double, double, double, double)>();
        for (int b = ByteAtoms.First; b <= 0xFF; b++)
        {
            var c = ByteAtoms.Coord((byte)b);
            double r2 = c[0] * c[0] + c[1] * c[1] + c[2] * c[2] + c[3] * c[3];
            Assert.InRange(Math.Sqrt(r2), 1.0 - 1e-12, 1.0 + 1e-12);
            Assert.True(seen.Add((c[0], c[1], c[2], c[3])), "placements distinct");
        }
        Assert.True(ByteAtoms.Coord(0xC2).SequenceEqual(ByteAtoms.Coord(0xC2)));
    }

    [Fact]
    public void Utf8Roles_FollowRfc3629()
    {
        Assert.Equal("continuation", ByteAtoms.Utf8Role(0x80));
        Assert.Equal("continuation", ByteAtoms.Utf8Role(0xBF));
        Assert.Equal("invalid", ByteAtoms.Utf8Role(0xC0));
        Assert.Equal("invalid", ByteAtoms.Utf8Role(0xC1));
        Assert.Equal("lead2", ByteAtoms.Utf8Role(0xC2));
        Assert.Equal("lead3", ByteAtoms.Utf8Role(0xE0));
        Assert.Equal("lead4", ByteAtoms.Utf8Role(0xF4));
        Assert.Equal("invalid", ByteAtoms.Utf8Role(0xF5));
        Assert.Equal("invalid", ByteAtoms.Utf8Role(0xFF));
    }

    [Fact]
    public void Cp1252_EuroAtEightyNotLatin1()
    {
        Assert.Equal(0x20AC, ByteAtoms.Cp1252High[0]);
        Assert.Equal(0, ByteAtoms.Cp1252High[0x81 - 0x80]);
    }
}
