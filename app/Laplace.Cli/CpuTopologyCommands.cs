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
                    Console.WriteLine(CpuTopology.ResolveCpuBoundWorkers(headroom: headroom));
                    return 0;
                case "--io-bound-workers":
                    Console.WriteLine(CpuTopology.ResolveIngestCommitWorkers(headroom: 1));
                    return 0;
                case "--ingest-commit-workers":
                    Console.WriteLine(CpuTopology.ResolveIngestCommitWorkers(headroom: 1));
                    return 0;
                case "--p-core-indices":
                    Console.WriteLine(string.Join(",", CpuTopology.PerformanceCoreCpuIndices));
                    return 0;
                case "--e-core-indices":
                    Console.WriteLine(string.Join(",", CpuTopology.EfficientCoreCpuIndices));
                    return 0;
                case "--verify-pin":
                {
                    bool pinned = CpuTopology.PinCurrentThreadToPerformanceCores();
                    Console.WriteLine(
                        $"pin_applied={pinned.ToString().ToLowerInvariant()} "
                        + $"source={CpuTopology.DetectionSource} "
                        + $"p_primary_lps=[{string.Join(",", CpuTopology.PerformanceCoreCpuIndices)}]");
                    return pinned ? 0 : 2;
                }
            }
        }

        Console.WriteLine(
            $"source={CpuTopology.DetectionSource} "
            + $"hybrid={CpuTopology.IsHybrid.ToString().ToLowerInvariant()} "
            + $"p_physical={CpuTopology.PerformanceCoreCount} "
            + $"p_logical={CpuTopology.PerformanceLogicalProcessorCount} "
            + $"e_cores={CpuTopology.EfficientCoreCount} "
            + $"logical={CpuTopology.LogicalProcessorCount} "
            + $"p_primary_lps=[{string.Join(",", CpuTopology.PerformanceCoreCpuIndices)}] "
            + $"e_lps=[{string.Join(",", CpuTopology.EfficientCoreCpuIndices)}] "
            + $"cpu_bound_workers={CpuTopology.ResolveCpuBoundWorkers(headroom: 1)} "
            + $"io_bound_workers={CpuTopology.ResolveIngestCommitWorkers(headroom: 1)} "
            + $"apply_partitions={CpuTopology.ResolveApplyPartitions()}");

        bool pinOk = CpuTopology.PinCurrentThreadToPerformanceCores();
        Console.WriteLine($"entry_pin={pinOk.ToString().ToLowerInvariant()}");

        var topo = IngestTopology.EnsureReady();
        Console.Error.WriteLine(
            $"ingest_ready: file={topo.FileWorkers} compose={topo.ComposeWorkers} "
            + $"commit={topo.CommitWorkers} apply={topo.ApplyPartitions} pinned={topo.EntryThreadPinned.ToString().ToLowerInvariant()}");
        return 0;
    }
}
