using Laplace.Chess.Service;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

/// <summary>
/// `laplace chess …` — thin CLI over the shared <see cref="ChessEngineService"/> (same service the web
/// host drives).
///   selfplay [--games N] [--temp T] [--max-plies M] [--weight W] [--report-every R]
///   move &lt;fen&gt;
/// </summary>
internal static class ChessCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) return Fail(Usage);
        return args[0] switch
        {
            "selfplay" => await SelfPlayAsync(args[1..]),
            "move"     => await MoveAsync(args[1..]),
            "fetch"    => await FetchAsync(args[1..]),
            _          => Fail($"unknown chess subcommand '{args[0]}'\n{Usage}"),
        };
    }

    private const string Usage =
        "usage: laplace chess <selfplay|move|fetch>\n"
        + "  selfplay [--games N] [--temp T] [--max-plies M] [--weight W] [--report-every R]\n"
        + "  move <fen>\n"
        + "  fetch <username> [--site chesscom|lichess] [--max N] [--out <path>]   (download a player's games as PGN)";

    private static async Task<int> SelfPlayAsync(string[] args)
    {
        int games = ArgInt(args, "--games", 200);
        double temp = ArgDouble(args, "--temp", 120d);
        int maxPlies = ArgInt(args, "--max-plies", 400);
        double weight = ArgDouble(args, "--weight", 0.5d);
        int reportEvery = ArgInt(args, "--report-every", 25);

        await using var svc = new ChessEngineService(ChessEngineService.ResolveConnString(), weight);
        Console.WriteLine($"chess selfplay: {games} games, temp={temp}, weight={weight}, maxPlies={maxPlies}");
        await svc.RunSelfPlayAsync(games, temp, maxPlies, reportEvery, st =>
            Console.WriteLine($"  [{st.Games}] W={st.White} B={st.Black} D={st.Draws} (adj {st.Adjudicated}); last {st.LastOutcome}"));

        var opening = await svc.ScoreAsync(svc.NewGameFen());
        Console.WriteLine("opening eff_mu: " + string.Join("  ",
            opening.Take(5).Select(m => $"{m.Uci}={m.EffMu:F1}{(m.Rated ? "" : "*")}")));
        return 0;
    }

    private static async Task<int> MoveAsync(string[] args)
    {
        var fen = string.Join(' ', args).Trim();
        if (fen.Length == 0) return Fail("usage: laplace chess move <fen>");

        await using var svc = new ChessEngineService(ChessEngineService.ResolveConnString());
        var best = await svc.BestMoveAsync(fen, 0d);
        if (best.Uci is null) { Console.WriteLine($"terminal: {best.Status}"); return 0; }
        Console.WriteLine($"bestmove {best.Uci}  (eff_mu {best.EffMu:F1}, {(best.Rated ? "rated" : "unrated-prior")})");
        foreach (var m in (await svc.ScoreAsync(fen)).Take(8))
            Console.WriteLine($"  {m.Uci,-6} {m.EffMu,8:F1} {(m.Rated ? "" : "(prior)")}");
        return 0;
    }

    private static async Task<int> FetchAsync(string[] a)
    {
        if (a.Length == 0 || a[0].StartsWith("--"))
            return Fail("usage: laplace chess fetch <username> [--site chesscom|lichess] [--max N] [--min-tc SECONDS] [--out <path>]\n"
                      + "  --min-tc: keep only games with base time control >= SECONDS (600 = rapid+classical, drops blitz/bullet)");
        var user = a[0];
        var site = ArgStr(a, "--site", "chesscom");
        int? max = ArgIntOrNull(a, "--max");
        int minTc = ArgInt(a, "--min-tc", 0);
        var outPath = ArgStr(a, "--out", ChessGameFetcher.DefaultOut(user, site));
        Console.WriteLine($"fetching {user} from {site} -> {outPath}" + (minTc > 0 ? $" (min TC {minTc}s)" : ""));
        int games = await ChessGameFetcher.FetchAsync(user, site, max, minTc, outPath, Console.WriteLine, CancellationToken.None);
        Console.WriteLine($"done: {games} games written to {outPath}");
        Console.WriteLine($"next: laplace ingest chess \"{outPath}\"  (once the pgn grammar is built into laplace_core)");
        return 0;
    }

    private static string ArgStr(string[] a, string flag, string def)
    {
        int i = Array.IndexOf(a, flag);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
    }

    private static int? ArgIntOrNull(string[] a, string flag)
    {
        int i = Array.IndexOf(a, flag);
        return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : (int?)null;
    }

    private static int ArgInt(string[] a, string flag, int def)
    {
        int i = Array.IndexOf(a, flag);
        return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : def;
    }

    private static double ArgDouble(string[] a, string flag, double def)
    {
        int i = Array.IndexOf(a, flag);
        return i >= 0 && i + 1 < a.Length && double.TryParse(a[i + 1], out var v) ? v : def;
    }
}
