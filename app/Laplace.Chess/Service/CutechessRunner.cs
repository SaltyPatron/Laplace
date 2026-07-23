using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public static partial class CutechessRunner
{
    [GeneratedRegex(@"Score of .*?:\s*(\d+)\s*-\s*(\d+)\s*-\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScoreRegex();

    [GeneratedRegex(@"Elo difference:\s*([+-]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex EloRegex();

    // cutechess-cli -debug traffic: "<ms> >Engine(0): position startpos moves e2e4 e7e5".
    // The "position" line cutechess sends before every "go" carries the full move list of
    // the game so far — parsing it (instead of per-engine bestmove lines) makes the live
    // board robust to ordering and to which engine is about to move.
    [GeneratedRegex(@"^\d+\s*>.*?\(\d+\):\s*position\s+(.+)$")]
    private static partial Regex DebugPositionRegex();

    [GeneratedRegex(@"^\d+\s*[<>]")]
    private static partial Regex DebugLineRegex();

    [GeneratedRegex(@"Started game (\d+) of (\d+)\s*\((.+?)\s+vs\s+(.+?)\)")]
    private static partial Regex GameStartRegex();

    public static bool ProbeCatalog(out bool cutechessOk, out bool stockfishOk, out bool qtOk)
    {
        var catalog = ChessLabPaths.Catalog;
        cutechessOk = catalog["cutechess"].Found;
        stockfishOk = catalog["stockfish"].Found;
        qtOk = catalog["qt"].Found;
        return cutechessOk && stockfishOk && qtOk;
    }

    public static async IAsyncEnumerable<ChessLabEvent> RunAsync(
        int rounds, int depth, string pgnOut,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in RunAsync(rounds, depth, st: 0, elo: 2000, pgnOut, ct))
            yield return evt;
    }

    public static async IAsyncEnumerable<ChessLabEvent> RunAsync(
        int rounds, int depth, double st, int elo, string pgnOut,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var catalog = ChessLabPaths.Catalog;
        var cc = catalog["cutechess"];
        var sf = catalog["stockfish"];
        var qt = catalog["qt"];
        var uci = catalog["laplaceUci"];

        if (!cc.Found || !sf.Found || !uci.Found)
        {
            yield return new ChessLabLogEvent("error",
                $"binaries missing: cutechess={cc.Found} stockfish={sf.Found} qt={qt.Found} laplaceUci={uci.Found}");
            yield return new ChessLabDoneEvent(ChessLabJobState.Failed, "missing binaries");
            yield break;
        }

        var psi = new ProcessStartInfo
        {
            FileName = cc.Path!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (qt.Found)
        {
            var prior = psi.Environment.TryGetValue("PATH", out var existing) ? existing
                : Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = string.IsNullOrEmpty(prior)
                ? qt.Path!
                : qt.Path! + Path.PathSeparator + prior;
        }

        // Every key=value MUST be its own argv token: the old single-token form
        // ("name=Stockfish cmd=... arg=\"setoption ...\"") reached cutechess-cli as ONE
        // engine parameter whose value was the rest of the string, so the engine never
        // started and jobs died with empty artifact dirs. proto=uci is likewise required —
        // cutechess defaults to the xboard protocol. UCI options ride the supported
        // option.NAME=value form, not raw setoption strings.
        psi.ArgumentList.Add("-engine");
        psi.ArgumentList.Add("name=Laplace");
        psi.ArgumentList.Add($"cmd={uci.Path}");
        psi.ArgumentList.Add("proto=uci");
        psi.ArgumentList.Add("-engine");
        psi.ArgumentList.Add("name=Stockfish");
        psi.ArgumentList.Add($"cmd={sf.Path}");
        psi.ArgumentList.Add("proto=uci");
        psi.ArgumentList.Add("option.UCI_LimitStrength=true");
        psi.ArgumentList.Add($"option.UCI_Elo={elo}");
        psi.ArgumentList.Add("-each");
        if (st > 0)
        {
            // Per-move seconds: the watchable default. tc=inf/depth=N let a deep search
            // sit on one move for minutes ("go depth N" has no clock at all).
            psi.ArgumentList.Add($"st={st.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            psi.ArgumentList.Add("timemargin=2000");
        }
        else
        {
            psi.ArgumentList.Add("tc=inf");
            psi.ArgumentList.Add($"depth={depth}");
        }
        psi.ArgumentList.Add("-rounds");
        psi.ArgumentList.Add(rounds.ToString());
        psi.ArgumentList.Add("-pgnout");
        psi.ArgumentList.Add(pgnOut);
        psi.ArgumentList.Add("-debug");

        yield return new ChessLabLogEvent("info",
            st > 0
                ? $"cutechess: {rounds} rounds, {st:0.##}s/move, Stockfish {elo} Elo"
                : $"cutechess: {rounds} rounds depth {depth}, Stockfish {elo} Elo");

        using var proc = Process.Start(psi)!;
        // stderr must be drained or a chatty engine can deadlock the pipe; surface the
        // tail as logs only when the run fails.
        var stderrTail = new Queue<string>();
        var stderrTask = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
                lock (stderrTail)
                {
                    stderrTail.Enqueue(line);
                    if (stderrTail.Count > 40) stderrTail.Dequeue();
                }
        }, CancellationToken.None);

        int done = 0;
        bool sawScore = false;
        var tracker = new LiveBoardTracker();

        await foreach (var line in ReadLinesAsync(proc, ct))
        {
            var debugPos = DebugPositionRegex().Match(line);
            if (debugPos.Success)
            {
                foreach (var evt in tracker.ApplyPositionLine(debugPos.Groups[1].Value))
                    yield return evt;
                continue;
            }
            if (DebugLineRegex().IsMatch(line))
                continue; // raw UCI traffic — parsed above, never forwarded as log spam

            yield return new ChessLabLogEvent("info", line);

            var started = GameStartRegex().Match(line);
            if (started.Success)
                tracker.Reset(
                    int.Parse(started.Groups[1].Value),
                    started.Groups[3].Value,
                    started.Groups[4].Value);

            var score = ScoreRegex().Match(line);
            if (score.Success)
            {
                sawScore = true;
                done = int.Parse(score.Groups[1].Value) + int.Parse(score.Groups[2].Value) + int.Parse(score.Groups[3].Value);
                yield return new ChessLabProgressEvent(done, rounds * 2);
            }
            var elo2 = EloRegex().Match(line);
            if (elo2.Success && double.TryParse(elo2.Groups[1].Value, out var eloVal))
                yield return new ChessLabMetricEvent("elo_diff", eloVal);
        }

        try { proc.Kill(entireProcessTree: true); } catch { }
        await proc.WaitForExitAsync(ct);
        try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); } catch { }
        // Exit code alone isn't sufficient: a 0 exit with zero parsed "Score of ..." lines means
        // cutechess-cli's output didn't match what we expect (version bump, localization, or a
        // run that produced no games) — that's a real failure, not a silent "Completed" with no
        // metrics.
        bool ok = proc.ExitCode == 0 && sawScore;
        if (!ok)
        {
            string[] tail;
            lock (stderrTail) tail = stderrTail.ToArray();
            foreach (var errLine in tail)
                yield return new ChessLabLogEvent("error", errLine);
        }
        yield return ok
            ? new ChessLabDoneEvent(ChessLabJobState.Completed)
            : new ChessLabDoneEvent(ChessLabJobState.Failed,
                proc.ExitCode == 0
                    ? "cutechess exited 0 but no \"Score of ...\" line was ever parsed from its output"
                    : $"cutechess exited with code {proc.ExitCode}");
    }

    // Tracks the live board across -debug "position" lines: replay only the new plies
    // and emit one board event per new ply. A class (not ref params) because the parse
    // loop is an async iterator, which cannot pass locals by ref.
    private sealed class LiveBoardTracker
    {
        private Board _board = Board.FromFen(ChessModality.StartFen);
        private int _plyCount;
        private int _game;
        private string? _white, _black;

        public void Reset(int game, string? white, string? black)
        {
            _game = game;
            _white = white;
            _black = black;
            _plyCount = 0;
            _board = Board.FromFen(ChessModality.StartFen);
        }

        // "startpos moves e2e4 e7e5" / "fen <6 fields> moves ..."
        public IEnumerable<ChessLabBoardEvent> ApplyPositionLine(string positionArgs)
        {
            var tok = positionArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int movesIdx = Array.IndexOf(tok, "moves");
            var moves = movesIdx >= 0 ? tok[(movesIdx + 1)..] : [];

            if (moves.Length < _plyCount)
            {
                // Shorter list than we've seen: a new game's first position line beat the
                // "Started game" banner (or a takeback) — restart from scratch.
                _plyCount = 0;
                _board = tok is ["fen", ..] && movesIdx >= 7
                    ? Board.FromFen(string.Join(' ', tok[1..7]))
                    : Board.FromFen(ChessModality.StartFen);
            }

            var events = new List<ChessLabBoardEvent>(Math.Max(0, moves.Length - _plyCount));
            for (int i = _plyCount; i < moves.Length; i++)
            {
                if (!TryApplyUci(_board, moves[i])) break;
                events.Add(new ChessLabBoardEvent(_game, i + 1, moves[i], _board.ToFen(), _white, _black));
            }
            _plyCount = moves.Length;
            return events;
        }

        private static bool TryApplyUci(Board board, string uci)
        {
            foreach (var m in MoveGen.Legal(board))
                if (m.ToUci() == uci) { MoveApply.Make(board, m); return true; }
            return false;
        }
    }

    internal static IEnumerable<ChessLabEvent> ParseLinesForTest(IEnumerable<string> lines)
    {
        int rounds = 10;
        var tracker = new LiveBoardTracker();
        foreach (var line in lines)
        {
            var debugPos = DebugPositionRegex().Match(line);
            if (debugPos.Success)
            {
                foreach (var evt in tracker.ApplyPositionLine(debugPos.Groups[1].Value))
                    yield return evt;
                continue;
            }
            if (DebugLineRegex().IsMatch(line)) continue;

            yield return new ChessLabLogEvent("info", line);
            var started = GameStartRegex().Match(line);
            if (started.Success)
                tracker.Reset(
                    int.Parse(started.Groups[1].Value),
                    started.Groups[3].Value,
                    started.Groups[4].Value);
            var score = ScoreRegex().Match(line);
            if (score.Success)
            {
                int done = int.Parse(score.Groups[1].Value) + int.Parse(score.Groups[2].Value) + int.Parse(score.Groups[3].Value);
                yield return new ChessLabProgressEvent(done, rounds * 2);
            }
            var elo = EloRegex().Match(line);
            if (elo.Success && double.TryParse(elo.Groups[1].Value, out var eloVal))
                yield return new ChessLabMetricEvent("elo_diff", eloVal);
        }
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(Process proc, [EnumeratorCancellation] CancellationToken ct)
    {
        while (!proc.HasExited)
        {
            string? line = await proc.StandardOutput.ReadLineAsync(ct);
            if (line is not null) yield return line;
            else await Task.Delay(50, ct);
        }
        while (proc.StandardOutput.ReadLine() is { } rest) yield return rest;
    }
}
