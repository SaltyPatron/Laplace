using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessRecordingMeasurementTests
{
    private static Hash128 Id(ulong value) => new(1, value);
    private static AttestationRow Witness => new(Id(1), Id(2), Id(3), Id(4), Id(5), Id(6),
        AttestationOutcome.Confirm, 10, 1, 700_000_000, 350_000_000_000);
    private static NpgsqlAttestationReads.WitnessRow Stored(AttestationRow row) => new(
        row.Id.ToBytes(), row.SubjectId.ToBytes(), row.TypeId.ToBytes(), row.ObjectId?.ToBytes(),
        row.SourceId.ToBytes(), row.ContextId?.ToBytes(), (short)row.Outcome, row.ObservationCount);

    [Fact]
    public void ExactWitnessSetAcceptsOriginalBody()
        => ChessRecordingMeasurement.ValidateWitnesses([Witness], [Stored(Witness)]);

    [Fact]
    public void ActualPgnHeadersDoNotBecomeExtraWitnessesThroughCartesianSelection()
    {
        // Already parsed, zero-move source record: this exercises the real recorder
        // and native content/attestation owners without a second PGN parser.
        var game = new ChessGameRecord(
            "[Event \"readback-scope\"]\n[White \"White\"]\n[Black \"Black\"]\n[Result \"1-0\"]\n",
            [], GameOutcome.WonBy(0), Id(20), Id(21), Id(22));
        var builder = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "readback-scope")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus);
        ChessPgnDecomposer.RecordGame(game, builder);
        var all = builder.Build().Attestations;
        var expected = all.Where(a => ChessRecordingMeasurement.IsGameWitness(a, new HashSet<Hash128> { game.PlayingId })).ToArray();
        var header = Assert.Single(all.Where(a => a.SubjectId == game.LineId
            && a.TypeId == ChessVocabulary.HasEventType && a.ContextId == game.PlayingId));
        Assert.DoesNotContain(header, expected);

        // The old independent column sets admitted this genuine but unselected
        // line-header witness and failed even though every selected row existed.
        var oldRead = all.Where(a => expected.Any(e => e.SubjectId == a.SubjectId)
            && expected.Any(e => e.TypeId == a.TypeId) && expected.Any(e => e.SourceId == a.SourceId)
            && (a.ContextId is not null && expected.Any(e => e.ContextId == a.ContextId)
                || a.ContextId is null && expected.Any(e => e.ContextId is null && e.SubjectId == a.SubjectId))).ToArray();
        Assert.Contains(header, oldRead);
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.ValidateWitnesses(expected, oldRead.Select(Stored).ToArray()));

        var scopes = ChessRecordingMeasurement.WitnessScopes(expected).ToHashSet();
        bool Selected(AttestationRow row) => scopes.Contains(new(row.SubjectId, row.TypeId, row.SourceId, row.ContextId));
        Assert.False(Selected(header));
        var actual = all.Where(Selected).Select(Stored).ToArray();
        ChessRecordingMeasurement.ValidateWitnesses(expected, actual);

        // Exact tuple selection must still expose another object in the same
        // proposition scope, rather than masking it by the expected witness ID.
        var first = expected[0];
        var conflict = NativeAttestation.CategoricalResolved(first.SubjectId, first.TypeId,
            Id(99), first.SourceId, first.ContextId, SourceTrust.StructuredCorpus);
        Assert.True(Selected(conflict));
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.ValidateWitnesses(
            expected, actual.Append(Stored(conflict)).ToArray()));
    }

    [Fact]
    public void WitnessScopeDeduplicatesOnlyCompleteTuples()
    {
        var rows = new[] { Witness, Witness with { Id = Id(90), ObjectId = Id(91) },
            Witness with { Id = Id(92), SourceId = Id(93) }, Witness with { Id = Id(94), ContextId = null } };
        var scopes = ChessRecordingMeasurement.WitnessScopes(rows);
        Assert.Equal(3, scopes.Length);
        Assert.Contains(new NpgsqlAttestationReads.WitnessScope(Witness.SubjectId, Witness.TypeId, Witness.SourceId, null), scopes);
        Assert.Contains(new NpgsqlAttestationReads.WitnessScope(Witness.SubjectId, Witness.TypeId, Id(93), Witness.ContextId), scopes);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("subject")]
    [InlineData("type")]
    [InlineData("object")]
    [InlineData("source")]
    [InlineData("context")]
    [InlineData("outcome")]
    [InlineData("count")]
    [InlineData("width")]
    public void SameIdWithAlteredStoredWitnessBodyRejects(string field)
    {
        var row = Stored(Witness);
        var changed = field switch
        {
            "id" => row with { Id = Id(90).ToBytes() },
            "subject" => row with { SubjectId = Id(90).ToBytes() },
            "type" => row with { TypeId = Id(90).ToBytes() },
            "object" => row with { ObjectId = null },
            "source" => row with { SourceId = Id(90).ToBytes() },
            "context" => row with { ContextId = null },
            "outcome" => row with { Outcome = (short)AttestationOutcome.Refute },
            "width" => row with { SourceId = row.SourceId.Concat(new byte[] { 0 }).ToArray() },
            _ => row with { ObservationCount = 0 },
        };
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.ValidateWitnesses([Witness], [changed]));
    }

    [Fact]
    public void MissingDuplicateAndExtraWitnessesReject()
    {
        var stored = Stored(Witness);
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.ValidateWitnesses([Witness], []));
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.ValidateWitnesses([Witness], [stored, stored]));
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.ValidateWitnesses([Witness, Witness], [stored, stored]));
    }

    private static ChessRecordingMeasurement.ExpectedGame Expected()
    {
        var record = new ChessGameRecord("", ["e4", "e5"], GameOutcome.Draw, Id(10), Id(11), Id(12))
            { PositionIds = [Id(20), Id(21), Id(22)], MoveIds = [Id(30), Id(31)] };
        return new(record, Id(40), Id(41));
    }
    private static ChessWitnessedGame Hydrated(ChessRecordingMeasurement.ExpectedGame expected) => new(
        expected.Record.LineId, expected.Record.PlayingId, expected.Record.Moves, expected.Record.Result,
        expected.WhitePlayer, expected.BlackPlayer, null, null, null, null)
        { MoveIds = expected.Record.MoveIds, StartPositionId = expected.Record.PositionIds[0] };

    [Fact]
    public void ExactHydratedGameAcceptsStoredIdentities()
    {
        var expected = Expected();
        ChessRecordingMeasurement.ValidateGames([expected], [Hydrated(expected)]);
    }

    [Theory]
    [InlineData("playing")]
    [InlineData("line")]
    [InlineData("start")]
    [InlineData("start-fen")]
    [InlineData("moves-order")]
    [InlineData("moves-missing")]
    [InlineData("result")]
    [InlineData("white")]
    [InlineData("black")]
    public void AlteredNativeGameReadbackRejects(string field)
    {
        var expected = Expected();
        var game = Hydrated(expected);
        var changed = field switch
        {
            "playing" => game with { PlayingId = Id(99) },
            "line" => game with { LineId = Id(99) },
            "start" => game with { StartPositionId = Id(99) },
            "start-fen" => game with { StartFen = "changed" },
            "moves-order" => game with { MoveIds = game.MoveIds.Reverse().ToArray() },
            "moves-missing" => game with { MoveIds = game.MoveIds.Take(1).ToArray() },
            "result" => game with { Result = GameOutcome.WonBy(0) },
            "white" => game with { WhitePlayer = game.BlackPlayer },
            _ => game with { BlackPlayer = null },
        };
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.ValidateGames([expected], [changed]));
    }

    [Fact]
    public void MissingAndDuplicatedGameReadbackRejects()
    {
        var expected = Expected();
        var game = Hydrated(expected);
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.ValidateGames([expected], []));
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.ValidateGames([expected, expected], [game, game]));
    }

    [Fact]
    public void EmptyPlayingReadbackRetainsTheStartAndDoesNotInventMoves()
    {
        var game = Expected();
        game = game with { Record = game.Record with
            { LineId = ChessCompose.LineId(game.Record.PositionIds[0], []),
              PositionIds = [game.Record.PositionIds[0]], MoveIds = [], Moves = [] } };
        var readback = Hydrated(game);
        ChessRecordingMeasurement.ValidateGames([game], [readback]);
        ChessRecordingMeasurement.ValidateCarriers([game.Record], []);
        Assert.Empty(readback.MoveIds);
        Assert.Equal(readback.LineId, readback.StartPositionId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void LineIdentityMatchesNativeContentTrajectoryIncludingSingleton(int moveCount)
    {
        var start = Id(20);
        var moves = Enumerable.Range(0, moveCount).Select(i => Id((ulong)i + 30)).ToArray();
        var trajectory = Trajectory.Build(new[] { start }.Concat(moves).ToArray());
        var native = Trajectory.ContentIdentity(trajectory, out var count);
        Assert.Equal(moveCount + 1, count);
        Assert.Equal(native, ChessCompose.LineId(start, moves));
    }

    private static NpgsqlSubstrateReads.ContentCarrierVertex[] Carrier(ChessGameRecord game)
        => new[] { game.PositionIds[0] }.Concat(game.MoveIds).Select((id, index) =>
            new NpgsqlSubstrateReads.ContentCarrierVertex(game.LineId.ToBytes(), game.MoveIds.Length + 1,
                index + 1, id.ToBytes(), 1, 0)).ToArray();

    [Fact]
    public void ExactCanonicalCarrierAcceptsSharedLineOnlyOnce()
    {
        var game = Expected().Record;
        ChessRecordingMeasurement.ValidateCarriers([game, game with { PlayingId = Id(90) }], Carrier(game));
    }

    [Theory]
    [InlineData("ordinal")]
    [InlineData("run")]
    [InlineData("flags")]
    [InlineData("count")]
    [InlineData("child")]
    [InlineData("parent")]
    public void SameMoveIdsWithAlteredCanonicalCarrierMetadataReject(string field)
    {
        var game = Expected().Record;
        var vertices = Carrier(game);
        vertices[1] = field switch
        {
            "ordinal" => vertices[1] with { Ordinal = 7 },
            "run" => vertices[1] with { RunLength = 2 },
            "flags" => vertices[1] with { Flags = 4 },
            "count" => vertices[1] with { ConstituentCount = 10 },
            "child" => vertices[1] with { ChildId = Id(98).ToBytes() },
            _ => vertices[1] with { ParentId = Id(99).ToBytes() },
        };
        Assert.Throws<InvalidDataException>(() => ChessRecordingMeasurement.ValidateCarriers([game], vertices));
    }

    [Fact]
    public async Task NormalRecordingStillAcceptsZeroMoveResignationAsTestimony()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chess-recording-match-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "fixture-executable");
            await File.WriteAllTextAsync(executable, "identity fixture, never executed");
            var receipt = new CutechessExperimentReceipt("test-id", new CutechessOptions { Rounds = 1 },
                new Dictionary<string, string>());
            await receipt.ObserveAsync(new ChessLabCommandEvent(executable, []), default);
            await receipt.ObserveAsync(new ChessLabGameEvent(1, "White", "Black", "1-0 (Black resigns)"), default);
            receipt.Complete(ChessLabJobState.Completed, null);
            Assert.True(await receipt.VerifyArtifactsAsync(default));
            var measurement = new ChessRecordingMeasurement("test-id", 1);
            measurement.ValidateMatch(receipt);
            var game = new ChessGameRecord("[Result \"1-0\"]", [], GameOutcome.WonBy(0), Id(20), Id(11), Id(12))
                { PositionIds = [Id(20)], MoveIds = [], WhiteName = "White", BlackName = "Black" };
            measurement.ObserveParsed(game);
            Assert.Equal(1, measurement.ParsedGames);
            Assert.False(ChessRecordingMeasurement.IsNormalResult(receipt.Games[0].Result));
            Assert.Throws<InvalidDataException>(() => measurement.ObserveParsed(game));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("1-0 (White mates)", true)]
    [InlineData("0-1 (Black mates)", true)]
    [InlineData("1/2-1/2 (Draw by stalemate)", true)]
    [InlineData("1/2-1/2 (Draw by insufficient mating material)", true)]
    [InlineData("1/2-1/2 (Draw by fifty moves rule)", true)]
    [InlineData("1/2-1/2 (Draw by 3-fold repetition)", true)]
    [InlineData("1-0 (Black loses on time)", false)]
    [InlineData("1-0 (Black disconnects)", false)]
    [InlineData("1-0 (Black resigns)", false)]
    [InlineData("1/2-1/2 (Draw by adjudication)", false)]
    [InlineData("1/2-1/2 (Draw by agreement)", false)]
    [InlineData("1-0 (Black makes an illegal move)", false)]
    [InlineData("1-0", false)]
    [InlineData("*", false)]
    public void FullGameCoverageSeparatesBoardRulesFromOtherRecordedOutcomes(string result, bool normal)
        => Assert.Equal(normal, ChessRecordingMeasurement.IsNormalResult(result));

    [Fact]
    public void FreshReceiptCannotClaimSuccessfulReadbackFromCountersAlone()
    {
        var measurement = new ChessRecordingMeasurement("test-id", 2);
        measurement.ObserveCommit(new ApplyResult(10, 8, 11, 9, 12, 12, 3, TimeSpan.FromSeconds(1), false, 2, 2)
            { PostgresCommit = new("on", true, true, true) });
        Assert.Equal(8, measurement.Writer.EntitiesInserted);
        Assert.Equal(9, measurement.Writer.PhysicalitiesInserted);
        Assert.Equal(12, measurement.Writer.AttestationsInserted);
        Assert.False(measurement.Verification.ExactGameBodies);
        Assert.Equal(0, measurement.ReadbackGames);
        Assert.Throws<InvalidDataException>(() => measurement.Complete("completed"));
        measurement.Complete("failed", "independent readback missing");
        Assert.False(measurement.Verification.CompletedGames);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("asynchronous")]
    [InlineData("fsync-disabled")]
    [InlineData("full-page-writes-disabled")]
    [InlineData("not-acknowledged")]
    [InlineData("journal-replay")]
    [InlineData("remote-write-only")]
    public void RecordingRejectsUnestablishedLocalWalAcknowledgement(string defect)
    {
        var receipt = new PostgresCommitReceipt("on", true, true, true);
        var result = new ApplyResult(1, 1, 0, 0, 1, 1, 1, TimeSpan.FromMilliseconds(1), false)
        {
            PostgresCommit = defect switch
            {
                "missing" => null,
                "asynchronous" => receipt with { SynchronousCommit = "off" },
                "fsync-disabled" => receipt with { Fsync = false },
                "full-page-writes-disabled" => receipt with { FullPageWrites = false },
                "not-acknowledged" => receipt with { WriteCommitAcknowledged = false },
                "remote-write-only" => receipt with { SynchronousCommit = "remote_write" },
                _ => receipt,
            },
            JournalReplayHit = defect == "journal-replay",
        };
        var measurement = new ChessRecordingMeasurement("test-id", 1);
        Assert.Throws<InvalidDataException>(() => measurement.ObserveCommit(result));
        Assert.Null(measurement.Durability);
        Assert.Equal(0, measurement.Writer.ApplyCalls);
    }

    [Fact]
    public async Task SeparateTransportReceiptBindsFinalArtifactWithoutRewritingCanonicalEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chess-recording-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var pgn = Path.Combine(directory, "games.pgn");
            var artifact = Path.Combine(directory, "experiment.json");
            var receipt = ChessExperimentEvidenceTests.Receipt;
            await File.WriteAllTextAsync(pgn, "original PGN bytes");
            await File.WriteAllTextAsync(artifact, receipt);
            var measurement = new ChessRecordingMeasurement("test-id", 2);
            await measurement.IdentifyPgnAsync(pgn, receipt, default);
            string canonicalHash = measurement.ExperimentReceiptSha256!;
            var final = receipt.Replace("\"ingested\": null", "\"ingested\": true");
            await File.WriteAllTextAsync(artifact, final);
            await measurement.IdentifyFinalExperimentAsync(artifact);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(final))), measurement.ExperimentArtifactSha256);
            Assert.Equal(canonicalHash, measurement.ExperimentReceiptSha256);
            Assert.Equal(ChessExperimentEvidence.Parse(receipt).ReceiptJson, ChessExperimentEvidence.Parse(final).ReceiptJson);
            await measurement.VerifyPgnUnchangedAsync(pgn, default);
            await File.AppendAllTextAsync(pgn, "mutation");
            await Assert.ThrowsAsync<InvalidDataException>(() => measurement.VerifyPgnUnchangedAsync(pgn, default));
            measurement.Complete("failed", "changed PGN");
            var recordingPath = Path.Combine(directory, "recording.json");
            await measurement.WriteAsync(recordingPath);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(recordingPath));
            Assert.Equal("laplace.chess-recording/v2", json.RootElement.GetProperty("schema").GetString());
            Assert.False(json.RootElement.GetProperty("verification").GetProperty("exactGameBodies").GetBoolean());
            Assert.Equal(final, await File.ReadAllTextAsync(artifact));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
