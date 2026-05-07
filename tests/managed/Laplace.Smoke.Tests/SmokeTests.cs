namespace Laplace.Smoke.Tests;

using System;
using Laplace.Core.Abstractions;
using Xunit;

/// <summary>
/// Phase 1 / Track A — sample managed test verifying the abstractions assembly
/// loads and the canonical record types behave deterministically. Real per-
/// service tests land in Phase 2 (Track B/D) as services come online.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void AtomId_FromSpan_RoundTrips()
    {
        var bytes = new byte[AtomId.SizeBytes];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        var atom = AtomId.FromSpan(bytes);

        Assert.Equal(bytes.Length, atom.AsSpan().Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            Assert.Equal(bytes[i], atom.AsSpan()[i]);
        }
    }

    [Fact]
    public void AtomId_FromSpan_RejectsWrongSize()
    {
        Assert.Throws<ArgumentException>(() => AtomId.FromSpan(new byte[AtomId.SizeBytes - 1]));
        Assert.Throws<ArgumentException>(() => AtomId.FromSpan(new byte[AtomId.SizeBytes + 1]));
    }

    [Fact]
    public void Point4D_NormalizedUnitVector_HasUnitNorm()
    {
        var p = new Point4D(1.0, 1.0, 1.0, 1.0).Normalized();

        Assert.Equal(1.0, p.Norm, precision: 12);
    }

    [Fact]
    public void GlickoState_DefaultMatchesGlickman2013Initialization()
    {
        var s = GlickoState.Default;

        Assert.Equal(1500.0, s.Mu);
        Assert.Equal(350.0, s.SigmaDisp);
        Assert.Equal(0.06, s.Volatility);
        Assert.Equal(0, s.Games);
    }
}
