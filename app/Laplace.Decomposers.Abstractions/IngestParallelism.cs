using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// File-level fan-out worker count for decomposers with multiple independent input files
/// (OMW per-language tab files, UD per-treebank conllu files, OpenSubtitles per-pair zips).
/// Single source of truth for file-level fan-out worker count. When
/// <c>LAPLACE_DECOMPOSE_WORKERS</c> is unset, auto-scales via <see cref="CpuTopology.ResolveCpuBoundWorkers"/>.
/// Only an explicit user env var (including <c>1</c> for serial) overrides auto-scale — scripts never
/// default this to 1.
/// </summary>
public static class IngestParallelism
{
    public static int ResolveFileWorkers(int coreHeadroom = 2) =>
        int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS"), out var w) && w > 0
            ? w
            : CpuTopology.ResolveCpuBoundWorkers(headroom: coreHeadroom, maxCap: 16);
}
