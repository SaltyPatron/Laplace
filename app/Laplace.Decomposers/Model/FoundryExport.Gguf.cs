using Laplace.Decomposers.Model;
using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

internal static partial class FoundryExport
{
    internal sealed record LoweredOperator(double[] Scores,double[] Values,int BasisRank,double ScoreError,double ValueError);
    internal static LoweredOperator LowerOperator(double[] basis,int vocab,int width,PlaneCoo plane)
    {
        var scores=new double[(long)width*width]; var values=new double[(long)width*width];
        nuint rank=0; double se=0,ve=0; int rc;
        unsafe {
            fixed(double* r=basis,s=scores,v=values,w=plane.Vals)
            fixed(int* rows=plane.Rows,cols=plane.Cols)
                rc=SynInterop.OperatorCompile(r,(nuint)vocab,(nuint)width,rows,cols,w,(nuint)plane.Nnz,s,v,&rank,&se,&ve);
        }
        if(rc!=0) throw new InvalidOperationException($"native operator compiler status {rc}");
        return new(scores,values,(int)rank,se,ve);
    }

    internal sealed record Factors(float[] Left, float[] Right, int Rank, int Dim, double SampleResidual, double SpectralNorm);













    /// Block Gram-Schmidt across the per-head OV output bases so each head writes an
    /// orthogonal residual slice. Rows are orthonormalized jointly in head order (earlier
    /// heads keep their span; later heads get the complement), then each row's original
    /// norm is re-applied so per-head singular-value energy survives. When the native
    /// routine reports rank deficiency, managed modified Gram-Schmidt zeroes the collinear
    /// directions.
    internal static void BlockOrthonormalizeLeft(
        IReadOnlyList<string> headKeys, Dictionary<string, Factors> fo, int dModel)
    {
        var live = new List<(string Key, Factors F)>();
        int total = 0;
        foreach (var k in headKeys)
            if (fo.TryGetValue(k, out var f) && f.Rank > 0 && f.Dim == dModel)
            { live.Add((k, f)); total += f.Rank; }
        if (live.Count < 2 || total > dModel) return;

        var buf = new double[(long)total * dModel];
        var norms = new double[total];
        int v = 0;
        foreach (var (_, f) in live)
            for (int r = 0; r < f.Rank; r++, v++)
            {
                double n2 = 0;
                for (int j = 0; j < dModel; j++)
                {
                    double x = f.Left[(long)r * dModel + j];
                    buf[(long)v * dModel + j] = x;
                    n2 += x * x;
                }
                norms[v] = Math.Sqrt(n2);
            }

        int rc;
        unsafe { fixed (double* p = buf) rc = DynInterop.GramSchmidtOrthonormalize(p, (nuint)total, (nuint)dModel); }
        if (rc != 0)
        {
            // Heads' output bases are collinear. Orthogonalize sequentially and zero any
            // direction that vanishes: a later head's collinear component adds nothing.
            int zeroed = 0;
            for (int a = 0; a < total; a++)
            {
                long ao = (long)a * dModel;
                for (int b2 = 0; b2 < a; b2++)
                {
                    long bo = (long)b2 * dModel;
                    double dot = 0;
                    for (int j = 0; j < dModel; j++) dot += buf[ao + j] * buf[bo + j];
                    if (dot == 0) continue;
                    for (int j = 0; j < dModel; j++) buf[ao + j] -= dot * buf[bo + j];
                }
                double n2r = 0;
                for (int j = 0; j < dModel; j++) n2r += buf[ao + j] * buf[ao + j];
                if (n2r < 1e-12)
                {
                    for (int j = 0; j < dModel; j++) buf[ao + j] = 0;
                    zeroed++;
                    continue;
                }
                double invr = 1.0 / Math.Sqrt(n2r);
                for (int j = 0; j < dModel; j++) buf[ao + j] *= invr;
            }
            Console.WriteLine($"  head-allocation: native QR rc={rc} → managed MGS, {zeroed}/{total} collinear directions zeroed (M2 overlap removed)");
        }

        v = 0;
        foreach (var (key, f) in live)
        {
            var left = new float[(long)f.Rank * dModel];
            for (int r = 0; r < f.Rank; r++, v++)
                for (int j = 0; j < dModel; j++)
                    left[(long)r * dModel + j] = (float)(norms[v] * buf[(long)v * dModel + j]);
            fo[key] = f with { Left = left };
        }
    }

