namespace Laplace.Engine.Core;

/// <summary>
/// Propagate detected P-core count into MKL/TBB env before native DLL static init.
/// Scripts may pre-set MKL_NUM_THREADS; this only fills gaps from <see cref="CpuTopology"/>.
/// </summary>
public static class NativeRuntimeEnv
{
    public static void ApplyFromTopologyIfUnset()
    {
        int threads = Math.Max(1, CpuTopology.PerformanceCoreCount);
        SetIfUnset("MKL_NUM_THREADS", threads);
        SetIfUnset("TBB_NUM_THREADS", threads);
        SetIfUnset("LAPLACE_NATIVE_THREADS", threads);
    }

    private static void SetIfUnset(string name, int value)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            return;
        Environment.SetEnvironmentVariable(name, value.ToString());
    }
}
