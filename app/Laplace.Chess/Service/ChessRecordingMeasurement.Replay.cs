using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public string ReplayScope => "Exact explicit EntityRows emitted by the source game recorder"
        + (IsCorpus ? "; " : " and experiment builder; ")
        + "selected nonempty line Content physicalities; exact source playing/header/setup/result"
        + (IsCorpus ? " witnesses" : " and experiment witnesses")
        + " with observation_count. Native text-stage interior rows, "
        + "calculated analysis lanes, shared bootstrap rows and unrelated service writes are outside this snapshot.";

    internal bool IsVerifiedNoOpReplay => retainedPgn && NovelGames == 0 && AppliedGames == 0
        && Writer.ApplyCalls == 0 && Writer.EntitiesInserted == 0 && Writer.PhysicalitiesInserted == 0
        && Writer.AttestationsInserted == 0
        && (IsCorpus ? CorpusReplayScopesUnchanged : ReplayScopes.Count > 0
            && ReplayScopes.All(s => s.Unchanged && ScopeRowsEqual(s.Before, s.After)));

    // This is a typed projection of CutechessExperimentReceipt written by this
    // service. Source PGN still enters through the registered native grammar;
    // replay transport does not inspect or decompose a source JSON container.
    private sealed record RetainedMatchReceipt(
        [property: JsonRequired] string? ExperimentId,
        [property: JsonRequired] string? MatchState,
        [property: JsonRequired] bool ArtifactIdentitiesUnchanged,
        [property: JsonRequired] ChessLabGameEvent[]? Games,
        [property: JsonRequired] RetainedCommandReceipt? Command);

    private sealed record RetainedCommandReceipt([property: JsonRequired] string[]? Arguments);

    private static RetainedMatchReceipt ReadRetainedMatch(string json)
    {
        RetainedMatchReceipt? receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<RetainedMatchReceipt>(json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("retained experiment receipt has an invalid transport shape", error);
        }
        if (receipt?.Games is null || receipt.Games.Any(g => g is null)
            || receipt.Command?.Arguments is null)
            throw new InvalidDataException("retained experiment game inventory or command is absent");
        return receipt;
    }

    internal static ChessRecordingMeasurement FromRetainedMatch(string experimentId, string json)
    {
        var receipt = ReadRetainedMatch(json);
        var measurement = new ChessRecordingMeasurement(experimentId, receipt.Games!.Length, retainedPgn: true);
        measurement.ValidateRetainedMatch(receipt);
        return measurement;
    }

    internal void ValidateRetainedMatch(string json) => ValidateRetainedMatch(ReadRetainedMatch(json));

    private void ValidateRetainedMatch(RetainedMatchReceipt receipt)
    {
        if (!Enum.TryParse<ChessLabJobState>(receipt.MatchState, out var state))
            throw new InvalidDataException("retained experiment state is invalid");
        ValidateMatchObservation(receipt.ExperimentId, state,
            receipt.ArtifactIdentitiesUnchanged, receipt.Games!, receipt.Command!.Arguments);
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
