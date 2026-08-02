namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Per-file START/COMPOSED/COMMITTED lines. Durable progress is
/// <c>ingest_run_journal.files_done</c> — console lines are optional narration.
/// CI (<see cref="IngestConsoleVerbosity.Ci"/>) emits none; failures always log.
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
        if (!IngestConsoleMode.LogPerFileLines) return false;
        if (IngestConsoleMode.LogEveryFileLine) return true;
        if (knownTotal > 0 && knownTotal <= VerboseBelow) return true;
        if (ordinal1Based == 1) return true;
        if (knownTotal > 0 && ordinal1Based >= knownTotal) return true;
        return ordinal1Based % SampleEvery == 0;
    }
}
