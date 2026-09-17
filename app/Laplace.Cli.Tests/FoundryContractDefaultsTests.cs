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

    [Fact]
    public void OperatorFactorization_DoesNotImplicitlyRetuneTheSpectrum()
    {
        // FoundryExport.Factor reconstructs (s_r / s_0)^alpha. Alpha=1 is the
        // normalized operator selected from substrate state; values below one
        // flatten its spectrum into a different operation before materialization.
        Assert.Equal(1.0, FoundryDefaults.FactorSpectrumAlpha, 12);
    }
}
