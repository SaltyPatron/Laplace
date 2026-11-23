using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Microsoft.Extensions.Logging;

namespace Laplace.Chess.Service;

public static partial class ChessLabRunners
{
    public static string LabDir => ChessLabPaths.LabDir;

    public static async Task RunSubstrateTestAsync(
        ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        var cfg = slot.Job.Config;
        string mode = Config(cfg, "mode", "transition");
        int games = ParseInt(cfg, "games", 20, 1, 1000);
        int depth = ParseInt(cfg, "depth", 4, 1, 20);
        int maxPlies = ParseInt(cfg, "maxPlies", 160, 1, 1000);
        int concurrency = ResolveConcurrency(cfg);
        bool openings = ParseBool(cfg, "openings", false);
        bool persistPgn = ChessLabStorage.PersistPgn(cfg);

        var liveHost = await lab.GetLiveHostAsync(ct);

        var ds = liveHost.DataSource;
        var nativeRuntime = mode == "off"
            || string.Equals(Environment.GetEnvironmentVariable("LAPLACE_CHESS_NATIVE"), "0", StringComparison.Ordinal)
            ? null
            : lab.ResolveNativeRuntime(ds);

        lab.Publish(slot, new ChessLabLogEvent("info",
            $"substrate-test [{mode}] {games} games depth {depth} ({(nativeRuntime is not null ? "native-c" : "managed")}) — recording to substrate"));

        var exactBias = new SubstrateRootBias(ds);
        Func<Board, SearchTablebaseVerdict?> tablebase = ChessTablebaseRuntime.ProbeSearch;
        Func<MoveChooser> guided = mode == "off"
            ? () => SearchChooser(depth, null, null, tablebase, ct)
            : nativeRuntime is not null
                ? () => NativeOrSearchChooser(nativeRuntime, depth, exactBias, tablebase, lab.Log, ct)
                : () => SearchChooser(depth, exactBias, null, tablebase, ct);
        Func<MoveChooser> pure = () => SearchChooser(depth, null, null, tablebase, ct);
        var book = openings ? OpeningSeed.Fens(OpeningSeed.DefaultDir) : null;
        ConcurrentBag<MatchPgnGame>? pgnSink = persistPgn ? new() : null;

        var progress = new Progress<(int Done, int AWins, int Draws, int BWins)>(p =>
        {
            lab.UpdateSummary(slot, new ChessLabJobSummary(p.Done, games, $"W{p.AWins}-D{p.Draws}-L{p.BWins}"));
            lab.Publish(slot, new ChessLabProgressEvent(p.Done, games));
        });

        var r = await MatchRunner.PlayAsync(
            guided, pure, games, maxPlies, seed: 99, concurrency: concurrency,
            openingFens: book, pgnSink: pgnSink, progress: progress, ct: ct,
            liveHost: liveHost,
            liveLearnContext: $"chess/lab/substrate-test/{mode}",
            onPly: LiveBoardPublisher(lab, slot, "Laplace", "Classical control"),
            aPlayerName: "Laplace",
            bPlayerName: "Classical-control",
            eventName: $"Laplace substrate lift ({mode})",
            externalIdPrefix: $"laplace-lab/substrate/{mode}/seed-99").ConfigureAwait(false);

        if (pgnSink is not null)
            AddPgnArtifact(lab, slot, pgnSink, $"chess-lab/substrate-test/{mode}");

        int recorded = r.Games;
        string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
        lab.Publish(slot, new ChessLabMetricEvent("games_recorded", recorded));
        lab.Publish(slot, new ChessLabMetricEvent("elo_diff", r.EloDiff));
        lab.Publish(slot, new ChessLabMetricEvent("search_depth", depth));
        lab.Publish(slot, new ChessLabMetricEvent("transition_trunk_reads", exactBias.RootReads));
        lab.Publish(slot, new ChessLabMetricEvent("transition_backend_reads", exactBias.BackendReads));
        lab.Publish(slot, new ChessLabMetricEvent("transition_frontier_builds", exactBias.FrontierBuilds));
        lab.Publish(slot, new ChessLabMetricEvent("transition_evidence_cache_hits", exactBias.EvidenceCacheHits));
        lab.Publish(slot, new ChessLabMetricEvent("exact_transition_roots", exactBias.RootsWithExactEvidence));
        lab.Publish(slot, new ChessLabMetricEvent("exact_transition_signals", exactBias.ExactTransitionSignals));
        lab.Publish(slot, new ChessLabMetricEvent("syzygy_max_men", ChessTablebaseRuntime.Largest));
        lab.Publish(slot, new ChessLabTableEvent("substrate-test", ["W", "D", "L", "Elo"],
            [[r.AWins.ToString(), r.Draws.ToString(), r.BWins.ToString(), elo]]));
        lab.UpdateSummary(slot, new ChessLabJobSummary(games, games,
            r.Incomplete > 0
                ? $"Elo {elo} · {recorded} recorded ({r.Incomplete} incomplete)"
                : $"Elo {elo} · {recorded} recorded"));
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    private static MoveChooser NativeOrSearchChooser(
        ILaplaceEngineRuntime nativeRuntime, int depth, IRootBias? bias,
        Func<Board, SearchTablebaseVerdict?> tablebase, ILogger log, CancellationToken ct)
    {
        MoveChooser? fallback = null;
        return (state, rng) =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string? variant = state.Board.Rules.IsStandard ? null : state.Board.Rules.Law;
                var response = nativeRuntime.ChessPlay(new EngineChessRequest(
                    Fen: state.Board.ToFen(),
                    Variant: variant,
                    MaxDepth: depth,
                    QuiesceDepth: 4,
                    MaxNodes: 200_000,
                    MaxWallUs: 250_000));
                if (response.StatusCode == 0
                    && ChessEngineService.ResolveNativeMove(state.Board, response.BestUci) is { } mv)
                {
                    return mv;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or ArgumentException)
            {
                log.LogDebug(ex, "Native lp_engine_chess_play unavailable in substrate-test; falling back to managed Search.");
            }

            fallback ??= SearchChooser(depth, bias, null, tablebase, ct);
            return fallback(state, rng);
        };
    }

