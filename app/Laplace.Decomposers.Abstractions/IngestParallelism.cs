namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// File-level fan-out worker count for decomposers with multiple independent input files
/// (OMW per-language tab files, UD per-treebank conllu files, OpenSubtitles per-pair zips).
/// Single source of truth for parsing <c>LAPLACE_DECOMPOSE_WORKERS</c> so decomposers don't
/// each duplicate the parse with slightly different env vars/thresholds. Callers still choose
/// their own core headroom for the auto-scale fallback (UD's heavier per-unit row expansion
/// warrants more headroom than a plain tsv/zip fan-out).
/// </summary>
public static class IngestParallelism
{
    public static int ResolveFileWorkers(int coreHeadroom = 2) =>
        int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS"), out var w) && w > 0
            ? w
            : Math.Clamp(Environment.ProcessorCount - coreHeadroom, 1, 16);
}
