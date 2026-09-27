namespace Laplace.Engine.Core;

/// <summary>
/// Set MKL/TBB/native thread counts and the GC heap count from the detected CPU topology.
/// With <c>force</c> the topology values overwrite anything already in the environment;
/// otherwise only unset variables are filled. MKL_DYNAMIC is always 0.
/// </summary>
public static class NativeRuntimeEnv
{
    public static void ApplyFromTopologyIfUnset() => ApplyFromTopology(force: false);

    public static void ApplyFromTopology(bool force = true)
    {
        int pThreads = Math.Max(1, CpuTopology.ResolveCpuBoundWorkers());
        int gcHeaps = Math.Max(1, CpuTopology.PerformanceCoreCount);

        SetThreadVar("MKL_NUM_THREADS", pThreads, force);
        SetThreadVar("TBB_NUM_THREADS", pThreads, force);
        SetThreadVar("LAPLACE_NATIVE_THREADS", pThreads, force);
        // MKL_DYNAMIC=0: a fixed MKL thread count keeps BLAS/LAPACK reduction order
        // reproducible, so export eigenmaps/DGEMM are bit-identical run to run and their
        // outputs stay content-addressable. A fixed count does not oversubscribe because
        // MKL/Eigen/TBB are reached only through Dynamics/Synthesis NativeInterop from
        // single-threaded orchestration; pinned ingest workers call only laplace_core C.
        // An MKL kernel inside a pinned worker region would need MKL threads set to 1 there.
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
