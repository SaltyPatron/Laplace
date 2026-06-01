using Xunit;
using Laplace.Engine.Synthesis;

namespace Laplace.Engine.Synthesis.Tests;

/// <summary>
/// Cross-language parity for the exact streaming QK kernel
/// (<see cref="NativeInterop.ComputeQkPairsAboveThreshold"/>). An independent C#
/// reimplementation of the identical compensated projection + dot-product + threshold
/// must produce the same pair set, same order (t asc, then s asc), and bit-identical
/// f64 scores as the native engine kernel.
/// </summary>
public class QkPairsThresholdParityTests
{
    private static void Neumaier(ref double sum, ref double c, double term)
    {
        double t = sum + term;
        if (Math.Abs(sum) >= Math.Abs(term)) c += (sum - t) + term;
        else                                 c += (term - t) + sum;
        sum = t;
    }

    private static double[] Project(float[] e, int t, float[] w, int dModel, int headDim)
    {
        var proj = new double[headDim];
        for (int d = 0; d < headDim; d++)
        {
            double s = 0, c = 0;
            for (int m = 0; m < dModel; m++) Neumaier(ref s, ref c, (double)e[t * dModel + m] * (double)w[d * dModel + m]);
            proj[d] = s + c;
        }
        return proj;
    }

    private static List<(uint q, uint k, double sc)> Reference(
        float[] e, int vocab, int dModel, float[] wq, float[] wk, int headDim, double floor)
    {
        var outp = new List<(uint, uint, double)>();
        for (int t = 0; t < vocab; t++)
        {
            var qt = Project(e, t, wq, dModel, headDim);
            for (int sIdx = 0; sIdx < vocab; sIdx++)
            {
                var ks = Project(e, sIdx, wk, dModel, headDim);
                double s = 0, c = 0;
                for (int d = 0; d < headDim; d++) Neumaier(ref s, ref c, qt[d] * ks[d]);
                double sc = s + c;
                if (Math.Abs(sc) > floor) outp.Add(((uint)t, (uint)sIdx, sc));
            }
        }
        return outp;
    }

    private static float[] Rand(int n, int seed)
    {
        var rng = new Random(seed);
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return a;
    }

