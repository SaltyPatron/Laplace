using System.Collections.Generic;
using Xunit;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// The faithful model-circuit edge emitter: contract the FULL operator
/// M = Left·Rightᵀ, emit one signed observation per SIGNAL edge (|m| &gt; θ),
/// skip draws (|m| ≤ θ) and self-edges (i = j). No argmax, no top-k. Proves it
/// emits exactly the above-θ off-diagonal cells, signed score ½(1+tanh(m/M)),
/// canonical kind, correct subject/object.
/// </summary>
public class ModelCircuitEdgesTests
{
    private static Hash128 Tok(int i) => Hash128.OfCanonical($"tok/{i}");

    [Fact]
    public void Emit_FullBilinear_SignalEdgesOnly_SignedSkipsDrawsAndSelf()
    {
        // 3 subjects × 4 objects, r=2. M[i][j] = left[i]·right[j].
        double[] left  = { 2, 0,   0, 2,   1, 1 };                 // [3 × 2]
        double[] right = { 3, 0,   0, 1,  -2, 0,   1, 1 };         // [4 × 2]
        const int V = 4, r = 2;                                    // vocab indices span 0..3 (subjects use 0..2)
        const double M = 2.0, theta = 1.5;

        // M rows:  i0=[6,0,-4,2]  i1=[0,2,0,2]  i2=[3,1,-2,2]
        // |m|>1.5 off-diagonal (skip i==j: (0,0),(1,1),(2,2)):
        //   (0,2,-4) (0,3,2) | (1,3,2) | (2,0,3) (2,3,2)   → 5 edges
        var expected = new Dictionary<(int, int), double>
        {
            { (0, 2), -4 }, { (0, 3), 2 },
            { (1, 3), 2 },
            { (2, 0), 3 },  { (2, 3), 2 },
        };

        var src = Hash128.OfCanonical("src/model");
        var witness = Hash128.OfCanonical("witness/l0h0");
        var builder = new SubstrateChangeBuilder(src, "test/circuit", null, 0, 0, 16);

        // Left has 3 rows; emit over subjects 0..2 (pass vocab=3 for the left side via tileRows).
        int n = ModelCircuitEdges.Emit(
            left, right, /*vocab(objects)*/ V, r,
            "COMPLETES_TO", arenaScale: M, theta: theta, sourceTrust: 0.5,
            tokenEntity: i => Tok(i), sourceId: src, witness: witness, builder, tileRows: 3);

        Assert.Equal(expected.Count, n);

        var change = builder.Build();
        Assert.Equal(expected.Count, change.Attestations.Length);

        Hash128 kind = KindRegistry.KindId("COMPLETES_TO");
        var seen = new HashSet<(int, int)>();
        foreach (var a in change.Attestations)
        {
            Assert.Equal(kind, a.KindId);
            Assert.Equal(witness, a.ContextId);
            // recover (i,j) from subject/object tok ids
            int i = -1, j = -1;
            for (int t = 0; t < 4; t++) { if (a.SubjectId == Tok(t)) i = t; if (a.ObjectId == Tok(t)) j = t; }
            Assert.True(i >= 0 && j >= 0, "subject/object map back to tokens");
            Assert.NotEqual(i, j);                                  // no self-edges
            Assert.True(expected.ContainsKey((i, j)), $"unexpected edge ({i},{j})");
            seen.Add((i, j));

            double m = expected[(i, j)];
            double wantScore = 0.5 * (1.0 + System.Math.Tanh(m / M));
            long wantFp = (long)(wantScore * Glicko2.FpScale);
            Assert.InRange(a.ScoreFp1e9 - wantFp, -2, 2);           // signed score, fp-rounding tolerance
        }
        Assert.Equal(expected.Count, seen.Count);                   // all expected present, none extra
    }

    // A·Bᵀ : A[n×k], B[m×k] → [n×m]
    private static double[] MatMulT(float[] A, int n, int k, float[] B, int m)
    {
        var o = new double[n * m];
        for (int i = 0; i < n; i++) for (int j = 0; j < m; j++)
        { double s = 0; for (int t = 0; t < k; t++) s += (double)A[i * k + t] * B[j * k + t]; o[i * m + j] = s; }
        return o;
    }
    // A·B : A[n×k], B[k×m] → [n×m]
    private static double[] MatMul(float[] A, int n, int k, float[] B, int m)
    {
        var o = new double[n * m];
        for (int i = 0; i < n; i++) for (int j = 0; j < m; j++)
        { double s = 0; for (int t = 0; t < k; t++) s += (double)A[i * k + t] * B[t * m + j]; o[i * m + j] = s; }
        return o;
    }
    private static void AssertClose(double[] a, double[] b)
    {
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.InRange(a[i] - b[i], -1e-9, 1e-9);
    }

