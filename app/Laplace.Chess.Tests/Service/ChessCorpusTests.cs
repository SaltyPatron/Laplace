using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessCorpusTests
{
    [Theory]
    [InlineData(@"E:\Data\Ingest\Games\Chess\Lumbras\otb\games.pgn", ChessCorpus.Otb)]
    [InlineData("/vault/Data/lumbrasgigabase-online/november.pgn", ChessCorpus.Online)]
    [InlineData("/vault/Data/twic/twic1530.pgn", ChessCorpus.Twic)]
    [InlineData("/vault/Data/lichess/broadcast.pgn", ChessCorpus.Lichess)]
    [InlineData(@"E:\Data\Ingest\Games\Chess\chesscom\ada\2024-01.pgn", ChessCorpus.ChessCom)]
    public void Path_SelectsTheCorpus(string path, string expected)
        => Assert.Equal(expected, ChessCorpus.FromPath(path));

    [Fact]
    public void Site_SelectsLichessAndChessCom()
    {
        Assert.Equal(ChessCorpus.Lichess, ChessCorpus.FromSite("https://lichess.org/AbCdEfGh"));
        Assert.Equal(ChessCorpus.ChessCom, ChessCorpus.FromSite("https://www.chess.com/game/live/1"));
        Assert.Null(ChessCorpus.FromSite("Wijk aan Zee"));
        Assert.Null(ChessCorpus.FromPath(@"E:\Data\Ingest\Games\Chess\misc\friend.pgn"));
    }

    [Fact]
    public void OnePass_KeepsCorpusTagsAndDerivedPaceTogether()
    {
        const string game =
            "[Event \"T\"]\n[Site \"https://lichess.org/AbCdEfGh\"]\n"
            + "[Date \"2024.01.01\"]\n[Round \"3.1\"]\n"
            + "[White \"Alice\"]\n[Black \"Bob\"]\n"
            + "[WhiteTitle \"GM\"]\n[BlackTitle \"IM\"]\n"
            + "[WhiteFideId \"1503014\"]\n[BlackFideId \"14109603\"]\n"
            + "[UTCDate \"2024.01.01\"]\n[UTCTime \"18:05:00\"]\n"
            + "[Result \"1-0\"]\n\n"
            + "1. e4 {[%clk 0:05:00]} e5 {[%clk 0:05:00]} "
            + "2. Qh5 {[%clk 0:04:50]} Nc6 {[%clk 0:04:55]} "
            + "3. Bc4 {[%clk 0:04:40]} Nf6 {[%clk 0:04:50]} "
            + "4. Qxf7# {[%clk 0:04:30]} 1-0\n";

        CodepointPerfcache.LoadDefault();
        var parsed = ChessPgnDecomposer.TryParseGame(game)! with { Corpus = ChessCorpus.Twic };
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/corpus");
        ChessPgnDecomposer.ComposeGame(parsed, b, analyzeInline: true);
        var change = b.SetInputUnitsConsumed(1).Build();

        var corpus = ChessCorpus.SourceId(ChessCorpus.Twic);
        foreach (string relation in new[]
                 {
                     "Site", "Round", "UTCDate", "UTCTime", "WhiteTitle", "BlackTitle",
                     "WhiteFideId", "BlackFideId",
                 })
        {
            var typeId = RelationTypeRegistry.RelationTypeId(relation);
            Assert.Contains(ChessSeedManifest.Relations, name => name == relation);
            Assert.Contains(change.Attestations, a => a.TypeId == typeId && a.SourceId == corpus);
        }

        Assert.Contains(change.Attestations, a => a.TypeId == ChessVocabulary.HasThinkClassType);
        Assert.Contains(change.Attestations, a => a.TypeId == ChessVocabulary.AnalysisVersionMetaTypeId);
        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
    }
}