    private static MoveChooser SearchChooser(
        int depth, IRootBias? bias, ISearchPositionEvaluator? positionEvaluator,
        Func<Board, SearchTablebaseVerdict?> tablebase, CancellationToken ct)
    {
        var search = new Search(
            EvalTerm.All, bias, ttBits: 16,
            positionEvaluator: positionEvaluator, tablebase: tablebase);
        return (state, rng) => search.Think(
            state, new Search.Limits(MaxDepth: depth), ct).BestMove!.Value;
    }

    public static async Task RunLadderAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        int games = ParseInt(slot.Job.Config, "games", 20, 1, 1000);
        int depth = ParseInt(slot.Job.Config, "depth", 4, 1, 20);
        int maxPlies = ParseInt(slot.Job.Config, "maxPlies", 160, 1, 1000);
        int budget = ResolveConcurrency(slot.Job.Config);
        bool record = ParseBool(slot.Job.Config, "record", false);
        bool persistPgn = ChessLabStorage.PersistPgn(slot.Job.Config);
        var terms = new[]
        {
            EvalTerm.Material, EvalTerm.Pst, EvalTerm.BishopPair,
            EvalTerm.RookFiles, EvalTerm.PawnStructure, EvalTerm.Tempo,
        };
        int perTerm = Math.Max(1, budget / terms.Length);
        int totalGames = games * terms.Length;

        var liveHost = record ? await lab.GetLiveHostAsync(ct) : null;
        ConcurrentBag<MatchPgnGame>? pgnSink = persistPgn ? new() : null;

        lab.Publish(slot, new ChessLabLogEvent("info",
            $"ladder depth {depth} × {games} games × {terms.Length} terms "
            + $"(parallel terms, {perTerm} games/term-slot, {budget} core budget, "
            + (record ? "recording to substrate)" : "read-only)")));
        lab.UpdateSummary(slot, new ChessLabJobSummary(0, totalGames, "starting"));

        var rows = new IReadOnlyList<string>?[terms.Length];
        var termDone = new int[terms.Length];
        var progressLock = new object();

