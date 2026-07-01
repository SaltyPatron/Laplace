namespace Laplace.Engine.Core;

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
