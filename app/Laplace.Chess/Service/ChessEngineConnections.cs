using System.Text.Json;

namespace Laplace.Chess.Service;

/// <summary>Installed UCI connections exposed by the match service. HTTP callers select a name,
/// never an executable, credential file, endpoint, or command line.</summary>
public sealed record ChessEngineConnection(
    string Id, string Label, string Implementation, string Environment, string Knowledge,
    string Host, string Executable, string Configuration)
{
    public IReadOnlyList<string> Arguments => ["connect", "--configuration", Configuration, "--profile", Id];
    public ChessLabPaths.Probe Probe => new(Executable, File.Exists(Executable), "installed UCI connection " + Id);
}

public static class ChessEngineConnections
{
    public static IReadOnlyList<ChessEngineConnection> Read()
    {
        var configuration = ChessRuntimeConfiguration.Read("LAPLACE_CHESS_UCI_CONFIG");
        var executable = ChessRuntimeConfiguration.Read("LAPLACE_CHESS_UCI_CONNECTOR");
        if (configuration is null && executable is null) return [];
        if (configuration is null || executable is null)
            throw new ArgumentException("Declare both LAPLACE_CHESS_UCI_CONFIG and LAPLACE_CHESS_UCI_CONNECTOR.");
        return Read(configuration, executable);
    }

    internal static IReadOnlyList<ChessEngineConnection> Read(string configuration, string executable)
    {
        if (!Path.IsPathFullyQualified(configuration) || !File.Exists(configuration)
            || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("Chess UCI configuration and connector must be existing absolute file paths.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(configuration));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ArgumentException($"Chess UCI configuration cannot be read: {configuration}", ex);
        }
        using (document)
        {
            if (document.RootElement.GetProperty("version").GetInt32() != 1)
                throw new ArgumentException("Unsupported chess UCI configuration version.");
            var result = new List<ChessEngineConnection>();
            foreach (var entry in document.RootElement.GetProperty("profiles").EnumerateObject())
            {
                var profile = entry.Value;
                if (profile.GetProperty("transport").GetString() != "websocket") continue;
                string Required(string key) => profile.GetProperty(key).GetString() is { Length: > 0 } value
                    ? value : throw new ArgumentException($"UCI profile {entry.Name} needs {key}.");
                if (entry.Name.Length == 0 || entry.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                    throw new ArgumentException("Invalid installed UCI profile name.");
                if (!Uri.TryCreate(Required("endpoint"), UriKind.Absolute, out var endpoint)
                    || endpoint.Scheme != "wss" || endpoint.UserInfo.Length != 0
                    || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                    throw new ArgumentException($"UCI profile {entry.Name} must use a credential-free WSS endpoint.");
                result.Add(new(entry.Name, Required("label"), Required("implementation"), Required("environment"),
                    Required("knowledge"), endpoint.Host, executable, configuration));
            }
            return result;
        }
    }

    public static CutechessOptions Select(CutechessOptions options, IReadOnlyDictionary<string, string> config)
    {
        var laplace = config.GetValueOrDefault("laplaceProfile")?.Trim();
        var stockfish = config.GetValueOrDefault("stockfishProfile")?.Trim();
        if (string.IsNullOrEmpty(laplace) && string.IsNullOrEmpty(stockfish)) return options;
        var profiles = Read();
        ChessEngineConnection? Get(string? id, string implementation)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return profiles.SingleOrDefault(p => p.Id == id && p.Implementation == implementation)
                ?? throw new ArgumentException($"No installed {implementation} UCI connection named '{id}'.");
        }
        var result = options with { LaplaceConnection = Get(laplace, "laplace-mono"), StockfishConnection = Get(stockfish, "stockfish") };
        if (result.StockfishConnection is not null && !string.IsNullOrWhiteSpace(result.StockfishSyzygyPath))
            throw new ArgumentException("Remote Stockfish uses its worker's configured Syzygy path; clear the local path override.");
        return result;
    }
}
