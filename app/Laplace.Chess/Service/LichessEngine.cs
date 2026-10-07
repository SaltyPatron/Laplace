using Laplace.Chess.Service.Uci;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>A chosen move with the engine's typed analysis and receipt.</summary>
public sealed record LichessMoveDecision(UciMove Move, EngineAnalysis Analysis);

/// <summary>One game's engine: opened at the game's start, asked for each of the bot's moves, disposed at its end.</summary>
public interface ILichessMoveProvider : IDisposable
{
    EngineIdentity Identity { get; }
    LichessMoveDecision Choose(ChessPosition position, UciClock clock, CancellationToken ct);
}

/// <summary>The engine the connector plays with. It opens one provider per game.</summary>
public interface ILichessEngine
{
    /// <summary>What the operator configured: an engine name (laplace, stockfish, lc0) or a UCI executable's path.</summary>
    string Name { get; }
    ILichessMoveProvider Open();
}

/// <summary>
/// The connector's engine over the generic UCI client. A name (laplace, stockfish, lc0) starts laplace-uci with
/// <c>--engine</c>, so laplace-uci holds the engine's configuration and receipt; any other value is a UCI executable's
/// path, started directly. Configured by LAPLACE_LICHESS_ENGINE (default laplace) and LAPLACE_LICHESS_ENGINE_OPTIONS
/// (<c>Name=Value;Name=Value</c>); LAPLACE_LICHESS_MOVE_OVERHEAD_MS (default 300) is taken off our clock for the network.
/// </summary>
public sealed class UciLichessEngine(string name, UciProcessStart start, IReadOnlyDictionary<string, string> directOptions,
    long overheadMs) : ILichessEngine
{
    public string Name { get; } = name;
    public UciProcessStart Start { get; } = start;
    public long OverheadMs { get; } = overheadMs;

    public static readonly string[] EngineNames = ["laplace", "stockfish", "lc0"];

    public static UciLichessEngine FromEnvironment()
    {
        string engine = Read("LAPLACE_LICHESS_ENGINE") ?? "laplace";
        var options = ParseOptions(Read("LAPLACE_LICHESS_ENGINE_OPTIONS"));
        long overhead = long.TryParse(Read("LAPLACE_LICHESS_MOVE_OVERHEAD_MS"), out var ms) && ms >= 0 ? ms : 300;
        return Create(engine, options, overhead);
    }

    public static UciLichessEngine Create(string engine, IReadOnlyDictionary<string, string> options, long overheadMs = 300)
    {
        if (EngineNames.Contains(engine.Trim().ToLowerInvariant()))
        {
            string name = engine.Trim().ToLowerInvariant();
            string exe = ChessLabPaths.LaplaceUciExecutable()
                ?? throw new UciEngineException(UciFailure.Unavailable, "laplace-uci is not built or published (scripts/win/publish-uci.cmd)");
            var args = new List<string> { "uci", "--engine", name, "--profile", "play" };
            foreach (var (k, v) in options) { args.Add("--option"); args.Add($"{k}={v}"); }
            return new UciLichessEngine(name, new UciProcessStart(exe, args, HandshakeTimeout: TimeSpan.FromSeconds(60)),
                new Dictionary<string, string>(), overheadMs);
        }
        return new UciLichessEngine(engine, new UciProcessStart(engine, HandshakeTimeout: TimeSpan.FromSeconds(60)), options, overheadMs);
    }

    public static IReadOnlyDictionary<string, string> ParseOptions(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) throw new FormatException($"LAPLACE_LICHESS_ENGINE_OPTIONS entry '{part}' is not Name=Value");
            map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return map;
    }

    public ILichessMoveProvider Open()
    {
        var process = UciProcess.Start(Start);
        try
        {
            foreach (var (k, v) in directOptions) process.SetOption(k, v);
            process.NewGame(TimeSpan.FromSeconds(120));
            // laplace-uci names the engine behind it before its first readyok. Without that line an older
            // laplace-uci ignored --engine and would play as Laplace under another engine's name: refuse it.
            if (Name is "stockfish" or "lc0" && process.Identity.Via is null)
                throw new UciEngineException(UciFailure.Protocol,
                    $"{Start.ExePath} did not report running {Name}; republish laplace-uci (scripts/win/publish-uci.cmd)");
            return new Provider(process, OverheadMs);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static string? Read(string key)
        => LaplaceInstall.TryReadConfig(key, "lichess.env") ?? LaplaceInstall.TryReadDeploySecret("lichess-service.env", key);

    private sealed class Provider(UciProcess process, long overheadMs) : ILichessMoveProvider
    {
        public EngineIdentity Identity => process.Identity;

        public LichessMoveDecision Choose(ChessPosition position, UciClock clock, CancellationToken ct)
        {
            clock = clock with { OverheadMs = overheadMs };
            long mine = position.WhiteToMove ? clock.WTimeMs : clock.BTimeMs;
            var analysis = process.Search(position, clock.ToLimits(), TimeSpan.FromMilliseconds(mine + 15_000), "warm", ct: ct);
            var receipt = analysis.Receipt with { OverheadMs = overheadMs };
            analysis = analysis with { Receipt = receipt };
            return analysis.BestMove.Move is { } move
                ? new LichessMoveDecision(move, analysis)
                : throw new UciEngineException(UciFailure.Protocol, $"{process.Identity.Name} returned no move");
        }

        public void Dispose() => process.Dispose();
    }
}
