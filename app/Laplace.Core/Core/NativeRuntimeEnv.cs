namespace Laplace.Engine.Core;

public static class NativeRuntimeEnv
{
    public static void ApplyFromTopologyIfUnset()
    {
        // Thread counts are owned by CpuTopology / MKL defaults — no process env mutation.
    }
}
