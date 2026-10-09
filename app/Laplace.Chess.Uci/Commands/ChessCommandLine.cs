using System.Globalization;
using Laplace.Chess.Service.Uci;

namespace Laplace.Chess.Uci.Commands;

/// <summary>A parsed command line: the verb, its positional arguments and its options. Parsing is separate from running
/// and from output, so a richer console can sit on top of the same commands.</summary>
public sealed record ChessCommandLine(string Verb, IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, string> Options, IReadOnlySet<string> Flags)
{
    public static readonly string[] Verbs =
        ["uci", "engines", "analyse", "bestmove", "compare", "match", "ladder", "check", "review", "lichess", "inspect", "help"];

    /// <summary>Options that take no value.</summary>
    public static readonly IReadOnlySet<string> BooleanFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "warm", "startpos", "json", "require-data", "require-ladder", "check-latest", "cutechess-gui", "rated",
        "no-tb", "recover", "all-positions",
    };

    /// <summary>Arguments that are not options, in order: the ladder action and run directory, the PGN to review or
    /// inspect, the moves or FEN to inspect.</summary>
    public IReadOnlyList<string> Positionals { get; init; } = [];

    /// <summary>Every value of an option given more than once (--speed), in order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Repeated { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public static ChessCommandLine Parse(IReadOnlyList<string> args)
    {
        string verb = args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal) ? "uci" : args[0];
        if (verb == "analyze") verb = "analyse";
        if (!Verbs.Contains(verb)) throw new ArgumentException($"unknown command '{verb}' (commands: {string.Join(", ", Verbs)})");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var repeated = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        // uci, engines, analyse, bestmove and compare take options only; the others take positional arguments too.
        bool takesPositionals = verb is "ladder" or "review" or "inspect";
        for (int i = verb == "uci" && (args.Count == 0 || args[0] != "uci") ? 0 : 1; i < args.Count; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                if (!takesPositionals) throw new ArgumentException($"unexpected argument '{a}'");
                positionals.Add(a);
                continue;
            }
            string key = a[2..];
            if (BooleanFlags.Contains(key)) { flags.Add(key); continue; }
            if (i + 1 >= args.Count) throw new ArgumentException($"--{key} needs a value");
            string value = args[++i];
            if (key == "option")
            {
                int eq = value.IndexOf('=');
                if (eq <= 0) throw new ArgumentException("--option takes Name=Value");
                options[value[..eq].Trim()] = value[(eq + 1)..].Trim();
                continue;
            }
            values[key] = value;
            if (!repeated.TryGetValue(key, out var list)) repeated[key] = list = [];
            list.Add(value);
        }
        return new ChessCommandLine(verb, values, options, flags)
        {
            Positionals = positionals,
            Repeated = repeated.ToDictionary(static p => p.Key, static p => (IReadOnlyList<string>)p.Value, StringComparer.OrdinalIgnoreCase),
        };
    }

    public string? Value(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public bool Flag(string key) => Flags.Contains(key);

    public int Int(string key, int fallback)
        => Value(key) is { } s ? int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) : fallback;

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
