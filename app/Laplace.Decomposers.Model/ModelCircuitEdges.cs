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
/// The contraction already sums the hidden units, so this is token→token
/// directly (no per-unit n-gram, no address book). The ONLY cut is the coherence
/// threshold θ = β·M (M = the §10 arena scale): |m| &gt; θ is SIGNAL (a win/loss
/// that moves consensus μ); |m| ≲ θ is a DRAW (tanh(m/M) ≈ ½, no win/loss) and is
/// skipped natively — never argmax, never top-k, never an a-priori amount. Score =
/// ½(1 + tanh(m/M)) via <see cref="KindRegistry.AttestWeighted"/> (canonical kind
/// id + kind_rank × source_trust → opponent φ). Self-edges (i = j) are skipped.
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

    /// <summary>RMS of the contracted operator magnitudes over a row sample — the
    /// §10 arena scale M used both for the score (tanh(m/M)) and to set θ = β·M.
    /// Computed from the same engine contraction, so it is exact and deterministic.</summary>
    public static double ArenaScale(double[] left, double[] right, int vocab, int r, int sampleRows = 256)
    {
        int S = Math.Min(sampleRows, vocab);
        long cap = (long)S * vocab;
        var rows = new int[cap]; var cols = new int[cap]; var vals = new double[cap];
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
        double sumsq = 0.0;
        for (long e = 0; e < cnt; e++) sumsq += vals[e] * vals[e];
        return cnt > 0 ? Math.Sqrt(sumsq / cnt) : 0.0;
    }
}
