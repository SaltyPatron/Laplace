using System.Text.Json;
using System.Text.Json.Serialization;
using Laplace.Chess.Service;
using Laplace.Chess.Service.Uci;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.Lichess;

internal sealed record LichessProfile(string Label, string Environment, string Implementation,
    string Knowledge, string ConnectionFile, string ConnectorProfile, int MaxConcurrent = 2,
    string[]? Speeds = null, bool Rated = false);

internal sealed record LichessControlConfiguration(int Version, int Port, string StateFile,
    string TokenFile, string ConnectorExecutable, string ConnectorConfiguration,
    Dictionary<string, LichessProfile> Profiles)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static LichessControlConfiguration Read(string path)
    {
        var c = JsonSerializer.Deserialize<LichessControlConfiguration>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty Lichess controller configuration.");
        if (c.Version != 1 || c.Port is < 1 or > 65535 || c.Profiles.Count == 0)
            throw new InvalidDataException("Invalid Lichess controller version, port or profiles.");
        foreach (var file in new[] { c.StateFile, c.TokenFile, c.ConnectorExecutable, c.ConnectorConfiguration })
            if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file))
                throw new InvalidDataException("Lichess controller paths must be explicitly configured absolute paths.");
        foreach (var (id, p) in c.Profiles)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(p.Label)
                || string.IsNullOrWhiteSpace(p.Environment) || string.IsNullOrWhiteSpace(p.Knowledge)
                || p.Implementation != "laplace-mono" || string.IsNullOrWhiteSpace(p.ConnectorProfile)
                || !Path.IsPathFullyQualified(p.ConnectionFile))
                throw new InvalidDataException($"Invalid monorepo Lichess profile: {id}.");
            new LichessOptions(p.MaxConcurrent, Challenges: Policy(p)).Validate();
        }
        return c;
    }

    public static LichessChallengePolicy Policy(LichessProfile p) =>
        new(p.Speeds?.ToHashSet(StringComparer.OrdinalIgnoreCase), p.Rated);

    public UciLichessEngine Engine(LichessProfile profile, ChessKnowledgeIdentity knowledge) =>
        new(profile.Label, new UciProcessStart(ConnectorExecutable,
            ["connect", "--configuration", ConnectorConfiguration, "--profile", profile.ConnectorProfile],
            HandshakeTimeout: TimeSpan.FromSeconds(120)), new Dictionary<string, string>(), 300)
        { RequiredKnowledge = knowledge };

    public static string Connection(LichessProfile profile) =>
        ManagedServiceDatabase.Resolve(File.ReadAllText(profile.ConnectionFile).Trim());
}
