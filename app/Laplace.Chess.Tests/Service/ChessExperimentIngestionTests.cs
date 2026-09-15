using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessExperimentIngestionTests
{
    [Fact]
    public async Task ReceiptIsNonfoldingMetadataOnEachPlayingWithStableRetryIds()
    {
        const string pgn = "[Event \"chess-lab/cutechess/test-id\"]\n[Round \"1\"]\n"
            + "[White \"Laplace\"]\n[Black \"Stockfish\"]\n[Result \"1-0\"]\n\n1. e4 e5 1-0\n";
        var first = ChessPgnDecomposer.TryParseGame(pgn)!;
        var second = ChessPgnDecomposer.TryParseGame(pgn.Replace("[Round \"1\"]", "[Round \"2\"]"))!;
        Assert.NotEqual(first.PlayingId, second.PlayingId);
        var evidence = ChessExperimentEvidence.Parse(ChessExperimentEvidenceTests.Receipt);

        var change = await evidence.BuildChangeAsync([first, second, first], CancellationToken.None);
        var retried = await evidence.BuildChangeAsync([first, second], CancellationToken.None);

        Assert.False(change.CountsAsUnit);
        Assert.Contains(change.Entities, entity => entity.Id == ChessExperimentEvidence.ReceiptMetaTypeId
            && entity.TypeId == BootstrapIntentBuilder.RelationTypeMetaTypeId);
        var rows = change.Attestations.Where(row => row.TypeId == ChessExperimentEvidence.ReceiptMetaTypeId).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { first.PlayingId, second.PlayingId }.ToHashSet(), rows.Select(row => row.SubjectId).ToHashSet());
        Assert.All(rows, row =>
        {
            Assert.Equal(ChessExperimentEvidence.SourceId, row.SourceId);
            Assert.NotEqual(ChessVocabulary.PgnSourceId, row.SourceId);
            Assert.Equal(ContentEmitter.RootId(evidence.ReceiptJson), row.ObjectId);
            Assert.Equal(ContentEmitter.RootId(evidence.PgnEvent), row.ContextId);
        });
        Assert.Equal(rows.Select(row => row.Id).ToHashSet(), retried.Attestations
            .Where(row => row.TypeId == ChessExperimentEvidence.ReceiptMetaTypeId).Select(row => row.Id).ToHashSet());
    }
}
