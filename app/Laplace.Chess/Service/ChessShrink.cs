using Laplace.Modality;

namespace Laplace.Chess.Service;

/// <summary>
/// Witness-count shrinkage toward the neutral prior, shared by every consensus-reading chess
/// ranker (SubstrateTurnHost move scoring, SubstrateRootBias). K0 = 15k is calibrated for
/// corpus scale; at sub-corpus scale it crushes nearly all signal into a ~0.4-point eff_mu
/// spread (doc 04), so hosts running against small folds can dial it via LAPLACE_CHESS_SHRINK_K0.
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
