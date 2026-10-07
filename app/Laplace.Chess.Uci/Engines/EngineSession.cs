using Laplace.Chess.Service.Uci;

namespace Laplace.Chess.Uci.Engines;

/// <summary>
/// One configured engine process: started from its <see cref="ChessEngineSpec"/>, given its pinned profile and any
/// request overrides (mapped to the engine's own names), and made ready. Every surface (the CLI commands, the UCI
/// proxy, the endpoint) opens engines only through this, so configuration and receipts live in one place.
/// </summary>
public sealed class EngineSession : IDisposable
{
    public ChessEngineSpec Spec { get; }
    public UciProcess Process { get; }
    /// <summary>Request overrides were applied: a pool discards this session after the job.</summary>
    public bool Customized { get; }
    public bool Broken => Process.Broken;

    private EngineSession(ChessEngineSpec spec, UciProcess process, bool customized)
    {
        Spec = spec;
        Process = process;
        Customized = customized;
    }

    public static EngineSession Open(ChessEngineSpec spec, IReadOnlyDictionary<string, string>? overrides = null,
        TimeSpan? readyTimeout = null)
    {
        if (spec.Start is null) throw new UciEngineException(UciFailure.Unavailable, spec.Missing ?? $"{spec.Name} is not installed");
        var process = UciProcess.Start(spec.Start);
        try
        {
            foreach (var (name, value) in spec.ProfileOptions) process.SetOption(name, value);
            bool customized = false;
            foreach (var (name, value) in overrides ?? new Dictionary<string, string>())
            {
                process.SetOption(spec.MapOption(name), value);
                customized = true;
            }
            // Lc0 loads its network on the first isready; give it the time a cold GPU start needs.
            process.IsReady(readyTimeout ?? TimeSpan.FromSeconds(120));
            return new EngineSession(spec, process, customized);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Search one position. State "fresh" (the default) starts from ucinewgame with the hash cleared, so the result
    /// depends only on the position, the options and the limits; "warm" keeps what the process has seen (a game in
    /// progress). The receipt records which.
    /// </summary>
    public EngineAnalysis Analyse(ChessPosition position, UciLimits limits, int multiPv = 1, string state = "fresh",
        TimeSpan? timeout = null, Action<EngineInfo>? onInfo = null, CancellationToken ct = default)
    {
        if (state is not ("fresh" or "warm")) throw new UciEngineException(UciFailure.Rejected, "state is fresh or warm");
        string multi = spec(multiPv);
        if (Process.Supports("MultiPV") && Process.OptionsSent.FirstOrDefault(static o => o.Key == "MultiPV").Value != multi)
            Process.SetOption("MultiPV", multi);
        else if (!Process.Supports("MultiPV") && multiPv != 1)
            throw new UciEngineException(UciFailure.Rejected, $"{Process.Identity.Name} has no MultiPV");
        if (state == "fresh") Process.NewGame(TimeSpan.FromSeconds(60));
        else Process.IsReady(TimeSpan.FromSeconds(60));
        return Process.Search(position, limits, timeout ?? DefaultTimeout(limits), state, onInfo: onInfo, ct: ct);

        static string spec(int k) => Math.Clamp(k, 1, 256).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A deadline for a bounded search: the declared time plus a margin, or a fixed budget for node/depth limits.</summary>
    public static TimeSpan DefaultTimeout(UciLimits limits)
    {
        long ms = limits.MoveTimeMs ?? 0;
        if (limits.WTimeMs is { } w && limits.BTimeMs is { } b) ms = Math.Max(ms, Math.Max(w, b));
        return TimeSpan.FromMilliseconds(Math.Max(ms + 10_000, 120_000));
    }

    public void Dispose() => Process.Dispose();
}
