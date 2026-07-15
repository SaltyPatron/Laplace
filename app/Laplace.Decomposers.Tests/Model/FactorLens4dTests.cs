using Laplace.Decomposers.Model;
using Xunit;
using Xunit.Abstractions;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model.Tests;

// Lens-quality measurement for the 4D observation geometry (campaign doc 26, B').
// Storage law: fireflies (factor records) are stored NATIVE-DIM and exact; the
// S3/low-dim projection is a calculated LENS whose only job is nomination —
// neighborhoods, cells, routing. This test MEASURES the lens: how much of the
// native-dim ranking structure survives at k=4 (and where it saturates), with
// every inner product computed by the native kernels. It does NOT gate
// exactness — exactness never leaves native dim by design.
public sealed class FactorLens4dTests
{
    private const string Snap =
        @"D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed1d9f207416be6d2e6f8de32d1f16199bf";

    private const int N = 2000;       // token rows sampled from the embedding
    private const int RowOffset = 2000;
    private const int Probes = 200;   // probe rows for rank/softmax metrics
    private const int TopK = 20;
    private const int KMax = 32;
    private static readonly int[] Ks = { 2, 4, 8, 16, 32 };

    private readonly ITestOutputHelper _out;
    public FactorLens4dTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Lens_Nomination_Quality_MiniLM()
    {
        if (!File.Exists(Path.Combine(Snap, "model.safetensors")))
        {
            _out.WriteLine("MiniLM snapshot not present - measurement skipped.");
            return;
        }

        var refs = SafetensorsContainerParser.ParseModel(Snap);
        var refMap = new Dictionary<string, SafetensorsContainerParser.TensorReference>(StringComparer.Ordinal);
        foreach (var r in refs) refMap[r.Name] = r;

        const int vocab = 30522, d = 384;
        float[] embed = WeightTensorETL.LoadTensorF32(refMap, "embeddings.word_embeddings.weight", (long)vocab * d);

        // Sample rows, then column-center (the system's own SimilarityPlane path):
        // the lens approximates the CENTERED gram, so the baseline is centered too.
        var A = new float[(long)N * d];
        Array.Copy(embed, (long)RowOffset * d, A, 0, (long)N * d);
        unsafe
        {
            fixed (float* pa = A)
                Assert.Equal(0, DynInterop.CenterColumnsF(pa, N, d));
        }

        // Native SVD, one call at KMax; k-ladders slice U*S.
        var U = new float[(long)N * KMax];
        var S = new float[KMax];
        var Vt = new float[(long)KMax * d];
        nuint rank = 0;
        unsafe
        {
            fixed (float* pa = A) fixed (float* pu = U) fixed (float* ps = S) fixed (float* pv = Vt)
                Assert.Equal(0, SynInterop.TensorSvdTruncate(pa, N, d, 0.0, &rank, pu, ps, pv, KMax));
        }
        Assert.True((int)rank >= Ks[^1], $"SVD rank {rank} below requested ladder");

        // Baseline: native-dim (centered) rows as f64.
        var baseD = new double[(long)N * d];
        unsafe
        {
            fixed (float* pa = A) fixed (double* pb = baseD)
                Assert.Equal(0, DynInterop.F32ToF64(pa, (nuint)((long)N * d), pb));
        }
        double[] baseScores = ProbeScores(baseD, d);

        double[][] baseTop = TopNeighbors(baseScores);
        double[] baseRanks0 = null!;

        _out.WriteLine($"native d={d} baseline vs rank-k lens (probes={Probes}, n={N})");
        _out.WriteLine("  k | gram relerr | spearman | top20 overlap | softmax KL");

        double overlapAt4 = 0, overlapAt32 = 0, spearmanAt4 = 0;
        foreach (int k in Ks)
        {
            var Y = new double[(long)N * k];
            for (int i = 0; i < N; i++)
                for (int j = 0; j < k; j++)
                    Y[(long)i * k + j] = (double)U[(long)i * KMax + j] * S[j];

            double[] lens = ProbeScores(Y, k);

            double relErr = GramRelErr(baseScores, lens);
            double spear = MeanSpearman(baseScores, lens, ref baseRanks0);
            double overlap = MeanTopOverlap(baseTop, lens);
            double kl = MeanSoftmaxKl(baseScores, lens, 1.0 / Math.Sqrt(d));

            _out.WriteLine($" {k,2} |   {relErr,7:F4}   |  {spear,6:F3}  |    {overlap,6:P1}    |  {kl,7:F3}");
            if (k == 4) { overlapAt4 = overlap; spearmanAt4 = spear; }
            if (k == 32) overlapAt32 = overlap;
        }

        // Lens viability floor: 4D nomination must beat chance decisively
        // (chance for top-20 of 1999 candidates is ~1.0%), and more dimensions
        // must not make the lens worse.
        Assert.True(overlapAt4 > 0.05, $"4D top-{TopK} overlap {overlapAt4:P1} <= 5x chance");
        Assert.True(spearmanAt4 > 0.2, $"4D spearman {spearmanAt4:F3} too weak to nominate");
        Assert.True(overlapAt32 >= overlapAt4, "lens quality should not degrade with rank");
    }

