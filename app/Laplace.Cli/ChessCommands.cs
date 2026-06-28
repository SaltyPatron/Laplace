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
            "ladder"   => await LadderAsync(args[1..]),
            "review"   => await ReviewAsync(args[1..]),
            "learned-pst" => await LearnedPstAsync(args[1..]),
            "learned-eval-test" => await LearnedEvalTestAsync(args[1..]),
            "tactics"  => await TacticsAsync(args[1..]),
            _          => Fail($"unknown chess subcommand '{args[0]}'\n{Usage}"),
        };
    }

    private const string Usage =
        "usage: laplace chess <selfplay|move|fetch|substrate-test>\n"
        + "  selfplay [--games N] [--temp T] [--max-plies M] [--weight W] [--report-every R]\n"
        + "  move <fen>\n"
        + "  fetch <username> [--site chesscom|lichess] [--max N] [--out <path>]   (download a player's games as PGN)\n"
        + "  substrate-test [--mode fold|edge|off] [--games N] [--depth D] [--cp-per-point X] [--cap C] [--openings]\n"
        + "      guided-vs-pure: the corpus's Elo lift over the classical floor.\n"
        + "      --mode fold = substructure-fold (generalizes, the honest transfer test; DEFAULT);\n"
        + "             edge = raw MOVE-edge eff_mu (popularity; the first null result); off = sanity (pure vs pure)\n"
        + "      --openings = seed games from the ingested ECO openings (where the corpus HAS data)\n"
        + "  ladder [--games N] [--depth D] [--openings]   (overlay-ablation: each EvalTerm's individual Elo)\n"
        + "  review <pgn-file|dir> [--depth D] [--max-games N]   (centipawn-loss + 'crazy win' triage over ingested games)\n"
        + "  learned-pst [--piece PNBRQK]   (what the corpus LEARNED about each piece-square — the data-driven PST)\n"
        + "  learned-eval-test [--games N] [--depth D] [--scale X] [--blend] [--openings]   (learned-PST vs PeSTO;\n"
        + "      --blend = PeSTO floor + small learned overlay (additive), else learned REPLACES PeSTO)\n"
        + "  tactics [epd-file] [--depth D]   (solve-rate over an EPD suite; built-in mate suite if no file)";

    /// <summary>The real test: pit a search whose root is biased by the substrate against the identical
    /// pure-classical search, and measure the Elo difference — how much the game graph adds to the
    /// classical floor. Both play the SAME depth; only the root prior differs. <c>--mode fold</c> uses the
    /// substructure-fold (generalizes to novel positions); <c>edge</c> uses raw MOVE eff_mu (popularity);
    /// <c>off</c> is a pure-vs-pure sanity check (should be ≈0). <c>--openings</c> seeds from book lines.</summary>
    private static async Task<int> SubstrateTestAsync(string[] args)
    {
        string mode = ArgStr(args, "--mode", "fold").ToLowerInvariant();
        int games = ArgInt(args, "--games", 200);
        int depth = ArgInt(args, "--depth", 4);
        int maxPlies = ArgInt(args, "--max-plies", 160);
        double cpPerPoint = ArgDouble(args, "--cp-per-point", 8.0);
        int cap = ArgInt(args, "--cap", 150);
        int concurrency = ArgInt(args, "--concurrency", 16); // 14900KS: 8P+16E — tune vs other load
        bool seedOpenings = HasFlag(args, "--openings");

        await using var ds = new NpgsqlDataSourceBuilder(ChessEngineService.ResolveConnString()).Build();
        IRootBias? bias = mode switch
        {
            "fold" => new SubstructureFoldBias(ds, cpPerPoint, cap),
            "edge" => new SubstrateRootBias(ds, cpPerPoint, cap),
            "off"  => null,
            _      => throw new ArgumentException($"unknown --mode '{mode}' (expected fold|edge|off)"),
        };
        // --learned: ALSO give the guided engine the light learned-PST leaf overlay (Part 9, +31 alone) on top
        // of the root prior — measures the STACKED corpus lift (fold + learned eval) over pure classical.
        bool useLearned = HasFlag(args, "--learned");
        int[][]? mg = null, eg = null;
        if (useLearned)
        {
            (mg, eg) = LearnedPst.BuildTables(ds, ArgDouble(args, "--learned-scale", 1.0));
            (mg, eg) = Evaluation.BlendPeStoWith(mg, eg);
        }
        var guided = MatchRunner.SearcherFactory(depth, EvalTerm.All, bias, ttBits: 16, mgPst: mg, egPst: eg);
        var pure   = MatchRunner.SearcherFactory(depth, EvalTerm.All, bias: null);
        // Seed from the ALREADY-INGESTED Lichess ECO openings (openings/*.tsv), not a hardcoded list.
        string openingsDir = ArgStr(args, "--openings-dir", OpeningSeed.DefaultDir);
        var book = seedOpenings ? OpeningSeed.Fens(openingsDir, plies: ArgInt(args, "--openings-plies", 10)) : null;

        string desc = mode switch
        {
            "fold" => $"substructure-fold prior ({cpPerPoint}cp/pt, cap {cap})",
            "edge" => $"raw MOVE-edge prior ({cpPerPoint}cp/pt, cap {cap})",
            _      => "NO prior (sanity)",
        };
        Console.WriteLine($"substrate-test [{mode}]: guided ({desc}) vs pure classical");
        Console.WriteLine($"  depth {depth}, {games} games, maxPlies {maxPlies}, concurrency {concurrency}, "
            + $"openings {(seedOpenings ? $"suite({book!.Count})" : "random")}, db={Redact(ChessEngineService.ResolveConnString())}");
        string pgnOut = ArgStr(args, "--pgn-out", "");
        var sink = string.IsNullOrEmpty(pgnOut)
            ? null
            : new System.Collections.Concurrent.ConcurrentBag<(IReadOnlyList<ChessMove> Moves, int Outcome, string StartFen)>();
        var r = MatchRunner.Play(guided, pure, games, maxPlies, seed: 99, concurrency: concurrency,
                                 openingFens: book, pgnSink: sink);
        if (sink is not null)
        {
            ChessPgnWriter.WriteFile(pgnOut, sink, white: "Laplace-guided", black: "Laplace-pure", @event: "substrate-test");
            Console.WriteLine($"  wrote {sink.Count} games -> {pgnOut}   (loop closure: laplace ingest chess \"{pgnOut}\")");
        }
        string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
        Console.WriteLine($"  guided W-D-L: {r.AWins}-{r.Draws}-{r.BWins}   score {r.Score:F3}   Elo {elo} +/- {r.Margin95:F0}");
        Console.WriteLine(r.EloDiff > 5
            ? "  => the substrate measurably raises the classical floor at this mode/scale."
            : "  => no clear lift at this mode/scale — try --openings, more games, deeper, or a larger --cp-per-point.");
        return 0;
    }

    private static bool HasFlag(string[] a, string flag) => Array.IndexOf(a, flag) >= 0;

    /// <summary>Game-review over ingested games: per-side ACPL + blunder counts + the "crazy win" triage
    /// (won despite a blunder — luck vs eval blind-spot). Pure search (no DB).</summary>
    private static Task<int> ReviewAsync(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
            return Task.FromResult(Fail("usage: laplace chess review <pgn-file|dir> [--depth D] [--max-games N]"));
        var path = args[0];
        int depth = ArgInt(args, "--depth", 4);
        int maxGames = ArgInt(args, "--max-games", 20);

        var games = ChessGameReview.ReviewFile(path, depth, maxGames);
        Console.WriteLine($"reviewed {games.Count} games (depth {depth}) from {path}");
        int crazy = 0;
        foreach (var g in games)
        {
            string res = g.Result is null ? "*" : g.Result.Value.IsDraw ? "1/2" : g.Result.Value.Winner == 0 ? "1-0" : "0-1";
            string flag = g.CrazyWin ? $"   *** CRAZY WIN (winner was -{g.WinnerDownCp}cp) ***" : "";
            Console.WriteLine($"  {Short(g.White)}({g.WhiteElo}) vs {Short(g.Black)}({g.BlackElo})  {res}  {g.Plies}p  "
                + $"ACPL W{g.WhiteAcpl:F0}/B{g.BlackAcpl:F0}  blunders W{g.WhiteBlunders}/B{g.BlackBlunders}{flag}");
            if (g.CrazyWin)
            {
                crazy++;
                foreach (var mv in g.Worst.Take(3))
                    Console.WriteLine($"      #{mv.MoveNo} {(mv.White ? "W" : "B")} played {mv.Played} (best {mv.Best})  -{mv.CpLoss}cp {mv.Tag}");
            }
        }
        Console.WriteLine($"crazy wins: {crazy}/{games.Count} — won despite a blunder; deep re-search discriminates luck from eval blind-spot");
        return Task.FromResult(0);
    }

    private static string Short(string name) => string.IsNullOrEmpty(name) ? "?" : (name.Length > 16 ? name[..16] : name);

    /// <summary>Dump the substrate's LEARNED piece-square values (the data-driven PST) as 8x8 grids — what
    /// the corpus says each piece is worth on each square. Read-only; touches no eval path.</summary>
    private static async Task<int> LearnedPstAsync(string[] args)
    {
        string pieces = ArgStr(args, "--piece", LearnedPst.WhitePieces).ToUpperInvariant();
        await using var ds = new NpgsqlDataSourceBuilder(ChessEngineService.ResolveConnString()).Build();
        var learned = LearnedPst.ReadWhite(ds);
        Console.WriteLine($"learned piece-square values (rating-point deviation from a draw; >0 = good for the mover), db={Redact(ChessEngineService.ResolveConnString())}");

        foreach (char pc in pieces)
        {
            var sqs = learned.Where(s => s.Piece == pc).ToList();
            if (sqs.Count == 0) continue;
            double cover = sqs.Count(s => s.Witness > 0) / 64.0 * 100;
            Console.WriteLine($"\n  {PieceName(pc)} ('{pc}')  — {cover:F0}% of squares have evidence");
            for (int rank = 7; rank >= 0; rank--)
            {
                var row = new System.Text.StringBuilder("    ");
                for (int file = 0; file < 8; file++)
                {
                    var s = sqs.First(x => x.File == file && x.Rank == rank);
                    row.Append((s.Witness > 0 ? $"{s.DevPoints:+0;-0;0}" : ".").PadLeft(6));
                }
                Console.WriteLine(row.ToString());
            }
            var best = sqs.Where(s => s.Witness > 0).OrderByDescending(s => s.DevPoints).FirstOrDefault();
            var worst = sqs.Where(s => s.Witness > 0).OrderBy(s => s.DevPoints).FirstOrDefault();
            if (best.Witness > 0)
                Console.WriteLine($"    best {(char)('a'+best.File)}{(char)('1'+best.Rank)} {best.DevPoints:+0;-0}  "
                    + $"worst {(char)('a'+worst.File)}{(char)('1'+worst.Rank)} {worst.DevPoints:+0;-0}");
        }
        return 0;
    }

    private static string PieceName(char p) => p switch
    {
        'P' => "Pawn", 'N' => "Knight", 'B' => "Bishop", 'R' => "Rook", 'Q' => "Queen", 'K' => "King", _ => p.ToString(),
    };

    /// <summary>Tactics solve-rate over an EPD suite (the second correctness bar). No file ⇒ the built-in
    /// mate suite (smoke test). Pure search, no DB.</summary>
    private static Task<int> TacticsAsync(string[] args)
    {
        int depth = ArgInt(args, "--depth", 6);
        string? file = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null;
        IEnumerable<string> lines = file is not null && System.IO.File.Exists(file)
            ? System.IO.File.ReadLines(file)
            : ChessTactics.Builtin;
        string label = file is not null && System.IO.File.Exists(file) ? file : "built-in mate suite";

        Console.WriteLine($"tactics: {label}, depth {depth}");
        var (solved, total, results) = ChessTactics.Run(lines, depth);
        foreach (var r in results)
            Console.WriteLine($"  [{(r.Solved ? "OK " : "MISS")}] {r.Id,-28} engine {r.Engine,-6} expected {r.Expected}");
        Console.WriteLine($"solved {solved}/{total} ({(total > 0 ? 100.0 * solved / total : 0):F0}%)");
        return Task.FromResult(0);
    }

    /// <summary>The data-driven-eval test: an engine whose leaf PST is the substrate-LEARNED table plays an
    /// otherwise-identical engine using hand-tuned PeSTO. Same material/structure terms; ONLY the PST differs,
    /// at EVERY node. Honest measurement of whether the corpus's learned piece-square values can stand in for
    /// (or beat) PeSTO — the guardrail says validate before trusting, so this is the validation.</summary>
    private static async Task<int> LearnedEvalTestAsync(string[] args)
    {
        int games = ArgInt(args, "--games", 200);
        int depth = ArgInt(args, "--depth", 4);
        int maxPlies = ArgInt(args, "--max-plies", 160);
        double scale = ArgDouble(args, "--scale", 6.0);
        int concurrency = ArgInt(args, "--concurrency", 16);
        bool seedOpenings = HasFlag(args, "--openings");
        string openingsDir = ArgStr(args, "--openings-dir", OpeningSeed.DefaultDir);
        var book = seedOpenings ? OpeningSeed.Fens(openingsDir, plies: ArgInt(args, "--openings-plies", 10)) : null;

        bool blend = HasFlag(args, "--blend");
        await using var ds = new NpgsqlDataSourceBuilder(ChessEngineService.ResolveConnString()).Build();
        var (mg, eg) = LearnedPst.BuildTables(ds, scale);
        // --blend: PeSTO floor + a small learned overlay (the "additive, don't replace" hypothesis).
        //  default: the learned table REPLACES PeSTO (the wholesale test — known to regress).
        if (blend) (mg, eg) = Evaluation.BlendPeStoWith(mg, eg);
        var learned = MatchRunner.SearcherFactory(depth, EvalTerm.All, bias: null, ttBits: 16, mgPst: mg, egPst: eg);
        var pesto   = MatchRunner.SearcherFactory(depth, EvalTerm.All);

        Console.WriteLine($"learned-eval-test [{(blend ? "blend: PeSTO+learned" : "replace: learned-only")}]: "
            + $"scale {scale}cp/pt vs hand-tuned PeSTO, depth {depth}, "
            + $"{games} games, openings {(seedOpenings ? $"suite({book!.Count})" : "random")}");
        var r = MatchRunner.Play(learned, pesto, games, maxPlies, seed: 99, concurrency: concurrency, openingFens: book);
        string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
        Console.WriteLine($"  learned W-D-L: {r.AWins}-{r.Draws}-{r.BWins}   score {r.Score:F3}   Elo {elo} +/- {r.Margin95:F0}");
        Console.WriteLine(r.EloDiff >= -10
            ? "  => learned PST is competitive with hand-tuned PeSTO — the corpus eval is viable (tune scale / phase-split to push ahead)."
            : "  => learned PST trails PeSTO at this scale (PST is the #2 pillar; needs phase-split + more evidence + validation before replacing).");
        return 0;
    }

    /// <summary>Overlay-ablation ladder: for each <see cref="EvalTerm"/>, pit the full eval against the
    /// eval with ONLY that term removed; the Elo of full-over-minus is that overlay's individual
    /// contribution. Pure C# (no DB) — the parallel <see cref="MatchRunner"/>. <c>--openings</c> seeds from
    /// the ingested ECO book so the ablation isn't dominated by random-opening noise.</summary>
    private static Task<int> LadderAsync(string[] args)
    {
        int games = ArgInt(args, "--games", 100);
        int depth = ArgInt(args, "--depth", 4);
        int maxPlies = ArgInt(args, "--max-plies", 160);
        int concurrency = ArgInt(args, "--concurrency", 16);
        bool seedOpenings = HasFlag(args, "--openings");
        string openingsDir = ArgStr(args, "--openings-dir", OpeningSeed.DefaultDir);
        var book = seedOpenings ? OpeningSeed.Fens(openingsDir, plies: ArgInt(args, "--openings-plies", 10)) : null;

        var terms = new[]
        {
            EvalTerm.Material, EvalTerm.Pst, EvalTerm.BishopPair,
            EvalTerm.RookFiles, EvalTerm.PawnStructure, EvalTerm.Tempo,
        };
        Console.WriteLine($"overlay-ablation ladder: full eval vs eval-minus-term, depth {depth}, {games} games each, "
            + $"openings {(seedOpenings ? $"suite({book!.Count})" : "random")}");
        Console.WriteLine($"  {"term",-14} {"W-D-L",-12} {"Elo",7}  {"+/-",5}");
        foreach (var t in terms)
        {
            var full  = MatchRunner.SearcherFactory(depth, EvalTerm.All);
            var minus = MatchRunner.SearcherFactory(depth, EvalTerm.All & ~t);
            var r = MatchRunner.Play(full, minus, games, maxPlies, seed: 7, concurrency: concurrency, openingFens: book);
            string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
            Console.WriteLine($"  {t,-14} {$"{r.AWins}-{r.Draws}-{r.BWins}",-12} {elo,7}  {r.Margin95,5:F0}");
        }
        Console.WriteLine("  (positive Elo = removing that overlay WEAKENS the engine, i.e. the overlay helps)");
        return Task.FromResult(0);
    }

    private static string Redact(string conn) =>
        System.Text.RegularExpressions.Regex.Replace(conn, "(?i)password=[^;]*", "password=***");

    private static async Task<int> SelfPlayAsync(string[] args)
    {
        int games = ArgInt(args, "--games", 200);
        int maxPlies = ArgInt(args, "--max-plies", 200);
        int reportEvery = ArgInt(args, "--report-every", 25);
        bool weak = HasFlag(args, "--weak");

        Action<ChessTrainStatus> report = st =>
            Console.WriteLine($"  [{st.Games}] W={st.White} B={st.Black} D={st.Draws} (adj {st.Adjudicated}); last {st.LastOutcome}");

        await using var svc = new ChessEngineService(ChessEngineService.ResolveConnString(),
            ArgDouble(args, "--weight", 0.5d));

        if (weak)
        {
            double temp = ArgDouble(args, "--temp", 120d);
            Console.WriteLine($"chess selfplay [WEAK: depth-1 substrate]: {games} games, temp={temp}, maxPlies={maxPlies}");
            await svc.RunSelfPlayAsync(games, temp, ArgInt(args, "--max-plies", 400), reportEvery, report);
        }
        else
        {
            // The unified engine plays itself — the LEARNER IS THE PLAYER (α-β + PeSTO⊕learned-PST + fold).
            int depth = ArgInt(args, "--depth", 4);
            int openingPlies = ArgInt(args, "--opening-plies", 6);
            Console.WriteLine($"chess selfplay [STRONG: unified α-β engine, depth {depth}, opening {openingPlies}]: "
                + $"{games} games, maxPlies {maxPlies} — checkmates up-weight, flags credit as draws");
            await svc.RunStrongSelfPlayAsync(games, depth, maxPlies, openingPlies, reportEvery, report);
        }

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
