using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// The FAITHFUL model-circuit edge emitter — replaces the argmax address book +
/// energy floor + top-k. Given a circuit's projected operands Left = E·Wenc and
/// Right = E_U·Wdec (both [V × r], r = the inner/head dim), it contracts the full
/// operator M = Left·Rightᵀ tile-by-tile (engine <c>bilinear_edges_tile</c>) and
/// emits one Glicko observation per SIGNAL edge — a content×content relation
/// token_i →(kind) token_j with the SIGNED coupling m.
///
/// The contraction already sums the hidden units, so this is token→token directly
/// (no per-unit n-gram, no address book). The ONLY cut is the coherence threshold θ
/// (≥ M = the §10 arena scale), derived per circuit by <see cref="DeriveTheta"/> from
/// one global retrieval-fidelity budget: |m| &gt; θ is SIGNAL (a win/loss that moves
/// consensus μ); |m| ≲ M is a DRAW (tanh(m/M) ≈ ½, no win/loss) skipped natively —
/// never argmax, never top-k, never an a-priori amount. Score = ½(1 + tanh(m/M)) via
/// <see cref="KindRegistry.AttestWeighted"/> (canonical kind id + kind_rank ×
/// source_trust → opponent φ). Self-edges (i = j) are skipped.
/// </summary>
public static class ModelCircuitEdges
{
    /// <summary>
    /// Emit the circuit's signal edges into <paramref name="builder"/>. Returns the
    /// number emitted. <paramref name="tokenEntity"/> maps a vocab index to its
    /// content entity id (null ⇒ skip that token — e.g. a special / unanchored id).
    /// </summary>
    public static int Emit(
        double[] left, double[] right, int vocab, int r,
        string kindName, double arenaScale, double theta, double sourceTrust,
        Func<int, Hash128?> tokenEntity, Hash128 sourceId, Hash128 witness,
        SubstrateChangeBuilder builder, int tileRows = 256)
    {
        if (left is null || right is null || builder is null) throw new ArgumentNullException();
        if (vocab <= 0 || r <= 0) return 0;

        long cap = (long)tileRows * vocab;        // worst case: every column above θ
        var rows = new int[cap];
        var cols = new int[cap];
        var vals = new double[cap];
        int emitted = 0;

        for (int b0 = 0; b0 < vocab; b0 += tileRows)
        {
            int b1 = Math.Min(b0 + tileRows, vocab);
            long cnt; int overflow;
            unsafe
            {
                fixed (double* lp = left) fixed (double* rp = right)
                fixed (int* orr = rows) fixed (int* occ = cols) fixed (double* ovv = vals)
                {
                    nuint c; int ov;
                    int rc = DynInterop.BilinearEdgesTile(
                        lp, (nuint)b0, (nuint)b1, rp, (nuint)vocab, (nuint)r, theta,
                        orr, occ, ovv, (nuint)cap, &c, &ov);
                    if (rc != 0) throw new InvalidOperationException($"bilinear_edges_tile rc={rc}");
                    cnt = (long)c; overflow = ov;
                }
            }
            if (overflow != 0)   // cap is the exact worst case, so this is a real invariant break
                throw new InvalidOperationException("bilinear_edges_tile overflow — tile buffer undersized");

            for (long e = 0; e < cnt; e++)
            {
                int i = rows[e], j = cols[e];
                if (i == j) continue;                       // skip self-edges
                var si = tokenEntity(i); var oj = tokenEntity(j);
                if (si is null || oj is null) continue;
                builder.AddAttestation(KindRegistry.AttestWeighted(
                    si.Value, kindName, oj.Value, sourceId, sourceTrust,
                    magnitude: vals[e], floor: arenaScale, contextId: witness));
                emitted++;
            }
        }
        return emitted;
    }

    /// <summary>Circuit factorization form — which embedding each side reads through.</summary>
    public enum CircuitForm { Qk, Ov, Ffn }

