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
