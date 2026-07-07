using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Rule #8 working-set switches (06_Engineering_Ruleset.txt). The working
/// set — not the 1024-record chunk — is the unit of dedup, descent, write,
/// and fold.
/// </summary>
public static class WorkingSetMode
{
    public static readonly bool Enabled = true;

    public static readonly int ProbeIntervalRecords =
        IngestSizing.ResolveWorkingSetProbeInterval(
            IngestSizing.ResolveRecordBatch(CpuTopology.PerformanceCoreCount),
            IngestSourceProfile.Default);

    public static readonly long BudgetBytes = IngestSizing.ResolveWorkingSetBudgetBytes();
}
