using System.Text;
using Laplace.Chess.Service;

namespace Laplace.Chess.Uci.Commands;

/// <summary><c>review &lt;pgn-file|dir&gt;</c>: <see cref="ChessGameReview"/> over a PGN or a directory of them: centipawn loss per
/// side, blunders, and "crazy wins" (won despite a large deficit). Read-only; nothing is recorded.</summary>
public static class ReviewCommand
{
    public sealed record Result(string Path, int Depth, IReadOnlyList<ReviewedGame> Games);

    public static Result Run(ChessCommandLine cmd)
    {
        string path = cmd.Positionals.Count > 0 ? cmd.Positionals[0] : throw new ArgumentException("review <pgn-file|dir> [--depth D] [--max-games N]");
        if (!File.Exists(path) && !Directory.Exists(path)) throw new ArgumentException($"{path} does not exist");
        int depth = cmd.Int("depth", 4), max = cmd.Int("max-games", 20);
        return new Result(path, depth, ChessGameReview.ReviewFile(path, depth, max));
    }

    public static string Render(Result r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"reviewed {r.Games.Count} games (depth {r.Depth}) from {r.Path}");
        int crazy = 0;
        foreach (var g in r.Games)
        {
            string res = g.Result is null ? "*" : g.Result.Value.IsDraw ? "1/2" : g.Result.Value.Winner == 0 ? "1-0" : "0-1";
            string flag = g.CrazyWin ? $"   *** CRAZY WIN (winner was -{g.WinnerDownCp}cp) ***" : "";
            sb.AppendLine($"  {Short(g.White)}({g.WhiteElo}) vs {Short(g.Black)}({g.BlackElo})  {res}  {g.Plies}p  "
                + $"ACPL W{g.WhiteAcpl:F0}/B{g.BlackAcpl:F0}  blunders W{g.WhiteBlunders}/B{g.BlackBlunders}{flag}");
            if (!g.CrazyWin) continue;
            crazy++;
            foreach (var mv in g.Worst.Take(3))
                sb.AppendLine($"      #{mv.MoveNo} {(mv.White ? "W" : "B")} played {mv.Played} (best {mv.Best})  -{mv.CpLoss}cp {mv.Tag}");
        }
        sb.Append($"crazy wins: {crazy}/{r.Games.Count}: won despite a blunder; a deeper re-search tells luck from an evaluation blind spot");
        return sb.ToString();
    }

    private static string Short(string name) => string.IsNullOrEmpty(name) ? "?" : name.Length > 16 ? name[..16] : name;
}

/// <summary>
/// <c>lichess</c>: the Lichess connector. It streams the account's events and plays challenges it accepts, every move
/// through the UCI client from the named engine, and records each game through <see cref="ChessLiveGameHost"/>. This is
/// the one implementation: <c>laplace chess lichess</c> runs it. Lichess allows one event stream per account, so never
/// run this beside the LaplaceLichess service on the same account.
/// </summary>
public static class LichessCommand
{
    public const string Usage =
        "laplace-uci lichess [--token T] [--engine laplace|stockfish|lc0|<uci exe>] [--max-concurrent N] [--speed bullet|blitz|rapid|classical]... [--rated]\n"
        + "  Token from --token, the LICHESS_API environment variable, or deploy\\secrets\\lichess.env.\n"
        + "  --engine: the UCI engine every move comes from (default LAPLACE_LICHESS_ENGINE, else laplace through laplace-uci).\n"
        + "  --speed: accept only this time-control class (repeatable); default all.\n"
        + "  --rated: accept rated challenges too; default casual only.";

    public sealed record Settings(string Token, string Engine, int MaxConcurrent, IReadOnlySet<string>? Speeds, bool Rated);

    /// <summary>The settings, without touching the network: a missing token is a usage error.</summary>
    public static Settings Parse(ChessCommandLine cmd)
    {
        string? token = LichessBot.ResolveToken(cmd.Value("token") ?? "");
        if (string.IsNullOrEmpty(token)) throw new ArgumentException("no Lichess token\n" + Usage);
        var speeds = cmd.Repeated.TryGetValue("speed", out var s)
            ? s.Select(static v => v.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        return new Settings(token, cmd.Value("engine") ?? "", cmd.Int("max-concurrent", 4), speeds, cmd.Flag("rated"));
    }

    public static async Task<int> RunAsync(Settings settings, TextWriter output, CancellationToken ct)
    {
        var engine = settings.Engine.Length > 0
            ? UciLichessEngine.Create(settings.Engine, new Dictionary<string, string>())
            : UciLichessEngine.FromEnvironment();
        var policy = new LichessChallengePolicy(settings.Speeds is { Count: > 0 } ? settings.Speeds : null, Rated: settings.Rated);
        output.WriteLine($"lichess bot: engine {engine.Name}, max {settings.MaxConcurrent} concurrent games, {policy}");
        output.WriteLine("  token configured; validating Lichess account and bot permissions.");
        output.WriteLine("  Ctrl-C to stop (finishes in-flight games first).");
        await using var liveHost = await ChessLiveGameHost.CreateAsync();
        await using var bot = new LichessBot(settings.Token, liveHost, engine: engine, policy: policy);
        await bot.RunAsync(settings.MaxConcurrent, ct);
        return 0;
    }
}