    /// <summary>
    /// Project a circuit's two sides to the operands of M = encProj·decProjᵀ:
    ///   QK  : encProj = E·Wqᵀ, decProj = E·Wkᵀ          (both through E; q,k are [out×dModel])
    ///   OV  : encProj = E·Wvᵀ, decProj = E_U·Wo          (v is [out×dModel]; o is [dModel×out] → transpose)
    ///   FFN : encProj = E·Wupᵀ, decProj = E_U·Wdown      (up is [interm×dModel]; down [dModel×interm] → transpose)
    /// enc is always [out×dModel] read through E. dec is read through E (QK) or E_U with a
    /// transpose (OV/FFN), since project_embedding computes pts·Wᵀ and those sides need pts·W.
    /// Returns the projected operands and their inner ranks.
    /// </summary>
    public static (double[] encProj, int rEnc, double[] decProj, int rDec) ProjectCircuit(
        CircuitForm form, float[] enc, int encOut, float[] dec, int decRows, int decCols,
        float[] E, float[] EU, int vocab, int dModel)
    {
        int rEnc = encOut;
        double[] encProj = Project(E, vocab, dModel, enc, rEnc);
        int rDec; double[] decProj;
        if (form == CircuitForm.Qk) { rDec = decRows; decProj = Project(E, vocab, dModel, dec, rDec); }
        else { rDec = decCols; decProj = Project(EU, vocab, dModel, Transpose(dec, decRows, decCols), rDec); }
        return (encProj, rEnc, decProj, rDec);
    }

    /// <summary>Extract head <paramref name="headIndex"/>'s contiguous [vocab × headDim] slice
    /// from a projected [vocab × R] matrix (GQA: a side may have fewer heads than the other).</summary>
    public static double[] SliceHead(double[] proj, int vocab, int R, int headIndex, int headDim)
    {
        var s = new double[(long)vocab * headDim];
        long off = (long)headIndex * headDim;
        for (int v = 0; v < vocab; v++) Array.Copy(proj, (long)v * R + off, s, (long)v * headDim, headDim);
        return s;
    }

    private static double[] Project(float[] pts, int n, int d, float[] W, int r)
    {
        var outp = new double[(long)n * r];
        unsafe
        {
            fixed (float* p = pts) fixed (float* w = W) fixed (double* o = outp)
                if (DynInterop.ProjectEmbedding(p, (nuint)n, (nuint)d, w, (nuint)r, o) != 0)
                    throw new InvalidOperationException("project_embedding failed");
        }
        return outp;
    }

