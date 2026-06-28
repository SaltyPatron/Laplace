using global::Npgsql;
using Laplace.Chess.Service;
using Laplace.Modality.Chess;
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
            "substrate-test" => await SubstrateTestAsync(args[1..]),
            _          => Fail($"unknown chess subcommand '{args[0]}'\n{Usage}"),
        };
    }

    private const string Usage =
        "usage: laplace chess <selfplay|move|fetch>\n"
        + "  selfplay [--games N] [--temp T] [--max-plies M] [--weight W] [--report-every R]\n"
        + "  move <fen>\n"
        + "  fetch <username> [--site chesscom|lichess] [--max N] [--out <path>]   (download a player's games as PGN)\n"
        + "  substrate-test [--games N] [--depth D] [--cp-per-point X] [--cap C]   (guided-vs-pure: the 34.5M-game graph's Elo lift)";

    /// <summary>The real test: pit a search whose root is biased by the substrate's learned MOVE eff_mu
    /// against the identical pure-classical search, and measure the Elo difference — i.e. how much the
    /// 34.5M-relation game graph adds to the classical floor. Both play the SAME depth; only the root
    /// prior differs.</summary>
    private static async Task<int> SubstrateTestAsync(string[] args)
    {
        int games = ArgInt(args, "--games", 30);
        int depth = ArgInt(args, "--depth", 4);
        int maxPlies = ArgInt(args, "--max-plies", 160);
        double cpPerPoint = ArgDouble(args, "--cp-per-point", 8.0);
        int cap = ArgInt(args, "--cap", 150);
        int concurrency = ArgInt(args, "--concurrency", 16); // 14900KS: 8P+16E — tune vs other load

        await using var ds = new NpgsqlDataSourceBuilder(ChessEngineService.ResolveConnString()).Build();
        var bias = new SubstrateRootBias(ds, cpPerPoint, cap);
        var guided = MatchRunner.SearcherFactory(depth, EvalTerm.All, bias);
        var pure   = MatchRunner.SearcherFactory(depth, EvalTerm.All, bias: null);

        Console.WriteLine($"substrate-test: guided (substrate root prior, {cpPerPoint}cp/pt, cap {cap}) vs pure classical");
        Console.WriteLine($"  depth {depth}, {games} games, maxPlies {maxPlies}, concurrency {concurrency}, db={Redact(ChessEngineService.ResolveConnString())}");
        var r = MatchRunner.Play(guided, pure, games, maxPlies, seed: 99, concurrency: concurrency);
        string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
        Console.WriteLine($"  guided W-D-L: {r.AWins}-{r.Draws}-{r.BWins}   score {r.Score:F3}   Elo {elo} +/- {r.Margin95:F0}");
        Console.WriteLine(r.EloDiff > 5
            ? "  => the 34.5M-game substrate measurably raises the classical floor."
            : "  => no clear lift at this depth/scale — try more games, deeper, or a larger --cp-per-point.");
        return 0;
    }

    private static string Redact(string conn) =>
        System.Text.RegularExpressions.Regex.Replace(conn, "(?i)password=[^;]*", "password=***");

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
