namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Per-file START/COMPOSED/COMMITTED lines are useful when a source has dozens of
/// files. At 14,900 files they are 99% of the Actions log and bury progress.
/// Sample after <see cref="VerboseBelow"/>; always emit failures.
/// </summary>
public static class MultiFileTelemetry
{
    public const int VerboseBelow = 64;
    public const int SampleEvery = 256;

    /// <param name="ordinal1Based">1-based file index in completion/start order.</param>
    /// <param name="knownTotal">Inventory file count when known; 0 if unknown yet.</param>
    public static bool ShouldLogFileLine(int ordinal1Based, int knownTotal = 0)
    {
        if (ordinal1Based < 1) return false;
        if (knownTotal > 0 && knownTotal <= VerboseBelow) return true;
        if (ordinal1Based == 1) return true;
        if (knownTotal > 0 && ordinal1Based >= knownTotal) return true;
        return ordinal1Based % SampleEvery == 0;
    }
}