    private static float[] Transpose(float[] w, int rows, int cols)
    {
        var t = new float[(long)cols * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                t[(long)c * rows + r] = w[(long)r * cols + c];
        return t;
    }

    /// <summary>
    /// Calibrate ONE arena (kind) — its magnitude scale M and coherence floor θ — pooled
    /// across a bounded sample of the arena's witness sub-operators. An arena is a KIND
    /// (ATTENDS / OV_RELATES / COMPLETES_TO), not a circuit: every head/layer/expert is a
    /// WITNESS into the same arena, so they must share ONE scale. Scoring each witness by
    /// its OWN scale would self-normalize it (the banned per-witness magnitude reduction)
    /// and collapse cross-witness consensus. M and θ are therefore frozen per arena and
    /// reused for every edge of the kind (frame-invariant; <see cref="AttestationFactory"/>
    /// scores ½(1+tanh(m/M)) and the doc names M "the per-arena scale").
    ///
    /// M = RMS|m| over the pooled (θ=0) contraction of the samples. θ (settled decision
    /// #1(b)) = the LARGEST floor ≥ M whose kept edges still reproduce each signal subject's
    /// top-K ranked retrieval at mean recall ≥ <paramref name="recallBudget"/> over the
    /// pooled sample — you fix the fidelity KEPT, the data fixes the cut (never energy %,
    /// surviveFrac, or top-k). |m| &lt; M is a DRAW (tanh≈½), skipped natively. Returns
    /// (M, θ, achieved recall); recall &lt; budget ⇒ the arena is diffuse and fell back to
    /// the M floor. Calibration only: bounded sample, never a per-cell loop over the vocab.
    /// </summary>
    public static (double arenaScale, double theta, double recall) CalibrateArena(
        IReadOnlyList<(double[] left, double[] right, int r)> samples, int vocab,
        double recallBudget, int topK = 32, int sampleRows = 256, int maxMultiple = 8, int stepsPerM = 4)
    {
        if (recallBudget <= 0.0 || recallBudget > 1.0)
            throw new ArgumentOutOfRangeException(nameof(recallBudget), "fidelity budget must be in (0,1]");
        if (samples is null || samples.Count == 0 || vocab <= 0) return (0.0, 0.0, 1.0);

        int S = Math.Min(sampleRows, vocab);
        var pooledRows = new List<double[]>(samples.Count * S);   // per-subject descending |m|, pooled across witnesses
        double sumsq = 0.0; long total = 0;

        long cap = (long)S * vocab;
        var rows = new int[cap]; var cols = new int[cap]; var vals = new double[cap];
        foreach (var (left, right, r) in samples)
        {
            if (left is null || right is null || r <= 0) continue;
            long cnt;
            unsafe
            {
                fixed (double* lp = left) fixed (double* rp = right)
                fixed (int* orr = rows) fixed (int* occ = cols) fixed (double* ovv = vals)
                {
                    nuint c; int ov;
                    int rc = DynInterop.BilinearEdgesTile(lp, 0, (nuint)S, rp, (nuint)vocab, (nuint)r, 0.0,
                                                          orr, occ, ovv, (nuint)cap, &c, &ov);
                    if (rc != 0) throw new InvalidOperationException($"bilinear_edges_tile rc={rc}");
                    cnt = (long)c;
                }
            }
            for (long e = 0; e < cnt; e++) { double a = Math.Abs(vals[e]); sumsq += a * a; }
            total += cnt;
            long ee = 0;
            for (int rr = 0; rr < S; rr++)
            {
                long start = ee;
                while (ee < cnt && rows[ee] == rr) ee++;
                var a = new double[ee - start];
                for (long i = start; i < ee; i++) a[i - start] = Math.Abs(vals[i]);
                Array.Sort(a); Array.Reverse(a);
                pooledRows.Add(a);
            }
        }
        if (total == 0) return (0.0, 0.0, 1.0);
        double M = Math.Sqrt(sumsq / total);
        if (M <= 0.0) return (0.0, 0.0, 1.0);

        // Signal subjects: top coupling exceeds the arena scale (real retrieval, not all-draws).
        bool anySignal = false;
        foreach (var a in pooledRows) if (a.Length > 0 && a[0] > M) { anySignal = true; break; }
        if (!anySignal) return (M, M, 1.0);   // nothing above the arena scale; floor at M

        // Largest θ = c·M (c from maxMultiple down to 1) whose pooled recall meets the budget.
        double recallAtM = 1.0;
        for (int s = maxMultiple * stepsPerM; s >= stepsPerM; s--)
        {
            double c = (double)s / stepsPerM;
            double theta = c * M;
            double recSum = 0.0; int seen = 0;
            foreach (var a in pooledRows)
            {
                if (a.Length == 0 || a[0] <= M) continue;       // not a signal subject
                seen++;
                int k = Math.Min(topK, a.Length);
                int nAbove = CountAbove(a, theta);
                recSum += (double)Math.Min(k, nAbove) / k;
            }
            double recall = seen > 0 ? recSum / seen : 0.0;
            if (Math.Abs(c - 1.0) < 1e-12) recallAtM = recall;
            if (recall >= recallBudget) return (M, theta, recall);
        }
        return (M, M, recallAtM);   // even the arena-scale floor misses the budget → floor at M
    }

    /// <summary>Count entries strictly greater than <paramref name="theta"/> in a
    /// descending-sorted array (binary search for the boundary).</summary>
    private static int CountAbove(double[] descSorted, double theta)
    {
        int lo = 0, hi = descSorted.Length;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (descSorted[mid] > theta) lo = mid + 1; else hi = mid; }
        return lo;
    }
}
