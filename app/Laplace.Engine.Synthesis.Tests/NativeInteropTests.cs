using Xunit;
using Laplace.Engine.Synthesis;

namespace Laplace.Engine.Synthesis.Tests;

/// <summary>
/// P/Invoke smoke tests for liblaplace_synthesis. Real coverage (recipe
/// parsing, native package emission, GGUF proof export, sparse-by-construction emission) lands
/// Chunks 7-8.
/// </summary>
public class NativeInteropTests
{
    [Fact]
    public void LaplaceSynthesisVersion_ReturnsExpected()
    {
        var version = NativeInterop.LaplaceSynthesisVersion();
        Assert.Equal("0.1.0", version);
    }
}
