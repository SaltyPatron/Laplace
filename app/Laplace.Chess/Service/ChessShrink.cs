using Laplace.Modality;

namespace Laplace.Chess.Service;

/// <summary>
/// Shrinks a consensus rating toward the neutral prior by witness count:
/// neutral + (eff_mu − neutral) · w / (w + K0). Turn scoring and learned
/// piece-square tables use <see cref="DefaultK0"/> (15,000, or
/// LAPLACE_CHESS_SHRINK_K0). An exact board transition uses
/// <see cref="ExactTransitionK0"/> so a short witnessed line keeps a finite edge.
/// </summary>
public static class ChessShrink
{
    public const double DefaultK0 = 15_000d;

    /// <summary>Prior strength of one exact (position, move, next position) transition.</summary>
    public const double ExactTransitionK0 = 5d;

    /// <summary>
    /// A context-free move average may break ties. It is not a second evaluation
    /// of a position that already has an exact transition.
    /// </summary>
    public const int ContextFreeTiebreakCp = 3;

    public static readonly double K0 = Resolve();

    private static double Resolve() =>
        double.TryParse(
            Environment.GetEnvironmentVariable("LAPLACE_CHESS_SHRINK_K0"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var v) && v >= 0
            ? v : DefaultK0;

    public static double Apply(double effMu, double witness, double? k0 = null)
    {
        double k = k0 ?? K0;
        return GlickoPriors.NeutralMu + (effMu - GlickoPriors.NeutralMu) * (witness / (witness + k));
    }

    /// <summary>
    /// Fisher information of a consensus deviation relative to a fresh rating.
    /// A missing, non-positive, or no-tighter-than-fresh deviation contributes
    /// scale 1, so the witness count alone meets the prior. A tighter deviation,
    /// the posterior a stronger opponent leaves, contributes (σ_ref / σ)².
    /// </summary>
    public static double FisherScale(double rd)
    {
        if (!(rd > 0d) || rd >= GlickoPriors.InitialRd) return 1d;
        double ratio = GlickoPriors.InitialRd / rd;
        return ratio * ratio;
    }

    /// <summary>
    /// Observations that may shrink an exact transition. Result witnesses carry
    /// the recorded-outcome mean. Calculated-evaluation witnesses, such as a
    /// Stockfish <c>HAS_EVAL</c> count, add precision only.
    /// </summary>
    public static double ExactEffectiveWitnesses(
        double resultWitnesses, double resultRd, double calculatedWitnesses, double calculatedRd)
        => Math.Max(0d, resultWitnesses) * FisherScale(resultRd)
           + Math.Max(0d, calculatedWitnesses) * FisherScale(calculatedRd);

    /// <summary>
    /// Shrink one exact transition. A null prior uses strength 5: ten witnesses
    /// at unit precision keep two thirds of the empirical edge. A prior of 0
    /// keeps that edge, which tests use to inspect centipawn conversion.
    /// The calculated evaluation's centipawn mean is not an argument.
    /// </summary>
    public static double ApplyExact(
        double effMu, double resultWitnesses, double resultRd,
        double calculatedWitnesses, double calculatedRd, double? priorK0 = null)
    {
        double k = priorK0 ?? ExactTransitionK0;
        if (k <= 0d) return effMu;
        double n = ExactEffectiveWitnesses(resultWitnesses, resultRd, calculatedWitnesses, calculatedRd);
        return GlickoPriors.NeutralMu + (effMu - GlickoPriors.NeutralMu) * (n / (n + k));
    }
}
