using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public static class IngestParallelism
{
    public static int ResolveFileWorkers(int coreHeadroom = 2) =>
        CpuTopology.ResolveCpuBoundWorkers(headroom: coreHeadroom);

    public static int ResolveComposeWorkers(int coreHeadroom = 1) =>
        CpuTopology.ResolveCpuBoundWorkers(headroom: coreHeadroom);
}
