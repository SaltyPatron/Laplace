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
    /// Records accumulated between descent-probe rounds. Large on purpose:
    /// probe round trips scale with (distinct ids / interval), never with
    /// row count — hot caches and the working-set absent cache make each
    /// distinct id probe at most once per working set regardless.
    /// </summary>
    public static readonly int ProbeIntervalRecords =
        int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_WS_PROBE_INTERVAL"), out var n) && n > 0
            ? n : 32_768;

    /// <summary>
    /// Safety valve, not a second architecture: when a working set's staged
    /// bytes exceed the budget it closes (one apply) and the next one opens
    /// through the same code path. Default 8 GiB.
    /// </summary>
    public static readonly long BudgetBytes =
        (long.TryParse(Environment.GetEnvironmentVariable("LAPLACE_WORKING_SET_BUDGET_MB"), out var mb) && mb > 0
            ? mb : 8_192) * 1024L * 1024L;
}
