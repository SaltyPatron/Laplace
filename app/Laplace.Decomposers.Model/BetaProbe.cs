using System.Text.Json;
using Laplace.Engine.Synthesis;                          // ProjectQkLayer
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// β coherence-floor probe (plan gate G2.0) — DB-free. For a real circuit it
/// projects Left/Right once, materializes the FULL contracted operator
/// M = Left·Rightᵀ for a sample of subject rows at θ=0 via
/// <see cref="DynInterop.BilinearEdgesTile"/>, then sweeps DATA-DERIVED |M|
/// percentile thresholds in-process and reports edge cardinality (p50/p99/max
/// per subject) vs RETRIEVAL fidelity (recall@K of each subject's top-K
/// neighbours). The per-circuit verdict — DIFFUSE (no θ sparsifies it while
/// holding retrieval → factored-operator/geometry surface) vs SPARSE
/// (a θ keeps recall ≥ β at a bounded edge count → materialize edges) — is what
/// gates how L2 ingests each kind. β is read off the curve, never an a-priori cut.
///
/// Circuits: "qk" (E·Wq·Wkᵀ·Eᵀ, layer-0 head-0), "ffn" ((E·Wup)·(E_U·Wdown)ᵀ,
/// layer-0 — the COMPLETES_TO kind the plan expects to be sparse).
/// </summary>
public static class BetaProbe
{
    public static void Run(string modelDir, string circuit = "qk", int sampleRows = 256, int topK = 32)
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
        int dFf    = c.GetProperty("intermediate_size").GetInt32();
        int headDim = dModel / nHeads;

        var refMap = SafetensorsContainerParser.ParseHeader(st).ToDictionary(r => r.Name, r => r);
        float[] E = LoadBF16AsF32(st, refMap["model.embed_tokens.weight"], (long)vocab * dModel);
        // Unembedding: separate lm_head if present, else tied to E.
        float[] EU = refMap.ContainsKey("lm_head.weight")
            ? LoadBF16AsF32(st, refMap["lm_head.weight"], (long)vocab * dModel)
            : E;

        int S = Math.Min(sampleRows, vocab);
        Console.WriteLine($"beta-probe [{circuit}] {modelDir}");
        Console.WriteLine($"  dims: vocab={vocab} dModel={dModel} nHeads={nHeads} nKv={nKv} headDim={headDim} dFf={dFf}; " +
                          $"sample={S} rows, K={topK}, E_U={(ReferenceEquals(EU, E) ? "tied" : "lm_head")}");

        double[] left; double[] right; int r;
        switch (circuit.ToLowerInvariant())
        {
            case "qk": (left, right, r) = ProjectQk(E, vocab, dModel, nHeads, nKv, headDim, S, refMap, st); break;
            case "ffn": (left, right, r) = ProjectFfn(E, EU, vocab, dModel, dFf, S, refMap, st); break;
            default: throw new ArgumentException($"unknown circuit '{circuit}' (qk|ffn)");
        }

