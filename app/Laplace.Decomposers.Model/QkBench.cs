using System.Diagnostics;
using System.Text.Json;
using Laplace.Engine.Synthesis;                                   // QkPairF64, NativeInterop
using SynthInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Isolated QK-kernel profiler on REAL weights — no DB, no ingest harness, no emit.
/// Loads the embedding + layer-0 q/k, projects all heads once (the per-layer project
/// cost), scores a bounded window of head-0 query rows at the real noise floor (the
/// per-row score cost + survivor density), and extrapolates full-QK wall time.
///
/// Purpose: decide whether the QK bottleneck is the scalar compensated dot or weak
/// Cauchy-Schwarz pruning — in minutes, not an hours-long full ingest. If score time
/// is high while survivors/row is low, the prune is not cutting the vocab² (a pruning
/// problem); if survivors/row is high too, it is genuine scoring volume (a dot/SIMD
/// problem). The native calls are the identical kernels WeightTensorETL drives.
/// </summary>
public static class QkBench
{
    const double QkNoiseFloor = 0.05;   // identical to WeightTensorETL.QkNoiseFloor
    const int    SampleRows   = 2000;   // head-0 query-row window to time + extrapolate

    public static void Run(string modelDir)
    {
        string st  = Path.Combine(modelDir, "model.safetensors");
        string cfg = Path.Combine(modelDir, "config.json");
        if (!File.Exists(st))  throw new FileNotFoundException($"no model.safetensors in {modelDir}");
        if (!File.Exists(cfg)) throw new FileNotFoundException($"no config.json in {modelDir}");

        using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
        var c = doc.RootElement;
        int dModel  = c.GetProperty("hidden_size").GetInt32();
        int nHeads  = c.GetProperty("num_attention_heads").GetInt32();
        int nKv     = c.TryGetProperty("num_key_value_heads", out var kv) ? kv.GetInt32() : nHeads;
        int vocab   = c.GetProperty("vocab_size").GetInt32();
        int nLayers = c.GetProperty("num_hidden_layers").GetInt32();
        int headDim = dModel / nHeads;

        var refs   = SafetensorsContainerParser.ParseHeader(st);
        var refMap = refs.ToDictionary(r => r.Name, r => r);

        Console.WriteLine($"qk-bench {modelDir}");
        Console.WriteLine($"  dims: vocab={vocab} dModel={dModel} nHeads={nHeads} nKv={nKv} headDim={headDim} nLayers={nLayers} floor={QkNoiseFloor}");

        float[] E  = LoadBF16AsF32(st, refMap["model.embed_tokens.weight"],              (long)vocab  * dModel);
        float[] qW = LoadBF16AsF32(st, refMap["model.layers.0.self_attn.q_proj.weight"], (long)nHeads * headDim * dModel);
        float[] kW = LoadBF16AsF32(st, refMap["model.layers.0.self_attn.k_proj.weight"], (long)nKv    * headDim * dModel);

        var qCache = new double[(long)vocab * nHeads * headDim];
        var kCache = new double[(long)vocab * nKv    * headDim];

        var sw = Stopwatch.StartNew();
        ProjectQk(E, vocab, dModel, qW, nHeads, kW, nKv, headDim, qCache, kCache);
        long projMs = sw.ElapsedMilliseconds;

        int q1 = Math.Min(vocab, SampleRows);
        var buf = new QkPairF64[1 << 23];   // 128 MB scratch for the sampled rows
        sw.Restart();
        long surv = ScoreHead(qCache, nHeads, kCache, nKv, vocab, headDim, 0, 0, QkNoiseFloor, 0, q1, buf, out int of);
        long scoreMs = sw.ElapsedMilliseconds;

        double scorePerRowMs = (double)scoreMs / q1;
        double survPerRow    = (double)surv / q1;
        double scoreFullMs   = scorePerRowMs * vocab * nHeads * nLayers;
        double projFullMs    = (double)projMs * nLayers;

        Console.WriteLine($"  LAYER 0 project (all {nHeads} heads, full vocab): {projMs:N0} ms");
        Console.WriteLine($"  HEAD 0 score rows [0,{q1}): {scoreMs:N0} ms ({scorePerRowMs:F3} ms/row), survivors={surv:N0} ({survPerRow:F2}/row), overflow={of}");
        Console.WriteLine($"  EXTRAPOLATED full QK ({nLayers} layers x {nHeads} heads, full vocab):");
        Console.WriteLine($"    project ~{projFullMs / 1000:N1} s | score ~{scoreFullMs / 60000:N1} min | total ~{(projFullMs + scoreFullMs) / 60000:N1} min");
        if (of != 0)
            Console.WriteLine($"  WARNING: sampled rows overflowed the {buf.Length:N0}-pair buffer; survivor/row is a floor and timing is partial.");
    }

    static unsafe void ProjectQk(
        float[] e, int vocab, int dModel, float[] qW, int nHeads, float[] kW, int nKv,
        int headDim, double[] qc, double[] kc)
    {
        fixed (float* ep = e)
        fixed (float* qp = qW)
        fixed (float* kp = kW)
        fixed (double* qcp = qc)
        fixed (double* kcp = kc)
        {
            int rc = SynthInterop.ProjectQkLayer(
                ep, (nuint)vocab, (nuint)dModel, qp, (nuint)nHeads, kp, (nuint)nKv,
                (nuint)headDim, qcp, kcp);
            if (rc != 0) throw new InvalidOperationException($"project_qk_layer rc={rc}");
        }
    }

    static unsafe long ScoreHead(
        double[] qc, int nHeads, double[] kc, int nKv, int vocab, int headDim,
        int head, int kvHead, double floor, int q0, int q1, QkPairF64[] buf, out int overflow)
    {
        int of;
        long n;
        fixed (double* qcp = qc)
        fixed (double* kcp = kc)
        fixed (QkPairF64* bp = buf)
        {
            n = SynthInterop.ScoreQkHeadCached(
                qcp, (nuint)nHeads, kcp, (nuint)nKv, (nuint)vocab, (nuint)headDim,
                (nuint)head, (nuint)kvHead, floor, (nuint)q0, (nuint)q1, bp, (nuint)buf.Length, &of);
        }
        overflow = of;
        if (n < 0) throw new InvalidOperationException("score_qk_head_cached rc=-1");
        return n;
    }

    static unsafe float[] LoadBF16AsF32(string path, SafetensorsContainerParser.TensorReference tref, long n)
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
                    float f;
                    Buffer.MemoryCopy(&bits, &f, 4, 4);
                    op[i] = f;
                }
            }
            else if (tref.Dtype == "F32")
            {
                Buffer.MemoryCopy(rp, op, n * 4, raw.Length);
            }
            else throw new NotSupportedException($"qk-bench: unsupported dtype {tref.Dtype} for {tref.Name}");
        }
        return outp;
    }
}
