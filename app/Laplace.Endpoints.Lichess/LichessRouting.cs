using Laplace.Chess.Service;

namespace Laplace.Endpoints.Lichess;

internal sealed record LichessReadSource(string Label, string Implementation, ChessKnowledgeIdentity Identity);
internal sealed record LichessRecordingTarget(string Label, string Environment, string Implementation,
    string ConnectionFile, ChessKnowledgeIdentity Identity);
internal sealed record LichessExecutionRoute(string ConnectorProfile, string[] RecordingTargets);
internal sealed record LichessExecutor(string Label, string Environment, string Implementation,
    Dictionary<string, LichessExecutionRoute> Sources, int MaxConcurrent = 2,
    string[]? Speeds = null, bool Rated = false);
internal sealed record LichessLegacySelection(string ReadSource, string RecordingTarget);
internal sealed record LichessRouting(Dictionary<string, LichessExecutor> Executors,
    Dictionary<string, LichessReadSource> Sources, Dictionary<string, LichessRecordingTarget> RecordingTargets,
    Dictionary<string, LichessLegacySelection>? LegacySelections = null)
{
    public void Validate()
    {
        if (Executors is not { Count: > 0 } || Sources is not { Count: > 0 } || RecordingTargets is not { Count: > 0 })
            throw new InvalidDataException("Declare execution locations, read sources and recording destinations.");
        foreach (var (id, source) in Sources)
        {
            RequireName(id, source.Label, source.Implementation);
            RequireIdentity(source.Identity);
        }
        foreach (var (id, target) in RecordingTargets)
        {
            RequireName(id, target.Label, target.Implementation);
            RequireIdentity(target.Identity);
            if (string.IsNullOrWhiteSpace(target.Environment) || !Path.IsPathFullyQualified(target.ConnectionFile))
                throw new InvalidDataException($"Invalid recording destination: {id}.");
        }
        foreach (var (id, executor) in Executors)
        {
            RequireName(id, executor.Label, executor.Implementation);
            if (string.IsNullOrWhiteSpace(executor.Environment) || executor.Sources is not { Count: > 0 })
                throw new InvalidDataException($"Invalid execution location: {id}.");
            new LichessOptions(executor.MaxConcurrent,
                Challenges: new(executor.Speeds?.ToHashSet(StringComparer.OrdinalIgnoreCase), executor.Rated)).Validate();
            foreach (var (source, route) in executor.Sources)
            {
                if (!Sources.ContainsKey(source) || string.IsNullOrWhiteSpace(route.ConnectorProfile)
                    || route.RecordingTargets is not { Length: > 0 }
                    || route.RecordingTargets.Any(target => !RecordingTargets.ContainsKey(target)))
                    throw new InvalidDataException($"Invalid authorized route for execution location {id} and read source {source}.");
            }
        }
        foreach (var (profile, old) in LegacySelections ?? [])
        {
            var activation = Resolve(profile, old.ReadSource, old.RecordingTarget);
            if (activation.ReadIdentity != activation.WriteIdentity)
                throw new InvalidDataException("A legacy combined selection must migrate to the same read/write database identity.");
        }
    }

    public LichessSelection Restore(LichessSelection selection)
    {
        if (selection.Profile is not null && selection.ReadSource is null && selection.RecordingTarget is null
            && LegacySelections?.TryGetValue(selection.Profile, out var old) == true)
            return selection with { ReadSource = old.ReadSource, RecordingTarget = old.RecordingTarget };
        return selection;
    }

    public LichessActivation Resolve(string executorId, string? sourceId, string? targetId)
    {
        if (!Executors.TryGetValue(executorId, out var executor)
            || sourceId is null || !executor.Sources.TryGetValue(sourceId, out var route)
            || targetId is null || !route.RecordingTargets.Contains(targetId, StringComparer.Ordinal))
            throw new ArgumentException("The installed grants do not authorize this execution location, read source and recording destination.");
        var source = Sources[sourceId];
        var target = RecordingTargets[targetId];
        return new(executor.Label, route.ConnectorProfile, source.Identity, target.ConnectionFile, target.Identity,
            executor.MaxConcurrent, new(executor.Speeds?.ToHashSet(StringComparer.OrdinalIgnoreCase), executor.Rated));
    }

    private static void RequireName(string id, string label, string implementation)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(label) || implementation != "laplace-mono")
            throw new InvalidDataException("Monorepo chess routes require named monorepo installations.");
    }

    private static void RequireIdentity(ChessKnowledgeIdentity identity)
    {
        if (identity is null || !ulong.TryParse(identity.Cluster, out _) || string.IsNullOrWhiteSpace(identity.Database))
            throw new InvalidDataException("A source or destination must declare its actual PostgreSQL cluster and database identity.");
    }
}

internal sealed record LichessActivation(string Label, string ConnectorProfile, ChessKnowledgeIdentity? ReadIdentity,
    string ConnectionFile, ChessKnowledgeIdentity? WriteIdentity, int MaxConcurrent, LichessChallengePolicy Policy);
