using System.Globalization;
using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci.Engines;

namespace Laplace.Chess.Uci.Commands;

/// <summary>A parsed command line: the verb and its options. Parsing is separate from running and from output, so a
/// richer console can sit on top of the same commands.</summary>
public sealed record ChessCommandLine(string Verb, IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, string> Options, IReadOnlySet<string> Flags)
{
    public static readonly string[] Verbs = ["uci", "engines", "analyse", "bestmove", "compare", "help"];

    public static ChessCommandLine Parse(IReadOnlyList<string> args)
    {
        string verb = args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal) ? "uci" : args[0];
        if (verb == "analyze") verb = "analyse";
        if (!Verbs.Contains(verb)) throw new ArgumentException($"unknown command '{verb}' (commands: {string.Join(", ", Verbs)})");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = verb == "uci" && (args.Count == 0 || args[0] != "uci") ? 0 : 1; i < args.Count; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"unexpected argument '{a}'");
            string key = a[2..];
            if (key is "warm" or "startpos") { flags.Add(key); continue; }
            if (i + 1 >= args.Count) throw new ArgumentException($"--{key} needs a value");
            string value = args[++i];
            if (key == "option")
            {
                int eq = value.IndexOf('=');
                if (eq <= 0) throw new ArgumentException("--option takes Name=Value");
                options[value[..eq].Trim()] = value[(eq + 1)..].Trim();
            }
            else values[key] = value;
        }
        return new ChessCommandLine(verb, values, options, flags);
    }

    public string? Value(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public string Engine => Value("engine") ?? "laplace";

    public ChessPosition Position => ChessPosition.From(Value("fen"),
        Value("moves")?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries));

    public UciLimits Limits
    {
        get
        {
            long? L(string k) => Value(k) is { } s ? long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) : null;
            var limits = new UciLimits(Nodes: L("nodes"), Depth: L("depth") is { } d ? (int)d : null, MoveTimeMs: L("movetime"));
            if (!limits.Bounded) throw new ArgumentException("give a bound: --nodes, --depth or --movetime");
            return limits;
        }
    }

    public int MultiPv => Value("multipv") is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : 1;
}

/// <summary>The commands' results, as data. <see cref="ChessCommands"/> produces them; a writer renders them.</summary>
public sealed record EngineListing(string Name, bool Available, string Profile, int MaxProcesses,
    IReadOnlyList<KeyValuePair<string, string>> ProfileOptions, EngineIdentity? Identity = null, string? Error = null);

public sealed record BestMoveResult(string Engine, string? BestMove, string? Ponder, EngineScore? Score, EngineReceipt Receipt);

public sealed record CompareEntry(string Engine, EngineAnalysis? Analysis, CommandError? Error);

public sealed record CommandError(string Kind, string Message)
{
    public static CommandError From(Exception ex) => ex switch
    {
        UciEngineException u => new(u.Failure.ToString().ToLowerInvariant(), u.Message),
        ArgumentException or FormatException => new("rejected", ex.Message),
        _ => new("failed", ex.Message),
    };
}

/// <summary>The non-UCI commands of laplace-uci: engines, analyse, bestmove, compare. Each returns data.</summary>
public static class ChessCommands
{
    public static IReadOnlyList<EngineListing> Engines(string? profile = null)
        => ChessEngineCatalog.Names.Select(name =>
        {
            ChessEngineSpec spec;
            try { spec = ChessEngineCatalog.Resolve(name, profile); }
            catch (UciEngineException ex) { return new EngineListing(name, false, profile ?? "play", 0, [], Error: ex.Message); }
            if (spec.Start is null) return new EngineListing(name, false, spec.Profile, spec.MaxProcesses, spec.ProfileOptions, Error: spec.Missing);
            try
            {
                // The handshake names the engine; nothing is loaded (Lc0 reads its network on isready, not uci).
                using var process = UciProcess.Start(spec.Start);
                return new EngineListing(name, true, spec.Profile, spec.MaxProcesses, spec.ProfileOptions, process.Identity);
            }
            catch (UciEngineException ex)
            {
                return new EngineListing(name, false, spec.Profile, spec.MaxProcesses, spec.ProfileOptions, Error: ex.Message);
            }
        }).ToList();

    public static EngineAnalysis Analyse(ChessCommandLine cmd, string engine, string defaultProfile = "analysis")
    {
        var spec = ChessEngineCatalog.Resolve(engine, cmd.Value("profile") ?? defaultProfile);
        var position = cmd.Position;
        var limits = cmd.Limits;
        using var session = EngineSession.Open(spec, cmd.Options);
        return session.Analyse(position, limits, cmd.MultiPv, cmd.Flags.Contains("warm") ? "warm" : "fresh");
    }

    public static BestMoveResult BestMove(ChessCommandLine cmd)
    {
        var analysis = Analyse(cmd, cmd.Engine, "play");
        return new BestMoveResult(cmd.Engine, analysis.BestMove.Move?.Text, analysis.BestMove.Ponder?.Text,
            analysis.Final?.Score, analysis.Receipt);
    }

    public static IReadOnlyList<CompareEntry> Compare(ChessCommandLine cmd)
    {
        var engines = cmd.Value("engines")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? ChessEngineCatalog.Names;
        _ = cmd.Position; _ = cmd.Limits; // reject a bad request before starting any engine
        return engines.Select(engine =>
        {
            try { return new CompareEntry(engine, Analyse(cmd, engine), null); }
            catch (Exception ex) when (ex is UciEngineException or ArgumentException or FormatException)
            {
                return new CompareEntry(engine, null, CommandError.From(ex));
            }
        }).ToList();
    }

    public const string Usage = """
        laplace-uci: the chess entry point.
          laplace-uci [uci] [--engine laplace|stockfish|lc0] [--profile P] [--option Name=Value]...
              a UCI engine on stdin/stdout (default: Laplace's own engine; stockfish/lc0 through their pinned profiles)
          laplace-uci engines [--profile P]                    the configured engines and their identities (JSON)
          laplace-uci analyse --engine E [--fen F] [--moves "e2e4 e7e5"] (--nodes N | --depth D | --movetime MS)
                              [--multipv K] [--profile P] [--option Name=Value]... [--warm]
          laplace-uci bestmove (same arguments; profile play)
          laplace-uci compare [--engines laplace,stockfish,lc0] (same arguments): the same limits on each engine
          Option names may be the engine's own or canonical: threads hash multipv wdl syzygy network backend overhead ponder substrate.
        """;
}
