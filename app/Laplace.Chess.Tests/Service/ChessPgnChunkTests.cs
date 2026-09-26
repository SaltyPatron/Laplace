using System.Buffers.Binary;
using System.Text;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessPgnChunkTests
{
    private static ChessGameRecord[] Games() => [Parse(1), Parse(2)];
    private static ChessGameRecord Parse(int number) => Assert.IsType<ChessGameRecord>(
        ChessPgnDecomposer.TryParseGame(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", $"position-playing-game-{number}.pgn")), requireCompleteSource: true));

    private static SubstrateChange[] Build(ChessPgnChunk chunk) =>
        [chunk.Record.Build(), chunk.Analyze.Build(), chunk.Repair.Build(), chunk.RepairAnalyze.Build()];

    private static void Dispose(IEnumerable<SubstrateChange> changes)
    {
        foreach (var change in changes)
            foreach (var stage in change.IntentStages) stage.Dispose();
    }

    // Different grouping changes operational intent/source-unit IDs; these controls
    // compare canonical objects and raw source payload/multiplicity, not generated
    // source-unit evidence IDs across arbitrary fresh schedules.
    // Exact form comparison excludes wall-clock time, which is sampled anew by each
    // actual composition, and the staging source, which is the first builder to stage
    // the row. Identity/coordinates/carrier bits stay exact.
    private static string Form(PhysicalityRow row) => string.Join("|",
        row.Id, row.EntityId, row.Type,
        BitConverter.DoubleToInt64Bits(row.CoordX), BitConverter.DoubleToInt64Bits(row.CoordY),
        BitConverter.DoubleToInt64Bits(row.CoordZ), BitConverter.DoubleToInt64Bits(row.CoordM),
        row.HilbertIndex, row.NConstituents, row.AlignmentResidual, row.SourceDim,
        row.TrajectoryXyzm is null ? "null" : string.Join(",", row.TrajectoryXyzm.Select(BitConverter.DoubleToInt64Bits)));

    private static string[] Physicalities(IEnumerable<SubstrateChange> changes) =>
        changes.SelectMany(change => change.Physicalities)
            .Select(Form).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static (Hash128 Id, long Count, long Score)[] Attestations(IEnumerable<SubstrateChange> changes) =>
        changes.SelectMany(change => change.Attestations).GroupBy(row => row.Id)
            .Select(group => (group.Key, group.Sum(row => row.ObservationCount),
                group.Sum(row => row.SumScoreFp1e9 ?? checked(row.ScoreFp1e9 * row.ObservationCount))))
            .OrderBy(row => row.Key.ToString(), StringComparer.Ordinal).ToArray();

    private static string[] EvidenceFacts(IEnumerable<SubstrateChange> changes) =>
        changes.SelectMany(change => change.Attestations)
            .Select(row => string.Join("|", row.Id, row.SubjectId, row.TypeId, row.ObjectId,
                row.SourceId, row.ContextId, row.FoldReplayable, row.OpponentRdFp1e9, row.QualifierMask))
            .Distinct().Order(StringComparer.Ordinal).ToArray();


    // A native stage stages each physicality at most once; repeated occurrences live in
    // their parents' trajectories. Separate chunks may each stage the same row, so a
    // split is compared with the whole by the set of staged physicality ids.
    private static Hash128[] StagedPhysicalityIds(IEnumerable<SubstrateChange> changes)
    {
        var all = new HashSet<Hash128>();
        foreach (var stage in changes.SelectMany(change => change.IntentStages))
        {
            var rows = stage.EmitCopyBinary(IntentStageTable.Physicalities);
            var inStage = new HashSet<Hash128>();
            int at = 19; // COPY binary signature, flags and header-extension length
            while (true)
            {
                short fields = BinaryPrimitives.ReadInt16BigEndian(rows.AsSpan(at));
                at += 2;
                if (fields == -1) break;
                for (int field = 0; field < fields; field++)
                {
                    int length = BinaryPrimitives.ReadInt32BigEndian(rows.AsSpan(at));
                    at += 4;
                    if (field == 0)
                    {
                        Assert.Equal(16, length);
                        Assert.True(inStage.Add(Hash128.FromBytes(rows.AsSpan(at, 16))));
                    }
                    if (length > 0) at += length;
                }
            }
            Assert.Equal(rows.Length, at);
            Assert.Equal(stage.PhysicalityCount, inStage.Count);
            all.UnionWith(inStage);
        }
        return all.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
    }

    // Deterministic legal games that share no line with the recorded fixtures, so each
    // one grows the composition window by its own content.
    private static ChessGameRecord DistinctGame(int seed)
    {
        var board = Board.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var pgn = new StringBuilder(
            $"[Event \"T\"]\n[White \"W{seed}\"]\n[Black \"B{seed}\"]\n[Result \"1/2-1/2\"]\n\n");
        for (int ply = 0; ply < 154; ply++)
        {
            var legal = MoveGen.Legal(board);
            if (legal.Count == 0) break;
            var move = legal[(ply * 7 + seed) % legal.Count];
            if (board.WhiteToMove) pgn.Append(board.FullmoveNumber).Append(". ");
            pgn.Append(San.ToSan(board, move)).Append(' ');
            MoveApply.Make(board, move);
        }
        pgn.Append("1/2-1/2\n");
        return Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn.ToString()));
    }

    private static void AssertRepairParity(SubstrateChange expected, SubstrateChange repaired)
    {
        Assert.Equal(expected.Metadata.SourceId, repaired.Metadata.SourceId);
        Assert.Equal(expected.Metadata.SourceContentUnitName, repaired.Metadata.SourceContentUnitName);
        Assert.Equal(expected.Metadata.IntentId, repaired.Metadata.IntentId);
        Assert.Equal(expected.Entities.Select(row => row.Id).OrderBy(id => id.ToString()),
            repaired.Entities.Select(row => row.Id).OrderBy(id => id.ToString()));
        Assert.Equal(Physicalities([expected]), Physicalities([repaired]));
        Assert.Equal(expected.Physicalities.Select(Form).Order(StringComparer.Ordinal),
            repaired.Physicalities.Select(Form).Order(StringComparer.Ordinal));
        // The same game/window must produce the complete canonical evidence body.
        // Only its new wall-clock observation time may change during recomposition.
        Assert.Equal<AttestationRow>(
            expected.Attestations.OrderBy(row => row.Id.ToString())
                .Select(row => row with { LastObservedAtUnixUs = 0 }),
            repaired.Attestations.OrderBy(row => row.Id.ToString())
                .Select(row => row with { LastObservedAtUnixUs = 0 }));
        Assert.Equal(expected.PhysicalitySourcePriors.OrderBy(pair => pair.Key.ToString()),
            repaired.PhysicalitySourcePriors.OrderBy(pair => pair.Key.ToString()));
        Assert.Equal(expected.IntentStages.Sum(stage => stage.PhysicalityCount),
            repaired.IntentStages.Sum(stage => stage.PhysicalityCount));
        foreach (var row in repaired.Physicalities)
            Assert.Equal(expected.RequireSourcePrior(row.SourceId), repaired.RequireSourcePrior(row.SourceId));
        foreach (var stage in repaired.IntentStages)
        {
            int covered = 0;
            foreach (var range in stage.PhysicalitySourceRanges)
            {
                Assert.Equal(covered, range.FirstRow);
                Assert.True(range.RowCount > 0);
                Assert.Equal(expected.RequireSourcePrior(range.SourceId), repaired.RequireSourcePrior(range.SourceId));
                covered = checked(covered + range.RowCount);
            }
            Assert.Equal(stage.PhysicalityCount, covered);
        }
    }

    [Fact]
    public void RepairRecomposesEveryOrdinaryLaneWithTheOriginalEvidenceBodyAndSourceIdentity()
    {
        var games = Games();
        var novel = games.Select(game => game.PlayingId).ToHashSet();
        var measurement = new ChessRecordingMeasurement(null, games.Length);
        int novelOffset = 0, repairOffset = 0;
        using var fresh = ChessPgnChunk.ComposeNext(games, novel, ref novelOffset, long.MaxValue);
        using var repair = ChessPgnChunk.ComposeNext(games, new HashSet<Hash128>(), ref repairOffset,
            long.MaxValue, measurement: measurement);
        Assert.Equal(games.Length, novelOffset);
        Assert.Equal(novelOffset, repairOffset);
        Assert.Equal(fresh.StagedBytes, repair.StagedBytes);
        Assert.Equal(fresh.ModeledSourceAdmissionBytes, repair.ModeledSourceAdmissionBytes);
        Assert.Equal(0, repair.NovelGames);
        Assert.True(novel.SetEquals(repair.RepairPlayings));
        Assert.True(fresh.ObservedPositions.SetEquals(repair.ObservedPositions));
        Assert.True(fresh.ObservedMoves.SetEquals(repair.ObservedMoves));
        Assert.Equal((long)games.Length, measurement.Work.RepairGamesComposed);
        Assert.Equal(0L, measurement.Work.NovelGamesComposed);
        Assert.Equal(0L, measurement.Work.NovelPliesComposed);
        Assert.Equal(0L, measurement.Work.PositionOccurrencesComposed);
        var expected = Build(fresh);
        var repaired = Build(repair);
        try
        {
            Assert.Empty(repaired[0].Entities);
            Assert.Empty(repaired[1].Attestations);
            AssertRepairParity(expected[0], repaired[2]);
            AssertRepairParity(expected[1], repaired[3]);
            Assert.Contains(repaired[2].Attestations,
                row => row.SourceId == ChessVocabulary.PgnSourceId
                    && row.TypeId == ChessVocabulary.PlaysLineType);
            foreach (var source in new[]
            {
                ChessAnalyze.SourceId, ChessTransitions.SourceId, ChessPositionOutcomes.SourceId
            })
                Assert.Contains(repaired[3].Attestations, row => row.SourceId == source);
            Assert.Contains(repaired[3].Physicalities,
                row => row.SourceId == ChessVocabulary.TrajectorySourceId);
        }
        finally { Dispose(expected); Dispose(repaired); }
    }

    [Fact]
    public void RepairIncludesTheAvailableNativeSyzygyLaneAndMeasuresItsActualCalls()
    {
        // The existing test host selects the repository's real three-man table set.
        Assert.Equal(3, ChessTablebaseRuntime.Largest);
        const string pgn =
            "[Event \"T\"]\n[White \"A\"]\n[Black \"B\"]\n[Date \"2024.01.01\"]\n"
            + "[Result \"1-0\"]\n[SetUp \"1\"]\n[FEN \"4k3/8/8/8/8/8/8/3QK3 w - - 0 1\"]\n\n"
            + "1. Qd5 1-0\n";
        var game = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn));
        ChessGameRecord[] games = [game];
        var measurement = new ChessRecordingMeasurement(null, games.Length);
        int novelOffset = 0, repairOffset = 0;
        using var fresh = ChessPgnChunk.ComposeNext(games, new HashSet<Hash128> { game.PlayingId },
            ref novelOffset, long.MaxValue);
        using var repair = ChessPgnChunk.ComposeNext(games, new HashSet<Hash128>(),
            ref repairOffset, long.MaxValue, measurement: measurement);
        var expected = Build(fresh);
        var repaired = Build(repair);
        try
        {
            AssertRepairParity(expected[0], repaired[2]);
            AssertRepairParity(expected[1], repaired[3]);
            Assert.Contains(repaired[3].Attestations, row => row.SourceId == ChessSyzygy.SourceId);
            Assert.Contains(repaired[3].Physicalities, row => row.SourceId == ChessSyzygy.SourceId);
            Assert.True(measurement.Work.SyzygyAvailable);
            Assert.Equal(1L, measurement.Work.SyzygyGameCalls);
            Assert.Equal(1L, measurement.Work.SyzygyGameCallsCompleted);
            Assert.Equal(1L, measurement.Work.RepairGamesComposed);
            Assert.Equal(0L, measurement.Work.NovelGamesComposed);
        }
        finally { Dispose(expected); Dispose(repaired); }
    }

    [Fact]
    public void ByteBoundaryKeepsFullGamesAndEveryObservationAcrossCanonicalComposition()
    {
        var games = Games();
        var novel = games.Select(game => game.PlayingId).ToHashSet();
        int wholeOffset = 0;
        using var whole = ChessPgnChunk.ComposeNext(games, novel, ref wholeOffset, long.MaxValue);
        var expected = Build(whole);
        var split = new List<SubstrateChange>();
        var admitted = new List<ChessGameRecord>();
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        try
        {
            int offset = 0;
            while (offset < games.Length)
            {
                using var chunk = ChessPgnChunk.ComposeNext(games, novel, ref offset, 1);
                Assert.Single(chunk.Games);
                Assert.True(chunk.StagedBytes >= 1);
                Assert.Equal(0L, chunk.StagedBytesBeforeLastGame);
                admitted.AddRange(chunk.Games);
                split.AddRange(Build(chunk));
            }
            Assert.Equal(games.Length, wholeOffset);
            Assert.Equal<ChessGameRecord>(games, admitted);
            Assert.Equal(games.Sum(game => game.MoveIds.Length), admitted.Sum(game => game.MoveIds.Length));
            Assert.Equal(154, admitted[0].MoveIds.Length);
            Assert.Equal(expected.SelectMany(c => c.Entities).Select(row => row.Id).Distinct().OrderBy(id => id.ToString()),
                split.SelectMany(c => c.Entities).Select(row => row.Id).Distinct().OrderBy(id => id.ToString()));
            Assert.Equal(Physicalities(expected), Physicalities(split));
            Assert.Equal(Attestations(expected), Attestations(split));
            Assert.Equal(EvidenceFacts(expected), EvidenceFacts(split));
            Assert.Equal(StagedPhysicalityIds(expected), StagedPhysicalityIds(split));
            long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            Assert.All(split.SelectMany(c => c.Physicalities)
                .Where(row => row.ObservedAtUnixUs != 0),
                row => Assert.InRange(row.ObservedAtUnixUs, before, after));
        }
        finally { Dispose(expected); Dispose(split); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedSerialWindowUsesItsExistingShareAndRetainsMixedRepairReplay(bool budgetIsSmaller)
    {
        var games = Games();
        var novel = new HashSet<Hash128> { games[0].PlayingId };
        long firstBytes;
        int probeOffset = 0;
        using (var probe = ChessPgnChunk.ComposeNext(games, novel, ref probeOffset, 1))
        {
            firstBytes = Math.Max(probe.StagedBytes, probe.ModeledSourceAdmissionBytes);
            Assert.Single(probe.Games);
        }

        // Derive the finite boundary from actual native-backed first-game composition.
        // Either existing machine limit may be tighter; neither is multiplied.
        long envelope = checked(firstBytes + 1);
        long larger = checked(envelope * 2);
        long budget = budgetIsSmaller ? envelope : larger;
        long flush = budgetIsSmaller ? larger : envelope;
        const int configuredWorkers = 6;
        long ownedGrant = ChessPgnIngestor.ResolveChunkStagedBytes(
            true, configuredWorkers, budget, flush);
        long attachedGrant = ChessPgnIngestor.ResolveChunkStagedBytes(
            false, configuredWorkers, budget, flush);
        Assert.Equal(envelope, ownedGrant);
        Assert.Equal(envelope / configuredWorkers, attachedGrant);

        int expectedOffset = 0;
        using var expectedChunk = ChessPgnChunk.ComposeNext(games, novel, ref expectedOffset, long.MaxValue);
        var expected = Build(expectedChunk);
        var owned = new List<SubstrateChange>();
        var attached = new List<SubstrateChange>();
        var replayed = new List<SubstrateChange>();
        try
        {
            int offset = 0;
            using (var chunk = ChessPgnChunk.ComposeNext(games, novel, ref offset, ownedGrant))
            {
                Assert.Equal<ChessGameRecord>(games, chunk.Games);
                Assert.Equal(1, chunk.NovelGames);
                Assert.Equal(games[1].PlayingId, Assert.Single(chunk.RepairPlayings));
                Assert.True(chunk.StagedBytesBeforeLastGame < ownedGrant);
                Assert.True(chunk.ModeledSourceAdmissionBytesBeforeLastGame < ownedGrant);
                // Every simultaneously held builder contributes to this same window.
                Assert.All(new[] { chunk.Record, chunk.Analyze, chunk.Repair, chunk.RepairAnalyze },
                    builder => Assert.True(builder.StagedBytesEstimate > 0));
                owned.AddRange(Build(chunk));
            }
            Assert.Equal(games.Length, offset);

            var attachedGames = new List<ChessGameRecord>();
            offset = 0;
            while (offset < games.Length)
            {
                using var chunk = ChessPgnChunk.ComposeNext(games, novel, ref offset, attachedGrant);
                Assert.Single(chunk.Games);
                attachedGames.AddRange(chunk.Games);
                attached.AddRange(Build(chunk));
            }
            Assert.Equal<ChessGameRecord>(games, attachedGames);
            Assert.Equal(games.Sum(game => game.MoveIds.Length),
                attachedGames.Sum(game => game.MoveIds.Length));

            // A sealed owned window is replayed intact even in a smaller shared grant.
            offset = 0;
            using (var replay = ChessPgnChunk.ComposeNext(games, novel, ref offset,
                attachedGrant, exactGameCount: games.Length))
            {
                Assert.Equal<ChessGameRecord>(games, replay.Games);
                replayed.AddRange(Build(replay));
            }
            Assert.Equal(games.Length, offset);
            foreach (var actual in new[] { owned, attached, replayed })
            {
                Assert.Equal(Physicalities(expected), Physicalities(actual));
                Assert.Equal(Attestations(expected), Attestations(actual));
                Assert.Equal(EvidenceFacts(expected), EvidenceFacts(actual));
                Assert.Equal(expected.SelectMany(change => change.Entities)
                        .Select(row => row.Id).Distinct().OrderBy(id => id.ToString()),
                    actual.SelectMany(change => change.Entities)
                        .Select(row => row.Id).Distinct().OrderBy(id => id.ToString()));
                Assert.Equal(expected.SelectMany(change => change.IntentStages)
                        .Sum(stage => stage.PhysicalityCount),
                    actual.SelectMany(change => change.IntentStages)
                        .Sum(stage => stage.PhysicalityCount));
            }
        }
        finally
        {
            Dispose(expected);
            Dispose(owned);
            Dispose(attached);
            Dispose(replayed);
        }
    }

    [Fact]
    public void SharedByteEstimatesCloseAfterTheCrossingGameWithoutANewGameCountCap()
    {
        // The second fixture repeats the first game's line, so it adds almost no staged
        // content; the window only crosses its share on games with content of their own.
        ChessGameRecord[] games = [Parse(1), .. Enumerable.Range(1, 16).Select(DistinctGame)];
        var novel = games.Select(game => game.PlayingId).ToHashSet();
        Assert.Equal(games.Length, novel.Count);
        int firstOffset = 0;
        using var first = ChessPgnChunk.ComposeNext(games, novel, ref firstOffset, 1);
        long firstBytes = Math.Max(first.StagedBytes, first.ModeledSourceAdmissionBytes);
        Dispose(Build(first));
        int offset = 0;
        long budget = checked(firstBytes + 1);
        using var combined = ChessPgnChunk.ComposeNext(games, novel, ref offset, budget);
        try
        {
            Assert.True(combined.Games.Count > 1);
            Assert.Equal(combined.Games.Count, offset);
            Assert.True(offset < games.Length);
            Assert.True(combined.StagedBytesBeforeLastGame < budget);
            Assert.True(combined.ModeledSourceAdmissionBytesBeforeLastGame < budget);
            Assert.True(Math.Max(combined.StagedBytes, combined.ModeledSourceAdmissionBytes) >= budget);
        }
        finally { Dispose(Build(combined)); }
    }

    [Fact]
    public void SourceLocalAdmissionModelClosesWholeGamesBeforeSerializedBytesAndReplayKeepsTheSchedule()
    {
        var games = Games();
        var novel = games.Select(game => game.PlayingId).ToHashSet();
        int probeOffset = 0;
        using var probe = ChessPgnChunk.ComposeNext(games, novel, ref probeOffset, 1);
        long budget = checked(probe.StagedBytes + 1);
        Assert.True(probe.ModeledSourceAdmissionBytes >= budget);
        Dispose(Build(probe));

        int wholeOffset = 0;
        using var whole = ChessPgnChunk.ComposeNext(games, novel, ref wholeOffset, long.MaxValue);
        var expected = Build(whole);
        var split = new List<SubstrateChange>();
        var schedule = new List<int>();
        var observed = new List<ChessGameRecord>();
        try
        {
            int offset = 0;
            while (offset < games.Length)
            {
                using var chunk = ChessPgnChunk.ComposeNext(games, novel, ref offset, budget);
                if (schedule.Count == 0)
                {
                    Assert.Single(chunk.Games);
                    Assert.True(chunk.StagedBytes < budget);
                    Assert.True(chunk.ModeledSourceAdmissionBytes >= budget);
                    Assert.Equal(0L, chunk.ModeledSourceAdmissionBytesBeforeLastGame);
                }
                schedule.Add(chunk.Games.Count);
                observed.AddRange(chunk.Games);
                split.AddRange(Build(chunk));
            }
            Assert.Equal<ChessGameRecord>(games, observed);
            Assert.Equal(games.Sum(game => game.MoveIds.Length), observed.Sum(game => game.MoveIds.Length));
            Assert.Equal(Physicalities(expected), Physicalities(split));
            Assert.Equal(Attestations(expected), Attestations(split));
            Assert.Equal(EvidenceFacts(expected), EvidenceFacts(split));
            Assert.Equal(expected.SelectMany(c => c.Entities).Select(row => row.Id).Distinct().OrderBy(id => id.ToString()),
                split.SelectMany(c => c.Entities).Select(row => row.Id).Distinct().OrderBy(id => id.ToString()));
            Assert.Equal(StagedPhysicalityIds(expected), StagedPhysicalityIds(split));

            int replayOffset = 0;
            var replayed = new List<ChessGameRecord>();
            foreach (int count in schedule)
            {
                // A larger runtime byte share must not merge the sealed fresh chunks.
                using var replay = ChessPgnChunk.ComposeNext(games, novel, ref replayOffset,
                    long.MaxValue, exactGameCount: count);
                Assert.Equal(count, replay.Games.Count);
                replayed.AddRange(replay.Games);
                Dispose(Build(replay));
            }
            Assert.Equal<ChessGameRecord>(games, replayed);
            Assert.Equal(games.Length, replayOffset);
        }
        finally { Dispose(expected); Dispose(split); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscardedComposedChunkDeterministicallyReleasesUnbuiltNativeSourceStages(bool repair)
    {
        var games = Games();
        var novel = repair ? new HashSet<Hash128>() : games.Select(game => game.PlayingId).ToHashSet();
        int offset = 0;
        var chunk = ChessPgnChunk.ComposeNext(games, novel, ref offset, 1);
        var record = repair ? chunk.Repair : chunk.Record;
        var analyze = repair ? chunk.RepairAnalyze : chunk.Analyze;
        var recordStage = record.ContentStage;
        var analyzeStage = analyze.ContentStage;
        Assert.True(recordStage.TotalTupleBytes > 0);
        Assert.True(analyzeStage.TotalTupleBytes > 0);
        chunk.Dispose();
        chunk.Dispose();
        Assert.True(recordStage.IsClosed);
        Assert.True(analyzeStage.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => record.Build());
        Assert.Throws<ObjectDisposedException>(() => analyze.Build());
    }

    [Fact]
    public void RepeatedPlayingAfterAcknowledgedByteChunkUsesRepairButUnacknowledgedWorkRemainsNovel()
    {
        var game = Parse(1);
        ChessGameRecord[] games = [game, game];
        var remainingNovel = new HashSet<Hash128> { game.PlayingId };
        int offset = 0;
        using var first = ChessPgnChunk.ComposeNext(games, remainingNovel, ref offset, 1);
        Assert.Equal(1, first.NovelGames);
        Assert.Equal(1, offset);
        var firstChanges = Build(first);
        try
        {
            // Without successful apply/readback there is no committed-presence claim.
            int unacknowledgedOffset = offset;
            using var unacknowledged = ChessPgnChunk.ComposeNext(
                games, remainingNovel, ref unacknowledgedOffset, 1);
            var unacknowledgedChanges = Build(unacknowledged);
            try
            {
                Assert.Equal(1, unacknowledged.NovelGames);
                Assert.Empty(unacknowledged.RepairPlayings);
                Assert.NotEmpty(unacknowledgedChanges[1].Attestations);
                Assert.Contains(game.PlayingId, remainingNovel);
            }
            finally { Dispose(unacknowledgedChanges); }

            // This is the same transition the ingestor invokes only after its awaited
            // ordinary apply and exact readback return successfully; no database is
            // simulated by this composition/novelty-state control.
            first.ForgetCommittedNovelty(remainingNovel);
            using var repeated = ChessPgnChunk.ComposeNext(games, remainingNovel, ref offset, 1);
            var repeatedChanges = Build(repeated);
            try
            {
                Assert.Equal(2, offset);
                Assert.Equal(0, repeated.NovelGames);
                Assert.Equal(game.PlayingId, Assert.Single(repeated.RepairPlayings));
                Assert.Empty(repeatedChanges[0].Entities);
                Assert.Empty(repeatedChanges[1].Attestations);
                Assert.Empty(repeatedChanges[1].Physicalities);
                Assert.Contains(repeatedChanges[2].Entities, row => row.Id == game.PlayingId);
                Assert.NotEmpty(repeatedChanges[2].Physicalities);
                AssertRepairParity(firstChanges[0], repeatedChanges[2]);
                AssertRepairParity(firstChanges[1], repeatedChanges[3]);
                Assert.DoesNotContain(game.PlayingId, remainingNovel);
            }
            finally { Dispose(repeatedChanges); }
        }
        finally { Dispose(firstChanges); }
    }

    [Fact]
    public void ReplayScheduleKeepsMixedNovelAndRepairGamesTogetherDespiteDifferentMemoryShape()
    {
        var games = Games();
        var novel = new HashSet<Hash128> { games[0].PlayingId };
        int offset = 0;
        using var chunk = ChessPgnChunk.ComposeNext(games, novel, ref offset, 1, exactGameCount: 2);
        var changes = Build(chunk);
        try
        {
            Assert.Equal(2, offset);
            Assert.Equal<ChessGameRecord>(games, chunk.Games);
            Assert.Equal(1, chunk.NovelGames);
            Assert.Equal(games[1].PlayingId, Assert.Single(chunk.RepairPlayings));
            Assert.Contains(changes[0].Entities, row => row.Id == games[0].PlayingId);
            Assert.Contains(changes[2].Entities, row => row.Id == games[1].PlayingId);
            Assert.DoesNotContain(changes[0].Entities, row => row.Id == games[1].PlayingId);
            Assert.NotEmpty(changes[2].Physicalities);
            Assert.NotEmpty(changes[3].Attestations);
        }
        finally { Dispose(changes); }

        int refused = 0;
        Assert.Throws<InvalidDataException>(() => ChessPgnChunk.ComposeNext(
            games, novel, ref refused, 1, exactGameCount: 3));
        Assert.Equal(0, refused);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ChessPgnChunk.ComposeNext(
            games, novel, ref refused, 1, ct: cancelled.Token));
        Assert.Equal(0, refused);
    }
}
