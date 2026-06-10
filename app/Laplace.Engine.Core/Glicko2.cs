using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Sequential)]
public struct Glicko2State
{
    public long RatingFp1e9;
    public long RdFp1e9;
    public long VolatilityFp1e9;
    public long LastObservedAtUnixNs;
    public long ObservationCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct Glicko2Observation
{
    public long OpponentRatingFp1e9;
    public long OpponentRdFp1e9;
    public long ScoreFp1e9;
}

public static unsafe class Glicko2
{
    public const long FpScale = 1_000_000_000L;

    public const long DefaultRatingFp1e9 = 1_500L * FpScale;

    public const long DefaultRdFp1e9 = 350L * FpScale;

    public const long DefaultVolatilityFp1e9 = 60_000_000L;

    public const long DefaultTauFp1e9 = 500_000_000L;

    public const long ScoreLoss = 0L;
    public const long ScoreDraw = 500_000_000L;
    public const long ScoreWin  = 1_000_000_000L;

    public static long EffectiveMuFp1e9(long ratingFp1e9, long rdFp1e9)
    {
        var st = new Glicko2State
        {
            RatingFp1e9 = ratingFp1e9,
            RdFp1e9     = rdFp1e9,
        };
        return NativeInterop.Glicko2EffectiveMu(&st);
    }

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

    /// <summary>Bit-identical to PG <c>laplace_glicko2_accumulate_games</c>.</summary>
    public static Glicko2State AccumulateGames(
        long priorRatingFp1e9,
        long priorRdFp1e9,
        long priorVolatilityFp1e9,
        long opponentRatingFp1e9,
        long opponentRdFp1e9,
        long games,
        long sumScoreFp,
        long tauFp1e9 = DefaultTauFp1e9)
    {
        if (games <= 0) throw new ArgumentOutOfRangeException(nameof(games));
        var state = new Glicko2State
        {
            RatingFp1e9     = priorRatingFp1e9,
            RdFp1e9         = priorRdFp1e9,
            VolatilityFp1e9 = priorVolatilityFp1e9,
        };
        long q = sumScoreFp / games;
        long rem = sumScoreFp - q * (games - 1);
        Span<Glicko2Observation> obs = games <= 4096
            ? stackalloc Glicko2Observation[(int)games]
            : new Glicko2Observation[games];
        for (long i = 0; i < games - 1; i++)
        {
            obs[(int)i] = new Glicko2Observation
            {
                OpponentRatingFp1e9 = opponentRatingFp1e9,
                OpponentRdFp1e9     = opponentRdFp1e9,
                ScoreFp1e9          = q,
            };
        }
        obs[(int)(games - 1)] = new Glicko2Observation
        {
            OpponentRatingFp1e9 = opponentRatingFp1e9,
            OpponentRdFp1e9     = opponentRdFp1e9,
            ScoreFp1e9          = rem,
        };
        UpdatePeriod(ref state, obs, tauFp1e9, 0);
        return state;
    }
}
