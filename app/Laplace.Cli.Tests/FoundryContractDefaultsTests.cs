using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Cli.Tests;

/// <summary>
/// Product-contract guards for foundry defaults whose values directly change the
/// constructed model rather than merely selecting execution/performance policy.
/// </summary>
public sealed class FoundryContractDefaultsTests
{
    [Fact]
    public void TrajectoryEvidence_IsNotImplicitlyPpmiReweighted()
    {
        Assert.False(FoundryDefaults.Ppmi);
    }
}
