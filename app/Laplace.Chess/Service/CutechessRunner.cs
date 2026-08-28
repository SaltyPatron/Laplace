using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>Everything the gauntlet needs to build a cutechess-cli invocation.</summary>
public sealed record CutechessOptions
{
    /// <summary>
    /// Games to play. cutechess's own manual: "for two-player tournaments this option
    /// [-rounds] should be used to set the total number of games to play" — one game per
    /// round, with the colours alternating between rounds. The lab treated it as rounds ×2
    /// for years, so every progress bar counted toward a total twice the size of the match
    /// it was measuring and stopped, by construction, at 50%.
    /// </summary>
    public int Rounds { get; init; } = 10;

    /// <summary>
    /// Fixed search depth for both engines. Greater than zero selects the unclocked
    /// <c>tc=inf depth=N</c> mode, where one move can occupy the engine for minutes.
    /// Zero (the default) uses <see cref="SecondsPerMove"/> instead.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>Per-move seconds (cutechess <c>st</c>). Ignored when <see cref="Depth"/> is set.</summary>
    public double SecondsPerMove { get; init; } = 1;

    /// <summary>Stockfish's <c>UCI_Elo</c> cap, paired with <c>UCI_LimitStrength</c>.</summary>
    public int StockfishElo { get; init; } = 2000;
    public bool StockfishLimitStrength { get; init; } = true;

    /// <summary>Games in flight. 1 keeps the transcript readable; higher finishes sooner.</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>Where cutechess writes the PGN.</summary>
    public string PgnOut { get; init; } = "";

    /// <summary>PGN <c>Event</c> tag — the provenance string the ingest lane sees.</summary>
    public string? Event { get; init; }
}

