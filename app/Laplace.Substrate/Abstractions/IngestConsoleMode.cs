namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Where ingest narration goes. The durable surface is
/// <c>laplace.ingest_run_journal</c> (+ ops CSV). Console is for operators;
/// CI must not be a second log warehouse.
/// </summary>
public enum IngestConsoleVerbosity
{
    /// <summary>START/COMPLETE/errors + rare progress; no per-file lines; Serilog console ≥ Warning.</summary>
    Ci = 0,
    /// <summary>Default interactive: sampled per-file lines + ~2s progress.</summary>
    Progress = 1,
    /// <summary>Every file START/COMPOSED/COMMITTED.</summary>
    Verbose = 2,
}

public static class IngestConsoleMode
{
    public const string EnvName = "LAPLACE_INGEST_CONSOLE";

    private static IngestConsoleVerbosity? _cached;

    public static IngestConsoleVerbosity Current
    {
        get
        {
            if (_cached is { } c) return c;
            _cached = Resolve();
            return _cached.Value;
        }
    }

    /// <summary>Test/override hook — clears on next process naturally.</summary>
    public static void Override(IngestConsoleVerbosity? value) => _cached = value;

    private static IngestConsoleVerbosity Resolve()
    {
        string? raw = Environment.GetEnvironmentVariable(EnvName);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (Enum.TryParse(raw, ignoreCase: true, out IngestConsoleVerbosity parsed))
                return parsed;
            if (raw.Equals("quiet", StringComparison.OrdinalIgnoreCase))
                return IngestConsoleVerbosity.Ci;
        }
        // Actions / CI: journal + file sinks are the record; don't flood the job log.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
            return IngestConsoleVerbosity.Ci;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            return IngestConsoleVerbosity.Ci;
        return IngestConsoleVerbosity.Progress;
    }

    public static bool LogPerFileLines => Current == IngestConsoleVerbosity.Verbose
        || Current == IngestConsoleVerbosity.Progress;

    public static bool LogEveryFileLine => Current == IngestConsoleVerbosity.Verbose;

    public static int ProgressMinIntervalMs => Current == IngestConsoleVerbosity.Ci ? 30_000 : 2_000;
}
