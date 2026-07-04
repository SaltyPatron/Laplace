using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// The analyzer's watermark: FilterUnanalyzedAsync must yield only games whose per-(game, version)
// AnalysisMarker isn't already present — that's how re-running skips already-derived games and
// picks up only new/unanalyzed ones. Live run proved apply + marker creation; this proves the skip.
public sealed class ChessAnalyzerWatermarkTests
{
    private const string GameA =
        "[Event \"A\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n\n1. e4 e5 1-0\n";
    private const string GameB =
        "[Event \"B\"]\n[White \"Carol\"]\n[Black \"Dave\"]\n[Date \"2024.01.02\"]\n\n1. d4 d5 0-1\n";

    private sealed class FakeReader : ISubstrateReader
    {
        public readonly HashSet<Hash128> Present = new();
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default) => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            var bm = new byte[(candidates.Count + 7) / 8];
            for (int i = 0; i < candidates.Count; i++)
                if (Present.Contains(candidates[i])) bm[i >> 3] |= (byte)(1 << (i & 7));
            return Task.FromResult(bm);
        }
    }

    [Fact]
    public async Task FilterUnanalyzed_SkipsGamesAlreadyMarked_AtCurrentVersion()
    {
        var a = ChessPgnDecomposer.TryParseGame(GameA)!;
        var b = ChessPgnDecomposer.TryParseGame(GameB)!;
        var reader = new FakeReader();
        // A is already analyzed at the current version; B is not.
        reader.Present.Add(ChessVocabulary.AnalysisMarkerId(a.GameId, ChessAnalyze.Version));

        var kept = new List<ChessPgnDecomposer.ParsedGame>();
        await foreach (var g in ChessAnalyzeDecomposer.FilterUnanalyzedAsync(
            new List<ChessPgnDecomposer.ParsedGame> { a, b }, reader, CancellationToken.None))
            kept.Add(g);

        Assert.Single(kept);
        Assert.Equal(b.GameId, kept[0].GameId);
    }

    [Fact]
    public async Task FilterUnanalyzed_NoneMarked_YieldsAll()
    {
        var a = ChessPgnDecomposer.TryParseGame(GameA)!;
        var b = ChessPgnDecomposer.TryParseGame(GameB)!;
        var kept = new List<ChessPgnDecomposer.ParsedGame>();
        await foreach (var g in ChessAnalyzeDecomposer.FilterUnanalyzedAsync(
            new List<ChessPgnDecomposer.ParsedGame> { a, b }, new FakeReader(), CancellationToken.None))
            kept.Add(g);
        Assert.Equal(2, kept.Count);
    }
}
