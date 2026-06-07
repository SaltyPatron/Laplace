using System.Diagnostics;
using Laplace.Engine.Synthesis;
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

public static class SvdExactBench
{
    const string DefaultTensor = "model.layers.0.self_attn.q_proj.weight";

    const double F64ExactTol = 1e-12;

    const float F32Epsilon       = 1.1920929e-07f;
    const double F32LosslessTol  = 1.0e3 * F32Epsilon;

    public static bool Run(string modelDir, string? tensorName = null)
    {
        string name = string.IsNullOrEmpty(tensorName) ? DefaultTensor : tensorName!;

        var refs   = SafetensorsContainerParser.ParseModel(modelDir);
        var refMap = refs.ToDictionary(r => r.Name, r => r);
        if (!refMap.TryGetValue(name, out var tref))
            throw new InvalidOperationException(
                $"tensor '{name}' not in {modelDir}; available e.g.: " +
                string.Join(", ", refs.Take(5).Select(r => r.Name)));

        if (tref.Shape.Length != 2)
            throw new InvalidOperationException(
                $"svd-exact-bench needs a 2-D matrix; '{name}' has shape [{string.Join(",", tref.Shape)}]");

        int m = tref.Shape[0];
        int n = tref.Shape[1];
        int k = Math.Min(m, n);
        long mn = (long)m * n;

        Console.WriteLine($"svd-exact-bench {modelDir}");
        Console.WriteLine($"  tensor : {name}");
        Console.WriteLine($"  dtype  : {tref.Dtype}   shape: [{m} x {n}]   min(m,n)={k}");

        float[] A = LoadBF16AsF32(tref);

        var U  = new float[(long)m * k];
        var S  = new float[k];
        var Vt = new float[(long)k * n];

        var sw = Stopwatch.StartNew();
        nuint outRank;
        int rc = SvdTruncate(A, (nuint)m, (nuint)n, 0.0, out outRank, U, S, Vt, (nuint)k);
        sw.Stop();

        if (rc == -2)
            throw new InvalidOperationException(
                "tensor_svd_truncate returned -2: LAPACK/MKL unavailable in the linked liblaplace_synthesis " +
                "(engine/synthesis/src/tensor_decompose.cpp:77). Cannot prove fp-exactness without it.");
        if (rc != 0)
            throw new InvalidOperationException($"tensor_svd_truncate rc={rc} (bad args / LAPACK info)");

        int r = (int)outRank;
        Console.WriteLine($"  SVD    : rank kept = {r} / {k}  (tol=0 ⇒ full thin-SVD)  in {sw.ElapsedMilliseconds:N0} ms");

        double sumSqA   = 0.0;
        double sumSqErr = 0.0;
        double maxAbs   = 0.0;
        for (int i = 0; i < m; i++)
        {
            long uRow  = (long)i * k;
            long aRow  = (long)i * n;
            for (int j = 0; j < n; j++)
            {
                double rec = 0.0;
                for (int t = 0; t < r; t++)
                    rec += (double)U[uRow + t] * (double)S[t] * (double)Vt[(long)t * n + j];

                double a = A[aRow + j];
                double d = a - rec;
                sumSqA   += a * a;
                sumSqErr += d * d;
                double ad = Math.Abs(d);
                if (ad > maxAbs) maxAbs = ad;
            }
        }

        double normA   = Math.Sqrt(sumSqA);
        double normErr = Math.Sqrt(sumSqErr);
        double relErr  = normA > 0.0 ? normErr / normA : normErr;
        double relInEps = relErr / F32Epsilon;

        Console.WriteLine($"  ‖A‖_F            = {normA:E6}");
        Console.WriteLine($"  ‖A − UΣVᵀ‖_F     = {normErr:E6}");
        Console.WriteLine($"  relative residual = {relErr:E6}   (= {relInEps:F1} × f32 ε)");
        Console.WriteLine($"  max abs residual  = {maxAbs:E6}");

        bool fullRank   = r == k;
        bool f32Lossless = fullRank && relErr <= F32LosslessTol;
        bool f64Exact    = relErr <= F64ExactTol;

        Console.WriteLine(f32Lossless
            ? $"  LOSSLESS (f32) — full rank {r}/{k}, residual {relErr:E3} ≤ {F32LosslessTol:E1} (f32 round-off floor)"
            : (!fullRank
                ? $"  NOT LOSSLESS — kernel dropped rank ({r}/{k}) at tol=0 (signal lost)"
                : $"  NOT LOSSLESS — residual {relErr:E3} > {F32LosslessTol:E1} (above f32 round-off; signal lost)"));

        Console.WriteLine(f64Exact
            ? $"  PASS (G2.1 f64) — relative residual {relErr:E3} ≤ {F64ExactTol:E0}"
            : $"  FAIL (G2.1 f64) — relative residual {relErr:E3} > {F64ExactTol:E0}: kernel is single-precision " +
              $"(LAPACKE_sgesdd, f32 buffers; tensor_decompose.cpp:37) — cannot reach 1e-12 by construction");

        return f32Lossless;
    }

    static unsafe int SvdTruncate(
        float[] a, nuint m, nuint n, double tol,
        out nuint outRank, float[] u, float[] s, float[] vt, nuint kmax)
    {
        nuint r;
        int rc;
        fixed (float* ap = a)
        fixed (float* up = u)
        fixed (float* sp = s)
        fixed (float* vtp = vt)
        {
            rc = SynthInterop.TensorSvdTruncate(ap, m, n, tol, &r, up, sp, vtp, kmax);
        }
        outRank = r;
        return rc;
    }

    static unsafe float[] LoadBF16AsF32(SafetensorsContainerParser.TensorReference tref)
    {
        long n = 1;
        foreach (var d in tref.Shape) n *= d;

        byte[] raw = new byte[tref.DataLength];
        using (var fs = new FileStream(tref.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: false))
        {
            fs.Seek(tref.AbsoluteDataStart, SeekOrigin.Begin);
            int total = 0;
            while (total < raw.Length)
            {
                int rd = fs.Read(raw, total, raw.Length - total);
                if (rd == 0) throw new IOException($"truncated tensor data ({tref.Name})");
                total += rd;
            }
        }

        var outp = new float[n];
        fixed (byte* rp = raw)
        fixed (float* op = outp)
        {
            if (tref.Dtype == "BF16")
            {
                ushort* src = (ushort*)rp;
                for (long i = 0; i < n; i++)
                {
                    uint bits = (uint)src[i] << 16;
                    float f;
                    Buffer.MemoryCopy(&bits, &f, 4, 4);
                    op[i] = f;
                }
            }
            else if (tref.Dtype == "F32")
            {
                Buffer.MemoryCopy(rp, op, n * 4, raw.Length);
            }
            else throw new NotSupportedException($"svd-exact-bench: unsupported dtype {tref.Dtype} for {tref.Name}");
        }
        return outp;
    }
}
