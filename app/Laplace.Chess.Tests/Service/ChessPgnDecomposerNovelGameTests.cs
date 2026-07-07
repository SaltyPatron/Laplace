using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessPgnDecomposerNovelGameTests
{
    private sealed class FakeReader : ISubstrateReader
    {
        public readonly HashSet<Hash128> Present = new();
        public int BitmapProbeCalls;

        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            BitmapProbeCalls++;
            var bm = new byte[(candidates.Count + 7) / 8];
            for (int i = 0; i < candidates.Count; i++)
                if (Present.Contains(candidates[i])) bm[i >> 3] |= (byte)(1 << (i & 7));
            return Task.FromResult(bm);
        }
    }

    private const string GameA =
        "[Event \"A\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n\n1. e4 e5 1-0\n";
    private const string GameB =
        "[Event \"B\"]\n[White \"Carol\"]\n[Black \"Dave\"]\n[Date \"2024.01.02\"]\n\n1. d4 d5 0-1\n";

    [Fact]
    public void ParsedGame_ImplementsTrunkRootRecord()
    {
        var g = ChessPgnDecomposer.TryParseGame(GameA)!;
        Assert.IsAssignableFrom<ITrunkRootRecord>(g);
        Assert.Equal(g.GameId, ((ITrunkRootRecord)g).TrunkRootId);
    }

    [Fact]
    public async Task FilterNovelAsync_SkipsGamesAlreadyPresent_BulkProbesOnce()
    {
        var a = ChessPgnDecomposer.TryParseGame(GameA)!;
        var b = ChessPgnDecomposer.TryParseGame(GameB)!;
        var reader = new FakeReader();
        reader.Present.Add(a.GameId);

        var novel = new List<ChessPgnDecomposer.ParsedGame>();
        await foreach (var g in ChessPgnDecomposer.FilterNovelAsync(new List<ChessPgnDecomposer.ParsedGame> { a, b }, reader, CancellationToken.None))
            novel.Add(g);

        Assert.Single(novel);
        Assert.Equal(b.GameId, novel[0].GameId);
        Assert.Equal(1, reader.BitmapProbeCalls);
    }

    [Fact]
    public async Task FilterNovelAsync_NoneAlreadyPresent_ReturnsAllGames()
    {
        var a = ChessPgnDecomposer.TryParseGame(GameA)!;
        var b = ChessPgnDecomposer.TryParseGame(GameB)!;
        var reader = new FakeReader();

        var novel = new List<ChessPgnDecomposer.ParsedGame>();
        await foreach (var g in ChessPgnDecomposer.FilterNovelAsync(new List<ChessPgnDecomposer.ParsedGame> { a, b }, reader, CancellationToken.None))
            novel.Add(g);

        Assert.Equal(2, novel.Count);
    }

    [Fact]
    public void TryParseGame_UnparseableText_ReturnsNull()
        => Assert.Null(ChessPgnDecomposer.TryParseGame("garbage, not a pgn game at all"));

    [Fact]
    public void TryParseGame_SameGameTwice_SameGameId()
    {
        var a1 = ChessPgnDecomposer.TryParseGame(GameA)!;
        var a2 = ChessPgnDecomposer.TryParseGame(GameA)!;
        Assert.Equal(a1.GameId, a2.GameId);
    }

    [Fact]
    public async Task FilterNovelAsync_NullReader_ReturnsAllGamesUnfiltered()
    {
        var a = ChessPgnDecomposer.TryParseGame(GameA)!;
        var novel = new List<ChessPgnDecomposer.ParsedGame>();
        await foreach (var g in ChessPgnDecomposer.FilterNovelAsync(new List<ChessPgnDecomposer.ParsedGame> { a }, null, CancellationToken.None))
            novel.Add(g);
        Assert.Single(novel);
    }
}
