using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

public class Hilbert128Tests
{
    [Fact]
    public void Encode_ProducesNonZeroForNonOriginCoord()
    {
        var h = Hilbert128.Encode(stackalloc double[] { 0.5, 0.5, 0.5, 0.5 });
        bool anyNonZero = false;
        unsafe { for (int i = 0; i < 16; i++) if (h.Bytes[i] != 0) { anyNonZero = true; break; } }
        Assert.True(anyNonZero);
    }

    [Fact]
    public void Encode_RejectsShortSpan()
    {
        Assert.Throws<ArgumentException>(() => Hilbert128.Encode(stackalloc double[3]));
    }

    [Fact]
    public void Encode_IsDeterministic()
    {
        var a = Hilbert128.Encode(stackalloc double[] { 0.1, 0.2, 0.3, 0.4 });
        var b = Hilbert128.Encode(stackalloc double[] { 0.1, 0.2, 0.3, 0.4 });
        Assert.Equal(0, a.CompareToBytewise(b));
    }

    [Fact]
    public void Encode_DifferentCoordsProduceDifferentEncoding()
    {
        var a = Hilbert128.Encode(stackalloc double[] { 0.1, 0.2, 0.3, 0.4 });
        var b = Hilbert128.Encode(stackalloc double[] { 0.5, 0.6, 0.7, 0.8 });
        Assert.NotEqual(0, a.CompareToBytewise(b));
    }

    [Fact]
    public void StructLayoutIsExactly16Bytes()
    {
        unsafe { Assert.Equal(16, sizeof(Hilbert128)); }
    }
}
