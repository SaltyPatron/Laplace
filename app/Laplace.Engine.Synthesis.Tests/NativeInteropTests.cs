using Xunit;
using Laplace.Engine.Synthesis;

namespace Laplace.Engine.Synthesis.Tests;

public class NativeInteropTests
{
    [Fact]
    public void LaplaceSynthesisVersion_ReturnsExpected()
    {
        var version = NativeInterop.LaplaceSynthesisVersion();
        Assert.Equal("0.1.0", version);
    }
}
