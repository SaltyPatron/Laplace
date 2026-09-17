using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessAnalyzerWatermarkTests
{
    private const string GameA =
        "[Event \"A\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n\n1. e4 e5 1-0\n";
    private const string GameB =
        "[Event \"B\"]\n[White \"Carol\"]\n[Black \"Dave\"]\n[Date \"2024.01.02\"]\n\n1. d4 d5 0-1\n";

    private sealed class FakeReader : ISubstrateReader
    {
        public readonly HashSet<Hash128> Present = new();
        public readonly HashSet<(Hash128 Type, Hash128 Id)> Receipts = new();
        public int EntityQueries;
        public int ReceiptQueries;
        public Task<IReadOnlySet<Hash128>> PresentAttestationIdsAsync(
            Hash128 typeId, IReadOnlyList<Hash128> ids, CancellationToken ct = default)
        {
            ReceiptQueries++;
            return Task.FromResult<IReadOnlySet<Hash128>>(
                ids.Where(id => Receipts.Contains((typeId, id))).ToHashSet());
        }
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default) => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            EntityQueries++;
            var bm = new byte[(candidates.Count + 7) / 8];
            for (int i = 0; i < candidates.Count; i++)
                if (Present.Contains(candidates[i])) bm[i >> 3] |= (byte)(1 << (i & 7));
            return Task.FromResult(bm);
        }
    }

    [Fact]
    public async Task FilterUnanalyzed_SkipsOnlyAcceptedCurrentOwnerReceipt()
    {
        var a = ChessPgnDecomposer.TryParseGame(GameA)!;
        var b = ChessPgnDecomposer.TryParseGame(GameB)!;
        var reader = new FakeReader();
        reader.Receipts.Add((IngestUnitCompletion.RelationTypeId(21),
            IngestUnitCompletion.AttestationId(
                ChessVocabulary.AnalysisMarkerId(a.PlayingId, ChessAnalyze.Version),
                ChessAnalyze.SourceId, 21)));

        var kept = new List<Hash128>();
        await foreach (var id in ChessWitnessHydrator.FilterUnanalyzedEventIdsAsync(
            [a.PlayingId, b.PlayingId], reader, CancellationToken.None))
            kept.Add(id);

        Assert.Single(kept);
        Assert.Equal(b.PlayingId, kept[0]);
        Assert.Equal(0, reader.EntityQueries);
        Assert.Equal(1, reader.ReceiptQueries);
    }

    [Fact]
    public async Task FilterUnanalyzed_NoneMarked_YieldsAll()
    {
        var a = ChessPgnDecomposer.TryParseGame(GameA)!;
        var b = ChessPgnDecomposer.TryParseGame(GameB)!;
        var kept = new List<Hash128>();
        await foreach (var id in ChessWitnessHydrator.FilterUnanalyzedEventIdsAsync(
            [a.PlayingId, b.PlayingId], new FakeReader(), CancellationToken.None))
            kept.Add(id);
        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public async Task EntityOnlyWrongOwnerAndWrongLayerCannotHideAnIncompleteUnit()
    {
        var game = ChessPgnDecomposer.TryParseGame(GameA)!;
        var marker = ChessVocabulary.AnalysisMarkerId(game.PlayingId, ChessAnalyze.Version);
        var reader = new FakeReader();
        reader.Present.Add(marker);
        reader.Receipts.Add((IngestUnitCompletion.RelationTypeId(21),
            IngestUnitCompletion.AttestationId(marker, ChessTransitions.SourceId, 21)));
        reader.Receipts.Add((IngestUnitCompletion.RelationTypeId(22),
            IngestUnitCompletion.AttestationId(marker, ChessAnalyze.SourceId, 22)));
        var kept = new List<Hash128>();
        await foreach (var id in ChessWitnessHydrator.FilterUnanalyzedEventIdsAsync(
                           [game.PlayingId], reader, CancellationToken.None))
            kept.Add(id);
        Assert.Equal(new[] { game.PlayingId }, kept);
        Assert.Equal(0, reader.EntityQueries);
    }

    [Fact]
    public async Task ExplicitInlineOwnersRetainOncePerLineSkipAcrossProbeChunks()
    {
        var a = ChessPgnDecomposer.TryParseGame(GameA)!;
        var b = ChessPgnDecomposer.TryParseGame(GameB)!;
        var reader = new FakeReader();
        Hash128 Marker(Hash128 line) => ChessMoveOutcomes.MarkerId(line, ChessMoveOutcomes.Version);
        reader.Receipts.Add((IngestUnitCompletion.RelationTypeId(22),
            IngestUnitCompletion.AttestationId(Marker(a.LineId), ChessVocabulary.PgnSourceId, 22)));
        // An unrelated owner cannot complete the second line for these callers.
        reader.Receipts.Add((IngestUnitCompletion.RelationTypeId(22),
            IngestUnitCompletion.AttestationId(Marker(b.LineId), ChessAnalyze.SourceId, 22)));
        var kept = new List<Hash128>();
        await foreach (var id in ChessWitnessHydrator.FilterByMarkerAsync(
                           new[] { a.LineId, b.LineId }, reader, 1, Marker, 22,
                           [ChessMoveOutcomes.SourceId, ChessVocabulary.PgnSourceId],
                           CancellationToken.None))
            kept.Add(id);
        Assert.Equal(new[] { b.LineId }, kept);
        Assert.Equal(2, reader.ReceiptQueries);
        Assert.Equal(0, reader.EntityQueries);
    }
}
