using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>Everything the gauntlet needs to build a conductor (cutechess-cli or fastchess) invocation.</summary>
public sealed partial record CutechessOptions
{
    public ChessEngineConnection? LaplaceConnection { get; init; }
    public ChessEngineConnection? StockfishConnection { get; init; }
    /// <summary>
    /// The match conductor: <c>cutechess</c> (cutechess-cli, the default and the GUI lab's continuity) or
    /// <c>fastchess</c> (pentanomial statistics, CPU affinity, no Qt; the conductor for matches, SPRT and the
    /// ladder, Laplace-Wiki Sequence/Conflicts.md). Both receive the same engines, budget and openings.
    /// </summary>
    public string Conductor { get; init; } = "cutechess";

    /// <summary>
    /// fastchess <c>-use-affinity</c> CPU list ("0,2,4,6" or "16-31"): every engine process is pinned to one of
    /// these logical processors. Time-controlled games on a hybrid CPU need it so both engines get equal cores.
    /// </summary>
    public string? Affinity { get; init; }

    /// <summary>
    /// Total games to play. Paired gauntlets use two games per encounter and half as
    /// many Cute Chess rounds, so each opening is played from both colours. Unpaired
    /// gauntlets use one game per encounter.
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

    // Null deliberately delegates to the installed engine's advertised default.
    // These apply only to Stockfish; Laplace's UCI surface does not advertise them.
    public int? StockfishThreads { get; init; }
    public int? StockfishHashMb { get; init; }
    public string? StockfishNumaPolicy { get; init; }
    public string? StockfishSyzygyPath { get; init; }

    // Stockfish 19 src/engine.cpp and src/engine.h advertise these spin ranges.
    public static int MaximumStockfishThreads => Math.Max(1024, 4 * Environment.ProcessorCount);
    public static int MaximumStockfishHashMb => Environment.Is64BitProcess ? 33554432 : 2048;

    public CutechessOptions WithStockfishConfiguration(IReadOnlyDictionary<string, string> config)
    {
        string? Text(string key) => config.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim() : null;
        int? Number(string key)
        {
            var raw = Text(key);
            if (raw is null) return null;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new ArgumentException($"{key} must be a whole number.", key);
            return value;
        }
        var result = this with
        {
            StockfishThreads = Number("stockfishThreads"),
            StockfishHashMb = Number("stockfishHashMb"),
            StockfishNumaPolicy = Text("stockfishNumaPolicy"),
            StockfishSyzygyPath = Text("stockfishSyzygyPath"),
        };
        result.ValidateStockfishConfiguration();
        return result;
    }

