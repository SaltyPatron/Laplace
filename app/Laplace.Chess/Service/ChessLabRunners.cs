using System.Collections.Concurrent;
using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Microsoft.Extensions.Logging;

namespace Laplace.Chess.Service;

public static class ChessLabRunners
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

        var workDir = Path.Combine(LabDir, slot.Job.Id);
        Directory.CreateDirectory(workDir);
        var liveHost = await lab.GetLiveHostAsync(ct);

        lab.Publish(slot, new ChessLabLogEvent("info",
            $"substrate-test [{mode}] {games} games depth {depth} — recording to substrate"));

        var ds = liveHost.DataSource;
        var exactBias = new SubstrateRootBias(ds);
        var boardEvaluator = new SubstrateBoardEvaluator(ds);
        Func<Board, SearchTablebaseVerdict?> tablebase = ChessTablebaseRuntime.ProbeSearch;
        Func<MoveChooser> guided = mode == "off"
            ? () => SearchChooser(depth, null, null, tablebase, ct)
            : () => SearchChooser(depth, exactBias, boardEvaluator, tablebase, ct);
        Func<MoveChooser> pure = () => SearchChooser(depth, null, null, tablebase, ct);
        var book = openings ? OpeningSeed.Fens(OpeningSeed.DefaultDir) : null;
        var pgnSink = new ConcurrentBag<MatchPgnGame>();

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

        var pgnPath = Path.Combine(workDir, "games.pgn");
        ChessPgnWriter.WriteFile(pgnPath, pgnSink, @event: $"chess-lab/substrate-test/{mode}");
        lab.AddArtifact(slot, "games.pgn", pgnPath);

        int recorded = r.AWins + r.Draws + r.BWins;
        string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
        lab.Publish(slot, new ChessLabMetricEvent("games_recorded", recorded));
        lab.Publish(slot, new ChessLabMetricEvent("elo_diff", r.EloDiff));
        lab.Publish(slot, new ChessLabMetricEvent("search_depth", depth));
        lab.Publish(slot, new ChessLabMetricEvent("transition_trunk_reads", exactBias.RootReads));
        lab.Publish(slot, new ChessLabMetricEvent("transition_backend_reads", exactBias.BackendReads));
        lab.Publish(slot, new ChessLabMetricEvent("exact_transition_roots", exactBias.RootsWithExactEvidence));
        lab.Publish(slot, new ChessLabMetricEvent("move_physicality_roots", exactBias.RootsWithMoveEvidence));
        lab.Publish(slot, new ChessLabMetricEvent("exact_transition_signals", exactBias.ExactTransitionSignals));
        lab.Publish(slot, new ChessLabMetricEvent("move_physicality_signals", exactBias.MovePhysicalitySignals));
        lab.Publish(slot, new ChessLabMetricEvent("transition_perfcache_hits", exactBias.TransitionPerfcacheHits));
        lab.Publish(slot, new ChessLabMetricEvent("transition_novel_hits", exactBias.TransitionNovelHits));
        lab.Publish(slot, new ChessLabMetricEvent("transition_compositions", exactBias.TransitionCompositions));
        lab.Publish(slot, new ChessLabMetricEvent("child_structure_reads", boardEvaluator.PositionReads));
        lab.Publish(slot, new ChessLabMetricEvent("child_structure_signals", boardEvaluator.PositionsWithEvidence));
        lab.Publish(slot, new ChessLabMetricEvent("position_atoms_loaded", boardEvaluator.LoadedAtoms));
        lab.Publish(slot, new ChessLabMetricEvent("position_evidence_generation", boardEvaluator.EvidenceGeneration));
        lab.Publish(slot, new ChessLabMetricEvent("syzygy_max_men", ChessTablebaseRuntime.Largest));
        lab.Publish(slot, new ChessLabMetricEvent("substrate_epoch", ChessTransitionObservations.Epoch));
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
            state.Board, new Search.Limits(MaxDepth: depth), ct).BestMove!.Value;
    }

    public static async Task RunLadderAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        int games = int.Parse(Config(slot.Job.Config, "games", "20"));
        int depth = int.Parse(Config(slot.Job.Config, "depth", "4"));
        int maxPlies = int.Parse(Config(slot.Job.Config, "maxPlies", "160"));
        int budget = ResolveConcurrency(slot.Job.Config);
        bool record = bool.TryParse(Config(slot.Job.Config, "record", "false"), out bool recordValue) && recordValue;
        var terms = new[]
        {
            EvalTerm.Material, EvalTerm.Pst, EvalTerm.BishopPair,
            EvalTerm.RookFiles, EvalTerm.PawnStructure, EvalTerm.Tempo,
        };
        int perTerm = Math.Max(1, budget / terms.Length);
        int totalGames = games * terms.Length;

        var workDir = Path.Combine(LabDir, slot.Job.Id);
        Directory.CreateDirectory(workDir);
        var liveHost = record ? await lab.GetLiveHostAsync(ct) : null;
        var pgnSink = new ConcurrentBag<MatchPgnGame>();

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

        var pgnPath = Path.Combine(workDir, "games.pgn");
        ChessPgnWriter.WriteFile(pgnPath, pgnSink, @event: "chess-lab/ladder");
        lab.AddArtifact(slot, "games.pgn", pgnPath);

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
            // GH #528 class: `path` arrives from the (still unauthenticated, #489) HTTP config
            // dict. Reviewing is legitimate only over the dirs this stack writes PGNs to — an
            // unconstrained path is an arbitrary-file-read primitive.
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

    public static async Task RunLearnedPstAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        var liveHost = await lab.GetLiveHostAsync(ct);
        var learned = LearnedPst.ReadWhite(liveHost.DataSource);
        var rows = learned.Where(s => s.Witness > 0).OrderByDescending(s => s.DevPoints).Take(32)
            .Select(s => (IReadOnlyList<string>)[((char)('a' + s.File)).ToString() + (s.Rank + 1), s.Piece.ToString(), s.DevPoints.ToString("+0;-0")])
            .ToList();
        lab.Publish(slot, new ChessLabTableEvent("learned PST (top squares)", ["sq", "piece", "dev"], rows));
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    public static async Task RunCutechessAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        var cfg = slot.Job.Config;
        var dir = Path.Combine(LabDir, slot.Job.Id);
        Directory.CreateDirectory(dir);
        var pgnOut = Path.Combine(dir, "games.pgn");

        var options = new CutechessOptions
        {
            Rounds = int.Parse(Config(cfg, "rounds", "10")),
            // Watchable by default: 1s/move via cutechess st. depth>0 switches to the old
            // tc=inf/depth mode, where a deep search may sit on one move for minutes.
            Depth = int.Parse(Config(cfg, "depth", "0")),
            SecondsPerMove = double.Parse(Config(cfg, "st", "1"), System.Globalization.CultureInfo.InvariantCulture),
            StockfishElo = int.Parse(Config(cfg, "elo", "2000")),
            StockfishLimitStrength = bool.Parse(Config(cfg, "limitStrength", "true")),
            Concurrency = Math.Max(1, int.Parse(Config(cfg, "concurrency", "1"))),
            PgnOut = pgnOut,
            Event = $"chess-lab/cutechess/{slot.Job.Id}",
        };

        var final = ChessLabJobState.Completed;
        string? finalMessage = null;
        // The in-memory transcript is a bounded ring — right for a live pane, useless as the
        // record of a match that ran for an hour. The file is the complete one, and it is
        // what /terminal.txt serves once it exists.
        var transcriptPath = Path.Combine(dir, "transcript.log");
        await using var transcript = new StreamWriter(transcriptPath) { AutoFlush = false };
        try
        {
            await foreach (var evt in CutechessRunner.RunAsync(options, ct))
            {
                switch (evt)
                {
                    // Raw process I/O never enters the bounded event channel — see
                    // ChessLabService.AppendTerminal for why the two are separate.
                    case ChessLabTerminalEvent terminal:
                        await transcript.WriteLineAsync(ChessLabTerminal.Format(
                            lab.AppendTerminal(slot, terminal)));
                        break;
                    case ChessLabProgressEvent prog:
                        lab.UpdateSummary(slot, new ChessLabJobSummary(prog.Done, prog.Total, prog.Label));
                        lab.Publish(slot, prog);
                        break;
                    // The runner owns the verdict. Publishing this straight through and then
                    // calling Finish(Completed) below is how a match that died on argv
                    // parsing still reported "Completed" — to the web UI, to the CLI's exit
                    // code, and to anyone reading the job list afterwards.
                    case ChessLabDoneEvent done:
                        final = done.FinalState;
                        finalMessage = done.Message;
                        break;
                    default:
                        lab.Publish(slot, evt);
                        break;
                }
            }
        }
        finally
        {
            await transcript.FlushAsync(CancellationToken.None);
            lab.AddArtifact(slot, "transcript.log", transcriptPath);
            // A stopped match keeps whatever games it finished: cutechess writes the PGN
            // incrementally, so the artifact is real evidence even when the run was cut short.
            if (File.Exists(pgnOut)) lab.AddArtifact(slot, "games.pgn", pgnOut);
        }

        // Loop closure: cutechess games are played by the external laplace-uci binary, which
        // cannot record its own plies — without this the PGN artifact is where the evidence
        // dies. Opt out with config ingest=false. Skipped for a failed run, whose PGN is
        // either absent or a fragment of a match that never happened.
        if (final == ChessLabJobState.Completed
            && Config(cfg, "ingest", "true") == "true"
            && File.Exists(pgnOut))
        {
            try
            {
                lab.Publish(slot, new ChessLabLogEvent("info", "ingesting games.pgn into substrate…"));
                var liveHost = await lab.GetLiveHostAsync(ct);
                await using var ingestor = await ChessPgnIngestor.AttachAsync(liveHost, ct);
                var r = await ingestor.IngestFileAsync(
                    pgnOut, msg => lab.Publish(slot, new ChessLabLogEvent("info", msg)), ct);
                lab.Publish(slot, new ChessLabMetricEvent("games_ingested", r.Applied));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lab.Publish(slot, new ChessLabLogEvent(
                    "error", $"substrate ingest failed ({ex.Message}) — artifact kept, retry via /ingest"));
            }
        }

        Finish(lab, slot, final, finalMessage);
    }

    public static async Task RunLichessBotAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string? token = LichessBot.ResolveToken(Config(slot.Job.Config, "token", ""));
        if (string.IsNullOrEmpty(token))
        {
            lab.Publish(slot, new ChessLabLogEvent("error", "LICHESS_API token missing"));
            Finish(lab, slot, ChessLabJobState.Failed, "no token");
            return;
        }
        int depth = int.Parse(Config(slot.Job.Config, "depth",
            LichessDefaults.SearchDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        int maxConcurrent = int.Parse(Config(slot.Job.Config, "maxConcurrent",
            LichessDefaults.MaxConcurrent.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var host = await lab.GetLiveHostAsync(ct);

        await using var bot = new LichessBot(
            token,
            host,
            substrate: true,
            record: true,
            maxDepth: depth,
            log: new LabLogger(lab, slot));
        lab.Publish(slot, new ChessLabLogEvent("info", "lichess bot starting (transition reads + recording)"));
        await bot.RunAsync(maxConcurrent, ct);
        Finish(lab, slot, ChessLabJobState.Cancelled, "stopped");
    }

    public static async Task RunLichessFetchAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string user = Config(slot.Job.Config, "user", "").Trim();
        string site = Config(slot.Job.Config, "site", "lichess");
        bool all = bool.TryParse(Config(slot.Job.Config, "all", "true"), out bool allValue) && allValue;
        bool ingest = bool.TryParse(Config(slot.Job.Config, "ingest", "true"), out bool ingestValue) && ingestValue;
        int? max = ChessGameFetcher.ResolveArchiveLimit(all, Config(slot.Job.Config, "max", ""));
        string fideId = Config(slot.Job.Config, "fideId", "").Trim();
        if (user.Length == 0) throw new ArgumentException("A provider username is required.");
        var outPath = Path.Combine(
            LabDir, slot.Job.Id, $"{ChessGameFetcher.Sanitize(user)}_{ChessGameFetcher.Sanitize(site)}.pgn");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        int games = await ChessGameFetcher.FetchAsync(user, site, max, 0, outPath,
            msg => lab.Publish(slot, new ChessLabLogEvent("info", msg)), ct);
        lab.AddArtifact(slot, "games.pgn", outPath);
        lab.Publish(slot, new ChessLabMetricEvent("games_fetched", games));
        var profiles = new List<ChessPlayerProfile>
        {
            await ChessGameFetcher.FetchProfileAsync(user, site, ct),
        };
        if (fideId.Length > 0)
            profiles.Add(await ChessGameFetcher.FetchProfileAsync(fideId, "fide", ct));
        lab.Publish(slot, ProfileTable(profiles));
        await AddProfileArtifactsAsync(lab, slot, profiles, ct);
        if (ingest)
        {
            var liveHost = await lab.GetLiveHostAsync(ct);
            await using var ingestor = await ChessPgnIngestor.AttachAsync(liveHost, ct);
            var gameResult = await ingestor.IngestFileAsync(outPath,
                msg => lab.Publish(slot, new ChessLabLogEvent("info", msg)), ct);
            var profileResult = await ingestor.IngestPlayerProfilesAsync(profiles, ct);
            lab.Publish(slot, new ChessLabMetricEvent("games_ingested", gameResult.Applied));
            lab.Publish(slot, new ChessLabMetricEvent("profiles_ingested", profileResult.Profiles));
            lab.Publish(slot, new ChessLabMetricEvent("identity_links", profileResult.Links));
            lab.UpdateSummary(slot, new ChessLabJobSummary(
                gameResult.Applied, games,
                $"{games} fetched · {gameResult.Applied} new games · {profileResult.Profiles} profiles · {profileResult.Links} identity links"));
        }
        else
        {
            lab.UpdateSummary(slot, new ChessLabJobSummary(
                games, games, $"{games} fetched · not ingested"));
        }
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    public static async Task RunPlayerProfileAsync(
        ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string user = Config(slot.Job.Config, "user", "").Trim();
        string site = Config(slot.Job.Config, "site", "lichess");
        string fideId = Config(slot.Job.Config, "fideId", "").Trim();
        bool ingest = bool.TryParse(Config(slot.Job.Config, "ingest", "true"), out bool ingestValue) && ingestValue;
        if (user.Length == 0) throw new ArgumentException("A provider username is required.");

        var online = await ChessGameFetcher.FetchProfileAsync(user, site, ct);
        var profiles = new List<ChessPlayerProfile> { online };
        if (fideId.Length > 0)
            profiles.Add(await ChessGameFetcher.FetchProfileAsync(fideId, "fide", ct));

        lab.Publish(slot, ProfileTable(profiles));
        await AddProfileArtifactsAsync(lab, slot, profiles, ct);

        if (!ingest)
        {
            lab.UpdateSummary(slot, new ChessLabJobSummary(profiles.Count, profiles.Count,
                $"{profiles.Count} profiles acquired · not ingested"));
            Finish(lab, slot, ChessLabJobState.Completed);
            return;
        }

        var liveHost = await lab.GetLiveHostAsync(ct);
        await using var ingestor = await ChessPgnIngestor.AttachAsync(liveHost, ct);
        var result = await ingestor.IngestPlayerProfilesAsync(profiles, ct);
        lab.Publish(slot, new ChessLabMetricEvent("profiles_ingested", result.Profiles));
        lab.Publish(slot, new ChessLabMetricEvent("identity_links", result.Links));
        lab.UpdateSummary(slot, new ChessLabJobSummary(result.Profiles, profiles.Count,
            $"{result.Profiles} profiles · {result.Links} identity links"));
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    public static async Task RunFideSearchAsync(
        ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string query = Config(slot.Job.Config, "query", "").Trim();
        int limit = Math.Clamp(int.Parse(Config(slot.Job.Config, "limit", "25")), 1, 100);
        var candidates = await ChessGameFetcher.SearchFideAsync(query, limit, ct);
        lab.Publish(slot, FideTable($"FIDE matches for {query}", candidates));
        lab.Publish(slot, new ChessLabMetricEvent("matches", candidates.Count));
        lab.UpdateSummary(slot, new ChessLabJobSummary(candidates.Count, candidates.Count,
            $"{candidates.Count} official FIDE candidates"));
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    public static async Task RunFideRosterAsync(
        ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string cohort = Config(slot.Job.Config, "cohort", "open");
        int limit = Math.Clamp(int.Parse(Config(slot.Job.Config, "limit", "25")), 1, 100);
        bool ingest = Config(slot.Job.Config, "ingest", "true") == "true";
        var candidates = await ChessGameFetcher.FetchFideTopAsync(cohort, limit, ct);
        lab.Publish(slot, FideTable($"FIDE {cohort} top {candidates.Count}", candidates));
        if (!ingest)
        {
            lab.UpdateSummary(slot, new ChessLabJobSummary(candidates.Count, candidates.Count,
                $"{candidates.Count} official FIDE profiles · not ingested"));
            Finish(lab, slot, ChessLabJobState.Completed);
            return;
        }

        var fetched = new ConcurrentDictionary<string, ChessPlayerProfile>(StringComparer.Ordinal);
        int done = 0;
        await Parallel.ForEachAsync(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (candidate, token) =>
            {
                var profile = await ChessGameFetcher.FetchFideProfileAsync(candidate.FideId, token);
                var facts = profile.Facts.ToDictionary(
                    static x => x.Key, static x => x.Value, StringComparer.OrdinalIgnoreCase);
                facts["cohort"] = cohort;
                facts["rank"] = candidate.Rank?.ToString() ?? "";
                fetched[candidate.FideId] = profile with { Facts = facts };
                int current = Interlocked.Increment(ref done);
                lab.UpdateSummary(slot, new ChessLabJobSummary(current, candidates.Count,
                    $"profiles {current}/{candidates.Count}"));
                lab.Publish(slot, new ChessLabProgressEvent(current, candidates.Count, candidate.Name));
            });

        var profiles = candidates.Select(c => fetched[c.FideId]).ToArray();
        var liveHost = await lab.GetLiveHostAsync(ct);
        await using var ingestor = await ChessPgnIngestor.AttachAsync(liveHost, ct);
        var result = await ingestor.IngestPlayerProfilesAsync(profiles, ct);
        lab.Publish(slot, new ChessLabMetricEvent("profiles_ingested", result.Profiles));
        lab.UpdateSummary(slot, new ChessLabJobSummary(result.Profiles, candidates.Count,
            $"{result.Profiles} official FIDE profiles ingested from {cohort}"));
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    private static ChessLabTableEvent FideTable(
        string title, IReadOnlyList<FidePlayerCandidate> candidates)
        => new(title,
            ["Rank", "FIDE ID", "Name", "Title", "Fed", "Standard", "Rapid", "Blitz", "Born"],
            candidates.Select(static c => (IReadOnlyList<string>)[
                c.Rank?.ToString() ?? "", c.FideId, c.Name, c.Title ?? "", c.Federation,
                c.Standard == 0 ? "" : c.Standard.ToString(),
                c.Rapid == 0 ? "" : c.Rapid.ToString(),
                c.Blitz == 0 ? "" : c.Blitz.ToString(),
                c.BirthYear == 0 ? "" : c.BirthYear.ToString(),
            ]).ToArray());

    private static ChessLabTableEvent ProfileTable(IReadOnlyList<ChessPlayerProfile> profiles)
        => new("Acquired player profiles",
            ["Provider", "Provider ID", "Display name", "Real name", "Title", "Federation", "Ratings", "Avatar"],
            profiles.Select(static p => (IReadOnlyList<string>)[
                p.Provider, p.ProviderId, p.DisplayName, p.RealName ?? "", p.Title ?? "",
                p.Federation ?? "", string.Join(", ", p.Ratings.Select(static x => $"{x.Key} {x.Value}")),
                p.AvatarUrl ?? "",
            ]).ToArray());

    private static async Task AddProfileArtifactsAsync(
        ChessLabService lab, ChessLabService.JobSlot slot,
        IReadOnlyList<ChessPlayerProfile> profiles, CancellationToken ct)
    {
        string directory = Path.Combine(LabDir, slot.Job.Id);
        Directory.CreateDirectory(directory);
        foreach (var profile in profiles)
        {
            string name = $"profile-{ChessGameFetcher.Sanitize(profile.Provider)}.json";
            string path = Path.Combine(directory, name);
            await File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }), ct);
            lab.AddArtifact(slot, name, path);
        }
    }

    private static void Finish(ChessLabService lab, ChessLabService.JobSlot slot, ChessLabJobState state, string? msg = null)
    {
        lock (slot.Gate)
        {
            slot.Job = slot.Job with
            {
                State = state,
                FinishedAt = DateTimeOffset.UtcNow,
                // Keep the last summary line when the terminal state has nothing to add —
                // this used to blank the final score the moment the run succeeded, which is
                // what ChessLabService.Finish has always done and these two had drifted apart.
                Summary = slot.Job.Summary with { Message = msg ?? slot.Job.Summary.Message },
            };
        }
        lab.Publish(slot, new ChessLabDoneEvent(state, msg));
        slot.Channel.Writer.TryComplete();
        slot.Terminal.Complete();
    }

    private static string Config(IReadOnlyDictionary<string, string> cfg, string key, string def)
        => cfg.TryGetValue(key, out var v) ? v : def;

    internal static bool IsReviewablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return false; }
        if (!full.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var root in new[] { LabDir, LaplaceInstall.ResolveChessGamesDir() })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var rooted = Path.GetFullPath(root);
            if (full.StartsWith(rooted.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Live-board tap for in-process self-play: convert the recorded ply's position surface
    // to a FEN and publish it as a board event. Parallel games at shallow depth emit plies
    // every few ms, so throttle per game (first ply always passes; then min 250ms apart)
    // — the viewer needs a watchable stream, not every node.
    private static Action<int, int, string, string> LiveBoardPublisher(
        ChessLabService lab, ChessLabService.JobSlot slot, string nameA, string nameB)
    {
        var lastEmit = new ConcurrentDictionary<int, long>();
        return (game, ply, uci, toKey) =>
        {
            long now = Environment.TickCount64;
            if (ply > 1 && lastEmit.TryGetValue(game, out var last) && now - last < 250)
                return;
            lastEmit[game] = now;
            if (!Laplace.Modality.Chess.PositionContent.TryFenFromSurface(toKey, out var fen))
                return;
            bool aWhite = game % 2 == 0; // MatchRunner alternates colors per game index
            lab.Publish(slot, new ChessLabBoardEvent(
                game, ply, uci, fen, aWhite ? nameA : nameB, aWhite ? nameB : nameA));
        };
    }

    private static int ResolveConcurrency(IReadOnlyDictionary<string, string> cfg, string key = "concurrency")
    {
        if (cfg.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed))
            return parsed <= 0 ? CpuTopology.PerformanceLogicalProcessorCount : parsed;

        return CpuTopology.PerformanceLogicalProcessorCount;
    }

    private static string Short(string name) => string.IsNullOrEmpty(name) ? "?" : (name.Length > 16 ? name[..16] : name);

    private sealed class LabLogger(ChessLabService lab, ChessLabService.JobSlot slot) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => lab.Publish(slot, new ChessLabLogEvent(logLevel.ToString().ToLowerInvariant(), formatter(state, exception)));
    }
}
