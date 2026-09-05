using Laplace.Decomposers.Model;
using Xunit;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// Checkpoints have no admitted numeric-to-evidence contraction yet. These tests
/// pin that the old top-k salience writer cannot silently return as testimony.
/// </summary>
public sealed class ModelTokenEdgeETLTests
{
    [Fact]
    public void DefaultMode_IsStructureOnly_AndHasNoMaterializedTestimonyWidth()
    {
        using var environment = new PlanesEnvironment(null);
        Assert.Equal("structure", ModelTokenEdgeETL.ResolvePlanesMode());
        Assert.Equal(0, ModelTokenEdgeETL.TestimonyWidthPerCircuit);
    }

    [Fact]
    public void UnsupportedEvidenceMode_IsRejected()
    {
        using var environment = new PlanesEnvironment("factors");
        Assert.Throws<InvalidOperationException>(ModelTokenEdgeETL.ResolvePlanesMode);
    }

    private sealed class PlanesEnvironment : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("LAPLACE_MODEL_PLANES");
        public PlanesEnvironment(string? value) => Environment.SetEnvironmentVariable("LAPLACE_MODEL_PLANES", value);
        public void Dispose() => Environment.SetEnvironmentVariable("LAPLACE_MODEL_PLANES", _old);
    }
}
