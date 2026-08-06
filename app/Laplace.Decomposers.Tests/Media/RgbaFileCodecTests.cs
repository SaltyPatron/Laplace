using Laplace.Decomposers.Media;
using Xunit;

namespace Laplace.Decomposers.Tests.Media;

public sealed class RgbaFileCodecTests
{
    [Fact]
    public void RoundTrip_OnePixel()
    {
        byte[] px = [0x11, 0x22, 0x33, 0xFF];
        byte[] file = RgbaFileCodec.Encode(1, 1, px);
        Assert.True(RgbaFileCodec.TryDecode(file, out uint w, out uint h, out byte[] rgba));
        Assert.Equal(1u, w);
        Assert.Equal(1u, h);
        Assert.Equal(px, rgba);
    }
}