        AnalyzeAndReport(left, S, right, vocab, r, topK);
    }

    // ── QK: layer-0 head-0, M = Q·Kᵀ where Q = E·Wq, K = E·Wk (per ProjectQkLayer). ──
    private static (double[] left, double[] right, int r) ProjectQk(
        float[] E, int vocab, int dModel, int nHeads, int nKv, int headDim, int S,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string st)
    {
        float[] qW = LoadBF16AsF32(st, refMap["model.layers.0.self_attn.q_proj.weight"], (long)nHeads * headDim * dModel);
        float[] kW = LoadBF16AsF32(st, refMap["model.layers.0.self_attn.k_proj.weight"], (long)nKv    * headDim * dModel);
        var qc = new double[(long)vocab * nHeads * headDim];
        var kc = new double[(long)vocab * nKv    * headDim];
        unsafe
        {
            fixed (float* ep = E) fixed (float* qp = qW) fixed (float* kp = kW)
            fixed (double* qcp = qc) fixed (double* kcp = kc)
            {
                int rc = SynthInterop.ProjectQkLayer(ep, (nuint)vocab, (nuint)dModel, qp, (nuint)nHeads,
                                                     kp, (nuint)nKv, (nuint)headDim, qcp, kcp);
                if (rc != 0) throw new InvalidOperationException($"project_qk_layer rc={rc}");
            }
        }
        var left  = new double[(long)S * headDim];          // sampled query rows, head 0
        var right = new double[(long)vocab * headDim];      // all key rows, kv-head 0
        for (int t = 0; t < S; t++)     Array.Copy(qc, (long)t * nHeads * headDim, left,  (long)t * headDim, headDim);
        for (int t = 0; t < vocab; t++) Array.Copy(kc, (long)t * nKv    * headDim, right, (long)t * headDim, headDim);
        return (left, right, headDim);
    }

    // ── FFN: M = (E·Wup)·(E_U·Wdown)ᵀ, inner rank = dFf (gate nonlinearity is runtime, not attested). ──
    private static (double[] left, double[] right, int r) ProjectFfn(
        float[] E, float[] EU, int vocab, int dModel, int dFf, int S,
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string st)
    {
        float[] up   = LoadBF16AsF32(st, refMap["model.layers.0.mlp.up_proj.weight"],   (long)dFf * dModel);     // [dFf × dModel]
        float[] down = LoadBF16AsF32(st, refMap["model.layers.0.mlp.down_proj.weight"], (long)dModel * dFf);     // [dModel × dFf]
        // Right = E_U·Wdown needs Wdownᵀ [dFf × dModel] (project_embedding computes pts·Wᵀ).
        var downT = new float[(long)dFf * dModel];
        for (int o = 0; o < dModel; o++)
            for (int f = 0; f < dFf; f++)
                downT[(long)f * dModel + o] = down[(long)o * dFf + f];

        var left  = new double[(long)S * dFf];
        var right = new double[(long)vocab * dFf];
        unsafe
        {
            fixed (float* ep = E) fixed (float* upp = up) fixed (double* lp = left)
            {   // Left = E[:S] · Wupᵀ  →  [S × dFf]
                if (DynInterop.ProjectEmbedding(ep, (nuint)S, (nuint)dModel, upp, (nuint)dFf, lp) != 0)
                    throw new InvalidOperationException("project_embedding(up) failed");
            }
            fixed (float* eup = EU) fixed (float* dtp = downT) fixed (double* rp = right)
            {   // Right = E_U · Wdown  →  [vocab × dFf]
                if (DynInterop.ProjectEmbedding(eup, (nuint)vocab, (nuint)dModel, dtp, (nuint)dFf, rp) != 0)
                    throw new InvalidOperationException("project_embedding(down) failed");
            }
        }
        return (left, right, dFf);
    }

    // ── Sweep + report: θ=0 emit once, then count edges + recall@K per data-derived θ. ──
    private static void AnalyzeAndReport(double[] left, int S, double[] right, int V, int r, int topK)
    {
        long cap = (long)S * V;
        var rows = new int[cap]; var cols = new int[cap]; var vals = new double[cap];
        long count; int overflow;
        unsafe
        {
            fixed (double* lp = left) fixed (double* rp = right)
            fixed (int* orr = rows) fixed (int* occ = cols) fixed (double* ovv = vals)
            {
                nuint cnt; int ov;
                int rc = DynInterop.BilinearEdgesTile(lp, 0, (nuint)S, rp, (nuint)V, (nuint)r, 0.0,
                                                      orr, occ, ovv, (nuint)cap, &cnt, &ov);
                if (rc != 0) throw new InvalidOperationException($"bilinear_edges_tile rc={rc}");
                count = (long)cnt; overflow = ov;
            }
        }
        Console.WriteLine($"  full operator (sample): {count:N0} nonzero cells over {S}×{V} = {(double)count / S:F0} keys/subject mean");
        if (overflow != 0) { Console.WriteLine("  WARNING: θ=0 emit overflowed; sample too large"); return; }

        var perRowAbs = new List<double[]>(S);
        {
            long e = 0;
            for (int rr = 0; rr < S; rr++)
            {
                long start = e;
                while (e < count && rows[e] == rr) e++;
                var a = new double[e - start];
                for (long i = start; i < e; i++) a[i - start] = Math.Abs(vals[i]);
                Array.Sort(a); Array.Reverse(a);
                perRowAbs.Add(a);
            }
        }

        // Arena scale M = RMS of |M| over the sample (the §10 tanh scale): an edge is
        // SIGNAL (a win/loss that moves μ) when |m| ≳ M, a DRAW (no win/loss, skip — not
        // an a-priori amount) when |m| ≪ M. The materialize cut is θ relative to M.
        double sumsq = 0.0;
        for (long i = 0; i < count; i++) { double a = Math.Abs(vals[i]); sumsq += a * a; }
        double mRms = Math.Sqrt(sumsq / count);
        Console.WriteLine($"  arena scale M (RMS|M|) = {mRms:E3}");
        Console.WriteLine("  θ      │ edges/subj p50 │ p99 │ max │ strong-subj │ recall@K (strong subjects only)");
        double[] mult = { 0.0, 1.0, 2.0, 3.0 };
        string[] lbl  = { "0", "M", "2M", "3M" };
        for (int ti = 0; ti < mult.Length; ti++)
        {
            double theta = mult[ti] * mRms;
            var kept = new int[S];
            int strongSubjects = 0; double recallStrongSum = 0.0;
            for (int rr = 0; rr < S; rr++)
            {
                double[] a = perRowAbs[rr];
                int nAbove = UpperCountAbove(a, theta);
                kept[rr] = nAbove;
                // A subject has real (non-draw) retrieval iff its TOP neighbour is signal.
                if (a.Length > 0 && a[0] > theta)
                {
                    strongSubjects++;
                    int k = Math.Min(topK, a.Length);
                    recallStrongSum += (double)Math.Min(k, nAbove) / k;
                }
            }
            Array.Sort(kept);
            int p50 = kept[(int)(0.50 * (S - 1))], p99 = kept[(int)(0.99 * (S - 1))], mx = kept[S - 1];
            string recall = strongSubjects > 0 ? (recallStrongSum / strongSubjects).ToString("P2") : "n/a";
            Console.WriteLine($"  {lbl[ti],-5} │ {p50,12:N0} │ {p99,6:N0} │ {mx,6:N0} │ {strongSubjects,5}/{S} │ {recall}");
        }
        Console.WriteLine("  → MATERIALIZE the strong tail: signal edges (|m| ≳ M) per subject are bounded;");
        Console.WriteLine("    draws (|m| ≪ M) are skipped natively (no win/loss). Diffuse = large-but-bounded balloon, not infeasible.");
    }

    private static int UpperCountAbove(double[] descSorted, double theta)
    {
        int lo = 0, hi = descSorted.Length;
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
