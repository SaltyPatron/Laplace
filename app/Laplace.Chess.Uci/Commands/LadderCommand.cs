using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Laplace.Chess.Service;
using Laplace.Chess.Uci.Lab;

namespace Laplace.Chess.Uci.Commands;

/// <summary>
/// A rung: an engine at a fixed budget, written as a spec.
///   sf:nodes=1024                 Stockfish, full strength, Threads=1, Hash=16, the installed Syzygy set (syzygy=off: none)
///   lc0:nodes=64[:net=FILE][:backend=cuda-fp16]
///                                 Lc0, Threads=1, a fixed minibatch, no smart pruning, temperature 0
///   laplace[:nodes=N][:substrate=substrate|off]
///                                 laplace-uci (LAPLACE_DB names its database)
/// </summary>
public sealed class Rung
{
    public string Spec { get; }
    public string Kind { get; }
    public long? Nodes { get; }
    public string Name { get; }
    public string Cmd { get; }
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
    public string? Net { get; }
    public string? Backend { get; }

    public Rung(string spec, IReadOnlyDictionary<string, string> cfg)
    {
        Spec = spec;
        var parts = spec.Split(':');
        Kind = parts[0];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in parts.Skip(1))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"'{part}' in '{spec}' is not key=value");
            fields[part[..eq]] = part[(eq + 1)..];
        }
        Nodes = fields.TryGetValue("nodes", out var n) ? long.Parse(n, CultureInfo.InvariantCulture) : null;
        string? cmd;
        switch (Kind)
        {
            case "sf":
                cmd = cfg.GetValueOrDefault("LAPLACE_STOCKFISH") ?? LabFiles.Which("stockfish");
                Options["Threads"] = "1";
                Options["Hash"] = "16";
                if (cfg.GetValueOrDefault("LAPLACE_SYZYGY") is { Length: > 0 } syzygy && fields.GetValueOrDefault("syzygy", "on") != "off")
                    Options["SyzygyPath"] = syzygy;
                Name = $"SF19-n{Nodes}";
                break;
            case "lc0":
            {
                cmd = cfg.GetValueOrDefault("LAPLACE_LC0");
                string net = fields.GetValueOrDefault("net") ?? cfg.GetValueOrDefault("LAPLACE_LC0_NET") ?? "";
                if (!File.Exists(net))
                {
                    // net= may name a network by part of its file name, beside the configured one
                    string dir = Path.GetDirectoryName(cfg.GetValueOrDefault("LAPLACE_LC0_NET", ".")) is { Length: > 0 } d ? d : ".";
                    string want = fields.GetValueOrDefault("net", "");
                    var candidates = Directory.Exists(dir)
                        ? Directory.EnumerateFiles(dir, "*.pb.gz").Where(p => Path.GetFileName(p).Contains(want, StringComparison.Ordinal)).ToList()
                        : [];
                    if (candidates.Count == 1) net = candidates[0];
                }
                Net = net;
                Backend = fields.GetValueOrDefault("backend") ?? cfg.GetValueOrDefault("LAPLACE_LC0_BACKEND") ?? "cuda-fp16";
                bool small = Nodes is not null && Nodes <= 64;
                Options["WeightsFile"] = net;
                Options["Backend"] = Backend;
                Options["Threads"] = "1";
                Options["MinibatchSize"] = small ? "1" : "32";
                Options["SmartPruningFactor"] = "0";
                Options["Temperature"] = "0";
                Options["NNCacheSize"] = "200000";
                string tag = Regex.Replace(Path.GetFileName(net).Split('-')[0], "[^A-Za-z0-9]+", "");
                tag = tag.Length > 6 ? tag[..6] : tag;
                Name = $"Lc0-{(tag.Length > 0 ? tag : "net")}-n{Nodes}";
                break;
            }
            case "laplace":
                cmd = ChessLabPaths.LaplaceUciExecutable();
                Options["Substrate"] = fields.GetValueOrDefault("substrate", "substrate");
                Name = "Laplace" + (Nodes is not null ? $"-n{Nodes}" : "") + (Options["Substrate"] == "off" ? "-off" : "");
                break;
            default:
                throw new ArgumentException($"unknown rung kind in '{spec}' (sf, lc0, laplace)");
        }
        if (string.IsNullOrEmpty(cmd) || !File.Exists(cmd)) throw new ArgumentException($"{spec}: no engine binary ({cmd}); see chess-lab.env");
        Cmd = cmd;
    }

    public ConductedEngine Engine => new(Name, Cmd, Options.ToList(), Nodes);

    public JsonObject Identity()
    {
        var o = new JsonObject
        {
            ["spec"] = Spec, ["name"] = Name, ["cmd"] = Cmd, ["sha256"] = LabFiles.Sha256(Cmd), ["options"] = Json.Options(Options),
        };
        if (Kind == "lc0")
        {
            o["network"] = new JsonObject { ["file"] = Net, ["sha256"] = File.Exists(Net) ? LabFiles.Sha256(Net!) : null };
            o["backend"] = Backend;
        }
        return o;
    }
}

