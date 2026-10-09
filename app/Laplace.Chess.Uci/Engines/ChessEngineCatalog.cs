using System.Text.Json;
using Laplace.Chess.Service;
using Laplace.Chess.Service.Uci;
using Laplace.Engine.Core;

namespace Laplace.Chess.Uci.Engines;

/// <summary>A configured engine: how to start it, the pinned profile options it is given, the canonical option names
/// it maps, and how many processes of it may exist at once. Start is null when it is not installed (Missing says why).</summary>
public sealed record ChessEngineSpec(
    string Name, string Profile, UciProcessStart? Start, string? Missing,
    IReadOnlyList<KeyValuePair<string, string>> ProfileOptions, int MaxProcesses)
{
    public bool Available => Start is not null;

    /// <summary>A request's option name in this engine's terms: a canonical name (threads, hash, multipv, wdl, syzygy,
    /// network, backend, overhead, ponder, substrate) maps per engine; any other name is the engine's own.</summary>
    public string MapOption(string name)
        => ChessEngineCatalog.OptionNames.TryGetValue(Name, out var map) && map.TryGetValue(name, out var native) ? native : name;
}

/// <summary>
/// The one place engine configuration lives: laplace (this binary's own backend), stockfish and lc0, resolved from
/// chess-lab.env (or the environment) and the pinned profiles deploy/stockfish-profiles.json and deploy/lc0-profiles.json.
/// </summary>
public static class ChessEngineCatalog
{
    public static readonly string[] Names = ["laplace", "stockfish", "lc0"];

    internal static readonly Dictionary<string, Dictionary<string, string>> OptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stockfish"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["threads"] = "Threads", ["hash"] = "Hash", ["multipv"] = "MultiPV", ["wdl"] = "UCI_ShowWDL",
            ["syzygy"] = "SyzygyPath", ["network"] = "EvalFile", ["overhead"] = "Move Overhead", ["ponder"] = "Ponder",
        },
        ["lc0"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["threads"] = "Threads", ["hash"] = "NNCacheSize", ["multipv"] = "MultiPV", ["wdl"] = "UCI_ShowWDL",
            ["syzygy"] = "SyzygyPath", ["network"] = "WeightsFile", ["backend"] = "Backend", ["overhead"] = "MoveOverheadMs",
            ["ponder"] = "Ponder",
        },
        ["laplace"] = new(StringComparer.OrdinalIgnoreCase) { ["substrate"] = "Substrate" },
    };

    /// <summary>The default profile for a purpose: "analysis" for analyse/compare, "play" for bestmove, uci and Lichess.</summary>
    public static ChessEngineSpec Resolve(string name, string? profile = null)
    {
        string engine = name.Trim().ToLowerInvariant();
        string p = string.IsNullOrWhiteSpace(profile) ? "play" : profile.Trim();
        return engine switch
        {
            "laplace" => Laplace(p),
            "stockfish" => FromProfile(engine, p, ChessLabPaths.Stockfish is { Found: true, Path: { } sf } ? sf : null,
                "Stockfish is not installed (LAPLACE_STOCKFISH in chess-lab.env)", maxProcesses: 2),
            "lc0" => FromProfile(engine, p, Config("LAPLACE_LC0") is { } lc0 && File.Exists(lc0) ? lc0 : null,
                "Lc0 is not installed (LAPLACE_LC0 in chess-lab.env)", maxProcesses: 1),
            _ => throw new UciEngineException(UciFailure.Rejected, $"unknown engine '{name}' (known: {string.Join(", ", Names)})"),
        };
    }

    /// <summary>laplace-uci itself: this process when it is laplace-uci, else the copy beside this assembly, the
    /// published tool (LAPLACE_TOOLS\chess\app) or the build output.</summary>
    public static string? LaplaceUciPath() => ChessLabPaths.LaplaceUciExecutable();

    private static ChessEngineSpec Laplace(string profile)
    {
        var exe = LaplaceUciPath();
        return new ChessEngineSpec("laplace", profile, exe is null ? null : new UciProcessStart(exe, ["uci"]),
            exe is null ? "laplace-uci is not built or published (scripts/win/publish-uci.cmd)" : null, [], MaxProcesses: 2);
    }

    private static ChessEngineSpec FromProfile(string engine, string profile, string? exe, string missing, int maxProcesses)
    {
        var options = ProfileOptions(engine, profile);
        string? network = options.FirstOrDefault(static o => o.Key is "WeightsFile" or "EvalFile").Value;
        if (network is not null && !Path.IsPathRooted(network)) network = null; // an embedded network is named by the binary
        return new ChessEngineSpec(engine, profile,
            exe is null ? null : new UciProcessStart(exe, NetworkPath: network, HandshakeTimeout: TimeSpan.FromSeconds(30)),
            exe is null ? missing : null, options, maxProcesses);
    }

    /// <summary>A pinned profile's options in order, ${VAR} expanded; an option whose variable is unset is left out
    /// (the engine keeps its default), and the receipt shows exactly what was sent.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ProfileOptions(string engine, string profile)
    {
        string file = engine + "-profiles.json";
        string? path = new[]
        {
            Path.Combine(AppContext.BaseDirectory, file),
            LaplaceInstall.TryRepoRoot(out var repo) ? Path.Combine(repo, "deploy", file) : null,
        }.FirstOrDefault(static p => p is not null && File.Exists(p));
        if (path is null) throw new UciEngineException(UciFailure.Unavailable, $"{file} is not installed beside laplace-uci");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.GetProperty("profiles").TryGetProperty(profile, out var p))
            throw new UciEngineException(UciFailure.Rejected, $"{engine} has no profile '{profile}' in {file}");
        var list = new List<KeyValuePair<string, string>>();
        foreach (var option in p.GetProperty("options").EnumerateObject())
        {
            string value = option.Value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => option.Value.GetRawText(),
                _ => option.Value.GetString() ?? "",
            };
            if (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'))
            {
                string key = value[2..^1];
                string? expanded = key == "LAPLACE_SYZYGY" ? ChessLabPaths.InstalledSyzygyEnginePath() : Config(key);
                if (string.IsNullOrWhiteSpace(expanded)) continue;
                value = expanded;
            }
            else if (value.Contains("..", StringComparison.Ordinal)) continue; // a range (UCI_Elo "1320..3190") is chosen per job
            list.Add(new(option.Name, value));
        }
        return list;
    }

    private static string? Config(string key) => LaplaceInstall.TryReadConfig(key, "chess-lab.env");
}
