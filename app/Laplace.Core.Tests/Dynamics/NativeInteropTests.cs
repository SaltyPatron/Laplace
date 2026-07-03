using Xunit;
using Laplace.Engine.Dynamics;

namespace Laplace.Engine.Dynamics.Tests;

public class NativeInteropTests
{
    [Fact]
    public void LaplaceDynamicsVersion_ReturnsExpected()
    {
        var version = NativeInterop.LaplaceDynamicsVersion();
        Assert.Equal("0.1.0", version);
    }

    [Fact]
    public void LaplaceDynamicsInit_Idempotent()
    {
        var rc = NativeInterop.LaplaceDynamicsInit();
        Assert.Equal(0, rc);
    }
}