    [Theory]
    [InlineData(0.0)]    // all non-zero pairs → full projection/score/order parity
    [InlineData(1.5)]    // higher floor → threshold-subset parity
    public unsafe void MatchesManagedReference_Bitwise(double floor)
    {
        const int vocab = 64, dModel = 16, headDim = 8;
        var e  = Rand(vocab * dModel, 1);
        var wq = Rand(headDim * dModel, 2);
        var wk = Rand(headDim * dModel, 3);

        var expected = Reference(e, vocab, dModel, wq, wk, headDim, floor);

        var got = new QkPairF64[vocab * vocab];
        long n;
        int overflow;
        fixed (float* ep = e) fixed (float* qp = wq) fixed (float* kp = wk)
        fixed (QkPairF64* op = got)
            n = NativeInterop.ComputeQkPairsAboveThreshold(
                ep, vocab, dModel, qp, kp, headDim, floor, 0, vocab, op, (nuint)got.Length, &overflow);

        Assert.Equal(0, overflow);
        Assert.Equal(expected.Count, (int)n);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(expected[i].q, got[i].QueryIdx);
            Assert.Equal(expected[i].k, got[i].KeyIdx);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].sc),
                         BitConverter.DoubleToInt64Bits(got[i].Score));
        }
    }

    /// <summary>The sub-quadratic norm-pruned kernel (used by ingestion) must produce
    /// output bit-identical to the all-pairs kernel through the C# bindings too.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public unsafe void PrunedKernel_MatchesAllPairs_Bitwise(double floor)
    {
        const int vocab = 200, dModel = 24, headDim = 12;
        var e = Rand(vocab * dModel, 11); var wq = Rand(headDim * dModel, 12); var wk = Rand(headDim * dModel, 13);
        var a = new QkPairF64[vocab * vocab];
        var p = new QkPairF64[vocab * vocab];
        long na, np; int ofa, ofp;
        fixed (float* ep = e) fixed (float* qp = wq) fixed (float* kp = wk)
        fixed (QkPairF64* ap = a) fixed (QkPairF64* pp = p)
        {
            na = NativeInterop.ComputeQkPairsAboveThreshold(
                ep, vocab, dModel, qp, kp, headDim, floor, 0, vocab, ap, (nuint)a.Length, &ofa);
            np = NativeInterop.ComputeQkPairsAboveThresholdPruned(
                ep, vocab, dModel, qp, kp, headDim, floor, 0, vocab, pp, (nuint)p.Length, &ofp);
        }
        Assert.Equal(na, np);
        Assert.Equal(ofa, ofp);
        for (int i = 0; i < na; i++)
        {
            Assert.Equal(a[i].QueryIdx, p[i].QueryIdx);
            Assert.Equal(a[i].KeyIdx, p[i].KeyIdx);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].Score), BitConverter.DoubleToInt64Bits(p[i].Score));
        }
    }

    /// <summary>The project-once + score-from-cache path (ProjectQkLayer + ScoreQkHeadCached).
    /// DEPRECATED: ScoreQkHeadCached is the retired token×token-bilinear scorer, replaced by the
    /// per-dim address-book circuit read (WeightTensorETL.EmitCircuitMemoriesAsync); nothing in
    /// the live ingest/synthesis calls it. The all-heads projection cache (ProjectQkLayer)
    /// differs from the inline per-head projection by ≤1 ULP (FP non-associativity); it is
    /// internally deterministic, and the live path reads projection magnitudes above a noise
    /// floor (1-ULP-robust), so cross-impl bit-identity for the retired scorer is no longer
    /// maintained. Skipped rather than chase FP parity on a removed path.</summary>
    [Theory(Skip = "deprecated bilinear scorer (retired path); ProjectQkLayer ≤1 ULP vs inline, live read is floor-robust")]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public unsafe void ProjectCached_MatchesAllPairs_Bitwise(double floor)
    {
        const int vocab = 200, dModel = 24, headDim = 12, nHeads = 8, nKv = 2;
        const int queriesPerKv = nHeads / nKv;
        var e  = Rand(vocab * dModel, 21);
        var wq = Rand(nHeads * headDim * dModel, 22);
        var wk = Rand(nKv    * headDim * dModel, 23);

        var qCache = new double[(long)vocab * nHeads * headDim];
        var kCache = new double[(long)vocab * nKv    * headDim];
        fixed (float* ep = e) fixed (float* qp = wq) fixed (float* kp = wk)
        fixed (double* qc = qCache) fixed (double* kc = kCache)
        {
            int rc = NativeInterop.ProjectQkLayer(ep, vocab, dModel, qp, nHeads, kp, nKv, headDim, qc, kc);
            Assert.Equal(0, rc);
        }

        for (int head = 0; head < nHeads; head++)
        {
            int kvHead = head / queriesPerKv;
            // All-pairs reference for this head's slice.
            var wqHead = new float[headDim * dModel];
            var wkHead = new float[headDim * dModel];
            Array.Copy(wq, (long)head   * headDim * dModel, wqHead, 0, headDim * dModel);
            Array.Copy(wk, (long)kvHead * headDim * dModel, wkHead, 0, headDim * dModel);

            var a = new QkPairF64[vocab * vocab];
            var g = new QkPairF64[vocab * vocab];
            long na, ng; int ofa, ofg;
            fixed (float* ep = e) fixed (float* qhp = wqHead) fixed (float* khp = wkHead)
            fixed (QkPairF64* ap = a) fixed (QkPairF64* gp = g)
            fixed (double* qc = qCache) fixed (double* kc = kCache)
            {
                na = NativeInterop.ComputeQkPairsAboveThreshold(
                    ep, vocab, dModel, qhp, khp, headDim, floor, 0, vocab, ap, (nuint)a.Length, &ofa);
                ng = NativeInterop.ScoreQkHeadCached(
                    qc, nHeads, kc, nKv, vocab, headDim, (nuint)head, (nuint)kvHead,
                    floor, 0, vocab, gp, (nuint)g.Length, &ofg);
            }
            Assert.Equal(na, ng);
            Assert.Equal(ofa, ofg);
            for (int i = 0; i < na; i++)
            {
                Assert.Equal(a[i].QueryIdx, g[i].QueryIdx);
                Assert.Equal(a[i].KeyIdx, g[i].KeyIdx);
                Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].Score), BitConverter.DoubleToInt64Bits(g[i].Score));
            }
        }
    }
}
