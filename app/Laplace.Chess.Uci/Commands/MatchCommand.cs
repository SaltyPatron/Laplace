using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci.Engines;
using Laplace.Chess.Uci.Lab;

namespace Laplace.Chess.Uci.Commands;

/// <summary>
/// One side of a match, written as <c>engine[:key=value]...</c>: <c>laplace</c>, <c>stockfish</c>, <c>lc0</c> (each from
/// <see cref="ChessEngineCatalog"/> with its pinned profile) or the path of any UCI executable. Keys: <c>nodes</c> (a node
/// budget per move), <c>profile</c>, <c>name</c> (the PGN name); any other key is a UCI option, canonical names mapped
/// to the engine's own (threads, hash, syzygy, network, backend...).
/// </summary>
public sealed record MatchEngineSpec(string Spec, string Engine, long? Nodes, string? Profile, string? Name,
    IReadOnlyList<KeyValuePair<string, string>> Options)
{
    public static MatchEngineSpec Parse(string spec)
    {
        // A Windows path has a drive colon: split options only after the executable.
        int start = spec.Length > 2 && spec[1] == ':' && spec[2] is '\\' or '/' ? 2 : 0;
        int colon = spec.IndexOf(':', start);
        string engine = colon < 0 ? spec : spec[..colon];
        long? nodes = null;
        string? profile = null, name = null;
        var options = new List<KeyValuePair<string, string>>();
        if (colon >= 0)
            foreach (var part in spec[(colon + 1)..].Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) throw new ArgumentException($"'{part}' in '{spec}' is not key=value");
                string key = part[..eq], value = part[(eq + 1)..];
                switch (key)
                {
                    case "nodes": nodes = long.Parse(value, CultureInfo.InvariantCulture); break;
                    case "profile": profile = value; break;
                    case "name": name = value; break;
                    default: options.Add(new(key, value)); break;
                }
            }
        if (engine.Length == 0) throw new ArgumentException($"'{spec}' names no engine");
        return new MatchEngineSpec(spec, engine, nodes, profile, name, options);
    }

    public bool Catalogued => ChessEngineCatalog.Names.Contains(Engine, StringComparer.OrdinalIgnoreCase);

    /// <summary>The engine as fastchess starts it, and where it comes from.</summary>
    public (ConductedEngine Engine, UciProcessStart Start) Resolve()
    {
        if (!Catalogued)
        {
            if (!File.Exists(Engine)) throw new ArgumentException($"{Spec}: no engine '{Engine}' (laplace, stockfish, lc0 or the path of a UCI executable)");
            string exe = Path.GetFullPath(Engine);
            return (new ConductedEngine(Name ?? Path.GetFileNameWithoutExtension(exe), exe, Options, Nodes), new UciProcessStart(exe));
        }
        string key = Engine.ToLowerInvariant();
        var spec = ChessEngineCatalog.Resolve(key, Profile ?? (key == "stockfish" ? "ladder" : "play"));
        if (spec.Start is null) throw new UciEngineException(UciFailure.Unavailable, spec.Missing ?? $"{key} is not installed");
        var options = spec.ProfileOptions.ToDictionary(static o => o.Key, static o => o.Value, StringComparer.Ordinal);
        foreach (var (k, v) in Options) options[spec.MapOption(k)] = v;
        string display = Name ?? key switch
        {
            "stockfish" => "Stockfish" + (Nodes is { } n ? $"-n{n}" : ""),
            "lc0" => "Lc0" + (Nodes is { } n2 ? $"-n{n2}" : ""),
            _ => "Laplace" + (Nodes is { } n3 ? $"-n{n3}" : ""),
        };
        return (new ConductedEngine(display, spec.Start.ExePath, options.ToList(), Nodes, spec.Start.Arguments), spec.Start);
    }
}

