using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

/// <summary>
/// Timer A: PGN → parse → Compose into SubstrateChange in RAM. Zero Postgres.
/// Hard gate: 20s. At 20s the run cancels and exits FAIL — 21s is already a fail.
/// </summary>
static class Program
{
    const double ProcessBudgetS = 20.0;

    static async Task<int> Main(string[] args)
    {
        string path = args.ElementAtOrDefault(0)
            ?? "/vault/Data/Games/Chess/Lumbras/otb/LumbrasGigaBase_OTB_2025.pgn";
        int limit = 0;
        if (args.Length > 1 && args[1] is not ("--" or "--no-analyze" or "--analyze" or "all"
                or "--serial" or "--workers"))
            _ = int.TryParse(args[1], out limit);
        bool analyze = !args.Contains("--no-analyze");
        bool peekOnly = args.Contains("--peek-only");
        bool serial = args.Contains("--serial");
        int workers = serial ? 1 : Math.Max(1, Environment.ProcessorCount);
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--workers" && int.TryParse(args[i + 1], out int w))
                workers = Math.Max(1, w);

        const int reportEvery = 10_000;

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Console.Error.WriteLine($"missing: {path}");
            return 2;
        }

        _ = ChessCompose.PositionId(
            Laplace.Modality.Chess.Board.FromFen(Laplace.Modality.Chess.ChessModality.StartFen));

        using var gateCts = new CancellationTokenSource(TimeSpan.FromSeconds(ProcessBudgetS));
        var ct = gateCts.Token;

        Console.WriteLine(
            $"chess-compose-bench path={path} limit={(limit == 0 ? "ALL" : limit.ToString())} "
            + $"analyzeInline={analyze} workers={workers} db=NONE "
            + $"timer_a_budget_s={ProcessBudgetS:F0} hard_cancel=true "
            + "ids=EventId(tournament)+PlayingId(novelty)+LineId(content)");

        var swTotal = Stopwatch.StartNew();
        long games = 0, parseFail = 0;
        long entities = 0, physicalities = 0, attestations = 0;
        long peakWorkingSet = 0;
        var eventIds = new ConcurrentDictionary<Hash128, byte>();
        var playingIds = new ConcurrentDictionary<Hash128, byte>();
        var lineIds = new ConcurrentDictionary<Hash128, byte>();
        bool timedOut = false;

