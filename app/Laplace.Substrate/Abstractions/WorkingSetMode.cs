using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Rule #8 working-set switches (06_Engineering_Ruleset.txt). Per-source probe
/// intervals and record caps come from <see cref="IngestSizing.ResolveForSource"/>.
/// </summary>
public static class WorkingSetMode
{
    public static readonly bool Enabled = true;

    public static readonly long BudgetBytes = IngestSizing.ResolveWorkingSetBudgetBytes();
}
