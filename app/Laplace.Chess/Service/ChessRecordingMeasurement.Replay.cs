using System.Diagnostics;
using System.Text.Json;
using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

internal sealed partial class ChessRecordingMeasurement
{
    public List<ScopeObservation> ReplayScopes { get; } = [];
    public sealed record StoredScopeRow(short Kind, string Id, long ObservationCount);
    public sealed record ScopeObservation(string[] EntityIds, string[] PhysicalityIds,
        string[] WitnessIds, IReadOnlyList<StoredScopeRow> Before, IReadOnlyList<StoredScopeRow> After,
        bool Unchanged);
    internal sealed record ScopeRequest(byte[][] Entities, byte[][] Physicalities,
        byte[][] Witnesses, byte[][] WitnessTypes, IReadOnlyList<StoredScopeRow> Before);

    public string ReplayScope => "Exact explicit EntityRows emitted by the source game recorder and experiment builder; "
        + "selected nonempty line Content physicalities; exact source playing/header/setup/result "
        + "and experiment witnesses with observation_count. Native text-stage interior rows, "
        + "calculated analysis lanes, shared bootstrap rows and unrelated service writes are outside this snapshot.";

    internal bool IsVerifiedNoOpReplay => retainedPgn && NovelGames == 0 && AppliedGames == 0
        && Writer.ApplyCalls == 0 && Writer.EntitiesInserted == 0 && Writer.PhysicalitiesInserted == 0
        && Writer.AttestationsInserted == 0 && ReplayScopes.Count > 0
        && ReplayScopes.All(s => s.Unchanged && ScopeRowsEqual(s.Before, s.After));

    internal void ValidateRetainedMatch(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var games = root.GetProperty("games").Deserialize<ChessLabGameEvent[]>(options)
            ?? throw new InvalidDataException("retained experiment game inventory is absent");
        var arguments = root.GetProperty("command").GetProperty("arguments").Deserialize<string[]>(options);
        if (!Enum.TryParse<ChessLabJobState>(root.GetProperty("matchState").GetString(), out var state))
            throw new InvalidDataException("retained experiment state is invalid");
        ValidateMatchObservation(root.GetProperty("experimentId").GetString(), state,
            root.GetProperty("artifactIdentitiesUnchanged").GetBoolean(), games, arguments);
    }

    internal async Task<ScopeRequest> ReadScopeBeforeAsync(NpgsqlDataSource ds,
        IReadOnlyList<EntityRow> sourceEntities, IReadOnlyList<AttestationRow> witnesses,
        IReadOnlyList<PhysicalityRow> carriers, CancellationToken ct)
    {
        var entities = sourceEntities.Select(e => e.Id).Distinct().Select(id => id.ToBytes()).ToArray();
        var physicalities = carriers.Select(p => p.Id).Distinct().Select(id => id.ToBytes()).ToArray();
        var ids = witnesses.Select(a => a.Id).Distinct().Select(id => id.ToBytes()).ToArray();
        var types = witnesses.Select(a => a.TypeId).Distinct().Select(id => id.ToBytes()).ToArray();
        var before = await ReadScopeAsync(ds, entities, physicalities, ids, types, ct);
        return new(entities, physicalities, ids, types, before);
    }

    internal async Task ObserveScopeAfterAsync(NpgsqlDataSource ds, ScopeRequest scope, CancellationToken ct)
    {
        var after = await ReadScopeAsync(ds, scope.Entities, scope.Physicalities, scope.Witnesses, scope.WitnessTypes, ct);
        ValidateScopeCoverage(scope.Entities, scope.Physicalities, scope.Witnesses, after);
        ReplayScopes.Add(new(scope.Entities.Select(Convert.ToHexStringLower).ToArray(),
            scope.Physicalities.Select(Convert.ToHexStringLower).ToArray(),
            scope.Witnesses.Select(Convert.ToHexStringLower).ToArray(), scope.Before, after,
            ScopeRowsEqual(scope.Before, after)));
    }

    private async Task<IReadOnlyList<StoredScopeRow>> ReadScopeAsync(NpgsqlDataSource ds,
        byte[][] entities, byte[][] physicalities, byte[][] witnesses, byte[][] types, CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            var rows = await NpgsqlSubstrateReads.RecordingScopeAsync(ds, entities, physicalities, witnesses, types, ct);
            return rows.Select(r => new StoredScopeRow(r.Kind, Hex(ReadId(r.Id)), r.ObservationCount))
                .OrderBy(r => r.Kind).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
        }
        finally { ElapsedSeconds.Readback += Stopwatch.GetElapsedTime(start).TotalSeconds; }
    }

    internal static bool ScopeRowsEqual(IReadOnlyList<StoredScopeRow> before, IReadOnlyList<StoredScopeRow> after) =>
        before.OrderBy(r => r.Kind).ThenBy(r => r.Id, StringComparer.Ordinal)
            .SequenceEqual(after.OrderBy(r => r.Kind).ThenBy(r => r.Id, StringComparer.Ordinal));

    internal static void ValidateScopeCoverage(byte[][] entities, byte[][] physicalities, byte[][] witnesses,
        IReadOnlyList<StoredScopeRow> rows)
    {
        var expected = entities.Select(id => (Kind: (short)1, Id: Convert.ToHexStringLower(id)))
            .Concat(physicalities.Select(id => (Kind: (short)2, Id: Convert.ToHexStringLower(id))))
            .Concat(witnesses.Select(id => (Kind: (short)3, Id: Convert.ToHexStringLower(id)))).ToHashSet();
        if (rows.Count != expected.Count || rows.Select(r => (r.Kind, r.Id)).Distinct().Count() != rows.Count
            || rows.Any(r => !expected.Contains((r.Kind, r.Id)) || (r.Kind == 3 && r.ObservationCount < 1)))
            throw new InvalidDataException("retained game scope is missing, duplicated or has invalid witness counts");
    }
}