    internal static Factors Factor(double[] m, int d, int rankCap, double relTol, bool transpose, bool preserveSpectrum=false)
    {
        var a = new float[(long)d * d];
        for (int i = 0; i < d; i++)
            for (int j = 0; j < d; j++)
                a[(long)i * d + j] = (float)(transpose ? m[(long)j * d + i] : m[(long)i * d + j]);

        var u = new float[(long)d * d];
        var s = new float[d];
        var vt = new float[(long)d * d];
        nuint outRank = 0;
        int rc;
        unsafe
        {
            fixed (float* pa = a) fixed (float* pu = u) fixed (float* ps = s) fixed (float* pvt = vt)
                rc = SynInterop.TensorSvdTruncate(pa, (nuint)d, (nuint)d, relTol, &outRank, pu, ps, pvt, (nuint)d);
        }
        if (rc != 0) throw new InvalidOperationException($"tensor_svd_truncate rc={rc} (d={d})");
        int k = Math.Min((int)outRank, rankCap);

        double s0 = k > 0 && s[0] > 0f ? s[0] : 1.0;
        var left = new float[(long)k * d];
        var right = new float[(long)k * d];
        // Spectrum flattening: each side carries (s_r/s0)^(alpha/2), so the
        // reconstructed direction r scales as (s_r/s0)^alpha.
        float halfAlpha = (float)(FoundryDefaults.FactorSpectrumAlpha / 2.0);
        for (int r = 0; r < k; r++)
        {
            float sq = preserveSpectrum ? MathF.Sqrt(Math.Max(0f,s[r])) : MathF.Pow((float)(Math.Max(0f, s[r]) / s0), halfAlpha);
            for (int j = 0; j < d; j++)
            {
                left[(long)r * d + j] = sq * u[(long)j * d + r];
                right[(long)r * d + j] = sq * vt[(long)r * d + j];
            }
        }



        double num = 0, den = 0;
        ulong rng = SplitMix(0x9E3779B97F4A7C15UL ^ (ulong)d);
        for (int t = 0; t < 512; t++)
        {
            int i = (int)(Next(ref rng) % (ulong)d);
            int j = (int)(Next(ref rng) % (ulong)d);
            double approx = 0;
            for (int r = 0; r < k; r++) approx += (double)left[(long)r * d + i] * right[(long)r * d + j];
            double exact = a[(long)i * d + j] / (preserveSpectrum?1:s0);
            num += (exact - approx) * (exact - approx);
            den += exact * exact;
        }
        double resid = den > 0 ? Math.Sqrt(num / den) : 0.0;
        return new Factors(left, right, k, d, resid, s0);
    }



    internal static unsafe void ApplyOperator(double[] basis,int vocab,int width,double[] value,double scale,double[] accumulator)
    {
        int rc;
        fixed(double* r=basis,v=value,a=accumulator)
            rc=SynInterop.OperatorApply(r,(nuint)vocab,(nuint)width,v,scale,a);
        if(rc!=0) throw new InvalidOperationException($"operator application failed ({rc})");
    }

    internal static Factors HeadFactors(Factors factors,IReadOnlyList<OperatorSpec> heads,int index,int capacity)
    {
        int copies=heads.Count(h=>h.Key==heads[index].Key);
        int ordinal=heads.Take(index).Count(h=>h.Key==heads[index].Key);
        int count=Math.Min(capacity,Math.Max(0,(factors.Rank-ordinal+copies-1)/copies));
        var left=new float[count*factors.Dim];var right=new float[count*factors.Dim];
        for(int i=0;i<count;i++) {
            Array.Copy(factors.Left,(ordinal+i*copies)*factors.Dim,left,i*factors.Dim,factors.Dim);
            Array.Copy(factors.Right,(ordinal+i*copies)*factors.Dim,right,i*factors.Dim,factors.Dim);
        }
        return factors with {Rank=count,Left=left,Right=right};
    }

    internal static void FillRows(float[] vals, int rows, int cols, Factors f, double scale)
    {
        int k = Math.Min(f.Rank, rows);
        for (int r = 0; r < k; r++)
            for (int j = 0; j < cols && j < f.Dim; j++)
                vals[(long)r * cols + j] = (float)(scale * f.Left[(long)r * f.Dim + j]);
    }

    internal static void FillRowsRight(float[] vals, int rows, int cols, Factors f, double scale)
    {
        int k = Math.Min(f.Rank, rows);
        for (int r = 0; r < k; r++)
            for (int j = 0; j < cols && j < f.Dim; j++)
                vals[(long)r * cols + j] = (float)(scale * f.Right[(long)r * f.Dim + j]);
    }

    internal static void FillCols(float[] vals, int rows, int cols, Factors f, double scale)
    {
        int k = Math.Min(f.Rank, cols);
        for (int r = 0; r < k; r++)
            for (int i = 0; i < rows && i < f.Dim; i++)
                vals[(long)i * cols + r] = (float)(scale * f.Left[(long)r * f.Dim + i]);
    }




