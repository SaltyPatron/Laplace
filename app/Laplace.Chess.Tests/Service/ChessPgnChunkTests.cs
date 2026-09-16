using Laplace.Engine.Core;
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
        [chunk.Record.Build(), chunk.Analyze.Build(), chunk.Repair.Build()];

    private static void Dispose(IEnumerable<SubstrateChange> changes)
    {
        foreach (var change in changes)
            foreach (var stage in change.IntentStages) stage.Dispose();
    }

    // Different grouping changes operational intent/source-unit IDs; these controls
    // compare canonical objects and raw source payload/multiplicity, not generated
    // source-unit evidence IDs across arbitrary fresh schedules.
    // Exact form comparison excludes only observation wall-clock time, which is sampled
    // anew by each actual composition. Identity/coordinates/carrier bits/source stay exact.
    private static string Form(PhysicalityRow row) => string.Join("|",
        row.Id, row.EntityId, row.SourceId, row.Type,
        BitConverter.DoubleToInt64Bits(row.CoordX), BitConverter.DoubleToInt64Bits(row.CoordY),
        BitConverter.DoubleToInt64Bits(row.CoordZ), BitConverter.DoubleToInt64Bits(row.CoordM),
        row.HilbertIndex, row.NConstituents, row.AlignmentResidual, row.SourceDim,
        row.TrajectoryXyzm is null ? "null" : string.Join(",", row.TrajectoryXyzm.Select(BitConverter.DoubleToInt64Bits)));

    private static string[] Observations(IEnumerable<SubstrateChange> changes) =>
        changes.SelectMany(change => change.PhysicalityObservations)
            .Select(Form).Order(StringComparer.Ordinal).ToArray();

    private static (Hash128 Id, long Count, long Score)[] Attestations(IEnumerable<SubstrateChange> changes) =>
        changes.SelectMany(change => change.Attestations).GroupBy(row => row.Id)
            .Select(group => (group.Key, group.Sum(row => row.ObservationCount),
                group.Sum(row => row.SumScoreFp1e9 ?? checked(row.ScoreFp1e9 * row.ObservationCount))))
            .OrderBy(row => row.Key.ToString(), StringComparer.Ordinal).ToArray();

    private static string[] EvidenceFacts(IEnumerable<SubstrateChange> changes) =>
        changes.SelectMany(change => change.Attestations)
            .Select(row => string.Join("|", row.Id, row.SubjectId, row.TypeId, row.ObjectId,
                row.SourceId, row.ContextId, row.FoldReplayable, row.OpponentRdFp1e9, row.HighwayMask))
            .Distinct().Order(StringComparer.Ordinal).ToArray();

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
            Assert.Equal(Observations(expected), Observations(split));
            Assert.Equal(Attestations(expected), Attestations(split));
            Assert.Equal(EvidenceFacts(expected), EvidenceFacts(split));
            Assert.Equal(expected.SelectMany(c => c.IntentStages).Sum(stage => stage.PhysicalityCount),
                split.SelectMany(c => c.IntentStages).Sum(stage => stage.PhysicalityCount));
            long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            Assert.All(split.SelectMany(c => c.PhysicalityObservations)
                .Where(row => row.ObservedAtUnixUs != 0),
                row => Assert.InRange(row.ObservedAtUnixUs, before, after));
        }
        finally { Dispose(expected); Dispose(split); }
    }

    [Fact]
    public void ActualStagedBytesCloseAfterTheCrossingGameWithoutANewGameCountCap()
    {
        var games = Games();
        var novel = games.Select(game => game.PlayingId).ToHashSet();
        int firstOffset = 0;
        using var first = ChessPgnChunk.ComposeNext(games, novel, ref firstOffset, 1);
        long firstBytes = first.StagedBytes;
        Dispose(Build(first));
        int offset = 0;
        using var combined = ChessPgnChunk.ComposeNext(games, novel, ref offset, checked(firstBytes + 1));
        try
        {
            Assert.Equal(2, combined.Games.Count);
            Assert.True(combined.StagedBytesBeforeLastGame < firstBytes + 1);
            Assert.True(combined.StagedBytes >= firstBytes + 1);
            Assert.Equal(2, offset);
        }
        finally { Dispose(Build(combined)); }
    }

    [Fact]
    public void DiscardedComposedChunkDeterministicallyReleasesUnbuiltNativeSourceStages()
    {
        var games = Games();
        var novel = games.Select(game => game.PlayingId).ToHashSet();
        int offset = 0;
        var chunk = ChessPgnChunk.ComposeNext(games, novel, ref offset, 1);
        var recordStage = chunk.Record.ContentStage;
        var analyzeStage = chunk.Analyze.ContentStage;
        Assert.True(recordStage.TotalTupleBytes > 0);
        Assert.True(analyzeStage.TotalTupleBytes > 0);
        chunk.Dispose();
        chunk.Dispose();
        Assert.True(recordStage.IsClosed);
        Assert.True(analyzeStage.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => chunk.Record.Build());
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
                Assert.Empty(repeatedChanges[1].PhysicalityObservations);
                Assert.Contains(repeatedChanges[2].Entities, row => row.Id == game.PlayingId);
                Assert.NotEmpty(repeatedChanges[2].PhysicalityObservations);
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
            Assert.NotEmpty(changes[2].PhysicalityObservations);
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
