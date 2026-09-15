using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Laplace.Chess.Service;

/// <summary>One match's requested configuration and observed execution, retained beside its job artifacts.</summary>
internal sealed class CutechessExperimentReceipt(string id, CutechessOptions options,
    IReadOnlyDictionary<string, string> config)
{
    public int FormatVersion => 1;
    public string ExperimentId { get; } = id;
    public string? PgnEvent { get; } = options.Event;
    public CutechessOptions RequestedOptions { get; } = options;
    public IReadOnlyDictionary<string, string> JobConfig { get; } = config;
    public string UnspecifiedStockfishOptions => "Use the installed engine defaults recorded in UciConfiguration.";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; private set; }
    public string OperatingSystem { get; } = RuntimeInformation.OSDescription;
    public string ProcessArchitecture { get; } = RuntimeInformation.ProcessArchitecture.ToString();
    public int ProcessVisibleProcessors { get; } = Environment.ProcessorCount;
    public ChessLabJobState MatchState { get; private set; } = ChessLabJobState.Pending;
    public string? MatchMessage { get; private set; }
    public bool? Ingested { get; set; }
    public ChessLabCommandEvent? Command { get; private set; }
    public Dictionary<string, ArtifactIdentity> Artifacts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ArtifactIdentity> ArtifactsAfterMatch { get; } = new(StringComparer.Ordinal);
    public bool? ArtifactIdentitiesUnchanged { get; private set; }
    public Dictionary<string, List<string>> UciConfiguration { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> UciConfigurationByInstance { get; } = new(StringComparer.Ordinal);
    public List<ChessLabGameEvent> Games { get; } = [];
    public Dictionary<string, double> Metrics { get; } = new(StringComparer.Ordinal);

    public async Task ObserveAsync(ChessLabEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case ChessLabCommandEvent command:
                Command = command;
                MatchState = ChessLabJobState.Running;
                Artifacts["cutechess"] = await IdentifyAsync(command.FileName, ct);
                string engine = "engine";
                foreach (var arg in command.Arguments)
                {
                    if (arg.StartsWith("name=", StringComparison.Ordinal)) engine = arg[5..];
                    else if (arg.StartsWith("cmd=", StringComparison.Ordinal))
                    {
                        var executable = arg[4..];
                        Artifacts[engine] = await IdentifyAsync(executable, ct);
                        // .NET apphosts alone do not identify the Laplace implementation.
                        // Record the managed and native payload that ships beside this executable.
                        if (engine == "Laplace" && Path.GetDirectoryName(executable) is { Length: > 0 } directory
                            && Directory.Exists(directory))
                            foreach (var payload in Directory.EnumerateFiles(directory)
                                         .Where(p => Path.GetExtension(p) is ".dll" or ".so"
                                             || p.EndsWith(".deps.json", StringComparison.Ordinal)
                                             || p.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
                                         .Order(StringComparer.Ordinal))
                                Artifacts["Laplace/" + Path.GetFileName(payload)] = await IdentifyAsync(payload, ct);
                    }
                    else if (arg.StartsWith("file=", StringComparison.Ordinal))
                        Artifacts["openingSuite"] = await IdentifyAsync(arg[5..], ct);
                }
                break;
            case ChessLabTerminalEvent { Engine: { } name } terminal when
                terminal.Text.StartsWith("id ", StringComparison.Ordinal)
                || terminal.Text.StartsWith("option name ", StringComparison.Ordinal)
                || terminal.Text.StartsWith("setoption name ", StringComparison.Ordinal):
                if (!UciConfiguration.TryGetValue(name, out var lines)) UciConfiguration[name] = lines = [];
                var line = $"{terminal.Direction}: {terminal.Text}";
                if (!lines.Contains(line, StringComparer.Ordinal)) lines.Add(line);
                if (terminal.EngineInstance is { } instance)
                {
                    string key = $"{name}({instance})";
                    if (!UciConfigurationByInstance.TryGetValue(key, out var instanceLines))
                        UciConfigurationByInstance[key] = instanceLines = [];
                    if (!instanceLines.Contains(line, StringComparer.Ordinal)) instanceLines.Add(line);
                }
                break;
            case ChessLabGameEvent game:
                Games.RemoveAll(g => g.Index == game.Index);
                Games.Add(game);
                Games.Sort((a, b) => a.Index.CompareTo(b.Index));
                break;
            case ChessLabMetricEvent metric:
                Metrics[metric.Name] = metric.Value;
                break;
            case ChessLabDoneEvent done:
                Complete(done.FinalState, done.Message);
                break;
        }
    }

    public void Complete(ChessLabJobState state, string? message)
    {
        MatchState = state;
        MatchMessage = message;
        FinishedAt = DateTimeOffset.UtcNow;
    }

    public async Task<bool> VerifyArtifactsAsync(CancellationToken ct)
    {
        if (Artifacts.Count == 0) return true;
        bool unchanged = true;
        foreach (var (name, before) in Artifacts)
        {
            var after = await IdentifyAsync(before.Path, ct);
            ArtifactsAfterMatch[name] = after;
            unchanged &= before.Sha256 is not null && before.Sha256 == after.Sha256
                && before.Bytes == after.Bytes && before.Error is null && after.Error is null;
        }
        ArtifactIdentitiesUnchanged = unchanged;
        return unchanged;
    }

    public async Task WriteAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + ".pending";
        await using (var file = File.Create(pending))
            await JsonSerializer.SerializeAsync(file, this,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }, CancellationToken.None);
        File.Move(pending, path, overwrite: true);
    }

    private static async Task<ArtifactIdentity> IdentifyAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return new(path, null, null, "not found");
        await using var file = File.OpenRead(path);
        var length = file.Length;
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
        return new(Path.GetFullPath(path), length, sha256, null);
    }

    public sealed record ArtifactIdentity(string Path, long? Bytes, string? Sha256, string? Error);
}
