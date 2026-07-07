using Laplace.Engine.Core;
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
    public void Analyzer_StampsAnalysisMarker()
    {
        var change = Analyze(Game);
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var marker = ChessVocabulary.AnalysisMarkerId(parsed.GameId, ChessAnalyze.Version);
        Assert.Contains(change.Entities, e => e.Id == marker && e.TypeId == ChessVocabulary.AnalysisMarkerType);
    }
}
