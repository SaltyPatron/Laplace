using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessPlayerContextOutcomeTests
{
    [Fact]
    public void DeriveGame_FoldsPlayerPhaseAndThinkContext_OncePerGameContext()
    {
        CodepointPerfcache.LoadDefault();
        var white = ChessVocabulary.PlayerId("Context White");
        var black = ChessVocabulary.PlayerId("Context Black");
        var playing = Hash128.OfCanonical("test/chess/player-context/playing");
        var game = new ChessWitnessedGame(
            LineId: Hash128.OfCanonical("test/chess/player-context/line"),
            PlayingId: playing,
            Moves: ["e4", "e5", "Nf3", "Nc6"],
            Result: GameOutcome.WonBy(0),
            WhitePlayer: white,
            BlackPlayer: black,
            StartFen: null,
            ClockTokens: null,
            EvalTokens: null,
            QualityTokens: null,
            SpentSeconds: [1d, 5d, 1d, 5d]);

        var builder = new SubstrateChangeBuilder(
            ChessPlayerContextOutcomes.SourceId, "test/player-context");
        ChessPlayerContextOutcomes.DeriveGame(builder, game);
        var change = builder.Build();

        var phase = Assert.IsType<Hash128>(ContentEmitter.RootId("phase:opening"));
        var rushed = Assert.IsType<Hash128>(ContentEmitter.RootId("rushed"));
        var deep = Assert.IsType<Hash128>(ContentEmitter.RootId("deep"));

        // Four opening plies do not become four votes for one game. White and Black each
        // contribute one phase observation, and their witnessed think styles remain separate.
        Assert.Single(change.Attestations, a =>
            a.SubjectId == white && a.TypeId == ChessVocabulary.OutcomeType && a.ObjectId == phase);
        Assert.Single(change.Attestations, a =>
            a.SubjectId == black && a.TypeId == ChessVocabulary.OutcomeType && a.ObjectId == phase);
        Assert.Single(change.Attestations, a =>
            a.SubjectId == white && a.TypeId == ChessVocabulary.OutcomeType && a.ObjectId == rushed);
        Assert.Single(change.Attestations, a =>
            a.SubjectId == black && a.TypeId == ChessVocabulary.OutcomeType && a.ObjectId == deep);

        Assert.All(change.Attestations.Where(a =>
                a.SubjectId == white || a.SubjectId == black),
            a => Assert.Equal(playing, a.ContextId));
        Assert.Contains(change.Entities,
            e => e.Id == ChessPlayerContextOutcomes.MarkerId(playing));
    }

    [Fact]
    public void PhaseClass_UsesBoardMaterial_NotMoveNumber()
    {
        Assert.Equal("phase:opening",
            ChessCanonical.PhaseClass(Board.FromFen(ChessModality.StartFen)));
        Assert.Equal("phase:endgame",
            ChessCanonical.PhaseClass(Board.FromFen("7k/8/8/8/8/8/5Q2/4K3 w - - 0 1")));
    }
}