    // Dense Probes x N score matrix via the native bilinear tile (self-pairs).
    private static double[] ProbeScores(double[] X, int dim)
    {
        long cap = (long)Probes * N;
        var rows = new int[cap];
        var cols = new int[cap];
        var vals = new double[cap];
        var fp = new long[cap];
        var dense = new double[cap];
        unsafe
        {
            nuint count = 0;
            int overflow = 0;
            fixed (double* px = X)
            fixed (int* pr = rows) fixed (int* pc = cols)
            fixed (double* pv = vals) fixed (long* ps = fp)
            {
                int rc = DynInterop.BilinearEdgesTile(px, 0, Probes, px, N, (nuint)dim, 0.0,
                    pr, pc, pv, ps, (nuint)cap, &count, &overflow);
                Assert.Equal(0, rc);
                Assert.Equal(0, overflow);
                for (nuint e = 0; e < count; e++)
                    dense[(long)rows[e] * N + cols[e]] = vals[e];
            }
        }
        return dense;
    }

    private static double GramRelErr(double[] a, double[] b)
    {
        double num = 0, den = 0;
        for (long i = 0; i < a.Length; i++)
        {
            int col = (int)(i % N);
            int row = (int)(i / N);
            if (col == row) continue;
            double e = a[i] - b[i];
            num += e * e;
            den += a[i] * a[i];
        }
        return Math.Sqrt(num / Math.Max(den, 1e-300));
    }

    private static double[][] TopNeighbors(double[] scores)
    {
        var tops = new double[Probes][];
        for (int p = 0; p < Probes; p++)
        {
            var idx = Enumerable.Range(0, N).Where(c => c != p)
                .OrderByDescending(c => scores[(long)p * N + c]).Take(TopK)
                .Select(c => (double)c).ToArray();
            tops[p] = idx;
        }
        return tops;
    }

    private static double MeanTopOverlap(double[][] baseTop, double[] lens)
    {
        double total = 0;
        for (int p = 0; p < Probes; p++)
        {
            var set = new HashSet<double>(baseTop[p]);
            int hit = Enumerable.Range(0, N).Where(c => c != p)
                .OrderByDescending(c => lens[(long)p * N + c]).Take(TopK)
                .Count(c => set.Contains(c));
            total += (double)hit / TopK;
        }
        return total / Probes;
    }

    private static double MeanSpearman(double[] a, double[] b, ref double[] _)
    {
        double total = 0;
        var buf = new int[N - 1];
        for (int p = 0; p < Probes; p++)
        {
            double[] ra = RowRanks(a, p, buf);
            double[] rb = RowRanks(b, p, buf);
            total += Pearson(ra, rb);
        }
        return total / Probes;
    }

    private static double[] RowRanks(double[] m, int p, int[] buf)
    {
        int n = 0;
        for (int c = 0; c < N; c++) if (c != p) buf[n++] = c;
        var order = buf.Take(n).OrderBy(c => m[(long)p * N + c]).ToArray();
        var ranks = new double[N];
        for (int i = 0; i < order.Length; i++) ranks[order[i]] = i;
        var outRow = new double[n];
        n = 0;
        for (int c = 0; c < N; c++) if (c != p) outRow[n++] = ranks[c];
        return outRow;
    }

    private static double Pearson(double[] x, double[] y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        return sxy / Math.Sqrt(Math.Max(sxx * syy, 1e-300));
    }

    private static double MeanSoftmaxKl(double[] a, double[] b, double temp)
    {
        double total = 0;
        for (int p = 0; p < Probes; p++)
        {
            double[] pa = SoftmaxRow(a, p, temp);
            double[] pb = SoftmaxRow(b, p, temp);
            double kl = 0;
            for (int c = 0; c < pa.Length; c++)
                kl += pa[c] * (Math.Log(pa[c] + 1e-12) - Math.Log(pb[c] + 1e-12));
            total += kl;
        }
        return total / Probes;
    }

    private static double[] SoftmaxRow(double[] m, int p, double temp)
    {
        var vals = new List<double>(N - 1);
        for (int c = 0; c < N; c++) if (c != p) vals.Add(m[(long)p * N + c] * temp);
        double max = vals.Max();
        double sum = 0;
        var e = new double[vals.Count];
        for (int i = 0; i < e.Length; i++) { e[i] = Math.Exp(vals[i] - max); sum += e[i]; }
        for (int i = 0; i < e.Length; i++) e[i] /= sum;
        return e;
    }
}
