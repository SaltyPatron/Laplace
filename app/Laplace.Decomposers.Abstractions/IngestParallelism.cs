using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// File-level fan-out for decomposers with multiple independent input files
/// (OMW per-language tab files, UD per-treebank conllu files, OpenSubtitles per-pair zips).
/// Counts come from <see cref="CpuTopology"/> only — no env vars, no script defaults.
/// </summary>
public static class IngestParallelism
{
    public static int ResolveFileWorkers(int coreHeadroom = 2) =>
        CpuTopology.ResolveCpuBoundWorkers(headroom: coreHeadroom);

    public static int ResolveComposeWorkers(int coreHeadroom = 1) =>
        CpuTopology.ResolveCpuBoundWorkers(headroom: coreHeadroom);
}
