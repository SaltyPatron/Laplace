using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public static class IngestParallelism
{
    public static int ResolveFileWorkers() =>
        CpuTopology.ResolveCpuBoundWorkers();

    public static int ResolveComposeWorkers() =>
        CpuTopology.ResolveCpuBoundWorkers();
}
