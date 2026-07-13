namespace Laplace.Engine.Core;

/// <summary>
/// Reconcile MKL/TBB/native thread counts from Intel hybrid topology (Rule #12).
/// env.cmd may seed fallbacks for non-CLI tools; the Laplace CLI always wins here.
/// </summary>
public static class NativeRuntimeEnv
{
    public static void ApplyFromTopologyIfUnset() => ApplyFromTopology(force: false);

    public static void ApplyFromTopology(bool force = true)
    {
        int pThreads = Math.Max(1, CpuTopology.ResolveCpuBoundWorkers(headroom: 0));
        int gcHeaps = Math.Clamp(CpuTopology.PerformanceCoreCount, 1, 16);

        SetThreadVar("MKL_NUM_THREADS", pThreads, force);
        SetThreadVar("TBB_NUM_THREADS", pThreads, force);
        SetThreadVar("LAPLACE_NATIVE_THREADS", pThreads, force);
        // MKL_DYNAMIC=0 is deliberate on BOTH counts, and the pairing is load-bearing:
        //  - REQUIRED for determinism: a FIXED MKL thread count keeps BLAS/LAPACK reduction
        //    order reproducible, so foundry eigenmaps/DGEMM are bit-identical run to run.
        //    MKL_DYNAMIC=1 lets MKL vary the count -> non-deterministic FP reductions ->
        //    breaks the rock's content-addressing. Do NOT flip this to 1.
        //  - SAFE (no oversubscription) ONLY under this INVARIANT: MKL/Eigen/TBB are reached
        //    exclusively through Dynamics/Synthesis NativeInterop (the foundry/export path),
        //    called from single-threaded orchestration -- NEVER nested inside a pinned ingest
        //    worker. The ingest RunPinned* regions (compose in IngestDescentFlush, the glicko
        //    fold in NpgsqlConsensusFold, apply/COPY in NpgsqlWorkingSetApply) call only
        //    custom laplace_core C (glicko2/hash/geometry), never MKL. If you ever invoke an
        //    MKL/Eigen kernel from inside a RunPinned* body, set MKL threads to 1 for that
        //    region FIRST -- otherwise pThreads-per-worker x workers is real oversubscription.
        SetThreadVar("MKL_DYNAMIC", 0, force: true);

        if (force || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_GCHeapCount")))
            Environment.SetEnvironmentVariable("DOTNET_GCHeapCount", gcHeaps.ToString());

        Console.Error.WriteLine(
            "native_runtime: source={0} hybrid={1} p_physical={2} mkl/tbb/native_threads={3} gc_heaps={4}",
            CpuTopology.DetectionSource,
            CpuTopology.IsHybrid.ToString().ToLowerInvariant(),
            CpuTopology.PerformanceCoreCount,
            pThreads,
            gcHeaps);
    }

    private static void SetThreadVar(string name, int value, bool force)
    {
        if (!force && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            return;
        Environment.SetEnvironmentVariable(name, value.ToString());
    }
}
