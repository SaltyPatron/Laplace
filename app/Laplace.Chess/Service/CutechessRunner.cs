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
    /// round. A valid paired gauntlet uses an even count so each opening is played from
    /// both colours.
    /// </summary>
    public int Rounds { get; init; } = 10;

    /// <summary>
    /// Shared fixed search depth. Greater than zero selects the unclocked
    /// <c>tc=inf depth=N</c> match budget for both engines. This is a matched-depth
    /// experiment, not an unrestricted Stockfish search. Zero (the default) uses
    /// <see cref="SecondsPerMove"/> instead.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>Per-move seconds (cutechess <c>st</c>). Ignored when <see cref="Depth"/> is set.</summary>
    public double SecondsPerMove { get; init; } = 1;

    /// <summary>Stockfish's <c>UCI_Elo</c> cap, paired with <c>UCI_LimitStrength</c>.</summary>
    public int StockfishElo { get; init; } = 2000;
    public bool StockfishLimitStrength { get; init; } = true;

    /// <summary>
    /// Use a deterministic opening suite and play every opening twice with colours swapped.
    /// This is on by default because repeating startpos is not a meaningful multi-game gauntlet.
    /// </summary>
    public bool PairOpenings { get; init; } = true;

    /// <summary>How many plies to replay from the admitted opening corpus before the engines take over.</summary>
    public int OpeningPlies { get; init; } = 10;

    /// <summary>
    /// Optional explicit EPD file. Normal lab runs generate one beside <see cref="PgnOut"/>;
    /// the preview uses the same deterministic path before that file exists.
    /// </summary>
    public string? OpeningsFile { get; init; }

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
    // between Stockfish releases, so the only honest source for them is the engine that is
    // actually running.
    [GeneratedRegex(@"option name UCI_Elo type spin.*?min (\d+) max (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex UciEloRangeRegex();

    // CI and developer hosts are not guaranteed to carry the large opening corpus. These
    // ordinary, legal two-ply positions are a deterministic floor so a paired experiment
    // never silently degrades back to repeated startpos. Production prefers OpeningSeed first.
    private static readonly string[] BuiltinOpeningFens =
    [
        "rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2", // 1.e4 e5
        "rnbqkbnr/ppp1pppp/8/3p4/3P4/8/PPP1PPPP/RNBQKBNR w KQkq - 0 2", // 1.d4 d5
        "rnbqkbnr/pppp1ppp/8/4p3/2P5/8/PP1PPPPP/RNBQKBNR w KQkq - 0 2", // 1.c4 e5
        "rnbqkbnr/ppp1pppp/8/3p4/8/5N2/PPPPPPPP/RNBQKB1R w KQkq - 0 2", // 1.Nf3 d5
        "rnbqkbnr/pp1ppppp/8/2p5/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2", // 1.e4 c5
        "rnbqkbnr/pppp1ppp/8/8/3pP3/8/PPP2PPP/RNBQKBNR w KQkq - 0 3",   // 1.e4 e5 2.d4 exd4
    ];

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
        // cutechess defaults to the xboard protocol. Make Laplace's substrate mode explicit
        // in the receipt instead of relying on an environment/default hidden from the match.
        var args = new List<string>
        {
            "-engine", "name=Laplace", $"cmd={laplaceUci}", "proto=uci", "option.Substrate=substrate",
            "-engine", "name=Stockfish", $"cmd={stockfish}", "proto=uci",
            $"option.UCI_LimitStrength={o.StockfishLimitStrength.ToString().ToLowerInvariant()}",
        };
        if (o.StockfishLimitStrength)
            args.Add($"option.UCI_Elo={o.StockfishElo}");
        args.Add("-each");

        if (o.Depth > 0)
        {
            // Shared on purpose: this is a matched-depth experiment. Disabling UCI_Elo does
            // not make this an unrestricted/max-host Stockfish run while depth remains set.
            args.Add("tc=inf");
            args.Add($"depth={o.Depth}");
        }
        else
        {
            // Per-move seconds: the watchable default. Both engines receive the same physical
            // move budget; StockfishLimitStrength controls only the UCI_Elo limiter.
            args.Add($"st={o.SecondsPerMove.ToString(CultureInfo.InvariantCulture)}");
            args.Add("timemargin=2000");
        }

        if (o.PairOpenings)
        {
            string openings = ResolveOpeningsPath(o);
            args.Add("-openings");
            args.Add($"file={openings}");
            args.Add("format=epd");
            args.Add("order=sequential");
            // Cute Chess's -repeat contract is exactly the color-swapped pair we need:
            // same opening twice, players swap sides after the first game.
            args.Add("-repeat");
            args.Add("2");
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
        if (options.PairOpenings && (options.Rounds < 2 || (options.Rounds & 1) != 0))
        {
            yield return new ChessLabLogEvent("error",
                $"paired gauntlet requires an even game count >= 2 (requested {options.Rounds})");
            yield return new ChessLabDoneEvent(ChessLabJobState.Failed,
                "paired opening schedule requires an even game count >= 2");
            yield break;
        }

        if (options.PairOpenings)
        {
            if (!TryPrepareOpeningSuite(options, out var openingPath, out var openingCount, out var error))
            {
                yield return new ChessLabLogEvent("error", error!);
                yield return new ChessLabDoneEvent(ChessLabJobState.Failed, error);
                yield break;
            }
            options = options with { OpeningsFile = openingPath };
            yield return new ChessLabLogEvent("info",
                $"paired opening suite: {openingCount} positions × 2 colours ({openingPath})");
        }

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

        var parser = new TranscriptParser(
            options.Rounds,
            options.StockfishLimitStrength ? options.StockfishElo : null,
            requireEngineIdentity: true,
            requirePairedSchedule: options.PairOpenings);

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

    private static string ResolveOpeningsPath(CutechessOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.OpeningsFile)) return o.OpeningsFile;
        string dir = Path.GetDirectoryName(o.PgnOut) is { Length: > 0 } p ? p : Environment.CurrentDirectory;
        return Path.Combine(dir, "openings.epd");
    }

    private static bool TryPrepareOpeningSuite(
        CutechessOptions o, out string path, out int openingCount, out string? error)
    {
        path = ResolveOpeningsPath(o);
        openingCount = o.Rounds / 2;
        error = null;
        try
        {
            var fens = new List<string>(openingCount);
            try
            {
                foreach (var fen in OpeningSeed.Fens(OpeningSeed.DefaultDir, Math.Max(1, o.OpeningPlies), openingCount))
                    if (!fens.Contains(fen, StringComparer.Ordinal)) fens.Add(fen);
            }
            catch
            {
                // The external opening corpus is an input, not a condition for running the
                // harness. The built-in floor below keeps CI and clean hosts paired as well.
            }

            for (int i = 0; fens.Count < openingCount; i++)
                fens.Add(BuiltinOpeningFens[i % BuiltinOpeningFens.Length]);

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(path, fens.Take(openingCount).Select(ToEpd));
            return true;
        }
        catch (Exception ex)
        {
            error = $"could not materialize paired opening suite '{path}': {ex.Message}";
            return false;
        }
    }

    private static string ToEpd(string fen)
    {
        var fields = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4) throw new FormatException($"opening FEN has fewer than four fields: '{fen}'");
        return string.Join(' ', fields[..4]);
    }

    private static string DescribeRun(CutechessOptions o)
    {
        string clock = o.Depth > 0
            ? $"matched depth {o.Depth} (unclocked)"
            : $"matched {o.SecondsPerMove:0.##}s/move";
        string parallel = o.Concurrency > 1 ? $", {o.Concurrency} games in flight" : "";
        string strength = o.StockfishLimitStrength
            ? $"UCI_Elo capped at {o.StockfishElo}"
            : "UCI_Elo unrestricted (match depth/time still applies)";
        string schedule = o.PairOpenings ? ", paired colour-swapped openings" : ", repeated startpos schedule";
        return $"cutechess: {o.Rounds} games, {clock}, Stockfish {strength}{schedule}{parallel}";
    }

    /// <summary>
    /// Turns one line of cutechess output into lab events. The live run and the tests share
    /// this instance type, so a parser change cannot pass a test that a real match then fails.
    /// </summary>
    internal sealed class TranscriptParser
    {
        private readonly LiveBoardTracker _tracker = new();
        private readonly int? _requestedElo;
        private readonly bool _requireEngineIdentity;
        private readonly bool _requirePairedSchedule;
        private readonly Dictionary<int, (string White, string Black)> _startedGames = new();
        private bool _eloRangeChecked;
        private bool _laplaceIdentityVerified;
        private bool _stockfishIdentityVerified;
        private string? _identityFailure;
        private string? _scheduleFailure;
        // Seeded from the config, then replaced by the count cutechess prints in its own
        // "Started game N of M" banner — the only total that is true by construction.
        private int _total;

        public TranscriptParser(
            int rounds, int? requestedElo = null,
            bool requireEngineIdentity = false, bool requirePairedSchedule = false)
        {
            _total = rounds;
            _requestedElo = requestedElo;
            _requireEngineIdentity = requireEngineIdentity;
            _requirePairedSchedule = requirePairedSchedule;
        }

        public bool SawScore { get; private set; }
        public int Done { get; private set; }
        public int Total => _total;

        // A zero exit, a score line, or a pretty result table is not enough. A valid run must
        // complete every game, prove the two UCI processes are the engines named in the receipt,
        // and (for the default paired protocol) prove each opening pair actually swaps colours.
        public ChessLabDoneEvent Complete(int exitCode)
        {
            var failures = new List<string>();
            if (exitCode != 0) failures.Add($"cutechess exited with code {exitCode}");
            if (!SawScore) failures.Add("no score line was parsed");
            if (Done <= 0 || Done != Total) failures.Add($"completed {Done}/{Total} games");

            if (_requireEngineIdentity)
            {
                if (_identityFailure is not null) failures.Add(_identityFailure);
                if (!_laplaceIdentityVerified) failures.Add("Laplace UCI identity was not verified");
                if (!_stockfishIdentityVerified) failures.Add("Stockfish UCI identity was not verified");
            }

            if (_requirePairedSchedule)
            {
                if (_scheduleFailure is not null) failures.Add(_scheduleFailure);
                if ((_total & 1) != 0) failures.Add($"paired schedule reported odd total {_total}");
                if (_startedGames.Count < _total)
                    failures.Add($"only {_startedGames.Count}/{_total} game starts were receipted");
            }

            return failures.Count == 0
                ? new ChessLabDoneEvent(ChessLabJobState.Completed)
                : new ChessLabDoneEvent(ChessLabJobState.Failed, string.Join("; ", failures));
        }

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
                int engineIndex = int.Parse(traffic.Groups[4].Value, CultureInfo.InvariantCulture);
                string payload = traffic.Groups[5].Value;
                yield return new ChessLabTerminalEvent(ChessLabStream.Uci, payload, engine, direction);

                if (direction == ChessLabDirection.Recv
                    && payload.StartsWith("id name ", StringComparison.OrdinalIgnoreCase))
                {
                    string idName = payload["id name ".Length..].Trim();
                    string? expected = engine.Equals("Laplace", StringComparison.OrdinalIgnoreCase)
                        ? "Laplace"
                        : engine.Equals("Stockfish", StringComparison.OrdinalIgnoreCase)
                            ? "Stockfish"
                            : engineIndex == 0 ? "Laplace" : engineIndex == 1 ? "Stockfish" : null;

                    if (expected is not null)
                    {
                        bool matches = idName.Equals(expected, StringComparison.OrdinalIgnoreCase)
                                       || idName.StartsWith(expected + " ", StringComparison.OrdinalIgnoreCase);
                        if (!matches)
                        {
                            _identityFailure ??=
                                $"engine slot '{engine}' ({engineIndex}) identified as '{idName}', expected {expected}";
                            yield return new ChessLabLogEvent("error", _identityFailure);
                        }
                        else if (expected == "Laplace" && !_laplaceIdentityVerified)
                        {
                            _laplaceIdentityVerified = true;
                            yield return new ChessLabLogEvent("info", $"verified Laplace UCI identity: {idName}");
                        }
                        else if (expected == "Stockfish" && !_stockfishIdentityVerified)
                        {
                            _stockfishIdentityVerified = true;
                            yield return new ChessLabLogEvent("info", $"verified Stockfish UCI identity: {idName}");
                        }
                    }
                }

                if (direction == ChessLabDirection.Send && payload.StartsWith("position ", StringComparison.Ordinal))
                {
                    // The "position" line cutechess sends before every "go" carries the full move
                    // list of the game so far — replaying it (instead of per-engine bestmove lines)
                    // makes the live board robust to ordering and to which engine is about to move.
                    foreach (var evt in _tracker.ApplyPositionLine(payload["position ".Length..]))
                        yield return evt;
                }
                else if (direction == ChessLabDirection.Recv && !_eloRangeChecked && _requestedElo is { } want)
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
                string white = started.Groups[3].Value;
                string black = started.Groups[4].Value;
                _tracker.Reset(index, white, black);
                _startedGames[index] = (white, black);

                if (_requirePairedSchedule)
                {
                    int mate = (index & 1) == 0 ? index - 1 : index + 1;
                    if (_startedGames.TryGetValue(mate, out var other)
                        && (!white.Equals(other.Black, StringComparison.Ordinal)
                            || !black.Equals(other.White, StringComparison.Ordinal)))
                    {
                        _scheduleFailure ??=
                            $"games {Math.Min(index, mate)}/{Math.Max(index, mate)} did not swap colours "
                            + $"({other.White} vs {other.Black}; {white} vs {black})";
                        yield return new ChessLabLogEvent("error", _scheduleFailure);
                    }
                }

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
        IEnumerable<string> lines, int rounds = 10, int? requestedElo = null,
        bool requireEngineIdentity = false, bool requirePairedSchedule = false)
    {
        var parser = new TranscriptParser(rounds, requestedElo, requireEngineIdentity, requirePairedSchedule);
        foreach (var line in lines)
            foreach (var evt in parser.Line(ChessLabStream.Stdout, line))
                yield return evt;
    }

    /// <summary>Drives the production parser over canned (stream, line) pairs.</summary>
    internal static IEnumerable<ChessLabEvent> ParseStreamsForTest(
        IEnumerable<(string Stream, string Text)> lines, int rounds = 10, int? requestedElo = null,
        bool requireEngineIdentity = false, bool requirePairedSchedule = false)
    {
        var parser = new TranscriptParser(rounds, requestedElo, requireEngineIdentity, requirePairedSchedule);
        foreach (var (stream, text) in lines)
            foreach (var evt in parser.Line(stream, text))
                yield return evt;
    }
}
