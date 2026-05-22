using Xunit;
using Laplace.Engine.Dynamics;

namespace Laplace.Engine.Dynamics.Tests;

/// <summary>
/// P/Invoke smoke tests for liblaplace_dynamics. The static constructor
/// of Laplace.Engine.Dynamics.NativeInterop runs laplace_dynamics_init()
/// which locks MKL threading + CBWR (per ADR 0030); reaching these tests
/// means init succeeded. Real coverage (Procrustes round-trip, eigenmaps
/// convergence, cross-thread-count determinism) lands Chunk 6 + Epic D.
/// </summary>
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
        // Static ctor already called this; explicit second call must also succeed.
        var rc = NativeInterop.LaplaceDynamicsInit();
        Assert.Equal(0, rc);
    }
}