/// <summary>A ladder run's result: the Ordo table, the run directory, the receipt, and whether every match exited cleanly.</summary>
public sealed record LadderResult(string Table, string? Run, JsonObject? Receipt, bool Failed, string? Warning);

/// <summary>
/// The conventional ladder Laplace is measured against (docs/guides/chess-lab.md, The ladder): calibrate plays each rung
/// against the next (paired, colour-swapped, balanced book); gauntlet plays the seed against every rung; rate runs Ordo
/// over a run's PGNs with one rung fixed; stockfish-receipt writes the installed Stockfish's identity. Every run is a
/// directory holding the games, fastchess's output and log, the Ordo table and receipt.json. A rating is a chess-engine
/// rating on this ladder, not a Glicko-2 standing and not a human rating.
/// </summary>
public static class LadderCommand
{
    public const string Statement = "chess-engine ratings on this ladder, anchored as stated; not a Glicko-2 standing, not a human rating";

    public static LadderResult Run(ChessCommandLine cmd, IToolRunner? runner = null, Action<string>? progress = null,
        IReadOnlyDictionary<string, string>? config = null)
    {
        runner ??= ProcessToolRunner.Instance;
        progress ??= static _ => { };
        string action = cmd.Positionals.Count > 0 ? cmd.Positionals[0]
            : throw new ArgumentException("ladder takes calibrate, gauntlet, rate or stockfish-receipt");
        var cfg = config ?? ChessLabConfig.Read();

        if (action == "stockfish-receipt")
        {
            string sfBinary = cfg.GetValueOrDefault("LAPLACE_STOCKFISH") ?? LabFiles.Which("stockfish") ?? throw new ArgumentException("LAPLACE_STOCKFISH is not set");
            var record = Conductor.StockfishReceipt(runner, sfBinary);
            string text = record.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (cmd.Value("out") is { } outFile) File.WriteAllText(outFile, text + "\n");
            return new LadderResult(text, null, record, false, null);
        }
        if (action == "rate")
        {
            string run = cmd.Positionals.Count > 1 ? cmd.Positionals[1] : throw new ArgumentException("ladder rate RUN_DIR --anchor NAME");
            string anchor = cmd.Value("anchor") ?? throw new ArgumentException("ladder rate needs --anchor");
            var rated = Conductor.Ordo(runner, cfg, run, anchor, AnchorElo(cmd), ladder: cmd.Value("ladder"));
            return new LadderResult(rated["table"]!.GetValue<string>(), run, null, false, null);
        }
        if (action is not ("calibrate" or "gauntlet"))
            throw new ArgumentException($"unknown ladder action '{action}' (calibrate, gauntlet, rate, stockfish-receipt)");

        int games = cmd.Int("games", 40);
        if (games < 2 || games % 2 != 0) throw new ArgumentException("--games must be even (colour-swapped pairs)");
        int concurrency = cmd.Int("concurrency", 4);
        string? affinity = cmd.Value("affinity"), tc = cmd.Value("tc");
        var rungs = (cmd.Value("rungs") ?? throw new ArgumentException("--rungs is required (comma-separated rung specs)"))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => new Rung(s, cfg)).ToList();
        Rung? seed = action == "gauntlet" ? new Rung(cmd.Value("seed") ?? throw new ArgumentException("gauntlet needs --seed (the rung under test)"), cfg) : null;
        var every = (seed is null ? new List<Rung>() : [seed]).Concat(rungs).ToList();
        GuardGpu(runner, every);
        string fastchess = Conductor.Fastchess(cfg);
        string outDir = Conductor.NewRun(cmd.Value("out") ?? Conductor.DefaultLadderDir(), action);
        var (book, bookReceipt) = Conductor.BookArgs(cfg, cmd.Value("book") ?? "8moves_v3.pgn",
            long.Parse(cmd.Value("srand") ?? "20261006", CultureInfo.InvariantCulture), cmd.Int("plies", 16));
        var adjudication = Conductor.Adjudication(cfg);
        var common = book.Concat(adjudication).ToList();
        int? wheaBefore = Conductor.Whea19(runner);
        var matches = new List<ConductedRun>();
        if (seed is null)
        {
            for (int i = 0; i + 1 < rungs.Count; i++)
            {
                var (low, high) = (rungs[i], rungs[i + 1]);
                matches.Add(Conductor.Run(runner, fastchess, outDir, $"{low.Name}_vs_{high.Name}",
                    [(low.Engine, low.Spec), (high.Engine, high.Spec)], common, games, concurrency, affinity, tc));
                progress($"{low.Name} vs {high.Name}: " + string.Join(" | ", matches[^1].Summary.TakeLast(3)));
            }
        }
        else
        {
            matches.Add(Conductor.Run(runner, fastchess, outDir, $"{seed.Name}_gauntlet",
                every.Select(r => (r.Engine, r.Spec)).ToList(), common, games, concurrency, affinity, tc, tournament: "gauntlet"));
            progress(string.Join(" | ", matches[^1].Summary));
        }
        string anchorName = cmd.Value("anchor") ?? rungs[0].Name;
        var rating = Conductor.Ordo(runner, cfg, outDir, anchorName, AnchorElo(cmd), ladder: cmd.Value("ladder"));
        int? wheaAfter = Conductor.Whea19(runner);