    [Fact]
    public void ProjectCircuit_Transposes_And_Sides_MatchReference()
    {
        const int V = 3, d = 2;
        float[] E  = { 1, 2,  -1, 0,  0.5f, 1 };          // [3×2]
        float[] EU = { 0, 1,   1, 1,  -1,   2 };          // [3×2] (untied)

        // QK: q=[outQ×d], k=[outK×d]; both read through E. encProj=E·qᵀ, decProj=E·kᵀ.
        float[] q = { 1, 0,  0, 1,  1, 1 };               // outQ=3
        float[] k = { 2, -1, 0, 1 };                      // outK=2
        var qk = ModelCircuitEdges.ProjectCircuit(ModelCircuitEdges.CircuitForm.Qk, q, 3, k, 2, d, E, EU, V, d);
        AssertClose(MatMulT(E, V, d, q, 3), qk.encProj);
        AssertClose(MatMulT(E, V, d, k, 2), qk.decProj);
        Assert.Equal(3, qk.rEnc); Assert.Equal(2, qk.rDec);

        // OV: v=[outV×d] through E; o=[d×out2] through E_U → decProj = E_U·o (transpose handled).
        float[] v = { 1, 1,  -1, 2 };                     // outV=2
        float[] o = { 1, 0, 2,  0, 1, -1 };               // [d=2 × out2=3]
        var ov = ModelCircuitEdges.ProjectCircuit(ModelCircuitEdges.CircuitForm.Ov, v, 2, o, d, 3, E, EU, V, d);
        AssertClose(MatMulT(E, V, d, v, 2), ov.encProj);
        AssertClose(MatMul(EU, V, d, o, 3), ov.decProj);  // E_U·o, NOT E_U·oᵀ
        Assert.Equal(2, ov.rEnc); Assert.Equal(3, ov.rDec);

        // FFN: up=[interm×d] through E; down=[d×interm] through E_U → decProj = E_U·down.
        float[] up   = { 1, 0,  0, 1,  1, 1 };            // interm=3
        float[] down = { 1, 1, 0,  0, 1, 1 };             // [d=2 × interm=3]
        var ffn = ModelCircuitEdges.ProjectCircuit(ModelCircuitEdges.CircuitForm.Ffn, up, 3, down, d, 3, E, EU, V, d);
        AssertClose(MatMulT(E, V, d, up, 3), ffn.encProj);
        AssertClose(MatMul(EU, V, d, down, 3), ffn.decProj);
        Assert.Equal(3, ffn.rEnc); Assert.Equal(3, ffn.rDec);
    }

    [Fact]
    public void SliceHead_ExtractsContiguousHead()
    {
        // proj [V=2 × R=4] = 2 heads of headDim=2.
        double[] proj = { 10, 11, 20, 21,  30, 31, 40, 41 };
        var h0 = ModelCircuitEdges.SliceHead(proj, 2, 4, 0, 2);
        var h1 = ModelCircuitEdges.SliceHead(proj, 2, 4, 1, 2);
        Assert.Equal(new double[] { 10, 11, 30, 31 }, h0);
        Assert.Equal(new double[] { 20, 21, 40, 41 }, h1);
    }

    [Fact]
    public void Emit_HigherTheta_KeepsOnlyStronger()
    {
        double[] left  = { 2, 0,   0, 2,   1, 1 };
        double[] right = { 3, 0,   0, 1,  -2, 0,   1, 1 };
        var src = Hash128.OfCanonical("src/model");
        var b1 = new SubstrateChangeBuilder(src, "t", null, 0, 0, 16);
        var b2 = new SubstrateChangeBuilder(src, "t", null, 0, 0, 16);

        int lo = ModelCircuitEdges.Emit(left, right, 4, 2, "COMPLETES_TO", 2.0, 1.5, 0.5,
                                        i => Tok(i), src, Hash128.Zero, b1, 3);
        int hi = ModelCircuitEdges.Emit(left, right, 4, 2, "COMPLETES_TO", 2.0, 2.5, 0.5,
                                        i => Tok(i), src, Hash128.Zero, b2, 3);
        // θ=2.5 keeps only |m|>2.5: (0,2,-4) (2,0,3) → 2; θ=1.5 keeps 5. Monotone.
        Assert.Equal(5, lo);
        Assert.Equal(2, hi);
    }
}
