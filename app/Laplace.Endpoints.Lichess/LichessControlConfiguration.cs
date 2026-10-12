using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Laplace.Chess.Service;
using Laplace.Chess.Service.Uci;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.Lichess;

internal sealed record LichessProfile(string Label, string Environment, string Implementation,
    string Database, string ConnectionFile, string ConnectorProfile, int MaxConcurrent = 2,
    string[]? Speeds = null, bool Rated = false);

internal sealed record LichessControlConfiguration(int Version, int Port, string StateFile,
    string TokenFile, string ConnectorExecutable, string ConnectorConfiguration,
    Dictionary<string, LichessProfile> Profiles, LichessRouting? Routing = null)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static LichessControlConfiguration Read(string path)
    {
        var document = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty Lichess controller configuration.");
        if (document["profiles"] is JsonObject profiles)
            foreach (var (_, profile) in profiles)
                if (profile is JsonObject fields && fields.Remove("knowledge", out var legacy))
                {
                    if (fields.TryGetPropertyValue("database", out var current) && !JsonNode.DeepEquals(current, legacy))
                        throw new InvalidDataException("Conflicting database names in Lichess profile.");
                    fields["database"] = legacy;
                }
        var c = document.Deserialize<LichessControlConfiguration>(Json)
            ?? throw new InvalidDataException("Empty Lichess controller configuration.");
        if (c.Port is < 1 or > 65535
            || !(c.Version == 1 && c.Routing is null && c.Profiles is { Count: > 0 }
                || c.Version == 2 && c.Routing is not null && c.Profiles is { Count: 0 }))
            throw new InvalidDataException("Invalid Lichess controller version, port or profiles.");
        foreach (var file in new[] { c.StateFile, c.TokenFile, c.ConnectorExecutable, c.ConnectorConfiguration })
            if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file))
                throw new InvalidDataException("Lichess controller paths must be explicitly configured absolute paths.");
        foreach (var (id, p) in c.Profiles)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(p.Label)
                || string.IsNullOrWhiteSpace(p.Environment) || string.IsNullOrWhiteSpace(p.Database)
                || p.Implementation != "laplace-mono" || string.IsNullOrWhiteSpace(p.ConnectorProfile)
                || !Path.IsPathFullyQualified(p.ConnectionFile))
                throw new InvalidDataException($"Invalid monorepo Lichess profile: {id}.");
            new LichessOptions(p.MaxConcurrent, Challenges: Policy(p)).Validate();
        }
        c.Routing?.Validate();
        return c;
    }

    public static LichessChallengePolicy Policy(LichessProfile p) =>
        new(p.Speeds?.ToHashSet(StringComparer.OrdinalIgnoreCase), p.Rated);

    public LichessActivation Resolve(string profile, string? readSource, string? recordingTarget)
    {
        if (Routing is not null) return Routing.Resolve(profile, readSource, recordingTarget);
        if (readSource is not null || recordingTarget is not null || !Profiles.TryGetValue(profile, out var legacy))
            throw new ArgumentException("Unknown installed Lichess profile or independent routing is not installed.");
        return new(legacy.Label, legacy.ConnectorProfile, null, legacy.ConnectionFile, null, legacy.MaxConcurrent, Policy(legacy));
    }

    public UciLichessEngine Engine(LichessActivation profile, ChessDatabaseIdentity database) =>
        new(profile.Label, new UciProcessStart(ConnectorExecutable,
            ["connect", "--configuration", ConnectorConfiguration, "--profile", profile.ConnectorProfile],
            HandshakeTimeout: TimeSpan.FromSeconds(120)), new Dictionary<string, string>(), 300)
        { RequiredDatabase = database };

    public static string Connection(LichessActivation profile) =>
        ManagedServiceDatabase.Resolve(File.ReadAllText(profile.ConnectionFile).Trim());
}
