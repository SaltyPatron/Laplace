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
        int games = int.Parse(Config(cfg, "games", "20"));
        int depth = int.Parse(Config(cfg, "depth", "4"));
        int maxPlies = int.Parse(Config(cfg, "maxPlies", "160"));
        int concurrency = ResolveConcurrency(cfg);
        bool openings = Config(cfg, "openings", "false") == "true";
        bool persistPgn = ChessLabStorage.PersistPgn(cfg);

        var liveHost = await lab.GetLiveHostAsync(ct);

        lab.Publish(slot, new ChessLabLogEvent("info",
            $"substrate-test [{mode}] {games} games depth {depth} — recording to substrate"));

        var ds = liveHost.DataSource;
        var exactBias = new SubstrateRootBias(ds);
        Func<Board, SearchTablebaseVerdict?> tablebase = ChessTablebaseRuntime.ProbeSearch;
        Func<MoveChooser> guided = mode == "off"
            ? () => SearchChooser(depth, null, null, tablebase, ct)
            : () => SearchChooser(depth, exactBias, null, tablebase, ct);
        Func<MoveChooser> pure = () => SearchChooser(depth, null, null, tablebase, ct);
        var book = openings ? OpeningSeed.Fens(OpeningSeed.DefaultDir) : null;
        ConcurrentBag<MatchPgnGame>? pgnSink = persistPgn ? new() : null;

        var progress = new Progress<(int Done, int AWins, int Draws, int BWins)>(p =>
        {
            lab.UpdateSummary(slot, new ChessLabJobSummary(p.Done, games, $"W{p.AWins}-D{p.Draws}-L{p.BWins}"));
            lab.Publish(slot, new ChessLabProgressEvent(p.Done, games));
        });

        var r = await Task.Run(() => MatchRunner.Play(
            guided, pure, games, maxPlies, seed: 99, concurrency: concurrency,
            openingFens: book, pgnSink: pgnSink, progress: progress, ct: ct,
            liveHost: liveHost,
            liveLearnContext: $"chess/lab/substrate-test/{mode}",
            onPly: LiveBoardPublisher(lab, slot, "Laplace", "Classical control"),
            aPlayerName: "Laplace",
            bPlayerName: "Classical-control",
            eventName: $"Laplace substrate lift ({mode})",
            externalIdPrefix: $"laplace-lab/substrate/{mode}/seed-99"), ct);

        if (pgnSink is not null)
            AddPgnArtifact(lab, slot, pgnSink, $"chess-lab/substrate-test/{mode}");

        int recorded = r.AWins + r.Draws + r.BWins;
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
        lab.UpdateSummary(slot, new ChessLabJobSummary(games, games, $"Elo {elo} · {recorded} recorded"));
        Finish(lab, slot, ChessLabJobState.Completed);
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
        int games = int.Parse(Config(slot.Job.Config, "games", "20"));
        int depth = int.Parse(Config(slot.Job.Config, "depth", "4"));
        int maxPlies = int.Parse(Config(slot.Job.Config, "maxPlies", "160"));
        int budget = ResolveConcurrency(slot.Job.Config);
        bool record = bool.TryParse(Config(slot.Job.Config, "record", "false"), out bool recordValue) && recordValue;
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

        await Task.Run(() =>
        {
            Parallel.For(0, terms.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = terms.Length,
                CancellationToken = ct,
            }, ti =>
            {
                var term = terms[ti];
                lab.Publish(slot, new ChessLabLogEvent("info", $"term {ti + 1}/{terms.Length}: {term}"));

                var full = MatchRunner.SearcherFactory(depth, EvalTerm.All, ct: ct);
                var minus = MatchRunner.SearcherFactory(depth, EvalTerm.All & ~term, ct: ct);
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

                var r = MatchRunner.Play(full, minus, games, maxPlies, seed: 7 + ti, concurrency: perTerm,
                    pgnSink: pgnSink, progress: progress, ct: ct,
                    liveHost: liveHost,
                    liveLearnContext: $"chess/lab/ladder/{term}",
                    onPly: LiveBoardPublisher(lab, slot, "Laplace-full", $"minus-{term}"),
                    aPlayerName: "Laplace-full",
                    bPlayerName: $"Laplace-minus-{term}",
                    eventName: $"Laplace evaluation ladder ({term})",
                    externalIdPrefix: $"laplace-lab/ladder/{term}/seed-{7 + ti}");
                string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
                rows[ti] = [term.ToString(), $"{r.AWins}-{r.Draws}-{r.BWins}", elo];
                lab.Publish(slot, new ChessLabLogEvent("info", $"{term}: {r.AWins}-{r.Draws}-{r.BWins} Elo {elo}"));
            });
        }, ct);

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
            int depth = int.Parse(Config(slot.Job.Config, "depth", "6"));
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
            int depth = int.Parse(Config(slot.Job.Config, "depth", "4"));
            int max = int.Parse(Config(slot.Job.Config, "maxGames", "10"));
            var games = ChessGameReview.ReviewFile(path, depth, max);
            var rows = games.Select(g => (IReadOnlyList<string>)[
                Short(g.White), Short(g.Black),
                g.Result?.IsDraw == true ? "1/2" : g.Result?.Winner == 0 ? "1-0" : "0-1",
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
        bool ingest = bool.TryParse(Config(cfg, "ingest", "true"), out bool ingestValue) && ingestValue;
        bool persistPgn = ChessLabStorage.PersistPgn(cfg, defaultValue: !ingest);
        bool persistTranscript = ChessLabStorage.PersistTranscript(cfg);
        var spoolDir = ChessLabStorage.CreateJobSpool(slot.Job.Id);
        var pgnOut = Path.Combine(spoolDir, "games.pgn");

        var options = new CutechessOptions
        {
            Rounds = int.Parse(Config(cfg, "rounds", "10")),
            Depth = int.Parse(Config(cfg, "depth", "0")),
            SecondsPerMove = double.Parse(Config(cfg, "st", "1"), System.Globalization.CultureInfo.InvariantCulture),
            StockfishElo = int.Parse(Config(cfg, "elo", "2000")),
            StockfishLimitStrength = bool.Parse(Config(cfg, "limitStrength", "true")),
            Concurrency = Math.Max(1, int.Parse(Config(cfg, "concurrency", "1"))),
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
