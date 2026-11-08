using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public static partial class CutechessRunner
{
    private static string ResolveOpeningsPath(CutechessOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.OpeningsFile)) return Path.GetFullPath(o.OpeningsFile);
        string dir = Path.GetDirectoryName(o.PgnOut) is { Length: > 0 } p ? p : Environment.CurrentDirectory;
        return Path.Combine(dir, "openings.epd");
    }

    internal static bool TryPrepareOpeningSuite(
        CutechessOptions o, out string path, out int openingCount, out int availableOpeningLines, out string? error)
    {
        path = o.OpeningsFile ?? "";
        openingCount = o.Rounds / 2;
        availableOpeningLines = 0;
        error = null;
        try
        {
            path = ResolveOpeningsPath(o);
            if (!string.IsNullOrWhiteSpace(o.OpeningsFile))
            {
                // The selected EPD is operator input; only its framing is checked here, and
                // Cute Chess parses and validates the positions.
                if (!File.Exists(path))
                    throw new FileNotFoundException("selected EPD input does not exist", path);
                if (string.Equals(path, Path.GetFullPath(o.PgnOut),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new IOException("selected EPD input must differ from the PGN output path");
                availableOpeningLines = File.ReadLines(path).Count(line => !string.IsNullOrWhiteSpace(line));
                if (availableOpeningLines < openingCount || availableOpeningLines == 0)
                    throw new InvalidDataException(
                        $"selected EPD input has {availableOpeningLines} nonempty EPD input lines; {openingCount} paired positions were requested");
                return true;
            }

            var fens = new List<string>(openingCount);
            try
            {
                foreach (var fen in OpeningSeed.Fens(OpeningSeed.DefaultDir, Math.Max(1, o.OpeningPlies), openingCount))
                    if (!fens.Contains(fen, StringComparer.Ordinal)) fens.Add(fen);
            }
            catch (Exception failure)
            {
                // A missing opening corpus does not stop the run; the built-in positions
                // below keep the gauntlet paired.
                Trace.TraceWarning("CHESS_OPENINGS_FALLBACK directory={0}; using built-in positions: {1}",
                    OpeningSeed.DefaultDir, failure);
            }

            for (int i = 0; fens.Count < openingCount; i++)
                fens.Add(BuiltinOpeningFens[i % BuiltinOpeningFens.Length]);

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(path, fens.Take(openingCount).Select(ToEpd));
            availableOpeningLines = openingCount;
            return true;
        }
        catch (Exception ex)
        {
            error = $"could not prepare paired opening suite '{path}': {ex.Message}";
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
        string resources = $", Stockfish per game: Threads={o.StockfishThreads?.ToString(CultureInfo.InvariantCulture) ?? "engine default"}"
            + $", Hash={o.StockfishHashMb?.ToString(CultureInfo.InvariantCulture) ?? "engine default"} MiB"
            + $", NumaPolicy={o.StockfishNumaPolicy ?? "engine default"}, SyzygyPath={o.StockfishSyzygyPath ?? "engine default"}";
        string affinity = string.IsNullOrWhiteSpace(o.Affinity) ? "" : $", engines pinned to CPUs {o.Affinity.Trim()}";
        return $"{o.Conductor}: {o.Rounds} games, {clock}, Stockfish {strength}{resources}{schedule}{parallel}{affinity}";
    }

    /// <summary>
    /// Turns one line of cutechess output into lab events. The live run and the tests share
    /// this instance type, so a parser change cannot pass a test that a real match then fails.
    /// </summary>
    internal sealed class TranscriptParser
    {
        private readonly Dictionary<int, GameBoardTracking> _boardGames = new();
        private readonly Dictionary<int, GameBoardTracking> _engineBoardGames = new();
        private readonly HashSet<int> _unresolvedBoardEngines = new();
        private readonly HashSet<int> _unboundBoardWarnings = new();
        private bool _sawBoardReset;
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

        private sealed class EngineBoardSlot(string name)
        {
            public string Name { get; } = name;
            public int? Instance { get; set; }
            public bool Ambiguous { get; set; }
        }

        private sealed class GameBoardTracking
        {
            public int Number { get; }
            public EngineBoardSlot[] Slots { get; }
            public LiveBoardTracker Tracker { get; } = new();

            public GameBoardTracking(int number, string white, string black)
            {
                Number = number;
                Slots = [new(white), new(black)];
                Tracker.Reset(number, white, black);
            }
        }

        private List<(GameBoardTracking Game, EngineBoardSlot Slot)> BoardCandidates(string engine)
            => _boardGames.Values.SelectMany(game => game.Slots
                .Where(slot => slot.Instance is null && slot.Name.Equals(engine, StringComparison.Ordinal))
                .Select(slot => (Game: game, Slot: slot))).ToList();

        private IEnumerable<ChessLabEvent> BeginEngineBoardSession(string engine, int instance)
        {
            _sawBoardReset = true;
            _unboundBoardWarnings.Remove(instance);
            _unresolvedBoardEngines.Remove(instance);
            bool resetBeforeFinish = _engineBoardGames.Remove(instance, out var previous);
            if (resetBeforeFinish)
            {
                foreach (var slot in previous!.Slots.Where(slot => slot.Instance == instance))
                {
                    slot.Instance = null;
                    slot.Ambiguous = true;
                }
            }

            // Official Cute Chess identifies engine instances in debug traffic, but its
            // Started-game banner contains only names. Threads can interleave their starts.
            // Consider every matching unfilled slot, including earlier ambiguous resets;
            // neither banner recency nor engine-id arithmetic proves a game association.
            var candidates = BoardCandidates(engine);
            if (!resetBeforeFinish && candidates.Count == 1 && !candidates[0].Slot.Ambiguous)
            {
                candidates[0].Slot.Instance = instance;
                _engineBoardGames[instance] = candidates[0].Game;
                yield break;
            }

            foreach (var candidate in candidates) candidate.Slot.Ambiguous = true;
            _unresolvedBoardEngines.Add(instance);
            foreach (var evt in UnboundBoardWarning(engine, instance,
                         resetBeforeFinish ? "received another ucinewgame before its game finished" : "has no unique Started-game association"))
                yield return evt;
        }

        private IEnumerable<ChessLabEvent> TrackPosition(string engine, int instance, string positionArgs)
        {
            if (!_engineBoardGames.TryGetValue(instance, out var game)
                && !_sawBoardReset && !_unresolvedBoardEngines.Contains(instance) && _boardGames.Count == 1)
            {
                // Older transcripts may omit ucinewgame. A sole active game is usable;
                // an unresolved concurrent session is never reassigned by elimination.
                var candidates = BoardCandidates(engine);
                if (candidates.Count == 1 && !candidates[0].Slot.Ambiguous)
                {
                    candidates[0].Slot.Instance = instance;
                    _engineBoardGames[instance] = game = candidates[0].Game;
                }
            }

            if (game is null)
            {
                _unresolvedBoardEngines.Add(instance);
                foreach (var evt in UnboundBoardWarning(engine, instance, "has no proven game association"))
                    yield return evt;
                yield break;
            }

            foreach (var evt in game.Tracker.ApplyPositionLine(positionArgs)) yield return evt;
        }

        private IEnumerable<ChessLabEvent> UnboundBoardWarning(string engine, int instance, string reason)
        {
            if (_unboundBoardWarnings.Add(instance))
                yield return new ChessLabLogEvent("warning",
                    $"live board: {engine}({instance}) {reason}; retaining UCI traffic without assigning a numbered board");
        }

        private void FinishBoardGame(int number)
        {
            if (!_boardGames.Remove(number, out var game)) return;
            foreach (var instance in _engineBoardGames.Where(pair => ReferenceEquals(pair.Value, game)).Select(pair => pair.Key).ToArray())
                _engineBoardGames.Remove(instance);
        }

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
                yield return new ChessLabTerminalEvent(ChessLabStream.Uci, payload, engine, direction, engineIndex);

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

                if (direction == ChessLabDirection.Send && payload == "ucinewgame")
                {
                    foreach (var evt in BeginEngineBoardSession(engine, engineIndex)) yield return evt;
                }
                else if (direction == ChessLabDirection.Send && payload.StartsWith("position ", StringComparison.Ordinal))
                {
                    // The "position" line cutechess sends before every "go" carries the full move
                    // list of the game so far — replaying it (instead of per-engine bestmove lines)
                    // makes the live board robust to ordering and to which engine is about to move.
                    foreach (var evt in TrackPosition(engine, engineIndex, payload["position ".Length..]))
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
                _boardGames[index] = new GameBoardTracking(index, white, black);
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
            {
                FinishBoardGame(int.Parse(finished.Groups[1].Value, CultureInfo.InvariantCulture));
                yield return new ChessLabGameEvent(
                    int.Parse(finished.Groups[1].Value, CultureInfo.InvariantCulture),
                    finished.Groups[2].Value,
                    finished.Groups[3].Value,
                    finished.Groups[5].Success && finished.Groups[5].Value.Length > 0
                        ? $"{finished.Groups[4].Value} ({finished.Groups[5].Value})"
                        : finished.Groups[4].Value);
            }

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
        private readonly List<string> _appliedMoves = new();
        private string? _baseFen;
        private int _game;
        private string? _white, _black;

        public void Reset(int game, string? white, string? black)
        {
            _game = game;
            _white = white;
            _black = black;
            _appliedMoves.Clear();
            _baseFen = null;
            _board = Board.FromFen(ChessModality.StartFen);
        }

        // "startpos moves e2e4 e7e5" / "fen <6 fields> moves ..."
        public IEnumerable<ChessLabBoardEvent> ApplyPositionLine(string positionArgs)
        {
            var tok = positionArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int movesIdx = Array.IndexOf(tok, "moves");
            var moves = movesIdx >= 0 ? tok[(movesIdx + 1)..] : [];

            string baseFen;
            if (tok is ["startpos", ..]) baseFen = ChessModality.StartFen;
            else if (tok is ["fen", ..] && tok.Length >= 7) baseFen = string.Join(' ', tok[1..7]);
            else return [];

            if (_baseFen != baseFen || moves.Length < _appliedMoves.Count
                || !_appliedMoves.SequenceEqual(moves.Take(_appliedMoves.Count), StringComparer.Ordinal))
            {
                // Each UCI position declares its starting board and exact move prefix.
                // Initialize from that board even when no moves follow it yet; a changed
                // base, takeback, or divergent prefix must not reuse the previous board.
                _board = Board.FromFen(baseFen);
                _baseFen = baseFen;
                _appliedMoves.Clear();
            }

            var events = new List<ChessLabBoardEvent>(Math.Max(0, moves.Length - _appliedMoves.Count));
            for (int i = _appliedMoves.Count; i < moves.Length; i++)
            {
                if (!TryApplyUci(_board, moves[i])) break;
                _appliedMoves.Add(moves[i]);
                events.Add(new ChessLabBoardEvent(_game, i + 1, moves[i], _board.ToFen(), _white, _black));
            }
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