        try
        {
            if (workers == 1)
            {
                await foreach (var gameText in ChessPgnDecomposer.StreamAllGamesAsync(
                                   path, SearchOption.TopDirectoryOnly, ct))
                {
                    if (peekOnly)
                    {
                        if (ChessPgnDecomposer.TryPeekPlaying(gameText) is not { } peek)
                        {
                            Interlocked.Increment(ref parseFail);
                            continue;
                        }
                        playingIds.TryAdd(peek.PlayingId, 0);
                        long gPeek = Interlocked.Increment(ref games);
                        Report(gPeek, parseFail, eventIds, playingIds, lineIds,
                            0, 0, 0, swTotal, ref peakWorkingSet, reportEvery);
                        if (limit > 0 && gPeek >= limit) break;
                        continue;
                    }
                    if (ChessPgnDecomposer.TryParseGame(gameText) is not { } parsed)
                    {
                        Interlocked.Increment(ref parseFail);
                        continue;
                    }
                    Accumulate(parsed, analyze, ref entities, ref physicalities, ref attestations,
                        eventIds, playingIds, lineIds);
                    long g = Interlocked.Increment(ref games);
                    Report(g, parseFail, eventIds, playingIds, lineIds,
                        entities, physicalities, attestations, swTotal, ref peakWorkingSet, reportEvery);
                    if (limit > 0 && g >= limit) break;
                }
            }
            else
            {
                var texts = Channel.CreateBounded<string>(new BoundedChannelOptions(workers * 8)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = true,
                    SingleReader = false,
                });
                var feeder = Task.Run(async () =>
                {
                    long fed = 0;
                    try
                    {
                        await foreach (var gameText in ChessPgnDecomposer.StreamAllGamesAsync(
                                           path, SearchOption.TopDirectoryOnly, ct))
                        {
                            await texts.Writer.WriteAsync(gameText, ct);
                            fed++;
                            if (limit > 0 && fed >= limit) break;
                        }
                    }
                    catch (OperationCanceledException) { /* gate */ }
                    finally { texts.Writer.TryComplete(); }
                }, ct);

                var workersTasks = new Task[workers];
                for (int w = 0; w < workers; w++)
                {
                    workersTasks[w] = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var gameText in texts.Reader.ReadAllAsync(ct))
                            {
                                if (peekOnly)
                                {
                                    if (ChessPgnDecomposer.TryPeekPlaying(gameText) is not { } peek)
                                    {
                                        Interlocked.Increment(ref parseFail);
                                        continue;
                                    }
                                    playingIds.TryAdd(peek.PlayingId, 0);
                                    long gPeek = Interlocked.Increment(ref games);
                                    Report(gPeek, parseFail, eventIds, playingIds, lineIds,
                                        0, 0, 0, swTotal, ref peakWorkingSet, reportEvery);
                                    if (limit > 0 && gPeek >= limit) break;
                                    continue;
                                }
                                if (ChessPgnDecomposer.TryParseGame(gameText) is not { } parsed)
                                {
                                    Interlocked.Increment(ref parseFail);
                                    continue;
                                }
                                long e = 0, p = 0, a = 0;
                                Accumulate(parsed, analyze, ref e, ref p, ref a,
                                    eventIds, playingIds, lineIds);
                                Interlocked.Add(ref entities, e);
                                Interlocked.Add(ref physicalities, p);
                                Interlocked.Add(ref attestations, a);
                                long g = Interlocked.Increment(ref games);
                                Report(g, parseFail, eventIds, playingIds, lineIds,
                                    Interlocked.Read(ref entities),
                                    Interlocked.Read(ref physicalities),
                                    Interlocked.Read(ref attestations),
                                    swTotal, ref peakWorkingSet, reportEvery);
                            }
                        }
                        catch (OperationCanceledException) { /* gate */ }
                    }, ct);
                }

                try { await feeder; await Task.WhenAll(workersTasks); }
                catch (OperationCanceledException) { timedOut = true; }
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
        }

        swTotal.Stop();
        double secs = Math.Max(1e-6, swTotal.Elapsed.TotalSeconds);
        if (ct.IsCancellationRequested) timedOut = true;
        bool finished = limit > 0 ? games >= limit : !timedOut && games > 0;
        // Whole-file / limit must finish inside 20s. Partial at timeout = FAIL.
        bool pass = finished && secs <= ProcessBudgetS && games > 0 && !timedOut;
        Console.WriteLine(
            $"DONE games={games:N0} parse_fail={parseFail} timed_out={timedOut} finished={finished} "
            + $"uniq_event={eventIds.Count:N0} uniq_playing={playingIds.Count:N0} "
            + $"uniq_line={lineIds.Count:N0} "
            + $"(event=tournament, playing=novelty, line=content Merkle) "
            + $"entities={entities:N0} physicalities={physicalities:N0} attestations={attestations:N0} "
            + $"wall_s={secs:F2} games_per_s={games / secs:F1} "
            + $"rows_per_game={(double)(entities + physicalities + attestations) / Math.Max(1, games):F0} "
            + $"peak_ws_mb={peakWorkingSet / (1024 * 1024)} "
            + $"TIMER_A_PROCESS_{(pass ? "PASS" : "FAIL")}_budget_s={ProcessBudgetS:F0}");
        return pass ? 0 : 1;
    }

    static void Accumulate(
        ChessGameRecord parsed, bool analyze,
        ref long entities, ref long physicalities, ref long attestations,
        ConcurrentDictionary<Hash128, byte> eventIds,
        ConcurrentDictionary<Hash128, byte> playingIds,
        ConcurrentDictionary<Hash128, byte> lineIds)
    {
        eventIds.TryAdd(parsed.EventId, 0);
        playingIds.TryAdd(parsed.PlayingId, 0);
        lineIds.TryAdd(parsed.LineId, 0);

        var b = new SubstrateChangeBuilder(
            ChessVocabulary.PgnSourceId, "chess/compose-bench");
        ChessPgnDecomposer.ComposeGame(parsed, b, analyzeInline: analyze);
        var change = b.Build();
        entities += change.Entities.Length;
        physicalities += change.Physicalities.Length;
        attestations += change.Attestations.Length;
    }

    static void Report(
        long games, long parseFail,
        ConcurrentDictionary<Hash128, byte> eventIds,
        ConcurrentDictionary<Hash128, byte> playingIds,
        ConcurrentDictionary<Hash128, byte> lineIds,
        long entities, long physicalities, long attestations,
        Stopwatch swTotal, ref long peakWorkingSet, int reportEvery)
    {
        if (games % reportEvery != 0) return;
        long ws = Environment.WorkingSet;
        long peak = Interlocked.Read(ref peakWorkingSet);
        while (ws > peak && Interlocked.CompareExchange(ref peakWorkingSet, ws, peak) != peak)
            peak = Interlocked.Read(ref peakWorkingSet);
        double gps = games / Math.Max(1e-6, swTotal.Elapsed.TotalSeconds);
        Console.WriteLine(
            $"progress games={games:N0} fail={parseFail} "
            + $"uniq_event={eventIds.Count:N0} uniq_playing={playingIds.Count:N0} "
            + $"uniq_line={lineIds.Count:N0} "
            + $"ent={entities:N0} phys={physicalities:N0} att={attestations:N0} "
            + $"rate={gps:F0}/s wall_s={swTotal.Elapsed.TotalSeconds:F1} "
            + $"ws_mb={ws / (1024 * 1024)}");
    }
}
