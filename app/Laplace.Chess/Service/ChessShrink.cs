using Laplace.Modality;

namespace Laplace.Chess.Service;

/// <summary>
/// Shrinks a consensus rating toward the neutral prior by witness count:
/// neutral + (eff_mu − neutral) · w / (w + K0). Used wherever chess ranking reads standing
/// (SubstrateTurnHost move scoring, SubstrateRootBias). K0 defaults to 15,000 and is
/// overridden by LAPLACE_CHESS_SHRINK_K0; a large K0 flattens standings folded from few witnesses.
/// </summary>
public static class ChessShrink
{
    public const double DefaultK0 = 15_000d;

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
}