/// <summary>
/// <c>match</c>: a fastchess match between two engines, paired and colour-swapped, from a book or the start position,
/// under a clock or a node budget, adjudicated by rules, tablebase and a move limit only, and receipted: the conductor
/// and both engines by SHA-256 and UCI identity, the exact argv, the book and its seed, the PGN and fastchess's table.
/// </summary>
public static class MatchCommand
{
    public static JsonObject Run(ChessCommandLine cmd, IToolRunner? runner = null, IReadOnlyDictionary<string, string>? config = null,
        Func<UciProcessStart, EngineIdentity>? identify = null)
    {
        runner ??= ProcessToolRunner.Instance;
        identify ??= static start => { using var p = UciProcess.Start(start); return p.Identity; };
        var cfg = config ?? ChessLabConfig.Read();
        var a = MatchEngineSpec.Parse(cmd.Value("engine1") ?? throw new ArgumentException("match needs --engine1 and --engine2"));
        var b = MatchEngineSpec.Parse(cmd.Value("engine2") ?? throw new ArgumentException("match needs --engine1 and --engine2"));
        int games = cmd.Int("games", 2);
        if (games < 2 || games % 2 != 0) throw new ArgumentException("--games must be even (colour-swapped pairs)");
        int concurrency = cmd.Int("concurrency", 1);
        string? tc = cmd.Value("tc");
        long? eachNodes = cmd.Value("nodes") is { } n ? long.Parse(n, CultureInfo.InvariantCulture) : null;
        long seed = long.Parse(cmd.Value("seed") ?? cmd.Value("srand") ?? "20261006", CultureInfo.InvariantCulture);
        var engines = new[] { a, b }.Select(s => s.Nodes is null && eachNodes is not null ? s with { Nodes = eachNodes } : s)
            .Select(s => (Spec: s, Resolved: s.Resolve())).ToList();
        if (engines[0].Resolved.Engine.Name == engines[1].Resolved.Engine.Name)
            engines[1] = (engines[1].Spec, engines[1].Resolved with { Engine = engines[1].Resolved.Engine with { Name = engines[1].Resolved.Engine.Name + "-2" } });
        string fastchess = Conductor.Fastchess(cfg);

        string bookName = cmd.Value("book") ?? "8moves_v3.pgn";
        List<string> bookArgs;
        JsonObject? bookReceipt = null;
        if (bookName == "none") bookArgs = ["-srand", seed.ToString(CultureInfo.InvariantCulture)];
        else
        {
            (bookArgs, bookReceipt) = Conductor.BookArgs(cfg, bookName, seed, cmd.Int("plies", 16));
            if (bookReceipt["file"]?.GetValue<string>() is { } file) bookReceipt["sha256_actual"] = LabFiles.Sha256(file);
        }
        var adjudication = Conductor.Adjudication(cfg, cmd.Int("maxmoves", 200), tablebases: !cmd.Flag("no-tb"));

        string outDir = cmd.Value("out") is { } o ? Directory.CreateDirectory(o).FullName : Conductor.NewRun(Conductor.DefaultMatchDir(), "match");
        string label = $"{engines[0].Resolved.Engine.Name}_vs_{engines[1].Resolved.Engine.Name}";
        int? wheaBefore = Conductor.Whea19(runner);
        var run = Conductor.Run(runner, fastchess, outDir, label, engines.Select(e => (e.Resolved.Engine, e.Spec.Spec)).ToList(),
            bookArgs.Concat(adjudication), games, concurrency, cmd.Value("affinity"), tc,
            eventName: cmd.Value("event") ?? $"laplace-match/{Path.GetFileName(outDir)}/{label}");
        string pgn = run.Pgn;
        if (cmd.Value("pgn") is { } pgnOut && File.Exists(run.Pgn))
        {
            File.Copy(run.Pgn, pgnOut, overwrite: true);
            pgn = Path.GetFullPath(pgnOut);
        }

        var receipt = new JsonObject { ["format"] = 1, ["kind"] = "match", ["run"] = Path.GetFileName(outDir) };
        foreach (var (k, v) in Conductor.Machine()) receipt[k] = v?.DeepClone();
        receipt["conductor"] = Conductor.ConductorIdentity(fastchess);
        receipt["engines"] = new JsonArray(engines.Select(e => (JsonNode?)EngineJson(e.Spec, e.Resolved.Engine, e.Resolved.Start, identify)).ToArray());
        receipt["argv"] = Json.Array(run.Argv);
        receipt["book"] = bookReceipt;
        receipt["seed"] = seed;
        receipt["adjudication"] = Json.Array(adjudication);
        receipt["tc"] = tc;
        receipt["nodes"] = eachNodes;
        receipt["games"] = games;
        receipt["concurrency"] = concurrency;
        receipt["affinity"] = cmd.Value("affinity");
        receipt["pgn"] = pgn;
        receipt["match"] = run.ToJson();
        receipt["whea19"] = new JsonObject { ["before"] = wheaBefore, ["after"] = Conductor.Whea19(runner) };
        receipt["statement"] = "a match result between these engine generations under this protocol; not a Glicko-2 standing, not a human rating";
        File.WriteAllText(Path.Combine(outDir, "receipt.json"), receipt.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return receipt;
    }

    private static JsonObject EngineJson(MatchEngineSpec spec, ConductedEngine engine, UciProcessStart start, Func<UciProcessStart, EngineIdentity> identify)
    {
        JsonNode? identity;
        try { identity = JsonSerializer.SerializeToNode(identify(start), UciJson.Options); }
        catch (UciEngineException ex) { identity = new JsonObject { ["error"] = ex.Message }; }
        return new JsonObject
        {
            ["spec"] = spec.Spec, ["name"] = engine.Name, ["cmd"] = engine.Cmd, ["args"] = Json.Array(engine.Args ?? []),
            ["sha256"] = LabFiles.Sha256(engine.Cmd), ["options"] = Json.Options(engine.Options), ["nodes"] = engine.Nodes,
            ["identity"] = identity,
        };
    }
}