    /// Rotary pair 0 (head components 0 and hd/2) rotates at frequency 1 whatever
    /// freq_base is. When skipping, factor row r maps to component r+1 (r+2 past the
    /// pair partner) so content operators stay out of that plane. Q and K use the same
    /// mapping because their components pair in the dot product; V is not rotated.
    private static int RotarySafeComponent(int r, int headDim, bool skip)
        => !skip ? r : (r + 1 < headDim / 2 ? r + 1 : r + 2);

    internal static void FillHead(float[] vals, int rows, int cols, int headIdx, int headDim, Factors f, double scale, bool skipRotaryPair0 = false)
    {
        int baseRow = headIdx * headDim;
        if (baseRow >= rows) return;
        int k = Math.Min(f.Rank, skipRotaryPair0 ? headDim - 2 : headDim);
        for (int r = 0; r < k; r++)
        {
            int c = RotarySafeComponent(r, headDim, skipRotaryPair0);
            if (baseRow + c >= rows) break;
            long dst = (long)(baseRow + c) * cols;
            for (int j = 0; j < cols && j < f.Dim; j++)
                vals[dst + j] = (float)(scale * f.Left[(long)r * f.Dim + j]);
        }
    }

    internal static void FillHeadRight(float[] vals, int rows, int cols, int headIdx, int headDim, Factors f, double scale, bool skipRotaryPair0 = false)
    {
        int baseRow = headIdx * headDim;
        if (baseRow >= rows) return;
        int k = Math.Min(f.Rank, skipRotaryPair0 ? headDim - 2 : headDim);
        for (int r = 0; r < k; r++)
        {
            int c = RotarySafeComponent(r, headDim, skipRotaryPair0);
            if (baseRow + c >= rows) break;
            long dst = (long)(baseRow + c) * cols;
            for (int j = 0; j < cols && j < f.Dim; j++)
                vals[dst + j] = (float)(scale * f.Right[(long)r * f.Dim + j]);
        }
    }



    internal static void FillColsHead(float[] vals, int rows, int cols, int headIdx, int headDim, Factors f, double scale)
    {
        int baseCol = headIdx * headDim;
        if (baseCol >= cols) return;
        int k = Math.Min(f.Rank, headDim);
        for (int r = 0; r < k && (baseCol + r) < cols; r++)
            for (int i = 0; i < rows && i < f.Dim; i++)
                vals[(long)i * cols + (baseCol + r)] = (float)(scale * f.Left[(long)r * f.Dim + i]);
    }






    internal static bool IsContinuationOperator(string opKey) =>
        opKey is "context" or "trajectory" or "sentence_order" or "relation:PRECEDES";

    internal static void FillHeadZero(float[] vals, int rows, int cols, int headIdx, int headDim)
    {
        int baseRow = headIdx * headDim;
        for (int r = 0; r < headDim && (baseRow + r) < rows; r++)
        {
            long dst = (long)(baseRow + r) * cols;
            for (int j = 0; j < cols; j++) vals[dst + j] = 0f;
        }
    }
    internal static void FillHeadIdentity(float[] vals, int rows, int cols, int headIdx, int headDim)
    {
        int baseRow = headIdx * headDim;
        for (int r = 0; r < headDim && (baseRow + r) < rows; r++)
        {
            long dst = (long)(baseRow + r) * cols;
            for (int j = 0; j < cols; j++) vals[dst + j] = 0f;
            if (baseRow + r < cols) vals[dst + (baseRow + r)] = 1f;
        }
    }
    internal static void FillColsHeadIdentity(float[] vals, int rows, int cols, int headIdx, int headDim)
    {
        int baseCol = headIdx * headDim;
        for (int r = 0; r < headDim && (baseCol + r) < cols; r++)
            for (int i = 0; i < rows; i++)
                vals[(long)i * cols + (baseCol + r)] = (i == baseCol + r) ? 1f : 0f;
    }

    internal static void FillHeadIdentityScaled(float[] vals, int rows, int cols, int headIdx, int headDim, float scale)
    {
        int baseRow = headIdx * headDim;
        for (int r = 0; r < headDim && (baseRow + r) < rows; r++)
        {
            long dst = (long)(baseRow + r) * cols;
            for (int j = 0; j < cols; j++) vals[dst + j] = 0f;
            if (baseRow + r < cols) vals[dst + (baseRow + r)] = scale;
        }
    }