public static partial class CutechessRunner
{
    [GeneratedRegex(@"Score of .*?:\s*(\d+)\s*-\s*(\d+)\s*-\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScoreRegex();

    [GeneratedRegex(@"Elo difference:\s*([+-]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex EloRegex();

    // cutechess-cli -debug traffic, verbatim from a real run:
    //   "11 >A(0): uci"                       harness -> engine
    //   "26 <A(0): Stockfish 14.1 by ..."     engine  -> harness
    // One regex for both directions: it classifies the line for the transcript AND
    // exposes the payload the live board is rebuilt from. Splitting those into two
    // patterns is how they drift apart.
    [GeneratedRegex(@"^(\d+)\s+([<>])(.+?)\((\d+)\):\s?(.*)$")]
    private static partial Regex DebugTrafficRegex();

    [GeneratedRegex(@"Started game (\d+) of (\d+)\s*\((.+?)\s+vs\s+(.+?)\)")]
    private static partial Regex GameStartRegex();

    // "Finished game 1 (A vs B): 1-0 {White mates}"
    [GeneratedRegex(@"^Finished game (\d+)\s*\((.+?)\s+vs\s+(.+?)\):\s*(\S+)(?:\s*\{(.*)\})?\s*$")]
    private static partial Regex GameEndRegex();

    // "option name UCI_Elo type spin default 1350 min 1350 max 2850" — the bounds differ
    // between Stockfish releases (14.1: 1350-2850, 16+: 1320-3190), so the only honest
    // source for them is the engine that is actually running.
    [GeneratedRegex(@"option name UCI_Elo type spin.*?min (\d+) max (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex UciEloRangeRegex();

    public static bool ProbeCatalog(out bool cutechessOk, out bool stockfishOk, out bool qtOk)
    {
        var catalog = ChessLabPaths.Catalog;
        cutechessOk = catalog["cutechess"].Found;
        stockfishOk = catalog["stockfish"].Found;
        qtOk = catalog["qt"].Found;
        return cutechessOk && stockfishOk && qtOk;
    }

    /// <summary>
    /// The argv for one gauntlet, as its own pure function so the UI can preview the exact
    /// command and a test can pin it without spawning anything.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(CutechessOptions o, string laplaceUci, string stockfish)
    {
        // Every key=value MUST be its own argv token: the old single-token form
        // ("name=Stockfish cmd=... arg=\"setoption ...\"") reached cutechess-cli as ONE
        // engine parameter whose value was the rest of the string, so the engine never
        // started and jobs died with empty artifact dirs. proto=uci is likewise required —
        // cutechess defaults to the xboard protocol. UCI options ride the supported
        // option.NAME=value form, not raw setoption strings.
        var args = new List<string>
        {
            "-engine", "name=Laplace", $"cmd={laplaceUci}", "proto=uci",
            "-engine", "name=Stockfish", $"cmd={stockfish}", "proto=uci",
            $"option.UCI_LimitStrength={o.StockfishLimitStrength.ToString().ToLowerInvariant()}",
        };
        if (o.StockfishLimitStrength)
            args.Add($"option.UCI_Elo={o.StockfishElo}");
        args.Add("-each");

        if (o.Depth > 0)
        {
            args.Add("tc=inf");
            args.Add($"depth={o.Depth}");
        }
        else
        {
            // Per-move seconds: the watchable default. tc=inf/depth=N lets a deep search
            // sit on one move for minutes ("go depth N" has no clock at all).
            args.Add($"st={o.SecondsPerMove.ToString(CultureInfo.InvariantCulture)}");
            args.Add("timemargin=2000");
        }

        args.Add("-rounds");
        args.Add(o.Rounds.ToString(CultureInfo.InvariantCulture));
        if (o.Concurrency > 1)
        {
            args.Add("-concurrency");
            args.Add(o.Concurrency.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(o.Event))
        {
            args.Add("-event");
            args.Add(o.Event);
        }
        args.Add("-pgnout");
        args.Add(o.PgnOut);

        // "-debug all", never a bare "-debug". cutechess's MatchParser turns an option with
        // no arguments into QVariant(true); upstream a70c5915 then added
        //     if (value == "all") ...; else if (!value.isNull()) ok = false;
        // to the -debug branch, so that boolean now fails the check and the process dies
        // before a single game with: Warning: Empty value for option "-debug" (exit 1).
        // "all" is the one accepted value, and it additionally sends "debug on" to each
        // engine — more transcript, which is the point of running with -debug at all.
        args.Add("-debug");
        args.Add("all");
        return args;
    }

    public static IAsyncEnumerable<ChessLabEvent> RunAsync(
        int rounds, int depth, string pgnOut, CancellationToken ct)
        => RunAsync(new CutechessOptions { Rounds = rounds, Depth = depth, PgnOut = pgnOut }, ct);

    public static IAsyncEnumerable<ChessLabEvent> RunAsync(
        int rounds, int depth, double st, int elo, string pgnOut, CancellationToken ct)
        => RunAsync(
            new CutechessOptions
            {
                Rounds = rounds,
                Depth = depth,
                SecondsPerMove = st,
                StockfishElo = elo,
                PgnOut = pgnOut,
            },
            ct);

    public static async IAsyncEnumerable<ChessLabEvent> RunAsync(
        CutechessOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var catalog = ChessLabPaths.Catalog;
        var cc = catalog["cutechess"];
        var sf = catalog["stockfish"];
        var qt = catalog["qt"];
        var uci = catalog["laplaceUci"];

        if (!cc.Found || !sf.Found || !uci.Found)
        {
            foreach (var (name, probe, hint) in new[]
                     {
                         ("cutechess-cli", cc, "LAPLACE_CUTECHESS"),
                         ("stockfish", sf, "LAPLACE_STOCKFISH"),
                         ("laplace-uci", uci, "publish the API host (it ships beside the entry assembly)"),
                     })
            {
                if (probe.Found) continue;
                yield return new ChessLabLogEvent("error",
                    $"{name} not found (looked at '{probe.Path ?? "-"}', source {probe.Source}) — set {hint} in deploy/secrets/chess-lab.env");
            }
            yield return new ChessLabDoneEvent(ChessLabJobState.Failed, "missing binaries");
            yield break;
        }

        var args = BuildArguments(options, uci.Path!, sf.Path!);
        var psi = new ProcessStartInfo
        {
            FileName = cc.Path!,
            WorkingDirectory = Path.GetDirectoryName(options.PgnOut) is { Length: > 0 } wd && Directory.Exists(wd)
                ? wd
                : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        if (qt.Found)
        {
            var prior = psi.Environment.TryGetValue("PATH", out var existing) ? existing
                : Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = string.IsNullOrEmpty(prior)
                ? qt.Path!
                : qt.Path! + Path.PathSeparator + prior;
        }

        var command = new ChessLabCommandEvent(psi.FileName, args, psi.WorkingDirectory);
        yield return command;
        yield return new ChessLabTerminalEvent(ChessLabStream.Command, command.CommandLine);
        yield return new ChessLabLogEvent("info", DescribeRun(options));

        var parser = new TranscriptParser(options.Rounds, options.StockfishLimitStrength ? options.StockfishElo : null);

        using var proc = Process.Start(psi)!;

        // stdout and stderr merged into one ordered stream. Draining both is not optional —
        // a chatty engine fills the unread pipe and deadlocks the match — and interleaving
        // them is what makes a warning legible against the traffic that provoked it. The old
        // shape kept stderr in a 40-line tail replayed only on failure, which is why the
        // "-debug" rejection above surfaced as an epitaph instead of as the first line.
        var merged = Channel.CreateUnbounded<(string Stream, string Text)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        async Task PumpAsync(StreamReader reader, string stream)
        {
            // Never cancelled: the pumps end at EOF, which the kill in the finally guarantees.
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
                await merged.Writer.WriteAsync((stream, line), CancellationToken.None);
        }

        var pumps = Task.WhenAll(
            PumpAsync(proc.StandardOutput, ChessLabStream.Stdout),
            PumpAsync(proc.StandardError, ChessLabStream.Stderr));
        _ = pumps.ContinueWith(t => merged.Writer.TryComplete(t.Exception), TaskScheduler.Default);

        try
        {
            await foreach (var (stream, text) in merged.Reader.ReadAllAsync(ct))
                foreach (var evt in parser.Line(stream, text))
                    yield return evt;
        }
        finally
        {
            // Cancellation throws straight out of the loop above. Process.Dispose does not
            // kill anything, so without this a Stop left cutechess and both engines running
            // and burning cores until the host was rebooted.
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }

        await proc.WaitForExitAsync(CancellationToken.None);
        try { await pumps.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); } catch { /* best effort */ }

        int exitCode = proc.ExitCode;
        yield return new ChessLabTerminalEvent(ChessLabStream.Runner, $"cutechess-cli exited with code {exitCode}");

        yield return parser.Complete(exitCode);
    }

    private static string DescribeRun(CutechessOptions o)
    {
        string clock = o.Depth > 0 ? $"depth {o.Depth} (unclocked)" : $"{o.SecondsPerMove:0.##}s/move";
        string parallel = o.Concurrency > 1 ? $", {o.Concurrency} games in flight" : "";
        string strength = o.StockfishLimitStrength ? $"capped at {o.StockfishElo} Elo" : "full strength";
        return $"cutechess: {o.Rounds} games, {clock}, Stockfish {strength}{parallel}";
    }

    /// <summary>
    /// Turns one line of cutechess output into lab events. The live run and the tests share
    /// this instance type, so a parser change cannot pass a test that a real match then fails.
    /// </summary>
    internal sealed class TranscriptParser(int rounds, int? requestedElo = null)
    {
        private readonly LiveBoardTracker _tracker = new();
        private bool _eloRangeChecked;
        // Seeded from the config, then replaced by the count cutechess prints in its own
        // "Started game N of M" banner — the only total that is true by construction.
        private int _total = rounds;

        public bool SawScore { get; private set; }
        public int Done { get; private set; }
        public int Total => _total;

        // cutechess can print a zero-game score after engine initialization fails.
        // A score line (or a successful process exit) does not prove a match completed.
        public ChessLabDoneEvent Complete(int exitCode)
            => exitCode == 0 && SawScore && Done > 0 && Done == Total
                ? new ChessLabDoneEvent(ChessLabJobState.Completed)
                : new ChessLabDoneEvent(ChessLabJobState.Failed,
                    $"cutechess exited with code {exitCode}; completed {Done}/{Total} games"
                    + (SawScore ? "" : "; no score line was parsed"));

        public IEnumerable<ChessLabEvent> Line(string stream, string text)
            => stream == ChessLabStream.Stderr ? Stderr(text) : Stdout(text);

        private IEnumerable<ChessLabEvent> Stderr(string text)
        {
            yield return new ChessLabTerminalEvent(ChessLabStream.Stderr, text);
            // Qt prefixes these; keep the prefix in the transcript, drop it from the feed.
            string level = text.StartsWith("Warning:", StringComparison.Ordinal) ? "warning" : "error";
            yield return new ChessLabLogEvent(level, text);
        }

        private IEnumerable<ChessLabEvent> Stdout(string text)
        {
            var traffic = DebugTrafficRegex().Match(text);
            if (traffic.Success)
            {
                string direction = traffic.Groups[2].Value == ">" ? ChessLabDirection.Send : ChessLabDirection.Recv;
                string engine = traffic.Groups[3].Value;
                string payload = traffic.Groups[5].Value;
                yield return new ChessLabTerminalEvent(ChessLabStream.Uci, payload, engine, direction);

                if (direction == ChessLabDirection.Send && payload.StartsWith("position ", StringComparison.Ordinal))
                {
                    // The "position" line cutechess sends before every "go" carries the full move
                    // list of the game so far — replaying it (instead of per-engine bestmove lines)
                    // makes the live board robust to ordering and to which engine is about to move.
                    foreach (var evt in _tracker.ApplyPositionLine(payload["position ".Length..]))
                        yield return evt;
                }
                else if (direction == ChessLabDirection.Recv && !_eloRangeChecked && requestedElo is { } want)
                {
                    var range = UciEloRangeRegex().Match(payload);
                    if (range.Success
                        && int.TryParse(range.Groups[1].Value, out int min)
                        && int.TryParse(range.Groups[2].Value, out int max))
                    {
                        _eloRangeChecked = true;
                        if (want < min || want > max)
                            yield return new ChessLabLogEvent("warning",
                                $"{engine} accepts UCI_Elo {min}–{max}; {want} was requested and will be clamped by the engine, "
                                + "so the reported strength cap is not the one you asked for");
                    }
                }
                yield break;
            }

            yield return new ChessLabTerminalEvent(ChessLabStream.Stdout, text);
            yield return new ChessLabLogEvent("info", text);

            var started = GameStartRegex().Match(text);
            if (started.Success)
            {
                int index = int.Parse(started.Groups[1].Value, CultureInfo.InvariantCulture);
                _total = int.Parse(started.Groups[2].Value, CultureInfo.InvariantCulture);
                _tracker.Reset(index, started.Groups[3].Value, started.Groups[4].Value);
                // Give the progress bar a denominator from the first banner, not from the
                // end of the first game.
                yield return new ChessLabProgressEvent(Done, _total, $"game {index}");
            }

            var finished = GameEndRegex().Match(text);
            if (finished.Success)
                yield return new ChessLabGameEvent(
                    int.Parse(finished.Groups[1].Value, CultureInfo.InvariantCulture),
                    finished.Groups[2].Value,
                    finished.Groups[3].Value,
                    finished.Groups[5].Success && finished.Groups[5].Value.Length > 0
                        ? $"{finished.Groups[4].Value} ({finished.Groups[5].Value})"
                        : finished.Groups[4].Value);

            var score = ScoreRegex().Match(text);
            if (score.Success)
            {
                // cutechess prints wins - losses - draws, from the first engine's side.
                int wins = int.Parse(score.Groups[1].Value, CultureInfo.InvariantCulture);
                int losses = int.Parse(score.Groups[2].Value, CultureInfo.InvariantCulture);
                int draws = int.Parse(score.Groups[3].Value, CultureInfo.InvariantCulture);
                SawScore = true;
                Done = wins + losses + draws;
                yield return new ChessLabMetricEvent("wins", wins);
                yield return new ChessLabMetricEvent("losses", losses);
                yield return new ChessLabMetricEvent("draws", draws);
                yield return new ChessLabProgressEvent(Done, _total, $"{wins}W-{losses}L-{draws}D");
            }

            var elo = EloRegex().Match(text);
            if (elo.Success && double.TryParse(elo.Groups[1].Value, CultureInfo.InvariantCulture, out var eloVal))
                yield return new ChessLabMetricEvent("elo_diff", eloVal);
        }
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

    /// <summary>Drives the production parser over canned stdout lines.</summary>
    internal static IEnumerable<ChessLabEvent> ParseLinesForTest(
        IEnumerable<string> lines, int rounds = 10, int? requestedElo = null)
    {
        var parser = new TranscriptParser(rounds, requestedElo);
        foreach (var line in lines)
            foreach (var evt in parser.Line(ChessLabStream.Stdout, line))
                yield return evt;
    }

    /// <summary>Drives the production parser over canned (stream, line) pairs.</summary>
    internal static IEnumerable<ChessLabEvent> ParseStreamsForTest(
        IEnumerable<(string Stream, string Text)> lines, int rounds = 10, int? requestedElo = null)
    {
        var parser = new TranscriptParser(rounds, requestedElo);
        foreach (var (stream, text) in lines)
            foreach (var evt in parser.Line(stream, text))
                yield return evt;
    }
}
