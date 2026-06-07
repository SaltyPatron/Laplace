using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed class CalibratedInverse
{
    private readonly Dictionary<long, (double[] Mu, double[] Wom)> _byN = new();
    private readonly NpgsqlDataSource _ds;
    private readonly long _phiFp1e9;

    public CalibratedInverse(NpgsqlDataSource ds, long phiFp1e9)
    {
        _ds = ds;
        _phiFp1e9 = phiFp1e9;
    }

    private (double[] Mu, double[] Wom) Map(long n)
    {
        if (_byN.TryGetValue(n, out var m)) return m;
        const int G = 4001; const double LO = -6.0, HI = 6.0;
        var wom = new double[G]; var sumFp = new long[G];
        for (int i = 0; i < G; i++)
        {
            double w = LO + (HI - LO) * i / (G - 1);
            wom[i] = w;
            double score = 0.5 * (1.0 + Math.Tanh(w));
            sumFp[i] = (long)Math.Round(score * 1e9) * n;
        }
        var mu = new double[G];
        using (var conn = _ds.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT g.i, (laplace.laplace_glicko2_accumulate_games("
                + "1500000000000,350000000000,60000000,1500000000000,$1,$2,s.sum,500000000)).rating "
                + "FROM unnest($3::bigint[]) WITH ORDINALITY AS s(sum,i) "
                + "CROSS JOIN LATERAL (SELECT s.i AS i) g ORDER BY g.i";
            cmd.Parameters.AddWithValue(_phiFp1e9);
            cmd.Parameters.AddWithValue(n);
            cmd.Parameters.AddWithValue(sumFp);
            using var rdr = cmd.ExecuteReader();
            int k = 0;
            while (rdr.Read()) mu[k++] = rdr.GetInt64(1) / 1e9;
        }
        var order = Enumerable.Range(0, G).OrderBy(i => mu[i]).ToArray();
        var muS = new double[G]; var womS = new double[G];
        for (int i = 0; i < G; i++) { muS[i] = mu[order[i]]; womS[i] = wom[order[i]]; }
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
        while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (mu[mid] <= r) lo = mid; else hi = mid; }
        double t = (r - mu[lo]) / (mu[hi] - mu[lo] + 1e-30);
        return wom[lo] + t * (wom[hi] - wom[lo]);
    }
}
