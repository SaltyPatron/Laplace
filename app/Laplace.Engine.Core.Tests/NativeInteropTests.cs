using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

public class NativeInteropTests
{
    [Fact]
    public void LaplaceCoreVersion_ReturnsExpected()
    {
        var version = NativeInterop.LaplaceCoreVersion();
        Assert.Equal("0.1.0", version);
    }
}
