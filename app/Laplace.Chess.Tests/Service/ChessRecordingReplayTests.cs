using System.Text.Json;
using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessRecordingReplayTests
{
    private static readonly byte[] Entity = Enumerable.Repeat((byte)1, 16).ToArray();
    private static readonly byte[] Physicality = Enumerable.Repeat((byte)2, 16).ToArray();
    private static readonly byte[] Witness = Enumerable.Repeat((byte)3, 16).ToArray();
    private static ChessRecordingMeasurement.StoredScopeRow[] Rows() =>
    [new(1, Convert.ToHexStringLower(Entity), 0),
     new(2, Convert.ToHexStringLower(Physicality), 0),
     new(3, Convert.ToHexStringLower(Witness), 17)];

    [Fact]
    public void ExactScopeComparesObservationCountsAndAllCanonicalIds()
    {
        var original = Rows();
        ChessRecordingMeasurement.ValidateScopeCoverage([Entity], [Physicality], [Witness], original);
        Assert.True(ChessRecordingMeasurement.ScopeRowsEqual(original, original.Reverse().ToArray()));
        var changed = original.ToArray();
        changed[2] = changed[2] with { ObservationCount = 18 };
        Assert.False(ChessRecordingMeasurement.ScopeRowsEqual(original, changed));
        changed = original.ToArray();
        changed[0] = changed[0] with { Id = Convert.ToHexStringLower(Physicality) };
        Assert.False(ChessRecordingMeasurement.ScopeRowsEqual(original, changed));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("zero-witness")]
    public void IncompleteOrConflictingScopeCannotProveReplay(string mutation)
    {
        var rows = Rows();
        var changed = mutation switch
        {
            "missing" => rows[..2],
            "duplicate" => new[] { rows[0], rows[0], rows[2] },
            "extra" => rows.Append(rows[0] with { Id = "not-selected" }).ToArray(),
            _ => new[] { rows[0], rows[1], rows[2] with { ObservationCount = 0 } },
        };
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.ValidateScopeCoverage(
            [Entity], [Physicality], [Witness], changed));
    }

    [Fact]
    public void NoOpReplayIsExplicitAndCannotFabricateFreshWalAcknowledgement()
    {
        var replay = new ChessRecordingMeasurement("test", 1, retainedPgn: true);
        Assert.False(replay.IsVerifiedNoOpReplay);
        var rows = Rows();
        replay.ReplayScopes.Add(new([Convert.ToHexStringLower(Entity)], [Convert.ToHexStringLower(Physicality)],
            [Convert.ToHexStringLower(Witness)], rows, rows, true));
        Assert.True(replay.IsVerifiedNoOpReplay);
        Assert.Null(replay.Durability);
        Assert.Throws<InvalidDataException>(() => replay.Complete("completed")); // no native game proof
        var fresh = new ChessRecordingMeasurement("test", 1);
        fresh.ReplayScopes.Add(replay.ReplayScopes.Single());
        Assert.False(fresh.IsVerifiedNoOpReplay);
        Assert.Throws<InvalidDataException>(() => fresh.Complete("completed"));
        replay.Writer.ApplyCalls = 1;
        Assert.False(replay.IsVerifiedNoOpReplay);
    }

    [Fact]
    public void FabricatedUnchangedFlagDoesNotHideObservationAmplification()
    {
        var replay = new ChessRecordingMeasurement("test", 1, retainedPgn: true);
        var rows = Rows();
        var changed = rows.ToArray(); changed[2] = changed[2] with { ObservationCount = 18 };
        replay.ReplayScopes.Add(new([], [], [], rows, changed, true));
        Assert.False(replay.IsVerifiedNoOpReplay);
    }

    [Fact]
    public void RetainedMatchValidationUsesRecordedCompletionWithoutChangingExperiment()
    {
        string json = JsonSerializer.Serialize(new
        {
            experimentId = "match", matchState = "Completed", artifactIdentitiesUnchanged = true,
            command = new { arguments = new[] { "-each", "depth=4" } },
            games = new[] { new { index = 1, white = "Laplace", black = "Stockfish", result = "1-0 (White mates)" } },
        });
        new ChessRecordingMeasurement("match", 1, retainedPgn: true).ValidateRetainedMatch(json);
        Assert.Throws<InvalidDataException>(() => new ChessRecordingMeasurement("other", 1, true).ValidateRetainedMatch(json));
        Assert.Throws<InvalidDataException>(() => new ChessRecordingMeasurement("match", 2, true).ValidateRetainedMatch(json));
        Assert.Throws<InvalidDataException>(() => new ChessRecordingMeasurement("match", 1, true)
            .ValidateRetainedMatch(json.Replace("Completed", "Failed", StringComparison.Ordinal)));
    }
}
