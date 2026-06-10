using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed class CalibratedInverse
{
    private readonly Dictionary<long, (double[] Mu, double[] Wom)> _byN = new();
    private readonly long _phiFp1e9;

    public CalibratedInverse(long phiFp1e9) => _phiFp1e9 = phiFp1e9;

    public CalibratedInverse(global::Npgsql.NpgsqlDataSource ds, long phiFp1e9) : this(phiFp1e9)
    {
        _ = ds;
    }

    private (double[] Mu, double[] Wom) Map(long n)
    {
        if (_byN.TryGetValue(n, out var m)) return m;
        const int G = 4001;
        var wom = new double[G];
        var mu = new double[G];
        for (int i = 0; i < G; i++)
        {
            long scoreFp = 1 + (long)((ScoreLaw.FpScale - 2) * (double)i / (G - 1));
            wom[i] = ScoreLaw.InverseFp(scoreFp, 1.0);
            long sumFp = scoreFp * n;
            var st = Glicko2.AccumulateGames(
                Glicko2.DefaultRatingFp1e9,
                Glicko2.DefaultRdFp1e9,
                Glicko2.DefaultVolatilityFp1e9,
                Glicko2.DefaultRatingFp1e9,
                _phiFp1e9,
                n,
                sumFp);
            mu[i] = st.RatingFp1e9 / 1e9;
        }
        var order = Enumerable.Range(0, G).OrderBy(i => mu[i]).ToArray();
        var muS = new double[G];
        var womS = new double[G];
        for (int i = 0; i < G; i++)
        {
            muS[i] = mu[order[i]];
            womS[i] = wom[order[i]];
        }
        var pair = (muS, womS);
        _byN[n] = pair;
        return pair;
    }

    public double Wom(long ratingFp1e9, long n)
    {
        var (mu, wom) = Map(n <= 0 ? 1 : n);
        double r = ratingFp1e9 / 1e9;
        int lo = 0, hi = mu.Length - 1;
        if (r <= mu[0]) return wom[0];
        if (r >= mu[hi]) return wom[hi];
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (mu[mid] <= r) lo = mid;
            else hi = mid;
        }
        double t = (r - mu[lo]) / (mu[hi] - mu[lo] + 1e-30);
        return wom[lo] + t * (wom[hi] - wom[lo]);
    }
}