    public void ValidateStockfishConfiguration()
    {
        if (StockfishThreads is { } threads && (threads < 1 || threads > MaximumStockfishThreads))
            throw new ArgumentOutOfRangeException(nameof(StockfishThreads),
                $"Stockfish threads must be between 1 and {MaximumStockfishThreads} per game.");
        if (StockfishHashMb is { } hash && (hash < 1 || hash > MaximumStockfishHashMb))
            throw new ArgumentOutOfRangeException(nameof(StockfishHashMb),
                $"Stockfish hash must be between 1 and {MaximumStockfishHashMb} MiB per game.");
        if (!string.IsNullOrWhiteSpace(StockfishNumaPolicy)
            && StockfishNumaPolicy.Trim() is not ("auto" or "system" or "hardware" or "none"))
            throw new ArgumentException("Stockfish NUMA policy must be auto, system, hardware, or none.",
                nameof(StockfishNumaPolicy));
        if (StockfishSyzygyPath?.Any(char.IsControl) == true)
            throw new ArgumentException("Stockfish Syzygy path cannot contain control characters.", nameof(StockfishSyzygyPath));
        if (Conductor is not ("cutechess" or "fastchess"))
            throw new ArgumentException("Conductor must be cutechess or fastchess.", nameof(Conductor));
        if (!string.IsNullOrWhiteSpace(Affinity))
        {
            if (Conductor != "fastchess")
                throw new ArgumentException("CPU affinity is a fastchess option; cutechess-cli has none.", nameof(Affinity));
            if (!AffinityRegex().IsMatch(Affinity.Trim()))
                throw new ArgumentException("Affinity must be a CPU list such as 0,2,4,6 or 16-31.", nameof(Affinity));
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+(-\d+)?(,\d+(-\d+)?)*$")]
    private static partial System.Text.RegularExpressions.Regex AffinityRegex();

    /// <summary>
    /// The tablebases Stockfish searches with in a lab match. A job that names <c>stockfishSyzygyPath</c> keeps
    /// it (<c>none</c> plays without tables); otherwise the installed Syzygy set (<c>LAPLACE_SYZYGY</c>, or the
    /// data root's) is given, as the directories that hold its files, so a lab match never silently runs
    /// Stockfish without the tables the host has. The receipt records the path either way.
    /// </summary>
    public CutechessOptions WithInstalledSyzygy(IReadOnlyDictionary<string, string> config, Func<string?> installedProbePath)
    {
        if (StockfishConnection is not null) return this;
        if (config.TryGetValue("stockfishSyzygyPath", out var raw) && !string.IsNullOrWhiteSpace(raw))
            return raw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
                ? this with { StockfishSyzygyPath = null }
                : this;
        return installedProbePath() is { Length: > 0 } path ? this with { StockfishSyzygyPath = path } : this;
    }

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

    /// <summary>PGN <c>Event</c> tag, which PGN admission composes as the playing's event.</summary>
    public string? Event { get; init; }
}

public static partial class CutechessRunner
{
    private static List<string> EngineArguments(CutechessOptions options, string laplace, string stockfish)
    {
        var args = new List<string>();
        void Engine(string name, string fallback, ChessEngineConnection? connection)
        {
            args.AddRange(["-engine", "name=" + name, "cmd=" + (connection?.Executable ?? fallback), "proto=uci"]);
            if (connection is null) return;
            if (options.Conductor == "cutechess")
                args.AddRange(connection.Arguments.Select(a => "arg=" + a));
            else
                args.Add("args=" + string.Join(' ', connection.Arguments.Select(QuoteEngineArgument)));
        }
        Engine("Laplace", laplace, options.LaplaceConnection);
        args.Add("option.Substrate=substrate");
        Engine("Stockfish", stockfish, options.StockfishConnection);
        args.Add($"option.UCI_LimitStrength={options.StockfishLimitStrength.ToString().ToLowerInvariant()}");
        return args;
    }

    internal static string QuoteEngineArgument(string value)
    {
        if (value.Any(char.IsControl) || value.Contains('"'))
            throw new ArgumentException("Engine arguments cannot contain control characters or quotes.");
        return value.Any(char.IsWhiteSpace) || value.Contains('\'') ? "\"" + value + "\"" : value;
    }

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
    // between Stockfish releases, so read them from the running engine.
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
        o.ValidateStockfishConfiguration();
        if (o.Conductor == "fastchess") return BuildFastchessArguments(o, laplaceUci, stockfish);
        if (o.PairOpenings && (o.Rounds < 2 || (o.Rounds & 1) != 0))
            throw new ArgumentOutOfRangeException(nameof(o.Rounds), "Paired games require an even total of at least two.");
        // Every key=value is its own argv token; cutechess-cli reads a single joined token as
        // one engine parameter. proto=uci is required because cutechess defaults to xboard.
        // Laplace's substrate mode is passed explicitly so the receipt records it.
        var args = EngineArguments(o, laplaceUci, stockfish);
        if (o.StockfishLimitStrength)
            args.Add($"option.UCI_Elo={o.StockfishElo}");
        if (!string.IsNullOrWhiteSpace(o.StockfishNumaPolicy))
            args.Add($"option.NumaPolicy={o.StockfishNumaPolicy.Trim()}");
        if (o.StockfishThreads is { } threads)
            args.Add($"option.Threads={threads.ToString(CultureInfo.InvariantCulture)}");
        if (o.StockfishHashMb is { } hash)
            args.Add($"option.Hash={hash.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(o.StockfishSyzygyPath))
            args.Add($"option.SyzygyPath={o.StockfishSyzygyPath.Trim()}");
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
            // Put both colour-swapped games in the same encounter and match the
            // repetition count to games-per-encounter, as required by Cute Chess's
            // scheduler. Total requested games remain unchanged below.
            args.Add("-games");
            args.Add("2");
            args.Add("-repeat");
            args.Add("2");
        }

        args.Add("-rounds");
        args.Add((o.PairOpenings ? o.Rounds / 2 : o.Rounds).ToString(CultureInfo.InvariantCulture));
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

        // "-debug all", never a bare "-debug": cutechess's MatchParser rejects a valueless
        // -debug ("Empty value for option") and exits. "all" also sends "debug on" to each
        // engine, which adds to the transcript.
        args.Add("-debug");
        args.Add("all");
        return args;
    }

    /// <summary>The engine traffic log fastchess writes beside the PGN; the runner reads it as the live transcript.</summary>
    public static string FastchessEngineLogPath(CutechessOptions o)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(o.PgnOut)) ?? Environment.CurrentDirectory, "fastchess-engines.log");

    /// <summary>
    /// The same match under fastchess: the same two engines, options, budget, paired openings, rounds, event and
    /// PGN. fastchess prints Cute Chess's result lines (<c>-output format=cutechess</c>), so one parser serves both;
    /// its engine traffic goes to a log file (<c>-log ... engine=true realtime=true</c>) that the runner reads and
    /// presents as Cute Chess debug traffic, so engine identity, the receipt's UCI configuration and the live
    /// board work the same way. Score-based adjudication is not used, as with cutechess.
    /// </summary>
    internal static IReadOnlyList<string> BuildFastchessArguments(CutechessOptions o, string laplaceUci, string stockfish)
    {
        var args = EngineArguments(o, laplaceUci, stockfish);
        if (o.StockfishLimitStrength)
            args.Add($"option.UCI_Elo={o.StockfishElo}");
        if (!string.IsNullOrWhiteSpace(o.StockfishNumaPolicy))
            args.Add($"option.NumaPolicy={o.StockfishNumaPolicy.Trim()}");
        if (o.StockfishThreads is { } threads)
            args.Add($"option.Threads={threads.ToString(CultureInfo.InvariantCulture)}");
        if (o.StockfishHashMb is { } hash)
            args.Add($"option.Hash={hash.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(o.StockfishSyzygyPath))
            args.Add($"option.SyzygyPath={o.StockfishSyzygyPath.Trim()}");
        args.Add("-each");
        if (o.Depth > 0)
        {
            args.Add("tc=inf");
            args.Add($"depth={o.Depth}");
        }
        else
        {
            args.Add($"st={o.SecondsPerMove.ToString(CultureInfo.InvariantCulture)}");
            args.Add("timemargin=2000");
        }

        if (o.PairOpenings)
        {
            args.Add("-openings");
            args.Add($"file={ResolveOpeningsPath(o)}");
            args.Add("format=epd");
            args.Add("order=sequential");
            args.Add("-games");
            args.Add("2");
        }
        else
        {
            args.Add("-games");
            args.Add("1");
        }
        args.Add("-rounds");
        args.Add((o.PairOpenings ? o.Rounds / 2 : o.Rounds).ToString(CultureInfo.InvariantCulture));
        if (o.Concurrency > 1)
        {
            args.Add("-concurrency");
            args.Add(o.Concurrency.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(o.Affinity))
        {
            args.Add("-use-affinity");
            args.Add(o.Affinity.Trim());
        }
        if (!string.IsNullOrWhiteSpace(o.Event))
        {
            args.Add("-event");
            args.Add(o.Event);
        }
        args.Add("-pgnout");
        args.Add($"file={o.PgnOut}");
        args.Add("-output");
        args.Add("format=cutechess");
        args.Add("-log");
        args.Add($"file={FastchessEngineLogPath(o)}");
        args.Add("level=info");
        args.Add("engine=true");
        args.Add("realtime=true");
        return args;
    }

    // fastchess engine log: "[Engine] [18:47:06.894761] <  2>  A <--- uci" (to the engine) and "... A ---> uciok".
    [GeneratedRegex(@"^\[Engine\]\s+\[[^\]]*\]\s+<\s*(\d+)>\s+(\S+)\s+(<---|--->)\s?(.*)$")]
    private static partial Regex FastchessEngineTrafficRegex();

    /// <summary>
    /// One fastchess engine-log line as Cute Chess debug traffic ("0 &gt;Laplace(3): uci"), or null for any other
    /// line. fastchess keeps one pair of engine processes per game thread, so (thread, engine name) names a
    /// process; each gets the next instance number, as Cute Chess numbers its engine processes.
    /// </summary>
    internal static string? FastchessTrafficAsCutechess(string line, Dictionary<(string Thread, string Engine), int> instances)
    {
        var m = FastchessEngineTrafficRegex().Match(line);
        if (!m.Success) return null;
        // The logger's thread token is an opaque process key, not a game index.
        // Linux emits values wider than Int32; no arithmetic is performed on it.
        string thread = m.Groups[1].Value;
        string engine = m.Groups[2].Value;
        if (!instances.TryGetValue((thread, engine), out int instance))
            instances[(thread, engine)] = instance = instances.Count;
        string direction = m.Groups[3].Value == "<---" ? ">" : "<";
        return $"0 {direction}{engine}({instance.ToString(CultureInfo.InvariantCulture)}): {m.Groups[4].Value}";
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
        options.ValidateStockfishConfiguration();
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
            if (!TryPrepareOpeningSuite(options, out var openingPath, out var openingCount, out var availableOpeningLines, out var error))
            {
                yield return new ChessLabLogEvent("error", error!);
                yield return new ChessLabDoneEvent(ChessLabJobState.Failed, error);
                yield break;
            }
            options = options with { OpeningsFile = openingPath };
            yield return new ChessLabLogEvent("info",
                $"paired opening suite: {openingCount} requested positions × 2 colours; {availableOpeningLines} nonempty EPD input lines available ({openingPath})");
        }

        var catalog = ChessLabPaths.Catalog;
        bool fastchess = options.Conductor == "fastchess";
        var cc = catalog[fastchess ? "fastchess" : "cutechess"];
        var sf = options.StockfishConnection?.Probe ?? catalog["stockfish"];
        var qt = catalog["qt"];
        var uci = options.LaplaceConnection?.Probe ?? catalog["laplaceUci"];
        string conductorName = fastchess ? "fastchess" : "cutechess-cli";

        if (!cc.Found || !sf.Found || !uci.Found)
        {
            foreach (var (name, probe, hint) in new[]
                     {
                         (conductorName, cc, fastchess ? "LAPLACE_FASTCHESS" : "LAPLACE_CUTECHESS"),
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

        if (qt.Found && !fastchess)
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

        string? engineLog = fastchess ? FastchessEngineLogPath(options) : null;
        if (engineLog is not null && File.Exists(engineLog)) File.Delete(engineLog);   // fastchess appends; one match per log

        using var proc = Process.Start(psi)!;

        // stdout and stderr merged into one ordered stream. Both must be drained (an unread
        // pipe deadlocks the match), and interleaving places each warning beside the traffic
        // that provoked it.
        var merged = Channel.CreateUnbounded<(string Stream, string Text)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        async Task PumpAsync(StreamReader reader, string stream)
        {
            // Never cancelled: the pumps end at EOF, which the kill in the finally guarantees.
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                // fastchess on Windows reports that it has no console to colour (it runs windowless here); that is
                // not an error of the match, so it is kept in the transcript as ordinary output
                string lineStream = fastchess && stream == ChessLabStream.Stderr
                    && line.StartsWith("Failed to get console mode", StringComparison.Ordinal) ? ChessLabStream.Stdout : stream;
                await merged.Writer.WriteAsync((lineStream, line), CancellationToken.None);
            }
        }

        // fastchess writes engine traffic to its log, not to stdout: follow the file while the match runs and
        // read it to the end once fastchess has exited, presenting each complete line as Cute Chess debug traffic.
        async Task FollowEngineLogAsync(string path)
        {
            var instances = new Dictionary<(string Thread, string Engine), int>();
            while (!File.Exists(path) && !proc.HasExited)
                await Task.Delay(100, CancellationToken.None);
            if (!File.Exists(path)) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var pending = new System.Text.StringBuilder();
            var buffer = new char[16384];
            async Task EmitAsync(string line)
            {
                if (FastchessTrafficAsCutechess(line.TrimEnd('\r'), instances) is { } traffic)
                    await merged.Writer.WriteAsync((ChessLabStream.Stdout, traffic), CancellationToken.None);
            }
            while (true)
            {
                bool exited = proc.HasExited;
                int read;
                while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
                {
                    pending.Append(buffer, 0, read);
                    string text = pending.ToString();
                    int last = text.LastIndexOf('\n');
                    if (last < 0) continue;
                    foreach (var line in text[..last].Split('\n')) await EmitAsync(line);
                    pending.Clear().Append(text[(last + 1)..]);
                }
                if (exited)
                {
                    if (pending.Length > 0) await EmitAsync(pending.ToString());
                    return;
                }
                await Task.Delay(100, CancellationToken.None);
            }
        }

        var pumps = Task.WhenAll(
            PumpAsync(proc.StandardOutput, ChessLabStream.Stdout),
            PumpAsync(proc.StandardError, ChessLabStream.Stderr),
            engineLog is null ? Task.CompletedTask : FollowEngineLogAsync(engineLog));
        _ = pumps.ContinueWith(t => merged.Writer.TryComplete(t.Exception), TaskScheduler.Default);

        try
        {
            await foreach (var (stream, text) in merged.Reader.ReadAllAsync(ct))
                foreach (var evt in parser.Line(stream, text))
                    yield return evt;
        }
        finally
        {
            // Cancellation throws out of the loop above and Process.Dispose kills nothing, so
            // the process tree (cutechess and both engines) is killed here.
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (Exception failure)
            {
                Trace.TraceWarning("CHESS_CONDUCTOR_STOP_FAILED conductor={0} pid={1}: {2}",
                    conductorName, proc.Id, failure);
            }
        }

        await proc.WaitForExitAsync(CancellationToken.None);
        try { await pumps.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
        catch (Exception failure)
        {
            Trace.TraceWarning("CHESS_CONDUCTOR_OUTPUT_INCOMPLETE conductor={0} pid={1}: {2}",
                conductorName, proc.Id, failure);
        }

        int exitCode = proc.ExitCode;
        yield return new ChessLabTerminalEvent(ChessLabStream.Runner, $"{conductorName} exited with code {exitCode}");

        yield return parser.Complete(exitCode);
    }
}
