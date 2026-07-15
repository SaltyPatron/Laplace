using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class FactorWalkTests
{
    [Fact]
    public void Pack_Then_Unpack_RoundTrips_BitExact()
    {
        // hd=32 head factor: full vertices only (32 = 5*6 + 2 → partial tail too).
        var values = new float[32];
        var rng = new Random(7);
        for (int i = 0; i < values.Length; i++)
            values[i] = (float)(rng.NextDouble() * 20.0 - 10.0);
        values[0] = 0f;
        values[1] = -0f;
        values[2] = float.Epsilon;
        values[3] = float.MaxValue;
        values[4] = float.MinValue;
        values[5] = float.NaN;
        values[6] = float.PositiveInfinity;

        byte[] packed = FactorWalk.Pack(values);
        Assert.Equal(FactorWalk.VertexCount(values.Length) * 4 * sizeof(double), packed.Length);

        double[] xyzm = MemoryMarshal.Cast<byte, double>(packed).ToArray();
        float[] back = FactorWalk.Unpack(xyzm);

        Assert.Equal(values.Length, back.Length);
        for (int i = 0; i < values.Length; i++)
            Assert.Equal(BitConverter.SingleToUInt32Bits(values[i]),
                         BitConverter.SingleToUInt32Bits(back[i]));
    }

    [Fact]
    public void Partial_Tail_Vertex_Preserves_Count()
    {
        var values = new float[] { 1.5f, -2.25f, 3.125f };
        double[] xyzm = MemoryMarshal.Cast<byte, double>(FactorWalk.Pack(values)).ToArray();
        Assert.Equal(4, xyzm.Length);

        float[] back = FactorWalk.Unpack(xyzm);
        Assert.Equal(values, back);
    }

    [Fact]
    public void Packed_Doubles_Are_Never_NaN_Or_Inf()
    {
        // The mantissa channel forces biased-zero exponents — WKB/PostGIS safety.
        var values = new float[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1f };
        double[] xyzm = MemoryMarshal.Cast<byte, double>(FactorWalk.Pack(values)).ToArray();
        foreach (double d in xyzm)
        {
            Assert.False(double.IsNaN(d));
            Assert.False(double.IsInfinity(d));
        }
    }

    [Fact]
    public void Testimony_Vertex_Is_Rejected()
    {
        var ids = new[] { new Hash128(0xAAABBBCCCDDDEEEFul, 0x0102030405060708ul) };
        var scores = new long[] { 123456789L };
        byte[] packed = TestimonyWalk.Pack(ids, scores);
        double[] xyzm = MemoryMarshal.Cast<byte, double>(packed).ToArray();
        Assert.Throws<InvalidOperationException>(() => FactorWalk.Unpack(xyzm));
    }
}