        await Parallel.ForEachAsync(Enumerable.Range(0, terms.Length), new ParallelOptions
        {
            MaxDegreeOfParallelism = terms.Length,
            CancellationToken = ct,
        }, async (ti, token) =>
        {
            var term = terms[ti];
            lab.Publish(slot, new ChessLabLogEvent("info", $"term {ti + 1}/{terms.Length}: {term}"));

            var full = MatchRunner.SearcherFactory(depth, EvalTerm.All, ct: token);
            var minus = MatchRunner.SearcherFactory(depth, EvalTerm.All & ~term, ct: token);
            var progress = new Progress<(int Done, int AWins, int Draws, int BWins)>(p =>
            {
                int overall;
                lock (progressLock)
                {
                    termDone[ti] = p.Done;
                    overall = 0;
                    foreach (var d in termDone) overall += d;
                    lab.UpdateSummary(slot, new ChessLabJobSummary(
                        overall, totalGames, $"{term}: {p.AWins}-{p.Draws}-{p.BWins}"));
                }
                lab.Publish(slot, new ChessLabProgressEvent(overall, totalGames, term.ToString()));
            });

            var r = await MatchRunner.PlayAsync(full, minus, games, maxPlies, seed: 7 + ti, concurrency: perTerm,
                pgnSink: pgnSink, progress: progress, ct: token,
                liveHost: liveHost,
                liveLearnContext: $"chess/lab/ladder/{term}",
                onPly: LiveBoardPublisher(lab, slot, "Laplace-full", $"minus-{term}"),
                aPlayerName: "Laplace-full",
                bPlayerName: $"Laplace-minus-{term}",
                eventName: $"Laplace evaluation ladder ({term})",
                externalIdPrefix: $"laplace-lab/ladder/{term}/seed-{7 + ti}").ConfigureAwait(false);
            string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
            rows[ti] = [term.ToString(), $"{r.AWins}-{r.Draws}-{r.BWins}", elo];
            lab.Publish(slot, new ChessLabLogEvent("info", $"{term}: {r.AWins}-{r.Draws}-{r.BWins} Elo {elo}"));
        }).ConfigureAwait(false);

        if (pgnSink is not null)
            AddPgnArtifact(lab, slot, pgnSink, "chess-lab/ladder");

        lab.Publish(slot, new ChessLabMetricEvent("games_generated", totalGames));
        lab.Publish(slot, new ChessLabMetricEvent("games_recorded", record ? totalGames : 0));
        lab.Publish(slot, new ChessLabTableEvent("overlay ladder", ["term", "W-D-L", "Elo"],
            rows.Select(r => r!).ToList()));
        lab.UpdateSummary(slot, new ChessLabJobSummary(totalGames, totalGames,
            record ? $"complete · {totalGames} recorded" : $"complete · {totalGames} generated · read-only"));
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    public static Task RunTacticsAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
        => Task.Run(() =>
        {
            int depth = ParseInt(slot.Job.Config, "depth", 6, 1, 20);
            var (solved, total, results) = ChessTactics.Run(ChessTactics.Builtin, depth);
            lab.Publish(slot, new ChessLabMetricEvent("solve_rate", total > 0 ? 100.0 * solved / total : 0, "%"));
            lab.Publish(slot, new ChessLabTableEvent("tactics", ["id", "ok", "engine", "expected"],
                results.Select(r => (IReadOnlyList<string>)[r.Id, r.Solved ? "ok" : "miss", r.Engine, r.Expected]).ToList()));
            Finish(lab, slot, ChessLabJobState.Completed);
        }, ct);

    public static Task RunReviewAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
        => Task.Run(() =>
        {
            string path = Config(slot.Job.Config, "path", "");
            if (!IsReviewablePath(path))
            {
                lab.Publish(slot, new ChessLabLogEvent("error",
                    $"path must be a .pgn under the lab dir or the chess games dir (got '{path}')"));
                Finish(lab, slot, ChessLabJobState.Failed, "path outside allowed dirs");
                return;
            }
            int depth = ParseInt(slot.Job.Config, "depth", 4, 1, 20);
            int max = ParseInt(slot.Job.Config, "maxGames", 10, 1, 1000);
            var games = ChessGameReview.ReviewFile(path, depth, max);
            var rows = games.Select(g => (IReadOnlyList<string>)[
                Short(g.White), Short(g.Black),
                g.Result is { IsUnknown: true } ? "*" : g.Result?.IsDraw == true ? "1/2" : g.Result?.Winner == 0 ? "1-0" : "0-1",
                g.WhiteAcpl.ToString("F0"), g.BlackAcpl.ToString("F0"),
                g.CrazyWin ? "crazy" : ""]).ToList();
            lab.Publish(slot, new ChessLabTableEvent("review", ["white", "black", "res", "wAcpl", "bAcpl", "flag"], rows));
            Finish(lab, slot, ChessLabJobState.Completed);
        }, ct);

