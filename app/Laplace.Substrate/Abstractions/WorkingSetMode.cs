using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Rule #8 working-set switches (06_Engineering_Ruleset.txt). The working
/// set — not the 1024-record chunk — is the unit of dedup, descent, write,
/// and fold. On by default; LAPLACE_WORKING_SET=0 restores per-batch
/// yielding for A/B comparison only (the per-batch lanes are scheduled for
/// deletion, not maintenance).
/// </summary>
public static class WorkingSetMode
{
    /// <summary>Working-set mode for all production ingest lanes.</summary>
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("LAPLACE_WORKING_SET") != "0";

    /// <summary>
    /// Records accumulated between descent-probe rounds when a pipeline config
    /// does not set <see cref="IngestBatchConfig.WorkingSetProbeInterval"/>.
    /// Scales with the default record batch and source profile; per-lane configs
    /// override this (relation triples probe more often — two trees per record).
    /// </summary>
    public static readonly int ProbeIntervalRecords =
        int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_WS_PROBE_INTERVAL"), out var n) && n > 0
            ? n
            : IngestSizing.ResolveWorkingSetProbeInterval(
                IngestSizing.ResolveRecordBatch(CpuTopology.PerformanceCoreCount),
                IngestSourceProfile.Default);

    /// <summary>
    /// Safety valve, not a second architecture: when a working set's staged
    /// bytes exceed the budget it closes (one apply) and the next one opens
    /// through the same code path. The default scales to the machine —
    /// staged-bytes estimate under-counts true resident cost ~2.5-3x (managed
    /// row objects, memo caches, the consensus accumulator, server-GC heap
    /// slack), and Postgres holds its own locked share of the same RAM. A
    /// fixed 8 GiB budget measured out as ~21 GB resident and drove a 48 GB
    /// box to 0.5 GB free mid-fold. Default is phys/32 capped at 2 GiB unless
    /// <c>LAPLACE_WORKING_SET_BUDGET_MB</c> is set explicitly.
    /// </summary>
    public static readonly long BudgetBytes =
        long.TryParse(Environment.GetEnvironmentVariable("LAPLACE_WORKING_SET_BUDGET_MB"), out var mb) && mb > 0
            ? mb * 1024L * 1024L
            : IngestSizing.ResolveWorkingSetBudgetBytes();
}
