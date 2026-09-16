using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessStartingSideOutcomeTests
{
    private const string AfterF3 =
        "rnbqkbnr/pppppppp/8/8/8/5P2/PPPPP1PP/RNBQKBNR b KQkq - 0 1";

    private static ChessGameRecord Game(bool blackStarts, bool repeatedMoves = false)
    {
        CodepointPerfcache.LoadDefault();
        string prefix = "[Event \"starting-side\"]\n[Site \"fixture\"]\n[Date \"2026.09.16\"]\n"
            + "[Round \"1\"]\n[White \"White\"]\n[Black \"Black\"]\n[Result \"0-1\"]\n"
            + "[Termination \"normal\"]\n";
        if (blackStarts) prefix += "[SetUp \"1\"]\n[FEN \"" + AfterF3 + "\"]\n";
        int plies = (blackStarts ? 3 : 4) + (repeatedMoves ? 8 : 0);
        prefix += "[PlyCount \"" + plies + "\"]\n\n";
        string moves = repeatedMoves
            ? "Nf6 2. a3 Ng8 3. a4 Nf6 4. b3 Ng8 5. g4 e5 6. a5 Qh4# 0-1\n"
            : "e5 2. g4 Qh4# 0-1\n";
        var game = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(
            prefix + (blackStarts ? "1... " : "1. f3 ") + moves,
            requireNormalCompletion: true, requireCompleteSource: true));
        Assert.Equal(plies, game.MoveIds.Length);
        Assert.Equal(!blackStarts, game.InitialWhiteToMove);
        Assert.True(game.NormalCompletionVerified);
        Assert.True(game.CompleteSourceVerified);
        return game;
    }

    private static ChessParsedReplay VerifyReplay(ChessGameRecord game)
    {
        var replay = ChessPgnDecomposer.MaterializeParsedReplay(game);
        Assert.True(replay.IsCompleteFor(game));
        var modality = new ChessModality();
        var state = game.StartFen is null ? modality.Initial() : modality.FromFen(game.StartFen);
        for (int ply = 0; ply < game.ResolvedMoves.Length; ply++)
        {
            Assert.Null(modality.Terminal(state));
            Assert.Contains(game.ResolvedMoves[ply], MoveGen.Legal(state.Board));
            Assert.Equal(game.PositionIds[ply], ChessCompose.PositionId(state.Board));
            Assert.Equal(state.Board.WhiteToMove, replay.Boards[ply].WhiteToMove);
            Assert.Equal(game.MoveIds[ply], ChessCompose.MoveId(
                state.Board.Squares[game.ResolvedMoves[ply].From], game.ResolvedMoves[ply]));
            state = modality.Apply(state, game.ResolvedMoves[ply]);
        }
        Assert.Equal(game.PositionIds[^1], ChessCompose.PositionId(state.Board));
        Assert.Empty(MoveGen.Legal(state.Board));
        Assert.True(MoveGen.InCheck(state.Board, state.Board.WhiteToMove));
        Assert.Equal(GameOutcome.WonBy(1), modality.Terminal(state));
        Assert.Equal(game.Result, modality.Terminal(state));
        Assert.Equal(game.LineId, ChessCompose.LineId(game.PositionIds[0], game.MoveIds));
        return replay;
    }

    private static ChessWitnessedGame Witnessed(ChessGameRecord game)
        => ChessAnalyze.WitnessedFromParsed(game) with
        {
            MoveIds = game.MoveIds,
            StartPositionId = game.PositionIds[0],
        };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CompleteParsedAndHydratedGamesRetainEveryMoveWithBoardRelativeScores(
        bool blackStarts, bool repeatedMoves)
    {
        var game = Game(blackStarts, repeatedMoves);
        var replay = VerifyReplay(game);
        var parsed = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/starting-side");
        ChessPgnDecomposer.RecordGame(game, parsed);
        ChessTransitions.DepositFromParsed(parsed, game);
        var hydrated = new SubstrateChangeBuilder(ChessMoveOutcomes.SourceId, "test/starting-side-hydrated");
        ChessMoveOutcomes.DeriveGame(hydrated, Witnessed(game));
        ChessTransitions.Deposit(hydrated, Witnessed(game));
        var changes = new[] { parsed.Build(), hydrated.Build() };
        try
        {
            AssertProjection(changes[0], game, replay, ChessVocabulary.PgnSourceId, 0.7);
            AssertProjection(changes[1], game, replay, ChessMoveOutcomes.SourceId, 0.9);
            Assert.Equal(Rows(changes[0], ChessTransitions.SourceId, ChessVocabulary.MoveType),
                Rows(changes[1], ChessTransitions.SourceId, ChessVocabulary.MoveType));
            foreach (var change in changes)
            {
                Assert.Contains(change.Entities, e => e.Id ==
                    Hash128.OfCanonical($"chess/transitions/{game.PlayingId}/1"));
                Assert.Contains(change.Entities, e => e.Id ==
                    Hash128.OfCanonical($"chess/move-outcomes/{game.LineId}/1"));
            }
            if (repeatedMoves)
                Assert.True(game.MoveIds.Distinct().Count() < game.MoveIds.Length);
        }
        finally { Dispose(changes); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveResolvedMoversAndBothResultInferenceOwnersAgreeWithTerminalBoard(bool blackStarts)
    {
        var game = Game(blackStarts);
        var replay = VerifyReplay(game);
        var modality = new ChessModality();
        var edges = new RecordedEdge[game.MoveIds.Length];
        var livePlies = new ChessLiveGameHost.RecordedPly[edges.Length];
        for (int ply = 0; ply < edges.Length; ply++)
        {
            string from = modality.StateKey(new ChessState(replay.Boards[ply]));
            string to = modality.StateKey(new ChessState(replay.Boards[ply + 1]));
            int mover = replay.Boards[ply].WhiteToMove ? 0 : 1;
            livePlies[ply] = ChessLiveGameHost.ResolveRecordedPly(
                from, to, game.ResolvedMoves[ply].ToUci(), null);
            Assert.Equal(mover, livePlies[ply].MoverSide);
            edges[ply] = new RecordedEdge(from, to, livePlies[ply].MoveToken, game.Result.ForMover(mover));
        }
        Assert.Equal(game.Result, ChessLiveGameHost.InferOutcome(edges, adjudicated: false));
        Assert.Equal(PlyOutcome.Loss, SubstrateTurnHost.WhiteOutcome(edges, adjudicated: false));
        Assert.Equal(GameOutcome.Draw, ChessLiveGameHost.InferOutcome(edges, adjudicated: true));
        Assert.Equal(PlyOutcome.Draw, SubstrateTurnHost.WhiteOutcome(edges, adjudicated: true));

        var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, "test/live-starting-side");
        ChessMoveOutcomes.AppendGame(b, game.LineId, game.MoveIds,
            ChessLiveGameHost.InferOutcome(edges, false), livePlies[0].MoverSide == 0,
            ChessVocabulary.SourceId, 0.7);
        ChessGraph.AppendTransitions(b, game.PositionIds, game.Result,
            livePlies[0].MoverSide == 0, SourceTrust.StructuredCorpus,
            ChessTransitions.SourceId, game.PlayingId);
        var change = b.Build();
        try { AssertProjection(change, game, replay, ChessVocabulary.SourceId, 0.7); }
        finally { Dispose(change); }
    }

    [Theory]
    [InlineData("record")]
    [InlineData("transitions")]
    public void MissingValidatedMoverIsRejectedBeforeAnyRowsAreStaged(string path)
    {
        var game = Game(true) with { InitialWhiteToMove = null };
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/missing-mover");
        Assert.Throws<InvalidDataException>(() =>
        {
            if (path == "record") ChessPgnDecomposer.RecordGame(game, b);
            else ChessTransitions.DepositFromParsed(b, game);
        });
        var change = b.Build();
        try
        {
            Assert.Empty(change.Entities);
            Assert.Empty(change.Attestations);
            Assert.Empty(change.Physicalities);
            Assert.Empty(change.PhysicalityObservations);
            Assert.Empty(change.IntentStages);
        }
        finally { Dispose(change); }
    }

    private static void AssertProjection(SubstrateChange actual, ChessGameRecord game,
        ChessParsedReplay replay, Hash128 moveSource, double moveWeight)
    {
        var correct = new SubstrateChangeBuilder(moveSource, "test/native-board-reference");
        var legacy = new SubstrateChangeBuilder(moveSource, "test/legacy-identity-reference");
        for (int ply = 0; ply < game.MoveIds.Length; ply++)
        {
            Add(correct, ply, game.Result.ForMover(replay.Boards[ply].WhiteToMove ? 0 : 1));
            Add(legacy, ply, game.Result.ForMover(ply & 1));
        }
        var expected = correct.Build();
        var old = legacy.Build();
        try
        {
            foreach (var (source, type) in new[]
            {
                (moveSource, ChessVocabulary.OutcomeType),
                (ChessTransitions.SourceId, ChessVocabulary.MoveType),
            })
            {
                var rows = Rows(actual, source, type);
                var reference = Rows(expected, source, type);
                var oldRows = Rows(old, source, type);
                Assert.Equal(reference, rows);
                Assert.Equal(oldRows.Select(a => a.Id), rows.Select(a => a.Id));
                Assert.Equal((long)game.MoveIds.Length, rows.Sum(a => a.ObservationCount));
                if (replay.Boards[0].WhiteToMove) Assert.Equal(oldRows, rows);
                else Assert.False(oldRows.Select(a => a.SumScoreFp1e9)
                    .SequenceEqual(rows.Select(a => a.SumScoreFp1e9)));
            }
        }
        finally { Dispose(expected, old); }

        void Add(SubstrateChangeBuilder target, int ply, PlyOutcome score)
        {
            target.AddAttestation(NativeAttestation.Aggregated(
                game.MoveIds[ply], ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject,
                moveSource, null, 1, ChessGraph.ScoreFp1e9(score), moveWeight));
            target.AddAttestation(NativeAttestation.Aggregated(
                game.PositionIds[ply], ChessVocabulary.MoveType, game.PositionIds[ply + 1],
                ChessTransitions.SourceId, game.PlayingId, 1, ChessGraph.ScoreFp1e9(score),
                SourceTrust.StructuredCorpus));
        }
    }

    private static AttestationRow[] Rows(SubstrateChange change, Hash128 source, Hash128 type)
        => change.Attestations.Where(a => a.SourceId == source && a.TypeId == type
                && (type != ChessVocabulary.OutcomeType || a.ContextId is null))
            .Select(a => a with { LastObservedAtUnixUs = 0 }).OrderBy(a => a.Id.ToString()).ToArray();

    private static void Dispose(params SubstrateChange[] changes)
    {
        foreach (var change in changes)
            foreach (var stage in change.IntentStages) stage.Dispose();
    }
}
