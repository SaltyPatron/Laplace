using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Glicko-2 fixed-point state — sequential layout matches C
/// <c>glicko2_state_t</c> in engine/core/include/laplace/core/glicko2.h.
/// All fields at scale 1e9. One source of math truth for
/// Glicko-2 across SQL, C, and C#.
/// </summary>
[StructLayout(LayoutTypeId.Sequential)]
public struct Glicko2State
{
    public long RatingFp1e9;
    public long RdFp1e9;
    public long VolatilityFp1e9;
    public long LastObservedAtUnixNs;
    public long ObservationCount;
}

/// <summary>
/// Glicko-2 single-matchup observation — sequential layout matches C
/// <c>glicko2_observation_t</c>. All fields at scale 1e9.
/// </summary>
[StructLayout(LayoutTypeId.Sequential)]
public struct Glicko2Observation
{
    /// <summary>Opponent's Glicko-1 rating (fp1e9).</summary>
    public long OpponentRatingFp1e9;
    /// <summary>Opponent's Glicko-1 RD (fp1e9).</summary>
    public long OpponentRdFp1e9;
    /// <summary>Matchup score: 0=loss, 5e8=draw, 1e9=win. fp1e9.</summary>
    public long ScoreFp1e9;
}

/// <summary>
/// Glicko-2 constants and primitives — C engine is the single source
/// of math truth (per CLAUDE.md "one source of math truth"). C# does
/// not reimplement <c>effective_mu</c> or any other Glicko-2 formula.
/// </summary>
public static unsafe class Glicko2
{
    /// <summary>Fixed-point scale (1e9). The encoding constant
    /// shared by C, SQL (laplace_substrate Glicko-2 aggregate), and C#.</summary>
    public const long FpScale = 1_000_000_000L;

    /// <summary>Default initial Glicko-1 mu (1500) at scale 1e9.</summary>
    public const long DefaultRatingFp1e9 = 1_500L * FpScale;

    /// <summary>Default initial Glicko-1 RD (350) at scale 1e9.</summary>
    public const long DefaultRdFp1e9 = 350L * FpScale;

    /// <summary>Default initial Glicko-2 volatility (0.06) at scale 1e9.</summary>
    public const long DefaultVolatilityFp1e9 = 60_000_000L;

    /// <summary>Glicko-2 system constant tau at scale 1e9 (matches
    /// <c>LAPLACE_GLICKO2_DEFAULT_TAU</c>).</summary>
    public const long DefaultTauFp1e9 = 500_000_000L;

    /// <summary>Glicko-2 score values at scale 1e9: 0 = loss, 5e8 = draw, 1e9 = win.</summary>
    public const long ScoreLoss = 0L;
    public const long ScoreDraw = 500_000_000L;
    public const long ScoreWin  = 1_000_000_000L;

    /// <summary>
    /// Effective mu for cascade scoring: <c>rating − 2·RD</c> at scale 1e9
    /// (the ~95% lower bound). Delegates to the C engine
    /// primitive — do NOT inline <c>rating - 2*rd</c> in C# call sites.
    /// </summary>
    public static long EffectiveMuFp1e9(long ratingFp1e9, long rdFp1e9)
    {
        var st = new Glicko2State
        {
            RatingFp1e9 = ratingFp1e9,
            RdFp1e9     = rdFp1e9,
        };
        return NativeInterop.Glicko2EffectiveMu(&st);
    }

    /// <summary>
    /// Apply a rating period of N matchup observations to <paramref name="state"/>.
    /// Delegates to the C engine <c>glicko2_update_period</c> — single source of
    /// math truth., the substrate aggregates per-instance evidence
    /// through proper Glicko-2 update math (not raw averaging).
    /// </summary>
    public static void UpdatePeriod(
        ref Glicko2State state,
        ReadOnlySpan<Glicko2Observation> observations,
        long tauFp1e9,
        long nowUnixNs)
    {
        if (observations.IsEmpty) return;
        fixed (Glicko2State* statePtr = &state)
        fixed (Glicko2Observation* obsPtr = observations)
        {
            NativeInterop.Glicko2UpdatePeriod(statePtr, obsPtr,
                (nuint)observations.Length, tauFp1e9, nowUnixNs);
        }
    }
}