        var (gpu, _) = every.Any(static r => r.Kind == "lc0") ? Conductor.GpuMode(runner) : (null, null);
        var sf = every.FirstOrDefault(static r => r.Kind == "sf");
        var receipt = new JsonObject { ["format"] = 1, ["kind"] = action, ["run"] = Path.GetFileName(outDir) };
        foreach (var (k, v) in Conductor.Machine()) receipt[k] = v?.DeepClone();
        receipt["conductor"] = Conductor.ConductorIdentity(fastchess);
        receipt["stockfish"] = sf is null ? null : Conductor.StockfishReceipt(runner, sf.Cmd);
        receipt["gpu"] = gpu;
        receipt["rungs"] = new JsonArray(every.Select(static r => (JsonNode?)r.Identity()).ToArray());
        receipt["book"] = bookReceipt;
        receipt["adjudication"] = Json.Array(adjudication);
        receipt["games_per_pairing"] = games;
        receipt["concurrency"] = concurrency;
        receipt["affinity"] = affinity;
        receipt["tc"] = tc;
        receipt["matches"] = new JsonArray(matches.Select(static m => (JsonNode?)m.ToJson()).ToArray());
        receipt["ordo"] = new JsonObject
        {
            ["anchor"] = anchorName, ["anchor_elo"] = AnchorElo(cmd), ["argv"] = rating["argv"]!.DeepClone(), ["ladder"] = cmd.Value("ladder"),
            ["error"] = rating["error"]?.DeepClone(),
        };
        receipt["whea19"] = new JsonObject { ["before"] = wheaBefore, ["after"] = wheaAfter };
        receipt["statement"] = Statement;
        File.WriteAllText(Path.Combine(outDir, "receipt.json"), receipt.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        string? warning = wheaBefore is not null && wheaAfter != wheaBefore
            ? $"WARNING: WHEA-Logger event 19 count went from {wheaBefore} to {wheaAfter}" : null;
        return new LadderResult(rating["table"]!.GetValue<string>(), outDir, receipt, matches.Any(static m => m.Exit != 0), warning);
    }

    private static double AnchorElo(ChessCommandLine cmd)
        => double.Parse(cmd.Value("anchor-elo") ?? "0", CultureInfo.InvariantCulture);

    /// <summary>Lc0 rungs need the GPU, and on HART-DESKTOP chess-GPU mode: this checks it and never switches it.</summary>
    public static void GuardGpu(IToolRunner runner, IEnumerable<Rung> rungs)
    {
        if (!rungs.Any(static r => r.Kind == "lc0")) return;
        var (gpu, held) = Conductor.GpuMode(runner);
        if (gpu is null) throw new ArgumentException("Lc0 rungs need an NVIDIA GPU (nvidia-smi not found)");
        if (held is { Count: > 0 })
            throw new ArgumentException($"Lc0 rungs need chess-GPU mode: {string.Join(", ", held)} still hold the card (Laplace-Operations chess/chess-gpu.ps1 on)");
    }
}