    public static async Task RunCutechessAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        var outcome = await RunCutechessCoreAsync(lab, slot, ct);
        Finish(lab, slot, outcome.State, outcome.Message);
    }

    private static async Task<(ChessLabJobState State, string? Message)> RunCutechessCoreAsync(
        ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        var cfg = slot.Job.Config;
        bool ingest = ParseBool(cfg, "ingest", true);
        bool persistPgn = ChessLabStorage.PersistPgn(cfg, defaultValue: !ingest);
        bool persistTranscript = ChessLabStorage.PersistTranscript(cfg);
        var spoolDir = ChessLabStorage.CreateJobSpool(slot.Job.Id);
        var pgnOut = Path.Combine(spoolDir, "games.pgn");

        int concCap = Math.Max(1, Environment.ProcessorCount / 2);
        var options = new CutechessOptions
        {
            Rounds = ParseInt(cfg, "rounds", 10, 1, 1000),
            Depth = ParseInt(cfg, "depth", 0, 0, 20),
            SecondsPerMove = ParseDouble(cfg, "st", 1.0, 0.05, 3600.0),
            StockfishElo = ParseInt(cfg, "elo", 2000, 100, 4000),
            StockfishLimitStrength = ParseBool(cfg, "limitStrength", true),
            Concurrency = Math.Clamp(ParseInt(cfg, "concurrency", 1, 1, 256), 1, concCap),
            Conductor = Config(cfg, "conductor", "fastchess").Trim().ToLowerInvariant(),
            Affinity = cfg.TryGetValue("affinity", out var affinity) && !string.IsNullOrWhiteSpace(affinity) ? affinity.Trim() : null,
            PgnOut = pgnOut,
            Event = $"chess-lab/cutechess/{slot.Job.Id}",
        }.WithStockfishConfiguration(cfg);
        options = ChessEngineConnections.Select(options, cfg)
            .WithInstalledSyzygy(cfg, ChessLabPaths.InstalledSyzygyEnginePath);
        options.ValidateStockfishConfiguration();

        // PGN Event already carries this stable experiment id. Keep its original description;
        // the same id addresses the exact configuration/artifact receipt after PGN cleanup.
        var receipt = new CutechessExperimentReceipt(slot.Job.Id, options, cfg) { Requester = slot.Job.Requester };
        string receiptPath = Path.Combine(spoolDir, "experiment.json");
        string? persistedReceiptPath = null;

        var final = ChessLabJobState.Completed;
        string? finalMessage = null;
        StreamWriter? transcript = null;
        string? transcriptPath = null;
        if (persistTranscript)
        {
            var transcriptDir = Path.Combine(LabDir, slot.Job.Id);
            Directory.CreateDirectory(transcriptDir);
            transcriptPath = Path.Combine(transcriptDir, "transcript.log");
            transcript = new StreamWriter(transcriptPath) { AutoFlush = false };
        }

        long playStarted = Stopwatch.GetTimestamp();
        try
        {
            await foreach (var evt in CutechessRunner.RunAsync(options, ct))
            {
                await receipt.ObserveAsync(evt, ct);
                if (evt is ChessLabCommandEvent) await receipt.WriteAsync(receiptPath);
                switch (evt)
                {
                    case ChessLabTerminalEvent terminal:
                    {
                        var line = ChessLabTerminal.Format(lab.AppendTerminal(slot, terminal));
                        if (transcript is not null) await transcript.WriteLineAsync(line);
                        break;
                    }
                    case ChessLabProgressEvent prog:
                        lab.UpdateSummary(slot, new ChessLabJobSummary(prog.Done, prog.Total, prog.Label));
                        lab.Publish(slot, prog);
                        break;
                    case ChessLabDoneEvent done:
                        final = done.FinalState;
                        finalMessage = done.Message;
                        break;
                    default:
                        lab.Publish(slot, evt);
                        break;
                }
            }
            if (!await receipt.VerifyArtifactsAsync(ct))
            {
                final = ChessLabJobState.Failed;
                finalMessage = "experiment artifacts changed during the match; configuration identity could not be verified";
                receipt.Complete(final, finalMessage);
                lab.Publish(slot, new ChessLabLogEvent("error", finalMessage));
            }
        }
        catch (OperationCanceledException)
        {
            receipt.Complete(ChessLabJobState.Cancelled, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            receipt.Complete(ChessLabJobState.Failed, ex.Message);
            throw;
        }
        finally
        {
            lab.Publish(slot, new ChessLabMetricEvent("play_seconds", Stopwatch.GetElapsedTime(playStarted).TotalSeconds));
            if (transcript is not null)
            {
                await transcript.FlushAsync(CancellationToken.None);
                await transcript.DisposeAsync();
                lab.AddArtifact(slot, "transcript.log", transcriptPath!);
            }
            await receipt.WriteAsync(receiptPath);
            persistedReceiptPath = ChessLabStorage.PersistArtifact(receiptPath, LabDir, slot.Job.Id, "experiment.json");
            lab.AddArtifact(slot, "experiment.json", persistedReceiptPath);
            if ((receipt.MatchState is ChessLabJobState.Failed or ChessLabJobState.Cancelled) && File.Exists(pgnOut))
                lab.AddArtifact(slot, "games.pgn", pgnOut);
        }

        bool ingestSucceeded = !ingest;
        try
        {
            if (final == ChessLabJobState.Completed && !File.Exists(pgnOut))
            {
                final = ChessLabJobState.Failed;
                finalMessage = "completed gauntlet did not produce games.pgn";
                lab.Publish(slot, new ChessLabLogEvent("error", finalMessage));
            }
            if (final == ChessLabJobState.Completed && ingest)
            {
                ChessLabIngestRun? journal = null;
                try
                {
                    int games = PgnGames.StreamGames(pgnOut).Count();
                    if (games != options.Rounds)
                        throw new InvalidDataException($"expected {options.Rounds} PGN games but the match wrote {games}");
                    lab.Publish(slot, new ChessLabLogEvent("info", "recording games.pgn through the Engine (witness Laplace)…"));
                    var liveHost = await lab.GetLiveHostAsync(ct);
                    journal = await ChessLabIngestRun.StartAsync(
                        liveHost, lab.Log, slot.Job.Id, $"cutechess/{slot.Job.Id}", ChessGameRecorder.Witness, ct);
                    await journal.IngestUnitAsync("games.pgn", pgnOut, ct);
                    lab.Publish(slot, new ChessLabMetricEvent("games_ingested", games));
                    ingestSucceeded = true;
                    journal.Complete();
                }
                catch (OperationCanceledException) { journal?.Cancel(); throw; }
                catch (Exception ex)
                {
                    journal?.Fail(ex);
                    final = ChessLabJobState.Failed;
                    finalMessage = $"substrate ingest failed: {ex.Message}";
                    lab.Publish(slot, new ChessLabLogEvent("error",
                        $"substrate ingest failed ({ex.Message}) — PGN remains only in temporary spool for retry"));
                }
            }
        }
        finally
        {
            // Cancellation can arrive after some chunks were admitted. Keep the PGN
            // addressable for an idempotent retry and finish receipt bookkeeping anyway.
            if (File.Exists(pgnOut))
            {
                if (persistPgn)
                {
                    var artifact = ChessLabStorage.PersistArtifact(pgnOut, LabDir, slot.Job.Id, "games.pgn");
                    lab.AddArtifact(slot, "games.pgn", artifact);
                }
                else if (ingestSucceeded && final == ChessLabJobState.Completed)
                {
                    ChessLabStorage.DeleteFile(pgnOut);
                }
                else
                {
                    lab.AddArtifact(slot, "games.pgn", pgnOut);
                }
            }
            ChessLabStorage.DeleteDirectoryIfEmpty(spoolDir);
            receipt.Ingested = ingest ? ingestSucceeded : null;
            await receipt.WriteAsync(persistedReceiptPath!);
        }

        return (final, finalMessage);
    }
}
