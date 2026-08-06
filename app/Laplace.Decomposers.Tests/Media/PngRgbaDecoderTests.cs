using Laplace.Decomposers.Media;
using Xunit;

namespace Laplace.Decomposers.Tests.Media;

public sealed class PngRgbaDecoderTests
{
    [Fact]
    public void Decodes_VaultColors5x5()
    {
        string path = "/vault/Data/test-data/images/colors_5x5.png";
        if (!File.Exists(path)) return; // skip silently off-host
        Assert.True(PngRgbaDecoder.TryDecode(File.ReadAllBytes(path), out uint w, out uint h, out byte[] rgba));
        Assert.Equal(5u, w);
        Assert.Equal(5u, h);
        Assert.Equal(5 * 5 * 4, rgba.Length);
        // Opaque expansion: every alpha byte is 0xFF for RGB source.
        for (int i = 3; i < rgba.Length; i += 4)
            Assert.Equal(0xFF, rgba[i]);
    }

    [Fact]
    public void Decodes_VaultGradient10x10()
    {
        string path = "/vault/Data/test-data/images/gradient_10x10.png";
        if (!File.Exists(path)) return;
        Assert.True(PngRgbaDecoder.TryDecode(File.ReadAllBytes(path), out uint w, out uint h, out _));
        Assert.Equal(10u, w);
        Assert.Equal(10u, h);
    }
}
