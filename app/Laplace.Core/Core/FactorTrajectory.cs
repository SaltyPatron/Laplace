using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

// The factor-trajectory layout LAW — the single shared convention between the
// deposit side (ModelTokenEdgeETL) and every reader (gates, item-B scorer).
//
//   vertex 0                  : ARENA factor vertex — the raw f32 arena scalar
//                               (score-law inversion needs it; f32 because
//                               embedding-scale arenas overflow fp1e9).
//   per token t (0-based)     : 1 HEADER testimony vertex — object = token
//                               entity id (in-DB identity), score =
//                               laplace_score_fp(salience_t, arena), games =
//                               factor dim (mod 2^16) — then ceil(dim/6)
//                               FACTOR vertices carrying the raw f32 factors.
//
// Vertex address of token t: 1 + t * (1 + ceil(dim/6)). Salience in the header
// IS the Cauchy–Schwarz pruning bound (|q·k| <= ||q||·||k||).
public static class FactorTrajectory
{
    public static int FactorVerticesPerToken(int dim) =>
        (dim + FactorWalk.ValuesPerVertex - 1) / FactorWalk.ValuesPerVertex;

    public static int StridePerToken(int dim) => 1 + FactorVerticesPerToken(dim);

    public static int TotalVertices(int tokens, int dim) => 1 + tokens * StridePerToken(dim);

    public static int TokenCount(int totalVertices, int dim)
    {
        int stride = StridePerToken(dim);
        int body = totalVertices - 1;
        if (body < 0 || body % stride != 0)
            throw new InvalidOperationException(
                $"trajectory of {totalVertices} vertices does not fit factor layout at dim {dim}");
        return body / stride;
    }


    public static double[] Pack(
        Hash128 carrier, double arena, ReadOnlySpan<Hash128> tokens,
        double[] factors, int dim, double[] salience)
    {
        int n = tokens.Length;
        int fvpt = FactorVerticesPerToken(dim);
        int stride = StridePerToken(dim);
        var xyzm = new double[(long)TotalVertices(n, dim) * 4];

        Span<Hash128> one = stackalloc Hash128[1];
        Span<long> oneScore = stackalloc long[1];
        Span<ushort> oneGames = stackalloc ushort[1];

        // Arena rides a FACTOR vertex (raw f32): exact, no fp1e9 range ceiling —
        // embedding-scale arenas (~140) overflow the testimony score field.
        Span<float> arenaVal = stackalloc float[1] { (float)arena };
        MemoryMarshal.Cast<byte, double>(FactorWalk.Pack(arenaVal))
            .CopyTo(xyzm.AsSpan(0, 4));

        var vals = new float[dim];
        for (int t = 0; t < n; t++)
        {
            one[0] = tokens[t];
            oneScore[0] = NativeInterop.ScoreFp(salience[t], arena);
            oneGames[0] = (ushort)dim;
            int baseV = (1 + t * stride) * 4;
            MemoryMarshal.Cast<byte, double>(TestimonyWalk.Pack(one, oneScore, oneGames))
                .CopyTo(xyzm.AsSpan(baseV, 4));

            for (int j = 0; j < dim; j++) vals[j] = (float)factors[(long)t * dim + j];
            MemoryMarshal.Cast<byte, double>(FactorWalk.Pack(vals))
                .CopyTo(xyzm.AsSpan(baseV + 4, fvpt * 4));
        }
        return xyzm;
    }

    public static double UnpackArena(ReadOnlySpan<double> xyzm)
    {
        float[] vals = FactorWalk.Unpack(xyzm.Slice(0, 4));
        if (vals.Length < 1)
            throw new InvalidOperationException("arena vertex holds no value");
        return vals[0];
    }

    public static (Hash128 Token, long SalienceFp) UnpackHeader(
        ReadOnlySpan<double> xyzm, int t, int dim)
    {
        int baseV = (1 + t * StridePerToken(dim)) * 4;
        UnpackTestimony(xyzm.Slice(baseV, 4), out var token, out long fp, out _);
        return (token, fp);
    }

    public static float[] UnpackFactors(ReadOnlySpan<double> xyzm, int t, int dim)
    {
        int baseV = (1 + t * StridePerToken(dim) + 1) * 4;
        float[] vals = FactorWalk.Unpack(xyzm.Slice(baseV, FactorVerticesPerToken(dim) * 4));
        if (vals.Length < dim)
            throw new InvalidOperationException($"factor run holds {vals.Length} values, expected {dim}");
        if (vals.Length == dim) return vals;
        var trimmed = new float[dim];
        Array.Copy(vals, trimmed, dim);
        return trimmed;
    }

    private static unsafe void UnpackTestimony(
        ReadOnlySpan<double> vertex, out Hash128 objectId, out long scoreFp, out ushort games)
    {
        Hash128 obj = default;
        long fp = 0;
        ushort g = 0, ord = 0;
        fixed (double* pv = vertex)
        {
            int rc = NativeInterop.LaplaceTestimonyUnpackVertex(pv, &obj, &fp, &g, &ord);
            if (rc != 0)
                throw new InvalidOperationException($"testimony unpack failed: {rc}");
        }
        objectId = obj;
        scoreFp = fp;
        games = g;
    }
}