    /// Per-token highway (relation-band) masks for the content gate. When no stored
    /// mask is set, consensus.highway_mask_refresh recomputes them and they are re-read.
    internal static async Task<Mask256[]> FillHighwayMasksAsync(
        NpgsqlDataSource ds, Dictionary<Hash128, List<int>> tokenSlots, int vocabSize)
    {
        var vocab = VocabArray(tokenSlots);
        var masks = new Mask256[vocabSize];

        void Apply(IReadOnlyList<NpgsqlFoundryReads.HighwayMaskRow> rows)
        {
            foreach (var row in rows)
            {
                if (!tokenSlots.TryGetValue(FromBytes(row.EntityId), out var slots)) continue;
                var m = Mask256.FromByteArray(row.HighwayMask);
                foreach (int s in slots) if (s >= 0 && s < vocabSize) masks[s] = m;
            }
        }

        try
        {
            Apply(await NpgsqlFoundryReads.EntityHighwayMasksAsync(ds, vocab));
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
        {
            throw RequiredQueryUnavailable("entity_highway_masks", ex);
        }

        // All-zero masks: consensus.highway_mask_refresh derives them over this vocab,
        // then they are re-read.
        bool anyStored = false;
        foreach (var m in masks) if (!m.IsZero) { anyStored = true; break; }
        if (!anyStored)
        {
            long refreshed;
            try
            {
                await using var conn = await ds.OpenConnectionAsync();
                var scalar = await NpgsqlFoundryReads.HighwayMaskRefreshAsync(conn, vocab);
                refreshed = Convert.ToInt64(scalar ?? 0L);
            }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42883")
            {
                throw RequiredQueryUnavailable("highway_mask_refresh", ex);
            }

            if (refreshed > 0)
                Apply(await NpgsqlFoundryReads.EntityHighwayMasksAsync(ds, vocab));
            Console.WriteLine($"  highway masks: recomputed via consensus.highway_mask_refresh({refreshed:N0} entities updated; stored masks were empty)");
        }
        return masks;
    }


    /// Gate rows keyed on per-band centroids: FFN block b opens for tokens aligned with
    /// band b's membership centroid, silu(gateZ·cos + floorZ). A band with no centroid
    /// gets a constant-open bias column.
    internal static void FillGateBanded(
        float[] vals, int rows, int cols, double[]?[] bandCentroids, double gateZ, double floorZ)
    {
        // gateZ/floorZ are logit units. For unit-content rows with bias 1, RMSNorm gives
        // h ≈ x·√(d/2), so weights scale by 1/√(d/2). Every token gets at least
        // silu(≈floorZ) through the bias column.
        double s = 1.0 / Math.Sqrt(cols / 2.0);
        int bands = bandCentroids.Length;
        for (int r = 0; r < rows; r++)
        {
            int b = (int)((long)r * bands / rows);
            var c = bandCentroids[b];
            if (c is null)
            {
                vals[(long)r * cols + (cols - 1)] = (float)(gateZ * s); // no centroid: constant-open
                continue;
            }
            for (int j = 0; j < cols - 1 && j < c.Length; j++)
                vals[(long)r * cols + j] = (float)(gateZ * s * c[j]);
            vals[(long)r * cols + (cols - 1)] = (float)(floorZ * s);
        }
    }

    internal static void FillGate(float[] vals, int rows, int cols, double gateCol)
    {
        for (int r = 0; r < rows; r++)
            vals[(long)r * cols + (cols - 1)] = (float)gateCol;
    }






    internal static void FillCoordHead(float[] vals, int rows, int cols, int headDim, int coordDims, double scale)
    {
        if (headDim <= 0) return;
        int nh = rows / headDim;
        for (int h = 0; h < nh; h++)
            for (int d = 0; d < coordDims && d < headDim && d < cols; d++)
                vals[(long)(h * headDim + d) * cols + d] = (float)scale;
    }

    internal static double Silu(double z) => z / (1.0 + Math.Exp(-z));



    internal static byte[] ToBf16Bytes(float[] data)
    {
        var o = new byte[(long)data.Length * 2];
        for (long i = 0; i < data.LongLength; i++)
        {
            uint bits = BitConverter.SingleToUInt32Bits(data[i]);
            ushort bf = (ushort)(bits >> 16);
            o[i * 2] = (byte)bf;
            o[i * 2 + 1] = (byte)(bf >> 8);
        }
        return o;
    }

    internal static byte[] ToF32Bytes(float[] data)
    {
        var o = new byte[(long)data.Length * 4];
        Buffer.BlockCopy(data, 0, o, 0, o.Length);
        return o;
    }



    private static ulong SplitMix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static ulong Next(ref ulong state)
    {
        state = SplitMix(state);
        return state;
    }

    private static double Gaussian(ref ulong state)
    {
        double u1 = (Next(ref state) >> 11) * (1.0 / 9007199254740992.0);
        double u2 = (Next(ref state) >> 11) * (1.0 / 9007199254740992.0);
        if (u1 < 1e-300) u1 = 1e-300;
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    internal static unsafe Hash128 FromBytes(byte[] bts)
    {
        if (bts.Length < 16) return Hash128.Zero;
        fixed (byte* p = bts) return *(Hash128*)p;
    }
}
