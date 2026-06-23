using Laplace.Engine.Core;

namespace Laplace.Cli;

internal static class CpuTopologyCommands
{
    public static int Run(string[] args)
    {
        int headroom = 2;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--p-cores":
                    Console.WriteLine(CpuTopology.PerformanceCoreCount);
                    return 0;
                case "--cpu-bound-workers":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int hr) && hr >= 0)
                    {
                        headroom = hr;
                        i++;
                    }
                    Console.WriteLine(CpuTopology.ResolveCpuBoundWorkers(headroom: headroom, maxCap: 16));
                    return 0;
                case "--io-bound-workers":
                    Console.WriteLine(CpuTopology.ResolveIoBoundWorkers(defaultCap: 8));
                    return 0;
                case "--p-core-indices":
                    Console.WriteLine(string.Join(",", CpuTopology.PerformanceCoreCpuIndices));
                    return 0;
                case "--verify-pin":
                {
                    bool pinned = CpuTopology.PinCurrentThreadToPerformanceCores();
                    Console.WriteLine(
                        $"pin_applied={pinned.ToString().ToLowerInvariant()} "
                        + $"p_core_indices=[{string.Join(",", CpuTopology.PerformanceCoreCpuIndices)}]");
                    return pinned ? 0 : 2;
                }
            }
        }

        Console.WriteLine(
            $"hybrid={CpuTopology.IsHybrid.ToString().ToLowerInvariant()} "
            + $"p_cores={CpuTopology.PerformanceCoreCount} "
            + $"e_cores={CpuTopology.EfficientCoreCount} "
            + $"logical={CpuTopology.LogicalProcessorCount} "
            + $"p_core_indices=[{string.Join(",", CpuTopology.PerformanceCoreCpuIndices)}] "
            + $"cpu_bound_workers={CpuTopology.ResolveCpuBoundWorkers(headroom: 1, maxCap: 16)} "
            + $"io_bound_workers={CpuTopology.ResolveIoBoundWorkers(defaultCap: 8)}");
        return 0;
    }
}
