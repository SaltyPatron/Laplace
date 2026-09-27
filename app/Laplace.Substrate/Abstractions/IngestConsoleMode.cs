namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Console verbosity of ingest narration. The durable record is
/// <c>laplace.ingest_run_journal</c> and the ops CSV; the console is for operators.
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

    /// <summary>Overrides the resolved verbosity for this process; null re-resolves.</summary>
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
        // Under CI the journal and file sinks are the record; keep the job log terse.
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
