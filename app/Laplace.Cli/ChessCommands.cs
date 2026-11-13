using global::Npgsql;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

internal static class ChessCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) return Fail(Usage);
        return args[0] switch
        {
            "selfplay" => await SelfPlayAsync(args[1..]),
            "move" => await MoveAsync(args[1..]),
            "fetch" => await FetchAsync(args[1..]),
            "record" => await RecordAsync(args[1..]),
            "substrate-test" => await SubstrateTestAsync(args[1..]),
            "ladder" => await LadderAsync(args[1..]),
            "review" => await ReviewAsync(args[1..]),
            "tactics" => await TacticsAsync(args[1..]),
            "lichess" => await LichessAsync(args[1..]),
            "lab" => await RunUciAsync(args[1..]),
            "match" => await MatchAsync(args[1..]),
            "bench" => Bench(args[1..]),
            _ => Fail($"unknown chess subcommand '{args[0]}'\n{Usage}"),
        };
    }

    private static async Task<int> RecordAsync(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
            return Fail("usage: laplace-mono chess record <pgn-file> (the Engine reads the file's games; its trunk is their witness)");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            string path = Path.GetFullPath(args[0]);
            await new ChessGameRecorder(ChessEngineService.ResolveConnString()).IngestFileAsync(null, path, cancellation.Token);
            Console.WriteLine($"recorded {path}");
            return 0;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    // Engine-vs-engine match with a live terminal board: drives the ChessLabService cutechess
    // job and renders its event stream. The job admits the games (--no-ingest sets ingest=false).
    private static async Task<int> MatchAsync(string[] args)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rounds"] = ArgInt(args, "--rounds", 10).ToString(),
            ["depth"] = ArgInt(args, "--depth", 0).ToString(),
            ["st"] = ArgDouble(args, "--st", 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["elo"] = ArgInt(args, "--elo", 2000).ToString(),
            ["ingest"] = HasFlag(args, "--no-ingest") ? "false" : "true",
        };
        using var cts = new CancellationTokenSource();
        return await ChessMatchDashboard.RunAsync(config, cts.Token);
    }

    private const string Usage =
        "usage: laplace-mono chess <selfplay|move|fetch|record|substrate-test|ladder|review|tactics|lichess|match|lab|bench>\n"
        + "  lab <check|match|ladder|analyse|inspect> [options]   (installed monorepo UCI tools, using this client's credentials and configuration)\n"
        + "  match [--rounds N] [--depth D] [--st S] [--elo E] [--no-ingest]   (engine-vs-engine, live terminal board; the Engine records the games)\n"
        + "  selfplay [--games N] [--temp T] [--max-plies M] [--weight W] [--report-every R]\n"
        + "  move <fen>\n"
        + "  fetch <username> [--site chesscom|lichess] [--max N] [--out <path>]   (download a player's games as PGN)\n"
        + "  record <pgn-file>   (the Engine reads the file's games; the file's trunk is their witness)\n"
        + "  substrate-test [--mode transition|off] [--games N] [--depth D] [--openings] [--no-record]\n"
        + "      guided-vs-pure: the standings of the moves recorded games played against a classical control.\n"
        + "      --mode transition = each root move's [position, move, position] standing (DEFAULT); off = sanity (pure vs pure)\n"
        + "      --openings = seed games from the opening book\n"
        + "  ladder [--games N] [--depth D] [--openings] [--no-record]   (overlay-ablation: each EvalTerm's individual Elo)\n"
        + "  review <pgn-file|dir> [--depth D] [--max-games N]   (centipawn-loss + 'crazy win' triage over a PGN's games)\n"
        + "  bench [--depth D]   (reproducible engine benchmark: fixed-depth Search over a standard\n"
        + "      position set; reports nodes, nodes/sec, and bytes allocated — the profile-before-\n"
        + "      optimizing baseline, deterministic node counts at a given depth)\n"
        + "  tactics [epd-file] [--depth D]   (solve-rate over an EPD suite; built-in mate suite if no file)\n"
        + "  lichess [--token T] [--engine laplace|stockfish|lc0|<uci exe>] [--max-concurrent N] [--speed bullet|blitz|rapid|classical]\n"
        + "      runs laplace-uci lichess: stream account events and play accepted challenges (token from LICHESS_API or deploy\\secrets\\lichess.env)";

    // Reproducible Search benchmark: fixed-depth search over a fixed position set, so node
    // counts are deterministic at a given depth and nodes/sec and bytes allocated are the
    // measure. The set spans opening, tactical middlegames, and endgames.
    private static readonly string[] BenchPositions =
    [
        ChessModality.StartFen,
        "r1bqkbnr/pppp1ppp/2n5/1B2p3/4P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",   // Ruy Lopez
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", // Kiwipete (tactical)
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",                            // endgame, sparse
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10", // symmetric middlegame
        "8/8/8/8/8/8/6k1/4K2R w K - 0 1",                                       // KR vs K endgame
        "rnbq1rk1/pp2bppp/4pn2/2pp4/3P4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 8",     // QGD middlegame
        "2rq1rk1/pp1bppbp/2np1np1/8/2BNP3/2N1BP2/PPPQ2PP/2KR3R w - - 0 11",     // sharp attack
    ];

    private static int Bench(string[] args)
    {
        int depth = ArgInt(args, "--depth", 8);
        Console.WriteLine($"chess bench — {BenchPositions.Length} positions @ depth {depth} (deterministic node counts)");
        long totalNodes = 0, totalMs = 0;
        long allocStart = GC.GetTotalAllocatedBytes(precise: true);
        // Warm the JIT so the reported numbers are steady-state, not first-call compilation.
        _ = new Search().Think(Board.FromFen(ChessModality.StartFen), new Search.Limits(MaxDepth: 4));
        long allocAfterWarm = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < BenchPositions.Length; i++)
        {
            var b = Board.FromFen(BenchPositions[i]);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new Search().Think(b, new Search.Limits(MaxDepth: depth));
            sw.Stop();
            totalNodes += r.Nodes;
            totalMs += sw.ElapsedMilliseconds;
            long nps = sw.ElapsedMilliseconds > 0 ? r.Nodes * 1000 / sw.ElapsedMilliseconds : 0;
            Console.WriteLine($"  [{i + 1,2}] nodes={r.Nodes,12:N0}  {sw.ElapsedMilliseconds,6} ms  {nps,10:N0} nps");
        }
        long allocBytes = GC.GetTotalAllocatedBytes(precise: true) - allocAfterWarm;
        long totalNps = totalMs > 0 ? totalNodes * 1000 / totalMs : 0;
        Console.WriteLine(new string('-', 52));
        Console.WriteLine($"  total nodes = {totalNodes:N0}   time = {totalMs:N0} ms   {totalNps:N0} nps");
        Console.WriteLine($"  allocated   = {allocBytes / (1024.0 * 1024.0):N1} MB  ({allocBytes / Math.Max(1, totalNodes)} bytes/node)");
        Console.WriteLine($"  (warm-up alloc excluded: {(allocAfterWarm - allocStart) / (1024.0 * 1024.0):N1} MB)");
        return 0;
    }

    private static async Task<int> SubstrateTestAsync(string[] args)
    {
        string mode = ArgStr(args, "--mode", "transition").ToLowerInvariant();
        int games = ArgInt(args, "--games", 200);
        int depth = ArgInt(args, "--depth", 4);
        int maxPlies = ArgInt(args, "--max-plies", 160);
        int concurrency = ArgInt(args, "--concurrency", 16);
        bool seedOpenings = HasFlag(args, "--openings");
        bool record = !HasFlag(args, "--no-record");

        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest);
        var exactBias = new SubstrateRootBias(ds);
        MoveChooser GuidedSearch()
        {
            var search = new Search(
                EvalTerm.All, exactBias, ttBits: 16,
                tablebase: ChessTablebaseRuntime.ProbeSearch);
            return (state, rng) => search.Think(
                state, new Search.Limits(MaxDepth: depth)).BestMove!.Value;
        }
        Func<MoveChooser> guided = mode switch
        {
            "transition" or "fold" or "edge" => GuidedSearch,
            "off" => MatchRunner.SearcherFactory(
                depth, EvalTerm.All, ttBits: 16,
                tablebase: ChessTablebaseRuntime.ProbeSearch),
            _ => throw new ArgumentException($"unknown --mode '{mode}' (expected transition|off)"),
        };
        var pure = MatchRunner.SearcherFactory(
            depth, EvalTerm.All, bias: null,
            tablebase: ChessTablebaseRuntime.ProbeSearch);

        var book = seedOpenings
            ? OpeningSeed.Fens(ArgStr(args, "--openings-dir", "") is { Length: > 0 } openingsDir ? openingsDir : null,
                plies: ArgInt(args, "--openings-plies", 10))
            : null;

        string desc = mode switch
        {
            "transition" or "fold" or "edge" => "full-depth conventional + the standings of the moves recorded games played",
            _ => "NO prior (sanity)",
        };
        Console.WriteLine($"substrate-test [{mode}]: guided ({desc}) vs pure classical");
        Console.WriteLine($"  depth {depth}, {games} games, maxPlies {maxPlies}, concurrency {concurrency}, "
            + $"openings {(seedOpenings ? $"suite({book!.Count})" : "random")}, record={record}, db={LaplaceInstall.RedactConnectionString(ChessEngineService.ResolveConnString())}");
        string pgnOut = ArgStr(args, "--pgn-out", "");
        var sink = string.IsNullOrEmpty(pgnOut)
            ? null
            : new System.Collections.Concurrent.ConcurrentBag<MatchPgnGame>();
        await using var recorder = record
            ? await ChessLabRecorder.OpenAsync("chess/cli/substrate-test")
            : null;
        var r = MatchRunner.Play(guided, pure, games, maxPlies, seed: 99, concurrency: concurrency,
                                 openingFens: book, pgnSink: sink,
                                 liveHost: recorder?.Host,
                                 liveLearnContext: $"chess/cli/substrate-test/{mode}",
                                 aPlayerName: "Laplace", bPlayerName: "Classical-control",
                                 eventName: $"Laplace CLI substrate lift ({mode})",
                                 externalIdPrefix: $"laplace-cli/substrate/{mode}/seed-99");
        if (sink is not null)
        {
            ChessPgnWriter.WriteFile(pgnOut, sink, @event: "substrate-test");
            Console.WriteLine($"  wrote {sink.Count} games -> {pgnOut}   (loop closure: laplace ingest chess \"{pgnOut}\")");
        }
        if (recorder is not null)
            Console.WriteLine($"  recorded {recorder.Host.GamesCompleted} games to substrate (chess/cli/substrate-test)");
        string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
        Console.WriteLine($"  guided W-D-L: {r.AWins}-{r.Draws}-{r.BWins}   score {r.Score:F3}   Elo {elo} +/- {r.Margin95:F0}");
        Console.WriteLine($"  search depth={depth}, transition frontiers={exactBias.RootReads:N0}, transition backend reads={exactBias.BackendReads:N0}, exact-evidence roots={exactBias.RootsWithExactEvidence:N0}, exact signals={exactBias.ExactTransitionSignals:N0}, syzygy={ChessTablebaseRuntime.Largest}-men");
        Console.WriteLine(r.EloDiff > 5
            ? "  => the substrate measurably raises the classical floor at this mode/scale."
            : "  => no clear lift at this mode/scale — try --openings, more games, deeper, or a larger --cp-per-point.");
        return 0;
    }

    private static bool HasFlag(string[] a, string flag) => Array.IndexOf(a, flag) >= 0;

    private static async Task<int> ReviewAsync(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
            return Fail("usage: laplace-mono chess review <pgn-file|dir> [--depth D] [--max-games N]");
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
        return 0;
    }

    private static string Short(string name) => string.IsNullOrEmpty(name) ? "?" : (name.Length > 16 ? name[..16] : name);

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

    private static async Task<int> LadderAsync(string[] args)
    {
        int games = ArgInt(args, "--games", 100);
        int depth = ArgInt(args, "--depth", 4);
        int maxPlies = ArgInt(args, "--max-plies", 160);
        int concurrency = ArgInt(args, "--concurrency", 16);
        bool seedOpenings = HasFlag(args, "--openings");
        bool record = !HasFlag(args, "--no-record");
        var book = seedOpenings
            ? OpeningSeed.Fens(ArgStr(args, "--openings-dir", "") is { Length: > 0 } openingsDir ? openingsDir : null,
                plies: ArgInt(args, "--openings-plies", 10))
            : null;

        var terms = new[]
        {
            EvalTerm.Material, EvalTerm.Pst, EvalTerm.BishopPair,
            EvalTerm.RookFiles, EvalTerm.PawnStructure, EvalTerm.Tempo,
        };
        Console.WriteLine($"overlay-ablation ladder: full eval vs eval-minus-term, depth {depth}, {games} games each, "
            + $"openings {(seedOpenings ? $"suite({book!.Count})" : "random")}, record={record}");
        await using var recorder = record
            ? await ChessLabRecorder.OpenAsync("chess/cli/ladder")
            : null;
        Console.WriteLine($"  {"term",-14} {"W-D-L",-12} {"Elo",7}  {"+/-",5}");
        foreach (var t in terms)
        {
            var full = MatchRunner.SearcherFactory(depth, EvalTerm.All);
            var minus = MatchRunner.SearcherFactory(depth, EvalTerm.All & ~t);
            var r = MatchRunner.Play(full, minus, games, maxPlies, seed: 7, concurrency: concurrency, openingFens: book,
                liveHost: recorder?.Host,
                liveLearnContext: $"chess/cli/ladder/{t}",
                aPlayerName: "Laplace-full", bPlayerName: $"Laplace-minus-{t}",
                eventName: $"Laplace CLI evaluation ladder ({t})",
                externalIdPrefix: $"laplace-cli/ladder/{t}/seed-7");
            string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
            Console.WriteLine($"  {t,-14} {$"{r.AWins}-{r.Draws}-{r.BWins}",-12} {elo,7}  {r.Margin95,5:F0}");
        }
        if (recorder is not null)
            Console.WriteLine($"  recorded {recorder.Host.GamesCompleted} games to substrate (chess/cli/ladder)");
        Console.WriteLine("  (positive Elo = removing that overlay WEAKENS the engine, i.e. the overlay helps)");
        return 0;
    }

    private static async Task<int> SelfPlayAsync(string[] args)
    {
        int games = ArgInt(args, "--games", 200);
        int maxPlies = ArgInt(args, "--max-plies", 200);
        int reportEvery = ArgInt(args, "--report-every", 25);
        bool weak = HasFlag(args, "--weak");

        Action<ChessTrainStatus> report = st =>
            Console.WriteLine($"  [{st.Games}] W={st.White} B={st.Black} D={st.Draws} (adj {st.Adjudicated}); last {st.LastOutcome}");

        await using var liveHost = await ChessLiveGameHost.CreateAsync();
        await using var svc = new ChessEngineService(
            ArgDouble(args, "--weight", 0.5d), liveHost);

        if (weak)
        {
            double temp = ArgDouble(args, "--temp", 120d);
            Console.WriteLine($"chess selfplay [WEAK: depth-1 substrate]: {games} games, temp={temp}, maxPlies={maxPlies}");
            await svc.RunSelfPlayAsync(games, temp, ArgInt(args, "--max-plies", 400), reportEvery, report);
        }
        else
        {

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
        if (fen.Length == 0) return Fail("usage: laplace-mono chess move <fen>");

        await using var liveHost = await ChessLiveGameHost.CreateAsync();
        await using var svc = new ChessEngineService(0.5d, liveHost);
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
            return Fail("usage: laplace-mono chess fetch <username> [--site chesscom|lichess] [--max N] [--min-tc SECONDS] [--out <path>]\n"
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

    /// <summary>
    /// The Lichess connector lives in laplace-uci (<c>laplace-uci lichess</c>); this runs it with the same arguments in
    /// this console, so there is one implementation. Ctrl-C reaches both processes: this one waits while laplace-uci
    /// finishes its in-flight games.
    /// </summary>
    private static async Task<int> LichessAsync(string[] args)
        => await RunUciAsync(["lichess", .. args]);

    private static async Task<int> RunUciAsync(string[] args)
    {
        if (args.Length == 0) return Fail("usage: laplace-mono chess lab <check|match|ladder|analyse|inspect> [options]");
        string? uci = ChessLabPaths.LaplaceUciExecutable();
        if (uci is null)
            return Fail("The selected monorepo installation has no UCI runtime. Install the complete application package or declare LAPLACE_UCI.");
        var psi = new System.Diagnostics.ProcessStartInfo(uci) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        ConsoleCancelEventHandler cancel = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += cancel;
        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return Fail($"could not start {uci}");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
