using System.Collections.Concurrent;
using global::Npgsql;
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
        string mode = Config(cfg, "mode", "fold");
        int games = int.Parse(Config(cfg, "games", "20"));
        int depth = int.Parse(Config(cfg, "depth", "4"));
        int maxPlies = int.Parse(Config(cfg, "maxPlies", "160"));
        int concurrency = int.Parse(Config(cfg, "concurrency", "4"));
        bool openings = Config(cfg, "openings", "false") == "true";

        lab.Publish(slot, new ChessLabLogEvent("info", $"substrate-test [{mode}] {games} games depth {depth}"));

        await using var ds = new NpgsqlDataSourceBuilder(ChessEngineService.ResolveConnString()).Build();
        IRootBias? bias = mode switch
        {
            "fold" => new SubstructureFoldBias(ds),
            "edge" => new SubstrateRootBias(ds),
            "off" => null,
            _ => null,
        };
        var guided = MatchRunner.SearcherFactory(depth, EvalTerm.All, bias);
        var pure = MatchRunner.SearcherFactory(depth, EvalTerm.All);
        var book = openings ? OpeningSeed.Fens(OpeningSeed.DefaultDir) : null;

        var progress = new Progress<(int Done, int AWins, int Draws, int BWins)>(p =>
        {
            lab.UpdateSummary(slot, new ChessLabJobSummary(p.Done, games, $"W{p.AWins}-D{p.Draws}-L{p.BWins}"));
            lab.Publish(slot, new ChessLabProgressEvent(p.Done, games));
        });

        var r = await Task.Run(() => MatchRunner.Play(
            guided, pure, games, maxPlies, seed: 99, concurrency: concurrency,
            openingFens: book, progress: progress), ct);

        string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
        lab.Publish(slot, new ChessLabMetricEvent("elo_diff", r.EloDiff));
        lab.Publish(slot, new ChessLabTableEvent("substrate-test", ["W", "D", "L", "Elo"],
            [[r.AWins.ToString(), r.Draws.ToString(), r.BWins.ToString(), elo]]));
        lab.UpdateSummary(slot, new ChessLabJobSummary(games, games, $"Elo {elo}"));
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    public static Task RunLadderAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
        => Task.Run(() =>
        {
            int games = int.Parse(Config(slot.Job.Config, "games", "20"));
            int depth = int.Parse(Config(slot.Job.Config, "depth", "4"));
            lab.Publish(slot, new ChessLabLogEvent("info", $"ladder depth {depth} × {games}"));
            var terms = new[] { EvalTerm.Material, EvalTerm.Pst, EvalTerm.BishopPair, EvalTerm.RookFiles, EvalTerm.PawnStructure, EvalTerm.Tempo };
            var rows = new List<IReadOnlyList<string>>();
            foreach (var t in terms)
            {
                ct.ThrowIfCancellationRequested();
                var full = MatchRunner.SearcherFactory(depth, EvalTerm.All);
                var minus = MatchRunner.SearcherFactory(depth, EvalTerm.All & ~t);
                var r = MatchRunner.Play(full, minus, games, seed: 7, concurrency: 4);
                string elo = (r.EloDiff >= 0 ? "+" : "") + r.EloDiff.ToString("F0");
                rows.Add([t.ToString(), $"{r.AWins}-{r.Draws}-{r.BWins}", elo]);
            }
            lab.Publish(slot, new ChessLabTableEvent("overlay ladder", ["term", "W-D-L", "Elo"], rows));
            Finish(lab, slot, ChessLabJobState.Completed);
        }, ct);

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
        await using var ds = new NpgsqlDataSourceBuilder(ChessEngineService.ResolveConnString()).Build();
        var learned = LearnedPst.ReadWhite(ds);
        var rows = learned.Where(s => s.Witness > 0).OrderByDescending(s => s.DevPoints).Take(32)
            .Select(s => (IReadOnlyList<string>)[((char)('a' + s.File)).ToString() + (s.Rank + 1), s.Piece.ToString(), s.DevPoints.ToString("+0;-0")])
            .ToList();
        lab.Publish(slot, new ChessLabTableEvent("learned PST (top squares)", ["sq", "piece", "dev"], rows));
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    public static async Task RunCutechessAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        var dir = Path.Combine(LabDir, slot.Job.Id);
        Directory.CreateDirectory(dir);
        var pgnOut = Path.Combine(dir, "games.pgn");
        int rounds = int.Parse(Config(slot.Job.Config, "rounds", "10"));
        int depth = int.Parse(Config(slot.Job.Config, "depth", "8"));

        await foreach (var evt in CutechessRunner.RunAsync(rounds, depth, pgnOut, ct))
        {
            switch (evt)
            {
                case ChessLabLogEvent log: lab.Publish(slot, log); break;
                case ChessLabProgressEvent prog: lab.UpdateSummary(slot, new ChessLabJobSummary(prog.Done, prog.Total)); lab.Publish(slot, prog); break;
                case ChessLabMetricEvent m: lab.Publish(slot, m); break;
                default: lab.Publish(slot, evt); break;
            }
        }
        lab.AddArtifact(slot, "games.pgn", pgnOut);
        Finish(lab, slot, ChessLabJobState.Completed);
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
        int depth = int.Parse(Config(slot.Job.Config, "depth", "4"));
        int maxConcurrent = int.Parse(Config(slot.Job.Config, "maxConcurrent", "2"));
        await using var bot = new LichessBot(token, depth, log: new LabLogger(lab, slot));
        lab.Publish(slot, new ChessLabLogEvent("info", "lichess bot starting"));
        await bot.RunAsync(maxConcurrent, ct);
        Finish(lab, slot, ChessLabJobState.Cancelled, "stopped");
    }

    public static async Task RunLichessFetchAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string user = Config(slot.Job.Config, "user", "");
        string site = Config(slot.Job.Config, "site", "lichess");
        int? max = int.TryParse(Config(slot.Job.Config, "max", ""), out var m) ? m : null;
        // user/site come straight from the public /chess/lab/start request body, unsanitized —
        // Path.Combine would otherwise honor a rooted or ".."-laden value here, letting an
        // unauthenticated caller redirect this write to an arbitrary filesystem location. Sanitize
        // only for the path; FetchAsync below still gets the real user/site for the API call.
        var outPath = Path.Combine(
            LabDir, slot.Job.Id, $"{ChessGameFetcher.Sanitize(user)}_{ChessGameFetcher.Sanitize(site)}.pgn");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        int games = await ChessGameFetcher.FetchAsync(user, site, max, 0, outPath,
            msg => lab.Publish(slot, new ChessLabLogEvent("info", msg)), ct);
        lab.AddArtifact(slot, "games.pgn", outPath);
        lab.Publish(slot, new ChessLabMetricEvent("games_fetched", games));
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    private static void Finish(ChessLabService lab, ChessLabService.JobSlot slot, ChessLabJobState state, string? msg = null)
    {
        lock (slot.Gate)
        {
            slot.Job = slot.Job with { State = state, FinishedAt = DateTimeOffset.UtcNow, Summary = slot.Job.Summary with { Message = msg } };
        }
        lab.Publish(slot, new ChessLabDoneEvent(state, msg));
        slot.Channel.Writer.TryComplete();
    }

    private static string Config(IReadOnlyDictionary<string, string> cfg, string key, string def)
        => cfg.TryGetValue(key, out var v) ? v : def;

    private static string Short(string name) => string.IsNullOrEmpty(name) ? "?" : (name.Length > 16 ? name[..16] : name);

    private sealed class LabLogger(ChessLabService lab, ChessLabService.JobSlot slot) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => lab.Publish(slot, new ChessLabLogEvent(logLevel.ToString().ToLowerInvariant(), formatter(state, exception)));
    }
}
