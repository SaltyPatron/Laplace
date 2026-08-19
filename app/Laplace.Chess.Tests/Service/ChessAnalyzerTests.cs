using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// The analyzer (ChessAnalyze.DeriveFromParsed) is the CALCULATED pass: it must emit exactly what
// the recorder does NOT — positions, substructures, geometry — plus the analysis-version marker
// the scan probes to skip already-derived games. Mirror of ChessRecorderTests.
public sealed class ChessAnalyzerTests
{
    private const string Game =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

    private static SubstrateChange Analyze(string pgn)
    {
        CodepointPerfcache.LoadDefault();
        var parsed = ChessPgnDecomposer.TryParseGame(pgn)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "test/analysis");
        ChessAnalyze.DeriveFromParsed(b, parsed);
        return b.SetInputUnitsConsumed(1).Build();
    }

    [Fact]
    public void WitnessedFromParsed_MatchesDirectDeriveOutput()
    {
        CodepointPerfcache.LoadDefault();
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var direct = Analyze(Game);
        var b = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "test/witnessed");
        ChessAnalyze.DeriveFromWitnessed(b, ChessAnalyze.WitnessedFromParsed(parsed));
        var viaWitness = b.SetInputUnitsConsumed(1).Build();

        Assert.Equal(direct.Entities.Length, viaWitness.Entities.Length);
        Assert.Equal(direct.Physicalities.Length, viaWitness.Physicalities.Length);
        Assert.Contains(viaWitness.Entities, e => e.TypeId == ChessVocabulary.PositionType);
    }

    [Fact]
    public void Analyzer_EmitsPositionsAndGeometry()
    {
        var change = Analyze(Game);
        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.PositionType);
        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.SubstructureType);
        Assert.False(change.Physicalities.IsDefaultOrEmpty);
        Assert.True(change.Physicalities.Length > 0, "analyzer emits geometry (physicalities)");
    }

    [Fact]
    public void Analyzer_KeepsExactTransitionsInTrajectory_NotConsensus()
    {
        var change = Analyze(Game);
        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == ChessVocabulary.MoveType);

        var positions = change.Entities
            .Where(e => e.TypeId == ChessVocabulary.PositionType)
            .Select(e => e.Id)
            .ToHashSet();
        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == ChessVocabulary.OutcomeType
                 && positions.Contains(a.SubjectId));

        var substructures = change.Entities
            .Where(e => e.TypeId == ChessVocabulary.SubstructureType)
            .Select(e => e.Id)
            .ToHashSet();
        Assert.Contains(change.Attestations,
            a => a.TypeId == ChessVocabulary.OutcomeType
                 && substructures.Contains(a.SubjectId));
    }

    [Fact]
    public void ThinkClass_FoldsOutcomeAtReusableClassGrain()
    {
        CodepointPerfcache.LoadDefault();
        var b = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "test/think");
        var position = Hash128.OfCanonical("test/chess/position");
        ChessGraph.AppendThinkClass(
            b, position, "rushed", PlyOutcome.Win, 0.7,
            ChessVocabulary.AnalysisSourceId, Hash128.OfCanonical("test/chess/playing"));
        var change = b.Build();
        var classId = ContentEmitter.RootId("rushed");
        Assert.NotNull(classId);

        Assert.Contains(change.Attestations,
            a => a.SubjectId == position
                 && a.TypeId == ChessVocabulary.HasThinkClassType
                 && a.ObjectId == classId.Value);
        Assert.Contains(change.Attestations,
            a => a.SubjectId == classId.Value
                 && a.TypeId == ChessVocabulary.OutcomeType
                 && a.ObjectId == ChessVocabulary.OutcomeObject
                 && a.ContextId is null);
    }

    [Fact]
    public void Analyzer_StampsAnalysisMarker()
    {
        var change = Analyze(Game);
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        // GH #736: the analyzer's unit is the PLAYING, so the marker keys on the playing —
        // the same id ChessAnalyze stamps. Keying it on the event made this probe miss.
        var marker = ChessVocabulary.AnalysisMarkerId(parsed.PlayingId, ChessAnalyze.Version);
        Assert.Contains(change.Entities, e => e.Id == marker && e.TypeId == ChessVocabulary.AnalysisMarkerType);
    }
}
