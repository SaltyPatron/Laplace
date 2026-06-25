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
            _          => Fail($"unknown chess subcommand '{args[0]}'\n{Usage}"),
        };
    }

    private const string Usage =
        "usage: laplace chess <selfplay|move>\n"
        + "  selfplay [--games N] [--temp T] [--max-plies M] [--weight W] [--report-every R]\n"
        + "  move <fen>";

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
