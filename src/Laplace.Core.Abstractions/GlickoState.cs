namespace Laplace.Core.Abstractions;

/// <summary>
/// Glicko-2 rating state per rated entity (source / entity / edge — the three
/// rated layers of the substrate). Uses Glickman 2013 display scale: μ around
/// 1500, σ as rating deviation, plus volatility φ. Use <c>IGlicko2.Apply</c>
/// to update; do not mutate this struct directly.
/// </summary>
public readonly record struct GlickoState(
    double Mu,
    double SigmaDisp,
    double Volatility,
    int Games)
{
    public static GlickoState Default { get; } = new(1500.0, 350.0, 0.06, 0);
}
