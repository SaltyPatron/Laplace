using System.Text.Json;
using Laplace.Engine.Synthesis;                          // ProjectQkLayer
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// β coherence-floor probe (plan gate G2.0) — DB-free. On a real circuit
/// (layer-0 head-0 QK) it projects Left/Right once (the same ProjectQkLayer the
/// ETL uses), materializes the FULL contracted operator for a sample of subject
/// rows at θ=0 via <see cref="DynInterop.BilinearEdgesTile"/>, then sweeps
/// DATA-DERIVED thresholds in-process and reports edge cardinality vs RETRIEVAL
/// fidelity (recall@K of each subject's top-K neighbours by |M|).
///
/// The point: β is a global RETRIEVAL-fidelity budget; the per-circuit θ is read
/// off the measured curve (the largest θ holding recall ≥ β). That is NOT an
/// a-priori energy %/top-k — the data fixes the threshold, you fix the fidelity.
/// </summary>
public static class BetaProbe
{
    public static void Run(string modelDir, int sampleRows = 256, int topK = 32)
    {
        string st  = Path.Combine(modelDir, "model.safetensors");
        string cfg = Path.Combine(modelDir, "config.json");
        if (!File.Exists(st))  throw new FileNotFoundException($"no model.safetensors in {modelDir}");
        if (!File.Exists(cfg)) throw new FileNotFoundException($"no config.json in {modelDir}");

        using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
        var c = doc.RootElement;
        int dModel = c.GetProperty("hidden_size").GetInt32();
        int nHeads = c.GetProperty("num_attention_heads").GetInt32();
        int nKv    = c.TryGetProperty("num_key_value_heads", out var kv) ? kv.GetInt32() : nHeads;
        int vocab  = c.GetProperty("vocab_size").GetInt32();
        int headDim = dModel / nHeads;

        var refMap = SafetensorsContainerParser.ParseHeader(st).ToDictionary(r => r.Name, r => r);
        float[] E  = LoadBF16AsF32(st, refMap["model.embed_tokens.weight"],              (long)vocab  * dModel);
        float[] qW = LoadBF16AsF32(st, refMap["model.layers.0.self_attn.q_proj.weight"], (long)nHeads * headDim * dModel);
        float[] kW = LoadBF16AsF32(st, refMap["model.layers.0.self_attn.k_proj.weight"], (long)nKv    * headDim * dModel);

        Console.WriteLine($"beta-probe {modelDir}");
        Console.WriteLine($"  dims: vocab={vocab} dModel={dModel} nHeads={nHeads} nKv={nKv} headDim={headDim}; sample={sampleRows} rows, K={topK}");

        var qc = new double[(long)vocab * nHeads * headDim];
        var kc = new double[(long)vocab * nKv    * headDim];
        unsafe
        {
            fixed (float* ep = E) fixed (float* qp = qW) fixed (float* kp = kW)
            fixed (double* qcp = qc) fixed (double* kcp = kc)
            {
                int rc = SynthInterop.ProjectQkLayer(
                    ep, (nuint)vocab, (nuint)dModel, qp, (nuint)nHeads, kp, (nuint)nKv,
                    (nuint)headDim, qcp, kcp);
                if (rc != 0) throw new InvalidOperationException($"project_qk_layer rc={rc}");
            }
        }

        // head-0 contiguous Left (sampled query rows) and Right (all key rows).
        int S = Math.Min(sampleRows, vocab);
        var left  = new double[(long)S * headDim];
        var right = new double[(long)vocab * headDim];
        for (int t = 0; t < S; t++)     Array.Copy(qc, (long)t * nHeads * headDim, left,  (long)t * headDim, headDim);
        for (int t = 0; t < vocab; t++) Array.Copy(kc, (long)t * nKv    * headDim, right, (long)t * headDim, headDim);

        // Emit the FULL head-0 operator for the sample once (θ=0 keeps every nonzero cell).
        long cap = (long)S * vocab;
        var rows = new int[cap]; var cols = new int[cap]; var vals = new double[cap];
        long count; int overflow;
        unsafe
        {
            fixed (double* lp = left) fixed (double* rp = right)
            fixed (int* orr = rows) fixed (int* occ = cols) fixed (double* ovv = vals)
            {
                nuint cnt; int ov;
                int rc = DynInterop.BilinearEdgesTile(
                    lp, 0, (nuint)S, rp, (nuint)vocab, (nuint)headDim, 0.0,
                    orr, occ, ovv, (nuint)cap, &cnt, &ov);
                if (rc != 0) throw new InvalidOperationException($"bilinear_edges_tile rc={rc}");
                count = (long)cnt; overflow = ov;
            }
        }
        Console.WriteLine($"  full operator (sample): {count:N0} nonzero cells over {S}×{vocab} = {(double)count / S:F0} keys/subject mean");
        if (overflow != 0) { Console.WriteLine("  WARNING: θ=0 emit overflowed; sample too large"); return; }

        // Per-row |M| sorted descending (edges are emitted row-major, cols ascending within a row).
        var perRowAbs = new List<double[]>(S);
        {
            long e = 0;
            for (int r = 0; r < S; r++)
            {
                long start = e;
                while (e < count && rows[e] == r) e++;
                var a = new double[e - start];
                for (long i = start; i < e; i++) a[i - start] = Math.Abs(vals[i]);
                Array.Sort(a); Array.Reverse(a);          // descending |M|
                perRowAbs.Add(a);
            }
        }

        // Data-derived θ candidates: percentiles of |M| over the whole sample.
        var flat = new double[count];
        for (long i = 0; i < count; i++) flat[i] = Math.Abs(vals[i]);
        Array.Sort(flat);
        double Pct(double p) => flat[Math.Clamp((long)(p / 100.0 * (count - 1)), 0, count - 1)];
        double[] thetas = { 0.0, Pct(50), Pct(90), Pct(99), Pct(99.9), Pct(99.99) };
        double[] pctLbl = { 0, 50, 90, 99, 99.9, 99.99 };

        Console.WriteLine($"  |M| range: min>0≈{flat[0]:E3}  p50={Pct(50):E3}  p99={Pct(99):E3}  max={flat[count-1]:E3}");
        Console.WriteLine("  θ(|M| pct) │ edges/subj p50 │ p99 │ max │ recall@K");
        for (int ti = 0; ti < thetas.Length; ti++)
        {
            double theta = thetas[ti];
            var kept = new int[S];
            double recallSum = 0.0;
            for (int r = 0; r < S; r++)
            {
                double[] a = perRowAbs[r];
                int nAbove = UpperCountAbove(a, theta);    // a is desc-sorted
                kept[r] = nAbove;
                int k = Math.Min(topK, a.Length);
                recallSum += k == 0 ? 1.0 : (double)Math.Min(k, nAbove) / k;
            }
            Array.Sort(kept);
            int p50 = kept[(int)(0.50 * (S - 1))], p99 = kept[(int)(0.99 * (S - 1))], mx = kept[S - 1];
            Console.WriteLine($"  {pctLbl[ti],6:0.##}%   │ {p50,12:N0} │ {p99,6:N0} │ {mx,6:N0} │ {recallSum / S:P2}");
        }
        Console.WriteLine("  → choose β = target recall@K; the per-circuit θ is the largest |M| percentile whose row above holds recall ≥ β.");
    }

