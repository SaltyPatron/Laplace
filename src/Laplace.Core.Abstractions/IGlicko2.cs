namespace Laplace.Core.Abstractions;

using System;

/// <summary>
/// Glicko-2 single-rating-period update. Three-layer (source / entity / edge)
/// rated-source attestation is composed in <c>Laplace.Pipeline</c>'s
/// <c>ISignificance</c>; this interface exposes only the paper-faithful kernel
/// (Glickman 2013 "Example of the Glicko-2 system"). Native implementation is
/// the drop-in port of the verified <c>glicko2_core.c</c> from
/// <c>Hartonomous-002</c>.
///
/// IMPORTANT: this is rated-source attestation (trusted source observed X →
/// weighted win for X scaled by source rating), NOT competitive negative
/// sampling. Absence of observation = high RD (uncertainty), not low rating.
/// </summary>
public interface IGlicko2
{
    /// <summary>
    /// Apply one rating period of observations. Each opponent appears with a
    /// score in [0, 1] (1 = self won, 0 = opponent won, 0.5 = draw).
    /// </summary>
    GlickoState Apply(
        GlickoState self,
        ReadOnlySpan<GlickoState> opponents,
        ReadOnlySpan<double> outcomes,
        double tau = 0.5);

    /// <summary>
    /// Pre-rating-period RD inflation when the rated entity had no observations
    /// in the period. Per Step 6 of Glickman 2013: φ⋆ = √(φ² + σ²); μ and σ
    /// unchanged.
    /// </summary>
    GlickoState PeriodDecay(GlickoState self);
}
