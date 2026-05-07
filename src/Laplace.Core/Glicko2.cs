namespace Laplace.Core;

using System;
using System.Buffers;

using Laplace.Core.Abstractions;
using Laplace.Core.Native;

/// <summary>
/// Managed wrapper over the native Glicko-2 kernel. Translates between the
/// substrate's display-scale <see cref="GlickoState"/> (rating ~1500, RD,
/// volatility) and the native internal scale (mu = (rating-1500)/173.7178,
/// phi = RD/173.7178). Phase 2 / Track D / D2.
/// </summary>
public sealed class Glicko2 : IGlicko2
{
    public GlickoState Apply(
        GlickoState self,
        ReadOnlySpan<GlickoState> opponents,
        ReadOnlySpan<double> outcomes,
        double tau = 0.5)
    {
        if (opponents.Length != outcomes.Length)
        {
            throw new ArgumentException("opponents and outcomes must be the same length.");
        }
        var n = opponents.Length;
        if (n == 0)
        {
            return PeriodDecay(self);
        }

        var input = ToNative(self);

        var pool = ArrayPool<NativeGlicko2.Observation>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; ++i)
            {
                pool[i] = new NativeGlicko2.Observation
                {
                    OpponentMu  = NativeGlicko2.FromRating(opponents[i].Mu),
                    OpponentPhi = NativeGlicko2.FromRatingDev(opponents[i].SigmaDisp),
                    Score       = outcomes[i],
                    Weight      = 1.0,
                };
            }
            NativeGlicko2.State output;
            unsafe
            {
                fixed (NativeGlicko2.Observation* op = pool)
                {
                    NativeGlicko2.Apply(in input, op, (nuint)n, tau, out output);
                }
            }
            return ToManaged(output, self.Games + n);
        }
        finally
        {
            ArrayPool<NativeGlicko2.Observation>.Shared.Return(pool);
        }
    }

    public GlickoState PeriodDecay(GlickoState self)
    {
        var input = ToNative(self);
        NativeGlicko2.PeriodDecay(in input, out var output);
        return ToManaged(output, self.Games);
    }

    private static NativeGlicko2.State ToNative(GlickoState s) => new()
    {
        Mu    = NativeGlicko2.FromRating(s.Mu),
        Phi   = NativeGlicko2.FromRatingDev(s.SigmaDisp),
        Sigma = s.Volatility,
        Games = s.Games,
    };

    private static GlickoState ToManaged(NativeGlicko2.State s, int games) => new(
        Mu:         NativeGlicko2.ToRating(s.Mu),
        SigmaDisp:  NativeGlicko2.ToRatingDev(s.Phi),
        Volatility: s.Sigma,
        Games:      games);
}
