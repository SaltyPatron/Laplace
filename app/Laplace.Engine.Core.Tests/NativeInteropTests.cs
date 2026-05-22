using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// P/Invoke smoke tests for liblaplace_core. Verifies the .so loads,
/// the calling convention matches, and the C ABI surface is callable
/// from .NET. Real coverage (math4d round-trip, hash128 known-answer,
/// hilbert locality, etc.) lands per-Chunk alongside the engine
/// implementations.
/// </summary>
public class NativeInteropTests
{
    [Fact]
    public void LaplaceCoreVersion_ReturnsExpected()
    {
        var version = NativeInterop.LaplaceCoreVersion();
        Assert.Equal("0.1.0", version);
    }
}