    /* Count of descending-sorted entries strictly greater than theta. */
    private static int UpperCountAbove(double[] descSorted, double theta)
    {
        int lo = 0, hi = descSorted.Length;          // first index with value <= theta
        while (lo < hi) { int mid = (lo + hi) >> 1; if (descSorted[mid] > theta) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private static unsafe float[] LoadBF16AsF32(string path, SafetensorsContainerParser.TensorReference tref, long n)
    {
        byte[] raw = new byte[tref.DataLength];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: false))
        {
            fs.Seek(tref.AbsoluteDataStart, SeekOrigin.Begin);
            int total = 0;
            while (total < raw.Length)
            {
                int r = fs.Read(raw, total, raw.Length - total);
                if (r == 0) throw new IOException($"truncated tensor data ({tref.Name})");
                total += r;
            }
        }
        var outp = new float[n];
        fixed (byte* rp = raw)
        fixed (float* op = outp)
        {
            if (tref.Dtype == "BF16")
            {
                ushort* s = (ushort*)rp;
                for (long i = 0; i < n; i++)
                {
                    uint bits = (uint)s[i] << 16;
                    float f; Buffer.MemoryCopy(&bits, &f, 4, 4);
                    op[i] = f;
                }
            }
            else if (tref.Dtype == "F32") Buffer.MemoryCopy(rp, op, n * 4, raw.Length);
            else throw new NotSupportedException($"beta-probe: unsupported dtype {tref.Dtype} for {tref.Name}");
        }
        return outp;
    }
}
